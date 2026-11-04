using Spokes_Server.Core.Models.Communication;
using Spokes_Server.Core.Models.HR;

namespace Spokes_Server.Core.Services.Communication.Chat;

/// <summary>
/// Singleton service to manage local chat state and events within the server process.
/// Replaces the need for SignalR loopback connections.
/// </summary>
public class ChatStateService
{
    // Events
    public event Action<ChatMessage>? MessageReceived;
    public event Action<string, string, string>? UserTyping; // channelId, userId, userName
    public event Action<string, string>? UserStoppedTyping; // channelId, userId
    public event Action<ChatMessage>? MessageEdited;
    public event Action<string, string>? MessageDeleted; // channelId, messageId
    public event Action<string, string, string, string, string>? ReactionAdded; // channelId, messageId, emoji, userId, userName
    public event Action<string, string, string, string>? ReactionRemoved; // channelId, messageId, emoji, userId
    public event Action<string, string, List<string>>? MessageRead; // channelId, messageId, readBy
    public event Action<ReportedMessage>? ModerationAlertReceived;
    public event Action<ReportedMessage>? ModerationReportUpdated;
    public event Action<Employee>? EmployeeProfileUpdated;
    public event Action<CalendarEvent>? CalendarEventUpdated;
    public event Action<string>? CalendarEventDeleted;
    public event Action<ChatPoll>? PollUpdated;
    public event Action<string>? PollDeleted;

    // Methods to trigger events
    public void NotifyCalendarEventUpdated(CalendarEvent calendarEvent) => CalendarEventUpdated?.Invoke(calendarEvent);
    public void NotifyCalendarEventDeleted(string eventId) => CalendarEventDeleted?.Invoke(eventId);
    public void NotifyPollUpdated(ChatPoll poll) => PollUpdated?.Invoke(poll);
    public void NotifyPollDeleted(string pollId) => PollDeleted?.Invoke(pollId);
    public void NotifyMessageReceived(ChatMessage message) => MessageReceived?.Invoke(message);
    public void NotifyModerationAlert(ReportedMessage report) => ModerationAlertReceived?.Invoke(report);
    public void NotifyModerationReportUpdated(ReportedMessage report) => ModerationReportUpdated?.Invoke(report);
    public void NotifyEmployeeProfileUpdated(Employee employee) => EmployeeProfileUpdated?.Invoke(employee);
    public void NotifyUserTyping(string channelId, string userId, string userName) => UserTyping?.Invoke(channelId, userId, userName);
    public void NotifyUserStoppedTyping(string channelId, string userId) => UserStoppedTyping?.Invoke(channelId, userId);
    public void NotifyMessageEdited(ChatMessage message) => MessageEdited?.Invoke(message);
    public void NotifyMessageDeleted(string channelId, string messageId) => MessageDeleted?.Invoke(channelId, messageId);
    public void NotifyReactionAdded(string channelId, string messageId, string emoji, string userId, string userName) => ReactionAdded?.Invoke(channelId, messageId, emoji, userId, userName);
    public void NotifyReactionRemoved(string channelId, string messageId, string emoji, string userId) => ReactionRemoved?.Invoke(channelId, messageId, emoji, userId);
    public void NotifyMessageRead(string channelId, string messageId, List<string> readBy) => MessageRead?.Invoke(channelId, messageId, readBy);

    public event Action<ChatChannel>? ChannelCreated;
    public void NotifyChannelCreated(ChatChannel channel) => ChannelCreated?.Invoke(channel);

    // Voice Presence State
    private readonly Dictionary<string, HashSet<string>> _voiceParticipants = new();
    private readonly Dictionary<(string ChannelId, string UserId), (string DeviceId, string DeviceName)> _voiceParticipantDevices = new();
    private readonly Dictionary<(string ChannelId, string UserId), (string FilterType, double EstimatedCaptureLatencyMs, string Platform, double ServerToClientDelayMs)> _voiceParticipantAudioTelemetry = new();
    private readonly Dictionary<string, string[]> _activeSpeakersList = new();
    private readonly Dictionary<string, HashSet<string>> _mutedUsers = new();
    private readonly object _voiceLock = new();

    public event Action<string, string>? VoiceMemberJoined;
    public event Action<string, string>? VoiceMemberLeft;
    public event Action<string, string[]>? VoiceActiveSpeakersChanged;
    public event Action<string, string, bool>? VoiceUserMutedChanged;
    public event Action? PhysicalRoomsChanged;

    public void NotifyPhysicalRoomsChanged() => PhysicalRoomsChanged?.Invoke();

