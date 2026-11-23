using System.Text.Json;
using Spokes_Server.Components.Pages.HR;
using Xunit;

namespace Spokes_Server.Tests.Components.Pages.HR;

public class TimesheetUiRowTests
{
    [Fact]
    public void FromTimesheet_And_ToTimeEntries_PreservesRowsWithoutWorkType_And_ReusesIds()
    {
        var weekStart = new DateTime(2025, 3, 3); // Monday
        var sheet = new Spokes_Server.Core.Models.HR.Timesheet
        {
            Id = "ts-clockify",
            EmployeeId = "emp-1",
            Year = 2025,
            WeekNumber = 10,
            Status = Spokes_Server.Core.Models.HR.TimesheetStatus.Approved,
            Entries =
            [
                new Spokes_Server.Core.Models.HR.TimeEntry
                {
                    Id = "clockify-entry-mon",
                    Date = new DateTime(2025, 3, 3, 14, 30, 0), // Non-midnight timestamp
                    ProjectId = "proj-legacy",
                    WorkTypeId = "", // Clockify migrated entry without task
                    SubTaskId = null,
                    Hours = 7.5m,
                    Description = "Imported from Clockify",
                    IsBillable = false
                },
                new Spokes_Server.Core.Models.HR.TimeEntry
                {
                    Id = "clockify-entry-wed",
                    Date = new DateTime(2025, 3, 5, 9, 0, 0), // Wednesday with time component
                    ProjectId = "proj-legacy",
                    WorkTypeId = "",
                    SubTaskId = null,
                    Hours = 4m,
                    Description = "Wednesday session",
                    IsBillable = false
                }
            ]
        };

        var rows = TimesheetUiRow.FromTimesheet(sheet, weekStart);
        Assert.Single(rows);
        Assert.Equal("proj-legacy", rows[0].ProjectId);
        Assert.Equal("", rows[0].WorkTypeId);
        Assert.False(rows[0].IsBillable);
        Assert.Equal(7.5m, rows[0].DailyHours[0]); // Monday
        Assert.Equal(4m, rows[0].DailyHours[2]);   // Wednesday

        // Simulate editing ProjectId on the row without selecting a WorkTypeId
        rows[0].ProjectId = "proj-corrected";

        var savedEntries = TimesheetUiRow.ToTimeEntries(rows, weekStart, sheet.Entries);
        Assert.Equal(2, savedEntries.Count);
        Assert.All(savedEntries, e =>
        {
            Assert.Equal("proj-corrected", e.ProjectId);
            Assert.Equal("", e.WorkTypeId);
            Assert.False(e.IsBillable);
        });
        Assert.Contains(savedEntries, e => e.Date == weekStart && e.Hours == 7.5m && e.Description == "Imported from Clockify");
        Assert.Contains(savedEntries, e => e.Date == weekStart.AddDays(2) && e.Hours == 4m && e.Description == "Wednesday session");
    }
}
