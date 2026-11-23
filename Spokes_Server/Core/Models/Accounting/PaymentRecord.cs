namespace Spokes_Server.Core.Models.Accounting;

using System;

public class PaymentRecord
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public DateTime Date { get; set; } = DateTime.Today;
    public decimal Amount { get; set; }
    public string Reference { get; set; } = string.Empty; // Check #, Wire Ref
    public string Method { get; set; } = "Check"; // Optional: Check, Credit Card, Wire, ACH, Cash
    public string Notes { get; set; } = string.Empty;

    /// <summary>
    /// Optional explicit Bank or Credit Card GL Account Code (e.g. "1020" Operating Bank, "2120" Corporate Credit Card).
    /// When null/empty, LedgerService resolves from Method ("Credit Card" -> CorporateCreditCard, otherwise OperatingBank).
    /// </summary>
    public string? BankGlAccountCode { get; set; }

    /// <summary>
    /// Optional settled amount in the company's functional currency (e.g. CAD) when paying a foreign-currency Invoice or Bill.
    /// Used by LedgerService & BankReconciliationService to book automatic Foreign Exchange Gain/Loss (5850).
    /// </summary>
    public decimal? SettledFunctionalAmount { get; set; }
}
