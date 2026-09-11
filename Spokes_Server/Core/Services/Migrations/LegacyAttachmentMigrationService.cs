using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Spokes_Server.Aggregate;
using Spokes_Server.Core.Services.Core;

namespace Spokes_Server.Core.Services.Migrations;

public class LegacyAttachmentMigrationService
{
    private readonly Database _db;
    private readonly IFileService _fileService;
    private readonly ILogger<LegacyAttachmentMigrationService> _logger;

    public LegacyAttachmentMigrationService(Database db, IFileService fileService, ILogger<LegacyAttachmentMigrationService> logger)
    {
        _db = db;
        _fileService = fileService;
        _logger = logger;
    }

    public async Task MigrateLegacyBase64AttachmentsAsync()
    {
        _logger.LogInformation("Starting migration of legacy Base64 attachments to physical files.");

        int migratedProjectNotes = await MigrateProjectNotesAsync();
        int migratedBills = await MigrateBillsAsync();

        _logger.LogInformation($"Migration complete. Migrated {migratedProjectNotes} Project Notes attachments and {migratedBills} Bill attachments.");
    }

    private async Task<int> MigrateProjectNotesAsync()
    {
        int migratedCount = 0;
        var notes = _db.ProjectNotes.GetAll().ToList();
        
        foreach (var note in notes)
        {
            if (note.Attachments == null || !note.Attachments.Any()) continue;
            
            bool modified = false;

            foreach (var att in note.Attachments)
            {
                if (IsLegacyBase64(att.FilePath))
                {
                    try
                    {
                        string newUrl = await MigrateBase64StringAsync("projects", note.ProjectId, att.FileName, att.FilePath);
                        if (!string.IsNullOrEmpty(newUrl))
                        {
                            att.FilePath = newUrl;
                            modified = true;
                            migratedCount++;
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, $"Failed to migrate attachment {att.FileName} in ProjectNote {note.Id}");
                    }
                }
            }

            if (modified)
            {
                _db.ProjectNotes.Save(note);
            }
        }

        return migratedCount;
    }

    private async Task<int> MigrateBillsAsync()
    {
        int migratedCount = 0;
        var bills = _db.Bills.GetAll().ToList();

        foreach (var bill in bills)
        {
            if (IsLegacyBase64(bill.AttachmentPath))
            {
                try
                {
                    string fileName = string.IsNullOrEmpty(bill.AttachmentName) ? $"bill_invoice_{bill.Id}.pdf" : bill.AttachmentName;
                    // contextId for bill can be its Id or PurchaseOrderId, we'll use Id to be safe and avoid missing dirs
                    string newUrl = await MigrateBase64StringAsync("bills", bill.Id, fileName, bill.AttachmentPath);
                    if (!string.IsNullOrEmpty(newUrl))
                    {
                        bill.AttachmentPath = newUrl;
                        _db.Bills.Save(bill);
                        migratedCount++;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, $"Failed to migrate attachment in Bill {bill.Id}");
                }
            }
        }

        return migratedCount;
    }

    private bool IsLegacyBase64(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        if (path.StartsWith("/spokesapi/")) return false;
        
        // typical data uri: data:image/png;base64,iVBORw0KGgo...
        return path.StartsWith("data:") && path.Contains(";base64,");
    }

    private async Task<string> MigrateBase64StringAsync(string category, string contextId, string fallbackFileName, string base64DataUri)
    {
        // Extract base64 part
        var parts = base64DataUri.Split(";base64,");
        if (parts.Length != 2) return null;

        var meta = parts[0]; // e.g., "data:image/png"
        var base64String = parts[1];

        // determine extension if missing
        string extension = ".bin";
        if (meta.Contains("image/png")) extension = ".png";
        else if (meta.Contains("image/jpeg")) extension = ".jpg";
        else if (meta.Contains("application/pdf")) extension = ".pdf";
        else if (meta.Contains("text/plain")) extension = ".txt";

        if (string.IsNullOrWhiteSpace(fallbackFileName))
        {
            fallbackFileName = $"migrated_file{extension}";
        }
        else if (!Path.HasExtension(fallbackFileName))
        {
            fallbackFileName += extension;
        }

        byte[] bytes = Convert.FromBase64String(base64String);

        using var ms = new MemoryStream(bytes);
        return await _fileService.UploadStreamAsync(category, contextId, ms, fallbackFileName);
    }
}
