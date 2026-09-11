namespace Spokes_Server.Core.Models.Projects;

using Spokes_Server.Core.Models.Core;
using Spokes_Server.Core.Models.Accounting;
using Spokes_Server.Core.Models.Communication;
using Spokes_Server.Core.Models.HR;

using Spokes_Server.Core.Data;




public class WorkType : IDataEntity
{
    public string Id { get; set; } = Guid.NewGuid().ToString();

    public override bool Equals(object? obj)
    {
        if (ReferenceEquals(this, obj)) return true;
        if (obj == null || GetType() != obj.GetType())
            return false;
        var other = (WorkType)obj;
        return Id == other.Id;
    }

    public override int GetHashCode() => Id?.GetHashCode() ?? base.GetHashCode();
    public string Name { get; set; } = string.Empty;
    public Dictionary<string, string> NameTranslations { get; set; } = new();

    // Revenue: What you charge the client
    public decimal DefaultRate { get; set; }

    // NEW: Cost: What it costs you (Avg. Labor Cost)
    public decimal CostRate { get; set; }

    public List<string> AssignedTeamIds { get; set; } = new();

    // Removed "Exclusive to project" logic in favor of Project-side approval list
    public List<SubTask> SubTasks { get; set; } = new();

    public bool IsActive { get; set; } = true;
}



