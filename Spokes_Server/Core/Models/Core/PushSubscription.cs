namespace Spokes_Server.Core.Models.Core;

using Spokes_Server.Core.Models.Projects;
using Spokes_Server.Core.Models.Accounting;
using Spokes_Server.Core.Models.Communication;
using Spokes_Server.Core.Models.HR;

using Spokes_Server.Core.Data;

/// <summary>
/// Represents a Web Push subscription for a user's browser.
/// Stores the endpoint and encryption keys needed to send push notifications.
/// </summary>
public class PushSubscription : IDataEntity
{
    public string Id { get; set; } = Guid.NewGuid().ToString();

    public override bool Equals(object? obj)
    {
        if (ReferenceEquals(this, obj)) return true;
        if (obj == null || GetType() != obj.GetType())
            return false;
        var other = (PushSubscription)obj;
        return Id == other.Id;
    }

    public override int GetHashCode() => Id?.GetHashCode() ?? base.GetHashCode();

    /// <summary>The persistent ID of the physical device/browser installation</summary>
    public string? DeviceId { get; set; }

    /// <summary>The ID of the authenticated DeviceSession this push subscription is linked to</summary>
    public string? SessionId { get; set; }

    /// <summary>User who owns this subscription</summary>
    public string UserId { get; set; } = string.Empty;

    /// <summary>Push service endpoint URL</summary>
    public string Endpoint { get; set; } = string.Empty;

    /// <summary>P256DH key for encryption</summary>
    public string P256dh { get; set; } = string.Empty;

    /// <summary>Auth secret for encryption</summary>
    public string Auth { get; set; } = string.Empty;

    /// <summary>When this subscription was created</summary>
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>User agent string for debugging</summary>
    public string UserAgent { get; set; } = string.Empty;

    /// <summary>Device type: "Desktop" or "Mobile"</summary>
    public string DeviceType { get; set; } = "Desktop";

    /// <summary>User-provided friendly name (e.g., "Work Laptop", "iPhone")</summary>
    public string DeviceName { get; set; } = string.Empty;

    /// <summary>Whether notifications are enabled for this device</summary>
    public bool IsEnabled { get; set; } = true;

    /// <summary>Whether this device uses the Idle API to report deep physical activity.</summary>
    public bool IsIdleDetectionEnabled { get; set; } = true;

    /// <summary>Type of subscription: "WebPush" or "NativeRelay"</summary>
    public string SubscriptionType { get; set; } = "WebPush";

    /// <summary>RSA Public Key for NativeRelay E2EE payloads</summary>
    public string PublicKey { get; set; } = string.Empty;
}



