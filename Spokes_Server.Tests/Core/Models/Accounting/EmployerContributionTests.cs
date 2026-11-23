using Spokes_Server.Core.Models.Accounting;
using Xunit;

namespace Spokes_Server.Tests.Core.Models.Accounting;

public class EmployerContributionTests
{
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

}
