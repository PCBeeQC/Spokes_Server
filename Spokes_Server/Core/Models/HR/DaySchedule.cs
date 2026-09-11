namespace Spokes_Server.Core.Models.HR;

/// <summary>
/// Represents the notification active window for a single day of the week.
/// </summary>
public class DaySchedule
{
    public DayOfWeek Day { get; set; }

    /// <summary>Whether notifications are allowed on this day.</summary>
    public bool IsEnabled { get; set; } = true;

    /// <summary>Start hour (0-23, inclusive) in server local time.</summary>
    public int StartHour { get; set; } = 8;

    /// <summary>End hour (0-23, exclusive upper bound) in server local time.</summary>
    public int EndHour { get; set; } = 17;
}
