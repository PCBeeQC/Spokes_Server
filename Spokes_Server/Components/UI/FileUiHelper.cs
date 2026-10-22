using System.IO;
using MudBlazor;

namespace Spokes_Server.Components.UI;

/// <summary>
/// UI-layer presentation helper resolving MudBlazor icon identifiers for file attachments.
/// Extracted from Core.Helpers to decouple domain models and core utilities from UI frameworks.
/// </summary>
public static class FileUiHelper
{
    public static string GetFileIcon(string fileName)
    {
        if (string.IsNullOrEmpty(fileName)) return Icons.Material.Filled.InsertDriveFile;
        var ext = Path.GetExtension(fileName).ToLowerInvariant();
        return ext switch
        {
            ".jpg" or ".jpeg" or ".png" or ".gif" or ".webp" or ".bmp" => Icons.Material.Filled.Image,
            ".pdf" => Icons.Material.Filled.PictureAsPdf,
            ".doc" or ".docx" => Icons.Material.Filled.Description,
            ".xls" or ".xlsx" => Icons.Material.Filled.TableChart,
            ".zip" or ".rar" or ".7z" => Icons.Material.Filled.FolderZip,
            ".mp4" or ".webm" or ".ogg" or ".mov" => Icons.Material.Filled.VideoFile,
            ".txt" => Icons.Material.Filled.TextSnippet,
            _ => Icons.Material.Filled.InsertDriveFile
        };
    }
}
