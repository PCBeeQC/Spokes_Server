using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Spokes_Server.Aggregate;
using Spokes_Server.Core.Data;
using Spokes_Server.Core.Helpers;
using Spokes_Server.Core.Middleware;
using Spokes_Server.Core.Models.Core;
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
/// Scans storage directories and purges unreferenced orphaned files and stale device sessions.
/// Safe against in-flight uploads via configurable safety window.
/// </summary>
public partial class StorageMaintenanceService
{
    [GeneratedRegex(@"/spokesapi/files/(?:thumb/)?[^\s""'<>)]+", RegexOptions.IgnoreCase)]
    private static partial Regex EmbeddedFileUrlRegex();

    private readonly Database _db;
    private readonly ILogger<StorageMaintenanceService> _logger;
    private readonly string _uploadsBasePath;
    private readonly string _pdfsBasePath;

    public StorageMaintenanceService(
        Database db,
        IConfiguration config,
        ILogger<StorageMaintenanceService> logger)
    {
        _db = db;
        _logger = logger;

        var dataPath = config["DataPath"] ?? "Data";
        _uploadsBasePath = Path.Combine(dataPath, "Uploads");
        _pdfsBasePath = Path.Combine(dataPath, "PDFs");
    }

    /// <summary>
    /// Sweeps the Uploads directory and removes files not referenced by any database entity.
    /// Files modified within the safety window are preserved to avoid race conditions with active uploads.
    /// </summary>
    public virtual async Task<OrphanCleanupResult> PurgeOrphanedUploadsAsync(
        TimeSpan? safetyWindow = null,
        CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(_uploadsBasePath))
        {
            _logger.LogInformation("[StorageMaintenance] Uploads directory does not exist at {Path}. Sweep complete.", _uploadsBasePath);
            return new OrphanCleanupResult();
        }

