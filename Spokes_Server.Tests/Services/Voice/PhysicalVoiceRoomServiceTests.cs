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
using Spokes_Server.Core.Services.Security;
using Xunit;

namespace Spokes_Server.Tests.Services.Voice;

public class PhysicalVoiceRoomServiceTests : IDisposable
{
    private readonly string _tempDir;
    private readonly DiskPersistenceService _persistence;
    private readonly PhysicalVoiceRoomRepository _roomRepo;
    private readonly EmployeeRepository _employeeRepo;
    private readonly ChatStateService _chatState;
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
                ["DataPath"] = _tempDir,
                ["DatabasePath"] = _tempDir
            })
            .Build();

        _persistence = new DiskPersistenceService(NullLogger<DiskPersistenceService>.Instance);
        _roomRepo = new PhysicalVoiceRoomRepository(_persistence, config);
        _roomRepo.LoadFromDisk();

        _employeeRepo = new EmployeeRepository(_persistence, config);
        _employeeRepo.LoadFromDisk();

        _chatState = new ChatStateService();
        _voiceBotMock = new Mock<IVoiceBotService>();
        _privacyMock = new Mock<IUserPrivacyService>();
        _privacyMock.Setup(p => p.CanDiscoverUser(It.IsAny<Employee?>(), It.IsAny<Employee>())).Returns(true);

        _service = new PhysicalVoiceRoomService(
            _roomRepo,
            _chatState,
            NullLogger<PhysicalVoiceRoomService>.Instance,
            _employeeRepo,
            null,
            _voiceBotMock.Object,
            realtimeGateway: null,
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
        _chatState.NotifyVoiceMemberJoined("chan-1", "emp-1", "dev-pc-1", "PC 1");
        var singleGroup = _service.GetActiveCoLocatedRoomGroups("chan-1");
        Assert.Empty(singleGroup);

        // Second user joins on their mobile phone (not enrolled) -> still should NOT activate
        _chatState.NotifyVoiceMemberJoined("chan-1", "emp-2", "dev-mobile-2", "iPhone");
        Assert.Empty(_service.GetActiveCoLocatedRoomGroups("chan-1"));

        // Second user joins on their enrolled PC -> now >= 2 enrolled devices are in the channel, so it activates!
        _chatState.SetVoiceParticipantDevice("chan-1", "emp-2", "dev-pc-2", "PC 2");
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

        _chatState.NotifyVoiceMemberJoined("chan-1", "emp-1", "dev-pc-1", "PC 1");
        _chatState.NotifyVoiceMemberJoined("chan-1", "emp-2", "dev-pc-2", "PC 2");

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
        _chatState.NotifyVoiceMemberJoined("chan-1", "emp-target", "dev-target-pc", "Target PC");

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

        _chatState.NotifyVoiceMemberJoined("chan-diag", alice.Id, "dev-alice", "Alice Rig");
        _chatState.NotifyVoiceMemberJoined("chan-diag", hiddenBob.Id, "dev-bob-secret", "Bob Secret Laptop");

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
}
