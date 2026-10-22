using System.Threading.Tasks;
using Spokes_Server.Core.Models.Core;
using Spokes_Server.Core.Services.Communication.Notifications;
using Spokes_Server.Core.Services.Communication.Notifications.Routing;

namespace Spokes_Server.Core.Services.Communication.Notifications.WebPush;

public interface IWebPushService
{
    string GetVapidPublicKey();
    bool IsConfigured { get; }
    Task SendNotificationAsync(PushNotificationRequest request);
    Task SendNotificationAsync(PushNotificationRequest request, CancellationToken cancellationToken);
    Task SendNotificationDirectAsync(PushNotificationRequest request);
    Task SendNotificationDirectAsync(PushNotificationRequest request, CancellationToken cancellationToken);

    Task SendClearNotificationAsync(string userId, string threadId);
    Task SendClearNotificationAsync(string userId, string threadId, CancellationToken cancellationToken);

    Task<TestNotificationResult> SendDeviceTestNotificationAsync(
        DeviceTestNotificationRequest request,
        CancellationToken cancellationToken = default);

    bool IsNativePushAllowed();
    NativePushAvailability GetNativePushStatus();
}

public class DeviceTestNotificationRequest
{
    public string SubscriptionId { get; set; } = string.Empty;
    public string UserId { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Body { get; set; } = string.Empty;
    public string? Icon { get; set; }
    public string Category { get; set; } = "chat";
}

public record NativePushAvailability(bool IsAvailable, string? Status, string? Message);

public record TestNotificationResult(bool IsSuccess, bool IsPayloadEncrypted = false, string? Message = null)
{
    public static TestNotificationResult Success(bool isPayloadEncrypted = false, string? message = null) =>
        new(true, isPayloadEncrypted, message);

    public static TestNotificationResult Failure(string message) =>
        new(false, false, message);
}
