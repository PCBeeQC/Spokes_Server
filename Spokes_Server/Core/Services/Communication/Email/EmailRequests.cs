using System.Collections.Generic;
using Spokes_Server.Core.Models.Communication;

namespace Spokes_Server.Core.Services.Communication.Email;

public class SendEmailRequest
{
    public string EmployeeId { get; set; } = string.Empty;
    public string? AccountId { get; set; }
    public string? SendingIdentityId { get; set; }
    public required string To { get; set; }
    public required string Subject { get; set; }
    public required string Body { get; set; }
    public List<EmailAttachment>? Attachments { get; set; }
    public string? Cc { get; set; }
    public string? Bcc { get; set; }
    public List<EmailAttachmentMeta>? ExistingAttachments { get; set; }
    public string? DraftId { get; set; }
    public List<(string FilePath, string FileName)>? FileAttachmentPaths { get; set; }
    public List<OutboundDocumentAttachmentRef>? DocumentAttachments { get; set; }
    public List<ForwardedEmailAttachmentRef>? ForwardedEmailAttachments { get; set; }
    public string? InReplyTo { get; set; }
    public List<string>? References { get; set; }
}

public enum OutboundDocumentKind
{
    Quote,
    Invoice,
    ProjectDocument,
    BusinessDocument,
    ProjectReport,
    PurchaseOrder
}

public sealed record OutboundDocumentAttachmentRef(
    OutboundDocumentKind Kind,
    string DocumentId,
    string? DisplayFileName = null);

public sealed record ForwardedEmailAttachmentRef(
    string SourceAccountId,
    string SourceMessageId,
    string AttachmentId,
    string? DisplayFileName = null);

public class SaveDraftRequest
{
    public string AccountId { get; set; } = string.Empty;
    public string EmployeeId { get; set; } = string.Empty;
    public string? SendingIdentityId { get; set; }
    public string? To { get; set; }
    public string? Subject { get; set; }
    public string? Body { get; set; }
    public List<EmailAttachment>? Attachments { get; set; }
    public string? Cc { get; set; }
    public string? Bcc { get; set; }
    public string? ExistingDraftId { get; set; }
    public string? InReplyTo { get; set; }
    public List<string>? References { get; set; }
}
