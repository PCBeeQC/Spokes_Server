using Spokes_Server.Core.Models.HR;

namespace Spokes_Server.Tests.Core.Models.HR;

public class DayScheduleTests
{
    [Fact]
    public void DaySchedule_Initialization_SetsDefaults()
    {
        // Act
        var schedule = new DaySchedule();

        // Assert
        Assert.Equal(DayOfWeek.Sunday, schedule.Day);
        Assert.True(schedule.IsEnabled);
        Assert.Equal(8, schedule.StartHour);
        Assert.Equal(17, schedule.EndHour);
    }

    [Fact]
    public void DaySchedule_PropertyMutations_UpdatesAndReadsCorrectly()
    {
        // Arrange
        var schedule = new DaySchedule();

        // Act
        schedule.Day = DayOfWeek.Monday;
        schedule.IsEnabled = false;
        schedule.StartHour = 9;
        schedule.EndHour = 18;

        // Assert
        Assert.Equal(DayOfWeek.Monday, schedule.Day);
        Assert.False(schedule.IsEnabled);
        Assert.Equal(9, schedule.StartHour);
        Assert.Equal(18, schedule.EndHour);
    }
}
