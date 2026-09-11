namespace Spokes_Server.Core.Helpers;

using System;
using System.Collections.Generic;
using System.Linq;
using Spokes_Server.Core.Models.HR;

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
}

public class TimesheetReportEntry
{
    public TimeEntry Entry { get; set; } = new();
    public string EmployeeId { get; set; } = "";
    public string EmployeeName { get; set; } = "";
    public string TeamId { get; set; } = "";
    public string ProjectId { get; set; } = "";
    public string ProjectName { get; set; } = "";
    public string ProjectGroupId { get; set; } = "";
    public string ClientId { get; set; } = "";
    public string TaskId { get; set; } = "";
    public string TaskName { get; set; } = "";
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

        return query.OrderByDescending(e => e.Entry.Date).ToList();
    }
}
