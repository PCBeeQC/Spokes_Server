namespace Spokes_Server.Core.Models.Projects;

using System;
using System.Collections.Generic;
using System.Linq;
using Spokes_Server.Core.Data;
using Spokes_Server.Core.Models.Accounting;
using Spokes_Server.Core.Models.Common;
using Spokes_Server.Core.Models.Core;

public class ProjectReport : IDataEntity, IHasAssociatedFiles
{
    public string Id { get; set; } = Guid.NewGuid().ToString();

    public override bool Equals(object? obj)
    {
        if (ReferenceEquals(this, obj)) return true;
        return obj is ProjectReport other && Id == other.Id;
    }

    public override int GetHashCode() => Id?.GetHashCode() ?? base.GetHashCode();

    public string ProjectId { get; set; } = string.Empty;
    public string ReportNumber { get; set; } = string.Empty;
    public string Title { get; set; } = "Project Status & Financial Report";
    public string Status { get; set; } = ProjectReportStatus.Draft;
    public string PdfPath { get; set; } = string.Empty;
    public string? TemplateId { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public string CreatedBy { get; set; } = string.Empty;

    /// <summary>
    /// Optional start date for the report's activity window (e.g., 2026-01-01 for a 2026 fiscal bilan).
    /// When null, includes all project activity from project inception up to <see cref="AsOfDate"/>.
    /// </summary>
    public DateTime? PeriodStartDate { get; set; }

    /// <summary>
    /// Point-in-time cutoff date for this report (inclusive). Activity after this date is excluded.
    /// </summary>
    public DateTime AsOfDate { get; set; } = DateTime.Today;

    // Narrative & Legal
    public string ExecutiveSummary { get; set; } = string.Empty;
    public string ClosingNotes { get; set; } = string.Empty;
    public bool IsConfidential { get; set; }
    public bool SignatureRequired { get; set; }
    public string ClientRepresentativeName { get; set; } = string.Empty;
    public string CompanyRepresentativeName { get; set; } = string.Empty;

    // Display & Section Configuration
    public ProjectReportOptions Options { get; set; } = new();

    // Frozen Point-in-Time Snapshot
    public ProjectReportSnapshot Snapshot { get; set; } = new();

    // Audit Trail
    public List<DocumentHistoryEntry> History { get; set; } = [];

    public IEnumerable<string> GetAssociatedFilePaths()
    {
        if (!string.IsNullOrWhiteSpace(PdfPath)) yield return PdfPath;
    }
}

public static class ProjectReportStatus
{
    public const string Draft = "Draft";
    public const string Final = "Final";

    public static int GetRank(string? status) => status switch
    {
        Draft => 0,
        Final => 1,
        _ => 0
    };

    public static IReadOnlyList<string> GetAllowedTransitions() =>
        [Draft, Final];

    public static bool RequiresReason(string? oldStatus, string? newStatus)
    {
        if (string.IsNullOrWhiteSpace(oldStatus) || string.IsNullOrWhiteSpace(newStatus)) return false;
        return GetRank(newStatus) < GetRank(oldStatus);
    }

    public static bool CanUnlockEdit(string? status) =>
        string.Equals(status, Final, StringComparison.OrdinalIgnoreCase);
}

public static class ProjectReportFinancialMode
{
    public const string InternalCostAndProfit = "InternalCostAndProfit";
    public const string ClientBillable = "ClientBillable";
    public const string HoursAndQuantitiesOnly = "HoursAndQuantitiesOnly";

    public static readonly string[] All = [InternalCostAndProfit, ClientBillable, HoursAndQuantitiesOnly];
}

public static class ProjectReportPeriodGranularity
{
    public const string Weekly = "Weekly";
    public const string Monthly = "Monthly";

    public static readonly string[] All = [Weekly, Monthly];
}

public static class ProjectReportLaborLayout
{
    public const string EmployeeMatrix = "EmployeeMatrix";
    public const string GroupedByEmployee = "GroupedByEmployee";
    public const string TaskMatrix = "TaskMatrix";

    public static readonly string[] All = [EmployeeMatrix, GroupedByEmployee, TaskMatrix];
}

public static class ProjectReportPurchasesGrouping
{
    public const string ByMonthDetailed = "ByMonthDetailed";
    public const string ByMonthSummaryOnly = "ByMonthSummaryOnly";
    public const string BySupplier = "BySupplier";
    public const string Chronological = "Chronological";

