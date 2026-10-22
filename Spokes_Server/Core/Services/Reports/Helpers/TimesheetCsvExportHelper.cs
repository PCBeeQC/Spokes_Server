namespace Spokes_Server.Core.Services.Reports.Helpers;

using System.Text;
using Spokes_Server.Core.Helpers;
using Spokes_Server.Core.Models.Reports;

/// <summary>
/// Generates CSV exports for summary, detailed, and weekly timesheet reports.
/// Uses <see cref="CsvSanitizer"/> for RFC 4180 escaping and formula injection mitigation.
/// </summary>
public static class TimesheetCsvExportHelper
{
    public static string GenerateSummaryCsv(List<TimesheetGroupSummary> groups, TimeDisplayFormat format)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Title,Subtitle,Duration,Billable Hours,Billable Amount,Cost Amount,Entries Count");

        foreach (var group in groups)
        {
            var title = CsvSanitizer.EscapeCsv(group.Key);
            var subtitle = CsvSanitizer.EscapeCsv(group.Subtitle);
            var duration = CsvSanitizer.EscapeCsv(TimesheetFormatHelper.FormatDuration(group.TotalHours, format));
            var billable = CsvSanitizer.EscapeCsv(TimesheetFormatHelper.FormatDuration(group.BillableHours, format));
            var amount = $"{group.BillableAmount:0.00}";
            var cost = $"{group.CostAmount:0.00}";
            sb.AppendLine($"{title},{subtitle},{duration},{billable},{amount},{cost},{group.EntryCount}");

            foreach (var sub in group.Subgroups)
            {
                var subTitle = CsvSanitizer.EscapeCsv($"  - {sub.Key}");
                var subDuration = CsvSanitizer.EscapeCsv(TimesheetFormatHelper.FormatDuration(sub.TotalHours, format));
                var subBillable = CsvSanitizer.EscapeCsv(TimesheetFormatHelper.FormatDuration(sub.BillableHours, format));
                var subAmount = $"{sub.BillableAmount:0.00}";
                var subCost = $"{sub.CostAmount:0.00}";
                sb.AppendLine($"{subTitle},,{subDuration},{subBillable},{subAmount},{subCost},{sub.EntryCount}");
            }
        }

        return sb.ToString();
    }

    public static string GenerateDetailedCsv(List<TimesheetReportEntry> entries, TimeDisplayFormat format)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Date,Employee,Project,Client,Task,Description,Is Billable,Rate,Hours,Amount");

        foreach (var item in entries.OrderByDescending(e => e.Entry.Date))
        {
            var date = item.Entry.Date.ToString("yyyy-MM-dd");
            var employee = CsvSanitizer.EscapeCsv(item.EmployeeName);
            var project = CsvSanitizer.EscapeCsv(item.ProjectName);
            var client = CsvSanitizer.EscapeCsv(item.ClientId);
            var task = CsvSanitizer.EscapeCsv(item.TaskName);
            var description = CsvSanitizer.EscapeCsv(item.Entry.Description);
            var billable = item.Entry.IsBillable ? "Yes" : "No";
            var rate = $"{item.HourlyRate:0.00}";
            var hours = CsvSanitizer.EscapeCsv(TimesheetFormatHelper.FormatDuration(item.Entry.Hours, format));
            var amount = $"{item.BillableAmount:0.00}";

            sb.AppendLine($"{date},{employee},{project},{client},{task},{description},{billable},{rate},{hours},{amount}");
        }

        return sb.ToString();
    }

    public static string GenerateWeeklyCsv(TimesheetWeeklyMatrix matrix, TimeDisplayFormat format)
    {
        var sb = new StringBuilder();
        sb.Append("Entity,Subtitle,");
        sb.Append(string.Join(",", matrix.DayHeaders.Select(CsvSanitizer.EscapeCsv)));
        sb.AppendLine(",Total Duration,Total Amount");

        foreach (var row in matrix.Rows)
        {
            var name = CsvSanitizer.EscapeCsv(row.EntityName);
            var subtitle = CsvSanitizer.EscapeCsv(row.Subtitle);
            var cells = string.Join(",", row.Cells.Select(c => CsvSanitizer.EscapeCsv(TimesheetFormatHelper.FormatDuration(c.Hours, format))));
            var totalDuration = CsvSanitizer.EscapeCsv(TimesheetFormatHelper.FormatDuration(row.TotalHours, format));
            var totalAmount = $"{row.TotalBillableAmount:0.00}";

            sb.AppendLine($"{name},{subtitle},{cells},{totalDuration},{totalAmount}");
        }

        // Summary row
        var dayTotals = string.Join(",", matrix.DayTotals.Select(h => CsvSanitizer.EscapeCsv(TimesheetFormatHelper.FormatDuration(h, format))));
        var grandTotalDuration = CsvSanitizer.EscapeCsv(TimesheetFormatHelper.FormatDuration(matrix.GrandTotalHours, format));
        var grandTotalAmount = $"{matrix.GrandTotalBillableAmount:0.00}";
        sb.AppendLine($"Total,,{dayTotals},{grandTotalDuration},{grandTotalAmount}");

        return sb.ToString();
    }
}
