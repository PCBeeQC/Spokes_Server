namespace Spokes_Server.Core.Models.Projects;

using Spokes_Server.Core.Data;

public class WorkType : IDataEntity
{
    public string Id { get; set; } = Guid.NewGuid().ToString();

    public override bool Equals(object? obj)
    {
        if (ReferenceEquals(this, obj)) return true;
        return obj is WorkType other && Id == other.Id;
    }

    public override int GetHashCode() => Id?.GetHashCode() ?? base.GetHashCode();
    public string Name { get; set; } = string.Empty;
    public Dictionary<string, string> NameTranslations { get; set; } = [];

    // Revenue: What you charge the client
    public decimal DefaultRate { get; set; }

    // NEW: Cost: What it costs you (Avg. Labor Cost)
    public decimal CostRate { get; set; }

    public List<string> AssignedTeamIds { get; set; } = [];

    // Removed "Exclusive to project" logic in favor of Project-side approval list
    public List<SubTask> SubTasks { get; set; } = [];

    public bool IsActive { get; set; } = true;
}



