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

public class RecurringExpenseBankCandidate
{
    public RecurringExpense Profile { get; set; } = null!;
    public decimal Confidence { get; set; }
    public string Summary { get; set; } = string.Empty;
}

public class BankCutoverSanityReport
{
    public DateTime? CutoverDate { get; set; }
    public string? MigrationBatchNumber { get; set; }
    public bool HasPreCutoverLines { get; set; }
    public decimal? BankBalanceAtCutover { get; set; }
    public decimal GlBalanceAtCutover { get; set; }
    public decimal? Discrepancy => BankBalanceAtCutover.HasValue
        ? Math.Round(BankBalanceAtCutover.Value - GlBalanceAtCutover, 2)
        : null;
    public bool IsBalancedAtCutover => Discrepancy.HasValue && Math.Abs(Discrepancy.Value) <= 0.05m;
    public int MatchedPreCutoverCount { get; set; }
    public int UnmatchedPreCutoverCount { get; set; }
    public int ExcludedPreCutoverCount { get; set; }
    public int UnmatchedPostCutoverCount { get; set; }
}

public class CrossAccountBankCandidate
{
    public JournalEntry Entry { get; set; } = null!;
    public string CurrentBankGlCode { get; set; } = string.Empty;
    public string CurrentBankGlName { get; set; } = string.Empty;
    public decimal SignedBankMovement { get; set; }
    public decimal Confidence { get; set; }
    public string Summary { get; set; } = string.Empty;
}

public class OpenDocumentBankCandidate
{
    public bool IsInvoice { get; set; }
    public string DocumentId { get; set; } = string.Empty;
    public string DocumentNumber { get; set; } = string.Empty;
    public string PartyName { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public bool RequiresFinalizationOrApproval { get; set; }
    public decimal OpenAmount { get; set; }
    public decimal Confidence { get; set; }
    public string Summary { get; set; } = string.Empty;
}

public interface IBankReconciliationService
{
    BankStatement ImportAndAutoMatch(string content, string fileName, string glAccountCode = "1020", CsvBankImportMapping? csvMapping = null, Employee? actor = null);
    int AutoMatchStatement(BankStatement statement, Employee? actor = null);
    List<BankMatchCandidate> GetCandidatesForLine(BankStatement statement, BankTransactionLine line, Employee? actor = null);
    List<RecurringExpenseBankCandidate> GetRecurringCandidatesForLine(BankStatement statement, BankTransactionLine line, Employee? actor = null);
    List<CrossAccountBankCandidate> GetCrossAccountCandidatesForLine(BankStatement statement, BankTransactionLine line, Employee? actor = null);
    List<OpenDocumentBankCandidate> GetOpenDocumentCandidatesForLine(BankStatement statement, BankTransactionLine line, Employee? actor = null);
    BankCutoverSanityReport EvaluateCutoverSanity(BankStatement statement, Employee? actor = null);
    int ExcludePreCutoverUnmatchedLines(BankStatement statement, DateTime cutoverDate, Employee? actor = null);
    void MatchLineToJournalEntry(BankStatement statement, string lineId, string journalEntryId, decimal? settledFunctionalAmount = null, Employee? actor = null);
    void ReassignAccountAndMatchLine(BankStatement statement, string lineId, string journalEntryId, Employee? actor = null);
    JournalEntry? SettleOpenDocumentAndMatchLine(BankStatement statement, string lineId, OpenDocumentBankCandidate candidate, IAccountingService accountingService, Employee? actor = null);
    JournalEntry? MatchLineToRecurringExpense(BankStatement statement, string lineId, string recurringExpenseId, bool splitTaxIncluded = true, Employee? actor = null);
    List<BankRule> GetBankRules(Employee? actor = null);
    BankRule SaveBankRule(BankRule rule, Employee? actor = null);
    void DeleteBankRule(string ruleId, Employee? actor = null);
    BankRule? MatchRuleForLine(BankStatement statement, BankTransactionLine line);
    int ApplyBankRulesToStatement(BankStatement statement, bool autoPostOnly = false, Employee? actor = null);
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
        ApplyBankRulesToStatement(statement, autoPostOnly: true, actor: actor);
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

