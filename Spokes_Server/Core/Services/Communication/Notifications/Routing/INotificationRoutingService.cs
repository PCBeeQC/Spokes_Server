using System.Threading.Tasks;
using Spokes_Server.Core.Models.Communication;
using Spokes_Server.Core.Models.Communication.Notifications;
using Spokes_Server.Core.Models.HR;

namespace Spokes_Server.Core.Services.Communication.Notifications.Routing;

/// <summary>
/// Service interface defining centralized routing for chat, email, and moderation notifications and badge tracking.
/// </summary>
public interface INotificationRoutingService
{
    int GetTotalBadgeCount(string userId);
    BadgeBreakdownResult GetBadgeBreakdown(string userId);
    Task RouteChatNotificationAsync(ChatMessage message, ChatChannel channel, Employee sender);
    Task ClearChannelNotificationsAsync(string channelId);
    Task RouteReactionNotificationAsync(ChatMessage message, ChatChannel channel, string emoji, Employee reactionSender);
    Task RouteEmailNotificationAsync(EmailMessage emailObj);
    Task RouteModerationNotificationAsync(ReportedMessage report);
}
