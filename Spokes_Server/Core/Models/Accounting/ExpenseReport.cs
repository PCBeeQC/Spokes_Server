namespace Spokes_Server.Core.Models.Accounting;

using System.Linq;
using System.Text.Json.Serialization;
using Spokes_Server.Core.Data;
using Spokes_Server.Core.Services.Accounting;

public class ExpenseReport : IDataEntity, IHasAssociatedFiles
{
    public string Id { get; set; } = Guid.NewGuid().ToString();

    public override bool Equals(object? obj)
    {
        if (ReferenceEquals(this, obj)) return true;
        return obj is ExpenseReport other && Id == other.Id;
    }

    public override int GetHashCode() => Id?.GetHashCode() ?? base.GetHashCode();
    public string ReportNumber { get; set; } = string.Empty; // e.g. EXP2512-001

    // Employee Info
    public string EmployeeId { get; set; } = string.Empty;
    public string EmployeeName { get; set; } = string.Empty;

    // Dates
    public DateTime DateCreated { get; set; } = DateTime.Today;
    public DateTime? DateSubmitted { get; set; }
    public DateTime? DatePaid { get; set; }

    // Accounting / Sage Export Tracking
    public bool IsExported => ExportedToSageAt.HasValue;
    public DateTime? ExportedToSageAt { get; set; }
    public string? SageBatchId { get; set; }

    // Content
    public List<ExpenseItem> LineItems { get; set; } = [];

    // Backward compatibility for legacy JSON files and existing call sites
    [JsonPropertyName("Items")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<ExpenseItem>? LegacyItems
    {
        get => null;
        set { if (value != null && LineItems.Count == 0) LineItems = value; }
    }

    [Obsolete("Prefer LineItems for consistency across accounting models.")]
    [JsonIgnore]
    public List<ExpenseItem> Items
    {
        get => LineItems;
        set => LineItems = value;
    }

    // Financials
    // This is a calculated property based on items
    public decimal TotalAmount => LineItems.Sum(i => i.Total);
    public decimal SubTotal => LineItems.Sum(i => i.EffectiveSubTotal);
    public decimal TpsAmount => LineItems.Sum(i => i.EffectiveTpsAmount);
    public decimal TvqAmount => LineItems.Sum(i => i.EffectiveTvqAmount);
    public decimal TaxAmount => TpsAmount + TvqAmount;

    public string Status =>
        DatePaid.HasValue ? ExpenseReportStatus.Paid :
        DateSubmitted.HasValue ? ExpenseReportStatus.Submitted :
        ExpenseReportStatus.Draft;

    public IEnumerable<string> GetAssociatedFilePaths() =>
        LineItems?.Select(i => i.ReceiptPath).Where(p => !string.IsNullOrWhiteSpace(p)) ?? Enumerable.Empty<string>();
}

public class ExpenseItem : IHasAssociatedFiles
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string ProjectId { get; set; } = string.Empty;

    public DateTime Date { get; set; } = DateTime.Today;
    public string Description { get; set; } = string.Empty;

    // Type of Expense
    public bool IsKilometrage { get; set; }

    // For Kilometrage
    public decimal Kilometers { get; set; } = 0;
    public decimal KilometrageRate { get; set; } = 0; // The rate at the time of entry

    // For Receipt
    public string ReceiptPath { get; set; } = string.Empty; // Path to file

    // Amount (If Kilometrage, calc from Km * Rate. If regular, manual entry)
    public decimal Amount { get; set; } = 0;

    // Tax & GL Breakdown for Sage 50 Export
    public bool IsTaxExempt { get; set; }
    public decimal SubTotal { get; set; } = 0;
    public decimal TpsAmount { get; set; } = 0;
    public decimal TvqAmount { get; set; } = 0;
    public string? GlAccountCode { get; set; }
    public string? SageItemCode { get; set; }

    public decimal Total => IsKilometrage ? System.Math.Round(Kilometers * KilometrageRate, 2, System.MidpointRounding.AwayFromZero) : Amount;

    [JsonIgnore]
    public decimal EffectiveTpsAmount =>
        IsTaxExempt ? 0m :
        (SubTotal > 0m || TpsAmount > 0m || TvqAmount > 0m) ? TpsAmount :
        FinancialCalculationEngine.CalculateFromGrandTotal(Total, IsTaxExempt).TpsAmount;

    [JsonIgnore]
    public decimal EffectiveTvqAmount =>
        IsTaxExempt ? 0m :
        (SubTotal > 0m || TpsAmount > 0m || TvqAmount > 0m) ? TvqAmount :
        FinancialCalculationEngine.CalculateFromGrandTotal(Total, IsTaxExempt).TvqAmount;

    [JsonIgnore]
    public decimal EffectiveSubTotal =>
        IsTaxExempt ? Total :
        (SubTotal > 0m && System.Math.Abs((SubTotal + TpsAmount + TvqAmount) - Total) <= 0.02m)
            ? Total - (TpsAmount + TvqAmount)
            : FinancialCalculationEngine.CalculateFromGrandTotal(Total, IsTaxExempt).SubTotal;

    public IEnumerable<string> GetAssociatedFilePaths()
    {
        if (!string.IsNullOrWhiteSpace(ReceiptPath)) yield return ReceiptPath;
    }
}

public static class ExpenseReportStatus
{
    public const string Draft = "Draft";
    public const string Submitted = "Submitted";
    public const string Paid = "Paid";
}