    public List<RecurringExpenseBankCandidate> GetRecurringCandidatesForLine(BankStatement statement, BankTransactionLine line, Employee? actor = null)
    {
        RequireInteractiveAccounting(actor, "inspect recurring expense bank match candidates");
        ArgumentNullException.ThrowIfNull(statement);
        ArgumentNullException.ThrowIfNull(line);

        if (line.Amount >= 0m || _accountingContext.RecurringExpenses == null) return [];

        var absAmount = Math.Abs(line.Amount);
        var lineText = PartyMatchingHelper.NormalizeCompanyName($"{line.Payee} {line.Description}");
        var results = new List<RecurringExpenseBankCandidate>();

        foreach (var profile in _accountingContext.RecurringExpenses.GetAll())
        {
            decimal score = 0m;

            var nameNorm = PartyMatchingHelper.NormalizeCompanyName(profile.Name);
            var supNorm = PartyMatchingHelper.NormalizeCompanyName(profile.Supplier);

            bool nameHit = !string.IsNullOrEmpty(nameNorm) && nameNorm.Length >= 3 && lineText.Contains(nameNorm, StringComparison.OrdinalIgnoreCase);
            bool supHit = !string.IsNullOrEmpty(supNorm) && supNorm.Length >= 3 && lineText.Contains(supNorm, StringComparison.OrdinalIgnoreCase);
            if (nameHit || supHit) score += 0.55m;

            if (profile.Amount > 0m)
            {
                var diff = Math.Abs(profile.Amount - absAmount);
                if (diff <= 0.05m) score += 0.45m;
                else if (diff / profile.Amount <= 0.15m) score += 0.25m;
            }

            if (score >= 0.45m)
            {
                var label = string.IsNullOrWhiteSpace(profile.Supplier) ? profile.Name : $"{profile.Name} ({profile.Supplier})";
                results.Add(new RecurringExpenseBankCandidate
                {
                    Profile = profile,
                    Confidence = Math.Min(1.0m, score),
                    Summary = $"{label} · {profile.Frequency}"
                });
            }
        }

        return results.OrderByDescending(r => r.Confidence).Take(5).ToList();
    }

