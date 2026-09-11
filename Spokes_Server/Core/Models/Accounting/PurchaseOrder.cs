namespace Spokes_Server.Core.Models.Accounting;

using Spokes_Server.Core.Models.Core;
using Spokes_Server.Core.Models.Projects;
using Spokes_Server.Core.Models.Communication;
using Spokes_Server.Core.Models.HR;

using Spokes_Server.Core.Data;




public class PurchaseOrder : IDataEntity
{
    public string Id { get; set; } = Guid.NewGuid().ToString();

    public override bool Equals(object? obj)
    {
        if (ReferenceEquals(this, obj)) return true;
        if (obj == null || GetType() != obj.GetType())
            return false;
        var other = (PurchaseOrder)obj;
        return Id == other.Id;
    }

    public override int GetHashCode() => Id?.GetHashCode() ?? base.GetHashCode();
    public string PoNumber { get; set; } = string.Empty; // e.g. PO2512-001

    // Header Info
    public string Status { get; set; } = "Draft"; // Draft, Final, Partially Billed, Fully Billed, Closed
    public string PdfPath { get; set; } = string.Empty;
    public DateTime Date { get; set; } = DateTime.Today;
    public string IssuedBy { get; set; } = string.Empty; // Name of employee
    public string SupplierQuoteRef { get; set; } = string.Empty;
    public string SupplierRepName { get; set; } = string.Empty;

    // Re-using ClientInfo for Supplier details (Address, etc)
    public ClientInfo Supplier { get; set; } = new ClientInfo();

    // Optional Custom Recipient details (if null, use default SpokesSettings main company)
    public ClientInfo? Recipient { get; set; }

    // Financials
    public bool ApplyTax { get; set; } = true;
    public decimal TaxRate { get; set; } = 0.14975m; // Default Quebec Tax (adjust as needed)

    // The Content
    public List<PoItem> Items { get; set; } = new();

    // Custom Columns Definitions (e.g. ["Color", "Size"])
    // We store the HEADERS here so we know what keys to look for in the items
    public List<string> CustomHeaders { get; set; } = new();

    // NEW: Template Override
    public string? TemplateId { get; set; }

    // Notes and Currency
    public string Note { get; set; } = string.Empty;
    public string Currency { get; set; } = string.Empty;

    // Totals
    public decimal SubTotal => Items.Sum(i => i.Total);
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
        if (obj == null || GetType() != obj.GetType())
            return false;
        var other = (PoItem)obj;
        return Id == other.Id;
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
    public Dictionary<string, string> CustomData { get; set; } = new();
}



