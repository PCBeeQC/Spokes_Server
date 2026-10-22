using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Spokes_Server.Core.Data;
using Spokes_Server.Core.Data.Repositories.Core;
using Spokes_Server.Core.Helpers;
using Spokes_Server.Core.Models.Core;
using Spokes_Server.Core.Models.HR;

using System.Text.Json.Serialization;

namespace Spokes_Server.Core.Services.Communication.Notifications.Routing;

/// <summary>
/// Queued notification awaiting delivery when the user's schedule window opens.
/// Composes PushNotificationRequest.
/// </summary>
public class QueuedNotification
{
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
    public PresenceTier? Tier { get => Request.Tier; set => Request.Tier = value; }
    public DateTime QueuedAtUtc { get; set; } = DateTime.UtcNow;
    public string? ThreadId { get => Request.ThreadId; set => Request.ThreadId = value; }
    public string? ServerName { get => Request.ServerName; set => Request.ServerName = value; }
    public string? ChannelName { get => Request.ChannelName; set => Request.ChannelName = value; }
    public bool IsGroupChat { get => Request.IsGroupChat; set => Request.IsGroupChat = value; }
    public int? Badge { get => Request.Badge; set => Request.Badge = value; }
    public Guid Id { get; set; } = Guid.NewGuid();
    public int RetryCount { get; set; } = 0;
    public DateTime? LeasedUntilUtc { get; set; }
    public string Category { get => Request.Category; set => Request.Category = value; }
    public bool IsSilent { get => Request.IsSilent; set => Request.IsSilent = value; }

    public QueuedNotification() { }

    public QueuedNotification(PushNotificationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        Request = new PushNotificationRequest
        {
            UserId = request.UserId,
            Title = request.Title,
            Body = request.Body,
            Url = request.Url,
            Icon = request.Icon,
            Tier = request.Tier,
            Tag = request.Tag,
            Actions = request.Actions,
            Category = request.Category,
            ThreadId = request.ThreadId,
            ServerName = request.ServerName,
            ChannelName = request.ChannelName,
            IsGroupChat = request.IsGroupChat,
            Badge = request.Badge,
            IsSilent = request.IsSilent,
            Sound = request.Sound
        };
        QueuedAtUtc = DateTime.UtcNow;
    }

    public PushNotificationRequest ToPushNotificationRequest() => Request;
}

