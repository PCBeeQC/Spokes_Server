using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Spokes_Server.Core.Data.Repositories.Core;

namespace Spokes_Server.Core.Services.Maintenance.Destinations;

public class LocalMountBackupProvider : IBackupDestinationProvider
{
    private readonly CompanyProfileRepository _profiles;
    private readonly ILogger<LocalMountBackupProvider>? _logger;

    public BackupDestinationType DestinationType => BackupDestinationType.LocalMount;

    public LocalMountBackupProvider(CompanyProfileRepository profiles, ILogger<LocalMountBackupProvider>? logger = null)
    {
        _profiles = profiles;
        _logger = logger;
    }

    public async Task<BackupUploadResult> UploadAsync(
        string localBackupPath,
        string remoteFileName,
        IProgress<BackupUploadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var config = _profiles.Get()?.Backup.SecondaryMount;
        if (config == null || !config.IsEnabled || string.IsNullOrWhiteSpace(config.MountPath))
        {
            return BackupUploadResult.Failed("Secondary mount destination is not configured or enabled.");
        }

        string? tempPath = null;
        try
        {
            Directory.CreateDirectory(config.MountPath);
            var destPath = Path.Combine(config.MountPath, remoteFileName);
            tempPath = destPath + ".tmp";

            var fileInfo = new FileInfo(localBackupPath);
            var totalBytes = fileInfo.Length;
            long bytesWritten = 0;

            progress?.Report(new BackupUploadProgress(0, totalBytes, 0));

            await using (var sourceStream = new FileStream(localBackupPath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true))
            await using (var destStream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            {
                var buffer = new byte[81920];
                int read;
                while ((read = await sourceStream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken)) > 0)
                {
                    await destStream.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                    bytesWritten += read;
                    double pct = totalBytes > 0 ? (double)bytesWritten / totalBytes * 100.0 : 0.0;
                    progress?.Report(new BackupUploadProgress(bytesWritten, totalBytes, Math.Min(100.0, pct)));
                }
            }

            File.Move(tempPath, destPath, overwrite: true);
            var info = new FileInfo(destPath);
            progress?.Report(new BackupUploadProgress(totalBytes, totalBytes, 100.0));
            _logger?.LogInformation("[MountBackup] Backup copied successfully to mount: {Path}", destPath);
            return BackupUploadResult.Succeeded(destPath, info.Length);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "[MountBackup] Failed to copy backup to mount directory {Path}", config.MountPath);
            try
            {
                if (File.Exists(tempPath))
                {
                    File.Delete(tempPath);
                    _logger?.LogInformation("[MountBackup] Cleaned up incomplete temporary file {Path}", tempPath);
                }
            }
            catch { /* Ignore secondary cleanup errors */ }
            return BackupUploadResult.Failed(ex.Message);
        }
    }

    public Task<List<RemoteBackupItem>> ListBackupsAsync(CancellationToken cancellationToken = default)
    {
        var config = _profiles.Get()?.Backup.SecondaryMount;
        if (config == null || !config.IsEnabled || string.IsNullOrWhiteSpace(config.MountPath) || !Directory.Exists(config.MountPath))
        {
            return Task.FromResult(new List<RemoteBackupItem>());
        }

        try
        {
            var dir = new DirectoryInfo(config.MountPath);
            var items = dir.GetFiles("Spokes_Backup_*.*")
                .Where(f => f.Extension.Equals(".zip", StringComparison.OrdinalIgnoreCase) || f.Extension.Equals(".spkbackup", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(f => f.CreationTimeUtc)
                .Select(f => new RemoteBackupItem(f.FullName, f.Name, f.Length, f.CreationTimeUtc, "LocalMount"))
                .ToList();

            return Task.FromResult(items);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "[MountBackup] Failed to list backups in {Path}", config.MountPath);
            return Task.FromResult(new List<RemoteBackupItem>());
        }
    }

    public Task<Stream?> DownloadBackupAsync(string remoteFileId, CancellationToken cancellationToken = default)
    {
        if (File.Exists(remoteFileId))
        {
            Stream stream = new FileStream(remoteFileId, FileMode.Open, FileAccess.Read, FileShare.Read);
            return Task.FromResult<Stream?>(stream);
        }
        return Task.FromResult<Stream?>(null);
    }

    public Task DeleteBackupAsync(string remoteFileId, CancellationToken cancellationToken = default)
    {
        if (File.Exists(remoteFileId))
        {
            try
            {
                File.Delete(remoteFileId);
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "[MountBackup] Failed to delete backup file {Path}", remoteFileId);
            }
        }
        return Task.CompletedTask;
    }

    public Task PruneBackupsAsync(int retentionCount, CancellationToken cancellationToken = default)
    {
        var config = _profiles.Get()?.Backup.SecondaryMount;
        if (config == null || !config.IsEnabled || string.IsNullOrWhiteSpace(config.MountPath) || !Directory.Exists(config.MountPath))
        {
            return Task.CompletedTask;
        }

        try
        {
            var dir = new DirectoryInfo(config.MountPath);

            // 1. Clean up stale or orphaned .tmp files
            var staleTempFiles = dir.GetFiles("Spokes_Backup_*.tmp");
            foreach (var tmp in staleTempFiles)
            {
                try
                {
                    tmp.Delete();
                    _logger?.LogInformation("[MountBackup] Cleaned up stale temporary file {Name}", tmp.Name);
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning(ex, "[MountBackup] Could not delete temporary file {Name}", tmp.Name);
                }
            }

            // 2. Prune old backups exceeding retention count
            var backups = dir.GetFiles("Spokes_Backup_*.*")
                .Where(f => !f.Name.Contains("_Manual_", StringComparison.OrdinalIgnoreCase))
                .Where(f => f.Extension.Equals(".zip", StringComparison.OrdinalIgnoreCase) || f.Extension.Equals(".spkbackup", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(f => f.CreationTimeUtc)
                .ToList();

            if (backups.Count > retentionCount)
            {
                foreach (var old in backups.Skip(retentionCount))
                {
                    try
                    {
                        old.Delete();
                        _logger?.LogInformation("[MountBackup] Pruned old backup from mount: {Name}", old.Name);
                    }
                    catch (Exception ex)
                    {
                        _logger?.LogWarning(ex, "[MountBackup] Failed to prune {Name}", old.Name);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "[MountBackup] Pruning failed in {Path}", config.MountPath);
        }

        return Task.CompletedTask;
    }

    public Task<TestConnectionResult> TestConnectionAsync(CancellationToken cancellationToken = default)
    {
        var config = _profiles.Get()?.Backup.SecondaryMount;
        if (config == null || string.IsNullOrWhiteSpace(config.MountPath))
        {
            return Task.FromResult(TestConnectionResult.Fail("Mount path cannot be empty."));
        }

        try
        {
            Directory.CreateDirectory(config.MountPath);
            var testFile = Path.Combine(config.MountPath, $".spokes_test_{Guid.NewGuid():N}.tmp");
            File.WriteAllText(testFile, "test");
            File.Delete(testFile);
            return Task.FromResult(TestConnectionResult.Ok($"Successfully verified write access to {config.MountPath}."));
        }
        catch (Exception ex)
        {
            return Task.FromResult(TestConnectionResult.Fail($"Failed to access mount path: {ex.Message}"));
        }
    }
}
