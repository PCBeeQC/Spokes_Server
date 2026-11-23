namespace Spokes_Server.Tests.Localization;

using System;
using System.Globalization;
using Spokes_Server.Core.Constants;
using Spokes_Server.Core.Extensions;
using Spokes_Server.Core.Models.Accounting;
using Spokes_Server.Core.Models.Core;
using Spokes_Server.Core.Models.Documents;
using Spokes_Server.Core.Services.UI;
using Xunit;

public class DateFormattingTests : IDisposable
{
    private readonly CultureInfo _originalCulture;
    private readonly CultureInfo? _originalDefaultThreadCulture;

    public DateFormattingTests()
    {
        _originalCulture = CultureInfo.CurrentCulture;
        _originalDefaultThreadCulture = CultureInfo.DefaultThreadCurrentCulture;
    }

    public void Dispose()
    {
        CultureInfo.CurrentCulture = _originalCulture;
        CultureInfo.DefaultThreadCurrentCulture = _originalDefaultThreadCulture;
    }

    [Fact]
    public void DateDisplayFormats_AvailableFormats_ContainsStandardPresets()
    {
        Assert.Contains(DateDisplayFormats.AvailableFormats, f => f.Format == "yyyy-MM-dd");
        Assert.Contains(DateDisplayFormats.AvailableFormats, f => f.Format == "dd/MM/yyyy");
        Assert.Contains(DateDisplayFormats.AvailableFormats, f => f.Format == "MM/dd/yyyy");
        Assert.Contains(DateDisplayFormats.AvailableFormats, f => f.Format == "dd.MM.yyyy");
        Assert.Contains(DateDisplayFormats.AvailableFormats, f => f.Format == "dd MMM yyyy");
        Assert.Contains(DateDisplayFormats.AvailableFormats, f => f.Format == "MMM dd, yyyy");
    }

    [Theory]
    [InlineData(null, DateDisplayFormats.Iso)]
    [InlineData("", DateDisplayFormats.Iso)]
    [InlineData("   ", DateDisplayFormats.Iso)]
    [InlineData("invalid-format", DateDisplayFormats.Iso)]
    [InlineData("yyyy-MM-dd", "yyyy-MM-dd")]
    [InlineData("dd/MM/yyyy", "dd/MM/yyyy")]
    [InlineData("MM/dd/yyyy", "MM/dd/yyyy")]
    [InlineData("dd.MM.yyyy", "dd.MM.yyyy")]
    public void DateDisplayFormats_Normalize_ResolvesExpectedFormat(string? input, string expected)
    {
        Assert.Equal(expected, DateDisplayFormats.Normalize(input));
    }

    [Theory]
    [InlineData("yyyy-MM-dd", 2026, 10, 14, "2026-10-14")]
    [InlineData("dd/MM/yyyy", 2026, 10, 14, "14/10/2026")]
    [InlineData("MM/dd/yyyy", 2026, 10, 14, "10/14/2026")]
    [InlineData("dd.MM.yyyy", 2026, 10, 14, "14.10.2026")]
    public void CultureCircuitHandler_ApplyDateFormat_UpdatesShortDateStringOutput(string format, int year, int month, int day, string expectedOutput)
    {
        CultureCircuitHandler.ApplyDateFormat(format);

        var testDate = new DateTime(year, month, day);
        Assert.Equal(expectedOutput, testDate.ToShortDateString());
        Assert.Equal(expectedOutput, testDate.ToString("d"));
    }

}
