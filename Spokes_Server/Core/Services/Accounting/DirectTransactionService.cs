namespace Spokes_Server.Core.Services.Accounting;

using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.DependencyInjection;
using Spokes_Server.Aggregate.Contexts;
using Spokes_Server.Core.Constants;
using Spokes_Server.Core.Extensions;
using Spokes_Server.Core.Models.Accounting;
using Spokes_Server.Core.Models.Common;
using Spokes_Server.Core.Models.HR;
using Spokes_Server.Core.Services.Security;

public interface IDirectTransactionService
{
    List<DirectTransaction> GetAll(Employee? actor = null);
    DirectTransaction? GetById(string id, Employee? actor = null);
    void RecalculateTotals(DirectTransaction transaction);
    DirectTransaction SaveTransaction(DirectTransaction transaction, Employee? actor = null);
    DirectTransaction VoidTransaction(string id, string reason, Employee? actor = null);
    List<JournalLine> BuildLedgerPreview(DirectTransaction draft);
    (bool IsLocked, string? Reason) IsTransactionLocked(DirectTransaction transaction);
}

public class DirectTransactionService : IDirectTransactionService
{
    private readonly AccountingContext _accountingContext;
    private readonly ILedgerService _ledgerService;
    private readonly IJurisdictionTemplateService? _templateService;

    [ActivatorUtilitiesConstructor]
    public DirectTransactionService(
        AccountingContext accountingContext,
        ILedgerService? ledgerService = null,
        IJurisdictionTemplateService? templateService = null)
    {
        ArgumentNullException.ThrowIfNull(accountingContext);
        _accountingContext = accountingContext;
        _ledgerService = ledgerService ?? new LedgerService(accountingContext, templateService: templateService);
        _templateService = templateService;
    }

    public List<DirectTransaction> GetAll(Employee? actor = null)
    {
        DomainActorGuard.RequirePermission(actor, AppPermissions.Finance.AccessAccounting, "view direct transactions", allowSuspended: true);
        return (_accountingContext.DirectTransactions?.GetOrdered() ?? [])
            .Select(t => t.DeepClone()!)
            .ToList();
    }

    public DirectTransaction? GetById(string id, Employee? actor = null)
    {
        DomainActorGuard.RequirePermission(actor, AppPermissions.Finance.AccessAccounting, "view direct transaction", allowSuspended: true);
        if (string.IsNullOrWhiteSpace(id)) return null;
        return _accountingContext.DirectTransactions?.GetById(id)?.DeepClone();
    }