    public static readonly string[] All = [ByMonthDetailed, ByMonthSummaryOnly, BySupplier, Chronological];
}

public static class ProjectReportExpensesGrouping
{
    public const string ByMonth = "ByMonth";
    public const string ByEmployee = "ByEmployee";
    public const string Chronological = "Chronological";

    public static readonly string[] All = [ByMonth, ByEmployee, Chronological];
}

public static class ProjectReportTaxDisplayMode
{
    public const string ExcludeTaxes = "ExcludeTaxes";
    public const string IncludeTaxes = "IncludeTaxes";
    public const string SeparateTaxes = "SeparateTaxes";

    public static readonly string[] All = [ExcludeTaxes, IncludeTaxes, SeparateTaxes];
}

public static class ProjectReportAudience
{
    public const string Internal = "Internal";
    public const string Client = "Client";

    public static readonly string[] All = [Internal, Client];
}

public class ProjectReportOptions
{
    public string Audience { get; set; } = ProjectReportAudience.Internal;
    public string FinancialMode { get; set; } = ProjectReportFinancialMode.InternalCostAndProfit;

    // Section 1: Financial Summary
    public bool ShowFinancialSummary { get; set; } = true;
    public bool ShowCashFlowPosition { get; set; } = true;
    public bool ShowOverheadSection { get; set; } = true;
    public bool ShowOverheadBreakdownTable { get; set; } = false;
    public string FinancialSummaryTaxMode { get; set; } = ProjectReportTaxDisplayMode.SeparateTaxes;

    // Section 2: Labor & Time Breakdown
    public bool ShowLaborSection { get; set; } = true;
    public bool ShowLaborByTask { get; set; } = true;
    public bool ShowLaborByEmployee { get; set; } = true;
    public bool ShowLaborPeriodicBreakdown { get; set; } = true;
    public string LaborPeriodGranularity { get; set; } = ProjectReportPeriodGranularity.Weekly;
    public string LaborPeriodicLayout { get; set; } = ProjectReportLaborLayout.EmployeeMatrix;
    public bool ShowLaborFinancials { get; set; } = true;

    // Section 3: Purchases & Procurement ("Achats par mois")
    public bool ShowPurchasesSection { get; set; } = true;
    public string PurchasesGrouping { get; set; } = ProjectReportPurchasesGrouping.ByMonthDetailed;
    public bool ShowPurchaseTaxBreakdown { get; set; } = true;

    // Section 4: Employee Expenses
    public bool ShowExpensesSection { get; set; } = true;
    public string ExpensesGrouping { get; set; } = ProjectReportExpensesGrouping.ByMonth;
    public bool SubmittedOrPaidExpensesOnly { get; set; } = true;

    // Section 4b: Other Direct Transactions
    public bool ShowDirectTransactions { get; set; } = true;

    // Section 5: Quotes, Invoices & Client POs
    public bool ShowBillingHistory { get; set; } = true;

    // Section 6: Commissions
    public bool ShowCommissionsSection { get; set; } = false;

    // Section 7: Timeline / Milestones
    public bool ShowTimelineNotes { get; set; } = false;

    // Page Break Controls
    public bool PageBreakBeforeOverhead { get; set; } = false;
    public bool PageBreakBeforeLabor { get; set; } = false;
    public bool PageBreakBeforePurchases { get; set; } = false;
    public bool PageBreakBeforeBilling { get; set; } = false;
}

public class ProjectReportSnapshot
{
    public DateTime CapturedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? PeriodStartDate { get; set; }
    public DateTime AsOfDate { get; set; } = DateTime.Today;

    // Project Metadata at Snapshot Time
    public string ProjectName { get; set; } = string.Empty;
    public string ProjectNumber { get; set; } = string.Empty;
    public string ProjectDescription { get; set; } = string.Empty;
    public string ProjectStatus { get; set; } = string.Empty;
    public string BillingMethod { get; set; } = string.Empty;
    public bool IsInternalProject { get; set; }
    public string ProjectManagerId { get; set; } = string.Empty;
    public string ProjectManagerName { get; set; } = string.Empty;
    public string ProjectManagerEmail { get; set; } = string.Empty;
    public DateOnly? ProjectStartDate { get; set; }
    public DateOnly? ProjectDeadline { get; set; }
    public ClientInfo Client { get; set; } = new();

