namespace Spokes_Server.Core.Services.Projects;

using System.Globalization;
using Spokes_Server.Core.Models.HR;
using Spokes_Server.Core.Models.Projects;

public class ProjectLaborProgressModel
{
    public decimal TotalHours { get; set; }
    public int TotalContributorsCount { get; set; }
    public int ActiveTasksCount => TaskSummaries.Count;
    public List<Employee> AllDistinctEmployees { get; set; } = [];
    public List<TaskLaborSummary> TaskSummaries { get; set; } = [];
    public List<SubTaskGroupViewModel> SubTaskGroups { get; set; } = [];
    public ProjectWeeklyLaborModel WeeklyLabor { get; set; } = new();
}

public class ProjectWeeklyLaborModel
{
    public List<Employee> Employees { get; set; } = [];
    public List<TeamSummaryViewModel> Teams { get; set; } = [];
    public List<ProjectWeeklyRowViewModel> Rows { get; set; } = [];

    public decimal GetTotalForEmployee(string empId) => Rows.Sum(r => r.GetHours(empId));
    public decimal GetTotalForTeam(string teamId) => Rows.Sum(r => r.GetTeamHours(teamId));
    public decimal GrandTotal => Rows.Sum(r => r.TotalHours);
}

public class TeamSummaryViewModel
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Color { get; set; } = "#1E88E5";
    public int MemberCount { get; set; }
}

public class ProjectWeeklyRowViewModel
{
    public int Year { get; set; }
    public int Week { get; set; }
    public Dictionary<string, decimal> EmployeeHours { get; set; } = [];
    public Dictionary<string, decimal> TeamHours { get; set; } = [];

    public string WeekLabel
    {
        get
        {
            try
            {
                var date = ISOWeek.ToDateTime(Year, Week, DayOfWeek.Monday);
                return $"Week {Week} ({date:MMM dd})";
            }
            catch
            {
                return $"Week {Week}, {Year}";
            }
        }
    }

    public decimal GetHours(string empId) =>
        EmployeeHours.TryGetValue(empId, out var h) ? h : 0;

    public decimal GetTeamHours(string teamId) =>
        TeamHours.TryGetValue(teamId, out var h) ? h : 0;

    public decimal TotalHours => EmployeeHours.Values.Sum();
    public decimal RunningTotal { get; set; }
}

public class TaskLaborSummary
{
    public string Key { get; set; } = string.Empty;
    public string TaskName { get; set; } = string.Empty;
    public string SubTaskName { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public decimal TotalHours { get; set; }
    public decimal Percentage { get; set; }
    public List<Employee> Employees { get; set; } = [];
}

public class SubTaskGroupViewModel
{
    public string Key { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string TaskName { get; set; } = string.Empty;
    public string SubTaskName { get; set; } = string.Empty;
    public List<Employee> Employees { get; set; } = [];
    public List<RowViewModel> Rows { get; set; } = [];

    public decimal GetTotalForEmployee(string empId) => Rows.Sum(r => r.GetHours(empId));
    public decimal GrandTotal => Rows.Sum(r => r.TotalHours);
}

public class RowViewModel
{
    public int Year { get; set; }
    public int Week { get; set; }
    public Dictionary<string, decimal> EmployeeHours { get; set; } = [];

    public string WeekLabel
    {
        get
        {
            try
            {
                var date = ISOWeek.ToDateTime(Year, Week, DayOfWeek.Monday);
                return $"Week {Week} ({date:MMM dd})";
            }
            catch
            {
                return $"Week {Week}, {Year}";
            }
        }
    }

    public decimal GetHours(string empId) =>
        EmployeeHours.TryGetValue(empId, out var h) ? h : 0;

