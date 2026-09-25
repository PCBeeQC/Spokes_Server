namespace Spokes_Server.Core.Helpers;

using Spokes_Server.Core.Models.HR;

using Spokes_Server.Core.Models.Reports;

public class TimesheetReportFilterCriteria
{
    public DateTime? StartDate { get; set; }
    public DateTime? EndDate { get; set; }
    public IEnumerable<string>? SelectedEmployees { get; set; }
    public int TotalEmployeesCount { get; set; }
    public IEnumerable<string>? SelectedTeams { get; set; }
    public int TotalTeamsCount { get; set; }
    public IEnumerable<string>? SelectedProjectGroups { get; set; }
    public int TotalProjectGroupsCount { get; set; }
    public IEnumerable<string>? SelectedClients { get; set; }
    public int TotalClientsCount { get; set; }
    public IEnumerable<string>? SelectedProjects { get; set; }
    public int TotalProjectsCount { get; set; }
    public IEnumerable<string>? SelectedTasks { get; set; }
    public int TotalTasksCount { get; set; }

    public BillableFilterStatus BillableStatus { get; set; } = BillableFilterStatus.All;
    public string? TimesheetStatus { get; set; }
    public string? DescriptionSearch { get; set; }
}

public class TimesheetReportEntry
{
    public TimeEntry Entry { get; set; } = new();
    public string EmployeeId { get; set; } = "";
    public string EmployeeName { get; set; } = "";
    public string EmployeeInitials { get; set; } = "";
    public string TeamId { get; set; } = "";
    public string TeamName { get; set; } = "";
    public string ProjectId { get; set; } = "";
    public string ProjectName { get; set; } = "";
    public string ProjectGroupId { get; set; } = "";
    public string ClientId { get; set; } = "";
    public string TaskId { get; set; } = "";
    public string TaskName { get; set; } = "";
    public string Status { get; set; } = "Draft";
    public string ProjectColor { get; set; } = "#3B82F6";
    public decimal? ProjectAllocatedHours { get; set; }

    public decimal HourlyRate { get; set; }
    public decimal CostRate { get; set; }
    public decimal BillableAmount => Entry.IsBillable ? Entry.Hours * HourlyRate : 0m;
    public decimal CostAmount => Entry.Hours * CostRate;
    public decimal ProfitAmount => BillableAmount - CostAmount;
}

public static class TimesheetReportFilterHelper
{
    public static List<TimesheetReportEntry> Filter(
        IEnumerable<TimesheetReportEntry> entries,
        TimesheetReportFilterCriteria criteria)
    {
        var query = entries.AsQueryable();

        if (criteria.StartDate.HasValue)
        {
            query = query.Where(e => e.Entry.Date.Date >= criteria.StartDate.Value.Date);
        }
        if (criteria.EndDate.HasValue)
        {
            query = query.Where(e => e.Entry.Date.Date <= criteria.EndDate.Value.Date);
        }

        // Employee filter: only filter if a strict subset is selected
        if (criteria.SelectedEmployees != null && criteria.TotalEmployeesCount > 0 && criteria.SelectedEmployees.Count() < criteria.TotalEmployeesCount)
        {
            var set = criteria.SelectedEmployees.ToHashSet(StringComparer.OrdinalIgnoreCase);
            query = query.Where(e => set.Contains(e.EmployeeId));
        }

        // Team filter: only filter if a strict subset is selected
        if (criteria.SelectedTeams != null && criteria.TotalTeamsCount > 0 && criteria.SelectedTeams.Count() < criteria.TotalTeamsCount)
        {
            var set = criteria.SelectedTeams.ToHashSet(StringComparer.OrdinalIgnoreCase);
            query = query.Where(e => !string.IsNullOrEmpty(e.TeamId) && set.Contains(e.TeamId));
        }

        // Project Group filter: only filter if a strict subset is selected
        if (criteria.SelectedProjectGroups != null && criteria.TotalProjectGroupsCount > 0 && criteria.SelectedProjectGroups.Count() < criteria.TotalProjectGroupsCount)
        {
            var set = criteria.SelectedProjectGroups.ToHashSet(StringComparer.OrdinalIgnoreCase);
            query = query.Where(e => !string.IsNullOrEmpty(e.ProjectGroupId) && set.Contains(e.ProjectGroupId));
        }

        // Client filter: only filter if a strict subset is selected
        if (criteria.SelectedClients != null && criteria.TotalClientsCount > 0 && criteria.SelectedClients.Count() < criteria.TotalClientsCount)
        {
            var set = criteria.SelectedClients.ToHashSet(StringComparer.OrdinalIgnoreCase);
            query = query.Where(e => !string.IsNullOrEmpty(e.ClientId) && set.Contains(e.ClientId));
        }

        // Project filter: only filter if a strict subset is selected
        if (criteria.SelectedProjects != null && criteria.TotalProjectsCount > 0 && criteria.SelectedProjects.Count() < criteria.TotalProjectsCount)
        {
            var set = criteria.SelectedProjects.ToHashSet(StringComparer.OrdinalIgnoreCase);
            query = query.Where(e => set.Contains(e.ProjectId));
        }

        // Task filter: only filter if a strict subset is selected
        if (criteria.SelectedTasks != null && criteria.TotalTasksCount > 0 && criteria.SelectedTasks.Count() < criteria.TotalTasksCount)
        {
            var set = criteria.SelectedTasks.ToHashSet(StringComparer.OrdinalIgnoreCase);
            query = query.Where(e => set.Contains(e.TaskId));
        }

        // Billability filter
        if (criteria.BillableStatus == BillableFilterStatus.BillableOnly)
        {
            query = query.Where(e => e.Entry.IsBillable);
        }
        else if (criteria.BillableStatus == BillableFilterStatus.NonBillableOnly)
        {
            query = query.Where(e => !e.Entry.IsBillable);
        }

        // Timesheet Status filter
        if (!string.IsNullOrWhiteSpace(criteria.TimesheetStatus) && !string.Equals(criteria.TimesheetStatus, "All", StringComparison.OrdinalIgnoreCase))
        {
            query = query.Where(e => string.Equals(e.Status, criteria.TimesheetStatus, StringComparison.OrdinalIgnoreCase));
        }

        // Search text
        if (!string.IsNullOrWhiteSpace(criteria.DescriptionSearch))
        {
            var s = criteria.DescriptionSearch.Trim();
            query = query.Where(e => 
                (e.Entry.Description != null && e.Entry.Description.Contains(s, StringComparison.OrdinalIgnoreCase)) ||
                (e.ProjectName != null && e.ProjectName.Contains(s, StringComparison.OrdinalIgnoreCase)) ||
                (e.EmployeeName != null && e.EmployeeName.Contains(s, StringComparison.OrdinalIgnoreCase)) ||
                (e.TaskName != null && e.TaskName.Contains(s, StringComparison.OrdinalIgnoreCase)) ||
                (e.ClientId != null && e.ClientId.Contains(s, StringComparison.OrdinalIgnoreCase))
            );
        }

        return query.OrderByDescending(e => e.Entry.Date).ToList();
    }
}
