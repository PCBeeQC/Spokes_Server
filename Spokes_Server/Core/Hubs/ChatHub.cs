using Spokes_Server.Core.Services.Communication;
using Spokes_Server.Core.Services.Projects;
using Spokes_Server.Core.Services.Core;
using Microsoft.AspNetCore.SignalR;
using Spokes_Server.Core.Data.Repositories.Core;
using Spokes_Server.Core.Data.Repositories.Projects;
using Spokes_Server.Core.Data.Repositories.Accounting;
using Spokes_Server.Core.Data.Repositories.Communication;
using Spokes_Server.Core.Data.Repositories.HR;
using Spokes_Server.Core.Models.Core;
using Spokes_Server.Core.Models.Projects;
using Spokes_Server.Core.Models.Accounting;
using Spokes_Server.Core.Models.Communication;
using Spokes_Server.Core.Models.HR;
using Microsoft.Extensions.Logging;
namespace Spokes_Server.Core.Hubs;

using Microsoft.AspNetCore.Authorization;

/// <summary>
/// SignalR hub for real-time chat functionality.
/// Handles message broadcasting, typing indicators, and presence.
/// </summary>
[Authorize]
public class ChatHub : Hub
{
    private readonly ChatChannelRepository _channels;
    private readonly ChatMessageRepository _messages;
    private readonly ProjectRepository _projects;
    private readonly UserService _userService;
    private readonly ILogger<ChatHub> _logger;
    private readonly TeamRepository _teams;

    // Concurrent dictionary to track which channels a connection is subscribed to
    private static readonly Dictionary<string, HashSet<string>> _connectionChannels = new();
    private static readonly object _lock = new();

    public ChatHub(
        ChatChannelRepository channels,
        ChatMessageRepository messages,
        ProjectRepository projects,
        UserService userService,
        TeamRepository teams,
        ILogger<ChatHub> logger)
    {
        _channels = channels;
        _messages = messages;
        _projects = projects;
        _userService = userService;
        _teams = teams;
        _logger = logger;
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
        if (channel == null || !UserHasAccessToChannel(employee, channel)) return;

        await Groups.AddToGroupAsync(Context.ConnectionId, $"channel_{channelId}");

        lock (_lock)
        {
            if (!_connectionChannels.ContainsKey(Context.ConnectionId))
            {
                _connectionChannels[Context.ConnectionId] = new HashSet<string>();
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

            if (!UserHasAccessToChannel(employee, channel))
            {
                _logger.LogWarning("SendMessage failed: User {UserId} denied read access to Channel {ChannelId}", employee.Id, channelId);
                return;
            }

            if (!UserCanPostToChannel(employee, channel))
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
                ReadBy = new List<string> { employee.Id }
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
        if (channel == null || !UserHasAccessToChannel(employee, channel)) return;

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
        if (channel == null || !UserHasAccessToChannel(employee, channel)) return;

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
        if (message == null || message.SenderId != employee.Id) return;

        var channel = _channels.GetById(message.ChannelId);
        if (channel == null || !UserCanPostToChannel(employee, channel)) return;

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
        if (message == null || message.SenderId != employee.Id) return;

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
        if (channel == null || !UserHasAccessToChannel(employee, channel) || !UserCanPostToChannel(employee, channel)) return;

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
        if (channel == null || !UserHasAccessToChannel(employee, channel) || !UserCanPostToChannel(employee, channel)) return;

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

    private bool UserHasAccessToChannel(Employee employee, ChatChannel channel)
    {
        if (employee.IsAdmin) return true;

        if (channel.ChannelType == "General" || channel.ChannelType == ChatChannelType.General)
        {
            if (channel.IsDefaultGeneral) return true;
            bool isRestricted = channel.ParticipantIds.Any() || channel.AllowedTeamIds.Any();
            if (!isRestricted) return true; // Public general

            if (channel.ParticipantIds.Contains(employee.Id)) return true;
            if (employee.TeamId != null && channel.AllowedTeamIds.Contains(employee.TeamId)) return true;

            return false;
        }

        if (channel.ChannelType == "Direct" || channel.ChannelType == ChatChannelType.Direct)
        {
            return channel.ParticipantIds.Contains(employee.Id);
        }

        if (channel.ChannelType == "Team" || channel.ChannelType == ChatChannelType.Team)
        {
            return channel.LinkedEntityId == employee.TeamId;
        }

        if (channel.ChannelType == "Project" || channel.ChannelType == ChatChannelType.Project)
        {
            if (channel.LinkedEntityId == null) return false;
            var project = _projects.GetById(channel.LinkedEntityId);
            if (project == null) return false;

            if (project.AccessPolicy == "Public") return true;
            if (project.AllowedUserIds.Contains(employee.Id)) return true;
            if (employee.TeamId != null && project.AllowedTeamIds.Contains(employee.TeamId)) return true;

            return false;
        }

        return false;
    }

    private bool UserCanPostToChannel(Employee employee, ChatChannel channel)
    {
        if (!channel.IsAnnouncementOnly) return true;

        var userTeamIds = new System.Collections.Generic.List<string>();
        if (employee.TeamId != null) userTeamIds.Add(employee.TeamId);
        userTeamIds.AddRange(_teams.GetAll().Where(t => t.LeaderId == employee.Id).Select(t => t.Id));

        return _channels.EvaluateChannelPostAccessRule(channel, employee.Id, userTeamIds, employee.IsAdmin);
    }

    private Employee? GetCurrentEmployee()
    {
        return Context.User == null ? null : _userService.GetEmployee(Context.User);
    }
}



