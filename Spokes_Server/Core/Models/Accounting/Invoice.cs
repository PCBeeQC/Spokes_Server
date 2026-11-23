namespace Spokes_Server.Core.Models.Accounting;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;
using Spokes_Server.Core.Data;
using Spokes_Server.Core.Extensions;
using Spokes_Server.Core.Models.Common;
using Spokes_Server.Core.Services.Accounting;

public class Invoice : IDataEntity, IHasDocumentHistory, IHasAssociatedFiles
{
    public string Id { get; set; } = Guid.NewGuid().ToString();

    public override bool Equals(object? obj)
    {
        if (ReferenceEquals(this, obj)) return true;
        return obj is Invoice other && Id == other.Id;
    }

    public override int GetHashCode() => Id?.GetHashCode() ?? base.GetHashCode();
    public string ProjectId { get; set; } = string.Empty;
    public string? ProjectGroupId { get; set; }  // If set, this is a group invoice
    public string InvoiceNumber { get; set; } = string.Empty; // e.g. INV-1001

    public DateTime Date { get; set; } = DateTime.Today;
    public DateTime DueDate { get; set; } = DateTime.Today.AddDays(30);
    public string Status { get; set; } = InvoiceStatus.Draft; // Draft, Final, Paid, Void
    public string PdfPath { get; set; } = string.Empty;

    // Template Override
    public string? TemplateId { get; set; }
    public bool HideCostAndMarkup { get; set; }
    public string Note { get; set; } = string.Empty; // Description/Memo
    public string PaymentTerms { get; set; } = string.Empty;

    // Chart of Accounts & Multi-Currency
    public string? GlAccountCode { get; set; }
    public string Currency { get; set; } = "CAD";
    public decimal ExchangeRate { get; set; } = 1.0m;
    public List<DocumentTaxLine> TaxComponents { get; set; } = [];

    // The Line Items
    public List<InvoiceLine> LineItems { get; set; } = [];

    // TRACKING: Which original source items does this invoice cover?
    // We use this to calculate "Unbilled" items later.
    public List<string> BilledTimeEntryIds { get; set; } = [];
    public List<string> BilledPoItemIds { get; set; } = [];
    public List<string> BilledExpenseItemIds { get; set; } = [];

    // Financials
    public decimal TaxRate { get; set; } = FinancialCalculationEngine.QuebecCombinedTaxRate;
    public decimal SubTotal => FinancialCalculationEngine.RoundCurrency(LineItems.Sum(l => l.Total));
    public decimal TotalCost => LineItems.Sum(l => l.Quantity * l.OriginalUnitCost);
    public decimal GrossMargin => SubTotal - TotalCost;
    public decimal TaxAmount => FinancialCalculationEngine.CalculateTax(SubTotal, TaxRate);
    public decimal TpsAmount => TaxComponents.FirstOrDefault(t =>
        string.Equals(t.Code, "TPS", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(t.Code, "GST", StringComparison.OrdinalIgnoreCase))?.Amount
        ?? FinancialCalculationEngine.CalculateFromSubTotal(SubTotal, TaxRate).TpsAmount;
    public decimal TvqAmount => TaxComponents.FirstOrDefault(t =>
        string.Equals(t.Code, "TVQ", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(t.Code, "QST", StringComparison.OrdinalIgnoreCase))?.Amount
        ?? FinancialCalculationEngine.CalculateFromSubTotal(SubTotal, TaxRate).TvqAmount;
    public decimal GrandTotal => SubTotal + TaxAmount;

    // Payment & Write-Off Tracking
    public List<PaymentRecord> Payments { get; set; } = [];
    public List<BalanceWriteOff> WriteOffs { get; set; } = [];

    [JsonIgnore]
    public decimal AmountPaid => Payments.Sum(p => p.Amount);

    [JsonIgnore]
    public decimal AmountWrittenOff => WriteOffs.Sum(w => w.Amount);

    [JsonIgnore]
    public bool HasWriteOffs => WriteOffs.Count > 0;

    public decimal BalanceDue => Math.Round(GrandTotal - AmountPaid - AmountWrittenOff, 2, MidpointRounding.AwayFromZero);
    public DateTime? DatePaid { get; set; }

    /// <summary>True when imported as a pre-cutover open receivable during accounting migration.</summary>
    public bool IsOpeningBalance { get; set; }
    /// <summary>Links this opening-balance invoice to its AccountingMigrationBatch for rollback.</summary>
    public string? MigrationBatchId { get; set; }

    public List<DocumentHistoryEntry> History { get; set; } = [];

    public IEnumerable<string> GetAssociatedFilePaths()
    {
        if (!string.IsNullOrWhiteSpace(PdfPath)) yield return PdfPath;
    }

    /// <summary>
    /// Creates an independent draft copy of this invoice with regenerated identity and line IDs
    /// and cleared billing, payment, and PDF references so it never aliases the original source items.
    /// </summary>
    public Invoice DuplicateAsDraft(string newInvoiceNumber, string? changedBy = null)
    {
        var copy = this.DeepClone();

        copy.Id = Guid.NewGuid().ToString();
        copy.InvoiceNumber = newInvoiceNumber;
        copy.Date = DateTime.Today;
        copy.DueDate = DateTime.Today.AddDays(30);
        copy.Status = InvoiceStatus.Draft;
        copy.PdfPath = string.Empty;
        copy.Note = string.IsNullOrWhiteSpace(Note) ? $"Duplicated from #{InvoiceNumber}" : $"{Note} (Copy)";
        copy.Payments = [];
        copy.WriteOffs = [];
        copy.DatePaid = null;
        copy.IsOpeningBalance = false;
        copy.MigrationBatchId = null;
        copy.BilledTimeEntryIds = [];
        copy.BilledPoItemIds = [];
        copy.BilledExpenseItemIds = [];

        foreach (var line in copy.LineItems)
        {
            line.Id = Guid.NewGuid().ToString();
            line.SourceId = null;
            line.SourceType = null;
            line.IsCreditUsed = false;
        }

        copy.History = [];
        DocumentHistoryHelper.RecordCreated(copy, changedBy, $"Created (Duplicated from #{InvoiceNumber})");

        return copy;
    }
}

public class InvoiceLine
{
    public string Id { get; set; } = Guid.NewGuid().ToString();

    public override bool Equals(object? obj)
    {
        if (ReferenceEquals(this, obj)) return true;
        return obj is InvoiceLine other && Id == other.Id;
    }

    public override int GetHashCode() => Id?.GetHashCode() ?? base.GetHashCode();
    public string Description { get; set; } = string.Empty;
    public string? WorkTypeId { get; set; }
    public string? SubTaskId { get; set; }
    public string? Note { get; set; } = string.Empty;
    public decimal Quantity { get; set; }
    public decimal UnitPrice { get; set; }
    public decimal Total => FinancialCalculationEngine.RoundCurrency(Quantity * UnitPrice);

    // NEW FIELDS FOR CREDIT LOGIC
    public bool IsPrepayment { get; set; } // Adds to credit balance when paid
    public bool IsCreditUsed { get; set; } // Reduces credit balance immediately

    public string Type { get; set; } = "General"; // General, Labor, Material

    // Tracking Source Item for Dynamic Unbilling
    public string? SourceId { get; set; }
    public string? SourceType { get; set; } // e.g. "PO", "Expense", "Time", "QuoteLabor", "QuoteItem", "Milestone"
    // Markup Transparency
    public decimal OriginalUnitCost { get; set; } = 0;
    public decimal MarkupPercentage { get; set; } = 0; // e.g. 0.15 for 15%
    public string? GlAccountCode { get; set; }
}
