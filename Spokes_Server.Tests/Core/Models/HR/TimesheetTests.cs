using System.Collections.Generic;
using Spokes_Server.Core.Models.HR;

namespace Spokes_Server.Tests.Core.Models.HR;

public class TimesheetTests
{
    [Fact]
    public void TotalHours_CalculatesSumOfEntryHours()
    {
        var sheet = new Timesheet();
        Assert.Equal(0m, sheet.TotalHours);

        sheet.Entries.Add(new TimeEntry { Hours = 4.5m });
        sheet.Entries.Add(new TimeEntry { Hours = 3.25m });
        sheet.Entries.Add(new TimeEntry { Hours = 1.75m });

        Assert.Equal(9.5m, sheet.TotalHours);
    }

}
