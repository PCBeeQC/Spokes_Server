using System;
using System.Collections.Generic;
using System.Linq;
using Spokes_Server.Core.Data;
using Spokes_Server.Core.Models.Common;

namespace Spokes_Server.Core.Models.Projects;

public class ProjectNote : IDataEntity, IHasAssociatedFiles
{
    public string Id { get; set; } = Guid.NewGuid().ToString();

    public override bool Equals(object? obj)
    {
        if (ReferenceEquals(this, obj)) return true;
        return obj is ProjectNote other && Id == other.Id;
    }

    public override int GetHashCode() => Id?.GetHashCode() ?? base.GetHashCode();
    public string ProjectId { get; set; } = string.Empty;
    public DateTime Date { get; set; } = DateTime.Today;
    public string Title { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty; // Supports Markdown
    public bool IsEmail { get; set; } // If true, render as read-only HTML via EmailContentRenderer
    public string? EmailHtmlPath { get; set; } // Relative URL to the project-local .eml copy
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public string CreatedBy { get; set; } = string.Empty; // User Name or ID

    // System Event Tracking
    public string SystemReferenceId { get; set; } = string.Empty;
    public bool IsSystemEvent { get; set; }

    // Attachments
    public List<NoteAttachment> Attachments { get; set; } = [];

    public IEnumerable<string> GetAssociatedFilePaths()
    {
        var paths = Attachments?.Select(a => a.FilePath).Where(p => !string.IsNullOrWhiteSpace(p)) ?? Enumerable.Empty<string>();
        if (!string.IsNullOrWhiteSpace(EmailHtmlPath))
        {
            paths = paths.Concat(new[] { EmailHtmlPath });
        }
        return paths;
    }
}

public class NoteAttachment : IFileAttachment
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string FileName { get; set; } = string.Empty;
    public string FilePath { get; set; } = string.Empty; // Relative or absolute path
    public string ContentType { get; set; } = string.Empty;
    public long FileSizeBytes { get; set; }
    public bool HasServerThumbnail { get; set; }
}



