using System.IO;
using MudBlazor;

namespace Spokes_Server.Core.Helpers;

public static class FileHelper
{
    public static bool IsImageFile(string fileName)
    {
        if (string.IsNullOrEmpty(fileName)) return false;
        var ext = Path.GetExtension(fileName).ToLowerInvariant();
        return ext is ".jpg" or ".jpeg" or ".png" or ".gif" or ".webp" or ".bmp";
    }

    public static bool IsVideoFile(string fileName)
    {
        if (string.IsNullOrEmpty(fileName)) return false;
        var ext = Path.GetExtension(fileName).ToLower();
        return ext == ".mp4" || ext == ".webm" || ext == ".mov" || ext == ".ogg" || ext == ".avi" || ext == ".mkv";
    }

    public static bool IsAudioFile(string fileName)
    {
        if (string.IsNullOrEmpty(fileName)) return false;
        var ext = Path.GetExtension(fileName).ToLower();
        return ext == ".mp3" || ext == ".wav" || ext == ".webm" || ext == ".weba" || ext == ".ogg" || ext == ".m4a" || ext == ".aac";
    }

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
