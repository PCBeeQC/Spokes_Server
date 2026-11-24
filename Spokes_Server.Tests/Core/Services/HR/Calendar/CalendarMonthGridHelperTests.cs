using System;
using System.Linq;
using Spokes_Server.Components.UI;
using Xunit;

namespace Spokes_Server.Tests.Core.Services.HR.Calendar;

public class CalendarMonthGridHelperTests
{
    [Fact]
    public void NormalizeMonth_AlwaysReturnsFirstDayOfMonth()
    {
        var normalized = CalendarMonthGridHelper.NormalizeMonth(new DateOnly(2026, 8, 27));
        Assert.Equal(new DateOnly(2026, 8, 1), normalized);
    }

    [Theory]
    [InlineData(2021, 2, 28, "2021-02-01", "2021-02-28")] // 4-week exact month (Mon Feb 1 -> Sun Feb 28)
    [InlineData(2026, 5, 35, "2026-04-27", "2026-05-31")] // 5-week month
    [InlineData(2026, 8, 42, "2026-07-27", "2026-09-06")] // 6-week month (Sat Aug 1, 31 days)
    [InlineData(2024, 2, 35, "2024-01-29", "2024-03-03")] // Leap year February
    public void BuildMonthDays_ProducesCompleteMondayToSundayWeeks(
        int year,
        int month,
        int expectedCellCount,
        string expectedFirstDate,
        string expectedLastDate)
    {
        var days = CalendarMonthGridHelper.BuildMonthDays(new DateOnly(year, month, 15));

        Assert.Equal(expectedCellCount, days.Count);
        Assert.Equal(0, days.Count % 7);
        Assert.Equal(DateOnly.Parse(expectedFirstDate), days[0].Date);
        Assert.Equal(DayOfWeek.Monday, days[0].Date.DayOfWeek);
        Assert.Equal(DateOnly.Parse(expectedLastDate), days[^1].Date);
        Assert.Equal(DayOfWeek.Sunday, days[^1].Date.DayOfWeek);

        // Verify strictly consecutive dates and accurate IsCurrentMonth flags
        for (int i = 0; i < days.Count; i++)
        {
            if (i > 0)
            {
                Assert.Equal(days[i - 1].Date.AddDays(1), days[i].Date);
            }
            Assert.Equal(days[i].Date.Month == month && days[i].Date.Year == year, days[i].IsCurrentMonth);
        }

        Assert.Equal(DateTime.DaysInMonth(year, month), days.Count(d => d.IsCurrentMonth));
    }

    [Fact]
    public void BuildThreeMonthWindow_HandlesYearBoundaryAndBoundingDates()
    {
        var (prev, current, next, windowStart, windowEnd) =
            CalendarMonthGridHelper.BuildThreeMonthWindow(new DateOnly(2025, 12, 19));

        Assert.Equal(new DateOnly(2025, 11, 1), prev.Month);
        Assert.Equal(new DateOnly(2025, 12, 1), current.Month);
        Assert.Equal(new DateOnly(2026, 1, 1), next.Month);

        Assert.Equal(prev.FirstDate, windowStart);
        Assert.Equal(next.LastDate, windowEnd);
        Assert.True(windowStart < current.FirstDate);
        Assert.True(windowEnd > current.LastDate);
        Assert.Contains(next.Days, d => d.Date == new DateOnly(2026, 1, 1) && d.IsCurrentMonth);
    }
}
