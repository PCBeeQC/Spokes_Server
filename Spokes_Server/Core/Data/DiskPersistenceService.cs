using System.Threading.Channels;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Spokes_Server.Core.Data;

public record WriteJob(string FilePath, byte[]? Data, bool IsDelete);

public class DiskPersistenceService : BackgroundService
{
    private readonly Channel<WriteJob> _queue;
    private readonly ILogger<DiskPersistenceService> _logger;
    private readonly JsonSerializerOptions _jsonOptions;
    private readonly Spokes_Server.Core.Services.Core.IStorageHealthService? _storageHealth;
    private readonly Spokes_Server.Core.Services.Logging.ISystemLogService? _systemLog;

    public DiskPersistenceService(ILogger<DiskPersistenceService> logger)
        : this(logger, null, null)
    {
    }

    public DiskPersistenceService(
        ILogger<DiskPersistenceService> logger,
        Spokes_Server.Core.Services.Core.IStorageHealthService? storageHealth = null,
        Spokes_Server.Core.Services.Logging.ISystemLogService? systemLog = null)
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

    public void QueueDelete(string path) => _queue.Writer.TryWrite(new WriteJob(path, null, true));

    public ValueTask QueueWriteAsync(string path, object data)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(data, _jsonOptions);
        return _queue.Writer.WriteAsync(new WriteJob(path, bytes, false));
    }

    public ValueTask QueueDeleteAsync(string path) => _queue.Writer.WriteAsync(new WriteJob(path, null, true));

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Process until token is cancelled
        while (!stoppingToken.IsCancellationRequested)
        {
            // Wait for an item, but don't throw if channel is completed
            if (await _queue.Reader.WaitToReadAsync(stoppingToken))
            {
                while (_queue.Reader.TryRead(out var job))
                {
                    await ProcessJob(job);
                }
            }
        }

        // FLUSH ON EXIT: Process remaining items
        while (_queue.Reader.TryRead(out var job))
        {
            await ProcessJob(job);
        }
    }

    public void FlushAll()
    {
        while (_queue.Reader.TryRead(out var job))
        {
            ProcessJob(job).GetAwaiter().GetResult();
        }
    }

    private async Task ProcessJob(WriteJob job)
    {
        string? tempPath = null;
        try
        {
            if (job.IsDelete)
            {
                if (File.Exists(job.FilePath)) File.Delete(job.FilePath);
            }
            else
            {
                // Ensure folder exists
                var dir = Path.GetDirectoryName(job.FilePath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

                // Write to temp file first, then move (Atomic Save)
                // This prevents corrupted files if power cuts out mid-write
                tempPath = job.FilePath + ".tmp";
                await using (var stream = File.Create(tempPath))
                {
                    await stream.WriteAsync(job.Data);
                }

                File.Move(tempPath, job.FilePath, overwrite: true);
                tempPath = null;
            }

            _storageHealth?.RecordPersistenceSuccess();
        }
        catch (Exception ex)
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
                catch
                {
                    // Ignore failure to delete temp file in error path
                }
            }
        }
    }

}
