namespace Spokes_Server.Core.Models.Accounting;

using Spokes_Server.Core.Models.Core;
using Spokes_Server.Core.Models.Projects;
using Spokes_Server.Core.Models.Communication;
using Spokes_Server.Core.Models.HR;

using Spokes_Server.Core.Data;




public class Invoice : IDataEntity
{
    public string Id { get; set; } = Guid.NewGuid().ToString();

    public override bool Equals(object? obj)
    {
        if (ReferenceEquals(this, obj)) return true;
        if (obj == null || GetType() != obj.GetType())
            return false;
        var other = (Invoice)obj;
        return Id == other.Id;
    }

    public override int GetHashCode() => Id?.GetHashCode() ?? base.GetHashCode();
    public string ProjectId { get; set; } = string.Empty;
    public string? ProjectGroupId { get; set; }  // If set, this is a group invoice
    public string InvoiceNumber { get; set; } = string.Empty; // e.g. INV-1001

    public DateTime Date { get; set; } = DateTime.Today;
    public DateTime DueDate { get; set; } = DateTime.Today.AddDays(30);
    public string Status { get; set; } = "Draft"; // Draft, Final, Paid, Void
    public string PdfPath { get; set; } = string.Empty;

    // NEW: Template Override
    public string? TemplateId { get; set; }
    public bool HideCostAndMarkup { get; set; } = false;
    public string Note { get; set; } = string.Empty; // Description/Memo

    // The Line Items
    public List<InvoiceLine> Lines { get; set; } = new();

    // TRACKING: Which original source items does this invoice cover?
    // We use this to calculate "Unbilled" items later.
    public List<string> BilledTimeEntryIds { get; set; } = new();
    public List<string> BilledPoItemIds { get; set; } = new();
    public List<string> BilledExpenseItemIds { get; set; } = new();

    // Financials
    public decimal TaxRate { get; set; } = 0.14975m;
    public decimal SubTotal => Lines.Sum(l => l.Total);
    public decimal TotalCost => Lines.Sum(l => l.Quantity * l.OriginalUnitCost);
    public decimal GrossMargin => SubTotal - TotalCost;
    public decimal TaxAmount => SubTotal * TaxRate;
    public decimal GrandTotal => SubTotal + TaxAmount;

    // Payment Tracking
    [System.Text.Json.Serialization.JsonPropertyName("AmountPaid")]
    public decimal LegacyAmountPaid { get; set; }
    
    public List<PaymentRecord> Payments { get; set; } = new();
    
    [System.Text.Json.Serialization.JsonIgnore]
    public decimal AmountPaid => Payments.Any() ? Payments.Sum(p => p.Amount) : LegacyAmountPaid;
    
    public decimal BalanceDue => GrandTotal - AmountPaid;
    public DateTime? DatePaid { get; set; }
}

public class InvoiceLine
{
    public string Id { get; set; } = Guid.NewGuid().ToString();

    public override bool Equals(object? obj)
    {
        if (ReferenceEquals(this, obj)) return true;
        if (obj == null || GetType() != obj.GetType())
            return false;
        var other = (InvoiceLine)obj;
        return Id == other.Id;
    }

    public override int GetHashCode() => Id?.GetHashCode() ?? base.GetHashCode();
    public string Description { get; set; } = string.Empty;
    public string? WorkTypeId { get; set; }
    public string? SubTaskId { get; set; }
    public decimal Quantity { get; set; }
    public decimal UnitPrice { get; set; }
    public decimal Total => Quantity * UnitPrice;

    // NEW FIELDS FOR CREDIT LOGIC
    public bool IsPrepayment { get; set; } = false; // Adds to credit balance when paid
    public bool IsCreditUsed { get; set; } = false; // Reduces credit balance immediately

    public string Type { get; set; } = "General"; // General, Labor, Material

    // Tracking Source Item for Dynamic Unbilling
    public string? SourceId { get; set; }
    public string? SourceType { get; set; } // e.g. "PO", "Expense", "Time", "QuoteLabor", "QuoteItem", "Milestone"
    // Markup Transparency
    public decimal OriginalUnitCost { get; set; } = 0;
    public decimal MarkupPercentage { get; set; } = 0; // e.g. 0.15 for 15%
}



