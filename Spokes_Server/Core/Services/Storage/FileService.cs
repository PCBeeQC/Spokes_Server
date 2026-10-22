using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Spokes_Server.Core.Data;
using Spokes_Server.Core.Services.Security;

namespace Spokes_Server.Core.Services.Storage;

public interface IFileService
{
    Task<string> UploadStreamAsync(string category, string contextId, Stream stream, string fileName, string? encryptionKey = null, string? predefinedSafeName = null);
    Task<string> UploadThumbnailAsync(string category, string contextId, Stream thumbStream, string baseFileName, string? encryptionKey = null);
    string GenerateSafeName(string originalFileName);
    Task<bool> DeleteAsync(string? fileIdentifier);
    Task<int> DeleteAssociatedFilesAsync(object? target);
    Task<int> DeleteDeltaFilesAsync(IHasAssociatedFiles? original, IHasAssociatedFiles? updated);
    Task<bool> DeleteFileAsync(string category, string contextId, string fileName);
    string GetPhysicalPath(string category, string contextId, string fileName);
    bool FileExists(string category, string contextId, string fileName);
    Stream? OpenRead(string category, string contextId, string fileName, string? encryptionKey = null);
    Stream OpenDecryptedStream(string physicalPath, string encryptionKey);
    string GetContentType(string fileName);
    void CleanTempContext(string contextId);
    void PurgeStaleTempFiles(TimeSpan? maxAge = null);
    string GetAttachmentPhysicalPath(string channelId, string fileName, bool thumb = false);
    string GetEmailAttachmentPhysicalPath(string employeeId, string messageId, string attachmentId);
    string GetAvatarPhysicalPath(string employeeId, string fileName);
    Task EncryptFileAsync(string physicalPath, string encryptionKey, CancellationToken cancellationToken = default);
    Task DecryptFileAsync(string physicalPath, string encryptionKey, CancellationToken cancellationToken = default);
    bool IsFileEncrypted(string physicalPath, string? expectedKey = null);
}

public class FileService : IFileService
{
    private readonly IConfiguration _configuration;
    private readonly ILogger<FileService> _logger;
    private readonly ICryptoService _crypto;
    private readonly string _baseDataPath;
    private readonly string _basePath;
    private static readonly FileExtensionContentTypeProvider _contentTypeProvider = new();

    public FileService(IConfiguration configuration, ILogger<FileService> logger, ICryptoService crypto)
    {
        _configuration = configuration;
        _logger = logger;
        _crypto = crypto;

        _baseDataPath = _configuration["DataPath"] ?? "Data";
        _basePath = Path.Combine(_baseDataPath, "Uploads");
    }


    public static string GetThumbnailFileName(string baseFileName) => $"{baseFileName}_thumb.jpg";

    public async Task<string> UploadThumbnailAsync(string category, string contextId, Stream thumbStream, string baseFileName, string? encryptionKey = null)
    {
        var thumbFileName = GetThumbnailFileName(baseFileName);
        return await UploadStreamAsync(category, contextId, thumbStream, thumbFileName, encryptionKey, predefinedSafeName: thumbFileName);
    }

    public string GenerateSafeName(string originalFileName)
    {
        var sanitizedFileName = Path.GetFileName(originalFileName);
        var extension = Path.GetExtension(sanitizedFileName);
        return $"{DateTime.Now.Ticks}_{Guid.NewGuid().ToString()[..8]}{extension}";
    }

    public async Task<string> UploadStreamAsync(string category, string contextId, Stream stream, string fileName, string? encryptionKey = null, string? predefinedSafeName = null)
    {
        try
        {
            ValidatePathSegment(category, nameof(category));
            ValidatePathSegment(contextId, nameof(contextId));

            var folder = Path.Combine(_basePath, category, contextId);
            if (!Directory.Exists(folder))
            {
                Directory.CreateDirectory(folder);
            }

            var safeName = string.IsNullOrWhiteSpace(predefinedSafeName) 
                ? GenerateSafeName(fileName) 
                : predefinedSafeName;

            var fullPath = Path.Combine(folder, safeName);

            using var fileStream = new FileStream(fullPath, FileMode.Create);

            if (!string.IsNullOrEmpty(encryptionKey))
            {
                await _crypto.EncryptStreamAsync(stream, fileStream, encryptionKey);
            }
            else
            {
                await stream.CopyToAsync(fileStream);
            }

            _logger.LogInformation("File uploaded to {Path}", fullPath);

            return $"/spokesapi/files/{category}/{contextId}/{safeName}";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to upload file {FileName} to {Category}/{ContextId}", fileName, category, contextId);
            throw;
        }
    }

