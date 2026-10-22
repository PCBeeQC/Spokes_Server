using Spokes_Server.Core.Models.Core;

namespace Spokes_Server.Core.Services.Communication.Notifications.Routing;

public class PushNotificationRequest
{
    public required string UserId { get; set; }
    public required string Title { get; set; }
    public required string Body { get; set; }
    public string? Url { get; set; }
    public string? Icon { get; set; }
    public PresenceTier? Tier { get; set; }
    public string? Tag { get; set; }
    public object[]? Actions { get; set; }
    public string Category { get; set; } = "chat";
    public string? ThreadId { get; set; }
    public string? ServerName { get; set; }
    public string? ChannelName { get; set; }
    public bool IsGroupChat { get; set; }
    public int? Badge { get; set; }
    public bool IsSilent { get; set; }
    public string? Sound { get; set; }

    /// <summary>
    /// Indicates whether this push notification is for an end-to-end encrypted chat message.
    /// </summary>
    public bool IsEncrypted { get; set; }

    /// <summary>
    /// Masked fallback text (e.g., "🔒 New Secure Message") to deliver to devices that have not saved the vault cookie.
    /// </summary>
    public string? MaskedBody { get; set; }

    /// <summary>
    /// Resolves the effective body to send to a specific device subscription.
    /// If the message is encrypted and the device has not saved the vault cookie,
    /// returns the masked placeholder instead of decrypted content.
    /// </summary>
    public string GetEffectiveBodyForDevice(bool hasVaultCookie)
    {
        if (IsEncrypted && !hasVaultCookie)
        {
            return MaskedBody ?? "🔒 New Secure Message";
        }
        return Body;
    }
}
