namespace Spokes_Server.Core.Models.Accounting;

using Spokes_Server.Core.Data;

public class CommissionLedgerRecord : IDataEntity
{
    public string Id { get; set; } = Guid.NewGuid().ToString();

    public override bool Equals(object? obj)
    {
        if (ReferenceEquals(this, obj)) return true;
        if (obj == null || GetType() != obj.GetType())
            return false;
        var other = (CommissionLedgerRecord)obj;
        return Id == other.Id;
    }

    public override int GetHashCode() => Id?.GetHashCode() ?? base.GetHashCode();
    public string ProjectId { get; set; } = string.Empty;
    public string? InvoiceId { get; set; } // Nullable if generated on a non-invoice milestone
    public string BeneficiaryId { get; set; } = string.Empty; // Name or EmployeeId to identify the payee
    public string BeneficiaryName { get; set; } = string.Empty;
    public string Type { get; set; } = "Internal"; // Internal or External

    // Financials
    public decimal BaseAmount { get; set; } // The revenue or gross margin it was calculated on
    public decimal CommissionAmount { get; set; }

    // Status Flow
    public string Status { get; set; } = CommissionStatus.Payable; // "Pending", "Payable", "Paid", "Clawback"
    public DateTime DateGenerated { get; set; } = DateTime.UtcNow;
    public DateTime? DatePaid { get; set; }
    public string? PaymentReference { get; set; }
}

public static class CommissionStatus
{
    public const string Pending = "Pending";
    public const string Payable = "Payable";
    public const string Paid = "Paid";
    public const string Clawback = "Clawback";
}

public static class CommissionBasisTypes
{
    public const string Revenue = "Revenue";
    public const string Margin = "Margin";
    public const string ProjectRevenue = "Project Revenue";
    public const string ProjectMargin = "Project Margin";
}
