using System;

namespace Spokes_Server.Core.Services.Maintenance;

public enum LocalBackupStage
{
    Idle,
    Preparing,
    Archiving,
    Encrypting,
    Finalizing,
    Completed,
    Failed
}

public record LocalBackupProgress(
    LocalBackupStage Stage,
    double Percent,
    long BytesProcessed,
    long TotalBytes,
    int FilesProcessed,
    int TotalFiles,
    string? CurrentFileName = null,
    string? ErrorMessage = null)
{
    public static LocalBackupProgress Idle => new(LocalBackupStage.Idle, 0, 0, 0, 0, 0);

    public string FormattedProgress
    {
        get
        {
            if (TotalBytes <= 0) return $"{Percent:F0}%";
            var processedMb = BytesProcessed / (1024.0 * 1024.0);
            var totalMb = TotalBytes / (1024.0 * 1024.0);
            return $"{processedMb:F1} MB / {totalMb:F1} MB ({Percent:F0}%)";
        }
    }
}
