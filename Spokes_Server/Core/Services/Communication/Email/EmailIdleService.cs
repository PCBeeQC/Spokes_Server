using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using MailKit;
using MailKit.Net.Imap;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Spokes_Server.Core.Data.Repositories.Communication;
using Spokes_Server.Core.Data.Repositories.Core;
using Spokes_Server.Core.Services.Security;

namespace Spokes_Server.Core.Services.Communication.Email;

/// <summary>
/// Singleton service that manages persistent IMAP IDLE connections per EmailAccount for realtime updates.
/// </summary>
public class EmailIdleService : IDisposable
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<EmailIdleService> _logger;
    private readonly IImapConnectionFactory _imapFactory;

    // Map: "AccountId:FolderPath" -> CancellationTokenSource
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _activeWatchers = new();

    public event Action<string, string, UniqueId>? MessageReceived;
    public event Action<string, string, UniqueId>? MessageFlagsChanged;

    internal void TriggerMessageReceived(string accountId, string folderPath, UniqueId uid) =>
        MessageReceived?.Invoke(accountId, folderPath, uid);

    internal void TriggerMessageFlagsChanged(string accountId, string folderPath, UniqueId uid) =>
        MessageFlagsChanged?.Invoke(accountId, folderPath, uid);

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
                    scope.ServiceProvider.GetRequiredService<EncryptionService>(),
                    scope.ServiceProvider.GetService<EmailAccountRepository>());
        }
    }

    public void WatchFolder(string accountId, string folderPath)
    {
        using (var scope = _scopeFactory.CreateScope())
        {
            var companyProfileRepo = scope.ServiceProvider.GetService<CompanyProfileRepository>();
            if (string.Equals(companyProfileRepo?.Get()?.Edition, "Family", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
        }

        var key = $"{accountId}:{folderPath}";

        StopWatching(accountId, folderPath);

        var cts = new CancellationTokenSource();
        _activeWatchers[key] = cts;

        _ = Task.Run(() => IdleLoopAsync(accountId, folderPath, cts), cts.Token);
    }

    public void StopWatching(string accountId, string folderPath)
    {
        var key = $"{accountId}:{folderPath}";
        if (_activeWatchers.TryRemove(key, out var cts))
        {
            try
            {
                cts.Cancel();
                cts.Dispose();
            }
            catch (Exception ex) { _logger.LogError(ex, "StopWatching Error for {AccountId} - {FolderPath}", accountId, folderPath); }
        }
    }

    public void StopWatchingAccount(string accountId)
    {
        if (string.IsNullOrWhiteSpace(accountId)) return;
        var prefix = $"{accountId}:";
        foreach (var key in _activeWatchers.Keys)
        {
            if (key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
                _activeWatchers.TryRemove(key, out var cts))
            {
                try
                {
                    cts.Cancel();
                    cts.Dispose();
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "StopWatchingAccount Error for {Key}", key);
                }
            }
        }
    }

    private async Task IdleLoopAsync(string accountId, string folderPath, CancellationTokenSource source)
    {
        var stopToken = source.Token;
        _logger.LogInformation("Starting IDLE for {AccountId} - {FolderPath}", accountId, folderPath);

        try
        {
            using var client = await _imapFactory.CreateAuthenticatedClientAsync(accountId, stopToken);
            if (client == null) return;

            var folder = await client.GetFolderAsync(folderPath, stopToken);
            await folder.OpenAsync(FolderAccess.ReadOnly, stopToken);

            CancellationTokenSource? done = null;

            folder.CountChanged += (s, e) =>
            {
                _logger.LogInformation("Count Changed in {FolderPath}", folderPath);
                MessageReceived?.Invoke(accountId, folderPath, UniqueId.MinValue);
                done?.Cancel();
            };

            folder.MessageFlagsChanged += (s, e) =>
            {
                _logger.LogInformation("Flags Changed for {Index}", e.Index);
                MessageFlagsChanged?.Invoke(accountId, folderPath, UniqueId.MinValue);
                done?.Cancel();
            };

            folder.MessageExpunged += (s, e) =>
            {
                _logger.LogInformation("Message Expunged at Index {Index} in {FolderPath}", e.Index, folderPath);
                MessageReceived?.Invoke(accountId, folderPath, UniqueId.MinValue);
                done?.Cancel();
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
            var key = $"{accountId}:{folderPath}";
            if (_activeWatchers.TryGetValue(key, out var currentCts) && currentCts == source)
            {
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
