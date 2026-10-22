using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Spokes_Server.Aggregate.Contexts;
using Spokes_Server.Core.Data.Repositories.Core;
using Spokes_Server.Core.Models.Core;
using Spokes_Server.Core.Security;
using Spokes_Server.Core.Services.Communication.Chat;
using Spokes_Server.Core.Services.Communication.Notifications;
using Spokes_Server.Core.Services.Communication.Notifications.Routing;
using Spokes_Server.Core.Services.Security;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Spokes_Server.Core.Services.Communication.Presence;

/// <summary>
/// Singleton service to track which users currently have the application open and focused.
/// Tracks transient per-subscription connections to distinguish device types and maintain last-seen timestamps.
/// </summary>
public class PresenceStateService : IPushDecisionService
{
    private readonly ChatStateService? _chatStateService;
    private readonly SessionService? _sessionService;
    private readonly ILogger<PresenceStateService>? _logger;

    [ActivatorUtilitiesConstructor]
    public PresenceStateService(
        ChatStateService? chatStateService,
        SessionService? sessionService,
        ILogger<PresenceStateService>? logger = null)
    {
        _chatStateService = chatStateService;
        _sessionService = sessionService;
        _logger = logger;

        if (_chatStateService != null)
        {
            _chatStateService.VoiceMemberJoined += OnVoiceMemberChanged;
            _chatStateService.VoiceMemberLeft += OnVoiceMemberChanged;
        }
    }

    private void OnVoiceMemberChanged(string channelId, string userId)
    {
        if (!string.IsNullOrEmpty(userId))
        {
            OnUserPresenceChanged?.Invoke(userId);
        }
    }

    public PresenceStateService(ChatStateService chatStateService)
        : this(chatStateService, (SessionService?)null, (ILogger<PresenceStateService>?)null)
    {
    }

    public PresenceStateService(ChatStateService chatStateService, SessionService? sessionService)
        : this(chatStateService, sessionService, (ILogger<PresenceStateService>?)null)
    {
    }

    public PresenceStateService(ChatStateService chatStateService, CoreContext coreContext, HrContext hrContext, ILogger<PresenceStateService>? logger = null)
        : this(chatStateService, new SessionService(coreContext, hrContext), logger)
    {
    }

    public event Action<string>? OnUserPresenceChanged;

    private Action<string>? _localSessionRevoked;
    public event Action<string>? OnSessionRevoked
    {
        add
        {
            if (_sessionService != null)
                _sessionService.OnSessionRevoked += value;
            else
                _localSessionRevoked += value;
        }
        remove
        {
            if (_sessionService != null)
                _sessionService.OnSessionRevoked -= value;
            else
                _localSessionRevoked -= value;
        }
    }
    // Per-subscription active connections: Key: SubscriptionId, Value: ConnectionInfo
    private readonly ConcurrentDictionary<string, ConnectionInfo> _activeConnections = new();

    // Last-seen timestamps per (UserId, DeviceType)
    private readonly ConcurrentDictionary<(string UserId, string DeviceType), DateTime> _lastSeen = new();

    // Last-seen timestamps strictly for push-enabled connections per (UserId, DeviceType)
    private readonly ConcurrentDictionary<(string UserId, string DeviceType), DateTime> _lastSeenForPush = new();

    public const int RecentlySeenMinutes = 5;
    private const double PushDelayMinutes = 1.5; // 90 seconds grace period

