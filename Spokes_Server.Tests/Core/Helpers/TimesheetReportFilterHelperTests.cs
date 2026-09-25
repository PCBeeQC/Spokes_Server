namespace Spokes_Server.Tests.Core.Helpers;

using Spokes_Server.Core.Helpers;
using Spokes_Server.Core.Models.HR;

public class TimesheetReportFilterHelperTests
{
    private readonly List<TimesheetReportEntry> _sampleEntries = new()
    {
        // Entry 1: Mats (No team, Internal Project with no group and no client)
        new TimesheetReportEntry
        {
            EmployeeId = "emp-mats",
            EmployeeName = "Mats Dallaire",
            TeamId = "", // No team
            ProjectId = "proj-internal",
            ProjectName = "Internal Admin",
            ProjectGroupId = "", // No group
            ClientId = "", // No client
            TaskId = "task-admin",
            TaskName = "Admin",
            Entry = new TimeEntry { Date = new DateTime(2026, 8, 15), Hours = 8.0m, Description = "Internal work" }
        },
        // Entry 2: Alice (Team A, Client Project in Group 1)
        new TimesheetReportEntry
        {
            EmployeeId = "emp-alice",
            EmployeeName = "Alice Smith",
            TeamId = "team-a",
            ProjectId = "proj-client1",
            ProjectName = "Website Redesign",
            ProjectGroupId = "group-web",
            ClientId = "Acme Corp",
            TaskId = "task-dev",
            TaskName = "Development",
            Entry = new TimeEntry { Date = new DateTime(2026, 8, 20), Hours = 6.5m, Description = "Frontend coding" }
        },
        // Entry 3: Bob (Team B, Client Project with no group)
        new TimesheetReportEntry
        {
            EmployeeId = "emp-bob",
            EmployeeName = "Bob Jones",
            TeamId = "team-b",
            ProjectId = "proj-client2",
            ProjectName = "Mobile App",
            ProjectGroupId = "",
            ClientId = "Beta LLC",
            TaskId = "task-qa",
            TaskName = "Testing",
            Entry = new TimeEntry { Date = new DateTime(2026, 8, 25), Hours = 4.0m, Description = "Regression test" }
        }
    };

    [Fact]
    public void Filter_WhenAllFiltersAreSelected_IncludesAllEntriesEvenWithoutTeamOrClientOrGroup()
    {
        // Simulates the default state of the report page where all filters are "Select All"
        var criteria = new TimesheetReportFilterCriteria
        {
            StartDate = new DateTime(2026, 8, 1),
            EndDate = new DateTime(2026, 8, 31),
            SelectedEmployees = new[] { "emp-mats", "emp-alice", "emp-bob" },
            TotalEmployeesCount = 3,
            SelectedTeams = new[] { "team-a", "team-b" },
            TotalTeamsCount = 2,
            SelectedProjectGroups = new[] { "group-web" },
            TotalProjectGroupsCount = 1,
            SelectedClients = new[] { "Acme Corp", "Beta LLC" },
            TotalClientsCount = 2,
            SelectedProjects = new[] { "proj-internal", "proj-client1", "proj-client2" },
            TotalProjectsCount = 3,
            SelectedTasks = new[] { "task-admin", "task-dev", "task-qa" },
            TotalTasksCount = 3
        };

        var results = TimesheetReportFilterHelper.Filter(_sampleEntries, criteria);

        // Mats's unassigned team/client/group entry MUST be included!
        Assert.Equal(3, results.Count);
        Assert.Contains(results, e => e.EmployeeId == "emp-mats");
        Assert.Contains(results, e => e.EmployeeId == "emp-alice");
        Assert.Contains(results, e => e.EmployeeId == "emp-bob");
    }

    [Fact]
    public void Filter_WhenSubsetOfTeamsSelected_OnlyIncludesMatchingTeams()
    {
        var criteria = new TimesheetReportFilterCriteria
        {
            SelectedTeams = new[] { "team-a" },
            TotalTeamsCount = 2 // 1 of 2 selected
        };

        var results = TimesheetReportFilterHelper.Filter(_sampleEntries, criteria);

        Assert.Single(results);
        Assert.Equal("emp-alice", results[0].EmployeeId);
    }

    [Fact]
    public void Filter_WhenSubsetOfEmployeesSelected_OnlyIncludesSelectedEmployees()
    {
        var criteria = new TimesheetReportFilterCriteria
        {
            SelectedEmployees = new[] { "emp-mats" },
            TotalEmployeesCount = 3
        };

        var results = TimesheetReportFilterHelper.Filter(_sampleEntries, criteria);

        Assert.Single(results);
        Assert.Equal("emp-mats", results[0].EmployeeId);
    }

