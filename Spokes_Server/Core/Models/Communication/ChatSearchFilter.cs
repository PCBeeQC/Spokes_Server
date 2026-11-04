namespace Spokes_Server.Core.Models.Communication;

using System;
using System.Collections.Generic;

/// <summary>
/// Represents parsed search criteria for chat messages.
/// </summary>
public class ChatSearchFilter
{
    public string RawQuery { get; set; } = string.Empty;

    /// <summary>
    /// Optional target channel ID. If null, indicates a multi-channel search across all channels accessible to the user.
    /// </summary>
    public string? ChannelId { get; set; }

    /// <summary>
    /// Free-text search terms to match against message content and attachment filenames.
    /// </summary>
    public List<string> TextTerms { get; set; } = [];

    /// <summary>
    /// Sender identifiers to match (Employee IDs, usernames, display names, or 'me').
    /// </summary>
    public List<string> SenderIdentifiers { get; set; } = [];

    /// <summary>
    /// Mentioned user identifiers to match.
    /// </summary>
    public List<string> MentionIdentifiers { get; set; } = [];

    /// <summary>
    /// True to require image attachments or GIFs; false to exclude images.
    /// </summary>
    public bool? HasImages { get; set; }

    /// <summary>
    /// True to require document/file attachments; false to exclude files.
    /// </summary>
    public bool? HasFiles { get; set; }

    /// <summary>
    /// True to require hyperlinks in content.
    /// </summary>
    public bool? HasLinks { get; set; }

    /// <summary>
    /// True to require reactions on the message.
    /// </summary>
    public bool? HasReactions { get; set; }

    /// <summary>
    /// Specific reaction emoji (e.g. "👍").
    /// </summary>
    public string? ReactionEmoji { get; set; }

    /// <summary>
    /// Filter messages sent on or before this timestamp.
    /// </summary>
    public DateTime? Before { get; set; }

    /// <summary>
    /// Filter messages sent on or after this timestamp.
    /// </summary>
    public DateTime? After { get; set; }

    public bool IsEmpty =>
        TextTerms.Count == 0 &&
        SenderIdentifiers.Count == 0 &&
        MentionIdentifiers.Count == 0 &&
        !HasImages.HasValue &&
        !HasFiles.HasValue &&
        !HasLinks.HasValue &&
        !HasReactions.HasValue &&
        string.IsNullOrEmpty(ReactionEmoji) &&
        !Before.HasValue &&
        !After.HasValue;
}
