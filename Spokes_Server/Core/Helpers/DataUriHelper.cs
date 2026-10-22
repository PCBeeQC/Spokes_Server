using System.Diagnostics.CodeAnalysis;

namespace Spokes_Server.Core.Helpers;

/// <summary>
/// Provides unified utilities for RFC 2397 Data URI parsing, MIME type resolution, and payload decoding.
/// </summary>
public static class DataUriHelper
{
    /// <summary>
    /// Attempts to parse an RFC 2397 Data URI (e.g. "data:image/png;base64,iVBORw...") or comma-separated base64 payload.
    /// Extracts the decoded byte array and detected MIME type.
    /// </summary>
    public static bool TryParse(
        string? dataUri,
        [NotNullWhen(true)] out byte[]? bytes,
        [NotNullWhen(true)] out string? mimeType,
        string defaultMimeType = "application/octet-stream")
    {
        bytes = null;
        mimeType = null;

        if (string.IsNullOrWhiteSpace(dataUri))
            return false;

        try
        {
            var commaIdx = dataUri.IndexOf(',');
            string base64Payload;
            string extractedMime = defaultMimeType;

            if (commaIdx >= 0)
            {
                var header = dataUri[..commaIdx].Trim();
                base64Payload = dataUri[(commaIdx + 1)..].Trim();

                if (header.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                {
                    var meta = header["data:".Length..];
                    var semicolonIdx = meta.IndexOf(';');
                    if (semicolonIdx >= 0)
                    {
                        var candidateMime = meta[..semicolonIdx].Trim();
                        if (!string.IsNullOrEmpty(candidateMime))
                        {
                            extractedMime = candidateMime;
                        }
                    }
                    else if (!string.IsNullOrEmpty(meta))
                    {
                        extractedMime = meta.Trim();
                    }
                }
            }
            else
            {
                if (dataUri.TrimStart().StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
                base64Payload = dataUri.Trim();
            }

            if (string.IsNullOrEmpty(base64Payload))
                return false;

            bytes = Convert.FromBase64String(base64Payload);
            mimeType = extractedMime;
            return true;
        }
        catch
        {
            bytes = null;
            mimeType = null;
            return false;
        }
    }

    /// <summary>
    /// Attempts to parse an image Data URI. Fails if the MIME type is not a recognized raster image (png, jpeg, webp, gif, bmp).
    /// </summary>
    public static bool TryParseImage(
        string? dataUri,
        [NotNullWhen(true)] out byte[]? bytes,
        [NotNullWhen(true)] out string? mimeType,
        string defaultMimeType = "image/png")
    {
        if (TryParse(dataUri, out bytes, out mimeType, defaultMimeType))
        {
            var normalized = mimeType.ToLowerInvariant();
            if (normalized is "image/png" or "image/jpeg" or "image/jpg" or "image/webp" or "image/gif" or "image/bmp")
            {
                mimeType = normalized == "image/jpg" ? "image/jpeg" : normalized;
                return true;
            }
        }

        bytes = null;
        mimeType = null;
        return false;
    }

    /// <summary>
    /// Maps a MIME type to a canonical file extension including the leading dot (e.g. "image/png" -> ".png").
    /// </summary>
    public static string GetExtensionForMimeType(string? mimeType)
    {
        if (string.IsNullOrWhiteSpace(mimeType)) return ".bin";

        var normalized = mimeType.Trim().ToLowerInvariant();
        return normalized switch
        {
            "image/png" => ".png",
            "image/jpeg" or "image/jpg" => ".jpg",
            "image/webp" => ".webp",
            "image/gif" => ".gif",
            "image/bmp" => ".bmp",
            "application/pdf" => ".pdf",
            "text/plain" => ".txt",
            "text/csv" => ".csv",
            "application/json" => ".json",
            "application/zip" => ".zip",
            _ => ".bin"
        };
    }
}
