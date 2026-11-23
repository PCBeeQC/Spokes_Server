using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Livekit.Server;
using Livekit.Server.Sdk.Dotnet;
using Microsoft.Extensions.Logging;
using Spokes_Server.Core.Data.Repositories.Communication;
using Spokes_Server.Core.Data.Repositories.Core;
using Spokes_Server.Core.Data.Repositories.HR;
using Spokes_Server.Core.Helpers;
using Spokes_Server.Core.Models.Communication;
using Spokes_Server.Core.Models.Communication.Notifications;
using Spokes_Server.Core.Models.HR;
using Spokes_Server.Core.Services.Communication.Chat;
using Spokes_Server.Core.Services.Communication.Notifications;
using Spokes_Server.Core.Services.Communication.Notifications.Presentation;
using Spokes_Server.Core.Services.Communication.Notifications.Routing;
using ChatMessage = Spokes_Server.Core.Models.Communication.ChatMessage;

namespace Spokes_Server.Core.Services.Communication.Voice;

/// <summary>
/// Domain service managing WebRTC voice channel signaling, participant presence, speaking states,
/// call invites, join tokens, and LiveKit daemon room coordination.
/// Extracted from ChatService to maintain single responsibility.
/// </summary>
public class VoiceChannelService
{
    private readonly VoiceStateService _voiceState;
    private readonly ChatStateService _chatState;
    private readonly ChatMessageRepository _messages;
    private readonly INotificationRoutingService _notificationRouting;
    private readonly SystemConfigRepository _systemConfigs;
    private readonly LiveKitService _liveKitService;
    private readonly EmployeeRepository _employees;
    private readonly ChatChannelRepository _channels;
    private readonly ChatReadStateRepository _readStates;
    private readonly IChatAuthorizationService? _chatAuth;
    private readonly IVoiceBotService? _voiceBotService;
    private readonly ILogger<VoiceChannelService> _logger;
    private readonly ConcurrentDictionary<(string ChannelId, string UserId), CancellationTokenSource> _pendingWebhookLeaveChimes = new();

    /// <summary>
    /// Short grace window before playing <c>channel_leave</c> for an involuntary SFU <c>participant_left</c>
    /// webhook so sub-second LiveKit full reconnects (e.g., iOS PiP backgrounding or Wi-Fi handoff)
    /// do not play phantom disconnect chimes when the participant rejoins immediately.
    /// Explicit leaves (<c>DisconnectAsync</c> / kicks) always play immediately.
    /// </summary>
    internal TimeSpan WebhookLeaveChimeDelay { get; set; } = TimeSpan.FromSeconds(4);

