using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Spokes_Server.Core.Configuration;
using Spokes_Server.Core.Constants;
using Spokes_Server.Core.Data;
using Spokes_Server.Core.Data.Repositories.Communication;
using Spokes_Server.Core.Data.Repositories.Core;
using Spokes_Server.Core.Data.Repositories.HR;
using Spokes_Server.Core.Data.Repositories.Projects;
using Spokes_Server.Core.Models.Communication;
using Spokes_Server.Core.Models.Core;
using Spokes_Server.Core.Models.HR;
using Spokes_Server.Core.Models.Projects;
using Spokes_Server.Core.Security;
using Spokes_Server.Core.Services.Communication.Chat;
using Spokes_Server.Core.Services.Communication.Email;
using Spokes_Server.Core.Services.Communication.Notifications;
using Spokes_Server.Core.Services.Communication.Voice;
using Spokes_Server.Core.Services.HR;
using Spokes_Server.Core.Services.Identity;
using Spokes_Server.Core.Services.Security;
using Xunit;

namespace Spokes_Server.Tests.Core.Services.Security;

public class SuspensionAndLiveReverificationTests : IDisposable
{
    private readonly string _tempDir;
    private readonly IConfiguration _config;
    private readonly DiskPersistenceService _persistence;

    private readonly EmployeeRepository _employees;
    private readonly OpenIdAccountRepository _openIdAccounts;
    private readonly CompanyProfileRepository _companyProfiles;
    private readonly TeamRepository _teams;
    private readonly DeviceSessionRepository _deviceSessions;
    private readonly SystemConfigRepository _systemConfigs;

    public SuspensionAndLiveReverificationTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"Spokes_SuspensionTests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);

