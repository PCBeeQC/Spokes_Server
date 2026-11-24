using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Microsoft.Extensions.Logging;
using Spokes_Server.Aggregate.Contexts;
using Spokes_Server.Core.Constants;
using Spokes_Server.Core.Data.Repositories.Accounting;
using Spokes_Server.Core.Data.Repositories.HR;
using Spokes_Server.Core.Data.Repositories.Projects;
using Spokes_Server.Core.Helpers;
using Spokes_Server.Core.Models.Accounting;
using Spokes_Server.Core.Models.Core;
using Spokes_Server.Core.Models.HR;
using Spokes_Server.Core.Services.Projects;
using Microsoft.Extensions.DependencyInjection;

namespace Spokes_Server.Core.Services.HR;

public class HRService
{
    private readonly TimesheetRepository _timesheets;
    private readonly EmployeeRepository _employees;
    private readonly TeamRepository _teams;
    private readonly ILogger<HRService>? _logger;
    private readonly ProjectRepository? _projects;
    private readonly InvoiceRepository? _invoices;

    public HRService(
        TimesheetRepository timesheets,
        EmployeeRepository employees,
        TeamRepository teams,
        ILogger<HRService>? logger = null,
        ProjectRepository? projects = null,
        InvoiceRepository? invoices = null)
    {
        _timesheets = timesheets;
        _employees = employees;
        _teams = teams;
        _logger = logger;
        _projects = projects;
        _invoices = invoices;
    }

    [ActivatorUtilitiesConstructor]
    public HRService(
        HrContext hrContext,
        ILogger<HRService>? logger = null,
        ProjectRepository? projects = null,
        InvoiceRepository? invoices = null)
        : this(hrContext.Timesheets, hrContext.Employees, hrContext.Teams, logger, projects, invoices)
    {
    }

    public SpokesResult SubmitTimesheet(string timesheetId, TimesheetSettings? settings = null)
    {
        var timesheet = _timesheets.GetById(timesheetId);
        if (timesheet == null)
            return SpokesResult.Failure($"Timesheet '{timesheetId}' not found.", "NOT_FOUND");

        if (timesheet.Status is not (TimesheetStatus.Draft or TimesheetStatus.Rejected))
            return SpokesResult.Failure($"Cannot submit timesheet with status '{timesheet.Status}'. Only Draft or Rejected timesheets can be submitted.", "INVALID_STATUS");

        if (settings != null)
        {
            if (IsTimesheetLocked(timesheet, settings))
                return SpokesResult.Failure($"Timesheet is locked because it is older than the cutoff period ({settings.LockTimesheetsOlderThanDays} days).", "TIMESHEET_LOCKED");

            if (settings.RequireNote && timesheet.Entries.Any(e => e.Hours > 0 && string.IsNullOrWhiteSpace(e.Description)))
                return SpokesResult.Failure("All time entries require a description note before submitting.", "NOTE_REQUIRED");
        }

        timesheet.Status = TimesheetStatus.Submitted;
        timesheet.SubmittedAt = DateTime.UtcNow;
        timesheet.RejectionNote = null;
        _timesheets.Save(timesheet);
        return SpokesResult.Success();
    }

    private bool TryValidateTimesheetReview(
        string timesheetId,
        string reviewerEmployeeId,
        string verb,
        string pastParticiple,
        string roleLabel,
        out Timesheet? timesheet,
        out SpokesResult? failure)
    {
        timesheet = _timesheets.GetById(timesheetId);
        if (timesheet == null)
        {
            failure = SpokesResult.Failure($"Timesheet '{timesheetId}' not found.", "NOT_FOUND");
            return false;
        }

        if (timesheet.Status != TimesheetStatus.Submitted)
        {
            failure = SpokesResult.Failure($"Cannot {verb} timesheet with status '{timesheet.Status}'. Only Submitted timesheets can be {pastParticiple}.", "INVALID_STATUS");
            return false;
        }

        var reviewer = _employees.GetById(reviewerEmployeeId);
        if (reviewer == null)
        {
            failure = SpokesResult.Failure($"{roleLabel} '{reviewerEmployeeId}' not found.", "NOT_FOUND");
            return false;
        }

        if (reviewer is not { IsActiveUnsuspended: true, IsSystem: false })
        {
            failure = SpokesResult.Failure($"{roleLabel} account is inactive or suspended.", "FORBIDDEN");
            return false;
        }

        if (timesheet.EmployeeId == reviewerEmployeeId && !reviewer.IsEffectiveAdmin)
        {
            failure = SpokesResult.Failure($"Employees cannot {verb} their own timesheets.", "FORBIDDEN");
            return false;
        }

        if (!CanManageEmployee(reviewer, timesheet.EmployeeId))
        {
            failure = SpokesResult.Failure($"{roleLabel} does not have managerial authority over this employee.", "FORBIDDEN");
            return false;
        }

        failure = null;
        return true;
    }

