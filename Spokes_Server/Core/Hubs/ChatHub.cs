using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Spokes_Server.Core.Data.Repositories.Communication;
using Spokes_Server.Core.Data.Repositories.HR;
using Spokes_Server.Core.Data.Repositories.Projects;
using Spokes_Server.Core.Models.Communication;
using Spokes_Server.Core.Models.HR;
using Spokes_Server.Core.Models.Projects;
using Spokes_Server.Core.Services.Core;
using Spokes_Server.Core.Services.Communication.Chat;

namespace Spokes_Server.Core.Hubs;

/// <summary>
/// SignalR hub for real-time chat functionality.
/// Handles message broadcasting, typing indicators, and presence.
/// </summary>
[Authorize]
public class ChatHub : Hub
{
    private readonly ChatChannelRepository _channels;
    private readonly ChatMessageRepository _messages;
    private readonly UserService _userService;
    private readonly ILogger<ChatHub> _logger;
    private readonly IChatAuthorizationService _chatAuth;

    // Concurrent dictionary to track which channels a connection is subscribed to
    private static readonly Dictionary<string, HashSet<string>> _connectionChannels = new();
    private static readonly object _lock = new();

    public ChatHub(
        ChatChannelRepository channels,
        ChatMessageRepository messages,
        ProjectRepository projects,
        UserService userService,
        TeamRepository teams,
        ILogger<ChatHub> logger,
        IChatAuthorizationService? chatAuth = null)
    {
        _channels = channels;
        _messages = messages;
        _userService = userService;
        _logger = logger;
        _chatAuth = chatAuth ?? new ChatAuthorizationService(channels, projects, teams, null);
    }

    /// <summary>
    /// Called when a client connects to the hub.
    /// </summary>
    public override async Task OnConnectedAsync()
    {
        var employee = GetCurrentEmployee();
        if (employee != null)
        {
            // Add to user-specific group for direct notifications
            await Groups.AddToGroupAsync(Context.ConnectionId, $"user_{employee.Id}");
        }

        await base.OnConnectedAsync();
    }

    /// <summary>
    /// Called when a client disconnects from the hub.
    /// </summary>
    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        // Clean up channel subscriptions
        lock (_lock)
        {
            _connectionChannels.Remove(Context.ConnectionId);
        }

