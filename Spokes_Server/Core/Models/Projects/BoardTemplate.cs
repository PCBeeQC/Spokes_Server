namespace Spokes_Server.Core.Models.Projects;

using Spokes_Server.Core.Data;

public class BoardTemplate : IDataEntity
{
    public string Id { get; set; } = Guid.NewGuid().ToString();

    public override bool Equals(object? obj)
    {
        if (ReferenceEquals(this, obj)) return true;
        return obj is BoardTemplate other && Id == other.Id;
    }

    public override int GetHashCode() => Id?.GetHashCode() ?? base.GetHashCode();
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string? Icon { get; set; }

    // Logic from Board
    public List<BoardProperty> Properties { get; set; } = [];
    public List<BoardView> Views { get; set; } = [];
}