    public decimal TotalHours => EmployeeHours.Values.Sum();
}

public static class ProjectLaborCalculator
{
    public static ProjectLaborProgressModel Calculate(
        string projectId,
        IEnumerable<Timesheet> timesheets,
        IReadOnlyDictionary<string, WorkType> workTypes,
        IReadOnlyDictionary<string, Employee> employees,
        IReadOnlyDictionary<string, Team>? teams = null,
        DateTime? weeklyStartDate = null,
        DateTime? weeklyEndDate = null)
    {
        var model = new ProjectLaborProgressModel();
        if (string.IsNullOrWhiteSpace(projectId))
        {
            return model;
        }

        // Flatten entries for this project
        var entries = new List<(string EmployeeId, string WorkTypeId, string? SubTaskId, int Year, int WeekNumber, decimal Hours)>();

        foreach (var ts in timesheets)
        {
            if (ts?.Entries == null) continue;

            foreach (var entry in ts.Entries)
            {
                if (entry != null && entry.ProjectId == projectId)
                {
                    entries.Add((ts.EmployeeId, entry.WorkTypeId, entry.SubTaskId, ts.Year, ts.WeekNumber, entry.Hours));
                }
            }
        }

        if (entries.Count == 0)
        {
            return model;
        }

        // All distinct employees who contributed to this project
        var allDistinctEmpIds = entries.Select(e => e.EmployeeId).Distinct().ToList();
        var allDistinctEmployees = allDistinctEmpIds
            .Select(id => employees.TryGetValue(id, out var emp)
                ? emp
                : new Employee { Id = id, FirstName = "Unknown" })
            .OrderBy(e => e.FirstName)
            .ToList();

        // Group by (WorkType, SubTask)
        var grouped = entries
            .GroupBy(x => new { x.WorkTypeId, x.SubTaskId })
            .OrderBy(x => x.Key.WorkTypeId)
            .ToList();

        var subTaskGroups = new List<SubTaskGroupViewModel>();

        foreach (var g in grouped)
        {
            var workType = workTypes.TryGetValue(g.Key.WorkTypeId, out var wt) ? wt : null;
            var taskName = workType?.Name ?? "Unknown Task";

            string subTaskName = string.Empty;
            string title = taskName;

            if (!string.IsNullOrEmpty(g.Key.SubTaskId) && workType != null)
            {
                var st = workType.SubTasks.FirstOrDefault(s => s.Id == g.Key.SubTaskId);
                if (st != null)
                {
                    subTaskName = st.Name;
                    title = $"{taskName} - {subTaskName}";
                }
            }

            var groupEmpIds = g.Select(x => x.EmployeeId).Distinct().ToList();
            var groupEmployees = groupEmpIds
                .Select(id => employees.TryGetValue(id, out var emp)
                    ? emp
                    : new Employee { Id = id, FirstName = "Unknown" })
                .OrderBy(e => e.FirstName)
                .ToList();

            var rowData = g.GroupBy(x => new { x.Year, x.WeekNumber })
                .Select(weekGroup => new RowViewModel
                {
                    Year = weekGroup.Key.Year,
                    Week = weekGroup.Key.WeekNumber,
                    EmployeeHours = weekGroup.GroupBy(x => x.EmployeeId)
                        .ToDictionary(x => x.Key, x => x.Sum(e => e.Hours))
                })
                .OrderByDescending(r => r.Year).ThenByDescending(r => r.Week)
                .ToList();

            var groupVm = new SubTaskGroupViewModel
            {
                Key = $"{g.Key.WorkTypeId}_{(string.IsNullOrEmpty(g.Key.SubTaskId) ? "root" : g.Key.SubTaskId)}",
                Title = title,
                TaskName = taskName,
                SubTaskName = subTaskName,
                Employees = groupEmployees,
                Rows = rowData
            };
            subTaskGroups.Add(groupVm);
        }

        var totalProjectHours = subTaskGroups.Sum(g => g.GrandTotal);

        var taskSummaries = subTaskGroups.Select(g => new TaskLaborSummary
        {
            Key = g.Key,
            TaskName = g.TaskName,
            SubTaskName = g.SubTaskName,
            Title = g.Title,
            TotalHours = g.GrandTotal,
            Percentage = totalProjectHours > 0 ? Math.Round((g.GrandTotal / totalProjectHours) * 100m, 1) : 0m,
            Employees = g.Employees
        })
        .OrderByDescending(s => s.TotalHours)
        .ToList();

        model.WeeklyLabor = CalculateWeeklyLabor(
            projectId,
            timesheets,
            employees,
            teams,
            weeklyStartDate,
            weeklyEndDate,
            allDistinctEmployees);

        model.TotalHours = totalProjectHours;
        model.TotalContributorsCount = allDistinctEmployees.Count;
        model.AllDistinctEmployees = allDistinctEmployees;
        model.TaskSummaries = taskSummaries;
        model.SubTaskGroups = subTaskGroups;

        return model;
    }

