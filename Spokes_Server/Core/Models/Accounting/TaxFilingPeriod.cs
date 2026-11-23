namespace Spokes_Server.Core.Models.Accounting;

using System;
using System.Collections.Generic;
using System.Linq;
using Spokes_Server.Core.Data;

public static class TaxFilingStatus
{
    public const string Open = "Open";
    public const string Filed = "Filed";
    public const string Paid = "Paid";
}

/// <summary>
/// Represents a Sales Tax Return / Remittance Period (e.g. Quarterly TPS/TVQ, GST/HST, or VAT filing).
/// </summary>
public class TaxFilingPeriod : IDataEntity
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string PeriodLabel { get; set; } = string.Empty; // e.g. "2026 Q1 (Jan 1 – Mar 31)"
    public string Frequency { get; set; } = "Quarterly"; // Monthly, Quarterly, Annual
    public DateTime StartDate { get; set; }
    public DateTime EndDate { get; set; }
    public string JurisdictionId { get; set; } = "CA-QC";
    public string Currency { get; set; } = "CAD";

    public string Status { get; set; } = TaxFilingStatus.Open;

    /// <summary>
    /// Total supplies / sales revenue before taxes (e.g. CRA / Revenu Québec Box 101).
    /// </summary>
    public decimal TotalSalesBeforeTax { get; set; }

    /// <summary>
    /// Total eligible purchases/expenses before taxes.
    /// </summary>
    public decimal TotalPurchasesBeforeTax { get; set; }

    public List<TaxAuthorityFilingLine> AuthorityLines { get; set; } = [];

    public decimal TotalTaxCollected => Math.Round(AuthorityLines.Sum(l => l.TaxCollectedOnSales), 2);
    public decimal TotalInputTaxCredits => Math.Round(AuthorityLines.Sum(l => l.InputTaxCreditsOnPurchases), 2);
    public decimal NetTaxRemittanceDue => Math.Round(TotalTaxCollected - TotalInputTaxCredits, 2);

    public DateTime? FiledAt { get; set; }
    public string? FiledBy { get; set; }
    public string? ClosingJournalEntryId { get; set; }

    public DateTime? PaidAt { get; set; }
    public string? PaymentReference { get; set; }
    public string? PaymentBankGlCode { get; set; }
    public string? PaymentJournalEntryId { get; set; }
}

public class TaxAuthorityFilingLine
{
    public string TaxCode { get; set; } = string.Empty; // "TPS", "TVQ", "HST", "VAT"
    public string TaxName { get; set; } = string.Empty;
    public string AuthorityName { get; set; } = string.Empty;
    public decimal Rate { get; set; }
    public bool IsRecoverable { get; set; } = true;

    public string CollectedLiabilityGlCode { get; set; } = "2160";
    public string RecoverableAssetGlCode { get; set; } = "2165";

    /// <summary>
    /// Tax collected on client invoices (e.g. GST/TPS Line 105, QST/TVQ Line 205).
    /// </summary>
    public decimal TaxCollectedOnSales { get; set; }

    /// <summary>
    /// Input Tax Credits / Refunds (CTI / ITC Line 108, RTI / ITR Line 208) on supplier bills, recurring bills, and employee expenses.
    /// </summary>
    public decimal InputTaxCreditsOnPurchases { get; set; }

    /// <summary>
    /// Positive = Net tax owing to authority; Negative = Net refund claimable from authority.
    /// </summary>
    public decimal NetTaxOwing => Math.Round(TaxCollectedOnSales - InputTaxCreditsOnPurchases, 2);
}
