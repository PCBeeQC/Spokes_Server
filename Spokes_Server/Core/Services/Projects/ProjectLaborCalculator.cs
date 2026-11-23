namespace Spokes_Server.Core.Services.Projects;

using System.Globalization;
using Spokes_Server.Core.Constants;
using Spokes_Server.Core.Models.HR;
using Spokes_Server.Core.Models.Projects;
using Spokes_Server.Core.Services.HR;
using Spokes_Server.Core.Services.Security;

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

public class ProjectWeeklyRowViewModel : RowViewModel
{
    public Dictionary<string, decimal> TeamHours { get; set; } = [];

    public decimal GetTeamHours(string teamId) =>
        TeamHours.TryGetValue(teamId, out var h) ? h : 0;

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
        var allDistinctEmployees = ResolveEmployees(entries.Select(e => e.EmployeeId), employees);

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

            var groupEmployees = ResolveEmployees(g.Select(x => x.EmployeeId), employees);

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

            allDistinctEmployees = ResolveEmployees(allEmpIds, employees);
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
                    var entryDate = TimeEntryTypes.ResolveEntryDate(ts, entry);

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

    public const string RestrictedContributorBucketId = "__restricted__";

    private static List<Employee> ResolveEmployees(
        IEnumerable<string> employeeIds,
        IReadOnlyDictionary<string, Employee> employees) =>
        employeeIds
            .Distinct()
            .Select(id => employees.TryGetValue(id, out var emp)
                ? emp
                : new Employee { Id = id, FirstName = "Unknown" })
            .OrderBy(e => e.FirstName)
            .ToList();

