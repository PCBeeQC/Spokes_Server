using System;
using System.Collections.Generic;

namespace Spokes_Server.Components.UI;

/// <summary>
/// Represents a single day cell within a rendered calendar month grid.
/// </summary>
public sealed record CalendarMonthDay(DateOnly Date, bool IsCurrentMonth);

/// <summary>
/// Represents a precomputed calendar month grid pane (4, 5, or 6 full Monday-to-Sunday weeks).
/// </summary>
public sealed record CalendarMonthPane(DateOnly Month, IReadOnlyList<CalendarMonthDay> Days)
{
    public DateOnly FirstDate => Days.Count > 0 ? Days[0].Date : Month;
    public DateOnly LastDate => Days.Count > 0 ? Days[^1].Date : Month;
}

/// <summary>
/// Pure calendar month grid math shared across month-swiping calendar surfaces.
/// </summary>
public static class CalendarMonthGridHelper
{
    /// <summary>
    /// Normalizes a date to the first day of its month.
    /// </summary>
    public static DateOnly NormalizeMonth(DateOnly date) => new(date.Year, date.Month, 1);

    /// <summary>
    /// Builds a Monday-first month grid covering all days of the target month in complete 7-day weeks.
    /// </summary>
    public static List<CalendarMonthDay> BuildMonthDays(DateOnly month)
    {
        var firstDayOfMonth = NormalizeMonth(month);
        var lastDayOfMonth = firstDayOfMonth.AddMonths(1).AddDays(-1);

        int diff = (7 + (firstDayOfMonth.DayOfWeek - DayOfWeek.Monday)) % 7;
        var runner = firstDayOfMonth.AddDays(-diff);

        var days = new List<CalendarMonthDay>(42);
        while (runner <= lastDayOfMonth || runner.DayOfWeek != DayOfWeek.Monday)
        {
            days.Add(new CalendarMonthDay(runner, runner.Month == firstDayOfMonth.Month));
            runner = runner.AddDays(1);
        }

        return days;
    }

    /// <summary>
    /// Builds the [Previous, Current, Next] month panes and the inclusive date window covering all three.
    /// </summary>
    public static (CalendarMonthPane Prev, CalendarMonthPane Current, CalendarMonthPane Next, DateOnly WindowStart, DateOnly WindowEnd)
        BuildThreeMonthWindow(DateOnly currentMonth)
    {
        var center = NormalizeMonth(currentMonth);
        var prevMonth = center.AddMonths(-1);
        var nextMonth = center.AddMonths(1);

        var prev = new CalendarMonthPane(prevMonth, BuildMonthDays(prevMonth));
        var current = new CalendarMonthPane(center, BuildMonthDays(center));
        var next = new CalendarMonthPane(nextMonth, BuildMonthDays(nextMonth));

        return (prev, current, next, prev.FirstDate, next.LastDate);
    }
}
