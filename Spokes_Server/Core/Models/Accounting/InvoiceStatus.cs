namespace Spokes_Server.Core.Models.Accounting;

/// <summary>
/// Authoritative domain lifecycle statuses for Invoices.
/// </summary>
public static class InvoiceStatus
{
    public const string Draft = "Draft";
    public const string Final = "Final";
    public const string Paid = "Paid";
    public const string Partial = "Partial";
    public const string Sent = "Sent";
    public const string Void = "Void";
}
