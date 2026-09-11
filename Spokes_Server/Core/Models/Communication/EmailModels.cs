namespace Spokes_Server.Core.Models.Communication;

using Spokes_Server.Core.Models.Core;
using Spokes_Server.Core.Models.Projects;
using Spokes_Server.Core.Models.Accounting;
using Spokes_Server.Core.Models.HR;

using Spokes_Server.Core.Data;




public class EmailFolder : IDataEntity
{
    public string Id { get; set; } = Guid.NewGuid().ToString();

    public override bool Equals(object? obj)
    {
        if (ReferenceEquals(this, obj)) return true;
        if (obj == null || GetType() != obj.GetType())
            return false;
        var other = (EmailFolder)obj;
        return Id == other.Id;
    }

    public override int GetHashCode() => Id?.GetHashCode() ?? base.GetHashCode();
    public string EmployeeId { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty; // Display Name (e.g. "Inbox")
    public string Path { get; set; } = string.Empty; // Full IMAP Path (e.g. "INBOX" or "Archive/2023")
    public string ParentPath { get; set; } = string.Empty; // Full path of the parent folder, empty if root
    public string Delimiter { get; set; } = "/";
    public int UnreadCount { get; set; } = 0;
    public int TotalCount { get; set; } = 0;

    // For local folders (like Drafts) where we generate UIDs manually
    public uint NextUid { get; set; } = 1;

    // Standard folders identification
    public bool IsInbox { get; set; } = false;
    public bool IsSent { get; set; } = false;
    public bool IsTrash { get; set; } = false;
    public bool IsDrafts { get; set; } = false;
    public bool IsArchive { get; set; } = false;
    public bool IsJunk { get; set; } = false;
}

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

    public List<string> ToAddresses { get; set; } = new();
    public List<string> CcAddresses { get; set; } = new();
    public List<string> BccAddresses { get; set; } = new();

    public DateTimeOffset Date { get; set; }

    // Preview
    public string Snippet { get; set; } = string.Empty; // First 100 chars of body text

    // Content Storage (Body is stored in a separate file: Data/Emails/{Id}.html)
    public bool HasAttachments { get; set; } = false;
    public List<EmailAttachmentMeta> Attachments { get; set; } = new();

    // State
    public bool IsRead { get; set; } = false;
    public bool IsFlagged { get; set; } = false;
}

public class EmailAttachmentMeta
{
    public string FileName { get; set; } = string.Empty;
    public string ContentType { get; set; } = string.Empty;
    public long Size { get; set; }
    public string Id { get; set; } = Guid.NewGuid().ToString(); // Used to retrieve file from Data/Attachments/Email/{MessageId}/{AttachmentId}
}



