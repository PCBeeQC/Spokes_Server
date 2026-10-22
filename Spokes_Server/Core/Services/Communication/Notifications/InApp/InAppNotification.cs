using Spokes_Server.Core.Models.Communication.Notifications;

namespace Spokes_Server.Core.Services.Communication.Notifications.InApp;

/// <summary>
/// Represents a normalized in-app notification payload to be presented to an active user session (e.g. snackbar and audio).
/// </summary>
public class InAppNotification
{
    public required string RecipientUserId { get; init; }
    public required string Title { get; init; }
    public required string Content { get; init; }
    public required string SenderName { get; init; }
    public string? SenderAvatarUrl { get; init; }
    public required string Initials { get; init; }
    public required string ChannelId { get; init; }
    public required string ChannelName { get; init; }
    public string? ServerName { get; init; }
    public bool IsReaction { get; init; }
    public bool IsGroupChat { get; init; }
    public string Category { get; init; } = NotificationCategories.Chat;
    public bool IsSilent { get; init; }

    /// <summary>
    /// Indicates whether this notification is for an end-to-end encrypted chat message.
    /// </summary>
    public bool IsEncrypted { get; init; }

    /// <summary>
    /// Masked fallback text (e.g., "🔒 New Secure Message") to show when the recipient's UI circuit is locked.
    /// </summary>
    public string? MaskedContent { get; init; }
}
