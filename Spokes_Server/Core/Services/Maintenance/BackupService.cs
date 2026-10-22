using System.IO.Compression;
using Spokes_Server.Core.Models.Core;
using Spokes_Server.Core.Data.Repositories.Core;
using Spokes_Server.Core.Services.Logging;
using Spokes_Server.Aggregate;
using Spokes_Server.Core.Data;
using Spokes_Server.Core.Services.Maintenance.Destinations;
using Spokes_Server.Core.Services.Identity;
using Spokes_Server.Core.Exceptions;
using Spokes_Server.Core.Helpers;

namespace Spokes_Server.Core.Services.Maintenance;

public class BackupService : BackgroundService
{
    private readonly ILogger<BackupService> _logger;
    private readonly CompanyProfileRepository _companyProfiles;
    private readonly IConfiguration _config;
    private readonly string _dataPath;
    private readonly DiskPersistenceService? _persistence;
    private readonly Database? _db;
    private readonly IBackupEncryptionService? _encryption;
    private readonly BackupDestinationCoordinator? _destinationCoordinator;
    private readonly ISystemLogService? _systemLog;

    private int _isRunningState;

    public DateTime? LastBackupAt { get; private set; }
    public string LastBackupStatus { get; private set; } = "Never run";
    public bool IsRunning => Volatile.Read(ref _isRunningState) == 1;
    public bool IsLocalBackupInProgress { get; private set; }
    public LocalBackupProgress CurrentProgress { get; private set; } = LocalBackupProgress.Idle;

    public event Action? OnLocalBackupStateChanged;
    public event Action<LocalBackupProgress>? OnLocalBackupProgressChanged;

    private LocalBackupStage _lastReportStage = LocalBackupStage.Idle;
    private double _lastReportPct = -1.0;
    private DateTime _lastReportTime = DateTime.MinValue;

    private void ReportProgress(LocalBackupStage stage, long processed, long total, int filesDone, int totalFiles, string? fileName = null, string? error = null)
    {
        double pct;
        if (total > 0)
        {
            pct = Math.Clamp((double)processed / total * 100.0, 0, 100);
        }
        else if (totalFiles > 0)
        {
            pct = Math.Clamp((double)filesDone / totalFiles * 100.0, 0, 100);
        }
        else
        {
            pct = stage == LocalBackupStage.Completed ? 100.0 : 0.0;
        }

        var now = DateTime.UtcNow;

        bool shouldReport = stage != _lastReportStage ||
                            pct >= 100 ||
                            Math.Abs(pct - _lastReportPct) >= 1.0 ||
                            (now - _lastReportTime).TotalMilliseconds >= 150;

        if (shouldReport)
        {
            _lastReportStage = stage;
            _lastReportPct = pct;
            _lastReportTime = now;

            CurrentProgress = new LocalBackupProgress(stage, pct, processed, total, filesDone, totalFiles, fileName, error);
            OnLocalBackupProgressChanged?.Invoke(CurrentProgress);
            OnLocalBackupStateChanged?.Invoke();
        }
    }

    public BackupService(
        ILogger<BackupService> logger,
        CompanyProfileRepository companyProfiles,
        IConfiguration config,
        ISystemLogService? systemLog = null)
        : this(logger, companyProfiles, config, null, null, null, null, systemLog)
    {
    }

    public BackupService(
        ILogger<BackupService> logger,
        CompanyProfileRepository companyProfiles,
        IConfiguration config,
        DiskPersistenceService? persistence,
        Database? db,
        ISystemLogService? systemLog = null)
        : this(logger, companyProfiles, config, persistence, db, null, null, systemLog)
    {
    }

