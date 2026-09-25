namespace Spokes_Server.Core.Services.Reports;

using System.Text;
using MudBlazor;
using Spokes_Server.Aggregate;
using Spokes_Server.Core.Helpers;
using Spokes_Server.Core.Models.Accounting;
using Spokes_Server.Core.Models.HR;
using Spokes_Server.Core.Models.Projects;
using Spokes_Server.Core.Models.Reports;
using Spokes_Server.Core.Services.HR;

public class TimesheetReportService : ITimesheetReportService
{
    private readonly Database _db;

    private static readonly string[] ProjectColorPalette =
    [
        "#3B82F6", "#10B981", "#F59E0B", "#EF4444", "#8B5CF6",
        "#EC4899", "#14B8A6", "#F97316", "#06B6D4", "#6366F1"
    ];

    public TimesheetReportService(Database db)
    {
        _db = db;
    }

    public async Task<List<TimesheetReportEntry>> LoadAllEntriesAsync()
    {
        return await Task.Run(() =>
        {
            var employees = _db.Employees.GetAll();
            var teams = _db.Teams.GetAll();
            var projects = _db.Projects.GetAll();
            var tasks = _db.WorkTypes.GetAll();
            var rateCards = _db.RateCards.GetAll();
            var timesheets = _db.Timesheets.GetAll();

            var projectMap = projects.ToDictionary(p => p.Id, StringComparer.OrdinalIgnoreCase);
            var employeeMap = employees.ToDictionary(e => e.Id, StringComparer.OrdinalIgnoreCase);
            var teamMap = teams.ToDictionary(t => t.Id, StringComparer.OrdinalIgnoreCase);
            var taskMap = tasks.ToDictionary(t => t.Id, StringComparer.OrdinalIgnoreCase);
            var rateCardMap = rateCards.ToDictionary(r => r.Id, StringComparer.OrdinalIgnoreCase);

            var result = new List<TimesheetReportEntry>();

            foreach (var ts in timesheets)
            {
                employeeMap.TryGetValue(ts.EmployeeId ?? string.Empty, out var emp);
                var empName = emp?.FullName ?? (!string.IsNullOrWhiteSpace(ts.EmployeeName) ? ts.EmployeeName : "Unknown Employee");
                var empInitials = emp?.Initials ?? (empName.Length >= 2 ? empName[..2].ToUpper() : "??");
                var teamId = emp?.TeamId ?? string.Empty;
                teamMap.TryGetValue(teamId, out var team);
                var teamName = team?.Name ?? string.Empty;

                if (ts.Entries == null) continue;

                foreach (var entry in ts.Entries)
                {
                    projectMap.TryGetValue(entry.ProjectId ?? string.Empty, out var proj);
                    taskMap.TryGetValue(entry.WorkTypeId ?? string.Empty, out var task);

                    RateCard? card = null;
                    if (proj != null && !string.IsNullOrEmpty(proj.RateCardId))
                    {
                        rateCardMap.TryGetValue(proj.RateCardId, out card);
                    }

                    var hourlyRate = 0m;
                    if (proj != null)
                    {
                        hourlyRate = proj.GetEffectiveRate(entry.WorkTypeId, task?.DefaultRate ?? 0, card);
                    }
                    else if (task != null)
                    {
                        hourlyRate = task.DefaultRate;
                    }

                    var costRate = 0m;
                    if (emp != null && emp.HourlyRate > 0)
                    {
                        costRate = emp.HourlyRate;
                    }
                    else if (task != null)
                    {
                        costRate = task.CostRate;
                    }

                    decimal? allocatedHours = null;
                    if (proj != null && proj.Allocations.Count > 0)
                    {
                        allocatedHours = proj.Allocations.Sum(a => a.QuotedHours);
                    }

                    var colorIndex = Math.Abs((proj?.DisplayName ?? entry.ProjectId ?? "").GetHashCode()) % ProjectColorPalette.Length;
                    var projectColor = ProjectColorPalette[colorIndex];

                    result.Add(new TimesheetReportEntry
                    {
                        Entry = entry,
                        EmployeeId = emp?.Id ?? ts.EmployeeId ?? string.Empty,
                        EmployeeName = empName,
                        EmployeeInitials = empInitials,
                        TeamId = teamId,
                        TeamName = teamName,
                        ProjectId = proj?.Id ?? entry.ProjectId ?? string.Empty,
                        ProjectName = proj?.DisplayName ?? (!string.IsNullOrWhiteSpace(entry.ProjectId) ? entry.ProjectId : "Unknown Project"),
                        ProjectGroupId = proj?.ProjectGroupId ?? string.Empty,
                        ClientId = proj?.Client?.BusinessName ?? string.Empty,
                        TaskId = task?.Id ?? entry.WorkTypeId ?? string.Empty,
                        TaskName = task?.Name ?? (!string.IsNullOrWhiteSpace(entry.WorkTypeId) ? entry.WorkTypeId : "Unknown Task"),
                        Status = ts.Status ?? "Draft",
                        ProjectColor = projectColor,
                        ProjectAllocatedHours = allocatedHours,
                        HourlyRate = hourlyRate,
                        CostRate = costRate
                    });
                }
            }

            return result;
        });
    }

