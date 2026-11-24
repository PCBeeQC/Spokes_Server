namespace Spokes_Server.Core.Services.Accounting;

using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.DependencyInjection;
using Spokes_Server.Aggregate.Contexts;
using Spokes_Server.Core.Models.Accounting;

public interface ITaxRemittanceService
{
    TaxFilingPeriod CalculateTaxPeriodSummary(DateTime startDate, DateTime endDate, string frequency = "Quarterly");
    TaxFilingPeriod FileAndClosePeriod(DateTime startDate, DateTime endDate, string frequency = "Quarterly", string? filedBy = null);
    TaxFilingPeriod RecordTaxRemittancePayment(string periodId, DateTime paymentDate, string bankGlCode = "1020", string? reference = null);
}

public class TaxRemittanceService : ITaxRemittanceService
{
    private readonly AccountingContext _accountingContext;
    private readonly ILedgerService _ledgerService;
    private readonly IJurisdictionTemplateService? _templateService;

    [ActivatorUtilitiesConstructor]
    public TaxRemittanceService(
        AccountingContext accountingContext,
        ILedgerService? ledgerService = null,
        IJurisdictionTemplateService? templateService = null)
    {
        ArgumentNullException.ThrowIfNull(accountingContext);
        _accountingContext = accountingContext;
        _ledgerService = ledgerService ?? new LedgerService(accountingContext);
        _templateService = templateService;
    }