    // Financial & KPI Rollups (Scoped to PeriodStartDate..AsOfDate, with Cumulative Budget Context)
    public decimal ExpectedRevenueSubTotal { get; set; }
    public decimal ExpectedRevenueTax { get; set; }
    public decimal ExpectedRevenue { get; set; }
    public decimal TotalClientBoughtBudget { get; set; }
    public decimal TotalProjectBudget => ExpectedRevenue + TotalClientBoughtBudget;

    public decimal TotalInvoicedSubTotal { get; set; }
    public decimal TotalInvoicedTax { get; set; }
    public decimal TotalInvoiced { get; set; }
    public decimal TotalPaid { get; set; }
    public decimal TotalWrittenOff { get; set; }
    public decimal BalanceDue => Math.Max(0m, Math.Round(TotalInvoiced - TotalPaid - TotalWrittenOff, 2, MidpointRounding.AwayFromZero));
    public decimal UnbilledLaborValue { get; set; }
    public decimal UnbilledExpensesValue { get; set; }

    public decimal TotalQuotedHours { get; set; }
    public decimal QuotedLaborCost { get; set; }
    public decimal QuotedMaterialCostNet { get; set; }
    public decimal QuotedMaterialCostTax { get; set; }
    public decimal QuotedMaterialCostIncTax => QuotedMaterialCostNet + QuotedMaterialCostTax;
    public decimal QuotedTotalCostPreTax => QuotedLaborCost + QuotedMaterialCostNet;
    public decimal QuotedTotalCostIncTax => QuotedLaborCost + QuotedMaterialCostIncTax;
    public bool HasQuoteBudget => TotalQuotedHours > 0m || QuotedLaborCost > 0m || QuotedMaterialCostNet > 0m || Quotes.Count > 0;

    public decimal TotalLaborHours { get; set; }
    public decimal CumulativeProjectHoursToAsOfDate { get; set; }
    public decimal TotalLaborInternalCost { get; set; }
    public decimal TotalLaborBillableValue { get; set; }

    public decimal TotalPurchaseNet { get; set; }
    public decimal TotalPurchaseTax { get; set; }
    public decimal TotalPurchaseCost => TotalPurchaseNet + TotalPurchaseTax;

    public decimal TotalExpenseSubTotal { get; set; }
    public decimal TotalExpenseTax { get; set; }
    public decimal TotalExpenseCost => TotalExpenseSubTotal + TotalExpenseTax;

    public decimal TotalDirectIncomeSubTotal { get; set; }
    public decimal TotalDirectIncomeTax { get; set; }
    public decimal TotalDirectIncome => TotalDirectIncomeSubTotal + TotalDirectIncomeTax;

    public decimal TotalDirectExpenseSubTotal { get; set; }
    public decimal TotalDirectExpenseTax { get; set; }
    public decimal TotalDirectExpense => TotalDirectExpenseSubTotal + TotalDirectExpenseTax;

    public decimal TotalCommissions { get; set; }

    public decimal GetExpectedRevenueSubTotal() =>
        ExpectedRevenueSubTotal > 0m
            ? ExpectedRevenueSubTotal
            : Quotes.Where(q => string.Equals(q.Status, "Accepted", StringComparison.OrdinalIgnoreCase)).Sum(q => q.SubTotal);

    public decimal GetExpectedRevenueTax() =>
        ExpectedRevenueTax > 0m
            ? ExpectedRevenueTax
            : Quotes.Where(q => string.Equals(q.Status, "Accepted", StringComparison.OrdinalIgnoreCase)).Sum(q => q.TaxAmount);

    public decimal GetTotalInvoicedSubTotal() =>
        TotalInvoicedSubTotal > 0m
            ? TotalInvoicedSubTotal
            : Invoices.Sum(i => i.SubTotal);

    public decimal GetTotalInvoicedTax() =>
        TotalInvoicedTax > 0m
            ? TotalInvoicedTax
            : Invoices.Sum(i => i.TaxAmount);

    public decimal TotalInternalSpentPreTax => TotalLaborInternalCost + TotalPurchaseNet + TotalExpenseSubTotal + TotalDirectExpenseSubTotal;
    public decimal TotalInternalSpentTax => TotalPurchaseTax + TotalExpenseTax + TotalDirectExpenseTax;

