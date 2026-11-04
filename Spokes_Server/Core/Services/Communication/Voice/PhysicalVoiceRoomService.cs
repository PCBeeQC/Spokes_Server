namespace Spokes_Server.Core.Services.Communication.Voice;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Spokes_Server.Core.Constants;
using Spokes_Server.Core.Data.Repositories.Communication;
using Spokes_Server.Core.Data.Repositories.Core;
using Spokes_Server.Core.Data.Repositories.HR;
using Spokes_Server.Core.Helpers;
using Spokes_Server.Core.Models.Communication;
using Spokes_Server.Core.Models.HR;
using Spokes_Server.Core.Services.Communication.Chat;
using Spokes_Server.Core.Services.Communication.Voice.Dsp;
using Spokes_Server.Core.Services.Communication.Voice.Pipeline;
using Spokes_Server.Core.Services.Security;

/// <summary>
/// Domain service managing global <see cref="PhysicalVoiceRoom"/> configurations, strict (EmployeeId + DeviceId)
/// enrollments, "Mute users in my room" preferences, and Chat Admin participant assignments.
/// </summary>
public class PhysicalVoiceRoomService
{
    public const int MaxDeviceIdLength = 128;
    public const int MaxDeviceNameLength = 128;
    public const int MaxRoomNameLength = 80;

    private readonly PhysicalVoiceRoomRepository _rooms;
    private readonly ChatStateService _chatState;
    private readonly EmployeeRepository? _employees;
    private readonly CompanyProfileRepository? _companyProfiles;
    private readonly IVoiceBotService? _voiceBotService;
    private readonly IServiceProvider? _serviceProvider;
    private readonly IChatRealtimeGateway? _realtimeGateway;
    private readonly IUserPrivacyService? _userPrivacy;
    private readonly ILogger<PhysicalVoiceRoomService> _logger;
    private readonly object _lock = new();

    protected PhysicalVoiceRoomService()
    {
        _rooms = null!;
        _chatState = null!;
        _logger = NullLogger<PhysicalVoiceRoomService>.Instance;
    }

    public PhysicalVoiceRoomService(
        PhysicalVoiceRoomRepository rooms,
        ChatStateService chatState,
        ILogger<PhysicalVoiceRoomService>? logger = null,
        EmployeeRepository? employees = null,
        CompanyProfileRepository? companyProfiles = null,
        IChatRealtimeGateway? realtimeGateway = null,
        IServiceProvider? serviceProvider = null,
        IUserPrivacyService? userPrivacy = null)
    {
        _rooms = rooms ?? throw new ArgumentNullException(nameof(rooms));
        _chatState = chatState ?? throw new ArgumentNullException(nameof(chatState));
        _logger = logger ?? NullLogger<PhysicalVoiceRoomService>.Instance;
        _employees = employees;
        _companyProfiles = companyProfiles;
        _realtimeGateway = realtimeGateway;
        _serviceProvider = serviceProvider;
        _userPrivacy = userPrivacy;
    }

    internal PhysicalVoiceRoomService(
        PhysicalVoiceRoomRepository rooms,
        ChatStateService chatState,
        ILogger<PhysicalVoiceRoomService>? logger,
        EmployeeRepository? employees,
        CompanyProfileRepository? companyProfiles,
        IVoiceBotService? voiceBotService,
        IChatRealtimeGateway? realtimeGateway = null,
        IUserPrivacyService? userPrivacy = null)
    {
        _rooms = rooms ?? throw new ArgumentNullException(nameof(rooms));
        _chatState = chatState ?? throw new ArgumentNullException(nameof(chatState));
        _logger = logger ?? NullLogger<PhysicalVoiceRoomService>.Instance;
        _employees = employees;
        _companyProfiles = companyProfiles;
        _voiceBotService = voiceBotService;
        _realtimeGateway = realtimeGateway;
        _userPrivacy = userPrivacy;
    }

