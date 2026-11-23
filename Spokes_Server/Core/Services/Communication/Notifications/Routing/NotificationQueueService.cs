using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Spokes_Server.Core.Constants;
using Spokes_Server.Core.Data;
using Spokes_Server.Core.Data.Repositories.Core;
using Spokes_Server.Core.Helpers;
using Spokes_Server.Core.Models.Communication.Notifications;
using Spokes_Server.Core.Models.Core;
using Spokes_Server.Core.Models.HR;
using Spokes_Server.Core.Services.Security;

namespace Spokes_Server.Core.Services.Communication.Notifications.Routing;

/// <summary>
/// Shared base for persisted notification queue entries composing <see cref="PushNotificationRequest"/>.
/// Preserves flat JSON serialization compatible with existing queue_state.json snapshots.
/// </summary>
public abstract class NotificationQueueItemBase
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public DateTime QueuedAtUtc { get; set; } = DateTime.UtcNow;
    public int RetryCount { get; set; } = 0;
    public DateTime? LeasedUntilUtc { get; set; }

    [JsonIgnore]
    public PushNotificationRequest Request { get; set; } = new()
    {
        UserId = string.Empty,
        Title = string.Empty,
        Body = string.Empty
    };

    public string UserId { get => Request.UserId; set => Request.UserId = value; }
    public string Title { get => Request.Title; set => Request.Title = value; }
    public string Body { get => Request.Body; set => Request.Body = value; }
    public string? Url { get => Request.Url; set => Request.Url = value; }
    public string? Icon { get => Request.Icon; set => Request.Icon = value; }
    public string? Tag { get => Request.Tag; set => Request.Tag = value; }
    public object[]? Actions { get => Request.Actions; set => Request.Actions = value; }
    public string? ServerName { get => Request.ServerName; set => Request.ServerName = value; }
    public string? ChannelName { get => Request.ChannelName; set => Request.ChannelName = value; }
    public bool IsGroupChat { get => Request.IsGroupChat; set => Request.IsGroupChat = value; }
    public int? Badge { get => Request.Badge; set => Request.Badge = value; }
    public string Category { get => Request.Category; set => Request.Category = value; }
    public bool IsSilent { get => Request.IsSilent; set => Request.IsSilent = value; }
    public string? Sound { get => Request.Sound; set => Request.Sound = value; }
    public DateTime SentAtUtc { get => Request.SentAtUtc; set => Request.SentAtUtc = value; }
    public int? TtlSeconds { get => Request.TtlSeconds; set => Request.TtlSeconds = value; }
    public bool IsEncrypted { get => Request.IsEncrypted; set => Request.IsEncrypted = value; }
    public string? MaskedBody { get => Request.MaskedBody; set => Request.MaskedBody = value; }
    public string? SourceChannelId { get => Request.SourceChannelId; set => Request.SourceChannelId = value; }
    public string? SourceMessageId { get => Request.SourceMessageId; set => Request.SourceMessageId = value; }
    public string? SourceEmailAccountId { get => Request.SourceEmailAccountId; set => Request.SourceEmailAccountId = value; }
    public string? SourceEmailFolderPath { get => Request.SourceEmailFolderPath; set => Request.SourceEmailFolderPath = value; }
    public string? SourceEmailMessageId { get => Request.SourceEmailMessageId; set => Request.SourceEmailMessageId = value; }
    public string? SourceReportId { get => Request.SourceReportId; set => Request.SourceReportId = value; }
    public string? SourceEventId { get => Request.SourceEventId; set => Request.SourceEventId = value; }

    protected static PushNotificationRequest CloneRequest(
        PushNotificationRequest source,
        PresenceTier? tierOverride = null,
        string? threadIdOverride = null,
        int? badgeOverride = null,
        bool preserveExcludedSessionIds = false) => new()
        {
            UserId = source.UserId,
            Title = source.Title,
            Body = source.Body,
            Url = source.Url,
            Icon = source.Icon,
            Tier = tierOverride ?? source.Tier,
            ExcludedSessionIds = preserveExcludedSessionIds ? source.ExcludedSessionIds : null,
            Tag = source.Tag,
            Actions = source.Actions,
            Category = source.Category,
            ThreadId = threadIdOverride ?? source.ThreadId,
            ServerName = source.ServerName,
            ChannelName = source.ChannelName,
            IsGroupChat = source.IsGroupChat,
            Badge = badgeOverride ?? source.Badge,
            IsSilent = source.IsSilent,
            Sound = source.Sound,
            SentAtUtc = source.SentAtUtc,
            TtlSeconds = source.TtlSeconds,
            IsEncrypted = source.IsEncrypted,
            MaskedBody = source.MaskedBody,
            SourceChannelId = source.SourceChannelId,
            SourceMessageId = source.SourceMessageId,
            SourceEmailAccountId = source.SourceEmailAccountId,
            SourceEmailFolderPath = source.SourceEmailFolderPath,
            SourceEmailMessageId = source.SourceEmailMessageId,
            SourceReportId = source.SourceReportId,
            SourceEventId = source.SourceEventId
        };
}

