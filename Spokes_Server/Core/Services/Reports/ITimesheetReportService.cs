namespace Spokes_Server.Core.Services.Reports;

using MudBlazor;
using Spokes_Server.Core.Helpers;
using Spokes_Server.Core.Models.Reports;

public interface ITimesheetReportService
{
    Task<List<TimesheetReportEntry>> LoadAllEntriesAsync();
    List<TimesheetReportEntry> ApplyFiltersAndRounding(
        List<TimesheetReportEntry> allEntries,
        TimesheetReportFilterCriteria criteria,
        bool applyRounding);

    TimesheetKPISummary CalculateKPISummary(List<TimesheetReportEntry> entries);

    List<TimesheetGroupSummary> BuildSummaryGroups(
        List<TimesheetReportEntry> entries,
        ReportGroupBy primaryGroup,
        ReportSubGroupBy secondaryGroup);

    TimesheetWeeklyMatrix BuildWeeklyMatrix(
        List<TimesheetReportEntry> entries,
        DateTime weekStartDate,
        WeeklyGroupBy groupBy);

    string FormatDuration(decimal hours, TimeDisplayFormat format);
    string FormatCurrency(decimal amount);

    DateRange GetPresetDateRange(string preset);
    DateRange ShiftDateRange(DateRange currentRange, int direction);

    string GenerateSummaryCsv(List<TimesheetGroupSummary> groups, TimeDisplayFormat format);
    string GenerateDetailedCsv(List<TimesheetReportEntry> entries, TimeDisplayFormat format);
    string GenerateWeeklyCsv(TimesheetWeeklyMatrix matrix, TimeDisplayFormat format);
}