    public void RecalculateTotals(DirectTransaction transaction)
    {
        ArgumentNullException.ThrowIfNull(transaction);

        var activeTemplate = _templateService?.GetActiveTemplate()
            ?? FinancialCalculationEngine.ActiveTemplate;

        if (string.IsNullOrWhiteSpace(transaction.Currency))
        {
            transaction.Currency = activeTemplate?.DefaultCurrency ?? "CAD";
        }
        if (transaction.ExchangeRate <= 0m)
        {
            transaction.ExchangeRate = 1.0m;
        }

        transaction.Lines ??= [];

        if (string.Equals(transaction.Direction, DirectTransactionDirection.Transfer, StringComparison.OrdinalIgnoreCase))
        {
            transaction.TaxMode = DirectTransactionTaxMode.NoTax;
            transaction.IsManualTaxOverride = false;
            transaction.TaxComponents = [];
            transaction.TaxAmount = 0m;
            transaction.TpsAmount = 0m;
            transaction.TvqAmount = 0m;
            transaction.ProjectId = null;

            var transferAmount = transaction.GrandTotal > 0m
                ? transaction.GrandTotal
                : transaction.Lines.Sum(l => Math.Max(0m, l.EnteredAmount));
            transaction.GrandTotal = FinancialCalculationEngine.RoundCurrency(Math.Max(0m, transferAmount));
            transaction.SubTotal = transaction.GrandTotal;
            transaction.Lines.Clear();
            return;
        }

        var taxRules = activeTemplate?.SalesTaxes;
        var isPurchase = string.Equals(transaction.Direction, DirectTransactionDirection.MoneyOut, StringComparison.OrdinalIgnoreCase);

        foreach (var line in transaction.Lines)
        {
            line.EnteredAmount = FinancialCalculationEngine.RoundCurrency(Math.Max(0m, line.EnteredAmount));
        }

        if (string.Equals(transaction.TaxMode, DirectTransactionTaxMode.NoTax, StringComparison.OrdinalIgnoreCase))
        {
            transaction.IsManualTaxOverride = false;
            foreach (var line in transaction.Lines)
            {
                line.SubTotal = line.EnteredAmount;
                line.TaxAmount = 0m;
            }
            transaction.SubTotal = FinancialCalculationEngine.RoundCurrency(transaction.Lines.Sum(l => l.SubTotal));
            transaction.TaxAmount = 0m;
            transaction.TpsAmount = 0m;
            transaction.TvqAmount = 0m;
            transaction.GrandTotal = transaction.SubTotal;
            transaction.TaxComponents = [];
            return;
        }

        if (string.Equals(transaction.TaxMode, DirectTransactionTaxMode.TaxIncluded, StringComparison.OrdinalIgnoreCase))
        {
            var totalEntered = FinancialCalculationEngine.RoundCurrency(transaction.Lines.Sum(l => l.EnteredAmount));

            if (transaction.IsManualTaxOverride && transaction.TaxComponents is { Count: > 0 })
            {
                NormalizeManualTaxComponents(transaction.TaxComponents, taxRules, isPurchase);
                var manualTax = FinancialCalculationEngine.RoundCurrency(transaction.TaxComponents.Sum(c => c.Amount));
                transaction.TaxAmount = Math.Min(manualTax, totalEntered);
                transaction.SubTotal = FinancialCalculationEngine.RoundCurrency(totalEntered - transaction.TaxAmount);
                transaction.GrandTotal = totalEntered;
            }
            else
            {
                var (netSub, components) = FinancialCalculationEngine.BuildTaxComponentsFromGrandTotal(
                    totalEntered,
                    taxRules,
                    isTaxExempt: false,
                    forPurchases: isPurchase);

                transaction.SubTotal = netSub;
                transaction.TaxComponents = components;
                transaction.TaxAmount = FinancialCalculationEngine.RoundCurrency(components.Sum(c => c.Amount));
                transaction.GrandTotal = totalEntered;
            }

            DistributeTotalsAcrossLines(transaction.Lines, transaction.SubTotal, transaction.TaxAmount);
        }
        else // PlusTax
        {
            foreach (var line in transaction.Lines)
            {
                line.SubTotal = line.EnteredAmount;
            }

            var totalSub = FinancialCalculationEngine.RoundCurrency(transaction.Lines.Sum(l => l.SubTotal));
            transaction.SubTotal = totalSub;

            if (transaction.IsManualTaxOverride && transaction.TaxComponents is { Count: > 0 })
            {
                NormalizeManualTaxComponents(transaction.TaxComponents, taxRules, isPurchase);
                transaction.TaxAmount = FinancialCalculationEngine.RoundCurrency(transaction.TaxComponents.Sum(c => c.Amount));
            }
            else
            {
                var components = FinancialCalculationEngine.BuildTaxComponentsFromSubTotal(
                    totalSub,
                    taxRules,
                    isTaxExempt: false,
                    forPurchases: isPurchase);

                transaction.TaxComponents = components;
                transaction.TaxAmount = FinancialCalculationEngine.RoundCurrency(components.Sum(c => c.Amount));
            }

            transaction.GrandTotal = FinancialCalculationEngine.RoundCurrency(transaction.SubTotal + transaction.TaxAmount);
            DistributeTaxOnlyAcrossLines(transaction.Lines, transaction.TaxAmount);
        }

        SyncLegacyTaxSummaryFields(transaction);
    }

