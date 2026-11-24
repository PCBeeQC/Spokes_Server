namespace Spokes_Server.Core.Models.Accounting;

using System;
using Spokes_Server.Core.Data;

public static class SuggestedInvoicePaymentStatus
{
    public const string Pending = "Pending";
    public const string Applied = "Applied";
    public const string Dismissed = "Dismissed";
}

/// <summary>
/// Persisted customer payment advice detected in the Company Bill Inbox that matches
/// an existing Draft or Open AR Invoice. Requires explicit human confirmation before
/// finalizing or recording payment on the invoice.
/// </summary>
public class SuggestedInvoicePayment : IDataEntity
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string InvoiceId { get; set; } = string.Empty;
    public string InvoiceNumber { get; set; } = string.Empty;
    public string ProjectId { get; set; } = string.Empty;
    public string ClientName { get; set; } = string.Empty;
    public decimal InvoiceTotal { get; set; }
    public decimal InvoiceBalanceDue { get; set; }
    public string InvoiceStatus { get; set; } = string.Empty;

    public decimal SuggestedAmount { get; set; }
    public DateTime PaymentDate { get; set; } = DateTime.Today;
    public string SourceEmailId { get; set; } = string.Empty;
    public string SourceEmailSubject { get; set; } = string.Empty;
    public string SourceSenderEmail { get; set; } = string.Empty;
    public string? AttachmentPath { get; set; }
    public string? AttachmentName { get; set; }
    public string MatchReason { get; set; } = string.Empty;

    public string Status { get; set; } = SuggestedInvoicePaymentStatus.Pending;
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? ResolvedAtUtc { get; set; }
    public string? ResolvedBy { get; set; }
}