    public BackupService(
        ILogger<BackupService> logger,
        CompanyProfileRepository companyProfiles,
        IConfiguration config,
        DiskPersistenceService? persistence,
        Database? db,
        IBackupEncryptionService? encryption,
        BackupDestinationCoordinator? destinationCoordinator,
        ISystemLogService? systemLog = null)
    {
        _logger = logger;
        _companyProfiles = companyProfiles;
        _config = config;
        _dataPath = config["DataPath"] ?? "Data";
        _persistence = persistence;
        _db = db;
        _encryption = encryption ?? new BackupEncryptionService();
        _destinationCoordinator = destinationCoordinator;
        _systemLog = systemLog;
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
                    var now = TimeZoneHelper.GetCompanyTime(_companyProfiles);
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

    public Task<BackupResult> RunBackupNowAsync() => RunBackupInternal(isManual: true);

    private async Task<BackupResult> RunBackupInternal(bool isManual)
    {
        if (Interlocked.CompareExchange(ref _isRunningState, 1, 0) != 0)
            return BackupResult.Busy();

        try
        {
            IsLocalBackupInProgress = true;
            ReportProgress(LocalBackupStage.Preparing, 0, 0, 0, 0);

            if (_persistence != null)
            {
                await _persistence.FlushAllAsync();
            }

            var profile = _companyProfiles.Get();
            var backupDir = Path.Combine(_dataPath, "Backups");
            Directory.CreateDirectory(backupDir);

            var timestamp = DateTime.UtcNow.ToString("yyyy-MM-dd_HHmmss");
            var prefix = isManual ? "Spokes_Backup_Manual_" : "Spokes_Backup_";

            bool encrypt = (profile?.Backup.EncryptBackupsWithMasterPassword ?? false) && _encryption != null;
            var extension = encrypt ? ".spkbackup" : ".zip";
            var finalBackupPath = Path.Combine(backupDir, $"{prefix}{timestamp}{extension}");
            var tempZipPath = Path.Combine(backupDir, $"{prefix}{timestamp}.tmp.zip");

            // Scan candidate files to determine total bytes and file count
            var dataDir = new DirectoryInfo(_dataPath);
            var candidateFiles = new List<(FileInfo File, string ZipEntryName)>();
            long totalUncompressedBytes = 0;

            if (dataDir.Exists)
            {
                foreach (var file in dataDir.GetFiles("*", SearchOption.AllDirectories))
                {
                    // Skip the Backups folder and temp files
                    var relativePath = Path.GetRelativePath(_dataPath, file.FullName);
                    if (relativePath.StartsWith("Backups", StringComparison.OrdinalIgnoreCase))
                        continue;
                    if (file.Extension.Equals(".tmp", StringComparison.OrdinalIgnoreCase))
                        continue;

                    var zipEntryName = relativePath.Replace('\\', '/');

                    if (profile != null && !profile.BackupEmployeeEmails)
                    {
                        if (zipEntryName.StartsWith("Employees/", StringComparison.OrdinalIgnoreCase) &&
                            zipEntryName.Contains("/Email/", StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }
                    }

                    candidateFiles.Add((file, zipEntryName));
                    totalUncompressedBytes += file.Length;
                }
            }

            int totalFiles = candidateFiles.Count;
            ReportProgress(LocalBackupStage.Archiving, 0, totalUncompressedBytes, 0, totalFiles);

            // Create the zip, streaming entries and reporting progress
            await Task.Run(async () =>
            {
                using var zip = ZipFile.Open(tempZipPath, ZipArchiveMode.Create);
                long bytesProcessed = 0;
                int filesProcessed = 0;
                var buffer = new byte[81920];

                foreach (var (file, zipEntryName) in candidateFiles)
                {
                    var entry = zip.CreateEntry(zipEntryName, CompressionLevel.Optimal);
                    try
                    {
                        using var fileStream = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 81920, useAsync: true);
                        using var entryStream = entry.Open();
                        int bytesRead;
                        while ((bytesRead = await fileStream.ReadAsync(buffer)) > 0)
                        {
                            await entryStream.WriteAsync(buffer.AsMemory(0, bytesRead));
                            bytesProcessed += bytesRead;
                            ReportProgress(LocalBackupStage.Archiving, bytesProcessed, totalUncompressedBytes, filesProcessed, totalFiles, zipEntryName);
                        }
                    }
                    catch (FileNotFoundException)
                    {
                        // File was removed concurrently between pre-scan and zip, continue gracefully
                    }

                    filesProcessed++;
                    ReportProgress(LocalBackupStage.Archiving, bytesProcessed, totalUncompressedBytes, filesProcessed, totalFiles, zipEntryName);
                }
            });

            if (encrypt)
            {
                var masterPassword = _config["SPOKES_MASTER_PASSWORD"]
                    ?? Environment.GetEnvironmentVariable("SPOKES_MASTER_PASSWORD")
                    ?? profile?.PublicChannelMasterPassword;

                if (string.IsNullOrEmpty(masterPassword))
                {
                    throw new InvalidOperationException("Cannot encrypt backup: SPOKES_MASTER_PASSWORD is not configured in the server environment.");
                }

                var zipFileInfo = new FileInfo(tempZipPath);
                long totalZipBytes = zipFileInfo.Length;
                ReportProgress(LocalBackupStage.Encrypting, 0, totalZipBytes, 0, 0);

                var encProgress = new Progress<long>(bytesEncrypted =>
                {
                    ReportProgress(LocalBackupStage.Encrypting, bytesEncrypted, totalZipBytes, 0, 0);
                });

                await using (var input = File.OpenRead(tempZipPath))
                await using (var output = File.Create(finalBackupPath))
                {
                    await _encryption!.EncryptBackupAsync(input, output, masterPassword, encProgress);
                }

                try { File.Delete(tempZipPath); } catch { }
            }
            else
            {
                // Atomic move
                File.Move(tempZipPath, finalBackupPath, overwrite: true);
            }

            ReportProgress(LocalBackupStage.Finalizing, totalUncompressedBytes, totalUncompressedBytes, totalFiles, totalFiles);

            // Prune old backups
            var retention = profile?.BackupRetentionCount ?? 5;
            PruneBackups(backupDir, retention);

            LastBackupAt = DateTime.UtcNow;
            LastBackupStatus = "Success";
            IsLocalBackupInProgress = false;
            ReportProgress(LocalBackupStage.Completed, totalUncompressedBytes, totalUncompressedBytes, totalFiles, totalFiles);
            _logger.LogInformation("Backup created successfully: {Path}", finalBackupPath);

            var finalFileInfo = new FileInfo(finalBackupPath);
            var sizeFormatted = finalFileInfo.Length < 1024 * 1024
                ? $"{finalFileInfo.Length / 1024.0:F1} KB"
                : $"{finalFileInfo.Length / (1024.0 * 1024.0):F1} MB";

            _systemLog?.LogInfo("Backup",
                $"Backup created successfully: {Path.GetFileName(finalBackupPath)}",
                $"Type: {(isManual ? "Manual" : "Scheduled")}\nFile: {Path.GetFileName(finalBackupPath)}\nPath: {finalBackupPath}\nSize: {sizeFormatted} ({finalFileInfo.Length:N0} bytes)");

            // Dispatch to remote destinations if configured
            if (_destinationCoordinator != null)
            {
                try
                {
                    var remoteResults = await _destinationCoordinator.DispatchUploadAsync(finalBackupPath, Path.GetFileName(finalBackupPath));
                    foreach (var (dest, res) in remoteResults)
                    {
                        if (res.Success)
                        {
                            _logger?.LogInformation("Remote backup uploaded to {Destination}: {File}", dest, Path.GetFileName(finalBackupPath));
                            _systemLog?.LogInfo("Backup", $"Remote backup uploaded to {dest}: {Path.GetFileName(finalBackupPath)}");
                        }
                        else
                        {
                            _logger?.LogWarning("Remote backup to {Destination} failed: {Error}", dest, res.ErrorMessage);
                            _systemLog?.LogError("Backup", $"Remote backup to {dest} failed: {res.ErrorMessage}");
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger?.LogError(ex, "Error uploading to remote backup destinations");
                }
            }

            return BackupResult.Succeeded(finalBackupPath);
        }
        catch (Exception ex)
        {
            IsLocalBackupInProgress = false;
            LastBackupStatus = $"Failed: {ex.Message}";
            ReportProgress(LocalBackupStage.Failed, 0, 0, 0, 0, error: ex.Message);
            _logger.LogError(ex, "Backup failed");
            _systemLog?.LogError("Backup", $"Backup failed: {ex.Message}", ex.ToString());
            return BackupResult.Failed(ex.Message);
        }
        finally
        {
            IsLocalBackupInProgress = false;
            Interlocked.Exchange(ref _isRunningState, 0);
            OnLocalBackupStateChanged?.Invoke();
        }
    }

    private void PruneBackups(string backupDir, int retentionCount)
    {
        try
        {
            var backups = new DirectoryInfo(backupDir)
                .GetFiles("Spokes_Backup_*.*")
                .Where(f => !f.Name.Contains("_Manual_", StringComparison.OrdinalIgnoreCase))
                .Where(f => f.Extension.Equals(".zip", StringComparison.OrdinalIgnoreCase) || f.Extension.Equals(".spkbackup", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(f => f.CreationTimeUtc)
                .ToList();

            if (backups.Count > retentionCount)
            {
                foreach (var old in backups.Skip(retentionCount))
                {
                    old.Delete();
                    _logger.LogInformation("Pruned old backup: {Name}", old.Name);
                    _systemLog?.LogInfo("Backup", $"Pruned old backup: {old.Name}");
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to prune old backups");
            _systemLog?.LogError("Backup", $"Failed to prune old backups: {ex.Message}", ex.ToString());
        }
    }

    public List<BackupFileInfo> GetBackups()
    {
        var backupDir = Path.Combine(_dataPath, "Backups");
        if (!Directory.Exists(backupDir))
            return [];

        return new DirectoryInfo(backupDir)
            .GetFiles("Spokes_Backup_*.*")
            .Where(f => f.Extension.Equals(".zip", StringComparison.OrdinalIgnoreCase) || f.Extension.Equals(".spkbackup", StringComparison.OrdinalIgnoreCase))
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
            _systemLog?.LogInfo("Backup", $"Deleted backup: {Path.GetFileName(fullPath)}");
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

    public virtual Task<BackupRestoreResult> RestoreBackupAsync(string filePath, ILogger? logger, CancellationToken cancellationToken)
        => RestoreBackupAsync(filePath, logger, cancellationToken, null);

    public virtual async Task<BackupRestoreResult> RestoreBackupAsync(string filePath, ILogger? logger = null, CancellationToken cancellationToken = default, IProgress<BackupRestoreProgress>? progress = null)
    {
        var targetLogger = logger ?? _logger;

        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
        {
            targetLogger.LogWarning("RestoreBackupAsync failed: backup file not found at '{Path}'", filePath);
            return BackupRestoreResult.Failed("Backup file not found.", exception: new FileNotFoundException("Backup file not found", filePath ?? string.Empty));
        }

        progress?.Report(new BackupRestoreProgress("Checking backup file...", 5));

        var fullFilePath = Path.GetFullPath(filePath);
        var fullDataPath = Path.GetFullPath(_dataPath);
        if (!fullDataPath.EndsWith(Path.DirectorySeparatorChar))
        {
            fullDataPath += Path.DirectorySeparatorChar;
        }

        string effectiveZipPath = fullFilePath;
        string? tempDecryptedZip = null;

        try
        {
            // Detect if archive is encrypted (.spkbackup or SPKENC01 magic bytes)
            bool isEncrypted = false;
            if (_encryption != null)
            {
                isEncrypted = _encryption.IsEncryptedFile(fullFilePath);
            }
            else if (fullFilePath.EndsWith(".spkbackup", StringComparison.OrdinalIgnoreCase))
            {
                isEncrypted = true;
            }

            if (isEncrypted)
            {
                if (_encryption == null)
                {
                    return BackupRestoreResult.Failed("Cannot restore encrypted backup: BackupEncryptionService is unavailable.");
                }

                var masterPassword = _config["SPOKES_MASTER_PASSWORD"]
                    ?? Environment.GetEnvironmentVariable("SPOKES_MASTER_PASSWORD")
                    ?? _companyProfiles.Get()?.PublicChannelMasterPassword;

                if (string.IsNullOrEmpty(masterPassword))
                {
                    return BackupRestoreResult.Failed("Cannot restore encrypted backup: SPOKES_MASTER_PASSWORD is not configured in the server environment.");
                }

                progress?.Report(new BackupRestoreProgress("Decrypting encrypted backup...", 15));

                tempDecryptedZip = Path.Combine(Path.GetTempPath(), $"spokes_decrypted_{Guid.NewGuid():N}.zip");
                await using (var input = File.OpenRead(fullFilePath))
                await using (var output = File.Create(tempDecryptedZip))
                {
                    await _encryption.DecryptBackupAsync(input, output, masterPassword, cancellationToken);
                }
                effectiveZipPath = tempDecryptedZip;
            }

            // 1. Validate it's a valid zip and pre-validate all entries against Zip Slip path traversal
            // BEFORE deleting any existing database files to prevent data loss on invalid or malicious archives.
            progress?.Report(new BackupRestoreProgress("Validating archive integrity...", 20));
            try
            {
                using var zip = ZipFile.OpenRead(effectiveZipPath);
                foreach (var entry in zip.Entries)
                {
                    var safeEntryName = entry.FullName.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar);
                    var destinationPath = Path.GetFullPath(Path.Combine(fullDataPath, safeEntryName));

                    if (!destinationPath.StartsWith(fullDataPath, StringComparison.OrdinalIgnoreCase))
                    {
                        targetLogger.LogError("RestoreBackupAsync failed: zip entry '{Entry}' resolves outside data directory", entry.FullName);
                        return BackupRestoreResult.Failed("Zip entry is outside of the target directory.", exception: new IOException("Zip entry is outside of the target directory."));
                    }
                }
            }
            catch (InvalidDataException ex)
            {
                targetLogger.LogError(ex, "RestoreBackupAsync failed: invalid backup file format");
                return BackupRestoreResult.Failed("Invalid backup file format.", exception: new InvalidDataException("Invalid backup file format.", ex));
            }
            catch (Exception ex)
            {
                targetLogger.LogError(ex, "RestoreBackupAsync pre-validation failed");
                return BackupRestoreResult.Failed(ex.Message, exception: ex);
            }

            var stagingDir = Path.Combine(Path.GetTempPath(), $"spokes_restore_{Guid.NewGuid():N}");
            var fullStagingPath = Path.GetFullPath(stagingDir);
            if (!fullStagingPath.EndsWith(Path.DirectorySeparatorChar))
            {
                fullStagingPath += Path.DirectorySeparatorChar;
            }

            try
            {
                Directory.CreateDirectory(fullStagingPath);

                // 2. Extract backup entries into staging directory first
                using (var zip = ZipFile.OpenRead(effectiveZipPath))
                {
                    int totalEntries = zip.Entries.Count;
                    int extractedCount = 0;

                    foreach (var entry in zip.Entries)
                    {
                        cancellationToken.ThrowIfCancellationRequested();

                        var safeEntryName = entry.FullName.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar);
                        var stagedEntryPath = Path.GetFullPath(Path.Combine(fullStagingPath, safeEntryName));

                        if (!stagedEntryPath.StartsWith(fullStagingPath, StringComparison.OrdinalIgnoreCase))
                        {
                            targetLogger.LogError("RestoreBackupAsync failed: zip entry '{Entry}' resolves outside staging directory", entry.FullName);
                            return BackupRestoreResult.Failed("Zip entry is outside of the target directory.", exception: new IOException("Zip entry is outside of the target directory."));
                        }

                        if (string.IsNullOrEmpty(entry.Name) || safeEntryName.EndsWith(Path.DirectorySeparatorChar))
                        {
                            Directory.CreateDirectory(stagedEntryPath);
                        }
                        else
                        {
                            var dir = Path.GetDirectoryName(stagedEntryPath);
                            if (!string.IsNullOrEmpty(dir))
                            {
                                Directory.CreateDirectory(dir);
                            }

                            await using (var entryStream = entry.Open())
                            await using (var outputStream = new FileStream(stagedEntryPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
                            {
                                await entryStream.CopyToAsync(outputStream, cancellationToken);
                            }
                        }

                        extractedCount++;
                        int extractPct = 30 + (totalEntries > 0 ? (int)(extractedCount * 50.0 / totalEntries) : 40);
                        progress?.Report(new BackupRestoreProgress("Extracting backup files...", extractPct, safeEntryName));
                    }
                }

                // Flush all pending writes to disk to prevent sharing violations during restore
                if (_persistence != null)
                {
                    await _persistence.FlushAllAsync();
                }

                progress?.Report(new BackupRestoreProgress("Restoring data files...", 85));

                // 3. Extraction succeeded; now clear current Data directory (except Backups and the backup archive itself)
                var dataDir = new DirectoryInfo(_dataPath);
                if (dataDir.Exists)
                {
                    foreach (var file in dataDir.GetFiles())
                    {
                        if (file.FullName.Equals(fullFilePath, StringComparison.OrdinalIgnoreCase)) continue;
                        if (file.Extension == ".tmp") continue;

                        try { file.Delete(); } catch (Exception ex) { targetLogger.LogWarning(ex, "Failed to delete file: {FileName}", file.Name); }
                    }

                    foreach (var dir in dataDir.GetDirectories())
                    {
                        if (dir.Name.Equals("Backups", StringComparison.OrdinalIgnoreCase)) continue;
                        try { dir.Delete(recursive: true); } catch (Exception ex) { targetLogger.LogWarning(ex, "Failed to delete directory: {DirName}", dir.Name); }
                    }
                }
                else
                {
                    Directory.CreateDirectory(_dataPath);
                }

                // 4. Copy staged files and directories to the destination data directory
                CopyExtractedDirectory(fullStagingPath, fullDataPath, targetLogger);

                // 5. Reload Database
                progress?.Report(new BackupRestoreProgress("Reloading database & services...", 95));
                _db?.ReloadAll();

                targetLogger.LogInformation("Database restored from backup: {Path}", filePath);
                _systemLog?.LogInfo("Backup", $"Database restored from backup: {Path.GetFileName(filePath)}");

                bool casdoorRestarted = false;
                var restoredSysConfig = _db?.SystemConfigs.Get();
                if (restoredSysConfig?.ProviderType == IdpType.BuiltInCasdoor)
                {
                    casdoorRestarted = await SyncAndRestartRestoredCasdoorAsync(fullDataPath, restoredSysConfig, targetLogger, cancellationToken);
                }

                progress?.Report(new BackupRestoreProgress("Restore completed successfully!", 100));
                return BackupRestoreResult.Succeeded(casdoorRestarted, details: $"Restored from {Path.GetFileName(filePath)}");
            }
            finally
            {
                try
                {
                    if (Directory.Exists(fullStagingPath))
                    {
                        Directory.Delete(fullStagingPath, recursive: true);
                    }
                }
                catch (Exception cleanupEx)
                {
                    targetLogger.LogWarning(cleanupEx, "Failed to clean up staging directory: {Dir}", fullStagingPath);
                }
            }
        }
        catch (InvalidMasterPasswordException ex)
        {
            targetLogger.LogError(ex, "RestoreBackupAsync failed: master password mismatch");
            return BackupRestoreResult.Failed("Backup decryption failed: The server's SPOKES_MASTER_PASSWORD does not match the password used to encrypt this backup.", exception: ex);
        }
        catch (Exception ex)
        {
            targetLogger.LogError(ex, "Failed during backup restoration execution");
            _systemLog?.LogError("Backup", $"Failed during backup restoration: {ex.Message}", ex.ToString());
            return BackupRestoreResult.Failed(ex.Message, exception: ex);
        }
        finally
        {
            if (tempDecryptedZip != null && File.Exists(tempDecryptedZip))
            {
                try { File.Delete(tempDecryptedZip); } catch { }
            }
        }
    }

    private async Task<bool> SyncAndRestartRestoredCasdoorAsync(
        string fullDataPath,
        SystemConfig restoredSysConfig,
        ILogger targetLogger,
        CancellationToken cancellationToken)
    {
        try
        {
            // 1. Ensure casdoor.enabled exists so entrypoint.sh autostarts Casdoor on future container restarts
            var enabledPath = Path.Combine(fullDataPath, "casdoor.enabled");
            if (!File.Exists(enabledPath))
            {
                await File.WriteAllTextAsync(enabledPath, "true", cancellationToken);
            }

            // 2. Sync persistent Casdoor configuration to /app/casdoor/conf/app.conf BEFORE restarting Casdoor.
            // On an unconfigured container, entrypoint.sh skipped copying /data/casdoor/conf/app.conf because
            // casdoor.enabled did not exist yet, leaving Casdoor's factory MySQL app.conf in /app/casdoor/conf/app.conf.
            var persistentConfDir = Path.Combine(fullDataPath, "casdoor", "conf");
            var persistentAppConf = Path.Combine(persistentConfDir, "app.conf");
            if (!File.Exists(persistentAppConf) && !string.IsNullOrWhiteSpace(restoredSysConfig.ServerPublicUrl))
            {
                Directory.CreateDirectory(persistentConfDir);
                var generator = new CasdoorConfigTemplateGenerator();
                var generatedConf = generator.GenerateAppConf(restoredSysConfig.ServerPublicUrl.TrimEnd('/'), _dataPath, "prod");
                await File.WriteAllTextAsync(persistentAppConf, generatedConf, cancellationToken);
            }

            try
            {
                const string ephemeralCasdoorDir = "/app/casdoor";
                var ephemeralConfDir = Path.Combine(ephemeralCasdoorDir, "conf");
                if (Directory.Exists(persistentConfDir))
                {
                    Directory.CreateDirectory(ephemeralConfDir);
                    foreach (var file in Directory.GetFiles(persistentConfDir))
                    {
                        var destFile = Path.Combine(ephemeralConfDir, Path.GetFileName(file));
                        File.Copy(file, destFile, overwrite: true);
                    }
                }

                var persistentInitData = Path.Combine(fullDataPath, "casdoor", "init_data.json");
                var ephemeralInitData = Path.Combine(ephemeralCasdoorDir, "init_data.json");
                if (File.Exists(persistentInitData))
                {
                    File.Copy(persistentInitData, ephemeralInitData, overwrite: true);
                }
                else if (File.Exists(ephemeralInitData))
                {
                    File.Delete(ephemeralInitData);
                }
            }
            catch (Exception syncEx)
            {
                targetLogger.LogWarning(syncEx, "Could not sync restored Casdoor configuration to /app/casdoor.");
            }

            // 3. Restart Casdoor via supervisorctl and wait for exit
            var psi = new System.Diagnostics.ProcessStartInfo("supervisorctl", "restart casdoor")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            using var process = System.Diagnostics.Process.Start(psi);
            if (process == null)
            {
                return false;
            }

            var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);
            var output = await outputTask;
            var error = await errorTask;
            targetLogger.LogInformation("Restarted builtin Casdoor after backup restore (exit code {ExitCode}, output: {Output}).", process.ExitCode, output.Trim());
            if (!string.IsNullOrWhiteSpace(error))
            {
                targetLogger.LogWarning("Supervisor restart casdoor stderr: {Error}", error.Trim());
            }

            // Note: When casdoor was previously in STOPPED state (such as on an unconfigured container),
            // 'supervisorctl restart casdoor' outputs 'casdoor: ERROR (not running)' followed by 'casdoor: started'
            // and exits with a non-zero status code even though Casdoor was started.
            var casdoorStarted = process.ExitCode == 0 || output.Contains("started", StringComparison.OrdinalIgnoreCase);
            if (casdoorStarted)
            {
                // 4. Wait until Casdoor's OIDC discovery endpoint is listening on port 8000 so that
                // when Spokes_Server restarts and redirects to /sso/login/auto, OIDC Challenge() succeeds immediately.
                using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
                var sw = System.Diagnostics.Stopwatch.StartNew();
                while (sw.Elapsed < TimeSpan.FromSeconds(15) && !cancellationToken.IsCancellationRequested)
                {
                    try
                    {
                        using var response = await httpClient.GetAsync("http://127.0.0.1:8000/.well-known/openid-configuration", cancellationToken);
                        if (response.IsSuccessStatusCode)
                        {
                            targetLogger.LogInformation("Builtin Casdoor OIDC discovery ready after {Elapsed:0.0}s.", sw.Elapsed.TotalSeconds);
                            break;
                        }
                    }
                    catch
                    {
                        // Casdoor is still starting up
                    }

                    await Task.Delay(250, cancellationToken);
                }
            }

            return casdoorStarted;
        }
        catch (Exception ex)
        {
            targetLogger.LogError(ex, "Failed to restart builtin Casdoor after backup restore.");
            return false;
        }
    }

    private static void CopyExtractedDirectory(string sourceDir, string targetDir, ILogger logger)
    {
        foreach (var dirPath in Directory.GetDirectories(sourceDir, "*", SearchOption.AllDirectories))
        {
            var relativePath = Path.GetRelativePath(sourceDir, dirPath);
            var destSubDir = Path.Combine(targetDir, relativePath);
            Directory.CreateDirectory(destSubDir);

            if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
            {
                try
                {
                    File.SetUnixFileMode(destSubDir,
                        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                        UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute |
                        UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Failed to set unix file mode on directory: {Dir}", destSubDir);
                }
            }
        }

        foreach (var filePath in Directory.GetFiles(sourceDir, "*", SearchOption.AllDirectories))
        {
            var relativePath = Path.GetRelativePath(sourceDir, filePath);
            var destFile = Path.Combine(targetDir, relativePath);
            var destFileDir = Path.GetDirectoryName(destFile);
            if (!string.IsNullOrEmpty(destFileDir))
            {
                Directory.CreateDirectory(destFileDir);
            }

            File.Copy(filePath, destFile, overwrite: true);

            if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
            {
                try
                {
                    File.SetUnixFileMode(destFile,
                        UnixFileMode.UserRead | UnixFileMode.UserWrite |
                        UnixFileMode.GroupRead | UnixFileMode.GroupWrite |
                        UnixFileMode.OtherRead | UnixFileMode.OtherWrite);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Failed to set unix file mode on file: {File}", destFile);
                }
            }
        }
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
    public bool IsEncrypted => FileName.EndsWith(".spkbackup", StringComparison.OrdinalIgnoreCase);

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

public record BackupResult(bool Success, string? ZipPath, string? ErrorMessage, bool IsBusy = false)
{
    public static BackupResult Succeeded(string zipPath) => new(true, zipPath, null, false);
    public static BackupResult Busy() => new(false, null, "A backup is already in progress", true);
    public static BackupResult Failed(string errorMessage) => new(false, null, errorMessage, false);
}

public record BackupRestoreResult(bool Success, string? Details = null, bool CasdoorRestarted = false, string? ErrorMessage = null, Exception? Exception = null)
{
    public static BackupRestoreResult Succeeded(bool casdoorRestarted = false, string? details = null) => new(true, details, casdoorRestarted, null, null);
    public static BackupRestoreResult Failed(string errorMessage, string? details = null, Exception? exception = null) => new(false, details, false, errorMessage, exception);
}

public record BackupRestoreProgress(string Stage, int Percentage, string? Detail = null);
