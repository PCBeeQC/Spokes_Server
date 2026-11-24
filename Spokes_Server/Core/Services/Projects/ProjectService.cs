using System;
using System.IO;
using System.Linq;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Spokes_Server.Aggregate;
using Spokes_Server.Core.Data;
using Spokes_Server.Core.Helpers;
using Spokes_Server.Core.Models.Accounting;
using Spokes_Server.Core.Models.Common;
using Spokes_Server.Core.Services.Communication.Chat;
using Spokes_Server.Core.Services.Communication.Notifications.Routing;
using Spokes_Server.Core.Services.Security;
using Spokes_Server.Core.Services.Storage;

namespace Spokes_Server.Core.Services.Projects;

public interface IProjectService
{
    SpokesResult DeleteProject(string projectId);
}

/// <summary>
/// Manages project lifecycle operations including dependency-checked cascading deletion
/// so deleting a project never leaves orphaned sub-folders, quotes, notes, documents, or broken accounting links.
/// </summary>
public class ProjectService : IProjectService
{
    private readonly Database _db;
    private readonly DiskPersistenceService _persistence;
    private readonly string _dataPath;
    private readonly ILogger<ProjectService>? _logger;
    private readonly IFileService? _fileService;
    private readonly FileTokenService? _fileTokenService;
    private readonly AlbumService? _albumService;
    private readonly NotificationQueueService? _notificationQueue;

