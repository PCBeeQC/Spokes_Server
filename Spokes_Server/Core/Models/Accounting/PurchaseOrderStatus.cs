namespace Spokes_Server.Core.Models.Accounting;

/// <summary>
/// Authoritative domain lifecycle statuses for Purchase Orders.
/// </summary>
public static class PurchaseOrderStatus
{
    public const string Draft = "Draft";
    public const string Final = "Final";
    public const string PartiallyBilled = "Partially Billed";
    public const string FullyBilled = "Fully Billed";
    public const string Closed = "Closed";
    public const string Void = "Void";
}
