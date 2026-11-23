using System;
using System.IO;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Spokes_Server.Core.Services.Maintenance.Destinations;

public enum BackupDestinationType
{
    LocalMount,
    S3,
    GoogleDrive,
    GitHub,
    WebDav,
    Sftp
}

public record BackupUploadProgress(long BytesTransferred, long TotalBytes, double Percent);

public enum RemoteUploadState
{
    Idle,
    Uploading,
    Success,
    Failed
}

public class DestinationUploadStatus
{
    public BackupDestinationType DestinationType { get; set; }
    public RemoteUploadState State { get; set; } = RemoteUploadState.Idle;
    public double Percent { get; set; }
    public long BytesTransferred { get; set; }
    public long TotalBytes { get; set; }
    public string? ErrorMessage { get; set; }
    public DateTime? LastCompletedAt { get; set; }

    public string FormattedProgress => BackupFormatHelper.FormatTransferProgress(BytesTransferred, TotalBytes, Percent);
}

public record BackupUploadResult(bool Success, string? DestinationId, string? ErrorMessage = null, long SizeBytes = 0)
{
    public static BackupUploadResult Succeeded(string destinationId, long sizeBytes) => new(true, destinationId, null, sizeBytes);
    public static BackupUploadResult Failed(string error) => new(false, null, error, 0);
}

public record RemoteBackupItem(string Id, string FileName, long SizeBytes, DateTime CreatedAtUtc, string DestinationType)
{
    public string FormattedSize => BackupFormatHelper.FormatKilobyteOrMegabyteSize(SizeBytes);
}

public record TestConnectionResult(bool Success, string Message)
{
    public static TestConnectionResult Ok(string message = "Connection successful.") => new(true, message);
    public static TestConnectionResult Fail(string error) => new(false, error);
}

public interface IBackupDestinationProvider
{
    BackupDestinationType DestinationType { get; }
    Task<BackupUploadResult> UploadAsync(string localBackupPath, string remoteFileName, IProgress<BackupUploadProgress>? progress = null, CancellationToken cancellationToken = default);
    Task<List<RemoteBackupItem>> ListBackupsAsync(CancellationToken cancellationToken = default);
    Task<Stream?> DownloadBackupAsync(string remoteFileId, CancellationToken cancellationToken = default);
    Task DeleteBackupAsync(string remoteFileId, CancellationToken cancellationToken = default);
    Task PruneBackupsAsync(int retentionCount, CancellationToken cancellationToken = default);
    Task<TestConnectionResult> TestConnectionAsync(CancellationToken cancellationToken = default);
}

internal static class BackupFormatHelper
{
    public static string FormatTransferProgress(long bytes, long totalBytes, double percent)
    {
        if (totalBytes <= 0) return $"{percent:F0}%";
        var transferredMb = bytes / (1024.0 * 1024.0);
        var totalMb = totalBytes / (1024.0 * 1024.0);
        return $"{transferredMb:F1} MB / {totalMb:F1} MB ({percent:F0}%)";
    }

    public static string FormatKilobyteOrMegabyteSize(long sizeBytes) => sizeBytes < 1024 * 1024
        ? $"{sizeBytes / 1024.0:F1} KB"
        : $"{sizeBytes / (1024.0 * 1024.0):F1} MB";

    public static string FormatBackupSize(long sizeBytes) => sizeBytes < 1024
        ? $"{sizeBytes} B"
        : FormatKilobyteOrMegabyteSize(sizeBytes);
}

internal static class BackupDestinationPruneHelper
{
    public static string ResolveCredential(Spokes_Server.Core.Services.Security.EncryptionService encryptionService, string? value) =>
        string.IsNullOrEmpty(value)
            ? string.Empty
            : (encryptionService.TryDecrypt(value, out var decrypted) ? decrypted : value);

    public static Spokes_Server.Core.Services.Security.EncryptionService ResolveEncryptionService(
        Spokes_Server.Core.Services.Security.EncryptionService? encryptionService,
        Spokes_Server.Core.Data.Repositories.Core.CompanyProfileRepository? profiles,
        Microsoft.Extensions.Configuration.IConfiguration? configuration) =>
        encryptionService ?? profiles?.EncryptionService ?? new Spokes_Server.Core.Services.Security.EncryptionService(configuration);
    public static async Task PruneScheduledBackupsAsync(
        IBackupDestinationProvider provider,
        int retentionCount,
        Microsoft.Extensions.Logging.ILogger? logger,
        string logTag,
        CancellationToken cancellationToken)
    {
        try
        {
            var backups = await provider.ListBackupsAsync(cancellationToken);
            var scheduled = backups
                .Where(b => !b.FileName.Contains("_Manual_", StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (scheduled.Count > retentionCount)
            {
                foreach (var old in scheduled.Skip(retentionCount))
                {
                    await provider.DeleteBackupAsync(old.Id, cancellationToken);
                }
            }
        }
        catch (Exception ex)
        {
            logger?.LogError(ex, "[{LogTag}] Pruning failed", logTag);
        }
    }
}

