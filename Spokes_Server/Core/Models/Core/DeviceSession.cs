namespace Spokes_Server.Core.Models.Core;

using Spokes_Server.Core.Data;

/// <summary>
/// Represents a persistent device session (Refresh Token) for an Employee.
/// </summary>
public class DeviceSession : IDataEntity
{
    public string Id { get; set; } = Guid.NewGuid().ToString();

    public override bool Equals(object? obj)
    {
        if (ReferenceEquals(this, obj)) return true;
        if (obj == null || GetType() != obj.GetType())
            return false;
        var other = (DeviceSession)obj;
        return Id == other.Id;
    }

    public override int GetHashCode() => Id?.GetHashCode() ?? base.GetHashCode();

    /// <summary>
    /// The persistent ID of the physical device/browser installation.
    /// </summary>
    public string? DeviceId { get; set; }

    /// <summary>
    /// The ID of the employee this session belongs to.
    /// </summary>
    public string EmployeeId { get; set; } = string.Empty;

    /// <summary>
    /// A cryptographic hash of the refresh token. 
    /// The plain text token is only ever given to the client as a cookie.
    /// </summary>
    public string TokenHash { get; set; } = string.Empty;

    /// <summary>
    /// Identifier for the device/browser used (e.g. User-Agent).
    /// </summary>
    public string DeviceInfo { get; set; } = string.Empty;

    /// <summary>
    /// When this session was created.
    /// </summary>
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// When this refresh token is set to expire (e.g., 90 days from creation/renewal).
    /// </summary>
    public DateTime ExpiresAt { get; set; } = DateTime.UtcNow.AddDays(90);

    /// <summary>
    /// When the device was last seen online.
    /// </summary>
    public DateTime LastSeenAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// If not null, this session has been explicitly revoked and should not be trusted.
    /// </summary>
    public DateTime? RevokedAt { get; set; }

    /// <summary>
    /// Whether this session has saved the vault password locally via a cookie.
    /// </summary>
    public bool HasVaultCookie { get; set; }

    /// <summary>
    /// The IdToken used for OIDC logout (stored here to prevent massive chunked cookies on mobile).
    /// </summary>
    public string? IdToken { get; set; }

    /// <summary>
    /// Serialized AuthenticationTicket (Base64). Stored server-side by DeviceSessionTicketStore
    /// so the browser cookie contains only a stable session reference key.
    /// </summary>
    public string? TicketData { get; set; }

    // --- Push Notification Fields (merged from PushSubscription) ---

    /// <summary>Push service endpoint URL (Web Push URL or FCM token). Null if push not enabled.</summary>
    public string? PushEndpoint { get; set; }

    /// <summary>P256DH key for Web Push encryption.</summary>
    public string PushP256dh { get; set; } = string.Empty;

    /// <summary>Auth secret for Web Push encryption.</summary>
    public string PushAuth { get; set; } = string.Empty;

    /// <summary>Type of subscription: "WebPush" or "NativeRelay".</summary>
    public string PushSubscriptionType { get; set; } = "WebPush";

    /// <summary>RSA Public Key for NativeRelay E2EE payloads.</summary>
    public string PushPublicKey { get; set; } = string.Empty;

    /// <summary>Whether push notifications are enabled for this device.</summary>
    public bool PushEnabled { get; set; }

    /// <summary>Whether this device uses the Idle API to report physical activity.</summary>
    public bool IsIdleDetectionEnabled { get; set; }

    /// <summary>When push was enabled on this device.</summary>
    public DateTime? PushSubscribedAt { get; set; }

    /// <summary>Device type: "Desktop" or "Mobile".</summary>
    public string DeviceType { get; set; } = "Desktop";

    /// <summary>User-provided friendly name (e.g., "Work Laptop", "iPhone").</summary>
    public string DeviceName { get; set; } = string.Empty;

    /// <summary>User agent string captured during push subscription (for debugging).</summary>
    public string PushUserAgent { get; set; } = string.Empty;

    // --- Computed helpers ---

    /// <summary>Whether this device has an active push subscription.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool HasPush => !string.IsNullOrEmpty(PushEndpoint);
}
