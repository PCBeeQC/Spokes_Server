namespace Spokes_Server.Core.Models.Communication;

using System;
using System.Collections.Generic;

/// <summary>
/// Lightweight in-memory summary of a channel's latest message used for sidebar rendering
/// without loading full message histories from disk.
/// </summary>
public class ChannelPreviewSummary
{
    public string MessageId { get; set; } = string.Empty;
    public string ChannelId { get; set; } = string.Empty;
    public string SenderId { get; set; } = string.Empty;
    public DateTime SentAt { get; set; }
    public string? PreviewText { get; set; }
    public bool IsEncrypted { get; set; }
    public string? EncryptedContent { get; set; }
    public List<ChatAttachment>? Attachments { get; set; }
}