    public virtual List<PhysicalVoiceRoom> GetAllRooms() =>
        _rooms.GetAll().OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>
    /// Returns all physical rooms selectable by <paramref name="actorUserId"/>, keeping room IDs and names
    /// available for one-click PIN-free device enrollment while redacting <see cref="PhysicalVoiceRoom.EnrolledDevices"/>
    /// and <see cref="PhysicalVoiceRoom.SavedCalibrations"/> on rooms where the caller is neither enrolled nor an editor,
    /// and filtering enrolled roommate records through <see cref="IUserPrivacyService.CanDiscoverUser"/>.
    /// </summary>
    public virtual List<PhysicalVoiceRoom> GetRoomsForUser(string? actorUserId)
    {
        if (string.IsNullOrWhiteSpace(actorUserId))
        {
            return [];
        }

        Employee? caller = null;
        if (_employees != null)
        {
            caller = _employees.GetById(actorUserId);
            if (caller == null || !caller.IsActive || caller.IsBanned)
            {
                return [];
            }

            bool hasChatAccess = caller.IsAdmin ||
                                 caller.HasPermission(AppPermissions.Chat.Use) ||
                                 (caller.IsSuspended && (caller.Permissions.Contains(AppPermissions.Chat.Use) ||
                                     caller.PermissionGroups.Any(g => g != null && g.Permissions.Contains(AppPermissions.Chat.Use))));
            if (!hasChatAccess)
            {
                return [];
            }
        }

        var rooms = GetAllRooms();
        var projected = new List<PhysicalVoiceRoom>(rooms.Count);

        foreach (var room in rooms)
        {
            bool isEnrolledInRoom = room.EnrolledDevices != null &&
                room.EnrolledDevices.Any(d => string.Equals(d.EmployeeId, actorUserId, StringComparison.Ordinal));
            bool canInspectDevices = isEnrolledInRoom || CanUserEditPhysicalRoom(room, actorUserId);

            List<PhysicalRoomDeviceEnrollment> visibleDevices = [];
            List<PhysicalRoomSavedPairCalibration> visibleCalibrations = [];

            if (canInspectDevices && room.EnrolledDevices != null)
            {
                var visibleUserIds = new HashSet<string>(StringComparer.Ordinal);
                foreach (var d in room.EnrolledDevices)
                {
                    if (IsUserVisibleToActor(caller, actorUserId, d.EmployeeId))
                    {
                        visibleUserIds.Add(d.EmployeeId);
                        visibleDevices.Add(new PhysicalRoomDeviceEnrollment
                        {
                            EmployeeId = d.EmployeeId,
                            DeviceId = d.DeviceId,
                            DeviceName = d.DeviceName,
                            MuteUsersInMyRoom = d.MuteUsersInMyRoom,
                            EnrolledAt = d.EnrolledAt
                        });
                    }
                }

                if (room.SavedCalibrations != null)
                {
                    foreach (var c in room.SavedCalibrations)
                    {
                        if (visibleUserIds.Contains(c.SourceEmployeeId) && visibleUserIds.Contains(c.TargetEmployeeId))
                        {
                            visibleCalibrations.Add(new PhysicalRoomSavedPairCalibration
                            {
                                SourceEmployeeId = c.SourceEmployeeId,
                                SourceDeviceId = c.SourceDeviceId,
                                SourceFilterType = c.SourceFilterType,
                                TargetEmployeeId = c.TargetEmployeeId,
                                TargetDeviceId = c.TargetDeviceId,
                                TargetFilterType = c.TargetFilterType,
                                DelaySamples = c.DelaySamples,
                                BleedGain = c.BleedGain,
                                Confidence = c.Confidence,
                                UpdatedAtUtc = c.UpdatedAtUtc
                            });
                        }
                    }
                }
            }

            projected.Add(new PhysicalVoiceRoom
            {
                Id = room.Id,
                Name = room.Name,
                CreatedByUserId = room.CreatedByUserId,
                CreatedAt = room.CreatedAt,
                EnrolledDevices = visibleDevices,
                SavedCalibrations = visibleCalibrations
            });
        }

        return projected;
    }

    public virtual PhysicalVoiceRoom? GetRoomById(string roomId) =>
        string.IsNullOrWhiteSpace(roomId) ? null : _rooms.GetById(roomId);

    public virtual PhysicalVoiceRoom? GetRoomForUserDevice(string employeeId, string deviceId) =>
        _rooms.GetRoomForUserDevice(employeeId, string.IsNullOrWhiteSpace(deviceId) ? deviceId : NormalizeAndClampDeviceId(deviceId));

    public virtual PhysicalRoomDeviceEnrollment? GetEnrollment(string employeeId, string deviceId) =>
        _rooms.GetEnrollment(employeeId, string.IsNullOrWhiteSpace(deviceId) ? deviceId : NormalizeAndClampDeviceId(deviceId));

    /// <summary>
    /// Creates a new global physical room and optionally enrolls the creator's current device.
    /// </summary>
    public virtual async Task<SpokesResult<PhysicalVoiceRoom>> CreateRoomAsync(
        string name,
        string creatorUserId,
        string? initialDeviceId = null,
        string? initialDeviceName = null,
        bool muteUsersInMyRoom = false,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return SpokesResult<PhysicalVoiceRoom>.Failure("Room name cannot be empty.", "VALIDATION_ERROR");
        }
        if (!TryValidateActiveChatActor(creatorUserId, out _, out var actorErr, out var actorCode))
        {
            return SpokesResult<PhysicalVoiceRoom>.Failure(actorErr, actorCode);
        }

        string trimmedName = NormalizeAndClampRoomName(name);
        string? normalizedInitialDeviceId = string.IsNullOrWhiteSpace(initialDeviceId)
            ? null
            : NormalizeAndClampDeviceId(initialDeviceId);
        PhysicalVoiceRoom room;

        lock (_lock)
        {
            if (_rooms.GetAll().Any(r => string.Equals(r.Name, trimmedName, StringComparison.OrdinalIgnoreCase)))
            {
                return SpokesResult<PhysicalVoiceRoom>.Failure("A physical room with this name already exists.", "DUPLICATE_NAME");
            }

            if (!string.IsNullOrWhiteSpace(normalizedInitialDeviceId))
            {
                RemoveDeviceFromAllRoomsLocked(creatorUserId, normalizedInitialDeviceId);
            }

            room = new PhysicalVoiceRoom
            {
                Id = Guid.NewGuid().ToString(),
                Name = trimmedName,
                CreatedByUserId = creatorUserId,
                CreatedAt = DateTime.UtcNow,
                EnrolledDevices = []
            };

            if (!string.IsNullOrWhiteSpace(normalizedInitialDeviceId))
            {
                room.EnrolledDevices.Add(new PhysicalRoomDeviceEnrollment
                {
                    EmployeeId = creatorUserId,
                    DeviceId = normalizedInitialDeviceId,
                    DeviceName = NormalizeAndClampDeviceName(initialDeviceName, "Desktop PC"),
                    MuteUsersInMyRoom = muteUsersInMyRoom,
                    EnrolledAt = DateTime.UtcNow
                });
            }

            _rooms.Save(room);
        }