    [Fact]
    public void Filter_WhenSubsetOfClientsSelected_OnlyIncludesMatchingClients()
    {
        var criteria = new TimesheetReportFilterCriteria
        {
            SelectedClients = new[] { "Acme Corp" },
            TotalClientsCount = 2
        };

        var results = TimesheetReportFilterHelper.Filter(_sampleEntries, criteria);

        Assert.Single(results);
        Assert.Equal("Acme Corp", results[0].ClientId);
    }

    [Fact]
    public void Filter_WhenSubsetOfProjectGroupsSelected_OnlyIncludesMatchingGroups()
    {
        var criteria = new TimesheetReportFilterCriteria
        {
            SelectedProjectGroups = new[] { "group-web" },
            TotalProjectGroupsCount = 2
        };

        var results = TimesheetReportFilterHelper.Filter(_sampleEntries, criteria);

        Assert.Single(results);
        Assert.Equal("group-web", results[0].ProjectGroupId);
    }

    [Fact]
    public void Filter_WhenSubsetOfProjectsSelected_OnlyIncludesMatchingProjects()
    {
        var criteria = new TimesheetReportFilterCriteria
        {
            SelectedProjects = new[] { "proj-internal" },
            TotalProjectsCount = 3
        };

        var results = TimesheetReportFilterHelper.Filter(_sampleEntries, criteria);

        Assert.Single(results);
        Assert.Equal("proj-internal", results[0].ProjectId);
    }

    [Fact]
    public void Filter_WhenSubsetOfTasksSelected_OnlyIncludesMatchingTasks()
    {
        var criteria = new TimesheetReportFilterCriteria
        {
            SelectedTasks = new[] { "task-qa" },
            TotalTasksCount = 3
        };

        var results = TimesheetReportFilterHelper.Filter(_sampleEntries, criteria);

        Assert.Single(results);
        Assert.Equal("task-qa", results[0].TaskId);
    }

    [Fact]
    public void Filter_WhenDateRangeApplied_FiltersCorrectly()
    {
        var criteria = new TimesheetReportFilterCriteria
        {
            StartDate = new DateTime(2026, 8, 18),
            EndDate = new DateTime(2026, 8, 22)
        };

        var results = TimesheetReportFilterHelper.Filter(_sampleEntries, criteria);

        Assert.Single(results);
        Assert.Equal("emp-alice", results[0].EmployeeId);
    }

    [Fact]
    public void Filter_WhenOnlyStartDateProvided_FiltersEntriesBeforeStartDate()
    {
        var criteria = new TimesheetReportFilterCriteria
        {
            StartDate = new DateTime(2026, 8, 20)
        };

        var results = TimesheetReportFilterHelper.Filter(_sampleEntries, criteria);

        Assert.Equal(2, results.Count);
        Assert.DoesNotContain(results, e => e.EmployeeId == "emp-mats");
        Assert.Contains(results, e => e.EmployeeId == "emp-alice");
        Assert.Contains(results, e => e.EmployeeId == "emp-bob");
    }

    [Fact]
    public void Filter_WhenOnlyEndDateProvided_FiltersEntriesAfterEndDate()
    {
        var criteria = new TimesheetReportFilterCriteria
        {
            EndDate = new DateTime(2026, 8, 20)
        };

        var results = TimesheetReportFilterHelper.Filter(_sampleEntries, criteria);

        Assert.Equal(2, results.Count);
        Assert.Contains(results, e => e.EmployeeId == "emp-mats");
        Assert.Contains(results, e => e.EmployeeId == "emp-alice");
        Assert.DoesNotContain(results, e => e.EmployeeId == "emp-bob");
    }

    [Fact]
    public void Filter_WhenEntriesHaveTimeComponents_ComparesDatesByCalendarDateOnly()
    {
        var entriesWithTimes = new List<TimesheetReportEntry>
        {
            new()
            {
                EmployeeId = "emp-1",
                Entry = new TimeEntry { Date = new DateTime(2026, 8, 20, 23, 59, 59) }
            },
            new()
            {
                EmployeeId = "emp-2",
                Entry = new TimeEntry { Date = new DateTime(2026, 8, 21, 0, 0, 1) }
            }
        };

        var criteria = new TimesheetReportFilterCriteria
        {
            StartDate = new DateTime(2026, 8, 20, 12, 0, 0),
            EndDate = new DateTime(2026, 8, 20, 15, 0, 0)
        };

        var results = TimesheetReportFilterHelper.Filter(entriesWithTimes, criteria);

        Assert.Single(results);
        Assert.Equal("emp-1", results[0].EmployeeId);
    }

