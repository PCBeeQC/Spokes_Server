using Spokes_Server.Core.Models.Communication;

namespace Spokes_Server.Core.Services.Communication.Chat;

/// <summary>
/// Domain service interface for querying channel memberships and accessible channels for users.
/// </summary>
public interface IChatChannelAccessService
{
    List<ChatChannel> GetChannelsForUser(string userId);
    List<string> GetUsersForChannel(string channelId);
}
