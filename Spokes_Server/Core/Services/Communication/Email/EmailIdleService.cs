using Spokes_Server.Core.Services.Communication;
using Spokes_Server.Core.Services.Projects;
using Spokes_Server.Core.Services.Core;
using System.Collections.Concurrent;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Security;
using Spokes_Server.Core.Data.Repositories.Core;
using Spokes_Server.Core.Data.Repositories.Projects;
using Spokes_Server.Core.Data.Repositories.Accounting;
using Spokes_Server.Core.Data.Repositories.Communication;
using Spokes_Server.Core.Data.Repositories.HR;
using Spokes_Server.Core.Models.Core;
using Spokes_Server.Core.Models.Projects;
using Spokes_Server.Core.Models.Accounting;
using Spokes_Server.Core.Models.Communication;
using Spokes_Server.Core.Models.HR;
using Microsoft.Extensions.DependencyInjection;
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Spokes_Server.Core.Services.Communication.Email;

/// <summary>
/// Singleton service that manages persistent IMAP IDLE connections for realtime updates.
/// </summary>
public class EmailIdleService : IDisposable
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<EmailIdleService> _logger;

    // Map: "EmployeeId:FolderPath" -> CancellationTokenSource (to cancel the running IDLE task)
    // We allow multiple active watchers per employee, differentiated by folder.
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _activeWatchers = new();

    // Events
    public event Action<string, string, UniqueId>? MessageReceived;
    public event Action<string, string, UniqueId>? MessageFlagsChanged;

    public EmailIdleService(IServiceScopeFactory scopeFactory, ILogger<EmailIdleService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
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

        // Create a dedicated scope for DB access
        using var scope = _scopeFactory.CreateScope();
        var employeesRepo = scope.ServiceProvider.GetRequiredService<EmployeeRepository>();
        var companyProfileRepo = scope.ServiceProvider.GetRequiredService<CompanyProfileRepository>();
        var encryptionService = scope.ServiceProvider.GetRequiredService<EncryptionService>();

        var employee = employeesRepo.GetById(employeeId);
        if (employee == null) return;

        var username = employee.Email;
        var password = encryptionService.Decrypt(employee.EncryptedEmailPassword);
        var settings = companyProfileRepo.Get()?.EmailSettings;

        if (settings == null || string.IsNullOrEmpty(username) || string.IsNullOrEmpty(settings.ImapHost)) return;

        try
        {
            using var client = new ImapClient();
            // Accept all certs if needed for dev
            // client.ServerCertificateValidationCallback = (s, c, h, e) => true;

            await client.ConnectAsync(settings.ImapHost, settings.ImapPort, settings.ImapSsl ? SecureSocketOptions.SslOnConnect : SecureSocketOptions.Auto, stopToken);
            await client.AuthenticateAsync(username, password, stopToken);

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
            cts.Cancel();
            cts.Dispose();
        }
        _activeWatchers.Clear();
    }
}



