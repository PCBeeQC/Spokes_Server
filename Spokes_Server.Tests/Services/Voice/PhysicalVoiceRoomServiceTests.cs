using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Spokes_Server.Core.Constants;
using Spokes_Server.Core.Data;
using Spokes_Server.Core.Data.Repositories.Communication;
using Spokes_Server.Core.Data.Repositories.HR;
using Spokes_Server.Core.Models.Communication;
using Spokes_Server.Core.Models.HR;
using Spokes_Server.Core.Services.Communication.Chat;
using Spokes_Server.Core.Services.Communication.Voice;
using Spokes_Server.Core.Services.Communication.Voice.Pipeline;
using Spokes_Server.Core.Services.Security;
using Xunit;

namespace Spokes_Server.Tests.Services.Voice;

public class PhysicalVoiceRoomServiceTests : IDisposable
{
    private readonly string _tempDir;
    private readonly DiskPersistenceService _persistence;
    private readonly PhysicalVoiceRoomRepository _roomRepo;
    private readonly EmployeeRepository _employeeRepo;
    private readonly VoiceStateService _voiceState;
    private readonly Mock<IVoiceBotService> _voiceBotMock;
    private readonly Mock<IUserPrivacyService> _privacyMock;
    private readonly PhysicalVoiceRoomService _service;

    public PhysicalVoiceRoomServiceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "spokes_phys_rooms_test_" + Guid.NewGuid().ToString("N"));
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

        _service = new PhysicalVoiceRoomService(
            _roomRepo,
            _voiceState,
            NullLogger<PhysicalVoiceRoomService>.Instance,
            _employeeRepo,
            _voiceBotMock.Object,
            userPrivacy: _privacyMock.Object);

        SeedChatEmployee("emp-1");
        SeedChatEmployee("emp-2");
        SeedChatEmployee("emp-dad");
        SeedChatEmployee("emp-mats");
        SeedChatEmployee("emp-admin", isAdmin: true);
    }

    private Employee SeedChatEmployee(
        string id,
        bool isAdmin = false,
        bool isActive = true,
        bool isSuspended = false,
        bool isBanned = false,
        bool hasChatUse = true)
    {
        var emp = new Employee
        {
            Id = id,
            FirstName = "User",
            LastName = id,
            IsAdmin = isAdmin,
            IsActive = isActive,
            IsSuspended = isSuspended,
            IsBanned = isBanned,
            Permissions = hasChatUse ? [AppPermissions.Chat.Use] : []
        };
        _employeeRepo.Save(emp);
        return emp;
    }

    public void Dispose()
    {
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
    public async Task CreateAndRenameRoom_PersistsToJsonRepository()
    {
        var createRes = await _service.CreateRoomAsync("Family Computer Room", "emp-1");
        Assert.True(createRes.IsSuccess);
        Assert.NotNull(createRes.Value);
        Assert.Equal("Family Computer Room", createRes.Value!.Name);

        // Verify duplicate name is rejected
        var dupRes = await _service.CreateRoomAsync("family computer room", "emp-1");
        Assert.False(dupRes.IsSuccess);

        // Verify rename by room creator persists to repository
        var renameRes = await _service.RenameRoomAsync(createRes.Value.Id, "Upstairs Gaming Room", "emp-1");
        Assert.True(renameRes.IsSuccess);

        var reloaded = _roomRepo.GetById(createRes.Value.Id);
        Assert.NotNull(reloaded);
        Assert.Equal("Upstairs Gaming Room", reloaded!.Name);

        // Non-creator non-admin cannot rename the room
        SeedChatEmployee("emp-other", isAdmin: false);
        var unauthorizedRename = await _service.RenameRoomAsync(createRes.Value.Id, "Hacked Room", "emp-other");
        Assert.False(unauthorizedRename.IsSuccess);
        Assert.Equal("FORBIDDEN", unauthorizedRename.Code);

        // Admin can rename the room
        var adminRename = await _service.RenameRoomAsync(createRes.Value.Id, "Basement LAN Room", "emp-admin");
        Assert.True(adminRename.IsSuccess);
        Assert.Equal("Basement LAN Room", _roomRepo.GetById(createRes.Value.Id)!.Name);
    }

    [Fact]
    public async Task EnrollDevice_ScopesStrictlyToEmployeeAndDeviceId_LeavingMobileDeviceOutOfRoom()
    {
        var room = (await _service.CreateRoomAsync("Office Room", "emp-dad")).Value!;

        // Enroll Dad on his Gaming PC ("dev-pc-1"), NOT his phone ("dev-phone-1")
        var enrollRes = await _service.EnrollUserDeviceAsync(room.Id, "emp-dad", "dev-pc-1", "Dad Gaming PC", actorUserId: "emp-dad");
        Assert.True(enrollRes.IsSuccess);

        // "Mute users in my room" is disabled by default
        var enrollment = _service.GetEnrollment("emp-dad", "dev-pc-1");
        Assert.NotNull(enrollment);
        Assert.False(enrollment!.MuteUsersInMyRoom);

        // Dad on his PC is in the room
        var pcRoom = _service.GetRoomForUserDevice("emp-dad", "dev-pc-1");
        Assert.NotNull(pcRoom);
        Assert.Equal(room.Id, pcRoom!.Id);

        // Dad on his phone is NOT in the room
        var phoneRoom = _service.GetRoomForUserDevice("emp-dad", "dev-phone-1");
        Assert.Null(phoneRoom);

        // Another user logging into Dad's PC ("dev-pc-1") is NOT automatically in the room unless enrolled for their own EmployeeId
        var guestOnPcRoom = _service.GetRoomForUserDevice("emp-guest", "dev-pc-1");
        Assert.Null(guestOnPcRoom);
    }

    [Fact]
    public async Task GetActiveCoLocatedRoomGroups_OnlyActivatesWhenTwoOrMoreEnrolledDevicesInSameRoomJoinChannel()
    {
        var room = (await _service.CreateRoomAsync("Basement LAN", "emp-1")).Value!;
        await _service.EnrollUserDeviceAsync(room.Id, "emp-1", "dev-pc-1", "PC 1", muteUsersInMyRoom: true, actorUserId: "emp-1");
        await _service.EnrollUserDeviceAsync(room.Id, "emp-2", "dev-pc-2", "PC 2", muteUsersInMyRoom: true, actorUserId: "emp-2");

        // Only 1 enrolled user in channel -> pipeline group should NOT be active
        _voiceState.NotifyVoiceMemberJoined("chan-1", "emp-1", "dev-pc-1", "PC 1");
        var singleGroup = _service.GetActiveCoLocatedRoomGroups("chan-1");
        Assert.Empty(singleGroup);

        // Second user joins on their mobile phone (not enrolled) -> still should NOT activate
        _voiceState.NotifyVoiceMemberJoined("chan-1", "emp-2", "dev-mobile-2", "iPhone");
        Assert.Empty(_service.GetActiveCoLocatedRoomGroups("chan-1"));

        // Second user joins on their enrolled PC -> now >= 2 enrolled devices are in the channel, so it activates!
        _voiceState.SetVoiceParticipantDevice("chan-1", "emp-2", "dev-pc-2", "PC 2");
        var activeGroups = _service.GetActiveCoLocatedRoomGroups("chan-1");
        Assert.Single(activeGroups);
        Assert.Equal(2, activeGroups[room.Id].ActiveUserIds.Count);
    }

    [Fact]
    public async Task SetMuteUsersInMyRoom_UpdatesEnrollmentAndReturnsActiveRoommates()
    {
        var room = (await _service.CreateRoomAsync("Living Room", "emp-1")).Value!;
        await _service.EnrollUserDeviceAsync(room.Id, "emp-1", "dev-pc-1", "PC 1", muteUsersInMyRoom: true, actorUserId: "emp-1");
        await _service.EnrollUserDeviceAsync(room.Id, "emp-2", "dev-pc-2", "PC 2", muteUsersInMyRoom: true, actorUserId: "emp-2");

        _voiceState.NotifyVoiceMemberJoined("chan-1", "emp-1", "dev-pc-1", "PC 1");
        _voiceState.NotifyVoiceMemberJoined("chan-1", "emp-2", "dev-pc-2", "PC 2");

        var roommates = _service.GetActiveRoommatesInChannel("chan-1", "emp-1", "dev-pc-1");
        Assert.Single(roommates);
        Assert.Contains("emp-2", roommates);

        var toggleRes = await _service.SetMuteUsersInMyRoomAsync("emp-1", "dev-pc-1", muteUsersInMyRoom: false, actorUserId: "emp-1");
        Assert.True(toggleRes.IsSuccess);

        var updatedEnrollment = _service.GetEnrollment("emp-1", "dev-pc-1");
        Assert.NotNull(updatedEnrollment);
        Assert.False(updatedEnrollment!.MuteUsersInMyRoom);
    }

    [Fact]
    public async Task AdminAssignParticipantDeviceToRoom_EnforcesChatAdminPermissions_AndSupportsAssignAndUnassign()
    {
        SeedChatEmployee("emp-regular", isAdmin: false);
        SeedChatEmployee("emp-target", isAdmin: false);

        var room = (await _service.CreateRoomAsync("LAN Room", "emp-admin")).Value!;
        _voiceState.NotifyVoiceMemberJoined("chan-1", "emp-target", "dev-target-pc", "Target PC");

        // Regular user without Moderator / EditAllPublicChannels / Admin cannot assign another user's device
        var forbiddenRes = await _service.AdminAssignParticipantDeviceToRoomAsync("emp-regular", "emp-target", "dev-target-pc", "Target PC", room.Id);
        Assert.False(forbiddenRes.IsSuccess);
        Assert.Equal("FORBIDDEN", forbiddenRes.Code);

        // Admin user can assign target user's active device to the room
        var allowedRes = await _service.AdminAssignParticipantDeviceToRoomAsync("emp-admin", "emp-target", "dev-target-pc", "Target PC", room.Id);
        Assert.True(allowedRes.IsSuccess);

        var assignedRoom = _service.GetRoomForUserDevice("emp-target", "dev-target-pc");
        Assert.NotNull(assignedRoom);
        Assert.Equal(room.Id, assignedRoom!.Id);
        _voiceBotMock.Verify(b => b.NotifyRoomConfigurationChangedAsync(null, It.IsAny<CancellationToken>()), Times.AtLeastOnce);

        // Admin user can also unassign (targetRoomId: null) end-to-end
        var unassignRes = await _service.AdminAssignParticipantDeviceToRoomAsync("emp-admin", "emp-target", "dev-target-pc", "Target PC", null);
        Assert.True(unassignRes.IsSuccess);
        Assert.Null(_service.GetRoomForUserDevice("emp-target", "dev-target-pc"));
    }

    [Fact]
    public async Task SaveLearnedPairCalibration_PersistsToRepositoryAndCleansUpOnUnenroll()
    {
        var room = (await _service.CreateRoomAsync("Bureau", "emp-admin")).Value!;
        await _service.EnrollUserDeviceAsync(room.Id, "emp-admin", "dev-pc", "Windows PC", actorUserId: "emp-admin");
        await _service.EnrollUserDeviceAsync(room.Id, "emp-mats", "dev-android", "Android Device", actorUserId: "emp-mats");

        _service.SaveLearnedPairCalibration(room.Id, new PhysicalRoomSavedPairCalibration
        {
            SourceEmployeeId = "emp-admin",
            SourceDeviceId = "dev-pc",
            TargetEmployeeId = "emp-mats",
            TargetDeviceId = "dev-android",
            SourceFilterType = "rnnoise",
            TargetFilterType = "webrtc",
            DelaySamples = -6240,
            BleedGain = 0.278f,
            Confidence = 0.72f
        });

        var persistedRoom = _roomRepo.GetById(room.Id);
        Assert.NotNull(persistedRoom);
        Assert.Single(persistedRoom!.SavedCalibrations);
        Assert.Equal(-6240, persistedRoom.SavedCalibrations[0].DelaySamples);
        Assert.Equal("rnnoise", persistedRoom.SavedCalibrations[0].SourceFilterType);

        // Removing device from room cleans up its saved calibrations
        var removeRes = await _service.RemoveUserDeviceAsync("emp-mats", "dev-android", actorUserId: "emp-mats");
        Assert.True(removeRes.IsSuccess);
        var afterRemove = _roomRepo.GetById(room.Id);
        Assert.NotNull(afterRemove);
        Assert.Empty(afterRemove!.SavedCalibrations);
    }

    [Fact]
    public async Task EnrollRemoveAndMute_WithMismatchedOrMissingActorUserId_ReturnsForbidden()
    {
        SeedChatEmployee("emp-a", isAdmin: false);
        SeedChatEmployee("emp-b", isAdmin: false);
        SeedChatEmployee("emp-c", isAdmin: false);
        SeedChatEmployee("emp-no-chat", isAdmin: false, hasChatUse: false);

        var room = (await _service.CreateRoomAsync("Shared Room", "emp-a")).Value!;

        // Missing/empty actorUserId is strictly rejected across all mutations
        Assert.Equal("FORBIDDEN", (await _service.CreateRoomAsync("Anon Room", "")).Code);
        Assert.Equal("FORBIDDEN", (await _service.EnrollUserDeviceAsync(room.Id, "emp-b", "dev-b", "Bob PC", actorUserId: null)).Code);
        Assert.Equal("FORBIDDEN", (await _service.RemoveUserDeviceAsync("emp-b", "dev-b", actorUserId: "")).Code);
        Assert.Equal("FORBIDDEN", (await _service.SetMuteUsersInMyRoomAsync("emp-b", "dev-b", true, actorUserId: "   ")).Code);

        // User lacking AppPermissions.Chat.Use is rejected
        Assert.Equal("FORBIDDEN", (await _service.CreateRoomAsync("NoChat Room", "emp-no-chat")).Code);
        Assert.Equal("FORBIDDEN", (await _service.EnrollUserDeviceAsync(room.Id, "emp-no-chat", "dev-nc", "PC", actorUserId: "emp-no-chat")).Code);

        // User B can self-enroll their own device with one click (PIN-free) when actorUserId == "emp-b"
        var selfEnroll = await _service.EnrollUserDeviceAsync(room.Id, "emp-b", "dev-b", "Bob PC", actorUserId: "emp-b");
        Assert.True(selfEnroll.IsSuccess);

        // User C (non-admin, non-creator, non-owner) cannot enroll, mute, or remove User B's device
        var crossEnroll = await _service.EnrollUserDeviceAsync(room.Id, "emp-b", "dev-b2", "Fake PC", actorUserId: "emp-c");
        Assert.False(crossEnroll.IsSuccess);
        Assert.Equal("FORBIDDEN", crossEnroll.Code);

        var crossMute = await _service.SetMuteUsersInMyRoomAsync("emp-b", "dev-b", true, actorUserId: "emp-c");
        Assert.False(crossMute.IsSuccess);
        Assert.Equal("FORBIDDEN", crossMute.Code);

        var crossRemove = await _service.RemoveUserDeviceAsync("emp-b", "dev-b", actorUserId: "emp-c");
        Assert.False(crossRemove.IsSuccess);
        Assert.Equal("FORBIDDEN", crossRemove.Code);
    }

    [Fact]
    public async Task SuspendedOrBannedUser_IsBlockedAcrossAllPhysicalRoomMutations()
    {
        var creator = SeedChatEmployee("emp-creator-susp");
        var room = (await _service.CreateRoomAsync("Creator Room", creator.Id, "dev-c1", "Creator PC")).Value!;

        // Suspend creator (even if promoted to IsAdmin = true, IsSuspended blocks all writes!)
        creator.IsAdmin = true;
        creator.IsSuspended = true;
        _employeeRepo.Save(creator);

        var banned = SeedChatEmployee("emp-banned", isBanned: true);

        Assert.False(_service.CanUserEditPhysicalRoom(room, creator.Id));
        Assert.False(_service.CanUserManagePhysicalRooms(creator.Id));
        Assert.Equal("FORBIDDEN", (await _service.CreateRoomAsync("New Room", creator.Id)).Code);
        Assert.Equal("FORBIDDEN", (await _service.RenameRoomAsync(room.Id, "Renamed", creator.Id)).Code);
        Assert.Equal("FORBIDDEN", (await _service.DeleteRoomAsync(room.Id, creator.Id)).Code);
        Assert.Equal("FORBIDDEN", (await _service.EnrollUserDeviceAsync(room.Id, creator.Id, "dev-c2", "PC2", actorUserId: creator.Id)).Code);
        Assert.Equal("FORBIDDEN", (await _service.SetMuteUsersInMyRoomAsync(creator.Id, "dev-c1", true, actorUserId: creator.Id)).Code);
        Assert.Equal("FORBIDDEN", (await _service.RemoveUserDeviceAsync(creator.Id, "dev-c1", actorUserId: creator.Id)).Code);
        Assert.Equal("FORBIDDEN", (await _service.AdminAssignParticipantDeviceToRoomAsync(creator.Id, "emp-1", "dev-1", "PC", room.Id)).Code);
        Assert.Equal("FORBIDDEN", (await _service.CreateRoomAsync("Banned Room", banned.Id)).Code);
    }

    [Fact]
    public async Task EnrollUserDevice_ClampsOversizedDeviceIdDeviceNameAndRoomName()
    {
        string oversizedRoomName = new('R', 200);
        string oversizedDeviceId = new('D', 300);
        string oversizedDeviceName = new('N', 300);

        var createRes = await _service.CreateRoomAsync(oversizedRoomName, "emp-1");
        Assert.True(createRes.IsSuccess);
        Assert.Equal(PhysicalVoiceRoomService.MaxRoomNameLength, createRes.Value!.Name.Length);

        var enrollRes = await _service.EnrollUserDeviceAsync(
            createRes.Value.Id,
            "emp-1",
            oversizedDeviceId,
            oversizedDeviceName,
            actorUserId: "emp-1");
        Assert.True(enrollRes.IsSuccess);

        var savedRoom = _roomRepo.GetById(createRes.Value.Id)!;
        var savedDevice = Assert.Single(savedRoom.EnrolledDevices);
        Assert.Equal(PhysicalVoiceRoomService.MaxDeviceIdLength, savedDevice.DeviceId.Length);
        Assert.Equal(PhysicalVoiceRoomService.MaxDeviceNameLength, savedDevice.DeviceName.Length);

        // Lookup with either clamped or unclamped deviceId resolves cleanly
        Assert.NotNull(_service.GetEnrollment("emp-1", oversizedDeviceId));
        Assert.NotNull(_service.GetRoomForUserDevice("emp-1", oversizedDeviceId));
    }

    [Fact]
    public async Task GetRoomsForUser_And_Diagnostics_RedactUnrelatedRoomDevicesAndRespectUserPrivacy()
    {
        var alice = SeedChatEmployee("emp-alice");
        var hiddenBob = SeedChatEmployee("emp-hidden-bob");
        var outsiderCarol = SeedChatEmployee("emp-carol");

        // Configure strict privacy: Alice and Carol CANNOT discover hiddenBob
        _privacyMock
            .Setup(p => p.CanDiscoverUser(It.Is<Employee?>(c => c != null && c.Id != "emp-admin"), It.Is<Employee>(t => t.Id == hiddenBob.Id)))
            .Returns(false);

        var room = (await _service.CreateRoomAsync("Gaming Den", "emp-admin")).Value!;
        await _service.EnrollUserDeviceAsync(room.Id, alice.Id, "dev-alice", "Alice Rig", actorUserId: alice.Id);
        await _service.EnrollUserDeviceAsync(room.Id, hiddenBob.Id, "dev-bob-secret", "Bob Secret Laptop", actorUserId: hiddenBob.Id);

        _voiceState.NotifyVoiceMemberJoined("chan-diag", alice.Id, "dev-alice", "Alice Rig");
        _voiceState.NotifyVoiceMemberJoined("chan-diag", hiddenBob.Id, "dev-bob-secret", "Bob Secret Laptop");

        // Carol (not enrolled in room, not creator/admin): still sees room Id & Name for PIN-free selection, but EnrolledDevices is redacted!
        var carolRooms = _service.GetRoomsForUser(outsiderCarol.Id);
        var carolProjectedRoom = Assert.Single(carolRooms, r => r.Id == room.Id);
        Assert.Equal("Gaming Den", carolProjectedRoom.Name);
        Assert.Empty(carolProjectedRoom.EnrolledDevices);

        // Carol cannot view or reset room diagnostics
        var carolSnap = _service.GetRoomDiagnosticSnapshot("chan-diag", room.Id, actorUserId: outsiderCarol.Id);
        Assert.Empty(carolSnap.Participants);
        Assert.False(_service.ResetRoomDiagnosticCounters("chan-diag", room.Id, actorUserId: outsiderCarol.Id));
        Assert.Empty(_service.GetRoomDiagnosticSnapshot("chan-diag", room.Id, actorUserId: null).Participants);

        // Alice (enrolled in room): sees her own device, while hiddenBob's device and diagnostic telemetry are filtered out by IUserPrivacyService!
        var aliceRooms = _service.GetRoomsForUser(alice.Id);
        var aliceProjectedRoom = Assert.Single(aliceRooms, r => r.Id == room.Id);
        var visibleDevice = Assert.Single(aliceProjectedRoom.EnrolledDevices);
        Assert.Equal(alice.Id, visibleDevice.EmployeeId);

        var aliceSnap = _service.GetRoomDiagnosticSnapshot("chan-diag", room.Id, actorUserId: alice.Id);
        var visibleDiagParticipant = Assert.Single(aliceSnap.Participants);
        Assert.Equal(alice.Id, visibleDiagParticipant.UserId);

        // Full Admin sees both Alice and hiddenBob
        var adminRooms = _service.GetRoomsForUser("emp-admin");
        var adminProjectedRoom = Assert.Single(adminRooms, r => r.Id == room.Id);
        Assert.Equal(2, adminProjectedRoom.EnrolledDevices.Count);
        var adminSnap = _service.GetRoomDiagnosticSnapshot("chan-diag", room.Id, actorUserId: "emp-admin");
        Assert.Equal(2, adminSnap.Participants.Count);
    }

    [Fact]
    public async Task SetRoomAudioMode_PersistsAcrossDiskReload_ProjectsToUsers_AndEnforcesPermissions()
    {
        var creator = SeedChatEmployee("emp-mode-creator");
        var nonCreator = SeedChatEmployee("emp-mode-member");

        var createRes = await _service.CreateRoomAsync("Studio LAN", creator.Id, "dev-creator", "Creator Desktop");
        Assert.True(createRes.IsSuccess);
        var roomId = createRes.Value!.Id;
        Assert.Equal(PhysicalRoomAudioMode.LowLatencyCleaned, createRes.Value.AudioMode);

        // Non-creator non-admin cannot change the room audio mode
        var forbiddenRes = await _service.SetRoomAudioModeAsync(roomId, PhysicalRoomAudioMode.ServerCleanup, nonCreator.Id);
        Assert.False(forbiddenRes.IsSuccess);
        Assert.Equal("FORBIDDEN", forbiddenRes.Code);

        // Room creator can switch to ServerCleanup
        var cleanupRes = await _service.SetRoomAudioModeAsync(roomId, PhysicalRoomAudioMode.ServerCleanup, creator.Id);
        Assert.True(cleanupRes.IsSuccess);
        Assert.Equal(PhysicalRoomAudioMode.ServerCleanup, cleanupRes.Value!.AudioMode);

        // Undefined / legacy numeric audio mode (2 = historical LowLatencyRaw) is rejected by API validation
        var invalidModeRes = await _service.SetRoomAudioModeAsync(roomId, (PhysicalRoomAudioMode)2, "emp-admin");
        Assert.False(invalidModeRes.IsSuccess);
        Assert.Equal("VALIDATION_ERROR", invalidModeRes.Code);

        // Admin switching back to LowLatencyCleaned persists LowLatencyCleaned
        var cleanedRes = await _service.SetRoomAudioModeAsync(roomId, PhysicalRoomAudioMode.LowLatencyCleaned, "emp-admin");
        Assert.True(cleanedRes.IsSuccess);
        Assert.Equal(PhysicalRoomAudioMode.LowLatencyCleaned, cleanedRes.Value!.AudioMode);

        // Legacy persisted JSON containing "AudioMode": 2 safely normalizes to LowLatencyCleaned on deserialization
        var legacyJsonRoom = System.Text.Json.JsonSerializer.Deserialize<PhysicalVoiceRoom>("""{"Id":"legacy-1","Name":"Legacy Room","AudioMode":2}""");
        Assert.NotNull(legacyJsonRoom);
        Assert.Equal(PhysicalRoomAudioMode.LowLatencyCleaned, legacyJsonRoom!.AudioMode);

        // Projected rooms include AudioMode for both members and non-members
        var projected = Assert.Single(_service.GetRoomsForUser(nonCreator.Id), r => r.Id == roomId);
        Assert.Equal(PhysicalRoomAudioMode.LowLatencyCleaned, projected.AudioMode);

        // Switch back to ServerCleanup to verify non-default mode round-trip persistence across disk reload
        var persistCleanupRes = await _service.SetRoomAudioModeAsync(roomId, PhysicalRoomAudioMode.ServerCleanup, "emp-admin");
        Assert.True(persistCleanupRes.IsSuccess);

        // Flush DiskPersistenceService and reload into a fresh repository to verify JSON round-trip persistence
        await _persistence.FlushAllAsync();
        var reloadedConfig = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DataPath"] = _tempDir, ["SPOKES_MASTER_PASSWORD"] = "UnitTest-Master-Password-2026!",
                ["DatabasePath"] = _tempDir
            })
            .Build();
        var freshRepo = new PhysicalVoiceRoomRepository(_persistence, reloadedConfig);
        freshRepo.LoadFromDisk();

        var diskReloadedRoom = freshRepo.GetById(roomId);
        Assert.NotNull(diskReloadedRoom);
        Assert.Equal(PhysicalRoomAudioMode.ServerCleanup, diskReloadedRoom!.AudioMode);
    }

    [Fact]
    public async Task PreviewClientPhysicalRoomStateForJoin_ComputesCoLocatedGroupAndMutedRoommates_BeforeJoinVoiceRuns()
    {
        var alice = SeedChatEmployee("emp-prev-alice");
        var bob = SeedChatEmployee("emp-prev-bob");
        var outsider = SeedChatEmployee("emp-prev-out");

        var createRes = await _service.CreateRoomAsync("Preview Room", alice.Id, "dev-alice", "Alice PC");
        Assert.True(createRes.IsSuccess);
        await _service.EnrollUserDeviceAsync(createRes.Value!.Id, bob.Id, "dev-bob", "Bob PC", muteUsersInMyRoom: true, actorUserId: bob.Id);

        // Alice and an outsider are already in the voice channel; Bob has NOT called JoinVoiceAsync yet
        _voiceState.NotifyVoiceMemberJoined("chan-prev", alice.Id, "dev-alice", "Alice PC");
        _voiceState.NotifyVoiceMemberJoined("chan-prev", outsider.Id, "dev-out", "Outsider Phone");

        var (coLocated, mutedRoommates) = _service.PreviewClientPhysicalRoomStateForJoin("chan-prev", bob.Id, "dev-bob");

        Assert.Contains(alice.Id, coLocated);
        Assert.Contains(bob.Id, coLocated);
        Assert.DoesNotContain(outsider.Id, coLocated);
        Assert.Equal([alice.Id], mutedRoommates);
    }

    [Fact]
    public void ResolveEffectiveCaptureLatencyMs_UsesAndroidNativeLowLatencyFloor_InsteadOfWebViewFloor()
    {
        double nativeRnnoise = PhysicalRoomPipelineStage.ResolveEffectiveCaptureLatencyMs(
            reportedLatencyMs: 0.0,
            platform: "android-native",
            deviceName: "Pixel Phone (Spokes App)",
            filterType: "rnnoise");
        Assert.Equal(35.0, nativeRnnoise);

        double nativeWebRtc = PhysicalRoomPipelineStage.ResolveEffectiveCaptureLatencyMs(
            reportedLatencyMs: 28.0,
            platform: "android-native",
            deviceName: "Android Mobile",
            filterType: "webrtc");
        Assert.Equal(35.0, nativeWebRtc);

        double legacyAndroidWebView = PhysicalRoomPipelineStage.ResolveEffectiveCaptureLatencyMs(
            reportedLatencyMs: 0.0,
            platform: "android",
            deviceName: "Android Chrome",
            filterType: "rnnoise");
        Assert.Equal(80.3, legacyAndroidWebView);
    }

    [Fact]
    public async Task DeleteRoomAsync_AllowsCreatorAndAdmins_BlocksOthers_AndPersistsDeletionAcrossReload()
    {
        var creator = SeedChatEmployee("emp-del-creator");
        var otherMember = SeedChatEmployee("emp-del-member");
        var moderator = new Employee
        {
            Id = "emp-del-mod",
            FirstName = "Mod",
            LastName = "User",
            IsActive = true,
            Permissions = [AppPermissions.Chat.Use, AppPermissions.Chat.Moderator]
        };
        _employeeRepo.Save(moderator);
        var settingsManager = new Employee
        {
            Id = "emp-del-mgr",
            FirstName = "Settings",
            LastName = "Manager",
            IsActive = true,
            Permissions = [AppPermissions.Chat.Use, AppPermissions.Admin.ManageSettings]
        };
        _employeeRepo.Save(settingsManager);

        // 1. Creator creates a room without enrolling any device, then deletes it cleanly
        var creatorRoom = (await _service.CreateRoomAsync("Creator Empty Room", creator.Id)).Value!;
        Assert.Contains(_service.GetRoomsForUser(creator.Id), r => r.Id == creatorRoom.Id);

        // Non-creator regular user and Chat.Moderator cannot delete someone else's room
        Assert.Equal("FORBIDDEN", (await _service.DeleteRoomAsync(creatorRoom.Id, otherMember.Id)).Code);
        Assert.Equal("FORBIDDEN", (await _service.DeleteRoomAsync(creatorRoom.Id, moderator.Id)).Code);

        int changedCount = 0;
        _voiceState.PhysicalRoomsChanged += () => changedCount++;

        var creatorDelete = await _service.DeleteRoomAsync(creatorRoom.Id, creator.Id);
        Assert.True(creatorDelete.IsSuccess);
        Assert.Null(_service.GetRoomById(creatorRoom.Id));
        Assert.True(changedCount >= 1);
        _voiceBotMock.Verify(b => b.NotifyRoomConfigurationChangedAsync(null, It.IsAny<CancellationToken>()), Times.AtLeastOnce);

        // Deleting an already-deleted room returns NOT_FOUND
        Assert.Equal("NOT_FOUND", (await _service.DeleteRoomAsync(creatorRoom.Id, creator.Id)).Code);

        // 2. Admin enrolled in Room A deletes Room B created by creator without disturbing Room A enrollment/calibrations
        var adminRoomA = (await _service.CreateRoomAsync("Admin Office", "emp-admin", "dev-admin-pc", "Admin PC")).Value!;
        _service.SaveLearnedPairCalibration(adminRoomA.Id, new PhysicalRoomSavedPairCalibration
        {
            SourceEmployeeId = "emp-admin",
            SourceDeviceId = "dev-admin-pc",
            TargetEmployeeId = "emp-1",
            TargetDeviceId = "dev-1",
            DelaySamples = 120,
            BleedGain = 0.25f,
            Confidence = 0.80
        });
        var creatorRoomB = (await _service.CreateRoomAsync("Creator Gaming Room", creator.Id, "dev-creator-pc", "Creator PC")).Value!;

        var adminDeleteB = await _service.DeleteRoomAsync(creatorRoomB.Id, "emp-admin");
        Assert.True(adminDeleteB.IsSuccess);
        Assert.Null(_service.GetRoomById(creatorRoomB.Id));

        // Admin's Room A enrollment and learned calibrations remain untouched
        var adminEnrolledRoom = _service.GetRoomForUserDevice("emp-admin", "dev-admin-pc");
        Assert.NotNull(adminEnrolledRoom);
        Assert.Equal(adminRoomA.Id, adminEnrolledRoom!.Id);
        Assert.Single(_roomRepo.GetById(adminRoomA.Id)!.SavedCalibrations);

        // 3. Settings manager (Admin.ManageSettings) can also delete another user's room
        var creatorRoomC = (await _service.CreateRoomAsync("Creator Lounge", creator.Id)).Value!;
        var mgrDeleteC = await _service.DeleteRoomAsync(creatorRoomC.Id, settingsManager.Id);
        Assert.True(mgrDeleteC.IsSuccess);
        Assert.Null(_service.GetRoomById(creatorRoomC.Id));

        // 4. Flush disk writes/deletes and reload into a fresh repository to verify deleted rooms stay deleted
        await _persistence.FlushAllAsync();
        var reloadedConfig = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DataPath"] = _tempDir,
                ["SPOKES_MASTER_PASSWORD"] = "UnitTest-Master-Password-2026!",
                ["DatabasePath"] = _tempDir
            })
            .Build();
        var freshRepo = new PhysicalVoiceRoomRepository(_persistence, reloadedConfig);
        freshRepo.LoadFromDisk();

        Assert.Null(freshRepo.GetById(creatorRoom.Id));
        Assert.Null(freshRepo.GetById(creatorRoomB.Id));
        Assert.Null(freshRepo.GetById(creatorRoomC.Id));
        Assert.NotNull(freshRepo.GetById(adminRoomA.Id));
    }

    [Fact]
    public async Task DeleteRoomAsync_ActiveCall_ClearsActiveCoLocatedGroups()
    {
        var room = (await _service.CreateRoomAsync("Active Call Room", "emp-1")).Value!;
        await _service.EnrollUserDeviceAsync(room.Id, "emp-1", "dev-1", "PC 1", actorUserId: "emp-1");
        await _service.EnrollUserDeviceAsync(room.Id, "emp-2", "dev-2", "PC 2", actorUserId: "emp-2");

        _voiceState.NotifyVoiceMemberJoined("chan-live-del", "emp-1", "dev-1", "PC 1");
        _voiceState.NotifyVoiceMemberJoined("chan-live-del", "emp-2", "dev-2", "PC 2");
        Assert.Single(_service.GetActiveCoLocatedRoomGroups("chan-live-del"));

        var delRes = await _service.DeleteRoomAsync(room.Id, "emp-admin");
        Assert.True(delRes.IsSuccess);
        Assert.Empty(_service.GetActiveCoLocatedRoomGroups("chan-live-del"));
        Assert.Null(_service.GetRoomForUserDevice("emp-1", "dev-1"));
        Assert.Null(_service.GetRoomForUserDevice("emp-2", "dev-2"));
    }

    [Fact]
    public async Task CanUserRemoveDeviceEnrollment_MatchesServerEnforcementHierarchy()
    {
        var creator = SeedChatEmployee("emp-rem-creator");
        var member = SeedChatEmployee("emp-rem-member");
        var suspended = SeedChatEmployee("emp-rem-susp", isSuspended: true);

        var room = (await _service.CreateRoomAsync("Hierarchy Room", creator.Id, "dev-c", "Creator PC")).Value!;
        await _service.EnrollUserDeviceAsync(room.Id, member.Id, "dev-m", "Member PC", actorUserId: member.Id);
        await _service.EnrollUserDeviceAsync(room.Id, "emp-admin", "dev-a", "Admin PC", actorUserId: "emp-admin");

        // Self-removal is allowed for active members, blocked for suspended users
        Assert.True(_service.CanUserRemoveDeviceEnrollment(room, member.Id, member.Id));
        Assert.False(_service.CanUserRemoveDeviceEnrollment(room, suspended.Id, suspended.Id));

        // Non-creator member cannot remove other users' devices
        Assert.False(_service.CanUserRemoveDeviceEnrollment(room, creator.Id, member.Id));

        // Room creator can remove non-admin member's device, but CANNOT remove an administrator's device
        Assert.True(_service.CanUserRemoveDeviceEnrollment(room, member.Id, creator.Id));
        Assert.False(_service.CanUserRemoveDeviceEnrollment(room, "emp-admin", creator.Id));
        Assert.Equal("FORBIDDEN", (await _service.RemoveUserDeviceAsync("emp-admin", "dev-a", actorUserId: creator.Id)).Code);

        // Full Admin can remove any device (including creator's and own)
        Assert.True(_service.CanUserRemoveDeviceEnrollment(room, creator.Id, "emp-admin"));
        Assert.True(_service.CanUserRemoveDeviceEnrollment(room, "emp-admin", "emp-admin"));
        Assert.True((await _service.RemoveUserDeviceAsync(creator.Id, "dev-c", actorUserId: "emp-admin")).IsSuccess);
    }

    [Fact]
    public async Task SyncEnrolledDeviceNameAsync_HealsStaleLabelAcrossReload_PreservesCalibrations_AndIgnoresFallbacks()
    {
        var user = SeedChatEmployee("emp-sync-name");
        var room = (await _service.CreateRoomAsync("Desktop Lab", user.Id, "dev-electron-1", "Windows PC (Chrome)", muteUsersInMyRoom: true)).Value!;
        _service.SaveLearnedPairCalibration(room.Id, new PhysicalRoomSavedPairCalibration
        {
            SourceEmployeeId = user.Id,
            SourceDeviceId = "dev-electron-1",
            TargetEmployeeId = "emp-1",
            TargetDeviceId = "dev-other",
            DelaySamples = 144,
            BleedGain = 0.22f,
            Confidence = 0.78
        });

        // Generic fallback names must never overwrite an existing label
        Assert.False(await _service.SyncEnrolledDeviceNameAsync(user.Id, "dev-electron-1", "This Device"));
        Assert.False(await _service.SyncEnrolledDeviceNameAsync(user.Id, "dev-electron-1", "Desktop Browser"));
        Assert.False(await _service.SyncEnrolledDeviceNameAsync(user.Id, "dev-electron-1", "Active Call Device"));
        Assert.Equal("Windows PC (Chrome)", _service.GetEnrollment(user.Id, "dev-electron-1")!.DeviceName);

        int changedCount = 0;
        _voiceState.PhysicalRoomsChanged += () => changedCount++;

        // Healing stale Chrome label -> Spokes Desktop persists to disk and notifies listeners
        Assert.True(await _service.SyncEnrolledDeviceNameAsync(user.Id, "dev-electron-1", "Windows PC (Spokes Desktop)"));
        Assert.Equal(1, changedCount);

        // Calling again with identical label is a no-op (zero extra writes/events)
        Assert.False(await _service.SyncEnrolledDeviceNameAsync(user.Id, "dev-electron-1", "Windows PC (Spokes Desktop)"));
        Assert.Equal(1, changedCount);

        // Mute preference and learned calibrations remain intact
        var enrollment = _service.GetEnrollment(user.Id, "dev-electron-1")!;
        Assert.Equal("Windows PC (Spokes Desktop)", enrollment.DeviceName);
        Assert.True(enrollment.MuteUsersInMyRoom);
        Assert.Single(_roomRepo.GetById(room.Id)!.SavedCalibrations);

        // Suspended users cannot mutate persisted device names
        user.IsSuspended = true;
        _employeeRepo.Save(user);
        Assert.False(await _service.SyncEnrolledDeviceNameAsync(user.Id, "dev-electron-1", "Mac (Spokes Desktop)"));

        // Verify disk reload persistence
        await _persistence.FlushAllAsync();
        var reloadedConfig = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DataPath"] = _tempDir,
                ["SPOKES_MASTER_PASSWORD"] = "UnitTest-Master-Password-2026!",
                ["DatabasePath"] = _tempDir
            })
            .Build();
        var freshRepo = new PhysicalVoiceRoomRepository(_persistence, reloadedConfig);
        freshRepo.LoadFromDisk();

        var reloadedEnrollment = freshRepo.GetEnrollment(user.Id, "dev-electron-1");
        Assert.NotNull(reloadedEnrollment);
        Assert.Equal("Windows PC (Spokes Desktop)", reloadedEnrollment!.DeviceName);
    }
}
