namespace Spokes_Server.Core.Models.HR;

using Spokes_Server.Core.Models.Core;
using Spokes_Server.Core.Models.Projects;
using Spokes_Server.Core.Models.Accounting;
using Spokes_Server.Core.Models.Communication;

public class TimesheetUiRow
{
    public string ProjectId { get; set; } = "";
    public string WorkTypeId { get; set; } = "";
    public string SubTaskId { get; set; } = "";
    public decimal[] DailyHours { get; set; } = new decimal[7]; // Index 0 = Mon, 6 = Sun
    public string[] DailyNotes { get; set; } = new string[7];   // Notes per day, matching DailyHours indices
    public string?[] EntryIds { get; set; } = new string?[7];   // Original TimeEntry.Id per day to preserve billing links
}

public class TimesheetDayEntryResult
{
    public bool IsDelete { get; set; }
    public string ProjectId { get; set; } = "";
    public string WorkTypeId { get; set; } = "";
    public string SubTaskId { get; set; } = "";
    public decimal Hours { get; set; }
    public string Note { get; set; } = "";
}




