namespace Spokes_Server.Tests.Core.Models.Core;

using Spokes_Server.Core.Models.Core;

public class PresenceTierTests
{
    [Fact]
    public void PresenceTier_Values_HaveExpectedIntegerValues()
    {
        Assert.Equal(0, (int)PresenceTier.None);
        Assert.Equal(1, (int)PresenceTier.MobileOnly);
        Assert.Equal(2, (int)PresenceTier.DesktopOnly);
        Assert.Equal(3, (int)PresenceTier.All);
    }

    [Theory]
    [InlineData("None", PresenceTier.None)]
    [InlineData("MobileOnly", PresenceTier.MobileOnly)]
    [InlineData("DesktopOnly", PresenceTier.DesktopOnly)]
    [InlineData("All", PresenceTier.All)]
    public void PresenceTier_CanBeParsedFromString(string valueString, PresenceTier expected)
    {
        var parsed = Enum.Parse<PresenceTier>(valueString);
        Assert.Equal(expected, parsed);
    }

    [Fact]
    public void PresenceTier_DefinedValuesCount()
    {
        var values = Enum.GetValues<PresenceTier>();
        Assert.Equal(4, values.Length);
    }
}