    public decimal BaseRevenuePreTax => (GetExpectedRevenueSubTotal() > 0m ? GetExpectedRevenueSubTotal() : GetTotalInvoicedSubTotal()) + TotalDirectIncomeSubTotal;
    public decimal BaseRevenueTax => (GetExpectedRevenueSubTotal() > 0m ? GetExpectedRevenueTax() : GetTotalInvoicedTax()) + TotalDirectIncomeTax;
    public decimal BaseRevenueIncTax => (ExpectedRevenue > 0m ? ExpectedRevenue : TotalInvoiced) + TotalDirectIncome;

    public decimal GrossMargin => ((TotalInvoiced > 0 ? TotalInvoiced : ExpectedRevenue) + TotalDirectIncome) - TotalPurchaseCost - TotalExpenseCost - TotalDirectExpense;
    public decimal TotalInternalSpent => TotalLaborInternalCost + TotalPurchaseCost + TotalExpenseCost + TotalDirectExpense;
    public decimal NetProfit => BaseRevenueIncTax - TotalLaborInternalCost - TotalPurchaseCost - TotalExpenseCost - TotalDirectExpense - TotalCommissions;
    public decimal NetMarginPercent => BaseRevenueIncTax > 0
        ? Math.Round(NetProfit / BaseRevenueIncTax * 100m, 1)
        : 0m;

    public decimal NetProfitPreTax => BaseRevenuePreTax - TotalLaborInternalCost - TotalPurchaseNet - TotalExpenseSubTotal - TotalDirectExpenseSubTotal - TotalCommissions;
    public decimal NetMarginPreTaxPercent => BaseRevenuePreTax > 0
        ? Math.Round(NetProfitPreTax / BaseRevenuePreTax * 100m, 1)
        : 0m;

    // Business Overhead Allocation Rollups (Prorated Monthly & Allocated by Direct Client Labor Hours)
    public decimal TotalCompanyDirectLaborHours { get; set; }
    public decimal OverheadAllocationSharePercent { get; set; }

    public decimal CompanyOverheadPoolPreTax { get; set; }
    public decimal CompanyOverheadPoolTax { get; set; }
    public decimal CompanyOverheadPoolTotal => CompanyOverheadPoolPreTax + CompanyOverheadPoolTax;

    public decimal CompanyFixedOverheadPoolPreTax { get; set; }
    public decimal CompanyFixedOverheadPoolTax { get; set; }

    public decimal AllocatedFixedOverheadPreTax { get; set; }
    public decimal AllocatedFixedOverheadTax { get; set; }
    public decimal AllocatedFixedOverheadTotal => AllocatedFixedOverheadPreTax + AllocatedFixedOverheadTax;

    public decimal CompanyInternalCostsPoolPreTax { get; set; }
    public decimal CompanyInternalCostsPoolTax { get; set; }

    public decimal AllocatedInternalCostsPreTax { get; set; }
    public decimal AllocatedInternalCostsTax { get; set; }
    public decimal AllocatedInternalCostsTotal => AllocatedInternalCostsPreTax + AllocatedInternalCostsTax;

    public decimal AllocatedOverheadPreTax { get; set; }
    public decimal AllocatedOverheadTax { get; set; }
    public decimal AllocatedOverheadTotal => AllocatedOverheadPreTax + AllocatedOverheadTax;

    public decimal NetProfitAfterOverhead => NetProfit - AllocatedOverheadTotal;
    public decimal NetMarginAfterOverheadPercent => BaseRevenueIncTax > 0
        ? Math.Round(NetProfitAfterOverhead / BaseRevenueIncTax * 100m, 1)
        : 0m;

    public decimal NetProfitAfterOverheadPreTax => NetProfitPreTax - AllocatedOverheadPreTax;
    public decimal NetMarginAfterOverheadPreTaxPercent => BaseRevenuePreTax > 0
        ? Math.Round(NetProfitAfterOverheadPreTax / BaseRevenuePreTax * 100m, 1)
        : 0m;

    public decimal CashPosition => (TotalPaid + TotalDirectIncome) - TotalInternalSpent;
    public decimal CashPositionPreTax => (TotalPaid + TotalDirectIncomeSubTotal) - TotalInternalSpentPreTax;
    public decimal LaborEfficiencyPercent => TotalQuotedHours > 0
        ? Math.Round(TotalLaborHours / TotalQuotedHours * 100m, 1)
        : 0m;

