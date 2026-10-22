using System;
using System.Text.Json;
using Spokes_Server.Core.Models.HR;
using Xunit;

namespace Spokes_Server.Tests.Core.Models.HR;

public class TimerStateTests
{
    [Fact]
    public void TimerState_Properties_WorkCorrectly()
    {
        var now = DateTime.UtcNow;
        var timer = new TimerState
        {
            EmployeeId = "emp_1",
            ProjectId = "proj_1",
            WorkTypeId = "wt_dev",
            SubTaskId = "sub_frontend",
            StartedAt = now,
            IsRunning = true,
            Note = "Implementing responsive dashboard"
        };

        Assert.Equal("emp_1", timer.EmployeeId);
        Assert.Equal("proj_1", timer.ProjectId);
        Assert.Equal("wt_dev", timer.WorkTypeId);
        Assert.Equal("sub_frontend", timer.SubTaskId);
        Assert.Equal(now, timer.StartedAt);
        Assert.True(timer.IsRunning);
        Assert.Equal("Implementing responsive dashboard", timer.Note);

        var json = JsonSerializer.Serialize(timer);
        var copy = JsonSerializer.Deserialize<TimerState>(json);
        Assert.NotNull(copy);
        Assert.Equal(timer.EmployeeId, copy.EmployeeId);
        Assert.True(copy.IsRunning);
    }
}
