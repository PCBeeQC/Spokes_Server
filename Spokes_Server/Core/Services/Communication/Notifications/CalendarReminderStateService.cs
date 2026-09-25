using System;
using System.Collections.Concurrent;
using Spokes_Server.Core.Models.HR;

namespace Spokes_Server.Core.Services.Communication.Notifications;

/// <summary>
/// Singleton service coordinating the real-time triggering and dispatching of calendar event reminders.
/// Tracks dispatched reminders to prevent duplicate triggers across ticks and restarts.
/// </summary>
public class CalendarReminderStateService
{
    private readonly ConcurrentDictionary<string, DateTime> _dispatchedCache = new();

    /// <summary>
    /// Event fired when a calendar reminder is triggered for a specific user.
    /// Scoped notification services listen to this event to play sounds and display alerts.
    /// </summary>
    public event Action<string, CalendarEvent, CalendarReminder>? OnReminderTriggered;

    /// <summary>
    /// Checks if a specific reminder has already been dispatched for this event, reminder, user, and start time.
    /// </summary>
    public bool HasBeenDispatched(string eventId, string reminderId, string userId, long startTicks)
    {
        var key = BuildKey(eventId, reminderId, userId, startTicks);
        return _dispatchedCache.ContainsKey(key);
    }

    /// <summary>
    /// Attempts to mark a reminder as dispatched. Returns true if successfully marked, false if already dispatched.
    /// </summary>
    public bool TryMarkDispatched(string eventId, string reminderId, string userId, long startTicks)
    {
        var key = BuildKey(eventId, reminderId, userId, startTicks);
        return _dispatchedCache.TryAdd(key, DateTime.UtcNow);
    }

    /// <summary>
    /// Dispatches a reminder trigger for a user.
    /// </summary>
    public void TriggerReminder(string userId, CalendarEvent evt, CalendarReminder reminder)
    {
        OnReminderTriggered?.Invoke(userId, evt, reminder);
    }

    /// <summary>
    /// Prunes cache entries older than the specified age to manage memory over time.
    /// </summary>
    public void CleanupOldEntries(TimeSpan maxAge)
    {
        var threshold = DateTime.UtcNow - maxAge;
        foreach (var kvp in _dispatchedCache)
        {
            if (kvp.Value < threshold)
            {
                _dispatchedCache.TryRemove(kvp.Key, out _);
            }
        }
    }

    public static string BuildKey(string eventId, string reminderId, string userId, long startTicks)
        => $"{eventId}_{reminderId}_{userId}_{startTicks}";
}
