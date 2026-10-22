using Spokes_Server.Core.Models.Core;

namespace Spokes_Server.Core.Services.Communication.Notifications.Routing;

/// <summary>
/// Evaluates presence and focus heuristics to determine push notification routing and tier delivery.
/// </summary>
public interface IPushDecisionService
{
    bool IsUserFocusedForPush(string userId);
    bool HasActiveDesktopConnectionForPush(string userId);
    bool IsUserAwayFromDesktopForPush(string userId);
    bool WasRecentlySeenOnDesktopForPush(string userId);
    bool ShouldSuppressMobilePush(string userId);
    PresenceTier DetermineNotificationTier(string userId);
}
