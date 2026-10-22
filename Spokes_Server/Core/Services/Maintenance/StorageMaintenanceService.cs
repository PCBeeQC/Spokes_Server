using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Spokes_Server.Aggregate;
using Spokes_Server.Core.Data;
using Spokes_Server.Core.Helpers;
using Spokes_Server.Core.Services.Storage;

namespace Spokes_Server.Core.Services.Maintenance;

public class OrphanCleanupResult
{
    public int FilesScanned { get; set; }
    public int ActiveReferencesCount { get; set; }
    public int OrphansDeleted { get; set; }
    public long BytesReclaimed { get; set; }
    public List<string> Errors { get; set; } = [];
    public string FormattedBytesReclaimed => FileHelper.FormatBytes(BytesReclaimed);
}

/// <summary>
/// Scans storage directories and purges unreferenced orphaned files from the filesystem.
/// Safe against in-flight uploads via configurable safety window.
/// </summary>
public class StorageMaintenanceService
{
    private readonly Database _db;
    private readonly IFileService _fileService;
    private readonly IConfiguration _config;
    private readonly ILogger<StorageMaintenanceService> _logger;
    private readonly string _uploadsBasePath;

    public StorageMaintenanceService(
        Database db,
        IFileService fileService,
        IConfiguration config,
        ILogger<StorageMaintenanceService> logger)
    {
        _db = db;
        _fileService = fileService;
        _config = config;
        _logger = logger;

        var dataPath = config["DataPath"] ?? "Data";
        _uploadsBasePath = Path.Combine(dataPath, "Uploads");
    }

