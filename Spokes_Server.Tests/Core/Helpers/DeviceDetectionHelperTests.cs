using Microsoft.AspNetCore.Http;
using Spokes_Server.Core.Helpers;
using Xunit;

namespace Spokes_Server.Tests.Core.Helpers;

public class DeviceDetectionHelperTests
{
    [Theory]
    [InlineData("Mozilla/5.0 (Windows NT 10.0; Win64; x64)", "Windows", false)]
    [InlineData("Mozilla/5.0 (iPhone; CPU iPhone OS 17_0 like Mac OS X)", "iOS", false)]
    [InlineData("Mozilla/5.0 (Linux; Android 14)", "Android", false)]
    [InlineData("Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7)", "macOS", false)]
    [InlineData("CustomBrowser/1.0", "Unknown Device", false)]
    [InlineData("Mozilla/5.0 (Windows NT 10.0; Win64; x64) SpokesDesktop/1.0.0", "Windows (Desktop App)", false)]
    [InlineData("Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) SpokesDesktop/1.0.0", "macOS (Desktop App)", false)]
    [InlineData("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Electron/32.2.8 Safari/537.36", "Windows (Desktop App)", false)]
    public void ParseDeviceInfo_DesktopAndStandardBrowsers_DetectsPlatform(string userAgent, string expectedDevice, bool expectedCapacitor)
    {
        var (deviceInfo, isCapacitor) = DeviceDetectionHelper.ParseDeviceInfo(userAgent);

        Assert.Equal(expectedDevice, deviceInfo);
        Assert.Equal(expectedCapacitor, isCapacitor);
    }

    [Theory]
    [InlineData("Mozilla/5.0 (iPhone; CPU iPhone OS 17_0 like Mac OS X) Capacitor/6.0", "iOS (App)", true)]
    [InlineData("Mozilla/5.0 (Linux; Android 14; wv) Mobile", "Android (App)", true)]
    public void ParseDeviceInfo_MobileAppUserAgents_DetectsAppSuffix(string userAgent, string expectedDevice, bool expectedCapacitor)
    {
        var (deviceInfo, isCapacitor) = DeviceDetectionHelper.ParseDeviceInfo(userAgent);

        Assert.Equal(expectedDevice, deviceInfo);
        Assert.Equal(expectedCapacitor, isCapacitor);
    }

    [Fact]
    public void ParseDeviceInfo_WithClientQueryOrCapacitorHeader_ReturnsCapacitorTrue()
    {
        var (deviceInfo1, isCapacitor1) = DeviceDetectionHelper.ParseDeviceInfo("Mozilla/5.0 (Windows NT 10.0)", clientQuery: "mobile");
        var (deviceInfo2, isCapacitor2) = DeviceDetectionHelper.ParseDeviceInfo("Mozilla/5.0 (Windows NT 10.0)", hasCapacitorHeader: true);

        Assert.True(isCapacitor1);
        Assert.Equal("Windows (App)", deviceInfo1);

        Assert.True(isCapacitor2);
        Assert.Equal("Windows (App)", deviceInfo2);
    }

    [Fact]
    public void IsMobileClient_WithHttpContext_EvaluatesCorrectly()
    {
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Headers["User-Agent"] = "Capacitor Mobile App";

        var isMobile = DeviceDetectionHelper.IsMobileClient(httpContext);

        Assert.True(isMobile);
    }

    [Theory]
    [InlineData("Mozilla/5.0 (iPhone; CPU iPhone OS 17_0 like Mac OS X)", true)]
    [InlineData("Mozilla/5.0 (Linux; Android 14; Mobile)", true)]
    [InlineData("Mozilla/5.0 (iPad; CPU OS 16_5 like Mac OS X)", true)]
    [InlineData("Mozilla/5.0 (iPhone; CPU iPhone OS 17_0 like Mac OS X) Capacitor/6.0", true)]
    [InlineData("Mozilla/5.0 (Windows NT 10.0; Win64; x64)", false)]
    [InlineData("Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7)", false)]
    [InlineData("Mozilla/5.0 (Windows NT 10.0; Win64; x64) SpokesDesktop/1.0.0", false)]
    public void IsMobileDevice_UserAgents_ClassifiedCorrectly(string userAgent, bool expected)
    {
        var result = DeviceDetectionHelper.IsMobileDevice(userAgent);
        Assert.Equal(expected, result);
    }
}
