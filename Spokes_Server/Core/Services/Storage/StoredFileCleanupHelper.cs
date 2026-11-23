using System;
using System.IO;
using Microsoft.Extensions.Logging;

namespace Spokes_Server.Core.Services.Storage;

/// <summary>
/// Shared helper for deleting stored upload files (and companion thumbnails) when destroying
/// chat channels, projects, albums, or deleted-user media, supporting both <see cref="IFileService"/>
/// and fallback path resolution when optional DI services are absent.
/// </summary>
public static class StoredFileCleanupHelper
{
    public static void DeleteFileWithThumbnail(
        string dataPath,
        IFileService? fileService,
        string? fileIdentifier,
        ILogger? logger = null)
    {
        if (string.IsNullOrWhiteSpace(fileIdentifier))
        {
            return;
        }

        if (fileService != null)
        {
            try
            {
                fileService.DeleteAsync(fileIdentifier).GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                logger?.LogWarning(ex, "Failed to delete stored file {FileIdentifier} via IFileService", fileIdentifier);
            }
            return;
        }

        try
        {
            var fullPath = TryResolveFallbackPhysicalPath(dataPath, fileIdentifier);
            if (string.IsNullOrEmpty(fullPath))
            {
                return;
            }

            if (File.Exists(fullPath))
            {
                File.Delete(fullPath);
            }

            var directory = Path.GetDirectoryName(fullPath);
            var fileName = Path.GetFileName(fullPath);
            var fileNameWithoutExt = Path.GetFileNameWithoutExtension(fullPath);
            if (!string.IsNullOrEmpty(directory) &&
                !string.IsNullOrEmpty(fileName) &&
                !string.IsNullOrEmpty(fileNameWithoutExt) &&
                !fileName.EndsWith("_thumb.jpg", StringComparison.OrdinalIgnoreCase) &&
                !fileName.EndsWith("_thumb_sm.jpg", StringComparison.OrdinalIgnoreCase) &&
                !fileNameWithoutExt.EndsWith("_thumb", StringComparison.OrdinalIgnoreCase) &&
                !fileNameWithoutExt.EndsWith("_thumb_sm", StringComparison.OrdinalIgnoreCase))
            {
                var canonicalThumbPath = Path.Combine(directory, FileService.GetThumbnailFileName(fileName));
                if (File.Exists(canonicalThumbPath))
                {
                    File.Delete(canonicalThumbPath);
                }

                var smallThumbPath = Path.Combine(directory, FileService.GetThumbnailFileName(fileName, small: true));
                if (File.Exists(smallThumbPath))
                {
                    File.Delete(smallThumbPath);
                }

                var legacyThumbPath = Path.Combine(directory, $"{fileNameWithoutExt}_thumb.jpg");
                if (File.Exists(legacyThumbPath))
                {
                    File.Delete(legacyThumbPath);
                }
            }

            if (!string.IsNullOrEmpty(directory) && Directory.Exists(directory) && Directory.GetFiles(directory).Length == 0 && Directory.GetDirectories(directory).Length == 0)
            {
                Directory.Delete(directory);
            }
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "Failed to delete stored file {FileIdentifier} via fallback cleanup", fileIdentifier);
        }
    }

    private static string? TryResolveFallbackPhysicalPath(string dataPath, string fileIdentifier)
    {
        if (string.IsNullOrWhiteSpace(dataPath))
        {
            return null;
        }

        var clean = fileIdentifier.Split('?', '#')[0].Trim().Replace('\\', '/');
        if (string.IsNullOrEmpty(clean) || clean.Contains("..", StringComparison.Ordinal))
        {
            return null;
        }

        const string thumbPrefix = "/spokesapi/files/thumb/";
        const string filesPrefix = "/spokesapi/files/";
        if (clean.StartsWith(thumbPrefix, StringComparison.OrdinalIgnoreCase))
        {
            clean = filesPrefix + clean[thumbPrefix.Length..];
        }

        if (clean.StartsWith(filesPrefix, StringComparison.OrdinalIgnoreCase))
        {
            var relative = clean[filesPrefix.Length..].TrimStart('/');
            var segments = relative.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length != 3)
            {
                return null;
            }

            foreach (var seg in segments)
            {
                if (seg is "." or ".." || seg.Contains(':'))
                {
                    return null;
                }
            }

            var uploadsRoot = Path.GetFullPath(Path.Combine(dataPath, "Uploads"));
            var resolved = Path.GetFullPath(Path.Combine(uploadsRoot, segments[0], segments[1], segments[2]));
            return resolved.StartsWith(uploadsRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                ? resolved
                : null;
        }

        var relativePath = clean.TrimStart('/');
        if (string.IsNullOrEmpty(relativePath) || Path.IsPathRooted(relativePath))
        {
            return null;
        }

        var root = Path.GetFullPath(dataPath);
        var full = Path.GetFullPath(Path.Combine(root, relativePath));
        return full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            ? full
            : null;
    }
}
