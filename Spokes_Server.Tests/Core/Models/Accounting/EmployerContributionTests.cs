using Spokes_Server.Core.Models.Accounting;
using Xunit;

namespace Spokes_Server.Tests.Core.Models.Accounting;

public class EmployerContributionTests
{
    [Fact]
    public void Initialization_SetsDefaultValues()
    {
        // Act
        var contribution = new EmployerContribution();

        // Assert
        Assert.False(string.IsNullOrWhiteSpace(contribution.Id));
        Assert.True(Guid.TryParse(contribution.Id, out _));
        Assert.Equal(string.Empty, contribution.Name);
        Assert.Equal(EmployerContributionType.Percentage, contribution.Type);
        Assert.Equal("Percentage", contribution.Type);
        Assert.Equal(0m, contribution.Value);
        Assert.Equal(EmployerContributionScope.Global, contribution.Scope);
        Assert.Equal("Global", contribution.Scope);
        Assert.NotNull(contribution.TargetIds);
        Assert.Empty(contribution.TargetIds);
        Assert.True(contribution.IsActive);
    }

    [Fact]
    public void Equals_And_GetHashCode_Behavior()
    {
        // Arrange
        var id = Guid.NewGuid().ToString();
        var contribution1 = new EmployerContribution { Id = id, Name = "Health" };
        var contribution2 = new EmployerContribution { Id = id, Name = "Dental" };
        var contribution3 = new EmployerContribution { Id = Guid.NewGuid().ToString(), Name = "Health" };

        // Reference equality
        Assert.True(contribution1.Equals(contribution1));

        // Value / ID-based equality
        Assert.True(contribution1.Equals(contribution2));
        Assert.True(contribution2.Equals(contribution1));
        Assert.Equal(contribution1.GetHashCode(), contribution2.GetHashCode());

        // Unequal IDs
        Assert.False(contribution1.Equals(contribution3));
        Assert.False(contribution3.Equals(contribution1));

        // Null and different type comparison
        Assert.False(contribution1.Equals(null));
        Assert.False(contribution1.Equals("some string"));
        Assert.False(contribution1.Equals(new object()));

        // Null Id fallback GetHashCode
        var nullIdContribution = new EmployerContribution { Id = null! };
        Assert.IsType<int>(nullIdContribution.GetHashCode());
    }

    [Fact]
    public void EmployerContributionType_Constants()
    {
        Assert.Equal("Percentage", EmployerContributionType.Percentage);
        Assert.Equal("FixedAmount", EmployerContributionType.FixedAmount);
    }

    [Fact]
    public void EmployerContributionScope_Constants()
    {
        Assert.Equal("Global", EmployerContributionScope.Global);
        Assert.Equal("Team", EmployerContributionScope.Team);
        Assert.Equal("Employee", EmployerContributionScope.Employee);
    }

    [Fact]
    public void Properties_CanBeSetAndRetrieved()
    {
        // Arrange & Act
        var contribution = new EmployerContribution
        {
            Id = "custom-id",
            Name = "Pension Plan",
            Type = EmployerContributionType.FixedAmount,
            Value = 150.50m,
            Scope = EmployerContributionScope.Employee,
            TargetIds = ["emp-1", "emp-2"],
            IsActive = false
        };

        // Assert
        Assert.Equal("custom-id", contribution.Id);
        Assert.Equal("Pension Plan", contribution.Name);
        Assert.Equal(EmployerContributionType.FixedAmount, contribution.Type);
        Assert.Equal(150.50m, contribution.Value);
        Assert.Equal(EmployerContributionScope.Employee, contribution.Scope);
        Assert.Equal(2, contribution.TargetIds.Count);
        Assert.Contains("emp-1", contribution.TargetIds);
        Assert.Contains("emp-2", contribution.TargetIds);
        Assert.False(contribution.IsActive);
    }
}
