namespace Spokes_Server.Core.Services.Reports.Helpers;

using Spokes_Server.Core.Models.Reports;

/// <summary>
/// Pure calculation and aggregation functions for timesheet reports.
/// </summary>
public static class TimesheetCalculationHelper
{
    public static TimesheetKPISummary CalculateKPISummary(List<TimesheetReportEntry> entries)
    {
        return new TimesheetKPISummary
        {
            TotalHours = entries.Sum(e => e.Entry.Hours),
            BillableHours = entries.Where(e => e.Entry.IsBillable).Sum(e => e.Entry.Hours),
            BillableAmount = entries.Sum(e => e.BillableAmount),
            CostAmount = entries.Sum(e => e.CostAmount),
            EntriesCount = entries.Count,
            ActiveProjectsCount = entries.Select(e => e.ProjectId).Where(id => !string.IsNullOrEmpty(id)).Distinct().Count(),
            ActiveEmployeesCount = entries.Select(e => e.EmployeeId).Where(id => !string.IsNullOrEmpty(id)).Distinct().Count()
        };
    }

    public static List<TimesheetGroupSummary> BuildSummaryGroups(
        List<TimesheetReportEntry> entries,
        ReportGroupBy primaryGroup,
        ReportSubGroupBy secondaryGroup)
    {
        var grouped = entries.GroupBy(e => GetPrimaryGroupKey(e, primaryGroup)).ToList();
        var result = new List<TimesheetGroupSummary>();

        foreach (var grp in grouped)
        {
            var firstItem = grp.First();
            var groupKey = grp.Key;
            var subtitle = GetPrimaryGroupSubtitle(firstItem, primaryGroup);
            var color = primaryGroup == ReportGroupBy.Project ? firstItem.ProjectColor : "#3B82F6";
            var allocated = primaryGroup == ReportGroupBy.Project ? firstItem.ProjectAllocatedHours : null;

            var groupSummary = new TimesheetGroupSummary
            {
                Key = groupKey,
                Subtitle = subtitle,
                Color = color,
                TotalHours = grp.Sum(e => e.Entry.Hours),
                BillableHours = grp.Where(e => e.Entry.IsBillable).Sum(e => e.Entry.Hours),
                BillableAmount = grp.Sum(e => e.BillableAmount),
                CostAmount = grp.Sum(e => e.CostAmount),
                EntryCount = grp.Count(),
                AllocatedHours = allocated
            };

            if (secondaryGroup != ReportSubGroupBy.None)
            {
                var subGrouped = grp.GroupBy(e => GetSecondaryGroupKey(e, secondaryGroup)).ToList();

                foreach (var subGrp in subGrouped)
                {
                    var subFirst = subGrp.First();
                    groupSummary.Subgroups.Add(new TimesheetSubGroupSummary
                    {
                        Key = subGrp.Key,
                        TotalHours = subGrp.Sum(e => e.Entry.Hours),
                        BillableHours = subGrp.Where(e => e.Entry.IsBillable).Sum(e => e.Entry.Hours),
                        BillableAmount = subGrp.Sum(e => e.BillableAmount),
                        CostAmount = subGrp.Sum(e => e.CostAmount),
                        EntryCount = subGrp.Count(),
                        HourlyRate = subFirst.HourlyRate,
                        Entries = subGrp.OrderByDescending(e => e.Entry.Date).ToList()
                    });
                }

                groupSummary.Subgroups = groupSummary.Subgroups
                    .OrderByDescending(s => s.TotalHours)
                    .ToList();
            }

            result.Add(groupSummary);
        }

        return result.OrderByDescending(g => g.TotalHours).ToList();
    }

