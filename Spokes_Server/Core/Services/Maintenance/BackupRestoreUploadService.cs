using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Spokes_Server.Core.Helpers;

namespace Spokes_Server.Core.Services.Maintenance;

public enum BackupUploadScope
{
    Admin = 0,
    Setup = 1
}

public sealed record BackupUploadInitRequest(
    string FileName,
    long TotalBytes,
    int? ChunkSizeBytes = null);

public sealed record BackupUploadInitResult(
    bool Success,
    string? UploadId,
    int ChunkSizeBytes,
    int TotalChunks,
    string? ErrorMessage,
    string? ErrorCode = null);

public sealed record BackupUploadChunkResult(
    bool Success,
    int ChunkIndex,
    long BytesReceived,
    long ExpectedTotalBytes,
    string? ErrorMessage,
    string? ErrorCode = null);

public sealed record BackupUploadCompleteResult(
    bool Success,
    string? UploadId,
    string? FileName,
    long FileSizeBytes,
    string? ErrorMessage,
    string? ErrorCode = null);

/// <summary>
/// Canonical chunked HTTP backup upload &amp; staging service shared by Admin backup restores
/// (<c>BackupController</c>) and Pre-Setup initial restores (<c>SetupApiController</c>).
/// Keeps multi-GB backup transfers completely off the Blazor SignalR circuit, enforces Chunk 0
/// fail-fast archive/password validation, and ensures staged archives are always cleaned up.
/// </summary>
public class BackupRestoreUploadService
{
    public const int DefaultChunkSizeBytes = 8 * 1024 * 1024; // 8 MB
    public const int MinChunkSizeBytes = 64 * 1024;           // 64 KB
    public const int MaxChunkSizeBytes = 16 * 1024 * 1024;    // 16 MB
    public const int MaxConcurrentSessions = 2;
    public static readonly TimeSpan SessionIdleTimeout = TimeSpan.FromMinutes(30);

    private static readonly byte[] ZipLocalFileMagic = [0x50, 0x4B, 0x03, 0x04];
    private static readonly byte[] ZipEmptyArchiveMagic = [0x50, 0x4B, 0x05, 0x06];

    private readonly BackupService _backupService;
    private readonly IBackupEncryptionService _encryption;
    private readonly ILogger<BackupRestoreUploadService> _logger;
    private readonly IStorageHealthService? _storageHealth;
    private readonly ConcurrentDictionary<string, UploadSession> _sessions = new(StringComparer.Ordinal);

    public BackupRestoreUploadService(
        BackupService backupService,
        IBackupEncryptionService encryption,
        ILogger<BackupRestoreUploadService> logger,
        IStorageHealthService? storageHealth = null)
    {
        _backupService = backupService;
        _encryption = encryption;
        _logger = logger;
        _storageHealth = storageHealth;
    }

