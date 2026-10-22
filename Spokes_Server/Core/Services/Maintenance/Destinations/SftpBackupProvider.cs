using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Renci.SshNet;
using Spokes_Server.Core.Data.Repositories.Core;

namespace Spokes_Server.Core.Services.Maintenance.Destinations;

public class SftpBackupProvider : IBackupDestinationProvider
{
    private readonly CompanyProfileRepository _profiles;
    private readonly ILogger<SftpBackupProvider>? _logger;

    public BackupDestinationType DestinationType => BackupDestinationType.Sftp;

    public SftpBackupProvider(CompanyProfileRepository profiles, ILogger<SftpBackupProvider>? logger = null)
    {
        _profiles = profiles;
        _logger = logger;
    }

    private SftpClient CreateClient()
    {
        var config = _profiles.Get()?.Backup.Sftp ?? throw new InvalidOperationException("SFTP is not configured.");
        var authMethod = new PasswordAuthenticationMethod(config.Username, config.Password);
        var connectionInfo = new Renci.SshNet.ConnectionInfo(config.Host, config.Port > 0 ? config.Port : 22, config.Username, authMethod)
        {
            Timeout = TimeSpan.FromSeconds(30)
        };
        return new SftpClient(connectionInfo);
    }

    internal static string NormalizeRemoteDirectory(string? remotePath)
    {
        if (string.IsNullOrWhiteSpace(remotePath) || remotePath.Trim() == "/")
            return "/";
        var trimmed = remotePath.Trim();
        if (!trimmed.StartsWith("/"))
            trimmed = "/" + trimmed;
        return trimmed.TrimEnd('/');
    }

    internal static string CombineRemotePath(string remoteDir, string fileName)
    {
        return remoteDir == "/" ? $"/{fileName}" : $"{remoteDir}/{fileName}";
    }

    public async Task<BackupUploadResult> UploadAsync(
        string localBackupPath,
        string remoteFileName,
        IProgress<BackupUploadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var config = _profiles.Get()?.Backup.Sftp;
        if (config == null || !config.IsEnabled || string.IsNullOrWhiteSpace(config.Host))
        {
            return BackupUploadResult.Failed("SFTP backup destination is not configured or enabled.");
        }

        string? targetPath = null;
        try
        {
            return await Task.Run(() =>
            {
                using var client = CreateClient();
                client.Connect();

                var remoteDir = NormalizeRemoteDirectory(config.RemotePath);
                if (!client.Exists(remoteDir))
                {
                    // Create remote directory tree if needed
                    CreateRemoteDirectoryRecursively(client, remoteDir);
                }

                targetPath = CombineRemotePath(remoteDir, remoteFileName);

                var fileInfo = new FileInfo(localBackupPath);
                long totalBytes = fileInfo.Length;
                progress?.Report(new BackupUploadProgress(0, totalBytes, 0));

                if (client.Exists(targetPath))
                {
                    client.DeleteFile(targetPath);
                }

                using (var localStream = File.OpenRead(localBackupPath))
                {
                    client.UploadFile(localStream, targetPath, uploaded =>
                    {
                        double pct = totalBytes > 0 ? (double)uploaded / totalBytes * 100.0 : 0.0;
                        progress?.Report(new BackupUploadProgress((long)uploaded, totalBytes, Math.Min(100.0, pct)));
                    });
                }

                client.Disconnect();
                progress?.Report(new BackupUploadProgress(fileInfo.Length, fileInfo.Length, 100.0));
                _logger?.LogInformation("[SftpBackup] Successfully uploaded {File} to sftp://{Host}{Path}", remoteFileName, config.Host, targetPath);
                return BackupUploadResult.Succeeded(targetPath, fileInfo.Length);
            }, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "[SftpBackup] Upload failed to {Host}", config.Host);
            if (targetPath != null)
            {
                try
                {
                    using var cleanupClient = CreateClient();
                    cleanupClient.Connect();
                    if (cleanupClient.Exists(targetPath))
                    {
                        cleanupClient.DeleteFile(targetPath);
                        _logger?.LogInformation("[SftpBackup] Cleaned up partial upload at {Path}", targetPath);
                    }
                    cleanupClient.Disconnect();
                }
                catch { /* Ignore secondary cleanup errors */ }
            }
            return BackupUploadResult.Failed(ex.Message);
        }
    }

