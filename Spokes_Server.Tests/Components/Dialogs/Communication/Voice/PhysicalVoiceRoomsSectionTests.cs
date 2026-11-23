using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using Bunit;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.JSInterop;
using Moq;
using MudBlazor;
using MudBlazor.Services;
using Spokes_Server.Aggregate;
using Spokes_Server.Components.Dialogs.Communication.Voice;
using Spokes_Server.Core.Constants;
using Spokes_Server.Core.Extensions;
using Spokes_Server.Core.Models.Communication;
using Spokes_Server.Core.Models.HR;
using Spokes_Server.Core.Services.Communication.Chat;
using Spokes_Server.Core.Services.Communication.Presence;
using Spokes_Server.Core.Services.Communication.Voice;
using Spokes_Server.Core.Services.Communication.Voice.Pipeline;
using Spokes_Server.Core.Services.Identity;
using Spokes_Server.Core.Services.Security;
using Xunit;

namespace Spokes_Server.Tests.Components.Dialogs.Communication.Voice;

public class PhysicalVoiceRoomsSectionTests : BunitContext
{
    private readonly string _testDataDir;
    private readonly Database _db;
    private readonly VoiceStateService _voiceState;
    private readonly PhysicalVoiceRoomService _roomService;
    private readonly GlobalVoiceService _voiceService;
    private readonly Employee _admin;
    private readonly Employee _creator;
    private readonly Employee _regularMember;
    private string _mockJsDeviceId = string.Empty;
    private string _mockJsDeviceName = string.Empty;
    private bool _mockIsDesktopClient;

    public PhysicalVoiceRoomsSectionTests()
    {
        _testDataDir = Path.Combine(Path.GetTempPath(), $"Spokes_Test_PhysRoomsBunit_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testDataDir);

        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddMudServices();
        Services.AddLocalization();
        Services.AddLogging();

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DataPath"] = _testDataDir,
                ["DatabasePath"] = _testDataDir,
                ["SPOKES_MASTER_PASSWORD"] = "UnitTest-Master-Password-2026!"
            })
            .Build();
        Services.AddSingleton<IConfiguration>(config);

        Services.AddSingleton<EncryptionService>();
        Services.AddSpokesDatabase();

        _voiceState = new VoiceStateService();
        Services.AddSingleton(_voiceState);
        Services.AddSingleton(new PresenceStateService(_voiceState));

        var chatState = new ChatStateService();
        Services.AddSingleton(chatState);

        var chatAuthMock = new Mock<IChatAuthorizationService>();
        chatAuthMock.Setup(c => c.CanUserAccessChannel(It.IsAny<ChatChannel>(), It.IsAny<Employee>())).Returns(true);
        Services.AddSingleton(chatAuthMock.Object);

        var privacyMock = new Mock<IUserPrivacyService>();
        privacyMock.Setup(p => p.CanDiscoverUser(It.IsAny<Employee?>(), It.IsAny<Employee>())).Returns(true);
        Services.AddSingleton(privacyMock.Object);

        var authProviderMock = new Mock<AuthenticationStateProvider>();
        authProviderMock
            .Setup(p => p.GetAuthenticationStateAsync())
            .ReturnsAsync(new AuthenticationState(new ClaimsPrincipal(new ClaimsIdentity())));
        Services.AddSingleton<AuthenticationStateProvider>(authProviderMock.Object);
        Services.AddScoped<UserService>();

        var jsMock = new Mock<IJSRuntime>();
        var voiceModuleMock = new Mock<IJSObjectReference>();
        voiceModuleMock
            .Setup(x => x.InvokeAsync<VoiceClientBootstrapInfo>("getClientVoiceBootstrap", It.IsAny<object[]>()))
            .Returns(() => new ValueTask<VoiceClientBootstrapInfo>(new VoiceClientBootstrapInfo
            {
                IsDesktopClient = _mockIsDesktopClient,
                DeviceId = _mockJsDeviceId,
                DeviceName = _mockJsDeviceName
            }));
        voiceModuleMock
            .Setup(x => x.InvokeAsync<PhysicalDeviceIdentity>("getOrCreatePhysicalDeviceIdentity", It.IsAny<object[]>()))
            .Returns(() => new ValueTask<PhysicalDeviceIdentity>(new PhysicalDeviceIdentity
            {
                DeviceId = _mockJsDeviceId,
                DeviceName = _mockJsDeviceName
            }));
        jsMock.Setup(x => x.InvokeAsync<IJSObjectReference>("import", It.IsAny<object[]>()))
            .Returns(new ValueTask<IJSObjectReference>(voiceModuleMock.Object));

