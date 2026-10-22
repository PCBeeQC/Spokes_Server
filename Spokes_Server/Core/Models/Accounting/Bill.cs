namespace Spokes_Server.Core.Models.Accounting;

using System.Collections.Generic;
using Spokes_Server.Core.Data;

public class Bill : IDataEntity, IHasAssociatedFiles
{
    public string Id { get; set; } = Guid.NewGuid().ToString();

    public override bool Equals(object? obj)
    {
        if (ReferenceEquals(this, obj)) return true;
        return obj is Bill other && Id == other.Id;
    }

    public override int GetHashCode() => Id?.GetHashCode() ?? base.GetHashCode();

    // The Bill Details
    public string SupplierId { get; set; } = string.Empty; // Useful if linked to a known supplier
    public string SupplierName { get; set; } = string.Empty; // Fallback if no ID or for quick display

    public string Description { get; set; } = string.Empty; // e.g. "Rent for Jan", "PO-10022 Invoice"
    public string? InvoiceNumber { get; set; } // Supplier invoice number

    public DateTime Date { get; set; } = DateTime.Today; // Invoice Date
    public DateTime DueDate { get; set; } = DateTime.Today.AddDays(30);

    public decimal Amount { get; set; } = 0; // Total
    public decimal SubTotal { get; set; } = 0;
    public decimal TaxAmount { get; set; } = 0;
    public decimal TpsAmount { get; set; } = 0;
    public decimal TvqAmount { get; set; } = 0;
    public bool IsTaxExempt { get; set; }
    public string Currency { get; set; } = "CAD";
    public bool IsCreditNote { get; set; }
    public string? LinkedRecurringExpenseId { get; set; }

    // Origin & Ingestion Tracking
    public string? SourceEmailId { get; set; }
    public string? SourceEmailSubject { get; set; }
    public string? SourceSenderEmail { get; set; }
    public bool AutoIngested { get; set; }
    public string? AutoMatchReason { get; set; }

    // Accounting / Sage Export Tracking
    public bool IsExported => ExportedToSageAt.HasValue;
    public DateTime? ExportedToSageAt { get; set; }
    public string? SageBatchId { get; set; }
    public string? GlAccountCode { get; set; }
    public string? SageItemCode { get; set; }

    // Status
    public string Status { get; set; } = BillStatus.AwaitingApproval; // Awaiting Approval, Approved, Unpaid, Paid, Void
    public DateTime? DatePaid { get; set; }

    // Void Tracking
    public DateTime? VoidedAt { get; set; }
    public string? VoidedBy { get; set; }
    public string? VoidReason { get; set; }
    public bool IsVoid => Status == BillStatus.Void;

    // Links
    public string? PurchaseOrderId { get; set; } // Optional: Linked PO

    // Attachments
    public string AttachmentPath { get; set; } = string.Empty; // Path to saved PDF/Image relative to storage
    public string AttachmentName { get; set; } = string.Empty; // Original filename

    // Payment Info (optional tracking)
    public string PaymentReference { get; set; } = string.Empty; // Check #, Wire Ref
    public List<PaymentRecord> Payments { get; set; } = [];

    public decimal AmountPaid => Payments.Any() ? Payments.Sum(p => p.Amount) : (DatePaid.HasValue || Status == BillStatus.Paid ? Amount : 0);
    public decimal BalanceDue => Status == BillStatus.Void ? 0m : Amount - AmountPaid;

    // 3-Way Matching
    public List<BillLineItem> LineItems { get; set; } = [];

    public IEnumerable<string> GetAssociatedFilePaths()
    {
        if (!string.IsNullOrWhiteSpace(AttachmentPath)) yield return AttachmentPath;
    }
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
