namespace Spokes_Server.Core.Models.Accounting;

using System;
using System.Collections.Generic;
using System.Linq;
using Spokes_Server.Core.Data;

public static class JournalSourceType
{
    public const string Invoice = "Invoice";
    public const string InvoicePayment = "InvoicePayment";
    public const string InvoiceWriteOff = "InvoiceWriteOff";
    public const string Bill = "Bill";
    public const string BillPayment = "BillPayment";
    public const string BillWriteOff = "BillWriteOff";
    public const string RecurringBill = "RecurringBill";
    public const string RecurringPayment = "RecurringPayment";
    public const string RecurringWriteOff = "RecurringWriteOff";
    public const string ExpenseReport = "ExpenseReport";
    public const string ExpensePayment = "ExpensePayment";
    public const string Commission = "Commission";
    public const string CommissionPayment = "CommissionPayment";
    public const string PayRun = "PayRun";
    public const string PayRunPayment = "PayRunPayment";
    public const string DasRemittancePayment = "DasRemittancePayment";
    public const string TaxRemittanceClose = "TaxRemittanceClose";
    public const string TaxRemittancePayment = "TaxRemittancePayment";
    public const string DirectReceipt = "DirectReceipt";
    public const string DirectPayment = "DirectPayment";
    public const string AccountTransfer = "AccountTransfer";
    public const string BankFeeOrInterest = "BankFeeOrInterest";
    public const string Manual = "Manual";
    public const string AccountantAdjustment = "AccountantAdjustment";
    public const string OpeningBalance = "OpeningBalance";
}

/// <summary>
/// Balanced Double-Entry General Ledger Journal Entry.
/// Persisted via <see cref="Data.Repositories.Accounting.JournalEntryRepository"/>.
/// Invariant: <c>TotalDebits == TotalCredits</c>.
/// </summary>
public class JournalEntry : IDataEntity
{
    public string Id { get; set; } = Guid.NewGuid().ToString();

    /// <summary>
    /// Sequential human-readable entry number generated via <see cref="SequenceService"/> (e.g. "JE-2611-0001").
    /// </summary>
    public string EntryNumber { get; set; } = string.Empty;

    public DateTime Date { get; set; } = DateTime.Today;
    public string Description { get; set; } = string.Empty;
    public string Reference { get; set; } = string.Empty; // Invoice #, Bill #, Check #, etc.
    public string PartyName { get; set; } = string.Empty; // Client, Supplier, or Employee name

    /// <summary>
    /// Operational source category (<see cref="JournalSourceType"/>).
    /// </summary>
    public string SourceType { get; set; } = JournalSourceType.Manual;

    /// <summary>
    /// ID of the source operational entity (e.g. Invoice.Id, PaymentRecord.Id, Bill.Id, PayRun.Id).
    /// </summary>
    public string? SourceId { get; set; }

    public string? ProjectId { get; set; }
    public string Currency { get; set; } = "CAD";
    public decimal ExchangeRate { get; set; } = 1.0m;

    /// <summary>
    /// True once locked by a reconciled BankStatement or closed TaxFilingPeriod / Fiscal Year.
    /// </summary>
    public bool IsLocked { get; set; }
    public bool IsReconciled { get; set; }
    public string? ReconciledStatementId { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public string CreatedBy { get; set; } = "System";

    public List<JournalLine> Lines { get; set; } = [];

    public decimal TotalDebits => Math.Round(Lines.Sum(l => l.Debit), 2, MidpointRounding.AwayFromZero);
    public decimal TotalCredits => Math.Round(Lines.Sum(l => l.Credit), 2, MidpointRounding.AwayFromZero);
    public bool IsBalanced => Math.Abs(TotalDebits - TotalCredits) <= 0.005m && Lines.Count >= 2;
}

public class JournalLine
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string GlAccountCode { get; set; } = string.Empty;
    public string GlAccountName { get; set; } = string.Empty;
    public string Memo { get; set; } = string.Empty;
    public decimal Debit { get; set; }
    public decimal Credit { get; set; }
    public string? ProjectId { get; set; }
    public string? TaxCode { get; set; }
}
