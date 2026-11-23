namespace Spokes_Server.Core.Models.Accounting;

using System;
using System.Collections.Generic;
using Spokes_Server.Core.Data;
using Spokes_Server.Core.Models.Common;
using Spokes_Server.Core.Services.Accounting;

public static class FinancialReportStatus
{
    public const string Draft = "Draft";
    public const string Final = "Final";
}

public static class FinancialReportType
{
    public const string Monthly = "Monthly";
    public const string Quarterly = "Quarterly";
    public const string Annual = "Annual";
    public const string Custom = "Custom";
}

public class FinancialReportOptions
{
    public bool IncludeExecutiveSummary { get; set; } = true;
    public bool IncludeIncomeStatement { get; set; } = true;
    public bool IncludeBalanceSheet { get; set; } = true;
    public bool IncludeCashFlowStatement { get; set; } = true;
    public bool IncludeFinancialRatios { get; set; } = true;
    public bool IncludeComparativePriorPeriod { get; set; } = true;
    public bool ShowGlAccountCodes { get; set; } = false;
    public string Language { get; set; } = "EN";
}

public class CashFlowLine
{
    public string Label { get; set; } = string.Empty;
    public string LabelFr { get; set; } = string.Empty;
    public decimal Amount { get; set; }
}

public class CashFlowStatementSnapshot
{
    public DateTime StartDate { get; set; }
    public DateTime EndDate { get; set; }

    public decimal NetIncome { get; set; }
    public decimal NonCashAdjustments { get; set; }
    public List<CashFlowLine> OperatingLines { get; set; } = [];
    public decimal NetCashFromOperating { get; set; }

    public List<CashFlowLine> InvestingLines { get; set; } = [];
    public decimal NetCashFromInvesting { get; set; }

    public List<CashFlowLine> FinancingLines { get; set; } = [];
    public decimal NetCashFromFinancing { get; set; }

    public decimal OpeningCashBalance { get; set; }
    public decimal NetChangeInCash { get; set; }
    public decimal ClosingCashBalance { get; set; }
    public decimal BalanceSheetCashBalance { get; set; }
    public decimal VarianceFromBalanceSheetCash => Math.Round(ClosingCashBalance - BalanceSheetCashBalance, 2);
}

public class FinancialRatiosSnapshot
{
    public decimal Revenue { get; set; }
    public decimal GrossProfit { get; set; }
    public decimal GrossMarginPercent { get; set; }
    public decimal OperatingExpenses { get; set; }
    public decimal Ebitda { get; set; }
    public decimal EbitdaMarginPercent { get; set; }
    public decimal NetIncome { get; set; }
    public decimal NetProfitMarginPercent { get; set; }

    public decimal TotalAssets { get; set; }
    public decimal CurrentAssets { get; set; }
    public decimal CashAndEquivalents { get; set; }
    public decimal AccountsReceivable { get; set; }
    public decimal CurrentLiabilities { get; set; }
    public decimal TotalLiabilities { get; set; }
    public decimal TotalEquity { get; set; }

    public decimal WorkingCapital { get; set; }
    public decimal CurrentRatio { get; set; }
    public decimal QuickRatio { get; set; }
    public decimal DebtToEquityRatio { get; set; }
    public decimal ReturnOnEquityPercent { get; set; }
    public decimal? RunwayMonths { get; set; }
}

public class FinancialReportSnapshot
{
    public string CompanyName { get; set; } = string.Empty;
    public string CompanyAddress { get; set; } = string.Empty;
    public string CompanyLogoPath { get; set; } = string.Empty;
    public string Currency { get; set; } = "CAD";

    public DateTime PeriodStart { get; set; }
    public DateTime PeriodEnd { get; set; }
    public DateTime? PriorPeriodStart { get; set; }
    public DateTime? PriorPeriodEnd { get; set; }
    public string FiscalYearLabel { get; set; } = string.Empty;

    public IncomeStatementReport IncomeStatement { get; set; } = new();
    public IncomeStatementReport? PriorIncomeStatement { get; set; }
    public BalanceSheetReport BalanceSheet { get; set; } = new();
    public BalanceSheetReport? PriorBalanceSheet { get; set; }
    public CashFlowStatementSnapshot CashFlow { get; set; } = new();
    public FinancialRatiosSnapshot Ratios { get; set; } = new();

    public bool IsBalanceSheetBalanced { get; set; } = true;
    public int PostedJournalEntryCount { get; set; }
}

/// <summary>
/// Persisted, versioned shareholder / investor financial statement package
/// generated from posted General Ledger data (Notice to Reader / Compilation format).
/// </summary>
public class FinancialReport : IDataEntity
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string ReportNumber { get; set; } = string.Empty;
    public string? TemplateId { get; set; }
    public string Title { get; set; } = "Financial Statements";
    public string Subtitle { get; set; } = string.Empty;
    public string PeriodType { get; set; } = FinancialReportType.Quarterly;

    public DateTime PeriodStart { get; set; }
    public DateTime PeriodEnd { get; set; }
    public DateTime AsOfDate { get; set; }

    public string Status { get; set; } = FinancialReportStatus.Draft;
    public int Version { get; set; } = 1;

    public string PreparedByEmployeeId { get; set; } = string.Empty;
    public string PreparedByName { get; set; } = string.Empty;
    public string ApprovedByName { get; set; } = string.Empty;

    public string ExecutiveSummary { get; set; } = string.Empty;
    public string ManagementDiscussionNotes { get; set; } = string.Empty;
    public string BasisOfPresentationNote { get; set; } =
        "Prepared on an accrual basis from posted General Ledger records for management, shareholder, and investor review (Compilation / Notice to Reader). Not audited or reviewed by an independent public accountant unless signed off externally.";

    public FinancialReportOptions Options { get; set; } = new();
    public FinancialReportSnapshot Snapshot { get; set; } = new();
    public List<DocumentHistoryEntry> History { get; set; } = [];

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? FinalizedAt { get; set; }
}