    public SpokesResult ApproveTimesheet(string timesheetId, string approvedByEmployeeId)
    {
        if (!TryValidateTimesheetReview(timesheetId, approvedByEmployeeId, "approve", "approved", "Approver", out var timesheet, out var failure))
        {
            return failure!;
        }

        timesheet!.Status = TimesheetStatus.Approved;
        timesheet.ApprovedAt = DateTime.UtcNow;
        timesheet.ApprovedBy = approvedByEmployeeId;
        _timesheets.Save(timesheet);
        return SpokesResult.Success();
    }

    public SpokesResult RejectTimesheet(string timesheetId, string rejectedByEmployeeId, string rejectionNote)
    {
        if (!TryValidateTimesheetReview(timesheetId, rejectedByEmployeeId, "reject", "rejected", "Rejector", out var timesheet, out var failure))
        {
            return failure!;
        }

        if (string.IsNullOrWhiteSpace(rejectionNote))
        {
            return SpokesResult.Failure("A rejection note is required.", "NOTE_REQUIRED");
        }

        timesheet!.Status = TimesheetStatus.Rejected;
        timesheet.RejectionNote = rejectionNote;
        timesheet.ApprovedBy = rejectedByEmployeeId; // Track who rejected
        timesheet.ApprovedAt = null;
        _timesheets.Save(timesheet);
        return SpokesResult.Success();
    }


    /// <summary>
    /// Checks if a manager has permission to view or manage a target employee's timesheet.
    /// </summary>
    public bool CanManageEmployee(Employee manager, string targetEmployeeId)
    {
        if (manager is not { IsActive: true, IsBanned: false, IsSystem: false } || string.IsNullOrWhiteSpace(targetEmployeeId)) return false;
        if (manager.IsEffectiveAdmin) return true;
        if (manager.Id == targetEmployeeId) return true;
        return GetManagedEmployeeIds(manager, includeSelf: true).Contains(targetEmployeeId);
    }

    /// <summary>
    /// Checks if an acting user has authority to unlock/override locked or approved timesheets
    /// or perform bulk edits for the target employee. Admins can override any timesheet (including self);
    /// Team Leaders can override timesheets for their managed subordinates (excluding self).
    /// </summary>
    public bool CanOverrideTimesheetLock(Employee? actingUser, string targetEmployeeId)
    {
        if (actingUser is not { IsActiveUnsuspended: true, IsSystem: false } || string.IsNullOrWhiteSpace(targetEmployeeId)) return false;
        if (actingUser.IsEffectiveAdmin) return true;
        if (actingUser.Id == targetEmployeeId) return false;
        return CanManageEmployee(actingUser, targetEmployeeId);
    }

