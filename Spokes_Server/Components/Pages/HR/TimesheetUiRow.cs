namespace Spokes_Server.Components.Pages.HR;

using Spokes_Server.Core.Models.HR;

public class TimesheetUiRow
{
    public string ProjectId { get; set; } = "";
    public string WorkTypeId { get; set; } = "";
    public string SubTaskId { get; set; } = "";
    public string EntryType { get; set; } = TimeEntryTypes.ProjectWork;
    public bool IsBillable { get; set; } = true;
    public decimal[] DailyHours { get; set; } = new decimal[7]; // Index 0 = Mon, 6 = Sun
    public string[] DailyNotes { get; set; } = new string[7];   // Notes per day, matching DailyHours indices
    public string?[] EntryIds { get; set; } = new string?[7];   // Original TimeEntry.Id per day to preserve billing links

    public bool IsLeaveRow =>
        TimeEntryTypes.IsLeaveEntryType(EntryType) || TimeEntryTypes.IsLeaveProjectKey(ProjectId);

    /// <summary>
    /// Converts a persisted <see cref="Timesheet"/> into weekly UI matrix rows.
    /// </summary>
    public static List<TimesheetUiRow> FromTimesheet(Timesheet sheet, DateTime weekStart)
    {
        var rows = new List<TimesheetUiRow>();
        if (sheet?.Entries == null || sheet.Entries.Count == 0)
        {
            return rows;
        }

        var normalizedWeekStart = weekStart.Date;
        var grouped = sheet.Entries
            .GroupBy(e =>
            {
                var resolvedType = TimeEntryTypes.ResolveEntryType(e.ProjectId, e.EntryType);
                var effectiveProjId = TimeEntryTypes.IsLeaveEntryType(resolvedType)
                    ? TimeEntryTypes.ToProjectKey(resolvedType)
                    : (e.ProjectId ?? string.Empty);
                return new
                {
                    ProjectId = effectiveProjId,
                    WorkTypeId = TimeEntryTypes.IsLeaveEntryType(resolvedType) ? string.Empty : (e.WorkTypeId ?? string.Empty),
                    SubTaskId = TimeEntryTypes.IsLeaveEntryType(resolvedType) ? string.Empty : (e.SubTaskId ?? string.Empty),
                    EntryType = resolvedType,
                    IsBillable = !TimeEntryTypes.IsLeaveEntryType(resolvedType) && e.IsBillable
                };
            });

        foreach (var group in grouped)
        {
            var row = new TimesheetUiRow
            {
                ProjectId = group.Key.ProjectId,
                WorkTypeId = group.Key.WorkTypeId,
                SubTaskId = group.Key.SubTaskId,
                EntryType = group.Key.EntryType,
                IsBillable = group.Key.IsBillable
            };

            foreach (var entry in group)
            {
                var dayIndex = (entry.Date.Date - normalizedWeekStart).Days;
                if (dayIndex >= 0 && dayIndex < 7)
                {
                    row.DailyHours[dayIndex] += entry.Hours;
                    row.EntryIds[dayIndex] ??= entry.Id;
                    if (!string.IsNullOrEmpty(entry.Description))
                    {
                        row.DailyNotes[dayIndex] = string.IsNullOrEmpty(row.DailyNotes[dayIndex])
                            ? entry.Description
                            : row.DailyNotes[dayIndex] + "; " + entry.Description;
                    }
                }
            }

            if (row.DailyHours.Any(h => h > 0))
            {
                rows.Add(row);
            }
        }

        return rows;
    }

    /// <summary>
    /// Flattens weekly UI matrix rows back into <see cref="TimeEntry"/> records.
    /// Preserves rows with a valid <see cref="ProjectId"/> or Leave <see cref="EntryType"/> even when <see cref="WorkTypeId"/> is empty.
    /// </summary>
    public static List<TimeEntry> ToTimeEntries(
        IEnumerable<TimesheetUiRow> rows,
        DateTime weekStart,
        IReadOnlyList<TimeEntry>? existingEntries = null)
    {
        var newEntries = new List<TimeEntry>();
        var normalizedWeekStart = weekStart.Date;

        foreach (var row in rows)
        {
            var resolvedType = TimeEntryTypes.ResolveEntryType(row.ProjectId, row.EntryType);
            bool isLeave = TimeEntryTypes.IsLeaveEntryType(resolvedType);
            var effectiveProjectId = isLeave
                ? TimeEntryTypes.ToProjectKey(resolvedType)
                : row.ProjectId;

            if (string.IsNullOrEmpty(effectiveProjectId)) continue;

            for (int i = 0; i < 7; i++)
            {
                var hours = row.DailyHours[i];
                if (hours > 0)
                {
                    var entryId = !string.IsNullOrEmpty(row.EntryIds[i])
                        ? row.EntryIds[i]!
                        : Guid.NewGuid().ToString();
                    row.EntryIds[i] = entryId;

                    newEntries.Add(new TimeEntry
                    {
                        Id = entryId,
                        ProjectId = effectiveProjectId,
                        WorkTypeId = isLeave ? string.Empty : (row.WorkTypeId ?? string.Empty),
                        SubTaskId = isLeave || string.IsNullOrEmpty(row.SubTaskId) ? null : row.SubTaskId,
                        EntryType = resolvedType,
                        Date = normalizedWeekStart.AddDays(i),
                        Hours = hours,
                        Description = row.DailyNotes[i] ?? string.Empty,
                        IsBillable = !isLeave && row.IsBillable
                    });
                }
            }
        }

        // Defensive preservation of any entries whose Date fell outside the 7-day window
        if (existingEntries is { Count: > 0 })
        {
            foreach (var existing in existingEntries)
            {
                var dayIndex = (existing.Date.Date - normalizedWeekStart).Days;
                if (dayIndex < 0 || dayIndex >= 7)
                {
                    newEntries.Add(existing);
                }
            }
        }

        return newEntries;
    }
}

public class TimesheetDayEntryResult
{
    public bool IsDelete { get; set; }
    public string ProjectId { get; set; } = "";
    public string WorkTypeId { get; set; } = "";
    public string SubTaskId { get; set; } = "";
    public decimal Hours { get; set; }
    public string Note { get; set; } = "";
}




