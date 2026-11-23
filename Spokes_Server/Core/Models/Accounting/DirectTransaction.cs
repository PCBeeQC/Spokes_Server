namespace Spokes_Server.Core.Models.Accounting;

using System;
using System.Collections.Generic;
using System.Linq;
using Spokes_Server.Core.Data;
using Spokes_Server.Core.Models.Common;
using Spokes_Server.Core.Services.Accounting;

public static class DirectTransactionDirection
{
    public const string MoneyIn = "MoneyIn";
    public const string MoneyOut = "MoneyOut";
    public const string Transfer = "Transfer";

    public static readonly string[] All = [MoneyIn, MoneyOut, Transfer];
}

public static class DirectTransactionStatus
{
    public const string Posted = "Posted";
    public const string Void = "Void";
}

public static class DirectTransactionTaxMode
{
    public const string NoTax = "NoTax";
    public const string TaxIncluded = "TaxIncluded";
    public const string PlusTax = "PlusTax";

    public static readonly string[] All = [NoTax, TaxIncluded, PlusTax];
}

/// <summary>
/// First-class operational record for cash/bank/card movements outside Invoices (AR) and Bills/POs (AP):
/// Money In (misc. sales, grants, rebates, interest, owner contributions),
/// Money Out (bank fees, direct purchases, loan repayments, owner draws),
/// and Account Transfers (e.g., bank to corporate credit card).
/// Automatically synchronized into balanced double-entry <see cref="JournalEntry"/> records by <see cref="LedgerService"/>.
/// </summary>
public class DirectTransaction : IDataEntity, IHasAssociatedFiles, IHasDocumentHistory
{
    public string Id { get; set; } = Guid.NewGuid().ToString();

    public override bool Equals(object? obj)
    {
        if (ReferenceEquals(this, obj)) return true;
        return obj is DirectTransaction other && Id == other.Id;
    }

    public override int GetHashCode() => Id?.GetHashCode() ?? base.GetHashCode();

    /// <summary>Sequential human-readable tracking number (e.g. "TXN-2611-0001").</summary>
    public string TransactionNumber { get; set; } = string.Empty;

    /// <summary><see cref="DirectTransactionDirection"/>: MoneyIn, MoneyOut, or Transfer.</summary>
    public string Direction { get; set; } = DirectTransactionDirection.MoneyIn;

    /// <summary><see cref="DirectTransactionStatus"/>: Posted or Void.</summary>
    public string Status { get; set; } = DirectTransactionStatus.Posted;

    public DateTime Date { get; set; } = DateTime.Today;

    /// <summary>Counterparty name (who paid us or who we paid).</summary>
    public string PartyName { get; set; } = string.Empty;

    public string Description { get; set; } = string.Empty;

    /// <summary>Cheque #, wire reference, receipt #, scale ticket #, etc.</summary>
    public string Reference { get; set; } = string.Empty;

    public string PaymentMethod { get; set; } = "EFT / Wire";
    public string Notes { get; set; } = string.Empty;

    /// <summary>
    /// Cash / Bank / Credit Card GL account receiving funds (MoneyIn), sending funds (MoneyOut),
    /// or source account (Transfer).
    /// </summary>
    public string AccountGlCode { get; set; } = "1020";

    /// <summary>Destination Cash / Bank / Credit Card GL account (used for Transfer only).</summary>
    public string? DestinationAccountGlCode { get; set; }

    /// <summary><see cref="DirectTransactionTaxMode"/>: NoTax, TaxIncluded, or PlusTax.</summary>
    public string TaxMode { get; set; } = DirectTransactionTaxMode.NoTax;

    /// <summary>True when the user manually edited tax component amounts instead of using automatic rate calculation.</summary>
    public bool IsManualTaxOverride { get; set; }

    /// <summary>Category / offset lines (usually 1 line; supports multi-line splits for MoneyIn / MoneyOut).</summary>
    public List<DirectTransactionLine> Lines { get; set; } = [];

    /// <summary>Total pre-tax amount in transaction currency.</summary>
    public decimal SubTotal { get; set; }

    /// <summary>Total sales tax amount in transaction currency.</summary>
    public decimal TaxAmount { get; set; }

    public decimal TpsAmount { get; set; }
    public decimal TvqAmount { get; set; }

    /// <summary>Total cash/bank movement (SubTotal + TaxAmount) in transaction currency.</summary>
    public decimal GrandTotal { get; set; }

    /// <summary>Explicit multi-jurisdiction sales tax breakdown.</summary>
    public List<DocumentTaxLine> TaxComponents { get; set; } = [];

    public string Currency { get; set; } = "CAD";
    public decimal ExchangeRate { get; set; } = 1.0m;

