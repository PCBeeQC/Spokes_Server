using System;
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
using Spokes_Server.Core.Services.Communication.Chat;
using Spokes_Server.Core.Services.Communication.Notifications;
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
    private readonly ChatStateService _chatState;
    private readonly IChatRealtimeGateway _realtimeGateway;
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

    /// <summary>
    /// Parameterless constructor for mocking / unit tests.
    /// </summary>
    protected VoiceChannelService()
    {
        _chatState = null!;
        _realtimeGateway = null!;
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
        ChatStateService chatState,
        IChatRealtimeGateway realtimeGateway,
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
        _chatState = chatState ?? throw new ArgumentNullException(nameof(chatState));
        _realtimeGateway = realtimeGateway ?? throw new ArgumentNullException(nameof(realtimeGateway));
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

    public virtual string GenerateJoinToken(string channelId, string userId, string userName, string userAvatarUrl = "")
    {
        if (_chatAuth != null)
        {
            var channel = _channels?.GetById(channelId);
            var emp = _employees?.GetById(userId);
            if (channel == null || emp == null || !emp.IsActive || emp.IsBanned || !_chatAuth.CanUserAccessChannel(channel, emp))
            {
                _logger?.LogWarning("GenerateJoinToken denied: User {UserId} is not authorized for channel {ChannelId}", userId, channelId);
                throw new UnauthorizedAccessException("User is not authorized to join this voice channel.");
            }
        }

        var config = _systemConfigs.Get();
        var apiKey = config.LiveKitApiKey ?? string.Empty;
        var apiSecret = config.LiveKitApiSecret ?? string.Empty;
        string roomName = $"channel-{channelId}";

        var token = new AccessToken(apiKey, apiSecret)
            .WithIdentity(userId)
            .WithName(userName)
            .WithMetadata(userAvatarUrl);

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

    public virtual string GenerateCameraCompanionToken(string channelId, string userId, string userName = "", string userAvatarUrl = "")
    {
        if (_chatAuth != null)
        {
            var channel = _channels?.GetById(channelId);
            var emp = _employees?.GetById(userId);
            if (channel == null || emp == null || !emp.IsActive || emp.IsBanned || !_chatAuth.CanUserAccessChannel(channel, emp))
            {
                _logger?.LogWarning("GenerateCameraCompanionToken denied: User {UserId} is not authorized for channel {ChannelId}", userId, channelId);
                throw new UnauthorizedAccessException("User is not authorized to join this voice channel.");
            }
            if (string.IsNullOrWhiteSpace(userName))
            {
                userName = emp.FullName ?? userId;
            }
        }

        var config = _systemConfigs.Get();
        var apiKey = config.LiveKitApiKey ?? string.Empty;
        var apiSecret = config.LiveKitApiSecret ?? string.Empty;
        string roomName = $"channel-{channelId}";
        string displayName = string.IsNullOrWhiteSpace(userName) ? $"{userId} (Camera)" : $"{userName} (Camera)";

        var token = new AccessToken(apiKey, apiSecret)
            .WithIdentity($"{userId}:cam")
            .WithName(displayName)
            .WithMetadata(userAvatarUrl);

        token.WithGrants(new VideoGrants
        {
            RoomJoin = true,
            Room = roomName,
            CanPublish = true,
            CanPublishData = false,
            CanSubscribe = true
        });

        return token.ToJwt();
    }

    public virtual async Task JoinVoiceAsync(string channelId, string userId, string? deviceId, string? deviceName, CancellationToken cancellationToken = default)
    {
        if (_chatAuth != null)
        {
            var channel = _channels?.GetById(channelId);
            var emp = _employees?.GetById(userId);
            if (channel == null || emp == null || !emp.IsActive || emp.IsBanned || !_chatAuth.CanUserAccessChannel(channel, emp))
            {
                _logger?.LogWarning("JoinVoiceAsync denied: User {UserId} is not authorized for channel {ChannelId}", userId, channelId);
                return;
            }
        }

        if (!string.IsNullOrWhiteSpace(deviceId))
        {
            _chatState.SetVoiceParticipantDevice(channelId, userId, deviceId, deviceName);
        }

        await JoinVoiceAsync(channelId, userId, cancellationToken);
    }

    public virtual async Task JoinVoiceAsync(string channelId, string userId, CancellationToken cancellationToken = default)
    {
        if (_chatAuth != null)
        {
            var channel = _channels?.GetById(channelId);
            var emp = _employees?.GetById(userId);
            if (channel == null || emp == null || !emp.IsActive || emp.IsBanned || !_chatAuth.CanUserAccessChannel(channel, emp))
            {
                _logger?.LogWarning("JoinVoiceAsync denied: User {UserId} is not authorized for channel {ChannelId}", userId, channelId);
                return;
            }
        }

        bool isNewMember = _chatState.NotifyVoiceMemberJoined(channelId, userId);
        if (isNewMember)
        {
            await _realtimeGateway.SendToChannelAsync(channelId, "VoiceMemberJoined", channelId, userId, cancellationToken);

            var currentParticipants = _chatState.GetVoiceParticipants(channelId);
            if (currentParticipants.Count > 1)
            {
                var activeInvite = _messages.GetByChannel(channelId, 50)
                    .FirstOrDefault(m => m.MessageType == ChatMessageType.CallInvite && m.CallStatus == CallStatus.Active);
                if (activeInvite != null)
                {
                    await ApplyCallInviteStatusCoreAsync(activeInvite, CallStatus.Joined, cancellationToken);
                }
            }
        }

        if (_voiceBotService != null)
        {
            try
            {
                await _voiceBotService.EnsureBotJoinedAsync(channelId, cancellationToken);
                await _voiceBotService.NotifyRoomConfigurationChangedAsync(channelId, cancellationToken);

                var participants = _chatState.GetVoiceParticipants(channelId);
                if (isNewMember && participants.Count > 1)
                {
                    _ = _voiceBotService.PlaySoundAsync(channelId, "channel_join", cancellationToken);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[VoiceChannelService] Failed to dispatch join sound via voice bot for channel {ChannelId}", channelId);
            }
        }
    }

    public virtual Task LeaveVoiceAsync(string channelId, string userId, CancellationToken cancellationToken = default) =>
        LeaveVoiceAsync(channelId, userId, evictFromLiveKit: true, cancellationToken);

    public virtual async Task LeaveVoiceAsync(string channelId, string userId, bool evictFromLiveKit, CancellationToken cancellationToken = default)
    {
        bool wasPresent = _chatState.NotifyVoiceMemberLeft(channelId, userId);
        if (!wasPresent)
        {
            if (evictFromLiveKit && !string.IsNullOrWhiteSpace(channelId) && !string.IsNullOrWhiteSpace(userId))
            {
                var cfg = _systemConfigs.Get();
                if (!string.IsNullOrEmpty(cfg.LiveKitApiKey) && !string.IsNullOrEmpty(cfg.LiveKitApiSecret))
                {
                    await _liveKitService.RemoveParticipantAsync(channelId, userId, cfg.LiveKitApiKey, cfg.LiveKitApiSecret, cancellationToken);
                    await _liveKitService.RemoveParticipantAsync(channelId, $"{userId}:cam", cfg.LiveKitApiKey, cfg.LiveKitApiSecret, cancellationToken);
                }
            }
            return;
        }

        var participants = _chatState.GetVoiceParticipants(channelId);
        if (participants.Count == 0)
        {
            var activeInvite = _messages.GetByChannel(channelId, 50)
                .FirstOrDefault(m => m.MessageType == ChatMessageType.CallInvite && (m.CallStatus == CallStatus.Active || m.CallStatus == CallStatus.Joined));
            if (activeInvite != null)
            {
                await ApplyCallInviteStatusCoreAsync(activeInvite, CallStatus.Ended, cancellationToken);
            }

            if (_voiceBotService != null)
            {
                try
                {
                    await _voiceBotService.NotifyRoomConfigurationChangedAsync(channelId, cancellationToken);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "[VoiceChannelService] Failed to notify bot pipeline on empty channel {ChannelId}", channelId);
                }
                _voiceBotService.ScheduleBotDeparture(channelId, TimeSpan.FromSeconds(30));
            }
        }
        else
        {
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
                _ = _voiceBotService.PlaySoundAsync(channelId, "channel_leave", cancellationToken);
            }
        }

        if (evictFromLiveKit)
        {
            // Evict participant from LiveKit via LiveKitService
            var config = _systemConfigs.Get();
            if (!string.IsNullOrEmpty(config.LiveKitApiKey) && !string.IsNullOrEmpty(config.LiveKitApiSecret))
            {
                await _liveKitService.RemoveParticipantAsync(channelId, userId, config.LiveKitApiKey, config.LiveKitApiSecret, cancellationToken);
                await _liveKitService.RemoveParticipantAsync(channelId, $"{userId}:cam", config.LiveKitApiKey, config.LiveKitApiSecret, cancellationToken);
            }
        }

        await _realtimeGateway.SendToChannelAsync(channelId, "VoiceMemberLeft", channelId, userId, cancellationToken);
    }

    public virtual async Task UpdateActiveSpeakersAsync(string channelId, string[] speakerIds, CancellationToken cancellationToken = default)
    {
        _chatState.NotifyVoiceActiveSpeakersChanged(channelId, speakerIds);
        await _realtimeGateway.SendToChannelAsync(channelId, "VoiceActiveSpeakersChanged", channelId, speakerIds, cancellationToken);
    }

    public virtual async Task SetUserSpeakingAsync(string channelId, string userId, bool isSpeaking, CancellationToken cancellationToken = default)
    {
        _chatState.SetUserSpeaking(channelId, userId, isSpeaking);
        var currentSpeakers = _chatState.GetActiveSpeakers(channelId);
        await _realtimeGateway.SendToChannelAsync(channelId, "VoiceActiveSpeakersChanged", channelId, currentSpeakers, cancellationToken);
    }

    public virtual async Task SetUserMutedAsync(string channelId, string userId, bool isMuted, CancellationToken cancellationToken = default)
    {
        _chatState.SetUserMuted(channelId, userId, isMuted);
        await _realtimeGateway.SendToChannelAsync(channelId, "VoiceUserMutedChanged", channelId, userId, isMuted, cancellationToken);
    }

    /// <summary>
    /// Sends a call invite to the specified channel and returns a <see cref="SpokesResult{ChatMessage}"/> containing
    /// the created invite message entity or structured failure details (e.g. NOT_FOUND, FORBIDDEN).
    /// </summary>
    public virtual async Task<SpokesResult<ChatMessage>> SendCallInviteAsync(string senderId, string channelId, CancellationToken cancellationToken = default)
    {
        var employee = _employees.GetById(senderId);
        if (employee == null)
        {
            _logger.LogWarning("SendCallInvite failed: Employee not found: {SenderId}", senderId);
            return SpokesResult<ChatMessage>.Failure($"User '{senderId}' not found.", "NOT_FOUND");
        }

        if (!employee.IsActive || employee.IsSuspended || employee.IsBanned)
        {
            _logger.LogWarning("SendCallInvite failed: Employee {SenderId} is inactive, suspended, or banned", senderId);
            return SpokesResult<ChatMessage>.Failure("User is not authorized to start a voice call.", "FORBIDDEN");
        }

        var channel = _channels.GetById(channelId);
        if (channel == null)
        {
            _logger.LogWarning("SendCallInvite failed: Channel not found: {ChannelId}", channelId);
            return SpokesResult<ChatMessage>.Failure($"Channel '{channelId}' not found.", "NOT_FOUND");
        }

        if (_chatAuth != null && (!_chatAuth.CanUserAccessChannel(channel, employee) || !_chatAuth.CanUserPostToChannel(channel, employee)))
        {
            _logger.LogWarning("SendCallInvite failed: User {UserId} denied access to Channel {ChannelId}", employee.Id, channelId);
            return SpokesResult<ChatMessage>.Failure("User is not authorized to access or post to this channel.", "FORBIDDEN");
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

        await _realtimeGateway.SendToChannelAsync(channelId, "ReceiveMessage", new
        {
            Id = message.Id,
            MessageId = message.Id,
            message.ChannelId,
            message.SenderId,
            UserId = message.SenderId,
            SenderName = employee.FullName,
            message.Content,
            message.SentAt,
            message.ReplyToId,
            Attachments = new List<object>()
        }, cancellationToken);

        _ = SafeDispatchNotificationAsync(() => _notificationRouting.RouteChatNotificationAsync(message, channel, employee), "call invite");

        return SpokesResult<ChatMessage>.Success(message);
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

        await _realtimeGateway.SendToChannelAsync(message.ChannelId, "MessageEdited", new
        {
            message.Id,
            message.ChannelId,
            message.Content,
            message.EditedAt,
            message.CallStatus
        }, cancellationToken);

        if (status == CallStatus.Ended || status == CallStatus.Declined || status == CallStatus.Joined)
        {
            _ = SafeDispatchNotificationAsync(() => _notificationRouting.ClearChannelNotificationsAsync(message.ChannelId), "clear channel notifications");
        }
    }

    internal Task SafeDispatchNotificationAsync(Func<Task> routeAction, string operation)
    {
        return Task.Run(async () =>
        {
            try
            {
                await routeAction();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to dispatch {Operation} notification in background", operation);
            }
        });
    }
}
