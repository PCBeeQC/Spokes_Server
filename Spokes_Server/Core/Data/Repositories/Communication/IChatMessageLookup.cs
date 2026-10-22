namespace Spokes_Server.Core.Data.Repositories.Communication;

using Spokes_Server.Core.Models.Communication;

/// <summary>
/// Abstraction for looking up cached chat messages without coupling directly to ChatMessageRepository.
/// </summary>
public interface IChatMessageLookup
{
    ChatMessage? GetCachedMessage(string messageId);
}