    /// <summary>
    /// Register an active Blazor circuit connection for a subscription using a registration options object.
    /// </summary>
    public void RegisterConnection(ConnectionRegistrationOptions options)
    {
        if (options == null || string.IsNullOrEmpty(options.SubscriptionId)) return;

        var now = DateTime.UtcNow;
        var lastInteraction = now;

        bool isMobileConnection = options.IsCapacitorApp ||
            string.Equals(options.DeviceType, Models.Core.DeviceType.Mobile, StringComparison.OrdinalIgnoreCase);

        if (!string.IsNullOrEmpty(options.SessionId))
        {
            foreach (var kvp in _activeConnections)
            {
                if (kvp.Key != options.SubscriptionId &&
                    kvp.Value.UserId == options.UserId &&
                    kvp.Value.SessionId == options.SessionId &&
                    (isMobileConnection ||
                     kvp.Value.IsCapacitorApp ||
                     string.Equals(kvp.Value.DeviceType, Models.Core.DeviceType.Mobile, StringComparison.OrdinalIgnoreCase)))
                {
                    _activeConnections.TryRemove(kvp.Key, out _);
                }
            }
        }

        if (options.IsReconnect && !options.IsFocused)
        {
            if (_activeConnections.TryGetValue(options.SubscriptionId, out var prevInfo))
            {
                lastInteraction = prevInfo.LastInteractionUtc;
            }
            else if (_lastSeen.TryGetValue((options.UserId, options.DeviceType), out var prevSeen) && prevSeen != default)
            {
                lastInteraction = prevSeen;
            }
        }

        _activeConnections[options.SubscriptionId] = new ConnectionInfo
        {
            UserId = options.UserId,
            SessionId = options.SessionId,
            DeviceType = options.DeviceType,
            IsFocused = options.IsFocused,
            HasPushEnabled = options.HasPushEnabled,
            IsCapacitorApp = options.IsCapacitorApp,
            ClientState = ConnectionPresenceState.Active,
            LastInteractionUtc = lastInteraction
        };

        if (!options.IsReconnect || options.IsFocused)
        {
            UpdateLastSeen(options.UserId, options.DeviceType, options.HasPushEnabled);
        }

        OnUserPresenceChanged?.Invoke(options.UserId);
    }

    /// <summary>
    /// Remove an active connection when a Blazor circuit closes.
    /// </summary>
    public void RemoveConnection(string? subscriptionId)
    {
        if (string.IsNullOrEmpty(subscriptionId)) return;

        if (_activeConnections.TryRemove(subscriptionId, out var info))
        {
            if (info.IsFocused && !IsConnectionIdle(info))
            {
                UpdateLastSeen(info.UserId, info.DeviceType, info.HasPushEnabled);
            }
            else
            {
                UpdateLastSeenIfNewer(info.UserId, info.DeviceType, info.HasPushEnabled, info.LastInteractionUtc);
            }
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
            if (!isFocused && !info.IsFocused)
            {
                return;
            }

            bool wasActivelyFocused = info.IsFocused && !IsConnectionIdle(info);
            info.IsFocused = isFocused;

            if (isFocused)
            {
                info.LastInteractionUtc = DateTime.UtcNow;
                if (info.ClientState == ConnectionPresenceState.Idle)
                {
                    info.ClientState = ConnectionPresenceState.Active;
                }
                UpdateLastSeen(userId, info.DeviceType, info.HasPushEnabled);
            }
            else if (wasActivelyFocused)
            {
                info.LastInteractionUtc = DateTime.UtcNow;
                UpdateLastSeen(userId, info.DeviceType, info.HasPushEnabled);
            }
        }

        OnUserPresenceChanged?.Invoke(userId);
    }

    /// <summary>
    /// Update focus state for a subscription connection directly by subscription ID.
    /// </summary>
    public void SetConnectionFocusBySubscription(string? subscriptionId, bool isFocused)
    {
        _logger?.LogDebug("[PresenceState] SetConnectionFocusBySubscription called for {SubscriptionId}, isFocused: {IsFocused}", subscriptionId, isFocused);
        if (!string.IsNullOrEmpty(subscriptionId) && _activeConnections.TryGetValue(subscriptionId, out var info))
        {
            if (!isFocused && !info.IsFocused)
            {
                return;
            }

            _logger?.LogDebug("[PresenceState] Found connection info. UserId: {UserId}, DeviceType: {DeviceType}", info.UserId, info.DeviceType);
            bool wasActivelyFocused = info.IsFocused && !IsConnectionIdle(info);
            info.IsFocused = isFocused;

            if (isFocused)
            {
                info.LastInteractionUtc = DateTime.UtcNow;
                if (info.ClientState == ConnectionPresenceState.Idle)
                {
                    info.ClientState = ConnectionPresenceState.Active;
                }
                UpdateLastSeen(info.UserId, info.DeviceType, info.HasPushEnabled);
            }
            else if (wasActivelyFocused)
            {
                info.LastInteractionUtc = DateTime.UtcNow;
                UpdateLastSeen(info.UserId, info.DeviceType, info.HasPushEnabled);
            }

            OnUserPresenceChanged?.Invoke(info.UserId);
        }
        else
        {
            _logger?.LogDebug("[PresenceState] Connection info NOT FOUND for subscriptionId: {SubscriptionId}", subscriptionId);
        }
    }

