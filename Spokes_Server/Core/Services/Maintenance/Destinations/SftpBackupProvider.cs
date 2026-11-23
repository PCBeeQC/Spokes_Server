using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Renci.SshNet;
using Renci.SshNet.Sftp;
using Spokes_Server.Core.Data.Repositories.Core;
using Spokes_Server.Core.Services.Security;

namespace Spokes_Server.Core.Services.Maintenance.Destinations;

public class SftpBackupProvider : IBackupDestinationProvider
{
    private readonly CompanyProfileRepository _profiles;
    private readonly EncryptionService _encryptionService;
    private readonly ILogger<SftpBackupProvider>? _logger;
    private readonly object _hostKeyLock = new();

    public BackupDestinationType DestinationType => BackupDestinationType.Sftp;

    public SftpBackupProvider(
        CompanyProfileRepository profiles,
        ILogger<SftpBackupProvider>? logger = null,
        EncryptionService? encryptionService = null,
        IConfiguration? configuration = null)
    {
        _profiles = profiles;
        _logger = logger;
        _encryptionService = BackupDestinationPruneHelper.ResolveEncryptionService(encryptionService, profiles, configuration);
    }

    private string ResolveCredential(string? value) =>
        BackupDestinationPruneHelper.ResolveCredential(_encryptionService, value);

    private sealed class HostKeyVerificationState
    {
        public string? MismatchError { get; set; }
    }

    internal static string ComputeHostKeyFingerprint(ReadOnlySpan<byte> hostKey)
    {
        if (hostKey.IsEmpty)
        {
            throw new ArgumentException("Host key cannot be empty.", nameof(hostKey));
        }

        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(hostKey, hash);
        return $"SHA256:{Convert.ToBase64String(hash)}";
    }