    public BackupUploadInitResult InitSession(
        string? fileName,
        long totalBytes,
        int? requestedChunkSizeBytes,
        BackupUploadScope scope,
        string? ownerUserId)
    {
        PurgeExpiredSessions();

        if (string.IsNullOrWhiteSpace(fileName))
        {
            return new BackupUploadInitResult(false, null, 0, 0, "Backup file name is required.", "INVALID_FILENAME");
        }

        var sanitizedName = FileHelper.SanitizeDisplayFileName(Path.GetFileName(fileName.Trim()));
        var ext = Path.GetExtension(sanitizedName);
        if (!string.Equals(ext, ".zip", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(ext, ".spkbackup", StringComparison.OrdinalIgnoreCase))
        {
            return new BackupUploadInitResult(false, null, 0, 0, "Only .zip and .spkbackup backup archives are supported.", "INVALID_EXTENSION");
        }

        if (totalBytes <= 0)
        {
            return new BackupUploadInitResult(false, null, 0, 0, "Backup file is empty (0 bytes).", "EMPTY_FILE");
        }

        var chunkSize = requestedChunkSizeBytes.HasValue
            ? Math.Clamp(requestedChunkSizeBytes.Value, MinChunkSizeBytes, MaxChunkSizeBytes)
            : DefaultChunkSizeBytes;

        if (_storageHealth is { TotalBytes: > 0, AvailableFreeBytes: > 0 })
        {
            if (!_storageHealth.IsUploadAllowed)
            {
                return new BackupUploadInitResult(false, null, 0, 0,
                    "Server storage is critically full. Free disk space before restoring a backup.", "INSUFFICIENT_STORAGE");
            }

            long availableBudget = _storageHealth.AvailableFreeBytes - StorageHealthService.CriticalThresholdBytes;
            if (totalBytes > availableBudget)
            {
                return new BackupUploadInitResult(false, null, 0, 0,
                    "Insufficient server disk space to stage this backup archive.", "INSUFFICIENT_STORAGE");
            }
        }

        if (_sessions.Count >= MaxConcurrentSessions)
        {
            return new BackupUploadInitResult(false, null, 0, 0,
                "Another backup restore upload is already in progress. Please wait or cancel the existing upload.", "TOO_MANY_UPLOADS");
        }

        var totalChunksLong = (totalBytes + chunkSize - 1) / chunkSize;
        if (totalChunksLong <= 0 || totalChunksLong > int.MaxValue)
        {
            return new BackupUploadInitResult(false, null, 0, 0, "Backup archive size or chunk count is invalid.", "INVALID_SIZE");
        }

        var uploadId = Guid.NewGuid().ToString("N");
        var stagedPath = _backupService.CreateRestoreUploadStagingPath(ext.ToLowerInvariant());

        var session = new UploadSession
        {
            UploadId = uploadId,
            Scope = scope,
            OwnerUserId = ownerUserId,
            FileName = sanitizedName,
            ExpectedTotalBytes = totalBytes,
            ChunkSizeBytes = chunkSize,
            TotalChunks = (int)totalChunksLong,
            StagedFilePath = stagedPath,
            LastActivityUtc = DateTime.UtcNow
        };

        if (!_sessions.TryAdd(uploadId, session))
        {
            return new BackupUploadInitResult(false, null, 0, 0, "Failed to initialize backup upload session.", "INIT_FAILED");
        }

        _logger.LogInformation("Initialized chunked backup upload {UploadId} ({FileName}, {TotalBytes} bytes, {TotalChunks} chunks, scope={Scope})",
            uploadId, sanitizedName, totalBytes, session.TotalChunks, scope);

        return new BackupUploadInitResult(true, uploadId, chunkSize, session.TotalChunks, null);
    }

    public async Task<BackupUploadChunkResult> AppendChunkAsync(
        string uploadId,
        int chunkIndex,
        Stream chunkBody,
        long? declaredLength,
        BackupUploadScope scope,
        string? ownerUserId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(uploadId) || !_sessions.TryGetValue(uploadId, out var session))
        {
            return new BackupUploadChunkResult(false, chunkIndex, 0, 0, "Backup upload session not found or expired.", "SESSION_NOT_FOUND");
        }

        if (!MatchesOwnership(session, scope, ownerUserId))
        {
            return new BackupUploadChunkResult(false, chunkIndex, 0, 0, "Not authorized to modify this backup upload session.", "SESSION_FORBIDDEN");
        }

        await session.Gate.WaitAsync(cancellationToken);
        try
        {
            if (session.IsCompleted || session.IsConsumed)
            {
                return new BackupUploadChunkResult(false, chunkIndex, session.BytesReceived, session.ExpectedTotalBytes,
                    "Backup upload session is already finalized.", "SESSION_COMPLETED");
            }

            if (chunkIndex < 0 || chunkIndex >= session.TotalChunks)
            {
                return new BackupUploadChunkResult(false, chunkIndex, session.BytesReceived, session.ExpectedTotalBytes,
                    $"Chunk index {chunkIndex} is out of range (0..{session.TotalChunks - 1}).", "INVALID_CHUNK_INDEX");
            }

            // Allow sequential progression or idempotent retry of the immediately preceding chunk
            if (chunkIndex > session.NextExpectedChunkIndex || chunkIndex < session.NextExpectedChunkIndex - 1)
            {
                return new BackupUploadChunkResult(false, chunkIndex, session.BytesReceived, session.ExpectedTotalBytes,
                    $"Unexpected chunk index {chunkIndex}; expected {session.NextExpectedChunkIndex}.", "CHUNK_OUT_OF_ORDER");
            }

            long offset = (long)chunkIndex * session.ChunkSizeBytes;
            long expectedChunkLength = chunkIndex == session.TotalChunks - 1
                ? session.ExpectedTotalBytes - offset
                : session.ChunkSizeBytes;

            if (declaredLength.HasValue && declaredLength.Value != expectedChunkLength)
            {
                return new BackupUploadChunkResult(false, chunkIndex, session.BytesReceived, session.ExpectedTotalBytes,
                    $"Chunk {chunkIndex} size mismatch (received {declaredLength.Value} bytes, expected {expectedChunkLength} bytes).", "CHUNK_SIZE_MISMATCH");
            }

            long written = 0;
            var buffer = new byte[81920];
            bool exceededLimit = false;

            await using (var fs = new FileStream(session.StagedFilePath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 81920, useAsync: true))
            {
                fs.Seek(offset, SeekOrigin.Begin);

                int read;
                while ((read = await chunkBody.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken)) > 0)
                {
                    if (written + read > expectedChunkLength)
                    {
                        exceededLimit = true;
                        break;
                    }

                    await fs.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                    written += read;
                }

                if (exceededLimit || written != expectedChunkLength)
                {
                    fs.SetLength(offset);
                    await fs.FlushAsync(cancellationToken);
                    return new BackupUploadChunkResult(false, chunkIndex, session.BytesReceived, session.ExpectedTotalBytes,
                        $"Chunk {chunkIndex} transfer was incomplete or oversized ({written} of {expectedChunkLength} bytes).", "CHUNK_SIZE_MISMATCH");
                }

                await fs.FlushAsync(cancellationToken);

                // Chunk 0 Fail-Fast: validate archive magic and (if encrypted) verify SPOKES_MASTER_PASSWORD immediately
                if (chunkIndex == 0)
                {
                    fs.Seek(0, SeekOrigin.Begin);
                    var headerValidationError = ValidateArchiveHeaderOnChunkZero(fs);
                    if (headerValidationError != null)
                    {
                        fs.Close();
                        RemoveAndDeleteSession(session);
                        return new BackupUploadChunkResult(false, chunkIndex, 0, session.ExpectedTotalBytes,
                            headerValidationError.Value.Message, headerValidationError.Value.Code);
                    }
                }
            }

            if (chunkIndex == session.NextExpectedChunkIndex)
            {
                session.NextExpectedChunkIndex++;
            }

            session.BytesReceived = Math.Max(session.BytesReceived, offset + written);
            session.LastActivityUtc = DateTime.UtcNow;

            return new BackupUploadChunkResult(true, chunkIndex, session.BytesReceived, session.ExpectedTotalBytes, null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed appending chunk {ChunkIndex} for upload {UploadId}", chunkIndex, uploadId);
            return new BackupUploadChunkResult(false, chunkIndex, session.BytesReceived, session.ExpectedTotalBytes, ex.Message, "CHUNK_WRITE_FAILED");
        }
        finally
        {
            session.Gate.Release();
        }
    }