    /// <summary>
    /// Explicitly sets the client presence state (Active, Idle, DoNotDisturb, Offline) for a connection.
    /// Used by Electron OS presence events, system tray status menu, and browser 5-minute idle timers.
    /// </summary>
    public void SetConnectionPresenceState(string? subscriptionId, ConnectionPresenceState state)
    {
        if (string.IsNullOrEmpty(subscriptionId)) return;
        if (_activeConnections.TryGetValue(subscriptionId, out var info))
        {
            info.ClientState = state;
            if (state == ConnectionPresenceState.Active)
            {
                info.LastInteractionUtc = DateTime.UtcNow;
                UpdateLastSeen(info.UserId, info.DeviceType, info.HasPushEnabled);
                TouchSession(info.SessionId);
            }
            OnUserPresenceChanged?.Invoke(info.UserId);
        }
    }

    /// <summary>
    /// Sets the last interaction timestamp for a specific connection (used for deterministic testing of idle timeouts).
    /// </summary>
    public void SetConnectionLastInteraction(string? subscriptionId, DateTime timestampUtc)
    {
        if (string.IsNullOrEmpty(subscriptionId)) return;
        if (_activeConnections.TryGetValue(subscriptionId, out var info))
        {
            info.LastInteractionUtc = timestampUtc;
            _lastSeen[(info.UserId, info.DeviceType)] = timestampUtc;
            if (info.HasPushEnabled)
            {
                _lastSeenForPush[(info.UserId, info.DeviceType)] = timestampUtc;
            }
        }
    }

    /// <summary>
    /// Periodically sweeps active connections and transitions any connection that has exceeded
    /// the 5-minute inactivity threshold to Idle, firing OnUserPresenceChanged for affected users.
    /// </summary>
    public void SweepStaleConnections()
    {
        var now = DateTime.UtcNow;
        var affectedUsers = new HashSet<string>();

        foreach (var info in _activeConnections.Values)
        {
            if (info.ClientState == ConnectionPresenceState.Active &&
                (now - info.LastInteractionUtc).TotalMinutes >= RecentlySeenMinutes)
            {
                info.ClientState = ConnectionPresenceState.Idle;
                affectedUsers.Add(info.UserId);
            }
        }

        foreach (var userId in affectedUsers)
        {
            OnUserPresenceChanged?.Invoke(userId);
        }
    }

    private static bool IsConnectionEffectiveOnline(ConnectionInfo c)
    {
        if (c.ClientState == ConnectionPresenceState.Offline)
            return false;

        if (string.Equals(c.DeviceType, Models.Core.DeviceType.Desktop, StringComparison.OrdinalIgnoreCase))
            return true;

        return c.IsFocused && c.ClientState != ConnectionPresenceState.Idle;
    }

    private bool IsConnectionIdle(ConnectionInfo c)
    {
        if (c.ClientState == ConnectionPresenceState.Idle || c.ClientState == ConnectionPresenceState.Offline)
            return true;

        var lastActivity = c.LastInteractionUtc;
        if (_lastSeen.TryGetValue((c.UserId, c.DeviceType), out var dictLastSeen) && dictLastSeen != default && dictLastSeen < lastActivity)
        {
            lastActivity = dictLastSeen;
        }

        return (DateTime.UtcNow - lastActivity).TotalMinutes >= RecentlySeenMinutes;
    }

