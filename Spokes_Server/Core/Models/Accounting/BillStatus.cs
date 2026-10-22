namespace Spokes_Server.Core.Models.Accounting;

/// <summary>
/// Authoritative domain lifecycle statuses for Bills.
/// </summary>
public static class BillStatus
{
    public const string AwaitingApproval = "Awaiting Approval";
    public const string Approved = "Approved";
    public const string Unpaid = "Unpaid";
    public const string Paid = "Paid";
    public const string Void = "Void";
}