    public async Task<List<RemoteBackupItem>> ListBackupsAsync(CancellationToken cancellationToken = default)
    {
        var config = _profiles.Get()?.Backup.Sftp;
        if (config == null || !config.IsEnabled || string.IsNullOrWhiteSpace(config.Host))
        {
            return new List<RemoteBackupItem>();
        }

        try
        {
            return await Task.Run(() =>
            {
                using var client = CreateClient();
                client.Connect();

                var remoteDir = NormalizeRemoteDirectory(config.RemotePath);
                if (!client.Exists(remoteDir))
                {
                    client.Disconnect();
                    return new List<RemoteBackupItem>();
                }

                var files = client.ListDirectory(remoteDir);
                var items = files
                    .Where(f => !f.IsDirectory)
                    .Where(f => f.Name.StartsWith("Spokes_Backup_", StringComparison.OrdinalIgnoreCase))
                    .Where(f => f.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) || f.Name.EndsWith(".spkbackup", StringComparison.OrdinalIgnoreCase))
                    .Select(f => new RemoteBackupItem(f.FullName, f.Name, f.Length, f.LastWriteTimeUtc, "SFTP"))
                    .OrderByDescending(f => f.CreatedAtUtc)
                    .ToList();

                client.Disconnect();
                return items;
            }, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "[SftpBackup] Failed to list backups from {Host}", config.Host);
            return new List<RemoteBackupItem>();
        }
    }

    public async Task<Stream?> DownloadBackupAsync(string remoteFileId, CancellationToken cancellationToken = default)
    {
        try
        {
            return await Task.Run<Stream?>(() =>
            {
                using var client = CreateClient();
                client.Connect();

                var ms = new MemoryStream();
                client.DownloadFile(remoteFileId, ms);
                ms.Position = 0;

                client.Disconnect();
                return ms;
            }, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "[SftpBackup] Failed to download {File}", remoteFileId);
            return null;
        }
    }

    public async Task DeleteBackupAsync(string remoteFileId, CancellationToken cancellationToken = default)
    {
        try
        {
            await Task.Run(() =>
            {
                using var client = CreateClient();
                client.Connect();
                if (client.Exists(remoteFileId))
                {
                    client.DeleteFile(remoteFileId);
                    _logger?.LogInformation("[SftpBackup] Deleted {File}", remoteFileId);
                }
                client.Disconnect();
            }, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "[SftpBackup] Failed to delete {File}", remoteFileId);
        }
    }

    public async Task PruneBackupsAsync(int retentionCount, CancellationToken cancellationToken = default)
    {
        var config = _profiles.Get()?.Backup.Sftp;
        if (config == null || !config.IsEnabled || string.IsNullOrWhiteSpace(config.Host))
        {
            return;
        }

        try
        {
            await Task.Run(() =>
            {
                using var client = CreateClient();
                client.Connect();

                var remoteDir = NormalizeRemoteDirectory(config.RemotePath);
                if (client.Exists(remoteDir))
                {
                    var files = client.ListDirectory(remoteDir).ToList();

                    // 1. Clean up legacy or abandoned .tmp files (including any previous .zip.tmp)
                    var tempFiles = files
                        .Where(f => !f.IsDirectory)
                        .Where(f => f.Name.StartsWith("Spokes_Backup_", StringComparison.OrdinalIgnoreCase))
                        .Where(f => f.Name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase));

                    foreach (var tmp in tempFiles)
                    {
                        try
                        {
                            client.DeleteFile(tmp.FullName);
                            _logger?.LogInformation("[SftpBackup] Cleaned up legacy temporary file {File}", tmp.FullName);
                        }
                        catch (Exception ex)
                        {
                            _logger?.LogWarning(ex, "[SftpBackup] Could not delete temporary file {File}", tmp.FullName);
                        }
                    }

                    // 2. Prune old backups exceeding retention count
                    var scheduled = files
                        .Where(f => !f.IsDirectory)
                        .Where(f => f.Name.StartsWith("Spokes_Backup_", StringComparison.OrdinalIgnoreCase))
                        .Where(f => !f.Name.Contains("_Manual_", StringComparison.OrdinalIgnoreCase))
                        .Where(f => f.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) || f.Name.EndsWith(".spkbackup", StringComparison.OrdinalIgnoreCase))
                        .Select(f => new RemoteBackupItem(f.FullName, f.Name, f.Length, f.LastWriteTimeUtc, "SFTP"))
                        .OrderByDescending(f => f.CreatedAtUtc)
                        .ToList();

                    if (scheduled.Count > retentionCount)
                    {
                        foreach (var old in scheduled.Skip(retentionCount))
                        {
                            try
                            {
                                if (client.Exists(old.Id))
                                {
                                    client.DeleteFile(old.Id);
                                    _logger?.LogInformation("[SftpBackup] Pruned old backup {File}", old.Id);
                                }
                            }
                            catch (Exception ex)
                            {
                                _logger?.LogWarning(ex, "[SftpBackup] Failed to prune {File}", old.Id);
                            }
                        }
                    }
                }

                client.Disconnect();
            }, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "[SftpBackup] Pruning failed");
        }
    }

    public async Task<TestConnectionResult> TestConnectionAsync(CancellationToken cancellationToken = default)
    {
        var config = _profiles.Get()?.Backup.Sftp;
        if (config == null || string.IsNullOrWhiteSpace(config.Host))
        {
            return TestConnectionResult.Fail("Host cannot be empty.");
        }

        try
        {
            return await Task.Run(() =>
            {
                using var client = CreateClient();
                client.Connect();

                var remoteDir = NormalizeRemoteDirectory(config.RemotePath);
                if (!client.Exists(remoteDir))
                {
                    CreateRemoteDirectoryRecursively(client, remoteDir);
                }

                var testFile = CombineRemotePath(remoteDir, $".spokes_test_{Guid.NewGuid():N}");
                using (var ms = new MemoryStream(new byte[] { 1 }))
                {
                    client.UploadFile(ms, testFile);
                }

                if (client.Exists(testFile))
                {
                    client.DeleteFile(testFile);
                }

                client.Disconnect();
                return TestConnectionResult.Ok($"Successfully connected and verified write access to SFTP host {config.Host}:{config.Port} ({remoteDir}).");
            }, cancellationToken);
        }
        catch (Exception ex)
        {
            return TestConnectionResult.Fail($"SFTP test failed: {ex.Message}");
        }
    }

    private static void CreateRemoteDirectoryRecursively(SftpClient client, string path)
    {
        var current = "";
        var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (path.StartsWith('/')) current = "/";

        foreach (var part in parts)
        {
            current = string.IsNullOrEmpty(current) || current == "/" ? $"/{part}" : $"{current}/{part}";
            if (!client.Exists(current))
            {
                client.CreateDirectory(current);
            }
        }
    }
}
