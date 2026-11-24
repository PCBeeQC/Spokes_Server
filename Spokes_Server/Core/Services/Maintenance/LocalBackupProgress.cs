using System;
using Spokes_Server.Core.Services.Maintenance.Destinations;

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

    public string FormattedProgress => FormatProgress();

    public string FormatProgress(string? rawUnitLabel = null) =>
        BackupFormatHelper.FormatTransferProgress(
            BytesProcessed,
            TotalBytes,
            Percent,
            Stage == LocalBackupStage.Archiving ? (string.IsNullOrWhiteSpace(rawUnitLabel) ? "raw" : rawUnitLabel) : null);
}
