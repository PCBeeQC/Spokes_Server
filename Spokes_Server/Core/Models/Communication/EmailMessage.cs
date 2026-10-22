namespace Spokes_Server.Core.Models.Communication;

using Spokes_Server.Core.Data;

public class EmailMessage : IDataEntity
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string EmployeeId { get; set; } = string.Empty;
    public string FolderPath { get; set; } = string.Empty; // Links to EmailFolder.Path

    // IMAP Metadata
    public uint UniqueId { get; set; } // IMAP Uid
    public string GlobalMessageId { get; set; } = string.Empty; // IMAP Envelope Message-Id

    // Header Info
    public string Subject { get; set; } = string.Empty;
    public string FromAddress { get; set; } = string.Empty;
    public string FromName { get; set; } = string.Empty;

    public List<string> ToAddresses { get; set; } = [];
    public List<string> CcAddresses { get; set; } = [];
    public List<string> BccAddresses { get; set; } = [];

    public DateTimeOffset Date { get; set; }

    // Preview
    public string Snippet { get; set; } = string.Empty; // First 100 chars of body text

    // Content Storage (Body is stored in a separate file: Data/Emails/{Id}.html)
    public bool HasAttachments { get; set; }
    public List<EmailAttachmentMeta> Attachments { get; set; } = [];

    // State
    public bool IsRead { get; set; }
    public bool IsFlagged { get; set; }
    private bool _isLocalOnly;
    public bool IsLocalOnly
    {
        get => _isLocalOnly || UniqueId >= (uint.MaxValue - 1000000);
        set => _isLocalOnly = value;
    }

    // Company Bill Inbox Workflow Tracking
    public string? BillingStatus { get; set; } = Spokes_Server.Core.Constants.CompanyBillInboxConstants.BillingStatus.Unprocessed;
    public string? LinkedBillId { get; set; }  // Links to primary Bill.Id
    public List<string> LinkedBillIds { get; set; } = []; // Links to all Bills created from this email
    public string? LinkedPoId { get; set; }    // Links to PurchaseOrder.Id
    public string? MatchedAttachmentPath { get; set; }
    public string? MatchedAttachmentName { get; set; }
    public decimal? DetectedAmount { get; set; }
    public decimal? DetectedPaidAmount { get; set; }
    public decimal? DetectedBalanceDue { get; set; }
    public string? DetectedCurrency { get; set; }
    public string? ProcessedByUserId { get; set; }
    public DateTime? ProcessedAt { get; set; }
    public string? LinkedRecurringExpenseId { get; set; }
    public string? LinkedRecurringRecordId { get; set; }
    public Spokes_Server.Core.Models.Accounting.BillInboxDecisionLog? DecisionLog { get; set; }
}