    public TaxFilingPeriod CalculateTaxPeriodSummary(DateTime startDate, DateTime endDate, string frequency = "Quarterly")
    {
        var s = startDate.Date;
        var e = endDate.Date;

        var existingSaved = _accountingContext.TaxFilingPeriods?.GetByDates(s, e);
        if (existingSaved != null && !string.Equals(existingSaved.Status, TaxFilingStatus.Open, StringComparison.OrdinalIgnoreCase))
        {
            return existingSaved;
        }

        var template = _templateService?.GetActiveTemplate()
                       ?? FinancialCalculationEngine.ActiveTemplate
                       ?? JurisdictionTemplateService.BuildFallbackQuebecTemplate();

        var period = existingSaved ?? new TaxFilingPeriod
        {
            Id = Guid.NewGuid().ToString(),
            StartDate = s,
            EndDate = e,
            Frequency = frequency,
            JurisdictionId = template.JurisdictionId,
            Currency = template.DefaultCurrency,
            PeriodLabel = $"{s:yyyy-MM-dd} → {e:yyyy-MM-dd}"
        };

        var linesByCode = new Dictionary<string, TaxAuthorityFilingLine>(StringComparer.OrdinalIgnoreCase);
        foreach (var rule in template.SalesTaxes)
        {
            linesByCode[rule.Code] = new TaxAuthorityFilingLine
            {
                TaxCode = rule.Code,
                TaxName = rule.Name,
                AuthorityName = rule.AuthorityName,
                Rate = rule.Rate,
                IsRecoverable = rule.IsRecoverable,
                CollectedLiabilityGlCode = rule.CollectedLiabilityGlCode,
                RecoverableAssetGlCode = rule.RecoverableAssetGlCode
            };
        }

        decimal totalSalesSubTotal = 0m;
        decimal totalPurchasesSubTotal = 0m;

        // 1. Sales Invoices (Posted in period — excluding pre-cutover opening balances, Drafts, and Voids)
        var invoices = _accountingContext.Invoices?.GetAll()
            .Where(i => !i.IsOpeningBalance &&
                        InvoiceStatus.IsPosted(i.Status) &&
                        i.Date.Date >= s && i.Date.Date <= e)
            .ToList() ?? [];

        foreach (var inv in invoices)
        {
            var fx = inv.ExchangeRate > 0m ? inv.ExchangeRate : 1.0m;
            totalSalesSubTotal += Math.Round(inv.SubTotal * fx, 2);

            if (inv.TaxComponents != null && inv.TaxComponents.Count > 0)
            {
                foreach (var tc in inv.TaxComponents)
                {
                    AddCollectedTax(linesByCode, tc.Code, tc.Name, Math.Round(tc.Amount * fx, 2));
                }
            }
            else if (inv.TaxAmount > 0m)
            {
                if (linesByCode.ContainsKey("TPS") || linesByCode.ContainsKey("TVQ"))
                {
                    AddCollectedTax(linesByCode, "TPS", "TPS / GST", Math.Round(inv.TpsAmount * fx, 2));
                    AddCollectedTax(linesByCode, "TVQ", "TVQ / QST", Math.Round(inv.TvqAmount * fx, 2));
                }
                else if (linesByCode.Count > 0)
                {
                    var firstKey = linesByCode.Keys.First();
                    AddCollectedTax(linesByCode, firstKey, firstKey, Math.Round(inv.TaxAmount * fx, 2));
                }
            }
        }

        // 2. Supplier Bills (excluding Void and pre-cutover opening balances; includes Overhead Bills and PO Bills)
        var bills = _accountingContext.Bills?.GetAll()
            .Where(b => !b.IsVoid && !b.IsOpeningBalance && b.Date.Date >= s && b.Date.Date <= e)
            .ToList() ?? [];

        foreach (var bill in bills)
        {
            var fx = bill.ExchangeRate > 0m ? bill.ExchangeRate : 1.0m;
            var sign = bill.IsCreditNote ? -1m : 1m;
            var sub = bill.SubTotal > 0m ? bill.SubTotal : Math.Max(0m, bill.Amount - (bill.TaxAmount > 0m ? bill.TaxAmount : (bill.TpsAmount + bill.TvqAmount)));
            totalPurchasesSubTotal += Math.Round(sub * fx * sign, 2);

            if (bill.IsTaxExempt) continue;

            if (bill.TaxComponents != null && bill.TaxComponents.Count > 0)
            {
                foreach (var tc in bill.TaxComponents.Where(t => t.IsRecoverable))
                {
                    AddRecoverableTax(linesByCode, tc.Code, tc.Name, Math.Round(tc.Amount * fx * sign, 2));
                }
            }
            else
            {
                var tps = bill.TpsAmount;
                var tvq = bill.TvqAmount;
                if (tps == 0m && tvq == 0m && bill.TaxAmount > 0m)
                {
                    var calc = FinancialCalculationEngine.CalculateFromGrandTotal(bill.Amount, bill.IsTaxExempt);
                    tps = calc.TpsAmount;
                    tvq = calc.TvqAmount;
                }

                if (linesByCode.ContainsKey("TPS") || linesByCode.ContainsKey("TVQ"))
                {
                    AddRecoverableTax(linesByCode, "TPS", "TPS / GST", Math.Round(tps * fx * sign, 2));
                    AddRecoverableTax(linesByCode, "TVQ", "TVQ / QST", Math.Round(tvq * fx * sign, 2));
                }
                else if (linesByCode.Count > 0 && (bill.TaxAmount > 0m || tps + tvq > 0m))
                {
                    var firstKey = linesByCode.Keys.First();
                    var totTax = bill.TaxAmount > 0m ? bill.TaxAmount : (tps + tvq);
                    AddRecoverableTax(linesByCode, firstKey, firstKey, Math.Round(totTax * fx * sign, 2));
                }
            }
        }

        // 3. Deduplicated Recurring Bills (only RecurringBillRecords not already covered by a linked Bill)
        var allBills = _accountingContext.Bills?.GetAll().Where(b => !b.IsVoid).ToList() ?? [];
        var recurringProfiles = _accountingContext.RecurringExpenses?.GetAll() ?? [];

        foreach (var profile in recurringProfiles)
        {
            if (profile.History == null) continue;
            var linkedToProfile = allBills
                .Where(b => string.Equals(b.LinkedRecurringExpenseId, profile.Id, StringComparison.OrdinalIgnoreCase))
                .ToList();

            foreach (var rec in profile.History)
            {
                if (rec == null || rec.Amount <= 0m || rec.Date.Date < s || rec.Date.Date > e) continue;

                if (FinancialCalculationEngine.IsRecurringRecordCoveredByBills(rec, linkedToProfile)) continue;

                var (sub, tps, tvq, isExempt) = FinancialCalculationEngine.ResolveRecurringRecordTaxSplit(profile, rec);

                totalPurchasesSubTotal += Math.Round(sub, 2);
                if (!isExempt)
                {
                    AddTaxLine(linesByCode, "TPS", "TPS / GST", Math.Round(tps, 2), isCollected: false);
                    AddTaxLine(linesByCode, "TVQ", "TVQ / QST", Math.Round(tvq, 2), isCollected: false);
                }
            }
        }

        // 4. Submitted / Paid Employee Expense Reports
        var expenseReports = _accountingContext.ExpenseReports?.GetAll()
            .Where(r => r.DateSubmitted.HasValue || r.DatePaid.HasValue)
            .ToList() ?? [];

        foreach (var report in expenseReports)
        {
            if (report.LineItems == null) continue;
            foreach (var item in report.LineItems)
            {
                if (item.Date.Date < s || item.Date.Date > e) continue;
                totalPurchasesSubTotal += Math.Round(item.EffectiveSubTotal, 2);
                if (!item.IsTaxExempt)
                {
                    AddRecoverableTax(linesByCode, "TPS", "TPS / GST", Math.Round(item.EffectiveTpsAmount, 2));
                    AddRecoverableTax(linesByCode, "TVQ", "TVQ / QST", Math.Round(item.EffectiveTvqAmount, 2));
                }
            }
        }

        // 5. External Commissions with Taxes
        var commissions = _accountingContext.CommissionLedgerRecords?.GetAll()
            .Where(c => (string.Equals(c.Status, CommissionStatus.Payable, StringComparison.OrdinalIgnoreCase) ||
                         string.Equals(c.Status, CommissionStatus.Paid, StringComparison.OrdinalIgnoreCase)) &&
                        c.DateGenerated.Date >= s && c.DateGenerated.Date <= e)
            .ToList() ?? [];

        foreach (var comm in commissions)
        {
            totalPurchasesSubTotal += Math.Round(comm.CommissionAmount, 2);
            if (comm.ApplyTaxes)
            {
                AddRecoverableTax(linesByCode, "TPS", "TPS / GST", Math.Round(comm.TpsAmount, 2));
                AddRecoverableTax(linesByCode, "TVQ", "TVQ / QST", Math.Round(comm.TvqAmount, 2));
            }
        }

        // 6. Direct Transactions (Money In & Money Out, excluding Void and Transfers)
        var directTransactions = _accountingContext.DirectTransactions?.GetAll()
            .Where(t => !t.IsVoid
                        && !string.Equals(t.Direction, DirectTransactionDirection.Transfer, StringComparison.OrdinalIgnoreCase)
                        && t.Date.Date >= s && t.Date.Date <= e)
            .ToList() ?? [];

        foreach (var tx in directTransactions)
        {
            if (string.Equals(tx.TaxMode, DirectTransactionTaxMode.NoTax, StringComparison.OrdinalIgnoreCase))
                continue;

            var fx = tx.ExchangeRate > 0m ? tx.ExchangeRate : 1.0m;
            var subFunc = Math.Round(tx.SubTotal * fx, 2, MidpointRounding.AwayFromZero);
            var isMoneyIn = string.Equals(tx.Direction, DirectTransactionDirection.MoneyIn, StringComparison.OrdinalIgnoreCase);

            if (isMoneyIn)
            {
                totalSalesSubTotal += subFunc;
                if (tx.TaxComponents is { Count: > 0 })
                {
                    foreach (var tc in tx.TaxComponents)
                    {
                        AddCollectedTax(linesByCode, tc.Code, tc.Name, Math.Round(tc.Amount * fx, 2, MidpointRounding.AwayFromZero));
                    }
                }
                else if (tx.TaxAmount > 0m)
                {
                    if (linesByCode.ContainsKey("TPS") || linesByCode.ContainsKey("TVQ"))
                    {
                        AddCollectedTax(linesByCode, "TPS", "TPS / GST", Math.Round(tx.TpsAmount * fx, 2, MidpointRounding.AwayFromZero));
                        AddCollectedTax(linesByCode, "TVQ", "TVQ / QST", Math.Round(tx.TvqAmount * fx, 2, MidpointRounding.AwayFromZero));
                    }
                    else if (linesByCode.Count > 0)
                    {
                        var firstKey = linesByCode.Keys.First();
                        AddCollectedTax(linesByCode, firstKey, firstKey, Math.Round(tx.TaxAmount * fx, 2, MidpointRounding.AwayFromZero));
                    }
                }
            }
            else
            {
                totalPurchasesSubTotal += subFunc;
                if (tx.TaxComponents is { Count: > 0 })
                {
                    foreach (var tc in tx.TaxComponents.Where(c => c.IsRecoverable))
                    {
                        AddRecoverableTax(linesByCode, tc.Code, tc.Name, Math.Round(tc.Amount * fx, 2, MidpointRounding.AwayFromZero));
                    }
                }
                else if (tx.TaxAmount > 0m)
                {
                    if (linesByCode.ContainsKey("TPS") || linesByCode.ContainsKey("TVQ"))
                    {
                        AddRecoverableTax(linesByCode, "TPS", "TPS / GST", Math.Round(tx.TpsAmount * fx, 2, MidpointRounding.AwayFromZero));
                        AddRecoverableTax(linesByCode, "TVQ", "TVQ / QST", Math.Round(tx.TvqAmount * fx, 2, MidpointRounding.AwayFromZero));
                    }
                    else if (linesByCode.Count > 0)
                    {
                        var firstKey = linesByCode.Keys.First();
                        AddRecoverableTax(linesByCode, firstKey, firstKey, Math.Round(tx.TaxAmount * fx, 2, MidpointRounding.AwayFromZero));
                    }
                }
            }
        }

        period.TotalSalesBeforeTax = Math.Round(totalSalesSubTotal, 2);
        period.TotalPurchasesBeforeTax = Math.Round(totalPurchasesSubTotal, 2);
        period.AuthorityLines = linesByCode.Values.ToList();

        return period;
    }