        await base.OnDisconnectedAsync(exception);
    }

    /// <summary>
    /// Join a specific channel to receive messages.
    /// </summary>
    public async Task JoinChannel(string channelId)
    {
        var employee = GetCurrentEmployee();
        if (employee == null) return;

        var channel = _channels.GetById(channelId);
        if (channel == null || !_chatAuth.CanUserAccessChannel(channel, employee)) return;

        await Groups.AddToGroupAsync(Context.ConnectionId, $"channel_{channelId}");

        lock (_lock)
        {
            if (!_connectionChannels.ContainsKey(Context.ConnectionId))
            {
                _connectionChannels[Context.ConnectionId] = [];
            }
            _connectionChannels[Context.ConnectionId].Add(channelId);
        }
    }

    /// <summary>
    /// Leave a specific channel.
    /// </summary>
    public async Task LeaveChannel(string channelId)
    {
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, $"channel_{channelId}");

        lock (_lock)
        {
            if (_connectionChannels.ContainsKey(Context.ConnectionId))
            {
                _connectionChannels[Context.ConnectionId].Remove(channelId);
            }
        }
    }

    /// <summary>
    /// Send a new message to a channel.
    /// The message will be saved and broadcast to all clients in the channel.
    /// </summary>
    public async Task SendMessage(string channelId, string content, string? replyToId = null)
    {
        try
        {
            var employee = GetCurrentEmployee();
            if (employee == null)
            {
                _logger.LogWarning("SendMessage failed: Employee not found.");
                return;
            }

            var channel = _channels.GetById(channelId);
            if (channel == null)
            {
                _logger.LogWarning("SendMessage failed: Channel not found: {ChannelId}", channelId);
                return;
            }

            if (!_chatAuth.CanUserAccessChannel(channel, employee))
            {
                _logger.LogWarning("SendMessage failed: User {UserId} denied read access to Channel {ChannelId}", employee.Id, channelId);
                return;
            }

            if (!_chatAuth.CanUserPostToChannel(channel, employee))
            {
                _logger.LogWarning("SendMessage failed: User {UserId} denied post access to Announcement Channel {ChannelId}", employee.Id, channelId);
                return;
            }

            var message = new ChatMessage
            {
                ChannelId = channelId,
                ChannelType = channel.ChannelType,
                SenderId = employee.Id,
                Content = content,
                ReplyToId = replyToId,
                SentAt = DateTime.UtcNow,
                ReadBy = [employee.Id]
            };

            // Save to database
            try
            {
                await _messages.SaveAsync(message);
                _logger.LogInformation("Message saved: {MessageId} in Channel: {ChannelId}", message.Id, channelId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to save message {MessageId}", message.Id);
            }

            // Update channel last activity
            await _channels.UpdateLastActivityAsync(channelId);

            // Auto-mark previous messages in this channel as read by the sender
            try
            {
                var recentMessages = _messages.GetByChannel(channelId, 50);
                foreach (var prevMsg in recentMessages)
                {
                    if (prevMsg.Id != message.Id && !prevMsg.ReadBy.Contains(employee.Id))
                    {
                        prevMsg.ReadBy.Add(employee.Id);
                        await _messages.SaveAsync(prevMsg);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to auto-mark previous messages as read for sender {SenderId} in channel {ChannelId}", employee.Id, channelId);
            }

            // Broadcast to channel
            await Clients.Group($"channel_{channelId}").SendAsync("ReceiveMessage", new
            {
                message.Id,
                message.ChannelId,
                message.SenderId,
                SenderName = employee.FullName,
                message.Content,
                message.SentAt,
                message.ReplyToId
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in SendMessage hub method");
        }
    }

    /// <summary>
    /// Signal that the current user is typing in a channel.
    /// </summary>
    public async Task StartTyping(string channelId)
    {
        var employee = GetCurrentEmployee();
        if (employee == null) return;

        var channel = _channels.GetById(channelId);
        if (channel == null || !_chatAuth.CanUserAccessChannel(channel, employee)) return;

        await Clients.OthersInGroup($"channel_{channelId}").SendAsync("UserTyping", new
        {
            ChannelId = channelId,
            UserId = employee.Id,
            UserName = employee.FullName
        });
    }

    /// <summary>
    /// Signal that the current user stopped typing.
    /// </summary>
    public async Task StopTyping(string channelId)
    {
        var employee = GetCurrentEmployee();
        if (employee == null) return;

        var channel = _channels.GetById(channelId);
        if (channel == null || !_chatAuth.CanUserAccessChannel(channel, employee)) return;

        await Clients.OthersInGroup($"channel_{channelId}").SendAsync("UserStoppedTyping", new
        {
            ChannelId = channelId,
            UserId = employee.Id
        });
    }

    /// <summary>
    /// Edit an existing message.
    /// </summary>
    public async Task EditMessage(string messageId, string newContent)
    {
        var employee = GetCurrentEmployee();
        if (employee == null) return;

        var message = _messages.GetById(messageId);
        if (message == null) return;

        var channel = _channels.GetById(message.ChannelId);
        if (channel == null || !_chatAuth.CanUserModifyMessage(message, channel, employee))
        {
            _logger.LogWarning("EditMessage failed: User {UserId} not authorized to edit message {MessageId}", employee.Id, messageId);
            return;
        }

        message.Content = newContent;
        message.EditedAt = DateTime.UtcNow;
        await _messages.SaveAsync(message);

        await Clients.Group($"channel_{message.ChannelId}").SendAsync("MessageEdited", new
        {
            message.Id,
            message.ChannelId,
            message.Content,
            message.EditedAt
        });
    }

    /// <summary>
    /// Delete a message (soft delete).
    /// </summary>
    public async Task DeleteMessage(string messageId)
    {
        var employee = GetCurrentEmployee();
        if (employee == null) return;

        var message = _messages.GetById(messageId);
        if (message == null) return;

        var channel = _channels.GetById(message.ChannelId);
        if (channel == null || !_chatAuth.CanUserDeleteMessage(message, channel, employee))
        {
            _logger.LogWarning("DeleteMessage failed: User {UserId} not authorized to delete message {MessageId}", employee.Id, messageId);
            return;
        }

        message.IsDeleted = true;
        await _messages.SaveAsync(message);

        await Clients.Group($"channel_{message.ChannelId}").SendAsync("MessageDeleted", new
        {
            message.Id,
            message.ChannelId
        });
    }

    /// <summary>
    /// Add a reaction to a message.
    /// </summary>
    public async Task AddReaction(string messageId, string emoji)
    {
        var employee = GetCurrentEmployee();
        if (employee == null) return;

        var message = _messages.GetById(messageId);
        if (message == null) return;

        var channel = _channels.GetById(message.ChannelId);
        if (channel == null || !_chatAuth.CanUserAccessChannel(channel, employee) || !_chatAuth.CanUserPostToChannel(channel, employee)) return;

        var reactionKey = $"{emoji}:{employee.Id}";
        if (!message.Reactions.Contains(reactionKey))
        {
            message.Reactions.Add(reactionKey);
            await _messages.SaveAsync(message);

            await Clients.Group($"channel_{message.ChannelId}").SendAsync("ReactionAdded", new
            {
                MessageId = message.Id,
                message.ChannelId,
                Emoji = emoji,
                UserId = employee.Id,
                UserName = employee.FullName
            });
        }
    }

    /// <summary>
    /// Remove a reaction from a message.
    /// </summary>
    public async Task RemoveReaction(string messageId, string emoji)
    {
        var employee = GetCurrentEmployee();
        if (employee == null) return;

        var message = _messages.GetById(messageId);
        if (message == null) return;

        var channel = _channels.GetById(message.ChannelId);
        if (channel == null || !_chatAuth.CanUserAccessChannel(channel, employee) || !_chatAuth.CanUserPostToChannel(channel, employee)) return;

        var reactionKey = $"{emoji}:{employee.Id}";
        if (message.Reactions.Contains(reactionKey))
        {
            message.Reactions.Remove(reactionKey);
            await _messages.SaveAsync(message);

            await Clients.Group($"channel_{message.ChannelId}").SendAsync("ReactionRemoved", new
            {
                MessageId = message.Id,
                message.ChannelId,
                Emoji = emoji,
                UserId = employee.Id
            });
        }
    }

    private Employee? GetCurrentEmployee()
    {
        return Context.User == null ? null : _userService.GetEmployee(Context.User);
    }
}
