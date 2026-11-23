using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Spokes_Server.Core.Data;
using Spokes_Server.Core.Helpers;
using Spokes_Server.Core.Models.HR;
using Spokes_Server.Core.Services.Security;

namespace Spokes_Server.Core.Services.Storage;

public interface IFileService
{
    Task<string> UploadStreamAsync(string category, string contextId, Stream stream, string fileName, string? encryptionKey = null, string? predefinedSafeName = null);
    Task<string> UploadThumbnailAsync(string category, string contextId, Stream thumbStream, string baseFileName, string? encryptionKey = null, bool small = false);
    string GenerateSafeName(string originalFileName);
    Task<bool> DeleteAsync(string? fileIdentifier);
    Task<int> DeleteAssociatedFilesAsync(object? target);
    Task<int> DeleteDeltaFilesAsync(IHasAssociatedFiles? original, IHasAssociatedFiles? updated);
    Task<bool> DeleteFileAsync(string category, string contextId, string fileName);
    Task<string?> CloneFileAsync(string? sourceFileIdentifier, string destinationCategory, string destinationContextId, Employee? actor = null, bool allowSystemMigration = false);
    bool TryResolveScopedFileName(string? fileIdentifier, string expectedCategory, string expectedContextId, out string safeName);
    string GetPhysicalPath(string category, string contextId, string fileName);
    bool FileExists(string category, string contextId, string fileName);
    Stream? OpenRead(string category, string contextId, string fileName, string? encryptionKey = null);
    Stream OpenDecryptedStream(string physicalPath, string encryptionKey);
    Stream OpenDecryptedStream(byte[] encryptedBytes, string encryptionKey);
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
    private readonly IServiceScopeFactory? _scopeFactory;
    private readonly ThumbnailCacheService? _thumbnailCache;
    private readonly string _baseDataPath;
    private readonly string _basePath;

    public FileService(
        IConfiguration configuration,
        ILogger<FileService> logger,
        ICryptoService crypto,
        IServiceScopeFactory? scopeFactory = null,
        ThumbnailCacheService? thumbnailCache = null)
    {
        _configuration = configuration;
        _logger = logger;
        _crypto = crypto;
        _scopeFactory = scopeFactory;
        _thumbnailCache = thumbnailCache;

        _baseDataPath = _configuration["DataPath"] ?? "Data";
        _basePath = Path.Combine(_baseDataPath, "Uploads");
    }


    public static string GetThumbnailFileName(string baseFileName, bool small = false) =>
        small ? $"{baseFileName}_thumb_sm.jpg" : $"{baseFileName}_thumb.jpg";

    public async Task<string> UploadThumbnailAsync(string category, string contextId, Stream thumbStream, string baseFileName, string? encryptionKey = null, bool small = false)
    {
        var thumbFileName = GetThumbnailFileName(baseFileName, small);
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

            if (_thumbnailCache != null && ThumbnailCacheService.IsThumbnailFileName(safeName))
            {
                using var diskBuffer = new MemoryStream();
                if (!string.IsNullOrEmpty(encryptionKey))
                {
                    await _crypto.EncryptStreamAsync(stream, diskBuffer, encryptionKey);
                }
                else
                {
                    await stream.CopyToAsync(diskBuffer);
                }

                var rawDiskBytes = diskBuffer.ToArray();
                await using (var fileStream = new FileStream(fullPath, FileMode.Create, FileAccess.Write, FileShare.None, bufferSize: 65536, useAsync: true))
                {
                    await fileStream.WriteAsync(rawDiskBytes);
                }

                _thumbnailCache.Set(fullPath, rawDiskBytes, DateTimeOffset.UtcNow, GetContentType(safeName));
            }
            else
            {
                await using (var fileStream = new FileStream(fullPath, FileMode.Create, FileAccess.Write, FileShare.None, bufferSize: 65536, useAsync: true))
                {
                    if (!string.IsNullOrEmpty(encryptionKey))
                    {
                        await _crypto.EncryptStreamAsync(stream, fileStream, encryptionKey);
                    }
                    else
                    {
                        await stream.CopyToAsync(fileStream);
                    }
                }

                _thumbnailCache?.Invalidate(fullPath);
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
                return await DeleteFileAsync(category, contextId, fileName).ConfigureAwait(false);
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
                return await DeleteFileAsync(category, contextId, fileName).ConfigureAwait(false);
            }
        }

