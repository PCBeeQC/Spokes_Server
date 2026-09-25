namespace Spokes_Server.Core.Models.HR;

using Spokes_Server.Core.Data;

public class WeeklyPlan : IDataEntity
{
    public string Id { get; set; } = Guid.NewGuid().ToString();

    public override bool Equals(object? obj)
    {
        if (ReferenceEquals(this, obj)) return true;
        return obj is WeeklyPlan other && Id == other.Id;
    }

    public override int GetHashCode() => Id?.GetHashCode() ?? base.GetHashCode();

    public string EmployeeId { get; set; } = string.Empty;
    public string EmployeeName { get; set; } = string.Empty; // Cache for easy display

    public int Year { get; set; }
    public int WeekNumber { get; set; }

    // The user's flexible plan
    public List<PlanEntry> Entries { get; set; } = [];

    public decimal TotalHours => Entries.Sum(e => e.Hours);
}

public class PlanEntry
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string ProjectId { get; set; } = string.Empty;
    public string ProjectName { get; set; } = string.Empty; // Cache for display
    public string WorkTypeId { get; set; } = string.Empty;

    public decimal Hours { get; set; }
    public string SubTaskId { get; set; } = string.Empty;
    public string Note { get; set; } = string.Empty; // e.g. "Working on login feature"
}



