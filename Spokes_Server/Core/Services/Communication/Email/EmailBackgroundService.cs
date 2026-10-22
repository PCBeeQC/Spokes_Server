using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Spokes_Server.Core.Data.Repositories.Communication;
using Spokes_Server.Core.Data.Repositories.HR;
using Spokes_Server.Core.Models.HR;

namespace Spokes_Server.Core.Services.Communication.Email;

public class EmailBackgroundService : BackgroundService
{
    private const int MaxChannelCapacity = 500;
    private const int WorkerCount = 2;

    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<EmailBackgroundService> _logger;
    private readonly EmailIdleService _idleService;

    // Track which employees already have IDLE running
    private readonly HashSet<string> _idleStarted = new();

    private readonly Channel<(string EmployeeId, string FolderPath)> _idleSyncChannel =
        Channel.CreateBounded<(string EmployeeId, string FolderPath)>(new BoundedChannelOptions(MaxChannelCapacity)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = false,
            SingleWriter = false
        });

    private readonly ConcurrentDictionary<(string EmployeeId, string FolderPath), byte> _queuedSyncs = new();
    private readonly CancellationTokenSource _cts = new();
    private Task[]? _workerTasks;

    public EmailBackgroundService(IServiceProvider serviceProvider, ILogger<EmailBackgroundService> logger, EmailIdleService idleService)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
        _idleService = idleService;

        // Subscribe to IDLE events — runs server-side, independent of any UI component
        _idleService.MessageReceived += OnIdleMessageReceived;
        _idleService.MessageFlagsChanged += OnIdleFlagsChanged;
    }

    private void OnIdleMessageReceived(string employeeId, string folderPath, MailKit.UniqueId uid)
    {
        SyncFolderFromIdle(employeeId, folderPath, "new message");
    }

    private void OnIdleFlagsChanged(string employeeId, string folderPath, MailKit.UniqueId uid)
    {
        SyncFolderFromIdle(employeeId, folderPath, "flags change");
    }

    private void SyncFolderFromIdle(string employeeId, string folderPath, string eventName)
    {
        _logger.LogInformation("[EmailBG] IDLE detected {EventName} in {FolderPath} for {EmployeeId}. Enqueueing sync...", eventName, folderPath, employeeId);

        var key = (employeeId, folderPath);
        if (_queuedSyncs.TryAdd(key, 0))
        {
            if (!_idleSyncChannel.Writer.TryWrite(key))
            {
                _queuedSyncs.TryRemove(key, out _);
                _logger.LogWarning("[EmailBG] IDLE sync channel is full. Dropping sync request for {EmployeeId} {FolderPath}", employeeId, folderPath);
            }
        }
    }

    private async Task ProcessWorkerLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (await _idleSyncChannel.Reader.WaitToReadAsync(cancellationToken))
            {
                while (_idleSyncChannel.Reader.TryRead(out var item))
                {
                    var (employeeId, folderPath) = item;
                    _queuedSyncs.TryRemove((employeeId, folderPath), out _);

                    try
                    {
                        using var scope = _serviceProvider.CreateScope();
                        var syncService = scope.ServiceProvider.GetRequiredService<IEmailSyncService>();
                        await syncService.SyncSingleFolderAsync(employeeId, folderPath, cancellationToken);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        return;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "[EmailBG] IDLE sync failed for {EmployeeId} {FolderPath}", employeeId, folderPath);
                    }
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Graceful shutdown
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[EmailBG] Error in IDLE sync worker loop");
        }
    }

    protected virtual List<Employee> GetSyncEmployees(EmployeeRepository repo)
    {
        return repo.GetAll()
            .Where(e => !string.IsNullOrEmpty(e.Email) && !string.IsNullOrEmpty(e.EncryptedEmailPassword))
            .ToList();
    }

    protected virtual Task WaitBeforeNextPollAsync(CancellationToken stoppingToken)
        => Task.Delay(TimeSpan.FromMinutes(5), stoppingToken);

    protected virtual Task WaitOnStartupAsync(CancellationToken stoppingToken)
        => Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Email Background Service started.");

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, _cts.Token);
        var effectiveToken = linkedCts.Token;

        var workerTasks = new Task[WorkerCount];
        for (int i = 0; i < WorkerCount; i++)
        {
            workerTasks[i] = Task.Run(() => ProcessWorkerLoopAsync(effectiveToken), effectiveToken);
        }
        _workerTasks = workerTasks;

        try
        {
            await WaitOnStartupAsync(effectiveToken);
        }
        catch (OperationCanceledException) when (effectiveToken.IsCancellationRequested) { return; }

        while (!effectiveToken.IsCancellationRequested)
        {
            try
            {
                await SyncPollCycleAsync(effectiveToken);
            }
            catch (OperationCanceledException) when (effectiveToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in Email Background Loop");
            }

            try
            {
                await WaitBeforeNextPollAsync(effectiveToken);
            }
            catch (OperationCanceledException) { break; }
        }

        _idleSyncChannel.Writer.TryComplete();
        try
        {
            await Task.WhenAll(workerTasks);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[EmailBG] Exception waiting for worker tasks to complete in ExecuteAsync");
        }
    }

    private async Task SyncPollCycleAsync(CancellationToken effectiveToken)
    {
        using var scope = _serviceProvider.CreateScope();
        var employeesRepo = scope.ServiceProvider.GetRequiredService<EmployeeRepository>();
        var foldersRepo = scope.ServiceProvider.GetRequiredService<EmailFolderRepository>();
        var messagesRepo = scope.ServiceProvider.GetRequiredService<EmailMessageRepository>();
        var syncService = scope.ServiceProvider.GetRequiredService<IEmailSyncService>();

        var employees = GetSyncEmployees(employeesRepo);

        foreach (var employee in employees)
        {
            if (effectiveToken.IsCancellationRequested) break;

            try
            {
                await SyncEmployeeCycleAsync(employee, syncService, foldersRepo, messagesRepo, effectiveToken);
            }
            catch (OperationCanceledException) when (effectiveToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error syncing email for employee {FullName}", employee.FullName);
            }
        }
    }

    private async Task SyncEmployeeCycleAsync(
        Employee employee,
        IEmailSyncService syncService,
        EmailFolderRepository foldersRepo,
        EmailMessageRepository messagesRepo,
        CancellationToken cancellationToken)
    {
        await syncService.SyncEmployeeAsync(employee.Id, cancellationToken);

        var folders = foldersRepo.GetByEmployee(employee.Id);
        ReconcileFolderUnreadCounts(employee.Id, folders, foldersRepo, messagesRepo);
        EnsureIdleWatchStarted(employee, folders);
    }

    private static void ReconcileFolderUnreadCounts(
        string employeeId,
        IEnumerable<EmailFolder> folders,
        EmailFolderRepository foldersRepo,
        EmailMessageRepository messagesRepo)
    {
        foreach (var folder in folders)
        {
            var indexCount = messagesRepo.GetUnreadCount(employeeId, folder.Path);
            if (folder.UnreadCount != indexCount)
            {
                folder.UnreadCount = indexCount;
                foldersRepo.Save(folder);
            }
        }
    }

    private void EnsureIdleWatchStarted(Employee employee, IEnumerable<EmailFolder> folders)
    {
        if (_idleStarted.Contains(employee.Id)) return;

        var inbox = folders.FirstOrDefault(f => f.IsInbox);
        if (inbox == null) return;

        _idleService.WatchFolder(employee.Id, inbox.Path);
        _idleStarted.Add(employee.Id);
        _logger.LogInformation("[EmailBG] Started IDLE watch for {FullName} on {InboxPath}", employee.FullName, inbox.Path);
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _idleSyncChannel.Writer.TryComplete();
        _cts.Cancel();
        if (_workerTasks != null)
        {
            try
            {
                await Task.WhenAll(_workerTasks).WaitAsync(cancellationToken);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "[EmailBG] Exception waiting for worker tasks in StopAsync");
            }
        }
        await base.StopAsync(cancellationToken);
    }

    public override void Dispose()
    {
        _idleService.MessageReceived -= OnIdleMessageReceived;
        _idleService.MessageFlagsChanged -= OnIdleFlagsChanged;
        _idleSyncChannel.Writer.TryComplete();
        _cts.Cancel();
        _cts.Dispose();
        base.Dispose();
    }
}
