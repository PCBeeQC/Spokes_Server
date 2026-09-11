using MudBlazor;
using Spokes_Server.Core.Helpers;
using Xunit;

namespace Spokes_Server.Tests.Core.Helpers;

public class StatusColorHelperTests
{
    [Theory]
    [InlineData("Paid", Color.Success)]
    [InlineData("paid", Color.Success)]
    [InlineData("PAID", Color.Success)]
    [InlineData(" Fully Billed ", Color.Success)]
    [InlineData("Approved", Color.Success)]
    [InlineData("Accepted", Color.Success)]
    [InlineData("Quote Accepted", Color.Success)]
    [InlineData("Active", Color.Success)]
    public void GetColor_ReturnsSuccess_ForPositiveStatuses(string status, Color expected)
    {
        Assert.Equal(expected, StatusColorHelper.GetColor(status));
    }

    [Theory]
    [InlineData("Final", Color.Info)]
    [InlineData("final", Color.Info)]
    [InlineData("Partially Paid", Color.Info)]
    [InlineData("Partially Billed", Color.Info)]
    [InlineData("Payable", Color.Info)]
    [InlineData("Submitted", Color.Info)]
    [InlineData("Quoted", Color.Info)]
    public void GetColor_ReturnsInfo_ForInformationalStatuses(string status, Color expected)
    {
        Assert.Equal(expected, StatusColorHelper.GetColor(status));
    }

    [Theory]
    [InlineData("Pending", Color.Warning)]
    [InlineData("Awaiting Approval", Color.Warning)]
    [InlineData("Discovery", Color.Warning)]
    [InlineData("In Progress", Color.Warning)]
    public void GetColor_ReturnsWarning_ForCautionaryStatuses(string status, Color expected)
    {
        Assert.Equal(expected, StatusColorHelper.GetColor(status));
    }

    [Theory]
    [InlineData("Overdue", Color.Error)]
    [InlineData("Clawback", Color.Error)]
    [InlineData("Rejected", Color.Error)]
    [InlineData("Cancelled", Color.Error)]
    [InlineData("Canceled", Color.Error)]
    [InlineData("Failed", Color.Error)]
    public void GetColor_ReturnsError_ForAlertStatuses(string status, Color expected)
    {
        Assert.Equal(expected, StatusColorHelper.GetColor(status));
    }

    [Theory]
    [InlineData("Draft", Color.Default)]
    [InlineData("Closed", Color.Default)]
    [InlineData("Completed", Color.Default)]
    [InlineData("Project In Progress", Color.Default)]
    [InlineData("Inactive", Color.Default)]
    [InlineData("None", Color.Default)]
    [InlineData(null, Color.Default)]
    [InlineData("", Color.Default)]
    [InlineData("   ", Color.Default)]
    [InlineData("SomeRandomUnknownStatus", Color.Default)]
    public void GetColor_ReturnsDefault_ForNeutralOrUnknownStatuses(string? status, Color expected)
    {
        Assert.Equal(expected, StatusColorHelper.GetColor(status));
    }
}
