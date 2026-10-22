using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Microsoft.Extensions.Logging;
using Spokes_Server.Aggregate.Contexts;
using Spokes_Server.Core.Data.Repositories.HR;
using Spokes_Server.Core.Data.Repositories.Projects;
using Spokes_Server.Core.Helpers;
using Spokes_Server.Core.Models.Core;
using Spokes_Server.Core.Models.HR;
using Microsoft.Extensions.DependencyInjection;

namespace Spokes_Server.Core.Services.HR;

public class HRService
{
    private readonly TimesheetRepository _timesheets;
    private readonly EmployeeRepository _employees;
    private readonly TeamRepository _teams;
    private readonly ILogger<HRService>? _logger;

    public HRService(
        TimesheetRepository timesheets,
        EmployeeRepository employees,
        TeamRepository teams,
        ILogger<HRService>? logger = null)
    {
        _timesheets = timesheets;
        _employees = employees;
        _teams = teams;
        _logger = logger;
    }

    [ActivatorUtilitiesConstructor]
    public HRService(HrContext hrContext, ILogger<HRService>? logger = null)
        : this(hrContext.Timesheets, hrContext.Employees, hrContext.Teams, logger)
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

    public SpokesResult ApproveTimesheet(string timesheetId, string approvedByEmployeeId)
    {
        var timesheet = _timesheets.GetById(timesheetId);
        if (timesheet == null)
            return SpokesResult.Failure($"Timesheet '{timesheetId}' not found.", "NOT_FOUND");

        if (timesheet.Status != TimesheetStatus.Submitted)
            return SpokesResult.Failure($"Cannot approve timesheet with status '{timesheet.Status}'. Only Submitted timesheets can be approved.", "INVALID_STATUS");

        var approver = _employees.GetById(approvedByEmployeeId);
        if (approver == null)
            return SpokesResult.Failure($"Approver '{approvedByEmployeeId}' not found.", "NOT_FOUND");

        // Disallow self-approval unless approver is an Admin
        if (timesheet.EmployeeId == approvedByEmployeeId && !approver.IsAdmin)
        {
            return SpokesResult.Failure("Employees cannot approve their own timesheets.", "FORBIDDEN");
        }

        // Verify managerial authority over the target employee
        if (!CanManageEmployee(approver, timesheet.EmployeeId))
        {
            return SpokesResult.Failure("Approver does not have managerial authority over this employee.", "FORBIDDEN");
        }

        timesheet.Status = TimesheetStatus.Approved;
        timesheet.ApprovedAt = DateTime.UtcNow;
        timesheet.ApprovedBy = approvedByEmployeeId;
        _timesheets.Save(timesheet);
        return SpokesResult.Success();
    }

    public SpokesResult RejectTimesheet(string timesheetId, string rejectedByEmployeeId, string rejectionNote)
    {
        var timesheet = _timesheets.GetById(timesheetId);
        if (timesheet == null)
            return SpokesResult.Failure($"Timesheet '{timesheetId}' not found.", "NOT_FOUND");

        if (timesheet.Status != TimesheetStatus.Submitted)
            return SpokesResult.Failure($"Cannot reject timesheet with status '{timesheet.Status}'. Only Submitted timesheets can be rejected.", "INVALID_STATUS");

        var rejector = _employees.GetById(rejectedByEmployeeId);
        if (rejector == null)
            return SpokesResult.Failure($"Rejector '{rejectedByEmployeeId}' not found.", "NOT_FOUND");

        // Disallow self-rejection unless approver is an Admin
        if (timesheet.EmployeeId == rejectedByEmployeeId && !rejector.IsAdmin)
        {
            return SpokesResult.Failure("Employees cannot reject their own timesheets.", "FORBIDDEN");
        }

        // Verify managerial authority over the target employee
        if (!CanManageEmployee(rejector, timesheet.EmployeeId))
        {
            return SpokesResult.Failure("Rejector does not have managerial authority over this employee.", "FORBIDDEN");
        }

        if (string.IsNullOrWhiteSpace(rejectionNote))
        {
            return SpokesResult.Failure("A rejection note is required.", "NOTE_REQUIRED");
        }

        timesheet.Status = TimesheetStatus.Rejected;
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
        if (manager.IsAdmin) return true;
        if (manager.Id == targetEmployeeId) return true;
        return GetManagedEmployeeIds(manager, includeSelf: true).Contains(targetEmployeeId);
    }

    /// <summary>
    /// Returns the set of employee IDs managed directly or indirectly by the given manager.
    /// </summary>
    public HashSet<string> GetManagedEmployeeIds(Employee manager, bool includeSelf = false)
    {
        var ids = new HashSet<string>();
        if (manager.IsAdmin)
        {
            foreach (var emp in _employees.GetAll())
            {
                if (includeSelf || emp.Id != manager.Id)
                    ids.Add(emp.Id);
            }
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
