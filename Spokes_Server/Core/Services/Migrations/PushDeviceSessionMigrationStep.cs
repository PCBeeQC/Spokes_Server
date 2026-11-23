using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Spokes_Server.Core.Data.Repositories.Core;
using Spokes_Server.Core.Models.Core;

namespace Spokes_Server.Core.Services.Migrations;

/// <summary>
/// Migration step that migrates standalone push subscriptions into unified device sessions and purges stale sessions.
/// Self-contained IMigrationStep implementation.
/// </summary>
public class PushDeviceSessionMigrationStep : IMigrationStep
{
    private readonly DeviceSessionRepository _deviceSessions;
    private readonly string _dataPath;
    private readonly ILogger? _logger;

    public string MigrationKey => "20260102_PushDeviceSessionUnified";
    public string Description => "Migrates standalone push subscriptions into unified device sessions and purges stale sessions";
    public int Order => 20;

    public PushDeviceSessionMigrationStep(
        DeviceSessionRepository deviceSessions,
        IConfiguration config,
        ILogger<PushDeviceSessionMigrationStep>? logger = null)
    {
        _deviceSessions = deviceSessions ?? throw new ArgumentNullException(nameof(deviceSessions));
        _dataPath = config["DataPath"] ?? "Data";
        _logger = logger;
    }

    internal PushDeviceSessionMigrationStep(
        DeviceSessionRepository deviceSessions,
        string dataPath,
        ILogger? logger = null)
    {
        _deviceSessions = deviceSessions ?? throw new ArgumentNullException(nameof(deviceSessions));
        _dataPath = dataPath;
        _logger = logger;
    }

    public virtual Task ExecuteAsync(CancellationToken cancellationToken = default)
    {
        MigratePushSubscriptionsToDeviceSessions();
        CleanupStaleDeviceSessions();
        return Task.CompletedTask;
    }

    public virtual void MigratePushSubscriptionsToDeviceSessions() =>
        MigratePushSubscriptionDirectories(_deviceSessions, _dataPath, _logger);

