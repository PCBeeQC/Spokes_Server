namespace Spokes_Server.Tests.Core.Services.Projects;

using System;
using System.Collections.Generic;
using System.Linq;
using Spokes_Server.Core.Models.HR;
using Spokes_Server.Core.Models.Projects;
using Spokes_Server.Core.Services.Projects;
using Spokes_Server.Core.Services.Reports.Helpers;
using Xunit;

public class ProjectLaborCalculatorTests
{
    [Fact]
    public void Calculate_WithEmptyOrNullProjectId_ReturnsEmptyModel()
    {
        var timesheets = new List<Timesheet>
        {
            new()
            {
                EmployeeId = "emp-1",
                Entries = [new TimeEntry { ProjectId = "proj-1", WorkTypeId = "wt-1", Hours = 5 }]
            }
        };

        var modelEmpty = ProjectLaborCalculator.Calculate("", timesheets, new Dictionary<string, WorkType>(), new Dictionary<string, Employee>());
        var modelNull = ProjectLaborCalculator.Calculate("   ", timesheets, new Dictionary<string, WorkType>(), new Dictionary<string, Employee>());

        Assert.Equal(0, modelEmpty.TotalHours);
        Assert.Empty(modelEmpty.TaskSummaries);
        Assert.Empty(modelEmpty.SubTaskGroups);
        Assert.Equal(0, modelNull.TotalHours);
    }

    [Fact]
    public void Calculate_WithNoMatchingTimesheets_ReturnsEmptyModel()
    {
        var timesheets = new List<Timesheet>
        {
            new()
            {
                EmployeeId = "emp-1",
                Entries = [new TimeEntry { ProjectId = "proj-OTHER", WorkTypeId = "wt-1", Hours = 5 }]
            }
        };

        var model = ProjectLaborCalculator.Calculate("proj-1", timesheets, new Dictionary<string, WorkType>(), new Dictionary<string, Employee>());

        Assert.Equal(0, model.TotalHours);
        Assert.Equal(0, model.TotalContributorsCount);
        Assert.Empty(model.TaskSummaries);
        Assert.Empty(model.SubTaskGroups);
    }

    [Fact]
    public void Calculate_FiltersOutEntriesFromOtherProjects()
    {
        var wt1 = new WorkType { Id = "wt-1", Name = "Framing" };
        var emp1 = new Employee { Id = "emp-1", FirstName = "Alice", LastName = "Smith" };

        var timesheets = new List<Timesheet>
        {
            new()
            {
                EmployeeId = "emp-1",
                Year = 2026,
                WeekNumber = 10,
                Entries =
                [
                    new TimeEntry { ProjectId = "proj-TARGET", WorkTypeId = "wt-1", Hours = 12.5m },
                    new TimeEntry { ProjectId = "proj-OTHER", WorkTypeId = "wt-1", Hours = 40m }
                ]
            }
        };

        var workTypes = new Dictionary<string, WorkType> { [wt1.Id] = wt1 };
        var employees = new Dictionary<string, Employee> { [emp1.Id] = emp1 };

        var model = ProjectLaborCalculator.Calculate("proj-TARGET", timesheets, workTypes, employees);

        Assert.Equal(12.5m, model.TotalHours);
        Assert.Single(model.TaskSummaries);
        Assert.Equal(12.5m, model.TaskSummaries[0].TotalHours);
        Assert.Equal(100m, model.TaskSummaries[0].Percentage);
    }