/// <summary>
/// A chat notification that was temporarily withheld from mobile devices
/// because the user was active on a desktop.
/// Composes PushNotificationRequest to avoid boundary parameter sprawl.
/// </summary>
public class DelayedMobileNotification
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Type { get; set; } = "Chat"; // "Chat" or "Email"
    public string MessageId { get; set; } = string.Empty;
    public string ChannelId { get; set; } = string.Empty;
    public DateTime ProcessAtUtc { get; set; }
    public DateTime QueuedAtUtc { get; set; }
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
    public bool IsSilent { get => Request.IsSilent; set => Request.IsSilent = value; }
    public int? Badge { get => Request.Badge; set => Request.Badge = value; }
    public string? Sound { get => Request.Sound; set => Request.Sound = value; }
    public string Category { get => Request.Category; set => Request.Category = value; }
    public bool IsEncrypted { get => Request.IsEncrypted; set => Request.IsEncrypted = value; }
    public string? MaskedBody { get => Request.MaskedBody; set => Request.MaskedBody = value; }

    public DelayedMobileNotification() { }

    public DelayedMobileNotification(
        PushNotificationRequest request,
        string messageId,
        string channelId,
        string type = "Chat",
        TimeSpan? delay = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        Request = new PushNotificationRequest
        {
            UserId = request.UserId,
            Title = request.Title,
            Body = request.Body,
            Url = request.Url,
            Icon = request.Icon,
            Tier = PresenceTier.MobileOnly,
            Tag = request.Tag,
            Actions = request.Actions,
            Category = request.Category,
            ThreadId = channelId,
            ServerName = request.ServerName,
            ChannelName = request.ChannelName,
            IsGroupChat = request.IsGroupChat,
            Badge = request.Badge,
            IsSilent = request.IsSilent,
            Sound = request.Sound,
            IsEncrypted = request.IsEncrypted,
            MaskedBody = request.MaskedBody
        };
        MessageId = messageId;
        ChannelId = channelId;
        Type = type;
        QueuedAtUtc = DateTime.UtcNow;
        ProcessAtUtc = QueuedAtUtc.Add(delay ?? TimeSpan.FromMinutes(1));
    }

    public PushNotificationRequest ToPushNotificationRequest(int? freshBadge = null)
    {
        return new PushNotificationRequest
        {
            UserId = Request.UserId,
            Title = Request.Title,
            Body = Request.Body,
            Url = Request.Url,
            Icon = Request.Icon,
            Tier = PresenceTier.MobileOnly,
            Tag = Request.Tag,
            Actions = Request.Actions,
            Category = Request.Category,
            ThreadId = ChannelId,
            ServerName = Request.ServerName,
            ChannelName = Request.ChannelName,
            IsGroupChat = Request.IsGroupChat,
            Badge = freshBadge ?? Request.Badge,
            IsSilent = Request.IsSilent,
            Sound = Request.Sound,
            IsEncrypted = Request.IsEncrypted,
            MaskedBody = Request.MaskedBody
        };
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
    private bool _restoredFromDisk;

    public NotificationQueueService(
        ILogger<NotificationQueueService> logger,
        DiskPersistenceService persistence,
        IConfiguration config,
        CompanyProfileRepository companyProfileRepo)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _persistence = persistence ?? throw new ArgumentNullException(nameof(persistence));
        _companyProfileRepo = companyProfileRepo ?? throw new ArgumentNullException(nameof(companyProfileRepo));
        ArgumentNullException.ThrowIfNull(config);

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
            var json = File.ReadAllText(_stateFilePath);
            var snapshot = JsonSerializer.Deserialize<NotificationQueueSnapshot>(json);
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
                        var queuedAt = item.QueuedAtUtc == default ? item.ProcessAtUtc : item.QueuedAtUtc;
                        if (now - queuedAt < DelayedMobileNotificationTtl)
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

        _persistence.QueueWrite(_stateFilePath, snapshot);
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
    public void AcknowledgeDelivery(string userId, IEnumerable<Guid> notificationIds)
    {
        ArgumentNullException.ThrowIfNull(notificationIds);
        var idSet = notificationIds.ToHashSet();
        if (idSet.Count == 0) return;

        lock (_lock)
        {
            EnsureRestoredFromDiskLocked();
            if (_queue.TryGetValue(userId, out var list))
            {
                int removed = list.RemoveAll(n => idSet.Contains(n.Id));
                if (list.Count == 0)
                {
                    _queue.Remove(userId);
                }
                if (removed > 0)
                {
                    PersistSnapshotLocked();
                }
            }
        }
    }

    /// <summary>
    /// Handles failed delivery of leased notifications. Increments retry count and either releases the lease
    /// for re-delivery or moves to the dead-letter store if max retries is exceeded.
    /// </summary>
    public void RequeueOnFailure(string userId, IEnumerable<Guid> notificationIds, Exception? exception = null)
    {
        ArgumentNullException.ThrowIfNull(notificationIds);
        var idSet = notificationIds.ToHashSet();
        if (idSet.Count == 0) return;

        lock (_lock)
        {
            EnsureRestoredFromDiskLocked();
            if (_queue.TryGetValue(userId, out var list))
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

                if (list.Count == 0)
                {
                    _queue.Remove(userId);
                }

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
    /// Retrieve all mobile notifications whose processing time has arrived in FIFO order.
    /// Evicts expired items and persists updated state to disk.
    /// </summary>
    public List<DelayedMobileNotification> DequeueMaturedMobilePushes()
    {
        var now = DateTime.UtcNow;
        List<DelayedMobileNotification> results = [];

        lock (_lock)
        {
            EnsureRestoredFromDiskLocked();
            var maturedKeys = _delayedMobileQueue
                .Where(kvp => kvp.Value.ProcessAtUtc <= now)
                .OrderBy(kvp => kvp.Value.ProcessAtUtc)
                .Select(kvp => kvp.Key)
                .ToList();

            foreach (var key in maturedKeys)
            {
                if (_delayedMobileQueue.TryGetValue(key, out var notif))
                {
                    _delayedMobileQueue.Remove(key);
                    var queuedAt = notif.QueuedAtUtc == default ? notif.ProcessAtUtc : notif.QueuedAtUtc;
                    if (now - queuedAt < DelayedMobileNotificationTtl)
                    {
                        results.Add(notif);
                    }
                }
            }

            if (maturedKeys.Count > 0)
            {
                PersistSnapshotLocked();
            }
        }

        return results;
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
        int purgedCount = 0;

        lock (_lock)
        {
            EnsureRestoredFromDiskLocked();
            var emptyUsers = new List<string>();
            foreach (var kvp in _queue)
            {
                var removed = kvp.Value.RemoveAll(n => now - n.QueuedAtUtc >= QueuedNotificationTtl);
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

            var expiredDelayedKeys = _delayedMobileQueue
                .Where(kvp => now - (kvp.Value.QueuedAtUtc == default ? kvp.Value.ProcessAtUtc : kvp.Value.QueuedAtUtc) >= DelayedMobileNotificationTtl)
                .Select(kvp => kvp.Key)
                .ToList();

            foreach (var key in expiredDelayedKeys)
            {
                _delayedMobileQueue.Remove(key);
                purgedCount++;
            }

            if (purgedCount > 0)
            {
                _logger.LogInformation("Purged {Count} expired notifications from queue", purgedCount);
                PersistSnapshotLocked();
            }
        }

        return purgedCount;
    }
}
