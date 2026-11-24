namespace Spokes_Server.Core.Services.Accounting;

using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.DependencyInjection;
using Spokes_Server.Aggregate.Contexts;
using Spokes_Server.Core.Constants;
using Spokes_Server.Core.Models.Accounting;
using Spokes_Server.Core.Models.HR;
using Spokes_Server.Core.Services.Security;

public interface IAccountingHealthService
{
    AccountingHealthReport EvaluateHealth(DateTime? asOfDate = null, Employee? actor = null);
    int ResyncMissingJournalEntries(Employee? actor = null);
}

/// <summary>
/// Single source of truth for deterministic accounting health diagnostics and mid-year close readiness.
/// Evaluates GL integrity, subledger drift, AR/AP hygiene, sales tax readiness, recurring overhead,
/// bank reconciliation status, payroll coverage, and period locking.
/// </summary>
public class AccountingHealthService : IAccountingHealthService
{
    private readonly AccountingContext _accounting;
    private readonly ILedgerService _ledger;
    private readonly IBusinessOverheadService? _overhead;
    private readonly HrContext? _hr;
    private readonly ProjectsContext? _projects;

    [ActivatorUtilitiesConstructor]
    public AccountingHealthService(
        AccountingContext accounting,
        ILedgerService ledger,
        IBusinessOverheadService? overhead = null,
        HrContext? hr = null,
        ProjectsContext? projects = null)
    {
        _accounting = accounting ?? throw new ArgumentNullException(nameof(accounting));
        _ledger = ledger ?? throw new ArgumentNullException(nameof(ledger));
        _overhead = overhead;
        _hr = hr;
        _projects = projects;
    }

