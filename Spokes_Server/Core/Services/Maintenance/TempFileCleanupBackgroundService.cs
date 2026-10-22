using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Spokes_Server.Core.Services.Storage;

namespace Spokes_Server.Core.Services.Maintenance;

/// <summary>
/// Background service that periodically purges abandoned temporary upload files.
/// Runs once on boot and periodically thereafter, eliminating constructor-level background task launches.
/// </summary>
public class TempFileCleanupBackgroundService : BackgroundService
{
    private readonly IFileService _fileService;
    private readonly ILogger<TempFileCleanupBackgroundService> _logger;
    private readonly TimeSpan _cleanupInterval;
    private readonly TimeProvider _timeProvider;
    private readonly object _lock = new();
    private int _completedIterations;
    private TaskCompletionSource _nextIterationTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public event Action? OnIterationCompleted;
    public int CompletedIterations => Volatile.Read(ref _completedIterations);

    public TempFileCleanupBackgroundService(
        IFileService fileService,
        ILogger<TempFileCleanupBackgroundService> logger)
        : this(fileService, logger, TimeSpan.FromHours(1), TimeProvider.System)
    {
    }

    public TempFileCleanupBackgroundService(
        IFileService fileService,
        ILogger<TempFileCleanupBackgroundService> logger,
        TimeSpan cleanupInterval)
        : this(fileService, logger, cleanupInterval, TimeProvider.System)
    {
    }

    public TempFileCleanupBackgroundService(
        IFileService fileService,
        ILogger<TempFileCleanupBackgroundService> logger,
        TimeSpan cleanupInterval,
        TimeProvider? timeProvider)
    {
        _fileService = fileService;
        _logger = logger;
        _cleanupInterval = cleanupInterval;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public Task WaitForIterationAsync(CancellationToken cancellationToken = default)
    {
        return WaitForIterationAsync(1, cancellationToken);
    }

    public Task WaitForIterationAsync(int minimumCompletedIterations, CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            if (_completedIterations >= minimumCompletedIterations)
            {
                return Task.CompletedTask;
            }

            return _nextIterationTcs.Task.WaitAsync(cancellationToken);
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    _fileService.PurgeStaleTempFiles();
                    _logger.LogDebug("[TempFileCleanup] Completed stale temp file purge.");
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[TempFileCleanup] Error occurred while purging stale temp files.");
                }
                finally
                {
                    TaskCompletionSource tcsToSignal;
                    lock (_lock)
                    {
                        _completedIterations++;
                        tcsToSignal = _nextIterationTcs;
                        _nextIterationTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    }
                    tcsToSignal.TrySetResult();
                    OnIterationCompleted?.Invoke();
                }

                try
                {
                    await Task.Delay(_cleanupInterval, _timeProvider, stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
            }
        }
        finally
        {
            lock (_lock)
            {
                _nextIterationTcs.TrySetCanceled(stoppingToken);
            }
        }
    }
}
