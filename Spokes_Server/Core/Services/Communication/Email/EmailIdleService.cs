using System;
using System.Collections.Concurrent;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Security;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Spokes_Server.Core.Data.Repositories.Core;
using Spokes_Server.Core.Data.Repositories.HR;
using Spokes_Server.Core.Services.Security;

namespace Spokes_Server.Core.Services.Communication.Email;

/// <summary>
/// Singleton service that manages persistent IMAP IDLE connections for realtime updates.
/// </summary>
public class EmailIdleService : IDisposable
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<EmailIdleService> _logger;
    private readonly IImapConnectionFactory _imapFactory;

    // Map: "EmployeeId:FolderPath" -> CancellationTokenSource (to cancel the running IDLE task)
    // We allow multiple active watchers per employee, differentiated by folder.
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _activeWatchers = new();

    // Events
    public event Action<string, string, UniqueId>? MessageReceived;
    public event Action<string, string, UniqueId>? MessageFlagsChanged;

    internal void TriggerMessageReceived(string employeeId, string folderPath, UniqueId uid) =>
        MessageReceived?.Invoke(employeeId, folderPath, uid);

    internal void TriggerMessageFlagsChanged(string employeeId, string folderPath, UniqueId uid) =>
        MessageFlagsChanged?.Invoke(employeeId, folderPath, uid);

    public EmailIdleService(IServiceScopeFactory scopeFactory, ILogger<EmailIdleService> logger, IImapConnectionFactory? imapFactory = null)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        if (imapFactory != null)
        {
            _imapFactory = imapFactory;
        }
        else
        {
            using var scope = scopeFactory.CreateScope();
            _imapFactory = scope.ServiceProvider.GetService<IImapConnectionFactory>()
                ?? new ImapConnectionFactory(
                    scope.ServiceProvider.GetRequiredService<CompanyProfileRepository>(),
                    scope.ServiceProvider.GetRequiredService<EmployeeRepository>(),
                    scope.ServiceProvider.GetRequiredService<EncryptionService>());
        }
    }

    /// <summary>
    /// Starts watching a specific folder for an employee. 
    /// Stops any existing watcher for that employee and folder first.
    /// </summary>
    public void WatchFolder(string employeeId, string folderPath)
    {
        var key = $"{employeeId}:{folderPath}";

        // 1. Stop existing watcher for this user/folder
        StopWatching(employeeId, folderPath);

        // 2. Start new watcher
        var cts = new CancellationTokenSource();
        _activeWatchers[key] = cts;

        // Fire and forget background task
        _ = Task.Run(() => IdleLoopAsync(employeeId, folderPath, cts), cts.Token);
    }

    /// <summary>
    /// Stops watching for an employee's specific folder.
    /// </summary>
    public void StopWatching(string employeeId, string folderPath)
    {
        var key = $"{employeeId}:{folderPath}";
        if (_activeWatchers.TryRemove(key, out var cts))
        {
            try
            {
                cts.Cancel();
                cts.Dispose();
            }
            catch (Exception ex) { _logger.LogError(ex, "StopWatching Error for {EmployeeId} - {FolderPath}", employeeId, folderPath); }
        }
    }

    private async Task IdleLoopAsync(string employeeId, string folderPath, CancellationTokenSource source)
    {
        var stopToken = source.Token;
        _logger.LogInformation("Starting IDLE for {EmployeeId} - {FolderPath}", employeeId, folderPath);

        try
        {
            using var client = await _imapFactory.CreateAuthenticatedClientAsync(employeeId, stopToken);
            if (client == null) return;

            var folder = await client.GetFolderAsync(folderPath, stopToken);
            await folder.OpenAsync(FolderAccess.ReadOnly, stopToken);

            CancellationTokenSource? done = null;

            // Hook up events
            folder.CountChanged += (s, e) =>
            {
                // This fires when count changes (new message or deleted). 
                _logger.LogInformation("Count Changed in {FolderPath}", folderPath);
                MessageReceived?.Invoke(employeeId, folderPath, UniqueId.MinValue);
                done?.Cancel(); // Break IDLE to trigger immediate UI refresh
            };

            // MessagesArrived might not be available or requires casting that fails in some contexts.
            // We rely on CountChanged which covers "New Messages".
            // For Flags, we keep MessageFlagsChanged.

            folder.MessageFlagsChanged += (s, e) =>
            {
                _logger.LogInformation("Flags Changed for {Index}", e.Index);
                MessageFlagsChanged?.Invoke(employeeId, folderPath, UniqueId.MinValue);
                done?.Cancel(); // Break IDLE to trigger immediate UI refresh
            };

            folder.MessageExpunged += (s, e) =>
            {
                _logger.LogInformation("Message Expunged at Index {Index} in {FolderPath}", e.Index, folderPath);
                MessageReceived?.Invoke(employeeId, folderPath, UniqueId.MinValue); // MessageReceived triggers SyncFolderAsync which handles deletions
                done?.Cancel(); // Break IDLE to trigger immediate UI refresh
            };

            while (!stopToken.IsCancellationRequested)
            {
                done = new CancellationTokenSource();
                using (var idleCts = new CancellationTokenSource(TimeSpan.FromMinutes(9)))
                using (var combined = CancellationTokenSource.CreateLinkedTokenSource(idleCts.Token, stopToken, done.Token))
                {
                    try
                    {
                        if (client.Capabilities.HasFlag(ImapCapabilities.Idle))
                        {
                            await client.IdleAsync(combined.Token);
                        }
                        else
                        {
                            // Fallback to NOOP polling
                            await Task.Delay(TimeSpan.FromMinutes(1), combined.Token);
                            await client.NoOpAsync(stopToken);
                        }
                    }
                    catch (OperationCanceledException ex) { _logger.LogInformation(ex, "Idle timeout or stop requested."); }
                    catch (ImapProtocolException ex) { _logger.LogWarning(ex, "Connection lost"); break; }
                    catch (IOException ex) { _logger.LogWarning(ex, "Socket lost"); break; }
                }
                done.Dispose();
            }

            await client.DisconnectAsync(true, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in EmailIdleService");
        }
        finally
        {
            var key = $"{employeeId}:{folderPath}";
            // Cleanup from active list if this task finishes naturally (e.g. error)
            // Only remove if it's OUR cts (handling race condition where new watcher started)
            if (_activeWatchers.TryGetValue(key, out var currentCts) && currentCts == source)
            {
                // We can safely remove it if it matches OUR source
                _activeWatchers.TryRemove(key, out _);
                source.Dispose();
            }
        }
    }

    public void Dispose()
    {
        foreach (var cts in _activeWatchers.Values)
        {
            try
            {
                if (!cts.IsCancellationRequested)
                {
                    cts.Cancel();
                }
            }
            catch (ObjectDisposedException) { }
            catch (AggregateException) { }

            try
            {
                cts.Dispose();
            }
            catch (ObjectDisposedException) { }
        }
        _activeWatchers.Clear();
    }
}
