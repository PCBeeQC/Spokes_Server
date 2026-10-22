using System;
using System.Collections.Generic;
using Spokes_Server.Core.Data;

namespace Spokes_Server.Core.Models.Common;

/// <summary>
/// Standard contract for file attachments displayed and managed in UI components.
/// Automatically satisfies IHasAssociatedFiles for deletion cascades.
/// </summary>
public interface IFileAttachment : IHasAssociatedFiles
{
    string Id { get; set; }
    string FileName { get; set; }
    string FilePath { get; set; }
    string ContentType { get; set; }
    long FileSizeBytes { get; set; }

    IEnumerable<string> IHasAssociatedFiles.GetAssociatedFilePaths() =>
        string.IsNullOrWhiteSpace(FilePath) ? Array.Empty<string>() : new[] { FilePath };
}
