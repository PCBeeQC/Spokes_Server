namespace Spokes_Server.Core.Models.Projects;

using Spokes_Server.Core.Models.Core;
using Spokes_Server.Core.Models.Accounting;
using Spokes_Server.Core.Models.Communication;
using Spokes_Server.Core.Models.HR;

using Spokes_Server.Core.Data;




public class ProjectNote : IDataEntity
{
    public string Id { get; set; } = Guid.NewGuid().ToString();

    public override bool Equals(object? obj)
    {
        if (ReferenceEquals(this, obj)) return true;
        if (obj == null || GetType() != obj.GetType())
            return false;
        var other = (ProjectNote)obj;
        return Id == other.Id;
    }

    public override int GetHashCode() => Id?.GetHashCode() ?? base.GetHashCode();
    public string ProjectId { get; set; } = string.Empty;
    public DateTime Date { get; set; } = DateTime.Today;
    public string Title { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty; // Supports Markdown
    public bool IsEmail { get; set; } = false; // If true, render as read-only HTML via EmailContentRenderer
    public string? EmailHtmlPath { get; set; } // Relative URL to the project-local .eml copy
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public string CreatedBy { get; set; } = string.Empty; // User Name or ID

    // System Event Tracking
    public string SystemReferenceId { get; set; } = string.Empty;
    public bool IsSystemEvent { get; set; } = false;

    // Attachments
    public List<NoteAttachment> Attachments { get; set; } = new();
}

public class NoteAttachment
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string FileName { get; set; } = string.Empty;
    public string FilePath { get; set; } = string.Empty; // Relative or absolute path
    public string ContentType { get; set; } = string.Empty;
}