    public TaxFilingPeriod FileAndClosePeriod(
        DateTime startDate,
        DateTime endDate,
        string frequency = "Quarterly",
        string? filedBy = null)
    {
        var period = CalculateTaxPeriodSummary(startDate, endDate, frequency);
        if (!string.Equals(period.Status, TaxFilingStatus.Open, StringComparison.OrdinalIgnoreCase))
        {
            return period;
        }

        var remittancePayableCode = _ledgerService.ResolveAccountCodeByRole(GlSystemRole.SalesTaxRemittancePayable, "2180");
        var jLines = new List<JournalLine>();

        foreach (var auth in period.AuthorityLines)
        {
            if (auth.TaxCollectedOnSales > 0m)
            {
                // Clear collected tax liability (Debit)
                jLines.Add(new JournalLine
                {
                    GlAccountCode = auth.CollectedLiabilityGlCode,
                    Debit = auth.TaxCollectedOnSales,
                    Credit = 0m,
                    Memo = $"Clear {auth.TaxCode} Collected ({period.PeriodLabel})",
                    TaxCode = auth.TaxCode
                });
            }
            if (auth.InputTaxCreditsOnPurchases > 0m && auth.IsRecoverable)
            {
                // Clear recoverable ITC/ITR (Credit)
                jLines.Add(new JournalLine
                {
                    GlAccountCode = auth.RecoverableAssetGlCode,
                    Debit = 0m,
                    Credit = auth.InputTaxCreditsOnPurchases,
                    Memo = $"Clear {auth.TaxCode} ITC/ITR ({period.PeriodLabel})",
                    TaxCode = auth.TaxCode
                });
            }
        }

        var netDue = period.NetTaxRemittanceDue;
        if (netDue > 0m)
        {
            jLines.Add(new JournalLine
            {
                GlAccountCode = remittancePayableCode,
                Debit = 0m,
                Credit = netDue,
                Memo = $"Net Sales Tax Remittance Payable ({period.PeriodLabel})"
            });
        }
        else if (netDue < 0m)
        {
            jLines.Add(new JournalLine
            {
                GlAccountCode = remittancePayableCode,
                Debit = Math.Abs(netDue),
                Credit = 0m,
                Memo = $"Net Sales Tax Refund Receivable ({period.PeriodLabel})"
            });
        }

        if (jLines.Count >= 2)
        {
            var closeEntry = _ledgerService.SaveManualJournalEntry(new JournalEntry
            {
                Date = period.EndDate,
                Description = $"Sales Tax Filing Close — {period.PeriodLabel}",
                Reference = period.PeriodLabel,
                PartyName = "Tax Authority",
                SourceType = JournalSourceType.TaxRemittanceClose,
                SourceId = period.Id,
                Currency = period.Currency,
                IsLocked = true,
                Lines = jLines
            });
            period.ClosingJournalEntryId = closeEntry.Id;
        }

        period.Status = TaxFilingStatus.Filed;
        period.FiledAt = DateTime.UtcNow;
        period.FiledBy = string.IsNullOrWhiteSpace(filedBy) ? "Admin" : filedBy;

        _accountingContext.TaxFilingPeriods?.Save(period);
        return period;
    }