    // Structured Snapshot Collections
    public List<ReportOverheadBreakdownRow> OverheadRows { get; set; } = [];
    public List<ReportTaskLaborRow> TaskLaborRows { get; set; } = [];
    public List<ReportEmployeeLaborRow> EmployeeLaborRows { get; set; } = [];
    public List<ReportTimeEntrySnapshot> TimeEntries { get; set; } = [];
    public List<ReportPurchaseItemSnapshot> PurchaseItems { get; set; } = [];
    public List<ReportExpenseItemSnapshot> ExpenseItems { get; set; } = [];
    public List<ReportDirectTransactionSnapshot> DirectTransactions { get; set; } = [];
    public List<ReportQuoteSnapshot> Quotes { get; set; } = [];
    public List<ReportInvoiceSnapshot> Invoices { get; set; } = [];
    public List<ClientPo> ClientPos { get; set; } = [];
    public List<ReportCommissionSnapshot> Commissions { get; set; } = [];
    public List<ReportTimelineNoteSnapshot> TimelineNotes { get; set; } = [];
}

public class ReportTaskLaborRow
{
    public string WorkTypeId { get; set; } = string.Empty;
    public string TaskName { get; set; } = string.Empty;
    public string? SubTaskId { get; set; }
    public string SubTaskName { get; set; } = string.Empty;
    public List<string> ContributorNames { get; set; } = [];
    public decimal QuotedHours { get; set; }
    public decimal ActualHours { get; set; }
    public decimal InternalCost { get; set; }
    public decimal BillableValue { get; set; }
    public decimal SharePercent { get; set; }

    public bool IsOverBudget => QuotedHours > 0 && ActualHours > QuotedHours;
    public decimal UsagePercent => QuotedHours > 0 ? Math.Round(ActualHours / QuotedHours * 100m, 1) : 0m;
}

public class ReportEmployeeLaborRow
{
    public string EmployeeId { get; set; } = string.Empty;
    public string EmployeeName { get; set; } = string.Empty;
    public string TeamId { get; set; } = string.Empty;
    public string TeamName { get; set; } = string.Empty;
    public decimal TotalHours { get; set; }
    public decimal SharePercent { get; set; }
    public decimal BaseHourlyRate { get; set; }
    public decimal EmployerContributionHourlyRate { get; set; }
    public decimal EffectiveInternalRate => TotalHours > 0 ? Math.Round(InternalCost / TotalHours, 2) : 0m;
    public decimal EffectiveBillableRate => TotalHours > 0 ? Math.Round(BillableValue / TotalHours, 2) : 0m;
    public decimal InternalCost { get; set; }
    public decimal BillableValue { get; set; }
}

public class ReportTimeEntrySnapshot
{
    public string EntryId { get; set; } = string.Empty;
    public string EmployeeId { get; set; } = string.Empty;
    public string EmployeeName { get; set; } = string.Empty;
    public string TeamId { get; set; } = string.Empty;
    public string TeamName { get; set; } = string.Empty;
    public string WorkTypeId { get; set; } = string.Empty;
    public string TaskName { get; set; } = string.Empty;
    public string? SubTaskId { get; set; }
    public string SubTaskName { get; set; } = string.Empty;
    public DateTime Date { get; set; }
    public int IsoYear { get; set; }
    public int IsoWeek { get; set; }
    public decimal Hours { get; set; }
    public decimal InternalCostRate { get; set; }
    public decimal BillableRate { get; set; }
    public string Description { get; set; } = string.Empty;

