namespace Spokes_Server.Core.Models.Accounting;

using System.Text.Json.Serialization;
using Spokes_Server.Core.Models.Core;
using Spokes_Server.Core.Data;

public class PurchaseOrder : IDataEntity
{
    public string Id { get; set; } = Guid.NewGuid().ToString();

    public override bool Equals(object? obj)
    {
        if (ReferenceEquals(this, obj)) return true;
        return obj is PurchaseOrder other && Id == other.Id;
    }

    public override int GetHashCode() => Id?.GetHashCode() ?? base.GetHashCode();
    public string PoNumber { get; set; } = string.Empty; // e.g. PO2512-001

    // Header Info
    public string Status { get; set; } = PurchaseOrderStatus.Draft; // Draft, Final, Partially Billed, Fully Billed, Closed
    public string PdfPath { get; set; } = string.Empty;
    public DateTime Date { get; set; } = DateTime.Today;
    public string IssuedBy { get; set; } = string.Empty; // Name of employee
    public string SupplierQuoteRef { get; set; } = string.Empty;
    public string SupplierRepName { get; set; } = string.Empty;

    // Party contact details for Supplier (Address, etc)
    public PartyContactInfo Supplier { get; set; } = new();

    // Optional Custom Recipient details (if null, use default SpokesSettings main company)
    public PartyContactInfo? Recipient { get; set; }

    // Financials
    public bool ApplyTax { get; set; } = true;
    public decimal TaxRate { get; set; } = 0.14975m; // Default Quebec Tax (adjust as needed)

    // The Content
    public List<PoItem> LineItems { get; set; } = [];

    // Backward compatibility for legacy JSON files and existing call sites
    [JsonPropertyName("Items")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<PoItem>? LegacyItems
    {
        get => null;
        set { if (value != null && LineItems.Count == 0) LineItems = value; }
    }

    [Obsolete("Prefer LineItems for consistency across accounting models.")]
    [JsonIgnore]
    public List<PoItem> Items
    {
        get => LineItems;
        set => LineItems = value;
    }

    // Custom Columns Definitions (e.g. ["Color", "Size"])
    // We store the HEADERS here so we know what keys to look for in the items
    public List<string> CustomHeaders { get; set; } = [];

    // NEW: Template Override
    public string? TemplateId { get; set; }

    // Notes and Currency
    public string Note { get; set; } = string.Empty;
    public string Currency { get; set; } = string.Empty;

    // Totals
    public decimal SubTotal => LineItems.Sum(i => i.Total);
    public decimal TaxAmount => ApplyTax ? SubTotal * TaxRate : 0;
    public decimal GrandTotal => SubTotal + TaxAmount;
    
    // Billing
    public decimal TotalBilled { get; set; } = 0; // Updated when bills are saved
    public decimal RemainingBalance => GrandTotal - TotalBilled;
}

public class PoItem
{
    public string Id { get; set; } = Guid.NewGuid().ToString();

    public override bool Equals(object? obj)
    {
        if (ReferenceEquals(this, obj)) return true;
        return obj is PoItem other && Id == other.Id;
    }

    public override int GetHashCode() => Id?.GetHashCode() ?? base.GetHashCode();

    // The Link to the Project
    public string ProjectId { get; set; } = string.Empty;

    public string PartNumber { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;

    public decimal Quantity { get; set; } = 1;
    public decimal UnitPrice { get; set; } = 0;

    public decimal Total => Quantity * UnitPrice;

    // Custom Data (Key = Header Name, Value = Cell Content)
    public Dictionary<string, string> CustomData { get; set; } = [];
}
