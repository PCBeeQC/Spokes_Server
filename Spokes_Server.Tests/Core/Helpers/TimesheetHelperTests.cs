namespace Spokes_Server.Tests.Core.Helpers;

using MudBlazor;
using Spokes_Server.Components.UI;
using Spokes_Server.Core.Helpers;
using Spokes_Server.Core.Services.Reports.Helpers;
using Spokes_Server.Core.Models.HR;
using Spokes_Server.Core.Models.Reports;
using Xunit;

public class TimesheetHelperTests
{
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

    [Fact]
    public void CalculateKPISummary_CalculatesTotalsAndCounts()
    {
        var entries = new List<TimesheetReportEntry>
        {
            new()
            {
                EmployeeId = "emp1",
                ProjectId = "proj1",
                HourlyRate = 100m,
                CostRate = 40m,
                Entry = new TimeEntry { Hours = 6.0m, IsBillable = true }
            },
            new()
            {
                EmployeeId = "emp2",
                ProjectId = "proj2",
                HourlyRate = 120m,
                CostRate = 50m,
                Entry = new TimeEntry { Hours = 4.0m, IsBillable = false }
            }
        };

        var kpi = TimesheetCalculationHelper.CalculateKPISummary(entries);

        Assert.Equal(10.0m, kpi.TotalHours);
        Assert.Equal(6.0m, kpi.BillableHours);
        Assert.Equal(4.0m, kpi.NonBillableHours);
        Assert.Equal(60.0m, kpi.BillablePercentage);
        Assert.Equal(600.0m, kpi.BillableAmount);
        Assert.Equal(440.0m, kpi.CostAmount); // 6*40 + 4*50
        Assert.Equal(160.0m, kpi.ProfitAmount); // 600 - 440
        Assert.Equal(2, kpi.ActiveProjectsCount);
        Assert.Equal(2, kpi.ActiveEmployeesCount);
        Assert.Equal(2, kpi.EntriesCount);
    }

    [Fact]
    public void BuildSummaryGroups_GroupsByProjectAndSubGroups()
    {
        var entries = new List<TimesheetReportEntry>
        {
            new()
            {
                ProjectName = "Alpha",
                ClientId = "Client A",
                HourlyRate = 100m,
                Entry = new TimeEntry { Description = "Task A", Hours = 5.0m, IsBillable = true }
            },
            new()
            {
                ProjectName = "Alpha",
                ClientId = "Client A",
                HourlyRate = 100m,
                Entry = new TimeEntry { Description = "Task B", Hours = 3.0m, IsBillable = true }
            }
        };

        var groups = TimesheetCalculationHelper.BuildSummaryGroups(entries, ReportGroupBy.Project, ReportSubGroupBy.Description);

        Assert.Single(groups);
        var alpha = groups[0];
        Assert.Equal("Alpha", alpha.Key);
        Assert.Equal("Client A", alpha.Subtitle);
        Assert.Equal(8.0m, alpha.TotalHours);
        Assert.Equal(2, alpha.Subgroups.Count);
    }

    [Fact]
    public void BuildWeeklyMatrix_PopulatesWeekRowsAndTotals()
    {
        var monday = new DateTime(2026, 9, 21);
        var entries = new List<TimesheetReportEntry>
        {
            new()
            {
                EmployeeId = "emp1",
                EmployeeName = "Bob",
                HourlyRate = 80m,
                Entry = new TimeEntry { Date = monday, Hours = 8.0m, IsBillable = true }
            },
            new()
            {
                EmployeeId = "emp1",
                EmployeeName = "Bob",
                HourlyRate = 80m,
                Entry = new TimeEntry { Date = monday.AddDays(2), Hours = 4.0m, IsBillable = true }
            }
        };

        var matrix = TimesheetCalculationHelper.BuildWeeklyMatrix(entries, monday, WeeklyGroupBy.Employee);

        Assert.Equal(monday.Date, matrix.WeekStartDate.Date);
        Assert.Single(matrix.Rows);
        Assert.Equal(12.0m, matrix.GrandTotalHours);
        Assert.Equal(8.0m, matrix.DayTotals[0]); // Monday
        Assert.Equal(0.0m, matrix.DayTotals[1]); // Tuesday
        Assert.Equal(4.0m, matrix.DayTotals[2]); // Wednesday
    }

    [Fact]
    public void DateRangeHelper_GetPresetAndShift_WorksCorrectly()
    {
        var thisWeek = TimesheetDateRangeHelper.GetPresetDateRange("This Week");
        Assert.NotNull(thisWeek.Start);
        Assert.NotNull(thisWeek.End);
        Assert.Equal(DayOfWeek.Monday, thisWeek.Start!.Value.DayOfWeek);
        Assert.Equal(DayOfWeek.Sunday, thisWeek.End!.Value.DayOfWeek);

        var shifted = TimesheetDateRangeHelper.ShiftDateRange(thisWeek, 1);
        Assert.Equal(thisWeek.Start.Value.AddDays(7), shifted.Start);
        Assert.Equal(thisWeek.End.Value.AddDays(7), shifted.End);
    }

    [Fact]
    public void CsvExportHelper_SanitizesFormulaInjection()
    {
        var entries = new List<TimesheetReportEntry>
        {
            new()
            {
                EmployeeName = "=cmd|' /C calc'!A0",
                ProjectName = "+12345",
                TaskName = "Dev",
                Entry = new TimeEntry { Date = new DateTime(2026, 9, 1), Hours = 1.0m, Description = "@payload" }
            }
        };

        var csv = TimesheetCsvExportHelper.GenerateDetailedCsv(entries, TimeDisplayFormat.DecimalHours);

        Assert.Contains("'=cmd|' /C calc'!A0", csv);
        Assert.Contains("'+12345", csv);
        Assert.Contains("'@payload", csv);
    }
}