    /// <summary>
    /// Gets the user ID associated with an active subscription connection, if any.
    /// </summary>
    public string? GetUserIdBySubscription(string? subscriptionId)
    {
        if (!string.IsNullOrEmpty(subscriptionId) && _activeConnections.TryGetValue(subscriptionId, out var info))
        {
            return info.UserId;
        }
        return null;
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
    /// Returns true if the user has at least one focused/active connection that is not idle.
    /// </summary>
    public bool IsUserFocused(string userId)
    {
        return _activeConnections.Values.Any(c =>
            c.UserId == userId &&
            c.IsFocused &&
            IsConnectionEffectiveOnline(c) &&
            !IsConnectionIdle(c));
    }

    /// <summary>
    /// Checks whether a specific connection is currently active, focused, and not idle (user actively looking at the screen).
    /// Returns false if the connection is severed, missing, unfocused, or idle.
    /// </summary>
    public bool IsConnectionActivelyViewed(string? subscriptionId)
    {
        if (string.IsNullOrEmpty(subscriptionId)) return false;
        return _activeConnections.TryGetValue(subscriptionId, out var info)
            && info.IsFocused
            && IsConnectionEffectiveOnline(info)
            && !IsConnectionIdle(info);
    }

    /// <summary>
    /// Checks whether a specific connection is associated with a mobile device.
    /// </summary>
    public bool IsConnectionMobile(string? subscriptionId)
    {
        if (string.IsNullOrEmpty(subscriptionId)) return false;
        return _activeConnections.TryGetValue(subscriptionId, out var info)
            && string.Equals(info.DeviceType, Models.Core.DeviceType.Mobile, StringComparison.OrdinalIgnoreCase);
    }

    // --- Device-tier queries ---

    /// <summary>
    /// Returns true if the user has a desktop connection but is away/idle on desktop (e.g. inactive for >= 5 minutes or screen locked).
    /// </summary>
    public bool IsUserAwayFromDesktop(string userId)
    {
        if (!HasActiveDesktopConnection(userId)) return false;
        if (IsUserFocused(userId)) return false;
        if (_chatStateService?.IsUserInAnyVoiceChannel(userId) == true) return false;

        var desktopConnections = _activeConnections.Values
            .Where(c => c.UserId == userId &&
                        c.DeviceType.Equals(Models.Core.DeviceType.Desktop, StringComparison.OrdinalIgnoreCase) &&
                        IsConnectionEffectiveOnline(c))
            .ToList();

        if (desktopConnections.Count > 0 && desktopConnections.All(IsConnectionIdle))
        {
            return true;
        }

        var lastSeen = GetLastSeenOnDesktop(userId);
        if (lastSeen.HasValue)
        {
            return (DateTime.UtcNow - lastSeen.Value).TotalMinutes >= RecentlySeenMinutes;
        }
        return false;
    }

    // --- Push-Specific Device-tier queries and Notification Routing Heuristics ---

    /// <summary>
    /// Returns true if the user has at least one focused/active connection that also has push enabled.
    /// </summary>
    public bool IsUserFocusedForPush(string userId)
    {
        return GetUserConnections(userId)
            .Any(c => c.IsFocused && c.HasPushEnabled && IsConnectionEffectiveOnline(c) && !IsConnectionIdle(c));
    }

    /// <summary>
    /// Returns true if the user has at least one active desktop connection that has push enabled.
    /// </summary>
    public bool HasActiveDesktopConnectionForPush(string userId)
    {
        return GetUserConnections(userId)
            .Any(c => c.DeviceType.Equals(Models.Core.DeviceType.Desktop, StringComparison.OrdinalIgnoreCase)
                   && c.HasPushEnabled
                   && IsConnectionEffectiveOnline(c));
    }

    /// <summary>
    /// Returns true if the user has a push-enabled desktop connection but has been unfocused for over 90 seconds.
    /// </summary>
    public bool IsUserAwayFromDesktopForPush(string userId)
    {
        if (!HasActiveDesktopConnectionForPush(userId)) return false;
        if (IsUserFocused(userId)) return false;
        if (_chatStateService?.IsUserInAnyVoiceChannel(userId) == true) return false;

        var lastSeen = GetLastSeenOnDesktopForPush(userId);
        if (lastSeen.HasValue)
        {
            return (DateTime.UtcNow - lastSeen.Value).TotalMinutes >= PushDelayMinutes;
        }
        return false;
    }

    /// <summary>
    /// Returns true if the user was seen on a desktop device within the 90-second push grace period.
    /// </summary>
    public bool WasRecentlySeenOnDesktopForPush(string userId)
    {
        var lastSeen = GetLastSeenOnDesktopForPush(userId) ?? GetLastSeenOnDesktop(userId);
        return lastSeen.HasValue && (DateTime.UtcNow - lastSeen.Value).TotalMinutes < PushDelayMinutes;
    }

    /// <summary>
    /// Checks if a matured 1-minute delayed mobile push should be suppressed because the user has refocused Spokes.
    /// </summary>
    public bool ShouldSuppressMobilePush(string userId)
    {
        return IsUserFocused(userId);
    }

    /// <summary>
    /// Determines the appropriate notification delivery tier (None, DesktopOnly, or All) using the 3-state Discord model:
    /// 1. Focused & Active -> PresenceTier.None (in-app notifications only)
    /// 2. Online on Desktop (unfocused/minimized/in tray, not Away) -> PresenceTier.DesktopOnly (Desktop push now + 1m delayed Mobile push)
    /// 3. Away on Desktop (>= 5m idle / locked / manual Away) or Offline on Desktop -> PresenceTier.All (Immediate Mobile push)
    /// </summary>
    public PresenceTier DetermineNotificationTier(string userId)
    {
        // State 1: User has app in focus and is actively viewing (not idle/away) -> Only local UI, NO push notifications
        if (IsUserFocused(userId))
            return PresenceTier.None;

        // State 2: User is Online on Desktop (not Away/Idle), with window unfocused/minimized/in tray
        if (HasActiveDesktopConnection(userId) && !IsUserAwayFromDesktop(userId))
        {
            return PresenceTier.DesktopOnly;
        }

        // State 3: Away on Desktop or Offline -> Notify all devices (Immediate Mobile push)
        return PresenceTier.All;
    }

    /// <summary>
    /// Returns true if the user is considered "Away" globally (connected, but inactive/idle on all devices for >= 5 minutes).
    /// Used by the UI layer to determine color of presence dots.
    /// </summary>
    public bool IsGloballyAway(string userId)
    {
        if (!HasActiveConnection(userId)) return false;
        if (IsUserFocused(userId)) return false;
        if (_chatStateService?.IsUserInAnyVoiceChannel(userId) == true) return false;

        var onlineConnections = _activeConnections.Values
            .Where(c => c.UserId == userId && IsConnectionEffectiveOnline(c))
            .ToList();

        if (onlineConnections.Count > 0 && onlineConnections.All(IsConnectionIdle))
        {
            return true;
        }

        var lastSeen = GetLastSeen(userId);
        if (lastSeen.HasValue)
        {
            return (DateTime.UtcNow - lastSeen.Value).TotalMinutes >= RecentlySeenMinutes;
        }
        return false;
    }

    /// <summary>
    /// Returns true if the user is actively online in the Capacitor mobile app and has no active, non-idle Desktop connection.
    /// Follows Discord's badge priority: Desktop Green Dot takes precedence when actively online on both Desktop and Mobile;
    /// when Desktop is Away or Offline while Mobile is open in the foreground, the Green Phone Icon is displayed.
    /// </summary>
    public bool IsOnlineOnCapacitorMobile(string userId)
    {
        var userConnections = _activeConnections.Values
            .Where(c => c.UserId == userId && IsConnectionEffectiveOnline(c))
            .ToList();

        bool hasActiveCapacitorMobile = userConnections.Any(c =>
            c.IsCapacitorApp &&
            c.DeviceType.Equals(Models.Core.DeviceType.Mobile, StringComparison.OrdinalIgnoreCase) &&
            c.IsFocused &&
            !IsConnectionIdle(c));

        if (!hasActiveCapacitorMobile)
            return false;

        bool hasActiveNonIdleDesktop = userConnections.Any(c =>
            c.DeviceType.Equals(Models.Core.DeviceType.Desktop, StringComparison.OrdinalIgnoreCase) &&
            !IsConnectionIdle(c));

        return !hasActiveNonIdleDesktop;
    }

    /// <summary>
    /// Returns true if the user has an active connection on ANY device type.
    /// For mobile devices, the connection must be focused to be considered active, 
    /// as minimizing the app instantly marks the user offline.
    /// </summary>
    public bool HasActiveConnection(string userId)
    {
        return _activeConnections.Values.Any(c => c.UserId == userId && IsConnectionEffectiveOnline(c));
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
    /// Returns true if the user has at least one active desktop Blazor connection (tab open and not offline/invisible).
    /// </summary>
    public bool HasActiveDesktopConnection(string userId)
    {
        return _activeConnections.Values.Any(c =>
            c.UserId == userId &&
            c.DeviceType.Equals(Models.Core.DeviceType.Desktop, StringComparison.OrdinalIgnoreCase) &&
            IsConnectionEffectiveOnline(c));
    }

    /// <summary>
    /// Returns when the user was last seen on a desktop device, or null if never tracked.
    /// </summary>
    public DateTime? GetLastSeenOnDesktop(string userId)
    {
        if (_lastSeen.TryGetValue((userId, DeviceType.Desktop), out var lastSeen))
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
        if (_lastSeenForPush.TryGetValue((userId, DeviceType.Desktop), out var lastSeen))
        {
            return lastSeen;
        }
        return null;
    }

    /// <summary>
    /// Returns true if the user was seen on ANY device within the last 5 minutes.
    /// </summary>
    public bool WasRecentlySeen(string userId)
    {
        var lastSeen = GetLastSeen(userId);
        return lastSeen.HasValue && (DateTime.UtcNow - lastSeen.Value).TotalMinutes < RecentlySeenMinutes;
    }

    /// <summary>
    /// Returns true if the user was seen on a desktop device within the last 5 minutes.
    /// </summary>
    public bool WasRecentlySeenOnDesktop(string userId)
    {
        var lastSeen = GetLastSeenOnDesktop(userId);
        return lastSeen.HasValue && (DateTime.UtcNow - lastSeen.Value).TotalMinutes < RecentlySeenMinutes;
    }


    /// <summary>
    /// Pings the server that a device is actively being used by the user physically (idle detection).
    /// </summary>
    public void PingDeviceActive(string? subscriptionId)
    {
        if (string.IsNullOrEmpty(subscriptionId)) return;
        if (_activeConnections.TryGetValue(subscriptionId, out var info))
        {
            info.LastInteractionUtc = DateTime.UtcNow;
            if (info.ClientState == ConnectionPresenceState.Idle)
            {
                info.ClientState = ConnectionPresenceState.Active;
            }
            UpdateLastSeen(info.UserId, info.DeviceType, info.HasPushEnabled);
            TouchSession(info.SessionId);
            OnUserPresenceChanged?.Invoke(info.UserId);
        }
    }

    /// <summary>
    /// Pings the server that the user is actively interacting with the app (e.g. typing, navigating).
    /// Updates the last seen timestamp on focused connections (or all connections if none are marked focused).
    /// </summary>
    public void PingUserActive(string userId)
    {
        var userConnections = _activeConnections.Values.Where(c => c.UserId == userId).ToList();
        var targetConnections = userConnections.Where(c => c.IsFocused).ToList();
        if (targetConnections.Count == 0)
        {
            targetConnections = userConnections;
        }

        foreach (var info in targetConnections)
        {
            info.LastInteractionUtc = DateTime.UtcNow;
            if (info.ClientState == ConnectionPresenceState.Idle)
            {
                info.ClientState = ConnectionPresenceState.Active;
            }
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
        var now = DateTime.UtcNow;
        _lastSeen[(userId, deviceType)] = now;
        if (hasPushEnabled)
        {
            _lastSeenForPush[(userId, deviceType)] = now;
        }

        CleanupLastSeenIfNeeded();
    }

    private void UpdateLastSeenIfNewer(string userId, string deviceType, bool hasPushEnabled, DateTime timestampUtc)
    {
        if (timestampUtc == default) return;

        var key = (userId, deviceType);
        _lastSeen.AddOrUpdate(key, timestampUtc, (_, existing) => timestampUtc > existing ? timestampUtc : existing);
        if (hasPushEnabled)
        {
            _lastSeenForPush.AddOrUpdate(key, timestampUtc, (_, existing) => timestampUtc > existing ? timestampUtc : existing);
        }

        CleanupLastSeenIfNeeded();
    }

    private void CleanupLastSeenIfNeeded()
    {
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

            var deviceType = string.IsNullOrWhiteSpace(session.DeviceType) ? Models.Core.DeviceType.Desktop : session.DeviceType;
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
    /// Updates LastSeenAt on the underlying DeviceSession via SessionService, throttled to at most once per minute
    /// to avoid excessive disk persistence operations.
    /// </summary>
    private void TouchSession(string? sessionId)
    {
        _sessionService?.TouchSession(sessionId);
    }

    /// <summary>
    /// Flushes the current LastSeenAt timestamps for all active connected sessions to the database via SessionService.
    /// Called during graceful server shutdown.
    /// </summary>
    public void FlushActiveSessionsToDatabase()
    {
        _sessionService?.FlushActiveSessions(GetOnlineSessions());
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
        => _activeConnections.Values
            .Where(c => !string.IsNullOrEmpty(c.SessionId))
            .Select(c => c.SessionId!)
            .Distinct()
            .ToList();

    /// <summary>
    /// Triggers a global broadcast to log out all active connections for the specified SessionId.
    /// </summary>
    public void TriggerRemoteLogout(string sessionId)
    {
        if (string.IsNullOrEmpty(sessionId)) return;

        if (_sessionService != null)
        {
            _sessionService.TriggerRemoteLogout(sessionId);
        }
        else
        {
            _localSessionRevoked?.Invoke(sessionId);
        }
    }

    /// <summary>
    /// Returns a snapshot of all active transient connections across the application.
    /// </summary>
    public IReadOnlyCollection<ConnectionInfo> GetActiveConnections() => _activeConnections.Values.ToList();

    /// <summary>
    /// Returns all active transient connections for a specific user.
    /// </summary>
    public IEnumerable<ConnectionInfo> GetUserConnections(string userId)
        => _activeConnections.Values.Where(c => c.UserId == userId).ToList();

    public class ConnectionInfo
    {
        public string UserId { get; set; } = string.Empty;
        public string? SessionId { get; set; }
        public string DeviceType { get; set; } = Models.Core.DeviceType.Desktop;
        public bool IsFocused { get; set; }
        public bool HasPushEnabled { get; set; }
        public bool IsCapacitorApp { get; set; }
        public ConnectionPresenceState ClientState { get; set; } = ConnectionPresenceState.Active;
        public DateTime LastInteractionUtc { get; set; } = DateTime.UtcNow;
    }
}

public enum ConnectionPresenceState
{
    Active,
    Idle,
    DoNotDisturb,
    Offline
}

public class ConnectionRegistrationOptions
{
    public string UserId { get; set; } = string.Empty;
    public string? SubscriptionId { get; set; }
    public string DeviceType { get; set; } = Spokes_Server.Core.Models.Core.DeviceType.Desktop;
    public bool HasPushEnabled { get; set; }
    public bool IsCapacitorApp { get; set; }
    public string? SessionId { get; set; }
    public bool IsFocused { get; set; }
    public bool IsReconnect { get; set; }
}
