using System;

namespace Spokes_Server.Core.Models.Common;

/// <summary>
/// Default concrete implementation of IFileAttachment for general-purpose or single-file scenarios.
/// </summary>
public class FileAttachmentItem : IFileAttachment
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string FileName { get; set; } = string.Empty;
    public string FilePath { get; set; } = string.Empty;
    public string ContentType { get; set; } = string.Empty;
    public long FileSizeBytes { get; set; }
}
