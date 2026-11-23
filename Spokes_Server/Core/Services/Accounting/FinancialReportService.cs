namespace Spokes_Server.Core.Services.Accounting;

using System;
using System.Collections.Generic;
using System.Linq;
using Spokes_Server.Aggregate.Contexts;
using Spokes_Server.Core.Constants;
using Spokes_Server.Core.Extensions;
using Spokes_Server.Core.Models.Accounting;
using Spokes_Server.Core.Models.Common;
using Spokes_Server.Core.Models.HR;
using Spokes_Server.Core.Services.Security;

public interface IFinancialReportService
{
    List<FinancialReport> GetReports(Employee? actor = null);
    FinancialReport? GetById(string id, Employee? actor = null);
    FinancialReportSnapshot BuildSnapshot(DateTime periodStart, DateTime periodEnd, FinancialReportOptions options, Employee? actor = null);
    FinancialReport CreateDraft(
        string title,
        string subtitle,
        string periodType,
        DateTime periodStart,
        DateTime periodEnd,
        FinancialReportOptions options,
        string executiveSummary,
        string managementNotes,
        string approvedByName,
        string userId,
        string userName,
        string? templateId = null,
        Employee? actor = null);
    FinancialReport UpdateReport(FinancialReport report, bool refreshSnapshot, string userId, string userName, Employee? actor = null);
    FinancialReport ChangeStatus(string reportId, string newStatus, string userId, string userName, Employee? actor = null);
    void DeleteReport(string reportId, Employee? actor = null);
}

/// <summary>
/// Builds and persists compiled shareholder / investor Financial Statements (P&amp;L,
/// Balance Sheet, Indirect Cash Flow Statement, and SaaS/Operations Financial Ratios)
/// directly from posted General Ledger entries.
/// </summary>
public class FinancialReportService : IFinancialReportService
{
    private readonly AccountingContext _accounting;
    private readonly ILedgerService _ledger;
    private readonly CoreContext? _coreContext;

    public FinancialReportService(
        AccountingContext accounting,
        ILedgerService ledger,
        CoreContext? coreContext = null)
    {
        _accounting = accounting ?? throw new ArgumentNullException(nameof(accounting));
        _ledger = ledger ?? throw new ArgumentNullException(nameof(ledger));
        _coreContext = coreContext;
    }

    public List<FinancialReport> GetReports(Employee? actor = null)
    {
        DomainActorGuard.RequirePermission(actor, AppPermissions.Finance.AccessAccounting, "view financial reports", allowSuspended: true);
        var reports = _accounting.FinancialReports?.GetOrdered() ?? [];
        return reports
            .Select(r => SanitizeReportForActor(r.DeepClone()!, actor!))
            .ToList();
    }

    public FinancialReport? GetById(string id, Employee? actor = null)
    {
        DomainActorGuard.RequirePermission(actor, AppPermissions.Finance.AccessAccounting, "view financial report", allowSuspended: true);
        if (string.IsNullOrWhiteSpace(id)) return null;
        var report = _accounting.FinancialReports?.GetById(id);
        return report != null ? SanitizeReportForActor(report.DeepClone()!, actor!) : null;
    }

    public FinancialReportSnapshot BuildSnapshot(DateTime periodStart, DateTime periodEnd, FinancialReportOptions options, Employee? actor = null)
    {
        DomainActorGuard.RequirePermission(actor, AppPermissions.Finance.AccessAccounting, "build financial report snapshot", allowSuspended: true);
        var snapshot = BuildSnapshotCore(periodStart, periodEnd, options);
        return SanitizeSnapshotForActor(snapshot, actor!);
    }

