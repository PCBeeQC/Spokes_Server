namespace Spokes_Server.Tests.Core.Models.Core;

using Spokes_Server.Core.Models.Core;

public class DeviceSessionTests
{
    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("https://push.example.com/v1/send/abc", true)]
    public void DeviceSession_HasPush_ReturnsExpectedValue(string? pushEndpoint, bool expected)
    {
        var session = new DeviceSession
        {
            PushEndpoint = pushEndpoint
        };

        Assert.Equal(expected, session.HasPush);
    }

    [Theory]
    [InlineData(true, "WebPush", "Chrome on Windows", "", true)]
    [InlineData(false, "NativeRelay", "Unknown Device", "", true)]
    [InlineData(false, "NativeRelay", "Windows", "", false)]
    [InlineData(false, "NativeRelay", "Windows (Desktop App)", "", false)]
    [InlineData(false, "NativeRelay", "macOS", "", false)]
    [InlineData(false, "NativeRelay", "Linux", "", false)]
    [InlineData(false, "WebPush", "iOS (App)", "", true)]
    [InlineData(false, "WebPush", "Android (App)", "", true)]
    [InlineData(false, "WebPush", "Unknown Device", "Mozilla/5.0 (iPhone; CPU iPhone OS 17_0) Capacitor/6.0", true)]
    [InlineData(false, "WebPush", "Windows", "Mozilla/5.0 (Windows NT 10.0; Win64; x64)", false)]
    public void DeviceSession_IsCapacitorApp_HeuristicsMatchCorrectly(
        bool isCapacitor,
        string subscriptionType,
        string deviceInfo,
        string pushUserAgent,
        bool expected)
    {
        var session = new DeviceSession
        {
            IsCapacitor = isCapacitor,
            PushSubscriptionType = subscriptionType,
            DeviceInfo = deviceInfo,
            PushUserAgent = pushUserAgent
        };

        Assert.Equal(expected, session.IsCapacitorApp);
    }

    [Fact]
    public void DeviceSession_DeserializesLegacyJsonWithRemovedIsIdleDetectionEnabledProperty()
    {
        const string legacyJson = """
            {
              "Id": "legacy-sess-1",
              "EmployeeId": "emp-1",
              "DeviceType": "Desktop",
              "DeviceName": "Work PC",
              "PushEndpoint": "https://push.example.com/v1/legacy",
              "PushEnabled": true,
              "IsIdleDetectionEnabled": true
            }
            """;

        var session = System.Text.Json.JsonSerializer.Deserialize<DeviceSession>(legacyJson);

        Assert.NotNull(session);
        Assert.Equal("legacy-sess-1", session.Id);
        Assert.Equal("emp-1", session.EmployeeId);
        Assert.Equal(DeviceType.Desktop, session.DeviceType);
        Assert.Equal("Work PC", session.DeviceName);
        Assert.True(session.PushEnabled);
        Assert.True(session.HasPush);
    }
}