    [Fact]
    public void Filter_Always_OrdersResultsByDescendingDate()
    {
        var criteria = new TimesheetReportFilterCriteria();

        var results = TimesheetReportFilterHelper.Filter(_sampleEntries, criteria);

        Assert.Equal(3, results.Count);
        Assert.Equal("emp-bob", results[0].EmployeeId);    // 2026-08-25
        Assert.Equal("emp-alice", results[1].EmployeeId);  // 2026-08-20
        Assert.Equal("emp-mats", results[2].EmployeeId);   // 2026-08-15
    }

    [Fact]
    public void Filter_WhenEntriesListIsEmpty_ReturnsEmptyList()
    {
        var criteria = new TimesheetReportFilterCriteria
        {
            StartDate = new DateTime(2026, 8, 1),
            EndDate = new DateTime(2026, 8, 31)
        };

        var results = TimesheetReportFilterHelper.Filter(new List<TimesheetReportEntry>(), criteria);

        Assert.NotNull(results);
        Assert.Empty(results);
    }

    [Fact]
    public void Filter_WithDifferentCasing_MatchesCaseInsensitively()
    {
        var criteria = new TimesheetReportFilterCriteria
        {
            SelectedEmployees = new[] { "EMP-ALICE" },
            TotalEmployeesCount = 3,
            SelectedTeams = new[] { "TEAM-A" },
            TotalTeamsCount = 2,
            SelectedProjectGroups = new[] { "GROUP-WEB" },
            TotalProjectGroupsCount = 2,
            SelectedClients = new[] { "acme corp" },
            TotalClientsCount = 2,
            SelectedProjects = new[] { "PROJ-CLIENT1" },
            TotalProjectsCount = 3,
            SelectedTasks = new[] { "TASK-DEV" },
            TotalTasksCount = 3
        };

        var results = TimesheetReportFilterHelper.Filter(_sampleEntries, criteria);

        Assert.Single(results);
        Assert.Equal("emp-alice", results[0].EmployeeId);
    }

    [Fact]
    public void Filter_WhenTotalCountIsZeroOrSelectedCountNotStrictlyLessThanTotal_DoesNotFilter()
    {
        var criteria = new TimesheetReportFilterCriteria
        {
            // TotalEmployeesCount is 0, so SelectedEmployees filter is ignored
            SelectedEmployees = new[] { "emp-mats" },
            TotalEmployeesCount = 0,
            // SelectedTeams count equals TotalTeamsCount, so filter is ignored
            SelectedTeams = new[] { "team-a" },
            TotalTeamsCount = 1
        };

        var results = TimesheetReportFilterHelper.Filter(_sampleEntries, criteria);

        Assert.Equal(3, results.Count);
    }

    [Fact]
    public void Filter_WhenMultipleFiltersApplied_IntersectsAllCriteria()
    {
        var criteria = new TimesheetReportFilterCriteria
        {
            StartDate = new DateTime(2026, 8, 18),
            EndDate = new DateTime(2026, 8, 31),
            SelectedEmployees = new[] { "emp-alice", "emp-bob" },
            TotalEmployeesCount = 3,
            SelectedProjects = new[] { "proj-client1" },
            TotalProjectsCount = 3
        };

        var results = TimesheetReportFilterHelper.Filter(_sampleEntries, criteria);

        Assert.Single(results);
        Assert.Equal("emp-alice", results[0].EmployeeId);
    }

    [Fact]
    public void Filter_WhenNoEntriesMatchCriteria_ReturnsEmptyList()
    {
        var criteria = new TimesheetReportFilterCriteria
        {
            StartDate = new DateTime(2025, 1, 1),
            EndDate = new DateTime(2025, 1, 31)
        };

        var results = TimesheetReportFilterHelper.Filter(_sampleEntries, criteria);

        Assert.Empty(results);
    }

    [Fact]
    public void Filter_WhenEntryHasEmptyTeamOrClientOrGroup_ExcludedWhenFilteringThoseFields()
    {
        // Mats has empty TeamId, ProjectGroupId, ClientId
        var criteria = new TimesheetReportFilterCriteria
        {
            SelectedTeams = new[] { "" }, // even if empty string is passed in SelectedTeams
            TotalTeamsCount = 2
        };

        var results = TimesheetReportFilterHelper.Filter(_sampleEntries, criteria);

        // Mats should not match because of !string.IsNullOrEmpty(e.TeamId) check
        Assert.Empty(results);
    }
}