    internal sealed record ViewerLaborScope(
        bool CanViewAllReport,
        HashSet<string> ManagedIds,
        Dictionary<string, Team> VisibleTeams,
        Dictionary<string, Employee> EmployeeLookup)
    {
        public bool IsVisibleContributor(Employee? viewer, string? empId, IUserPrivacyService privacyService)
        {
            if (viewer == null || string.IsNullOrWhiteSpace(empId))
            {
                return false;
            }

            if (string.Equals(empId, viewer.Id, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (!CanViewAllReport && !ManagedIds.Contains(empId))
            {
                return false;
            }

            return EmployeeLookup.TryGetValue(empId, out var empObj) && privacyService.CanDiscoverUser(viewer, empObj);
        }
    }

    internal static ViewerLaborScope ResolveViewerLaborScope(
        Employee? viewer,
        IEnumerable<Employee> allEmployees,
        IEnumerable<Team> allTeams,
        HRService hrService,
        IUserPrivacyService privacyService)
    {
        var employeeLookup = allEmployees
            .Where(e => e != null && !string.IsNullOrEmpty(e.Id))
            .ToDictionary(e => e.Id, StringComparer.OrdinalIgnoreCase);

        if (viewer == null)
        {
            return new ViewerLaborScope(
                false,
                new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                new Dictionary<string, Team>(StringComparer.OrdinalIgnoreCase),
                employeeLookup);
        }

        var canViewAllReport = viewer.HasEffectivePermission(AppPermissions.Timesheets.ViewReport, allowSuspended: true);
        var managedIds = canViewAllReport
            ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(hrService.GetManagedEmployeeIds(viewer, includeSelf: true), StringComparer.OrdinalIgnoreCase);

        if (!string.IsNullOrEmpty(viewer.Id))
        {
            managedIds.Add(viewer.Id);
        }

        var visibleTeams = canViewAllReport
            ? privacyService.FilterDiscoverableTeams(viewer, allTeams)
                .ToDictionary(t => t.Id, t => t, StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, Team>(StringComparer.OrdinalIgnoreCase);

        return new ViewerLaborScope(canViewAllReport, managedIds, visibleTeams, employeeLookup);
    }

    public sealed record ScopedLaborInputs(
        List<Timesheet> ScopedTimesheets,
        Dictionary<string, Employee> VisibleEmployees,
        Dictionary<string, Team> VisibleTeams,
        HashSet<string> VisibleContributorIds,
        bool CanViewAllReport);

    /// <summary>
    /// Filters project labor inputs by viewer permissions, timesheet lifecycle status,
    /// manager hierarchy, and user/team privacy discovery rules without mutating cached entities.
    /// </summary>
    public static ScopedLaborInputs PrepareViewerScopedInputs(
        Employee? viewer,
        IEnumerable<Timesheet> allTimesheets,
        IEnumerable<Employee> allEmployees,
        IEnumerable<Team> allTeams,
        HRService hrService,
        IUserPrivacyService privacyService)
    {
        ArgumentNullException.ThrowIfNull(allTimesheets);
        ArgumentNullException.ThrowIfNull(allEmployees);
        ArgumentNullException.ThrowIfNull(allTeams);
        ArgumentNullException.ThrowIfNull(hrService);
        ArgumentNullException.ThrowIfNull(privacyService);

        if (viewer is not { IsActive: true, IsBanned: false, IsSystem: false })
        {
            return new ScopedLaborInputs([], [], [], new HashSet<string>(StringComparer.OrdinalIgnoreCase), false);
        }

        var scope = ResolveViewerLaborScope(viewer, allEmployees, allTeams, hrService, privacyService);
        var visibleTeams = scope.VisibleTeams;

        var visibleContributorIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var visibleEmployees = new Dictionary<string, Employee>(StringComparer.OrdinalIgnoreCase);

        foreach (var emp in scope.EmployeeLookup.Values)
        {
            if (!scope.IsVisibleContributor(viewer, emp.Id, privacyService))
            {
                continue;
            }

            visibleContributorIds.Add(emp.Id);

            var visibleTeamId = !string.IsNullOrEmpty(emp.TeamId) && visibleTeams.ContainsKey(emp.TeamId)
                ? emp.TeamId
                : string.Empty;

            // Clone for display so cached JsonRepository<Employee> instances are never mutated
            visibleEmployees[emp.Id] = new Employee
            {
                Id = emp.Id,
                FirstName = emp.FirstName,
                LastName = emp.LastName,
                Position = emp.Position,
                IsEmailPublic = emp.IsEmailPublic,
                Email = emp.IsEmailPublic ? emp.Email : string.Empty,
                TeamId = visibleTeamId,
                AvatarFile = emp.AvatarFile,
                AvatarBase64 = emp.AvatarBase64,
                AvatarVersion = emp.AvatarVersion
            };
        }

        var scopedTimesheets = new List<Timesheet>();
        foreach (var ts in allTimesheets)
        {
            if (ts == null) continue;

            var isSelfTimesheet = string.Equals(ts.EmployeeId, viewer.Id, StringComparison.OrdinalIgnoreCase);
            var statusAllowed = string.Equals(ts.Status, TimesheetStatus.Approved, StringComparison.OrdinalIgnoreCase)
                || string.Equals(ts.Status, TimesheetStatus.Submitted, StringComparison.OrdinalIgnoreCase)
                || (isSelfTimesheet && string.Equals(ts.Status, TimesheetStatus.Draft, StringComparison.OrdinalIgnoreCase));

            if (!statusAllowed)
            {
                continue;
            }

            var isVisibleContributor = visibleContributorIds.Contains(ts.EmployeeId);
            scopedTimesheets.Add(new Timesheet
            {
                Id = ts.Id,
                EmployeeId = isVisibleContributor ? ts.EmployeeId : RestrictedContributorBucketId,
                EmployeeName = isVisibleContributor ? ts.EmployeeName : string.Empty,
                Year = ts.Year,
                WeekNumber = ts.WeekNumber,
                Status = ts.Status,
                SubmittedAt = ts.SubmittedAt,
                ApprovedAt = ts.ApprovedAt,
                ApprovedBy = ts.ApprovedBy,
                RejectionNote = ts.RejectionNote,
                Entries = ts.Entries
            });
        }

        return new ScopedLaborInputs(
            scopedTimesheets,
            visibleEmployees,
            visibleTeams,
            visibleContributorIds,
            scope.CanViewAllReport);
    }
}