    [Fact]
    public void Calculate_WithMultipleTasksAndSubtasks_GroupsAndCalculatesCorrectly()
    {
        var wt1 = new WorkType
        {
            Id = "wt-elec",
            Name = "Electrical",
            SubTasks = [new SubTask { Id = "st-rough", Name = "Rough-In" }]
        };
        var wt2 = new WorkType
        {
            Id = "wt-plumb",
            Name = "Plumbing"
        };

        var emp1 = new Employee { Id = "emp-1", FirstName = "Alice", LastName = "Smith" };
        var emp2 = new Employee { Id = "emp-2", FirstName = "Bob", LastName = "Jones" };

        var timesheets = new List<Timesheet>
        {
            // Alice: 20h Electrical (no subtask), 10h Electrical (Rough-In)
            new()
            {
                EmployeeId = "emp-1",
                Year = 2026,
                WeekNumber = 1,
                Entries =
                [
                    new TimeEntry { ProjectId = "proj-1", WorkTypeId = "wt-elec", SubTaskId = null, Hours = 20m },
                    new TimeEntry { ProjectId = "proj-1", WorkTypeId = "wt-elec", SubTaskId = "st-rough", Hours = 10m }
                ]
            },
            // Bob: 20h Plumbing
            new()
            {
                EmployeeId = "emp-2",
                Year = 2026,
                WeekNumber = 1,
                Entries =
                [
                    new TimeEntry { ProjectId = "proj-1", WorkTypeId = "wt-plumb", SubTaskId = null, Hours = 20m }
                ]
            }
        };

        var workTypes = new Dictionary<string, WorkType> { [wt1.Id] = wt1, [wt2.Id] = wt2 };
        var employees = new Dictionary<string, Employee> { [emp1.Id] = emp1, [emp2.Id] = emp2 };

        var model = ProjectLaborCalculator.Calculate("proj-1", timesheets, workTypes, employees);

        // Grand total: 20 + 10 + 20 = 50 hours
        Assert.Equal(50m, model.TotalHours);
        Assert.Equal(2, model.TotalContributorsCount);
        Assert.Equal(3, model.ActiveTasksCount);

        // Verify task summaries are sorted descending by TotalHours
        Assert.Equal(3, model.TaskSummaries.Count);
        Assert.True(model.TaskSummaries[0].TotalHours >= model.TaskSummaries[1].TotalHours);
        Assert.True(model.TaskSummaries[1].TotalHours >= model.TaskSummaries[2].TotalHours);

        // Check Electrical (Rough-in)
        var roughInSummary = model.TaskSummaries.FirstOrDefault(s => s.SubTaskName == "Rough-In");
        Assert.NotNull(roughInSummary);
        Assert.Equal("Electrical", roughInSummary.TaskName);
        Assert.Equal(10m, roughInSummary.TotalHours);
        Assert.Equal(20.0m, roughInSummary.Percentage); // 10/50 = 20%
        Assert.Single(roughInSummary.Employees);
        Assert.Equal("Alice", roughInSummary.Employees[0].FirstName);

        // Check Electrical (main)
        var elecMainSummary = model.TaskSummaries.FirstOrDefault(s => s.TaskName == "Electrical" && string.IsNullOrEmpty(s.SubTaskName));
        Assert.NotNull(elecMainSummary);
        Assert.Equal(20m, elecMainSummary.TotalHours);
        Assert.Equal(40.0m, elecMainSummary.Percentage); // 20/50 = 40%

        // Check Plumbing
        var plumbSummary = model.TaskSummaries.FirstOrDefault(s => s.TaskName == "Plumbing");
        Assert.NotNull(plumbSummary);
        Assert.Equal(20m, plumbSummary.TotalHours);
        Assert.Equal(40.0m, plumbSummary.Percentage); // 20/50 = 40%
        Assert.Single(plumbSummary.Employees);
        Assert.Equal("Bob", plumbSummary.Employees[0].FirstName);
    }

