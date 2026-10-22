namespace Spokes_Server.Tests.Components.UI;

using Spokes_Server.Components.UI;
using Xunit;

public class MediaZoomHelperTests
{
    [Theory]
    [InlineData(0.5, 1.0)]
    [InlineData(1.0, 1.0)]
    [InlineData(2.5, 2.5)]
    [InlineData(5.0, 5.0)]
    [InlineData(10.0, 5.0)]
    public void ClampScale_RestrictsValuesWithinRange(double input, double expected)
    {
        var result = MediaZoomHelper.ClampScale(input);
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData(1.0, 100)]
    [InlineData(1.25, 125)]
    [InlineData(1.5, 150)]
    [InlineData(2.0, 200)]
    [InlineData(5.0, 500)]
    [InlineData(0.8, 100)]
    [InlineData(6.0, 500)]
    public void ToPercent_CalculatesAccuratePercentages(double scale, int expectedPercent)
    {
        var result = MediaZoomHelper.ToPercent(scale);
        Assert.Equal(expectedPercent, result);
    }

    [Theory]
    [InlineData(1.0, 1.25)]
    [InlineData(1.25, 1.50)]
    [InlineData(1.75, 2.00)]
    [InlineData(2.0, 2.50)]
    [InlineData(4.5, 5.00)]
    [InlineData(5.0, 5.00)]
    public void GetNextZoomIn_IncrementsInExpectedSteps(double currentScale, double expectedScale)
    {
        var result = MediaZoomHelper.GetNextZoomIn(currentScale);
        Assert.Equal(expectedScale, result, 2);
    }

    [Theory]
    [InlineData(5.0, 4.50)]
    [InlineData(4.5, 4.00)]
    [InlineData(2.5, 2.00)]
    [InlineData(2.0, 1.75)]
    [InlineData(1.25, 1.00)]
    [InlineData(1.0, 1.00)]
    public void GetNextZoomOut_DecrementsInExpectedSteps(double currentScale, double expectedScale)
    {
        var result = MediaZoomHelper.GetNextZoomOut(currentScale);
        Assert.Equal(expectedScale, result, 2);
    }

    [Fact]
    public void CalculatePanBounds_WhenScaleIsOneOrLess_ReturnsZeroOffsets()
    {
        var bounds = MediaZoomHelper.CalculatePanBounds(
            containerWidth: 1000,
            containerHeight: 800,
            contentWidth: 800,
            contentHeight: 600,
            scale: 1.0,
            targetTx: 100,
            targetTy: 100);

        Assert.Equal(0.0, bounds.ClampedTx);
        Assert.Equal(0.0, bounds.ClampedTy);
    }

    [Fact]
    public void CalculatePanBounds_WhenVisualContentFitsContainer_BoundsRemainZero()
    {
        // Container 1000x800, Content 400x300 at scale 2 = visual 800x600 (still fits within 1000x800)
        var bounds = MediaZoomHelper.CalculatePanBounds(
            containerWidth: 1000,
            containerHeight: 800,
            contentWidth: 400,
            contentHeight: 300,
            scale: 2.0,
            targetTx: 50,
            targetTy: 50);

        Assert.Equal(0.0, bounds.ClampedTx);
        Assert.Equal(0.0, bounds.ClampedTy);
    }

    [Fact]
    public void CalculatePanBounds_WhenVisualContentExceedsContainer_ClampsProperly()
    {
        // Container 1000x800, Content 800x600 at scale 2 = visual 1600x1200
        // maxX = (1600 - 1000) / 2 = 300
        // maxY = (1200 - 800) / 2 = 200

        // Case 1: Within bounds
        var within = MediaZoomHelper.CalculatePanBounds(1000, 800, 800, 600, 2.0, 150, -100);
        Assert.Equal(150.0, within.ClampedTx);
        Assert.Equal(-100.0, within.ClampedTy);

        // Case 2: Exceeding positive bounds
        var positiveExceed = MediaZoomHelper.CalculatePanBounds(1000, 800, 800, 600, 2.0, 500, 350);
        Assert.Equal(300.0, positiveExceed.ClampedTx);
        Assert.Equal(200.0, positiveExceed.ClampedTy);

        // Case 3: Exceeding negative bounds
        var negativeExceed = MediaZoomHelper.CalculatePanBounds(1000, 800, 800, 600, 2.0, -450, -400);
        Assert.Equal(-300.0, negativeExceed.ClampedTx);
        Assert.Equal(-200.0, negativeExceed.ClampedTy);
    }
}