    public DirectTransaction SaveTransaction(DirectTransaction transaction, Employee? actor = null)
    {
        DomainActorGuard.RequireInteractivePermission(actor, AppPermissions.Finance.AccessAccounting, "save direct transactions");
        ArgumentNullException.ThrowIfNull(transaction);

        var repo = _accountingContext.DirectTransactions
            ?? throw new InvalidOperationException("DirectTransactions repository is not configured.");

        var existing = !string.IsNullOrWhiteSpace(transaction.Id) ? repo.GetById(transaction.Id) : null;
        if (existing != null)
        {
            if (existing.IsVoid)
            {
                throw new InvalidOperationException("Voided transactions cannot be modified.");
            }
            EnsureNotLocked(existing, "edit");
        }

        EnsureDateAfterPeriodLock(transaction.Date);
        RecalculateTotals(transaction);
        ValidateTransaction(transaction);

        var actorName = !string.IsNullOrWhiteSpace(actor?.FullName) ? actor!.FullName : "Admin";
        var now = DateTime.UtcNow;

        if (existing == null)
        {
            if (string.IsNullOrWhiteSpace(transaction.Id))
            {
                transaction.Id = Guid.NewGuid().ToString();
            }
            if (string.IsNullOrWhiteSpace(transaction.TransactionNumber))
            {
                transaction.TransactionNumber = repo.GenerateTransactionNumber();
            }
            transaction.Status = DirectTransactionStatus.Posted;
            transaction.CreatedAt = now;
            transaction.CreatedBy = actorName;
            transaction.UpdatedAt = now;
            DocumentHistoryHelper.RecordCreated(
                transaction,
                actorName,
                $"{transaction.Direction} {transaction.GrandTotal:C2} ({transaction.Currency})");
        }
        else
        {
            transaction.TransactionNumber = existing.TransactionNumber;
            transaction.Status = existing.Status;
            transaction.CreatedAt = existing.CreatedAt;
            transaction.CreatedBy = existing.CreatedBy;
            transaction.UpdatedAt = now;
            transaction.LinkedBankStatementId = existing.LinkedBankStatementId;
            transaction.LinkedBankLineId = existing.LinkedBankLineId;
            transaction.History = existing.History?.ToList() ?? [];
            DocumentHistoryHelper.RecordPaymentAction(
                transaction,
                DocumentHistoryActions.Edited,
                existing.Status,
                transaction.Status,
                $"Updated {transaction.Direction} to {transaction.GrandTotal:C2} ({transaction.Currency})",
                actorName);
        }

        repo.Save(transaction);
        _ledgerService.SyncDirectTransaction(transaction);
        return transaction.DeepClone()!;
    }

    public DirectTransaction VoidTransaction(string id, string reason, Employee? actor = null)
    {
        DomainActorGuard.RequireInteractivePermission(actor, AppPermissions.Finance.AccessAccounting, "void direct transactions");
        if (string.IsNullOrWhiteSpace(id))
            throw new ArgumentException("Transaction ID is required.", nameof(id));
        if (string.IsNullOrWhiteSpace(reason))
            throw new InvalidOperationException("A reason is required to void a transaction.");

        var repo = _accountingContext.DirectTransactions
            ?? throw new InvalidOperationException("DirectTransactions repository is not configured.");

        var existing = repo.GetById(id)
            ?? throw new KeyNotFoundException($"Direct transaction '{id}' was not found.");

        if (existing.IsVoid)
            return existing.DeepClone()!;

        EnsureNotLocked(existing, "void");

        var actorName = !string.IsNullOrWhiteSpace(actor?.FullName) ? actor!.FullName : "Admin";
        var now = DateTime.UtcNow;

        existing.Status = DirectTransactionStatus.Void;
        existing.VoidedAt = now;
        existing.VoidedBy = actorName;
        existing.VoidReason = reason.Trim();
        existing.UpdatedAt = now;
        DocumentHistoryHelper.RecordStatusChange(
            existing,
            DirectTransactionStatus.Posted,
            DirectTransactionStatus.Void,
            actorName,
            $"Voided {existing.TransactionNumber} ({existing.GrandTotal:C2})",
            reason.Trim());

        repo.Save(existing);
        _ledgerService.SyncDirectTransaction(existing);
        return existing.DeepClone()!;
    }

