using System;
using System.Text.Json;
using Spokes_Server.Core.Models.HR;
using Xunit;

namespace Spokes_Server.Tests.Core.Models.HR;

public class EmployeeWorkScheduleTests
{
    [Fact]
    public void EmployeeWorkSchedule_Properties_WorkCorrectly()
    {
        var schedule = new EmployeeWorkSchedule
        {
            Id = "sched_1",
            StartDate = new DateOnly(2026, 1, 1),
            EndDate = new DateOnly(2026, 12, 31),
            WeeklyHours = 37.5m,
            Note = "Standard full-time contract"
        };

        Assert.Equal("sched_1", schedule.Id);
        Assert.Equal(new DateOnly(2026, 1, 1), schedule.StartDate);
        Assert.Equal(new DateOnly(2026, 12, 31), schedule.EndDate);
        Assert.Equal(37.5m, schedule.WeeklyHours);
        Assert.Equal("Standard full-time contract", schedule.Note);

        var json = JsonSerializer.Serialize(schedule);
        var copy = JsonSerializer.Deserialize<EmployeeWorkSchedule>(json);
        Assert.NotNull(copy);
        Assert.Equal(37.5m, copy.WeeklyHours);
    }
}