        // Pattern 3: Direct or relative physical path (confined to allowed upload/attachment roots)
        if (clean.Contains("..", StringComparison.Ordinal))
        {
            _logger.LogWarning("Rejected DeleteAsync with path traversal segment: {FileIdentifier}", fileIdentifier);
            return false;
        }

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

        try
        {
            physicalPath = Path.GetFullPath(physicalPath);
        }
        catch
        {
            return false;
        }

        var allowedRoots = new[]
        {
            Path.GetFullPath(_basePath) + Path.DirectorySeparatorChar,
            Path.GetFullPath(Path.Combine(_baseDataPath, "Attachments")) + Path.DirectorySeparatorChar,
            Path.GetFullPath(Path.Combine(_baseDataPath, "PDFs")) + Path.DirectorySeparatorChar
        };

        if (!allowedRoots.Any(root => physicalPath.StartsWith(root, StringComparison.OrdinalIgnoreCase)))
        {
            _logger.LogWarning("Rejected DeleteAsync outside allowed storage roots: {FileIdentifier}", fileIdentifier);
            return false;
        }

        if (!File.Exists(physicalPath)) return false;

        try
        {
            await Task.Run(() => File.Delete(physicalPath)).ConfigureAwait(false);
            _logger.LogInformation("File deleted: {Path}", physicalPath);
            CleanupCompanionThumbnailsAndEmptyDir(physicalPath);

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
            return await DeleteAsync(path).ConfigureAwait(false) ? 1 : 0;
        }

        if (target is IHasAssociatedFiles entity)
        {
            int count = 0;
            var paths = entity.GetAssociatedFilePaths();
            if (paths != null)
            {
                foreach (var p in paths)
                {
                    if (await DeleteAsync(p).ConfigureAwait(false)) count++;
                }
            }
            return count;
        }

        if (target is IEnumerable enumerable)
        {
            int total = 0;
            foreach (var item in enumerable)
            {
                total += await DeleteAssociatedFilesAsync(item).ConfigureAwait(false);
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
            if (await DeleteAsync(p).ConfigureAwait(false)) count++;
        }
        return count;
    }

    public async Task<string?> CloneFileAsync(
        string? sourceFileIdentifier,
        string destinationCategory,
        string destinationContextId,
        Employee? actor = null,
        bool allowSystemMigration = false)
    {
        if (string.IsNullOrWhiteSpace(sourceFileIdentifier) ||
            string.IsNullOrWhiteSpace(destinationCategory) ||
            string.IsNullOrWhiteSpace(destinationContextId))
        {
            return null;
        }

        ValidatePathSegment(destinationCategory, nameof(destinationCategory));
        ValidatePathSegment(destinationContextId, nameof(destinationContextId));

        var clean = sourceFileIdentifier.Split('?', '#')[0].Trim();
        const string prefix = "/spokesapi/files/";
        if (!clean.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var parts = clean[prefix.Length..].Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 3 ||
            !IsValidPathSegment(parts[0]) ||
            !IsValidPathSegment(parts[1]) ||
            !IsValidPathSegment(parts[2]))
        {
            return null;
        }

        var srcCat = parts[0];
        var srcCtx = parts[1];
        var srcFile = parts[2];

        // Fail closed unless authorized for a live unsuspended actor or explicit system migration.
        if (!allowSystemMigration && (actor == null || !actor.IsActiveUnsuspended || _scopeFactory == null))
        {
            _logger.LogWarning(
                "Rejected unauthorized or unattributed CloneFileAsync call from {SrcCat}/{SrcCtx} to {DstCat}/{DstCtx}.",
                srcCat,
                srcCtx,
                destinationCategory,
                destinationContextId);
            return null;
        }

        if (srcCat.Equals("temp", StringComparison.OrdinalIgnoreCase) && !allowSystemMigration)
        {
            _logger.LogWarning("Rejected CloneFileAsync from temp storage for {SrcCtx}/{SrcFile}.", srcCtx, srcFile);
            return null;
        }

        string? srcEncryptionKey = null;
        string? dstEncryptionKey = null;

        if (!allowSystemMigration)
        {
            using var scope = _scopeFactory!.CreateScope();
            var authService = scope.ServiceProvider.GetService<IFileAuthorizationService>();
            if (authService == null ||
                !authService.CanAccess(srcCat, srcCtx, actor!) ||
                !authService.CanUpload(destinationCategory, destinationContextId, actor!))
            {
                _logger.LogWarning(
                    "Rejected CloneFileAsync: actor {ActorId} lacks authorization for {SrcCat}/{SrcCtx} -> {DstCat}/{DstCtx}.",
                    actor!.Id,
                    srcCat,
                    srcCtx,
                    destinationCategory,
                    destinationContextId);
                return null;
            }

            var srcKeyRes = authService.ResolveDownloadKey(srcCat, srcCtx, actor!, tokenEncryptionKey: null, httpContext: null, fileName: srcFile);
            if (srcKeyRes.IsEncrypted)
            {
                if (string.IsNullOrEmpty(srcKeyRes.EncryptionKey))
                {
                    _logger.LogWarning("Rejected CloneFileAsync: source encryption key unavailable for {SrcCat}/{SrcCtx}.", srcCat, srcCtx);
                    return null;
                }
                srcEncryptionKey = srcKeyRes.EncryptionKey;
            }

            var dstKeyRes = authService.ResolveUploadKey(new FileUploadKeyRequest(destinationCategory, destinationContextId, actor!));
            if (dstKeyRes.IsEncrypted)
            {
                if (string.IsNullOrEmpty(dstKeyRes.EncryptionKey))
                {
                    _logger.LogWarning("Rejected CloneFileAsync: destination encryption key unavailable for {DstCat}/{DstCtx}.", destinationCategory, destinationContextId);
                    return null;
                }
                dstEncryptionKey = dstKeyRes.EncryptionKey;
            }
        }

        await using var stream = OpenRead(srcCat, srcCtx, srcFile, srcEncryptionKey);
        if (stream == null)
        {
            return null;
        }

        return await UploadStreamAsync(destinationCategory, destinationContextId, stream, srcFile, dstEncryptionKey);
    }

