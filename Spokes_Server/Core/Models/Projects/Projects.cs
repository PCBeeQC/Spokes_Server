namespace Spokes_Server.Core.Models.Projects;

using Spokes_Server.Core.Models.Core;
using Spokes_Server.Core.Models.Accounting;
using Spokes_Server.Core.Models.Communication;
using Spokes_Server.Core.Models.HR;

using Spokes_Server.Core.Data;




public class Project : IDataEntity
{
    public string Id { get; set; } = Guid.NewGuid().ToString();

    public override bool Equals(object? obj)
    {
        if (ReferenceEquals(this, obj)) return true;
        if (obj == null || GetType() != obj.GetType())
            return false;
        var other = (Project)obj;
        return Id == other.Id;
    }

    public override int GetHashCode() => Id?.GetHashCode() ?? base.GetHashCode();
    public string ProjectNumber { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;

    public string DisplayName => string.IsNullOrEmpty(ProjectNumber) ? Name : $"{ProjectNumber} - {Name}";
    public ClientInfo Client { get; set; } = new ClientInfo();
    public string Description { get; set; } = string.Empty;

    public string Status { get; set; } = ProjectStatus.Draft;

    public string? RateCardId { get; set; }

    // Key = WorkTypeId, Value = The Custom Rate
    public Dictionary<string, decimal> CustomRates { get; set; } = new();

    public bool IsInternal { get; set; } = false;
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime LastEdited { get; set; } = DateTime.UtcNow;

    public string BillingMethod { get; set; } = BillingMethods.TimeAndMaterials;
    public string? DefaultTemplateId { get; set; }
    public string? ProjectManagerId { get; set; }

    // Commissions
    public List<CommissionBeneficiary> Commissions { get; set; } = new();

    // Access Control (for Contribution: Timesheets, Expenses, Chat)
    // Visibility is controlled by permissions (projects.view)
    public string AccessPolicy { get; set; } = "Public"; // "Public" or "Restricted"
    public List<string> AllowedTeamIds { get; set; } = new();
    public List<string> AllowedUserIds { get; set; } = new();

    // Approved Tasks for this project (If empty, NO tasks are allowed? Or all? User said "only the approved task... will show up")
    // Use hashset for performance if needed, but List is fine for JSON serialization
    public List<string> ApprovedWorkTypeIds { get; set; } = new();

    // NEW: Budget & Allocations
    public List<ProjectTaskAllocation> Allocations { get; set; } = new();

    // NEW: Purchase Orders
    public List<ClientPO> PurchaseOrders { get; set; } = new();

    public List<string> NoteCategories { get; set; } = new() { "General", "Meeting", "Event", "Maintenance", "Email" };
    public Dictionary<string, string> NoteCategoryColors { get; set; } = new();

    // Project Group membership (optional) - if set, invoicing is managed at group level
    public string? ProjectGroupId { get; set; }

    // --- NEW HELPER METHOD ---
    public decimal GetEffectiveRate(string workTypeId, decimal defaultSystemRate, RateCard? rateCard = null)
    {
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

    public decimal GetAvailableCredit(List<Invoice> projectInvoices)
    {
        decimal creditEarned = 0;
        decimal creditUsed = 0;

        foreach (var inv in projectInvoices)
        {
            // 1. Credit is Earned only when the invoice is PAID
            if (inv.Status == "Paid")
            {
                creditEarned += inv.Lines.Where(l => l.IsPrepayment).Sum(l => l.Total);
            }

            // 2. Credit is Used whenever applied (unless Void)
            if (inv.Status != "Void")
            {
                // Note: Credit Used lines are negative numbers in the invoice, so we subtract their absolute value
                creditUsed += inv.Lines.Where(l => l.IsCreditUsed).Sum(l => Math.Abs(l.Total));
            }
        }

        return creditEarned - creditUsed;
    }

    public bool IsCommissionPayable(List<Invoice> projectInvoices, List<ProjectStatusConfig> availableStatuses)
    {
        var config = availableStatuses.FirstOrDefault(s => string.Equals(s.Name, Status, StringComparison.OrdinalIgnoreCase));
        bool isProjectDone = config != null && (config.SystemMapping == SystemStatusMappings.Completed || config.SystemMapping == SystemStatusMappings.Archived);
        if (!isProjectDone) return false;

        // 2. All Invoices must be fully Paid or Void
        bool allInvoicesPaid = projectInvoices.All(i => i.Status == "Paid" || i.Status == "Void");

        return allInvoicesPaid;
    }
}

public static class BillingMethods
{
    public const string TimeAndMaterials = "Time & Materials (Cost+)";
    public const string FixedPrice = "Fixed Price / Milestones";
}

public static class ProjectStatus
{
    public const string Draft = "Draft";
    public const string Quoted = "Quoted";
    public const string InProduction = "Quote Accepted / In Production";
    public const string WaitingForPayment = "Waiting for last payment";
    public const string Discovery = "Discovery";
    public const string OnHold = "On Hold";
    public const string Completed = "Completed";
    public const string Archived = "Archived";

    public static List<string> All => new()
    {
        Draft, Quoted, InProduction, WaitingForPayment, Discovery, OnHold, Completed, Archived
    };
}

public class ProjectStatusConfig
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Name { get; set; } = string.Empty;
    public string Color { get; set; } = "Default"; // MudBlazor Color Enum as string
    public bool IsSystemDefault { get; set; } = false; // Cannot be renamed/deleted if true (optional, but good practice)
    public string SystemMapping { get; set; } = SystemStatusMappings.Standard;
}

public static class SystemStatusMappings
{
    public const string Standard = "Standard";
    public const string Completed = "Completed";
    public const string Archived = "Archived";

    public static List<string> All => new() { Standard, Completed, Archived };
}

public class ProjectTaskAllocation
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string WorkTypeId { get; set; } = string.Empty;
    public string TeamId { get; set; } = string.Empty;
    public decimal QuotedHours { get; set; }
    public string Description { get; set; } = string.Empty;
}



