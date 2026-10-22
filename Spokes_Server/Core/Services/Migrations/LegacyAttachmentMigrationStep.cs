using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Spokes_Server.Aggregate.Contexts;
using Spokes_Server.Core.Helpers;
using Spokes_Server.Core.Services.Storage;

namespace Spokes_Server.Core.Services.Migrations;

/// <summary>
/// Migrates legacy Base64 attachments in ProjectNotes and Bills to physical disk files.
/// Tracked by MigrationRunnerService and recorded in MigrationHistory.json so it executes exactly once.
/// Self-contained IMigrationStep implementation.
/// </summary>
public class LegacyAttachmentMigrationStep : IMigrationStep
{
    private readonly ProjectsContext _projects;
    private readonly AccountingContext _accounting;
    private readonly IFileService _fileService;
    private readonly ILogger? _logger;

    public string MigrationKey => "20260105_LegacyBase64AttachmentMigration";
    public string Description => "Migrates legacy Base64 attachments in ProjectNotes and Bills to physical files";
    public int Order => 50;

    [ActivatorUtilitiesConstructor]
    public LegacyAttachmentMigrationStep(
        ProjectsContext projects,
        AccountingContext accounting,
        IFileService fileService,
        ILogger<LegacyAttachmentMigrationStep>? logger = null)
    {
        _projects = projects;
        _accounting = accounting;
        _fileService = fileService;
        _logger = logger;
    }

    public async Task ExecuteAsync(CancellationToken cancellationToken = default)
    {
        _logger?.LogInformation("[LegacyAttachmentMigrationStep] Executing legacy attachment migration...");
        await MigrateLegacyBase64AttachmentsAsync();
    }

    public virtual async Task MigrateLegacyBase64AttachmentsAsync()
    {
        _logger?.LogInformation("Starting migration of legacy Base64 attachments to physical files.");

        int migratedProjectNotes = await MigrateProjectNotesAsync();
        int migratedBills = await MigrateBillsAsync();

        _logger?.LogInformation($"Migration complete. Migrated {migratedProjectNotes} Project Notes attachments and {migratedBills} Bill attachments.");
    }

    private async Task<int> MigrateProjectNotesAsync()
    {
        int migratedCount = 0;
        var notes = _projects.ProjectNotes.GetAll().ToList();
        
        foreach (var note in notes)
        {
            if (note.Attachments == null || note.Attachments.Count == 0) continue;
            
            bool modified = false;

            foreach (var att in note.Attachments)
            {
                if (IsLegacyBase64(att.FilePath))
                {
                    try
                    {
                        string? newUrl = await MigrateBase64StringAsync("projects", note.ProjectId, att.FileName, att.FilePath);
                        if (!string.IsNullOrEmpty(newUrl))
                        {
                            att.FilePath = newUrl;
                            modified = true;
                            migratedCount++;
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger?.LogError(ex, $"Failed to migrate attachment {att.FileName} in ProjectNote {note.Id}");
                    }
                }
            }

            if (modified)
            {
                _projects.ProjectNotes.Save(note);
            }
        }

        return migratedCount;
    }

    private async Task<int> MigrateBillsAsync()
    {
        int migratedCount = 0;
        var bills = _accounting.Bills.GetAll().ToList();

        foreach (var bill in bills)
        {
            if (IsLegacyBase64(bill.AttachmentPath))
            {
                try
                {
                    string fileName = string.IsNullOrEmpty(bill.AttachmentName) ? $"bill_invoice_{bill.Id}.pdf" : bill.AttachmentName;
                    string? newUrl = await MigrateBase64StringAsync("bills", bill.Id, fileName, bill.AttachmentPath);
                    if (!string.IsNullOrEmpty(newUrl))
                    {
                        bill.AttachmentPath = newUrl;
                        _accounting.Bills.Save(bill);
                        migratedCount++;
                    }
                }
                catch (Exception ex)
                {
                    _logger?.LogError(ex, $"Failed to migrate attachment in Bill {bill.Id}");
                }
            }
        }

        return migratedCount;
    }

    private static bool IsLegacyBase64(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        if (path.StartsWith("/spokesapi/")) return false;
        
        return path.StartsWith("data:") && path.Contains(";base64,");
    }

    private async Task<string?> MigrateBase64StringAsync(string category, string contextId, string fallbackFileName, string base64DataUri)
    {
        if (!DataUriHelper.TryParse(base64DataUri, out var bytes, out var mimeType))
        {
            throw new FormatException($"Invalid or malformed base64 data URI in attachment: {fallbackFileName}");
        }

        string extension = DataUriHelper.GetExtensionForMimeType(mimeType);

        if (string.IsNullOrWhiteSpace(fallbackFileName))
        {
            fallbackFileName = $"migrated_file{extension}";
        }
        else if (!Path.HasExtension(fallbackFileName))
        {
            fallbackFileName += extension;
        }

        using var ms = new MemoryStream(bytes);
        return await _fileService.UploadStreamAsync(category, contextId, ms, fallbackFileName);
    }
}
