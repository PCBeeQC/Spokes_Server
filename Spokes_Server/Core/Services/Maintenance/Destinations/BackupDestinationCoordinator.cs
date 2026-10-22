using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Spokes_Server.Core.Configuration;
using Spokes_Server.Core.Data.Repositories.Core;

namespace Spokes_Server.Core.Services.Maintenance.Destinations;

public class BackupDestinationCoordinator
{
    private readonly IEnumerable<IBackupDestinationProvider> _providers;
    private readonly CompanyProfileRepository _profiles;
    private readonly ILogger<BackupDestinationCoordinator>? _logger;
    private readonly ConcurrentDictionary<BackupDestinationType, DestinationUploadStatus> _statuses = new();

    public event Action<BackupDestinationType, DestinationUploadStatus>? OnDestinationStatusChanged;

    public BackupDestinationCoordinator(
        IEnumerable<IBackupDestinationProvider> providers,
        CompanyProfileRepository profiles,
        ILogger<BackupDestinationCoordinator>? logger = null)
    {
        _providers = providers;
        _profiles = profiles;
        _logger = logger;

        foreach (BackupDestinationType type in Enum.GetValues<BackupDestinationType>())
        {
            _statuses[type] = new DestinationUploadStatus { DestinationType = type, State = RemoteUploadState.Idle };
        }
    }

    public DestinationUploadStatus GetStatus(BackupDestinationType type) =>
        _statuses.GetOrAdd(type, t => new DestinationUploadStatus { DestinationType = t, State = RemoteUploadState.Idle });

    public IBackupDestinationProvider? GetProvider(BackupDestinationType type) =>
        _providers.FirstOrDefault(p => p.DestinationType == type);

    public async Task<Dictionary<BackupDestinationType, BackupUploadResult>> DispatchUploadAsync(
        string localBackupPath,
        string remoteFileName,
        CancellationToken cancellationToken = default)
    {
        var profile = _profiles.Get();
        var backupConfig = profile?.Backup;
        var results = new Dictionary<BackupDestinationType, BackupUploadResult>();

        if (backupConfig == null) return results;

        var enabledProviders = _providers.Where(provider => provider.DestinationType switch
        {
            BackupDestinationType.LocalMount => backupConfig.SecondaryMount.IsEnabled,
            BackupDestinationType.S3 => backupConfig.S3.IsEnabled,
            BackupDestinationType.GoogleDrive => backupConfig.GoogleDrive.IsEnabled,
            BackupDestinationType.WebDav => backupConfig.WebDav.IsEnabled,
            BackupDestinationType.Sftp => backupConfig.Sftp.IsEnabled,
            _ => false
        }).ToList();

        if (enabledProviders.Count == 0) return results;

        // Initialize all enabled destinations as Uploading / 0%
        foreach (var provider in enabledProviders)
        {
            var status = GetStatus(provider.DestinationType);
            status.State = RemoteUploadState.Uploading;
            status.Percent = 0;
            status.BytesTransferred = 0;
            status.TotalBytes = 0;
            status.ErrorMessage = null;
            OnDestinationStatusChanged?.Invoke(provider.DestinationType, status);
        }

        // Run all enabled destination uploads concurrently in parallel
        var uploadTasks = enabledProviders.Select(provider =>
            UploadSingleDestinationAsync(provider, localBackupPath, remoteFileName, backupConfig, cancellationToken));

        var completedResults = await Task.WhenAll(uploadTasks);

        foreach (var (type, result) in completedResults)
        {
            results[type] = result;
        }

        return results;
    }