    public List<TimesheetReportEntry> ApplyFiltersAndRounding(
        List<TimesheetReportEntry> allEntries,
        TimesheetReportFilterCriteria criteria,
        bool applyRounding)
    {
        var filtered = TimesheetReportFilterHelper.Filter(allEntries, criteria);

        if (!applyRounding)
        {
            return filtered;
        }

        var profile = _db.CompanyProfile.Get();
        var roundingInterval = profile.TimesheetConfig?.RoundingIntervalMinutes ?? 15;
        var roundingDirection = profile.TimesheetConfig?.RoundingDirection ?? "Nearest";

        var roundedEntries = new List<TimesheetReportEntry>(filtered.Count);
        foreach (var item in filtered)
        {
            var roundedHours = HRService.RoundHours(item.Entry.Hours, roundingInterval, roundingDirection);

            var clonedEntry = new TimeEntry
            {
                Id = item.Entry.Id,
                ProjectId = item.Entry.ProjectId,
                WorkTypeId = item.Entry.WorkTypeId,
                SubTaskId = item.Entry.SubTaskId,
                Date = item.Entry.Date,
                Hours = roundedHours,
                Description = item.Entry.Description,
                IsBillable = item.Entry.IsBillable
            };

            roundedEntries.Add(new TimesheetReportEntry
            {
                Entry = clonedEntry,
                EmployeeId = item.EmployeeId,
                EmployeeName = item.EmployeeName,
                EmployeeInitials = item.EmployeeInitials,
                TeamId = item.TeamId,
                TeamName = item.TeamName,
                ProjectId = item.ProjectId,
                ProjectName = item.ProjectName,
                ProjectGroupId = item.ProjectGroupId,
                ClientId = item.ClientId,
                TaskId = item.TaskId,
                TaskName = item.TaskName,
                Status = item.Status,
                ProjectColor = item.ProjectColor,
                ProjectAllocatedHours = item.ProjectAllocatedHours,
                HourlyRate = item.HourlyRate,
                CostRate = item.CostRate
            });
        }

        return roundedEntries;
    }

    public TimesheetKPISummary CalculateKPISummary(List<TimesheetReportEntry> entries)
    {
        var summary = new TimesheetKPISummary
        {
            TotalHours = entries.Sum(e => e.Entry.Hours),
            BillableHours = entries.Where(e => e.Entry.IsBillable).Sum(e => e.Entry.Hours),
            BillableAmount = entries.Sum(e => e.BillableAmount),
            CostAmount = entries.Sum(e => e.CostAmount),
            EntriesCount = entries.Count,
            ActiveProjectsCount = entries.Select(e => e.ProjectId).Where(id => !string.IsNullOrEmpty(id)).Distinct().Count(),
            ActiveEmployeesCount = entries.Select(e => e.EmployeeId).Where(id => !string.IsNullOrEmpty(id)).Distinct().Count()
        };

        return summary;
    }

