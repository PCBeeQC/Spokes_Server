using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Spokes_Server.Core.Models.Communication;

/// <summary>
/// Domain model for an email attachment decoupled from presentation/UI frameworks.
/// Can be backed by in-memory bytes, an open stream, or a local file path.
/// </summary>
public class EmailAttachment
{
    public required string FileName { get; set; }
    public string ContentType { get; set; } = "application/octet-stream";
    public byte[]? ContentBytes { get; set; }
    public Stream? ContentStream { get; set; }
    public string? FilePath { get; set; }
    public long? Size { get; set; }

    public static EmailAttachment FromBytes(string fileName, byte[] bytes, string? contentType = null)
    {
        return new EmailAttachment
        {
            FileName = fileName,
            ContentBytes = bytes,
            ContentType = string.IsNullOrWhiteSpace(contentType) ? "application/octet-stream" : contentType,
            Size = bytes.Length
        };
    }

    public static EmailAttachment FromStream(string fileName, Stream stream, string? contentType = null, long? size = null)
    {
        return new EmailAttachment
        {
            FileName = fileName,
            ContentStream = stream,
            ContentType = string.IsNullOrWhiteSpace(contentType) ? "application/octet-stream" : contentType,
            Size = size ?? (stream.CanSeek ? stream.Length : null)
        };
    }

    public static EmailAttachment FromFile(string filePath, string? fileName = null, string? contentType = null)
    {
        return new EmailAttachment
        {
            FileName = fileName ?? Path.GetFileName(filePath),
            FilePath = filePath,
            ContentType = string.IsNullOrWhiteSpace(contentType)
                ? (filePath.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase) ? "application/pdf" : "application/octet-stream")
                : contentType,
            Size = File.Exists(filePath) ? new FileInfo(filePath).Length : null
        };
    }

    public async Task CopyToAsync(Stream destination, CancellationToken cancellationToken = default)
    {
        if (ContentBytes != null)
        {
            await destination.WriteAsync(ContentBytes, cancellationToken);
        }
        else if (ContentStream != null)
        {
            if (ContentStream.CanSeek)
            {
                ContentStream.Position = 0;
            }
            await ContentStream.CopyToAsync(destination, cancellationToken);
        }
        else if (!string.IsNullOrEmpty(FilePath) && File.Exists(FilePath))
        {
            await using var fileStream = File.OpenRead(FilePath);
            await fileStream.CopyToAsync(destination, cancellationToken);
        }
    }

    public async Task<byte[]> GetBytesAsync(CancellationToken cancellationToken = default)
    {
        if (ContentBytes != null)
        {
            return ContentBytes;
        }

        if (ContentStream != null)
        {
            if (ContentStream is MemoryStream ms)
            {
                return ms.ToArray();
            }
            if (ContentStream.CanSeek)
            {
                ContentStream.Position = 0;
            }
            using var copyMs = new MemoryStream();
            await ContentStream.CopyToAsync(copyMs, cancellationToken);
            return copyMs.ToArray();
        }

        if (!string.IsNullOrEmpty(FilePath) && File.Exists(FilePath))
        {
            return await File.ReadAllBytesAsync(FilePath, cancellationToken);
        }

        return Array.Empty<byte>();
    }
}