    public decimal InternalCost => Math.Round(Hours * InternalCostRate, 2);
    public decimal BillableValue => Math.Round(Hours * BillableRate, 2);
}

public class ReportPurchaseItemSnapshot
{
    public string PoId { get; set; } = string.Empty;
    public string PoItemId { get; set; } = string.Empty;
    public string PoNumber { get; set; } = string.Empty;
    public DateTime Date { get; set; }
    public string SupplierName { get; set; } = string.Empty;
    public string PartNumber { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public decimal Quantity { get; set; } = 1m;
    public decimal UnitPrice { get; set; }
    public decimal NetTotal { get; set; }
    public decimal TaxAmount { get; set; }
    public decimal GrandTotal => NetTotal + TaxAmount;
}

public class ReportExpenseItemSnapshot
{
    public string ExpenseReportId { get; set; } = string.Empty;
    public string ExpenseItemId { get; set; } = string.Empty;
    public string ReportNumber { get; set; } = string.Empty;
    public string EmployeeId { get; set; } = string.Empty;
    public string EmployeeName { get; set; } = string.Empty;
    public DateTime Date { get; set; }
    public string Description { get; set; } = string.Empty;
    public bool IsKilometrage { get; set; }
    public decimal Kilometers { get; set; }
    public decimal KilometrageRate { get; set; }
    public decimal SubTotal { get; set; }
    public decimal TaxAmount { get; set; }
    public decimal Total { get; set; }
    public string Status { get; set; } = string.Empty;
}

public class ReportQuoteSnapshot
{
    public string QuoteId { get; set; } = string.Empty;
    public string QuoteNumber { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public DateTime Date { get; set; }
    public string Status { get; set; } = string.Empty;
    public decimal QuotedHours { get; set; }
    public decimal EstimatedLaborCost { get; set; }
    public decimal EstimatedMaterialCostNet { get; set; }
    public decimal EstimatedMaterialCostTax { get; set; }
    public decimal TotalLabor { get; set; }
    public decimal TotalItems { get; set; }
    public decimal SubTotal { get; set; }
    public decimal TaxAmount { get; set; }
    public decimal GrandTotal { get; set; }
    public decimal TotalClientBought { get; set; }
}

public class ReportInvoiceSnapshot
{
    public string InvoiceId { get; set; } = string.Empty;
    public string InvoiceNumber { get; set; } = string.Empty;
    public DateTime Date { get; set; }
    public DateTime DueDate { get; set; }
    public string Status { get; set; } = string.Empty;
    public decimal SubTotal { get; set; }
    public decimal TaxAmount { get; set; }
    public decimal GrandTotal { get; set; }
    public decimal AmountPaid { get; set; }
    public decimal AmountWrittenOff { get; set; }
    public decimal BalanceDue => Math.Max(0m, Math.Round(GrandTotal - AmountPaid - AmountWrittenOff, 2, MidpointRounding.AwayFromZero));
    public DateTime? DatePaid { get; set; }
}

public class ReportCommissionSnapshot
{
    public string BeneficiaryName { get; set; } = string.Empty;
    public string Basis { get; set; } = string.Empty;
    public decimal Percentage { get; set; }
    public decimal BaseAmount { get; set; }
    public decimal CommissionAmount { get; set; }
    public string Status { get; set; } = string.Empty;
}

public class ReportTimelineNoteSnapshot
{
    public string NoteId { get; set; } = string.Empty;
    public DateTime Date { get; set; }
    public string Category { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
    public string CreatedBy { get; set; } = string.Empty;
}

public class ReportDirectTransactionSnapshot
{
    public string TransactionId { get; set; } = string.Empty;
    public string TransactionNumber { get; set; } = string.Empty;
    public DateTime Date { get; set; }
    public string Direction { get; set; } = DirectTransactionDirection.MoneyIn;
    public string PartyName { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string CategoryLabel { get; set; } = string.Empty;
    public decimal SubTotal { get; set; }
    public decimal TaxAmount { get; set; }
    public decimal GrandTotal { get; set; }
}

public static class OverheadCostCategory
{
    public const string RecurringFixed = "RecurringFixed";
    public const string OneTimeAmortized = "OneTimeAmortized";
    public const string InternalLabor = "InternalLabor";
    public const string InternalPurchasesAndExpenses = "InternalPurchasesAndExpenses";
}

public class ReportOverheadBreakdownRow
{
    public string Category { get; set; } = OverheadCostCategory.RecurringFixed;
    public string Name { get; set; } = string.Empty;
    public string Details { get; set; } = string.Empty;
    public decimal Hours { get; set; }
    public decimal CompanyPoolPreTax { get; set; }
    public decimal CompanyPoolTax { get; set; }
    public decimal CompanyPoolTotal => CompanyPoolPreTax + CompanyPoolTax;
    public decimal AllocatedPreTax { get; set; }
    public decimal AllocatedTax { get; set; }
    public decimal AllocatedTotal => AllocatedPreTax + AllocatedTax;
}