    /// <summary>
    /// Bulk updates selected time entries across one or more timesheets while preserving timesheet approval status
    /// and entry IDs.
    /// </summary>
    public SpokesResult<BulkTimesheetEditResult> BulkUpdateTimeEntries(BulkTimesheetEditRequest request, string actingEmployeeId)
    {
        if (request == null || request.EntryIds == null || request.EntryIds.Count == 0)
        {
            return SpokesResult<BulkTimesheetEditResult>.Failure("No time entries were selected for bulk editing.", "EMPTY_SELECTION");
        }

        if (!request.HasAnyFieldChange)
        {
            return SpokesResult<BulkTimesheetEditResult>.Failure("Select at least one field to update.", "NO_CHANGES_SPECIFIED");
        }

        if (request.UpdateProject && string.IsNullOrWhiteSpace(request.ProjectId))
        {
            return SpokesResult<BulkTimesheetEditResult>.Failure("A target project must be selected when updating projects.", "PROJECT_REQUIRED");
        }

        var actor = _employees.GetById(actingEmployeeId);
        if (actor == null)
        {
            return SpokesResult<BulkTimesheetEditResult>.Failure($"Acting employee '{actingEmployeeId}' not found.", "NOT_FOUND");
        }

        if (actor is not { IsActiveUnsuspended: true, IsSystem: false })
        {
            return SpokesResult<BulkTimesheetEditResult>.Failure("Acting employee account is inactive or suspended.", "FORBIDDEN");
        }

        var targetIds = new HashSet<string>(request.EntryIds.Where(id => !string.IsNullOrWhiteSpace(id)), StringComparer.Ordinal);
        int skippedCount = 0;
        if (request.ExcludedEntryIds is { Count: > 0 })
        {
            foreach (var excludedId in request.ExcludedEntryIds)
            {
                if (targetIds.Remove(excludedId))
                {
                    skippedCount++;
                }
            }
        }

        if (targetIds.Count == 0)
        {
            return SpokesResult<BulkTimesheetEditResult>.Failure("All selected entries were excluded from the update.", "ALL_EXCLUDED");
        }

        var allTimesheets = _timesheets.GetAll();
        var affectedTimesheets = allTimesheets
            .Where(ts => ts.Entries != null && ts.Entries.Any(e => targetIds.Contains(e.Id)))
            .ToList();

        if (affectedTimesheets.Count == 0)
        {
            return SpokesResult<BulkTimesheetEditResult>.Failure("None of the selected time entries could be found.", "NOT_FOUND");
        }

        foreach (var ts in affectedTimesheets)
        {
            if (!CanOverrideTimesheetLock(actor, ts.EmployeeId))
            {
                return SpokesResult<BulkTimesheetEditResult>.Failure(
                    "You do not have managerial authority to bulk edit one or more of the selected employee timesheets.",
                    "FORBIDDEN");
            }
        }

        if (_projects != null && !actor.IsEffectiveAdmin)
        {
            if (request.UpdateProject &&
                !string.IsNullOrWhiteSpace(request.ProjectId) &&
                !TimeEntryTypes.IsLeaveProjectKey(request.ProjectId))
            {
                var targetProject = _projects.GetById(request.ProjectId);
                if (targetProject == null || !ProjectAccessHelper.CanAccess(actor, targetProject, _teams, allowAccountingFinancialRead: false))
                {
                    return SpokesResult<BulkTimesheetEditResult>.Failure(
                        "You do not have access to the selected target project.",
                        "FORBIDDEN");
                }
            }

            foreach (var ts in affectedTimesheets)
            {
                foreach (var entry in ts.Entries)
                {
                    if (!targetIds.Contains(entry.Id)) continue;
                    if (string.IsNullOrWhiteSpace(entry.ProjectId) || entry.IsLeaveOrHoliday)
                    {
                        continue;
                    }

                    var sourceProject = _projects.GetById(entry.ProjectId);
                    if (sourceProject == null || !ProjectAccessHelper.CanAccess(actor, sourceProject, _teams, allowAccountingFinancialRead: false))
                    {
                        return SpokesResult<BulkTimesheetEditResult>.Failure(
                            "You do not have access to one or more restricted project time entries selected for bulk edit.",
                            "FORBIDDEN");
                    }
                }
            }
        }

        if (_invoices != null &&
            !actor.IsEffectiveAdmin &&
            !actor.HasEffectivePermission(AppPermissions.Finance.AccessAccounting) &&
            !actor.HasEffectivePermission(AppPermissions.Finance.ApproveInvoices))
        {
            var billedIds = _invoices.GetAll()
                .Where(i => InvoiceStatus.IsPosted(i.Status) && i.BilledTimeEntryIds != null)
                .SelectMany(i => i.BilledTimeEntryIds)
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .ToHashSet(StringComparer.Ordinal);

            if (targetIds.Any(billedIds.Contains))
            {
                return SpokesResult<BulkTimesheetEditResult>.Failure(
                    "Only accounting personnel or administrators may modify time entries already billed on an invoice.",
                    "FORBIDDEN");
            }
        }

        int updatedEntriesCount = 0;
        var modifiedSheets = new List<Timesheet>();

        foreach (var ts in affectedTimesheets)
        {
            bool sheetModified = false;
            foreach (var entry in ts.Entries)
            {
                if (!targetIds.Contains(entry.Id)) continue;

                if (request.UpdateProject)
                {
                    entry.ProjectId = request.ProjectId!;
                }

                if (request.UpdateTask)
                {
                    entry.WorkTypeId = request.WorkTypeId ?? string.Empty;
                    entry.SubTaskId = string.IsNullOrEmpty(request.SubTaskId) ? null : request.SubTaskId;
                }

                if (request.UpdateBillable)
                {
                    entry.IsBillable = request.IsBillable;
                }

                if (request.NoteMode == BulkNoteUpdateMode.Replace)
                {
                    entry.Description = request.NoteText?.Trim() ?? string.Empty;
                }
                else if (request.NoteMode == BulkNoteUpdateMode.Append && !string.IsNullOrWhiteSpace(request.NoteText))
                {
                    var appended = request.NoteText.Trim();
                    entry.Description = string.IsNullOrWhiteSpace(entry.Description)
                        ? appended
                        : $"{entry.Description}; {appended}";
                }

                updatedEntriesCount++;
                sheetModified = true;
            }

            if (sheetModified)
            {
                modifiedSheets.Add(ts);
            }
        }

        if (modifiedSheets.Count > 0)
        {
            _timesheets.SaveBatch(modifiedSheets);
        }

        return SpokesResult<BulkTimesheetEditResult>.Success(new BulkTimesheetEditResult
        {
            UpdatedEntriesCount = updatedEntriesCount,
            UpdatedTimesheetsCount = modifiedSheets.Count,
            SkippedExcludedCount = skippedCount
        });
    }