/// <summary>
/// Queued notification awaiting delivery when the user's schedule window opens.
/// Composes PushNotificationRequest.
/// </summary>
public class QueuedNotification : NotificationQueueItemBase
{
    public PresenceTier? Tier { get => Request.Tier; set => Request.Tier = value; }
    public string? ThreadId { get => Request.ThreadId; set => Request.ThreadId = value; }

    public QueuedNotification() { }

    public QueuedNotification(PushNotificationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        Request = CloneRequest(request, preserveExcludedSessionIds: true);
        QueuedAtUtc = DateTime.UtcNow;
    }

    public PushNotificationRequest ToPushNotificationRequest() => Request;
}

/// <summary>
/// A chat notification that was temporarily withheld from mobile devices
/// because the user was active on a desktop.
/// Composes PushNotificationRequest to avoid boundary parameter sprawl.
/// </summary>
public class DelayedMobileNotification : NotificationQueueItemBase
{
    public string Type { get; set; } = "Chat"; // "Chat" or "Email"
    public string MessageId { get; set; } = string.Empty;
    public string ChannelId { get; set; } = string.Empty;
    public string AccountId { get; set; } = string.Empty;
    public string FolderPath { get; set; } = string.Empty;
    public DateTime ProcessAtUtc { get; set; }

    public DelayedMobileNotification()
    {
        QueuedAtUtc = default;
    }

    public DelayedMobileNotification(
        PushNotificationRequest request,
        string messageId,
        string channelId,
        string type = "Chat",
        TimeSpan? delay = null,
        string? accountId = null,
        string? folderPath = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        var resolvedAccountId = accountId ?? request.SourceEmailAccountId ?? string.Empty;
        var resolvedFolderPath = folderPath ?? request.SourceEmailFolderPath ?? string.Empty;
        var cloned = CloneRequest(request, tierOverride: PresenceTier.MobileOnly, threadIdOverride: channelId);
        cloned.SourceChannelId = request.SourceChannelId ?? (string.Equals(type, "Chat", StringComparison.OrdinalIgnoreCase) ? channelId : null);
        cloned.SourceMessageId = request.SourceMessageId ?? (string.Equals(type, "Chat", StringComparison.OrdinalIgnoreCase) ? messageId : null);
        cloned.SourceEmailAccountId = string.IsNullOrEmpty(resolvedAccountId) ? request.SourceEmailAccountId : resolvedAccountId;
        cloned.SourceEmailFolderPath = string.IsNullOrEmpty(resolvedFolderPath) ? request.SourceEmailFolderPath : resolvedFolderPath;
        cloned.SourceEmailMessageId = request.SourceEmailMessageId ?? (string.Equals(type, "Email", StringComparison.OrdinalIgnoreCase) ? messageId : null);
        cloned.SourceReportId = request.SourceReportId ?? (string.Equals(type, "Moderation", StringComparison.OrdinalIgnoreCase) ? messageId : null);
        cloned.SourceEventId = request.SourceEventId ?? (string.Equals(type, "Calendar", StringComparison.OrdinalIgnoreCase) ? channelId : null);
        Request = cloned;

        MessageId = messageId;
        ChannelId = channelId;
        AccountId = resolvedAccountId;
        FolderPath = resolvedFolderPath;
        Type = type;
        QueuedAtUtc = DateTime.UtcNow;
        ProcessAtUtc = QueuedAtUtc.Add(delay ?? TimeSpan.FromMinutes(1));
    }

    public PushNotificationRequest ToPushNotificationRequest(int? freshBadge = null)
    {
        var cloned = CloneRequest(Request, tierOverride: PresenceTier.MobileOnly, threadIdOverride: ChannelId, badgeOverride: freshBadge);
        cloned.SourceEmailAccountId = Request.SourceEmailAccountId ?? (string.IsNullOrEmpty(AccountId) ? null : AccountId);
        cloned.SourceEmailFolderPath = Request.SourceEmailFolderPath ?? (string.IsNullOrEmpty(FolderPath) ? null : FolderPath);
        return cloned;
    }
}

/// <summary>
/// Serializable snapshot representing persisted notification queues on disk.
/// </summary>
public class NotificationQueueSnapshot
{
    public Dictionary<string, List<QueuedNotification>> QueuedNotifications { get; set; } = new();
    public List<DelayedMobileNotification> DelayedMobileNotifications { get; set; } = [];
    public List<QueuedNotification> DeadLetterNotifications { get; set; } = [];
    public List<DelayedMobileNotification> DeadLetterMobileNotifications { get; set; } = [];
}