    /// <summary>
    /// Sweeps the Uploads directory and removes files not referenced by any database entity.
    /// Files modified within the safety window are preserved to avoid race conditions with active uploads.
    /// </summary>
    public virtual async Task<OrphanCleanupResult> PurgeOrphanedUploadsAsync(
        TimeSpan? safetyWindow = null,
        CancellationToken cancellationToken = default)
    {
        var result = new OrphanCleanupResult();
        var effectiveSafetyWindow = safetyWindow ?? TimeSpan.FromMinutes(15);
        var cutoffTimeUtc = DateTime.UtcNow - effectiveSafetyWindow;

        if (!Directory.Exists(_uploadsBasePath))
        {
            _logger.LogInformation("[StorageMaintenance] Uploads directory does not exist at {Path}. Sweep complete.", _uploadsBasePath);
            return result;
        }

        return await Task.Run(() =>
        {
            // 1. Collect all active referenced file paths across all database aggregates
            var activePaths = CollectAllActiveReferencedPaths();
            result.ActiveReferencesCount = activePaths.Count;

            _logger.LogInformation(
                "[StorageMaintenance] Starting orphan sweep with {Count} active entity references. Preserving files modified after {Cutoff:u}.",
                activePaths.Count, cutoffTimeUtc);

            // 2. Scan physical files under Uploads/
            var uploadsDir = new DirectoryInfo(_uploadsBasePath);
            FileInfo[] physicalFiles;
            try
            {
                physicalFiles = uploadsDir.GetFiles("*", SearchOption.AllDirectories);
            }
            catch (Exception ex)
            {
                result.Errors.Add($"Failed to enumerate files: {ex.Message}");
                _logger.LogError(ex, "[StorageMaintenance] Error enumerating files under {Path}", _uploadsBasePath);
                return result;
            }

            result.FilesScanned = physicalFiles.Length;

            foreach (var file in physicalFiles)
            {
                if (cancellationToken.IsCancellationRequested) break;

                try
                {
                    // Never delete temporary files (managed by TempFileAccessProvider / TempFileCleanup)
                    var relativePath = Path.GetRelativePath(_uploadsBasePath, file.FullName);
                    if (relativePath.StartsWith("temp", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    // Preserve files modified within the safety window
                    if (file.LastWriteTimeUtc >= cutoffTimeUtc)
                    {
                        continue;
                    }

                    var normalized = NormalizeStoragePath(relativePath);

                    if (!activePaths.Contains(normalized))
                    {
                        long fileSize = file.Length;
                        file.Delete();
                        result.OrphansDeleted++;
                        result.BytesReclaimed += fileSize;

                        _logger.LogDebug("[StorageMaintenance] Deleted orphan: {RelativePath} ({Bytes} bytes)", relativePath, fileSize);
                    }
                }
                catch (Exception ex)
                {
                    result.Errors.Add($"{file.Name}: {ex.Message}");
                    _logger.LogWarning(ex, "[StorageMaintenance] Failed to delete file {FullName}", file.FullName);
                }
            }

            // 3. Prune empty directories under Uploads
            PruneEmptyDirectories(_uploadsBasePath);

            _logger.LogInformation(
                "[StorageMaintenance] Orphan sweep finished: {Deleted} orphans deleted, {Bytes} reclaimed ({Errors} errors).",
                result.OrphansDeleted, result.FormattedBytesReclaimed, result.Errors.Count);

            return result;
        }, cancellationToken);
    }

    private HashSet<string> CollectAllActiveReferencedPaths()
    {
        var activePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Register(string? rawPath)
        {
            if (string.IsNullOrWhiteSpace(rawPath)) return;
            var normalized = NormalizeStoragePath(rawPath);
            if (!string.IsNullOrEmpty(normalized))
            {
                activePaths.Add(normalized);

                // Register companion thumbnail ({fileName}_thumb.jpg) as well as legacy stripped-extension variant
                activePaths.Add(FileService.GetThumbnailFileName(normalized));
                var ext = Path.GetExtension(normalized);
                if (!string.IsNullOrEmpty(ext))
                {
                    var legacyThumb = normalized[..^ext.Length] + "_thumb.jpg";
                    activePaths.Add(legacyThumb);
                }
            }
        }

        void Harvest(IHasAssociatedFiles? entity)
        {
            if (entity == null) return;
            try
            {
                foreach (var p in entity.GetAssociatedFilePaths())
                {
                    Register(p);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[StorageMaintenance] Error harvesting associated files from {Type}", entity.GetType().Name);
            }
        }

        // Project Notes & Attachments
        foreach (var note in _db.ProjectNotes.GetAll()) Harvest(note);

        // Chat Messages & Attachments
        foreach (var msg in _db.ChatMessages.GetAll()) Harvest(msg);

        // Project Documents
        foreach (var doc in _db.ProjectDocuments.GetAll()) Harvest(doc);

        // Projects & POs
        foreach (var proj in _db.Projects.GetAll()) Harvest(proj);

        // Bills & Invoices
        foreach (var bill in _db.Bills.GetAll()) Harvest(bill);

        // Recurring Expenses & Billing Records
        foreach (var rec in _db.RecurringExpenses.GetAll()) Harvest(rec);

        // Photo Albums & Media
        foreach (var album in _db.Albums.GetAll()) Harvest(album);

        // Employees & Avatars
        foreach (var emp in _db.Employees.GetAll()) Harvest(emp);

        // Expense Reports & Receipts
        foreach (var report in _db.ExpenseReports.GetAll()) Harvest(report);

        return activePaths;
    }

    private static string NormalizeStoragePath(string path)
    {
        var clean = path.Trim();
        var qIdx = clean.IndexOf('?');
        if (qIdx >= 0) clean = clean[..qIdx];

        if (clean.StartsWith("/spokesapi/files/thumb/", StringComparison.OrdinalIgnoreCase))
        {
            clean = clean["/spokesapi/files/thumb/".Length..];
        }
        else if (clean.StartsWith("/spokesapi/files/", StringComparison.OrdinalIgnoreCase))
        {
            clean = clean["/spokesapi/files/".Length..];
        }
        else if (clean.StartsWith("Uploads/", StringComparison.OrdinalIgnoreCase))
        {
            clean = clean["Uploads/".Length..];
        }

        return clean.Replace('/', Path.DirectorySeparatorChar).TrimStart(Path.DirectorySeparatorChar);
    }

    private static void PruneEmptyDirectories(string rootDir)
    {
        try
        {
            foreach (var dir in Directory.GetDirectories(rootDir, "*", SearchOption.AllDirectories).OrderByDescending(d => d.Length))
            {
                try
                {
                    if (Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any())
                    {
                        Directory.Delete(dir);
                    }
                }
                catch { /* Ignore directory deletion locks */ }
            }
        }
        catch { /* Ignore enumeration errors */ }
    }
}
