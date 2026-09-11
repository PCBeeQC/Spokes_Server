using System.IO.Compression;
using Spokes_Server.Core.Models.Core;
using Spokes_Server.Core.Models.Projects;
using Spokes_Server.Core.Models.Accounting;
using Spokes_Server.Core.Models.Communication;
using Spokes_Server.Core.Models.HR;
using Spokes_Server.Core.Data.Repositories.Core;

namespace Spokes_Server.Core.Services.Core;

public class BackupService : BackgroundService
{
    private readonly ILogger<BackupService> _logger;
    private readonly CompanyProfileRepository _companyProfiles;
    private readonly string _dataPath;

    public DateTime? LastBackupAt { get; private set; }
    public string LastBackupStatus { get; private set; } = "Never run";
    public bool IsRunning { get; private set; }

    public BackupService(ILogger<BackupService> logger, CompanyProfileRepository companyProfiles, IConfiguration config)
    {
        _logger = logger;
        _companyProfiles = companyProfiles;
        _dataPath = config["DataPath"] ?? "Data";
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var profile = _companyProfiles.Get();
                if (profile != null && profile.BackupsEnabled)
                {
                    var now = DateTime.Now;
                    if (TimeOnly.TryParse(profile.BackupTimeLocal, out var scheduledTime))
                    {
                        var scheduledToday = new DateTime(now.Year, now.Month, now.Day,
                            scheduledTime.Hour, scheduledTime.Minute, 0, DateTimeKind.Local);

                        // Check if we're within the minute of the scheduled time and haven't run today
                        if (Math.Abs((now - scheduledToday).TotalSeconds) < 45 &&
                            (LastBackupAt == null || LastBackupAt.Value.Date != now.Date))
                        {
                            await RunBackupInternal(isManual: false);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in backup scheduler loop");
            }

            await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
        }
    }

    public async Task<string> RunBackupNow()
    {
        return await RunBackupInternal(isManual: true);
    }

    private async Task<string> RunBackupInternal(bool isManual)
    {
        if (IsRunning)
            return "A backup is already in progress";

        IsRunning = true;
        try
        {
            var profile = _companyProfiles.Get();
            var backupDir = Path.Combine(_dataPath, "Backups");
            Directory.CreateDirectory(backupDir);

            var timestamp = DateTime.UtcNow.ToString("yyyy-MM-dd_HHmmss");
            var prefix = isManual ? "Spokes_Backup_Manual_" : "Spokes_Backup_";
            var zipPath = Path.Combine(backupDir, $"{prefix}{timestamp}.zip");
            var tempZipPath = zipPath + ".tmp";

            // Create the zip, excluding the Backups folder itself
            await Task.Run(() =>
            {
                using var zip = ZipFile.Open(tempZipPath, ZipArchiveMode.Create);
                var dataDir = new DirectoryInfo(_dataPath);
                foreach (var file in dataDir.GetFiles("*", SearchOption.AllDirectories))
                {
                    // Skip the Backups folder and temp files
                    var relativePath = Path.GetRelativePath(_dataPath, file.FullName);
                    if (relativePath.StartsWith("Backups", StringComparison.OrdinalIgnoreCase))
                        continue;
                    if (file.Extension == ".tmp")
                        continue;

                    var zipEntryName = relativePath.Replace('\\', '/');

                    if (!profile.BackupEmployeeEmails)
                    {
                        if (zipEntryName.StartsWith("Employees/", StringComparison.OrdinalIgnoreCase) &&
                            zipEntryName.Contains("/Email/", StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }
                    }

                    zip.CreateEntryFromFile(file.FullName, zipEntryName, CompressionLevel.Optimal);
                }
            });

            // Atomic move
            File.Move(tempZipPath, zipPath, overwrite: true);

            // Prune old backups
            var retention = profile?.BackupRetentionCount ?? 5;
            PruneBackups(backupDir, retention);

            LastBackupAt = DateTime.UtcNow;
            LastBackupStatus = "Success";
            _logger.LogInformation("Backup created successfully: {Path}", zipPath);

            return zipPath;
        }
        catch (Exception ex)
        {
            LastBackupStatus = $"Failed: {ex.Message}";
            _logger.LogError(ex, "Backup failed");
            return $"Error: {ex.Message}";
        }
        finally
        {
            IsRunning = false;
        }
    }

    private void PruneBackups(string backupDir, int retentionCount)
    {
        try
        {
            var backups = new DirectoryInfo(backupDir)
                .GetFiles("Spokes_Backup_*.zip")
                .Where(f => !f.Name.Contains("_Manual_", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(f => f.CreationTimeUtc)
                .ToList();

            if (backups.Count > retentionCount)
            {
                foreach (var old in backups.Skip(retentionCount))
                {
                    old.Delete();
                    _logger.LogInformation("Pruned old backup: {Name}", old.Name);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to prune old backups");
        }
    }

    public List<BackupFileInfo> GetBackups()
    {
        var backupDir = Path.Combine(_dataPath, "Backups");
        if (!Directory.Exists(backupDir))
            return new List<BackupFileInfo>();

        return new DirectoryInfo(backupDir)
            .GetFiles("Spokes_Backup_*.zip")
            .OrderByDescending(f => f.CreationTimeUtc)
            .Select(f => new BackupFileInfo
            {
                FileName = f.Name,
                FilePath = f.FullName,
                SizeBytes = f.Length,
                CreatedAt = f.CreationTimeUtc
            })
            .ToList();
    }

    public void DeleteBackup(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath)) return;

        var backupDir = Path.GetFullPath(Path.Combine(_dataPath, "Backups"));
        if (!backupDir.EndsWith(Path.DirectorySeparatorChar)) backupDir += Path.DirectorySeparatorChar;

        var fullPath = Path.GetFullPath(filePath);
        if (File.Exists(fullPath) && fullPath.StartsWith(backupDir, StringComparison.OrdinalIgnoreCase))
        {
            File.Delete(fullPath);
        }
    }

    public async Task<byte[]?> GetBackupBytesAsync(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath)) return null;

        var backupDir = Path.GetFullPath(Path.Combine(_dataPath, "Backups"));
        if (!backupDir.EndsWith(Path.DirectorySeparatorChar)) backupDir += Path.DirectorySeparatorChar;

        var fullPath = Path.GetFullPath(filePath);
        if (File.Exists(fullPath) && fullPath.StartsWith(backupDir, StringComparison.OrdinalIgnoreCase))
        {
            return await File.ReadAllBytesAsync(fullPath);
        }
        return null;
    }


}

public class BackupFileInfo
{
    public string FileName { get; set; } = string.Empty;
    public string FilePath { get; set; } = string.Empty;
    public long SizeBytes { get; set; }
    public DateTime CreatedAt { get; set; }

    public bool IsManual => FileName.Contains("_Manual_", StringComparison.OrdinalIgnoreCase);
    public string BackupType => IsManual ? "Manual" : "Auto";

    public string FormattedSize
    {
        get
        {
            if (SizeBytes < 1024) return $"{SizeBytes} B";
            if (SizeBytes < 1024 * 1024) return $"{SizeBytes / 1024.0:F1} KB";
            return $"{SizeBytes / (1024.0 * 1024.0):F1} MB";
        }
    }
}


