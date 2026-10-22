using System.Text.Json;
using Spokes_Server.Components.Pages.HR;
using Xunit;

namespace Spokes_Server.Tests.Components.Pages.HR;

public class TimesheetUiRowTests
{
    [Fact]
    public void TimesheetUiRow_Properties_WorkCorrectly()
    {
        var row = new TimesheetUiRow
        {
            ProjectId = "proj_1",
            WorkTypeId = "wt_1",
            SubTaskId = "sub_1",
            DailyHours = new decimal[] { 8m, 8m, 8m, 8m, 8m, 0m, 0m },
            DailyNotes = new string[] { "Mon", "Tue", "Wed", "Thu", "Fri", "", "" }
        };

        Assert.Equal("proj_1", row.ProjectId);
        Assert.Equal("wt_1", row.WorkTypeId);
        Assert.Equal("sub_1", row.SubTaskId);
        Assert.Equal(8m, row.DailyHours[0]);
        Assert.Equal(7, row.DailyHours.Length);

        var result = new TimesheetDayEntryResult
        {
            IsDelete = false,
            ProjectId = "proj_1",
            WorkTypeId = "wt_1",
            SubTaskId = "sub_1",
            Hours = 7.5m,
            Note = "Sprint planning"
        };

        Assert.False(result.IsDelete);
        Assert.Equal(7.5m, result.Hours);
        Assert.Equal("Sprint planning", result.Note);

        var json = JsonSerializer.Serialize(row);
        var copy = JsonSerializer.Deserialize<TimesheetUiRow>(json);
        Assert.NotNull(copy);
        Assert.Equal("proj_1", copy.ProjectId);
    }
}
