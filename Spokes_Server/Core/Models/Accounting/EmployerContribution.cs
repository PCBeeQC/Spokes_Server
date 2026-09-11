namespace Spokes_Server.Core.Models.Accounting;

using Spokes_Server.Core.Models.Core;
using Spokes_Server.Core.Models.Projects;
using Spokes_Server.Core.Models.Communication;
using Spokes_Server.Core.Models.HR;

using Spokes_Server.Core.Data;




public class EmployerContribution : IDataEntity
{
    public string Id { get; set; } = Guid.NewGuid().ToString();

    public override bool Equals(object? obj)
    {
        if (ReferenceEquals(this, obj)) return true;
        if (obj == null || GetType() != obj.GetType())
            return false;
        var other = (EmployerContribution)obj;
        return Id == other.Id;
    }

    public override int GetHashCode() => Id?.GetHashCode() ?? base.GetHashCode();
    public string Name { get; set; } = string.Empty; // e.g. "QPP", "Health Insurance"

    // Type of Contribution
    public string Type { get; set; } = EmployerContributionType.Percentage; // Percentage or Fixed
    public decimal Value { get; set; } = 0; // The percentage (e.g,. 17.0 for 17%) or Fixed Amount (e.g. 50.00)

    // Scope
    public string Scope { get; set; } = EmployerContributionScope.Global; // Global, Team, or Employee
    public List<string> TargetIds { get; set; } = new(); // IDs of Teams or Employees depending on Scope

    public bool IsActive { get; set; } = true;
}

public static class EmployerContributionType
{
    public const string Percentage = "Percentage"; // % of Gross Salary
    public const string FixedAmount = "FixedAmount"; // $ per Pay Period
}

public static class EmployerContributionScope
{
    public const string Global = "Global"; // All employees
    public const string Team = "Team"; // Specific Teams
    public const string Employee = "Employee"; // Specific Employees
}