        await BroadcastRoomChangesAsync(cancellationToken);
        return SpokesResult<PhysicalVoiceRoom>.Success(room);
    }

    public virtual async Task<SpokesResult<PhysicalVoiceRoom>> RenameRoomAsync(
        string roomId,
        string newName,
        string actorUserId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(roomId) || string.IsNullOrWhiteSpace(newName))
        {
            return SpokesResult<PhysicalVoiceRoom>.Failure("Room ID and new name are required.", "VALIDATION_ERROR");
        }
        if (!TryValidateActiveChatActor(actorUserId, out _, out var actorErr, out var actorCode))
        {
            return SpokesResult<PhysicalVoiceRoom>.Failure(actorErr, actorCode);
        }

        string trimmedName = NormalizeAndClampRoomName(newName);
        PhysicalVoiceRoom? room;
        lock (_lock)
        {
            room = _rooms.GetById(roomId);
            if (room == null)
            {
                return SpokesResult<PhysicalVoiceRoom>.Failure("Physical room not found.", "NOT_FOUND");
            }

            if (!CanUserEditPhysicalRoom(room, actorUserId))
            {
                return SpokesResult<PhysicalVoiceRoom>.Failure("Only the room creator or an administrator can rename this physical room.", "FORBIDDEN");
            }

            if (_rooms.GetAll().Any(r => r.Id != roomId && string.Equals(r.Name, trimmedName, StringComparison.OrdinalIgnoreCase)))
            {
                return SpokesResult<PhysicalVoiceRoom>.Failure("A physical room with this name already exists.", "DUPLICATE_NAME");
            }

            room.Name = trimmedName;
            _rooms.Save(room);
        }

        await BroadcastRoomChangesAsync(cancellationToken);
        return SpokesResult<PhysicalVoiceRoom>.Success(room);
    }

    public virtual async Task<SpokesResult<bool>> DeleteRoomAsync(
        string roomId,
        string actorUserId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(roomId))
        {
            return SpokesResult<bool>.Failure("Room ID is required.", "VALIDATION_ERROR");
        }
        if (!TryValidateActiveChatActor(actorUserId, out _, out var actorErr, out var actorCode))
        {
            return SpokesResult<bool>.Failure(actorErr, actorCode);
        }

        lock (_lock)
        {
            var room = _rooms.GetById(roomId);
            if (room == null)
            {
                return SpokesResult<bool>.Failure("Physical room not found.", "NOT_FOUND");
            }

            if (!CanUserEditPhysicalRoom(room, actorUserId))
            {
                return SpokesResult<bool>.Failure("Only the room creator or an administrator can delete this physical room.", "FORBIDDEN");
            }

            _rooms.Delete(roomId);
        }

        await BroadcastRoomChangesAsync(cancellationToken);
        return SpokesResult<bool>.Success(true);
    }

    /// <summary>
    /// Enrolls a specific (employeeId, deviceId) pair into the target global physical room.
    /// Automatically removes that (employeeId, deviceId) pair from any previously assigned physical room.
    /// </summary>
    public virtual async Task<SpokesResult<PhysicalVoiceRoom>> EnrollUserDeviceAsync(
        string roomId,
        string employeeId,
        string deviceId,
        string deviceName,
        bool? muteUsersInMyRoom = null,
        CancellationToken cancellationToken = default,
        string? actorUserId = null)
    {
        if (string.IsNullOrWhiteSpace(roomId) || string.IsNullOrWhiteSpace(employeeId) || string.IsNullOrWhiteSpace(deviceId))
        {
            return SpokesResult<PhysicalVoiceRoom>.Failure("RoomId, EmployeeId, and DeviceId are required.", "VALIDATION_ERROR");
        }

        if (!TryValidateActiveChatActor(actorUserId, out _, out var actorErr, out var actorCode))
        {
            return SpokesResult<PhysicalVoiceRoom>.Failure(actorErr, actorCode);
        }

        if (!string.Equals(actorUserId, employeeId, StringComparison.Ordinal) &&
            !CanUserManagePhysicalRooms(actorUserId))
        {
            return SpokesResult<PhysicalVoiceRoom>.Failure("You do not have permission to enroll another user's device.", "FORBIDDEN");
        }

        if (_employees != null)
        {
            var targetEmployee = _employees.GetById(employeeId);
            if (targetEmployee == null || !targetEmployee.IsActive || targetEmployee.IsBanned)
            {
                return SpokesResult<PhysicalVoiceRoom>.Failure("Target user is not eligible for physical room enrollment.", "FORBIDDEN");
            }
        }

        string normalizedDeviceId = NormalizeAndClampDeviceId(deviceId);
        string normalizedDeviceName = NormalizeAndClampDeviceName(deviceName, "Device");

        PhysicalVoiceRoom? targetRoom;
        lock (_lock)
        {
            targetRoom = _rooms.GetById(roomId);
            if (targetRoom == null)
            {
                return SpokesResult<PhysicalVoiceRoom>.Failure("Physical room not found.", "NOT_FOUND");
            }

            // Preserve previous MuteUsersInMyRoom setting if not explicitly overridden
            bool previousMutePref = _rooms.GetEnrollment(employeeId, normalizedDeviceId)?.MuteUsersInMyRoom ?? false;
            bool finalMutePref = muteUsersInMyRoom ?? previousMutePref;

            RemoveDeviceFromAllRoomsLocked(employeeId, normalizedDeviceId);

            // Re-fetch in case targetRoom was mutated
            targetRoom = _rooms.GetById(roomId)!;
            targetRoom.EnrolledDevices.Add(new PhysicalRoomDeviceEnrollment
            {
                EmployeeId = employeeId,
                DeviceId = normalizedDeviceId,
                DeviceName = normalizedDeviceName,
                MuteUsersInMyRoom = finalMutePref,
                EnrolledAt = DateTime.UtcNow
            });

            _rooms.Save(targetRoom);
        }

        await BroadcastRoomChangesAsync(cancellationToken);
        return SpokesResult<PhysicalVoiceRoom>.Success(targetRoom);
    }

    /// <summary>
    /// Removes the specified (employeeId, deviceId) pair from its current physical room.
    /// </summary>
    public virtual async Task<SpokesResult<bool>> RemoveUserDeviceAsync(
        string employeeId,
        string deviceId,
        CancellationToken cancellationToken = default,
        string? actorUserId = null)
    {
        if (string.IsNullOrWhiteSpace(employeeId) || string.IsNullOrWhiteSpace(deviceId))
        {
            return SpokesResult<bool>.Failure("EmployeeId and DeviceId are required.", "VALIDATION_ERROR");
        }

        if (!TryValidateActiveChatActor(actorUserId, out _, out var actorErr, out var actorCode))
        {
            return SpokesResult<bool>.Failure(actorErr, actorCode);
        }

        string normalizedDeviceId = NormalizeAndClampDeviceId(deviceId);

        bool removed;
        lock (_lock)
        {
            if (!string.Equals(actorUserId, employeeId, StringComparison.Ordinal))
            {
                var currentRoom = _rooms.GetRoomForUserDevice(employeeId, normalizedDeviceId);
                if (currentRoom != null && !CanUserEditPhysicalRoom(currentRoom, actorUserId))
                {
                    return SpokesResult<bool>.Failure("You do not have permission to remove this device from the physical room.", "FORBIDDEN");
                }
                if (currentRoom == null && !CanUserManagePhysicalRooms(actorUserId))
                {
                    return SpokesResult<bool>.Failure("You do not have permission to remove this device.", "FORBIDDEN");
                }
            }

            removed = RemoveDeviceFromAllRoomsLocked(employeeId, normalizedDeviceId);
        }

        if (removed)
        {
            await BroadcastRoomChangesAsync(cancellationToken);
        }

        return SpokesResult<bool>.Success(removed);
    }

    /// <summary>
    /// Updates the "Mute users in my room" setting for a specific enrolled (employeeId, deviceId).
    /// </summary>
    public virtual async Task<SpokesResult<bool>> SetMuteUsersInMyRoomAsync(
        string employeeId,
        string deviceId,
        bool muteUsersInMyRoom,
        CancellationToken cancellationToken = default,
        string? actorUserId = null)
    {
        if (string.IsNullOrWhiteSpace(employeeId) || string.IsNullOrWhiteSpace(deviceId))
        {
            return SpokesResult<bool>.Failure("EmployeeId and DeviceId are required.", "VALIDATION_ERROR");
        }

        if (!TryValidateActiveChatActor(actorUserId, out var actor, out var actorErr, out var actorCode))
        {
            return SpokesResult<bool>.Failure(actorErr, actorCode);
        }

        if (!string.Equals(actorUserId, employeeId, StringComparison.Ordinal))
        {
            if (_employees != null && (actor == null || !actor.IsAdmin))
            {
                return SpokesResult<bool>.Failure("Only the device owner or an administrator can modify personal room mute preferences.", "FORBIDDEN");
            }
        }

        string normalizedDeviceId = NormalizeAndClampDeviceId(deviceId);

        lock (_lock)
        {
            var room = _rooms.GetRoomForUserDevice(employeeId, normalizedDeviceId);
            if (room == null)
            {
                return SpokesResult<bool>.Failure("This device is not enrolled in a physical room.", "NOT_FOUND");
            }

            var enrollment = room.EnrolledDevices.FirstOrDefault(d =>
                string.Equals(d.EmployeeId, employeeId, StringComparison.Ordinal) &&
                string.Equals(d.DeviceId, normalizedDeviceId, StringComparison.Ordinal));

            if (enrollment == null)
            {
                return SpokesResult<bool>.Failure("Enrollment not found.", "NOT_FOUND");
            }

            enrollment.MuteUsersInMyRoom = muteUsersInMyRoom;
            _rooms.Save(room);
        }

        await BroadcastRoomChangesAsync(cancellationToken);
        return SpokesResult<bool>.Success(true);
    }

    /// <summary>
    /// Allows a Chat Admin (or user with channel management permissions) to assign or remove
    /// another participant's active device to/from a Physical Room.
    /// </summary>
    public virtual async Task<SpokesResult<bool>> AdminAssignParticipantDeviceToRoomAsync(
        string adminUserId,
        string targetEmployeeId,
        string targetDeviceId,
        string targetDeviceName,
        string? targetRoomId,
        CancellationToken cancellationToken = default)
    {
        if (!TryValidateActiveChatActor(adminUserId, out _, out var actorErr, out var actorCode))
        {
            return SpokesResult<bool>.Failure(actorErr, actorCode);
        }

        if (!CanUserManagePhysicalRooms(adminUserId))
        {
            return SpokesResult<bool>.Failure("You do not have permission to assign other users to physical rooms.", "FORBIDDEN");
        }

        if (string.IsNullOrWhiteSpace(targetEmployeeId) || string.IsNullOrWhiteSpace(targetDeviceId))
        {
            return SpokesResult<bool>.Failure("Target user and device ID are required.", "VALIDATION_ERROR");
        }

        if (string.IsNullOrWhiteSpace(targetRoomId))
        {
            return await RemoveUserDeviceAsync(targetEmployeeId, targetDeviceId, cancellationToken, actorUserId: adminUserId);
        }

        var res = await EnrollUserDeviceAsync(targetRoomId, targetEmployeeId, targetDeviceId, targetDeviceName, null, cancellationToken, actorUserId: adminUserId);
        return res.IsSuccess
            ? SpokesResult<bool>.Success(true)
            : SpokesResult<bool>.Failure(res.ErrorMessage ?? "Failed to assign room.", res.Code ?? "ERROR");
    }

    public virtual bool CanUserEditPhysicalRoom(PhysicalVoiceRoom? room, string? userId)
    {
        if (room == null || string.IsNullOrWhiteSpace(userId)) return false;

        if (_employees != null)
        {
            var emp = _employees.GetById(userId);
            if (emp == null || !emp.IsActive || emp.IsSuspended || emp.IsBanned)
            {
                return false;
            }
            if (!emp.IsAdmin && !emp.HasPermission(AppPermissions.Chat.Use))
            {
                return false;
            }
        }

        if (string.Equals(room.CreatedByUserId, userId, StringComparison.Ordinal)) return true;
        return CanUserManagePhysicalRooms(userId);
    }

    public virtual bool CanUserManagePhysicalRooms(string? userId)
    {
        if (string.IsNullOrWhiteSpace(userId)) return false;
        if (_employees == null) return true;

        var emp = _employees.GetById(userId);
        if (emp == null || !emp.IsActive || emp.IsSuspended || emp.IsBanned) return false;
        if (emp.IsAdmin) return true;

        return emp.HasPermission(AppPermissions.Admin.ManageSettings) ||
               emp.HasPermission(AppPermissions.Chat.Moderator) ||
               emp.HasPermission(AppPermissions.Chat.EditAllPublicChannels) ||
               emp.HasPermission(AppPermissions.Chat.CreateChannels);
    }

    /// <summary>
    /// Returns a map of participant UserId -> (Room, Enrollment) for all connected participants in the channel
    /// whose active device is enrolled in a Physical Room.
    /// </summary>
    public virtual Dictionary<string, (PhysicalVoiceRoom Room, PhysicalRoomDeviceEnrollment Enrollment)> GetParticipantRoomMap(string channelId)
    {
        var result = new Dictionary<string, (PhysicalVoiceRoom Room, PhysicalRoomDeviceEnrollment Enrollment)>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(channelId)) return result;

        var participants = _chatState.GetVoiceParticipants(channelId);
        if (participants.Count == 0) return result;

        foreach (var userId in participants)
        {
            var devInfo = _chatState.GetVoiceParticipantDevice(channelId, userId);
            if (devInfo == null || string.IsNullOrWhiteSpace(devInfo.Value.DeviceId)) continue;

            string normalizedDeviceId = NormalizeAndClampDeviceId(devInfo.Value.DeviceId);
            var room = _rooms.GetRoomForUserDevice(userId, normalizedDeviceId);
            var enrollment = _rooms.GetEnrollment(userId, normalizedDeviceId);
            if (room != null && enrollment != null)
            {
                result[userId] = (room, enrollment);
            }
        }

        return result;
    }

    /// <summary>
    /// Returns all Physical Rooms in the given voice channel that currently have at least 1 connected participant
    /// using an enrolled (EmployeeId, DeviceId) pair, mapped to the list of active participant EmployeeIds in that room.
    /// Note: Server-side DSP isolation activates for rooms where the returned participant list has Count >= 2.
    /// </summary>
    public virtual Dictionary<string, (PhysicalVoiceRoom Room, List<string> ActiveUserIds)> GetChannelRoomEnrollments(string channelId)
    {
        var result = new Dictionary<string, (PhysicalVoiceRoom Room, List<string> ActiveUserIds)>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(channelId)) return result;

        var participants = _chatState.GetVoiceParticipants(channelId);
        if (participants.Count == 0) return result;

        var allRooms = _rooms.GetAll();
        if (allRooms.Count == 0) return result;

        foreach (var userId in participants)
        {
            var devInfo = _chatState.GetVoiceParticipantDevice(channelId, userId);
            if (devInfo == null || string.IsNullOrWhiteSpace(devInfo.Value.DeviceId)) continue;

            string normalizedDeviceId = NormalizeAndClampDeviceId(devInfo.Value.DeviceId);
            var room = allRooms.FirstOrDefault(r =>
                r.EnrolledDevices != null &&
                r.EnrolledDevices.Any(d =>
                    string.Equals(d.EmployeeId, userId, StringComparison.Ordinal) &&
                    string.Equals(d.DeviceId, normalizedDeviceId, StringComparison.Ordinal)));

            if (room != null)
            {
                if (!result.TryGetValue(room.Id, out var entry))
                {
                    entry = (room, []);
                    result[room.Id] = entry;
                }
                if (!entry.ActiveUserIds.Contains(userId))
                {
                    entry.ActiveUserIds.Add(userId);
                }
            }
        }

        return result;
    }

    /// <summary>
    /// Returns only the Physical Rooms in the given voice channel that have 2 or more co-located participants
    /// simultaneously connected (meaning server-side cross-talk DSP is active for those participants).
    /// </summary>
    public virtual Dictionary<string, (PhysicalVoiceRoom Room, List<string> ActiveUserIds)> GetActiveCoLocatedRoomGroups(string channelId)
    {
        return GetChannelRoomEnrollments(channelId)
            .Where(kvp => kvp.Value.ActiveUserIds.Count >= 2)
            .ToDictionary(k => k.Key, v => v.Value, StringComparer.Ordinal);
    }

    /// <summary>
    /// Gets the roommates (other active userIds in the same Physical Room in the same channel) for a specific user+device.
    /// </summary>
    public virtual List<string> GetActiveRoommatesInChannel(string channelId, string userId, string? deviceId = null)
    {
        if (string.IsNullOrWhiteSpace(channelId) || string.IsNullOrWhiteSpace(userId)) return [];

        string? resolvedDeviceId = deviceId ?? _chatState.GetVoiceParticipantDevice(channelId, userId)?.DeviceId;
        if (string.IsNullOrWhiteSpace(resolvedDeviceId)) return [];

        string normalizedDeviceId = NormalizeAndClampDeviceId(resolvedDeviceId);
        var room = _rooms.GetRoomForUserDevice(userId, normalizedDeviceId);
        if (room == null) return [];

        var groups = GetActiveCoLocatedRoomGroups(channelId);
        if (groups.TryGetValue(room.Id, out var group))
        {
            return group.ActiveUserIds.Where(id => !string.Equals(id, userId, StringComparison.Ordinal)).ToList();
        }

        return [];
    }

    /// <summary>
    /// Retrieves a live <see cref="PhysicalRoomDiagnosticSnapshot"/> for the specified voice channel and physical room.
    /// If the server-side pipeline processor is active, returns live DSP telemetry; otherwise synthesizes an idle snapshot
    /// for the connected participants in the room so diagnostic UI and agent logs always have full context.
    /// </summary>
    public virtual PhysicalRoomDiagnosticSnapshot GetRoomDiagnosticSnapshot(string channelId, string physicalRoomId, string? actorUserId = null)
    {
        if (!TryAuthorizeRoomDiagnostics(physicalRoomId, actorUserId, requireNotSuspended: false, out var caller))
        {
            return new PhysicalRoomDiagnosticSnapshot(
                DateTime.UtcNow,
                0.0,
                AcousticRoomProcessor.SampleRate,
                [],
                [],
                [],
                [],
                ["Not authorized to inspect diagnostics for this physical room."]);
        }

        var pipelineStage = _serviceProvider?.GetService<PhysicalRoomPipelineStage>();
        var liveSnapshot = pipelineStage?.GetRoomDiagnosticSnapshot(channelId, physicalRoomId);
        if (liveSnapshot != null)
        {
            return FilterDiagnosticSnapshotForActor(liveSnapshot, caller, actorUserId!);
        }

        var enrollments = GetChannelRoomEnrollments(channelId);
        var activeUsers = enrollments.TryGetValue(physicalRoomId, out var group)
            ? group.ActiveUserIds
            : [];

        var visibleActiveUsers = activeUsers
            .Where(uid => IsUserVisibleToActor(caller, actorUserId!, uid))
            .ToList();

        var idleParticipants = visibleActiveUsers.Select(uid => new ParticipantRoomDspTelemetry(
            uid,
            AcousticGateState.Open,
            1.0f,
            -100.0,
            AcousticRoomProcessor.LookaheadMs,
            0.0,
            0,
            0,
            0,
            0,
            0,
            0.0,
            -100.0)).ToList();

        var warnings = new List<string>();
        if (visibleActiveUsers.Count < 2)
        {
            warnings.Add("Need at least 2 enrolled devices connected to the same voice channel in this Physical Room for server-side isolation DSP to activate.");
        }
        else
        {
            warnings.Add("Waiting for live audio frames from connected room devices.");
        }

        return new PhysicalRoomDiagnosticSnapshot(
            DateTime.UtcNow,
            0.0,
            AcousticRoomProcessor.SampleRate,
            idleParticipants,
            [],
            [],
            [],
            warnings);
    }

    /// <summary>
    /// Resets rolling diagnostic telemetry counters for a fresh capture window in the specified channel and physical room.
    /// </summary>
    public virtual bool ResetRoomDiagnosticCounters(string channelId, string physicalRoomId, string? actorUserId = null)
    {
        if (!TryAuthorizeRoomDiagnostics(physicalRoomId, actorUserId, requireNotSuspended: true, out _))
        {
            return false;
        }

        var pipelineStage = _serviceProvider?.GetService<PhysicalRoomPipelineStage>();
        return pipelineStage?.ResetRoomDiagnosticCounters(channelId, physicalRoomId) ?? false;
    }

    /// <summary>
    /// Persists a verified directional acoustic calibration between two enrolled devices in the specified physical room.
    /// Only writes to disk when a new device/filter pair is calibrated or when the measured lag/gain changes meaningfully.
    /// </summary>
    public virtual bool SaveLearnedPairCalibration(string roomId, PhysicalRoomSavedPairCalibration calibration)
    {
        if (string.IsNullOrWhiteSpace(roomId) ||
            calibration == null ||
            string.IsNullOrWhiteSpace(calibration.SourceEmployeeId) ||
            string.IsNullOrWhiteSpace(calibration.TargetEmployeeId) ||
            calibration.Confidence < 0.45)
        {
            return false;
        }

        string srcDeviceId = string.IsNullOrWhiteSpace(calibration.SourceDeviceId) ? string.Empty : NormalizeAndClampDeviceId(calibration.SourceDeviceId);
        string tgtDeviceId = string.IsNullOrWhiteSpace(calibration.TargetDeviceId) ? string.Empty : NormalizeAndClampDeviceId(calibration.TargetDeviceId);
        string srcFilter = string.IsNullOrWhiteSpace(calibration.SourceFilterType) ? "webrtc" : calibration.SourceFilterType.Trim();
        string tgtFilter = string.IsNullOrWhiteSpace(calibration.TargetFilterType) ? "webrtc" : calibration.TargetFilterType.Trim();

        lock (_lock)
        {
            var room = _rooms.GetById(roomId);
            if (room == null) return false;

            room.SavedCalibrations ??= [];

            var existing = room.SavedCalibrations.FirstOrDefault(c =>
                string.Equals(c.SourceEmployeeId, calibration.SourceEmployeeId, StringComparison.Ordinal) &&
                string.Equals(c.SourceDeviceId, srcDeviceId, StringComparison.Ordinal) &&
                string.Equals(c.SourceFilterType, srcFilter, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(c.TargetEmployeeId, calibration.TargetEmployeeId, StringComparison.Ordinal) &&
                string.Equals(c.TargetDeviceId, tgtDeviceId, StringComparison.Ordinal) &&
                string.Equals(c.TargetFilterType, tgtFilter, StringComparison.OrdinalIgnoreCase));

            if (existing != null)
            {
                bool delayChanged = Math.Abs(existing.DelaySamples - calibration.DelaySamples) >= 48; // >= 1.0ms
                bool gainChanged = Math.Abs(existing.BleedGain - calibration.BleedGain) >= 0.02f;
                bool confidenceImproved = calibration.Confidence > existing.Confidence + 0.05;

                if (!delayChanged && !gainChanged && !confidenceImproved)
                {
                    return false;
                }

                bool largeDelayShift = Math.Abs(existing.DelaySamples - calibration.DelaySamples) >= 192; // >= 4.0ms re-anchor
                existing.DelaySamples = calibration.DelaySamples;
                existing.BleedGain = calibration.BleedGain;
                existing.Confidence = largeDelayShift
                    ? calibration.Confidence
                    : Math.Max(existing.Confidence, calibration.Confidence);
                existing.UpdatedAtUtc = DateTime.UtcNow;
            }
            else
            {
                room.SavedCalibrations.Add(new PhysicalRoomSavedPairCalibration
                {
                    SourceEmployeeId = calibration.SourceEmployeeId,
                    SourceDeviceId = srcDeviceId,
                    SourceFilterType = srcFilter,
                    TargetEmployeeId = calibration.TargetEmployeeId,
                    TargetDeviceId = tgtDeviceId,
                    TargetFilterType = tgtFilter,
                    DelaySamples = calibration.DelaySamples,
                    BleedGain = calibration.BleedGain,
                    Confidence = calibration.Confidence,
                    UpdatedAtUtc = DateTime.UtcNow
                });

                if (room.SavedCalibrations.Count > 64)
                {
                    var oldest = room.SavedCalibrations.OrderBy(c => c.UpdatedAtUtc).First();
                    room.SavedCalibrations.Remove(oldest);
                }
            }

            _rooms.Save(room);
            return true;
        }
    }

    private bool RemoveDeviceFromAllRoomsLocked(string employeeId, string deviceId)
    {
        bool anyRemoved = false;
        foreach (var room in _rooms.GetAll())
        {
            int removedCount = room.EnrolledDevices.RemoveAll(d =>
                string.Equals(d.EmployeeId, employeeId, StringComparison.Ordinal) &&
                string.Equals(d.DeviceId, deviceId, StringComparison.Ordinal));
            int removedCalCount = room.SavedCalibrations?.RemoveAll(c =>
                (string.Equals(c.SourceEmployeeId, employeeId, StringComparison.Ordinal) && string.Equals(c.SourceDeviceId, deviceId, StringComparison.Ordinal)) ||
                (string.Equals(c.TargetEmployeeId, employeeId, StringComparison.Ordinal) && string.Equals(c.TargetDeviceId, deviceId, StringComparison.Ordinal))) ?? 0;
            if (removedCount > 0 || removedCalCount > 0)
            {
                anyRemoved = true;
                _rooms.Save(room);
            }
        }
        return anyRemoved;
    }

    private async Task BroadcastRoomChangesAsync(CancellationToken cancellationToken)
    {
        _chatState.NotifyPhysicalRoomsChanged();

        var voiceBotService = _voiceBotService ?? _serviceProvider?.GetService<IVoiceBotService>();
        if (voiceBotService != null)
        {
            try
            {
                await voiceBotService.NotifyRoomConfigurationChangedAsync(null, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[PhysicalVoiceRoomService] Error notifying VoiceBotService of room configuration change");
            }
        }

        if (_realtimeGateway != null)
        {
            foreach (var activeChannelId in _chatState.GetAllActiveVoiceChannels())
            {
                try
                {
                    await _realtimeGateway.SendToChannelAsync(activeChannelId, "VoicePhysicalRoomsChanged", activeChannelId, cancellationToken);
                }
                catch { }
            }
        }
    }

    private static string NormalizeAndClampDeviceId(string deviceId)
    {
        string trimmed = deviceId.Trim();
        return trimmed.Length > MaxDeviceIdLength ? trimmed[..MaxDeviceIdLength] : trimmed;
    }

    private static string NormalizeAndClampDeviceName(string? deviceName, string fallback)
    {
        string trimmed = string.IsNullOrWhiteSpace(deviceName) ? fallback : deviceName.Trim();
        return trimmed.Length > MaxDeviceNameLength ? trimmed[..MaxDeviceNameLength] : trimmed;
    }

    private static string NormalizeAndClampRoomName(string roomName)
    {
        string trimmed = roomName.Trim();
        return trimmed.Length > MaxRoomNameLength ? trimmed[..MaxRoomNameLength] : trimmed;
    }

    private bool TryValidateActiveChatActor(string? actorUserId, out Employee? actor, out string errorMessage, out string errorCode)
    {
        actor = null;
        if (string.IsNullOrWhiteSpace(actorUserId))
        {
            errorMessage = "Actor user ID is required.";
            errorCode = "FORBIDDEN";
            return false;
        }

        if (_employees != null)
        {
            actor = _employees.GetById(actorUserId);
            if (actor == null || !actor.IsActive || actor.IsSuspended || actor.IsBanned)
            {
                errorMessage = "User account is not permitted to modify physical voice rooms.";
                errorCode = "FORBIDDEN";
                return false;
            }

            if (!actor.IsAdmin && !actor.HasPermission(AppPermissions.Chat.Use))
            {
                errorMessage = "User does not have permission to use chat or physical voice rooms.";
                errorCode = "FORBIDDEN";
                return false;
            }
        }

        errorMessage = string.Empty;
        errorCode = string.Empty;
        return true;
    }

    private bool IsUserVisibleToActor(Employee? caller, string actorUserId, string targetUserId)
    {
        if (string.Equals(actorUserId, targetUserId, StringComparison.Ordinal))
        {
            return true;
        }

        if (caller?.IsAdmin == true)
        {
            return true;
        }

        if (_userPrivacy == null || _employees == null)
        {
            return true;
        }

        var target = _employees.GetById(targetUserId);
        if (target == null)
        {
            return false;
        }

        return _userPrivacy.CanDiscoverUser(caller, target);
    }

    private bool TryAuthorizeRoomDiagnostics(string physicalRoomId, string? actorUserId, bool requireNotSuspended, out Employee? caller)
    {
        caller = null;
        if (string.IsNullOrWhiteSpace(physicalRoomId) || string.IsNullOrWhiteSpace(actorUserId))
        {
            return false;
        }

        var room = _rooms.GetById(physicalRoomId);
        if (room == null)
        {
            return false;
        }

        if (_employees != null)
        {
            caller = _employees.GetById(actorUserId);
            if (caller == null || !caller.IsActive || caller.IsBanned)
            {
                return false;
            }
            if (requireNotSuspended && caller.IsSuspended)
            {
                return false;
            }

            bool hasChatAccess = caller.IsAdmin ||
                                 caller.HasPermission(AppPermissions.Chat.Use) ||
                                 (caller.IsSuspended && (caller.Permissions.Contains(AppPermissions.Chat.Use) ||
                                     caller.PermissionGroups.Any(g => g != null && g.Permissions.Contains(AppPermissions.Chat.Use))));
            if (!hasChatAccess)
            {
                return false;
            }
        }

        bool isEnrolledInRoom = room.EnrolledDevices != null &&
            room.EnrolledDevices.Any(d => string.Equals(d.EmployeeId, actorUserId, StringComparison.Ordinal));

        return isEnrolledInRoom || CanUserEditPhysicalRoom(room, actorUserId);
    }

    private PhysicalRoomDiagnosticSnapshot FilterDiagnosticSnapshotForActor(
        PhysicalRoomDiagnosticSnapshot snapshot,
        Employee? caller,
        string actorUserId)
    {
        if (_userPrivacy == null || _employees == null || caller?.IsAdmin == true)
        {
            return snapshot;
        }

        var visibleUserIds = new HashSet<string>(StringComparer.Ordinal);
        var filteredParticipants = new List<ParticipantRoomDspTelemetry>(snapshot.Participants.Count);
        foreach (var p in snapshot.Participants)
        {
            if (IsUserVisibleToActor(caller, actorUserId, p.UserId))
            {
                visibleUserIds.Add(p.UserId);
                filteredParticipants.Add(p);
            }
        }

        var filteredCalibrations = snapshot.Calibrations
            .Where(c => visibleUserIds.Contains(c.SourceUserId) && visibleUserIds.Contains(c.TargetUserId))
            .ToList();

        var filteredDecompositions = snapshot.Decompositions
            .Where(d => visibleUserIds.Contains(d.UserAId) && visibleUserIds.Contains(d.UserBId))
            .ToList();

        var filteredBuckets = new List<AcousticTimelineBucket>(snapshot.TimelineBuckets.Count);
        foreach (var b in snapshot.TimelineBuckets)
        {
            bool strippedAny = false;
            var filteredGates = new Dictionary<string, AcousticGateState>(StringComparer.Ordinal);
            foreach (var kvp in b.ParticipantGateStates)
            {
                if (visibleUserIds.Contains(kvp.Key))
                {
                    filteredGates[kvp.Key] = kvp.Value;
                }
                else
                {
                    strippedAny = true;
                }
            }

            string? dominant = b.DominantSpeakerUserId != null && visibleUserIds.Contains(b.DominantSpeakerUserId)
                ? b.DominantSpeakerUserId
                : null;

            filteredBuckets.Add(new AcousticTimelineBucket(
                b.StartTimeSeconds,
                b.EndTimeSeconds,
                dominant,
                dominant != null ? b.DominantVocalDbFs : -100.0,
                filteredGates,
                b.ClickTransientsRejected,
                strippedAny ? null : b.EventSummary));
        }

        return new PhysicalRoomDiagnosticSnapshot(
            snapshot.CapturedAtUtc,
            snapshot.CaptureDurationSeconds,
            snapshot.SampleRate,
            filteredParticipants,
            filteredCalibrations,
            filteredDecompositions,
            filteredBuckets,
            snapshot.Warnings);
    }
}