        _config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DataPath"] = _tempDir,
                ["DatabasePath"] = _tempDir,
                ["SPOKES_MASTER_PASSWORD"] = "UnitTest-Master-Password-2026!"
            })
            .Build();

        _persistence = new DiskPersistenceService(NullLogger<DiskPersistenceService>.Instance);
        _employees = new EmployeeRepository(_persistence, _config);
        _openIdAccounts = new OpenIdAccountRepository(_persistence, _config);
        _companyProfiles = new CompanyProfileRepository(_persistence, _config);
        _teams = new TeamRepository(_persistence, _config);
        _deviceSessions = new DeviceSessionRepository(_persistence, _config);
        _systemConfigs = new SystemConfigRepository(_persistence, _config);
    }

    public void Dispose()
    {
        _persistence.Dispose();
        if (Directory.Exists(_tempDir))
        {
            try { Directory.Delete(_tempDir, true); } catch { }
        }
    }

    [Fact]
    public async Task SetUserSuspensionState_WhenSuspended_EvictsActiveVoiceParticipantAndFiresSessionEvent()
    {
        var sysCfg = _systemConfigs.Get();
        sysCfg.LiveKitApiKey = "lk-key";
        sysCfg.LiveKitApiSecret = "lk-secret-long-enough-for-jwt-signing-2026";
        _systemConfigs.Save(sysCfg);

        var emp = new Employee
        {
            Id = "emp-voice-1",
            FirstName = "Alice",
            LastName = "Voice",
            IsActive = true,
            Permissions = [AppPermissions.Chat.Use]
        };
        _employees.Save(emp);

        var channels = new ChatChannelRepository(_persistence, _config);
        var messages = new ChatMessageRepository(_persistence, _config, _companyProfiles);
        var readStates = new ChatReadStateRepository(_persistence, _config, _companyProfiles);
        var voiceState = new VoiceStateService();
        var chatState = new ChatStateService();

        var mockLiveKit = new Mock<LiveKitService>(NullLogger<LiveKitService>.Instance, _config);
        mockLiveKit
            .Setup(l => l.RemoveParticipantAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var voiceService = new VoiceChannelService(
            voiceState,
            chatState,
            messages,
            new Mock<INotificationRoutingService>().Object,
            _systemConfigs,
            mockLiveKit.Object,
            NullLogger<VoiceChannelService>.Instance,
            _employees,
            channels,
            readStates);

        voiceState.NotifyVoiceMemberJoined("chan-voice-1", emp.Id, "dev-1", "Desktop");
        Assert.Contains(emp.Id, voiceState.GetVoiceParticipants("chan-voice-1"));

        var sessionService = new SessionService(_deviceSessions, _employees, _systemConfigs);
        string? changedUserId = null;
        bool? changedState = null;
        sessionService.OnUserSuspensionChanged += (id, suspended) =>
        {
            changedUserId = id;
            changedState = suspended;
        };

        var userService = new UserService(
            new Mock<AuthenticationStateProvider>().Object,
            _employees,
            _openIdAccounts,
            _companyProfiles,
            _teams,
            sessionService,
            voiceService);

        userService.SetUserSuspensionState(emp.Id, true);

        // EvictUserFromAllVoiceChannelsAsync leaves VoiceState synchronously before awaiting LiveKit removal;
        // yield once so background continuation also finishes before verifying LiveKit call.
        await Task.Delay(50);

        Assert.Equal(emp.Id, changedUserId);
        Assert.True(changedState);
        Assert.True(_employees.GetById(emp.Id)?.IsSuspended);
        Assert.DoesNotContain(emp.Id, voiceState.GetVoiceParticipants("chan-voice-1"));
        mockLiveKit.Verify(l => l.RemoveParticipantAsync("chan-voice-1", emp.Id, "lk-key", "lk-secret-long-enough-for-jwt-signing-2026", It.IsAny<CancellationToken>()), Times.AtLeastOnce);
    }

    [Fact]
    public async Task PhysicalRoomDirectSession_BlocksSuspendedParticipantFromUpdatingEndpointsOrReceivingPeerKeys()
    {
        var roomRepo = new PhysicalVoiceRoomRepository(_persistence, _config);
        var voiceState = new VoiceStateService();
        var mockPrivacy = new Mock<IUserPrivacyService>();
        mockPrivacy.Setup(p => p.CanDiscoverUser(It.IsAny<Employee?>(), It.IsAny<Employee>())).Returns(true);

        var alice = new Employee { Id = "emp-alice", FirstName = "Alice", LastName = "Room", IsActive = true, Permissions = [AppPermissions.Chat.Use] };
        var bob = new Employee { Id = "emp-bob", FirstName = "Bob", LastName = "Room", IsActive = true, Permissions = [AppPermissions.Chat.Use] };
        _employees.Save(alice);
        _employees.Save(bob);

        var roomService = new PhysicalVoiceRoomService(
            roomRepo,
            voiceState,
            NullLogger<PhysicalVoiceRoomService>.Instance,
            _employees,
            new Mock<IVoiceBotService>().Object,
            userPrivacy: mockPrivacy.Object);

        using var directService = new PhysicalRoomDirectSessionService(
            roomService,
            voiceState,
            _employees,
            mockPrivacy.Object);

        var room = (await roomService.CreateRoomAsync("Lab", alice.Id, "dev-alice", "Alice PC", audioMode: PhysicalRoomAudioMode.LowLatencyCleaned)).Value!;
        await roomService.EnrollUserDeviceAsync(room.Id, bob.Id, "dev-bob", "Bob PC", actorUserId: bob.Id);

        voiceState.NotifyVoiceMemberJoined("chan-room-1", alice.Id, "dev-alice", "Alice PC");
        voiceState.NotifyVoiceMemberJoined("chan-room-1", bob.Id, "dev-bob", "Bob PC");

        Assert.True(directService.UpdateParticipantEndpoints("chan-room-1", alice.Id, "dev-alice", ["192.168.1.10:52001"]));
        Assert.NotNull(directService.BuildSessionDescriptorForParticipant("chan-room-1", alice.Id, "dev-alice"));

        // Suspend Alice while still technically present in memory/session structures
        alice.IsSuspended = true;
        _employees.Save(alice);

        Assert.False(directService.UpdateParticipantEndpoints("chan-room-1", alice.Id, "dev-alice", ["192.168.1.10:52002"]));
        Assert.Null(directService.BuildSessionDescriptorForParticipant("chan-room-1", alice.Id, "dev-alice"));
    }

    [Fact]
    public void UserService_TargetAndPermissionGroupGuards_DenySuspendedAdminAndPreserveNullGroupForActiveAdmin()
    {
        var activeAdmin = new Employee { Id = "adm-active", IsActive = true, IsAdmin = true };
        var suspendedAdmin = new Employee { Id = "adm-susp", IsActive = true, IsAdmin = true, IsSuspended = true };
        var suspendedManager = new Employee
        {
            Id = "mgr-susp",
            IsActive = true,
            IsSuspended = true,
            Permissions = [AppPermissions.Admin.ManageUsers, AppPermissions.Hr.ManageEmployees]
        };
        var target = new Employee { Id = "emp-target", IsActive = true };
        var group = new PermissionGroup { Id = "grp-1", Name = "Editors", Permissions = [AppPermissions.Projects.View] };

        Assert.True(UserService.CanManageEmployeeTarget(activeAdmin, target));
        Assert.True(UserService.CanAssignPermissionGroup(activeAdmin, group));
        Assert.True(UserService.CanAssignPermissionGroup(activeAdmin, null));

        Assert.False(UserService.CanManageEmployeeTarget(suspendedAdmin, target));
        Assert.False(UserService.CanManageEmployeeTarget(suspendedManager, target));
        Assert.False(UserService.CanAssignPermissionGroup(suspendedAdmin, group));
        Assert.False(UserService.CanAssignPermissionGroup(suspendedAdmin, null));
        Assert.False(UserService.CanAssignPermissionGroup(suspendedManager, group));
        Assert.False(UserService.CanAssignPermissionGroup(suspendedManager, null));
    }

    [Fact]
    public void TimesheetTimerService_EnforcedCtor_DeniesSuspendedMissingPermissionAndUnauthorizedProject()
    {
        var timesheets = new TimesheetRepository(_persistence, _config);
        var projects = new ProjectRepository(_persistence, _config);

        var assignedProject = new Project
        {
            Id = "proj-assigned",
            Name = "Assigned Restricted",
            AccessPolicy = ProjectAccessPolicy.Restricted,
            AllowedUserIds = ["emp-permitted"]
        };
        var restrictedUnassigned = new Project
        {
            Id = "proj-secret",
            Name = "Secret Restricted",
            AccessPolicy = ProjectAccessPolicy.Restricted,
            AllowedUserIds = ["someone-else"]
        };
        projects.Save(assignedProject);
        projects.Save(restrictedUnassigned);

        var permittedUser = new Employee
        {
            Id = "emp-permitted",
            IsActive = true,
            Permissions = [AppPermissions.Timesheets.AccessTimesheet]
        };
        var noPermUser = new Employee
        {
            Id = "emp-noperm",
            IsActive = true,
            Permissions = []
        };
        _employees.Save(permittedUser);
        _employees.Save(noPermUser);

        var timerService = new TimesheetTimerService(
            timesheets,
            _employees,
            projects,
            _teams,
            _config,
            NullLogger<TimesheetTimerService>.Instance);

        // 1. Missing Timesheets.AccessTimesheet permission denied
        var noPermStart = timerService.Start(noPermUser.Id, assignedProject.Id, "wt-1", null);
        Assert.False(noPermStart.IsSuccess);
        Assert.Equal("FORBIDDEN", noPermStart.Code);

        // 2. Unauthorized Restricted project denied even with Timesheets.AccessTimesheet
        var unauthorizedProjStart = timerService.Start(permittedUser.Id, restrictedUnassigned.Id, "wt-1", null);
        Assert.False(unauthorizedProjStart.IsSuccess);
        Assert.Equal("FORBIDDEN", unauthorizedProjStart.Code);

        // 3. Assigned project succeeds while active & unsuspended
        var validStart = timerService.Start(permittedUser.Id, assignedProject.Id, "wt-1", null);
        Assert.True(validStart.IsSuccess);
        Assert.True(timerService.GetState(permittedUser.Id).IsRunning);

        // 4. Suspending the employee blocks Start, Stop (no time entry logged), and Cancel
        timerService.GetState(permittedUser.Id).StartedAt = DateTime.UtcNow.AddMinutes(-15);
        permittedUser.IsSuspended = true;
        _employees.Save(permittedUser);

        var stopResult = timerService.Stop(permittedUser.Id);
        Assert.False(stopResult.IsSuccess);
        Assert.Equal("FORBIDDEN", stopResult.Code);
        Assert.True(timerService.GetState(permittedUser.Id).IsRunning);
        Assert.Empty(timesheets.GetAll());

        timerService.Cancel(permittedUser.Id);
        Assert.True(timerService.GetState(permittedUser.Id).IsRunning);

        var suspendedStart = timerService.Start("emp-noperm-or-susp", assignedProject.Id, "wt-1", null);
        Assert.False(suspendedStart.IsSuccess);

        noPermUser.IsSuspended = true;
        noPermUser.Permissions = [AppPermissions.Timesheets.AccessTimesheet];
        _employees.Save(noPermUser);
        var suspendedPermittedStart = timerService.Start(noPermUser.Id, assignedProject.Id, "wt-1", null);
        Assert.False(suspendedPermittedStart.IsSuccess);
        Assert.Equal("FORBIDDEN", suspendedPermittedStart.Code);
    }

    [Fact]
    public async Task EmailFolderService_RejectsSuspendedAdminOnServerManagedAccount()
    {
        var accounts = new EmailAccountRepository(_persistence, _config);
        var folders = new EmailFolderRepository(_persistence, _config);
        var messages = new EmailMessageRepository(_persistence, _config);
        var encryption = new EncryptionService(_config);
        var mockImapFactory = new Mock<IImapConnectionFactory>();

        var serverMailbox = new EmailAccount
        {
            Id = "acc-server-1",
            Name = "Shared Support Inbox",
            EmailAddress = "support@example.com",
            IsServerManaged = true,
            AllowedUserIds = []
        };
        accounts.Save(serverMailbox);

        var suspendedAdmin = new Employee
        {
            Id = "emp-admin-susp",
            IsActive = true,
            IsAdmin = true,
            IsSuspended = true
        };
        _employees.Save(suspendedAdmin);

        var folderService = new EmailFolderService(
            _companyProfiles,
            folders,
            messages,
            encryption,
            accounts,
            NullLogger<EmailFolderService>.Instance,
            mockImapFactory.Object,
            syncStateService: null,
            employees: _employees,
            teams: _teams);

        var createResult = await folderService.CreateFolderAsync(serverMailbox.Id, "", "Subfolder", suspendedAdmin.Id);
        var deleteResult = await folderService.DeleteFolderAsync(serverMailbox.Id, "Archive", suspendedAdmin.Id);

        Assert.False(createResult.IsSuccess);
        Assert.Equal("ACCESS_DENIED", createResult.Code);
        Assert.False(deleteResult.IsSuccess);
        Assert.Equal("ACCESS_DENIED", deleteResult.Code);
        mockImapFactory.Verify(f => f.CreateAuthenticatedClientAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task UserPrivacyService_RejectsSuspendedAndStaleCircuitCallersOnContactMutations()
    {
        var profile = _companyProfiles.Get();
        profile.Privacy.ApplyPreset(PrivacyPreset.Open);
        profile.Privacy.DiscoveryMethod = UserDiscoveryMethod.ExactEmailOnly;
        _companyProfiles.Save(profile);

        var persistedCaller = new Employee
        {
            Id = "emp-caller",
            FirstName = "Caller",
            LastName = "Persisted",
            Email = "caller@example.com",
            IsActive = true,
            IsSuspended = true,
            ContactUserIds = ["emp-target"]
        };
        var target = new Employee
        {
            Id = "emp-target",
            FirstName = "Target",
            LastName = "User",
            Email = "target@example.com",
            IsActive = true,
            ContactUserIds = ["emp-caller"]
        };
        var stranger = new Employee
        {
            Id = "emp-stranger",
            FirstName = "Stranger",
            LastName = "User",
            Email = "stranger@example.com",
            IsActive = true
        };
        _employees.Save(persistedCaller);
        _employees.Save(target);
        _employees.Save(stranger);

        // Simulate a stale in-memory circuit copy that still shows IsSuspended = false
        var staleCallerCopy = new Employee
        {
            Id = "emp-caller",
            FirstName = "Caller",
            LastName = "Persisted",
            Email = "caller@example.com",
            IsActive = true,
            IsSuspended = false,
            ContactUserIds = ["emp-target"]
        };

        var privacyService = new UserPrivacyService(_companyProfiles, _employees, _teams);

        var addResult = await privacyService.AddContactByIdentifierAsync(staleCallerCopy, "stranger@example.com");
        Assert.Equal(AddContactStatus.Forbidden, addResult.Status);
        Assert.DoesNotContain("emp-stranger", _employees.GetById("emp-caller")!.ContactUserIds);
        Assert.DoesNotContain("emp-caller", _employees.GetById("emp-stranger")!.ContactUserIds);

        var removed = await privacyService.RemoveContactAsync(staleCallerCopy, "emp-target");
        Assert.False(removed);
        Assert.Contains("emp-target", _employees.GetById("emp-caller")!.ContactUserIds);
        Assert.Contains("emp-caller", _employees.GetById("emp-target")!.ContactUserIds);
    }
}