    internal static void MigratePushSubscriptionDirectories(
        DeviceSessionRepository deviceSessions,
        string dataPath,
        ILogger? logger = null)
    {
        if (string.IsNullOrEmpty(dataPath) || !Directory.Exists(dataPath)) return;

        var pushDirs = Directory.EnumerateDirectories(dataPath)
            .Where(d => string.Equals(Path.GetFileName(d), "PushSubscriptions", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        foreach (var pushSubDir in pushDirs)
        {
            MigrateSinglePushSubscriptionDirectory(deviceSessions, pushSubDir, logger);
        }
    }

    private static void MigrateSinglePushSubscriptionDirectory(
        DeviceSessionRepository deviceSessions,
        string pushSubDir,
        ILogger? logger)
    {
        if (string.IsNullOrEmpty(pushSubDir) || !Directory.Exists(pushSubDir)) return;

        var files = Directory.GetFiles(pushSubDir, "*.json", SearchOption.AllDirectories)
            .Where(f => !f.Contains(".corrupted"))
            .ToArray();

        if (files.Length == 0)
        {
            try
            {
                if (!Directory.Exists(Path.Combine(pushSubDir, ".corrupted")))
                {
                    Directory.Delete(pushSubDir, true);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger?.LogWarning(ex, "[Migration] Failed to delete empty push subscriptions directory {Dir}: {Error}", pushSubDir, ex.Message);
            }
            return;
        }

        logger?.LogInformation("[Migration] Merging {Count} PushSubscriptions from disk into DeviceSessions...", files.Length);
        int merged = 0, orphaned = 0;

        foreach (var file in files)
        {
            string? json = null;
            try
            {
                json = File.ReadAllText(file);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger?.LogWarning(ex, "[Migration] Failed to read legacy push file {File}: {Error}", file, ex.Message);
                continue;
            }

            LegacyPushSubscription? sub = null;
            try
            {
                sub = JsonSerializer.Deserialize<LegacyPushSubscription>(json);
            }
            catch (Exception ex)
            {
                logger?.LogWarning(ex, "[Migration] Failed to deserialize legacy push file {File}: {Error}", file, ex.Message);
            }

            if (sub == null)
            {
                try
                {
                    var corruptedDir = Path.Combine(pushSubDir, ".corrupted");
                    Directory.CreateDirectory(corruptedDir);
                    var destFile = Path.Combine(corruptedDir, Path.GetFileName(file));
                    File.Move(file, destFile, overwrite: true);
                    logger?.LogWarning("[Migration] Quarantined corrupted legacy push file {File} to {DestFile}", file, destFile);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    logger?.LogWarning(ex, "[Migration] Failed to quarantine corrupted legacy push file {File}: {Error}", file, ex.Message);
                }
                continue;
            }

            DeviceSession? session = null;

            // Try strict match: SessionId
            if (!string.IsNullOrEmpty(sub.SessionId))
                session = deviceSessions.GetById(sub.SessionId);

            // Fallback: DeviceId + UserId
            if (session == null && !string.IsNullOrEmpty(sub.DeviceId))
                session = deviceSessions.GetByEmployeeId(sub.UserId)
                    .FirstOrDefault(s => s.DeviceId == sub.DeviceId && s.RevokedAt == null);

            // Fallback: Fuzzy match on UserAgent (for legacy subscriptions without SessionId/DeviceId)
            if (session == null && !string.IsNullOrEmpty(sub.UserAgent))
            {
                var userSessions = deviceSessions.GetAll()
                    .Where(s => s.EmployeeId == sub.UserId && s.RevokedAt == null && !s.HasPush)
                    .ToList();
                var ua = sub.UserAgent;
                session = userSessions.FirstOrDefault(s =>
                    !string.IsNullOrEmpty(s.DeviceInfo) &&
                    (s.DeviceInfo.Contains(ua, StringComparison.OrdinalIgnoreCase) ||
                     ua.Contains(s.DeviceInfo, StringComparison.OrdinalIgnoreCase)));
            }

            if (session != null)
            {
                if (!session.HasPush)
                {
                    session.PushEndpoint = sub.Endpoint;
                    session.PushP256dh = sub.P256dh ?? string.Empty;
                    session.PushAuth = sub.Auth ?? string.Empty;
                    session.PushSubscriptionType = sub.SubscriptionType ?? global::Spokes_Server.Core.Models.Core.PushSubscriptionType.WebPush;
                    session.PushPublicKey = sub.PublicKey ?? string.Empty;
                    session.PushEnabled = sub.IsEnabled;
                    session.PushSubscribedAt = sub.CreatedAt;
                    session.PushUserAgent = sub.UserAgent ?? string.Empty;
                    session.DeviceType = sub.DeviceType ?? global::Spokes_Server.Core.Models.Core.DeviceType.Desktop;
                    if (!string.IsNullOrEmpty(sub.DeviceName))
                        session.DeviceName = sub.DeviceName;
                    deviceSessions.Save(session);
                }
                merged++;
            }
            else
            {
                var endpointPreview = sub.Endpoint.Length > 30 ? sub.Endpoint[..30] + "..." : sub.Endpoint;
                logger?.LogWarning("[Migration] Orphaned PushSubscription {SubId} (user={UserId}, endpoint={Endpoint})", sub.Id, sub.UserId, endpointPreview);
                orphaned++;
            }

            try
            {
                File.Delete(file);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger?.LogWarning(ex, "[Migration] Failed to delete migrated legacy push file {File}: {Error}", file, ex.Message);
            }
        }

        try
        {
            if (Directory.Exists(pushSubDir) && !Directory.EnumerateFileSystemEntries(pushSubDir).Any())
            {
                Directory.Delete(pushSubDir, true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger?.LogWarning(ex, "[Migration] Failed to clean up empty push subscriptions directory {Dir}: {Error}", pushSubDir, ex.Message);
        }

        logger?.LogInformation("[Migration] Complete: {Merged} merged, {Orphaned} orphaned.", merged, orphaned);
    }

    public virtual void CleanupStaleDeviceSessions()
    {
        var expiredSessions = _deviceSessions.GetAll()
            .Where(s => s.RevokedAt == null && s.ExpiresAt < DateTime.UtcNow)
            .ToList();

        if (!expiredSessions.Any()) return;

        _logger?.LogInformation("[Cleanup] Revoking {Count} expired device sessions...", expiredSessions.Count);
        foreach (var session in expiredSessions)
        {
            session.RevokedAt = DateTime.UtcNow;
            if (session.HasPush)
            {
                session.PushEndpoint = null;
                session.PushEnabled = false;
            }
            _deviceSessions.Save(session);
        }
    }

    private class LegacyPushSubscription
    {
        public string Id { get; set; } = string.Empty;
        public string UserId { get; set; } = string.Empty;
        public string Endpoint { get; set; } = string.Empty;
        public string P256dh { get; set; } = string.Empty;
        public string Auth { get; set; } = string.Empty;
        public string? SessionId { get; set; }
        public string? DeviceId { get; set; }
        public string? DeviceName { get; set; }
        public string? DeviceType { get; set; }
        public string? UserAgent { get; set; }
        public string SubscriptionType { get; set; } = "WebPush";
        public string? PublicKey { get; set; }
        public bool IsEnabled { get; set; } = true;
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    }
}
