using Spokes_Server.Aggregate;
using Spokes_Server.Core.Models.HR;
using Spokes_Server.Core.Models.Core;
using System.Globalization;

namespace Spokes_Server.Core.Services.HR;

public class HRService
{
    private readonly Database _db;
    private readonly OvertimeService _overtimeService;

    public HRService(Database db, OvertimeService overtimeService)
    {
        _db = db;
        _overtimeService = overtimeService;
    }

    public HourBankResult CalculateHourBank(string employeeId)
    {
        return _overtimeService.CalculateHourBank(employeeId);
    }

    public WeeklyOvertimeResult CalculateWeeklyOvertime(string employeeId, int year, int weekNumber)
    {
        return _overtimeService.CalculateWeeklyOvertime(employeeId, year, weekNumber);
    }

    public bool SubmitTimesheet(string timesheetId, TimesheetSettings? settings = null)
    {
        var timesheet = _db.Timesheets.GetById(timesheetId);
        if (timesheet == null || (timesheet.Status != "Draft" && timesheet.Status != "Rejected"))
            return false;

        if (settings != null)
        {
            if (IsTimesheetLocked(timesheet, settings))
                return false;

            if (settings.RequireNote && timesheet.Entries.Any(e => e.Hours > 0 && string.IsNullOrWhiteSpace(e.Description)))
                return false;
        }

        timesheet.Status = "Submitted";
        timesheet.SubmittedAt = DateTime.UtcNow;
        timesheet.RejectionNote = null;
        _db.Timesheets.Save(timesheet);
        return true;
    }

    public void ApproveTimesheet(string timesheetId, string approvedByEmployeeId)
    {
        var timesheet = _db.Timesheets.GetById(timesheetId);
        if (timesheet != null && timesheet.Status == "Submitted")
        {
            // Disallow self-approval unless approver is an Admin
            if (timesheet.EmployeeId == approvedByEmployeeId)
            {
                var approver = _db.Employees.GetById(approvedByEmployeeId);
                if (approver == null || !approver.IsAdmin)
                    throw new InvalidOperationException("Employees cannot approve their own timesheets.");
            }

            timesheet.Status = "Approved";
            timesheet.ApprovedAt = DateTime.UtcNow;
            timesheet.ApprovedBy = approvedByEmployeeId;
            _db.Timesheets.Save(timesheet);
        }
    }

    public void RejectTimesheet(string timesheetId, string rejectionNote, string rejectedByEmployeeId)
    {
        var timesheet = _db.Timesheets.GetById(timesheetId);
        if (timesheet != null && timesheet.Status == "Submitted")
        {
            timesheet.Status = "Rejected";
            timesheet.RejectionNote = rejectionNote;
            timesheet.ApprovedBy = rejectedByEmployeeId; // Track who rejected
            timesheet.ApprovedAt = null;
            _db.Timesheets.Save(timesheet);
        }
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
            foreach (var emp in _db.Employees.GetAll())
            {
                if (includeSelf || emp.Id != manager.Id)
                    ids.Add(emp.Id);
            }
            return ids;
        }

        var visitedTeamIds = new HashSet<string>();
        var teamQueue = new Queue<string>();

        foreach (var t in _db.Teams.GetAll().Where(t => t.LeaderId == manager.Id))
        {
            if (visitedTeamIds.Add(t.Id))
                teamQueue.Enqueue(t.Id);
        }

        while (teamQueue.Count > 0)
        {
            var teamId = teamQueue.Dequeue();
            var members = _db.Employees.GetAll().Where(e => e.TeamId == teamId);
            foreach (var member in members)
            {
                if (includeSelf || member.Id != manager.Id)
                    ids.Add(member.Id);

                // Include sub-teams led by members of this team
                foreach (var subTeam in _db.Teams.GetAll().Where(t => t.LeaderId == member.Id))
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

        try
        {
            var weekMonday = System.Globalization.ISOWeek.ToDateTime(timesheet.Year, timesheet.WeekNumber, DayOfWeek.Monday);
            var weekSunday = weekMonday.AddDays(6);
            var cutoffDate = DateTime.Today.AddDays(-settings.LockTimesheetsOlderThanDays);
            return weekSunday < cutoffDate;
        }
        catch
        {
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
