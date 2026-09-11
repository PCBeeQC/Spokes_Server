using System.Collections.Generic;

namespace Spokes_Server.Core.Models.Communication;

public class SendMessageOptions
{
    public string SenderId { get; set; } = string.Empty;
    public string ChannelId { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
    public List<ChatAttachment>? Attachments { get; set; }
    public string? ReplyToId { get; set; }
    public string? PrivateKey { get; set; }
}
