namespace Spokes_Server.Core.Models.Accounting;

using Spokes_Server.Core.Models.Core;
using Spokes_Server.Core.Models.Projects;
using Spokes_Server.Core.Models.Communication;
using Spokes_Server.Core.Models.HR;

using Spokes_Server.Core.Data;




public class RateCard : IDataEntity
{
    public string Id { get; set; } = Guid.NewGuid().ToString();

    public override bool Equals(object? obj)
    {
        if (ReferenceEquals(this, obj)) return true;
        if (obj == null || GetType() != obj.GetType())
            return false;
        var other = (RateCard)obj;
        return Id == other.Id;
    }

    public override int GetHashCode() => Id?.GetHashCode() ?? base.GetHashCode();
    public string Name { get; set; } = string.Empty; // e.g. "Discounted", "Premium"
    public bool IsActive { get; set; } = true;
    public decimal MarkupRate { get; set; } = 0.15m; // 15% default markup for expenses

    // Key = WorkTypeId, Value = The Overridden Rate
    public Dictionary<string, decimal> Rates { get; set; } = new();
}



