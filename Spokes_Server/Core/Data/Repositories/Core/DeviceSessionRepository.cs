namespace Spokes_Server.Core.Data.Repositories.Core;

using Microsoft.Extensions.Configuration;
using Spokes_Server.Core.Models.Core;
using System.Collections.Generic;
using System.IO;
using System.Linq;

public class DeviceSessionRepository : JsonRepository<DeviceSession>
{
    public DeviceSessionRepository(DiskPersistenceService writer, IConfiguration config)
        : base(writer, Path.Combine(config["DataPath"] ?? "Data", "DeviceSessions"))
    {
    }

    public IEnumerable<DeviceSession> GetByEmployeeId(string employeeId)
    {
        return _cache.Values.Where(s => s.EmployeeId == employeeId);
    }

    /// <summary>
    /// Get all active (non-revoked) sessions for a user (for the device list UI).
    /// </summary>
    public List<DeviceSession> GetActiveByEmployeeId(string employeeId)
    {
        return _cache.Values.Where(s => s.EmployeeId == employeeId && s.RevokedAt == null && s.ExpiresAt > DateTime.UtcNow).ToList();
    }

    /// <summary>
    /// Get all active sessions with push enabled for a user, optionally filtered by device type.
    /// Used by WebPushService for notification delivery.
    /// </summary>
    public List<DeviceSession> GetPushEnabledByEmployeeId(string employeeId, string? deviceType = null)
    {
        var query = _cache.Values
            .Where(s => s.EmployeeId == employeeId && s.RevokedAt == null && s.ExpiresAt > DateTime.UtcNow && s.HasPush && s.PushEnabled);
        if (!string.IsNullOrEmpty(deviceType))
            query = query.Where(s => s.DeviceType.Equals(deviceType, StringComparison.OrdinalIgnoreCase));
        return query.ToList();
    }

    /// <summary>
    /// Find a session by its push endpoint URL/token.
    /// </summary>
    public DeviceSession? GetByPushEndpoint(string endpoint)
    {
        return _cache.Values.FirstOrDefault(s => s.PushEndpoint == endpoint && s.RevokedAt == null && s.ExpiresAt > DateTime.UtcNow);
    }

    /// <summary>
    /// Find a session by its token hash (the SHA-256 hash of the refresh token).
    /// </summary>
    public DeviceSession? GetByTokenHash(string tokenHash)
    {
        return _cache.Values.FirstOrDefault(s => s.TokenHash == tokenHash);
    }

    /// <summary>
    /// Find an active (non-revoked, non-expired) session by its token hash.
    /// </summary>
    public DeviceSession? GetActiveByTokenHash(string tokenHash)
    {
        return _cache.Values.FirstOrDefault(s => s.TokenHash == tokenHash && s.RevokedAt == null && s.ExpiresAt > DateTime.UtcNow);
    }

    /// <summary>
    /// Clear all push-related fields from a session (used when unsubscribing or cleaning dead endpoints).
    /// </summary>
    public void ClearPushFields(DeviceSession session)
    {
        session.PushEndpoint = null;
        session.PushP256dh = string.Empty;
        session.PushAuth = string.Empty;
        session.PushPublicKey = string.Empty;
        session.PushSubscriptionType = "WebPush";
        session.PushEnabled = false;
        session.PushSubscribedAt = null;
        session.PushUserAgent = string.Empty;
        Save(session);
    }

    protected override string GetFilePath(DeviceSession item)
    {
        return Path.Combine(_basePath, $"{item.Id}.json");
    }
}
