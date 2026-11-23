namespace Spokes_Server.Core.Models.Accounting;

using System;

/// <summary>
/// Non-cash balance adjustment / write-off applied to an Invoice, Bill, or RecurringBillRecord.
/// Reduces <c>BalanceDue</c> and clears AR/AP in the General Ledger without touching Bank or Credit Card accounts.
/// </summary>
public class BalanceWriteOff
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public DateTime Date { get; set; } = DateTime.Today;
    public decimal Amount { get; set; }
    public string Reason { get; set; } = string.Empty;

    /// <summary>
    /// Optional non-cash offset GL Account Code (e.g. "5860" Bad Debts &amp; Balance Write-Offs).
    /// When null/empty, LedgerService resolves from <see cref="GlSystemRole.BadDebtsAndWriteOffs"/> ("5860").
    /// </summary>
    public string? GlAccountCode { get; set; }

    public string CreatedBy { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