    private FinancialReportSnapshot BuildSnapshotCore(DateTime periodStart, DateTime periodEnd, FinancialReportOptions options)
    {
        var start = periodStart.Date;
        var end = periodEnd.Date;
        if (start > end) (start, end) = (end, start);

        options ??= new FinancialReportOptions();

        // Ensure all invoices, bills, expenses, and payroll are posted to the GL first
        _ledger.RebuildOperationalLedger();

        var incomeStatement = _ledger.CalculateIncomeStatement(start, end);
        var balanceSheet = _ledger.CalculateBalanceSheet(end);

        // Prior period matching duration immediately preceding periodStart
        var periodDays = Math.Max(1, (end - start).Days + 1);
        DateTime priorEnd = start.AddDays(-1);
        DateTime priorStart = start.Month == 1 && start.Day == 1 && end.Month == 12 && end.Day == 31 && start.Year == end.Year
            ? new DateTime(start.Year - 1, 1, 1)
            : (start.Day == 1 && end.Day == DateTime.DaysInMonth(end.Year, end.Month) && start.Year == end.Year
                ? new DateTime(start.Year - 1, start.Month, 1)
                : priorEnd.AddDays(-(periodDays - 1)));

        if (start.Year != end.Year || !(start.Month == 1 && start.Day == 1 && end.Month == 12 && end.Day == 31))
        {
            priorStart = priorEnd.AddDays(-(periodDays - 1));
        }

        IncomeStatementReport? priorIncome = options.IncludeComparativePriorPeriod
            ? _ledger.CalculateIncomeStatement(priorStart, priorEnd)
            : null;
        BalanceSheetReport? priorBs = options.IncludeComparativePriorPeriod
            ? _ledger.CalculateBalanceSheet(priorEnd)
            : null;

        var cashFlow = BuildCashFlowStatement(start, end, incomeStatement, balanceSheet);
        var ratios = BuildFinancialRatios(start, end, incomeStatement, balanceSheet, cashFlow);

        var profile = _coreContext?.CompanyProfile.Get();
        var config = _accounting.GetAccountingConfig();
        var addressParts = new[]
        {
            profile?.AddressStreet,
            profile?.AddressCity,
            profile?.AddressState,
            profile?.AddressZip,
            profile?.AddressCountry
        }.Where(s => !string.IsNullOrWhiteSpace(s));

        var entryCount = _accounting.JournalEntries?.GetByDateRange(start, end).Count ?? 0;

        return new FinancialReportSnapshot
        {
            CompanyName = !string.IsNullOrWhiteSpace(profile?.CompanyName) ? profile.CompanyName : "Company",
            CompanyAddress = string.Join(", ", addressParts),
            CompanyLogoPath = profile?.HasCustomLogo == true ? profile.LogoBase64 : string.Empty,
            Currency = !string.IsNullOrWhiteSpace(config.DefaultCurrency) ? config.DefaultCurrency : "CAD",
            PeriodStart = start,
            PeriodEnd = end,
            PriorPeriodStart = options.IncludeComparativePriorPeriod ? priorStart : null,
            PriorPeriodEnd = options.IncludeComparativePriorPeriod ? priorEnd : null,
            FiscalYearLabel = start.Year == end.Year ? $"FY {end.Year}" : $"{start:yyyy}-{end:yyyy}",
            IncomeStatement = incomeStatement,
            PriorIncomeStatement = priorIncome,
            BalanceSheet = balanceSheet,
            PriorBalanceSheet = priorBs,
            CashFlow = cashFlow,
            Ratios = ratios,
            IsBalanceSheetBalanced = balanceSheet.IsBalanced,
            PostedJournalEntryCount = entryCount
        };
    }

