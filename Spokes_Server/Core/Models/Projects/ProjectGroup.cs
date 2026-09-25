namespace Spokes_Server.Core.Models.Projects;

using Spokes_Server.Core.Data;

public class ProjectGroup : IDataEntity
{
    public string Id { get; set; } = Guid.NewGuid().ToString();

    public override bool Equals(object? obj)
    {
        if (ReferenceEquals(this, obj)) return true;
        return obj is ProjectGroup other && Id == other.Id;
    }

    public override int GetHashCode() => Id?.GetHashCode() ?? base.GetHashCode();
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;

    // Client info - all grouped projects must share this client
    public ClientInfo Client { get; set; } = new();

    // List of project IDs in this group
    public List<string> ProjectIds { get; set; } = [];

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}