    private async Task<(BackupDestinationType Type, BackupUploadResult Result)> UploadSingleDestinationAsync(
        IBackupDestinationProvider provider,
        string localBackupPath,
        string remoteFileName,
        BackupConfig backupConfig,
        CancellationToken cancellationToken)
    {
        var status = GetStatus(provider.DestinationType);

        try
        {
            _logger?.LogInformation("[BackupDispatcher] Uploading {File} to destination {Destination} in parallel...", remoteFileName, provider.DestinationType);

            var lastReportTime = DateTime.MinValue;
            var lastReportPct = -1.0;
            var progress = new Progress<BackupUploadProgress>(p =>
            {
                // Guard against race conditions: do not overwrite terminal states
                if (status.State == RemoteUploadState.Success || status.State == RemoteUploadState.Failed)
                {
                    return;
                }

                var now = DateTime.UtcNow;
                bool shouldReport = p.Percent >= 100 ||
                                    Math.Abs(p.Percent - lastReportPct) >= 1.0 ||
                                    (now - lastReportTime).TotalMilliseconds >= 150;
                if (shouldReport)
                {
                    lastReportTime = now;
                    lastReportPct = p.Percent;
                    status.Percent = p.Percent;
                    status.BytesTransferred = p.BytesTransferred;
                    status.TotalBytes = p.TotalBytes;
                    status.State = RemoteUploadState.Uploading;
                    OnDestinationStatusChanged?.Invoke(provider.DestinationType, status);
                }
            });

            var uploadResult = await provider.UploadAsync(localBackupPath, remoteFileName, progress, cancellationToken);

            if (uploadResult.Success)
            {
                status.State = RemoteUploadState.Success;
                status.Percent = 100;
                if (status.TotalBytes > 0)
                {
                    status.BytesTransferred = status.TotalBytes;
                }
                else if (uploadResult.SizeBytes > 0)
                {
                    status.TotalBytes = uploadResult.SizeBytes;
                    status.BytesTransferred = uploadResult.SizeBytes;
                }
                status.ErrorMessage = null;
                status.LastCompletedAt = DateTime.UtcNow;
                OnDestinationStatusChanged?.Invoke(provider.DestinationType, status);

                int retention = provider.DestinationType switch
                {
                    BackupDestinationType.LocalMount => backupConfig.SecondaryMount.RetentionCount,
                    BackupDestinationType.S3 => backupConfig.S3.RetentionCount,
                    BackupDestinationType.GoogleDrive => backupConfig.GoogleDrive.RetentionCount,
                    BackupDestinationType.WebDav => backupConfig.WebDav.RetentionCount,
                    BackupDestinationType.Sftp => backupConfig.Sftp.RetentionCount,
                    _ => 5
                };

                try
                {
                    await provider.PruneBackupsAsync(retention, cancellationToken);
                }
                catch (Exception pruneEx)
                {
                    _logger?.LogWarning(pruneEx, "[BackupDispatcher] Error pruning backups on {Destination}", provider.DestinationType);
                }

                return (provider.DestinationType, uploadResult);
            }
            else
            {
                status.State = RemoteUploadState.Failed;
                status.ErrorMessage = uploadResult.ErrorMessage;
                OnDestinationStatusChanged?.Invoke(provider.DestinationType, status);
                return (provider.DestinationType, uploadResult);
            }
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "[BackupDispatcher] Error dispatching to {Destination}", provider.DestinationType);
            status.State = RemoteUploadState.Failed;
            status.ErrorMessage = ex.Message;
            OnDestinationStatusChanged?.Invoke(provider.DestinationType, status);
            return (provider.DestinationType, BackupUploadResult.Failed(ex.Message));
        }
    }

    public async Task<List<RemoteBackupItem>> ListAllRemoteBackupsAsync(CancellationToken cancellationToken = default)
    {
        var all = new List<RemoteBackupItem>();
        var profile = _profiles.Get();
        var backupConfig = profile?.Backup;
        if (backupConfig == null) return all;

        foreach (var provider in _providers)
        {
            bool isEnabled = provider.DestinationType switch
            {
                BackupDestinationType.LocalMount => backupConfig.SecondaryMount.IsEnabled,
                BackupDestinationType.S3 => backupConfig.S3.IsEnabled,
                BackupDestinationType.GoogleDrive => backupConfig.GoogleDrive.IsEnabled,
                BackupDestinationType.WebDav => backupConfig.WebDav.IsEnabled,
                BackupDestinationType.Sftp => backupConfig.Sftp.IsEnabled,
                _ => false
            };

            if (!isEnabled) continue;

            try
            {
                var items = await provider.ListBackupsAsync(cancellationToken);
                all.AddRange(items);
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "[BackupDispatcher] Error listing backups from {Destination}", provider.DestinationType);
            }
        }

        return all.OrderByDescending(i => i.CreatedAtUtc).ToList();
    }

    public async Task<TestConnectionResult> TestConnectionAsync(BackupDestinationType type, CancellationToken cancellationToken = default)
    {
        var provider = GetProvider(type);
        if (provider == null)
        {
            return TestConnectionResult.Fail($"Provider for {type} is not registered.");
        }
        return await provider.TestConnectionAsync(cancellationToken);
    }

    public async Task<string?> GetGoogleDriveAuthUrlAsync(string callbackUrl, CancellationToken cancellationToken = default)
    {
        if (GetProvider(BackupDestinationType.GoogleDrive) is GoogleDriveBackupProvider gDrive)
        {
            return await gDrive.GetRelayAuthUrlAsync(callbackUrl, cancellationToken);
        }
        return null;
    }
}
