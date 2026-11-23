using System;
using System.Collections.Generic;
using System.Linq;

namespace Spokes_Server.Core.Services.Communication.Voice;

/// <summary>
/// Singleton service managing in-process LiveKit voice channel presence, participant sessions,
/// mute/media state, canonical active speakers, audio telemetry, and physical room DSP ownership.
/// </summary>
public class VoiceStateService
{
    private readonly TimeProvider _timeProvider;

    public VoiceStateService() : this(TimeProvider.System)
    {
    }

    internal VoiceStateService(TimeProvider timeProvider)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    private static readonly TimeSpan ClosedVoiceSessionRetention = TimeSpan.FromMinutes(10);
    private const int MaxClosedVoiceSessions = 512;

    public readonly record struct VoiceParticipantMediaState(
        bool IsCameraOn,
        bool IsScreenSharing,
        bool HasScreenAudio,
        int VideoHeight,
        int VideoFps);

    private sealed class VoiceParticipantState
    {
        public string? SessionId;
        public string? ParticipantSid;
        public string? JoinChimePlayedSessionId;
        public string? DeviceId;
        public string DeviceName = string.Empty;
        public string FilterType = "webrtc";
        public double CaptureLatencyMs;
        public string Platform = "web";
        public double ServerDelayMs;
        public bool HasTelemetry;
        public bool IsMuted;
        public bool IsCameraOn;
        public bool IsScreenSharing;
        public bool HasScreenAudio;
        public int VideoHeight;
        public int VideoFps;
        public DateTime LastActivityUtc;
    }

    private readonly record struct ClosedVoiceSessionEntry(
        DateTime ClosedAtUtc,
        bool ExplicitlyClosed,
        string? LastClosedParticipantSid);

    private readonly Dictionary<string, HashSet<string>> _voiceParticipants = new();
    private readonly Dictionary<(string ChannelId, string UserId), VoiceParticipantState> _voiceStates = new();
    private readonly Dictionary<string, ClosedVoiceSessionEntry> _closedVoiceSessionIds = new(StringComparer.Ordinal);
    private readonly Dictionary<string, HashSet<string>> _activeSpeakersList = new(StringComparer.Ordinal);
    private readonly Dictionary<string, HashSet<string>> _physicalRoomDspManagedUsers = new(StringComparer.Ordinal);
    private readonly Dictionary<string, HashSet<string>> _physicalRoomDspSpeakers = new(StringComparer.Ordinal);
    private readonly object _voiceLock = new();

    public event Action<string, string>? VoiceMemberJoined;
    public event Action<string, string>? VoiceMemberLeft;
    public event Action<string, string[]>? VoiceActiveSpeakersChanged;
    public event Action<string, string, bool>? VoiceUserMutedChanged;
    public event Action<string, string, VoiceParticipantMediaState>? VoiceUserMediaStateChanged;
    public event Action? PhysicalRoomsChanged;

    public void NotifyPhysicalRoomsChanged() => PhysicalRoomsChanged?.Invoke();

    private VoiceParticipantState GetOrCreateVoiceStateLocked(string channelId, string userId)
    {
        if (!_voiceStates.TryGetValue((channelId, userId), out var state))
        {
            state = new VoiceParticipantState();
            _voiceStates[(channelId, userId)] = state;
        }
        return state;
    }

    private bool ValidateSessionUnlockedLocked(string channelId, string userId, string? normalizedSessionId, DateTime nowUtc)
    {
        PruneClosedVoiceSessionsLocked(nowUtc);
        if (normalizedSessionId == null)
        {
            return true;
        }

        if (_closedVoiceSessionIds.ContainsKey(normalizedSessionId))
        {
            return false;
        }

        if (_voiceStates.TryGetValue((channelId, userId), out var state) &&
            !string.IsNullOrWhiteSpace(state.SessionId) &&
            !string.Equals(state.SessionId, normalizedSessionId, StringComparison.Ordinal))
        {
            return false;
        }

        return true;
    }

    public bool IsVoiceSessionClosed(string? voiceSessionId)
    {
        if (string.IsNullOrWhiteSpace(voiceSessionId))
        {
            return false;
        }

        string trimmed = voiceSessionId.Trim();
        DateTime nowUtc = _timeProvider.GetUtcNow().UtcDateTime;
        lock (_voiceLock)
        {
            PruneClosedVoiceSessionsLocked(nowUtc);
            return _closedVoiceSessionIds.ContainsKey(trimmed);
        }
    }

    public DateTime? GetVoiceSessionClosedAtUtc(string? voiceSessionId)
    {
        if (string.IsNullOrWhiteSpace(voiceSessionId))
        {
            return null;
        }

        string trimmed = voiceSessionId.Trim();
        DateTime nowUtc = _timeProvider.GetUtcNow().UtcDateTime;
        lock (_voiceLock)
        {
            PruneClosedVoiceSessionsLocked(nowUtc);
            return _closedVoiceSessionIds.TryGetValue(trimmed, out var entry) ? entry.ClosedAtUtc : null;
        }
    }

    public bool IsVoiceSessionExplicitlyClosed(string? voiceSessionId)
    {
        if (string.IsNullOrWhiteSpace(voiceSessionId))
        {
            return false;
        }

        string trimmed = voiceSessionId.Trim();
        DateTime nowUtc = _timeProvider.GetUtcNow().UtcDateTime;
        lock (_voiceLock)
        {
            PruneClosedVoiceSessionsLocked(nowUtc);
            return _closedVoiceSessionIds.TryGetValue(trimmed, out var entry) && entry.ExplicitlyClosed;
        }
    }