    private bool CancelPendingWebhookLeaveChime(string channelId, string userId)
    {
        if (_pendingWebhookLeaveChimes.TryRemove((channelId, userId), out var pendingCts))
        {
            try
            {
                pendingCts.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
            return true;
        }
        return false;
    }

    private void ScheduleDeferredWebhookLeaveChime(string channelId, string userId)
    {
        if (_voiceBotService == null) return;
        var delay = WebhookLeaveChimeDelay;
        if (delay <= TimeSpan.Zero)
        {
            CancelPendingWebhookLeaveChime(channelId, userId);
            _ = _voiceBotService.PlaySoundAsync(channelId, "channel_leave", CancellationToken.None);
            return;
        }

        var key = (channelId, userId);
        var cts = new CancellationTokenSource();
        _ = _pendingWebhookLeaveChimes.AddOrUpdate(key, cts, (_, existing) =>
        {
            try { existing.Cancel(); } catch (ObjectDisposedException) { }
            return cts;
        });

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(delay, cts.Token);
                if (cts.IsCancellationRequested) return;
                if (!_pendingWebhookLeaveChimes.TryRemove(new KeyValuePair<(string, string), CancellationTokenSource>(key, cts)))
                {
                    return;
                }

                // Only play the leave chime if the participant has NOT reconnected and others are still in the call.
                var remaining = _voiceState.GetVoiceParticipants(channelId);
                if (remaining.Count > 0 && !remaining.Contains(userId))
                {
                    await _voiceBotService.PlaySoundAsync(channelId, "channel_leave", CancellationToken.None);
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                _logger?.LogDebug(ex, "[VoiceChannelService] Deferred webhook leave chime failed for {UserId} in channel {ChannelId}", userId, channelId);
            }
            finally
            {
                _pendingWebhookLeaveChimes.TryRemove(new KeyValuePair<(string, string), CancellationTokenSource>(key, cts));
                cts.Dispose();
            }
        });
    }

    /// <summary>
    /// Parameterless constructor for mocking / unit tests.
    /// </summary>
    protected VoiceChannelService()
    {
        _voiceState = null!;
        _chatState = null!;
        _messages = null!;
        _notificationRouting = null!;
        _systemConfigs = null!;
        _liveKitService = null!;
        _employees = null!;
        _channels = null!;
        _readStates = null!;
        _chatAuth = null;
        _voiceBotService = null;
        _logger = null!;
    }


    public VoiceChannelService(
        VoiceStateService voiceState,
        ChatStateService chatState,
        ChatMessageRepository messages,
        INotificationRoutingService notificationRouting,
        SystemConfigRepository systemConfigs,
        LiveKitService liveKitService,
        ILogger<VoiceChannelService> logger,
        EmployeeRepository employees,
        ChatChannelRepository channels,
        ChatReadStateRepository readStates,
        IChatAuthorizationService? chatAuth = null,
        IVoiceBotService? voiceBotService = null)
    {
        _voiceState = voiceState ?? throw new ArgumentNullException(nameof(voiceState));
        _chatState = chatState ?? throw new ArgumentNullException(nameof(chatState));
        _messages = messages ?? throw new ArgumentNullException(nameof(messages));
        _notificationRouting = notificationRouting ?? throw new ArgumentNullException(nameof(notificationRouting));
        _systemConfigs = systemConfigs ?? throw new ArgumentNullException(nameof(systemConfigs));
        _liveKitService = liveKitService ?? throw new ArgumentNullException(nameof(liveKitService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _employees = employees ?? throw new ArgumentNullException(nameof(employees));
        _channels = channels ?? throw new ArgumentNullException(nameof(channels));
        _readStates = readStates ?? throw new ArgumentNullException(nameof(readStates));
        _chatAuth = chatAuth;
        _voiceBotService = voiceBotService;
    }

    public virtual string GenerateJoinToken(string channelId, string userId, string userName, string userAvatarUrl = "") =>
        GenerateJoinToken(channelId, userId, userName, userAvatarUrl, voiceSessionId: null);

    public virtual string GenerateJoinToken(
        string channelId,
        string userId,
        string userName,
        string? userAvatarUrl,
        string? voiceSessionId,
        string? deviceId = null,
        string? deviceName = null,
        string? platform = null)
    {
        if (!IsAuthorizedForVoiceChannel(channelId, userId, nameof(GenerateJoinToken), out _))
        {
            throw new UnauthorizedAccessException("User is not authorized to join this voice channel.");
        }

        var config = _systemConfigs.Get();
        var apiKey = config.LiveKitApiKey ?? string.Empty;
        var apiSecret = config.LiveKitApiSecret ?? string.Empty;
        string roomName = LiveKitService.GetRoomName(channelId);

        var metadataJson = new VoiceParticipantMetadata
        {
            AvatarUrl = userAvatarUrl ?? string.Empty,
            VoiceSessionId = voiceSessionId?.Trim() ?? string.Empty,
            DeviceId = deviceId?.Trim() ?? string.Empty,
            DeviceName = deviceName?.Trim() ?? string.Empty,
            Platform = platform?.Trim() ?? string.Empty
        }.ToJson();

        var token = new AccessToken(apiKey, apiSecret)
            .WithIdentity(userId)
            .WithName(userName)
            .WithMetadata(metadataJson);

        token.WithGrants(new VideoGrants
        {
            RoomJoin = true,
            Room = roomName,
            CanPublish = true,
            CanPublishData = true,
            CanSubscribe = true
        });

        return token.ToJwt();
    }

    public virtual Task JoinVoiceAsync(string channelId, string userId, string? deviceId, string? deviceName, CancellationToken cancellationToken = default) =>
        JoinVoiceAsync(channelId, userId, deviceId, deviceName, voiceSessionId: null, cancellationToken);

    public virtual async Task JoinVoiceAsync(string channelId, string userId, string? deviceId, string? deviceName, string? voiceSessionId, CancellationToken cancellationToken = default)
    {
        if (!IsAuthorizedForVoiceChannel(channelId, userId, "JoinVoiceAsync") ||
            _voiceState.IsVoiceSessionClosed(voiceSessionId))
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(deviceId))
        {
            _voiceState.SetVoiceParticipantDevice(channelId, userId, deviceId, deviceName);
        }

        await JoinVoiceCoreAsync(channelId, userId, voiceSessionId, playJoinSound: true, cancellationToken);
    }

    public virtual Task JoinVoiceAsync(string channelId, string userId, CancellationToken cancellationToken = default) =>
        JoinVoiceCoreAsync(channelId, userId, voiceSessionId: null, playJoinSound: true, cancellationToken);

    private bool IsAuthorizedForVoiceChannel(string channelId, string userId, string operationName) =>
        IsAuthorizedForVoiceChannel(channelId, userId, operationName, out _);

    private bool IsAuthorizedForVoiceChannel(string channelId, string userId, string operationName, out Employee? authorizedEmp)
    {
        authorizedEmp = _employees?.GetById(userId);
        if (authorizedEmp != null && !authorizedEmp.IsActiveUnsuspended)
        {
            _logger?.LogWarning("{Operation} denied: User {UserId} is inactive, banned, or suspended for channel {ChannelId}", operationName, userId, channelId);
            authorizedEmp = null;
            return false;
        }

        if (_chatAuth == null)
        {
            return true;
        }

        var channel = _channels?.GetById(channelId);
        if (channel == null || authorizedEmp == null || !authorizedEmp.IsActiveUnsuspended || !_chatAuth.CanUserAccessChannel(channel, authorizedEmp))
        {
            _logger?.LogWarning("{Operation} denied: User {UserId} is not authorized for channel {ChannelId}", operationName, userId, channelId);
            authorizedEmp = null;
            return false;
        }

        return true;
    }

    private async Task JoinVoiceCoreAsync(string channelId, string userId, string? voiceSessionId, bool playJoinSound, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(voiceSessionId))
        {
            voiceSessionId = _voiceState.GetVoiceParticipantSessionId(channelId, userId);
        }

        if (!IsAuthorizedForVoiceChannel(channelId, userId, "JoinVoiceAsync") ||
            _voiceState.IsVoiceSessionClosed(voiceSessionId))
        {
            return;
        }

        CancelPendingWebhookLeaveChime(channelId, userId);

        bool isNewMember = _voiceState.NotifyVoiceMemberJoined(channelId, userId, voiceSessionId: voiceSessionId);
        if (isNewMember)
        {
            var currentParticipants = _voiceState.GetVoiceParticipants(channelId);
            if (currentParticipants.Count > 1)
            {
                if (_voiceBotService != null)
                {
                    await _voiceBotService.StopRingingAsync(channelId);
                }

                var activeInvite = _messages.GetByChannel(channelId, 50)
                    .FirstOrDefault(m => m.MessageType == ChatMessageType.CallInvite && m.CallStatus == CallStatus.Active);
                if (activeInvite != null)
                {
                    await ApplyCallInviteStatusCoreAsync(activeInvite, CallStatus.Joined, cancellationToken);
                }
            }
        }

        bool shouldPlayJoinChime = playJoinSound && _voiceState.TryClaimJoinChime(channelId, userId, voiceSessionId);

        if (_voiceBotService != null)
        {
            try
            {
                var ensureTask = _voiceBotService.EnsureBotJoinedAsync(channelId, CancellationToken.None);
                if (ensureTask.IsCompleted)
                {
                    await CompleteVoiceBotJoinAsync(ensureTask, channelId, shouldPlayJoinChime, CancellationToken.None);
                }
                else
                {
                    // Warm up the server bot in the background so human callers never block 5-10s
                    // waiting on server-side LiveKit WebRTC ICE/DTLS negotiation before joining.
                    _ = CompleteVoiceBotJoinAsync(ensureTask, channelId, shouldPlayJoinChime, CancellationToken.None);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[VoiceChannelService] Failed to dispatch join sound via voice bot for channel {ChannelId}", channelId);
            }
        }
    }

    private async Task CompleteVoiceBotJoinAsync(Task ensureTask, string channelId, bool shouldPlayJoinChime, CancellationToken cancellationToken)
    {
        try
        {
            await ensureTask;
            await _voiceBotService!.NotifyRoomConfigurationChangedAsync(channelId, cancellationToken);

            var participants = _voiceState.GetVoiceParticipants(channelId);
            if (shouldPlayJoinChime && participants.Count > 1)
            {
                await _voiceBotService.StopRingingAsync(channelId);
                _ = _voiceBotService.PlaySoundAsync(channelId, "channel_join", CancellationToken.None);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[VoiceChannelService] Failed to dispatch join sound via voice bot for channel {ChannelId}", channelId);
        }
    }

    public virtual Task LeaveVoiceAsync(string channelId, string userId, CancellationToken cancellationToken = default) =>
        LeaveVoiceCoreAsync(channelId, userId, evictFromLiveKit: true, expectedSessionId: null, cancellationToken);

    public virtual Task LeaveVoiceAsync(string channelId, string userId, bool evictFromLiveKit, CancellationToken cancellationToken = default) =>
        LeaveVoiceCoreAsync(channelId, userId, evictFromLiveKit, expectedSessionId: null, cancellationToken);

    public virtual Task<bool> TryLeaveVoiceAsync(string channelId, string userId, string? expectedSessionId, bool evictFromLiveKit = true, CancellationToken cancellationToken = default) =>
        LeaveVoiceCoreAsync(channelId, userId, evictFromLiveKit, expectedSessionId, cancellationToken);

    private async Task EvictUserFromLiveKitAsync(string channelId, string userId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(channelId) || string.IsNullOrWhiteSpace(userId)) return;
        var cfg = _systemConfigs.Get();
        if (!string.IsNullOrEmpty(cfg.LiveKitApiKey) && !string.IsNullOrEmpty(cfg.LiveKitApiSecret))
        {
            await _liveKitService.RemoveParticipantAsync(channelId, userId, cfg.LiveKitApiKey, cfg.LiveKitApiSecret, cancellationToken);
        }
    }

    private async Task<bool> LeaveVoiceCoreAsync(
        string channelId,
        string userId,
        bool evictFromLiveKit,
        string? expectedSessionId,
        CancellationToken cancellationToken,
        bool explicitlyClosed = true,
        string? closedParticipantSid = null)
    {
        if (!string.IsNullOrWhiteSpace(expectedSessionId))
        {
            var activeSessionId = _voiceState.GetVoiceParticipantSessionId(channelId, userId);
            if (!string.IsNullOrWhiteSpace(activeSessionId) &&
                !string.Equals(activeSessionId, expectedSessionId.Trim(), StringComparison.Ordinal))
            {
                _logger?.LogDebug(
                    "[VoiceChannelService] Ignoring stale leave for {UserId} in channel {ChannelId} (expectedSession={Expected}, activeSession={Active}).",
                    userId, channelId, expectedSessionId, activeSessionId);
                return false;
            }
        }

        bool wasPresent = _voiceState.TryNotifyVoiceMemberLeft(
            channelId,
            userId,
            expectedSessionId,
            markSessionClosed: true,
            explicitlyClosed: explicitlyClosed,
            closedParticipantSid: closedParticipantSid);
        if (!wasPresent)
        {
            if (explicitlyClosed &&
                CancelPendingWebhookLeaveChime(channelId, userId) &&
                _voiceBotService != null &&
                _voiceState.GetVoiceParticipants(channelId).Count > 0)
            {
                // Explicit hangup confirmed a leave whose SFU webhook arrived milliseconds earlier:
                // promote the deferred webhook leave chime to play immediately.
                _ = _voiceBotService.PlaySoundAsync(channelId, "channel_leave", CancellationToken.None);
            }
            if (evictFromLiveKit)
            {
                await EvictUserFromLiveKitAsync(channelId, userId, cancellationToken);
            }
            return false;
        }

        var participants = _voiceState.GetVoiceParticipants(channelId);
        if (participants.Count == 0)
        {
            CancelPendingWebhookLeaveChime(channelId, userId);
            if (_voiceBotService != null)
            {
                await _voiceBotService.StopRingingAsync(channelId);
            }

            var activeInvite = _messages.GetByChannel(channelId, 50)
                .FirstOrDefault(m => m.MessageType == ChatMessageType.CallInvite && (m.CallStatus == CallStatus.Active || m.CallStatus == CallStatus.Joined));
            if (activeInvite != null)
            {
                await ApplyCallInviteStatusCoreAsync(activeInvite, CallStatus.Ended, cancellationToken);
            }
        }

        if (_voiceBotService != null)
        {
            try
            {
                await _voiceBotService.NotifyRoomConfigurationChangedAsync(channelId, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "[VoiceChannelService] Failed to notify bot pipeline on leave in channel {ChannelId}", channelId);
            }

            if (participants.Count == 0)
            {
                _voiceBotService.ScheduleBotDeparture(channelId, TimeSpan.FromSeconds(30));
            }
            else if (explicitlyClosed)
            {
                CancelPendingWebhookLeaveChime(channelId, userId);
                _ = _voiceBotService.PlaySoundAsync(channelId, "channel_leave", CancellationToken.None);
            }
            else
            {
                ScheduleDeferredWebhookLeaveChime(channelId, userId);
            }
        }

        if (evictFromLiveKit)
        {
            await EvictUserFromLiveKitAsync(channelId, userId, cancellationToken);
        }

        return true;
    }

    public virtual async Task EvictUserFromAllVoiceChannelsAsync(string userId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(userId))
        {
            return;
        }

        var activeChannels = _voiceState.GetAllActiveVoiceChannels()
            .Where(ch => _voiceState.GetVoiceParticipants(ch).Contains(userId))
            .ToList();

        foreach (var channelId in activeChannels)
        {
            try
            {
                await LeaveVoiceAsync(channelId, userId, evictFromLiveKit: true, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "[VoiceChannelService] Failed to evict user {UserId} from voice channel {ChannelId}", userId, channelId);
            }
        }
    }

    public virtual Task SetUserSpeakingAsync(string channelId, string userId, bool isSpeaking, CancellationToken cancellationToken = default)
    {
        if (!IsAuthorizedForVoiceChannel(channelId, userId, nameof(SetUserSpeakingAsync)))
        {
            return Task.CompletedTask;
        }

        _voiceState.SetUserSpeaking(channelId, userId, isSpeaking);
        return Task.CompletedTask;
    }

    public virtual Task SetUserMutedAsync(string channelId, string userId, bool isMuted, CancellationToken cancellationToken = default) =>
        TrySetUserMutedAsync(channelId, userId, isMuted, expectedSessionId: null, cancellationToken);

    public virtual Task<bool> TrySetUserMutedAsync(string channelId, string userId, bool isMuted, string? expectedSessionId, CancellationToken cancellationToken = default)
    {
        if (!IsAuthorizedForVoiceChannel(channelId, userId, nameof(TrySetUserMutedAsync)))
        {
            return Task.FromResult(false);
        }

        if (!_voiceState.TrySetUserMuted(channelId, userId, isMuted, expectedSessionId))
        {
            return Task.FromResult(false);
        }

        return Task.FromResult(true);
    }

    /// <summary>
    /// Non-destructively reconciles an active LiveKit participant into C# voice state, restoring missing
    /// member slots, physical device metadata, and mic mute state without replaying join/leave chimes.
    /// Rejects any session ID that has already been closed/tombstoned.
    /// </summary>
    public virtual async Task ReconcileMemberStateAsync(
        string channelId,
        string userId,
        VoiceParticipantMetadata? metadata = null,
        bool? isMicMuted = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(channelId) ||
            string.IsNullOrWhiteSpace(userId) ||
            LiveKitService.IsSyntheticParticipantIdentity(userId) ||
            !IsAuthorizedForVoiceChannel(channelId, userId, "ReconcileMemberStateAsync"))
        {
            return;
        }

        var sessionId = string.IsNullOrWhiteSpace(metadata?.VoiceSessionId) ? null : metadata!.VoiceSessionId.Trim();
        if (_voiceState.IsVoiceSessionClosed(sessionId))
        {
            _logger?.LogDebug(
                "[VoiceChannelService] Ignoring ReconcileMemberStateAsync for {UserId} in channel {ChannelId} with closed session {SessionId}.",
                userId, channelId, sessionId);
            return;
        }

        var previousSessionId = _voiceState.GetVoiceParticipantSessionId(channelId, userId);
        bool isPresent = _voiceState.GetVoiceParticipants(channelId).Contains(userId);
        CancelPendingWebhookLeaveChime(channelId, userId);

        if (!isPresent)
        {
            if (!string.IsNullOrWhiteSpace(metadata?.DeviceId))
            {
                _voiceState.SetVoiceParticipantDevice(channelId, userId, metadata.DeviceId, metadata.DeviceName);
            }

            await JoinVoiceCoreAsync(
                channelId,
                userId,
                sessionId,
                playJoinSound: false,
                cancellationToken);
        }
        else
        {
            _voiceState.TouchVoiceParticipantActivity(channelId, userId, sessionId);
            if (!string.IsNullOrWhiteSpace(metadata?.DeviceId) &&
                (_voiceState.GetVoiceParticipantDevice(channelId, userId) == null ||
                 (sessionId != null && !string.Equals(previousSessionId, sessionId, StringComparison.Ordinal))))
            {
                _voiceState.SetVoiceParticipantDevice(channelId, userId, metadata.DeviceId, metadata.DeviceName);
            }
        }

        if (!string.IsNullOrWhiteSpace(metadata?.Platform))
        {
            var existingTelemetry = _voiceState.GetVoiceParticipantAudioTelemetry(channelId, userId);
            _voiceState.SetVoiceParticipantAudioTelemetry(
                channelId,
                userId,
                existingTelemetry?.FilterType ?? "webrtc",
                existingTelemetry?.EstimatedCaptureLatencyMs ?? 0.0,
                metadata.Platform,
                existingTelemetry?.ServerToClientDelayMs ?? 0.0);
        }

        if (isMicMuted.HasValue && _voiceState.IsUserMuted(channelId, userId) != isMicMuted.Value)
        {
            await TrySetUserMutedAsync(channelId, userId, isMicMuted.Value, sessionId, cancellationToken);
        }
    }

    /// <summary>
    /// Processes a verified LiveKit webhook event using session-fenced lifecycle transitions.
    /// Track events only mutate already-active participants and never resurrect departed users.
    /// </summary>
    public virtual async Task IngestWebhookEventAsync(WebhookEvent webhookEvent, CancellationToken cancellationToken = default)
    {
        if (webhookEvent == null ||
            !LiveKitService.TryExtractChannelIdFromRoomName(webhookEvent.Room?.Name, out var channelId))
        {
            return;
        }

        var eventName = webhookEvent.Event ?? string.Empty;
        var participant = webhookEvent.Participant;
        var identity = participant?.Identity ?? string.Empty;

        switch (eventName)
        {
            case "participant_joined":
            {
                if (participant == null || string.IsNullOrWhiteSpace(identity) || LiveKitService.IsSyntheticParticipantIdentity(identity))
                {
                    return;
                }

                var snapshot = LiveKitService.ToParticipantSnapshot(participant);
                var metadata = VoiceParticipantMetadata.Parse(snapshot.Metadata);
                if (_voiceState.IsVoiceSessionClosed(metadata.VoiceSessionId))
                {
                    if (_voiceState.CanReopenWebhookClosedSession(metadata.VoiceSessionId, participant.Sid))
                    {
                        _voiceState.ReopenVoiceSession(channelId, snapshot.Identity, metadata.VoiceSessionId, participant.Sid);
                    }
                    else
                    {
                        return;
                    }
                }

                await ReconcileMemberStateAsync(channelId, snapshot.Identity, metadata, snapshot.IsMicMuted, cancellationToken);
                _voiceState.SetVoiceParticipantSid(channelId, snapshot.Identity, metadata.VoiceSessionId, snapshot.Sid);
                _voiceState.TrySetVoiceParticipantMediaState(
                    channelId,
                    snapshot.Identity,
                    metadata.VoiceSessionId,
                    isCameraOn: snapshot.IsCameraOn,
                    isScreenSharing: snapshot.IsScreenSharing,
                    hasScreenAudio: snapshot.HasScreenAudio);
                break;
            }
            case "participant_left":
            {
                if (participant == null || string.IsNullOrWhiteSpace(identity) || LiveKitService.IsSyntheticParticipantIdentity(identity))
                {
                    return;
                }

                var metadata = VoiceParticipantMetadata.Parse(participant.Metadata);
                var expectedSessionId = string.IsNullOrWhiteSpace(metadata.VoiceSessionId) ? null : metadata.VoiceSessionId;
                await LeaveVoiceCoreAsync(
                    channelId,
                    identity,
                    evictFromLiveKit: false,
                    expectedSessionId,
                    cancellationToken,
                    explicitlyClosed: false,
                    closedParticipantSid: participant.Sid);
                break;
            }
            case "track_published":
            case "track_unpublished":
            case "track_muted":
            case "track_unmuted":
            {
                if (participant == null || string.IsNullOrWhiteSpace(identity) || LiveKitService.IsSyntheticParticipantIdentity(identity))
                {
                    return;
                }

                var snapshot = LiveKitService.ToParticipantSnapshot(participant);
                var metadata = VoiceParticipantMetadata.Parse(snapshot.Metadata);
                var sessionId = string.IsNullOrWhiteSpace(metadata.VoiceSessionId) ? null : metadata.VoiceSessionId.Trim();
                if (_voiceState.IsVoiceSessionClosed(sessionId))
                {
                    return;
                }

                var activeSessionId = _voiceState.GetVoiceParticipantSessionId(channelId, identity);
                if (sessionId != null &&
                    !string.IsNullOrWhiteSpace(activeSessionId) &&
                    !string.Equals(activeSessionId, sessionId, StringComparison.Ordinal))
                {
                    return;
                }

                bool isVideoOrScreenAudio = webhookEvent.Track != null &&
                    (webhookEvent.Track.Type == TrackType.Video ||
                     webhookEvent.Track.Source == TrackSource.ScreenShareAudio);

                if (isVideoOrScreenAudio)
                {
                    if (!_voiceState.IsKnownVoiceChannelParticipant(channelId, identity))
                    {
                        return;
                    }

                    bool isActive = eventName is "track_published" or "track_unmuted"
                        && !(eventName == "track_published" && webhookEvent.Track!.Muted);
                    if (webhookEvent.Track!.Source == TrackSource.ScreenShare)
                    {
                        _voiceState.TrySetVoiceParticipantMediaState(channelId, identity, sessionId, isScreenSharing: isActive);
                    }
                    else if (webhookEvent.Track.Source == TrackSource.ScreenShareAudio)
                    {
                        _voiceState.TrySetVoiceParticipantMediaState(channelId, identity, sessionId, hasScreenAudio: isActive);
                    }
                    else if (webhookEvent.Track.Type == TrackType.Video)
                    {
                        _voiceState.TrySetVoiceParticipantMediaState(channelId, identity, sessionId, isCameraOn: isActive);
                    }
                    break;
                }

                if (webhookEvent.Track != null &&
                    (webhookEvent.Track.Type != TrackType.Audio ||
                     (webhookEvent.Track.Source != TrackSource.Microphone && webhookEvent.Track.Source != TrackSource.Unknown)))
                {
                    return;
                }

                if (!_voiceState.GetVoiceParticipants(channelId).Contains(identity))
                {
                    return;
                }

                _voiceState.TouchVoiceParticipantActivity(channelId, identity, sessionId);
                if (string.Equals(eventName, "track_unpublished", StringComparison.Ordinal))
                {
                    // Web clients unpublish mic tracks during noise-filter and microphone device switches,
                    // not only on mute; never infer muted state from track_unpublished.
                    break;
                }

                bool? muted = eventName switch
                {
                    "track_muted" => true,
                    "track_unmuted" => false,
                    "track_published" => webhookEvent.Track != null ? webhookEvent.Track.Muted : (snapshot.IsMicMuted ?? false),
                    _ => snapshot.IsMicMuted
                };
                if (muted.HasValue && _voiceState.IsUserMuted(channelId, identity) != muted.Value)
                {
                    await TrySetUserMutedAsync(channelId, identity, muted.Value, sessionId, cancellationToken);
                }
                break;
            }
            case "room_finished":
            {
                var remaining = _voiceState.GetVoiceParticipants(channelId);
                var cutoffUtc = DateTime.UtcNow - TimeSpan.FromSeconds(5);
                foreach (var userId in remaining)
                {
                    var lastActivity = _voiceState.GetVoiceParticipantLastActivityUtc(channelId, userId);
                    if (lastActivity.HasValue && lastActivity.Value > cutoffUtc)
                    {
                        continue;
                    }

                    await LeaveVoiceAsync(channelId, userId, evictFromLiveKit: false, cancellationToken);
                }
                break;
            }
        }
    }

    /// <summary>
    /// Sends a call invite to the specified channel and returns a <see cref="SpokesResult{ChatMessage}"/> containing
    /// the created invite message entity or structured failure details (e.g. NOT_FOUND, FORBIDDEN).
    /// </summary>
    public virtual Task<SpokesResult<ChatMessage>> SendCallInviteAsync(string senderId, string channelId, CancellationToken cancellationToken = default)
    {
        var employee = _employees.GetById(senderId);
        if (employee == null)
        {
            _logger.LogWarning("SendCallInvite failed: Employee not found: {SenderId}", senderId);
            return Task.FromResult(SpokesResult<ChatMessage>.Failure($"User '{senderId}' not found.", "NOT_FOUND"));
        }

        if (!employee.IsActive || employee.IsSuspended || employee.IsBanned)
        {
            _logger.LogWarning("SendCallInvite failed: Employee {SenderId} is inactive, suspended, or banned", senderId);
            return Task.FromResult(SpokesResult<ChatMessage>.Failure("User is not authorized to start a voice call.", "FORBIDDEN"));
        }

        var channel = _channels.GetById(channelId);
        if (channel == null)
        {
            _logger.LogWarning("SendCallInvite failed: Channel not found: {ChannelId}", channelId);
            return Task.FromResult(SpokesResult<ChatMessage>.Failure($"Channel '{channelId}' not found.", "NOT_FOUND"));
        }

        if (_chatAuth != null && (!_chatAuth.CanUserAccessChannel(channel, employee) || !_chatAuth.CanUserPostToChannel(channel, employee)))
        {
            _logger.LogWarning("SendCallInvite failed: User {UserId} denied access to Channel {ChannelId}", employee.Id, channelId);
            return Task.FromResult(SpokesResult<ChatMessage>.Failure("User is not authorized to access or post to this channel.", "FORBIDDEN"));
        }

        var message = new ChatMessage
        {
            ChannelId = channelId,
            ChannelType = channel.ChannelType,
            SenderId = employee.Id,
            Content = $"🎙️ {employee.FullName} started a voice call.",
            MessageType = ChatMessageType.CallInvite,
            CallStatus = CallStatus.Active,
            SentAt = DateTime.UtcNow,
            ReadBy = new List<string> { employee.Id }
        };

        _messages.Save(message);
        _channels.UpdateLastActivity(channelId);
        _readStates.MarkAsRead(employee.Id, channelId, channel.ChannelType.ToString(), channel.IsVoiceChannel);

        _chatState.NotifyMessageReceived(message);

        _ = SafeDispatchNotificationAsync(() => _notificationRouting.RouteChatNotificationAsync(message, channel, employee), "call invite");

        if (_voiceBotService != null &&
            string.Equals(channel.ChannelType, ChatChannelType.Direct, StringComparison.OrdinalIgnoreCase) &&
            _voiceState.GetVoiceParticipants(channelId).Count <= 1)
        {
            var callerCallSound = employee.GetNotificationSound(NotificationCategories.Call);
            var ringbackFileName = NotificationSoundCatalog.ResolveCallRingbackFileName(callerCallSound);
            _ = SafeDispatchNotificationAsync(
                () => _voiceBotService.StartRingingAsync(channelId, ringbackFileName, TimeSpan.FromSeconds(60)),
                "caller ringback");
        }

        return Task.FromResult(SpokesResult<ChatMessage>.Success(message));
    }

    public virtual async Task UpdateCallInviteStatusAsync(
        string messageId,
        string status,
        CancellationToken cancellationToken = default,
        string? actorUserId = null)
    {
        var message = _messages.GetById(messageId);
        if (message == null || message.MessageType != ChatMessageType.CallInvite) return;

        if (_chatAuth != null)
        {
            if (string.IsNullOrWhiteSpace(actorUserId))
            {
                _logger?.LogWarning("UpdateCallInviteStatusAsync denied: missing actorUserId for message {MessageId}", messageId);
                return;
            }

            var channel = _channels?.GetById(message.ChannelId);
            var emp = _employees?.GetById(actorUserId);
            if (channel == null || emp == null || !emp.IsActive || emp.IsBanned || emp.IsSuspended || !_chatAuth.CanUserAccessChannel(channel, emp))
            {
                _logger?.LogWarning("UpdateCallInviteStatusAsync denied: User {UserId} is not authorized for channel {ChannelId}", actorUserId, message.ChannelId);
                return;
            }
        }

        await ApplyCallInviteStatusCoreAsync(message, status, cancellationToken);
    }

    private async Task ApplyCallInviteStatusCoreAsync(ChatMessage message, string status, CancellationToken cancellationToken)
    {
        message.CallStatus = status;
        _messages.Save(message);

        _chatState.NotifyMessageEdited(message);

        if (status == CallStatus.Ended || status == CallStatus.Declined || status == CallStatus.Joined)
        {
            if (_voiceBotService != null)
            {
                await _voiceBotService.StopRingingAsync(message.ChannelId);
            }

            _ = SafeDispatchNotificationAsync(() => _notificationRouting.ClearChannelNotificationsAsync(message.ChannelId), "clear channel notifications");
        }
    }

    internal Task SafeDispatchNotificationAsync(Func<Task> routeAction, string operation) =>
        NotificationDispatchHelper.SafeDispatchAsync(
            routeAction,
            operation,
            _logger,
            logMessageTemplate: "Failed to dispatch {Operation} notification in background");
}
