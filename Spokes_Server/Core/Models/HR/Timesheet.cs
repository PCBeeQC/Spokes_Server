namespace Spokes_Server.Core.Models.HR;

using Spokes_Server.Core.Data;

public class Timesheet : IDataEntity
{
    public string Id { get; set; } = Guid.NewGuid().ToString();

    public override bool Equals(object? obj)
    {
        if (ReferenceEquals(this, obj)) return true;
        return obj is Timesheet other && Id == other.Id;
    }

    public override int GetHashCode() => Id?.GetHashCode() ?? base.GetHashCode();
    public string EmployeeId { get; set; } = string.Empty;
    public string EmployeeName { get; set; } = string.Empty; // Store name for easier reporting later

    public int Year { get; set; }
    public int WeekNumber { get; set; }

    // Status: Draft, Submitted, Approved, Rejected
    public string Status { get; set; } = TimesheetStatus.Draft;

    // Approval metadata
    public DateTime? SubmittedAt { get; set; }
    public DateTime? ApprovedAt { get; set; }
    public string? ApprovedBy { get; set; }      // Employee ID of the approver
    public string? RejectionNote { get; set; }    // Reason for rejection

    public List<TimeEntry> Entries { get; set; } = [];

    // Helper to get total hours
    public decimal TotalHours => Entries.Sum(e => e.Hours);
}

public static class TimeEntryTypes
{
    public const string ProjectWork = "ProjectWork";
    public const string Vacation = "Vacation";
    public const string StatutoryHoliday = "StatutoryHoliday";
    public const string BankedTimeOff = "BankedTimeOff";
    public const string SickOrPersonalLeave = "SickOrPersonalLeave";

    public const string LeaveProjectPrefix = "__LEAVE__:";
    public const string VacationRowKey = "__LEAVE__:Vacation";
    public const string StatutoryHolidayRowKey = "__LEAVE__:StatutoryHoliday";
    public const string BankedTimeOffRowKey = "__LEAVE__:BankedTimeOff";
    public const string SickOrPersonalLeaveRowKey = "__LEAVE__:SickOrPersonalLeave";

    public static readonly string[] AllLeaveRowKeys =
    [
        VacationRowKey,
        StatutoryHolidayRowKey,
        BankedTimeOffRowKey,
        SickOrPersonalLeaveRowKey
    ];

    public static bool IsLeaveEntryType(string? entryType) =>
        !string.IsNullOrWhiteSpace(entryType) &&
        !string.Equals(entryType, ProjectWork, StringComparison.OrdinalIgnoreCase);

    public static bool IsLeaveProjectKey(string? projectId) =>
        !string.IsNullOrWhiteSpace(projectId) &&
        projectId.StartsWith(LeaveProjectPrefix, StringComparison.OrdinalIgnoreCase);

    public static string ToProjectKey(string? entryType) =>
        IsLeaveEntryType(entryType) ? $"{LeaveProjectPrefix}{entryType}" : string.Empty;

    public static string ResolveEntryType(string? projectId, string? explicitEntryType = null)
    {
        if (!string.IsNullOrWhiteSpace(explicitEntryType) && IsLeaveEntryType(explicitEntryType))
        {
            return explicitEntryType;
        }
        if (IsLeaveProjectKey(projectId))
        {
            return projectId![LeaveProjectPrefix.Length..];
        }
        return ProjectWork;
    }

