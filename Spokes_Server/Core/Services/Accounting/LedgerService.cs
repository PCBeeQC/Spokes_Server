namespace Spokes_Server.Core.Services.Accounting;

using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Spokes_Server.Aggregate.Contexts;
using Spokes_Server.Core.Models.Accounting;
using Spokes_Server.Core.Models.HR;

public class TrialBalanceRow
{
    public string AccountCode { get; set; } = string.Empty;
    public string AccountName { get; set; } = string.Empty;
    public string AccountNameFr { get; set; } = string.Empty;
    public string AccountType { get; set; } = string.Empty;
    public string SubType { get; set; } = string.Empty;
    public string StandardTaxCode { get; set; } = string.Empty;
    public bool IsDebitNormal { get; set; }

    public decimal OpeningBalance { get; set; }
    public decimal PeriodDebits { get; set; }
    public decimal PeriodCredits { get; set; }
    public decimal NetMovement => IsDebitNormal ? PeriodDebits - PeriodCredits : PeriodCredits - PeriodDebits;
    public decimal EndingBalance => OpeningBalance + NetMovement;

    public decimal TrialBalanceDebitColumn =>
        IsDebitNormal
            ? (EndingBalance >= 0m ? EndingBalance : 0m)
            : (EndingBalance < 0m ? Math.Abs(EndingBalance) : 0m);

    public decimal TrialBalanceCreditColumn =>
        !IsDebitNormal
            ? (EndingBalance >= 0m ? EndingBalance : 0m)
            : (EndingBalance < 0m ? Math.Abs(EndingBalance) : 0m);
}

public class TrialBalanceReport
{
    public DateTime StartDate { get; set; }
    public DateTime EndDate { get; set; }
    public List<TrialBalanceRow> Rows { get; set; } = [];
    public decimal TotalPeriodDebits => Math.Round(Rows.Sum(r => r.PeriodDebits), 2);
    public decimal TotalPeriodCredits => Math.Round(Rows.Sum(r => r.PeriodCredits), 2);
    public decimal TotalEndingDebitColumn => Math.Round(Rows.Sum(r => r.TrialBalanceDebitColumn), 2);
    public decimal TotalEndingCreditColumn => Math.Round(Rows.Sum(r => r.TrialBalanceCreditColumn), 2);
    public bool IsBalanced => Math.Abs(TotalPeriodDebits - TotalPeriodCredits) <= 0.01m;
}

public class FinancialStatementLine
{
    public string AccountCode { get; set; } = string.Empty;
    public string AccountName { get; set; } = string.Empty;
    public string AccountNameFr { get; set; } = string.Empty;
    public string StandardTaxCode { get; set; } = string.Empty;
    public string SubType { get; set; } = string.Empty;
    public decimal Amount { get; set; }

    public string GetLocalizedName(bool isFrench) =>
        isFrench && !string.IsNullOrWhiteSpace(AccountNameFr)
            ? AccountNameFr
            : (!string.IsNullOrWhiteSpace(AccountName) ? AccountName : AccountNameFr);
}

public class IncomeStatementReport
{
    public DateTime StartDate { get; set; }
    public DateTime EndDate { get; set; }
    public List<FinancialStatementLine> RevenueLines { get; set; } = [];
    public List<FinancialStatementLine> DirectCostLines { get; set; } = [];
    public List<FinancialStatementLine> PayrollExpenseLines { get; set; } = [];
    public List<FinancialStatementLine> OverheadExpenseLines { get; set; } = [];

    public decimal TotalRevenue => Math.Round(RevenueLines.Sum(l => l.Amount), 2);
    public decimal TotalDirectCosts => Math.Round(DirectCostLines.Sum(l => l.Amount), 2);
    public decimal GrossProfit => Math.Round(TotalRevenue - TotalDirectCosts, 2);
    public decimal TotalPayrollExpenses => Math.Round(PayrollExpenseLines.Sum(l => l.Amount), 2);
    public decimal TotalOverheadExpenses => Math.Round(OverheadExpenseLines.Sum(l => l.Amount), 2);
    public decimal TotalOperatingExpenses => Math.Round(TotalPayrollExpenses + TotalOverheadExpenses, 2);
    public decimal NetIncome => Math.Round(GrossProfit - TotalOperatingExpenses, 2);
}

public class BalanceSheetReport
{
    public DateTime AsOfDate { get; set; }
    public List<FinancialStatementLine> AssetLines { get; set; } = [];
    public List<FinancialStatementLine> LiabilityLines { get; set; } = [];
    public List<FinancialStatementLine> EquityLines { get; set; } = [];
    public decimal CurrentYearNetIncome { get; set; }

    public decimal TotalAssets => Math.Round(AssetLines.Sum(l => l.Amount), 2);
    public decimal TotalLiabilities => Math.Round(LiabilityLines.Sum(l => l.Amount), 2);
    public decimal TotalEquityBeforeNetIncome => Math.Round(EquityLines.Sum(l => l.Amount), 2);
    public decimal TotalEquity => Math.Round(TotalEquityBeforeNetIncome + CurrentYearNetIncome, 2);
    public decimal TotalLiabilitiesAndEquity => Math.Round(TotalLiabilities + TotalEquity, 2);
    public bool IsBalanced => Math.Abs(TotalAssets - TotalLiabilitiesAndEquity) <= 0.02m;
}

public interface ILedgerService
{
    void SyncInvoice(Invoice invoice, string? clientName = null);
    void SyncBill(Bill bill);
    void SyncRecurringExpense(RecurringExpense profile);
    void SyncExpenseReport(ExpenseReport report);
    void SyncCommission(CommissionLedgerRecord record);
    void SyncPayRun(PayRun payRun);
    JournalEntry? SyncDirectTransaction(DirectTransaction transaction);
    List<JournalLine> BuildDirectTransactionJournalLines(DirectTransaction transaction);
    void RemoveEntriesForSource(string sourceType, string sourceId);
    JournalEntry SaveManualJournalEntry(JournalEntry entry);
    void DeleteManualJournalEntry(string entryId);
    int RebuildOperationalLedger();
    TrialBalanceReport CalculateTrialBalance(DateTime startDate, DateTime endDate, bool includeZeroAccounts = false);
    IncomeStatementReport CalculateIncomeStatement(DateTime startDate, DateTime endDate);
    BalanceSheetReport CalculateBalanceSheet(DateTime asOfDate);
    string ResolveAccountCodeByRole(string systemRole, string fallbackCode);
    DateTime GetFiscalYearStart(DateTime referenceDate);
}

public class LedgerService : ILedgerService
{
    private readonly AccountingContext _accountingContext;
    private readonly ProjectsContext? _projectsContext;
    private readonly HrContext? _hrContext;
    private readonly IJurisdictionTemplateService? _templateService;
    private readonly ILogger<LedgerService>? _logger;

    [ActivatorUtilitiesConstructor]
    public LedgerService(
        AccountingContext accountingContext,
        ProjectsContext? projectsContext = null,
        HrContext? hrContext = null,
        IJurisdictionTemplateService? templateService = null,
        ILogger<LedgerService>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(accountingContext);
        _accountingContext = accountingContext;
        _projectsContext = projectsContext;
        _hrContext = hrContext;
        _templateService = templateService;
        _logger = logger;

        EnsureDefaultAccountsAndInitialSync();
    }

    public string ResolveAccountCodeByRole(string systemRole, string fallbackCode)
    {
        var acct = _accountingContext.GlAccounts?.GetBySystemRole(systemRole);
        if (acct != null && !string.IsNullOrWhiteSpace(acct.Code))
        {
            return acct.Code;
        }
        return fallbackCode;
    }

    public string GetAccountName(string code)
    {
        var acct = _accountingContext.GlAccounts?.GetByCode(code);
        return acct?.GetLocalizedName() ?? code;
    }

