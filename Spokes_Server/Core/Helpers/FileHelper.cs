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
        var ext = Path.GetExtension(fileName).ToLowerInvariant();
        return ext is ".mp4" or ".webm" or ".mov" or ".ogg" or ".avi" or ".mkv";
    }

    public static bool IsAudioFile(string fileName)
    {
        if (string.IsNullOrEmpty(fileName)) return false;
        var ext = Path.GetExtension(fileName).ToLowerInvariant();
        return ext is ".mp3" or ".wav" or ".webm" or ".weba" or ".ogg" or ".m4a" or ".aac";
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