    public static ProjectWeeklyLaborModel CalculateWeeklyLabor(
        string projectId,
        IEnumerable<Timesheet> timesheets,
        IReadOnlyDictionary<string, Employee> employees,
        IReadOnlyDictionary<string, Team>? teams = null,
        DateTime? startDate = null,
        DateTime? endDate = null,
        IEnumerable<Employee>? allProjectEmployees = null)
    {
        var weeklyModel = new ProjectWeeklyLaborModel();
        if (string.IsNullOrWhiteSpace(projectId))
        {
            return weeklyModel;
        }

        var allDistinctEmployees = allProjectEmployees?.ToList();
        if (allDistinctEmployees == null)
        {
            var allEmpIds = new HashSet<string>();
            foreach (var ts in timesheets)
            {
                if (ts?.Entries == null) continue;
                foreach (var entry in ts.Entries)
                {
                    if (entry != null && entry.ProjectId == projectId)
                    {
                        allEmpIds.Add(ts.EmployeeId);
                    }
                }
            }

            allDistinctEmployees = allEmpIds
                .Select(id => employees.TryGetValue(id, out var emp)
                    ? emp
                    : new Employee { Id = id, FirstName = "Unknown" })
                .OrderBy(e => e.FirstName)
                .ToList();
        }

        if (allDistinctEmployees.Count == 0)
        {
            return weeklyModel;
        }

        var teamLookup = teams ?? new Dictionary<string, Team>();
        var empTeamMap = allDistinctEmployees.ToDictionary(
            e => e.Id,
            e => !string.IsNullOrEmpty(e.TeamId) && teamLookup.ContainsKey(e.TeamId) ? e.TeamId : string.Empty);

        var distinctTeamIds = allDistinctEmployees
            .Select(e => empTeamMap[e.Id])
            .Distinct()
            .ToList();

        var teamSummaries = new List<TeamSummaryViewModel>();
        foreach (var tid in distinctTeamIds)
        {
            if (string.IsNullOrEmpty(tid))
            {
                teamSummaries.Add(new TeamSummaryViewModel
                {
                    Id = string.Empty,
                    Name = "Unassigned",
                    Color = "#9E9E9E",
                    MemberCount = allDistinctEmployees.Count(e => string.IsNullOrEmpty(empTeamMap[e.Id]))
                });
            }
            else if (teamLookup.TryGetValue(tid, out var team))
            {
                teamSummaries.Add(new TeamSummaryViewModel
                {
                    Id = team.Id,
                    Name = team.Name,
                    Color = team.Color,
                    MemberCount = allDistinctEmployees.Count(e => empTeamMap[e.Id] == tid)
                });
            }
        }
        weeklyModel.Teams = teamSummaries
            .OrderBy(t => t.Id == string.Empty ? 1 : 0)
            .ThenBy(t => t.Name)
            .ToList();

        var weeklyEntries = new List<(string EmployeeId, int Year, int WeekNumber, decimal Hours)>();
        foreach (var ts in timesheets)
        {
            if (ts?.Entries == null) continue;

            foreach (var entry in ts.Entries)
            {
                if (entry != null && entry.ProjectId == projectId)
                {
                    var entryDate = entry.Date != default
                        ? entry.Date.Date
                        : ISOWeek.ToDateTime(ts.Year, ts.WeekNumber, DayOfWeek.Monday);

                    if (startDate.HasValue && entryDate < startDate.Value.Date) continue;
                    if (endDate.HasValue && entryDate > endDate.Value.Date) continue;

                    weeklyEntries.Add((ts.EmployeeId, ts.Year, ts.WeekNumber, entry.Hours));
                }
            }
        }

        var hasDateFilter = startDate.HasValue || endDate.HasValue;
        if (hasDateFilter)
        {
            var activeEmpIds = weeklyEntries.Select(e => e.EmployeeId).Distinct().ToHashSet();
            weeklyModel.Employees = allDistinctEmployees
                .Where(e => activeEmpIds.Contains(e.Id))
                .ToList();
        }
        else
        {
            weeklyModel.Employees = allDistinctEmployees;
        }

        var weeklyRows = weeklyEntries
            .GroupBy(x => new { x.Year, x.WeekNumber })
            .Select(wg =>
            {
                var empHours = wg
                    .GroupBy(x => x.EmployeeId)
                    .ToDictionary(x => x.Key, x => x.Sum(e => e.Hours));

                var teamHours = wg
                    .GroupBy(x => empTeamMap.TryGetValue(x.EmployeeId, out var tid) ? tid : string.Empty)
                    .ToDictionary(x => x.Key, x => x.Sum(e => e.Hours));

                return new ProjectWeeklyRowViewModel
                {
                    Year = wg.Key.Year,
                    Week = wg.Key.WeekNumber,
                    EmployeeHours = empHours,
                    TeamHours = teamHours
                };
            })
            .OrderByDescending(r => r.Year)
            .ThenByDescending(r => r.Week)
            .ToList();

        var chronological = weeklyRows.OrderBy(r => r.Year).ThenBy(r => r.Week).ToList();
        decimal running = 0m;
        foreach (var row in chronological)
        {
            running += row.TotalHours;
            row.RunningTotal = running;
        }

        weeklyModel.Rows = weeklyRows;
        return weeklyModel;
    }
}
