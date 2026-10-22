namespace Spokes_Server.Core.Services.Reports.Helpers;

using Spokes_Server.Core.Models.HR;
using Spokes_Server.Core.Models.Reports;

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
