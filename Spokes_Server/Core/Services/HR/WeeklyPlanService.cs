using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Spokes_Server.Aggregate;
using Spokes_Server.Core.Models.HR;
using Spokes_Server.Core.Models.Projects;

namespace Spokes_Server.Core.Services.HR;

/// <summary>
/// Single source of truth for Weekly Planning ISO week calendar arithmetic,
/// plan loading/enrichment, project selection lookup, and restricted-project entry access checks.
/// </summary>
public class WeeklyPlanService
{
    private readonly Database _db;

    public WeeklyPlanService(Database db)
    {
        _db = db;
    }

    public static (int Year, int Week) GetCurrentIsoYearWeek(DateTime? today = null)
    {
        var date = (today ?? DateTime.Today).Date;
        return (ISOWeek.GetYear(date), ISOWeek.GetWeekOfYear(date));
    }

    public static (int Year, int Week) ShiftIsoWeek(int year, int week, int deltaWeeks)
    {
        if (deltaWeeks == 0)
        {
            return (year, week);
        }

        int currentYear = year;
        int currentWeek = week;
        int step = deltaWeeks > 0 ? 1 : -1;
        int remaining = Math.Abs(deltaWeeks);

        while (remaining > 0)
        {
            currentWeek += step;
            if (currentWeek < 1)
            {
                currentYear--;
                currentWeek = ISOWeek.GetWeeksInYear(currentYear);
            }
            else if (currentWeek > ISOWeek.GetWeeksInYear(currentYear))
            {
                currentYear++;
                currentWeek = 1;
            }
            remaining--;
        }

        return (currentYear, currentWeek);
    }

    public static void EnrichProjectNames(WeeklyPlan? plan, IReadOnlyList<Project> accessibleProjects)
    {
        if (plan == null || plan.Entries.Count == 0 || accessibleProjects.Count == 0)
        {
            return;
        }

        foreach (var entry in plan.Entries)
        {
            if (string.IsNullOrEmpty(entry.ProjectName) && !string.IsNullOrEmpty(entry.ProjectId))
            {
                var project = accessibleProjects.FirstOrDefault(x => x.Id == entry.ProjectId);
                if (project != null)
                {
                    entry.ProjectName = project.DisplayName;
                }
            }
        }
    }

    public WeeklyPlan GetOrLoadEnrichedPlan(
        string employeeId,
        int year,
        int week,
        string? employeeName,
        IReadOnlyList<Project> accessibleProjects)
    {
        var plan = _db.WeeklyPlans.GetOrStubByEmployeeAndWeek(employeeId, year, week);
        if (!string.IsNullOrWhiteSpace(employeeName))
        {
            plan.EmployeeName = employeeName;
        }

        EnrichProjectNames(plan, accessibleProjects);
        return plan;
    }

    public bool CanAccessEntry(PlanEntry entry, IReadOnlySet<string> accessibleProjectIds) =>
        CanAccessEntry(entry, accessibleProjectIds, _db.Projects.GetAll());

    public static bool CanAccessEntry(
        PlanEntry entry,
        IReadOnlySet<string> accessibleProjectIds,
        IEnumerable<Project> allProjects)
    {
        if (!string.IsNullOrWhiteSpace(entry.ProjectId))
        {
            return accessibleProjectIds.Contains(entry.ProjectId);
        }

        if (string.IsNullOrWhiteSpace(entry.ProjectName))
        {
            return true;
        }

        var trimmedName = entry.ProjectName.Trim();
        var matchingProjects = allProjects
            .Where(p => string.Equals(p.DisplayName, trimmedName, StringComparison.OrdinalIgnoreCase))
            .ToList();

        return matchingProjects.Count == 0 || matchingProjects.Any(p => accessibleProjectIds.Contains(p.Id));
    }

    public static Project? ResolveAccessibleProjectByName(string? projectName, IReadOnlyList<Project> accessibleProjects)
    {
        if (string.IsNullOrWhiteSpace(projectName))
        {
            return null;
        }

        var trimmedName = projectName.Trim();
        return accessibleProjects.FirstOrDefault(p =>
            string.Equals(p.DisplayName, trimmedName, StringComparison.OrdinalIgnoreCase));
    }

    public static IEnumerable<string> SearchProjectNames(string? query, IReadOnlyList<Project> accessibleProjects)
    {
        if (string.IsNullOrEmpty(query))
        {
            return accessibleProjects.Select(x => x.DisplayName);
        }

        return accessibleProjects
            .Where(x => x.DisplayName.Contains(query, StringComparison.InvariantCultureIgnoreCase))
            .Select(x => x.DisplayName);
    }
}