    public TaxFilingPeriod RecordTaxRemittancePayment(
        string periodId,
        DateTime paymentDate,
        string bankGlCode = "1020",
        string? reference = null)
    {
        var period = _accountingContext.TaxFilingPeriods?.GetById(periodId)
            ?? throw new KeyNotFoundException($"Tax filing period '{periodId}' not found.");

        var remittancePayableCode = _ledgerService.ResolveAccountCodeByRole(GlSystemRole.SalesTaxRemittancePayable, "2180");
        var netDue = period.NetTaxRemittanceDue;

        if (Math.Abs(netDue) > 0m)
        {
            var absAmt = Math.Abs(netDue);
            var isPaymentOut = netDue > 0m;

            var payLines = new List<JournalLine>
            {
                new()
                {
                    GlAccountCode = remittancePayableCode,
                    Debit = isPaymentOut ? absAmt : 0m,
                    Credit = isPaymentOut ? 0m : absAmt,
                    Memo = $"Sales Tax Remittance Settlement ({period.PeriodLabel})"
                },
                new()
                {
                    GlAccountCode = bankGlCode,
                    Debit = isPaymentOut ? 0m : absAmt,
                    Credit = isPaymentOut ? absAmt : 0m,
                    Memo = $"Sales Tax Payment / Refund ({period.PeriodLabel})"
                }
            };

            var payEntry = _ledgerService.SaveManualJournalEntry(new JournalEntry
            {
                Date = paymentDate,
                Description = $"Sales Tax Remittance Payment — {period.PeriodLabel}",
                Reference = reference ?? period.PeriodLabel,
                PartyName = "Tax Authority",
                SourceType = JournalSourceType.TaxRemittancePayment,
                SourceId = period.Id,
                Currency = period.Currency,
                Lines = payLines
            });

            period.PaymentJournalEntryId = payEntry.Id;
        }

        period.Status = TaxFilingStatus.Paid;
        period.PaidAt = paymentDate;
        period.PaymentBankGlCode = bankGlCode;
        period.PaymentReference = reference;

        _accountingContext.TaxFilingPeriods?.Save(period);
        return period;
    }

    private static void AddCollectedTax(Dictionary<string, TaxAuthorityFilingLine> map, string code, string name, decimal amount) =>
        AddTaxLine(map, code, name, amount, isCollected: true);

    private static void AddRecoverableTax(Dictionary<string, TaxAuthorityFilingLine> map, string code, string name, decimal amount) =>
        AddTaxLine(map, code, name, amount, isCollected: false);

    private static void AddTaxLine(
        Dictionary<string, TaxAuthorityFilingLine> map,
        string code,
        string name,
        decimal amount,
        bool isCollected)
    {
        if (amount == 0m) return;
        if (!map.TryGetValue(code, out var line))
        {
            line = new TaxAuthorityFilingLine
            {
                TaxCode = code,
                TaxName = name,
                IsRecoverable = true
            };
            map[code] = line;
        }
        if (isCollected)
        {
            line.TaxCollectedOnSales = Math.Round(line.TaxCollectedOnSales + amount, 2);
        }
        else if (line.IsRecoverable)
        {
            line.InputTaxCreditsOnPurchases = Math.Round(line.InputTaxCreditsOnPurchases + amount, 2);
        }
    }
}
