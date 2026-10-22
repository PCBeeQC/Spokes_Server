namespace Spokes_Server.Core.Models.Communication;

public class EmailAttachmentMeta
{
    public string FileName { get; set; } = string.Empty;
    public string ContentType { get; set; } = string.Empty;
    public long Size { get; set; }
    public string Id { get; set; } = Guid.NewGuid().ToString(); // Used to retrieve file from Data/Attachments/Email/{MessageId}/{AttachmentId}
}