    /// <summary>
    /// Returns the set of employee IDs managed directly or indirectly by the given manager.
    /// </summary>
    public HashSet<string> GetManagedEmployeeIds(Employee manager, bool includeSelf = false)
    {
        var ids = new HashSet<string>();
        if (manager is not { IsActive: true, IsBanned: false, IsSystem: false })
        {
            return ids;
        }

        if (manager.IsEffectiveAdmin)
        {
            foreach (var emp in _employees.GetAll())
            {
                if (includeSelf || emp.Id != manager.Id)
                    ids.Add(emp.Id);
            }
            if (includeSelf && !string.IsNullOrEmpty(manager.Id))
                ids.Add(manager.Id);
            return ids;
        }

        var visitedTeamIds = new HashSet<string>();
        var teamQueue = new Queue<string>();

        foreach (var t in _teams.GetAll().Where(t => t.LeaderId == manager.Id))
        {
            if (visitedTeamIds.Add(t.Id))
                teamQueue.Enqueue(t.Id);
        }

        while (teamQueue.Count > 0)
        {
            var teamId = teamQueue.Dequeue();
            var members = _employees.GetAll().Where(e => e.TeamId == teamId);
            foreach (var member in members)
            {
                if (includeSelf || member.Id != manager.Id)
                    ids.Add(member.Id);

                // Include sub-teams led by members of this team
                foreach (var subTeam in _teams.GetAll().Where(t => t.LeaderId == member.Id))
                {
                    if (visitedTeamIds.Add(subTeam.Id))
                        teamQueue.Enqueue(subTeam.Id);
                }
            }
        }

        if (includeSelf && !string.IsNullOrEmpty(manager.Id))
            ids.Add(manager.Id);

        return ids;
    }

    /// <summary>
    /// Checks if a timesheet is locked due to date cutoff settings.
    /// </summary>
    public bool IsTimesheetLocked(Timesheet timesheet, TimesheetSettings settings)
    {
        if (!settings.EnableTimesheetLocking) return false;

        if (timesheet.Year < 1 || timesheet.Year > 9999)
        {
            _logger?.LogWarning("Timesheet '{TimesheetId}' has invalid Year {Year} for lock checking.", timesheet.Id, timesheet.Year);
            return false;
        }

        int maxWeeks = ISOWeek.GetWeeksInYear(timesheet.Year);
        if (timesheet.WeekNumber < 1 || timesheet.WeekNumber > maxWeeks)
        {
            _logger?.LogWarning("Timesheet '{TimesheetId}' has invalid WeekNumber {WeekNumber} for Year {Year} (max weeks: {MaxWeeks}).",
                timesheet.Id, timesheet.WeekNumber, timesheet.Year, maxWeeks);
            return false;
        }

        try
        {
            var weekMonday = ISOWeek.ToDateTime(timesheet.Year, timesheet.WeekNumber, DayOfWeek.Monday);
            var weekSunday = weekMonday.AddDays(6);
            var cutoffDate = DateTime.Today.AddDays(-settings.LockTimesheetsOlderThanDays);
            return weekSunday < cutoffDate;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Unexpected error checking timesheet lock status for Timesheet '{TimesheetId}'.", timesheet.Id);
            return false;
        }
    }

    /// <summary>
    /// Rounds hours based on settings. Applied to reports/exports only.
    /// </summary>
    public static decimal RoundHours(decimal hours, int intervalMinutes, string direction)
    {
        if (hours == 0) return 0;
        decimal intervalHours = intervalMinutes / 60m;
        return direction switch
        {
            "Up" => Math.Ceiling(hours / intervalHours) * intervalHours,
            "Down" => Math.Floor(hours / intervalHours) * intervalHours,
            "Nearest" => Math.Round(hours / intervalHours, MidpointRounding.AwayFromZero) * intervalHours,
            _ => hours
        };
    }
}