    internal static bool TryDecodeSha256Fingerprint(string? fingerprint, out byte[] digest32)
    {
        if (string.IsNullOrWhiteSpace(fingerprint))
        {
            digest32 = [];
            return false;
        }

        var trimmed = fingerprint.Trim();
        if (trimmed.StartsWith("SHA256:", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = trimmed["SHA256:".Length..].Trim();
        }
        else if (trimmed.StartsWith("MD5:", StringComparison.OrdinalIgnoreCase) || trimmed.Contains(':'))
        {
            digest32 = [];
            return false;
        }

        if (string.IsNullOrEmpty(trimmed))
        {
            digest32 = [];
            return false;
        }

        int mod = trimmed.Length % 4;
        if (mod == 1)
        {
            digest32 = [];
            return false;
        }

        if (mod > 0)
        {
            trimmed = trimmed.PadRight(trimmed.Length + (4 - mod), '=');
        }

        try
        {
            var decoded = Convert.FromBase64String(trimmed);
            if (decoded.Length == 32)
            {
                digest32 = decoded;
                return true;
            }
        }
        catch (FormatException)
        {
        }

        digest32 = [];
        return false;
    }

    internal bool VerifyOrPinHostKey(byte[]? hostKey, out string? errorMessage)
    {
        if (hostKey == null || hostKey.Length == 0)
        {
            errorMessage = "SFTP server presented an empty SSH host key.";
            _logger?.LogError("[SftpBackup] {Error}", errorMessage);
            return false;
        }

        var actualFingerprint = ComputeHostKeyFingerprint(hostKey);

        lock (_hostKeyLock)
        {
            var profile = _profiles.Get();
            var sftpConfig = profile?.Backup.Sftp;
            if (profile == null || sftpConfig == null)
            {
                errorMessage = "SFTP backup destination is not configured.";
                _logger?.LogError("[SftpBackup] {Error}", errorMessage);
                return false;
            }

            var pinnedFingerprint = sftpConfig.TrustedHostKeyFingerprint?.Trim();
            if (string.IsNullOrEmpty(pinnedFingerprint))
            {
                sftpConfig.TrustedHostKeyFingerprint = actualFingerprint;
                _profiles.Save(profile);
                _logger?.LogInformation(
                    "[SftpBackup] Pinned SSH host key fingerprint for {Host}:{Port} via TOFU: {Fingerprint}",
                    sftpConfig.Host,
                    sftpConfig.Port > 0 ? sftpConfig.Port : 22,
                    actualFingerprint);
                errorMessage = null;
                return true;
            }

            var actualDigest = SHA256.HashData(hostKey);
            if (!TryDecodeSha256Fingerprint(pinnedFingerprint, out var expectedDigest) ||
                !CryptographicOperations.FixedTimeEquals(actualDigest, expectedDigest))
            {
                int port = sftpConfig.Port > 0 ? sftpConfig.Port : 22;
                errorMessage =
                    $"SSH host key verification failed for {sftpConfig.Host}:{port}. " +
                    $"Expected '{pinnedFingerprint}', but server presented '{actualFingerprint}'. " +
                    "Connection rejected to prevent a potential MITM attack.";
                _logger?.LogError("[SftpBackup] {Error}", errorMessage);
                return false;
            }

            errorMessage = null;
            return true;
        }
    }

    private (SftpClient Client, HostKeyVerificationState HostKeyState) CreateClient()
    {
        var config = _profiles.Get()?.Backup.Sftp ?? throw new InvalidOperationException("SFTP is not configured.");
        var username = ResolveCredential(config.Username);
        var password = ResolveCredential(config.Password);
        var authMethod = new PasswordAuthenticationMethod(username, password);
        var connectionInfo = new Renci.SshNet.ConnectionInfo(config.Host, config.Port > 0 ? config.Port : 22, username, authMethod)
        {
            Timeout = TimeSpan.FromSeconds(30)
        };

        var state = new HostKeyVerificationState();
        var client = new SftpClient(connectionInfo);
        client.HostKeyReceived += (_, e) =>
        {
            e.CanTrust = VerifyOrPinHostKey(e.HostKey, out var mismatchError);
            if (!e.CanTrust && !string.IsNullOrEmpty(mismatchError))
            {
                state.MismatchError = mismatchError;
            }
        };

        return (client, state);
    }

    private static void ConnectVerified(SftpClient client, HostKeyVerificationState state)
    {
        try
        {
            client.Connect();
        }
        catch (Exception ex) when (!string.IsNullOrEmpty(state.MismatchError))
        {
            throw new InvalidOperationException(state.MismatchError, ex);
        }
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

    private T ExecuteWithClient<T>(Func<SftpClient, T> action)
    {
        var (client, hostKeyState) = CreateClient();
        using (client)
        {
            ConnectVerified(client, hostKeyState);
            var result = action(client);
            client.Disconnect();
            return result;
        }
    }

    private void ExecuteWithClient(Action<SftpClient> action) =>
        ExecuteWithClient(client =>
        {
            action(client);
            return true;
        });

    private Task<T> ExecuteWithClientAsync<T>(Func<SftpClient, T> action, CancellationToken cancellationToken) =>
        Task.Run(() => ExecuteWithClient(action), cancellationToken);

    private Task ExecuteWithClientAsync(Action<SftpClient> action, CancellationToken cancellationToken) =>
        Task.Run(() => ExecuteWithClient(action), cancellationToken);

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
            return await ExecuteWithClientAsync(client =>
            {
                var remoteDir = EnsureRemoteDirectory(client, config.RemotePath);

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
                    ExecuteWithClient(cleanupClient =>
                    {
                        if (cleanupClient.Exists(targetPath))
                        {
                            cleanupClient.DeleteFile(targetPath);
                            _logger?.LogInformation("[SftpBackup] Cleaned up partial upload at {Path}", targetPath);
                        }
                    });
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
            return await ExecuteWithClientAsync(client =>
            {
                var remoteDir = NormalizeRemoteDirectory(config.RemotePath);
                if (!client.Exists(remoteDir))
                {
                    return new List<RemoteBackupItem>();
                }

                var files = client.ListDirectory(remoteDir);
                return FilterAndOrderBackups(files, excludeManual: false);
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
            return await ExecuteWithClientAsync<Stream?>(client =>
            {
                var ms = new MemoryStream();
                client.DownloadFile(remoteFileId, ms);
                ms.Position = 0;
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
            await ExecuteWithClientAsync(client =>
            {
                if (client.Exists(remoteFileId))
                {
                    client.DeleteFile(remoteFileId);
                    _logger?.LogInformation("[SftpBackup] Deleted {File}", remoteFileId);
                }
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
            await ExecuteWithClientAsync(client =>
            {
                var remoteDir = NormalizeRemoteDirectory(config.RemotePath);
                if (!client.Exists(remoteDir))
                {
                    return;
                }

                var files = client.ListDirectory(remoteDir).ToList();

                // 1. Clean up legacy or abandoned .tmp files (including any previous .zip.tmp)
                var tempFiles = files
                    .Where(f => !f.IsDirectory)
                    .Where(f => f.Name.StartsWith("Spokes_Backup_", StringComparison.OrdinalIgnoreCase))
                    .Where(f => f.Name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase) ||
                                f.Name.EndsWith(".tmp.zip", StringComparison.OrdinalIgnoreCase));

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
                var scheduled = FilterAndOrderBackups(files, excludeManual: true);

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
            return await ExecuteWithClientAsync(client =>
            {
                var remoteDir = EnsureRemoteDirectory(client, config.RemotePath);

                var testFile = CombineRemotePath(remoteDir, $".spokes_test_{Guid.NewGuid():N}");
                using (var ms = new MemoryStream(new byte[] { 1 }))
                {
                    client.UploadFile(ms, testFile);
                }

                if (client.Exists(testFile))
                {
                    client.DeleteFile(testFile);
                }

                var pinned = _profiles.Get()?.Backup.Sftp.TrustedHostKeyFingerprint;
                var fingerprintSuffix = string.IsNullOrWhiteSpace(pinned) ? string.Empty : $" [Host key: {pinned}]";
                return TestConnectionResult.Ok($"Successfully connected and verified write access to SFTP host {config.Host}:{config.Port} ({remoteDir}).{fingerprintSuffix}");
            }, cancellationToken);
        }
        catch (Exception ex)
        {
            return TestConnectionResult.Fail($"SFTP test failed: {ex.Message}");
        }
    }

    private static string EnsureRemoteDirectory(SftpClient client, string? remotePath)
    {
        var remoteDir = NormalizeRemoteDirectory(remotePath);
        if (!client.Exists(remoteDir))
        {
            CreateRemoteDirectoryRecursively(client, remoteDir);
        }
        return remoteDir;
    }

    private static List<RemoteBackupItem> FilterAndOrderBackups(IEnumerable<ISftpFile> files, bool excludeManual) =>
        files
            .Where(f => !f.IsDirectory)
            .Where(f => f.Name.StartsWith("Spokes_Backup_", StringComparison.OrdinalIgnoreCase))
            .Where(f => !excludeManual || !f.Name.Contains("_Manual_", StringComparison.OrdinalIgnoreCase))
            .Where(f => !f.Name.EndsWith(".tmp.zip", StringComparison.OrdinalIgnoreCase))
            .Where(f => f.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) || f.Name.EndsWith(".spkbackup", StringComparison.OrdinalIgnoreCase))
            .Select(f => new RemoteBackupItem(f.FullName, f.Name, f.Length, f.LastWriteTimeUtc, "SFTP"))
            .OrderByDescending(f => f.CreatedAtUtc)
            .ToList();

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
