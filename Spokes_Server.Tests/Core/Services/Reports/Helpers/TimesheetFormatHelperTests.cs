namespace Spokes_Server.Tests.Core.Services.Reports.Helpers;

using Spokes_Server.Core.Models.Reports;
using Spokes_Server.Core.Services.Reports.Helpers;
using Xunit;

public class TimesheetFormatHelperTests
{
    [Theory]
    // 1. 'h' notation (hours only)
    [InlineData("6h", 6.0)]
    [InlineData("6H", 6.0)]
    [InlineData("6 h", 6.0)]
    [InlineData("8h", 8.0)]
    [InlineData("0h", 0.0)]
    // 2. 'h' + minutes notation
    [InlineData("6h30", 6.5)]
    [InlineData("6H30", 6.5)]
    [InlineData("6h 30", 6.5)]
    [InlineData("6h30m", 6.5)]
    [InlineData("6h 30m", 6.5)]
    [InlineData("6h 30 min", 6.5)]
    [InlineData("6h05", 6.0833)]
    [InlineData("6h5", 6.0833)]
    [InlineData("1h15", 1.25)]
    [InlineData("0h45", 0.75)]
    [InlineData("2h45m", 2.75)]
    // 3. 'h' + decimal
    [InlineData("6.5h", 6.5)]
    [InlineData("6,5h", 6.5)]
    [InlineData("0.5h", 0.5)]
    // 4. Minute-only notation
    [InlineData("30m", 0.5)]
    [InlineData("45 min", 0.75)]
    [InlineData("15m", 0.25)]
    [InlineData("90m", 1.5)]
    [InlineData("90 min", 1.5)]
    // 5. Colon notation
    [InlineData("8:30", 8.5)]
    [InlineData("08:30", 8.5)]
    [InlineData("1:05", 1.0833)]
    [InlineData("0:45", 0.75)]
    [InlineData("8:30h", 8.5)]
    // 6. Decimal notation (dot and comma)
    [InlineData("8.5", 8.5)]
    [InlineData("8,5", 8.5)]
    [InlineData("7.25", 7.25)]
    [InlineData("7,25", 7.25)]
    [InlineData(".5", 0.5)]
    [InlineData(",5", 0.5)]
    // 7. Integer quick format
    [InlineData("8", 8.0)]
    [InlineData("24", 24.0)]
    [InlineData("0", 0.0)]
    [InlineData("830", 8.5)]
    [InlineData("120", 1.3333)]
    [InlineData("85", 1.4167)]
    public void ParseTimeInput_ParsesVariousFormats(string input, double expected)
    {
        var parsed = TimesheetFormatHelper.ParseTimeInput(input);
        Assert.Equal((decimal)expected, Math.Round(parsed, 4));
    }

    [Theory]
    [InlineData("-1h30", -1.5)]
    [InlineData("-6h", -6.0)]
    [InlineData("-1.5", -1.5)]
    [InlineData("-1,5", -1.5)]
    [InlineData("-8:30", -8.5)]
    [InlineData("-30m", -0.5)]
    [InlineData("-8", -8.0)]
    [InlineData("-830", -8.5)]
    public void ParseTimeInput_NegativeValues_ReturnsNegativeDecimalHours(string input, double expected)
    {
        var parsed = TimesheetFormatHelper.ParseTimeInput(input);
        Assert.Equal((decimal)expected, Math.Round(parsed, 4));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("invalid")]
    [InlineData("abc")]
    [InlineData("12:34:56")]
    public void ParseTimeInput_InvalidOrEmptyText_ReturnsZero(string? input)
    {
        var result = TimesheetFormatHelper.ParseTimeInput(input);
        Assert.Equal(0, result);
    }

    [Theory]
    [InlineData(0, "")]
    [InlineData(8.0, "8:00")]
    [InlineData(7.5, "7:30")]
    [InlineData(1.25, "1:15")]
    [InlineData(40.0, "40:00")]
    [InlineData(-1.5, "-1:30")]
    [InlineData(-0.75, "-0:45")]
    [InlineData(-2.0, "-2:00")]
    public void FormatHours_ReturnsExpectedFormat(decimal input, string expected)
    {
        Assert.Equal(expected, TimesheetFormatHelper.FormatHours(input));
    }

    [Theory]
    [InlineData(1.5, TimeDisplayFormat.DurationHhmm, "1:30")]
    [InlineData(1.5, TimeDisplayFormat.DecimalHours, "1.50 h")]
    [InlineData(0.25, TimeDisplayFormat.DurationHhmm, "0:15")]
    [InlineData(0.0, TimeDisplayFormat.DurationHhmm, "0:00")]
    [InlineData(-2.5, TimeDisplayFormat.DurationHhmm, "-2:30")]
    public void FormatDuration_FormatsAccurately(decimal hours, TimeDisplayFormat format, string expected)
    {
        var result = TimesheetFormatHelper.FormatDuration(hours, format);
        Assert.Equal(expected, result);
    }

    [Fact]
    public void FormatCurrency_AppendsCustomSymbol()
    {
        var result = TimesheetFormatHelper.FormatCurrency(1500.75m, "CAD");
        Assert.Equal("1,500.75 CAD", result);
    }
}
