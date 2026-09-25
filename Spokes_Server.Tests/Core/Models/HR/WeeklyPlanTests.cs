using System.Collections.Generic;
using Spokes_Server.Core.Models.HR;

namespace Spokes_Server.Tests.Core.Models.HR;

public class WeeklyPlanTests
{
    [Fact]
    public void WeeklyPlan_Initialization_SetsExpectedDefaults()
    {
        // Act
        var plan = new WeeklyPlan();

        // Assert
        Assert.False(string.IsNullOrWhiteSpace(plan.Id));
        Assert.True(Guid.TryParse(plan.Id, out var parsedGuid));
        Assert.NotEqual(Guid.Empty, parsedGuid);
        Assert.Equal(string.Empty, plan.EmployeeId);
        Assert.Equal(string.Empty, plan.EmployeeName);
        Assert.Equal(0, plan.Year);
        Assert.Equal(0, plan.WeekNumber);
        Assert.NotNull(plan.Entries);
        Assert.Empty(plan.Entries);
        Assert.Equal(0m, plan.TotalHours);
    }

    [Fact]
    public void PlanEntry_Initialization_SetsExpectedDefaults()
    {
        // Act
        var entry = new PlanEntry();

        // Assert
        Assert.False(string.IsNullOrWhiteSpace(entry.Id));
        Assert.True(Guid.TryParse(entry.Id, out var parsedGuid));
        Assert.NotEqual(Guid.Empty, parsedGuid);
        Assert.Equal(string.Empty, entry.ProjectId);
        Assert.Equal(string.Empty, entry.ProjectName);
        Assert.Equal(string.Empty, entry.WorkTypeId);
        Assert.Equal(string.Empty, entry.SubTaskId);
        Assert.Equal(string.Empty, entry.Note);
        Assert.Equal(0m, entry.Hours);
    }

    [Fact]
    public void TotalHours_EmptyEntries_ReturnsZero()
    {
        // Arrange
        var plan = new WeeklyPlan { Entries = [] };

        // Act & Assert
        Assert.Equal(0m, plan.TotalHours);
    }

    [Fact]
    public void TotalHours_WithEntries_CalculatesSumCorrectly()
    {
        // Arrange
        var plan = new WeeklyPlan
        {
            Entries =
            [
                new() { Hours = 4.5m },
                new() { Hours = 3.25m },
                new() { Hours = 2.25m }
            ]
        };

        // Act & Assert
        Assert.Equal(10.0m, plan.TotalHours);
    }

    [Fact]
    public void WeeklyPlan_PropertyMutations_UpdatesAndReadsProperties()
    {
        // Arrange
        var plan = new WeeklyPlan();
        List<PlanEntry> entries =
        [
            new() { ProjectId = "proj-1", Hours = 8m }
        ];

        // Act
        plan.Id = "custom-plan-id";
        plan.EmployeeId = "emp-101";
        plan.EmployeeName = "Jane Doe";
        plan.Year = 2026;
        plan.WeekNumber = 38;
        plan.Entries = entries;

        // Assert
        Assert.Equal("custom-plan-id", plan.Id);
        Assert.Equal("emp-101", plan.EmployeeId);
        Assert.Equal("Jane Doe", plan.EmployeeName);
        Assert.Equal(2026, plan.Year);
        Assert.Equal(38, plan.WeekNumber);
        Assert.Same(entries, plan.Entries);
        Assert.Equal(8m, plan.TotalHours);
    }

    [Fact]
    public void PlanEntry_PropertyMutations_UpdatesAndReadsProperties()
    {
        // Arrange
        var entry = new PlanEntry();

        // Act
        entry.Id = "custom-entry-id";
        entry.ProjectId = "proj-100";
        entry.ProjectName = "Automation Hub";
        entry.WorkTypeId = "wt-dev";
        entry.SubTaskId = "sub-42";
        entry.Note = "Refactoring services";
        entry.Hours = 7.5m;

        // Assert
        Assert.Equal("custom-entry-id", entry.Id);
        Assert.Equal("proj-100", entry.ProjectId);
        Assert.Equal("Automation Hub", entry.ProjectName);
        Assert.Equal("wt-dev", entry.WorkTypeId);
        Assert.Equal("sub-42", entry.SubTaskId);
        Assert.Equal("Refactoring services", entry.Note);
        Assert.Equal(7.5m, entry.Hours);
    }

    [Fact]
    public void Equals_SameReference_ReturnsTrue()
    {
        // Arrange
        var plan = new WeeklyPlan();

        // Act & Assert
        Assert.True(plan.Equals(plan));
    }

    [Fact]
    public void Equals_SameId_ReturnsTrue()
    {
        // Arrange
        var sharedId = Guid.NewGuid().ToString();
        var plan1 = new WeeklyPlan { Id = sharedId, EmployeeId = "emp-1" };
        var plan2 = new WeeklyPlan { Id = sharedId, EmployeeId = "emp-2" };

        // Act & Assert
        Assert.True(plan1.Equals(plan2));
        Assert.True(plan2.Equals(plan1));
    }

    [Fact]
    public void Equals_DifferentId_ReturnsFalse()
    {
        // Arrange
        var plan1 = new WeeklyPlan { Id = "plan-1" };
        var plan2 = new WeeklyPlan { Id = "plan-2" };

        // Act & Assert
        Assert.False(plan1.Equals(plan2));
        Assert.False(plan2.Equals(plan1));
    }

    [Fact]
    public void Equals_Null_ReturnsFalse()
    {
        // Arrange
        var plan = new WeeklyPlan();

        // Act & Assert
        Assert.False(plan.Equals(null));
    }

    [Fact]
    public void Equals_DifferentType_ReturnsFalse()
    {
        // Arrange
        var plan = new WeeklyPlan();
        var other = new object();

        // Act & Assert
        Assert.False(plan.Equals(other));
        Assert.False(plan.Equals("plan-string"));
    }

    [Fact]
    public void GetHashCode_SameId_ReturnsMatchingHashCode()
    {
        // Arrange
        var id = "consistent-plan-id-123";
        var plan1 = new WeeklyPlan { Id = id };
        var plan2 = new WeeklyPlan { Id = id };

        // Act & Assert
        Assert.Equal(plan1.GetHashCode(), plan2.GetHashCode());
        Assert.Equal(id.GetHashCode(), plan1.GetHashCode());
    }

    [Fact]
    public void GetHashCode_NullId_FallsBackWithoutThrowingException()
    {
        // Arrange
        var plan = new WeeklyPlan { Id = null! };

        // Act
        var ex = Record.Exception(() => plan.GetHashCode());

        // Assert
        Assert.Null(ex);
        Assert.IsType<int>(plan.GetHashCode());
    }
}
