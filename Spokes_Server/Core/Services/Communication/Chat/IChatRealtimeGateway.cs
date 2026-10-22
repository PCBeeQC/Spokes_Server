namespace Spokes_Server.Core.Services.Communication.Chat;

/// <summary>
/// Domain-level real-time gateway abstraction for broadcasting events to chat channels and users.
/// Decouples domain services from presentation SignalR Hub implementations.
/// </summary>
public interface IChatRealtimeGateway
{
    Task SendToChannelAsync(string channelId, string method, object? arg1, CancellationToken cancellationToken = default);
    Task SendToChannelAsync(string channelId, string method, object? arg1, object? arg2, CancellationToken cancellationToken = default);
    Task SendToChannelAsync(string channelId, string method, object? arg1, object? arg2, object? arg3, CancellationToken cancellationToken = default);
    Task SendToChannelAsync(string channelId, string method, object?[] args, CancellationToken cancellationToken = default);
    Task SendToUserAsync(string userId, string method, object? arg1, CancellationToken cancellationToken = default);
    Task SendToAllAsync(string method, object? arg1, CancellationToken cancellationToken = default);
}
