using System.Threading.Tasks;
using Spokes_Server.Core.Models.Core;

namespace Spokes_Server.Core.Services.Communication.Notifications;

public interface IWebPushService
{
    string GetVapidPublicKey();
    bool IsConfigured { get; }
    PresenceTier DetermineNotificationTier(string userId);
    Task SendNotificationAsync(
        string userId,
        string title,
        string body,
        string? url = null,
        string? icon = null,
        PresenceTier? tier = null,
        string? tag = null,
        object[]? actions = null,
        string category = "chat",
        string? threadId = null,
        string? serverName = null,
        string? channelName = null,
        bool isGroupChat = false,
        int? badge = null,
        bool isSilent = false);
    Task SendClearNotificationAsync(string userId, string threadId);
    Task SendNotificationDirectAsync(
        string userId,
        string title,
        string body,
        string? url = null,
        string? icon = null,
        PresenceTier? tier = null,
        string? tag = null,
        object[]? actions = null,
        string category = "chat",
        string? threadId = null,
        string? serverName = null,
        string? channelName = null,
        bool isGroupChat = false,
        int? badge = null,
        bool isSilent = false);
    Task<bool> SendDeviceTestNotificationAsync(
        string subscriptionId,
        string userId,
        string title,
        string body,
        string? icon = null,
        string category = "chat");
}
