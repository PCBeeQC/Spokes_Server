using Spokes_Server.Core.Services.Communication;
using Spokes_Server.Core.Services.Projects;
using Spokes_Server.Core.Services.Core;
using Spokes_Server.Aggregate;
using Spokes_Server.Core.Models.Core;
using System.Collections.Concurrent;

namespace Spokes_Server.Core.Services.Communication.Presence;

/// <summary>
/// Singleton service to track which users currently have the application open and focused.
/// Tracks per-subscription connections to distinguish device types and maintain last-seen timestamps.
/// </summary>
public class PresenceStateService
{
    private readonly Spokes_Server.Core.Services.Communication.Chat.ChatStateService _chatStateService;
    private readonly Database? _db;

    public PresenceStateService(Spokes_Server.Core.Services.Communication.Chat.ChatStateService chatStateService)
        : this(chatStateService, null)
    {
    }

    public PresenceStateService(Spokes_Server.Core.Services.Communication.Chat.ChatStateService chatStateService, Database? db)
    {
        _chatStateService = chatStateService;
        _db = db;
    }

    public event Action<string>? OnUserPresenceChanged;
    public event Action<string>? OnSessionRevoked;
    // Per-subscription active connections: Key: SubscriptionId, Value: ConnectionInfo
    private readonly ConcurrentDictionary<string, ConnectionInfo> _activeConnections = new();

    // Last-seen timestamps per (UserId, DeviceType)
    private readonly ConcurrentDictionary<(string UserId, string DeviceType), DateTime> _lastSeen = new();

    // Last-seen timestamps strictly for push-enabled connections per (UserId, DeviceType)
    private readonly ConcurrentDictionary<(string UserId, string DeviceType), DateTime> _lastSeenForPush = new();

    private const int RecentlySeenMinutes = 15;
    private const double PushDelayMinutes = 1.5; // 90 seconds grace period

    /// <summary>
    /// Register an active Blazor circuit connection for a subscription.
    /// </summary>
    public void RegisterConnection(string userId, string? subscriptionId, string deviceType, bool hasPushEnabled = false, string? sessionId = null, bool isFocused = false)
    {
        if (string.IsNullOrEmpty(subscriptionId)) return;

        _activeConnections[subscriptionId] = new ConnectionInfo
        {
            UserId = userId,
            SessionId = sessionId,
            DeviceType = deviceType,
            IsFocused = isFocused,
            HasPushEnabled = hasPushEnabled
        };

        UpdateLastSeen(userId, deviceType, hasPushEnabled);
        OnUserPresenceChanged?.Invoke(userId);
    }

    /// <summary>
    /// Remove an active connection when a Blazor circuit closes.
    /// </summary>
    public void RemoveConnection(string? subscriptionId)
    {
        if (string.IsNullOrEmpty(subscriptionId)) return;

        if (_activeConnections.TryRemove(subscriptionId, out var info))
        {
            UpdateLastSeen(info.UserId, info.DeviceType, info.HasPushEnabled);
            TouchSession(info.SessionId);
            OnUserPresenceChanged?.Invoke(info.UserId);
        }
    }

    /// <summary>
    /// Update focus state for a subscription connection.
    /// </summary>
    public void SetConnectionFocus(string userId, string? subscriptionId, bool isFocused)
    {
        if (!string.IsNullOrEmpty(subscriptionId) && _activeConnections.TryGetValue(subscriptionId, out var info))
        {
            info.IsFocused = isFocused;
            UpdateLastSeen(userId, info.DeviceType, info.HasPushEnabled);
        }

        OnUserPresenceChanged?.Invoke(userId);
    }

    /// <summary>
    /// Update focus state for a subscription connection directly by subscription ID.
    /// </summary>
    public void SetConnectionFocusBySubscription(string? subscriptionId, bool isFocused)
    {
        Console.WriteLine($"[PresenceState] SetConnectionFocusBySubscription called for {subscriptionId}, isFocused: {isFocused}");
        if (!string.IsNullOrEmpty(subscriptionId) && _activeConnections.TryGetValue(subscriptionId, out var info))
        {
            Console.WriteLine($"[PresenceState] Found connection info. UserId: {info.UserId}, DeviceType: {info.DeviceType}");
            info.IsFocused = isFocused;
            UpdateLastSeen(info.UserId, info.DeviceType, info.HasPushEnabled);
            OnUserPresenceChanged?.Invoke(info.UserId);
        }
        else
        {
            Console.WriteLine($"[PresenceState] Connection info NOT FOUND for subscriptionId: {subscriptionId}");
        }
    }

