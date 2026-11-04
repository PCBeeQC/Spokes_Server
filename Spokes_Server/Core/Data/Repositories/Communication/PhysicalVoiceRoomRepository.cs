namespace Spokes_Server.Core.Data.Repositories.Communication;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Extensions.Configuration;
using Spokes_Server.Core.Models.Communication;

/// <summary>
/// File-backed JSON repository for global <see cref="PhysicalVoiceRoom"/> configurations.
/// </summary>
public class PhysicalVoiceRoomRepository : JsonRepository<PhysicalVoiceRoom>
{
    public PhysicalVoiceRoomRepository(DiskPersistenceService writer, IConfiguration config)
        : base(writer, Path.Combine(config["DataPath"] ?? Path.Combine(AppContext.BaseDirectory, "data"), "physical-voice-rooms"), "*.json")
    {
    }

    protected override string GetFilePath(PhysicalVoiceRoom item) =>
        Path.Combine(_basePath, $"{item.Id}.json");

    /// <summary>
    /// Finds the physical voice room in which the specified (employeeId, deviceId) pair is enrolled, if any.
    /// </summary>
    public virtual PhysicalVoiceRoom? GetRoomForUserDevice(string employeeId, string deviceId)
    {
        if (string.IsNullOrWhiteSpace(employeeId) || string.IsNullOrWhiteSpace(deviceId))
        {
            return null;
        }

        return GetAll().FirstOrDefault(r =>
            r.EnrolledDevices != null &&
            r.EnrolledDevices.Any(d =>
                string.Equals(d.EmployeeId, employeeId, StringComparison.Ordinal) &&
                string.Equals(d.DeviceId, deviceId, StringComparison.Ordinal)));
    }

    /// <summary>
    /// Finds the enrollment record for the specified (employeeId, deviceId) pair across all physical rooms.
    /// </summary>
    public virtual PhysicalRoomDeviceEnrollment? GetEnrollment(string employeeId, string deviceId)
    {
        var room = GetRoomForUserDevice(employeeId, deviceId);
        return room?.EnrolledDevices.FirstOrDefault(d =>
            string.Equals(d.EmployeeId, employeeId, StringComparison.Ordinal) &&
            string.Equals(d.DeviceId, deviceId, StringComparison.Ordinal));
    }
}
