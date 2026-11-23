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
using Spokes_Server.Core.Data.Repositories.Core;
using Spokes_Server.Core.Models.Communication;

namespace Spokes_Server.Core.Services.Communication.Email;

public class EmailBackgroundService : BackgroundService
{
    private const int MaxChannelCapacity = 500;
    private const int WorkerCount = 2;

    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<EmailBackgroundService> _logger;
    private readonly EmailIdleService _idleService;

    private readonly HashSet<string> _idleStarted = new();

    private readonly Channel<(string AccountId, string FolderPath)> _idleSyncChannel =
        Channel.CreateBounded<(string AccountId, string FolderPath)>(new BoundedChannelOptions(MaxChannelCapacity)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = false,
            SingleWriter = false
        });

    private readonly ConcurrentDictionary<(string AccountId, string FolderPath), byte> _queuedSyncs = new();
    private readonly CancellationTokenSource _cts = new();
    private Task[]? _workerTasks;

    public EmailBackgroundService(IServiceProvider serviceProvider, ILogger<EmailBackgroundService> logger, EmailIdleService idleService)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
        _idleService = idleService;

        _idleService.MessageReceived += OnIdleMessageReceived;
        _idleService.MessageFlagsChanged += OnIdleFlagsChanged;
    }

    private void OnIdleMessageReceived(string accountId, string folderPath, MailKit.UniqueId uid)
    {
        SyncFolderFromIdle(accountId, folderPath, "new message");
    }

    private void OnIdleFlagsChanged(string accountId, string folderPath, MailKit.UniqueId uid)
    {
        SyncFolderFromIdle(accountId, folderPath, "flags change");
    }

    private void SyncFolderFromIdle(string accountId, string folderPath, string eventName)
    {
        _logger.LogInformation("[EmailBG] IDLE detected {EventName} in {FolderPath} for {AccountId}. Enqueueing sync...", eventName, folderPath, accountId);

        var key = (accountId, folderPath);
        if (_queuedSyncs.TryAdd(key, 0))
        {
            if (!_idleSyncChannel.Writer.TryWrite(key))
            {
                _queuedSyncs.TryRemove(key, out _);
                _logger.LogWarning("[EmailBG] IDLE sync channel is full. Dropping sync request for {AccountId} {FolderPath}", accountId, folderPath);
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
                    var (accountId, folderPath) = item;
                    _queuedSyncs.TryRemove((accountId, folderPath), out _);

                    try
                    {
                        using var scope = _serviceProvider.CreateScope();
                        var syncService = scope.ServiceProvider.GetRequiredService<IEmailSyncService>();
                        await syncService.SyncSingleFolderAsync(accountId, folderPath, cancellationToken);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        return;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "[EmailBG] IDLE sync failed for {AccountId} {FolderPath}", accountId, folderPath);
                    }
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[EmailBG] Error in IDLE sync worker loop");
        }
    }

    protected virtual List<EmailAccount> GetSyncAccounts(EmailAccountRepository repo)
    {
        return GetSyncAccounts(repo, _serviceProvider.GetService<EmailInboxRepository>());
    }

    protected virtual List<EmailAccount> GetSyncAccounts(EmailAccountRepository repo, EmailInboxRepository? inboxesRepo)
    {
        return repo.GetAll()
            .Where(a => a.Enabled &&
                        !a.IsArchived &&
                        (!string.IsNullOrEmpty(a.Username) || !string.IsNullOrEmpty(a.EmailAddress)) &&
                        !string.IsNullOrEmpty(a.EncryptedPassword) &&
                        EmailInboxService.HasWebmailConsumers(a, inboxesRepo))
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
        var companyProfileRepo = scope.ServiceProvider.GetService<CompanyProfileRepository>();
        if (string.Equals(companyProfileRepo?.Get()?.Edition, "Family", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var accountsRepo = scope.ServiceProvider.GetRequiredService<EmailAccountRepository>();
        var inboxesRepo = scope.ServiceProvider.GetService<EmailInboxRepository>();
        var foldersRepo = scope.ServiceProvider.GetRequiredService<EmailFolderRepository>();
        var messagesRepo = scope.ServiceProvider.GetRequiredService<EmailMessageRepository>();
        var syncService = scope.ServiceProvider.GetRequiredService<IEmailSyncService>();
        var syncStateService = scope.ServiceProvider.GetService<EmailSyncStateService>();

        var accounts = GetSyncAccounts(accountsRepo, inboxesRepo);
        var activeIds = accounts.Select(a => a.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var watchedId in _idleStarted.ToList())
        {
            if (!activeIds.Contains(watchedId))
            {
                _idleStarted.Remove(watchedId);
                _idleService.StopWatchingAccount(watchedId);
            }
        }

        foreach (var account in accounts)
        {
            if (effectiveToken.IsCancellationRequested) break;

            try
            {
                await SyncAccountCycleAsync(account, syncService, foldersRepo, messagesRepo, syncStateService, effectiveToken);
            }
            catch (OperationCanceledException) when (effectiveToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error syncing email for account {Email}", account.EmailAddress);
            }
        }
    }

    private async Task SyncAccountCycleAsync(
        EmailAccount account,
        IEmailSyncService syncService,
        EmailFolderRepository foldersRepo,
        EmailMessageRepository messagesRepo,
        EmailSyncStateService? syncStateService,
        CancellationToken cancellationToken)
    {
        await syncService.SyncAccountAsync(account.Id, cancellationToken);

        var folders = foldersRepo.GetByAccount(account.Id);
        if (ReconcileFolderUnreadCounts(account.Id, folders, foldersRepo, messagesRepo))
        {
            syncStateService?.NotifyUnreadCountChanged(account.Id);
        }
        EnsureIdleWatchStarted(account, folders);
    }

    private static bool ReconcileFolderUnreadCounts(
        string accountId,
        IEnumerable<EmailFolder> folders,
        EmailFolderRepository foldersRepo,
        EmailMessageRepository messagesRepo)
    {
        bool changed = false;
        foreach (var folder in folders)
        {
            var indexCount = messagesRepo.GetUnreadCount(accountId, folder.Path);
            if (folder.UnreadCount != indexCount)
            {
                folder.UnreadCount = indexCount;
                foldersRepo.Save(folder);
                changed = true;
            }
        }
        return changed;
    }

    private void EnsureIdleWatchStarted(EmailAccount account, IEnumerable<EmailFolder> folders)
    {
        if (_idleStarted.Contains(account.Id)) return;

        var inbox = folders.FirstOrDefault(f => f.IsInbox);
        if (inbox == null) return;

        _idleService.WatchFolder(account.Id, inbox.Path);
        _idleStarted.Add(account.Id);
        _logger.LogInformation("[EmailBG] Started IDLE watch for {Email} on {InboxPath}", account.EmailAddress, inbox.Path);
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
