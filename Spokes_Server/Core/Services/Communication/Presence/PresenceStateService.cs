using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Spokes_Server.Aggregate.Contexts;
using Spokes_Server.Core.Data.Repositories.Core;
using Spokes_Server.Core.Models.Core;
using Spokes_Server.Core.Security;
using Spokes_Server.Core.Services.Communication.Voice;
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
    private readonly VoiceStateService? _voiceStateService;
    private readonly SessionService? _sessionService;
    private readonly ILogger<PresenceStateService>? _logger;

    [ActivatorUtilitiesConstructor]
    public PresenceStateService(
        VoiceStateService? voiceStateService,
        SessionService? sessionService,
        ILogger<PresenceStateService>? logger = null)
    {
        _voiceStateService = voiceStateService;
        _sessionService = sessionService;
        _logger = logger;

        if (_voiceStateService != null)
        {
            _voiceStateService.VoiceMemberJoined += OnVoiceMemberChanged;
            _voiceStateService.VoiceMemberLeft += OnVoiceMemberChanged;
        }
    }

    private void OnVoiceMemberChanged(string channelId, string userId)
    {
        if (!string.IsNullOrEmpty(userId))
        {
            OnUserPresenceChanged?.Invoke(userId);
        }
    }

    public PresenceStateService(VoiceStateService voiceStateService)
        : this(voiceStateService, (SessionService?)null, (ILogger<PresenceStateService>?)null)
    {
    }

    public PresenceStateService(VoiceStateService voiceStateService, SessionService? sessionService)
        : this(voiceStateService, sessionService, (ILogger<PresenceStateService>?)null)
    {
    }

    public PresenceStateService(VoiceStateService voiceStateService, CoreContext coreContext, HrContext hrContext, ILogger<PresenceStateService>? logger = null)
        : this(voiceStateService, new SessionService(coreContext, hrContext), logger)
    {
    }

    public event Action<string>? OnUserPresenceChanged;

    // Per-subscription active connections: Key: SubscriptionId, Value: ConnectionInfo
    private readonly ConcurrentDictionary<string, ConnectionInfo> _activeConnections = new();

    private readonly record struct UnloadedTombstone(DateTime UnloadedAtUtc, long Sequence);

    // Recently unloaded/terminated subscriptions so late out-of-order JS interop cannot resurrect a closed tab
    private readonly ConcurrentDictionary<string, UnloadedTombstone> _unloadedSubscriptions = new();

    // Last-seen timestamps per (UserId, DeviceType)
    private readonly ConcurrentDictionary<(string UserId, string DeviceType), DateTime> _lastSeen = new();

    public const int RecentlySeenMinutes = 5;
    private const int UnloadedTombstoneRetentionMinutes = 15;

    private static bool InvokeTransportAlive(Func<bool> predicate)
    {
        try
        {
            return predicate();
        }
        catch
        {
            return false;
        }
    }

    private static bool IsTransportAlive(ConnectionInfo c)
        => c.IsTransportAlive == null || InvokeTransportAlive(c.IsTransportAlive);

    private bool TryGetActiveUnloadedTombstone(string? subscriptionId, out UnloadedTombstone tombstone)
    {
        tombstone = default;
        if (string.IsNullOrEmpty(subscriptionId)) return false;
        if (_unloadedSubscriptions.TryGetValue(subscriptionId, out var entry))
        {
            if ((DateTime.UtcNow - entry.UnloadedAtUtc).TotalMinutes < UnloadedTombstoneRetentionMinutes)
            {
                tombstone = entry;
                return true;
            }
            _unloadedSubscriptions.TryRemove(subscriptionId, out _);
        }
        return false;
    }

    private bool IsSubscriptionUnloaded(string? subscriptionId)
        => TryGetActiveUnloadedTombstone(subscriptionId, out _);

    private static bool IsStaleSequence(long? incomingSequence, long lastSequence)
        => incomingSequence.HasValue && incomingSequence.Value > 0 && incomingSequence.Value < lastSequence;

    private static void AdvanceSequence(ConnectionInfo info, long? incomingSequence)
    {
        if (incomingSequence.HasValue && incomingSequence.Value > info.LastPresenceSequence)
        {
            info.LastPresenceSequence = incomingSequence.Value;
        }
    }

    /// <summary>
    /// Returns true if the given subscription ID is currently registered in active connections and its transport is alive.
    /// </summary>
    public bool HasConnection(string? subscriptionId)
    {
        return !string.IsNullOrEmpty(subscriptionId)
            && _activeConnections.TryGetValue(subscriptionId, out var info)
            && IsTransportAlive(info);
    }

    /// <summary>
    /// Register an active Blazor circuit connection for a subscription using a registration options object.
    /// </summary>
    public void RegisterConnection(ConnectionRegistrationOptions options)
    {
        if (options == null || string.IsNullOrEmpty(options.SubscriptionId)) return;

        if (options.IsTransportAlive != null && !InvokeTransportAlive(options.IsTransportAlive))
            return;

        if (TryGetActiveUnloadedTombstone(options.SubscriptionId, out var tombstone))
        {
            bool newerThanTombstone = tombstone.Sequence > 0
                && options.PresenceSequence.HasValue
                && options.PresenceSequence.Value > tombstone.Sequence;
            if (!options.IsReconnect && !newerThanTombstone)
                return;

            _unloadedSubscriptions.TryRemove(options.SubscriptionId, out _);
        }

        var now = DateTime.UtcNow;
        var lastInteraction = now;

        bool isMobileConnection = options.IsCapacitorApp ||
            string.Equals(options.DeviceType, Models.Core.DeviceType.Mobile, StringComparison.OrdinalIgnoreCase);

        bool upgradedFromDesktop = false;
        bool existingFocused = false;
        var existingClientState = ConnectionPresenceState.Active;
        DateTime existingLastInteraction = now;
        long existingSequence = 0;
        Func<bool>? existingTransportAlive = null;

        if (_activeConnections.TryGetValue(options.SubscriptionId, out var existingForSub))
        {
            lock (existingForSub.SyncRoot)
            {
                if (isMobileConnection &&
                    string.Equals(existingForSub.DeviceType, Models.Core.DeviceType.Desktop, StringComparison.OrdinalIgnoreCase))
                {
                    upgradedFromDesktop = true;
                }
                existingFocused = existingForSub.IsFocused;
                existingClientState = existingForSub.ClientState;
                existingLastInteraction = existingForSub.LastInteractionUtc;
                existingSequence = existingForSub.LastPresenceSequence;
                existingTransportAlive = existingForSub.IsTransportAlive;
            }
        }

        var effectiveTransportAlive = options.IsTransportAlive ?? existingTransportAlive;
        if (effectiveTransportAlive != null && !InvokeTransportAlive(effectiveTransportAlive))
            return;

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
                    if (isMobileConnection &&
                        string.Equals(kvp.Value.DeviceType, Models.Core.DeviceType.Desktop, StringComparison.OrdinalIgnoreCase))
                    {
                        upgradedFromDesktop = true;
                    }
                    _activeConnections.TryRemove(kvp.Key, out _);
                }
            }
        }

        bool preserveExisting = options.PreserveExistingActivity && existingForSub != null;
        bool effectiveIsFocused = preserveExisting ? existingFocused : options.IsFocused;
        var effectiveClientState = preserveExisting ? existingClientState : ConnectionPresenceState.Active;
        long effectiveSequence = Math.Max(existingSequence, options.PresenceSequence ?? 0);

        if (preserveExisting)
        {
            lastInteraction = existingLastInteraction;
        }
        else if (options.IsReconnect && !effectiveIsFocused)
        {
            if (existingForSub != null)
            {
                lastInteraction = existingLastInteraction;
            }
            else if (_lastSeen.TryGetValue((options.UserId, options.DeviceType), out var prevSeen) && prevSeen != default)
            {
                lastInteraction = prevSeen;
            }
        }

        var normalizedDeviceType = isMobileConnection ? Models.Core.DeviceType.Mobile : options.DeviceType;

        _activeConnections[options.SubscriptionId] = new ConnectionInfo
        {
            UserId = options.UserId,
            SessionId = options.SessionId,
            DeviceType = normalizedDeviceType,
            IsFocused = effectiveIsFocused,
            HasPushEnabled = options.HasPushEnabled,
            IsCapacitorApp = options.IsCapacitorApp,
            ClientState = effectiveClientState,
            LastInteractionUtc = lastInteraction,
            LastPresenceSequence = effectiveSequence,
            IsTransportAlive = effectiveTransportAlive
        };

        if (upgradedFromDesktop && !HasActiveDesktopConnection(options.UserId))
        {
            _lastSeen.TryRemove((options.UserId, Models.Core.DeviceType.Desktop), out _);
        }

        if ((!options.IsReconnect && !preserveExisting) || effectiveIsFocused)
        {
            UpdateLastSeen(options.UserId, normalizedDeviceType);
        }

        OnUserPresenceChanged?.Invoke(options.UserId);
    }

    /// <summary>
    /// Marks a connection offline due to explicit client unload/termination beacon and prevents late
    /// out-of-order hub invocations from resurrecting it unless the transport genuinely reconnects
    /// or reports a strictly newer presence sequence on a live circuit.
    /// </summary>
    public void MarkConnectionOffline(string? subscriptionId, long? sequence = null)
        => RemoveConnection(subscriptionId, markTerminated: true, sequence: sequence);

    /// <summary>
    /// Remove an active connection when a Blazor circuit disconnects or closes.
    /// </summary>
    public void RemoveConnection(string? subscriptionId)
        => RemoveConnection(subscriptionId, markTerminated: false, sequence: null);

    /// <summary>
    /// Remove an active connection, optionally recording an unload tombstone to block late re-registration races.
    /// </summary>
    public void RemoveConnection(string? subscriptionId, bool markTerminated, long? sequence = null)
    {
        if (string.IsNullOrEmpty(subscriptionId)) return;

        if (sequence.HasValue && sequence.Value > 0 && _activeConnections.TryGetValue(subscriptionId, out var existingInfo))
        {
            lock (existingInfo.SyncRoot)
            {
                if (IsStaleSequence(sequence, existingInfo.LastPresenceSequence))
                {
                    _logger?.LogDebug(
                        "[PresenceState] Ignoring stale offline/remove for {SubscriptionId} (seq={Seq} < lastSeq={LastSeq})",
                        subscriptionId, sequence.Value, existingInfo.LastPresenceSequence);
                    return;
                }
            }
        }

        if (markTerminated)
        {
            var incomingSeq = sequence.GetValueOrDefault();
            var now = DateTime.UtcNow;
            _unloadedSubscriptions.AddOrUpdate(
                subscriptionId,
                new UnloadedTombstone(now, incomingSeq),
                (_, prev) => incomingSeq > 0 && prev.Sequence > incomingSeq
                    ? prev
                    : new UnloadedTombstone(now, Math.Max(prev.Sequence, incomingSeq)));
            CleanupUnloadedSubscriptionsIfNeeded();
        }

        if (_activeConnections.TryRemove(subscriptionId, out var info))
        {
            if (info.IsFocused && !IsConnectionIdle(info))
            {
                UpdateLastSeen(info.UserId, info.DeviceType);
            }
            else
            {
                UpdateLastSeenIfNewer(info.UserId, info.DeviceType, info.LastInteractionUtc);
            }
            TouchSession(info.SessionId);
            OnUserPresenceChanged?.Invoke(info.UserId);
        }
    }

    /// <summary>
    /// Update focus state for a subscription connection.
    /// </summary>
    public void SetConnectionFocus(string userId, string? subscriptionId, bool isFocused, long? sequence = null)
    {
        if (TryApplyConnectionFocus(subscriptionId, isFocused, userIdFallback: userId, logPrefix: false, sequence: sequence))
        {
            OnUserPresenceChanged?.Invoke(userId);
        }
    }

    /// <summary>
    /// Update focus state for a subscription connection directly by subscription ID.
    /// Returns true when the focus transition was accepted and applied; false if missing, dead, or rejected as stale.
    /// </summary>
    public bool SetConnectionFocusBySubscription(string? subscriptionId, bool isFocused, long? sequence = null)
    {
        _logger?.LogDebug("[PresenceState] SetConnectionFocusBySubscription called for {SubscriptionId}, isFocused: {IsFocused}, seq: {Sequence}", subscriptionId, isFocused, sequence);
        return TryApplyConnectionFocus(subscriptionId, isFocused, userIdFallback: null, logPrefix: true, sequence: sequence);
    }

    private bool TryApplyConnectionFocus(string? subscriptionId, bool isFocused, string? userIdFallback, bool logPrefix, long? sequence)
    {
        if (IsSubscriptionUnloaded(subscriptionId)) return false;

        if (!string.IsNullOrEmpty(subscriptionId) && _activeConnections.TryGetValue(subscriptionId, out var info))
        {
            if (!IsTransportAlive(info))
            {
                RemoveConnection(subscriptionId);
                return false;
            }

            string targetUserId;
            string deviceType;
            bool shouldUpdateLastSeen = false;

            lock (info.SyncRoot)
            {
                if (IsStaleSequence(sequence, info.LastPresenceSequence))
                {
                    _logger?.LogDebug(
                        "[PresenceState] Ignoring stale focus({IsFocused}) for {SubscriptionId} (seq={Seq} < lastSeq={LastSeq})",
                        isFocused, subscriptionId, sequence, info.LastPresenceSequence);
                    return false;
                }

                AdvanceSequence(info, sequence);

                if (!isFocused && !info.IsFocused)
                {
                    return false;
                }

                if (logPrefix)
                {
                    _logger?.LogDebug("[PresenceState] Found connection info. UserId: {UserId}, DeviceType: {DeviceType}", info.UserId, info.DeviceType);
                }

                bool wasActivelyFocused = info.IsFocused && !IsConnectionIdle(info);
                info.IsFocused = isFocused;
                targetUserId = userIdFallback ?? info.UserId;
                deviceType = info.DeviceType;

                if (isFocused)
                {
                    info.LastInteractionUtc = DateTime.UtcNow;
                    if (info.ClientState == ConnectionPresenceState.Idle)
                    {
                        info.ClientState = ConnectionPresenceState.Active;
                    }
                    shouldUpdateLastSeen = true;
                }
                else if (wasActivelyFocused)
                {
                    info.LastInteractionUtc = DateTime.UtcNow;
                    shouldUpdateLastSeen = true;
                }
            }

            if (shouldUpdateLastSeen)
            {
                UpdateLastSeen(targetUserId, deviceType);
            }

            if (userIdFallback == null)
            {
                OnUserPresenceChanged?.Invoke(info.UserId);
            }
            return true;
        }

        if (logPrefix)
        {
            _logger?.LogDebug("[PresenceState] Connection info NOT FOUND for subscriptionId: {SubscriptionId}", subscriptionId);
        }

        return userIdFallback != null;
    }

    /// <summary>
    /// Explicitly sets the client presence state (Active, Idle, DoNotDisturb, Offline) for a connection.
    /// Used by Electron OS presence events, system tray status menu, and browser 5-minute idle timers.
    /// Returns true when the state update was accepted and applied; false if missing, dead, or rejected as stale.
    /// </summary>
    public bool SetConnectionPresenceState(string? subscriptionId, ConnectionPresenceState state, long? sequence = null)
    {
        if (string.IsNullOrEmpty(subscriptionId)) return false;
        if (state != ConnectionPresenceState.Offline && IsSubscriptionUnloaded(subscriptionId)) return false;

        if (_activeConnections.TryGetValue(subscriptionId, out var info))
        {
            if (!IsTransportAlive(info))
            {
                RemoveConnection(subscriptionId);
                return false;
            }

            string userId;
            string deviceType;
            string? sessionId;

            lock (info.SyncRoot)
            {
                if (IsStaleSequence(sequence, info.LastPresenceSequence))
                {
                    _logger?.LogDebug(
                        "[PresenceState] Ignoring stale state({State}) for {SubscriptionId} (seq={Seq} < lastSeq={LastSeq})",
                        state, subscriptionId, sequence, info.LastPresenceSequence);
                    return false;
                }

                AdvanceSequence(info, sequence);

                info.ClientState = state;
                if (state == ConnectionPresenceState.Active)
                {
                    info.LastInteractionUtc = DateTime.UtcNow;
                }
                userId = info.UserId;
                deviceType = info.DeviceType;
                sessionId = info.SessionId;
            }

            if (state == ConnectionPresenceState.Active)
            {
                UpdateLastSeen(userId, deviceType);
                TouchSession(sessionId);
            }
            OnUserPresenceChanged?.Invoke(userId);
            return true;
        }

        return false;
    }

    /// <summary>
    /// Sets the last interaction timestamp for a specific connection (used for deterministic testing of idle timeouts).
    /// </summary>
    public void SetConnectionLastInteraction(string? subscriptionId, DateTime timestampUtc)
    {
        if (string.IsNullOrEmpty(subscriptionId)) return;
        if (_activeConnections.TryGetValue(subscriptionId, out var info))
        {
            lock (info.SyncRoot)
            {
                info.LastInteractionUtc = timestampUtc;
            }
            _lastSeen[(info.UserId, info.DeviceType)] = timestampUtc;
        }
    }

    /// <summary>
    /// Periodically sweeps active connections, evicts any connection whose underlying circuit transport is dead,
    /// and transitions any connection that has exceeded the 5-minute inactivity threshold to Idle,
    /// firing OnUserPresenceChanged for affected users.
    /// </summary>
    public void SweepStaleConnections()
    {
        var now = DateTime.UtcNow;
        var affectedUsers = new HashSet<string>();

        foreach (var tombstone in _unloadedSubscriptions)
        {
            if ((now - tombstone.Value.UnloadedAtUtc).TotalMinutes >= UnloadedTombstoneRetentionMinutes)
            {
                _unloadedSubscriptions.TryRemove(tombstone.Key, out _);
            }
        }

        foreach (var kvp in _activeConnections)
        {
            var info = kvp.Value;
            if (!IsTransportAlive(info))
            {
                if (_activeConnections.TryRemove(kvp.Key, out var evicted))
                {
                    UpdateLastSeenIfNewer(evicted.UserId, evicted.DeviceType, evicted.LastInteractionUtc);
                    TouchSession(evicted.SessionId);
                    affectedUsers.Add(evicted.UserId);
                }
                continue;
            }

            // Voice participation counts as active presence (speaking/listening without keyboard/mouse/touch input)
            if (_voiceStateService?.IsUserInAnyVoiceChannel(info.UserId) == true)
            {
                continue;
            }

            lock (info.SyncRoot)
            {
                if (info.ClientState == ConnectionPresenceState.Active &&
                    (now - info.LastInteractionUtc).TotalMinutes >= RecentlySeenMinutes)
                {
                    info.ClientState = ConnectionPresenceState.Idle;
                    affectedUsers.Add(info.UserId);
                }
            }
        }

        foreach (var userId in affectedUsers)
        {
            OnUserPresenceChanged?.Invoke(userId);
        }
    }

    private bool IsConnectionEffectiveOnline(ConnectionInfo c)
    {
        if (!IsTransportAlive(c))
            return false;

        if (c.ClientState == ConnectionPresenceState.Offline)
            return false;

        if (string.Equals(c.DeviceType, Models.Core.DeviceType.Desktop, StringComparison.OrdinalIgnoreCase))
            return true;

        if (_voiceStateService?.IsUserInAnyVoiceChannel(c.UserId) == true)
            return c.IsFocused;

        return c.IsFocused && c.ClientState != ConnectionPresenceState.Idle;
    }

    private bool IsConnectionIdle(ConnectionInfo c)
    {
        if (c.ClientState == ConnectionPresenceState.Offline)
            return true;

        // Voice participation counts as active presence (speaking/listening without keyboard/mouse/touch input)
        if (_voiceStateService?.IsUserInAnyVoiceChannel(c.UserId) == true)
            return false;

        if (c.ClientState == ConnectionPresenceState.Idle)
            return true;

        return (DateTime.UtcNow - c.LastInteractionUtc).TotalMinutes >= RecentlySeenMinutes;
    }

    /// <summary>
    /// Gets the user ID associated with an active subscription connection, if any.
    /// </summary>
    public string? GetUserIdBySubscription(string? subscriptionId)
    {
        if (!string.IsNullOrEmpty(subscriptionId)
            && _activeConnections.TryGetValue(subscriptionId, out var info)
            && IsTransportAlive(info))
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
        if (IsSubscriptionUnloaded(subscriptionId)) return;

        if (!string.IsNullOrEmpty(subscriptionId) && _activeConnections.TryGetValue(subscriptionId, out var info))
        {
            if (!IsTransportAlive(info))
            {
                RemoveConnection(subscriptionId);
                return;
            }

            info.HasPushEnabled = hasPushEnabled;
            if (hasPushEnabled)
            {
                UpdateLastSeen(info.UserId, info.DeviceType);
            }
        }
    }


    /// <summary>
    /// Returns true if the user has at least one focused/active connection that is not idle.
    /// </summary>
    public IReadOnlyCollection<string> GetFocusedSessionIds(string userId)
    {
        return _activeConnections.Values
            .Where(c => c.UserId == userId &&
                        !string.IsNullOrEmpty(c.SessionId) &&
                        c.IsFocused &&
                        IsConnectionEffectiveOnline(c) &&
                        !IsConnectionIdle(c))
            .Select(c => c.SessionId!)
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }

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

    // --- Device-tier queries ---

    /// <summary>
    /// Returns true if the user has at least one focused/active connection on a Desktop device that is not idle.
    /// </summary>
    public bool IsUserFocusedOnDesktop(string userId)
    {
        return _activeConnections.Values.Any(c =>
            c.UserId == userId &&
            c.DeviceType.Equals(Models.Core.DeviceType.Desktop, StringComparison.OrdinalIgnoreCase) &&
            c.IsFocused &&
            IsConnectionEffectiveOnline(c) &&
            !IsConnectionIdle(c));
    }

    /// <summary>
    /// Returns true if the user has a desktop connection but is away/idle on desktop (e.g. inactive for >= 5 minutes or screen locked).
    /// </summary>
    public bool IsUserAwayFromDesktop(string userId)
    {
        if (!HasActiveDesktopConnection(userId)) return false;
        if (IsUserFocusedOnDesktop(userId)) return false;
        if (_voiceStateService?.IsUserInAnyVoiceChannel(userId) == true) return false;

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

    /// <summary>
    /// Checks if a matured 1-minute delayed mobile push should be suppressed because the user has refocused Spokes.
    /// </summary>
    public bool ShouldSuppressMobilePush(string userId)
    {
        return IsUserFocused(userId);
    }

    /// <summary>
    /// Returns true if the user is currently participating in any active voice channel.
    /// </summary>
    public bool IsUserInVoiceCall(string userId)
    {
        return _voiceStateService?.IsUserInAnyVoiceChannel(userId) == true;
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
        if (_voiceStateService?.IsUserInAnyVoiceChannel(userId) == true) return false;

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
            (c.IsCapacitorApp || c.DeviceType.Equals(Models.Core.DeviceType.Mobile, StringComparison.OrdinalIgnoreCase)) &&
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
    /// Resolves the unified presence display badge for a user following Discord-like device priority:
    /// 1. Active on Desktop -> OnlineDesktop (green circle takes precedence when working at desk)
    /// 2. Active on Mobile -> OnlineMobile (smartphone icon when desktop is away or offline)
    /// 3. Globally Away -> Away (amber circle when connected but idle everywhere)
    /// 4. Fallback Active -> OnlineDesktop (connected non-idle generic clients)
    /// 5. No Active Connections -> Offline (gray circle)
    /// </summary>
    public UserPresenceBadge GetUserPresenceBadge(string userId)
    {
        if (!HasActiveConnection(userId))
            return UserPresenceBadge.Offline;

        if (HasActiveDesktopConnection(userId) && !IsUserAwayFromDesktop(userId))
            return UserPresenceBadge.OnlineDesktop;

        if (IsOnlineOnCapacitorMobile(userId))
            return UserPresenceBadge.OnlineMobile;

        if (IsGloballyAway(userId))
            return UserPresenceBadge.Away;

        return UserPresenceBadge.OnlineDesktop;
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
    /// Pings the server that a device is actively being used by the user physically (idle detection).
    /// </summary>
    public void PingDeviceActive(string? subscriptionId)
    {
        if (string.IsNullOrEmpty(subscriptionId) || IsSubscriptionUnloaded(subscriptionId)) return;
        if (_activeConnections.TryGetValue(subscriptionId, out var info))
        {
            if (!IsTransportAlive(info))
            {
                RemoveConnection(subscriptionId);
                return;
            }

            string userId;
            string deviceType;
            string? sessionId;
            lock (info.SyncRoot)
            {
                info.LastInteractionUtc = DateTime.UtcNow;
                if (info.ClientState == ConnectionPresenceState.Idle)
                {
                    info.ClientState = ConnectionPresenceState.Active;
                }
                userId = info.UserId;
                deviceType = info.DeviceType;
                sessionId = info.SessionId;
            }
            UpdateLastSeen(userId, deviceType);
            TouchSession(sessionId);
            OnUserPresenceChanged?.Invoke(userId);
        }
    }

    /// <summary>
    /// Pings the server that the user is actively interacting with the app (e.g. typing, navigating).
    /// Updates the last seen timestamp on focused connections (or all connections if none are marked focused).
    /// </summary>
    public void PingUserActive(string userId)
    {
        var userConnections = _activeConnections.Values.Where(c => c.UserId == userId && IsTransportAlive(c)).ToList();
        var targetConnections = userConnections.Where(c => c.IsFocused).ToList();
        if (targetConnections.Count == 0)
        {
            targetConnections = userConnections;
        }

        foreach (var info in targetConnections)
        {
            string deviceType;
            string? sessionId;
            lock (info.SyncRoot)
            {
                info.LastInteractionUtc = DateTime.UtcNow;
                if (info.ClientState == ConnectionPresenceState.Idle)
                {
                    info.ClientState = ConnectionPresenceState.Active;
                }
                deviceType = info.DeviceType;
                sessionId = info.SessionId;
            }
            UpdateLastSeen(userId, deviceType);
            TouchSession(sessionId);
        }

        if (userConnections.Count == 0)
        {
            // Fallback if they have no tracked connections but are active
            UpdateLastSeen(userId, "Unknown");
        }

        OnUserPresenceChanged?.Invoke(userId);
    }

    private void UpdateLastSeen(string userId, string deviceType)
    {
        _lastSeen[(userId, deviceType)] = DateTime.UtcNow;
        CleanupLastSeenIfNeeded();
    }

    private void UpdateLastSeenIfNewer(string userId, string deviceType, DateTime timestampUtc)
    {
        if (timestampUtc == default) return;

        var key = (userId, deviceType);
        _lastSeen.AddOrUpdate(key, timestampUtc, (_, existing) => timestampUtc > existing ? timestampUtc : existing);
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
            }
        }
    }

    private void CleanupUnloadedSubscriptionsIfNeeded()
    {
        if (_unloadedSubscriptions.Count > 5000)
        {
            var now = DateTime.UtcNow;
            foreach (var kvp in _unloadedSubscriptions)
            {
                if ((now - kvp.Value.UnloadedAtUtc).TotalMinutes >= UnloadedTombstoneRetentionMinutes)
                {
                    _unloadedSubscriptions.TryRemove(kvp.Key, out _);
                }
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
            .Where(c => !string.IsNullOrEmpty(c.SessionId) && IsTransportAlive(c))
            .Select(c => c.SessionId!)
            .Distinct()
            .ToList();

    /// <summary>
    /// Returns a snapshot of all active transient connections across the application.
    /// </summary>
    public IReadOnlyCollection<ConnectionInfo> GetActiveConnections()
        => _activeConnections.Values.Where(IsTransportAlive).ToList();

    /// <summary>
    /// Returns all active transient connections for a specific user.
    /// </summary>
    public IEnumerable<ConnectionInfo> GetUserConnections(string userId)
        => _activeConnections.Values.Where(c => c.UserId == userId && IsTransportAlive(c)).ToList();

    public class ConnectionInfo
    {
        internal object SyncRoot { get; } = new();
        public string UserId { get; set; } = string.Empty;
        public string? SessionId { get; set; }
        public string DeviceType { get; set; } = Models.Core.DeviceType.Desktop;
        public bool IsFocused { get; set; }
        public bool HasPushEnabled { get; set; }
        public bool IsCapacitorApp { get; set; }
        public ConnectionPresenceState ClientState { get; set; } = ConnectionPresenceState.Active;
        public DateTime LastInteractionUtc { get; set; } = DateTime.UtcNow;
        public long LastPresenceSequence { get; set; }
        public Func<bool>? IsTransportAlive { get; set; }
    }
}

public enum UserPresenceBadge
{
    Offline,
    OnlineDesktop,
    OnlineMobile,
    Away
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
    public bool PreserveExistingActivity { get; set; }
    public long? PresenceSequence { get; set; }
    public Func<bool>? IsTransportAlive { get; set; }
}