        var jsInterop = new VoiceJsInteropService(jsMock.Object);
        var deviceService = new VoiceDeviceService(jsInterop, NullLogger<VoiceDeviceService>.Instance);
        var voiceChannelServiceMock = new Mock<VoiceChannelService>();
        var voiceBotMock = new Mock<IVoiceBotService>();

        Services.AddSingleton(sp =>
        {
            var db = sp.GetRequiredService<Database>();
            return new PhysicalVoiceRoomService(
                db.PhysicalVoiceRooms,
                _voiceState,
                NullLogger<PhysicalVoiceRoomService>.Instance,
                db.Employees,
                voiceBotMock.Object,
                userPrivacy: privacyMock.Object,
                channels: db.ChatChannels,
                chatAuth: chatAuthMock.Object);
        });

        Services.AddScoped(sp => new GlobalVoiceService(
            jsInterop,
            voiceChannelServiceMock.Object,
            _voiceState,
            chatState,
            NullLogger<GlobalVoiceService>.Instance,
            deviceService,
            physicalRoomService: sp.GetRequiredService<PhysicalVoiceRoomService>()));

        _db = Services.GetRequiredService<Database>();
        _db.Initialize();
        _roomService = Services.GetRequiredService<PhysicalVoiceRoomService>();
        _voiceService = Services.GetRequiredService<GlobalVoiceService>();

