namespace Spokes_Server.Core.Models.Accounting;

using System;
using System.Collections.Generic;
using System.Linq;
using Spokes_Server.Core.Data;
using Spokes_Server.Core.Models.Common;
using Spokes_Server.Core.Services.Accounting;

public class Bill : IDataEntity, IHasAssociatedFiles, IHasDocumentHistory
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
    public List<DocumentTaxLine> TaxComponents { get; set; } = [];
    public bool IsTaxExempt { get; set; }
    public string Currency { get; set; } = "CAD";
    public decimal ExchangeRate { get; set; } = 1.0m;
    public bool IsCreditNote { get; set; }
    public string? LinkedRecurringExpenseId { get; set; }
    public bool IsBusinessOverhead { get; set; }

    /// <summary>True when imported as a pre-cutover open payable during accounting migration.</summary>
    public bool IsOpeningBalance { get; set; }
    /// <summary>Links this opening-balance bill to its AccountingMigrationBatch for rollback.</summary>
    public string? MigrationBatchId { get; set; }

    // Origin & Ingestion Tracking
    public string? SourceEmailId { get; set; }
    public string? SourceEmailSubject { get; set; }
    public string? SourceSenderEmail { get; set; }
    public bool AutoIngested { get; set; }
    public string? AutoMatchReason { get; set; }

    // Chart of Accounts Mapping
    public string? GlAccountCode { get; set; }

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

    // Payment & Write-Off Info (optional tracking)
    public string PaymentReference { get; set; } = string.Empty; // Check #, Wire Ref
    public List<PaymentRecord> Payments { get; set; } = [];
    public List<BalanceWriteOff> WriteOffs { get; set; } = [];

    [System.Text.Json.Serialization.JsonIgnore]
    public decimal AmountWrittenOff => WriteOffs.Sum(w => w.Amount);

    [System.Text.Json.Serialization.JsonIgnore]
    public bool HasWriteOffs => WriteOffs.Count > 0;

    public decimal AmountPaid => Payments.Any()
        ? Payments.Sum(p => p.Amount)
        : ((DatePaid.HasValue || Status == BillStatus.Paid) && !HasWriteOffs && Status != BillStatus.WrittenOff ? Amount : 0);

    public decimal BalanceDue => Status == BillStatus.Void
        ? 0m
        : Math.Round(Amount - AmountPaid - AmountWrittenOff, 2, MidpointRounding.AwayFromZero);

    // 3-Way Matching
    public List<BillLineItem> LineItems { get; set; } = [];

    // Audit History
    public List<DocumentHistoryEntry> History { get; set; } = [];

    [System.Text.Json.Serialization.JsonIgnore]
    public Bill? LastSavedSnapshot { get; set; }

    public void CaptureAuditSnapshot()
    {
        LastSavedSnapshot = new Bill
        {
            Id = Id,
            SupplierName = SupplierName,
            InvoiceNumber = InvoiceNumber,
            Date = Date,
            DueDate = DueDate,
            Amount = Amount,
            SubTotal = SubTotal,
            TaxAmount = TaxAmount,
            GlAccountCode = GlAccountCode,
            Status = Status
        };
    }

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
    public string? GlAccountCode { get; set; }
    public decimal Total => FinancialCalculationEngine.RoundCurrency(Quantity * UnitPrice);
}