    public List<JournalLine> BuildLedgerPreview(DirectTransaction draft)
    {
        ArgumentNullException.ThrowIfNull(draft);
        var copy = draft.DeepClone() ?? new DirectTransaction();
        RecalculateTotals(copy);
        return _ledgerService.BuildDirectTransactionJournalLines(copy);
    }

    public (bool IsLocked, string? Reason) IsTransactionLocked(DirectTransaction transaction)
    {
        if (transaction == null) return (false, null);

        var periodLockDate = _accountingContext.GetAccountingConfig().PeriodLockDate;
        if (periodLockDate.HasValue && transaction.Date.Date <= periodLockDate.Value.Date)
        {
            return (true, $"Locked by accounting period close through {periodLockDate.Value:yyyy-MM-dd}.");
        }

        if (_accountingContext.JournalEntries != null && !string.IsNullOrWhiteSpace(transaction.Id))
        {
            var je = FindLinkedJournalEntry(transaction.Id);
            if (je != null && (je.IsLocked || je.IsReconciled))
            {
                return (true, "Locked because this transaction has been reconciled on a finalized bank statement.");
            }
        }

        return (false, null);
    }

    private JournalEntry? FindLinkedJournalEntry(string transactionId)
    {
        var repo = _accountingContext.JournalEntries;
        if (repo == null) return null;

        return repo.GetBySource(JournalSourceType.DirectReceipt, transactionId)
            ?? repo.GetBySource(JournalSourceType.DirectPayment, transactionId)
            ?? repo.GetBySource(JournalSourceType.AccountTransfer, transactionId);
    }

    private void EnsureNotLocked(DirectTransaction tx, string action)
    {
        var (isLocked, reason) = IsTransactionLocked(tx);
        if (isLocked)
        {
            throw new InvalidOperationException($"Cannot {action} {tx.TransactionNumber}: {reason}");
        }
    }

    private void EnsureDateAfterPeriodLock(DateTime date)
    {
        var periodLockDate = _accountingContext.GetAccountingConfig().PeriodLockDate;
        if (periodLockDate.HasValue && date.Date <= periodLockDate.Value.Date)
        {
            throw new InvalidOperationException(
                $"Cannot post a transaction dated {date:yyyy-MM-dd} because the accounting period is locked through {periodLockDate.Value:yyyy-MM-dd}.");
        }
    }