    /// <summary>
    /// True if this entry type counts toward fulfilling the employee's weekly expected schedule
    /// in <see cref="Services.HR.OvertimeService"/> (so the Hour Bank is not penalized on vacation or holidays).
    /// False for <see cref="BankedTimeOff"/> because redeeming banked hours intentionally reduces the Hour Bank balance.
    /// </summary>
    public static bool CountsTowardWeeklyExpectedTarget(string? entryType, string? projectId = null)
    {
        var resolved = ResolveEntryType(projectId, entryType);
        return !string.Equals(resolved, BankedTimeOff, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Resolves the calendar date for a <see cref="TimeEntry"/>, falling back to the ISO week Monday
    /// of its parent <see cref="Timesheet"/> or <see cref="DateTime.Today"/> when unset/invalid.
    /// </summary>
    public static DateTime ResolveEntryDate(Timesheet ts, TimeEntry entry)
    {
        if (entry.Date != default && entry.Date.Year > 1900)
            return entry.Date.Date;

        try
        {
            if (ts.Year > 1900 && ts.WeekNumber is >= 1 and <= 53)
                return System.Globalization.ISOWeek.ToDateTime(ts.Year, ts.WeekNumber, DayOfWeek.Monday).Date;
        }
        catch
        {
            // Ignore invalid ISO week metadata and fall back to today
        }

        return DateTime.Today;
    }

    public static string GetDisplayLabel(string? entryTypeOrKey)
    {
        var type = IsLeaveProjectKey(entryTypeOrKey)
            ? entryTypeOrKey![LeaveProjectPrefix.Length..]
            : (entryTypeOrKey ?? ProjectWork);
        return type switch
        {
            Vacation => "Vacation / Paid Time Off",
            StatutoryHoliday => "Statutory / Public Holiday",
            BankedTimeOff => "Banked Time Off (Hour Bank)",
            SickOrPersonalLeave => "Sick / Personal Leave",
            _ => "Project Work"
        };
    }
}

public class TimeEntry
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string ProjectId { get; set; } = string.Empty;

    public string WorkTypeId { get; set; } = string.Empty;
    public string? SubTaskId { get; set; }

    /// <summary>
    /// Categorizes whether this entry is standard <see cref="TimeEntryTypes.ProjectWork"/>
    /// or non-project time off (<see cref="TimeEntryTypes.Vacation"/>, <see cref="TimeEntryTypes.StatutoryHoliday"/>,
    /// <see cref="TimeEntryTypes.BankedTimeOff"/>, <see cref="TimeEntryTypes.SickOrPersonalLeave"/>).
    /// </summary>
    public string EntryType { get; set; } = TimeEntryTypes.ProjectWork;

    // We store the specific date to avoid "Year change" edge case bugs
    public DateTime Date { get; set; }

    public decimal Hours { get; set; }
    public string Description { get; set; } = string.Empty;
    public bool IsBillable { get; set; } = true;

    public bool IsLeaveOrHoliday =>
        TimeEntryTypes.IsLeaveEntryType(EntryType) || TimeEntryTypes.IsLeaveProjectKey(ProjectId);
}

/// <summary>
/// Authoritative domain lifecycle statuses for Timesheets.
/// </summary>
public static class TimesheetStatus
{
    public const string Draft = "Draft";
    public const string Submitted = "Submitted";
    public const string Approved = "Approved";
    public const string Rejected = "Rejected";
}

public enum BulkNoteUpdateMode
{
    KeepExisting,
    Replace,
    Append
}

public class BulkTimesheetEditRequest
{
    public IReadOnlyCollection<string> EntryIds { get; set; } = [];
    public IReadOnlySet<string>? ExcludedEntryIds { get; set; }

    public bool UpdateProject { get; set; }
    public string? ProjectId { get; set; }

    public bool UpdateTask { get; set; }
    public string? WorkTypeId { get; set; }
    public string? SubTaskId { get; set; }

    public bool UpdateBillable { get; set; }
    public bool IsBillable { get; set; } = true;

    public BulkNoteUpdateMode NoteMode { get; set; } = BulkNoteUpdateMode.KeepExisting;
    public string? NoteText { get; set; }

    public bool HasAnyFieldChange =>
        UpdateProject ||
        UpdateTask ||
        UpdateBillable ||
        NoteMode != BulkNoteUpdateMode.KeepExisting;
}

public class BulkTimesheetEditResult
{
    public int UpdatedEntriesCount { get; set; }
    public int UpdatedTimesheetsCount { get; set; }
    public int SkippedExcludedCount { get; set; }
}