    [Fact]
    public void Calculate_DistinctContributors_DeduplicatesEmployeesAcrossWeeks()
    {
        var wt1 = new WorkType { Id = "wt-1", Name = "General" };
        var emp1 = new Employee { Id = "emp-1", FirstName = "Alice", LastName = "Smith" };

        var timesheets = new List<Timesheet>
        {
            new()
            {
                EmployeeId = "emp-1",
                Year = 2026,
                WeekNumber = 1,
                Entries = [new TimeEntry { ProjectId = "proj-1", WorkTypeId = "wt-1", Hours = 10m }]
            },
            new()
            {
                EmployeeId = "emp-1",
                Year = 2026,
                WeekNumber = 2,
                Entries = [new TimeEntry { ProjectId = "proj-1", WorkTypeId = "wt-1", Hours = 15m }]
            }
        };

        var workTypes = new Dictionary<string, WorkType> { [wt1.Id] = wt1 };
        var employees = new Dictionary<string, Employee> { [emp1.Id] = emp1 };

        var model = ProjectLaborCalculator.Calculate("proj-1", timesheets, workTypes, employees);

        Assert.Equal(25m, model.TotalHours);
        Assert.Equal(1, model.TotalContributorsCount);
        Assert.Single(model.AllDistinctEmployees);
        Assert.Equal("Alice Smith", model.AllDistinctEmployees[0].FullName);
    }

