using System;
using System.IO;
using Microsoft.AspNetCore.StaticFiles;
using MudBlazor;

namespace Spokes_Server.Core.Helpers;

public static class FileHelper
{
    private static readonly FileExtensionContentTypeProvider _contentTypeProvider = new();

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

    public static bool IsMediaFile(string? fileName, string? contentType = null)
    {
        if (!string.IsNullOrEmpty(contentType))
        {
            if (contentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase) ||
                contentType.StartsWith("video/", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        if (!string.IsNullOrEmpty(fileName))
        {
            return IsImageFile(fileName) || IsVideoFile(fileName);
        }

        return false;
    }

    public static string GetContentType(string fileName, string? fallbackContentType = null)
    {
        if (!string.IsNullOrWhiteSpace(fallbackContentType) && fallbackContentType != "application/octet-stream")
        {
            return fallbackContentType;
        }

        if (!string.IsNullOrEmpty(fileName) && _contentTypeProvider.TryGetContentType(fileName, out var contentType))
        {
            return contentType;
        }

        return fallbackContentType ?? "application/octet-stream";
    }

    public static string GetFileIcon(string fileName, string? contentType = null)
    {
        if (!string.IsNullOrEmpty(contentType))
        {
            if (contentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase)) return Icons.Material.Filled.Image;
            if (contentType.StartsWith("video/", StringComparison.OrdinalIgnoreCase)) return Icons.Material.Filled.Videocam;
            if (contentType.StartsWith("audio/", StringComparison.OrdinalIgnoreCase)) return Icons.Material.Filled.AudioFile;
            if (contentType.Equals("application/pdf", StringComparison.OrdinalIgnoreCase)) return Icons.Material.Filled.PictureAsPdf;
        }

        if (IsImageFile(fileName)) return Icons.Material.Filled.Image;
        if (IsVideoFile(fileName)) return Icons.Material.Filled.Videocam;
        if (IsAudioFile(fileName)) return Icons.Material.Filled.AudioFile;

        var ext = Path.GetExtension(fileName).ToLowerInvariant();
        if (ext == ".pdf") return Icons.Material.Filled.PictureAsPdf;
        if (ext is ".doc" or ".docx" or ".odt" or ".rtf" or ".txt") return Icons.Material.Filled.Description;
        if (ext is ".xls" or ".xlsx" or ".csv") return Icons.Material.Filled.TableChart;
        if (ext is ".zip" or ".rar" or ".7z" or ".tar" or ".gz") return Icons.Material.Filled.FolderZip;

        return Icons.Material.Filled.AttachFile;
    }

    public static string FormatBytes(long bytes)
    {
        if (bytes <= 0) return "0 B";
        string[] suffixes = ["B", "KB", "MB", "GB", "TB"];
        int i = 0;
        double d = bytes;
        while (d >= 1024 && i < suffixes.Length - 1)
        {
            d /= 1024;
            i++;
        }
        return $"{d:0.#} {suffixes[i]}";
    }
}