    public bool TryResolveScopedFileName(string? fileIdentifier, string expectedCategory, string expectedContextId, out string safeName)
    {
        safeName = string.Empty;
        if (string.IsNullOrWhiteSpace(fileIdentifier) ||
            !IsValidPathSegment(expectedCategory) ||
            !IsValidPathSegment(expectedContextId))
        {
            return false;
        }

        var clean = fileIdentifier.Split('?', '#')[0].Trim();
        const string prefix = "/spokesapi/files/";
        if (!clean.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var parts = clean[prefix.Length..].Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 3 ||
            !parts[0].Equals(expectedCategory, StringComparison.OrdinalIgnoreCase) ||
            !parts[1].Equals(expectedContextId, StringComparison.OrdinalIgnoreCase) ||
            !IsValidPathSegment(parts[2]))
        {
            return false;
        }

        if (!FileExists(expectedCategory, expectedContextId, parts[2]))
        {
            return false;
        }

        safeName = parts[2];
        return true;
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
            await Task.Run(() => File.Delete(path)).ConfigureAwait(false);
            _logger.LogInformation("File deleted: {Path}", path);
            CleanupCompanionThumbnailsAndEmptyDir(path);

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
        ValidatePathSegment(category, nameof(category));
        ValidatePathSegment(contextId, nameof(contextId));
        if (!string.IsNullOrEmpty(fileName))
        {
            ValidatePathSegment(fileName, nameof(fileName));
        }

        return Path.Combine(_basePath, category, contextId, fileName ?? string.Empty);
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

    public Stream OpenDecryptedStream(byte[] encryptedBytes, string encryptionKey)
    {
        ArgumentNullException.ThrowIfNull(encryptedBytes);
        ArgumentException.ThrowIfNullOrWhiteSpace(encryptionKey);

        var memoryStream = new MemoryStream(encryptedBytes, writable: false);
        try
        {
            var keyBytes = Convert.FromBase64String(encryptionKey);
            return new SeekableAesStream(memoryStream, keyBytes);
        }
        catch
        {
            memoryStream.Dispose();
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

    public Task EncryptFileAsync(string physicalPath, string encryptionKey, CancellationToken cancellationToken = default) =>
        TransformFileAsync(physicalPath, encryptionKey, encrypt: true, cancellationToken);

    public Task DecryptFileAsync(string physicalPath, string encryptionKey, CancellationToken cancellationToken = default) =>
        TransformFileAsync(physicalPath, encryptionKey, encrypt: false, cancellationToken);

    private async Task TransformFileAsync(string physicalPath, string encryptionKey, bool encrypt, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(physicalPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(encryptionKey);

        if (!File.Exists(physicalPath)) return;

        // Idempotency: skip if already in desired encryption state
        if (IsFileEncrypted(physicalPath, encryptionKey) == encrypt) return;

        var tempPath = $"{physicalPath}.tmp.{Guid.NewGuid():N}";
        try
        {
            await using (var input = new FileStream(physicalPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            await using (var output = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                if (encrypt)
                {
                    await _crypto.EncryptStreamAsync(input, output, encryptionKey, cancellationToken);
                }
                else
                {
                    await _crypto.DecryptStreamAsync(input, output, encryptionKey, cancellationToken);
                }
            }

            File.Move(tempPath, physicalPath, overwrite: true);
            _thumbnailCache?.InvalidateFileAndThumbnails(physicalPath);
            _logger.LogInformation(encrypt ? "File encrypted at {Path}" : "File decrypted at {Path}", physicalPath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, encrypt ? "Failed to encrypt file at {Path}" : "Failed to decrypt file at {Path}", physicalPath);
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

    public string GetContentType(string fileName) => FileHelper.GetContentType(fileName);

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

    public string GetEmailAttachmentPhysicalPath(string accountId, string messageId, string attachmentId)
    {
        ValidatePathSegment(accountId, nameof(accountId));
        ValidatePathSegment(messageId, nameof(messageId));
        ValidatePathSegment(attachmentId, nameof(attachmentId));

        var accountDirPath = Path.GetFullPath(Path.Combine(_baseDataPath, "EmailAccounts", accountId, "Attachments", messageId));
        var accountFilePath = Path.GetFullPath(Path.Combine(accountDirPath, attachmentId));
        if (!accountFilePath.StartsWith(accountDirPath, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Invalid path traversal detected");
        }

        return accountFilePath;
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

    private void CleanupCompanionThumbnailsAndEmptyDir(string physicalPath)
    {
        _thumbnailCache?.InvalidateFileAndThumbnails(physicalPath);
        var dir = Path.GetDirectoryName(physicalPath);
        var fileName = Path.GetFileName(physicalPath);
        var fileNameNoExt = Path.GetFileNameWithoutExtension(physicalPath);
        if (!string.IsNullOrEmpty(dir) &&
            !fileName.EndsWith("_thumb.jpg", StringComparison.OrdinalIgnoreCase) &&
            !fileName.EndsWith("_thumb_sm.jpg", StringComparison.OrdinalIgnoreCase) &&
            !fileNameNoExt.EndsWith("_thumb", StringComparison.OrdinalIgnoreCase) &&
            !fileNameNoExt.EndsWith("_thumb_sm", StringComparison.OrdinalIgnoreCase))
        {
            var thumbPath = Path.Combine(dir, GetThumbnailFileName(fileName));
            if (File.Exists(thumbPath))
            {
                try { File.Delete(thumbPath); } catch { /* Ignore thumbnail delete error */ }
            }

            var smallThumbPath = Path.Combine(dir, GetThumbnailFileName(fileName, small: true));
            if (File.Exists(smallThumbPath))
            {
                try { File.Delete(smallThumbPath); } catch { /* Ignore thumbnail delete error */ }
            }

            var legacyThumbPath = Path.Combine(dir, $"{fileNameNoExt}_thumb.jpg");
            if (File.Exists(legacyThumbPath))
            {
                try { File.Delete(legacyThumbPath); } catch { /* Ignore thumbnail delete error */ }
            }
        }

        if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
        {
            var fullDirPath = Path.GetFullPath(dir);
            var fullBasePath = Path.GetFullPath(_basePath);
            if (fullDirPath.StartsWith(fullBasePath, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(fullDirPath, fullBasePath, StringComparison.OrdinalIgnoreCase) &&
                !Directory.EnumerateFileSystemEntries(fullDirPath).Any())
            {
                try { Directory.Delete(fullDirPath); } catch { /* Ignore */ }
            }
        }
    }

    private static void ValidatePathSegment(string? segment, string paramName = "path")
    {
        if (!FileHelper.IsValidPathSegment(segment))
        {
            throw new ArgumentException($"Invalid path parameter: {paramName}", paramName);
        }
    }

    private static bool IsValidPathSegment(string? segment) => FileHelper.IsValidPathSegment(segment);
}
