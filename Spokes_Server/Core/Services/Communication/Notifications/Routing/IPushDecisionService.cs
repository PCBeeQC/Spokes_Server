using Spokes_Server.Core.Models.Core;

namespace Spokes_Server.Core.Services.Communication.Notifications.Routing;

/// <summary>
/// Evaluates presence and focus heuristics to determine push notification routing and tier delivery.
/// </summary>
public interface IPushDecisionService
{
    bool ShouldSuppressMobilePush(string userId);
    PresenceTier DetermineNotificationTier(string userId);
    bool IsUserInVoiceCall(string userId) => false;
    System.Collections.Generic.IReadOnlyCollection<string> GetFocusedSessionIds(string userId) => System.Array.Empty<string>();
}
