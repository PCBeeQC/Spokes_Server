namespace Spokes_Server.Core.Services.Reports;

using System.Text;
using MudBlazor;
using Spokes_Server.Aggregate.Contexts;
using Microsoft.Extensions.DependencyInjection;
using Spokes_Server.Core.Data.Repositories.Core;
using Spokes_Server.Core.Data.Repositories.HR;
using Spokes_Server.Core.Data.Repositories.Projects;
using Spokes_Server.Core.Helpers;
using Spokes_Server.Core.Services.Reports.Helpers;
using Spokes_Server.Core.Models.Accounting;
using Spokes_Server.Core.Models.HR;
using Spokes_Server.Core.Models.Projects;
using Spokes_Server.Core.Models.Reports;
using Spokes_Server.Core.Services.HR;

public class TimesheetReportService
{
    private readonly EmployeeRepository _employees;
    private readonly TeamRepository _teams;
    private readonly ProjectRepository _projects;
    private readonly WorkTypeRepository _workTypes;
    private readonly RateCardRepository _rateCards;
    private readonly TimesheetRepository _timesheets;
    private readonly CompanyProfileRepository _companyProfiles;

    private static readonly string[] ProjectColorPalette =
    [
        "#3B82F6", "#10B981", "#F59E0B", "#EF4444", "#8B5CF6",
        "#EC4899", "#14B8A6", "#F97316", "#06B6D4", "#6366F1"
    ];

    [ActivatorUtilitiesConstructor]
    public TimesheetReportService(HrContext hrContext, ProjectsContext projectsContext, CoreContext coreContext)
    {
        ArgumentNullException.ThrowIfNull(hrContext);
        ArgumentNullException.ThrowIfNull(projectsContext);
        ArgumentNullException.ThrowIfNull(coreContext);

        _employees = hrContext.Employees;
        _teams = hrContext.Teams;
        _projects = projectsContext.Projects;
        _workTypes = projectsContext.WorkTypes;
        _rateCards = projectsContext.RateCards;
        _timesheets = hrContext.Timesheets;
        _companyProfiles = coreContext.CompanyProfile;
    }

    public async Task<List<TimesheetReportEntry>> LoadAllEntriesAsync()
    {
        return await Task.Run(() =>
        {
            var employees = _employees.GetAll();
            var teams = _teams.GetAll();
            var projects = _projects.GetAll();
            var tasks = _workTypes.GetAll();
            var rateCards = _rateCards.GetAll();
            var timesheets = _timesheets.GetAll();

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
                        hourlyRate = proj.GetEffectiveRate(entry.WorkTypeId ?? string.Empty, task?.DefaultRate ?? 0, card);
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
                        Status = ts.Status ?? TimesheetStatus.Draft,
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

        var profile = _companyProfiles.Get();
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
        => TimesheetCalculationHelper.CalculateKPISummary(entries);

    public List<TimesheetGroupSummary> BuildSummaryGroups(
        List<TimesheetReportEntry> entries,
        ReportGroupBy primaryGroup,
        ReportSubGroupBy secondaryGroup)
        => TimesheetCalculationHelper.BuildSummaryGroups(entries, primaryGroup, secondaryGroup);

    public TimesheetWeeklyMatrix BuildWeeklyMatrix(
        List<TimesheetReportEntry> entries,
        DateTime weekStartDate,
        WeeklyGroupBy groupBy)
        => TimesheetCalculationHelper.BuildWeeklyMatrix(entries, weekStartDate, groupBy);

    public string FormatDuration(decimal hours, TimeDisplayFormat format)
        => TimesheetFormatHelper.FormatDuration(hours, format);

    public string FormatCurrency(decimal amount)
        => TimesheetFormatHelper.FormatCurrency(amount, _companyProfiles.Get()?.CurrencySymbol ?? "$");

    public DateRange GetPresetDateRange(string preset)
        => TimesheetDateRangeHelper.GetPresetDateRange(preset);

    public DateRange ShiftDateRange(DateRange currentRange, int direction)
        => TimesheetDateRangeHelper.ShiftDateRange(currentRange, direction);

    public string GenerateSummaryCsv(List<TimesheetGroupSummary> groups, TimeDisplayFormat format)
        => TimesheetCsvExportHelper.GenerateSummaryCsv(groups, format);

    public string GenerateDetailedCsv(List<TimesheetReportEntry> entries, TimeDisplayFormat format)
        => TimesheetCsvExportHelper.GenerateDetailedCsv(entries, format);

    public string GenerateWeeklyCsv(TimesheetWeeklyMatrix matrix, TimeDisplayFormat format)
        => TimesheetCsvExportHelper.GenerateWeeklyCsv(matrix, format);
}
