using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Spokes_Server.Core.Models.Core;
using Spokes_Server.Core.Models.HR;

namespace Spokes_Server.Core.Services.Communication.Notifications;

/// <summary>
/// Queued notification awaiting delivery when the user's schedule window opens.
/// </summary>
public class QueuedNotification
{
    public string UserId { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Body { get; set; } = string.Empty;
    public string? Url { get; set; }
    public string? Icon { get; set; }
    public string? Tag { get; set; }
    public object[]? Actions { get; set; }
    public PresenceTier? Tier { get; set; }
    public DateTime QueuedAtUtc { get; set; } = DateTime.UtcNow;
    public string? ThreadId { get; set; }
    public string? ServerName { get; set; }
    public string? ChannelName { get; set; }
    public bool IsGroupChat { get; set; }
    public int? Badge { get; set; }
}

/// <summary>
/// A chat notification that was temporarily withheld from mobile devices
/// because the user was active on a desktop.
/// </summary>
public class DelayedMobileNotification
{
    public string Type { get; set; } = "Chat"; // "Chat" or "Email"
    public string UserId { get; set; } = string.Empty;
    public string MessageId { get; set; } = string.Empty;
    public string ChannelId { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Body { get; set; } = string.Empty;
    public string? Url { get; set; }
    public string? Icon { get; set; }
    public string? Tag { get; set; }
    public object[]? Actions { get; set; }
    public DateTime ProcessAtUtc { get; set; }
    public string? ServerName { get; set; }
    public string? ChannelName { get; set; }
    public bool IsGroupChat { get; set; }
    public bool IsSilent { get; set; }
    public int? Badge { get; set; }
}

/// <summary>
/// Singleton service that checks notification schedules and queues
/// notifications for delivery when users are outside their active hours.
/// </summary>
public class NotificationQueueService
{
    private readonly ConcurrentDictionary<string, ConcurrentBag<QueuedNotification>> _queue = new();
    private readonly ConcurrentDictionary<Guid, DelayedMobileNotification> _delayedMobileQueue = new();
    private readonly ILogger<NotificationQueueService> _logger;

    public NotificationQueueService(ILogger<NotificationQueueService> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Check whether the current server local time falls within the employee's
    /// notification schedule for today.
    /// Returns true (allow notifications) when the schedule feature is disabled.
    /// </summary>
    public bool IsWithinSchedule(Employee employee)
    {
        if (!employee.NotificationScheduleEnabled)
            return true; // Schedule not active — always allow

        var now = DateTime.Now;
        var today = now.DayOfWeek;

        var daySchedule = employee.NotificationSchedule
            .FirstOrDefault(d => d.Day == today);

        // If no schedule entry for today, default to allowing
        if (daySchedule == null)
            return true;

        // Day is disabled — no notifications at all today
        if (!daySchedule.IsEnabled)
            return false;

        var currentHour = now.Hour;
        return currentHour >= daySchedule.StartHour && currentHour < daySchedule.EndHour;
    }

    /// <summary>
    /// Add a notification to the queue for later delivery.
    /// </summary>
    public void Enqueue(QueuedNotification notification)
    {
        var bag = _queue.GetOrAdd(notification.UserId, _ => new ConcurrentBag<QueuedNotification>());
        bag.Add(notification);
        _logger.LogDebug("Queued notification for user {UserId}: {Title}", notification.UserId, notification.Title);
    }

    /// <summary>
    /// Get all queued notifications for a specific user and clear them from the queue.
    /// </summary>
    public List<QueuedNotification> DequeueAll(string userId)
    {
        if (_queue.TryRemove(userId, out var bag))
        {
            return bag.ToList();
        }
        return new List<QueuedNotification>();
    }

    /// <summary>
    /// Get the user IDs that currently have queued notifications.
    /// </summary>
    public IEnumerable<string> GetQueuedUserIds()
    {
        return _queue.Keys.ToList();
    }

    /// <summary>
    /// Get the count of queued notifications for a user (for diagnostics).
    /// </summary>
    public int GetQueuedCount(string userId)
    {
        return _queue.TryGetValue(userId, out var bag) ? bag.Count : 0;
    }

    // --- Delayed Mobile Push Logic ---

    /// <summary>
    /// Add a notification to the delayed mobile queue.
    /// </summary>
    public void EnqueueDelayedMobilePush(DelayedMobileNotification notification)
    {
        _delayedMobileQueue.TryAdd(Guid.NewGuid(), notification);
        _logger.LogDebug("Queued delayed mobile push for user {UserId} at {ProcessAt}", notification.UserId, notification.ProcessAtUtc);
    }

    /// <summary>
    /// Retrieve all mobile notifications whose processing time has arrived.
    /// </summary>
    public List<DelayedMobileNotification> DequeueMaturedMobilePushes()
    {
        var now = DateTime.UtcNow;
        var maturedKeys = _delayedMobileQueue.Where(kvp => kvp.Value.ProcessAtUtc <= now).Select(kvp => kvp.Key).ToList();

        var results = new List<DelayedMobileNotification>();
        foreach (var key in maturedKeys)
        {
            if (_delayedMobileQueue.TryRemove(key, out var notif))
            {
                results.Add(notif);
            }
        }
        return results;
    }
}
