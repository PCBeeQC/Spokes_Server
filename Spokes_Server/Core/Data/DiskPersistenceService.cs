using System.Threading.Channels;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Spokes_Server.Core.Data;

public record WriteJob(string FilePath, byte[]? Data, bool IsDelete, bool IsDirectory = false);

public class DiskPersistenceService : BackgroundService
{
    private readonly Channel<WriteJob> _queue;
    private readonly SemaphoreSlim _drainLock = new(1, 1);
    private readonly ILogger<DiskPersistenceService> _logger;
    private readonly JsonSerializerOptions _jsonOptions;
    private readonly IStorageHealthNotifier? _storageHealth;
    private readonly IStoragePersistenceLogger? _systemLog;
    private volatile RepositoryFileCascade? _fileCascade;

    public void AttachFileCascade(RepositoryFileCascade cascade)
    {
        _fileCascade = cascade ?? throw new ArgumentNullException(nameof(cascade));
    }

    internal void NotifyEntityDeleted(IHasAssociatedFiles entity)
    {
        _fileCascade?.HandleEntityDeleted(entity);
    }

    public DiskPersistenceService(ILogger<DiskPersistenceService> logger)
        : this(logger, null, null)
    {
    }

    public DiskPersistenceService(
        ILogger<DiskPersistenceService> logger,
        IStorageHealthNotifier? storageHealth = null,
        IStoragePersistenceLogger? systemLog = null)
    {
        _logger = logger;
        _storageHealth = storageHealth;
        _systemLog = systemLog;
        // Unbounded = Infinite buffer. Fast UI, but consumes RAM if disk is slow.
        _queue = Channel.CreateUnbounded<WriteJob>();
        _jsonOptions = new() { WriteIndented = true };
    }

    // UI calls this. Returns IMMEDIATELY.
    public void QueueWrite(string path, object data)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(data, _jsonOptions);
        _queue.Writer.TryWrite(new WriteJob(path, bytes, false));
    }

    /// <summary>
    /// Queues pre-serialized or encrypted raw bytes for atomic background persistence (.tmp + move).
    /// </summary>
    public void QueueWriteBytes(string path, byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        _queue.Writer.TryWrite(new WriteJob(path, data, false));
    }

    public void QueueDelete(string path) => _queue.Writer.TryWrite(new WriteJob(path, null, true, false));

    public void QueueDeleteDirectory(string directoryPath) => _queue.Writer.TryWrite(new WriteJob(directoryPath, null, true, true));


    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            // Process until token is cancelled
            while (!stoppingToken.IsCancellationRequested)
            {
                // Wait for an item, but don't throw if channel is completed
                if (await _queue.Reader.WaitToReadAsync(stoppingToken))
                {
                    await DrainQueueAsync();
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }

        // FLUSH ON EXIT: Process remaining items
        await DrainQueueAsync();
    }

    public Task FlushAllAsync() => DrainQueueAsync();

    private async Task DrainQueueAsync()
    {
        await _drainLock.WaitAsync().ConfigureAwait(false);
        try
        {
            while (_queue.Reader.TryRead(out var job))
            {
                await ProcessJob(job).ConfigureAwait(false);
            }
        }
        finally
        {
            _drainLock.Release();
        }
    }

    [Obsolete("Prefer FlushAllAsync() to avoid blocking thread pool threads.")]
    public void FlushAll() => DrainQueueAsync().GetAwaiter().GetResult();

    private async Task ProcessJob(WriteJob job)
    {
        string? tempPath = null;
        try
        {
            if (job.IsDelete)
            {
                DeleteTarget(job);
            }
            else
            {
                // Ensure folder exists
                var dir = Path.GetDirectoryName(job.FilePath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

                // Write to unique temp file first, then move (Atomic Save)
                // This prevents corrupted files if power cuts out mid-write and avoids .tmp contention
                tempPath = CreateUniqueTempPath(job.FilePath);
                await using (var stream = File.Create(tempPath))
                {
                    await stream.WriteAsync(job.Data).ConfigureAwait(false);
                }

                File.Move(tempPath, job.FilePath, overwrite: true);
                tempPath = null;
            }

            _storageHealth?.RecordPersistenceSuccess();
        }
        catch (Exception ex)
        {
            HandlePersistenceFailure(job, ex, tempPath);
        }
    }

    private static string CreateUniqueTempPath(string filePath) => $"{filePath}.{Guid.NewGuid():N}.tmp";

    private static void DeleteTarget(WriteJob job)
    {
        if (job.IsDirectory)
        {
            if (Directory.Exists(job.FilePath)) Directory.Delete(job.FilePath, true);
        }
        else
        {
            if (File.Exists(job.FilePath)) File.Delete(job.FilePath);
        }
    }

    private void HandlePersistenceFailure(WriteJob job, Exception ex, string? tempPath)
    {
        _logger.LogError(ex, "Failed to persist file: {Path}", job.FilePath);
        _storageHealth?.RecordPersistenceFailure();
        _systemLog?.LogError("Storage", $"Failed to persist file {job.FilePath}: {ex.Message}", ex.ToString());

        if (tempPath != null)
        {
            try
            {
                if (File.Exists(tempPath)) File.Delete(tempPath);
            }
            catch (Exception cleanupEx)
            {
                _systemLog?.Log("Storage", "Warning", $"Failed to delete temp file {tempPath}: {cleanupEx.Message}");
            }
        }
    }

}
