using System.Collections.Generic;
using Spokes_Server.Core.Models.Communication;

namespace Spokes_Server.Core.Services.Communication.Email;

public class SendEmailRequest
{
    public required string EmployeeId { get; set; }
    public required string To { get; set; }
    public required string Subject { get; set; }
    public required string Body { get; set; }
    public List<EmailAttachment>? Attachments { get; set; }
    public string? Cc { get; set; }
    public string? Bcc { get; set; }
    public List<EmailAttachmentMeta>? ExistingAttachments { get; set; }
    public string? DraftId { get; set; }
    public List<(string FilePath, string FileName)>? FileAttachmentPaths { get; set; }
}

public class SaveDraftRequest
{
    public required string EmployeeId { get; set; }
    public string? To { get; set; }
    public string? Subject { get; set; }
    public string? Body { get; set; }
    public List<EmailAttachment>? Attachments { get; set; }
    public string? Cc { get; set; }
    public string? Bcc { get; set; }
    public string? ExistingDraftId { get; set; }
}
