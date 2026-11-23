namespace Spokes_Server.Core.Models.Accounting;

using System;
using System.Collections.Generic;
using System.Linq;
using Spokes_Server.Core.Data;

public static class BankStatementStatus
{
    public const string InProgress = "InProgress";
    public const string Reconciled = "Reconciled";
}

public static class BankLineMatchStatus
{
    public const string Unmatched = "Unmatched";
    public const string AutoMatched = "AutoMatched";
    public const string ManuallyMatched = "ManuallyMatched";
    public const string CategorizedDirect = "CategorizedDirect";
    public const string Excluded = "Excluded";
}

/// <summary>
/// Represents a monthly Bank or Credit Card Statement imported via OFX/QFX, CAMT.053 XML, or CSV
/// for reconciliation against Spokes' General Ledger and operational payments.
/// </summary>
public class BankStatement : IDataEntity, IHasAssociatedFiles
{
    public string Id { get; set; } = Guid.NewGuid().ToString();

    /// <summary>
    /// Linked Bank or Credit Card GL Account Code (e.g. "1020" Operating Bank, "2120" Corporate Credit Card).
    /// </summary>
    public string GlAccountCode { get; set; } = "1020";
    public string GlAccountName { get; set; } = string.Empty;

    public string StatementName { get; set; } = string.Empty;
    public DateTime StartDate { get; set; } = new(DateTime.Today.Year, DateTime.Today.Month, 1);
    public DateTime EndDate { get; set; } = DateTime.Today;
    public string Currency { get; set; } = "CAD";

    public decimal OpeningBalance { get; set; }
    public decimal ClosingBalance { get; set; }

    public string Status { get; set; } = BankStatementStatus.InProgress;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? ReconciledAt { get; set; }
    public string? ReconciledBy { get; set; }

    public string SourceFileName { get; set; } = string.Empty;
    public string SourceFormat { get; set; } = "CSV"; // OFX, QFX, CAMT053, CSV
    public string AttachmentPath { get; set; } = string.Empty;

    public List<BankTransactionLine> Lines { get; set; } = [];

    public decimal TotalNetMovement =>
        Math.Round(Lines.Where(l => !string.Equals(l.MatchStatus, BankLineMatchStatus.Excluded, StringComparison.OrdinalIgnoreCase)).Sum(l => l.Amount), 2);

    public decimal ClearedNetMovement =>
        Math.Round(Lines.Where(l => l.IsMatchedOrCategorized).Sum(l => l.Amount), 2);

    public decimal CalculatedClearedEndingBalance =>
        Math.Round(OpeningBalance + ClearedNetMovement, 2);

    public decimal OutOfBalanceDifference =>
        Math.Round(ClosingBalance - CalculatedClearedEndingBalance, 2);

    public bool IsReadyToReconcile =>
        Lines.Count > 0 &&
        Lines.All(l => l.IsMatchedOrCategorized || string.Equals(l.MatchStatus, BankLineMatchStatus.Excluded, StringComparison.OrdinalIgnoreCase)) &&
        Math.Abs(OutOfBalanceDifference) <= 0.01m;

    public IEnumerable<string> GetAssociatedFilePaths()
    {
        if (!string.IsNullOrWhiteSpace(AttachmentPath)) yield return AttachmentPath;
    }
}

public class BankTransactionLine
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public DateTime Date { get; set; } = DateTime.Today;
    public string Description { get; set; } = string.Empty;
    public string Payee { get; set; } = string.Empty;

    /// <summary>
    /// Positive for deposits / credits into the account; Negative for withdrawals / payments / charges.
    /// </summary>
    public decimal Amount { get; set; }

    public string ReferenceNumber { get; set; } = string.Empty;

    /// <summary>
    /// Financial Institution Transaction ID (OFX FITID / CAMT AcctSvcrRef) for deterministic duplicate prevention.
    /// </summary>
    public string ExternalFitId { get; set; } = string.Empty;

    public string MatchStatus { get; set; } = BankLineMatchStatus.Unmatched;
    public string? MatchedJournalEntryId { get; set; }
    public string? MatchedSourceType { get; set; }
    public string? MatchedSourceId { get; set; }
    public string? MatchedDocumentSummary { get; set; }
    public string? DirectGlAccountCode { get; set; }
    public decimal MatchConfidence { get; set; }

    public bool IsMatchedOrCategorized =>
        string.Equals(MatchStatus, BankLineMatchStatus.AutoMatched, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(MatchStatus, BankLineMatchStatus.ManuallyMatched, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(MatchStatus, BankLineMatchStatus.CategorizedDirect, StringComparison.OrdinalIgnoreCase);
}
