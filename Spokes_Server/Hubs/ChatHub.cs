using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Spokes_Server.Core.Data.Repositories.Communication;
using Spokes_Server.Core.Data.Repositories.HR;
using Spokes_Server.Core.Data.Repositories.Projects;
using Spokes_Server.Core.Models.Communication;
using Spokes_Server.Core.Models.HR;
using Spokes_Server.Core.Models.Projects;
using Spokes_Server.Core.Services.Communication.Chat;
using Spokes_Server.Core.Services.Identity;

namespace Spokes_Server.Hubs;

/// <summary>
/// SignalR hub for real-time chat functionality.
/// Handles message broadcasting, typing indicators, and presence.
/// </summary>
[Authorize]
public class ChatHub : Hub
{
    private readonly ChatChannelRepository _channels;
    private readonly UserService _userService;
    private readonly ILogger<ChatHub> _logger;
    private readonly IChatAuthorizationService _chatAuth;
    private readonly IChatService _chatService;

    [ActivatorUtilitiesConstructor]
    public ChatHub(
        ChatChannelRepository channels,
        UserService userService,
        ILogger<ChatHub> logger,
        IChatAuthorizationService chatAuth,
        IChatService chatService)
    {
        _channels = channels;
        _userService = userService;
        _logger = logger;
        _chatAuth = chatAuth;
        _chatService = chatService ?? throw new ArgumentNullException(nameof(chatService));
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
    /// Join a specific channel to receive messages.
    /// </summary>
    public async Task JoinChannelAsync(string channelId)
    {
        var employee = GetCurrentEmployee();
        if (employee == null)
        {
            throw new HubException("User is not authenticated.");
        }

        var channel = _channels.GetById(channelId);
        if (channel == null)
        {
            throw new HubException($"Channel '{channelId}' not found.");
        }

        if (!_chatAuth.CanUserAccessChannel(channel, employee))
        {
            throw new HubException($"User is not authorized to access channel '{channelId}'.");
        }

        await Groups.AddToGroupAsync(Context.ConnectionId, $"channel_{channelId}");
    }

    /// <summary>
    /// Leave a specific channel.
    /// </summary>
    public async Task LeaveChannelAsync(string channelId)
    {
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, $"channel_{channelId}");
    }

    /// <summary>
    /// Send a new message to a channel.
    /// The message will be saved and broadcast to all clients in the channel.
    /// </summary>
    public async Task SendMessageAsync(string channelId, string content, string? replyToId = null)
    {
        var employee = GetCurrentEmployee();
        if (employee == null)
        {
            _logger.LogWarning("SendMessage failed: Employee not found.");
            throw new HubException("User is not authenticated.");
        }

        var options = new SendMessageOptions
        {
            SenderId = employee.Id,
            ChannelId = channelId,
            Content = content,
            ReplyToId = replyToId
        };

        var result = await _chatService.SendMessageAsync(options);
        if (!result.IsSuccess)
        {
            throw new HubException(result.ErrorMessage ?? "Failed to save message to persistence: channel not found or access denied.");
        }
    }

    /// <summary>
    /// Signal that the current user is typing in a channel.
    /// </summary>
    public async Task StartTypingAsync(string channelId)
    {
        var employee = GetCurrentEmployee();
        if (employee == null) return;

        var channel = _channels.GetById(channelId);
        if (channel == null || !_chatAuth.CanUserAccessChannel(channel, employee)) return;

        await _chatService.StartTypingAsync(employee.Id, channelId);
    }

    /// <summary>
    /// Signal that the current user stopped typing.
    /// </summary>
    public async Task StopTypingAsync(string channelId)
    {
        var employee = GetCurrentEmployee();
        if (employee == null) return;

        var channel = _channels.GetById(channelId);
        if (channel == null || !_chatAuth.CanUserAccessChannel(channel, employee)) return;

        await _chatService.StopTypingAsync(employee.Id, channelId);
    }

    /// <summary>
    /// Edit an existing message.
    /// </summary>
    public async Task EditMessageAsync(string messageId, string newContent)
    {
        var employee = GetCurrentEmployee();
        if (employee == null)
        {
            throw new HubException("User is not authenticated.");
        }

        var result = await _chatService.EditMessageAsync(employee.Id, messageId, newContent);
        if (!result.IsSuccess)
        {
            throw new HubException(result.ErrorMessage ?? "Failed to edit message.");
        }
    }

    /// <summary>
    /// Delete a message (soft delete).
    /// </summary>
    public async Task DeleteMessageAsync(string messageId)
    {
        var employee = GetCurrentEmployee();
        if (employee == null)
        {
            throw new HubException("User is not authenticated.");
        }

        var result = await _chatService.DeleteMessageAsync(employee.Id, messageId);
        if (!result.IsSuccess)
        {
            throw new HubException(result.ErrorMessage ?? "Failed to delete message.");
        }
    }

    /// <summary>
    /// Add a reaction to a message.
    /// </summary>
    public async Task AddReactionAsync(string messageId, string emoji)
    {
        var employee = GetCurrentEmployee();
        if (employee == null)
        {
            throw new HubException("User is not authenticated.");
        }

        var result = await _chatService.AddReactionAsync(employee.Id, messageId, emoji);
        if (!result.IsSuccess)
        {
            throw new HubException(result.ErrorMessage ?? "Failed to add reaction.");
        }
    }

    /// <summary>
    /// Remove a reaction from a message.
    /// </summary>
    public async Task RemoveReactionAsync(string messageId, string emoji)
    {
        var employee = GetCurrentEmployee();
        if (employee == null)
        {
            throw new HubException("User is not authenticated.");
        }

        var result = await _chatService.RemoveReactionAsync(employee.Id, messageId, emoji);
        if (!result.IsSuccess)
        {
            throw new HubException(result.ErrorMessage ?? "Failed to remove reaction.");
        }
    }

    private Employee? GetCurrentEmployee()
    {
        return Context.User == null ? null : _userService.GetEmployee(Context.User);
    }
}