    public List<TimesheetGroupSummary> BuildSummaryGroups(
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

    public TimesheetWeeklyMatrix BuildWeeklyMatrix(
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

    public string FormatDuration(decimal hours, TimeDisplayFormat format)
    {
        if (format == TimeDisplayFormat.Decimal_Hours)
        {
            return $"{hours:0.00} h";
        }

        var totalMinutes = (int)Math.Round(hours * 60m);
        var sign = totalMinutes < 0 ? "-" : "";
        totalMinutes = Math.Abs(totalMinutes);
        var h = totalMinutes / 60;
        var m = totalMinutes % 60;
        return $"{sign}{h}:{m:D2}";
    }

    public string FormatCurrency(decimal amount)
    {
        var symbol = _db.CompanyProfile.Get()?.CurrencySymbol ?? "$";
        return $"{amount:N2} {symbol}".Trim();
    }

    public DateRange GetPresetDateRange(string preset)
    {
        var now = DateTime.Now;

        return preset switch
        {
            "Today" => new DateRange(now.Date, now.Date),
            "Yesterday" => new DateRange(now.Date.AddDays(-1), now.Date.AddDays(-1)),
            "This Week" => GetWeekRange(now),
            "Last Week" => GetWeekRange(now.AddDays(-7)),
            "This Month" => new DateRange(new DateTime(now.Year, now.Month, 1), new DateTime(now.Year, now.Month, DateTime.DaysInMonth(now.Year, now.Month))),
            "Last Month" => GetLastMonthRange(now),
            "This Year" => new DateRange(new DateTime(now.Year, 1, 1), new DateTime(now.Year, 12, 31)),
            "Last Year" => new DateRange(new DateTime(now.Year - 1, 1, 1), new DateTime(now.Year - 1, 12, 31)),
            _ => GetWeekRange(now)
        };
    }

    public DateRange ShiftDateRange(DateRange currentRange, int direction)
    {
        if (currentRange.Start == null || currentRange.End == null)
        {
            return GetPresetDateRange("This Week");
        }

        var start = currentRange.Start.Value;
        var end = currentRange.End.Value;
        var totalDays = (end - start).TotalDays + 1;

        if (totalDays <= 7)
        {
            return new DateRange(start.AddDays(7 * direction), end.AddDays(7 * direction));
        }
        else if (totalDays >= 28 && totalDays <= 31)
        {
            var newStart = start.AddMonths(direction);
            var daysInMonth = DateTime.DaysInMonth(newStart.Year, newStart.Month);
            return new DateRange(new DateTime(newStart.Year, newStart.Month, 1), new DateTime(newStart.Year, newStart.Month, daysInMonth));
        }
        else
        {
            return new DateRange(start.AddDays((int)totalDays * direction), end.AddDays((int)totalDays * direction));
        }
    }

    public string GenerateSummaryCsv(List<TimesheetGroupSummary> groups, TimeDisplayFormat format)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Title,Subtitle,Duration,Billable Hours,Billable Amount,Cost Amount,Entries Count");

        foreach (var group in groups)
        {
            var title = EscapeCsv(group.Key);
            var subtitle = EscapeCsv(group.Subtitle);
            var duration = EscapeCsv(FormatDuration(group.TotalHours, format));
            var billable = EscapeCsv(FormatDuration(group.BillableHours, format));
            var amount = $"{group.BillableAmount:0.00}";
            var cost = $"{group.CostAmount:0.00}";
            sb.AppendLine($"{title},{subtitle},{duration},{billable},{amount},{cost},{group.EntryCount}");

            foreach (var sub in group.Subgroups)
            {
                var subTitle = EscapeCsv($"  - {sub.Key}");
                var subDuration = EscapeCsv(FormatDuration(sub.TotalHours, format));
                var subBillable = EscapeCsv(FormatDuration(sub.BillableHours, format));
                var subAmount = $"{sub.BillableAmount:0.00}";
                var subCost = $"{sub.CostAmount:0.00}";
                sb.AppendLine($"{subTitle},,{subDuration},{subBillable},{subAmount},{subCost},{sub.EntryCount}");
            }
        }

        return sb.ToString();
    }

    public string GenerateDetailedCsv(List<TimesheetReportEntry> entries, TimeDisplayFormat format)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Date,Employee,Project,Client,Task,Description,Is Billable,Rate,Hours,Amount");

        foreach (var item in entries.OrderByDescending(e => e.Entry.Date))
        {
            var date = item.Entry.Date.ToString("yyyy-MM-dd");
            var employee = EscapeCsv(item.EmployeeName);
            var project = EscapeCsv(item.ProjectName);
            var client = EscapeCsv(item.ClientId);
            var task = EscapeCsv(item.TaskName);
            var description = EscapeCsv(item.Entry.Description);
            var billable = item.Entry.IsBillable ? "Yes" : "No";
            var rate = $"{item.HourlyRate:0.00}";
            var hours = EscapeCsv(FormatDuration(item.Entry.Hours, format));
            var amount = $"{item.BillableAmount:0.00}";

            sb.AppendLine($"{date},{employee},{project},{client},{task},{description},{billable},{rate},{hours},{amount}");
        }

        return sb.ToString();
    }