    /// <summary>
    /// Update the push enabled state dynamically mid-session if the user accepts a prompt.
    /// </summary>
    public void UpdatePushEnabledState(string? subscriptionId, bool hasPushEnabled)
    {
        if (!string.IsNullOrEmpty(subscriptionId) && _activeConnections.TryGetValue(subscriptionId, out var info))
        {
            info.HasPushEnabled = hasPushEnabled;
            if (hasPushEnabled)
            {
                UpdateLastSeen(info.UserId, info.DeviceType, true);
            }
        }
    }


    /// <summary>
    /// Returns true if the user has at least one focused/active connection.
    /// </summary>
    public bool IsUserFocused(string userId)
    {
        return _activeConnections.Values.Any(c => c.UserId == userId && c.IsFocused);
    }

    // --- Device-tier queries ---

    /// <summary>
    /// Returns true if the user has a desktop connection but has been unfocused for over 15 minutes.
    /// Used natively by the notification system to determine if desktop push should bypass.
    /// </summary>
    public bool IsUserAwayFromDesktop(string userId)
    {
        if (!HasActiveDesktopConnection(userId)) return false;
        if (IsUserFocused(userId)) return false;
        if (_chatStateService.IsUserInAnyVoiceChannel(userId)) return false;

        var lastSeen = GetLastSeenOnDesktop(userId);
        if (lastSeen.HasValue)
        {
            return (DateTime.UtcNow - lastSeen.Value).TotalMinutes >= 15;
        }
        return false;
    }

    // --- Push-Specific Device-tier queries ---

    /// <summary>
    /// Returns true if the user has at least one focused/active connection that also has push enabled.
    /// </summary>
    public bool IsUserFocusedForPush(string userId)
    {
        return _activeConnections.Values.Any(c => c.UserId == userId && c.IsFocused && c.HasPushEnabled);
    }

    /// <summary>
    /// Returns true if the user has at least one active desktop connection that has push enabled.
    /// </summary>
    public bool HasActiveDesktopConnectionForPush(string userId)
    {
        return _activeConnections.Values.Any(c =>
            c.UserId == userId &&
            c.DeviceType.Equals("Desktop", StringComparison.OrdinalIgnoreCase) &&
            c.HasPushEnabled);
    }

    /// <summary>
    /// Returns true if the user has a push-enabled desktop connection but has been unfocused for over 90 seconds.
    /// Used natively by the notification system to determine if a mobile push should bypass the delay.
    /// </summary>
    public bool IsUserAwayFromDesktopForPush(string userId)
    {
        if (!HasActiveDesktopConnectionForPush(userId)) return false;
        if (IsUserFocusedForPush(userId)) return false;

        var lastSeen = GetLastSeenOnDesktopForPush(userId); // We use push-specific desktop last seen
        if (lastSeen.HasValue)
        {
            return (DateTime.UtcNow - lastSeen.Value).TotalMinutes >= PushDelayMinutes;
        }
        return false;
    }

    /// <summary>
    /// Returns true if the user is considered "Away" globally (connected, but inactive on all devices for over 15 minutes).
    /// Used by the UI layer to determine color of presence dots.
    /// </summary>
    public bool IsGloballyAway(string userId)
    {
        if (!HasActiveConnection(userId)) return false;
        if (IsUserFocused(userId)) return false;
        if (_chatStateService.IsUserInAnyVoiceChannel(userId)) return false;

        var lastSeen = GetLastSeen(userId);
        if (lastSeen.HasValue)
        {
            return (DateTime.UtcNow - lastSeen.Value).TotalMinutes >= 15;
        }
        return false;
    }

    /// <summary>
    /// Returns true if the user has an active connection on ANY device type.
    /// For mobile devices, the connection must be focused to be considered active, 
    /// as minimizing the app instantly marks the user offline.
    /// </summary>
    public bool HasActiveConnection(string userId)
    {
        return _activeConnections.Values.Any(c => c.UserId == userId && (c.DeviceType == "Desktop" || c.IsFocused));
    }

    /// <summary>
    /// Returns when the user was last seen on ANY device.
    /// </summary>
    public DateTime? GetLastSeen(string userId)
    {
        var records = _lastSeen.Where(x => x.Key.UserId == userId).Select(x => x.Value).ToList();
        if (records.Count > 0)
        {
            return records.Max();
        }
        return null;
    }