/// <summary>
/// Singleton service that checks notification schedules and queues
/// notifications for delivery when users are outside their active hours.
/// Features capacity bounds, TTL eviction, and durable disk persistence.
/// </summary>
public class NotificationQueueService
{
    public const int MaxQueuePerUser = 100;
    public const int MaxTotalQueuedNotifications = 5000;
    public const int MaxDelayedMobileNotifications = 2000;
    public const int MaxDeliveryRetries = 5;
    public static readonly TimeSpan QueuedNotificationTtl = TimeSpan.FromHours(24);
    public static readonly TimeSpan DelayedMobileNotificationTtl = TimeSpan.FromHours(1);

    private readonly Dictionary<string, List<QueuedNotification>> _queue = new();
    private readonly Dictionary<Guid, DelayedMobileNotification> _delayedMobileQueue = new();
    private readonly List<QueuedNotification> _deadLetterQueue = [];
    private readonly List<DelayedMobileNotification> _deadLetterMobileQueue = [];
    private readonly object _lock = new();

    public IReadOnlyList<QueuedNotification> DeadLetterNotifications
    {
        get
        {
            lock (_lock)
            {
                return _deadLetterQueue.ToList();
            }
        }
    }

    public IReadOnlyList<DelayedMobileNotification> DeadLetterMobileNotifications
    {
        get
        {
            lock (_lock)
            {
                return _deadLetterMobileQueue.ToList();
            }
        }
    }

    private readonly ILogger<NotificationQueueService> _logger;
    private readonly DiskPersistenceService _persistence;
    private readonly string _stateFilePath;
    private readonly CompanyProfileRepository _companyProfileRepo;
    private readonly EncryptionService _encryptionService;
    private bool _restoredFromDisk;

    public NotificationQueueService(
        ILogger<NotificationQueueService> logger,
        DiskPersistenceService persistence,
        IConfiguration config,
        CompanyProfileRepository companyProfileRepo,
        EncryptionService? encryptionService = null)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _persistence = persistence ?? throw new ArgumentNullException(nameof(persistence));
        _companyProfileRepo = companyProfileRepo ?? throw new ArgumentNullException(nameof(companyProfileRepo));
        ArgumentNullException.ThrowIfNull(config);
        _encryptionService = encryptionService ?? new EncryptionService(config);

