namespace Spokes_Server.Core.Models.Communication;

using Spokes_Server.Core.Data;

public class ReportedMessage : IDataEntity
{
    public string Id { get; set; } = Guid.NewGuid().ToString();

    public override bool Equals(object? obj)
    {
        if (ReferenceEquals(this, obj)) return true;
        if (obj == null || GetType() != obj.GetType())
            return false;
        var other = (ReportedMessage)obj;
        return Id == other.Id;
    }

    public override int GetHashCode() => Id?.GetHashCode() ?? base.GetHashCode();

    /// <summary>
    /// ID of the reported ChatMessage.
    /// </summary>
    public string MessageId { get; set; } = string.Empty;

    /// <summary>
    /// Employee ID of the user who submitted the report.
    /// </summary>
    public string ReporterId { get; set; } = string.Empty;

    /// <summary>
    /// Employee ID of the user whose message was reported.
    /// </summary>
    public string ReportedUserId { get; set; } = string.Empty;

    /// <summary>
    /// The reason selected by the reporter.
    /// </summary>
    public string Reason { get; set; } = string.Empty;

    /// <summary>
    /// When the report was submitted.
    /// </summary>
    public DateTime ReportedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Status of the report: "Open", "Resolved", "Dismissed"
    /// </summary>
    public string Status { get; set; } = "Open";

    /// <summary>
    /// Snapshot of the message content at the time of reporting. 
    /// Crucial for E2E encrypted channels to allow moderators to view the offending message.
    /// </summary>
    public string SnapshotContent { get; set; } = string.Empty;

    /// <summary>
    /// Snapshot of attachments at the time of reporting.
    /// </summary>
    public List<ChatAttachment> Attachments { get; set; } = new();

    /// <summary>
    /// Action taken when the report was resolved (e.g., "Dismissed", "Deleted Message").
    /// </summary>
    public string ResolutionAction { get; set; } = string.Empty;

    /// <summary>
    /// The name of the moderator who resolved this report.
    /// </summary>
    public string ResolvedBy { get; set; } = string.Empty;
}
