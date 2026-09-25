namespace Spokes_Server.Tests.Core.Services.Communication;

using Microsoft.Extensions.Logging;
using Moq;
using Spokes_Server.Core.Services.Communication.Voice;

public class GlobalVoiceServiceTests
{
    [Fact(Skip = "Requires full JS Interop mock for navigator.userAgent logic")]
    public void ConnectAsync_OnIOS_OverridesNoiseSuppressionToWebrtc()
    {
        // Logic requires JSRuntime mock
    }

    [Fact]
    public void LogToServer_LimitsTo10CallsPerMinute()
    {
        var mockLogger = new Mock<ILogger<GlobalVoiceService>>();
        var service = new GlobalVoiceService(null!, null!, null!, null!, mockLogger.Object, null!);

        for (int i = 0; i < 15; i++)
        {
            service.LogToServer("INFO", $"Message {i}");
        }

        // Only 10 logs should have been passed to ILogger.
        // We can verify that LogInformation was called exactly 10 times.
        mockLogger.Verify(
            x => x.Log(
                LogLevel.Information,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, t) => true),
                It.IsAny<Exception>(),
                It.Is<Func<It.IsAnyType, Exception?, string>>((v, t) => true)),
            Times.Exactly(10));
    }
}