    public string GenerateWeeklyCsv(TimesheetWeeklyMatrix matrix, TimeDisplayFormat format)
    {
        var sb = new StringBuilder();
        sb.Append("Entity,Subtitle,");
        sb.Append(string.Join(",", matrix.DayHeaders.Select(EscapeCsv)));
        sb.AppendLine(",Total Duration,Total Amount");

        foreach (var row in matrix.Rows)
        {
            var name = EscapeCsv(row.EntityName);
            var subtitle = EscapeCsv(row.Subtitle);
            var cells = string.Join(",", row.Cells.Select(c => EscapeCsv(FormatDuration(c.Hours, format))));
            var totalDuration = EscapeCsv(FormatDuration(row.TotalHours, format));
            var totalAmount = $"{row.TotalBillableAmount:0.00}";

            sb.AppendLine($"{name},{subtitle},{cells},{totalDuration},{totalAmount}");
        }

        // Summary row
        var dayTotals = string.Join(",", matrix.DayTotals.Select(h => EscapeCsv(FormatDuration(h, format))));
        var grandTotalDuration = EscapeCsv(FormatDuration(matrix.GrandTotalHours, format));
        var grandTotalAmount = $"{matrix.GrandTotalBillableAmount:0.00}";
        sb.AppendLine($"Total,,{dayTotals},{grandTotalDuration},{grandTotalAmount}");

        return sb.ToString();
    }

    private static DateRange GetWeekRange(DateTime date)
    {
        var diff = (7 + (date.DayOfWeek - DayOfWeek.Monday)) % 7;
        var monday = date.Date.AddDays(-diff);
        return new DateRange(monday, monday.AddDays(6));
    }

    private static DateRange GetLastMonthRange(DateTime date)
    {
        var prev = date.AddMonths(-1);
        var days = DateTime.DaysInMonth(prev.Year, prev.Month);
        return new DateRange(new DateTime(prev.Year, prev.Month, 1), new DateTime(prev.Year, prev.Month, days));
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

    private static string EscapeCsv(string? value)
    {
        if (string.IsNullOrEmpty(value)) return "\"\"";
        return $"\"{value.Replace("\"", "\"\"")}\"";
    }
}
