namespace Spokes_Server.Core.Models.Accounting;

using System.Collections.Generic;
using System.Linq;
using Spokes_Server.Core.Data;

public class RecurringExpense : IDataEntity, IHasAssociatedFiles
{
    public string Id { get; set; } = Guid.NewGuid().ToString();

    public override bool Equals(object? obj)
    {
        if (ReferenceEquals(this, obj)) return true;
        return obj is RecurringExpense other && Id == other.Id;
    }

    public override int GetHashCode() => Id?.GetHashCode() ?? base.GetHashCode();
    public bool IsEstimate { get; set; } = true; // True for Forward Planning, False for Actual Accounting Bills
    public string Name { get; set; } = string.Empty; // e.g. "Rent", "Internet"
    public string Supplier { get; set; } = string.Empty; // Vendor/Supplier name
    public string? SupplierId { get; set; }
    public string Description { get; set; } = string.Empty; // Additional notes

    public decimal Amount { get; set; } = 0; // Default amount
    public bool IsTaxExempt { get; set; }
    public string? GlAccountCode { get; set; }
    public string? SageItemCode { get; set; }
    public string Frequency { get; set; } = RecurringExpenseFrequency.Monthly;

    public DateTime CreatedDate { get; set; } = DateTime.Today; // When this recurring expense was set up
    public DateTime NextDueDate { get; set; } = DateTime.Today;

    // History of Bills/Payments
    public List<RecurringBillRecord> History { get; set; } = [];

    public IEnumerable<string> GetAssociatedFilePaths()
    {
        return History?.Select(h => h.AttachmentPath).Where(p => !string.IsNullOrWhiteSpace(p)) ?? Enumerable.Empty<string>();
    }
}

public class RecurringBillRecord : IHasAssociatedFiles
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string? InvoiceNumber { get; set; }
    public DateTime Date { get; set; } = DateTime.Today; // The date received / billing date
    public DateTime? DueDate { get; set; } // The payment due date
    public decimal Amount { get; set; } = 0;
    public decimal SubTotal { get; set; } = 0;
    public decimal TaxAmount { get; set; } = 0;
    public decimal TpsAmount { get; set; } = 0;
    public decimal TvqAmount { get; set; } = 0;
    public bool? IsTaxExempt { get; set; }
    public string? GlAccountCode { get; set; }
    public string? SageItemCode { get; set; }

    // Accounting / Sage Export Tracking
    public bool IsExported => ExportedToSageAt.HasValue;
    public DateTime? ExportedToSageAt { get; set; }
    public string? SageBatchId { get; set; }

    // Attachment
    public string AttachmentPath { get; set; } = string.Empty;
    public string AttachmentName { get; set; } = string.Empty;
    public string? SourceEmailId { get; set; }

    public bool IsPaid { get; set; }
    public DateTime? DatePaid { get; set; }

    public List<PaymentRecord> Payments { get; set; } = [];
    
    public decimal AmountPaid => Payments.Any() ? Payments.Sum(p => p.Amount) : (IsPaid ? Amount : 0);
    public decimal BalanceDue => Amount - AmountPaid;

    public IEnumerable<string> GetAssociatedFilePaths()
    {
        if (!string.IsNullOrWhiteSpace(AttachmentPath)) yield return AttachmentPath;
    }
}

public static class RecurringExpenseFrequency
{
    public const string Weekly = "Weekly";
    public const string BiWeekly = "Bi-Weekly";
    public const string Monthly = "Monthly";
    public const string Yearly = "Yearly";
    public const string OneTime = "One-Time";
    public const string AsNeeded = "As Needed";
}
