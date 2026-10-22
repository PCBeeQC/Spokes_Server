namespace Spokes_Server.Core.Models.Accounting;

/// <summary>
/// Method or criteria used to match an incoming inbox email with an existing bill in Spokes.
/// </summary>
public enum ExistingBillMatchMethod
{
    ExactInvoiceNumber,
    NormalizedInvoiceNumber,
    DescriptionInvoiceNumber,
    PurchaseOrderAndAmount,
    SupplierAndExactAmount,
    SubjectInvoiceNumber,
    SnippetInvoiceNumber,
    SupplierAndTextAmount,
    SupplierAndAmountPossibleDuplicate
}

/// <summary>
/// Detailed result when an incoming billing email or extracted document matches a pre-existing bill.
/// </summary>
public class ExistingBillMatch
{
    public Bill Bill { get; set; } = default!;
    public string MatchReason { get; set; } = string.Empty;
    public ExistingBillMatchMethod Method { get; set; }
    public decimal AmountDifference { get; set; }
    public bool HasConflictingAmount => Math.Abs(AmountDifference) > 0.05m;
    public bool ExistingBillHasAttachment => !string.IsNullOrEmpty(Bill.AttachmentPath);
    public bool IsPossibleDuplicate => Method is ExistingBillMatchMethod.SupplierAndAmountPossibleDuplicate or ExistingBillMatchMethod.SupplierAndTextAmount;
}