    /// <summary>Optional header-level project link (lines may override or inherit this value).</summary>
    public string? ProjectId { get; set; }

    // Supporting Voucher / Receipt Attachment
    public string AttachmentPath { get; set; } = string.Empty;
    public string AttachmentName { get; set; } = string.Empty;

    // Bank Reconciliation direct-categorization back-reference (when created from Bank Reconciliation)
    public string? LinkedBankStatementId { get; set; }
    public string? LinkedBankLineId { get; set; }

    // Audit & Void Tracking
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public string CreatedBy { get; set; } = string.Empty;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? VoidedAt { get; set; }
    public string? VoidedBy { get; set; }
    public string? VoidReason { get; set; }

    public bool IsVoid => string.Equals(Status, DirectTransactionStatus.Void, StringComparison.OrdinalIgnoreCase);

    public List<DocumentHistoryEntry> History { get; set; } = [];

    public IEnumerable<string> GetAssociatedFilePaths()
    {
        if (!string.IsNullOrWhiteSpace(AttachmentPath)) yield return AttachmentPath;
    }

    public string PrimaryCategoryGlCode =>
        string.Equals(Direction, DirectTransactionDirection.Transfer, StringComparison.OrdinalIgnoreCase)
            ? (DestinationAccountGlCode ?? string.Empty)
            : (Lines.FirstOrDefault(l => !string.IsNullOrWhiteSpace(l.GlAccountCode))?.GlAccountCode ?? string.Empty);

    public IEnumerable<string> GetReferencedProjectIds()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(ProjectId) && seen.Add(ProjectId))
        {
            yield return ProjectId;
        }
        if (Lines != null)
        {
            foreach (var line in Lines)
            {
                if (!string.IsNullOrWhiteSpace(line.ProjectId) && seen.Add(line.ProjectId))
                {
                    yield return line.ProjectId;
                }
            }
        }
    }
}

public class DirectTransactionLine
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Description { get; set; } = string.Empty;
    public string GlAccountCode { get; set; } = string.Empty;

    /// <summary>
    /// Amount entered by the user on this line (interpreted according to parent <see cref="DirectTransaction.TaxMode"/>:
    /// net amount for NoTax / PlusTax, or tax-included gross amount for TaxIncluded).
    /// </summary>
    public decimal EnteredAmount { get; set; }

    /// <summary>Calculated pre-tax net amount for this line in transaction currency.</summary>
    public decimal SubTotal { get; set; }

    /// <summary>Calculated sales tax attributed to this line in transaction currency.</summary>
    public decimal TaxAmount { get; set; }

    /// <summary>Optional project link for this specific split line (falls back to parent <see cref="DirectTransaction.ProjectId"/>).</summary>
    public string? ProjectId { get; set; }

    public decimal GrandTotal => FinancialCalculationEngine.RoundCurrency(SubTotal + TaxAmount);
}

/// <summary>
/// Functional-currency projection of a single project-attributed direct transaction line.
/// Produced by <see cref="Data.Repositories.Accounting.DirectTransactionRepository.SummarizeForProject"/>.
/// </summary>
public class ProjectDirectTransactionItem
{
    public string TransactionId { get; set; } = string.Empty;
    public string TransactionNumber { get; set; } = string.Empty;
    public DateTime Date { get; set; }
    public string Direction { get; set; } = DirectTransactionDirection.MoneyIn;
    public string PartyName { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string GlAccountCode { get; set; } = string.Empty;
    public decimal SubTotal { get; set; }
    public decimal TaxAmount { get; set; }
    public decimal GrandTotal { get; set; }
    public string AttachmentPath { get; set; } = string.Empty;
}

/// <summary>
/// Canonical rollup of all non-void direct transactions (MoneyIn &amp; MoneyOut, excluding Transfers)
/// attributed to a specific project in functional currency.
/// Shared by ProjectReportService, ProjectOverview, AccountingService (commissions), and BusinessOverheadService.
/// </summary>
public class ProjectDirectTransactionSummary
{
    public decimal DirectIncomeSubTotal { get; set; }
    public decimal DirectIncomeTax { get; set; }
    public decimal DirectIncomeTotal => FinancialCalculationEngine.RoundCurrency(DirectIncomeSubTotal + DirectIncomeTax);

    public decimal DirectExpenseSubTotal { get; set; }
    public decimal DirectExpenseTax { get; set; }
    public decimal DirectExpenseTotal => FinancialCalculationEngine.RoundCurrency(DirectExpenseSubTotal + DirectExpenseTax);

    public List<ProjectDirectTransactionItem> Items { get; set; } = [];
}
