namespace Spokes_Server.Core.Models.Communication;

using System;
using System.Collections.Generic;
using Spokes_Server.Core.Data;

/// <summary>
/// Controls how co-located roommates in a <see cref="PhysicalVoiceRoom"/> hear each other's audio.
/// Remote callers outside the physical room always continue receiving server-cleaned audio via <c>processed-mic:*</c>.
/// </summary>
public enum PhysicalRoomAudioMode
{
    /// <summary>
    /// Standard server-side cross-talk and echo isolation via LiveKit (<c>processed-mic:{userId}</c>).
    /// </summary>
    ServerCleanup = 0,

    /// <summary>
    /// Co-located Spokes Desktop clients stream encrypted 48 kHz 128-sample LAN UDP audio (&lt;20 ms)
    /// and apply zero-lookahead causal local bleed gating, Power Wiener cancellation, and speech leveling.
    /// Web and Mobile clients fall back to <see cref="ServerCleanup"/>.
    /// </summary>
    LowLatencyCleaned = 1
}

/// <summary>
/// Represents a global physical room on the server (e.g., "Family Computer Room") where multiple
/// co-located user devices share the same acoustic space and require server-side cross-talk and echo isolation.
/// </summary>
public class PhysicalVoiceRoom : IDataEntity
{
    public string Id { get; set; } = Guid.NewGuid().ToString();

    public string Name { get; set; } = string.Empty;

    public string CreatedByUserId { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    private PhysicalRoomAudioMode _audioMode = PhysicalRoomAudioMode.LowLatencyCleaned;

    /// <summary>
    /// Configures whether co-located Spokes Desktop roommates on the same LAN use low-latency direct LAN audio
    /// (&lt;20 ms cleaned by default) with automatic fallback to <see cref="PhysicalRoomAudioMode.ServerCleanup"/>
    /// whenever non-Desktop or non-LAN roommates are connected in the room, or always use server-side cleanup.
    /// Any legacy/unknown numeric value (such as historical mode <c>2</c>) deserialized from disk safely normalizes
    /// to <see cref="PhysicalRoomAudioMode.LowLatencyCleaned"/>.
    /// </summary>
    public PhysicalRoomAudioMode AudioMode
    {
        get => _audioMode;
        set => _audioMode = value == PhysicalRoomAudioMode.ServerCleanup
            ? PhysicalRoomAudioMode.ServerCleanup
            : PhysicalRoomAudioMode.LowLatencyCleaned;
    }

    /// <summary>
    /// Enrolled (EmployeeId + DeviceId) pairs belonging to this physical room.
    /// </summary>
    public List<PhysicalRoomDeviceEnrollment> EnrolledDevices { get; set; } = [];

    /// <summary>
    /// Persisted acoustic delay and bleed calibrations learned between enrolled device pairs in this room,
    /// enabling instant zero-echo warm startup on subsequent voice calls.
    /// </summary>
    public List<PhysicalRoomSavedPairCalibration> SavedCalibrations { get; set; } = [];
}

/// <summary>
/// Persists a verified directional acoustic calibration between two specific enrolled devices and their active noise filters.
/// </summary>
public class PhysicalRoomSavedPairCalibration
{
    public string SourceEmployeeId { get; set; } = string.Empty;

    public string SourceDeviceId { get; set; } = string.Empty;

    public string SourceFilterType { get; set; } = string.Empty;

    public string TargetEmployeeId { get; set; } = string.Empty;

    public string TargetDeviceId { get; set; } = string.Empty;

    public string TargetFilterType { get; set; } = string.Empty;

    public int DelaySamples { get; set; }

    public float BleedGain { get; set; }

    public double Confidence { get; set; }

    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Binds a specific user on a specific physical device (EmployeeId + DeviceId) to a <see cref="PhysicalVoiceRoom"/>.
/// </summary>
public class PhysicalRoomDeviceEnrollment
{
    public string EmployeeId { get; set; } = string.Empty;

    public string DeviceId { get; set; } = string.Empty;

    public string DeviceName { get; set; } = string.Empty;

    /// <summary>
    /// When enabled, other members in the same physical room are muted in this user's headphones
    /// so they only hear roommates naturally through the air in the room without network latency.
    /// </summary>
    public bool MuteUsersInMyRoom { get; set; }

    public DateTime EnrolledAt { get; set; } = DateTime.UtcNow;
}