        return await Task.Run(() =>
        {
            var activePaths = CollectAllActiveReferencedPaths();
            return SweepOrphanedDirectory(
                _uploadsBasePath,
                activePaths,
                safetyWindow ?? TimeSpan.FromMinutes(15),
                isSkipped: file =>
                {
                    var relativePath = Path.GetRelativePath(_uploadsBasePath, file.FullName);
                    return relativePath.StartsWith("temp", StringComparison.OrdinalIgnoreCase);
                },
                toLookupKey: file => NormalizeStoragePath(Path.GetRelativePath(_uploadsBasePath, file.FullName)),
                sweepName: "Uploads",
                cancellationToken);
        }, cancellationToken);
    }

    /// <summary>
    /// Sweeps the PDFs directory and removes rendered PDF files not referenced by any active document, quote, invoice, PO, report, or paystub.
    /// </summary>
    public virtual async Task<OrphanCleanupResult> PurgeOrphanedPdfsAsync(
        TimeSpan? safetyWindow = null,
        CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(_pdfsBasePath))
        {
            return new OrphanCleanupResult();
        }

        return await Task.Run(() =>
        {
            var activePdfPaths = CollectAllActivePdfRelativePaths();
            return SweepOrphanedDirectory(
                _pdfsBasePath,
                activePdfPaths,
                safetyWindow ?? TimeSpan.FromMinutes(15),
                isSkipped: _ => false,
                toLookupKey: file => NormalizePdfRelativePath(Path.GetRelativePath(_pdfsBasePath, file.FullName)),
                sweepName: "PDFs",
                cancellationToken);
        }, cancellationToken);
    }

    /// <summary>
    /// Purges leaked/expired headless renderer sessions immediately and deletes stale user device sessions
    /// whose expiry or revocation exceeds <paramref name="revokedRetention"/> (default 30 days).
    /// </summary>
    public virtual int PurgeStaleDeviceSessions(TimeSpan? revokedRetention = null)
    {
        var sessionsRepo = _db.DeviceSessions;
        if (sessionsRepo == null) return 0;

        var now = DateTime.UtcNow;
        var retentionCutoff = now - (revokedRetention ?? TimeSpan.FromDays(30));
        var removedCount = 0;

        foreach (var session in sessionsRepo.GetAll().ToList())
        {
            var isLegacySystemRenderer = string.Equals(session.EmployeeId, RenderTokenMiddleware.SystemRendererId, StringComparison.OrdinalIgnoreCase);
            if (isLegacySystemRenderer)
            {
                sessionsRepo.Delete(session.Id);
                removedCount++;
                continue;
            }

            var isHeadlessRenderer = string.Equals(session.DeviceName, RenderTokenMiddleware.HeadlessRendererName, StringComparison.Ordinal);
            if (isHeadlessRenderer)
            {
                if (session.RevokedAt != null || session.ExpiresAt <= now)
                {
                    sessionsRepo.Delete(session.Id);
                    removedCount++;
                }
                continue;
            }

            var isExpiredPastRetention = session.ExpiresAt < retentionCutoff;

            if (session.RevokedAt == null && session.ExpiresAt < now && !isExpiredPastRetention)
            {
                session.RevokedAt = now;
                session.PushEndpoint = null;
                session.PushEnabled = false;
                session.PushP256dh = string.Empty;
                session.PushAuth = string.Empty;
                sessionsRepo.Save(session);
            }

            var isRevokedPastRetention = session.RevokedAt != null && session.RevokedAt < retentionCutoff;

            if (isRevokedPastRetention || isExpiredPastRetention)
            {
                sessionsRepo.Delete(session.Id);
                removedCount++;
            }
        }

        if (removedCount > 0)
        {
            _logger.LogInformation("[StorageMaintenance] Purged {Count} stale DeviceSessions.", removedCount);
        }

        return removedCount;
    }

    private OrphanCleanupResult SweepOrphanedDirectory(
        string rootDir,
        HashSet<string> activeKeys,
        TimeSpan effectiveSafetyWindow,
        Func<FileInfo, bool> isSkipped,
        Func<FileInfo, string> toLookupKey,
        string sweepName,
        CancellationToken cancellationToken)
    {
        var result = new OrphanCleanupResult { ActiveReferencesCount = activeKeys.Count };
        var cutoffTimeUtc = DateTime.UtcNow - effectiveSafetyWindow;

        _logger.LogInformation(
            "[StorageMaintenance] Starting {SweepName} orphan sweep with {Count} active references. Preserving files modified after {Cutoff:u}.",
            sweepName, activeKeys.Count, cutoffTimeUtc);

        FileInfo[] physicalFiles;
        try
        {
            physicalFiles = new DirectoryInfo(rootDir).GetFiles("*", SearchOption.AllDirectories);
        }
        catch (Exception ex)
        {
            result.Errors.Add($"Failed to enumerate files: {ex.Message}");
            _logger.LogError(ex, "[StorageMaintenance] Error enumerating files under {Path}", rootDir);
            return result;
        }

        result.FilesScanned = physicalFiles.Length;

        foreach (var file in physicalFiles)
        {
            if (cancellationToken.IsCancellationRequested) break;

            try
            {
                if (isSkipped(file)) continue;
                if (file.LastWriteTimeUtc >= cutoffTimeUtc) continue;

                var key = toLookupKey(file);
                if (!activeKeys.Contains(key))
                {
                    long fileSize = file.Length;
                    file.Delete();
                    result.OrphansDeleted++;
                    result.BytesReclaimed += fileSize;
                    _logger.LogDebug("[StorageMaintenance] Deleted {SweepName} orphan: {Key} ({Bytes} bytes)", sweepName, key, fileSize);
                }
            }
            catch (Exception ex)
            {
                result.Errors.Add($"{file.Name}: {ex.Message}");
                _logger.LogWarning(ex, "[StorageMaintenance] Failed to delete file {FullName}", file.FullName);
            }
        }

        PruneEmptyDirectories(rootDir);

        _logger.LogInformation(
            "[StorageMaintenance] {SweepName} orphan sweep finished: {Deleted} orphans deleted, {Bytes} reclaimed ({Errors} errors).",
            sweepName, result.OrphansDeleted, result.FormattedBytesReclaimed, result.Errors.Count);

        return result;
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

                // Register companion thumbnails ({fileName}_thumb.jpg, {fileName}_thumb_sm.jpg) as well as legacy stripped-extension variant
                activePaths.Add(FileService.GetThumbnailFileName(normalized));
                activePaths.Add(FileService.GetThumbnailFileName(normalized, small: true));
                var ext = Path.GetExtension(normalized);
                if (!string.IsNullOrEmpty(ext))
                {
                    var legacyThumb = normalized[..^ext.Length] + "_thumb.jpg";
                    activePaths.Add(legacyThumb);
                }
            }
        }

        void HarvestEmbeddedUrls(string? text)
        {
            if (string.IsNullOrWhiteSpace(text)) return;
            foreach (Match match in EmbeddedFileUrlRegex().Matches(text))
            {
                Register(match.Value);
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
        if (_db.ProjectNotes != null)
            foreach (var note in _db.ProjectNotes.GetAll()) Harvest(note);

        // Chat Messages & Attachments
        if (_db.ChatMessages != null)
            foreach (var msg in _db.ChatMessages.GetAll()) Harvest(msg);

        // Project Documents & Business Documents (including embedded /spokesapi/files/ URLs in Blocks & ResolvedBody)
        if (_db.ProjectDocuments != null)
        {
            foreach (var doc in _db.ProjectDocuments.GetAll())
            {
                Harvest(doc);
                HarvestEmbeddedUrls(doc.ResolvedBody);
                foreach (var block in doc.Blocks)
                {
                    foreach (var val in block.Properties.Values)
                    {
                        HarvestEmbeddedUrls(val);
                    }
                }
            }
        }

        // Standard Business Documents (including embedded /spokesapi/files/ URLs in Body & Blocks)
        if (_db.StandardDocuments != null)
        {
            foreach (var doc in _db.StandardDocuments.GetAll())
            {
                HarvestEmbeddedUrls(doc.Body);
                foreach (var block in doc.Blocks)
                {
                    foreach (var val in block.Properties.Values)
                    {
                        HarvestEmbeddedUrls(val);
                    }
                }
            }
        }

        // Project Reports
        if (_db.ProjectReports != null)
            foreach (var report in _db.ProjectReports.GetAll()) Harvest(report);

        // Projects & Client POs
        if (_db.Projects != null)
            foreach (var proj in _db.Projects.GetAll()) Harvest(proj);

        // Quotes, Invoices, Purchase Orders, Bills, Bank Statements
        if (_db.Quotes != null)
            foreach (var quote in _db.Quotes.GetAll()) Harvest(quote);

        if (_db.Invoices != null)
            foreach (var invoice in _db.Invoices.GetAll()) Harvest(invoice);

        if (_db.PurchaseOrders != null)
            foreach (var po in _db.PurchaseOrders.GetAll()) Harvest(po);

        if (_db.Bills != null)
            foreach (var bill in _db.Bills.GetAll()) Harvest(bill);

        if (_db.BankStatements != null)
            foreach (var statement in _db.BankStatements.GetAll()) Harvest(statement);

        // Recurring Expenses & Billing Records
        if (_db.RecurringExpenses != null)
            foreach (var rec in _db.RecurringExpenses.GetAll()) Harvest(rec);

        // Photo Albums & Media
        if (_db.Albums != null)
            foreach (var album in _db.Albums.GetAll()) Harvest(album);

        // Employees (Avatars & Signatures)
        if (_db.Employees != null)
            foreach (var emp in _db.Employees.GetAll()) Harvest(emp);

        // Expense Reports & Receipts
        if (_db.ExpenseReports != null)
            foreach (var report in _db.ExpenseReports.GetAll()) Harvest(report);

        // PayRuns (PayStub PDFs)
        if (_db.PayRuns != null)
            foreach (var run in _db.PayRuns.GetAll()) Harvest(run);

        // Email Messages (Bill inbox matched attachments) & Sending Identities (Signature images)
        if (_db.EmailMessages != null)
            foreach (var email in _db.EmailMessages.GetAll()) Register(email.MatchedAttachmentPath);

        if (_db.EmailSendingIdentities != null)
            foreach (var identity in _db.EmailSendingIdentities.GetAll()) HarvestEmbeddedUrls(identity.SignatureHtml);

        return activePaths;
    }

    private HashSet<string> CollectAllActivePdfRelativePaths()
    {
        var activePdfPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void RegisterPdf(string? pdfPath)
        {
            if (string.IsNullOrWhiteSpace(pdfPath)) return;
            var key = NormalizePdfRelativePath(pdfPath);
            if (!string.IsNullOrEmpty(key))
            {
                activePdfPaths.Add(key);
            }
        }

        if (_db.Quotes != null)
            foreach (var q in _db.Quotes.GetAll()) RegisterPdf(q.PdfPath);
        if (_db.Invoices != null)
            foreach (var i in _db.Invoices.GetAll()) RegisterPdf(i.PdfPath);
        if (_db.PurchaseOrders != null)
            foreach (var p in _db.PurchaseOrders.GetAll()) RegisterPdf(p.PdfPath);
        if (_db.ProjectDocuments != null)
            foreach (var d in _db.ProjectDocuments.GetAll()) RegisterPdf(d.PdfPath);
        if (_db.ProjectReports != null)
            foreach (var r in _db.ProjectReports.GetAll()) RegisterPdf(r.PdfPath);
        if (_db.PayRuns != null)
            foreach (var run in _db.PayRuns.GetAll())
                foreach (var stub in run.Stubs) RegisterPdf(stub.PdfPath);

        return activePdfPaths;
    }

    private static string NormalizePdfRelativePath(string path)
    {
        var unified = path.Trim().Replace('\\', '/');
        var marker = "/PDFs/";
        var idx = unified.LastIndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (idx >= 0)
        {
            unified = unified[(idx + marker.Length)..];
        }
        else if (unified.StartsWith("PDFs/", StringComparison.OrdinalIgnoreCase))
        {
            unified = unified["PDFs/".Length..];
        }

        return unified.Replace('/', Path.DirectorySeparatorChar).TrimStart(Path.DirectorySeparatorChar);
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
