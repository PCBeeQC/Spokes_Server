using Spokes_Server.Core.Models.Accounting;
using Spokes_Server.Core.Models.Core;
using Spokes_Server.Core.Models.Projects;

namespace Spokes_Server.Tests.Core.Models.Projects;

public class ProjectsTests
{
    [Fact]
    public void Project_DisplayName_ConcatenatesProperly()
    {
        var p1 = new Project { Name = "Test Project" };
        Assert.Equal("Test Project", p1.DisplayName);

        var p2 = new Project { ProjectNumber = "P100", Name = "Test Project" };
        Assert.Equal("P100 - Test Project", p2.DisplayName);
    }

    [Fact]
    public void GetEffectiveRate_PrioritizesCorrectly()
    {
        var project = new Project();
        project.CustomRates["WorkA"] = 150m;

        var rateCard = new RateCard();
        rateCard.Rates["WorkA"] = 120m;
        rateCard.Rates["WorkB"] = 100m;

        // 1. Project Override
        Assert.Equal(150m, project.GetEffectiveRate("WorkA", 80m, rateCard));

        // 2. Rate Card
        Assert.Equal(100m, project.GetEffectiveRate("WorkB", 80m, rateCard));

        // 3. System Default
        Assert.Equal(80m, project.GetEffectiveRate("WorkC", 80m, rateCard));
    }

    #region Equals and GetHashCode

    [Fact]
    public void Project_Equals_HandlesReferenceEquality()
    {
        var p = new Project { Id = "P-1" };
        Assert.True(p.Equals(p));
        Assert.True(p.Equals((object)p));
    }

    [Fact]
    public void Project_Equals_SameId_ReturnsTrue()
    {
        var p1 = new Project { Id = "P-1", Name = "Project 1" };
        var p2 = new Project { Id = "P-1", Name = "Different Name" };
        Assert.True(p1.Equals(p2));
        Assert.True(p1.Equals((object)p2));
    }

    [Fact]
    public void Project_Equals_DifferentId_ReturnsFalse()
    {
        var p1 = new Project { Id = "P-1" };
        var p2 = new Project { Id = "P-2" };
        Assert.False(p1.Equals(p2));
    }

    [Fact]
    public void Project_Equals_NullOrDifferentType_ReturnsFalse()
    {
        var p = new Project { Id = "P-1" };
        Assert.False(p.Equals(null));
        Assert.False(p.Equals("string object"));
        Assert.False(p.Equals(123));
        Assert.False(p.Equals(new Invoice { Id = "P-1" }));
    }

    [Fact]
    public void Project_GetHashCode_SameId_ProducesSameHashCode()
    {
        var p1 = new Project { Id = "P-1" };
        var p2 = new Project { Id = "P-1" };
        Assert.Equal(p1.GetHashCode(), p2.GetHashCode());
    }

    [Fact]
    public void Project_GetHashCode_NullId_DoesNotThrow()
    {
        var p = new Project { Id = null! };
        var exception = Record.Exception(() => p.GetHashCode());
        Assert.Null(exception);
    }

    #endregion

    #region GetEffectiveRate

    [Fact]
    public void GetEffectiveRate_FallbacksWorkAsExpected()
    {
        var project = new Project();
        decimal defaultRate = 75m;

        // 1. CustomRates doesn't contain key and rateCard is null -> returns defaultSystemRate
        var rate1 = project.GetEffectiveRate("NonExistentWorkType", defaultRate, null);
        Assert.Equal(defaultRate, rate1);

        // 2. CustomRates doesn't contain key and rateCard is not null, but rateCard.Rates doesn't contain key -> returns defaultSystemRate
        var rateCard = new RateCard();
        rateCard.Rates["OtherWorkType"] = 120m;
        var rate2 = project.GetEffectiveRate("NonExistentWorkType", defaultRate, rateCard);
        Assert.Equal(defaultRate, rate2);

        // 3. CustomRates is null fallback -> does not throw, returns rateCard or default
        project.CustomRates = null!;
        var rate3 = project.GetEffectiveRate("NonExistentWorkType", defaultRate, null);
        Assert.Equal(defaultRate, rate3);

        var rate4 = project.GetEffectiveRate("OtherWorkType", defaultRate, rateCard);
        Assert.Equal(120m, rate4);
    }

    [Fact]
    public void GetEffectiveRate_WhenWorkTypeIdNullOrEmpty_ReturnsDefaultSystemRate()
    {
        var project = new Project();
        project.CustomRates["WorkA"] = 150m;
        var rateCard = new RateCard();
        rateCard.Rates["WorkA"] = 120m;

        var nullResult = project.GetEffectiveRate(null, 75m, rateCard);
        var emptyResult = project.GetEffectiveRate(string.Empty, 75m, rateCard);

        Assert.Equal(75m, nullResult);
        Assert.Equal(75m, emptyResult);
    }

    #endregion

}