        var dataPath = config["DataPath"] ?? "Data";
        _stateFilePath = Path.Combine(dataPath, "Notifications", "queue_state.json");
    }

    /// <summary>
    /// Restores the queue state from disk if not already restored.
    /// This can be called eagerly during background service startup or lazily upon first queue access.
    /// </summary>
    public void RestoreFromDisk()
    {
        lock (_lock)
        {
            EnsureRestoredFromDiskLocked();
        }
    }

    private void EnsureRestoredFromDiskLocked()
    {
        if (_restoredFromDisk) return;
        _restoredFromDisk = true;

        if (!File.Exists(_stateFilePath)) return;

        try
        {
            var fileBytes = File.ReadAllBytes(_stateFilePath);
            if (fileBytes.Length == 0) return;

            var snapshot = _encryptionService.DecryptJsonEnvelopeOrLegacy<NotificationQueueSnapshot>(
                fileBytes,
                out bool wasLegacyUnwrapped);

            if (snapshot != null)
            {
                var now = DateTime.UtcNow;
                if (snapshot.QueuedNotifications != null)
                {
                    foreach (var kvp in snapshot.QueuedNotifications)
                    {
                        var valid = kvp.Value
                            .Where(n => now - n.QueuedAtUtc < QueuedNotificationTtl)
                            .Take(MaxQueuePerUser)
                            .ToList();
                        if (valid.Count > 0)
                        {
                            _queue[kvp.Key] = valid;
                        }
                    }
                }

                if (snapshot.DelayedMobileNotifications != null)
                {
                    foreach (var item in snapshot.DelayedMobileNotifications)
                    {
                        if (item.Id == Guid.Empty) item.Id = Guid.NewGuid();
                        if (item.QueuedAtUtc == default) item.QueuedAtUtc = item.ProcessAtUtc;
                        if (now - item.QueuedAtUtc < DelayedMobileNotificationTtl)
                        {
                            _delayedMobileQueue[item.Id] = item;
                        }
                    }
                }

                if (snapshot.DeadLetterNotifications != null)
                {
                    _deadLetterQueue.Clear();
                    _deadLetterQueue.AddRange(snapshot.DeadLetterNotifications);
                }

                if (snapshot.DeadLetterMobileNotifications != null)
                {
                    _deadLetterMobileQueue.Clear();
                    _deadLetterMobileQueue.AddRange(snapshot.DeadLetterMobileNotifications);
                }

                if (wasLegacyUnwrapped)
                {
                    PersistSnapshotLocked();
                }

                _logger.LogInformation("Restored {QueuedCount} queued notifications, {DelayedCount} delayed mobile pushes, and {DeadLetterCount} dead-letter items from disk",
                    _queue.Values.Sum(v => v.Count), _delayedMobileQueue.Count, _deadLetterQueue.Count + _deadLetterMobileQueue.Count);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to restore notification queue state from {FilePath}", _stateFilePath);
        }
    }

    private void PersistSnapshotLocked()
    {
        var snapshot = new NotificationQueueSnapshot
        {
            QueuedNotifications = _queue.ToDictionary(k => k.Key, v => v.Value.ToList()),
            DelayedMobileNotifications = _delayedMobileQueue.Values.ToList(),
            DeadLetterNotifications = _deadLetterQueue.ToList(),
            DeadLetterMobileNotifications = _deadLetterMobileQueue.ToList()
        };

        var encryptedEnvelopeBytes = _encryptionService.EncryptJsonEnvelopeToUtf8Bytes(snapshot);
        _persistence.QueueWriteBytes(_stateFilePath, encryptedEnvelopeBytes);
    }

    /// <summary>
    /// Gets the current time converted to the configured company timezone,
    /// or server local time if not configured or timezone lookup fails.
    /// </summary>
    public DateTime GetCurrentScheduleTime()
    {
        return TimeZoneHelper.GetCompanyTime(_companyProfileRepo);
    }

    /// <summary>
    /// Check whether the current time (evaluated in the configured company timezone)
    /// falls within the employee's notification schedule for today.
    /// Supports overnight schedules where StartHour > EndHour.
    /// Returns true (allow notifications) when the schedule feature is disabled.
    /// </summary>
    public bool IsWithinSchedule(Employee employee, DateTime? referenceTime = null)
    {
        if (!employee.NotificationScheduleEnabled)
            return true; // Schedule not active — always allow

        var now = referenceTime ?? GetCurrentScheduleTime();
        var today = now.DayOfWeek;

        var daySchedule = employee.NotificationSchedule
            .FirstOrDefault(d => d.Day == today);

        // If no schedule entry for today, default to allowing
        if (daySchedule == null)
            return true;

        // Day is disabled — no notifications at all today
        if (!daySchedule.IsEnabled)
            return false;

        if (daySchedule.StartHour == daySchedule.EndHour)
            return false;

        var currentHour = now.Hour;
        if (daySchedule.StartHour > daySchedule.EndHour)
        {
            return currentHour >= daySchedule.StartHour || currentHour < daySchedule.EndHour;
        }

        return currentHour >= daySchedule.StartHour && currentHour < daySchedule.EndHour;
    }

    /// <summary>
    /// Add a notification to the queue for later delivery.
    /// Enforces per-user limit (FIFO eviction), global capacity limit, and TTL.
    /// Persists updated state to disk.
    /// </summary>
    public void Enqueue(QueuedNotification notification)
    {
        if (notification == null || string.IsNullOrEmpty(notification.UserId)) return;
        if (notification.QueuedAtUtc == default) notification.QueuedAtUtc = DateTime.UtcNow;

        lock (_lock)
        {
            EnsureRestoredFromDiskLocked();
            var now = DateTime.UtcNow;
            if (_queue.TryGetValue(notification.UserId, out var list))
            {
                list.RemoveAll(n => now - n.QueuedAtUtc >= QueuedNotificationTtl);
            }
            else
            {
                list = [];
                _queue[notification.UserId] = list;
            }

            // Global capacity limit check
            var totalCount = _queue.Values.Sum(l => l.Count);
            if (totalCount >= MaxTotalQueuedNotifications)
            {
                string? oldestUser = null;
                QueuedNotification? oldestNotif = null;

                foreach (var kvp in _queue)
                {
                    if (kvp.Value.Count > 0)
                    {
                        var candidate = kvp.Value[0];
                        if (oldestNotif == null || candidate.QueuedAtUtc < oldestNotif.QueuedAtUtc)
                        {
                            oldestNotif = candidate;
                            oldestUser = kvp.Key;
                        }
                    }
                }

                if (oldestUser != null && _queue.TryGetValue(oldestUser, out var oldestList) && oldestList.Count > 0)
                {
                    oldestList.RemoveAt(0);
                    _logger.LogWarning("Notification queue reached global capacity ({Max}); evicted oldest notification for user {UserId}", MaxTotalQueuedNotifications, oldestUser);
                }
            }

            // Per-user capacity limit (FIFO eviction)
            while (list.Count >= MaxQueuePerUser)
            {
                list.RemoveAt(0);
                _logger.LogWarning("Notification queue reached user limit ({Max}) for {UserId}; evicting oldest", MaxQueuePerUser, notification.UserId);
            }

            list.Add(notification);
            PersistSnapshotLocked();
        }

        _logger.LogDebug("Queued notification for user {UserId}: {Title}", notification.UserId, notification.Title);
    }

    /// <summary>
    /// Get all queued notifications for a specific user and clear them from the queue.
    /// Evicts expired items and persists updated state to disk.
    /// </summary>
    public List<QueuedNotification> DequeueAll(string userId)
    {
        List<QueuedNotification> result = [];
        lock (_lock)
        {
            EnsureRestoredFromDiskLocked();
            if (_queue.TryGetValue(userId, out var list))
            {
                _queue.Remove(userId);
                var now = DateTime.UtcNow;
                result = list.Where(n => now - n.QueuedAtUtc < QueuedNotificationTtl).ToList();
                PersistSnapshotLocked();
            }
        }
        return result;
    }

    /// <summary>
    /// Leases unleased queued notifications for a user so they can be dispatched.
    /// Does not remove them from the queue until acknowledged via AcknowledgeDelivery.
    /// If delivery fails, RequeueOnFailure clears the lease or moves to dead-letter store.
    /// </summary>
    public List<QueuedNotification> LeaseDueNotifications(string userId, TimeSpan leaseDuration)
    {
        lock (_lock)
        {
            EnsureRestoredFromDiskLocked();
            var now = DateTime.UtcNow;
            if (!_queue.TryGetValue(userId, out var list)) return [];

            list.RemoveAll(n => now - n.QueuedAtUtc >= QueuedNotificationTtl);
            if (list.Count == 0)
            {
                _queue.Remove(userId);
                PersistSnapshotLocked();
                return [];
            }

            var unleased = list.Where(n => n.LeasedUntilUtc == null || n.LeasedUntilUtc.Value <= now).ToList();
            var leaseExpiry = now.Add(leaseDuration);
            foreach (var item in unleased)
            {
                item.LeasedUntilUtc = leaseExpiry;
            }

            if (unleased.Count > 0)
            {
                PersistSnapshotLocked();
            }

            return unleased;
        }
    }

    /// <summary>
    /// Confirms successful delivery of leased notifications and removes them permanently from the queue.
    /// </summary>
    public void AcknowledgeDelivery(string userId, IEnumerable<Guid> notificationIds) =>
        MutateUserQueueItemsLocked(userId, notificationIds, (list, idSet) => list.RemoveAll(n => idSet.Contains(n.Id)) > 0);

    /// <summary>
    /// Handles failed delivery of leased notifications. Increments retry count and either releases the lease
    /// for re-delivery or moves to the dead-letter store if max retries is exceeded.
    /// </summary>
    public void RequeueOnFailure(string userId, IEnumerable<Guid> notificationIds, Exception? exception = null) =>
        MutateUserQueueItemsLocked(userId, notificationIds, (list, idSet) =>
        {
            var deadLettered = new List<QueuedNotification>();
            foreach (var item in list.Where(n => idSet.Contains(n.Id)))
            {
                item.RetryCount++;
                item.LeasedUntilUtc = null;

                if (item.RetryCount >= MaxDeliveryRetries)
                {
                    deadLettered.Add(item);
                    _logger.LogWarning("Queued notification {Id} for user {UserId} exceeded max retries ({Max}); moving to dead-letter store: {Message}",
                        item.Id, userId, MaxDeliveryRetries, exception?.Message ?? "Delivery failed");
                }
                else
                {
                    _logger.LogInformation("Requeued notification {Id} for user {UserId} on delivery failure (attempt {Attempt}/{Max})",
                        item.Id, userId, item.RetryCount, MaxDeliveryRetries);
                }
            }

            foreach (var dead in deadLettered)
            {
                list.Remove(dead);
                _deadLetterQueue.Add(dead);
            }

            return true;
        });

    private void MutateUserQueueItemsLocked(
        string userId,
        IEnumerable<Guid> notificationIds,
        Func<List<QueuedNotification>, HashSet<Guid>, bool> mutator)
    {
        ArgumentNullException.ThrowIfNull(notificationIds);
        var idSet = notificationIds.ToHashSet();
        if (idSet.Count == 0) return;

        lock (_lock)
        {
            EnsureRestoredFromDiskLocked();
            if (!_queue.TryGetValue(userId, out var list)) return;

            bool shouldPersist = mutator(list, idSet);
            if (list.Count == 0)
            {
                _queue.Remove(userId);
            }

            if (shouldPersist)
            {
                PersistSnapshotLocked();
            }
        }
    }

    /// <summary>
    /// Get the user IDs that currently have queued notifications.
    /// </summary>
    public IEnumerable<string> GetQueuedUserIds()
    {
        lock (_lock)
        {
            EnsureRestoredFromDiskLocked();
            return _queue.Where(kvp => kvp.Value.Count > 0).Select(kvp => kvp.Key).ToList();
        }
    }

    /// <summary>
    /// Get the count of queued notifications for a user (for diagnostics).
    /// </summary>
    public int GetQueuedCount(string userId)
    {
        lock (_lock)
        {
            EnsureRestoredFromDiskLocked();
            return _queue.TryGetValue(userId, out var list) ? list.Count : 0;
        }
    }

    // --- Delayed Mobile Push Logic ---

    /// <summary>
    /// Add a notification to the delayed mobile queue.
    /// Enforces capacity bounds, TTL eviction, and disk persistence.
    /// </summary>
    public void EnqueueDelayedMobilePush(DelayedMobileNotification notification)
    {
        if (notification == null) return;
        if (notification.Id == Guid.Empty) notification.Id = Guid.NewGuid();
        if (notification.QueuedAtUtc == default)
        {
            notification.QueuedAtUtc = notification.ProcessAtUtc <= DateTime.UtcNow && notification.ProcessAtUtc != default
                ? notification.ProcessAtUtc
                : DateTime.UtcNow;
        }

        lock (_lock)
        {
            EnsureRestoredFromDiskLocked();
            var now = DateTime.UtcNow;
            var expiredKeys = _delayedMobileQueue
                .Where(kvp => now - (kvp.Value.QueuedAtUtc == default ? kvp.Value.ProcessAtUtc : kvp.Value.QueuedAtUtc) >= DelayedMobileNotificationTtl)
                .Select(kvp => kvp.Key)
                .ToList();
            foreach (var expKey in expiredKeys)
            {
                _delayedMobileQueue.Remove(expKey);
            }

            if (_delayedMobileQueue.Count >= MaxDelayedMobileNotifications)
            {
                var oldest = _delayedMobileQueue.OrderBy(kvp => kvp.Value.ProcessAtUtc).FirstOrDefault();
                _delayedMobileQueue.Remove(oldest.Key);
                _logger.LogWarning("Delayed mobile queue reached capacity ({Max}); evicted oldest item", MaxDelayedMobileNotifications);
            }

            _delayedMobileQueue[notification.Id] = notification;
            PersistSnapshotLocked();
        }

        _logger.LogDebug("Queued delayed mobile push for user {UserId} at {ProcessAt}", notification.UserId, notification.ProcessAtUtc);
    }

    /// <summary>
    /// Postpones the processing time of a delayed mobile notification (e.g. when user has an active desktop session at maturity).
    /// Ensures withheld mobile notifications are not permanently discarded while user is active on desktop.
    /// </summary>
    public bool PostponeDelayedMobilePush(DelayedMobileNotification notification, TimeSpan delay)
    {
        if (notification == null) return false;
        var now = DateTime.UtcNow;
        var queuedAt = notification.QueuedAtUtc == default ? notification.ProcessAtUtc : notification.QueuedAtUtc;
        if (now - queuedAt >= DelayedMobileNotificationTtl)
        {
            return false; // Already exceeded max TTL
        }

        notification.LeasedUntilUtc = null;
        notification.ProcessAtUtc = now.Add(delay);
        EnqueueDelayedMobilePush(notification);
        return true;
    }

    /// <summary>
    /// Leases matured delayed mobile notifications for dispatch without immediately dequeuing them.
    /// If delivery fails, RequeueMobilePushOnFailure requeues with backoff or moves to dead-letter store.
    /// </summary>
    public List<DelayedMobileNotification> LeaseMaturedMobilePushes(TimeSpan leaseDuration)
    {
        var now = DateTime.UtcNow;
        List<DelayedMobileNotification> results = [];

        lock (_lock)
        {
            EnsureRestoredFromDiskLocked();
            var maturedKeys = _delayedMobileQueue
                .Where(kvp => kvp.Value.ProcessAtUtc <= now && (kvp.Value.LeasedUntilUtc == null || kvp.Value.LeasedUntilUtc.Value <= now))
                .OrderBy(kvp => kvp.Value.ProcessAtUtc)
                .Select(kvp => kvp.Key)
                .ToList();

            var leaseExpiry = now.Add(leaseDuration);
            foreach (var key in maturedKeys)
            {
                if (_delayedMobileQueue.TryGetValue(key, out var notif))
                {
                    var queuedAt = notif.QueuedAtUtc == default ? notif.ProcessAtUtc : notif.QueuedAtUtc;
                    if (now - queuedAt < DelayedMobileNotificationTtl)
                    {
                        notif.LeasedUntilUtc = leaseExpiry;
                        results.Add(notif);
                    }
                    else
                    {
                        _delayedMobileQueue.Remove(key);
                    }
                }
            }

            if (results.Count > 0)
            {
                PersistSnapshotLocked();
            }
        }

        return results;
    }

    /// <summary>
    /// Confirms delivery or disposal of a delayed mobile push notification and removes it from the queue.
    /// </summary>
    public void AcknowledgeMobilePushDelivery(Guid id)
    {
        lock (_lock)
        {
            EnsureRestoredFromDiskLocked();
            if (_delayedMobileQueue.Remove(id))
            {
                PersistSnapshotLocked();
            }
        }
    }

    /// <summary>
    /// Handles failed delivery of a delayed mobile push notification. Increments retry count, clears lease,
    /// applies exponential backoff, or moves to dead-letter store if max retries is exceeded.
    /// </summary>
    public void RequeueMobilePushOnFailure(Guid id, Exception? exception = null)
    {
        lock (_lock)
        {
            EnsureRestoredFromDiskLocked();
            if (_delayedMobileQueue.TryGetValue(id, out var item))
            {
                item.RetryCount++;
                item.LeasedUntilUtc = null;

                if (item.RetryCount >= MaxDeliveryRetries)
                {
                    _delayedMobileQueue.Remove(id);
                    _deadLetterMobileQueue.Add(item);
                    _logger.LogWarning("Delayed mobile push {Id} for user {UserId} exceeded max retries ({Max}); moving to dead-letter store: {Message}",
                        id, item.UserId, MaxDeliveryRetries, exception?.Message ?? "Delivery failed");
                }
                else
                {
                    var backoffSeconds = (int)Math.Min(300, 15 * Math.Pow(2, item.RetryCount));
                    item.ProcessAtUtc = DateTime.UtcNow.AddSeconds(backoffSeconds);
                    _logger.LogInformation("Requeued delayed mobile push {Id} for user {UserId} on delivery failure with {Backoff}s backoff (attempt {Attempt}/{Max})",
                        id, item.UserId, backoffSeconds, item.RetryCount, MaxDeliveryRetries);
                }

                PersistSnapshotLocked();
            }
        }
    }

    /// <summary>
    /// Periodically purge expired items from both queues based on their TTLs.
    /// </summary>
    public int PurgeExpired()
    {
        var now = DateTime.UtcNow;
        return PurgeNotificationsLocked(
            q => now - q.QueuedAtUtc >= QueuedNotificationTtl,
            m => now - (m.QueuedAtUtc == default ? m.ProcessAtUtc : m.QueuedAtUtc) >= DelayedMobileNotificationTtl,
            "expired",
            includeDeadLetter: false);
    }

    /// <summary>
    /// Proactively purges queued, delayed mobile, and dead-letter notifications for a deleted chat message.
    /// </summary>
    public int PurgeForChatMessage(string channelId, string messageId)
    {
        if (string.IsNullOrWhiteSpace(messageId)) return 0;

        return PurgeNotificationsLocked(
            q => (string.IsNullOrWhiteSpace(channelId) || MatchesChatChannel(q, channelId) || string.IsNullOrEmpty(q.SourceChannelId))
                && (string.Equals(q.SourceMessageId, messageId, StringComparison.OrdinalIgnoreCase)
                    || (IsChatQueuedItem(q) && !string.IsNullOrEmpty(q.Tag) && q.Tag.Contains(messageId, StringComparison.OrdinalIgnoreCase))),
            m => (string.IsNullOrWhiteSpace(channelId) || string.Equals(m.SourceChannelId ?? m.ChannelId, channelId, StringComparison.OrdinalIgnoreCase))
                && (string.Equals(m.SourceMessageId, messageId, StringComparison.OrdinalIgnoreCase)
                    || (string.Equals(m.Type, "Chat", StringComparison.OrdinalIgnoreCase) && string.Equals(m.MessageId, messageId, StringComparison.OrdinalIgnoreCase))),
            $"deleted chat message {messageId}");
    }

    /// <summary>
    /// Proactively purges queued, delayed mobile, and dead-letter notifications for a channel (or for one evicted user).
    /// </summary>
    public int PurgeForChannel(string channelId, string? userId = null)
    {
        if (string.IsNullOrWhiteSpace(channelId)) return 0;

        return PurgeNotificationsLocked(
            q => (userId == null || string.Equals(q.UserId, userId, StringComparison.OrdinalIgnoreCase))
                && MatchesChatChannel(q, channelId),
            m => (userId == null || string.Equals(m.UserId, userId, StringComparison.OrdinalIgnoreCase))
                && (string.Equals(m.SourceChannelId, channelId, StringComparison.OrdinalIgnoreCase)
                    || (string.Equals(m.Type, "Chat", StringComparison.OrdinalIgnoreCase) && string.Equals(m.ChannelId, channelId, StringComparison.OrdinalIgnoreCase))),
            userId == null ? $"channel {channelId}" : $"channel {channelId} for user {userId}");
    }

    /// <summary>
    /// Proactively purges queued, delayed mobile, and dead-letter moderation notifications for a resolved or removed report.
    /// </summary>
    public int PurgeForModerationReport(string reportId, string? userId = null)
    {
        if (string.IsNullOrWhiteSpace(reportId)) return 0;

        return PurgeNotificationsLocked(
            q => (userId == null || string.Equals(q.UserId, userId, StringComparison.OrdinalIgnoreCase))
                && (string.Equals(q.SourceReportId, reportId, StringComparison.OrdinalIgnoreCase)
                    || (string.Equals(q.Category, "moderation", StringComparison.OrdinalIgnoreCase)
                        && (string.Equals(q.ThreadId, $"mod-{reportId}", StringComparison.OrdinalIgnoreCase)
                            || string.Equals(q.Tag, $"mod-{reportId}", StringComparison.OrdinalIgnoreCase)))),
            m => (userId == null || string.Equals(m.UserId, userId, StringComparison.OrdinalIgnoreCase))
                && (string.Equals(m.SourceReportId, reportId, StringComparison.OrdinalIgnoreCase)
                    || (string.Equals(m.Type, "Moderation", StringComparison.OrdinalIgnoreCase) && string.Equals(m.MessageId, reportId, StringComparison.OrdinalIgnoreCase))),
            userId == null ? $"moderation report {reportId}" : $"moderation report {reportId} for user {userId}");
    }

    /// <summary>
    /// Proactively purges queued, delayed mobile, and dead-letter notifications for a deleted or un-RSVPed calendar event.
    /// </summary>
    public int PurgeForCalendarEvent(string eventId, string? userId = null)
    {
        if (string.IsNullOrWhiteSpace(eventId)) return 0;

        return PurgeNotificationsLocked(
            q => (userId == null || string.Equals(q.UserId, userId, StringComparison.OrdinalIgnoreCase))
                && (string.Equals(q.SourceEventId, eventId, StringComparison.OrdinalIgnoreCase)
                    || (string.Equals(q.Category, NotificationCategories.Calendar, StringComparison.OrdinalIgnoreCase)
                        && !string.IsNullOrEmpty(q.Tag)
                        && q.Tag.StartsWith($"calendar_{eventId}_", StringComparison.OrdinalIgnoreCase))),
            m => (userId == null || string.Equals(m.UserId, userId, StringComparison.OrdinalIgnoreCase))
                && (string.Equals(m.SourceEventId, eventId, StringComparison.OrdinalIgnoreCase)
                    || (string.Equals(m.Type, "Calendar", StringComparison.OrdinalIgnoreCase)
                        && (string.Equals(m.ChannelId, eventId, StringComparison.OrdinalIgnoreCase)
                            || string.Equals(m.MessageId, eventId, StringComparison.OrdinalIgnoreCase)
                            || (!string.IsNullOrEmpty(m.MessageId) && m.MessageId.StartsWith($"calendar_{eventId}_", StringComparison.OrdinalIgnoreCase))))),
            userId == null ? $"calendar event {eventId}" : $"calendar event {eventId} for user {userId}");
    }

    /// <summary>
    /// Proactively purges all queued, delayed mobile, and dead-letter notifications for a deleted/banned/inactive user.
    /// </summary>
    public int PurgeForUser(string userId)
    {
        if (string.IsNullOrWhiteSpace(userId)) return 0;

        return PurgeNotificationsLocked(
            q => string.Equals(q.UserId, userId, StringComparison.OrdinalIgnoreCase),
            m => string.Equals(m.UserId, userId, StringComparison.OrdinalIgnoreCase),
            $"user {userId}");
    }

    private int PurgeNotificationsLocked(
        Func<QueuedNotification, bool> queuedPredicate,
        Func<DelayedMobileNotification, bool> mobilePredicate,
        string reason,
        bool includeDeadLetter = true)
    {
        int purgedCount = 0;

        lock (_lock)
        {
            EnsureRestoredFromDiskLocked();

            var emptyUsers = new List<string>();
            foreach (var kvp in _queue)
            {
                var removed = kvp.Value.RemoveAll(n => queuedPredicate(n));
                purgedCount += removed;
                if (kvp.Value.Count == 0)
                {
                    emptyUsers.Add(kvp.Key);
                }
            }

            foreach (var emptyUser in emptyUsers)
            {
                _queue.Remove(emptyUser);
            }

            var matchingDelayedKeys = _delayedMobileQueue
                .Where(kvp => mobilePredicate(kvp.Value))
                .Select(kvp => kvp.Key)
                .ToList();
            foreach (var key in matchingDelayedKeys)
            {
                _delayedMobileQueue.Remove(key);
                purgedCount++;
            }

            if (includeDeadLetter)
            {
                purgedCount += _deadLetterQueue.RemoveAll(n => queuedPredicate(n));
                purgedCount += _deadLetterMobileQueue.RemoveAll(m => mobilePredicate(m));
            }

            if (purgedCount > 0)
            {
                _logger.LogInformation("Proactively purged {Count} queued notifications ({Reason})", purgedCount, reason);
                PersistSnapshotLocked();
            }
        }

        return purgedCount;
    }

    private static bool IsChatQueuedItem(QueuedNotification q) =>
        string.Equals(q.Category, NotificationCategories.Chat, StringComparison.OrdinalIgnoreCase)
        || (!string.IsNullOrEmpty(q.Url) && q.Url.StartsWith("/chat", StringComparison.OrdinalIgnoreCase));

    private static bool MatchesChatChannel(QueuedNotification q, string channelId)
    {
        if (string.Equals(q.SourceChannelId, channelId, StringComparison.OrdinalIgnoreCase))
            return true;

        if (!IsChatQueuedItem(q))
            return false;

        if (string.Equals(q.ThreadId, channelId, StringComparison.OrdinalIgnoreCase))
            return true;

        if (!string.IsNullOrEmpty(q.Url))
        {
            var segments = q.Url.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length >= 2 && string.Equals(segments[0], "chat", StringComparison.OrdinalIgnoreCase)
                && string.Equals(segments[1], channelId, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
