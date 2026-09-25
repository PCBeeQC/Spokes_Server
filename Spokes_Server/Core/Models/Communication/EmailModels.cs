namespace Spokes_Server.Core.Models.Communication;

using Spokes_Server.Core.Data;

public class EmailFolder : IDataEntity
{
    public string Id { get; set; } = Guid.NewGuid().ToString();

    public override bool Equals(object? obj)
    {
        if (ReferenceEquals(this, obj)) return true;
        return obj is EmailFolder other && Id == other.Id;
    }

    public override int GetHashCode() => Id?.GetHashCode() ?? base.GetHashCode();
    public string EmployeeId { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty; // Display Name (e.g. "Inbox")
    public string Path { get; set; } = string.Empty; // Full IMAP Path (e.g. "INBOX" or "Archive/2023")
    public string ParentPath { get; set; } = string.Empty; // Full path of the parent folder, empty if root
    public string Delimiter { get; set; } = "/";
    public int UnreadCount { get; set; }
    public int TotalCount { get; set; }

    // For local folders (like Drafts) where we generate UIDs manually
    public uint NextUid { get; set; } = 1;

    // Standard folders identification
    public bool IsInbox { get; set; }
    public bool IsSent { get; set; }
    public bool IsTrash { get; set; }
    public bool IsDrafts { get; set; }
    public bool IsArchive { get; set; }
    public bool IsJunk { get; set; }
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
}

public class EmailAttachmentMeta
{
    public string FileName { get; set; } = string.Empty;
    public string ContentType { get; set; } = string.Empty;
    public long Size { get; set; }
    public string Id { get; set; } = Guid.NewGuid().ToString(); // Used to retrieve file from Data/Attachments/Email/{MessageId}/{AttachmentId}
}