    public async Task<BackupUploadCompleteResult> CompleteSessionAsync(
        string uploadId,
        BackupUploadScope scope,
        string? ownerUserId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(uploadId) || !_sessions.TryGetValue(uploadId, out var session))
        {
            return new BackupUploadCompleteResult(false, null, null, 0, "Backup upload session not found or expired.", "SESSION_NOT_FOUND");
        }

        if (!MatchesOwnership(session, scope, ownerUserId))
        {
            return new BackupUploadCompleteResult(false, null, null, 0, "Not authorized to finalize this backup upload session.", "SESSION_FORBIDDEN");
        }

        await session.Gate.WaitAsync(cancellationToken);
        try
        {
            if (session.IsConsumed)
            {
                return new BackupUploadCompleteResult(false, null, null, 0, "Backup upload has already been consumed.", "SESSION_CONSUMED");
            }

            if (session.NextExpectedChunkIndex < session.TotalChunks || session.BytesReceived != session.ExpectedTotalBytes)
            {
                return new BackupUploadCompleteResult(false, null, null, session.BytesReceived,
                    $"Backup upload is incomplete ({session.BytesReceived} of {session.ExpectedTotalBytes} bytes received).", "UPLOAD_INCOMPLETE");
            }

            if (!File.Exists(session.StagedFilePath))
            {
                RemoveAndDeleteSession(session);
                return new BackupUploadCompleteResult(false, null, null, 0, "Staged backup file is missing from disk.", "STAGED_FILE_MISSING");
            }

            var actualLength = new FileInfo(session.StagedFilePath).Length;
            if (actualLength != session.ExpectedTotalBytes)
            {
                RemoveAndDeleteSession(session);
                return new BackupUploadCompleteResult(false, null, null, actualLength,
                    $"Staged backup size mismatch ({actualLength} vs expected {session.ExpectedTotalBytes} bytes).", "SIZE_MISMATCH");
            }

            session.IsCompleted = true;
            session.LastActivityUtc = DateTime.UtcNow;

            _logger.LogInformation("Completed chunked backup upload {UploadId} ({FileName}, {Size} bytes)",
                uploadId, session.FileName, actualLength);

            return new BackupUploadCompleteResult(true, uploadId, session.FileName, actualLength, null);
        }
        finally
        {
            session.Gate.Release();
        }
    }

    public async Task<bool> CancelSessionAsync(
        string uploadId,
        BackupUploadScope scope,
        string? ownerUserId)
    {
        if (string.IsNullOrWhiteSpace(uploadId) || !_sessions.TryGetValue(uploadId, out var session))
        {
            return false;
        }

        if (!MatchesOwnership(session, scope, ownerUserId))
        {
            return false;
        }

        await session.Gate.WaitAsync();
        try
        {
            RemoveAndDeleteSession(session);
            return true;
        }
        finally
        {
            session.Gate.Release();
        }
    }

    public async Task<BackupRestoreResult> RestoreStagedUploadAsync(
        string uploadId,
        BackupUploadScope expectedScope,
        string? expectedOwnerUserId,
        ILogger? logger = null,
        CancellationToken cancellationToken = default,
        IProgress<BackupRestoreProgress>? progress = null,
        string? backupPassword = null)
    {
        if (string.IsNullOrWhiteSpace(uploadId) || !_sessions.TryGetValue(uploadId, out var session))
        {
            return BackupRestoreResult.Failed("Uploaded backup session not found or expired.");
        }

        if (!MatchesOwnership(session, expectedScope, expectedOwnerUserId))
        {
            return BackupRestoreResult.Failed("Not authorized to restore this uploaded backup.");
        }

        string stagedPath;
        await session.Gate.WaitAsync(cancellationToken);
        try
        {
            if (!session.IsCompleted || session.IsConsumed)
            {
                return BackupRestoreResult.Failed("Uploaded backup is not ready or has already been consumed.");
            }

            session.IsConsumed = true;
            stagedPath = session.StagedFilePath;
            _sessions.TryRemove(uploadId, out _);
        }
        finally
        {
            session.Gate.Release();
        }

        try
        {
            return await _backupService.RestoreBackupAsync(stagedPath, logger, cancellationToken, progress, backupPassword);
        }
        finally
        {
            TryDeleteFile(stagedPath);
        }
    }

    private (string Message, string Code)? ValidateArchiveHeaderOnChunkZero(Stream stream)
    {
        if (stream.Length < 4)
        {
            return ("Invalid backup file format. File is too small to be a valid archive.", "INVALID_BACKUP_FORMAT");
        }

        if (_encryption.IsEncryptedArchive(stream))
        {
            stream.Position = 0;
            var masterPassword = _backupService.ResolveMasterPasswordForValidation();
            if (string.IsNullOrWhiteSpace(masterPassword))
            {
                return ("Backup archive is encrypted, but SPOKES_MASTER_PASSWORD is not configured on this server.", "MASTER_PASSWORD_MISSING");
            }

            if (!_encryption.VerifyMasterPassword(stream, masterPassword))
            {
                return ("Backup master password does not match SPOKES_MASTER_PASSWORD.", "MASTER_PASSWORD_MISMATCH");
            }

            return null;
        }

        stream.Position = 0;
        var magic = new byte[4];
        int read = stream.Read(magic, 0, 4);
        bool isZip = read == 4 &&
                     (magic.AsSpan().SequenceEqual(ZipLocalFileMagic) ||
                      magic.AsSpan().SequenceEqual(ZipEmptyArchiveMagic));

        if (!isZip)
        {
            return ("Invalid backup file format. Expected a .zip or encrypted .spkbackup archive.", "INVALID_BACKUP_FORMAT");
        }

        return null;
    }

    private static bool MatchesOwnership(UploadSession session, BackupUploadScope scope, string? ownerUserId)
    {
        if (session.Scope != scope) return false;
        if (scope == BackupUploadScope.Setup) return true;
        return !string.IsNullOrWhiteSpace(ownerUserId) &&
               string.Equals(session.OwnerUserId, ownerUserId, StringComparison.Ordinal);
    }

    private void RemoveAndDeleteSession(UploadSession session)
    {
        _sessions.TryRemove(session.UploadId, out _);
        TryDeleteFile(session.StagedFilePath);
    }

    private void PurgeExpiredSessions()
    {
        var cutoff = DateTime.UtcNow - SessionIdleTimeout;
        foreach (var kvp in _sessions)
        {
            if (kvp.Value.LastActivityUtc < cutoff && _sessions.TryRemove(kvp.Key, out var expired))
            {
                _logger.LogInformation("Purging expired backup upload session {UploadId}", expired.UploadId);
                TryDeleteFile(expired.StagedFilePath);
            }
        }
    }

    private static void TryDeleteFile(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch { }
    }

    private sealed class UploadSession
    {
        public required string UploadId { get; init; }
        public required BackupUploadScope Scope { get; init; }
        public string? OwnerUserId { get; init; }
        public required string FileName { get; init; }
        public required long ExpectedTotalBytes { get; init; }
        public required int ChunkSizeBytes { get; init; }
        public required int TotalChunks { get; init; }
        public required string StagedFilePath { get; init; }
        public int NextExpectedChunkIndex { get; set; }
        public long BytesReceived { get; set; }
        public bool IsCompleted { get; set; }
        public bool IsConsumed { get; set; }
        public DateTime LastActivityUtc { get; set; }
        public SemaphoreSlim Gate { get; } = new(1, 1);
    }
}