    public async Task<bool> DeleteAsync(string? fileIdentifier)
    {
        if (string.IsNullOrWhiteSpace(fileIdentifier)) return false;

        // Clean query string and anchor (e.g. ?v=123 or #section)
        var clean = fileIdentifier.Trim();
        var qIdx = clean.IndexOf('?');
        if (qIdx >= 0) clean = clean[..qIdx];
        var hIdx = clean.IndexOf('#');
        if (hIdx >= 0) clean = clean[..hIdx];
        clean = clean.Trim();
        if (string.IsNullOrWhiteSpace(clean)) return false;

        // Pattern 1: /spokesapi/files/thumb/{category}/{contextId}/{fileName}
        if (clean.StartsWith("/spokesapi/files/thumb/", StringComparison.OrdinalIgnoreCase))
        {
            var parts = clean["/spokesapi/files/thumb/".Length..].Split('/');
            if (parts.Length >= 3)
            {
                var category = parts[0];
                var contextId = parts[1];
                var fileName = parts[2];
                return await DeleteFileAsync(category, contextId, fileName);
            }
        }

        // Pattern 2: /spokesapi/files/{category}/{contextId}/{fileName}
        if (clean.StartsWith("/spokesapi/files/", StringComparison.OrdinalIgnoreCase))
        {
            var parts = clean["/spokesapi/files/".Length..].Split('/');
            if (parts.Length >= 3)
            {
                var category = parts[0];
                var contextId = parts[1];
                var fileName = parts[2];
                return await DeleteFileAsync(category, contextId, fileName);
            }
        }

        // Pattern 3: Direct or relative physical path
        string physicalPath = clean;
        if (!Path.IsPathRooted(physicalPath))
        {
            var candidateInUploads = Path.GetFullPath(Path.Combine(_basePath, physicalPath.TrimStart('/', '\\')));
            var candidateInData = Path.GetFullPath(Path.Combine(_baseDataPath, physicalPath.TrimStart('/', '\\')));

            if (File.Exists(candidateInUploads))
            {
                physicalPath = candidateInUploads;
            }
            else if (File.Exists(candidateInData))
            {
                physicalPath = candidateInData;
            }
            else
            {
                physicalPath = candidateInUploads;
            }
        }

        if (!File.Exists(physicalPath)) return false;

        try
        {
            await Task.Run(() => File.Delete(physicalPath));
            _logger.LogInformation("File deleted: {Path}", physicalPath);

            // Clean companion thumbnail if it exists
            var dir = Path.GetDirectoryName(physicalPath);
            var fileName = Path.GetFileName(physicalPath);
            var fileNameNoExt = Path.GetFileNameWithoutExtension(physicalPath);
            if (!string.IsNullOrEmpty(dir) &&
                !fileName.EndsWith("_thumb.jpg", StringComparison.OrdinalIgnoreCase) &&
                !fileNameNoExt.EndsWith("_thumb", StringComparison.OrdinalIgnoreCase))
            {
                var thumbPath = Path.Combine(dir, GetThumbnailFileName(fileName));
                if (File.Exists(thumbPath))
                {
                    try { File.Delete(thumbPath); } catch { /* Ignore thumbnail delete error */ }
                }

                var legacyThumbPath = Path.Combine(dir, $"{fileNameNoExt}_thumb.jpg");
                if (File.Exists(legacyThumbPath))
                {
                    try { File.Delete(legacyThumbPath); } catch { /* Ignore thumbnail delete error */ }
                }
            }

            // Prune context directory if empty and within _basePath
            if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
            {
                var fullDirPath = Path.GetFullPath(dir);
                var fullBasePath = Path.GetFullPath(_basePath);
                if (fullDirPath.StartsWith(fullBasePath, StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(fullDirPath, fullBasePath, StringComparison.OrdinalIgnoreCase))
                {
                    if (!Directory.EnumerateFileSystemEntries(fullDirPath).Any())
                    {
                        try { Directory.Delete(fullDirPath); } catch { /* Ignore */ }
                    }
                }
            }

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete physical file {Path}", physicalPath);
            throw new IOException($"Failed to delete physical file '{physicalPath}': {ex.Message}", ex);
        }
    }

    public async Task<int> DeleteAssociatedFilesAsync(object? target)
    {
        if (target == null) return 0;

        if (target is string path)
        {
            return await DeleteAsync(path) ? 1 : 0;
        }

        if (target is IHasAssociatedFiles entity)
        {
            int count = 0;
            var paths = entity.GetAssociatedFilePaths();
            if (paths != null)
            {
                foreach (var p in paths)
                {
                    if (await DeleteAsync(p)) count++;
                }
            }
            return count;
        }

        if (target is IEnumerable enumerable)
        {
            int total = 0;
            foreach (var item in enumerable)
            {
                total += await DeleteAssociatedFilesAsync(item);
            }
            return total;
        }

        return 0;
    }

    public async Task<int> DeleteDeltaFilesAsync(IHasAssociatedFiles? original, IHasAssociatedFiles? updated)
    {
        if (original == null) return 0;

        var origPaths = original.GetAssociatedFilePaths()?
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Distinct(StringComparer.OrdinalIgnoreCase) ?? Enumerable.Empty<string>();

        var updatedPaths = new HashSet<string>(
            updated?.GetAssociatedFilePaths()?.Where(p => !string.IsNullOrWhiteSpace(p)) ?? Enumerable.Empty<string>(),
            StringComparer.OrdinalIgnoreCase);

        var removedPaths = origPaths.Where(p => !updatedPaths.Contains(p)).ToList();

        int count = 0;
        foreach (var p in removedPaths)
        {
            if (await DeleteAsync(p)) count++;
        }
        return count;
    }

    public async Task<bool> DeleteFileAsync(string category, string contextId, string fileName)
    {
        if (string.IsNullOrWhiteSpace(category) || string.IsNullOrWhiteSpace(contextId) || string.IsNullOrWhiteSpace(fileName))
        {
            return false;
        }

        var path = GetPhysicalPath(category, contextId, fileName);
        if (!File.Exists(path))
        {
            return false;
        }

        try
        {
            await Task.Run(() => File.Delete(path));
            _logger.LogInformation("File deleted: {Path}", path);

            // Also delete companion thumbnail if present
            var fileNameWithoutExt = Path.GetFileNameWithoutExtension(fileName);
            if (!fileName.EndsWith("_thumb.jpg", StringComparison.OrdinalIgnoreCase) &&
                !fileNameWithoutExt.EndsWith("_thumb", StringComparison.OrdinalIgnoreCase))
            {
                var thumbPath = GetPhysicalPath(category, contextId, GetThumbnailFileName(fileName));
                if (File.Exists(thumbPath))
                {
                    try { File.Delete(thumbPath); } catch { }
                }

                var legacyThumbPath = GetPhysicalPath(category, contextId, $"{fileNameWithoutExt}_thumb.jpg");
                if (File.Exists(legacyThumbPath))
                {
                    try { File.Delete(legacyThumbPath); } catch { }
                }
            }

            // Prune parent context folder if empty
            var folder = Path.Combine(_basePath, category, contextId);
            if (Directory.Exists(folder) && !Directory.EnumerateFileSystemEntries(folder).Any())
            {
                try { Directory.Delete(folder); } catch { }
            }

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete physical file {Category}/{ContextId}/{FileName}", category, contextId, fileName);
            throw new IOException($"Failed to delete physical file '{path}': {ex.Message}", ex);
        }
    }

    public string GetPhysicalPath(string category, string contextId, string fileName)
    {
        // Sanitize inputs
        category = Path.GetFileName(category);
        contextId = Path.GetFileName(contextId);
        fileName = Path.GetFileName(fileName);

        return Path.Combine(_basePath, category, contextId, fileName);
    }

    public bool FileExists(string category, string contextId, string fileName)
    {
        var path = GetPhysicalPath(category, contextId, fileName);
        return File.Exists(path);
    }

    public Stream OpenDecryptedStream(string physicalPath, string encryptionKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(physicalPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(encryptionKey);

        var fileStream = new FileStream(
            physicalPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 65536,
            options: FileOptions.Asynchronous | FileOptions.SequentialScan);
        try
        {
            var keyBytes = Convert.FromBase64String(encryptionKey);
            return new SeekableAesStream(fileStream, keyBytes);
        }
        catch
        {
            fileStream.Dispose();
            throw;
        }
    }

    public bool IsFileEncrypted(string physicalPath, string? expectedKey = null)
    {
        if (string.IsNullOrWhiteSpace(physicalPath) || !File.Exists(physicalPath))
            return false;

        try
        {
            var fileInfo = new FileInfo(physicalPath);
            // AES-CBC files encrypted by CryptoService have 16-byte IV + N 16-byte ciphertext blocks
            if (fileInfo.Length < 32 || fileInfo.Length % 16 != 0)
            {
                return false;
            }

            // Check if file starts with common unencrypted magic numbers
            using (var fs = new FileStream(physicalPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                var header = new byte[8];
                int read = fs.Read(header, 0, 8);
                if (read >= 3)
                {
                    // JPEG: FF D8 FF
                    if (header[0] == 0xFF && header[1] == 0xD8 && header[2] == 0xFF) return false;
                }
                if (read >= 8)
                {
                    // PNG: 89 50 4E 47 0D 0A 1A 0A
                    if (header[0] == 0x89 && header[1] == 0x50 && header[2] == 0x4E && header[3] == 0x47 &&
                        header[4] == 0x0D && header[5] == 0x0A && header[6] == 0x1A && header[7] == 0x0A) return false;
                }
                if (read >= 4)
                {
                    // GIF: GIF8
                    if (header[0] == 0x47 && header[1] == 0x49 && header[2] == 0x46 && header[3] == 0x38) return false;
                    // PDF: %PDF
                    if (header[0] == 0x25 && header[1] == 0x50 && header[2] == 0x44 && header[3] == 0x46) return false;
                    // ZIP: PK\x03\x04
                    if (header[0] == 0x50 && header[1] == 0x4B && header[2] == 0x03 && header[3] == 0x04) return false;
                    // WebP: RIFF (bytes 0-3)
                    if (header[0] == 0x52 && header[1] == 0x49 && header[2] == 0x46 && header[3] == 0x46) return false;
                }

                // If an expected AES key was provided, verify by testing PKCS7 padding on the last block
                if (!string.IsNullOrEmpty(expectedKey))
                {
                    fs.Seek(-32, SeekOrigin.End);
                    var lastTwoBlocks = new byte[32];
                    int bytesRead = 0;
                    while (bytesRead < 32)
                    {
                        int r = fs.Read(lastTwoBlocks, bytesRead, 32 - bytesRead);
                        if (r == 0) break;
                        bytesRead += r;
                    }
                    if (bytesRead < 32) return false;

                    var ivForLastBlock = new byte[16];
                    Buffer.BlockCopy(lastTwoBlocks, 0, ivForLastBlock, 0, 16);
                    var lastCipherBlock = new byte[16];
                    Buffer.BlockCopy(lastTwoBlocks, 16, lastCipherBlock, 0, 16);

                    using var aes = System.Security.Cryptography.Aes.Create();
                    aes.Key = Convert.FromBase64String(expectedKey);
                    aes.IV = ivForLastBlock;
                    aes.Mode = System.Security.Cryptography.CipherMode.CBC;
                    aes.Padding = System.Security.Cryptography.PaddingMode.None;

                    using var decryptor = aes.CreateDecryptor();
                    var plainBlock = decryptor.TransformFinalBlock(lastCipherBlock, 0, 16);
                    int paddingLen = plainBlock[15];
                    if (paddingLen < 1 || paddingLen > 16) return false;
                    for (int i = 16 - paddingLen; i < 16; i++)
                    {
                        if (plainBlock[i] != paddingLen) return false;
                    }
                }
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    public async Task EncryptFileAsync(string physicalPath, string encryptionKey, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(physicalPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(encryptionKey);

        if (!File.Exists(physicalPath)) return;

        // Idempotency: skip if already encrypted with this key
        if (IsFileEncrypted(physicalPath, encryptionKey)) return;

        var tempPath = $"{physicalPath}.tmp.{Guid.NewGuid():N}";
        try
        {
            await using (var input = new FileStream(physicalPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            await using (var output = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                await _crypto.EncryptStreamAsync(input, output, encryptionKey, cancellationToken);
            }

            File.Move(tempPath, physicalPath, overwrite: true);
            _logger.LogInformation("File encrypted at {Path}", physicalPath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to encrypt file at {Path}", physicalPath);
            throw;
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                try { File.Delete(tempPath); } catch { }
            }
        }
    }

    public async Task DecryptFileAsync(string physicalPath, string encryptionKey, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(physicalPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(encryptionKey);

        if (!File.Exists(physicalPath)) return;

        // Idempotency: skip if already decrypted (plaintext)
        if (!IsFileEncrypted(physicalPath, encryptionKey)) return;

        var tempPath = $"{physicalPath}.tmp.{Guid.NewGuid():N}";
        try
        {
            await using (var input = new FileStream(physicalPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            await using (var output = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                await _crypto.DecryptStreamAsync(input, output, encryptionKey, cancellationToken);
            }

            File.Move(tempPath, physicalPath, overwrite: true);
            _logger.LogInformation("File decrypted at {Path}", physicalPath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to decrypt file at {Path}", physicalPath);
            throw;
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                try { File.Delete(tempPath); } catch { }
            }
        }
    }

    public Stream? OpenRead(string category, string contextId, string fileName, string? encryptionKey = null)
    {
        var path = GetPhysicalPath(category, contextId, fileName);
        if (!File.Exists(path)) return null;

        if (!string.IsNullOrEmpty(encryptionKey))
        {
            return OpenDecryptedStream(path, encryptionKey);
        }

        return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
    }

    public string GetContentType(string fileName)
    {
        if (_contentTypeProvider.TryGetContentType(fileName, out var contentType))
        {
            return contentType;
        }
        return "application/octet-stream";
    }

    public string GetAttachmentPhysicalPath(string channelId, string fileName, bool thumb = false)
    {
        ValidatePathSegment(channelId, nameof(channelId));
        ValidatePathSegment(fileName, nameof(fileName));

        var targetFileName = thumb ? $"{fileName}_thumb.jpg" : fileName;
        var filePath = Path.Combine(_baseDataPath, "Attachments", channelId, targetFileName);
        if (thumb && !File.Exists(filePath))
        {
            filePath = Path.Combine(_baseDataPath, "Attachments", channelId, fileName);
        }
        return filePath;
    }

    public string GetEmailAttachmentPhysicalPath(string employeeId, string messageId, string attachmentId)
    {
        ValidatePathSegment(employeeId, nameof(employeeId));
        ValidatePathSegment(messageId, nameof(messageId));
        ValidatePathSegment(attachmentId, nameof(attachmentId));

        var baseDirPath = Path.GetFullPath(Path.Combine(_baseDataPath, "Employees", employeeId, "Email", "Attachments", messageId));
        var filePath = Path.GetFullPath(Path.Combine(baseDirPath, attachmentId));
        if (!filePath.StartsWith(baseDirPath, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Invalid path traversal detected");
        }
        return filePath;
    }

    public string GetAvatarPhysicalPath(string employeeId, string fileName)
    {
        ValidatePathSegment(employeeId, nameof(employeeId));
        ValidatePathSegment(fileName, nameof(fileName));

        var safeFileName = Path.GetFileName(fileName);
        var baseDirPath = Path.GetFullPath(Path.Combine(_baseDataPath, "Employees", employeeId));
        var filePath = Path.GetFullPath(Path.Combine(baseDirPath, safeFileName));
        if (!filePath.StartsWith(baseDirPath, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Invalid path traversal detected");
        }
        return filePath;
    }

    public void CleanTempContext(string contextId)
    {
        if (!IsValidPathSegment(contextId))
        {
            return;
        }

        try
        {
            var folder = Path.Combine(_basePath, "temp", Path.GetFileName(contextId));
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, recursive: true);
                _logger.LogInformation("Cleaned temp context directory: {Folder}", folder);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to clean temp context directory for contextId {ContextId}", contextId);
        }
    }

    public void PurgeStaleTempFiles(TimeSpan? maxAge = null)
    {
        try
        {
            var tempFolder = Path.Combine(_basePath, "temp");
            if (!Directory.Exists(tempFolder)) return;

            var threshold = DateTime.UtcNow - (maxAge ?? TimeSpan.FromHours(1));
            var dirs = Directory.GetDirectories(tempFolder);
            foreach (var dir in dirs)
            {
                try
                {
                    var dirInfo = new DirectoryInfo(dir);
                    if (dirInfo.LastWriteTimeUtc < threshold)
                    {
                        dirInfo.Delete(recursive: true);
                        _logger.LogInformation("Purged stale temp upload directory: {Dir}", dir);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to purge stale temp directory {Dir}", dir);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error scanning for stale temp files");
        }
    }

    private static void ValidatePathSegment(string? segment, string paramName = "path")
    {
        if (string.IsNullOrWhiteSpace(segment) ||
            segment.Contains("..") ||
            segment.Contains('/') ||
            segment.Contains('\\') ||
            segment.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            throw new ArgumentException($"Invalid path parameter: {paramName}", paramName);
        }
    }

    private static bool IsValidPathSegment(string? segment)
    {
        return !string.IsNullOrWhiteSpace(segment) &&
               !segment.Contains("..") &&
               !segment.Contains('/') &&
               !segment.Contains('\\') &&
               segment.IndexOfAny(Path.GetInvalidFileNameChars()) < 0;
    }
}
