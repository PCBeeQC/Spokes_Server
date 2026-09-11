namespace Spokes_Server.Core.Models.Accounting;

using Spokes_Server.Core.Models.Core;
using Spokes_Server.Core.Models.Projects;
using Spokes_Server.Core.Models.Communication;
using Spokes_Server.Core.Models.HR;

using Spokes_Server.Core.Data;




public class RecurringExpense : IDataEntity
{
    public string Id { get; set; } = Guid.NewGuid().ToString();

    public override bool Equals(object? obj)
    {
        if (ReferenceEquals(this, obj)) return true;
        if (obj == null || GetType() != obj.GetType())
            return false;
        var other = (RecurringExpense)obj;
        return Id == other.Id;
    }

    public override int GetHashCode() => Id?.GetHashCode() ?? base.GetHashCode();
    public bool IsEstimate { get; set; } = true; // True for Forward Planning, False for Actual Accounting Bills
    public string Name { get; set; } = string.Empty; // e.g. "Rent", "Internet"
    public string Supplier { get; set; } = string.Empty; // Vendor/Supplier name
    public string Description { get; set; } = string.Empty; // Additional notes

    public decimal Amount { get; set; } = 0; // Default amount
    public string Frequency { get; set; } = RecurringExpenseFrequency.Monthly;

    public DateTime CreatedDate { get; set; } = DateTime.Today; // When this recurring expense was set up
    public DateTime NextDueDate { get; set; } = DateTime.Today;

    // History of Bills/Payments
    public List<RecurringBillRecord> History { get; set; } = new();
}

public class RecurringBillRecord
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public DateTime Date { get; set; } = DateTime.Today; // The date received / billing date
    public DateTime? DueDate { get; set; } // The payment due date
    public decimal Amount { get; set; } = 0;

    // Attachment
    public string AttachmentPath { get; set; } = string.Empty;
    public string AttachmentName { get; set; } = string.Empty;

    public bool IsPaid { get; set; } = false;
    public DateTime? DatePaid { get; set; }

    public List<PaymentRecord> Payments { get; set; } = new();
    
    public decimal AmountPaid => Payments.Any() ? Payments.Sum(p => p.Amount) : (IsPaid ? Amount : 0);
    public decimal BalanceDue => Amount - AmountPaid;
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



