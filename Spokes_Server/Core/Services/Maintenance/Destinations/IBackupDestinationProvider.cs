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
    WebDav,
    Sftp
}

public record BackupUploadProgress(long BytesTransferred, long TotalBytes, double Percent);

public enum RemoteUploadState
{
    Idle,
    Pending,
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

    public string FormattedProgress
    {
        get
        {
            if (TotalBytes <= 0) return $"{Percent:F0}%";
            var transferredMb = BytesTransferred / (1024.0 * 1024.0);
            var totalMb = TotalBytes / (1024.0 * 1024.0);
            return $"{transferredMb:F1} MB / {totalMb:F1} MB ({Percent:F0}%)";
        }
    }
}

public record BackupUploadResult(bool Success, string? DestinationId, string? ErrorMessage = null, long SizeBytes = 0)
{
    public static BackupUploadResult Succeeded(string destinationId, long sizeBytes) => new(true, destinationId, null, sizeBytes);
    public static BackupUploadResult Failed(string error) => new(false, null, error, 0);
}

public record RemoteBackupItem(string Id, string FileName, long SizeBytes, DateTime CreatedAtUtc, string DestinationType)
{
    public string FormattedSize => SizeBytes < 1024 * 1024
        ? $"{SizeBytes / 1024.0:F1} KB"
        : $"{SizeBytes / (1024.0 * 1024.0):F1} MB";
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

