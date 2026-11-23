using System.Collections.Generic;
using Spokes_Server.Core.Models.HR;

namespace Spokes_Server.Tests.Core.Models.HR;

public class WeeklyPlanTests
{
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

}
