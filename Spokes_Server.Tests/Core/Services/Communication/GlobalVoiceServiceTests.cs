using Xunit;

namespace Spokes_Server.Tests.Core.Services.Communication
{
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
            var mockLogger = new Moq.Mock<Microsoft.Extensions.Logging.ILogger<Spokes_Server.Core.Services.Communication.Voice.GlobalVoiceService>>();
            var service = new Spokes_Server.Core.Services.Communication.Voice.GlobalVoiceService(null!, null!, null!, null!, mockLogger.Object);

            for (int i = 0; i < 15; i++)
            {
                service.LogToServer("INFO", $"Message {i}");
            }

            // Only 10 logs should have been passed to ILogger.
            // We can verify that LogInformation was called exactly 10 times.
            mockLogger.Verify(
                x => x.Log(
                    Microsoft.Extensions.Logging.LogLevel.Information,
                    Moq.It.IsAny<Microsoft.Extensions.Logging.EventId>(),
                    Moq.It.Is<Moq.It.IsAnyType>((v, t) => true),
                    Moq.It.IsAny<System.Exception>(),
                    Moq.It.Is<System.Func<Moq.It.IsAnyType, System.Exception?, string>>((v, t) => true)),
                Moq.Times.Exactly(10));
        }
    }
}
