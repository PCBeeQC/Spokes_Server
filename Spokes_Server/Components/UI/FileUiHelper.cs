using System;
using System.IO;
using MudBlazor;
using Spokes_Server.Core.Helpers;

namespace Spokes_Server.Components.UI;

/// <summary>
/// UI-layer presentation helper resolving MudBlazor icon identifiers for file attachments.
/// Extracted from Core.Helpers to decouple domain models and core utilities from UI frameworks.
/// </summary>
public static class FileUiHelper
{
    public static string GetFileIcon(string? fileName, string? contentType = null)
    {
        if (!string.IsNullOrEmpty(contentType))
        {
            if (contentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase)) return Icons.Material.Filled.Image;
            if (contentType.StartsWith("video/", StringComparison.OrdinalIgnoreCase)) return Icons.Material.Filled.VideoFile;
            if (contentType.StartsWith("audio/", StringComparison.OrdinalIgnoreCase)) return Icons.Material.Filled.AudioFile;
            if (contentType.Equals("application/pdf", StringComparison.OrdinalIgnoreCase)) return Icons.Material.Filled.PictureAsPdf;
        }

        if (string.IsNullOrEmpty(fileName)) return Icons.Material.Filled.InsertDriveFile;

        if (FileHelper.IsImageFile(fileName)) return Icons.Material.Filled.Image;
        if (FileHelper.IsVideoFile(fileName)) return Icons.Material.Filled.VideoFile;
        if (FileHelper.IsAudioFile(fileName)) return Icons.Material.Filled.AudioFile;

        var ext = Path.GetExtension(fileName).ToLowerInvariant();
        return ext switch
        {
            ".pdf" => Icons.Material.Filled.PictureAsPdf,
            ".doc" or ".docx" or ".odt" or ".rtf" => Icons.Material.Filled.Description,
            ".txt" => Icons.Material.Filled.TextSnippet,
            ".xls" or ".xlsx" or ".csv" => Icons.Material.Filled.TableChart,
            ".zip" or ".rar" or ".7z" or ".tar" or ".gz" => Icons.Material.Filled.FolderZip,
            _ => Icons.Material.Filled.InsertDriveFile
        };
    }
}