        _admin = SaveEmployee("emp-admin", "Admin", "Owner", isAdmin: true);
        _creator = SaveEmployee("emp-creator", "Alice", "Creator", isAdmin: false);
        _regularMember = SaveEmployee("emp-member", "Bob", "Member", isAdmin: false);
    }

    private Employee SaveEmployee(string id, string first, string last, bool isAdmin)
    {
        var emp = new Employee
        {
            Id = id,
            FirstName = first,
            LastName = last,
            IsActive = true,
            IsAdmin = isAdmin,
            Permissions = [AppPermissions.Chat.Use]
        };
        _db.Employees.Save(emp);
        return emp;
    }

    private Employee? ResolveEmployee(string? id) =>
        string.IsNullOrWhiteSpace(id) ? null : _db.Employees.GetById(id);

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            DisposeAsync().AsTask().GetAwaiter().GetResult();
            if (Directory.Exists(_testDataDir))
            {
                try { Directory.Delete(_testDataDir, true); } catch { }
            }
        }
        base.Dispose(disposing);
    }

    [Fact]
    public async Task Creator_NotEnrolledOnCurrentDevice_SeesManageRoomsAndCanConfirmDelete()
    {
        var room = (await _roomService.CreateRoomAsync("Creator Room", _creator.Id, "other-device-id", "Other PC")).Value!;

        Render<MudPopoverProvider>();
        var dialogProvider = Render<MudDialogProvider>();
        var cut = Render<PhysicalVoiceRoomsSection>(p => p
            .Add(x => x.CurrentUser, _creator)
            .Add(x => x.ResolveVisibleEmployee, ResolveEmployee));

        // Creator's current device is NOT enrolled in the room, yet Manage Rooms still shows it with Rename & Delete
        var row = cut.Find($"[data-physical-room-row='{room.Id}']");
        Assert.Contains("Creator Room", row.TextContent, StringComparison.Ordinal);
        Assert.Contains("Created by you", row.TextContent, StringComparison.Ordinal);

        // Clicking Delete opens confirmation dialog; canceling keeps the room intact
        var deleteBtn = row.QuerySelector("button[title='Delete Room']");
        Assert.NotNull(deleteBtn);
        await cut.InvokeAsync(() => deleteBtn!.Click());

        var cancelBtn = dialogProvider.FindAll("button").First(b => b.TextContent.Contains("Cancel", StringComparison.OrdinalIgnoreCase));
        await dialogProvider.InvokeAsync(() => cancelBtn.Click());
        Assert.NotNull(_roomService.GetRoomById(room.Id));

        // Clicking Delete and confirming permanently deletes the room
        row = cut.Find($"[data-physical-room-row='{room.Id}']");
        deleteBtn = row.QuerySelector("button[title='Delete Room']")!;
        await cut.InvokeAsync(() => deleteBtn.Click());

        var confirmDeleteBtn = dialogProvider.FindAll(".mud-message-box button")
            .Last(b => b.TextContent.Contains("Delete", StringComparison.OrdinalIgnoreCase));
        await dialogProvider.InvokeAsync(() => confirmDeleteBtn.Click());

        Assert.Null(_roomService.GetRoomById(room.Id));
        Assert.Empty(cut.FindAll($"[data-physical-room-row='{room.Id}']"));
    }

    [Fact]
    public async Task Admin_EnrolledInOwnRoom_CanDeleteOtherUsersRoomWithoutLosingOwnEnrollmentOrCalibrations()
    {
        var adminRoom = (await _roomService.CreateRoomAsync("Admin Office", _admin.Id, $"dev-{_admin.Id}", "Admin PC")).Value!;
        _roomService.SaveLearnedPairCalibration(adminRoom.Id, new PhysicalRoomSavedPairCalibration
        {
            SourceEmployeeId = _admin.Id,
            SourceDeviceId = $"dev-{_admin.Id}",
            TargetEmployeeId = _regularMember.Id,
            TargetDeviceId = "dev-member",
            DelaySamples = 96,
            BleedGain = 0.2f,
            Confidence = 0.85
        });

        var otherRoom = (await _roomService.CreateRoomAsync("Alice Gaming Den", _creator.Id, "dev-alice", "Alice PC")).Value!;

        Render<MudPopoverProvider>();
        var dialogProvider = Render<MudDialogProvider>();
        var cut = Render<PhysicalVoiceRoomsSection>(p => p
            .Add(x => x.CurrentUser, _admin)
            .Add(x => x.ResolveVisibleEmployee, ResolveEmployee));

        // Admin sees both rooms in Manage Rooms with Admin badge and creator attribution
        Assert.Contains("Admin", cut.Find("[data-physical-rooms-manage='true']").TextContent, StringComparison.Ordinal);
        Assert.NotEmpty(cut.FindAll($"[data-physical-room-row='{adminRoom.Id}']"));
        var otherRow = cut.Find($"[data-physical-room-row='{otherRoom.Id}']");
        Assert.Contains("Alice Gaming Den", otherRow.TextContent, StringComparison.Ordinal);
        Assert.Contains("Created by Alice Creator", otherRow.TextContent, StringComparison.Ordinal);

        // Admin deletes Alice's room via Manage Rooms
        var deleteOtherBtn = otherRow.QuerySelector("button[title='Delete Room']")!;
        await cut.InvokeAsync(() => deleteOtherBtn.Click());

        var confirmDeleteBtn = dialogProvider.FindAll(".mud-message-box button")
            .Last(b => b.TextContent.Contains("Delete", StringComparison.OrdinalIgnoreCase));
        await dialogProvider.InvokeAsync(() => confirmDeleteBtn.Click());

        // Alice's room is deleted while Admin's own room enrollment and calibrations remain untouched
        Assert.Null(_roomService.GetRoomById(otherRoom.Id));
        var stillEnrolled = _roomService.GetRoomForUserDevice(_admin.Id, $"dev-{_admin.Id}");
        Assert.NotNull(stillEnrolled);
        Assert.Equal(adminRoom.Id, stillEnrolled!.Id);
        Assert.Single(_db.PhysicalVoiceRooms.GetById(adminRoom.Id)!.SavedCalibrations);
    }

    [Fact]
    public async Task RegularNonCreatorMember_DoesNotSeeManageRoomsOrDeleteControls()
    {
        var otherRoom = (await _roomService.CreateRoomAsync("Alice Room", _creator.Id, "dev-alice", "Alice PC")).Value!;
        await _roomService.EnrollUserDeviceAsync(otherRoom.Id, _regularMember.Id, $"dev-{_regularMember.Id}", "Bob PC", actorUserId: _regularMember.Id);

        Render<MudPopoverProvider>();
        Render<MudDialogProvider>();
        var cut = Render<PhysicalVoiceRoomsSection>(p => p
            .Add(x => x.CurrentUser, _regularMember)
            .Add(x => x.ResolveVisibleEmployee, ResolveEmployee));

        Assert.Empty(cut.FindAll("[data-physical-rooms-manage='true']"));
        Assert.Empty(cut.FindAll("button[title='Delete Room']"));
    }

    [Fact]
    public async Task NonAdminCreator_DoesNotSeeRemoveButtonOnAdminEnrolledDevice()
    {
        var room = (await _roomService.CreateRoomAsync("Shared Den", _creator.Id, $"dev-{_creator.Id}", "Alice PC")).Value!;
        await _roomService.EnrollUserDeviceAsync(room.Id, _admin.Id, "dev-admin-pc", "Admin PC", actorUserId: _admin.Id);

        Render<MudPopoverProvider>();
        Render<MudDialogProvider>();
        var cut = Render<PhysicalVoiceRoomsSection>(p => p
            .Add(x => x.CurrentUser, _creator)
            .Add(x => x.ResolveVisibleEmployee, ResolveEmployee));

        // Exactly 1 remove button: Alice's own device. Admin's device has NO remove button for a non-admin creator.
        var removeButtons = cut.FindAll("button[title='Remove device from room']");
        Assert.Single(removeButtons);
    }

    [Fact]
    public async Task Creator_NotEnrolledOnCurrentDevice_CanInlineRenameRoomInManageRooms()
    {
        var room = (await _roomService.CreateRoomAsync("Old Room Name", _creator.Id, "other-device-id", "Other PC")).Value!;

        Render<MudPopoverProvider>();
        Render<MudDialogProvider>();
        var cut = Render<PhysicalVoiceRoomsSection>(p => p
            .Add(x => x.CurrentUser, _creator)
            .Add(x => x.ResolveVisibleEmployee, ResolveEmployee));

        var row = cut.Find($"[data-physical-room-row='{room.Id}']");
        var editBtn = row.QuerySelector("button[title='Edit Room Name']")!;
        await cut.InvokeAsync(() => editBtn.Click());

        var input = cut.Find($"[data-physical-room-row='{room.Id}'] input");
        await cut.InvokeAsync(() => input.Input("Renamed Studio"));

        var saveBtn = cut.Find($"[data-physical-room-row='{room.Id}'] button[title='Save']");
        await cut.InvokeAsync(() => saveBtn.Click());

        var updated = _roomService.GetRoomById(room.Id);
        Assert.NotNull(updated);
        Assert.Equal("Renamed Studio", updated!.Name);
        Assert.Contains("Renamed Studio", cut.Find($"[data-physical-room-row='{room.Id}']").TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OpeningSectionOnSpokesDesktop_HealsStaleChromeDeviceLabelToSpokesDesktop()
    {
        const string desktopDeviceId = "dev-electron-desktop-1";
        _mockJsDeviceId = desktopDeviceId;
        _mockJsDeviceName = "Windows PC (Spokes Desktop)";
        _mockIsDesktopClient = true;

        // Pre-seed room with stale pre-fix Electron label "Windows PC (Chrome)"
        await _roomService.CreateRoomAsync("Home Office", _creator.Id, desktopDeviceId, "Windows PC (Chrome)");

        Render<MudPopoverProvider>();
        Render<MudDialogProvider>();
        var cut = Render<PhysicalVoiceRoomsSection>(p => p
            .Add(x => x.CurrentUser, _creator)
            .Add(x => x.ResolveVisibleEmployee, ResolveEmployee));

        cut.WaitForAssertion(() =>
        {
            Assert.Contains("Windows PC (Spokes Desktop)", cut.Markup, StringComparison.Ordinal);
            Assert.DoesNotContain("Windows PC (Chrome)", cut.Markup, StringComparison.Ordinal);
        });

        var healedEnrollment = _roomService.GetEnrollment(_creator.Id, desktopDeviceId);
        Assert.NotNull(healedEnrollment);
        Assert.Equal("Windows PC (Spokes Desktop)", healedEnrollment!.DeviceName);
    }
}
