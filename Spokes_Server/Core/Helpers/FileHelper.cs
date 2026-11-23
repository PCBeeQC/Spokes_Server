using System;
using System.IO;
using Microsoft.AspNetCore.StaticFiles;

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

    /// <summary>
    /// Returns true if the file name or URL path has a raster image extension supported by
    /// server-side thumbnail generation and on-demand thumbnail self-healing (.jpg, .jpeg, .png, .gif, .webp, .bmp).
    /// </summary>
    public static bool CanSelfHealThumbnail(string? fileNameOrPath)
    {
        if (string.IsNullOrWhiteSpace(fileNameOrPath)) return false;
        var clean = fileNameOrPath.Split('?', '#')[0];
        return IsImageFile(clean);
    }

    public static string BuildThumbnailUrl(string? filePath, bool canUseServerThumbnail)
    {
        if (!canUseServerThumbnail || string.IsNullOrWhiteSpace(filePath))
            return filePath!;

        if (filePath.StartsWith("/spokesapi/files/", StringComparison.Ordinal))
            return filePath.Replace("/spokesapi/files/", "/spokesapi/files/thumb/", StringComparison.Ordinal);
        if (filePath.StartsWith("/internal/attachments/", StringComparison.Ordinal))
            return filePath.Replace("/internal/attachments/", "/internal/attachments/thumb/", StringComparison.Ordinal);
        if (filePath.StartsWith("/spokesapi/attachments/", StringComparison.Ordinal))
            return filePath.Replace("/spokesapi/attachments/", "/spokesapi/attachments/thumb/", StringComparison.Ordinal);

        return filePath;
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

    public static bool IsValidPathSegment(string? segment) =>
        !string.IsNullOrWhiteSpace(segment) &&
        !segment.Contains("..", StringComparison.Ordinal) &&
        !segment.Contains('/') &&
        !segment.Contains('\\') &&
        segment.IndexOfAny(Path.GetInvalidFileNameChars()) < 0;

    private static bool IsUnsafeFilenameChar(char c)
    {
        if (c < 0x20 || c == 0x7F || char.IsControl(c))
        {
            return true;
        }

        // Bidi override/isolate and directional format characters (Trojan Source / extension spoofing)
        if ((c >= '\u202A' && c <= '\u202E') ||
            (c >= '\u2066' && c <= '\u2069') ||
            c == '\u200E' ||
            c == '\u200F' ||
            c == '\u061C')
        {
            return true;
        }

        return c is '<' or '>' or ':' or '"' or '/' or '\\' or '|' or '?' or '*';
    }

    /// <summary>
    /// Extracts a safe single-segment display filename without path traversal, control/bidi
    /// characters, or OS-reserved symbols, capped at 200 characters while preserving extension.
    /// </summary>
    public static string SanitizeDisplayFileName(string? rawFileName, string fallback = "attachment")
    {
        string safeFallback = string.IsNullOrWhiteSpace(fallback) ? "attachment" : fallback.Trim();
        if (string.IsNullOrWhiteSpace(rawFileName))
        {
            return safeFallback;
        }

        // Split on both POSIX and Windows separators regardless of server host OS
        string normalizedSeparators = rawFileName.Replace('\\', '/');
        int lastSlash = normalizedSeparators.LastIndexOf('/');
        string segment = lastSlash >= 0
            ? normalizedSeparators[(lastSlash + 1)..]
            : normalizedSeparators;

        var sb = new System.Text.StringBuilder(segment.Length);
        foreach (char c in segment)
        {
            if (!IsUnsafeFilenameChar(c))
            {
                sb.Append(c);
            }
        }

        string cleaned = sb.ToString().Trim().Trim('.');
        while (cleaned.Contains("..", StringComparison.Ordinal))
        {
            cleaned = cleaned.Replace("..", "_", StringComparison.Ordinal);
        }
        cleaned = cleaned.Trim().Trim('.');

        if (string.IsNullOrWhiteSpace(cleaned))
        {
            return safeFallback;
        }

        const int maxLen = 200;
        if (cleaned.Length > maxLen)
        {
            string ext = Path.GetExtension(cleaned);
            if (!string.IsNullOrEmpty(ext) && ext.Length <= 20 && ext.Length < maxLen)
            {
                string stem = cleaned[..(cleaned.Length - ext.Length)];
                int maxStem = maxLen - ext.Length;
                cleaned = stem[..Math.Min(stem.Length, maxStem)].TrimEnd('.', ' ') + ext;
            }
            else
            {
                cleaned = cleaned[..maxLen].TrimEnd('.', ' ');
            }
        }

        return string.IsNullOrWhiteSpace(cleaned) ? safeFallback : cleaned;
    }
}
