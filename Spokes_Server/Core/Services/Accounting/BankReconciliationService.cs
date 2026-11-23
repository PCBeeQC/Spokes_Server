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

public class BankMatchCandidate
{
    public JournalEntry Entry { get; set; } = null!;
    public decimal SignedBankMovement { get; set; }
    public decimal Confidence { get; set; }
    public string Summary { get; set; } = string.Empty;
}

public interface IBankReconciliationService
{
    BankStatement ImportAndAutoMatch(string content, string fileName, string glAccountCode = "1020", CsvBankImportMapping? csvMapping = null, Employee? actor = null);
    int AutoMatchStatement(BankStatement statement, Employee? actor = null);
    List<BankMatchCandidate> GetCandidatesForLine(BankStatement statement, BankTransactionLine line, Employee? actor = null);
    void MatchLineToJournalEntry(BankStatement statement, string lineId, string journalEntryId, decimal? settledFunctionalAmount = null, Employee? actor = null);
    JournalEntry CategorizeUnmatchedLineDirect(
        BankStatement statement,
        string lineId,
        string targetGlAccountCode,
        string? memo = null,
        bool includeSalesTaxes = false,
        Employee? actor = null);
    void UnmatchLine(BankStatement statement, string lineId, Employee? actor = null);
    void ReconcileAndLockStatement(BankStatement statement, string? reconciledBy = null, Employee? actor = null);
}

public class BankReconciliationService : IBankReconciliationService
{
    private readonly AccountingContext _accountingContext;
    private readonly ILedgerService _ledgerService;
    private readonly BankStatementImportService _importService;

    [ActivatorUtilitiesConstructor]
    public BankReconciliationService(
        AccountingContext accountingContext,
        ILedgerService? ledgerService = null,
        BankStatementImportService? importService = null)
    {
        ArgumentNullException.ThrowIfNull(accountingContext);
        _accountingContext = accountingContext;
        _ledgerService = ledgerService ?? new LedgerService(accountingContext);
        _importService = importService ?? new BankStatementImportService();
    }

    public BankStatement ImportAndAutoMatch(
        string content,
        string fileName,
        string glAccountCode = "1020",
        CsvBankImportMapping? csvMapping = null,
        Employee? actor = null)
    {
        RequireInteractiveAccounting(actor, "import bank statements");
        var statement = _importService.ParseStatement(content, fileName, glAccountCode, csvMapping);
        var acct = _accountingContext.GlAccounts?.GetByCode(glAccountCode);
        statement.GlAccountName = acct?.GetLocalizedName() ?? glAccountCode;

        // Deduplicate lines by ExternalFitId against previously imported statements on the same bank account
        var existingFitIds = _accountingContext.BankStatements?.GetByAccount(glAccountCode)
            .SelectMany(s => s.Lines)
            .Where(l => !string.IsNullOrWhiteSpace(l.ExternalFitId))
            .Select(l => l.ExternalFitId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase) ?? [];

        if (existingFitIds.Count > 0)
        {
            statement.Lines = statement.Lines
                .Where(l => string.IsNullOrWhiteSpace(l.ExternalFitId) || !existingFitIds.Contains(l.ExternalFitId))
                .ToList();
        }

        AutoMatchStatement(statement, actor);
        _accountingContext.BankStatements?.Save(statement);
        return statement;
    }

