using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Spokes_Server.Core.Models.Communication;
using Spokes_Server.Core.Helpers;

namespace Spokes_Server.Core.Services.Communication.Chat;

/// <summary>
/// Service interface defining chat messaging, channel lifecycle, typing, reactions, and read state operations.
/// </summary>
public interface IChatService
{
    Task<SpokesResult<ChatMessage>> SendMessageAsync(string senderId, string channelId, string content, string? replyToId = null);
    Task<SpokesResult<ChatMessage>> SendMessageAsync(string senderId, string channelId, string content, string? replyToId, CancellationToken cancellationToken);
    Task<SpokesResult<ChatMessage>> SendMessageAsync(SendMessageOptions options, CancellationToken cancellationToken = default);
    Task<SpokesResult> EditMessageAsync(string userId, string messageId, string newContent, string? requestingUserPrivateKey = null, CancellationToken cancellationToken = default);
    Task<SpokesResult> DeleteMessageAsync(string userId, string messageId, CancellationToken cancellationToken = default);
    Task<SpokesResult> ToggleReactionAsync(string userId, string messageId, string emoji, CancellationToken cancellationToken = default);
    Task<SpokesResult> AddReactionAsync(string userId, string messageId, string emoji, CancellationToken cancellationToken = default);
    Task<SpokesResult> RemoveReactionAsync(string userId, string messageId, string emoji, CancellationToken cancellationToken = default);
    Task MarkMessageAsReadAsync(string userId, string channelId, string messageId, CancellationToken cancellationToken = default);
    Task MarkChannelMessagesAsReadAsync(string userId, string channelId, int count = 50, CancellationToken cancellationToken = default);
    Task StartTypingAsync(string userId, string channelId, CancellationToken cancellationToken = default);
    Task StopTypingAsync(string userId, string channelId, CancellationToken cancellationToken = default);
    Task<SpokesResult> AdminDeleteMessageAsync(string messageId, CancellationToken cancellationToken = default);
    ChatChannel CreateDirectMessageChannel(string initiatorUserId, string targetUserId);
    ChatChannel CreateGroupDirectMessageChannel(string initiatorUserId, List<string> targetUserIds);
    ChatChannel CreateTeamChannel(string initiatorUserId, List<string> teamIds);
    ChatChannel CreateChannel(ChatChannel channel);
    void UpdateChannel(ChatChannel channel);
    SpokesResult UpdateChannel(ChatChannel channel, string? requestingUserId = null, bool isSessionUnlocked = true);
    SpokesResult DeleteChannel(string channelId, string? requestingUserId = null, bool isSessionUnlocked = true);
    SpokesResult ArchiveChannel(string channelId, bool archive, string? requestingUserId = null, bool isSessionUnlocked = true);
    void UpdateChannelOrders(Dictionary<string, int> channelOrders);
}
