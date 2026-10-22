using System;
using MudBlazor;

namespace Spokes_Server.Core.Services.Reports.Helpers;

/// <summary>
/// Helper for DateRange presets and directional range shifting.
/// </summary>
public static class TimesheetDateRangeHelper
{
    public static DateRange GetPresetDateRange(string preset)
    {
        var now = DateTime.Now;

        return preset switch
        {
            "Today" => new DateRange(now.Date, now.Date),
            "Yesterday" => new DateRange(now.Date.AddDays(-1), now.Date.AddDays(-1)),
            "This Week" => GetWeekRange(now),
            "Last Week" => GetWeekRange(now.AddDays(-7)),
            "This Month" => new DateRange(new DateTime(now.Year, now.Month, 1), new DateTime(now.Year, now.Month, DateTime.DaysInMonth(now.Year, now.Month))),
            "Last Month" => GetLastMonthRange(now),
            "This Year" => new DateRange(new DateTime(now.Year, 1, 1), new DateTime(now.Year, 12, 31)),
            "Last Year" => new DateRange(new DateTime(now.Year - 1, 1, 1), new DateTime(now.Year - 1, 12, 31)),
            _ => GetWeekRange(now)
        };
    }

    public static DateRange ShiftDateRange(DateRange currentRange, int direction)
    {
        if (currentRange.Start == null || currentRange.End == null)
        {
            return GetPresetDateRange("This Week");
        }

        var start = currentRange.Start.Value;
        var end = currentRange.End.Value;
        var totalDays = (end - start).TotalDays + 1;

        if (totalDays <= 7)
        {
            return new DateRange(start.AddDays(7 * direction), end.AddDays(7 * direction));
        }
        else if (totalDays >= 28 && totalDays <= 31)
        {
            var newStart = start.AddMonths(direction);
            var daysInMonth = DateTime.DaysInMonth(newStart.Year, newStart.Month);
            return new DateRange(new DateTime(newStart.Year, newStart.Month, 1), new DateTime(newStart.Year, newStart.Month, daysInMonth));
        }
        else
        {
            return new DateRange(start.AddDays((int)totalDays * direction), end.AddDays((int)totalDays * direction));
        }
    }

    public static DateRange GetWeekRange(DateTime date)
    {
        var diff = (7 + (date.DayOfWeek - DayOfWeek.Monday)) % 7;
        var monday = date.Date.AddDays(-diff);
        return new DateRange(monday, monday.AddDays(6));
    }

    public static DateRange GetLastMonthRange(DateTime date)
    {
        var prev = date.AddMonths(-1);
        var days = DateTime.DaysInMonth(prev.Year, prev.Month);
        return new DateRange(new DateTime(prev.Year, prev.Month, 1), new DateTime(prev.Year, prev.Month, days));
    }
}