    public AccountingHealthReport EvaluateHealth(DateTime? asOfDate = null, Employee? actor = null)
    {
        if (actor != null)
        {
            DomainActorGuard.RequirePermission(actor, AppPermissions.Finance.AccessAccounting, "evaluate accounting health", allowSuspended: false);
        }

        var today = (asOfDate ?? DateTime.Today).Date;
        var report = new AccountingHealthReport { AsOfDate = today };
        var config = _accounting.GetAccountingConfig();

        var allEntries = _accounting.JournalEntries?.GetAll() ?? [];
        var allInvoices = _accounting.Invoices?.GetAll() ?? [];
        var allBills = _accounting.Bills?.GetAll() ?? [];
        var allExpenses = _accounting.ExpenseReports?.GetAll() ?? [];
        var allStatements = _accounting.BankStatements?.GetAll() ?? [];
        var allTaxFilings = _accounting.TaxFilingPeriods?.GetAll() ?? [];
        var allMigrationBatches = _accounting.MigrationBatches?.GetAll() ?? [];
        var invoicesById = allInvoices.ToDictionary(i => i.Id, StringComparer.OrdinalIgnoreCase);
        var pendingSuggestions = (_accounting.SuggestedInvoicePayments?.GetPending() ?? [])
            .Where(s => invoicesById.TryGetValue(s.InvoiceId, out var inv) &&
                        InvoiceStatus.IsPosted(inv.Status) &&
                        !InvoiceStatus.IsSettled(inv.Status))
            .ToList();

        var jeBySourceId = allEntries
            .Where(e => !string.IsNullOrWhiteSpace(e.SourceId))
            .Select(e => e.SourceId!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // 1. Unbalanced Journal Entries
        var unbalanced = allEntries.Where(e => !e.IsBalanced).ToList();
        if (unbalanced.Count > 0)
        {
            report.Issues.Add(new AccountingHealthIssue
            {
                Code = "GL_UNBALANCED_JE",
                Category = "General Ledger",
                Severity = AccountingHealthSeverity.Critical,
                Title = "Unbalanced Journal Entries",
                Details = $"{unbalanced.Count} journal entry(s) have Debits != Credits.",
                AffectedCount = unbalanced.Count,
                ActionKey = AccountingHealthActionKey.OpenGeneralLedger,
                ActionLabel = "Inspect GL"
            });
        }

        // 2. Suspected Zeroed-Out Ledger After OpeningBalance Migration
        var appliedMigrations = allMigrationBatches
            .Where(b => string.Equals(b.Status, AccountingMigrationBatchStatus.Applied, StringComparison.OrdinalIgnoreCase) &&
                        b.CreatedJournalEntryIds.Count > 0 &&
                        b.OpeningTotalDebits > 0m)
            .ToList();
        var fiscalStart = _ledger.GetFiscalYearStart(today);
        var tb = _ledger.CalculateTrialBalance(fiscalStart, today, includeZeroAccounts: false);
        var tbByCode = tb.Rows.ToDictionary(r => r.AccountCode, StringComparer.OrdinalIgnoreCase);

        bool suspectedZeroedLedger = false;
        if (appliedMigrations.Count > 0 && allEntries.Count >= 5)
        {
            var obEntryIds = appliedMigrations.SelectMany(b => b.CreatedJournalEntryIds).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var nonObOperationalCount = allEntries.Count(e => !obEntryIds.Contains(e.Id));
            var bankCode = _ledger.ResolveAccountCodeByRole(GlSystemRole.OperatingBank, "1020");
            var arCode = _ledger.ResolveAccountCodeByRole(GlSystemRole.AccountsReceivable, "1200");
            tbByCode.TryGetValue(bankCode, out var bankRow);
            tbByCode.TryGetValue(arCode, out var arRow);

            var openArSubledger = allInvoices
                .Where(i => InvoiceStatus.IsPosted(i.Status) &&
                            !InvoiceStatus.IsSettled(i.Status) &&
                            i.BalanceDue > 0.01m)
                .Sum(i => i.BalanceDue);

            if (nonObOperationalCount >= 5 &&
                Math.Abs(bankRow?.EndingBalance ?? 0m) <= 0.05m &&
                openArSubledger > 10m &&
                Math.Abs(arRow?.EndingBalance ?? 0m) <= 0.50m)
            {
                suspectedZeroedLedger = true;
                report.Issues.Add(new AccountingHealthIssue
                {
                    Code = "GL_MIGRATION_ZEROED",
                    Category = "General Ledger",
                    Severity = AccountingHealthSeverity.Critical,
                    Title = "Migration Entry Appears to Have Zeroed Out the General Ledger",
                    Details = $"Opening Balance migration ({appliedMigrations[0].BatchNumber}) offset Bank ({bankCode}) and AR ({arCode}) to ~$0.00 despite {openArSubledger:C2} in open invoices. Roll back and re-import with an actual Trial Balance.",
                    AffectedCount = appliedMigrations.Count,
                    AffectedAmount = appliedMigrations[0].OpeningTotalDebits,
                    ActionKey = AccountingHealthActionKey.OpenMigration,
                    ActionLabel = "Review Migration"
                });
            }
        }

        // 3. Opening Balance Equity (3900) Residual
        var ob3900Code = _ledger.ResolveAccountCodeByRole(GlSystemRole.OpeningBalanceEquity, "3900");
        if (tbByCode.TryGetValue(ob3900Code, out var ob3900Row) && Math.Abs(ob3900Row.EndingBalance) > 0.05m)
        {
            report.Issues.Add(new AccountingHealthIssue
            {
                Code = "GL_3900_RESIDUAL",
                Category = "General Ledger",
                Severity = AccountingHealthSeverity.Critical,
                Title = $"Opening Balance Equity ({ob3900Code}) Not Cleared",
                Details = $"Account {ob3900Code} has a residual balance of {ob3900Row.EndingBalance:C2} instead of $0.00.",
                AffectedCount = 1,
                AffectedAmount = ob3900Row.EndingBalance,
                ActionKey = AccountingHealthActionKey.OpenGeneralLedger,
                ActionLabel = "Inspect GL"
            });
        }

        // 4. Posted Documents Missing Journal Entries
        var missingInvJe = allInvoices.Where(i =>
            InvoiceStatus.IsPosted(i.Status) &&
            i.GrandTotal > 0m &&
            !jeBySourceId.Contains(i.Id)).ToList();

        var missingBillJe = allBills.Where(b =>
            !b.IsVoid &&
            !string.Equals(b.Status, BillStatus.Void, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(b.Status, BillStatus.AwaitingApproval, StringComparison.OrdinalIgnoreCase) &&
            b.Amount > 0m &&
            !jeBySourceId.Contains(b.Id)).ToList();

        var missingExpJe = allExpenses.Where(e =>
            !string.Equals(e.Status, ExpenseReportStatus.Draft, StringComparison.OrdinalIgnoreCase) &&
            e.TotalAmount > 0m &&
            !jeBySourceId.Contains(e.Id)).ToList();

        int missingJeTotal = missingInvJe.Count + missingBillJe.Count + missingExpJe.Count;
        if (missingJeTotal > 0)
        {
            report.Issues.Add(new AccountingHealthIssue
            {
                Code = "GL_MISSING_JES",
                Category = "General Ledger",
                Severity = AccountingHealthSeverity.Warning,
                Title = "Posted Documents Missing Journal Entries",
                Details = $"{missingInvJe.Count} invoice(s), {missingBillJe.Count} bill(s), and {missingExpJe.Count} expense report(s) have no posted GL entry.",
                AffectedCount = missingJeTotal,
                ActionKey = AccountingHealthActionKey.ResyncMissingJEs,
                ActionLabel = "Re-sync Missing JEs"
            });
        }

        // 5. Subledger Drift: AR (1200) vs Open Invoices & AP (2100) vs Open Bills
        var arAcctCode = _ledger.ResolveAccountCodeByRole(GlSystemRole.AccountsReceivable, "1200");
        var openArTotal = Math.Round(allInvoices
            .Where(i => InvoiceStatus.IsPosted(i.Status) &&
                        !InvoiceStatus.IsSettled(i.Status) &&
                        i.BalanceDue > 0m)
            .Sum(i => i.BalanceDue), 2);
        var glArBalance = tbByCode.TryGetValue(arAcctCode, out var glArRow) ? glArRow.EndingBalance : 0m;
        if (!suspectedZeroedLedger && Math.Abs(openArTotal - glArBalance) > 0.50m)
        {
            report.Issues.Add(new AccountingHealthIssue
            {
                Code = "SUBLEDGER_AR_DRIFT",
                Category = "Subledger Integrity",
                Severity = AccountingHealthSeverity.Warning,
                Title = "Accounts Receivable (GL vs. Invoices) Mismatch",
                Details = $"Open Invoices total {openArTotal:C2}, while GL {arAcctCode} balance is {glArBalance:C2} (diff {(openArTotal - glArBalance):C2}).",
                AffectedCount = 1,
                AffectedAmount = openArTotal - glArBalance,
                ActionKey = AccountingHealthActionKey.OpenReceivables,
                ActionLabel = "Review Receivables"
            });
        }

        var apAcctCode = _ledger.ResolveAccountCodeByRole(GlSystemRole.AccountsPayable, "2100");
        var openApTotal = Math.Round(allBills
            .Where(b => !b.IsVoid &&
                        !string.Equals(b.Status, BillStatus.Void, StringComparison.OrdinalIgnoreCase) &&
                        !string.Equals(b.Status, BillStatus.AwaitingApproval, StringComparison.OrdinalIgnoreCase) &&
                        !BillStatus.IsSettled(b.Status) &&
                        b.BalanceDue > 0m)
            .Sum(b => b.BalanceDue), 2);
        var glApBalance = tbByCode.TryGetValue(apAcctCode, out var glApRow) ? glApRow.EndingBalance : 0m;
        if (!suspectedZeroedLedger && Math.Abs(openApTotal - glApBalance) > 0.50m)
        {
            report.Issues.Add(new AccountingHealthIssue
            {
                Code = "SUBLEDGER_AP_DRIFT",
                Category = "Subledger Integrity",
                Severity = AccountingHealthSeverity.Warning,
                Title = "Accounts Payable (GL vs. Bills) Mismatch",
                Details = $"Open Bills total {openApTotal:C2}, while GL {apAcctCode} balance is {glApBalance:C2} (diff {(openApTotal - glApBalance):C2}).",
                AffectedCount = 1,
                AffectedAmount = openApTotal - glApBalance,
                ActionKey = AccountingHealthActionKey.OpenPayables,
                ActionLabel = "Review Payables"
            });
        }

        // 6. Bills with Negative BalanceDue (overpaid / misparsed payment)
        var negativeBills = allBills.Where(b =>
            !b.IsVoid &&
            !string.Equals(b.Status, BillStatus.Void, StringComparison.OrdinalIgnoreCase) &&
            b.BalanceDue < -0.01m).ToList();
        if (negativeBills.Count > 0)
        {
            report.Issues.Add(new AccountingHealthIssue
            {
                Code = "AP_NEGATIVE_BALANCE",
                Category = "Payables Hygiene",
                Severity = AccountingHealthSeverity.Critical,
                Title = "Bills with Negative Balance Due",
                Details = $"{negativeBills.Count} bill(s) have AmountPaid exceeding Amount (possible misclassified payment advice or duplicate payment).",
                AffectedCount = negativeBills.Count,
                AffectedAmount = negativeBills.Sum(b => b.BalanceDue),
                ActionKey = AccountingHealthActionKey.OpenPayables,
                ActionLabel = "Inspect Bills"
            });
        }

        // 7. Pending Suggested Invoice Payments from Bill Inbox
        if (pendingSuggestions.Count > 0)
        {
            report.Issues.Add(new AccountingHealthIssue
            {
                Code = "AR_SUGGESTED_PAYMENTS",
                Category = "Receivables Hygiene",
                Severity = AccountingHealthSeverity.Info,
                Title = "Customer Payment Advices Pending Review",
                Details = $"{pendingSuggestions.Count} incoming customer payment advice(s) matched existing invoices ({pendingSuggestions.Sum(s => s.SuggestedAmount):C2}).",
                AffectedCount = pendingSuggestions.Count,
                AffectedAmount = pendingSuggestions.Sum(s => s.SuggestedAmount),
                ActionKey = AccountingHealthActionKey.OpenReceivables,
                ActionLabel = "Review Suggestions"
            });
        }

        // 8. Sales Tax Registration Numbers & Bills Missing Tax Breakdown
        bool jurisdictionIsTaxed = FinancialCalculationEngine.ActiveCombinedTaxRate > 0m;
        if (jurisdictionIsTaxed && (config.TaxRegistrationNumbersByCode == null || config.TaxRegistrationNumbersByCode.Count == 0))
        {
            report.Issues.Add(new AccountingHealthIssue
            {
                Code = "TAX_MISSING_REG_NUMBERS",
                Category = "Sales Tax",
                Severity = AccountingHealthSeverity.Warning,
                Title = "Sales Tax Registration Numbers Not Configured",
                Details = "No GST/TPS or QST/TVQ registration numbers are set in Accounting Settings.",
                AffectedCount = 1,
                ActionKey = AccountingHealthActionKey.OpenBusinessSettings,
                ActionLabel = "Configure Settings"
            });
        }

        var billsMissingTax = allBills.Where(b =>
            !b.IsVoid &&
            !string.Equals(b.Status, BillStatus.Void, StringComparison.OrdinalIgnoreCase) &&
            !b.IsTaxExempt &&
            b.Amount > 0m &&
            b.TaxAmount <= 0m &&
            b.TpsAmount <= 0m &&
            b.TvqAmount <= 0m).ToList();
        if (jurisdictionIsTaxed && billsMissingTax.Count > 0)
        {
            report.Issues.Add(new AccountingHealthIssue
            {
                Code = "TAX_BILLS_MISSING_SPLIT",
                Category = "Sales Tax",
                Severity = AccountingHealthSeverity.Warning,
                Title = "Bills Missing Sales Tax Breakdown (ITC/ITR)",
                Details = $"{billsMissingTax.Count} non-exempt bill(s) totaling {billsMissingTax.Sum(b => b.Amount):C2} have $0 tax split, which may overstate sales tax payable.",
                AffectedCount = billsMissingTax.Count,
                AffectedAmount = billsMissingTax.Sum(b => b.Amount),
                ActionKey = AccountingHealthActionKey.BackfillBillTaxes,
                ActionLabel = "Backfill Tax Split"
            });
        }

        // 10. Unfiled Completed Quarters in Current Fiscal Year
        int completedQuarters = Math.Max(0, ((today.Month - 1) / 3));
        var filedCurrentYear = allTaxFilings.Count(t =>
            t.EndDate.Year == today.Year &&
            (string.Equals(t.Status, TaxFilingStatus.Filed, StringComparison.OrdinalIgnoreCase) ||
             string.Equals(t.Status, TaxFilingStatus.Paid, StringComparison.OrdinalIgnoreCase)));
        if (jurisdictionIsTaxed && completedQuarters > 0 && filedCurrentYear < completedQuarters)
        {
            int missingQuarters = completedQuarters - filedCurrentYear;
            report.Issues.Add(new AccountingHealthIssue
            {
                Code = "TAX_UNFILED_QUARTERS",
                Category = "Sales Tax",
                Severity = AccountingHealthSeverity.Warning,
                Title = "Unfiled Sales Tax Periods",
                Details = $"{missingQuarters} completed quarter(s) in {today.Year} have no filed/remitted sales tax return recorded.",
                AffectedCount = missingQuarters,
                ActionKey = AccountingHealthActionKey.OpenTaxFiling,
                ActionLabel = "Open Tax Filing"
            });
        }

        // 11. Overdue Recurring Expenses
        var overdueRecurring = _overhead?.GetOverdueRecurringOccurrences(today) ?? [];
        if (overdueRecurring.Count > 0)
        {
            int totalMissed = overdueRecurring.Sum(o => o.MissedPeriodsCount);
            report.Issues.Add(new AccountingHealthIssue
            {
                Code = "OVERHEAD_STALE_RECURRING",
                Category = "Overhead",
                Severity = AccountingHealthSeverity.Info,
                Title = "Overdue Recurring Expense Schedules",
                Details = $"{overdueRecurring.Count} recurring expense profile(s) have {totalMissed} past-due billing period(s).",
                AffectedCount = overdueRecurring.Count,
                ActionKey = AccountingHealthActionKey.GenerateDueRecurringBills,
                ActionLabel = "Generate Due Bills"
            });
        }

        // 12. Bank Reconciliation Coverage
        int reconciledCount = allEntries.Count(e => e.IsReconciled);
        if (allStatements.Count == 0 && allEntries.Count > 0)
        {
            report.Issues.Add(new AccountingHealthIssue
            {
                Code = "BANK_NO_STATEMENTS",
                Category = "Bank Reconciliation",
                Severity = AccountingHealthSeverity.Warning,
                Title = "No Bank Statements Imported",
                Details = $"0 bank statements imported and {allEntries.Count - reconciledCount} of {allEntries.Count} journal entries are unreconciled.",
                AffectedCount = allEntries.Count - reconciledCount,
                ActionKey = AccountingHealthActionKey.OpenBankReconciliation,
                ActionLabel = "Import Statements"
            });
        }

        // 13. Payroll / External Payroll Coverage (Informational)
        var activeEmployees = (_hr?.Employees.GetAll() ?? []).Count(e => e.IsActive && !e.IsSystem);
        var wagesCode = _ledger.ResolveAccountCodeByRole(GlSystemRole.WagesAndSalariesExpense, "5400");
        bool hasPayrollPosted = tbByCode.TryGetValue(wagesCode, out var wagesRow) && Math.Abs(wagesRow.EndingBalance) > 0.01m;
        if (activeEmployees > 0 && !hasPayrollPosted)
        {
            report.Issues.Add(new AccountingHealthIssue
            {
                Code = "PAYROLL_NO_POSTINGS",
                Category = "Payroll",
                Severity = AccountingHealthSeverity.Info,
                Title = "No Payroll Expenses Posted in Current Fiscal Year",
                Details = $"{activeEmployees} active employee(s) found, but GL {wagesCode} has $0.00. Run Payroll in Spokes or post summary journal entries from your external payroll provider.",
                AffectedCount = activeEmployees,
                ActionKey = AccountingHealthActionKey.OpenPayroll,
                ActionLabel = "Open Payroll"
            });
        }

        // 14. Period Lock Date
        if (!config.PeriodLockDate.HasValue && allEntries.Count > 0)
        {
            report.Issues.Add(new AccountingHealthIssue
            {
                Code = "CLOSE_NO_PERIOD_LOCK",
                Category = "Period Close",
                Severity = AccountingHealthSeverity.Info,
                Title = "Accounting Period Lock Date Not Set",
                Details = "Set a Period Lock Date once reconciled to protect closed months from accidental edits.",
                AffectedCount = 1,
                ActionKey = AccountingHealthActionKey.OpenGeneralLedger,
                ActionLabel = "Review GL"
            });
        }

        // Build Data-Driven Guided Mid-Year Cutover & Close Checklist
        report.CloseChecklist =
        [
            new AccountingCloseChecklistStep
            {
                Key = "gl-integrity",
                Title = "Opening Balance Sheet & GL Integrity",
                Summary = unbalanced.Count == 0 && !suspectedZeroedLedger && !(tbByCode.TryGetValue(ob3900Code, out var r39) && Math.Abs(r39.EndingBalance) > 0.05m) && missingJeTotal == 0
                    ? "Trial Balance balanced, no migration wipe, all posted documents synced."
                    : "Resolve GL integrity / migration issues before reconciling.",
                IsComplete = unbalanced.Count == 0 && !suspectedZeroedLedger && !(tbByCode.TryGetValue(ob3900Code, out var r39b) && Math.Abs(r39b.EndingBalance) > 0.05m) && missingJeTotal == 0,
                HasBlocker = unbalanced.Count > 0 || suspectedZeroedLedger,
                ActionKey = suspectedZeroedLedger ? AccountingHealthActionKey.OpenMigration : AccountingHealthActionKey.OpenGeneralLedger
            },
            new AccountingCloseChecklistStep
            {
                Key = "subledger-cleanup",
                Title = "AP Subledger Cleanup (Negative Balances & Tax Splits)",
                Summary = negativeBills.Count == 0 && billsMissingTax.Count == 0
                    ? "No negative-balance bills or missing tax splits."
                    : $"{negativeBills.Count} negative bill(s), {billsMissingTax.Count} bill(s) missing tax split.",
                IsComplete = negativeBills.Count == 0 && billsMissingTax.Count == 0,
                HasBlocker = negativeBills.Count > 0,
                ActionKey = AccountingHealthActionKey.OpenPayables
            },
            new AccountingCloseChecklistStep
            {
                Key = "bank-reconciliation",
                Title = "Bank & Credit Card Statements Reconciled",
                Summary = allStatements.Count > 0 && allStatements.All(s => string.Equals(s.Status, BankStatementStatus.Reconciled, StringComparison.OrdinalIgnoreCase))
                    ? $"{allStatements.Count} bank statement(s) imported and locked."
                    : $"{allStatements.Count} statement(s) imported; {reconciledCount}/{allEntries.Count} JEs reconciled.",
                IsComplete = allStatements.Count > 0 && allStatements.All(s => string.Equals(s.Status, BankStatementStatus.Reconciled, StringComparison.OrdinalIgnoreCase)),
                HasBlocker = false,
                ActionKey = AccountingHealthActionKey.OpenBankReconciliation
            },
            new AccountingCloseChecklistStep
            {
                Key = "payroll",
                Title = "Payroll / External Payroll JEs Recorded",
                Summary = hasPayrollPosted || activeEmployees == 0
                    ? "Payroll postings detected in GL."
                    : "No payroll expenses posted yet for this fiscal year.",
                IsComplete = hasPayrollPosted || activeEmployees == 0,
                HasBlocker = false,
                ActionKey = AccountingHealthActionKey.OpenPayroll
            },
            new AccountingCloseChecklistStep
            {
                Key = "sales-tax",
                Title = "Sales Tax Registration & Filings Up to Date",
                Summary = !jurisdictionIsTaxed || ((config.TaxRegistrationNumbersByCode?.Count ?? 0) > 0 && filedCurrentYear >= completedQuarters)
                    ? "Tax registration configured and completed quarters filed."
                    : "Configure TPS/TVQ registration numbers and record completed tax periods.",
                IsComplete = !jurisdictionIsTaxed || ((config.TaxRegistrationNumbersByCode?.Count ?? 0) > 0 && filedCurrentYear >= completedQuarters),
                HasBlocker = false,
                ActionKey = AccountingHealthActionKey.OpenTaxFiling
            },
            new AccountingCloseChecklistStep
            {
                Key = "period-lock",
                Title = "Period Lock Date Set & Financial Statements Ready",
                Summary = config.PeriodLockDate.HasValue
                    ? $"Locked through {config.PeriodLockDate.Value:yyyy-MM-dd}."
                    : "No Period Lock Date set yet.",
                IsComplete = config.PeriodLockDate.HasValue,
                HasBlocker = false,
                ActionKey = AccountingHealthActionKey.OpenGeneralLedger
            }
        ];

        return report;
    }

    public int ResyncMissingJournalEntries(Employee? actor = null)
    {
        if (actor != null)
        {
            DomainActorGuard.RequireAnyPermission(actor, "re-sync missing journal entries", allowSuspended: false,
                AppPermissions.Finance.AccessAccounting);
        }

        var existingSourceIds = (_accounting.JournalEntries?.GetAll() ?? [])
            .Where(e => !string.IsNullOrWhiteSpace(e.SourceId))
            .Select(e => e.SourceId!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var projectsById = (_projects?.Projects.GetAll() ?? []).ToDictionary(p => p.Id, StringComparer.OrdinalIgnoreCase);
        int synced = 0;

        foreach (var inv in _accounting.Invoices?.GetAll() ?? [])
        {
            if (!InvoiceStatus.IsPosted(inv.Status) ||
                inv.GrandTotal <= 0m ||
                existingSourceIds.Contains(inv.Id))
                continue;

            projectsById.TryGetValue(inv.ProjectId, out var proj);
            _ledger.SyncInvoice(inv, proj?.Client?.BusinessName ?? proj?.Name);
            synced++;
        }

        foreach (var bill in _accounting.Bills?.GetAll() ?? [])
        {
            if (bill.IsVoid ||
                string.Equals(bill.Status, BillStatus.Void, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(bill.Status, BillStatus.AwaitingApproval, StringComparison.OrdinalIgnoreCase) ||
                bill.Amount <= 0m ||
                existingSourceIds.Contains(bill.Id))
                continue;

            _ledger.SyncBill(bill);
            synced++;
        }

        foreach (var exp in _accounting.ExpenseReports?.GetAll() ?? [])
        {
            if (string.Equals(exp.Status, ExpenseReportStatus.Draft, StringComparison.OrdinalIgnoreCase) ||
                exp.TotalAmount <= 0m ||
                existingSourceIds.Contains(exp.Id))
                continue;

            _ledger.SyncExpenseReport(exp);
            synced++;
        }

        return synced;
    }
}
