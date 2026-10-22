using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.SignalR;
using Spokes_Server.Core.Services.Communication.Chat;

namespace Spokes_Server.Hubs;

/// <summary>
/// SignalR-based implementation of <see cref="IChatRealtimeGateway"/> using <see cref="ChatHub"/>.
/// Lives in the presentation Hubs layer to keep domain services decoupled from SignalR hubs.
/// </summary>
public class ChatRealtimeGateway : IChatRealtimeGateway
{
    private readonly IHubContext<ChatHub> _hubContext;

    public ChatRealtimeGateway(IHubContext<ChatHub> hubContext)
    {
        _hubContext = hubContext;
    }

    public Task SendToChannelAsync(string channelId, string method, object? arg1, CancellationToken cancellationToken = default)
    {
        return _hubContext.Clients.Group($"channel_{channelId}").SendAsync(method, arg1, cancellationToken);
    }

    public Task SendToChannelAsync(string channelId, string method, object? arg1, object? arg2, CancellationToken cancellationToken = default)
    {
        return _hubContext.Clients.Group($"channel_{channelId}").SendAsync(method, arg1, arg2, cancellationToken);
    }

    public Task SendToChannelAsync(string channelId, string method, object? arg1, object? arg2, object? arg3, CancellationToken cancellationToken = default)
    {
        return _hubContext.Clients.Group($"channel_{channelId}").SendAsync(method, arg1, arg2, arg3, cancellationToken);
    }

    public Task SendToChannelAsync(string channelId, string method, object?[] args, CancellationToken cancellationToken = default)
    {
        return _hubContext.Clients.Group($"channel_{channelId}").SendCoreAsync(method, args, cancellationToken);
    }

    public Task SendToUserAsync(string userId, string method, object? arg1, CancellationToken cancellationToken = default)
    {
        return _hubContext.Clients.User(userId).SendAsync(method, arg1, cancellationToken);
    }

    public Task SendToAllAsync(string method, object? arg1, CancellationToken cancellationToken = default)
    {
        return _hubContext.Clients.All.SendAsync(method, arg1, cancellationToken);
    }
}