    public FinancialReport CreateDraft(
        string title,
        string subtitle,
        string periodType,
        DateTime periodStart,
        DateTime periodEnd,
        FinancialReportOptions options,
        string executiveSummary,
        string managementNotes,
        string approvedByName,
        string userId,
        string userName,
        string? templateId = null,
        Employee? actor = null)
    {
        DomainActorGuard.RequirePermission(actor, AppPermissions.Finance.AccessAccounting, "create financial report", allowSuspended: false);
        if (actor!.IsSystem)
            throw new UnauthorizedAccessException("System service actors cannot create financial reports.");

        var repo = _accounting.FinancialReports
            ?? throw new InvalidOperationException("FinancialReports repository is not configured.");

        options ??= new FinancialReportOptions();
        var snapshot = BuildSnapshotCore(periodStart, periodEnd, options);

        var actorDisplay = !string.IsNullOrWhiteSpace(userName) ? userName.Trim() : actor.FullName;
        var report = new FinancialReport
        {
            ReportNumber = repo.GenerateReportNumber(),
            TemplateId = templateId,
            Title = string.IsNullOrWhiteSpace(title) ? "Financial Statements" : title.Trim(),
            Subtitle = subtitle?.Trim() ?? string.Empty,
            PeriodType = string.IsNullOrWhiteSpace(periodType) ? FinancialReportType.Quarterly : periodType,
            PeriodStart = periodStart.Date,
            PeriodEnd = periodEnd.Date,
            AsOfDate = periodEnd.Date,
            Status = FinancialReportStatus.Draft,
            Version = 1,
            PreparedByEmployeeId = !string.IsNullOrWhiteSpace(actor.Id) ? actor.Id : (userId ?? string.Empty),
            PreparedByName = string.IsNullOrWhiteSpace(actorDisplay) ? "System" : actorDisplay,
            ApprovedByName = approvedByName?.Trim() ?? string.Empty,
            ExecutiveSummary = executiveSummary?.Trim() ?? string.Empty,
            ManagementDiscussionNotes = managementNotes?.Trim() ?? string.Empty,
            Options = options,
            Snapshot = snapshot,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        report.History.Add(new DocumentHistoryEntry
        {
            Timestamp = DateTime.UtcNow,
            Action = DocumentHistoryActions.Created,
            NewStatus = FinancialReportStatus.Draft,
            ChangedBy = DocumentHistoryHelper.NormalizeActor(actorDisplay),
            Summary = $"Created {report.ReportNumber} ({report.PeriodStart:yyyy-MM-dd} to {report.PeriodEnd:yyyy-MM-dd})"
        });

        repo.Save(report);
        return SanitizeReportForActor(report.DeepClone()!, actor);
    }

    public FinancialReport UpdateReport(FinancialReport report, bool refreshSnapshot, string userId, string userName, Employee? actor = null)
    {
        DomainActorGuard.RequirePermission(actor, AppPermissions.Finance.AccessAccounting, "update financial report", allowSuspended: false);
        if (actor!.IsSystem)
            throw new UnauthorizedAccessException("System service actors cannot update financial reports.");

        ArgumentNullException.ThrowIfNull(report);
        var repo = _accounting.FinancialReports
            ?? throw new InvalidOperationException("FinancialReports repository is not configured.");

        var existing = repo.GetById(report.Id)
            ?? throw new KeyNotFoundException($"Financial report '{report.Id}' not found.");

        var toSave = report.DeepClone()!;
        toSave.AsOfDate = toSave.PeriodEnd.Date;
        if (refreshSnapshot)
        {
            toSave.Snapshot = BuildSnapshotCore(toSave.PeriodStart, toSave.PeriodEnd, toSave.Options);
        }
        else
        {
            // Preserve the persisted full snapshot so callers without Hr.ManageSalaries do not overwrite redacted payroll lines.
            toSave.Snapshot = existing.Snapshot;
        }

        var actorDisplay = !string.IsNullOrWhiteSpace(userName) ? userName.Trim() : actor.FullName;
        toSave.UpdatedAt = DateTime.UtcNow;
        toSave.History.Add(new DocumentHistoryEntry
        {
            Timestamp = DateTime.UtcNow,
            Action = toSave.Status == FinancialReportStatus.Final ? DocumentHistoryActions.EditedInFinal : DocumentHistoryActions.Edited,
            OldStatus = toSave.Status,
            NewStatus = toSave.Status,
            ChangedBy = DocumentHistoryHelper.NormalizeActor(actorDisplay),
            Summary = refreshSnapshot ? "Updated narrative & refreshed GL snapshot" : "Updated report details"
        });

        repo.Save(toSave);
        return SanitizeReportForActor(toSave.DeepClone()!, actor);
    }

    public FinancialReport ChangeStatus(string reportId, string newStatus, string userId, string userName, Employee? actor = null)
    {
        DomainActorGuard.RequirePermission(actor, AppPermissions.Finance.AccessAccounting, "change financial report status", allowSuspended: false);
        if (actor!.IsSystem)
            throw new UnauthorizedAccessException("System service actors cannot change financial report status.");

        var repo = _accounting.FinancialReports
            ?? throw new InvalidOperationException("FinancialReports repository is not configured.");

        var report = repo.GetById(reportId)
            ?? throw new KeyNotFoundException($"Financial report '{reportId}' not found.");

        if (string.Equals(report.Status, newStatus, StringComparison.OrdinalIgnoreCase))
            return SanitizeReportForActor(report.DeepClone()!, actor);

        var oldStatus = report.Status;
        report.Status = newStatus;
        report.UpdatedAt = DateTime.UtcNow;

        if (string.Equals(newStatus, FinancialReportStatus.Final, StringComparison.OrdinalIgnoreCase))
        {
            report.FinalizedAt = DateTime.UtcNow;
        }
        else if (string.Equals(oldStatus, FinancialReportStatus.Final, StringComparison.OrdinalIgnoreCase) &&
                 string.Equals(newStatus, FinancialReportStatus.Draft, StringComparison.OrdinalIgnoreCase))
        {
            report.Version++;
            report.FinalizedAt = null;
        }

        var actorDisplay = !string.IsNullOrWhiteSpace(userName) ? userName.Trim() : actor.FullName;
        report.History.Add(new DocumentHistoryEntry
        {
            Timestamp = DateTime.UtcNow,
            Action = DocumentHistoryActions.StatusChanged,
            OldStatus = oldStatus,
            NewStatus = newStatus,
            ChangedBy = DocumentHistoryHelper.NormalizeActor(actorDisplay),
            Summary = $"Status changed from {oldStatus} to {newStatus} (v{report.Version})"
        });

        repo.Save(report);
        return SanitizeReportForActor(report.DeepClone()!, actor);
    }

    public void DeleteReport(string reportId, Employee? actor = null)
    {
        DomainActorGuard.RequirePermission(actor, AppPermissions.Finance.AccessAccounting, "delete financial report", allowSuspended: false);
        if (actor!.IsSystem)
            throw new UnauthorizedAccessException("System service actors cannot delete financial reports.");

        if (string.IsNullOrWhiteSpace(reportId)) return;
        _accounting.FinancialReports?.Delete(reportId);
    }

    private static FinancialReport SanitizeReportForActor(FinancialReport report, Employee actor)
    {
        if (report.Snapshot != null)
        {
            SanitizeSnapshotForActor(report.Snapshot, actor);
        }
        return report;
    }

    private static FinancialReportSnapshot SanitizeSnapshotForActor(FinancialReportSnapshot snapshot, Employee actor)
    {
        if (actor.HasEffectivePermission(AppPermissions.Hr.ManageSalaries, allowSuspended: true))
            return snapshot;

        CollapsePayrollLines(snapshot.IncomeStatement);
        CollapsePayrollLines(snapshot.PriorIncomeStatement);
        return snapshot;
    }

    private static void CollapsePayrollLines(IncomeStatementReport? statement)
    {
        if (statement == null) return;
        var totalPayroll = statement.TotalPayrollExpenses;
        if (statement.PayrollExpenseLines.Count == 0 && totalPayroll == 0m)
            return;

        statement.PayrollExpenseLines =
        [
            new FinancialStatementLine
            {
                AccountCode = "5000",
                AccountName = "Payroll & Employee Compensation",
                AccountNameFr = "Paie et rémunération",
                SubType = GlAccountSubType.PayrollExpense,
                Amount = totalPayroll
            }
        ];
    }

    private CashFlowStatementSnapshot BuildCashFlowStatement(
        DateTime start,
        DateTime end,
        IncomeStatementReport pl,
        BalanceSheetReport endingBs)
    {
        var tb = _ledger.CalculateTrialBalance(start, end, includeZeroAccounts: true);

        decimal SumSubTypeDelta(string accountType, string subType)
        {
            decimal total = 0m;
            foreach (var r in tb.Rows.Where(x =>
                string.Equals(x.AccountType, accountType, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(x.SubType, subType, StringComparison.OrdinalIgnoreCase)))
            {
                // Debit increases asset (uses cash), Credit increases liability/equity (provides cash)
                total += r.PeriodCredits - r.PeriodDebits;
            }
            return Round(total);
        }

        decimal SumAllSubTypesExcept(string accountType, params string[] excludedSubTypes)
        {
            decimal total = 0m;
            foreach (var r in tb.Rows.Where(x =>
                string.Equals(x.AccountType, accountType, StringComparison.OrdinalIgnoreCase) &&
                !excludedSubTypes.Contains(x.SubType, StringComparer.OrdinalIgnoreCase)))
            {
                total += r.PeriodCredits - r.PeriodDebits;
            }
            return Round(total);
        }

        // Depreciation / Amortization non-cash add-back (keeps cash math invariant by adding to Operating & removing same from Fixed Assets)
        var nonCashDep = Round(pl.OverheadExpenseLines
            .Where(l => l.AccountCode == "5500" ||
                        l.AccountName.Contains("Depreciation", StringComparison.OrdinalIgnoreCase) ||
                        l.AccountName.Contains("Amortization", StringComparison.OrdinalIgnoreCase) ||
                        l.AccountName.Contains("Amortissement", StringComparison.OrdinalIgnoreCase))
            .Sum(l => l.Amount));

        var operatingLines = new List<CashFlowLine>();
        if (nonCashDep != 0m)
        {
            operatingLines.Add(new CashFlowLine
            {
                Label = "Depreciation & Amortization (Non-Cash)",
                LabelFr = "Amortissement (hors caisse)",
                Amount = nonCashDep
            });
        }

        var dAr = SumSubTypeDelta(GlAccountType.Asset, GlAccountSubType.AccountsReceivable);
        if (dAr != 0m)
        {
            operatingLines.Add(new CashFlowLine
            {
                Label = "Change in Accounts Receivable",
                LabelFr = "Variation des comptes clients",
                Amount = dAr
            });
        }

        var dOtherCurrentAssets = SumAllSubTypesExcept(GlAccountType.Asset, GlAccountSubType.Bank, GlAccountSubType.AccountsReceivable, GlAccountSubType.FixedAsset);
        if (dOtherCurrentAssets != 0m)
        {
            operatingLines.Add(new CashFlowLine
            {
                Label = "Change in Prepaid & Other Current Assets",
                LabelFr = "Variation des frais payés d'avance et autres actifs courants",
                Amount = dOtherCurrentAssets
            });
        }

        var dAp = SumSubTypeDelta(GlAccountType.Liability, GlAccountSubType.AccountsPayable) +
                  SumSubTypeDelta(GlAccountType.Liability, GlAccountSubType.CreditCard);
        if (dAp != 0m)
        {
            operatingLines.Add(new CashFlowLine
            {
                Label = "Change in Accounts Payable & Credit Cards",
                LabelFr = "Variation des comptes fournisseurs et cartes de crédit",
                Amount = Round(dAp)
            });
        }

        var dSalesTax = SumSubTypeDelta(GlAccountType.Liability, GlAccountSubType.SalesTax);
        if (dSalesTax != 0m)
        {
            operatingLines.Add(new CashFlowLine
            {
                Label = "Change in Sales Tax Payable",
                LabelFr = "Variation des taxes de vente à payer",
                Amount = dSalesTax
            });
        }

        var dPayroll = SumSubTypeDelta(GlAccountType.Liability, GlAccountSubType.PayrollLiability);
        if (dPayroll != 0m)
        {
            operatingLines.Add(new CashFlowLine
            {
                Label = "Change in Payroll & Remittance Liabilities",
                LabelFr = "Variation des passifs salariaux et retenues",
                Amount = dPayroll
            });
        }

        var netOperating = Round(pl.NetIncome + operatingLines.Sum(l => l.Amount));

        // Investing Activities: Capital Assets (subtracting non-cash depreciation already added back in Operating so gross CapEx matches)
        var dFixedAssetsNet = SumSubTypeDelta(GlAccountType.Asset, GlAccountSubType.FixedAsset);
        var grossCapExCashFlow = Round(dFixedAssetsNet - nonCashDep);
        var investingLines = new List<CashFlowLine>();
        if (grossCapExCashFlow != 0m)
        {
            investingLines.Add(new CashFlowLine
            {
                Label = "Purchases / Disposals of Property & Equipment",
                LabelFr = "Acquisitions / cessions d'immobilisations",
                Amount = grossCapExCashFlow
            });
        }
        var netInvesting = Round(investingLines.Sum(l => l.Amount));

        // Financing Activities: Loans / Other Liabilities & Equity injections/draws
        var financingLines = new List<CashFlowLine>();
        var dDebt = SumAllSubTypesExcept(
            GlAccountType.Liability,
            GlAccountSubType.AccountsPayable,
            GlAccountSubType.CreditCard,
            GlAccountSubType.SalesTax,
            GlAccountSubType.PayrollLiability);
        if (dDebt != 0m)
        {
            financingLines.Add(new CashFlowLine
            {
                Label = "Proceeds / Repayments of Debt & Other Liabilities",
                LabelFr = "Emprunts / remboursements et autres passifs",
                Amount = dDebt
            });
        }

        var dEquity = SumAllSubTypesExcept(GlAccountType.Equity);
        if (dEquity != 0m)
        {
            financingLines.Add(new CashFlowLine
            {
                Label = "Share Capital, Contributions & Dividends / Draws",
                LabelFr = "Capital-actions, apports et dividendes / retraits",
                Amount = dEquity
            });
        }
        var netFinancing = Round(financingLines.Sum(l => l.Amount));

        var openingCash = Round(tb.Rows
            .Where(r => string.Equals(r.SubType, GlAccountSubType.Bank, StringComparison.OrdinalIgnoreCase))
            .Sum(r => r.OpeningBalance));

        var bsCash = Round(endingBs.AssetLines
            .Where(a => string.Equals(a.SubType, GlAccountSubType.Bank, StringComparison.OrdinalIgnoreCase))
            .Sum(a => a.Amount));

        var netChangeInCash = Round(netOperating + netInvesting + netFinancing);
        var closingCash = Round(openingCash + netChangeInCash);

        return new CashFlowStatementSnapshot
        {
            StartDate = start,
            EndDate = end,
            NetIncome = pl.NetIncome,
            NonCashAdjustments = nonCashDep,
            OperatingLines = operatingLines,
            NetCashFromOperating = netOperating,
            InvestingLines = investingLines,
            NetCashFromInvesting = netInvesting,
            FinancingLines = financingLines,
            NetCashFromFinancing = netFinancing,
            OpeningCashBalance = openingCash,
            NetChangeInCash = netChangeInCash,
            ClosingCashBalance = closingCash,
            BalanceSheetCashBalance = bsCash
        };
    }

    private static FinancialRatiosSnapshot BuildFinancialRatios(
        DateTime start,
        DateTime end,
        IncomeStatementReport pl,
        BalanceSheetReport bs,
        CashFlowStatementSnapshot cf)
    {
        var revenue = pl.TotalRevenue;
        var grossProfit = pl.GrossProfit;
        var netIncome = pl.NetIncome;
        var ebitda = Round(netIncome + cf.NonCashAdjustments);

        var cash = cf.BalanceSheetCashBalance;
        var ar = Round(bs.AssetLines
            .Where(a => string.Equals(a.SubType, GlAccountSubType.AccountsReceivable, StringComparison.OrdinalIgnoreCase))
            .Sum(a => a.Amount));
        var fixedAssets = Round(bs.AssetLines
            .Where(a => string.Equals(a.SubType, GlAccountSubType.FixedAsset, StringComparison.OrdinalIgnoreCase))
            .Sum(a => a.Amount));
        var currentAssets = Round(bs.TotalAssets - fixedAssets);

        var longTermLiabilities = Round(bs.LiabilityLines
            .Where(l => string.Equals(l.SubType, GlAccountSubType.OtherLiability, StringComparison.OrdinalIgnoreCase))
            .Sum(l => l.Amount));
        var currentLiabilities = Round(bs.TotalLiabilities - longTermLiabilities);

        var workingCapital = Round(currentAssets - currentLiabilities);
        var currentRatio = currentLiabilities > 0m ? Round(currentAssets / currentLiabilities) : (currentAssets > 0m ? 99m : 0m);
        var quickRatio = currentLiabilities > 0m ? Round((cash + ar) / currentLiabilities) : ((cash + ar) > 0m ? 99m : 0m);
        var debtToEquity = bs.TotalEquity > 0m ? Round(bs.TotalLiabilities / bs.TotalEquity) : 0m;
        var roe = bs.TotalEquity > 0m ? Round((netIncome / bs.TotalEquity) * 100m) : 0m;

        var monthsInPeriod = Math.Max(1m, (decimal)((end - start).TotalDays + 1.0) / 30.4375m);
        decimal? runwayMonths = null;
        if (cf.NetCashFromOperating < 0m && cash > 0m)
        {
            var monthlyBurn = Math.Abs(cf.NetCashFromOperating) / monthsInPeriod;
            if (monthlyBurn > 0.01m)
                runwayMonths = Round(cash / monthlyBurn);
        }

        return new FinancialRatiosSnapshot
        {
            Revenue = revenue,
            GrossProfit = grossProfit,
            GrossMarginPercent = revenue > 0m ? Round((grossProfit / revenue) * 100m) : 0m,
            OperatingExpenses = pl.TotalOperatingExpenses,
            Ebitda = ebitda,
            EbitdaMarginPercent = revenue > 0m ? Round((ebitda / revenue) * 100m) : 0m,
            NetIncome = netIncome,
            NetProfitMarginPercent = revenue > 0m ? Round((netIncome / revenue) * 100m) : 0m,
            TotalAssets = bs.TotalAssets,
            CurrentAssets = currentAssets,
            CashAndEquivalents = cash,
            AccountsReceivable = ar,
            CurrentLiabilities = currentLiabilities,
            TotalLiabilities = bs.TotalLiabilities,
            TotalEquity = bs.TotalEquity,
            WorkingCapital = workingCapital,
            CurrentRatio = currentRatio,
            QuickRatio = quickRatio,
            DebtToEquityRatio = debtToEquity,
            ReturnOnEquityPercent = roe,
            RunwayMonths = runwayMonths
        };
    }

    private static decimal Round(decimal value) => Math.Round(value, 2, MidpointRounding.AwayFromZero);
}