    public ProjectService(
        Database db,
        DiskPersistenceService persistence,
        IConfiguration config,
        ILogger<ProjectService>? logger = null,
        IFileService? fileService = null,
        FileTokenService? fileTokenService = null,
        AlbumService? albumService = null,
        NotificationQueueService? notificationQueue = null)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _persistence = persistence ?? throw new ArgumentNullException(nameof(persistence));
        _dataPath = config["DataPath"] ?? "Data";
        _logger = logger;
        _fileService = fileService;
        _fileTokenService = fileTokenService;
        _albumService = albumService;
        _notificationQueue = notificationQueue;
    }

    public SpokesResult DeleteProject(string projectId)
    {
        if (string.IsNullOrWhiteSpace(projectId))
        {
            return SpokesResult.Failure("Project ID is required.", "INVALID_INPUT");
        }

        var project = _db.Projects.GetById(projectId);
        if (project == null)
        {
            return SpokesResult.Failure("Project not found.", "NOT_FOUND");
        }

        // 1. Block deletion if posted financial or HR records depend on this project
        var postedInvoicesCount = _db.Invoices?.GetAll()
            .Count(i => i.ProjectId == projectId &&
                        InvoiceStatus.IsPosted(i.Status)) ?? 0;

        if (postedInvoicesCount > 0)
        {
            return SpokesResult.Failure(
                $"Cannot delete project because {postedInvoicesCount} posted invoice(s) reference it. Archive the project instead.",
                "HAS_POSTED_INVOICES");
        }

        var activePosCount = _db.PurchaseOrders?.GetAll()
            .Count(po => !string.Equals(po.Status, PurchaseOrderStatus.Draft, StringComparison.OrdinalIgnoreCase) &&
                         !string.Equals(po.Status, PurchaseOrderStatus.Void, StringComparison.OrdinalIgnoreCase) &&
                         po.LineItems.Any(li => li.ProjectId == projectId)) ?? 0;

        if (activePosCount > 0)
        {
            return SpokesResult.Failure(
                $"Cannot delete project because {activePosCount} active purchase order(s) reference it. Archive the project instead.",
                "HAS_ACTIVE_POS");
        }

        var timesheetCount = _db.Timesheets?.GetAll().Sum(t => t.Entries.Count(e => e.ProjectId == projectId)) ?? 0;
        if (timesheetCount > 0)
        {
            return SpokesResult.Failure(
                $"Cannot delete project because {timesheetCount} timesheet entry/entries are logged to it. Archive the project instead.",
                "HAS_TIMESHEETS");
        }

        var postedExpensesCount = _db.ExpenseReports?.GetAll()
            .Count(r => !string.Equals(r.Status, ExpenseReportStatus.Draft, StringComparison.OrdinalIgnoreCase) &&
                        r.LineItems.Any(item => item.ProjectId == projectId)) ?? 0;

        if (postedExpensesCount > 0)
        {
            return SpokesResult.Failure(
                $"Cannot delete project because {postedExpensesCount} submitted expense report(s) reference it. Archive the project instead.",
                "HAS_EXPENSES");
        }

        // 2. Cascade-delete draft/void invoices and draft/void PO line references
        if (_db.Invoices != null)
        {
            foreach (var inv in _db.Invoices.GetAll().Where(i => i.ProjectId == projectId).ToList())
            {
                _db.Invoices.Delete(inv.Id);
            }
        }

        if (_db.PurchaseOrders != null)
        {
            foreach (var po in _db.PurchaseOrders.GetAll().Where(p => p.LineItems.Any(li => li.ProjectId == projectId)).ToList())
            {
                po.LineItems.RemoveAll(li => li.ProjectId == projectId);
                if (po.LineItems.Count == 0)
                {
                    _db.PurchaseOrders.Delete(po.Id);
                }
                else
                {
                    _db.PurchaseOrders.Save(po);
                }
            }
        }

        // 3. Cascade-delete owned project sub-entities (triggering RepositoryFileCascade for each)
        if (_db.Quotes != null)
        {
            foreach (var quote in _db.Quotes.GetByProject(projectId).ToList())
            {
                _db.Quotes.Delete(quote.Id);
            }
        }

        if (_db.ProjectNotes != null)
        {
            foreach (var note in _db.ProjectNotes.GetByProject(projectId).ToList())
            {
                _db.ProjectNotes.Delete(note.Id);
            }
        }

        if (_db.ProjectDocuments != null)
        {
            foreach (var doc in _db.ProjectDocuments.GetByProject(projectId).ToList())
            {
                _db.ProjectDocuments.Delete(doc.Id);
            }
        }

        if (_db.ProjectReports != null)
        {
            foreach (var report in _db.ProjectReports.GetByProject(projectId).ToList())
            {
                _db.ProjectReports.Delete(report.Id);
            }
        }

        // 4. Clean up calendar, weekly plans & chat links
        if (_db.CalendarEvents != null)
        {
            foreach (var ev in _db.CalendarEvents.GetAll().Where(e => e.ProjectId == projectId).ToList())
            {
                ev.ProjectId = string.Empty;
                _db.CalendarEvents.Save(ev);
            }
        }

        if (_db.WeeklyPlans != null)
        {
            foreach (var plan in _db.WeeklyPlans.GetAll().Where(p => p.Entries.Any(e => e.ProjectId == projectId)).ToList())
            {
                plan.Entries.RemoveAll(e => e.ProjectId == projectId);
                _db.WeeklyPlans.Save(plan);
            }
        }

        var projectChannel = _db.ChatChannels?.GetProjectChannel(projectId);
        if (projectChannel != null)
        {
            ChatService.PurgeChannelCascade(
                projectChannel.Id,
                _db.ChatChannels,
                _db.ChatMessages,
                _db.ChatReadStates,
                _db.ChatPolls,
                _db.ReportedMessages,
                _albumService,
                _db.Albums,
                _fileTokenService,
                _notificationQueue,
                deleteAttachmentFile: (filePath, _) =>
                    StoredFileCleanupHelper.DeleteFileWithThumbnail(_dataPath, _fileService, filePath, _logger));
        }

        // 5. Delete the project root entity & queue recursive removal of Projects/{projectId} folder
        _db.Projects.Delete(projectId);

        var projectDir = Path.Combine(_dataPath, "Projects", projectId);
        _persistence.QueueDeleteDirectory(projectDir);

        _logger?.LogInformation("Deleted project {ProjectId} ('{Name}') with cascading sub-entity cleanup.", projectId, project.Name);
        return SpokesResult.Success();
    }
}
