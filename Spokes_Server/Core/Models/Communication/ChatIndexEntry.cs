namespace Spokes_Server.Core.Models.Communication;

public class ChatIndexEntry
{
    public string Id { get; set; } = string.Empty;
    public string ChannelId { get; set; } = string.Empty;
    public DateTime SentAt { get; set; }
    public string SenderId { get; set; } = string.Empty;
    public bool IsDeleted { get; set; }
    public bool IsEncrypted { get; set; }

    /// <summary>
    /// Lowercase plain text for fast searching. 
    /// MUST BE NULL if IsEncrypted is true to prevent plaintext leaks.
    /// </summary>
    public string? SearchText { get; set; }
    public string? ReplyToId { get; set; }
}
