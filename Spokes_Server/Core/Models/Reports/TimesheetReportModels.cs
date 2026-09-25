namespace Spokes_Server.Core.Models.Reports;

using Spokes_Server.Core.Helpers;

public enum ReportGroupBy
{
    Project,
    Client,
    Employee,
    Task,
    Date
}

public enum ReportSubGroupBy
{
    Description,
    Task,
    Employee,
    Project,
    None
}

public enum WeeklyGroupBy
{
    Employee,
    Project,
    Client
}

public enum TimeDisplayFormat
{
    Duration_HHMM,
    Decimal_Hours
}

public enum BillableFilterStatus
{
    All,
    BillableOnly,
    NonBillableOnly
}

public enum AmountDisplayMode
{
    BillableAmount,
    CostAmount,
    ProfitMargin,
    HideAmounts
}

public class TimesheetKPISummary
{
    public decimal TotalHours { get; set; }
    public decimal BillableHours { get; set; }
    public decimal NonBillableHours => Math.Max(0, TotalHours - BillableHours);
    public decimal BillablePercentage => TotalHours > 0 ? Math.Round((BillableHours / TotalHours) * 100m, 1) : 0m;
    public decimal BillableAmount { get; set; }
    public decimal CostAmount { get; set; }
    public decimal ProfitAmount => BillableAmount - CostAmount;
    public decimal ProfitMarginPercentage => BillableAmount > 0 ? Math.Round((ProfitAmount / BillableAmount) * 100m, 1) : 0m;
    public int EntriesCount { get; set; }
    public int ActiveProjectsCount { get; set; }
    public int ActiveEmployeesCount { get; set; }
}

public class TimesheetGroupSummary
{
    public string Key { get; set; } = string.Empty;
    public string Subtitle { get; set; } = string.Empty;
    public string Color { get; set; } = "#3B82F6";
    public decimal TotalHours { get; set; }
    public decimal BillableHours { get; set; }
    public decimal BillableAmount { get; set; }
    public decimal CostAmount { get; set; }
    public int EntryCount { get; set; }
    public decimal? AllocatedHours { get; set; }
    public decimal? BudgetUsedPercentage => AllocatedHours > 0 ? Math.Round((TotalHours / AllocatedHours.Value) * 100m, 1) : null;
    public bool IsExpanded { get; set; } = false;
    public List<TimesheetSubGroupSummary> Subgroups { get; set; } = [];
}

public class TimesheetSubGroupSummary
{
    public string Key { get; set; } = string.Empty;
    public decimal TotalHours { get; set; }
    public decimal BillableHours { get; set; }
    public decimal BillableAmount { get; set; }
    public decimal CostAmount { get; set; }
    public int EntryCount { get; set; }
    public decimal? HourlyRate { get; set; }
    public List<TimesheetReportEntry> Entries { get; set; } = [];
}

public class TimesheetWeeklyCell
{
    public DateTime Date { get; set; }
    public DayOfWeek DayOfWeek { get; set; }
    public string DayLabel { get; set; } = string.Empty;
    public decimal Hours { get; set; }
    public decimal BillableHours { get; set; }
    public decimal BillableAmount { get; set; }
}

public class TimesheetWeeklyRow
{
    public string EntityId { get; set; } = string.Empty;
    public string EntityName { get; set; } = string.Empty;
    public string Subtitle { get; set; } = string.Empty;
    public string Color { get; set; } = "#3B82F6";
    public decimal TargetHours { get; set; } = 40m;
    public List<TimesheetWeeklyCell> Cells { get; set; } = [];
    public decimal TotalHours => Cells.Sum(c => c.Hours);
    public decimal TotalBillableHours => Cells.Sum(c => c.BillableHours);
    public decimal TotalBillableAmount => Cells.Sum(c => c.BillableAmount);
    public bool IsExpanded { get; set; } = false;
    public List<TimesheetWeeklyRow> SubRows { get; set; } = [];
}

public class TimesheetWeeklyMatrix
{
    public DateTime WeekStartDate { get; set; }
    public DateTime WeekEndDate { get; set; }
    public List<string> DayHeaders { get; set; } = [];
    public List<TimesheetWeeklyRow> Rows { get; set; } = [];
    public decimal[] DayTotals { get; set; } = new decimal[7];
    public decimal GrandTotalHours => Rows.Sum(r => r.TotalHours);
    public decimal GrandTotalBillableHours => Rows.Sum(r => r.TotalBillableHours);
    public decimal GrandTotalBillableAmount => Rows.Sum(r => r.TotalBillableAmount);
}
