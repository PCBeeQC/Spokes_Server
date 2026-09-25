using Spokes_Server.Core.Models.Communication;

namespace Spokes_Server.Components.Pages.Chat;

/// <summary>
/// Tracks a file that has been selected/pasted but not yet sent with a message.
/// </summary>
public class PendingFile
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string FileName { get; set; } = "";
    public bool IsUploading { get; set; }
    public ChatAttachment? Attachment { get; set; }
    public double UploadProgress { get; set; }
}

