using System;
using System.Collections.Generic;
using Spokes_Server.Core.Models.HR;
using Spokes_Server.Core.Models.Projects;
using Spokes_Server.Core.Services.HR;
using Xunit;

namespace Spokes_Server.Tests.Core.Services.HR;

public class WeeklyPlanServiceTests
{
    [Theory]
    [InlineData(2025, 12, 29, 2026, 1)]  // ISO week 1 of 2026 starts on Dec 29, 2025
    [InlineData(2021, 1, 1, 2020, 53)]   // Jan 1, 2021 belongs to ISO week 53 of 2020
    [InlineData(2026, 12, 31, 2026, 53)] // 2026 is a 53-week ISO year
    [InlineData(2026, 6, 15, 2026, 25)]
    public void GetCurrentIsoYearWeek_HandlesYearBoundariesCorrectly(int y, int m, int d, int expectedYear, int expectedWeek)
    {
        var (year, week) = WeeklyPlanService.GetCurrentIsoYearWeek(new DateTime(y, m, d));
        Assert.Equal(expectedYear, year);
        Assert.Equal(expectedWeek, week);
    }

    [Theory]
    [InlineData(2026, 52, 1, 2026, 53)]  // 2026 has 53 ISO weeks
    [InlineData(2026, 53, 1, 2027, 1)]   // Wraps from W53 to W01
    [InlineData(2027, 1, -1, 2026, 53)]  // Steps back into 53-week year
    [InlineData(2025, 52, 1, 2026, 1)]   // 2025 has 52 ISO weeks
    [InlineData(2026, 1, -1, 2025, 52)]  // Steps back into 52-week year
    public void ShiftIsoWeek_Handles52And53WeekYearsAccurately(int year, int week, int delta, int expectedYear, int expectedWeek)
    {
        var (nextYear, nextWeek) = WeeklyPlanService.ShiftIsoWeek(year, week, delta);
        Assert.Equal(expectedYear, nextYear);
        Assert.Equal(expectedWeek, nextWeek);
    }

    [Fact]
    public void EnrichProjectNames_PopulatesBlankProjectNamesWithoutOverwritingExisting()
    {
        var projects = new List<Project>
        {
            new() { Id = "p1", Name = "Alpha", ProjectNumber = "PRJ-01" },
            new() { Id = "p2", Name = "Beta", ProjectNumber = "PRJ-02" }
        };

        var plan = new WeeklyPlan
        {
            Entries =
            [
                new PlanEntry { ProjectId = "p1", ProjectName = "" },
                new PlanEntry { ProjectId = "p2", ProjectName = "Custom Label" },
                new PlanEntry { ProjectId = "missing", ProjectName = "" }
            ]
        };

        WeeklyPlanService.EnrichProjectNames(plan, projects);

        Assert.Equal(projects[0].DisplayName, plan.Entries[0].ProjectName);
        Assert.Equal("Custom Label", plan.Entries[1].ProjectName);
        Assert.Equal("", plan.Entries[2].ProjectName);
    }

    [Fact]
    public void CanAccessEntry_RespectsAccessibleIdsAndRestrictedNameFallback()
    {
        var publicProj = new Project { Id = "p1", Name = "Public Project" };
        var restrictedProj = new Project { Id = "p2", Name = "Secret Project" };
        var allProjects = new[] { publicProj, restrictedProj };
        var accessibleIds = new HashSet<string> { "p1" };

        Assert.True(WeeklyPlanService.CanAccessEntry(new PlanEntry { ProjectId = "p1" }, accessibleIds, allProjects));
        Assert.False(WeeklyPlanService.CanAccessEntry(new PlanEntry { ProjectId = "p2" }, accessibleIds, allProjects));
        Assert.True(WeeklyPlanService.CanAccessEntry(new PlanEntry { ProjectId = "", ProjectName = "" }, accessibleIds, allProjects));
        Assert.False(WeeklyPlanService.CanAccessEntry(new PlanEntry { ProjectId = "", ProjectName = restrictedProj.DisplayName }, accessibleIds, allProjects));
        Assert.True(WeeklyPlanService.CanAccessEntry(new PlanEntry { ProjectId = "", ProjectName = "Ad-hoc Freeform Activity" }, accessibleIds, allProjects));
    }

    [Fact]
    public void ResolveAndSearchProjects_MatchCaseInsensitively()
    {
        var projects = new List<Project>
        {
            new() { Id = "p1", Name = "Apollo", ProjectNumber = "100" },
            new() { Id = "p2", Name = "Zephyr", ProjectNumber = "200" }
        };

        var resolved = WeeklyPlanService.ResolveAccessibleProjectByName($"  {projects[0].DisplayName.ToUpperInvariant()}  ", projects);
        Assert.Equal("p1", resolved?.Id);
        Assert.Null(WeeklyPlanService.ResolveAccessibleProjectByName("Nonexistent", projects));

        var matches = WeeklyPlanService.SearchProjectNames("zeph", projects).ToList();
        Assert.Single(matches);
        Assert.Equal(projects[1].DisplayName, matches[0]);
    }
}