    public bool NotifyVoiceMemberJoined(string channelId, string userId, string? deviceId = null, string? deviceName = null)
    {
        bool added;
        lock (_voiceLock)
        {
            if (!_voiceParticipants.ContainsKey(channelId)) _voiceParticipants[channelId] = new HashSet<string>();
            added = _voiceParticipants[channelId].Add(userId);
            if (!string.IsNullOrWhiteSpace(deviceId))
            {
                _voiceParticipantDevices[(channelId, userId)] = (deviceId, deviceName ?? string.Empty);
            }
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
        lock (_voiceLock)
        {
            _voiceParticipantDevices[(channelId, userId)] = (deviceId, deviceName ?? string.Empty);
        }
        PhysicalRoomsChanged?.Invoke();
    }

    public (string DeviceId, string DeviceName)? GetVoiceParticipantDevice(string channelId, string userId)
    {
        lock (_voiceLock)
        {
            if (_voiceParticipantDevices.TryGetValue((channelId, userId), out var info))
            {
                return info;
            }
            return null;
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

        lock (_voiceLock)
        {
            if (clampedServerDelay <= 0.0 && _voiceParticipantAudioTelemetry.TryGetValue((channelId, userId), out var existing))
            {
                clampedServerDelay = existing.ServerToClientDelayMs;
            }
            _voiceParticipantAudioTelemetry[(channelId, userId)] = (cleanFilter, clampedLatency, cleanPlatform, clampedServerDelay);
        }
        PhysicalRoomsChanged?.Invoke();
    }

    public (string FilterType, double EstimatedCaptureLatencyMs, string Platform, double ServerToClientDelayMs)? GetVoiceParticipantAudioTelemetry(string channelId, string userId)
    {
        lock (_voiceLock)
        {
            if (_voiceParticipantAudioTelemetry.TryGetValue((channelId, userId), out var telemetry))
            {
                return telemetry;
            }
            return null;
        }
    }

    public bool NotifyVoiceMemberLeft(string channelId, string userId)
    {
        bool wasRemoved = false;
        bool wasMuted = false;
        string[]? updatedSpeakers = null;
        lock (_voiceLock)
        {
            _voiceParticipantDevices.Remove((channelId, userId));
            _voiceParticipantAudioTelemetry.Remove((channelId, userId));
            if (_voiceParticipants.ContainsKey(channelId))
            {
                wasRemoved = _voiceParticipants[channelId].Remove(userId);
                if (_voiceParticipants[channelId].Count == 0) _voiceParticipants.Remove(channelId);
            }
            if (_mutedUsers.ContainsKey(channelId))
            {
                wasMuted = _mutedUsers[channelId].Remove(userId);
                if (_mutedUsers[channelId].Count == 0) _mutedUsers.Remove(channelId);
            }
            if (_activeSpeakersList.TryGetValue(channelId, out var existingSpeakers))
            {
                var speakers = existingSpeakers.ToList();
                if (speakers.Remove(userId))
                {
                    updatedSpeakers = speakers.ToArray();
                    _activeSpeakersList[channelId] = updatedSpeakers;
                }
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
        return wasRemoved;
    }

    public void SetUserSpeaking(string channelId, string userId, bool isSpeaking)
    {
        string[] currentSpeakers;
        lock (_voiceLock)
        {
            var set = _activeSpeakersList.TryGetValue(channelId, out var existing)
                ? existing.ToHashSet()
                : new HashSet<string>();

            if (isSpeaking)
            {
                set.Add(userId);
            }
            else
            {
                set.Remove(userId);
            }
            currentSpeakers = set.ToArray();
            _activeSpeakersList[channelId] = currentSpeakers;
        }
        VoiceActiveSpeakersChanged?.Invoke(channelId, currentSpeakers);
    }

    public void NotifyVoiceActiveSpeakersChanged(string channelId, string[] speakerIds)
    {
        lock (_voiceLock)
        {
            _activeSpeakersList[channelId] = speakerIds;
        }
        VoiceActiveSpeakersChanged?.Invoke(channelId, speakerIds);
    }

    public List<string> GetVoiceParticipants(string channelId)
    {
        lock (_voiceLock)
        {
            if (_voiceParticipants.TryGetValue(channelId, out var p)) return p.ToList();
            return [];
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
            if (_activeSpeakersList.TryGetValue(channelId, out var s)) return s;
            return [];
        }
    }

    public void SetUserMuted(string channelId, string userId, bool isMuted)
    {
        bool changed = false;
        lock (_voiceLock)
        {
            if (!_mutedUsers.ContainsKey(channelId)) _mutedUsers[channelId] = new HashSet<string>();
            if (isMuted)
            {
                changed = _mutedUsers[channelId].Add(userId);
            }
            else
            {
                changed = _mutedUsers[channelId].Remove(userId);
                if (_mutedUsers[channelId].Count == 0) _mutedUsers.Remove(channelId);
            }
        }
        if (changed)
        {
            VoiceUserMutedChanged?.Invoke(channelId, userId, isMuted);
        }
    }

    public HashSet<string> GetMutedUsers(string channelId)
    {
        lock (_voiceLock)
        {
            if (_mutedUsers.TryGetValue(channelId, out var set)) return set.ToHashSet();
            return [];
        }
    }
}
