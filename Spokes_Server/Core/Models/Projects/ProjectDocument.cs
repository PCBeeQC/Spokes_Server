namespace Spokes_Server.Core.Models.Projects;

using Spokes_Server.Core.Data;
using Spokes_Server.Core.Models.Core;

public class ProjectDocument : IDataEntity
{
    public string Id { get; set; } = Guid.NewGuid().ToString();

    public override bool Equals(object? obj)
    {
        if (ReferenceEquals(this, obj)) return true;
        if (obj == null || GetType() != obj.GetType())
            return false;
        var other = (ProjectDocument)obj;
        return Id == other.Id;
    }

    public override int GetHashCode() => Id?.GetHashCode() ?? base.GetHashCode();
    public string ProjectId { get; set; } = string.Empty;
    public string StandardDocumentId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string ResolvedBody { get; set; } = string.Empty;
    public string Status { get; set; } = ProjectDocumentStatus.Draft;
    public bool IsManualEdit { get; set; } = false;
    public bool IsConfidential { get; set; } = false;
    public bool IsLongFooter { get; set; } = false;
    public List<DocumentBlockInstance> Blocks { get; set; } = new();
    public string PdfPath { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

public static class ProjectDocumentStatus
{
    public const string Draft = "Draft";
    public const string Final = "Final";
}