    private static void ValidateTransaction(DirectTransaction tx)
    {
        if (string.IsNullOrWhiteSpace(tx.AccountGlCode))
        {
            throw new InvalidOperationException("Please select a bank, cash, or credit card account.");
        }

        if (tx.GrandTotal <= 0m)
        {
            throw new InvalidOperationException("Transaction amount must be greater than zero.");
        }

        if (string.Equals(tx.Direction, DirectTransactionDirection.Transfer, StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(tx.DestinationAccountGlCode))
            {
                throw new InvalidOperationException("Please select a destination account for the transfer.");
            }
            if (string.Equals(tx.AccountGlCode.Trim(), tx.DestinationAccountGlCode.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Source and destination accounts must be different for a transfer.");
            }
            return;
        }

        if (tx.Lines == null || tx.Lines.Count == 0)
        {
            throw new InvalidOperationException("Please enter at least one category line.");
        }

        foreach (var line in tx.Lines)
        {
            if (string.IsNullOrWhiteSpace(line.GlAccountCode))
            {
                throw new InvalidOperationException("Every line must have a category / GL account selected.");
            }
            if (line.EnteredAmount <= 0m)
            {
                throw new InvalidOperationException("Every line must have an amount greater than zero.");
            }
        }
    }

    private static void NormalizeManualTaxComponents(
        List<DocumentTaxLine> components,
        IReadOnlyList<SalesTaxRule>? rules,
        bool isPurchase)
    {
        foreach (var comp in components)
        {
            comp.Amount = FinancialCalculationEngine.RoundCurrency(Math.Max(0m, comp.Amount));
            if (string.IsNullOrWhiteSpace(comp.GlAccountCode) && rules != null)
            {
                var matchingRule = rules.FirstOrDefault(r => string.Equals(r.Code, comp.Code, StringComparison.OrdinalIgnoreCase));
                if (matchingRule != null)
                {
                    comp.GlAccountCode = isPurchase
                        ? (!string.IsNullOrWhiteSpace(matchingRule.RecoverableAssetGlCode) ? matchingRule.RecoverableAssetGlCode : matchingRule.CollectedLiabilityGlCode)
                        : matchingRule.CollectedLiabilityGlCode;
                    comp.IsRecoverable = matchingRule.IsRecoverable;
                    comp.AuthorityName = matchingRule.AuthorityName;
                }
            }
        }
    }

    private static void DistributeTotalsAcrossLines(List<DirectTransactionLine> lines, decimal totalSub, decimal totalTax)
    {
        if (lines.Count == 0) return;

        var totalEntered = lines.Sum(l => l.EnteredAmount);
        if (totalEntered <= 0m)
        {
            foreach (var l in lines)
            {
                l.SubTotal = 0m;
                l.TaxAmount = 0m;
            }
            return;
        }

        decimal runningSub = 0m;
        decimal runningTax = 0m;
        int lastPositiveIdx = lines.FindLastIndex(l => l.EnteredAmount > 0m);
        if (lastPositiveIdx < 0) lastPositiveIdx = lines.Count - 1;

        for (int i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            if (i == lastPositiveIdx)
            {
                line.SubTotal = FinancialCalculationEngine.RoundCurrency(totalSub - runningSub);
                line.TaxAmount = FinancialCalculationEngine.RoundCurrency(totalTax - runningTax);
            }
            else if (line.EnteredAmount > 0m)
            {
                var share = line.EnteredAmount / totalEntered;
                line.SubTotal = FinancialCalculationEngine.RoundCurrency(totalSub * share);
                line.TaxAmount = FinancialCalculationEngine.RoundCurrency(totalTax * share);
                runningSub += line.SubTotal;
                runningTax += line.TaxAmount;
            }
            else
            {
                line.SubTotal = 0m;
                line.TaxAmount = 0m;
            }
        }
    }

    private static void DistributeTaxOnlyAcrossLines(List<DirectTransactionLine> lines, decimal totalTax)
    {
        if (lines.Count == 0) return;

        var totalSub = lines.Sum(l => l.SubTotal);
        if (totalSub <= 0m || totalTax == 0m)
        {
            foreach (var l in lines) l.TaxAmount = 0m;
            return;
        }

        decimal runningTax = 0m;
        int lastPositiveIdx = lines.FindLastIndex(l => l.SubTotal > 0m);
        if (lastPositiveIdx < 0) lastPositiveIdx = lines.Count - 1;

        for (int i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            if (i == lastPositiveIdx)
            {
                line.TaxAmount = FinancialCalculationEngine.RoundCurrency(totalTax - runningTax);
            }
            else if (line.SubTotal > 0m)
            {
                line.TaxAmount = FinancialCalculationEngine.RoundCurrency(totalTax * (line.SubTotal / totalSub));
                runningTax += line.TaxAmount;
            }
            else
            {
                line.TaxAmount = 0m;
            }
        }
    }

    private static void SyncLegacyTaxSummaryFields(DirectTransaction tx)
    {
        decimal tps = 0m;
        decimal tvq = 0m;
        foreach (var comp in tx.TaxComponents)
        {
            if (string.Equals(comp.Code, "TPS", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(comp.Code, "GST", StringComparison.OrdinalIgnoreCase))
            {
                tps += comp.Amount;
            }
            else if (string.Equals(comp.Code, "TVQ", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(comp.Code, "QST", StringComparison.OrdinalIgnoreCase))
            {
                tvq += comp.Amount;
            }
        }

        tx.TpsAmount = FinancialCalculationEngine.RoundCurrency(tps);
        tx.TvqAmount = FinancialCalculationEngine.RoundCurrency(tvq);
    }
}