    /// <summary>
    /// Returns true if the given session ID was closed solely by an SFU <c>participant_left</c> webhook
    /// (not an explicit user/server disconnect) and the incoming LiveKit participant SID is a newly
    /// negotiated PeerConnection (<c>incomingParticipantSid != LastClosedParticipantSid</c>).
    /// </summary>
    public bool CanReopenWebhookClosedSession(string? voiceSessionId, string? incomingParticipantSid)
    {
        if (string.IsNullOrWhiteSpace(voiceSessionId) || string.IsNullOrWhiteSpace(incomingParticipantSid))
        {
            return false;
        }

        string trimmedSession = voiceSessionId.Trim();
        string trimmedSid = incomingParticipantSid.Trim();
        DateTime nowUtc = _timeProvider.GetUtcNow().UtcDateTime;
        lock (_voiceLock)
        {
            PruneClosedVoiceSessionsLocked(nowUtc);
            if (!_closedVoiceSessionIds.TryGetValue(trimmedSession, out var entry))
            {
                return true;
            }

            if (entry.ExplicitlyClosed)
            {
                return false;
            }

            return !string.Equals(entry.LastClosedParticipantSid, trimmedSid, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// Un-tombstones and re-activates a voice session ID when an authoritative live client circuit
    /// completes a LiveKit full reconnect using the same JWT/session ID after a transient transport drop.
    /// </summary>
    public void ReopenVoiceSession(string channelId, string userId, string? voiceSessionId, string? participantSid = null)
    {
        if (string.IsNullOrWhiteSpace(channelId) || string.IsNullOrWhiteSpace(userId) || string.IsNullOrWhiteSpace(voiceSessionId))
        {
            return;
        }

        string normalizedSessionId = voiceSessionId.Trim();
        string? normalizedSid = string.IsNullOrWhiteSpace(participantSid) ? null : participantSid.Trim();
        DateTime nowUtc = _timeProvider.GetUtcNow().UtcDateTime;
        lock (_voiceLock)
        {
            PruneClosedVoiceSessionsLocked(nowUtc);
            _closedVoiceSessionIds.Remove(normalizedSessionId);
            var state = GetOrCreateVoiceStateLocked(channelId, userId);
            if (!string.IsNullOrWhiteSpace(state.SessionId) &&
                !string.Equals(state.SessionId, normalizedSessionId, StringComparison.Ordinal))
            {
                RecordClosedVoiceSessionLocked(state.SessionId, nowUtc);
                state.ParticipantSid = null;
            }
            state.SessionId = normalizedSessionId;
            if (normalizedSid != null)
            {
                state.ParticipantSid = normalizedSid;
            }
            state.LastActivityUtc = nowUtc;
        }
    }

    /// <summary>
    /// Atomically claims the one-shot join chime for the participant's current voice session.
    /// Returns true at most once per voice session (or once per membership life when no session ID is present).
    /// </summary>
    public bool TryClaimJoinChime(string channelId, string userId, string? voiceSessionId)
    {
        if (string.IsNullOrWhiteSpace(channelId) || string.IsNullOrWhiteSpace(userId))
        {
            return false;
        }

        string? normalizedSessionId = string.IsNullOrWhiteSpace(voiceSessionId) ? null : voiceSessionId.Trim();
        DateTime nowUtc = _timeProvider.GetUtcNow().UtcDateTime;
        lock (_voiceLock)
        {
            PruneClosedVoiceSessionsLocked(nowUtc);
            if (normalizedSessionId != null && _closedVoiceSessionIds.ContainsKey(normalizedSessionId))
            {
                return false;
            }

            if (!_voiceParticipants.TryGetValue(channelId, out var participants) || !participants.Contains(userId))
            {
                return false;
            }

            var state = GetOrCreateVoiceStateLocked(channelId, userId);
            string effectiveSessionKey = normalizedSessionId
                ?? (!string.IsNullOrWhiteSpace(state.SessionId) ? state.SessionId! : "__joined__");

            if (string.Equals(state.JoinChimePlayedSessionId, effectiveSessionKey, StringComparison.Ordinal))
            {
                return false;
            }

            state.JoinChimePlayedSessionId = effectiveSessionKey;
            return true;
        }
    }

    private void RecordClosedVoiceSessionLocked(
        string? voiceSessionId,
        DateTime nowUtc,
        bool explicitlyClosed = true,
        string? participantSid = null)
    {
        if (string.IsNullOrWhiteSpace(voiceSessionId))
        {
            return;
        }

        PruneClosedVoiceSessionsLocked(nowUtc);
        string key = voiceSessionId.Trim();
        string? cleanSid = string.IsNullOrWhiteSpace(participantSid) ? null : participantSid.Trim();
        if (_closedVoiceSessionIds.TryGetValue(key, out var existing))
        {
            _closedVoiceSessionIds[key] = new ClosedVoiceSessionEntry(
                nowUtc,
                existing.ExplicitlyClosed || explicitlyClosed,
                cleanSid ?? existing.LastClosedParticipantSid);
        }
        else
        {
            _closedVoiceSessionIds[key] = new ClosedVoiceSessionEntry(nowUtc, explicitlyClosed, cleanSid);
        }
    }

    private void PruneClosedVoiceSessionsLocked(DateTime nowUtc)
    {
        if (_closedVoiceSessionIds.Count == 0)
        {
            return;
        }

        DateTime cutoffUtc = nowUtc - ClosedVoiceSessionRetention;
        foreach (var key in _closedVoiceSessionIds.Where(kvp => kvp.Value.ClosedAtUtc < cutoffUtc).Select(kvp => kvp.Key).ToList())
        {
            _closedVoiceSessionIds.Remove(key);
        }

        if (_closedVoiceSessionIds.Count > MaxClosedVoiceSessions)
        {
            foreach (var key in _closedVoiceSessionIds
                .OrderBy(kvp => kvp.Value.ClosedAtUtc)
                .Take(_closedVoiceSessionIds.Count - MaxClosedVoiceSessions)
                .Select(kvp => kvp.Key)
                .ToList())
            {
                _closedVoiceSessionIds.Remove(key);
            }
        }
    }

    private static bool IsSpecificPlatformTag(string? platform) =>
        !string.IsNullOrWhiteSpace(platform) &&
        !string.Equals(platform.Trim(), "web", StringComparison.OrdinalIgnoreCase);

    private static string ResolveMergedPlatformTag(string? incomingPlatform, string? existingPlatform, bool isSameSession)
    {
        string cleanIncoming = string.IsNullOrWhiteSpace(incomingPlatform) ? "web" : incomingPlatform.Trim();
        return isSameSession && !IsSpecificPlatformTag(cleanIncoming) && IsSpecificPlatformTag(existingPlatform)
            ? existingPlatform!.Trim()
            : cleanIncoming;
    }

    public bool NotifyVoiceMemberJoined(string channelId, string userId, string? deviceId = null, string? deviceName = null, string? voiceSessionId = null, string? participantSid = null)
    {
        bool added;
        DateTime nowUtc = _timeProvider.GetUtcNow().UtcDateTime;
        string? normalizedSessionId = string.IsNullOrWhiteSpace(voiceSessionId) ? null : voiceSessionId.Trim();
        string? normalizedSid = string.IsNullOrWhiteSpace(participantSid) ? null : participantSid.Trim();
        lock (_voiceLock)
        {
            PruneClosedVoiceSessionsLocked(nowUtc);
            if (normalizedSessionId != null && _closedVoiceSessionIds.ContainsKey(normalizedSessionId))
            {
                return false;
            }

            if (!_voiceParticipants.TryGetValue(channelId, out var set))
            {
                _voiceParticipants[channelId] = set = new HashSet<string>();
            }
            added = set.Add(userId);
            var state = GetOrCreateVoiceStateLocked(channelId, userId);
            if (!string.IsNullOrWhiteSpace(deviceId))
            {
                state.DeviceId = deviceId;
                state.DeviceName = deviceName ?? string.Empty;
            }
            if (normalizedSessionId != null)
            {
                if (!string.IsNullOrWhiteSpace(state.SessionId) &&
                    !string.Equals(state.SessionId, normalizedSessionId, StringComparison.Ordinal))
                {
                    RecordClosedVoiceSessionLocked(state.SessionId, nowUtc);
                    state.JoinChimePlayedSessionId = null;
                    state.ParticipantSid = null;
                }
                state.SessionId = normalizedSessionId;
            }
            if (normalizedSid != null)
            {
                state.ParticipantSid = normalizedSid;
            }
            state.LastActivityUtc = nowUtc;
        }
        if (added)
        {
            VoiceMemberJoined?.Invoke(channelId, userId);
        }
        return added;
    }

    public void SetVoiceParticipantDevice(string channelId, string userId, string deviceId, string? deviceName = null)
    {
        if (string.IsNullOrWhiteSpace(channelId) || string.IsNullOrWhiteSpace(userId) || string.IsNullOrWhiteSpace(deviceId))
        {
            return;
        }
        DateTime nowUtc = _timeProvider.GetUtcNow().UtcDateTime;
        lock (_voiceLock)
        {
            var state = GetOrCreateVoiceStateLocked(channelId, userId);
            state.DeviceId = deviceId;
            state.DeviceName = deviceName ?? string.Empty;
            state.LastActivityUtc = nowUtc;
        }
        PhysicalRoomsChanged?.Invoke();
    }

    public string? GetVoiceParticipantSessionId(string channelId, string userId)
    {
        if (string.IsNullOrWhiteSpace(channelId) || string.IsNullOrWhiteSpace(userId))
        {
            return null;
        }

        lock (_voiceLock)
        {
            return _voiceStates.TryGetValue((channelId, userId), out var state) ? state.SessionId : null;
        }
    }

    public DateTime? GetVoiceParticipantLastActivityUtc(string channelId, string userId)
    {
        if (string.IsNullOrWhiteSpace(channelId) || string.IsNullOrWhiteSpace(userId))
        {
            return null;
        }

        lock (_voiceLock)
        {
            return _voiceStates.TryGetValue((channelId, userId), out var state) && state.LastActivityUtc != default
                ? state.LastActivityUtc
                : null;
        }
    }

    public void TouchVoiceParticipantActivity(string channelId, string userId, string? voiceSessionId = null, string? participantSid = null)
    {
        if (string.IsNullOrWhiteSpace(channelId) || string.IsNullOrWhiteSpace(userId))
        {
            return;
        }

        string? normalizedSessionId = string.IsNullOrWhiteSpace(voiceSessionId) ? null : voiceSessionId.Trim();
        string? normalizedSid = string.IsNullOrWhiteSpace(participantSid) ? null : participantSid.Trim();
        DateTime nowUtc = _timeProvider.GetUtcNow().UtcDateTime;
        lock (_voiceLock)
        {
            if (!ValidateSessionUnlockedLocked(channelId, userId, normalizedSessionId, nowUtc))
            {
                return;
            }

            var state = GetOrCreateVoiceStateLocked(channelId, userId);
            state.LastActivityUtc = nowUtc;
            if (normalizedSessionId != null)
            {
                state.SessionId = normalizedSessionId;
            }
            if (normalizedSid != null)
            {
                state.ParticipantSid = normalizedSid;
            }
        }
    }

    /// <summary>
    /// Records the active LiveKit <c>ParticipantSid</c> for an existing participant session without
    /// modifying <c>LastActivityUtc</c> (preserving watchdog activity-grace semantics).
    /// </summary>
    public void SetVoiceParticipantSid(string channelId, string userId, string? voiceSessionId, string? participantSid)
    {
        if (string.IsNullOrWhiteSpace(channelId) || string.IsNullOrWhiteSpace(userId) || string.IsNullOrWhiteSpace(participantSid))
        {
            return;
        }

        string? normalizedSessionId = string.IsNullOrWhiteSpace(voiceSessionId) ? null : voiceSessionId.Trim();
        string normalizedSid = participantSid.Trim();
        DateTime nowUtc = _timeProvider.GetUtcNow().UtcDateTime;
        lock (_voiceLock)
        {
            if (!ValidateSessionUnlockedLocked(channelId, userId, normalizedSessionId, nowUtc))
            {
                return;
            }

            if (_voiceStates.TryGetValue((channelId, userId), out var state))
            {
                state.ParticipantSid = normalizedSid;
            }
        }
    }

    public (string DeviceId, string DeviceName)? GetVoiceParticipantDevice(string channelId, string userId)
    {
        lock (_voiceLock)
        {
            return _voiceStates.TryGetValue((channelId, userId), out var state) && !string.IsNullOrWhiteSpace(state.DeviceId)
                ? (state.DeviceId, state.DeviceName)
                : null;
        }
    }

    /// <summary>
    /// Returns true if the specified user device is currently connected to any active voice channel.
    /// </summary>
    public bool IsVoiceDeviceConnected(string? userId, string? deviceId)
    {
        if (string.IsNullOrWhiteSpace(userId) || string.IsNullOrWhiteSpace(deviceId))
        {
            return false;
        }

        lock (_voiceLock)
        {
            foreach (var kvp in _voiceStates)
            {
                if (string.Equals(kvp.Key.UserId, userId, StringComparison.Ordinal) &&
                    string.Equals(kvp.Value.DeviceId, deviceId, StringComparison.Ordinal) &&
                    _voiceParticipants.TryGetValue(kvp.Key.ChannelId, out var participants) &&
                    participants.Contains(userId))
                {
                    return true;
                }
            }

            return false;
        }
    }

    public void SetVoiceParticipantAudioTelemetry(
        string channelId,
        string userId,
        string filterType,
        double estimatedCaptureLatencyMs,
        string? platform = null,
        double serverToClientDelayMs = 0.0)
    {
        if (string.IsNullOrWhiteSpace(channelId) || string.IsNullOrWhiteSpace(userId))
        {
            return;
        }

        string cleanFilter = string.IsNullOrWhiteSpace(filterType) ? "webrtc" : filterType.Trim();
        double clampedLatency = Math.Clamp(estimatedCaptureLatencyMs, 0.0, 500.0);
        string cleanPlatform = string.IsNullOrWhiteSpace(platform) ? "web" : platform.Trim();
        double clampedServerDelay = Math.Clamp(serverToClientDelayMs, 0.0, 1000.0);

        bool changed = true;
        lock (_voiceLock)
        {
            var state = GetOrCreateVoiceStateLocked(channelId, userId);
            if (state.HasTelemetry)
            {
                cleanPlatform = ResolveMergedPlatformTag(cleanPlatform, state.Platform, isSameSession: true);
                if (clampedServerDelay <= 0.0)
                {
                    clampedServerDelay = state.ServerDelayMs;
                }
                changed = !string.Equals(state.FilterType, cleanFilter, StringComparison.Ordinal) ||
                          !string.Equals(state.Platform, cleanPlatform, StringComparison.Ordinal) ||
                          Math.Abs(state.CaptureLatencyMs - clampedLatency) >= 25.0 ||
                          Math.Abs(state.ServerDelayMs - clampedServerDelay) >= 25.0;
            }
            state.FilterType = cleanFilter;
            state.CaptureLatencyMs = clampedLatency;
            state.Platform = cleanPlatform;
            state.ServerDelayMs = clampedServerDelay;
            state.HasTelemetry = true;
        }
        if (changed)
        {
            PhysicalRoomsChanged?.Invoke();
        }
    }

    public (string FilterType, double EstimatedCaptureLatencyMs, string Platform, double ServerToClientDelayMs)? GetVoiceParticipantAudioTelemetry(string channelId, string userId)
    {
        lock (_voiceLock)
        {
            return _voiceStates.TryGetValue((channelId, userId), out var state) && state.HasTelemetry
                ? (state.FilterType, state.CaptureLatencyMs, state.Platform, state.ServerDelayMs)
                : null;
        }
    }

    /// <summary>
    /// Atomically populates a participant's enrolled physical device, client DSP capture telemetry, and initial mute state
    /// under a single lock acquisition before <c>JoinVoiceAsync</c> / WebRTC connect so server room pipelines see complete metadata immediately.
    /// </summary>
    public void SetVoiceParticipantSessionContext(
        string channelId,
        string userId,
        string? deviceId,
        string? deviceName,
        string filterType,
        double estimatedCaptureLatencyMs,
        string? platform,
        bool isMuted,
        string? voiceSessionId = null)
    {
        if (string.IsNullOrWhiteSpace(channelId) || string.IsNullOrWhiteSpace(userId))
        {
            return;
        }

        string cleanFilter = string.IsNullOrWhiteSpace(filterType) ? "webrtc" : filterType.Trim();
        double clampedLatency = Math.Clamp(estimatedCaptureLatencyMs, 0.0, 500.0);
        string cleanPlatform = string.IsNullOrWhiteSpace(platform) ? "web" : platform.Trim();
        string? normalizedSessionId = string.IsNullOrWhiteSpace(voiceSessionId) ? null : voiceSessionId.Trim();
        bool muteChanged = false;
        bool physicalRoomMetadataChanged = false;
        DateTime nowUtc = _timeProvider.GetUtcNow().UtcDateTime;

        lock (_voiceLock)
        {
            PruneClosedVoiceSessionsLocked(nowUtc);
            if (normalizedSessionId != null && _closedVoiceSessionIds.ContainsKey(normalizedSessionId))
            {
                return;
            }

            var state = GetOrCreateVoiceStateLocked(channelId, userId);
            bool isSameSession = true;
            if (normalizedSessionId != null)
            {
                if (!string.IsNullOrWhiteSpace(state.SessionId))
                {
                    isSameSession = string.Equals(state.SessionId, normalizedSessionId, StringComparison.Ordinal);
                    if (!isSameSession)
                    {
                        RecordClosedVoiceSessionLocked(state.SessionId, nowUtc);
                        state.JoinChimePlayedSessionId = null;
                        state.ParticipantSid = null;
                        physicalRoomMetadataChanged = true;
                    }
                }
                else
                {
                    physicalRoomMetadataChanged = true;
                }
                state.SessionId = normalizedSessionId;
            }
            state.LastActivityUtc = nowUtc;

            if (!string.IsNullOrWhiteSpace(deviceId))
            {
                string cleanDeviceName = deviceName ?? string.Empty;
                if (!string.Equals(state.DeviceId, deviceId, StringComparison.Ordinal) ||
                    !string.Equals(state.DeviceName, cleanDeviceName, StringComparison.Ordinal))
                {
                    physicalRoomMetadataChanged = true;
                }
                state.DeviceId = deviceId;
                state.DeviceName = cleanDeviceName;
            }

            if (state.HasTelemetry)
            {
                cleanPlatform = ResolveMergedPlatformTag(cleanPlatform, state.Platform, isSameSession);
                if (!string.Equals(state.FilterType, cleanFilter, StringComparison.Ordinal) ||
                    !string.Equals(state.Platform, cleanPlatform, StringComparison.Ordinal) ||
                    Math.Abs(state.CaptureLatencyMs - clampedLatency) >= 2.0)
                {
                    physicalRoomMetadataChanged = true;
                }
            }
            else
            {
                physicalRoomMetadataChanged = true;
            }
            state.FilterType = cleanFilter;
            state.CaptureLatencyMs = clampedLatency;
            state.Platform = cleanPlatform;
            state.HasTelemetry = true;

            muteChanged = state.IsMuted != isMuted;
            state.IsMuted = isMuted;
        }

        if (muteChanged)
        {
            VoiceUserMutedChanged?.Invoke(channelId, userId, isMuted);
        }

        if (physicalRoomMetadataChanged)
        {
            PhysicalRoomsChanged?.Invoke();
        }
    }

    public bool NotifyVoiceMemberLeft(string channelId, string userId) =>
        TryNotifyVoiceMemberLeft(channelId, userId, expectedSessionId: null, markSessionClosed: true);

    public bool TryNotifyVoiceMemberLeft(
        string channelId,
        string userId,
        string? expectedSessionId,
        bool markSessionClosed = true,
        bool explicitlyClosed = true,
        string? closedParticipantSid = null)
    {
        bool wasRemoved = false;
        bool wasMuted = false;
        string[]? updatedSpeakers = null;
        DateTime nowUtc = _timeProvider.GetUtcNow().UtcDateTime;
        string? normalizedExpectedSessionId = string.IsNullOrWhiteSpace(expectedSessionId) ? null : expectedSessionId.Trim();
        string? normalizedClosedSid = string.IsNullOrWhiteSpace(closedParticipantSid) ? null : closedParticipantSid.Trim();
        lock (_voiceLock)
        {
            _voiceStates.TryGetValue((channelId, userId), out var state);
            string? activeSessionId = state?.SessionId;
            if (normalizedExpectedSessionId != null &&
                !string.IsNullOrWhiteSpace(activeSessionId) &&
                !string.Equals(activeSessionId, normalizedExpectedSessionId, StringComparison.Ordinal))
            {
                RecordClosedVoiceSessionLocked(normalizedExpectedSessionId, nowUtc, explicitlyClosed, closedParticipantSid);
                return false;
            }

            // If this is an involuntary SFU participant_left webhook for an older ParticipantSid
            // after a newer ParticipantSid (e.g. LiveKit .full reconnect) has already joined on the
            // same VoiceSessionId, ignore the stale departure completely so live presence stays intact.
            if (!explicitlyClosed &&
                normalizedClosedSid != null &&
                !string.IsNullOrWhiteSpace(state?.ParticipantSid) &&
                !string.Equals(state.ParticipantSid, normalizedClosedSid, StringComparison.Ordinal))
            {
                return false;
            }

            if (markSessionClosed)
            {
                RecordClosedVoiceSessionLocked(activeSessionId, nowUtc, explicitlyClosed, closedParticipantSid);
                RecordClosedVoiceSessionLocked(normalizedExpectedSessionId, nowUtc, explicitlyClosed, closedParticipantSid);
            }

            wasMuted = state?.IsMuted ?? false;
            var beforeSpeakers = BuildEffectiveActiveSpeakersLocked(channelId);
            _voiceStates.Remove((channelId, userId));
            if (_voiceParticipants.TryGetValue(channelId, out var participants))
            {
                wasRemoved = participants.Remove(userId);
                if (participants.Count == 0) _voiceParticipants.Remove(channelId);
            }
            if (_activeSpeakersList.TryGetValue(channelId, out var existingSpeakers))
            {
                existingSpeakers.Remove(userId);
                if (existingSpeakers.Count == 0) _activeSpeakersList.Remove(channelId);
            }
            if (_physicalRoomDspManagedUsers.TryGetValue(channelId, out var managedUsers))
            {
                managedUsers.Remove(userId);
                if (managedUsers.Count == 0) _physicalRoomDspManagedUsers.Remove(channelId);
            }
            if (_physicalRoomDspSpeakers.TryGetValue(channelId, out var dspSpeakers))
            {
                dspSpeakers.Remove(userId);
                if (dspSpeakers.Count == 0) _physicalRoomDspSpeakers.Remove(channelId);
            }
            var afterSpeakers = BuildEffectiveActiveSpeakersLocked(channelId);
            if (!SpeakerSetsEqual(beforeSpeakers, afterSpeakers))
            {
                updatedSpeakers = afterSpeakers;
            }
        }
        if (updatedSpeakers != null)
        {
            VoiceActiveSpeakersChanged?.Invoke(channelId, updatedSpeakers);
        }
        if (wasMuted)
        {
            VoiceUserMutedChanged?.Invoke(channelId, userId, false);
        }
        VoiceMemberLeft?.Invoke(channelId, userId);
        if (wasRemoved)
        {
            PhysicalRoomsChanged?.Invoke();
        }
        return wasRemoved;
    }

    private static bool SpeakerSetsEqual(string[] a, string[] b)
    {
        if (a.Length != b.Length) return false;
        if (a.Length == 0) return true;
        return a.ToHashSet(StringComparer.Ordinal).SetEquals(b);
    }

    /// <summary>
    /// Computes the single canonical speaking set for a channel:
    /// - Participants managed by an active 2+ Physical Room DSP worker use post-isolation DSP state exclusively
    ///   (ignoring raw pre-cancellation client self-reports so desk bleed never flashes a quiet roommate).
    /// - All other participants use their own local microphone VAD self-report exclusively.
    /// - Muted participants are always excluded.
    /// </summary>
    private string[] BuildEffectiveActiveSpeakersLocked(string channelId)
    {
        _physicalRoomDspManagedUsers.TryGetValue(channelId, out var dspManaged);
        HashSet<string>? combined = null;

        if (_activeSpeakersList.TryGetValue(channelId, out var clientSpeakers) && clientSpeakers.Count > 0)
        {
            foreach (var uid in clientSpeakers)
            {
                if (dspManaged != null && dspManaged.Contains(uid)) continue;
                if (_voiceStates.TryGetValue((channelId, uid), out var st) && st.IsMuted) continue;
                combined ??= new HashSet<string>(StringComparer.Ordinal);
                combined.Add(uid);
            }
        }

        if (_physicalRoomDspSpeakers.TryGetValue(channelId, out var dspSpeakers) && dspSpeakers.Count > 0)
        {
            foreach (var uid in dspSpeakers)
            {
                if (_voiceStates.TryGetValue((channelId, uid), out var st) && st.IsMuted) continue;
                combined ??= new HashSet<string>(StringComparer.Ordinal);
                combined.Add(uid);
            }
        }

        return combined == null || combined.Count == 0 ? [] : combined.ToArray();
    }

    public bool IsPhysicalRoomDspManaged(string channelId, string userId)
    {
        if (string.IsNullOrWhiteSpace(channelId) || string.IsNullOrWhiteSpace(userId))
        {
            return false;
        }

        lock (_voiceLock)
        {
            return _physicalRoomDspManagedUsers.TryGetValue(channelId, out var set) && set.Contains(userId);
        }
    }

    /// <summary>
    /// Claims or releases authoritative speaking ownership for a participant being processed
    /// by an active 2+ Physical Room server DSP worker.
    /// </summary>
    public void SetPhysicalRoomDspManaged(string channelId, string userId, bool isManaged)
    {
        if (string.IsNullOrWhiteSpace(channelId) || string.IsNullOrWhiteSpace(userId))
        {
            return;
        }

        string[]? updatedSpeakers = null;
        lock (_voiceLock)
        {
            var before = BuildEffectiveActiveSpeakersLocked(channelId);
            if (isManaged)
            {
                if (!_physicalRoomDspManagedUsers.TryGetValue(channelId, out var set))
                {
                    set = new HashSet<string>(StringComparer.Ordinal);
                    _physicalRoomDspManagedUsers[channelId] = set;
                }
                set.Add(userId);
            }
            else
            {
                if (_physicalRoomDspManagedUsers.TryGetValue(channelId, out var managedSet))
                {
                    managedSet.Remove(userId);
                    if (managedSet.Count == 0) _physicalRoomDspManagedUsers.Remove(channelId);
                }
                if (_physicalRoomDspSpeakers.TryGetValue(channelId, out var dspSet))
                {
                    dspSet.Remove(userId);
                    if (dspSet.Count == 0) _physicalRoomDspSpeakers.Remove(channelId);
                }
            }

            var after = BuildEffectiveActiveSpeakersLocked(channelId);
            if (!SpeakerSetsEqual(before, after))
            {
                updatedSpeakers = after;
            }
        }

        if (updatedSpeakers != null)
        {
            VoiceActiveSpeakersChanged?.Invoke(channelId, updatedSpeakers);
        }
    }

    public void SetUserSpeaking(string channelId, string userId, bool isSpeaking)
    {
        if (string.IsNullOrWhiteSpace(channelId) || string.IsNullOrWhiteSpace(userId))
        {
            return;
        }

        string[]? updatedSpeakers = null;
        DateTime nowUtc = _timeProvider.GetUtcNow().UtcDateTime;
        lock (_voiceLock)
        {
            var before = BuildEffectiveActiveSpeakersLocked(channelId);

            if (isSpeaking)
            {
                if (_voiceStates.TryGetValue((channelId, userId), out var state) && state.IsMuted)
                {
                    return;
                }
                GetOrCreateVoiceStateLocked(channelId, userId).LastActivityUtc = nowUtc;
                if (!_activeSpeakersList.TryGetValue(channelId, out var set))
                {
                    set = new HashSet<string>(StringComparer.Ordinal);
                    _activeSpeakersList[channelId] = set;
                }
                set.Add(userId);
            }
            else if (_activeSpeakersList.TryGetValue(channelId, out var set))
            {
                set.Remove(userId);
                if (set.Count == 0)
                {
                    _activeSpeakersList.Remove(channelId);
                }
            }

            var after = BuildEffectiveActiveSpeakersLocked(channelId);
            if (!SpeakerSetsEqual(before, after))
            {
                updatedSpeakers = after;
            }
        }

        if (updatedSpeakers != null)
        {
            VoiceActiveSpeakersChanged?.Invoke(channelId, updatedSpeakers);
        }
    }

    /// <summary>
    /// Updates authoritative post-isolation speaking state produced by the server-side
    /// <see cref="Spokes_Server.Core.Services.Communication.Voice.Pipeline.PhysicalRoomPipelineStage"/> DSP worker.
    /// </summary>
    public void SetPhysicalRoomDspSpeaking(string channelId, string userId, bool isSpeaking)
    {
        if (string.IsNullOrWhiteSpace(channelId) || string.IsNullOrWhiteSpace(userId))
        {
            return;
        }

        string[]? updatedSpeakers = null;
        DateTime nowUtc = _timeProvider.GetUtcNow().UtcDateTime;
        lock (_voiceLock)
        {
            var before = BuildEffectiveActiveSpeakersLocked(channelId);

            if (isSpeaking)
            {
                if (_voiceStates.TryGetValue((channelId, userId), out var state) && state.IsMuted)
                {
                    return;
                }
                GetOrCreateVoiceStateLocked(channelId, userId).LastActivityUtc = nowUtc;
                if (!_physicalRoomDspSpeakers.TryGetValue(channelId, out var set))
                {
                    set = new HashSet<string>(StringComparer.Ordinal);
                    _physicalRoomDspSpeakers[channelId] = set;
                }
                if (!set.Add(userId))
                {
                    return;
                }
            }
            else
            {
                if (!_physicalRoomDspSpeakers.TryGetValue(channelId, out var set) || !set.Remove(userId))
                {
                    return;
                }
                if (set.Count == 0)
                {
                    _physicalRoomDspSpeakers.Remove(channelId);
                }
            }

            var after = BuildEffectiveActiveSpeakersLocked(channelId);
            if (!SpeakerSetsEqual(before, after))
            {
                updatedSpeakers = after;
            }
        }

        if (updatedSpeakers != null)
        {
            VoiceActiveSpeakersChanged?.Invoke(channelId, updatedSpeakers);
        }
    }

    public List<string> GetVoiceParticipants(string channelId)
    {
        lock (_voiceLock)
        {
            return _voiceParticipants.TryGetValue(channelId, out var p) ? p.ToList() : [];
        }
    }

    public bool IsKnownVoiceChannelParticipant(string channelId, string userId)
    {
        if (string.IsNullOrWhiteSpace(channelId) || string.IsNullOrWhiteSpace(userId))
        {
            return false;
        }

        DateTime nowUtc = _timeProvider.GetUtcNow().UtcDateTime;
        lock (_voiceLock)
        {
            if (_voiceParticipants.TryGetValue(channelId, out var participants) && participants.Contains(userId))
            {
                return true;
            }

            if (_voiceStates.TryGetValue((channelId, userId), out var state))
            {
                PruneClosedVoiceSessionsLocked(nowUtc);
                return string.IsNullOrWhiteSpace(state.SessionId) || !_closedVoiceSessionIds.ContainsKey(state.SessionId);
            }

            return false;
        }
    }

    public List<string> GetAllActiveVoiceChannels()
    {
        lock (_voiceLock)
        {
            return _voiceParticipants.Keys.ToList();
        }
    }

    public bool IsUserInAnyVoiceChannel(string userId)
    {
        lock (_voiceLock)
        {
            return _voiceParticipants.Values.Any(p => p.Contains(userId));
        }
    }

    public string[] GetActiveSpeakers(string channelId)
    {
        lock (_voiceLock)
        {
            return BuildEffectiveActiveSpeakersLocked(channelId);
        }
    }

    public void SetUserMuted(string channelId, string userId, bool isMuted) =>
        TrySetUserMuted(channelId, userId, isMuted, expectedSessionId: null);

    public bool TrySetUserMuted(string channelId, string userId, bool isMuted, string? expectedSessionId)
    {
        bool changed = false;
        string[]? updatedSpeakers = null;
        DateTime nowUtc = _timeProvider.GetUtcNow().UtcDateTime;
        string? normalizedExpectedSessionId = string.IsNullOrWhiteSpace(expectedSessionId) ? null : expectedSessionId.Trim();
        lock (_voiceLock)
        {
            if (!ValidateSessionUnlockedLocked(channelId, userId, normalizedExpectedSessionId, nowUtc))
            {
                return false;
            }

            var beforeSpeakers = isMuted ? BuildEffectiveActiveSpeakersLocked(channelId) : null;
            var state = GetOrCreateVoiceStateLocked(channelId, userId);
            state.LastActivityUtc = nowUtc;
            changed = state.IsMuted != isMuted;
            state.IsMuted = isMuted;

            if (isMuted)
            {
                if (_activeSpeakersList.TryGetValue(channelId, out var existingSpeakers))
                {
                    existingSpeakers.Remove(userId);
                    if (existingSpeakers.Count == 0) _activeSpeakersList.Remove(channelId);
                }
                if (_physicalRoomDspSpeakers.TryGetValue(channelId, out var dspSpeakers))
                {
                    dspSpeakers.Remove(userId);
                    if (dspSpeakers.Count == 0) _physicalRoomDspSpeakers.Remove(channelId);
                }
                var afterSpeakers = BuildEffectiveActiveSpeakersLocked(channelId);
                if (beforeSpeakers != null && !SpeakerSetsEqual(beforeSpeakers, afterSpeakers))
                {
                    updatedSpeakers = afterSpeakers;
                }
            }
        }
        if (updatedSpeakers != null)
        {
            VoiceActiveSpeakersChanged?.Invoke(channelId, updatedSpeakers);
        }
        if (changed)
        {
            VoiceUserMutedChanged?.Invoke(channelId, userId, isMuted);
        }
        return true;
    }

    public bool IsUserMuted(string channelId, string userId)
    {
        if (string.IsNullOrWhiteSpace(channelId) || string.IsNullOrWhiteSpace(userId))
        {
            return false;
        }

        lock (_voiceLock)
        {
            return _voiceStates.TryGetValue((channelId, userId), out var state) && state.IsMuted;
        }
    }

    public bool TrySetVoiceParticipantMediaState(
        string channelId,
        string userId,
        string? expectedSessionId = null,
        bool? isCameraOn = null,
        bool? isScreenSharing = null,
        bool? hasScreenAudio = null,
        int? videoHeight = null,
        int? videoFps = null)
    {
        if (string.IsNullOrWhiteSpace(channelId) || string.IsNullOrWhiteSpace(userId))
        {
            return false;
        }

        bool changed = false;
        VoiceParticipantMediaState snapshot;
        DateTime nowUtc = _timeProvider.GetUtcNow().UtcDateTime;
        string? normalizedExpectedSessionId = string.IsNullOrWhiteSpace(expectedSessionId) ? null : expectedSessionId.Trim();

        lock (_voiceLock)
        {
            if (!ValidateSessionUnlockedLocked(channelId, userId, normalizedExpectedSessionId, nowUtc))
            {
                return false;
            }

            var state = GetOrCreateVoiceStateLocked(channelId, userId);
            state.LastActivityUtc = nowUtc;

            if (isCameraOn.HasValue && state.IsCameraOn != isCameraOn.Value)
            {
                state.IsCameraOn = isCameraOn.Value;
                changed = true;
            }
            if (isScreenSharing.HasValue && state.IsScreenSharing != isScreenSharing.Value)
            {
                state.IsScreenSharing = isScreenSharing.Value;
                if (!isScreenSharing.Value && state.HasScreenAudio)
                {
                    state.HasScreenAudio = false;
                }
                changed = true;
            }
            if (hasScreenAudio.HasValue && state.HasScreenAudio != hasScreenAudio.Value)
            {
                state.HasScreenAudio = hasScreenAudio.Value;
                changed = true;
            }
            if (videoHeight.HasValue && videoHeight.Value >= 0 && state.VideoHeight != videoHeight.Value)
            {
                state.VideoHeight = videoHeight.Value;
                changed = true;
            }
            if (videoFps.HasValue && videoFps.Value >= 0 && state.VideoFps != videoFps.Value)
            {
                state.VideoFps = videoFps.Value;
                changed = true;
            }

            if (!state.IsCameraOn && !state.IsScreenSharing && (state.VideoHeight != 0 || state.VideoFps != 0))
            {
                state.VideoHeight = 0;
                state.VideoFps = 0;
                changed = true;
            }

            snapshot = new VoiceParticipantMediaState(
                state.IsCameraOn,
                state.IsScreenSharing,
                state.HasScreenAudio,
                state.VideoHeight,
                state.VideoFps);
        }

        if (changed)
        {
            VoiceUserMediaStateChanged?.Invoke(channelId, userId, snapshot);
        }

        return true;
    }

    public VoiceParticipantMediaState GetVoiceParticipantMediaState(string channelId, string userId)
    {
        if (string.IsNullOrWhiteSpace(channelId) || string.IsNullOrWhiteSpace(userId))
        {
            return default;
        }

        lock (_voiceLock)
        {
            if (_voiceStates.TryGetValue((channelId, userId), out var state))
            {
                return new VoiceParticipantMediaState(
                    state.IsCameraOn,
                    state.IsScreenSharing,
                    state.HasScreenAudio,
                    state.VideoHeight,
                    state.VideoFps);
            }

            return default;
        }
    }

    public HashSet<string> GetMutedUsers(string channelId)
    {
        lock (_voiceLock)
        {
            var result = new HashSet<string>();
            foreach (var kvp in _voiceStates)
            {
                if (string.Equals(kvp.Key.ChannelId, channelId, StringComparison.Ordinal) && kvp.Value.IsMuted)
                {
                    result.Add(kvp.Key.UserId);
                }
            }
            return result;
        }
    }
}