    public static TimesheetWeeklyMatrix BuildWeeklyMatrix(
        List<TimesheetReportEntry> entries,
        DateTime weekStartDate,
        WeeklyGroupBy groupBy)
    {
        // Normalize week start to Monday
        var diff = (7 + (weekStartDate.DayOfWeek - DayOfWeek.Monday)) % 7;
        var monday = weekStartDate.Date.AddDays(-diff);
        var sunday = monday.AddDays(6);

        var weekDays = Enumerable.Range(0, 7).Select(i => monday.AddDays(i)).ToList();
        var headers = weekDays.Select(d => d.ToString("ddd, MMM dd")).ToList();

        var matrix = new TimesheetWeeklyMatrix
        {
            WeekStartDate = monday,
            WeekEndDate = sunday,
            DayHeaders = headers
        };

        // Filter entries within that Monday..Sunday week
        var weekEntries = entries
            .Where(e => e.Entry.Date.Date >= monday && e.Entry.Date.Date <= sunday)
            .ToList();

        var grouped = weekEntries.GroupBy(e => GetWeeklyGroupKey(e, groupBy)).ToList();

        foreach (var grp in grouped)
        {
            var firstItem = grp.First();
            var row = new TimesheetWeeklyRow
            {
                EntityId = grp.Key,
                EntityName = GetWeeklyGroupName(firstItem, groupBy),
                Subtitle = GetWeeklyGroupSubtitle(firstItem, groupBy),
                Color = groupBy == WeeklyGroupBy.Project ? firstItem.ProjectColor : "#3B82F6",
                TargetHours = 40m
            };

            for (int i = 0; i < 7; i++)
            {
                var dayDate = monday.AddDays(i);
                var dayItems = grp.Where(e => e.Entry.Date.Date == dayDate).ToList();

                row.Cells.Add(new TimesheetWeeklyCell
                {
                    Date = dayDate,
                    DayOfWeek = dayDate.DayOfWeek,
                    DayLabel = dayDate.ToString("ddd dd"),
                    Hours = dayItems.Sum(e => e.Entry.Hours),
                    BillableHours = dayItems.Where(e => e.Entry.IsBillable).Sum(e => e.Entry.Hours),
                    BillableAmount = dayItems.Sum(e => e.BillableAmount)
                });
            }

            // Subrows: if grouped by Employee, break down by Project; if Project, break down by Employee
            var subGroupKeySelector = groupBy == WeeklyGroupBy.Employee
                ? (Func<TimesheetReportEntry, string>)(e => e.ProjectName)
                : (Func<TimesheetReportEntry, string>)(e => e.EmployeeName);

            var subGroups = grp.GroupBy(subGroupKeySelector);
            foreach (var sub in subGroups)
            {
                var subFirst = sub.First();
                var subRow = new TimesheetWeeklyRow
                {
                    EntityId = sub.Key,
                    EntityName = sub.Key,
                    Subtitle = groupBy == WeeklyGroupBy.Employee ? subFirst.ClientId : subFirst.TeamName,
                    Color = subFirst.ProjectColor
                };

                for (int i = 0; i < 7; i++)
                {
                    var dayDate = monday.AddDays(i);
                    var dayItems = sub.Where(e => e.Entry.Date.Date == dayDate).ToList();

                    subRow.Cells.Add(new TimesheetWeeklyCell
                    {
                        Date = dayDate,
                        DayOfWeek = dayDate.DayOfWeek,
                        DayLabel = dayDate.ToString("ddd dd"),
                        Hours = dayItems.Sum(e => e.Entry.Hours),
                        BillableHours = dayItems.Where(e => e.Entry.IsBillable).Sum(e => e.Entry.Hours),
                        BillableAmount = dayItems.Sum(e => e.BillableAmount)
                    });
                }

                row.SubRows.Add(subRow);
            }

            row.SubRows = row.SubRows.OrderByDescending(s => s.TotalHours).ToList();
            matrix.Rows.Add(row);
        }

        matrix.Rows = matrix.Rows.OrderByDescending(r => r.TotalHours).ToList();

        // Calculate day totals
        for (int i = 0; i < 7; i++)
        {
            var dayDate = monday.AddDays(i);
            matrix.DayTotals[i] = weekEntries.Where(e => e.Entry.Date.Date == dayDate).Sum(e => e.Entry.Hours);
        }

        return matrix;
    }

    private static string GetPrimaryGroupKey(TimesheetReportEntry e, ReportGroupBy groupBy) => groupBy switch
    {
        ReportGroupBy.Project => !string.IsNullOrWhiteSpace(e.ProjectName) ? e.ProjectName : "No Project",
        ReportGroupBy.Client => !string.IsNullOrWhiteSpace(e.ClientId) ? e.ClientId : "No Client",
        ReportGroupBy.Employee => !string.IsNullOrWhiteSpace(e.EmployeeName) ? e.EmployeeName : "Unknown Employee",
        ReportGroupBy.Task => !string.IsNullOrWhiteSpace(e.TaskName) ? e.TaskName : "Unknown Task",
        ReportGroupBy.Date => e.Entry.Date.ToString("yyyy-MM-dd (ddd)"),
        _ => e.ProjectName
    };

    private static string GetPrimaryGroupSubtitle(TimesheetReportEntry e, ReportGroupBy groupBy) => groupBy switch
    {
        ReportGroupBy.Project => e.ClientId,
        ReportGroupBy.Employee => e.TeamName,
        ReportGroupBy.Task => e.ProjectName,
        _ => string.Empty
    };

    private static string GetSecondaryGroupKey(TimesheetReportEntry e, ReportSubGroupBy subGroup) => subGroup switch
    {
        ReportSubGroupBy.Description => !string.IsNullOrWhiteSpace(e.Entry.Description) ? e.Entry.Description : "(No description)",
        ReportSubGroupBy.Task => !string.IsNullOrWhiteSpace(e.TaskName) ? e.TaskName : "(No task)",
        ReportSubGroupBy.Employee => !string.IsNullOrWhiteSpace(e.EmployeeName) ? e.EmployeeName : "(No employee)",
        ReportSubGroupBy.Project => !string.IsNullOrWhiteSpace(e.ProjectName) ? e.ProjectName : "(No project)",
        _ => string.Empty
    };

    private static string GetWeeklyGroupKey(TimesheetReportEntry e, WeeklyGroupBy groupBy) => groupBy switch
    {
        WeeklyGroupBy.Employee => !string.IsNullOrEmpty(e.EmployeeId) ? e.EmployeeId : e.EmployeeName,
        WeeklyGroupBy.Project => !string.IsNullOrEmpty(e.ProjectId) ? e.ProjectId : e.ProjectName,
        WeeklyGroupBy.Client => !string.IsNullOrEmpty(e.ClientId) ? e.ClientId : "No Client",
        _ => e.EmployeeId
    };

    private static string GetWeeklyGroupName(TimesheetReportEntry e, WeeklyGroupBy groupBy) => groupBy switch
    {
        WeeklyGroupBy.Employee => e.EmployeeName,
        WeeklyGroupBy.Project => e.ProjectName,
        WeeklyGroupBy.Client => !string.IsNullOrEmpty(e.ClientId) ? e.ClientId : "No Client",
        _ => e.EmployeeName
    };

    private static string GetWeeklyGroupSubtitle(TimesheetReportEntry e, WeeklyGroupBy groupBy) => groupBy switch
    {
        WeeklyGroupBy.Employee => e.TeamName,
        WeeklyGroupBy.Project => e.ClientId,
        WeeklyGroupBy.Client => string.Empty,
        _ => string.Empty
    };
}
