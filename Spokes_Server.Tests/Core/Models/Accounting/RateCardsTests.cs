using System;
using Spokes_Server.Core.Models.Accounting;
using Xunit;

namespace Spokes_Server.Tests.Core.Models.Accounting;

public class RateCardsTests
{
    [Fact]
    public void Initialization_SetsDefaultValues()
    {
        var rateCard = new RateCard();

        Assert.NotNull(rateCard.Id);
        Assert.True(Guid.TryParse(rateCard.Id, out var parsedGuid));
        Assert.NotEqual(Guid.Empty, parsedGuid);
        Assert.Equal(string.Empty, rateCard.Name);
        Assert.True(rateCard.IsActive);
        Assert.Equal(0.15m, rateCard.MarkupRate);
        Assert.NotNull(rateCard.Rates);
        Assert.Empty(rateCard.Rates);
    }

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

    [Fact]
    public void Rates_CanAddAndRetrieveRateOverrides()
    {
        var rateCard = new RateCard();
        var workTypeId1 = "worktype-developer";
        var workTypeId2 = "worktype-designer";

        rateCard.Rates[workTypeId1] = 125.50m;
        rateCard.Rates[workTypeId2] = 95.00m;

        Assert.Equal(2, rateCard.Rates.Count);
        Assert.True(rateCard.Rates.ContainsKey(workTypeId1));
        Assert.Equal(125.50m, rateCard.Rates[workTypeId1]);
        Assert.True(rateCard.Rates.ContainsKey(workTypeId2));
        Assert.Equal(95.00m, rateCard.Rates[workTypeId2]);
    }
}
