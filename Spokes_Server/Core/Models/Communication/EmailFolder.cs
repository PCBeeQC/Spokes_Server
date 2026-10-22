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
