using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.JSInterop;
using Microsoft.JSInterop.Infrastructure;
using Moq;
using Spokes_Server.Core.Constants;
using Spokes_Server.Core.Data;
using Spokes_Server.Core.Data.Repositories.Communication;
using Spokes_Server.Core.Data.Repositories.HR;
using Spokes_Server.Core.Models.Communication;
using Spokes_Server.Core.Models.HR;
using Spokes_Server.Core.Services.Communication.Chat;
using Spokes_Server.Core.Services.Communication.Voice;
using Spokes_Server.Core.Services.Communication.Voice.Dsp;
using Spokes_Server.Core.Services.Security;
using Xunit;

namespace Spokes_Server.Tests.Services.Voice;

public class PhysicalRoomDirectSessionServiceTests : IDisposable
{
    private readonly string _tempDir;
    private readonly DiskPersistenceService _persistence;
    private readonly PhysicalVoiceRoomRepository _roomRepo;
    private readonly EmployeeRepository _employeeRepo;
    private readonly VoiceStateService _voiceState;
    private readonly Mock<IVoiceBotService> _voiceBotMock;
    private readonly Mock<IUserPrivacyService> _privacyMock;
    private readonly PhysicalVoiceRoomService _roomService;
    private readonly PhysicalRoomDirectSessionService _directService;

    public PhysicalRoomDirectSessionServiceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "spokes_direct_room_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DataPath"] = _tempDir, ["SPOKES_MASTER_PASSWORD"] = "UnitTest-Master-Password-2026!",
                ["DatabasePath"] = _tempDir
            })
            .Build();

        _persistence = new DiskPersistenceService(NullLogger<DiskPersistenceService>.Instance);
        _roomRepo = new PhysicalVoiceRoomRepository(_persistence, config);
        _roomRepo.LoadFromDisk();

        _employeeRepo = new EmployeeRepository(_persistence, config);
        _employeeRepo.LoadFromDisk();

        _voiceState = new VoiceStateService();
        _voiceBotMock = new Mock<IVoiceBotService>();
        _privacyMock = new Mock<IUserPrivacyService>();
        _privacyMock.Setup(p => p.CanDiscoverUser(It.IsAny<Employee?>(), It.IsAny<Employee>())).Returns(true);

        _roomService = new PhysicalVoiceRoomService(
            _roomRepo,
            _voiceState,
            NullLogger<PhysicalVoiceRoomService>.Instance,
            _employeeRepo,
            _voiceBotMock.Object,
            userPrivacy: _privacyMock.Object);

        _directService = new PhysicalRoomDirectSessionService(
            _roomService,
            _voiceState,
            _employeeRepo,
            _privacyMock.Object);

        SeedEmployee("emp-alice");
        SeedEmployee("emp-bob");
        SeedEmployee("emp-carol");
    }

    private Employee SeedEmployee(string id, bool isAdmin = false)
    {
        var emp = new Employee
        {
            Id = id,
            FirstName = "User",
            LastName = id,
            IsAdmin = isAdmin,
            IsActive = true,
            Permissions = [AppPermissions.Chat.Use]
        };
        _employeeRepo.Save(emp);
        return emp;
    }

    public void Dispose()
    {
        _directService.Dispose();
        _persistence.Dispose();
        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, true);
            }
        }
        catch { }
    }

    [Fact]
    public void SanitizeLanEndpoints_AcceptsPrivateLinkLocalTailscaleAndLoopback_RejectsPublicWanAndPrivilegedPorts()
    {
        var raw = new[]
        {
            "192.168.1.25:51200",
            "10.0.4.18:51200",
            "172.20.1.9:51200",
            "100.90.12.4:51200",     // CGNAT / Tailscale LAN mesh
            "169.254.10.20:51200",   // Link-local IPv4
            "[fd00:abcd::12]:51201", // IPv6 ULA
            "[fe80::1234]:51201",    // IPv6 link-local
            "127.0.0.1:51200",       // Loopback
            "[::1]:51201",           // IPv6 loopback
            "8.8.8.8:51200",         // Public WAN IPv4 -> REJECT
            "1.1.1.1:51200",         // Public WAN IPv4 -> REJECT
            "[2606:4700:4700::1111]:51201", // Public WAN IPv6 -> REJECT
            "192.168.1.25:80",       // Privileged port < 1024 -> REJECT
            "192.168.1.25:99999",    // Invalid port -> REJECT
            "example.com:51200",     // Hostname -> REJECT
            ""
        };

        var sanitized = PhysicalRoomDirectSessionService.SanitizeLanEndpoints(raw);

        Assert.Contains("192.168.1.25:51200", sanitized);
        Assert.Contains("10.0.4.18:51200", sanitized);
        Assert.Contains("172.20.1.9:51200", sanitized);
        Assert.Contains("100.90.12.4:51200", sanitized);
        Assert.Contains("169.254.10.20:51200", sanitized);
        Assert.Contains("[fd00:abcd::12]:51201", sanitized);
        Assert.Contains("[fe80::1234]:51201", sanitized);
        Assert.Contains("127.0.0.1:51200", sanitized);
        Assert.Contains("[::1]:51201", sanitized);

        Assert.DoesNotContain(sanitized, ep => ep.Contains("8.8.8.8"));
        Assert.DoesNotContain(sanitized, ep => ep.Contains("1.1.1.1"));
        Assert.DoesNotContain(sanitized, ep => ep.Contains("2606:4700"));
        Assert.DoesNotContain(sanitized, ep => ep.EndsWith(":80"));
        Assert.DoesNotContain(sanitized, ep => ep.Contains("example.com"));
    }

    [Fact]
    public async Task BuildSessionDescriptorForParticipant_IssuesShared256BitKey_EnforcesPrivacyAndMuteOptOut_AndRotatesEpochOnDeparture()
    {
        var room = (await _roomService.CreateRoomAsync(
            "Podcast Studio",
            "emp-alice",
            "dev-alice",
            "Alice Desktop",
            audioMode: PhysicalRoomAudioMode.LowLatencyCleaned)).Value!;

        await _roomService.EnrollUserDeviceAsync(room.Id, "emp-bob", "dev-bob", "Bob Desktop", actorUserId: "emp-bob");
        await _roomService.EnrollUserDeviceAsync(room.Id, "emp-carol", "dev-carol", "Carol Desktop", actorUserId: "emp-carol");

        _voiceState.NotifyVoiceMemberJoined("chan-1", "emp-alice", "dev-alice", "Alice Desktop");
        _voiceState.NotifyVoiceMemberJoined("chan-1", "emp-bob", "dev-bob", "Bob Desktop");
        _voiceState.NotifyVoiceMemberJoined("chan-1", "emp-carol", "dev-carol", "Carol Desktop");

        _directService.UpdateParticipantEndpoints("chan-1", "emp-alice", "dev-alice", ["192.168.1.10:52001", "8.8.8.8:52001"]);
        _directService.UpdateParticipantEndpoints("chan-1", "emp-bob", "dev-bob", ["192.168.1.11:52002"]);
        _directService.UpdateParticipantEndpoints("chan-1", "emp-carol", "dev-carol", ["192.168.1.12:52003"]);

        // Configure privacy: Alice cannot discover Carol
        _privacyMock
            .Setup(p => p.CanDiscoverUser(
                It.Is<Employee?>(e => e != null && e.Id == "emp-alice"),
                It.Is<Employee>(t => t.Id == "emp-carol")))
            .Returns(false);

        var aliceDesc = _directService.BuildSessionDescriptorForParticipant("chan-1", "emp-alice", "dev-alice");
        var bobDesc = _directService.BuildSessionDescriptorForParticipant("chan-1", "emp-bob", "dev-bob");

        Assert.NotNull(aliceDesc);
        Assert.NotNull(bobDesc);
        Assert.Equal(PhysicalRoomAudioMode.LowLatencyCleaned, aliceDesc!.AudioMode);
        Assert.Equal(aliceDesc.SessionKeyBase64, bobDesc!.SessionKeyBase64);
        Assert.Equal(32, Convert.FromBase64String(aliceDesc.SessionKeyBase64).Length);
        Assert.Equal(aliceDesc.KeyEpoch, bobDesc.KeyEpoch);
        Assert.NotEqual(aliceDesc.LocalPeerToken, bobDesc.LocalPeerToken);

        // Alice sees Bob (with sanitized LAN IP only) and does NOT see Carol due to IUserPrivacyService
        var bobPeerForAlice = Assert.Single(aliceDesc.Peers);
        Assert.Equal("emp-bob", bobPeerForAlice.UserId);
        Assert.Equal(bobDesc.LocalPeerToken, bobPeerForAlice.PeerToken);
        Assert.Equal(["192.168.1.11:52002"], bobPeerForAlice.Endpoints);

        // Bob sees Alice with public 8.8.8.8 stripped out
        var alicePeerForBob = Assert.Single(bobDesc.Peers, p => p.UserId == "emp-alice");
        Assert.Equal(["192.168.1.10:52001"], alicePeerForBob.Endpoints);

        // MuteUsersInMyRoom opts Alice out of direct audio reception AND excludes Alice from Bob's direct peer list
        await _roomService.SetMuteUsersInMyRoomAsync("emp-alice", "dev-alice", muteUsersInMyRoom: true, actorUserId: "emp-alice");
        Assert.Null(_directService.BuildSessionDescriptorForParticipant("chan-1", "emp-alice", "dev-alice"));
        var bobDescWhileAliceMuted = _directService.BuildSessionDescriptorForParticipant("chan-1", "emp-bob", "dev-bob")!;
        Assert.DoesNotContain(bobDescWhileAliceMuted.Peers, p => p.UserId == "emp-alice");

        await _roomService.SetMuteUsersInMyRoomAsync("emp-alice", "dev-alice", muteUsersInMyRoom: false, actorUserId: "emp-alice");

        // Ensure Carol is registered in AuthorizedMembers before she departs
        _ = _directService.BuildSessionDescriptorForParticipant("chan-1", "emp-carol", "dev-carol");

        // Departure of Carol from the voice channel rotates KeyEpoch and generates a brand-new 256-bit key
        var keyBeforeLeave = aliceDesc.SessionKeyBase64;
        var epochBeforeLeave = aliceDesc.KeyEpoch;
        string? notifiedChannel = null;
        string? notifiedRoom = null;
        _directService.DirectSessionPeersChanged += (ch, rm) =>
        {
            notifiedChannel = ch;
            notifiedRoom = rm;
        };

        _voiceState.NotifyVoiceMemberLeft("chan-1", "emp-carol");

        var aliceDescAfterLeave = _directService.BuildSessionDescriptorForParticipant("chan-1", "emp-alice", "dev-alice")!;
        Assert.Equal("chan-1", notifiedChannel);
        Assert.Equal(room.Id, notifiedRoom);
        Assert.True(aliceDescAfterLeave.KeyEpoch > epochBeforeLeave);
        Assert.NotEqual(keyBeforeLeave, aliceDescAfterLeave.SessionKeyBase64);

        // Switching room back to ServerCleanup disables direct session descriptors
        await _roomService.SetRoomAudioModeAsync(room.Id, PhysicalRoomAudioMode.ServerCleanup, "emp-alice");
        Assert.Null(_directService.BuildSessionDescriptorForParticipant("chan-1", "emp-alice", "dev-alice"));
    }

    [Fact]
    public async Task GlobalVoiceService_NegotiatesDesktopDirectSession_WhenLowLatencyRoomActive_AndStopsOnFallback()
    {
        var room = (await _roomService.CreateRoomAsync(
            "LAN Pod",
            "emp-alice",
            "dev-alice",
            "Alice PC",
            audioMode: PhysicalRoomAudioMode.LowLatencyCleaned)).Value!;
        await _roomService.EnrollUserDeviceAsync(room.Id, "emp-bob", "dev-bob", "Bob PC", actorUserId: "emp-bob");

        var jsMock = new Mock<IJSRuntime>();
        var moduleMock = new Mock<IJSObjectReference>();
        jsMock.Setup(x => x.InvokeAsync<IJSObjectReference>("import", It.IsAny<object[]>()))
            .Returns(new ValueTask<IJSObjectReference>(moduleMock.Object));

        moduleMock.Setup(x => x.InvokeAsync<bool>("isAppleMobileDevice", It.IsAny<object[]>())).ReturnsAsync(false);
        moduleMock.Setup(x => x.InvokeAsync<bool>("isAndroidDevice", It.IsAny<object[]>())).ReturnsAsync(false);
        moduleMock.Setup(x => x.InvokeAsync<bool>("isDesktopDirectRoomSupported", It.IsAny<object[]>())).ReturnsAsync(true);
        moduleMock.Setup(x => x.InvokeAsync<PhysicalDeviceIdentity>("getOrCreatePhysicalDeviceIdentity", It.IsAny<object[]>()))
            .ReturnsAsync(new PhysicalDeviceIdentity { DeviceId = "dev-alice", DeviceName = "Alice PC" });
        moduleMock.Setup(x => x.InvokeAsync<string[]>("prepareDirectRoomEndpoints", It.IsAny<object[]>()))
            .ReturnsAsync(["192.168.1.50:53000"]);
        moduleMock.Setup(x => x.InvokeAsync<bool>("connectToVoice", It.IsAny<object[]>())).ReturnsAsync(true);

        PhysicalRoomDirectSessionDescriptor? lastSyncedDescriptor = null;
        int syncCalls = 0;
        moduleMock
            .Setup(x => x.InvokeAsync<IJSVoidResult>("syncDirectRoomSession", It.IsAny<object[]>()))
            .Callback<string, object[]>((_, args) =>
            {
                syncCalls++;
                lastSyncedDescriptor = args[0] as PhysicalRoomDirectSessionDescriptor;
            })
            .Returns(new ValueTask<IJSVoidResult>(Mock.Of<IJSVoidResult>()));

        var voiceChannelMock = new Mock<VoiceChannelService>();
        voiceChannelMock
            .Setup(x => x.JoinVoiceAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, CancellationToken>((ch, uid, _) => _voiceState.NotifyVoiceMemberJoined(ch, uid, "dev-alice", "Alice PC"))
            .Returns(Task.CompletedTask);
        voiceChannelMock
            .Setup(x => x.LeaveVoiceAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, CancellationToken>((ch, uid, _) => _voiceState.NotifyVoiceMemberLeft(ch, uid))
            .Returns(Task.CompletedTask);

        // Bob is already connected with LAN endpoints
        _voiceState.NotifyVoiceMemberJoined("chan-lan", "emp-bob", "dev-bob", "Bob PC");
        _directService.UpdateParticipantEndpoints("chan-lan", "emp-bob", "dev-bob", ["192.168.1.51:53001"]);

        var jsInterop = new VoiceJsInteropService(jsMock.Object);
        var deviceService = new VoiceDeviceService(jsInterop, NullLogger<VoiceDeviceService>.Instance);
        await using var globalVoice = new GlobalVoiceService(
            jsInterop,
            voiceChannelMock.Object,
            _voiceState,
            new ChatStateService(),
            NullLogger<GlobalVoiceService>.Instance,
            deviceService,
            _roomService,
            _directService);

        await globalVoice.ConnectAsync(new VoiceConnectionOptions("tok", "ws://lk", "chan-lan", "emp-alice", 64, "rnnoise"));

        Assert.True(globalVoice.IsDesktopDirectRoomSupported);
        Assert.True(syncCalls >= 1);
        Assert.NotNull(lastSyncedDescriptor);
        Assert.Equal(PhysicalRoomAudioMode.LowLatencyCleaned, lastSyncedDescriptor!.AudioMode);
        Assert.Contains(lastSyncedDescriptor.Peers, p => p.UserId == "emp-bob" && p.Endpoints.Contains("192.168.1.51:53001"));

        // Switching room back to ServerCleanup sends null descriptor to stop native direct audio
        await _roomService.SetRoomAudioModeAsync(room.Id, PhysicalRoomAudioMode.ServerCleanup, "emp-alice");
        await globalVoice.RefreshPhysicalRoomStateAsync();
        Assert.Null(lastSyncedDescriptor);
    }

    [Fact]
    public async Task MixedClientRoom_UnmutedMobileRoommateSuspendsDirectLan_AndMutingOrLeavingRestoresDirectLan()
    {
        SeedEmployee("emp-charlie");
        SeedEmployee("emp-dave");

        var createRes = await _roomService.CreateRoomAsync("Mixed Studio", "emp-alice", "dev-alice", "Alice Desktop");
        var room = createRes.Value!;
        Assert.Equal(PhysicalRoomAudioMode.LowLatencyCleaned, room.AudioMode);

        await _roomService.EnrollUserDeviceAsync(room.Id, "emp-bob", "dev-bob", "Bob Desktop", muteUsersInMyRoom: false, actorUserId: "emp-bob");
        await _roomService.EnrollUserDeviceAsync(room.Id, "emp-charlie", "dev-charlie-phone", "Charlie Android", muteUsersInMyRoom: false, actorUserId: "emp-charlie");

        const string channelId = "chan-mixed";
        _voiceState.SetVoiceParticipantSessionContext(channelId, "emp-alice", "dev-alice", "Alice Desktop", "rnnoise", 25.0, "desktop", false);
        _voiceState.NotifyVoiceMemberJoined(channelId, "emp-alice", "dev-alice", "Alice Desktop");
        _directService.UpdateParticipantEndpoints(channelId, "emp-alice", "dev-alice", ["192.168.1.10:53000"]);

        _voiceState.SetVoiceParticipantSessionContext(channelId, "emp-bob", "dev-bob", "Bob Desktop", "rnnoise", 25.0, "desktop", false);
        _voiceState.NotifyVoiceMemberJoined(channelId, "emp-bob", "dev-bob", "Bob Desktop");
        _directService.UpdateParticipantEndpoints(channelId, "emp-bob", "dev-bob", ["192.168.1.11:53001"]);

        // 1. Outside Android caller (Dave, NOT enrolled in the room) joins -> Direct LAN between Alice & Bob remains active
        _voiceState.SetVoiceParticipantSessionContext(channelId, "emp-dave", "dev-dave-phone", "Dave Android", "rnnoise", 75.0, "android", false);
        _voiceState.NotifyVoiceMemberJoined(channelId, "emp-dave", "dev-dave-phone", "Dave Android");

        var descWithOutsideMobile = _directService.BuildSessionDescriptorForParticipant(channelId, "emp-alice", "dev-alice");
        Assert.NotNull(descWithOutsideMobile);
        Assert.False(descWithOutsideMobile!.IsSuspendedForMixedRoomClients);
        Assert.Single(descWithOutsideMobile.Peers);
        Assert.Equal("emp-bob", descWithOutsideMobile.Peers[0].UserId);

        // 2. Same-room Android roommate (Charlie, enrolled in room) joins unmuted -> Direct LAN suspends so all 3 roommates unify on ServerCleanup
        int peerChangedEvents = 0;
        _directService.DirectSessionPeersChanged += (ch, rm) =>
        {
            if (ch == channelId && rm == room.Id) peerChangedEvents++;
        };

        _voiceState.SetVoiceParticipantSessionContext(channelId, "emp-charlie", "dev-charlie-phone", "Charlie Android", "rnnoise", 75.0, "android", false);
        _voiceState.NotifyVoiceMemberJoined(channelId, "emp-charlie", "dev-charlie-phone", "Charlie Android");

        Assert.True(peerChangedEvents >= 1);
        var descWhileMixedUnmuted = _directService.BuildSessionDescriptorForParticipant(channelId, "emp-alice", "dev-alice");
        Assert.NotNull(descWhileMixedUnmuted);
        Assert.True(descWhileMixedUnmuted!.IsSuspendedForMixedRoomClients);
        Assert.Empty(descWhileMixedUnmuted.Peers);
        Assert.Equal(
            DirectRoomOperatingStatus.FallbackNonDesktopRoommateInRoom,
            _directService.EvaluateDesktopRoomFallbackStatus(channelId, "emp-alice", "dev-alice"));

        // 3. Charlie mutes their phone -> Direct LAN between Alice & Bob immediately resumes
        peerChangedEvents = 0;
        _voiceState.SetUserMuted(channelId, "emp-charlie", true);
        Assert.True(peerChangedEvents >= 1);

        var descWhileCharlieMuted = _directService.BuildSessionDescriptorForParticipant(channelId, "emp-alice", "dev-alice");
        Assert.NotNull(descWhileCharlieMuted);
        Assert.False(descWhileCharlieMuted!.IsSuspendedForMixedRoomClients);
        Assert.Single(descWhileCharlieMuted.Peers);

        // 4. Charlie unmutes then leaves the call -> Direct LAN resumes cleanly
        _voiceState.SetUserMuted(channelId, "emp-charlie", false);
        Assert.Empty(_directService.BuildSessionDescriptorForParticipant(channelId, "emp-alice", "dev-alice")!.Peers);

        _voiceState.NotifyVoiceMemberLeft(channelId, "emp-charlie");
        var descAfterCharlieLeaves = _directService.BuildSessionDescriptorForParticipant(channelId, "emp-alice", "dev-alice");
        Assert.NotNull(descAfterCharlieLeaves);
        Assert.False(descAfterCharlieLeaves!.IsSuspendedForMixedRoomClients);
        Assert.Single(descAfterCharlieLeaves.Peers);
    }

    [Fact]
    public async Task DesktopGlobalVoiceService_ShowsRoomWideFallback_WhenParticipantConnectedPrecedesVoiceMemberJoined_AndRestoresOnLeave()
    {
        SeedEmployee("emp-charlie");

        var room = (await _roomService.CreateRoomAsync(
            "Bureau Stewie",
            "emp-alice",
            "dev-alice",
            "Alice Desktop",
            audioMode: PhysicalRoomAudioMode.LowLatencyCleaned)).Value!;
        await _roomService.EnrollUserDeviceAsync(room.Id, "emp-bob", "dev-bob", "Bob Desktop", actorUserId: "emp-bob");
        await _roomService.EnrollUserDeviceAsync(room.Id, "emp-charlie", "dev-charlie-phone", "Charlie Android", actorUserId: "emp-charlie");

        const string channelId = "chan-race";

        var jsMock = new Mock<IJSRuntime>();
        var moduleMock = new Mock<IJSObjectReference>();
        jsMock.Setup(x => x.InvokeAsync<IJSObjectReference>("import", It.IsAny<object[]>()))
            .Returns(new ValueTask<IJSObjectReference>(moduleMock.Object));

        moduleMock.Setup(x => x.InvokeAsync<VoiceClientBootstrapInfo>("getClientVoiceBootstrap", It.IsAny<object[]>()))
            .ReturnsAsync(new VoiceClientBootstrapInfo
            {
                IsIOS = false,
                IsAndroid = false,
                IsNativePlatform = false,
                IsDesktopDirectRoomSupported = true,
                DeviceId = "dev-alice",
                DeviceName = "Alice Desktop",
                SavedNoiseFilter = "rnnoise"
            });
        moduleMock.Setup(x => x.InvokeAsync<string[]>("prepareDirectRoomEndpoints", It.IsAny<object[]>()))
            .ReturnsAsync(["192.168.1.50:53000"]);
        moduleMock.Setup(x => x.InvokeAsync<bool>("connectToVoice", It.IsAny<object[]>())).ReturnsAsync(true);

        PhysicalRoomDirectSessionDescriptor? lastSyncedDescriptor = null;
        moduleMock
            .Setup(x => x.InvokeAsync<IJSVoidResult>("syncDirectRoomSession", It.IsAny<object[]>()))
            .Callback<string, object[]>((_, args) =>
            {
                lastSyncedDescriptor = args[0] as PhysicalRoomDirectSessionDescriptor;
            })
            .Returns(new ValueTask<IJSVoidResult>(Mock.Of<IJSVoidResult>()));

        var voiceChannelMock = new Mock<VoiceChannelService>();
        voiceChannelMock
            .Setup(x => x.JoinVoiceAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, CancellationToken>((ch, uid, _) => _voiceState.NotifyVoiceMemberJoined(ch, uid, "dev-alice", "Alice Desktop"))
            .Returns(Task.CompletedTask);
        voiceChannelMock
            .Setup(x => x.LeaveVoiceAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, CancellationToken>((ch, uid, _) => _voiceState.NotifyVoiceMemberLeft(ch, uid))
            .Returns(Task.CompletedTask);

        // Bob Desktop is already in the room on the same LAN
        _voiceState.SetVoiceParticipantSessionContext(channelId, "emp-bob", "dev-bob", "Bob Desktop", "rnnoise", 25.0, "desktop", false);
        _voiceState.NotifyVoiceMemberJoined(channelId, "emp-bob", "dev-bob", "Bob Desktop");
        _directService.UpdateParticipantEndpoints(channelId, "emp-bob", "dev-bob", ["192.168.1.51:53001"]);

        var jsInterop = new VoiceJsInteropService(jsMock.Object);
        var deviceService = new VoiceDeviceService(jsInterop, NullLogger<VoiceDeviceService>.Instance);
        await using var globalVoice = new GlobalVoiceService(
            jsInterop,
            voiceChannelMock.Object,
            _voiceState,
            new ChatStateService(),
            NullLogger<GlobalVoiceService>.Instance,
            deviceService,
            _roomService,
            _directService);

        var activeTel = new DirectRoomClientTelemetry
        {
            ActiveDirectPeerUserIds = ["emp-bob"],
            EstimatedEndToEndMs = 18.2,
            MaxPeerRttMs = 1.1,
            JitterBufferMs = 4.0,
            AchievedBleedSuppressionDb = 24.5
        };

        await globalVoice.ConnectAsync(new VoiceConnectionOptions("tok", "ws://lk", channelId, "emp-alice", 64, "rnnoise"));
        globalVoice.OnDirectRoomAudioStatusChanged(activeTel);
        Assert.Equal(DirectRoomOperatingStatus.ActiveDirectLan, globalVoice.GetPhysicalRoomDirectStatus(room.Id));

        // Reproduce race: LiveKit OnParticipantConnected("emp-charlie") fires BEFORE Server NotifyVoiceMemberJoined
        globalVoice.OnParticipantConnected("emp-charlie");
        _voiceState.SetVoiceParticipantSessionContext(channelId, "emp-charlie", "dev-charlie-phone", "Charlie Android", "rnnoise", 75.0, "android", false);
        _voiceState.NotifyVoiceMemberJoined(channelId, "emp-charlie", "dev-charlie-phone", "Charlie Android");

        // Allow fire-and-forget RefreshPhysicalRoomStateAsync to complete
        await Task.Delay(40);

        // Even if an in-flight JS telemetry callback arrives while suspended, Desktop must display FallbackNonDesktopRoommateInRoom
        globalVoice.OnDirectRoomAudioStatusChanged(activeTel);
        Assert.Equal(DirectRoomOperatingStatus.FallbackNonDesktopRoommateInRoom, globalVoice.GetPhysicalRoomDirectStatus(room.Id));
        Assert.Null(globalVoice.DirectRoomTelemetry);
        Assert.NotNull(lastSyncedDescriptor);
        Assert.True(lastSyncedDescriptor!.IsSuspendedForMixedRoomClients);
        Assert.Empty(lastSyncedDescriptor.Peers);

        // Reproduce disconnect race: LiveKit OnParticipantDisconnected("emp-charlie") fires BEFORE Server NotifyVoiceMemberLeft
        globalVoice.OnParticipantDisconnected("emp-charlie");
        _voiceState.NotifyVoiceMemberLeft(channelId, "emp-charlie");
        await Task.Delay(40);

        Assert.NotNull(lastSyncedDescriptor);
        Assert.False(lastSyncedDescriptor!.IsSuspendedForMixedRoomClients);
        Assert.Single(lastSyncedDescriptor.Peers);
        Assert.Equal("emp-bob", lastSyncedDescriptor.Peers[0].UserId);
    }

    [Fact]
    public async Task RemoveParticipantEndpoints_SessionFencing_PreservesNewerSessionEndpoints()
    {
        var room = (await _roomService.CreateRoomAsync(
            "Fence Studio",
            "emp-alice",
            "dev-alice",
            "Alice Desktop",
            audioMode: PhysicalRoomAudioMode.LowLatencyCleaned)).Value!;
        await _roomService.EnrollUserDeviceAsync(room.Id, "emp-bob", "dev-bob", "Bob Desktop", actorUserId: "emp-bob");

        _voiceState.SetVoiceParticipantSessionContext("chan-fence", "emp-alice", "dev-alice", "Alice Desktop", "rnnoise", 25.0, "desktop", false);
        _voiceState.NotifyVoiceMemberJoined("chan-fence", "emp-alice", "dev-alice", "Alice Desktop", "sess-new");
        _voiceState.SetVoiceParticipantSessionContext("chan-fence", "emp-bob", "dev-bob", "Bob Desktop", "rnnoise", 25.0, "desktop", false);
        _voiceState.NotifyVoiceMemberJoined("chan-fence", "emp-bob", "dev-bob", "Bob Desktop", "sess-bob");

        _directService.UpdateParticipantEndpoints("chan-fence", "emp-alice", "dev-alice", ["192.168.1.10:53000"], voiceSessionId: "sess-new");
        _directService.UpdateParticipantEndpoints("chan-fence", "emp-bob", "dev-bob", ["192.168.1.11:53000"], voiceSessionId: "sess-bob");

        // Old disconnected Blazor circuit trying to clean up endpoints with its old session ID must be ignored
        _directService.RemoveParticipantEndpoints("chan-fence", "emp-alice", expectedSessionId: "sess-old");
        var afterStaleRemove = _directService.BuildSessionDescriptorForParticipant("chan-fence", "emp-bob", "dev-bob");
        Assert.NotNull(afterStaleRemove);
        Assert.Contains(afterStaleRemove!.Peers, p => p.UserId == "emp-alice" && p.Endpoints.Contains("192.168.1.10:53000"));

        // Matching session ID removes them cleanly
        _directService.RemoveParticipantEndpoints("chan-fence", "emp-alice", expectedSessionId: "sess-new");
        var afterMatchingRemove = _directService.BuildSessionDescriptorForParticipant("chan-fence", "emp-bob", "dev-bob");
        Assert.NotNull(afterMatchingRemove);
        Assert.DoesNotContain(afterMatchingRemove!.Peers, p => p.UserId == "emp-alice");
    }

    [Fact]
    public void DiagnosticReportFormatter_IncludesNativeWasapiAndPeerJitterTelemetry_AndHandlesLegacySnapshots()
    {
        var snapshot = new PhysicalRoomDiagnosticSnapshot(
            CapturedAtUtc: new DateTime(2026, 11, 19, 15, 0, 0, DateTimeKind.Utc),
            CaptureDurationSeconds: 10.0,
            SampleRate: 48000,
            Participants: [],
            Calibrations: [],
            Decompositions: [],
            TimelineBuckets: [],
            Warnings: []);

        var displayNames = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["emp-mats2"] = "mats2",
            ["emp-admin"] = "admin first"
        };

        var telemetry = new DirectRoomClientTelemetry
        {
            EstimatedEndToEndMs = 17.8,
            MaxPeerRttMs = 0.62,
            JitterBufferMs = 5.33,
            AchievedBleedSuppressionDb = 31.4,
            CapturePeriodMs = 10.0,
            PlaybackPeriodMs = 2.67,
            CaptureNativeRateHz = 48000,
            CaptureNativeChannels = 1,
            CaptureUsingAudioClient3 = false,
            CaptureRawMode = true,
            CaptureDiscontinuities = 0,
            PlaybackNativeRateHz = 48000,
            PlaybackNativeChannels = 2,
            PlaybackUsingAudioClient3 = true,
            LocalPeakDbfs = -14.2,
            RefUnderrunCount = 0,
            RefOverrunCount = 0,
            TxPacketCount = 3750,
            RxAudioPacketCount = 3750,
            AuthDropCount = 0,
            PeerDiagnostics = new Dictionary<string, DirectRoomPeerDiagnosticEntry>(StringComparer.Ordinal)
            {
                ["emp-mats2"] = new DirectRoomPeerDiagnosticEntry
                {
                    UserId = "emp-mats2",
                    RttMs = 0.62,
                    LagMs = 36.2,
                    LookaheadMs = 0.0,
                    BleedDb = -16.1,
                    Confidence = 0.88,
                    PeakInputDbfs = -11.5,
                    ClippedSamples = 0,
                    LevelerGainDb = 12.4,
                    BleedGatedPct = 4.0,
                    JitterUnderruns = 0,
                    JitterOverflows = 0,
                    JitterTrims = 1,
                    MaxBurstHops = 4
                },
                ["emp-admin"] = new DirectRoomPeerDiagnosticEntry
                {
                    UserId = "emp-admin",
                    RttMs = 0.68,
                    LagMs = -20.0,
                    LookaheadMs = 29.3,
                    BleedDb = -23.5,
                    Confidence = 0.91,
                    PeakInputDbfs = -4.2,
                    ClippedSamples = 0,
                    LevelerGainDb = 0.4,
                    BleedGatedPct = 18.0,
                    JitterUnderruns = 0,
                    JitterOverflows = 0,
                    JitterTrims = 2,
                    MaxBurstHops = 4
                }
            }
        };

        string report = AcousticRoomDiagnosticReportFormatter.FormatLiveRoomReport(
            snapshot,
            "Bureau Stewie",
            displayNames,
            configuredAudioMode: PhysicalRoomAudioMode.LowLatencyCleaned,
            directOperatingStatus: DirectRoomOperatingStatus.ActiveDirectLan,
            directClientTelemetry: telemetry);

        Assert.Contains("Capture=10.00ms (48000Hz 1ch, AC3=false, Raw=true, Disc=0)", report);
        Assert.Contains("Playback=2.67ms (48000Hz 2ch, AC3=true)", report);
        Assert.Contains("CausalLag=+36.2ms, Bleed=-16.1dB", report);
        Assert.Contains("CausalLag=-20.0ms, Lookahead=29.3ms, Bleed=-23.5dB", report);
        Assert.Contains("Jitter(Und=0, Ovf=0, Trim=1, MaxBurst=4)", report);

        // Legacy client telemetry (native rate/channels omitted = 0) formats cleanly without empty parentheticals
        var legacyTelemetry = new DirectRoomClientTelemetry
        {
            CapturePeriodMs = 2.67,
            PlaybackPeriodMs = 2.67
        };
        string legacyReport = AcousticRoomDiagnosticReportFormatter.FormatLiveRoomReport(
            snapshot,
            "Bureau Stewie",
            configuredAudioMode: PhysicalRoomAudioMode.LowLatencyCleaned,
            directOperatingStatus: DirectRoomOperatingStatus.ActiveDirectLan,
            directClientTelemetry: legacyTelemetry);

        Assert.Contains("WASAPIPeriods: Capture=2.67ms, Playback=2.67ms |", legacyReport);
    }
}