    public int AutoMatchStatement(BankStatement statement, Employee? actor = null)
    {
        RequireInteractiveAccounting(actor, "auto-match bank statements");
        ArgumentNullException.ThrowIfNull(statement);
        if (_accountingContext.JournalEntries == null) return 0;

        var usedEntryIds = statement.Lines
            .Where(l => !string.IsNullOrWhiteSpace(l.MatchedJournalEntryId))
            .Select(l => l.MatchedJournalEntryId!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        int matchedCount = 0;

        foreach (var line in statement.Lines)
        {
            if (line.IsMatchedOrCategorized || string.Equals(line.MatchStatus, BankLineMatchStatus.Excluded, StringComparison.OrdinalIgnoreCase))
                continue;

            var candidates = GetCandidatesForLineInternal(statement.GlAccountCode, line, usedEntryIds);
            var best = candidates.FirstOrDefault();
            if (best != null && best.Confidence >= 0.70m)
            {
                line.MatchStatus = BankLineMatchStatus.AutoMatched;
                line.MatchedJournalEntryId = best.Entry.Id;
                line.MatchedSourceType = best.Entry.SourceType;
                line.MatchedSourceId = best.Entry.SourceId;
                line.MatchedDocumentSummary = best.Summary;
                line.MatchConfidence = best.Confidence;
                usedEntryIds.Add(best.Entry.Id);
                matchedCount++;
            }
        }

        _accountingContext.BankStatements?.Save(statement);
        return matchedCount;
    }

    public List<BankMatchCandidate> GetCandidatesForLine(BankStatement statement, BankTransactionLine line, Employee? actor = null)
    {
        RequireInteractiveAccounting(actor, "inspect bank match candidates");
        ArgumentNullException.ThrowIfNull(statement);
        ArgumentNullException.ThrowIfNull(line);
        var usedEntryIds = statement.Lines
            .Where(l => l.Id != line.Id && !string.IsNullOrWhiteSpace(l.MatchedJournalEntryId))
            .Select(l => l.MatchedJournalEntryId!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return GetCandidatesForLineInternal(statement.GlAccountCode, line, usedEntryIds);
    }

    public void MatchLineToJournalEntry(
        BankStatement statement,
        string lineId,
        string journalEntryId,
        decimal? settledFunctionalAmount = null,
        Employee? actor = null)
    {
        RequireInteractiveAccounting(actor, "match bank statement lines");
        ArgumentNullException.ThrowIfNull(statement);
        var line = statement.Lines.FirstOrDefault(l => l.Id == lineId)
            ?? throw new KeyNotFoundException($"Bank line '{lineId}' not found.");
        var entry = _accountingContext.JournalEntries?.GetById(journalEntryId)
            ?? throw new KeyNotFoundException($"Journal entry '{journalEntryId}' not found.");

        // If a foreign-currency settlement differs from booked functional amount, book automatic FX Gain/Loss (5850)
        var bankMovement = GetSignedBankMovement(entry, statement.GlAccountCode);
        var targetMovement = settledFunctionalAmount.HasValue
            ? (line.Amount >= 0m ? Math.Abs(settledFunctionalAmount.Value) : -Math.Abs(settledFunctionalAmount.Value))
            : line.Amount;

        var fxDiff = Math.Round(targetMovement - bankMovement, 2);
        if (Math.Abs(fxDiff) >= 0.01m && !entry.IsLocked)
        {
            ApplyFxSettlementAdjustment(entry, statement.GlAccountCode, targetMovement, fxDiff);
        }

        line.MatchStatus = BankLineMatchStatus.ManuallyMatched;
        line.MatchedJournalEntryId = entry.Id;
        line.MatchedSourceType = entry.SourceType;
        line.MatchedSourceId = entry.SourceId;
        line.MatchedDocumentSummary = $"{entry.EntryNumber} — {entry.Description}";
        line.MatchConfidence = 1.0m;

        _accountingContext.BankStatements?.Save(statement);
    }

    public JournalEntry CategorizeUnmatchedLineDirect(
        BankStatement statement,
        string lineId,
        string targetGlAccountCode,
        string? memo = null,
        bool includeSalesTaxes = false,
        Employee? actor = null)
    {
        RequireInteractiveAccounting(actor, "categorize bank transactions");
        ArgumentNullException.ThrowIfNull(statement);
        var line = statement.Lines.FirstOrDefault(l => l.Id == lineId)
            ?? throw new KeyNotFoundException($"Bank line '{lineId}' not found.");

        var absAmount = Math.Round(Math.Abs(line.Amount), 2, MidpointRounding.AwayFromZero);
        var isDeposit = line.Amount > 0m;
        var bankGlCode = statement.GlAccountCode;
        var description = !string.IsNullOrWhiteSpace(memo)
            ? memo!.Trim()
            : (!string.IsNullOrWhiteSpace(line.Description) ? line.Description.Trim() : line.Payee);

        JournalEntry savedEntry;

        if (_accountingContext.DirectTransactions != null)
        {
            var directTxService = new DirectTransactionService(_accountingContext, _ledgerService);
            var existingTx = _accountingContext.DirectTransactions.GetByBankLine(statement.Id, line.Id);
            var tx = existingTx ?? new DirectTransaction();
            tx.Direction = isDeposit ? DirectTransactionDirection.MoneyIn : DirectTransactionDirection.MoneyOut;
            tx.Date = line.Date;
            tx.PartyName = line.Payee ?? string.Empty;
            tx.Description = description;
            tx.Reference = line.ReferenceNumber ?? string.Empty;
            tx.PaymentMethod = "Bank Statement";
            tx.AccountGlCode = bankGlCode;
            tx.Currency = string.IsNullOrWhiteSpace(statement.Currency) ? "CAD" : statement.Currency;
            tx.ExchangeRate = 1.0m;
            tx.TaxMode = includeSalesTaxes ? DirectTransactionTaxMode.TaxIncluded : DirectTransactionTaxMode.NoTax;
            tx.IsManualTaxOverride = false;
            tx.LinkedBankStatementId = statement.Id;
            tx.LinkedBankLineId = line.Id;
            tx.Lines =
            [
                new DirectTransactionLine
                {
                    Description = description,
                    GlAccountCode = targetGlAccountCode,
                    EnteredAmount = absAmount
                }
            ];

            var savedTx = directTxService.SaveTransaction(tx, actor);
            var syncedEntry = _ledgerService.SyncDirectTransaction(savedTx)
                ?? throw new InvalidOperationException("Failed to post balanced journal entry for direct bank categorization.");
            savedEntry = syncedEntry;
        }
        else
        {
            var (netSub, taxComponents) = FinancialCalculationEngine.BuildTaxComponentsFromGrandTotal(
                absAmount,
                isTaxExempt: !includeSalesTaxes,
                forPurchases: !isDeposit);

            var jLines = new List<JournalLine>();
            if (isDeposit)
            {
                jLines.Add(new JournalLine { GlAccountCode = bankGlCode, Debit = absAmount, Credit = 0m, Memo = description });
                jLines.Add(new JournalLine { GlAccountCode = targetGlAccountCode, Debit = 0m, Credit = netSub, Memo = description });
                foreach (var tc in taxComponents.Where(c => c.Amount > 0m))
                {
                    jLines.Add(new JournalLine { GlAccountCode = tc.GlAccountCode, Debit = 0m, Credit = tc.Amount, Memo = $"{tc.Code} Collected — {description}", TaxCode = tc.Code });
                }
            }
            else
            {
                var nonRecTax = taxComponents.Where(c => !c.IsRecoverable).Sum(c => c.Amount);
                jLines.Add(new JournalLine { GlAccountCode = targetGlAccountCode, Debit = netSub + nonRecTax, Credit = 0m, Memo = description });
                foreach (var tc in taxComponents.Where(c => c.IsRecoverable && c.Amount > 0m))
                {
                    jLines.Add(new JournalLine { GlAccountCode = tc.GlAccountCode, Debit = tc.Amount, Credit = 0m, Memo = $"{tc.Code} ITC/ITR — {description}", TaxCode = tc.Code });
                }
                jLines.Add(new JournalLine { GlAccountCode = bankGlCode, Debit = 0m, Credit = absAmount, Memo = description });
            }

            var entry = new JournalEntry
            {
                Date = line.Date,
                Description = description,
                Reference = line.ReferenceNumber,
                PartyName = line.Payee,
                SourceType = isDeposit ? JournalSourceType.DirectReceipt : JournalSourceType.DirectPayment,
                SourceId = $"{statement.Id}:{line.Id}",
                Currency = statement.Currency,
                Lines = jLines
            };
            savedEntry = _ledgerService.SaveManualJournalEntry(entry);
        }

        line.MatchStatus = BankLineMatchStatus.CategorizedDirect;
        line.DirectGlAccountCode = targetGlAccountCode;
        line.MatchedJournalEntryId = savedEntry.Id;
        line.MatchedSourceType = savedEntry.SourceType;
        line.MatchedSourceId = savedEntry.SourceId;
        line.MatchedDocumentSummary = $"{savedEntry.EntryNumber} — {targetGlAccountCode}";
        line.MatchConfidence = 1.0m;

        _accountingContext.BankStatements?.Save(statement);
        return savedEntry;
    }

    public void UnmatchLine(BankStatement statement, string lineId, Employee? actor = null)
    {
        RequireInteractiveAccounting(actor, "unmatch bank statement lines");
        ArgumentNullException.ThrowIfNull(statement);
        if (string.Equals(statement.Status, BankStatementStatus.Reconciled, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Cannot unmatch a line on a locked/reconciled statement.");
        }

        var line = statement.Lines.FirstOrDefault(l => l.Id == lineId);
        if (line == null) return;

        if (string.Equals(line.MatchStatus, BankLineMatchStatus.CategorizedDirect, StringComparison.OrdinalIgnoreCase))
        {
            if (!string.IsNullOrWhiteSpace(line.MatchedJournalEntryId))
            {
                _ledgerService.DeleteManualJournalEntry(line.MatchedJournalEntryId);
            }

            var linkedTx = _accountingContext.DirectTransactions?.GetByBankLine(statement.Id, line.Id);
            if (linkedTx != null)
            {
                _accountingContext.DirectTransactions?.Delete(linkedTx.Id);
            }
        }

        line.MatchStatus = BankLineMatchStatus.Unmatched;
        line.MatchedJournalEntryId = null;
        line.MatchedSourceType = null;
        line.MatchedSourceId = null;
        line.MatchedDocumentSummary = null;
        line.DirectGlAccountCode = null;
        line.MatchConfidence = 0m;

        _accountingContext.BankStatements?.Save(statement);
    }

    private static void RequireInteractiveAccounting(Employee? actor, string action) =>
        DomainActorGuard.RequireInteractivePermission(actor, AppPermissions.Finance.AccessAccounting, action);

    public void ReconcileAndLockStatement(BankStatement statement, string? reconciledBy = null, Employee? actor = null)
    {
        RequireInteractiveAccounting(actor, "finalize and lock bank statements");
        ArgumentNullException.ThrowIfNull(statement);

        if (Math.Abs(statement.OutOfBalanceDifference) > 0.01m)
        {
            throw new InvalidOperationException(
                $"Cannot finalize reconciliation while out-of-balance difference is {statement.OutOfBalanceDifference:C2}.");
        }

        statement.Status = BankStatementStatus.Reconciled;
        statement.ReconciledAt = DateTime.UtcNow;
        statement.ReconciledBy = string.IsNullOrWhiteSpace(reconciledBy) ? "Admin" : reconciledBy;

        if (_accountingContext.JournalEntries != null)
        {
            foreach (var line in statement.Lines.Where(l => !string.IsNullOrWhiteSpace(l.MatchedJournalEntryId)))
            {
                var entry = _accountingContext.JournalEntries.GetById(line.MatchedJournalEntryId!);
                if (entry != null)
                {
                    entry.IsReconciled = true;
                    entry.IsLocked = true;
                    entry.ReconciledStatementId = statement.Id;
                    _accountingContext.JournalEntries.Save(entry);
                }
            }
        }

        _accountingContext.BankStatements?.Save(statement);
    }

    private List<BankMatchCandidate> GetCandidatesForLineInternal(
        string bankGlCode,
        BankTransactionLine line,
        HashSet<string> usedEntryIds)
    {
        var results = new List<BankMatchCandidate>();
        if (_accountingContext.JournalEntries == null) return results;

        var candidateEntries = _accountingContext.JournalEntries
            .GetByAccountCode(bankGlCode, line.Date.AddDays(-14), line.Date.AddDays(14))
            .Where(j => !j.IsReconciled && !usedEntryIds.Contains(j.Id))
            .ToList();

        foreach (var entry in candidateEntries)
        {
            var movement = GetSignedBankMovement(entry, bankGlCode);
            if (movement == 0m) continue;

            // Direction must match (deposit > 0 vs withdrawal < 0)
            if ((line.Amount > 0m && movement < 0m) || (line.Amount < 0m && movement > 0m))
                continue;

            var amtDiff = Math.Abs(line.Amount - movement);
            var daysDiff = Math.Abs((line.Date.Date - entry.Date.Date).TotalDays);

            decimal score = 0m;
            if (amtDiff <= 0.01m)
            {
                score += 0.65m;
            }
            else if (entry.ExchangeRate != 1.0m && amtDiff <= Math.Abs(movement) * 0.08m)
            {
                // Foreign currency payment settled within 8% FX tolerance
                score += 0.45m;
            }
            else
            {
                continue;
            }

            if (daysDiff <= 1) score += 0.25m;
            else if (daysDiff <= 4) score += 0.18m;
            else if (daysDiff <= 7) score += 0.10m;
            else score += 0.03m;

            if (!string.IsNullOrWhiteSpace(entry.PartyName) &&
                (!string.IsNullOrWhiteSpace(line.Payee) && line.Payee.Contains(entry.PartyName, StringComparison.OrdinalIgnoreCase) ||
                 !string.IsNullOrWhiteSpace(line.Description) && line.Description.Contains(entry.PartyName, StringComparison.OrdinalIgnoreCase)))
            {
                score += 0.10m;
            }
            else if (!string.IsNullOrWhiteSpace(entry.Reference) &&
                     (!string.IsNullOrWhiteSpace(line.ReferenceNumber) && string.Equals(line.ReferenceNumber, entry.Reference, StringComparison.OrdinalIgnoreCase) ||
                      !string.IsNullOrWhiteSpace(line.Description) && line.Description.Contains(entry.Reference, StringComparison.OrdinalIgnoreCase)))
            {
                score += 0.10m;
            }

            results.Add(new BankMatchCandidate
            {
                Entry = entry,
                SignedBankMovement = movement,
                Confidence = Math.Min(1.0m, Math.Round(score, 2)),
                Summary = $"{entry.EntryNumber} ({entry.Date:yyyy-MM-dd}) — {entry.PartyName} {entry.Description}".Trim()
            });
        }

        return results
            .OrderByDescending(c => c.Confidence)
            .ThenBy(c => Math.Abs((c.Entry.Date.Date - line.Date.Date).TotalDays))
            .ToList();
    }

    private static decimal GetSignedBankMovement(JournalEntry entry, string bankGlCode)
    {
        var bankLines = entry.Lines
            .Where(l => string.Equals(l.GlAccountCode, bankGlCode, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (bankLines.Count == 0) return 0m;

        // For both Bank (Asset) and Credit Card (Liability), a Debit increases cash / reduces card balance (positive movement),
        // and a Credit decreases cash / increases card charge (negative movement).
        return Math.Round(bankLines.Sum(l => l.Debit - l.Credit), 2);
    }

    private void ApplyFxSettlementAdjustment(JournalEntry entry, string bankGlCode, decimal targetBankMovement, decimal fxDiff)
    {
        var bankLine = entry.Lines.FirstOrDefault(l => string.Equals(l.GlAccountCode, bankGlCode, StringComparison.OrdinalIgnoreCase));
        if (bankLine == null) return;

        if (targetBankMovement >= 0m)
        {
            bankLine.Debit = Math.Abs(targetBankMovement);
            bankLine.Credit = 0m;
        }
        else
        {
            bankLine.Debit = 0m;
            bankLine.Credit = Math.Abs(targetBankMovement);
        }

        var fxCode = _ledgerService.ResolveAccountCodeByRole(GlSystemRole.ForeignExchangeGainLoss, "5850");
        entry.Lines.Add(new JournalLine
        {
            GlAccountCode = fxCode,
            GlAccountName = fxCode,
            Debit = fxDiff < 0m ? Math.Abs(fxDiff) : 0m,
            Credit = fxDiff > 0m ? Math.Abs(fxDiff) : 0m,
            Memo = "FX Gain/Loss on bank settlement"
        });

        if (entry.IsBalanced)
        {
            _accountingContext.JournalEntries?.Save(entry);
        }
    }
}