    public void SyncInvoice(Invoice invoice, string? clientName = null)
    {
        if (invoice == null || _accountingContext.JournalEntries == null) return;

        // Draft or Void invoices do not remain on the active General Ledger
        var isPosted = string.Equals(invoice.Status, InvoiceStatus.Final, StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(invoice.Status, InvoiceStatus.Paid, StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(invoice.Status, InvoiceStatus.WrittenOff, StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(invoice.Status, InvoiceStatus.Partial, StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(invoice.Status, InvoiceStatus.Sent, StringComparison.OrdinalIgnoreCase);

        if (!isPosted || invoice.GrandTotal <= 0m)
        {
            RemoveEntriesForSource(JournalSourceType.Invoice, invoice.Id);
            CleanupStalePrefixEntries(JournalSourceType.InvoicePayment, $"{invoice.Id}:");
            CleanupStalePrefixEntries(JournalSourceType.InvoiceWriteOff, $"{invoice.Id}:");
            return;
        }

        var resolvedClientName = clientName;
        if (string.IsNullOrWhiteSpace(resolvedClientName) && !string.IsNullOrWhiteSpace(invoice.ProjectId))
        {
            var proj = _projectsContext?.Projects.GetById(invoice.ProjectId);
            resolvedClientName = proj?.Client?.BusinessName ?? proj?.Name ?? string.Empty;
        }

        var rate = invoice.ExchangeRate > 0m ? invoice.ExchangeRate : 1.0m;
        var arCode = ResolveAccountCodeByRole(GlSystemRole.AccountsReceivable, "1200");
        var defaultRevCode = !string.IsNullOrWhiteSpace(invoice.GlAccountCode)
            ? invoice.GlAccountCode!
            : ResolveAccountCodeByRole(GlSystemRole.ServiceRevenue, "4000");
        var matRevCode = ResolveAccountCodeByRole(GlSystemRole.MaterialRevenue, "4100");

        var lines = new List<JournalLine>();
        var totalArFunctional = Round(invoice.GrandTotal * rate);

        // 1. Debit Accounts Receivable
        lines.Add(CreateLine(arCode, totalArFunctional, 0m, $"Invoice {invoice.InvoiceNumber}", invoice.ProjectId));

        if (invoice.IsOpeningBalance)
        {
            // Pre-cutover open receivable clears through Opening Balance Equity (3900) so revenue & sales tax are not double-counted against the Trial Balance.
            var contraCode = ResolveAccountCodeByRole(GlSystemRole.OpeningBalanceEquity, "3900");
            lines.Add(CreateLine(contraCode, 0m, totalArFunctional, $"Opening Balance Equity Clearing — {invoice.InvoiceNumber}", invoice.ProjectId));
        }
        else
        {
            // 2. Credit Revenue lines (grouped by GL account)
            if (invoice.LineItems != null && invoice.LineItems.Count > 0)
            {
                var grouped = invoice.LineItems.GroupBy(l =>
                    !string.IsNullOrWhiteSpace(l.GlAccountCode)
                        ? l.GlAccountCode!
                        : (string.Equals(l.Type, "Material", StringComparison.OrdinalIgnoreCase) || string.Equals(l.SourceType, "PO", StringComparison.OrdinalIgnoreCase)
                            ? matRevCode
                            : defaultRevCode));

                foreach (var grp in grouped)
                {
                    var amt = Round(grp.Sum(x => x.Total) * rate);
                    if (amt == 0m) continue;
                    if (amt > 0m)
                    {
                        lines.Add(CreateLine(grp.Key, 0m, amt, $"Revenue — {invoice.InvoiceNumber}", invoice.ProjectId));
                    }
                    else
                    {
                        lines.Add(CreateLine(grp.Key, Math.Abs(amt), 0m, $"Credit adjustment — {invoice.InvoiceNumber}", invoice.ProjectId));
                    }
                }
            }
            else if (invoice.SubTotal > 0m)
            {
                var subFunc = Round(invoice.SubTotal * rate);
                lines.Add(CreateLine(defaultRevCode, 0m, subFunc, $"Revenue — {invoice.InvoiceNumber}", invoice.ProjectId));
            }

            // 3. Credit Sales Taxes Collected
            if (invoice.TaxAmount > 0m)
            {
                var taxLines = GetEffectiveInvoiceTaxes(invoice);
                foreach (var tl in taxLines)
                {
                    var taxFunc = Round(tl.Amount * rate);
                    if (taxFunc <= 0m) continue;
                    var glCode = !string.IsNullOrWhiteSpace(tl.GlAccountCode)
                        ? tl.GlAccountCode
                        : ResolveAccountCodeByRole(GlSystemRole.FederalTaxCollected, "2160");
                    lines.Add(CreateLine(glCode, 0m, taxFunc, $"{tl.Code} Collected — {invoice.InvoiceNumber}", invoice.ProjectId, tl.Code));
                }
            }
        }

        BalanceRoundingPenny(lines, isTargetCredit: true);

        UpsertSystemEntry(
            JournalSourceType.Invoice,
            invoice.Id,
            invoice.Date,
            $"Client Invoice {invoice.InvoiceNumber}",
            invoice.InvoiceNumber,
            resolvedClientName ?? string.Empty,
            invoice.ProjectId,
            invoice.Currency,
            rate,
            lines);

        // 4. Sync Invoice Payments (with FX Gain/Loss at settlement support)
        SyncInvoicePayments(invoice, resolvedClientName ?? string.Empty, arCode, rate);

        // 5. Sync Non-Cash Balance Write-Offs (never touches Bank/Credit Card accounts)
        SyncInvoiceWriteOffs(invoice, resolvedClientName ?? string.Empty, arCode, rate);
    }

    private void SyncInvoicePayments(Invoice invoice, string clientName, string arCode, decimal invoiceRate)
    {
        var activePaymentKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (invoice.Payments != null)
        {
            foreach (var p in invoice.Payments)
            {
                if (p.Amount <= 0m) continue;
                var sourceKey = $"{invoice.Id}:{p.Id}";
                activePaymentKeys.Add(sourceKey);

                var bankCode = !string.IsNullOrWhiteSpace(p.BankGlAccountCode)
                    ? p.BankGlAccountCode!
                    : (string.Equals(p.Method, "Credit Card", StringComparison.OrdinalIgnoreCase)
                        ? ResolveAccountCodeByRole(GlSystemRole.CorporateCreditCard, "2120")
                        : ResolveAccountCodeByRole(GlSystemRole.OperatingBank, "1020"));

                var arClearedFunctional = Round(p.Amount * invoiceRate);
                var bankSettledFunctional = p.SettledFunctionalAmount.HasValue && p.SettledFunctionalAmount.Value > 0m
                    ? Round(p.SettledFunctionalAmount.Value)
                    : arClearedFunctional;

                var pLines = new List<JournalLine>
                {
                    CreateLine(bankCode, bankSettledFunctional, 0m, $"Payment received — {invoice.InvoiceNumber} ({p.Method})", invoice.ProjectId),
                    CreateLine(arCode, 0m, arClearedFunctional, $"AR settlement — {invoice.InvoiceNumber}", invoice.ProjectId)
                };

                var fxDiff = Round(bankSettledFunctional - arClearedFunctional);
                if (Math.Abs(fxDiff) >= 0.01m)
                {
                    var fxCode = ResolveAccountCodeByRole(GlSystemRole.ForeignExchangeGainLoss, "5850");
                    if (fxDiff > 0m)
                    {
                        // Received more functional currency than invoiced -> FX Gain (Credit)
                        pLines.Add(CreateLine(fxCode, 0m, fxDiff, $"FX Gain on settlement — {invoice.InvoiceNumber}", invoice.ProjectId));
                    }
                    else
                    {
                        // Received less functional currency than invoiced -> FX Loss (Debit)
                        pLines.Add(CreateLine(fxCode, Math.Abs(fxDiff), 0m, $"FX Loss on settlement — {invoice.InvoiceNumber}", invoice.ProjectId));
                    }
                }

                UpsertSystemEntry(
                    JournalSourceType.InvoicePayment,
                    sourceKey,
                    p.Date,
                    $"Payment received for {invoice.InvoiceNumber} ({p.Reference})",
                    !string.IsNullOrWhiteSpace(p.Reference) ? p.Reference : invoice.InvoiceNumber,
                    clientName,
                    invoice.ProjectId,
                    invoice.Currency,
                    invoiceRate,
                    pLines);
            }
        }

        // Clean up any removed payment records for this invoice
        CleanupStalePrefixEntries(JournalSourceType.InvoicePayment, $"{invoice.Id}:", activePaymentKeys);
    }

    private void SyncInvoiceWriteOffs(Invoice invoice, string clientName, string arCode, decimal invoiceRate)
    {
        var activeWriteOffKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (invoice.WriteOffs != null)
        {
            foreach (var w in invoice.WriteOffs)
            {
                if (w.Amount <= 0m) continue;
                var sourceKey = $"{invoice.Id}:{w.Id}";
                activeWriteOffKeys.Add(sourceKey);

                var offsetCode = !string.IsNullOrWhiteSpace(w.GlAccountCode)
                    ? w.GlAccountCode!
                    : ResolveAccountCodeByRole(GlSystemRole.BadDebtsAndWriteOffs, "5860");

                var amountFunctional = Round(w.Amount * invoiceRate);
                if (amountFunctional <= 0m) continue;

                var reasonSuffix = string.IsNullOrWhiteSpace(w.Reason) ? string.Empty : $": {w.Reason.Trim()}";
                var wLines = new List<JournalLine>
                {
                    CreateLine(offsetCode, amountFunctional, 0m, $"Balance write-off — {invoice.InvoiceNumber}{reasonSuffix}", invoice.ProjectId),
                    CreateLine(arCode, 0m, amountFunctional, $"AR write-off — {invoice.InvoiceNumber}", invoice.ProjectId)
                };

                UpsertSystemEntry(
                    JournalSourceType.InvoiceWriteOff,
                    sourceKey,
                    w.Date,
                    $"Balance write-off for {invoice.InvoiceNumber}{reasonSuffix}",
                    invoice.InvoiceNumber,
                    clientName,
                    invoice.ProjectId,
                    invoice.Currency,
                    invoiceRate,
                    wLines);
            }
        }

        CleanupStalePrefixEntries(JournalSourceType.InvoiceWriteOff, $"{invoice.Id}:", activeWriteOffKeys);
    }

    public void SyncBill(Bill bill)
    {
        if (bill == null || _accountingContext.JournalEntries == null) return;

        if (bill.IsVoid || bill.Amount <= 0m)
        {
            RemoveEntriesForSource(JournalSourceType.Bill, bill.Id);
            CleanupStalePrefixEntries(JournalSourceType.BillPayment, $"{bill.Id}:");
            CleanupStalePrefixEntries(JournalSourceType.BillWriteOff, $"{bill.Id}:");
            return;
        }

        var rate = bill.ExchangeRate > 0m ? bill.ExchangeRate : 1.0m;
        var apCode = ResolveAccountCodeByRole(GlSystemRole.AccountsPayable, "2100");
        var defaultExpenseCode = ResolveDefaultBillExpenseAccount(bill);

        string? projectId = null;
        if (!string.IsNullOrWhiteSpace(bill.PurchaseOrderId))
        {
            var po = _accountingContext.PurchaseOrders?.GetById(bill.PurchaseOrderId);
            projectId = po?.LineItems?.FirstOrDefault(i => !string.IsNullOrWhiteSpace(i.ProjectId))?.ProjectId;
        }

        var preTax = bill.SubTotal > 0m
            ? bill.SubTotal
            : Math.Max(0m, bill.Amount - (bill.TaxAmount > 0m ? bill.TaxAmount : (bill.TpsAmount + bill.TvqAmount)));

        var totalFunctional = Round(bill.Amount * rate);
        var preTaxFunctional = Round(preTax * rate);

        var lines = new List<JournalLine>();
        var isCreditNote = bill.IsCreditNote;

        if (bill.IsOpeningBalance)
        {
            // Pre-cutover open payable clears through Opening Balance Equity (3900) so expenses & ITCs are not double-counted.
            var contraCode = ResolveAccountCodeByRole(GlSystemRole.OpeningBalanceEquity, "3900");
            lines.Add(CreateLine(
                contraCode,
                isCreditNote ? 0m : totalFunctional,
                isCreditNote ? totalFunctional : 0m,
                $"Opening Balance Equity Clearing — {bill.SupplierName} ({bill.InvoiceNumber})",
                projectId));

            lines.Add(CreateLine(
                apCode,
                isCreditNote ? totalFunctional : 0m,
                isCreditNote ? 0m : totalFunctional,
                $"Opening AP — {bill.SupplierName} ({bill.InvoiceNumber})",
                projectId));
        }
        else
        {
            // 1. Expense lines (Debit for normal bill, Credit for credit note)
            if (bill.LineItems != null && bill.LineItems.Count > 0 && Math.Abs(bill.LineItems.Sum(l => l.Total) - preTax) <= 0.05m)
            {
                foreach (var grp in bill.LineItems.GroupBy(l => !string.IsNullOrWhiteSpace(l.GlAccountCode) ? l.GlAccountCode! : defaultExpenseCode))
                {
                    var lineFunc = Round(grp.Sum(x => x.Total) * rate);
                    if (lineFunc <= 0m) continue;
                    lines.Add(CreateLine(
                        grp.Key,
                        isCreditNote ? 0m : lineFunc,
                        isCreditNote ? lineFunc : 0m,
                        !string.IsNullOrWhiteSpace(bill.Description) ? bill.Description : $"Supplier Bill {bill.InvoiceNumber}",
                        projectId));
                }
            }
            else if (preTaxFunctional > 0m)
            {
                lines.Add(CreateLine(
                    defaultExpenseCode,
                    isCreditNote ? 0m : preTaxFunctional,
                    isCreditNote ? preTaxFunctional : 0m,
                    !string.IsNullOrWhiteSpace(bill.Description) ? bill.Description : $"Supplier Bill {bill.InvoiceNumber}",
                    projectId));
            }

            // 2. Input Tax Credits (CTI / ITC & RTI / ITR)
            if (!bill.IsTaxExempt)
            {
                foreach (var tl in GetEffectiveBillTaxes(bill, defaultExpenseCode))
                {
                    var taxFunc = Round(tl.Amount * rate);
                    if (taxFunc <= 0m) continue;
                    lines.Add(CreateLine(
                        tl.GlAccountCode,
                        isCreditNote ? 0m : taxFunc,
                        isCreditNote ? taxFunc : 0m,
                        $"{tl.Code} ITC/ITR — {bill.SupplierName}",
                        projectId,
                        tl.Code));
                }
            }

            // 3. Accounts Payable (Credit for normal bill, Debit for credit note)
            lines.Add(CreateLine(
                apCode,
                isCreditNote ? totalFunctional : 0m,
                isCreditNote ? 0m : totalFunctional,
                $"AP — {bill.SupplierName} ({bill.InvoiceNumber})",
                projectId));

            BalanceRoundingPenny(lines, isTargetCredit: isCreditNote);
        }

        UpsertSystemEntry(
            JournalSourceType.Bill,
            bill.Id,
            bill.Date,
            !string.IsNullOrWhiteSpace(bill.Description) ? bill.Description : $"Bill from {bill.SupplierName}",
            bill.InvoiceNumber ?? string.Empty,
            bill.SupplierName,
            projectId,
            bill.Currency,
            rate,
            lines);

        SyncBillPayments(bill, apCode, projectId, rate);
        SyncBillWriteOffs(bill, apCode, projectId, rate);
    }

    private void SyncBillPayments(Bill bill, string apCode, string? projectId, decimal billRate)
    {
        var activeKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var payments = bill.Payments != null && bill.Payments.Count > 0
            ? bill.Payments
            : (!bill.HasWriteOffs && !string.Equals(bill.Status, BillStatus.WrittenOff, StringComparison.OrdinalIgnoreCase) &&
               (string.Equals(bill.Status, BillStatus.Paid, StringComparison.OrdinalIgnoreCase) || bill.DatePaid.HasValue)
                ? [new PaymentRecord { Id = "default", Amount = bill.Amount, Date = bill.DatePaid ?? bill.Date, Reference = bill.PaymentReference, Method = "Bank" }]
                : []);

        foreach (var p in payments)
        {
            if (p.Amount <= 0m) continue;
            var key = $"{bill.Id}:{p.Id}";
            activeKeys.Add(key);

            var bankCode = !string.IsNullOrWhiteSpace(p.BankGlAccountCode)
                ? p.BankGlAccountCode!
                : (string.Equals(p.Method, "Credit Card", StringComparison.OrdinalIgnoreCase)
                    ? ResolveAccountCodeByRole(GlSystemRole.CorporateCreditCard, "2120")
                    : ResolveAccountCodeByRole(GlSystemRole.OperatingBank, "1020"));

            var apClearedFunc = Round(p.Amount * billRate);
            var bankSettledFunc = p.SettledFunctionalAmount.HasValue && p.SettledFunctionalAmount.Value > 0m
                ? Round(p.SettledFunctionalAmount.Value)
                : apClearedFunc;

            var pLines = new List<JournalLine>
            {
                CreateLine(apCode, bill.IsCreditNote ? 0m : apClearedFunc, bill.IsCreditNote ? apClearedFunc : 0m, $"AP Payment — {bill.SupplierName}", projectId),
                CreateLine(bankCode, bill.IsCreditNote ? bankSettledFunc : 0m, bill.IsCreditNote ? 0m : bankSettledFunc, $"Disbursement — {bill.SupplierName}", projectId)
            };

            var fxDiff = Round(bankSettledFunc - apClearedFunc);
            if (Math.Abs(fxDiff) >= 0.01m && !bill.IsCreditNote)
            {
                var fxCode = ResolveAccountCodeByRole(GlSystemRole.ForeignExchangeGainLoss, "5850");
                if (fxDiff > 0m)
                {
                    // Paid more functional currency than booked -> FX Loss (Debit)
                    pLines.Add(CreateLine(fxCode, fxDiff, 0m, $"FX Loss on bill payment — {bill.SupplierName}", projectId));
                }
                else
                {
                    // Paid less functional currency than booked -> FX Gain (Credit)
                    pLines.Add(CreateLine(fxCode, 0m, Math.Abs(fxDiff), $"FX Gain on bill payment — {bill.SupplierName}", projectId));
                }
            }

            UpsertSystemEntry(
                JournalSourceType.BillPayment,
                key,
                p.Date,
                $"Bill payment to {bill.SupplierName}",
                !string.IsNullOrWhiteSpace(p.Reference) ? p.Reference : (bill.InvoiceNumber ?? string.Empty),
                bill.SupplierName,
                projectId,
                bill.Currency,
                billRate,
                pLines);
        }

        CleanupStalePrefixEntries(JournalSourceType.BillPayment, $"{bill.Id}:", activeKeys);
    }

    private void SyncBillWriteOffs(Bill bill, string apCode, string? projectId, decimal billRate)
    {
        var activeKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (bill.WriteOffs != null)
        {
            foreach (var w in bill.WriteOffs)
            {
                if (w.Amount <= 0m) continue;
                var key = $"{bill.Id}:{w.Id}";
                activeKeys.Add(key);

                var offsetCode = !string.IsNullOrWhiteSpace(w.GlAccountCode)
                    ? w.GlAccountCode!
                    : ResolveAccountCodeByRole(GlSystemRole.BadDebtsAndWriteOffs, "5860");

                var amountFunc = Round(w.Amount * billRate);
                if (amountFunc <= 0m) continue;

                var reasonSuffix = string.IsNullOrWhiteSpace(w.Reason) ? string.Empty : $": {w.Reason.Trim()}";
                var wLines = new List<JournalLine>
                {
                    CreateLine(apCode, bill.IsCreditNote ? 0m : amountFunc, bill.IsCreditNote ? amountFunc : 0m, $"AP write-off — {bill.SupplierName}", projectId),
                    CreateLine(offsetCode, bill.IsCreditNote ? amountFunc : 0m, bill.IsCreditNote ? 0m : amountFunc, $"Balance write-off — {bill.SupplierName}{reasonSuffix}", projectId)
                };

                UpsertSystemEntry(
                    JournalSourceType.BillWriteOff,
                    key,
                    w.Date,
                    $"Bill balance write-off — {bill.SupplierName}{reasonSuffix}",
                    bill.InvoiceNumber ?? string.Empty,
                    bill.SupplierName,
                    projectId,
                    bill.Currency,
                    billRate,
                    wLines);
            }
        }

        CleanupStalePrefixEntries(JournalSourceType.BillWriteOff, $"{bill.Id}:", activeKeys);
    }

    public void SyncRecurringExpense(RecurringExpense profile)
    {
        if (profile == null || _accountingContext.JournalEntries == null) return;

        // Deduplicate against standalone Bills that have LinkedRecurringExpenseId == profile.Id
        var linkedBills = _accountingContext.Bills?.GetAll()
            .Where(b => !b.IsVoid && string.Equals(b.LinkedRecurringExpenseId, profile.Id, StringComparison.OrdinalIgnoreCase))
            .ToList() ?? [];

        var activeBillKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var activePayKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var activeWriteOffKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (profile.History != null)
        {
            foreach (var rec in profile.History)
            {
                if (rec == null || rec.Amount <= 0m) continue;

                // If a standalone Bill already exists for this recurring profile in the same year/month or matching invoice #, skip to prevent double-counting!
                if (FinancialCalculationEngine.IsRecurringRecordCoveredByBills(rec, linkedBills)) continue;

                var billKey = $"{profile.Id}:{rec.Id}";
                activeBillKeys.Add(billKey);

                var apCode = ResolveAccountCodeByRole(GlSystemRole.AccountsPayable, "2100");
                var expCode = !string.IsNullOrWhiteSpace(rec.GlAccountCode)
                    ? rec.GlAccountCode!
                    : (!string.IsNullOrWhiteSpace(profile.GlAccountCode)
                        ? profile.GlAccountCode!
                        : ResolveAccountCodeByRole(GlSystemRole.RentAndUtilitiesExpense, "5610"));

                var (subTotal, tps, tvq, _) = FinancialCalculationEngine.ResolveRecurringRecordTaxSplit(profile, rec);

                var lines = new List<JournalLine>
                {
                    CreateLine(expCode, Round(subTotal), 0m, $"{profile.Name} — {profile.Supplier}", null)
                };

                if (tps > 0m)
                {
                    var tpsCode = ResolveAccountCodeByRole(GlSystemRole.FederalTaxRecoverable, "2165");
                    lines.Add(CreateLine(tpsCode, Round(tps), 0m, $"TPS ITC — {profile.Name}", null, "TPS"));
                }
                if (tvq > 0m)
                {
                    var tvqCode = ResolveAccountCodeByRole(GlSystemRole.RegionalTaxRecoverable, "2175");
                    lines.Add(CreateLine(tvqCode, Round(tvq), 0m, $"TVQ ITR — {profile.Name}", null, "TVQ"));
                }

                lines.Add(CreateLine(apCode, 0m, Round(rec.Amount), $"AP — {profile.Supplier} ({profile.Name})", null));
                BalanceRoundingPenny(lines, isTargetCredit: false);

                UpsertSystemEntry(
                    JournalSourceType.RecurringBill,
                    billKey,
                    rec.Date,
                    $"Recurring Bill: {profile.Name}",
                    rec.InvoiceNumber ?? string.Empty,
                    profile.Supplier,
                    null,
                    "CAD",
                    1.0m,
                    lines);

                var payments = rec.Payments != null && rec.Payments.Count > 0
                    ? rec.Payments
                    : (rec.IsPaid && !rec.HasWriteOffs
                        ? [new PaymentRecord { Id = "default", Amount = rec.Amount, Date = rec.DatePaid ?? rec.Date, Method = "Bank" }]
                        : []);

                foreach (var p in payments)
                {
                    if (p.Amount <= 0m) continue;
                    var payKey = $"{profile.Id}:{rec.Id}:{p.Id}";
                    activePayKeys.Add(payKey);

                    var bankCode = !string.IsNullOrWhiteSpace(p.BankGlAccountCode)
                        ? p.BankGlAccountCode!
                        : ResolveAccountCodeByRole(GlSystemRole.OperatingBank, "1020");

                    var pLines = new List<JournalLine>
                    {
                        CreateLine(apCode, Round(p.Amount), 0m, $"Payment — {profile.Name}", null),
                        CreateLine(bankCode, 0m, Round(p.Amount), $"Disbursement — {profile.Supplier}", null)
                    };

                    UpsertSystemEntry(
                        JournalSourceType.RecurringPayment,
                        payKey,
                        p.Date,
                        $"Payment for {profile.Name}",
                        p.Reference,
                        profile.Supplier,
                        null,
                        "CAD",
                        1.0m,
                        pLines);
                }

                if (rec.WriteOffs != null)
                {
                    foreach (var w in rec.WriteOffs)
                    {
                        if (w.Amount <= 0m) continue;
                        var writeOffKey = $"{profile.Id}:{rec.Id}:{w.Id}";
                        activeWriteOffKeys.Add(writeOffKey);

                        var offsetCode = !string.IsNullOrWhiteSpace(w.GlAccountCode)
                            ? w.GlAccountCode!
                            : ResolveAccountCodeByRole(GlSystemRole.BadDebtsAndWriteOffs, "5860");

                        var amt = Round(w.Amount);
                        if (amt <= 0m) continue;

                        var reasonSuffix = string.IsNullOrWhiteSpace(w.Reason) ? string.Empty : $": {w.Reason.Trim()}";
                        var wLines = new List<JournalLine>
                        {
                            CreateLine(apCode, amt, 0m, $"AP write-off — {profile.Name}", null),
                            CreateLine(offsetCode, 0m, amt, $"Balance write-off — {profile.Name}{reasonSuffix}", null)
                        };

                        UpsertSystemEntry(
                            JournalSourceType.RecurringWriteOff,
                            writeOffKey,
                            w.Date,
                            $"Balance write-off for {profile.Name}{reasonSuffix}",
                            rec.InvoiceNumber ?? string.Empty,
                            profile.Supplier,
                            null,
                            "CAD",
                            1.0m,
                            wLines);
                    }
                }
            }
        }

        CleanupStalePrefixEntries(JournalSourceType.RecurringBill, $"{profile.Id}:", activeBillKeys);
        CleanupStalePrefixEntries(JournalSourceType.RecurringPayment, $"{profile.Id}:", activePayKeys);
        CleanupStalePrefixEntries(JournalSourceType.RecurringWriteOff, $"{profile.Id}:", activeWriteOffKeys);
    }

    public void SyncExpenseReport(ExpenseReport report)
    {
        if (report == null || _accountingContext.JournalEntries == null) return;

        var isSubmittedOrPaid = report.DateSubmitted.HasValue || report.DatePaid.HasValue;
        if (!isSubmittedOrPaid || report.TotalAmount <= 0m || report.LineItems == null || report.LineItems.Count == 0)
        {
            RemoveEntriesForSource(JournalSourceType.ExpenseReport, report.Id);
            RemoveEntriesForSource(JournalSourceType.ExpensePayment, report.Id);
            return;
        }

        var reimbPayableCode = ResolveAccountCodeByRole(GlSystemRole.EmployeeReimbursementsPayable, "2140");
        var defaultTravelCode = ResolveAccountCodeByRole(GlSystemRole.TravelAndMileageExpense, "5700");
        var tpsCode = ResolveAccountCodeByRole(GlSystemRole.FederalTaxRecoverable, "2165");
        var tvqCode = ResolveAccountCodeByRole(GlSystemRole.RegionalTaxRecoverable, "2175");

        var lines = new List<JournalLine>();
        foreach (var item in report.LineItems)
        {
            if (item.Total <= 0m) continue;
            var expCode = !string.IsNullOrWhiteSpace(item.GlAccountCode) ? item.GlAccountCode! : defaultTravelCode;
            var sub = Round(item.EffectiveSubTotal);
            var tps = Round(item.EffectiveTpsAmount);
            var tvq = Round(item.EffectiveTvqAmount);

            if (sub > 0m)
            {
                lines.Add(CreateLine(expCode, sub, 0m, $"{item.Description} ({report.EmployeeName})", item.ProjectId));
            }
            if (tps > 0m)
            {
                lines.Add(CreateLine(tpsCode, tps, 0m, $"TPS ITC — {report.ReportNumber}", item.ProjectId, "TPS"));
            }
            if (tvq > 0m)
            {
                lines.Add(CreateLine(tvqCode, tvq, 0m, $"TVQ ITR — {report.ReportNumber}", item.ProjectId, "TVQ"));
            }
        }

        var total = Round(report.TotalAmount);
        var primaryProjectId = report.LineItems.FirstOrDefault(i => !string.IsNullOrWhiteSpace(i.ProjectId))?.ProjectId;
        lines.Add(CreateLine(reimbPayableCode, 0m, total, $"Expense Reimbursement Payable — {report.EmployeeName}", primaryProjectId));
        BalanceRoundingPenny(lines, isTargetCredit: false);

        UpsertSystemEntry(
            JournalSourceType.ExpenseReport,
            report.Id,
            report.DateSubmitted ?? report.DateCreated,
            $"Expense Report {report.ReportNumber} — {report.EmployeeName}",
            report.ReportNumber,
            report.EmployeeName,
            primaryProjectId,
            "CAD",
            1.0m,
            lines);

        if (report.DatePaid.HasValue)
        {
            var bankCode = !string.IsNullOrWhiteSpace(report.PaymentSourceGlCode)
                ? report.PaymentSourceGlCode!
                : ResolveAccountCodeByRole(GlSystemRole.OperatingBank, "1020");

            var payLines = new List<JournalLine>
            {
                CreateLine(reimbPayableCode, total, 0m, $"Reimbursement paid — {report.ReportNumber}", primaryProjectId),
                CreateLine(bankCode, 0m, total, $"Expense reimbursement to {report.EmployeeName}", primaryProjectId)
            };

            UpsertSystemEntry(
                JournalSourceType.ExpensePayment,
                report.Id,
                report.DatePaid.Value,
                $"Expense Reimbursement Payment {report.ReportNumber}",
                report.PaymentReference ?? report.ReportNumber,
                report.EmployeeName,
                primaryProjectId,
                "CAD",
                1.0m,
                payLines);
        }
        else
        {
            RemoveEntriesForSource(JournalSourceType.ExpensePayment, report.Id);
        }
    }

    public void SyncCommission(CommissionLedgerRecord record)
    {
        if (record == null || _accountingContext.JournalEntries == null) return;

        var isActive = (string.Equals(record.Status, CommissionStatus.Payable, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(record.Status, CommissionStatus.Paid, StringComparison.OrdinalIgnoreCase)) &&
                       record.CommissionAmount > 0m;

        if (!isActive)
        {
            RemoveEntriesForSource(JournalSourceType.Commission, record.Id);
            RemoveEntriesForSource(JournalSourceType.CommissionPayment, record.Id);
            return;
        }

        var commExpCode = !string.IsNullOrWhiteSpace(record.GlAccountCode)
            ? record.GlAccountCode!
            : ResolveAccountCodeByRole(GlSystemRole.CommissionsExpense, "5300");
        var commPayableCode = ResolveAccountCodeByRole(GlSystemRole.CommissionsPayable, "2150");
        var tpsCode = ResolveAccountCodeByRole(GlSystemRole.FederalTaxRecoverable, "2165");
        var tvqCode = ResolveAccountCodeByRole(GlSystemRole.RegionalTaxRecoverable, "2175");

        var baseComm = Round(record.CommissionAmount);
        var tps = record.ApplyTaxes ? Round(record.TpsAmount) : 0m;
        var tvq = record.ApplyTaxes ? Round(record.TvqAmount) : 0m;
        var totalPayable = Round(baseComm + tps + tvq);

        var lines = new List<JournalLine>
        {
            CreateLine(commExpCode, baseComm, 0m, $"Commission — {record.BeneficiaryName}", record.ProjectId)
        };
        if (tps > 0m) lines.Add(CreateLine(tpsCode, tps, 0m, $"TPS ITC — Commission {record.BeneficiaryName}", record.ProjectId, "TPS"));
        if (tvq > 0m) lines.Add(CreateLine(tvqCode, tvq, 0m, $"TVQ ITR — Commission {record.BeneficiaryName}", record.ProjectId, "TVQ"));
        lines.Add(CreateLine(commPayableCode, 0m, totalPayable, $"Commission Payable — {record.BeneficiaryName}", record.ProjectId));

        UpsertSystemEntry(
            JournalSourceType.Commission,
            record.Id,
            record.DateGenerated.Date,
            $"Commission — {record.BeneficiaryName}",
            record.PaymentReference ?? string.Empty,
            record.BeneficiaryName,
            record.ProjectId,
            "CAD",
            1.0m,
            lines);

        if (string.Equals(record.Status, CommissionStatus.Paid, StringComparison.OrdinalIgnoreCase) || record.DatePaid.HasValue)
        {
            var bankCode = !string.IsNullOrWhiteSpace(record.PaymentSourceGlCode)
                ? record.PaymentSourceGlCode!
                : ResolveAccountCodeByRole(GlSystemRole.OperatingBank, "1020");

            var payLines = new List<JournalLine>
            {
                CreateLine(commPayableCode, totalPayable, 0m, $"Commission settled — {record.BeneficiaryName}", record.ProjectId),
                CreateLine(bankCode, 0m, totalPayable, $"Commission paid to {record.BeneficiaryName}", record.ProjectId)
            };

            UpsertSystemEntry(
                JournalSourceType.CommissionPayment,
                record.Id,
                record.DatePaid ?? record.DateGenerated.Date,
                $"Commission Payment — {record.BeneficiaryName}",
                record.PaymentReference ?? string.Empty,
                record.BeneficiaryName,
                record.ProjectId,
                "CAD",
                1.0m,
                payLines);
        }
        else
        {
            RemoveEntriesForSource(JournalSourceType.CommissionPayment, record.Id);
        }
    }

    public void SyncPayRun(PayRun payRun)
    {
        if (payRun == null || _accountingContext.JournalEntries == null) return;

        var isPosted = (string.Equals(payRun.Status, PayRunStatus.Finalized, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(payRun.Status, PayRunStatus.Paid, StringComparison.OrdinalIgnoreCase)) &&
                       payRun.Stubs.Count > 0 &&
                       payRun.TotalEmployerCost > 0m;

        if (!isPosted)
        {
            RemoveEntriesForSource(JournalSourceType.PayRun, payRun.Id);
            RemoveEntriesForSource(JournalSourceType.PayRunPayment, payRun.Id);
            RemoveEntriesForSource(JournalSourceType.DasRemittancePayment, payRun.Id);
            return;
        }

        var wagesExpCode = ResolveAccountCodeByRole(GlSystemRole.WagesAndSalariesExpense, "5400");
        var employerTaxExpCode = ResolveAccountCodeByRole(GlSystemRole.EmployerPayrollTaxesExpense, "5420");
        var netWagesPayableCode = ResolveAccountCodeByRole(GlSystemRole.WagesPayable, "2200");
        var dasPayableCode = ResolveAccountCodeByRole(GlSystemRole.PayrollDeductionsPayable, "2210");
        var vacAccruedCode = ResolveAccountCodeByRole(GlSystemRole.VacationPayAccrued, "2220");

        var grossPlusVacExp = Round(payRun.TotalGrossPay + payRun.TotalVacationAccrued);
        var employerTaxes = Round(payRun.TotalEmployerContributions);
        var netPay = Round(payRun.TotalNetPay);
        var vacAccrued = Round(payRun.TotalVacationAccrued);
        var totalDasLiability = Round(payRun.TotalEmployeeDeductions + payRun.TotalEmployerContributions);

        var lines = new List<JournalLine>();
        if (grossPlusVacExp > 0m)
        {
            lines.Add(CreateLine(wagesExpCode, grossPlusVacExp, 0m, $"Gross Wages & Vacation Expense — {payRun.RunNumber}", null));
        }
        if (employerTaxes > 0m)
        {
            lines.Add(CreateLine(employerTaxExpCode, employerTaxes, 0m, $"Employer Payroll Contributions — {payRun.RunNumber}", null));
        }
        if (netPay > 0m)
        {
            lines.Add(CreateLine(netWagesPayableCode, 0m, netPay, $"Net Wages Payable — {payRun.RunNumber}", null));
        }
        if (totalDasLiability > 0m)
        {
            lines.Add(CreateLine(dasPayableCode, 0m, totalDasLiability, $"Payroll Source Deductions Payable (DAS) — {payRun.RunNumber}", null));
        }
        if (vacAccrued > 0m)
        {
            lines.Add(CreateLine(vacAccruedCode, 0m, vacAccrued, $"Vacation Pay Accrued — {payRun.RunNumber}", null));
        }

        BalanceRoundingPenny(lines, isTargetCredit: true);

        UpsertSystemEntry(
            JournalSourceType.PayRun,
            payRun.Id,
            payRun.PayDate,
            $"Payroll Run {payRun.RunNumber} ({payRun.PeriodStart:yyyy-MM-dd} to {payRun.PeriodEnd:yyyy-MM-dd})",
            payRun.RunNumber,
            "Payroll",
            null,
            payRun.Currency,
            1.0m,
            lines);

        var bankCode = !string.IsNullOrWhiteSpace(payRun.PaymentBankGlCode)
            ? payRun.PaymentBankGlCode!
            : ResolveAccountCodeByRole(GlSystemRole.OperatingBank, "1020");

        var paidStubNet = payRun.Stubs.Any(s => s.IsPaid)
            ? Round(payRun.Stubs.Where(s => s.IsPaid).Sum(s => s.NetPay))
            : 0m;
        var disbursedNet = string.Equals(payRun.Status, PayRunStatus.Paid, StringComparison.OrdinalIgnoreCase)
            ? (paidStubNet > 0m ? paidStubNet : netPay)
            : paidStubNet;

        if (disbursedNet > 0m && netPay > 0m)
        {
            var payLines = new List<JournalLine>
            {
                CreateLine(netWagesPayableCode, disbursedNet, 0m, $"Net Wages Disbursed — {payRun.RunNumber}", null),
                CreateLine(bankCode, 0m, disbursedNet, $"Payroll Direct Deposit — {payRun.RunNumber}", null)
            };

            UpsertSystemEntry(
                JournalSourceType.PayRunPayment,
                payRun.Id,
                payRun.DatePaid ?? payRun.Stubs.Where(s => s.IsPaid && s.DatePaid.HasValue).Select(s => s.DatePaid!.Value).DefaultIfEmpty(payRun.PayDate).Max(),
                $"Payroll Net Wages Disbursement — {payRun.RunNumber}",
                payRun.PaymentReference ?? payRun.RunNumber,
                "Employees",
                null,
                payRun.Currency,
                1.0m,
                payLines);
        }
        else
        {
            RemoveEntriesForSource(JournalSourceType.PayRunPayment, payRun.Id);
        }

        if (payRun.IsDasRemitted && totalDasLiability > 0m)
        {
            var dasLines = new List<JournalLine>
            {
                CreateLine(dasPayableCode, totalDasLiability, 0m, $"DAS Remittance Settled — {payRun.RunNumber}", null),
                CreateLine(bankCode, 0m, totalDasLiability, $"DAS Government Payment — {payRun.RunNumber}", null)
            };

            UpsertSystemEntry(
                JournalSourceType.DasRemittancePayment,
                payRun.Id,
                payRun.DasRemittedAt ?? payRun.PayDate,
                $"Government DAS Remittance — {payRun.RunNumber}",
                payRun.DasPaymentReference ?? payRun.RunNumber,
                "Revenu Québec / CRA",
                null,
                payRun.Currency,
                1.0m,
                dasLines);
        }
        else
        {
            RemoveEntriesForSource(JournalSourceType.DasRemittancePayment, payRun.Id);
        }
    }

    public List<JournalLine> BuildDirectTransactionJournalLines(DirectTransaction transaction)
    {
        var lines = new List<JournalLine>();
        if (transaction == null || transaction.IsVoid || transaction.GrandTotal <= 0m || string.IsNullOrWhiteSpace(transaction.AccountGlCode))
        {
            return lines;
        }

        var rate = transaction.ExchangeRate > 0m ? transaction.ExchangeRate : 1.0m;
        var bankCode = transaction.AccountGlCode.Trim();
        var grandFunctional = Round(transaction.GrandTotal * rate);
        var headerMemo = !string.IsNullOrWhiteSpace(transaction.Description)
            ? transaction.Description.Trim()
            : (!string.IsNullOrWhiteSpace(transaction.PartyName) ? transaction.PartyName.Trim() : transaction.TransactionNumber);

        if (string.Equals(transaction.Direction, DirectTransactionDirection.Transfer, StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(transaction.DestinationAccountGlCode)) return lines;
            var destCode = transaction.DestinationAccountGlCode.Trim();
            lines.Add(CreateLine(destCode, grandFunctional, 0m, $"Transfer in — {headerMemo}", null));
            lines.Add(CreateLine(bankCode, 0m, grandFunctional, $"Transfer out — {headerMemo}", null));
            return lines;
        }

        var isMoneyIn = string.Equals(transaction.Direction, DirectTransactionDirection.MoneyIn, StringComparison.OrdinalIgnoreCase);

        if (isMoneyIn)
        {
            // Debit Bank/Cash/Card for gross receipt
            lines.Add(CreateLine(bankCode, grandFunctional, 0m, $"Receipt — {headerMemo}", transaction.ProjectId));

            // Credit each category/revenue/offset line (net subtotal)
            if (transaction.Lines != null && transaction.Lines.Count > 0)
            {
                foreach (var txLine in transaction.Lines)
                {
                    if (string.IsNullOrWhiteSpace(txLine.GlAccountCode)) continue;
                    var netFunc = Round(txLine.SubTotal * rate);
                    if (netFunc <= 0m) continue;
                    var lineMemo = !string.IsNullOrWhiteSpace(txLine.Description) ? txLine.Description.Trim() : headerMemo;
                    var lineProj = !string.IsNullOrWhiteSpace(txLine.ProjectId) ? txLine.ProjectId : transaction.ProjectId;
                    lines.Add(CreateLine(txLine.GlAccountCode.Trim(), 0m, netFunc, lineMemo, lineProj));
                }
            }
            else
            {
                var defaultIncome = ResolveAccountCodeByRole(GlSystemRole.InterestAndOtherIncome, "4800");
                var subFunc = Round(transaction.SubTotal * rate);
                if (subFunc > 0m)
                {
                    lines.Add(CreateLine(defaultIncome, 0m, subFunc, headerMemo, transaction.ProjectId));
                }
            }

            // Credit sales taxes collected
            if (!string.Equals(transaction.TaxMode, DirectTransactionTaxMode.NoTax, StringComparison.OrdinalIgnoreCase) &&
                transaction.TaxComponents is { Count: > 0 })
            {
                foreach (var tc in transaction.TaxComponents)
                {
                    var taxFunc = Round(tc.Amount * rate);
                    if (taxFunc <= 0m) continue;
                    var taxGl = ResolveDirectTaxGlCode(tc, forPurchases: false);
                    lines.Add(CreateLine(taxGl, 0m, taxFunc, $"{tc.Code} Collected — {headerMemo}", transaction.ProjectId, tc.Code));
                }
            }

            BalanceRoundingPenny(lines, isTargetCredit: true);
        }
        else // MoneyOut
        {
            decimal nonRecoverableTaxFunctional = 0m;
            var recoverableTaxLines = new List<JournalLine>();

            if (!string.Equals(transaction.TaxMode, DirectTransactionTaxMode.NoTax, StringComparison.OrdinalIgnoreCase) &&
                transaction.TaxComponents is { Count: > 0 })
            {
                foreach (var tc in transaction.TaxComponents)
                {
                    var taxFunc = Round(tc.Amount * rate);
                    if (taxFunc <= 0m) continue;
                    if (tc.IsRecoverable)
                    {
                        var taxGl = ResolveDirectTaxGlCode(tc, forPurchases: true);
                        recoverableTaxLines.Add(CreateLine(taxGl, taxFunc, 0m, $"{tc.Code} ITC/ITR — {headerMemo}", transaction.ProjectId, tc.Code));
                    }
                    else
                    {
                        nonRecoverableTaxFunctional += taxFunc;
                    }
                }
            }

            // Debit each category/expense/offset line
            if (transaction.Lines != null && transaction.Lines.Count > 0)
            {
                var positiveLines = transaction.Lines.Where(l => !string.IsNullOrWhiteSpace(l.GlAccountCode) && l.SubTotal > 0m).ToList();
                var totalLineSub = positiveLines.Sum(l => l.SubTotal);
                decimal allocatedNonRec = 0m;

                for (int i = 0; i < positiveLines.Count; i++)
                {
                    var txLine = positiveLines[i];
                    var netFunc = Round(txLine.SubTotal * rate);
                    decimal lineNonRec = 0m;
                    if (nonRecoverableTaxFunctional > 0m && totalLineSub > 0m)
                    {
                        lineNonRec = i == positiveLines.Count - 1
                            ? Round(nonRecoverableTaxFunctional - allocatedNonRec)
                            : Round(nonRecoverableTaxFunctional * (txLine.SubTotal / totalLineSub));
                        allocatedNonRec += lineNonRec;
                    }

                    var lineMemo = !string.IsNullOrWhiteSpace(txLine.Description) ? txLine.Description.Trim() : headerMemo;
                    var lineProj = !string.IsNullOrWhiteSpace(txLine.ProjectId) ? txLine.ProjectId : transaction.ProjectId;
                    lines.Add(CreateLine(txLine.GlAccountCode.Trim(), Round(netFunc + lineNonRec), 0m, lineMemo, lineProj));
                }
            }
            else
            {
                var defaultExp = ResolveAccountCodeByRole(GlSystemRole.OfficeAndOverheadExpense, "5600");
                var subFunc = Round(transaction.SubTotal * rate + nonRecoverableTaxFunctional);
                if (subFunc > 0m)
                {
                    lines.Add(CreateLine(defaultExp, subFunc, 0m, headerMemo, transaction.ProjectId));
                }
            }

            lines.AddRange(recoverableTaxLines);

            // Credit Bank/Cash/Card for gross payment
            lines.Add(CreateLine(bankCode, 0m, grandFunctional, $"Payment — {headerMemo}", transaction.ProjectId));

            BalanceRoundingPenny(lines, isTargetCredit: false);
        }

        return lines;
    }

    public JournalEntry? SyncDirectTransaction(DirectTransaction transaction)
    {
        if (transaction == null || _accountingContext.JournalEntries == null) return null;

        var activeSourceType = transaction.Direction switch
        {
            DirectTransactionDirection.MoneyIn => JournalSourceType.DirectReceipt,
            DirectTransactionDirection.MoneyOut => JournalSourceType.DirectPayment,
            DirectTransactionDirection.Transfer => JournalSourceType.AccountTransfer,
            _ => JournalSourceType.DirectPayment
        };

        // Remove any stale entries under other direct source types (e.g. if direction changed while unlocked)
        foreach (var st in new[] { JournalSourceType.DirectReceipt, JournalSourceType.DirectPayment, JournalSourceType.AccountTransfer })
        {
            if (transaction.IsVoid || transaction.GrandTotal <= 0m || !string.Equals(st, activeSourceType, StringComparison.OrdinalIgnoreCase))
            {
                RemoveEntriesForSource(st, transaction.Id);
            }
        }

        if (transaction.IsVoid || transaction.GrandTotal <= 0m)
        {
            return null;
        }

        var lines = BuildDirectTransactionJournalLines(transaction);
        if (lines.Count < 2) return null;

        var rate = transaction.ExchangeRate > 0m ? transaction.ExchangeRate : 1.0m;
        var description = !string.IsNullOrWhiteSpace(transaction.Description)
            ? transaction.Description.Trim()
            : $"{transaction.Direction} {transaction.TransactionNumber}".Trim();
        var reference = !string.IsNullOrWhiteSpace(transaction.Reference)
            ? transaction.Reference.Trim()
            : transaction.TransactionNumber;
        var projectId = transaction.ProjectId
            ?? transaction.Lines?.Select(l => l.ProjectId).FirstOrDefault(p => !string.IsNullOrWhiteSpace(p));

        return UpsertSystemEntry(
            activeSourceType,
            transaction.Id,
            transaction.Date,
            description,
            reference,
            transaction.PartyName ?? string.Empty,
            projectId,
            transaction.Currency,
            rate,
            lines);
    }

    private string ResolveDirectTaxGlCode(DocumentTaxLine taxLine, bool forPurchases)
    {
        if (!string.IsNullOrWhiteSpace(taxLine.GlAccountCode))
        {
            return taxLine.GlAccountCode.Trim();
        }

        var template = _templateService?.GetActiveTemplate() ?? FinancialCalculationEngine.ActiveTemplate;
        var rule = template?.SalesTaxes?.FirstOrDefault(r => string.Equals(r.Code, taxLine.Code, StringComparison.OrdinalIgnoreCase));
        if (rule != null)
        {
            return forPurchases
                ? (!string.IsNullOrWhiteSpace(rule.RecoverableAssetGlCode) ? rule.RecoverableAssetGlCode : rule.CollectedLiabilityGlCode)
                : rule.CollectedLiabilityGlCode;
        }

        var isRegional = string.Equals(taxLine.Code, "TVQ", StringComparison.OrdinalIgnoreCase) ||
                         string.Equals(taxLine.Code, "QST", StringComparison.OrdinalIgnoreCase);

        return forPurchases
            ? (isRegional
                ? ResolveAccountCodeByRole(GlSystemRole.RegionalTaxRecoverable, "2175")
                : ResolveAccountCodeByRole(GlSystemRole.FederalTaxRecoverable, "2165"))
            : (isRegional
                ? ResolveAccountCodeByRole(GlSystemRole.RegionalTaxCollected, "2170")
                : ResolveAccountCodeByRole(GlSystemRole.FederalTaxCollected, "2160"));
    }

    public void RemoveEntriesForSource(string sourceType, string sourceId)
    {
        if (_accountingContext.JournalEntries == null) return;
        var existing = _accountingContext.JournalEntries.GetBySource(sourceType, sourceId);
        if (existing != null && !existing.IsLocked)
        {
            _accountingContext.JournalEntries.Delete(existing.Id);
        }
    }

    public JournalEntry SaveManualJournalEntry(JournalEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (_accountingContext.JournalEntries == null)
        {
            throw new InvalidOperationException("JournalEntryRepository is not initialized.");
        }

        if (entry.Lines == null || entry.Lines.Count < 2)
        {
            throw new InvalidOperationException("A journal entry must contain at least 2 lines.");
        }

        foreach (var line in entry.Lines)
        {
            line.Debit = Round(Math.Max(0m, line.Debit));
            line.Credit = Round(Math.Max(0m, line.Credit));
            if (string.IsNullOrWhiteSpace(line.GlAccountName) && !string.IsNullOrWhiteSpace(line.GlAccountCode))
            {
                line.GlAccountName = GetAccountName(line.GlAccountCode);
            }
        }

        if (!entry.IsBalanced)
        {
            throw new InvalidOperationException($"Journal entry is out of balance: Debits ({entry.TotalDebits:C2}) != Credits ({entry.TotalCredits:C2}).");
        }

        if (string.IsNullOrWhiteSpace(entry.EntryNumber))
        {
            var prefix = string.Equals(entry.SourceType, JournalSourceType.AccountantAdjustment, StringComparison.OrdinalIgnoreCase)
                ? "AJE"
                : string.Equals(entry.SourceType, JournalSourceType.OpeningBalance, StringComparison.OrdinalIgnoreCase)
                    ? "OB"
                    : "JE";
            entry.EntryNumber = _accountingContext.JournalEntries.GenerateEntryNumber(prefix);
        }

        _accountingContext.JournalEntries.Save(entry);
        return entry;
    }

    public void DeleteManualJournalEntry(string entryId)
    {
        if (_accountingContext.JournalEntries == null || string.IsNullOrWhiteSpace(entryId)) return;
        var existing = _accountingContext.JournalEntries.GetById(entryId);
        if (existing == null) return;
        if (existing.IsLocked)
        {
            throw new InvalidOperationException("Cannot delete a locked or reconciled journal entry.");
        }
        _accountingContext.JournalEntries.Delete(entryId);
    }

    public int RebuildOperationalLedger()
    {
        if (_accountingContext.JournalEntries == null) return 0;

        int synced = 0;

        if (_accountingContext.Invoices != null)
        {
            foreach (var inv in _accountingContext.Invoices.GetAll())
            {
                SyncInvoice(inv);
                synced++;
            }
        }

        if (_accountingContext.Bills != null)
        {
            foreach (var bill in _accountingContext.Bills.GetAll())
            {
                SyncBill(bill);
                synced++;
            }
        }

        if (_accountingContext.RecurringExpenses != null)
        {
            foreach (var rec in _accountingContext.RecurringExpenses.GetAll())
            {
                SyncRecurringExpense(rec);
                synced++;
            }
        }

        if (_accountingContext.ExpenseReports != null)
        {
            foreach (var exp in _accountingContext.ExpenseReports.GetAll())
            {
                SyncExpenseReport(exp);
                synced++;
            }
        }

        if (_accountingContext.CommissionLedgerRecords != null)
        {
            foreach (var comm in _accountingContext.CommissionLedgerRecords.GetAll())
            {
                SyncCommission(comm);
                synced++;
            }
        }

        if (_hrContext?.PayRuns != null)
        {
            foreach (var pr in _hrContext.PayRuns.GetAll())
            {
                SyncPayRun(pr);
                synced++;
            }
        }

        if (_accountingContext.DirectTransactions != null)
        {
            foreach (var tx in _accountingContext.DirectTransactions.GetAll())
            {
                SyncDirectTransaction(tx);
                synced++;
            }
        }

        return synced;
    }

    public DateTime GetFiscalYearStart(DateTime referenceDate)
    {
        var config = _accountingContext.GetAccountingConfig();
        var startMonth = Math.Clamp(config.FiscalYearStartMonth, 1, 12);
        var d = referenceDate.Date;
        var fyYear = d.Month >= startMonth ? d.Year : d.Year - 1;
        return new DateTime(fyYear, startMonth, 1);
    }

    public TrialBalanceReport CalculateTrialBalance(DateTime startDate, DateTime endDate, bool includeZeroAccounts = false)
    {
        var s = startDate.Date;
        var e = endDate.Date;
        var fiscalStart = GetFiscalYearStart(s);
        var retainedEarningsCode = ResolveAccountCodeByRole(GlSystemRole.RetainedEarnings, "3600");
        var accounts = _accountingContext.GlAccounts?.GetAll().OrderBy(a => a.Code).ToList() ?? [];
        var allEntries = _accountingContext.JournalEntries?.GetAll().Where(j => j.Date.Date <= e).ToList() ?? [];

        var rowsByCode = accounts.ToDictionary(
            a => a.Code,
            a => new TrialBalanceRow
            {
                AccountCode = a.Code,
                AccountName = a.GetLocalizedName("en"),
                AccountNameFr = a.GetLocalizedName("fr"),
                AccountType = a.Type,
                SubType = a.SubType,
                StandardTaxCode = a.StandardTaxCode,
                IsDebitNormal = a.IsDebitNormal
            },
            StringComparer.OrdinalIgnoreCase);

        TrialBalanceRow GetOrCreateRow(string code, string? name = null)
        {
            if (rowsByCode.TryGetValue(code, out var existing)) return existing;
            var created = new TrialBalanceRow
            {
                AccountCode = code,
                AccountName = !string.IsNullOrWhiteSpace(name) ? name : code,
                AccountNameFr = !string.IsNullOrWhiteSpace(name) ? name : code,
                AccountType = code.StartsWith('1') ? GlAccountType.Asset :
                              code.StartsWith('2') ? GlAccountType.Liability :
                              code.StartsWith('3') ? GlAccountType.Equity :
                              code.StartsWith('4') ? GlAccountType.Revenue : GlAccountType.Expense,
                IsDebitNormal = code.StartsWith('1') || code.StartsWith('5')
            };
            rowsByCode[code] = created;
            return created;
        }

        foreach (var entry in allEntries)
        {
            var isPrior = entry.Date.Date < s;
            var isPriorFiscalYear = entry.Date.Date < fiscalStart;
            foreach (var line in entry.Lines)
            {
                if (string.IsNullOrWhiteSpace(line.GlAccountCode)) continue;
                var row = GetOrCreateRow(line.GlAccountCode, line.GlAccountName);

                if (isPrior)
                {
                    var isBalanceSheetAccount =
                        string.Equals(row.AccountType, GlAccountType.Asset, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(row.AccountType, GlAccountType.Liability, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(row.AccountType, GlAccountType.Equity, StringComparison.OrdinalIgnoreCase);

                    if (isBalanceSheetAccount)
                    {
                        var netPrior = row.IsDebitNormal ? (line.Debit - line.Credit) : (line.Credit - line.Debit);
                        row.OpeningBalance = Round(row.OpeningBalance + netPrior);
                    }
                    else if (isPriorFiscalYear)
                    {
                        // Roll prior fiscal year Revenue/Expense into Retained Earnings (Equity, credit-normal)
                        var reRow = GetOrCreateRow(retainedEarningsCode, "Retained Earnings");
                        var netIncomeContribution = line.Credit - line.Debit;
                        reRow.OpeningBalance = Round(reRow.OpeningBalance + netIncomeContribution);
                    }
                    else
                    {
                        // Prior period within the current fiscal year keeps its YTD opening balance on the P&L account
                        var netPriorYtd = row.IsDebitNormal ? (line.Debit - line.Credit) : (line.Credit - line.Debit);
                        row.OpeningBalance = Round(row.OpeningBalance + netPriorYtd);
                    }
                }
                else
                {
                    row.PeriodDebits = Round(row.PeriodDebits + line.Debit);
                    row.PeriodCredits = Round(row.PeriodCredits + line.Credit);
                }
            }
        }

        var resultRows = rowsByCode.Values
            .Where(r => includeZeroAccounts || r.OpeningBalance != 0m || r.PeriodDebits != 0m || r.PeriodCredits != 0m || r.EndingBalance != 0m)
            .OrderBy(r => r.AccountCode)
            .ToList();

        return new TrialBalanceReport
        {
            StartDate = s,
            EndDate = e,
            Rows = resultRows
        };
    }

    public IncomeStatementReport CalculateIncomeStatement(DateTime startDate, DateTime endDate)
    {
        var tb = CalculateTrialBalance(startDate, endDate, includeZeroAccounts: false);
        return BuildIncomeStatementFromTrialBalance(tb, startDate, endDate);
    }

    private static IncomeStatementReport BuildIncomeStatementFromTrialBalance(
        TrialBalanceReport tb,
        DateTime startDate,
        DateTime endDate)
    {
        var report = new IncomeStatementReport
        {
            StartDate = startDate.Date,
            EndDate = endDate.Date
        };

        foreach (var row in tb.Rows)
        {
            if (string.Equals(row.AccountType, GlAccountType.Revenue, StringComparison.OrdinalIgnoreCase))
            {
                var amount = Round(row.PeriodCredits - row.PeriodDebits);
                if (amount != 0m)
                {
                    report.RevenueLines.Add(new FinancialStatementLine
                    {
                        AccountCode = row.AccountCode,
                        AccountName = row.AccountName,
                        AccountNameFr = row.AccountNameFr,
                        StandardTaxCode = row.StandardTaxCode,
                        SubType = row.SubType,
                        Amount = amount
                    });
                }
            }
            else if (string.Equals(row.AccountType, GlAccountType.Expense, StringComparison.OrdinalIgnoreCase))
            {
                var amount = Round(row.PeriodDebits - row.PeriodCredits);
                if (amount == 0m) continue;

                var line = new FinancialStatementLine
                {
                    AccountCode = row.AccountCode,
                    AccountName = row.AccountName,
                    AccountNameFr = row.AccountNameFr,
                    StandardTaxCode = row.StandardTaxCode,
                    SubType = row.SubType,
                    Amount = amount
                };

                if (string.Equals(row.SubType, GlAccountSubType.DirectCost, StringComparison.OrdinalIgnoreCase))
                {
                    report.DirectCostLines.Add(line);
                }
                else if (string.Equals(row.SubType, GlAccountSubType.PayrollExpense, StringComparison.OrdinalIgnoreCase))
                {
                    report.PayrollExpenseLines.Add(line);
                }
                else
                {
                    report.OverheadExpenseLines.Add(line);
                }
            }
        }

        return report;
    }

    public BalanceSheetReport CalculateBalanceSheet(DateTime asOfDate)
    {
        var cutoff = asOfDate.Date;
        var fiscalStart = GetFiscalYearStart(cutoff);
        var tb = CalculateTrialBalance(fiscalStart, cutoff, includeZeroAccounts: false);
        var pl = BuildIncomeStatementFromTrialBalance(tb, fiscalStart, cutoff);

        var report = new BalanceSheetReport
        {
            AsOfDate = cutoff,
            CurrentYearNetIncome = pl.NetIncome
        };

        foreach (var row in tb.Rows)
        {
            var ending = Round(row.EndingBalance);
            if (ending == 0m) continue;

            var line = new FinancialStatementLine
            {
                AccountCode = row.AccountCode,
                AccountName = row.AccountName,
                AccountNameFr = row.AccountNameFr,
                StandardTaxCode = row.StandardTaxCode,
                SubType = row.SubType,
                Amount = ending
            };

            if (string.Equals(row.AccountType, GlAccountType.Asset, StringComparison.OrdinalIgnoreCase))
            {
                report.AssetLines.Add(line);
            }
            else if (string.Equals(row.AccountType, GlAccountType.Liability, StringComparison.OrdinalIgnoreCase))
            {
                report.LiabilityLines.Add(line);
            }
            else if (string.Equals(row.AccountType, GlAccountType.Equity, StringComparison.OrdinalIgnoreCase))
            {
                report.EquityLines.Add(line);
            }
        }

        return report;
    }

    private void EnsureDefaultAccountsAndInitialSync()
    {
        try
        {
            if (_accountingContext.GlAccounts != null && _accountingContext.GlAccounts.GetAll().Count == 0)
            {
                if (_templateService != null)
                {
                    _templateService.EnsureChartOfAccountsSeeded();
                }
                else
                {
                    var fallback = JurisdictionTemplateService.BuildFallbackQuebecTemplate();
                    FinancialCalculationEngine.ActiveTemplate = fallback;
                    foreach (var item in fallback.ChartOfAccounts)
                    {
                        _accountingContext.GlAccounts.Save(new GlAccount
                        {
                            Id = $"gl-{item.Code}",
                            Code = item.Code,
                            Name = item.Name,
                            NameFr = item.NameFr,
                            Type = item.Type,
                            SubType = item.SubType,
                            StandardTaxCode = item.StandardTaxCode,
                            SystemRole = item.SystemRole,
                            Currency = fallback.DefaultCurrency,
                            IsSystemAccount = !string.IsNullOrWhiteSpace(item.SystemRole),
                            IsActive = true
                        });
                    }
                }
            }

            if (_accountingContext.GlAccounts != null)
            {
                JurisdictionTemplateService.EnsureBadDebtsAndWriteOffsSeeded(
                    _accountingContext.GlAccounts,
                    _templateService?.GetActiveTemplate() ?? FinancialCalculationEngine.ActiveTemplate);
            }

            if (_accountingContext.JournalEntries != null && _accountingContext.JournalEntries.GetAll().Count == 0)
            {
                RebuildOperationalLedger();
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "LedgerService initial sync encountered a non-fatal warning.");
        }
    }

    private string ResolveDefaultBillExpenseAccount(Bill bill)
    {
        if (!string.IsNullOrWhiteSpace(bill.GlAccountCode))
        {
            return bill.GlAccountCode!;
        }
        if (!string.IsNullOrWhiteSpace(bill.PurchaseOrderId))
        {
            return ResolveAccountCodeByRole(GlSystemRole.DirectProjectPurchases, "5100");
        }
        if (!string.IsNullOrWhiteSpace(bill.LinkedRecurringExpenseId))
        {
            var profile = _accountingContext.RecurringExpenses?.GetById(bill.LinkedRecurringExpenseId);
            if (!string.IsNullOrWhiteSpace(profile?.GlAccountCode))
            {
                return profile!.GlAccountCode!;
            }
            return ResolveAccountCodeByRole(GlSystemRole.RentAndUtilitiesExpense, "5610");
        }
        return ResolveAccountCodeByRole(GlSystemRole.OfficeAndOverheadExpense, "5600");
    }

    private List<DocumentTaxLine> GetEffectiveInvoiceTaxes(Invoice invoice)
    {
        if (invoice.TaxComponents != null && invoice.TaxComponents.Count > 0)
        {
            return invoice.TaxComponents;
        }

        var tpsCode = ResolveAccountCodeByRole(GlSystemRole.FederalTaxCollected, "2160");
        var tvqCode = ResolveAccountCodeByRole(GlSystemRole.RegionalTaxCollected, "2170");
        var result = new List<DocumentTaxLine>();
        if (invoice.TpsAmount > 0m)
        {
            result.Add(new DocumentTaxLine { Code = "TPS", Name = "TPS / GST", Amount = invoice.TpsAmount, GlAccountCode = tpsCode });
        }
        if (invoice.TvqAmount > 0m)
        {
            result.Add(new DocumentTaxLine { Code = "TVQ", Name = "TVQ / QST", Amount = invoice.TvqAmount, GlAccountCode = tvqCode });
        }
        if (result.Count == 0 && invoice.TaxAmount > 0m)
        {
            result.Add(new DocumentTaxLine { Code = "TAX", Name = "Sales Tax", Amount = invoice.TaxAmount, GlAccountCode = tpsCode });
        }
        return result;
    }

    private List<DocumentTaxLine> GetEffectiveBillTaxes(Bill bill, string expenseFallbackCode)
    {
        if (bill.TaxComponents != null && bill.TaxComponents.Count > 0)
        {
            return bill.TaxComponents.Select(t => new DocumentTaxLine
            {
                Code = t.Code,
                Name = t.Name,
                Rate = t.Rate,
                Amount = t.Amount,
                IsRecoverable = t.IsRecoverable,
                AuthorityName = t.AuthorityName,
                GlAccountCode = t.IsRecoverable
                    ? (!string.IsNullOrWhiteSpace(t.GlAccountCode) ? t.GlAccountCode : ResolveAccountCodeByRole(GlSystemRole.FederalTaxRecoverable, "2165"))
                    : expenseFallbackCode
            }).ToList();
        }

        var tpsItcCode = ResolveAccountCodeByRole(GlSystemRole.FederalTaxRecoverable, "2165");
        var tvqItrCode = ResolveAccountCodeByRole(GlSystemRole.RegionalTaxRecoverable, "2175");
        var list = new List<DocumentTaxLine>();

        if (bill.TpsAmount > 0m || bill.TvqAmount > 0m)
        {
            if (bill.TpsAmount > 0m)
                list.Add(new DocumentTaxLine { Code = "TPS", Name = "TPS ITC", Amount = bill.TpsAmount, IsRecoverable = true, GlAccountCode = tpsItcCode });
            if (bill.TvqAmount > 0m)
                list.Add(new DocumentTaxLine { Code = "TVQ", Name = "TVQ ITR", Amount = bill.TvqAmount, IsRecoverable = true, GlAccountCode = tvqItrCode });
        }
        else if (bill.TaxAmount > 0m)
        {
            var calc = FinancialCalculationEngine.CalculateFromGrandTotal(bill.Amount, bill.IsTaxExempt);
            if (Math.Abs((calc.TpsAmount + calc.TvqAmount) - bill.TaxAmount) <= 0.05m)
            {
                if (calc.TpsAmount > 0m)
                    list.Add(new DocumentTaxLine { Code = "TPS", Name = "TPS ITC", Amount = calc.TpsAmount, IsRecoverable = true, GlAccountCode = tpsItcCode });
                if (calc.TvqAmount > 0m)
                    list.Add(new DocumentTaxLine { Code = "TVQ", Name = "TVQ ITR", Amount = calc.TvqAmount, IsRecoverable = true, GlAccountCode = tvqItrCode });
            }
            else
            {
                list.Add(new DocumentTaxLine { Code = "TAX", Name = "Recoverable Tax", Amount = bill.TaxAmount, IsRecoverable = true, GlAccountCode = tpsItcCode });
            }
        }

        return list;
    }

    private JournalLine CreateLine(string glCode, decimal debit, decimal credit, string memo, string? projectId, string? taxCode = null)
    {
        return new JournalLine
        {
            GlAccountCode = glCode,
            GlAccountName = GetAccountName(glCode),
            Debit = Round(debit),
            Credit = Round(credit),
            Memo = memo,
            ProjectId = projectId,
            TaxCode = taxCode
        };
    }

    private static void BalanceRoundingPenny(List<JournalLine> lines, bool isTargetCredit)
    {
        if (lines.Count < 2) return;
        var debits = Math.Round(lines.Sum(l => l.Debit), 2, MidpointRounding.AwayFromZero);
        var credits = Math.Round(lines.Sum(l => l.Credit), 2, MidpointRounding.AwayFromZero);
        var diff = Math.Round(debits - credits, 2, MidpointRounding.AwayFromZero);
        if (diff == 0m || Math.Abs(diff) > 0.05m) return;

        if (isTargetCredit)
        {
            var target = lines.LastOrDefault(l => l.Credit > 0m);
            if (target != null && target.Credit + diff > 0m)
            {
                target.Credit = Math.Round(target.Credit + diff, 2, MidpointRounding.AwayFromZero);
            }
        }
        else
        {
            var target = lines.FirstOrDefault(l => l.Debit > 0m);
            if (target != null && target.Debit - diff > 0m)
            {
                target.Debit = Math.Round(target.Debit - diff, 2, MidpointRounding.AwayFromZero);
            }
        }
    }

    private JournalEntry? UpsertSystemEntry(
        string sourceType,
        string sourceId,
        DateTime date,
        string description,
        string reference,
        string partyName,
        string? projectId,
        string currency,
        decimal exchangeRate,
        List<JournalLine> lines)
    {
        if (_accountingContext.JournalEntries == null || lines.Count < 2) return null;

        var existing = _accountingContext.JournalEntries.GetBySource(sourceType, sourceId);
        if (existing != null && existing.IsLocked)
        {
            return existing; // Locked by bank reconciliation or tax period filing
        }

        var entry = existing ?? new JournalEntry
        {
            Id = Guid.NewGuid().ToString(),
            EntryNumber = _accountingContext.JournalEntries.GenerateEntryNumber("JE"),
            SourceType = sourceType,
            SourceId = sourceId,
            CreatedAt = DateTime.UtcNow,
            CreatedBy = "System"
        };

        entry.Date = date;
        entry.Description = description;
        entry.Reference = reference;
        entry.PartyName = partyName;
        entry.ProjectId = projectId;
        entry.Currency = string.IsNullOrWhiteSpace(currency) ? "CAD" : currency;
        entry.ExchangeRate = exchangeRate > 0m ? exchangeRate : 1.0m;
        entry.Lines = lines;

        if (entry.IsBalanced)
        {
            _accountingContext.JournalEntries.Save(entry);
            return entry;
        }

        return null;
    }

    private void CleanupStalePrefixEntries(string sourceType, string prefix, HashSet<string>? activeKeys = null)
    {
        if (_accountingContext.JournalEntries == null) return;
        var matches = _accountingContext.JournalEntries.GetAll()
            .Where(j => string.Equals(j.SourceType, sourceType, StringComparison.OrdinalIgnoreCase) &&
                        (j.SourceId?.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ?? false))
            .ToList();

        foreach (var m in matches)
        {
            if (m.IsLocked) continue;
            if (activeKeys == null || (m.SourceId != null && !activeKeys.Contains(m.SourceId)))
            {
                _accountingContext.JournalEntries.Delete(m.Id);
            }
        }
    }

    private static decimal Round(decimal val) =>
        Math.Round(val, 2, MidpointRounding.AwayFromZero);
}