    /// <summary>
    /// Returns true if the user has at least one active desktop Blazor connection (tab open).
    /// </summary>
    public bool HasActiveDesktopConnection(string userId)
    {
        return _activeConnections.Values.Any(c =>
            c.UserId == userId &&
            c.DeviceType.Equals("Desktop", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Returns when the user was last seen on a desktop device, or null if never tracked.
    /// </summary>
    public DateTime? GetLastSeenOnDesktop(string userId)
    {
        if (_lastSeen.TryGetValue((userId, "Desktop"), out var lastSeen))
        {
            return lastSeen;
        }
        return null;
    }

    /// <summary>
    /// Returns when the user was last seen on a desktop device WITH push enabled, or null if never tracked.
    /// </summary>
    public DateTime? GetLastSeenOnDesktopForPush(string userId)
    {
        if (_lastSeenForPush.TryGetValue((userId, "Desktop"), out var lastSeen))
        {
            return lastSeen;
        }
        return null;
    }

    /// <summary>
    /// Returns true if the user was seen on ANY device within the last 15 minutes.
    /// </summary>
    public bool WasRecentlySeen(string userId)
    {
        var lastSeen = GetLastSeen(userId);
        return lastSeen.HasValue && (DateTime.UtcNow - lastSeen.Value).TotalMinutes < RecentlySeenMinutes;
    }

    /// <summary>
    /// Returns true if the user was seen on a desktop device within the last 15 minutes.
    /// </summary>
    public bool WasRecentlySeenOnDesktop(string userId)
    {
        var lastSeen = GetLastSeenOnDesktop(userId);
        return lastSeen.HasValue && (DateTime.UtcNow - lastSeen.Value).TotalMinutes < RecentlySeenMinutes;
    }

    /// <summary>
    /// Returns true if the user was seen on a desktop device within the 90-second push grace period.
    /// </summary>
    public bool WasRecentlySeenOnDesktopForPush(string userId)
    {
        var lastSeen = GetLastSeenOnDesktop(userId);
        return lastSeen.HasValue && (DateTime.UtcNow - lastSeen.Value).TotalMinutes < PushDelayMinutes;
    }

    /// <summary>
    /// Pings the server that a device is actively being used by the user physically (idle detection).
    /// </summary>
    public void PingDeviceActive(string? subscriptionId)
    {
        if (string.IsNullOrEmpty(subscriptionId)) return;
        if (_activeConnections.TryGetValue(subscriptionId, out var info))
        {
            UpdateLastSeen(info.UserId, info.DeviceType, info.HasPushEnabled);
            TouchSession(info.SessionId);
            OnUserPresenceChanged?.Invoke(info.UserId);
        }
    }

    /// <summary>
    /// Pings the server that the user is actively interacting with the app (e.g. typing, navigating).
    /// Updates the last seen timestamp across all their active connections.
    /// </summary>
    public void PingUserActive(string userId)
    {
        var userConnections = _activeConnections.Values.Where(c => c.UserId == userId).ToList();
        foreach (var info in userConnections)
        {
            UpdateLastSeen(userId, info.DeviceType, info.HasPushEnabled);
            TouchSession(info.SessionId);
        }

        if (userConnections.Count == 0)
        {
            // Fallback if they have no tracked connections but are active
            UpdateLastSeen(userId, "Unknown", false);
        }

        OnUserPresenceChanged?.Invoke(userId);
    }

    private void UpdateLastSeen(string userId, string deviceType, bool hasPushEnabled)
    {
        _lastSeen[(userId, deviceType)] = DateTime.UtcNow;
        if (hasPushEnabled)
        {
            _lastSeenForPush[(userId, deviceType)] = DateTime.UtcNow;
        }

        // Bounded capacity cleanup to prevent small memory leaks over time
        if (_lastSeen.Count > 5000)
        {
            var cutoff = DateTime.UtcNow.AddDays(-30);
            var oldKeys = _lastSeen.Where(x => x.Value < cutoff).Select(x => x.Key).ToList();
            foreach (var key in oldKeys)
            {
                _lastSeen.TryRemove(key, out _);
                _lastSeenForPush.TryRemove(key, out _);
            }
        }
    }

    /// <summary>
    /// Pre-seeds in-memory last seen timestamps from persisted device sessions on server startup.
    /// </summary>
    public void InitializeFromSessions(IEnumerable<DeviceSession> sessions)
    {
        if (sessions == null) return;

        foreach (var session in sessions)
        {
            if (string.IsNullOrEmpty(session.EmployeeId) || session.LastSeenAt == default)
                continue;

            var deviceType = string.IsNullOrWhiteSpace(session.DeviceType) ? "Desktop" : session.DeviceType;
            var key = (session.EmployeeId, deviceType);

            // Keep the most recent timestamp seen across all sessions for this user + device
            _lastSeen.AddOrUpdate(
                key,
                session.LastSeenAt,
                (_, existing) => session.LastSeenAt > existing ? session.LastSeenAt : existing);

            if (session.PushEnabled && session.HasPush)
            {
                _lastSeenForPush.AddOrUpdate(
                    key,
                    session.LastSeenAt,
                    (_, existing) => session.LastSeenAt > existing ? session.LastSeenAt : existing);
            }
        }
    }

    /// <summary>
    /// Updates LastSeenAt on the underlying DeviceSession, throttled to at most once per minute
    /// to avoid excessive disk persistence operations.
    /// </summary>
    private void TouchSession(string? sessionId)
    {
        if (_db == null || string.IsNullOrEmpty(sessionId)) return;

        var session = _db.DeviceSessions.GetById(sessionId);
        if (session != null && session.LastSeenAt < DateTime.UtcNow.AddMinutes(-1))
        {
            session.LastSeenAt = DateTime.UtcNow;
            _db.DeviceSessions.Save(session);
        }
    }

    /// <summary>
    /// Flushes the current LastSeenAt timestamps for all active connected sessions to the database.
    /// Called during graceful server shutdown.
    /// </summary>
    public void FlushActiveSessionsToDatabase()
    {
        if (_db == null) return;

        var now = DateTime.UtcNow;
        var activeSessionIds = _activeConnections.Values
            .Where(c => !string.IsNullOrEmpty(c.SessionId))
            .Select(c => c.SessionId!)
            .Distinct()
            .ToList();

        foreach (var sessionId in activeSessionIds)
        {
            var session = _db.DeviceSessions.GetById(sessionId);
            if (session != null)
            {
                session.LastSeenAt = now;
                _db.DeviceSessions.Save(session);
            }
        }
    }

    /// <summary>
    /// Formats a last seen UTC timestamp into a human-friendly string.
    /// Returns "Offline" if null or default.
    /// </summary>
    public static string FormatLastSeen(DateTime? lastSeenUtc)
    {
        if (!lastSeenUtc.HasValue || lastSeenUtc.Value == default)
        {
            return "Offline";
        }

        var localTime = lastSeenUtc.Value.ToLocalTime();
        var diff = DateTime.UtcNow - lastSeenUtc.Value;

        // Future timestamp safeguard (e.g. slight clock drift)
        if (diff.TotalSeconds < 0)
        {
            return "Last seen just now";
        }

        if (diff.TotalSeconds < 60)
        {
            return "Last seen just now";
        }

        if (diff.TotalMinutes < 60)
        {
            var minutes = (int)diff.TotalMinutes;
            return minutes == 1 ? "Last seen 1 minute ago" : $"Last seen {minutes} minutes ago";
        }

        if (diff.TotalHours < 24)
        {
            var hours = (int)diff.TotalHours;
            return hours == 1 ? "Last seen 1 hour ago" : $"Last seen {hours} hours ago";
        }

        if (diff.TotalDays < 2)
        {
            return "Last seen yesterday";
        }

        if (diff.TotalDays < 7)
        {
            var days = (int)diff.TotalDays;
            return $"Last seen {days} days ago";
        }

        if (localTime.Year == DateTime.Now.Year)
        {
            return $"Last seen {localTime:MMM d}";
        }

        return $"Last seen {localTime:MMM d, yyyy}";
    }

    /// <summary>
    /// Returns a distinct list of active SessionIds.
    /// </summary>
    public IEnumerable<string> GetOnlineSessions()
    {
        return _activeConnections.Values
            .Where(c => !string.IsNullOrEmpty(c.SessionId))
            .Select(c => c.SessionId!)
            .Distinct()
            .ToList();
    }

    /// <summary>
    /// Triggers a global broadcast to log out all active connections for the specified SessionId.
    /// </summary>
    public void TriggerRemoteLogout(string sessionId)
    {
        if (!string.IsNullOrEmpty(sessionId))
        {
            OnSessionRevoked?.Invoke(sessionId);
        }
    }

    private class ConnectionInfo
    {
        public string UserId { get; set; } = string.Empty;
        public string? SessionId { get; set; }
        public string DeviceType { get; set; } = "Desktop";
        public bool IsFocused { get; set; }
        public bool HasPushEnabled { get; set; }
    }
}
