namespace Spokes_Server.Core.Models.Projects;

using Spokes_Server.Core.Models.Accounting;
using Spokes_Server.Core.Models.Core;
using Spokes_Server.Core.Data;

using System.Collections.Generic;
using System.Linq;

public class Project : IDataEntity, IHasAssociatedFiles
{
    public string Id { get; set; } = Guid.NewGuid().ToString();

    public override bool Equals(object? obj)
    {
        if (ReferenceEquals(this, obj)) return true;
        return obj is Project other && Id == other.Id;
    }

    public override int GetHashCode() => Id?.GetHashCode() ?? base.GetHashCode();
    public string ProjectNumber { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;

    public string DisplayName => string.IsNullOrEmpty(ProjectNumber) ? Name : $"{ProjectNumber} - {Name}";
    public ClientInfo Client { get; set; } = new();
    public string Description { get; set; } = string.Empty;

    public string Status { get; set; } = ProjectStatus.Draft;

    public string? RateCardId { get; set; }

    // Key = WorkTypeId, Value = The Custom Rate
    public Dictionary<string, decimal> CustomRates { get; set; } = [];

    public bool IsInternal { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime LastEdited { get; set; } = DateTime.UtcNow;

    public string BillingMethod { get; set; } = BillingMethods.TimeAndMaterials;
    public string? DefaultTemplateId { get; set; }
    public string? ProjectManagerId { get; set; }

    // Commissions
    public List<CommissionBeneficiary> Commissions { get; set; } = [];

    // Access Control (for Contribution: Timesheets, Expenses, Chat)
    // Visibility is controlled by permissions (projects.view)
    public string AccessPolicy { get; set; } = ProjectAccessPolicy.Public;
    public List<string> AllowedTeamIds { get; set; } = [];
    public List<string> AllowedUserIds { get; set; } = [];

    // Approved Tasks for this project (If empty, NO tasks are allowed? Or all? User said "only the approved task... will show up")
    // Use hashset for performance if needed, but List is fine for JSON serialization
    public List<string> ApprovedWorkTypeIds { get; set; } = [];

    // NEW: Budget & Allocations
    public List<ProjectTaskAllocation> Allocations { get; set; } = [];

    // NEW: Purchase Orders
    public List<ClientPo> PurchaseOrders { get; set; } = [];

    public IEnumerable<string> GetAssociatedFilePaths()
    {
        return PurchaseOrders?.Select(p => p.FilePath).Where(p => !string.IsNullOrWhiteSpace(p)) ?? Enumerable.Empty<string>();
    }

    public List<string> NoteCategories { get; set; } = ["General", "Meeting", "Event", "Maintenance", "Email"];
    public Dictionary<string, string> NoteCategoryColors { get; set; } = [];

    // Project Group membership (optional) - if set, invoicing is managed at group level
    public string? ProjectGroupId { get; set; }

    // --- NEW HELPER METHOD ---
    public decimal GetEffectiveRate(string? workTypeId, decimal defaultSystemRate, RateCard? rateCard = null)
    {
        if (string.IsNullOrEmpty(workTypeId))
        {
            return defaultSystemRate;
        }

        // 1. Check for Project-Specific Override (Highest Priority)
        if (CustomRates != null && CustomRates.TryGetValue(workTypeId, out var customRate))
        {
            return customRate;
        }

        // 2. Check Rate Card
        if (rateCard != null && rateCard.Rates.TryGetValue(workTypeId, out var cardRate))
        {
            return cardRate;
        }

        // 3. Fallback to System Default
        return defaultSystemRate;
    }

}

public static class ProjectAccessPolicy
{
    public const string Public = "Public";
    public const string Restricted = "Restricted";
    public static readonly string[] All = [Public, Restricted];
}

public static class BillingMethods
{
    public const string TimeAndMaterials = "Time & Materials (Cost+)";
    public const string FixedPrice = "Fixed Price / Milestones";
}

public class ProjectTaskAllocation
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string WorkTypeId { get; set; } = string.Empty;
    public string TeamId { get; set; } = string.Empty;
    public decimal QuotedHours { get; set; }
    public string Description { get; set; } = string.Empty;
}