    [Theory]
    [InlineData(24.5, "24h30")]
    [InlineData(0.5, "0h30")]
    [InlineData(1.25, "1h15")]
    [InlineData(0, "0h00")]
    [InlineData(10.0, "10h00")]
    [InlineData(7.75, "7h45")]
    [InlineData(-2.5, "-2h30")]
    public void FormatDurationHhMm_FormatsCorrectly(double hoursDouble, string expected)
    {
        var hours = (decimal)hoursDouble;
        var actual = TimesheetFormatHelper.FormatDurationHhMm(hours);
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void Calculate_WeeklyLabor_AggregatesAllTasksPerWeek()
    {
        var wt1 = new WorkType { Id = "wt-1", Name = "Engineering" };
        var wt2 = new WorkType { Id = "wt-2", Name = "Testing" };

        var emp1 = new Employee { Id = "emp-1", FirstName = "Alice", LastName = "Smith" };
        var emp2 = new Employee { Id = "emp-2", FirstName = "Bob", LastName = "Jones" };
        var emp3 = new Employee { Id = "emp-3", FirstName = "Charlie", LastName = "Brown" };

        var timesheets = new List<Timesheet>
        {
            // Week 10: Alice logs 10h wt1, 5h wt2 (total 15h). Bob logs 8h wt1.
            new()
            {
                EmployeeId = "emp-1",
                Year = 2026,
                WeekNumber = 10,
                Entries =
                [
                    new TimeEntry { ProjectId = "proj-1", WorkTypeId = "wt-1", Hours = 10m },
                    new TimeEntry { ProjectId = "proj-1", WorkTypeId = "wt-2", Hours = 5m }
                ]
            },
            new()
            {
                EmployeeId = "emp-2",
                Year = 2026,
                WeekNumber = 10,
                Entries =
                [
                    new TimeEntry { ProjectId = "proj-1", WorkTypeId = "wt-1", Hours = 8m }
                ]
            },
            // Week 11: Charlie logs 4h wt2.
            new()
            {
                EmployeeId = "emp-3",
                Year = 2026,
                WeekNumber = 11,
                Entries =
                [
                    new TimeEntry { ProjectId = "proj-1", WorkTypeId = "wt-2", Hours = 4m }
                ]
            }
        };

        var workTypes = new Dictionary<string, WorkType> { [wt1.Id] = wt1, [wt2.Id] = wt2 };
        var employees = new Dictionary<string, Employee> { [emp1.Id] = emp1, [emp2.Id] = emp2, [emp3.Id] = emp3 };

        var model = ProjectLaborCalculator.Calculate("proj-1", timesheets, workTypes, employees);

        // Grand total: 10 + 5 + 8 + 4 = 27 hours
        Assert.Equal(27m, model.TotalHours);
        Assert.Equal(27m, model.WeeklyLabor.GrandTotal);

        // 2 distinct weeks: Week 11 and Week 10 (sorted descending)
        Assert.Equal(2, model.WeeklyLabor.Rows.Count);
        Assert.Equal(11, model.WeeklyLabor.Rows[0].Week);
        Assert.Equal(10, model.WeeklyLabor.Rows[1].Week);

        // Week 11 row checks
        var w11 = model.WeeklyLabor.Rows[0];
        Assert.Equal(4m, w11.TotalHours);
        Assert.Equal(4m, w11.GetHours("emp-3"));
        Assert.Equal(0m, w11.GetHours("emp-1"));

        // Week 10 row checks: Alice should have 15h across wt1 + wt2, Bob has 8h
        var w10 = model.WeeklyLabor.Rows[1];
        Assert.Equal(23m, w10.TotalHours);
        Assert.Equal(15m, w10.GetHours("emp-1"));
        Assert.Equal(8m, w10.GetHours("emp-2"));
        Assert.Equal(0m, w10.GetHours("emp-3"));

        // Contributor totals across all weeks
        Assert.Equal(15m, model.WeeklyLabor.GetTotalForEmployee("emp-1"));
        Assert.Equal(8m, model.WeeklyLabor.GetTotalForEmployee("emp-2"));
        Assert.Equal(4m, model.WeeklyLabor.GetTotalForEmployee("emp-3"));
    }

    [Fact]
    public void Calculate_WeeklyLabor_AggregatesByTeamCorrectly()
    {
        var wt1 = new WorkType { Id = "wt-1", Name = "Development" };

        var teamDev = new Team { Id = "team-dev", Name = "Developers" };
        var teamQa = new Team { Id = "team-qa", Name = "QA" };

        var emp1 = new Employee { Id = "emp-1", FirstName = "Alice", TeamId = "team-dev" };
        var emp2 = new Employee { Id = "emp-2", FirstName = "Bob", TeamId = "team-dev" };
        var emp3 = new Employee { Id = "emp-3", FirstName = "Charlie", TeamId = "team-qa" };
        var emp4 = new Employee { Id = "emp-4", FirstName = "Dave", TeamId = "" }; // Unassigned

        var timesheets = new List<Timesheet>
        {
            new()
            {
                EmployeeId = "emp-1",
                Year = 2026,
                WeekNumber = 15,
                Entries = [new TimeEntry { ProjectId = "proj-1", WorkTypeId = "wt-1", Hours = 20m }]
            },
            new()
            {
                EmployeeId = "emp-2",
                Year = 2026,
                WeekNumber = 15,
                Entries = [new TimeEntry { ProjectId = "proj-1", WorkTypeId = "wt-1", Hours = 15m }]
            },
            new()
            {
                EmployeeId = "emp-3",
                Year = 2026,
                WeekNumber = 15,
                Entries = [new TimeEntry { ProjectId = "proj-1", WorkTypeId = "wt-1", Hours = 10m }]
            },
            new()
            {
                EmployeeId = "emp-4",
                Year = 2026,
                WeekNumber = 15,
                Entries = [new TimeEntry { ProjectId = "proj-1", WorkTypeId = "wt-1", Hours = 5m }]
            }
        };

        var workTypes = new Dictionary<string, WorkType> { [wt1.Id] = wt1 };
        var employees = new Dictionary<string, Employee>
        {
            [emp1.Id] = emp1,
            [emp2.Id] = emp2,
            [emp3.Id] = emp3,
            [emp4.Id] = emp4
        };
        var teams = new Dictionary<string, Team>
        {
            [teamDev.Id] = teamDev,
            [teamQa.Id] = teamQa
        };

        var model = ProjectLaborCalculator.Calculate("proj-1", timesheets, workTypes, employees, teams);

        Assert.Equal(50m, model.TotalHours);
        Assert.Equal(50m, model.WeeklyLabor.GrandTotal);

        // Teams should contain Developers, QA, and Unassigned
        Assert.Equal(3, model.WeeklyLabor.Teams.Count);

        var devTeamSummary = model.WeeklyLabor.Teams.First(t => t.Id == "team-dev");
        Assert.Equal(2, devTeamSummary.MemberCount);

        var qaTeamSummary = model.WeeklyLabor.Teams.First(t => t.Id == "team-qa");
        Assert.Equal(1, qaTeamSummary.MemberCount);

        var unassignedSummary = model.WeeklyLabor.Teams.First(t => t.Id == string.Empty);
        Assert.Equal(1, unassignedSummary.MemberCount);

        // Check Week 15 team hours:
        // Dev: Alice(20) + Bob(15) = 35
        // QA: Charlie(10) = 10
        // Unassigned: Dave(5) = 5
        var w15 = model.WeeklyLabor.Rows.Single();
        Assert.Equal(35m, w15.GetTeamHours("team-dev"));
        Assert.Equal(10m, w15.GetTeamHours("team-qa"));
        Assert.Equal(5m, w15.GetTeamHours(string.Empty));
        Assert.Equal(50m, w15.TotalHours);

        // Check team grand totals
        Assert.Equal(35m, model.WeeklyLabor.GetTotalForTeam("team-dev"));
        Assert.Equal(10m, model.WeeklyLabor.GetTotalForTeam("team-qa"));
        Assert.Equal(5m, model.WeeklyLabor.GetTotalForTeam(string.Empty));
    }

    [Fact]
    public void CalculateWeeklyLabor_RunningTotal_StartsFromOldestWeekAndAccumulates()
    {
        var emp1 = new Employee { Id = "emp-1", FirstName = "Alice" };
        var employees = new Dictionary<string, Employee> { [emp1.Id] = emp1 };

        var timesheets = new List<Timesheet>
        {
            // Oldest: Week 10 with 10h
            new()
            {
                EmployeeId = "emp-1",
                Year = 2026,
                WeekNumber = 10,
                Entries = [new TimeEntry { ProjectId = "proj-1", Hours = 10m, Date = new DateTime(2026, 3, 2) }]
            },
            // Middle: Week 11 with 15h
            new()
            {
                EmployeeId = "emp-1",
                Year = 2026,
                WeekNumber = 11,
                Entries = [new TimeEntry { ProjectId = "proj-1", Hours = 15m, Date = new DateTime(2026, 3, 9) }]
            },
            // Newest: Week 12 with 5h
            new()
            {
                EmployeeId = "emp-1",
                Year = 2026,
                WeekNumber = 12,
                Entries = [new TimeEntry { ProjectId = "proj-1", Hours = 5m, Date = new DateTime(2026, 3, 16) }]
            }
        };

        var weekly = ProjectLaborCalculator.CalculateWeeklyLabor("proj-1", timesheets, employees);

        // Rows are ordered descending (Week 12, Week 11, Week 10)
        Assert.Equal(3, weekly.Rows.Count);
        Assert.Equal(12, weekly.Rows[0].Week);
        Assert.Equal(11, weekly.Rows[1].Week);
        Assert.Equal(10, weekly.Rows[2].Week);

        // Oldest week (Week 10): 10h
        Assert.Equal(10m, weekly.Rows[2].TotalHours);
        Assert.Equal(10m, weekly.Rows[2].RunningTotal);

        // Next week (Week 11): 10 + 15 = 25h
        Assert.Equal(15m, weekly.Rows[1].TotalHours);
        Assert.Equal(25m, weekly.Rows[1].RunningTotal);

        // Newest week (Week 12): 25 + 5 = 30h
        Assert.Equal(5m, weekly.Rows[0].TotalHours);
        Assert.Equal(30m, weekly.Rows[0].RunningTotal);
        Assert.Equal(30m, weekly.GrandTotal);
    }

    [Fact]
    public void CalculateWeeklyLabor_WithDateRange_FiltersEntriesAccurately()
    {
        var emp1 = new Employee { Id = "emp-1", FirstName = "Alice" };
        var employees = new Dictionary<string, Employee> { [emp1.Id] = emp1 };

        var timesheets = new List<Timesheet>
        {
            new()
            {
                EmployeeId = "emp-1",
                Year = 2026,
                WeekNumber = 10,
                Entries = [new TimeEntry { ProjectId = "proj-1", Hours = 10m, Date = new DateTime(2026, 3, 2) }]
            },
            new()
            {
                EmployeeId = "emp-1",
                Year = 2026,
                WeekNumber = 11,
                Entries = [new TimeEntry { ProjectId = "proj-1", Hours = 15m, Date = new DateTime(2026, 3, 10) }]
            },
            new()
            {
                EmployeeId = "emp-1",
                Year = 2026,
                WeekNumber = 12,
                Entries = [new TimeEntry { ProjectId = "proj-1", Hours = 8m, Date = new DateTime(2026, 3, 25) }]
            }
        };

        // Filter to only include Week 11 (March 10)
        var startDate = new DateTime(2026, 3, 5);
        var endDate = new DateTime(2026, 3, 15);

        var weekly = ProjectLaborCalculator.CalculateWeeklyLabor(
            "proj-1",
            timesheets,
            employees,
            startDate: startDate,
            endDate: endDate);

        Assert.Single(weekly.Rows);
        Assert.Equal(11, weekly.Rows[0].Week);
        Assert.Equal(15m, weekly.Rows[0].TotalHours);
        Assert.Equal(15m, weekly.Rows[0].RunningTotal);
        Assert.Equal(15m, weekly.GrandTotal);
    }

    [Fact]
    public void CalculateWeeklyLabor_WithDateRange_ScopesEmployeesToActiveContributorsInPeriod()
    {
        var emp1 = new Employee { Id = "emp-1", FirstName = "Alice", TeamId = "team-1" };
        var emp2 = new Employee { Id = "emp-2", FirstName = "Bob", TeamId = "team-2" };
        var team1 = new Team { Id = "team-1", Name = "Team A" };
        var team2 = new Team { Id = "team-2", Name = "Team B" };

        var employees = new Dictionary<string, Employee> { [emp1.Id] = emp1, [emp2.Id] = emp2 };
        var teams = new Dictionary<string, Team> { [team1.Id] = team1, [team2.Id] = team2 };

        var timesheets = new List<Timesheet>
        {
            // Alice worked March 2 (Week 10)
            new()
            {
                EmployeeId = "emp-1",
                Year = 2026,
                WeekNumber = 10,
                Entries = [new TimeEntry { ProjectId = "proj-1", Hours = 12m, Date = new DateTime(2026, 3, 2) }]
            },
            // Bob worked April 6 (Week 15)
            new()
            {
                EmployeeId = "emp-2",
                Year = 2026,
                WeekNumber = 15,
                Entries = [new TimeEntry { ProjectId = "proj-1", Hours = 20m, Date = new DateTime(2026, 4, 6) }]
            }
        };

        // Filter strictly to March
        var weeklyMarch = ProjectLaborCalculator.CalculateWeeklyLabor(
            "proj-1",
            timesheets,
            employees,
            teams,
            startDate: new DateTime(2026, 3, 1),
            endDate: new DateTime(2026, 3, 31));

        // In March, only Alice has entries
        Assert.Single(weeklyMarch.Employees);
        Assert.Equal("emp-1", weeklyMarch.Employees[0].Id);

        // However, project teams are preserved
        Assert.Equal(2, weeklyMarch.Teams.Count);

        // Overall Calculate also delegates properly with date range
        var model = ProjectLaborCalculator.Calculate(
            "proj-1",
            timesheets,
            new Dictionary<string, WorkType>(),
            employees,
            teams,
            weeklyStartDate: new DateTime(2026, 3, 1),
            weeklyEndDate: new DateTime(2026, 3, 31));

        Assert.Equal(32m, model.TotalHours); // Total project labor across all time is 32h
        Assert.Equal(12m, model.WeeklyLabor.GrandTotal); // Weekly labor in March is 12h
        Assert.Single(model.WeeklyLabor.Rows);
        Assert.Equal(12m, model.WeeklyLabor.Rows[0].RunningTotal);
    }
}
