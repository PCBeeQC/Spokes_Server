namespace Spokes_Server.Core.Models.Communication;

using System;
using System.Collections.Generic;
using Spokes_Server.Core.Data;

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
