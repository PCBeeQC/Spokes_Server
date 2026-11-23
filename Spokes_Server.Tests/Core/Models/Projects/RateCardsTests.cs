using System;
using Spokes_Server.Core.Models.Projects;
using Xunit;

namespace Spokes_Server.Tests.Core.Models.Projects;

public class RateCardsTests
{
    [Fact]
    public void Equals_And_GetHashCode_Behavior()
    {
        var id = Guid.NewGuid().ToString();
        var card1 = new RateCard { Id = id, Name = "Standard" };
        var card2 = new RateCard { Id = id, Name = "Different Name" };
        var card3 = new RateCard { Id = Guid.NewGuid().ToString(), Name = "Standard" };

        // Reference equality
        Assert.True(card1.Equals(card1));

        // Same ID equality
        Assert.True(card1.Equals(card2));
        Assert.Equal(card1.GetHashCode(), card2.GetHashCode());

        // Different ID inequality
        Assert.False(card1.Equals(card3));

        // Null and different type inequality
        Assert.False(card1.Equals(null));
        Assert.False(card1.Equals("some string"));
        Assert.False(card1.Equals(new object()));
    }

}