    public JournalEntry? MatchLineToRecurringExpense(
        BankStatement statement,
        string lineId,
        string recurringExpenseId,
        bool splitTaxIncluded = true,
        Employee? actor = null)
    {
        RequireInteractiveAccounting(actor, "match bank line to recurring expense");
        ArgumentNullException.ThrowIfNull(statement);
        var line = statement.Lines.FirstOrDefault(l => l.Id == lineId)
            ?? throw new KeyNotFoundException($"Bank line '{lineId}' not found.");
        var profile = _accountingContext.RecurringExpenses?.GetById(recurringExpenseId)
            ?? throw new KeyNotFoundException($"Recurring expense '{recurringExpenseId}' not found.");

        var absAmount = Math.Round(Math.Abs(line.Amount), 2, MidpointRounding.AwayFromZero);
        bool isTaxExempt = profile.IsTaxExempt || !splitTaxIncluded;
        var breakdown = FinancialCalculationEngine.CalculateFromGrandTotal(absAmount, isTaxExempt);

        var record = new RecurringBillRecord
        {
            Id = Guid.NewGuid().ToString(),
            Date = line.Date.Date,
            DueDate = line.Date.Date,
            Amount = absAmount,
            SubTotal = breakdown.SubTotal,
            TpsAmount = breakdown.TpsAmount,
            TvqAmount = breakdown.TvqAmount,
            TaxAmount = breakdown.TaxAmount,
            IsTaxExempt = isTaxExempt,
            GlAccountCode = profile.GlAccountCode,
            PaymentSourceGlCode = statement.GlAccountCode,
            TaxComponents = FinancialCalculationEngine.BuildTaxComponentsFromSubTotal(
                breakdown.SubTotal,
                FinancialCalculationEngine.ActiveTemplate?.SalesTaxes,
                isTaxExempt,
                forPurchases: true),
            IsPaid = true,
            DatePaid = line.Date.Date,
            Payments =
            [
                new PaymentRecord
                {
                    Id = Guid.NewGuid().ToString(),
                    Date = line.Date.Date,
                    Amount = absAmount,
                    Method = "Bank Withdrawal",
                    Reference = line.Description,
                    BankGlAccountCode = statement.GlAccountCode
                }
            ]
        };

        profile.History ??= [];
        profile.History.Add(record);

        if (BusinessOverheadService.IsScheduledFrequency(profile.Frequency) &&
            line.Date.Date >= profile.NextDueDate.Date)
        {
            var next = profile.NextDueDate.Date;
            int guard = 0;
            while (next <= line.Date.Date && guard++ < 120)
            {
                next = BusinessOverheadService.AdvanceRecurringDate(next, profile.Frequency);
            }
            profile.NextDueDate = next;
        }

        _accountingContext.RecurringExpenses.Save(profile);
        _ledgerService.SyncRecurringExpense(profile);

        var expectedPayKey = $"{profile.Id}:{record.Id}:{record.Payments[0].Id}";
        var paymentJe = _accountingContext.JournalEntries?.GetAll()
            .FirstOrDefault(je =>
                string.Equals(je.SourceType, JournalSourceType.RecurringPayment, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(je.SourceId, expectedPayKey, StringComparison.OrdinalIgnoreCase));

        if (paymentJe != null)
        {
            line.MatchStatus = BankLineMatchStatus.ManuallyMatched;
            line.MatchedJournalEntryId = paymentJe.Id;
            line.MatchedSourceType = paymentJe.SourceType;
            line.MatchedSourceId = paymentJe.SourceId;
            line.MatchedDocumentSummary = $"Recurring: {profile.Name} ({absAmount:C2})";
            line.MatchConfidence = 1.0m;

            paymentJe.IsReconciled = true;
            paymentJe.ReconciledStatementId = statement.Id;
            _accountingContext.JournalEntries?.Save(paymentJe);
            _accountingContext.BankStatements?.Save(statement);
        }

        return paymentJe;
    }

    public BankCutoverSanityReport EvaluateCutoverSanity(BankStatement statement, Employee? actor = null)
    {
        RequireInteractiveAccounting(actor, "evaluate cutover bank sanity");
        ArgumentNullException.ThrowIfNull(statement);

        var report = new BankCutoverSanityReport();
        var latestMigration = (_accountingContext.MigrationBatches?.GetAll() ?? [])
            .Where(b => string.Equals(b.Status, AccountingMigrationBatchStatus.Applied, StringComparison.OrdinalIgnoreCase) &&
                        b.AsOfDate != default)
            .OrderByDescending(b => b.AsOfDate)
            .ThenByDescending(b => b.ExecutedAtUtc)
            .FirstOrDefault();

        if (latestMigration == null) return report;

        var cutover = latestMigration.AsOfDate.Date;
        report.CutoverDate = cutover;
        report.MigrationBatchNumber = latestMigration.BatchNumber;

        var preLines = statement.Lines.Where(l => l.Date.Date <= cutover).ToList();
        var postLines = statement.Lines.Where(l => l.Date.Date > cutover).ToList();

        report.HasPreCutoverLines = preLines.Count > 0;
        report.MatchedPreCutoverCount = preLines.Count(l => l.IsMatchedOrCategorized);
        report.ExcludedPreCutoverCount = preLines.Count(l => string.Equals(l.MatchStatus, BankLineMatchStatus.Excluded, StringComparison.OrdinalIgnoreCase));
        report.UnmatchedPreCutoverCount = preLines.Count(l => !l.IsMatchedOrCategorized && !string.Equals(l.MatchStatus, BankLineMatchStatus.Excluded, StringComparison.OrdinalIgnoreCase));
        report.UnmatchedPostCutoverCount = postLines.Count(l => !l.IsMatchedOrCategorized && !string.Equals(l.MatchStatus, BankLineMatchStatus.Excluded, StringComparison.OrdinalIgnoreCase));

        // Bank running balance as of cutover date (latest line <= cutoverDate that carries a RunningBalance)
        var lastCutoverLine = preLines.LastOrDefault(l => l.RunningBalance.HasValue);
        if (lastCutoverLine?.RunningBalance != null)
        {
            report.BankBalanceAtCutover = lastCutoverLine.RunningBalance.Value;
        }

        // GL signed ending balance on the statement's GL account as of cutoverDate
        var fiscalStart = _ledgerService.GetFiscalYearStart(cutover);
        var tb = _ledgerService.CalculateTrialBalance(fiscalStart, cutover, includeZeroAccounts: true);
        var glRow = tb.Rows.FirstOrDefault(r => string.Equals(r.AccountCode, statement.GlAccountCode, StringComparison.OrdinalIgnoreCase));
        if (glRow != null)
        {
            // For Bank (Asset, IsDebitNormal=true), EndingBalance is positive for Debit, negative for Credit (overdraft).
            // For Credit Card (Liability, IsDebitNormal=false), negate so liability owed matches negative statement balance.
            report.GlBalanceAtCutover = Math.Round(glRow.IsDebitNormal ? glRow.EndingBalance : -glRow.EndingBalance, 2);
        }

        return report;
    }

    public int ExcludePreCutoverUnmatchedLines(BankStatement statement, DateTime cutoverDate, Employee? actor = null)
    {
        RequireInteractiveAccounting(actor, "exclude pre-cutover bank lines");
        ArgumentNullException.ThrowIfNull(statement);

        var cutoff = cutoverDate.Date;
        int excluded = 0;

        foreach (var line in statement.Lines)
        {
            if (line.Date.Date > cutoff) continue;
            if (line.IsMatchedOrCategorized || string.Equals(line.MatchStatus, BankLineMatchStatus.Excluded, StringComparison.OrdinalIgnoreCase))
                continue;

            line.MatchStatus = BankLineMatchStatus.Excluded;
            line.MatchedDocumentSummary = $"Covered by cutover opening balance (<= {cutoff:yyyy-MM-dd})";
            excluded++;
        }

        if (excluded > 0)
        {
            _accountingContext.BankStatements?.Save(statement);
        }

        return excluded;
    }

    public List<CrossAccountBankCandidate> GetCrossAccountCandidatesForLine(BankStatement statement, BankTransactionLine line, Employee? actor = null)
    {
        RequireInteractiveAccounting(actor, "inspect cross-account bank match candidates");
        ArgumentNullException.ThrowIfNull(statement);
        ArgumentNullException.ThrowIfNull(line);
        if (_accountingContext.JournalEntries == null || _accountingContext.GlAccounts == null) return [];

        var otherBankAccounts = _accountingContext.GlAccounts.GetAll()
            .Where(a => !string.Equals(a.Code, statement.GlAccountCode, StringComparison.OrdinalIgnoreCase) &&
                        (string.Equals(a.SubType, GlAccountSubType.Bank, StringComparison.OrdinalIgnoreCase) ||
                         string.Equals(a.SubType, GlAccountSubType.CreditCard, StringComparison.OrdinalIgnoreCase) ||
                         string.Equals(a.SystemRole, GlSystemRole.OperatingBank, StringComparison.OrdinalIgnoreCase) ||
                         string.Equals(a.SystemRole, GlSystemRole.CorporateCreditCard, StringComparison.OrdinalIgnoreCase)))
            .ToDictionary(a => a.Code, StringComparer.OrdinalIgnoreCase);

        if (otherBankAccounts.Count == 0) return [];

        var usedEntryIds = statement.Lines
            .Where(l => l.Id != line.Id && !string.IsNullOrWhiteSpace(l.MatchedJournalEntryId))
            .Select(l => l.MatchedJournalEntryId!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var results = new List<CrossAccountBankCandidate>();

        foreach (var entry in _accountingContext.JournalEntries.GetAll())
        {
            if (entry.IsReconciled || entry.IsLocked || usedEntryIds.Contains(entry.Id)) continue;
            if (string.Equals(entry.SourceType, JournalSourceType.OpeningBalance, StringComparison.OrdinalIgnoreCase)) continue;

            var daysDiff = Math.Abs((entry.Date.Date - line.Date.Date).TotalDays);
            if (daysDiff > 7) continue;

            foreach (var jl in entry.Lines)
            {
                if (!otherBankAccounts.TryGetValue(jl.GlAccountCode, out var otherAcct)) continue;
                var signed = Math.Round(jl.Debit - jl.Credit, 2);
                if (Math.Abs(signed - line.Amount) > 0.05m) continue;

                var conf = daysDiff <= 1 ? 0.95m : (daysDiff <= 3 ? 0.85m : 0.75m);
                results.Add(new CrossAccountBankCandidate
                {
                    Entry = entry,
                    CurrentBankGlCode = otherAcct.Code,
                    CurrentBankGlName = otherAcct.Name,
                    SignedBankMovement = signed,
                    Confidence = conf,
                    Summary = $"{entry.EntryNumber} on {otherAcct.Code} ({entry.PartyName} · {entry.Description})"
                });
                break;
            }
        }

        return results.OrderByDescending(r => r.Confidence).ThenBy(r => Math.Abs((r.Entry.Date.Date - line.Date.Date).TotalDays)).Take(5).ToList();
    }

    public void ReassignAccountAndMatchLine(BankStatement statement, string lineId, string journalEntryId, Employee? actor = null)
    {
        RequireInteractiveAccounting(actor, "reassign bank account and match line");
        ArgumentNullException.ThrowIfNull(statement);
        var line = statement.Lines.FirstOrDefault(l => l.Id == lineId)
            ?? throw new KeyNotFoundException($"Bank line '{lineId}' not found.");
        var entry = _accountingContext.JournalEntries?.GetById(journalEntryId)
            ?? throw new KeyNotFoundException($"Journal entry '{journalEntryId}' not found.");

        if (entry.IsLocked)
            throw new InvalidOperationException($"Journal entry '{entry.EntryNumber}' is locked and cannot be reassigned.");

        var targetAcct = _accountingContext.GlAccounts?.GetByCode(statement.GlAccountCode);
        var bankLine = entry.Lines.FirstOrDefault(jl => Math.Abs(Math.Round(jl.Debit - jl.Credit, 2) - line.Amount) <= 0.05m)
            ?? throw new InvalidOperationException($"No matching movement line found on journal entry '{entry.EntryNumber}'.");

        var oldCode = bankLine.GlAccountCode;
        bankLine.GlAccountCode = statement.GlAccountCode;
        bankLine.GlAccountName = targetAcct?.Name ?? statement.GlAccountCode;
        bankLine.Memo = string.IsNullOrWhiteSpace(bankLine.Memo)
            ? $"Reassigned from {oldCode} during bank reconciliation"
            : $"{bankLine.Memo} (reassigned from {oldCode})";

        entry.IsReconciled = true;
        entry.ReconciledStatementId = statement.Id;
        _accountingContext.JournalEntries.Save(entry);

        line.MatchStatus = BankLineMatchStatus.ManuallyMatched;
        line.MatchedJournalEntryId = entry.Id;
        line.MatchedSourceType = entry.SourceType;
        line.MatchedSourceId = entry.SourceId;
        line.MatchedDocumentSummary = $"Reassigned {oldCode} -> {statement.GlAccountCode}: {entry.EntryNumber} ({entry.Description})";
        line.MatchConfidence = 1.0m;

        _accountingContext.BankStatements?.Save(statement);
    }

    public List<OpenDocumentBankCandidate> GetOpenDocumentCandidatesForLine(BankStatement statement, BankTransactionLine line, Employee? actor = null)
    {
        RequireInteractiveAccounting(actor, "inspect open document bank candidates");
        ArgumentNullException.ThrowIfNull(statement);
        ArgumentNullException.ThrowIfNull(line);

        var absAmt = Math.Abs(line.Amount);
        var lineText = PartyMatchingHelper.NormalizeCompanyName($"{line.Payee} {line.Description}");
        var results = new List<OpenDocumentBankCandidate>();

        if (line.Amount > 0m && _accountingContext.Invoices != null)
        {
            foreach (var inv in _accountingContext.Invoices.GetAll())
            {
                if (string.Equals(inv.Status, InvoiceStatus.Void, StringComparison.OrdinalIgnoreCase) ||
                    InvoiceStatus.IsSettled(inv.Status))
                    continue;

                bool isDraft = string.Equals(inv.Status, InvoiceStatus.Draft, StringComparison.OrdinalIgnoreCase);
                var expected = inv.BalanceDue > 0m ? inv.BalanceDue : inv.GrandTotal;
                if (expected <= 0m || Math.Abs(expected - absAmt) > 0.05m) continue;

                decimal score = 0.75m;
                if (!string.IsNullOrWhiteSpace(inv.InvoiceNumber) &&
                    line.Description.Contains(inv.InvoiceNumber, StringComparison.OrdinalIgnoreCase))
                {
                    score = 0.98m;
                }

                results.Add(new OpenDocumentBankCandidate
                {
                    IsInvoice = true,
                    DocumentId = inv.Id,
                    DocumentNumber = inv.InvoiceNumber,
                    PartyName = string.Empty,
                    Status = inv.Status,
                    RequiresFinalizationOrApproval = isDraft,
                    OpenAmount = expected,
                    Confidence = score,
                    Summary = isDraft
                        ? $"Finalize & Pay Draft {inv.InvoiceNumber} ({expected:C2})"
                        : $"Record Payment on {inv.InvoiceNumber} ({expected:C2})"
                });
            }
        }
        else if (line.Amount < 0m && _accountingContext.Bills != null)
        {
            foreach (var bill in _accountingContext.Bills.GetAll())
            {
                if (bill.IsVoid ||
                    string.Equals(bill.Status, BillStatus.Void, StringComparison.OrdinalIgnoreCase) ||
                    BillStatus.IsSettled(bill.Status))
                    continue;

                bool awaiting = string.Equals(bill.Status, BillStatus.AwaitingApproval, StringComparison.OrdinalIgnoreCase);
                var expected = bill.BalanceDue > 0m ? bill.BalanceDue : bill.Amount;
                if (expected <= 0m || Math.Abs(expected - absAmt) > 0.05m) continue;

                decimal score = 0.70m;
                var supNorm = PartyMatchingHelper.NormalizeCompanyName(bill.SupplierName);
                if (!string.IsNullOrEmpty(supNorm) && supNorm.Length >= 3 && lineText.Contains(supNorm, StringComparison.OrdinalIgnoreCase))
                    score = 0.95m;

                results.Add(new OpenDocumentBankCandidate
                {
                    IsInvoice = false,
                    DocumentId = bill.Id,
                    DocumentNumber = bill.InvoiceNumber ?? bill.Id[..8],
                    PartyName = bill.SupplierName,
                    Status = bill.Status,
                    RequiresFinalizationOrApproval = awaiting,
                    OpenAmount = expected,
                    Confidence = score,
                    Summary = awaiting
                        ? $"Approve & Pay Bill {bill.SupplierName} ({expected:C2})"
                        : $"Pay Bill {bill.SupplierName} ({expected:C2})"
                });
            }
        }

        return results.OrderByDescending(r => r.Confidence).Take(5).ToList();
    }

    public JournalEntry? SettleOpenDocumentAndMatchLine(
        BankStatement statement,
        string lineId,
        OpenDocumentBankCandidate candidate,
        IAccountingService accountingService,
        Employee? actor = null)
    {
        RequireInteractiveAccounting(actor, "settle open document from bank line");
        ArgumentNullException.ThrowIfNull(statement);
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(accountingService);

        var line = statement.Lines.FirstOrDefault(l => l.Id == lineId)
            ?? throw new KeyNotFoundException($"Bank line '{lineId}' not found.");
        var actorName = actor?.FullName ?? "System";
        var absAmt = Math.Round(Math.Abs(line.Amount), 2, MidpointRounding.AwayFromZero);

        var payment = new PaymentRecord
        {
            Id = Guid.NewGuid().ToString(),
            Date = line.Date.Date,
            Amount = absAmt,
            Method = "Bank Statement",
            Reference = !string.IsNullOrWhiteSpace(line.ReferenceNumber) ? line.ReferenceNumber : line.Description,
            Notes = "Settled via Bank Reconciliation",
            BankGlAccountCode = statement.GlAccountCode
        };

        string expectedSourceType;
        string expectedSourceId;

        if (candidate.IsInvoice)
        {
            var inv = _accountingContext.Invoices?.GetById(candidate.DocumentId)
                ?? throw new KeyNotFoundException($"Invoice '{candidate.DocumentNumber}' not found.");
            if (string.Equals(inv.Status, InvoiceStatus.Draft, StringComparison.OrdinalIgnoreCase))
            {
                accountingService.ProcessInvoiceStatusChange(inv, InvoiceStatus.Draft, InvoiceStatus.Final, actorName, actor);
            }
            accountingService.RecordInvoicePayment(inv, payment, actorName, actor);
            expectedSourceType = JournalSourceType.InvoicePayment;
            expectedSourceId = $"{inv.Id}:{payment.Id}";
        }
        else
        {
            var bill = _accountingContext.Bills?.GetById(candidate.DocumentId)
                ?? throw new KeyNotFoundException($"Bill '{candidate.DocumentNumber}' not found.");
            if (string.Equals(bill.Status, BillStatus.AwaitingApproval, StringComparison.OrdinalIgnoreCase))
            {
                accountingService.ApproveBill(bill, actorName, actor);
            }
            accountingService.RecordBillPayment(bill, payment, actorName, actor);
            expectedSourceType = JournalSourceType.BillPayment;
            expectedSourceId = $"{bill.Id}:{payment.Id}";
        }

        var paymentJe = _accountingContext.JournalEntries?.GetAll()
            .FirstOrDefault(je =>
                string.Equals(je.SourceType, expectedSourceType, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(je.SourceId, expectedSourceId, StringComparison.OrdinalIgnoreCase));

        if (paymentJe != null)
        {
            line.MatchStatus = BankLineMatchStatus.ManuallyMatched;
            line.MatchedJournalEntryId = paymentJe.Id;
            line.MatchedSourceType = paymentJe.SourceType;
            line.MatchedSourceId = paymentJe.SourceId;
            line.MatchedDocumentSummary = candidate.Summary;
            line.MatchConfidence = 1.0m;

            paymentJe.IsReconciled = true;
            paymentJe.ReconciledStatementId = statement.Id;
            _accountingContext.JournalEntries?.Save(paymentJe);
            _accountingContext.BankStatements?.Save(statement);
        }

        return paymentJe;
    }

    public List<BankRule> GetBankRules(Employee? actor = null)
    {
        if (actor != null) RequireInteractiveAccounting(actor, "view bank rules");
        return _accountingContext.BankRules?.GetOrderedAll() ?? [];
    }

    public BankRule SaveBankRule(BankRule rule, Employee? actor = null)
    {
        RequireInteractiveAccounting(actor, "save bank rules");
        ArgumentNullException.ThrowIfNull(rule);
        if (string.IsNullOrWhiteSpace(rule.Keywords))
            throw new ArgumentException("Bank rule must specify at least one keyword.", nameof(rule));
        if (string.Equals(rule.Action, BankRuleAction.CategorizeToAccount, StringComparison.OrdinalIgnoreCase) &&
            string.IsNullOrWhiteSpace(rule.TargetGlAccountCode))
            throw new ArgumentException("Categorize rule must specify a target GL account.", nameof(rule));

        if (string.IsNullOrWhiteSpace(rule.Name))
            rule.Name = rule.Keywords.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? "Bank Rule";

        rule.UpdatedAtUtc = DateTime.UtcNow;
        _accountingContext.BankRules?.Save(rule);
        return rule;
    }

    public void DeleteBankRule(string ruleId, Employee? actor = null)
    {
        RequireInteractiveAccounting(actor, "delete bank rules");
        if (string.IsNullOrWhiteSpace(ruleId)) return;
        _accountingContext.BankRules?.Delete(ruleId);
    }

    public BankRule? MatchRuleForLine(BankStatement statement, BankTransactionLine line)
    {
        ArgumentNullException.ThrowIfNull(statement);
        ArgumentNullException.ThrowIfNull(line);
        var rules = _accountingContext.BankRules?.GetOrderedActive();
        if (rules == null || rules.Count == 0) return null;

        var haystack = PartyMatchingHelper.StripDiacritics($"{line.Description} {line.Payee}").ToLowerInvariant();

        foreach (var rule in rules)
        {
            if (!string.IsNullOrWhiteSpace(rule.BankGlAccountCode) &&
                !string.Equals(rule.BankGlAccountCode.Trim(), statement.GlAccountCode, StringComparison.OrdinalIgnoreCase))
                continue;

            if (string.Equals(rule.Direction, BankRuleDirection.MoneyOut, StringComparison.OrdinalIgnoreCase) && line.Amount >= 0m)
                continue;
            if (string.Equals(rule.Direction, BankRuleDirection.MoneyIn, StringComparison.OrdinalIgnoreCase) && line.Amount <= 0m)
                continue;

            var keywords = rule.Keywords
                .Split([',', ';', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(k => PartyMatchingHelper.StripDiacritics(k).ToLowerInvariant())
                .Where(k => k.Length >= 2);

            if (keywords.Any(k => haystack.Contains(k, StringComparison.OrdinalIgnoreCase)))
                return rule;
        }

        return null;
    }

    public int ApplyBankRulesToStatement(BankStatement statement, bool autoPostOnly = false, Employee? actor = null)
    {
        RequireInteractiveAccounting(actor, "apply bank rules");
        ArgumentNullException.ThrowIfNull(statement);
        if (_accountingContext.BankRules == null) return 0;

        // Resolve latest Applied migration cutover date so Bank Rules NEVER double-post pre-cutover lines
        var cutoverDate = (_accountingContext.MigrationBatches?.GetAll() ?? [])
            .Where(b => string.Equals(b.Status, AccountingMigrationBatchStatus.Applied, StringComparison.OrdinalIgnoreCase) &&
                        b.AsOfDate != default)
            .OrderByDescending(b => b.AsOfDate)
            .Select(b => (DateTime?)b.AsOfDate.Date)
            .FirstOrDefault();

        int applied = 0;
        bool statementDirty = false;

        foreach (var line in statement.Lines)
        {
            if (line.IsMatchedOrCategorized || string.Equals(line.MatchStatus, BankLineMatchStatus.Excluded, StringComparison.OrdinalIgnoreCase))
                continue;

            var rule = MatchRuleForLine(statement, line);
            if (rule == null) continue;
            if (autoPostOnly && !rule.AutoPostOnImport) continue;

            if (string.Equals(rule.Action, BankRuleAction.Exclude, StringComparison.OrdinalIgnoreCase))
            {
                line.MatchStatus = BankLineMatchStatus.Excluded;
                line.MatchedDocumentSummary = $"Excluded by rule: {rule.Name}";
                applied++;
                statementDirty = true;
            }
            else if (string.Equals(rule.Action, BankRuleAction.CategorizeToAccount, StringComparison.OrdinalIgnoreCase) &&
                     !string.IsNullOrWhiteSpace(rule.TargetGlAccountCode))
            {
                // Cutover Safety Invariant: never auto-post or bulk-post pre-cutover lines (already in Opening Balance TB)
                if (cutoverDate.HasValue && line.Date.Date <= cutoverDate.Value)
                    continue;

                CategorizeUnmatchedLineDirect(
                    statement,
                    line.Id,
                    rule.TargetGlAccountCode,
                    memo: $"{rule.Name}: {line.Description}",
                    includeSalesTaxes: rule.IncludeSalesTax,
                    actor: actor);
                applied++;
            }
        }

        if (statementDirty)
        {
            _accountingContext.BankStatements?.Save(statement);
        }

        return applied;
    }
}
