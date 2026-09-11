namespace Spokes_Server.Core.Models.Accounting;

using Spokes_Server.Core.Models.Core;
using Spokes_Server.Core.Models.Projects;
using Spokes_Server.Core.Models.Communication;
using Spokes_Server.Core.Models.HR;

using Spokes_Server.Core.Data;




public class Bill : IDataEntity
{
    public string Id { get; set; } = Guid.NewGuid().ToString();

    public override bool Equals(object? obj)
    {
        if (ReferenceEquals(this, obj)) return true;
        if (obj == null || GetType() != obj.GetType())
            return false;
        var other = (Bill)obj;
        return Id == other.Id;
    }

    public override int GetHashCode() => Id?.GetHashCode() ?? base.GetHashCode();

    // The Bill Details
    public string SupplierId { get; set; } = string.Empty; // Useful if linked to a known supplier
    public string SupplierName { get; set; } = string.Empty; // Fallback if no ID or for quick display

    public string Description { get; set; } = string.Empty; // e.g. "Rent for Jan", "PO-10022 Invoice"

    public DateTime Date { get; set; } = DateTime.Today; // Invoice Date
    public DateTime DueDate { get; set; } = DateTime.Today.AddDays(30);

    public decimal Amount { get; set; } = 0;

    // Status
    public string Status { get; set; } = "Awaiting Approval"; // Awaiting Approval, Approved, Unpaid, Paid, Void
    public DateTime? DatePaid { get; set; }

    // Links
    public string? PurchaseOrderId { get; set; } // Optional: Linked PO

    // Attachments
    public string AttachmentPath { get; set; } = string.Empty; // Path to saved PDF/Image relative to storage
    public string AttachmentName { get; set; } = string.Empty; // Original filename

    // Payment Info (optional tracking)
    public string PaymentReference { get; set; } = string.Empty; // Check #, Wire Ref
    public List<PaymentRecord> Payments { get; set; } = new();

    public decimal AmountPaid => Payments.Any() ? Payments.Sum(p => p.Amount) : (DatePaid.HasValue || Status == "Paid" ? Amount : 0);
    public decimal BalanceDue => Amount - AmountPaid;

    // 3-Way Matching
    public List<BillLineItem> LineItems { get; set; } = new();
}

public class BillLineItem
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string PoItemId { get; set; } = string.Empty; // Links to the PurchaseOrder -> PoItem
    public string Description { get; set; } = string.Empty;
    public decimal Quantity { get; set; } = 1;
    public decimal UnitPrice { get; set; } = 0;
    public decimal Total => Quantity * UnitPrice;
}
