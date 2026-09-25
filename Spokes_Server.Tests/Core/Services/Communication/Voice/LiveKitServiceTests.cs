using System;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;
using Spokes_Server.Core.Models.Core;
using Spokes_Server.Core.Services.Communication.Voice;

namespace Spokes_Server.Tests.Core.Services.Communication.Voice;

public class LiveKitServiceTests : IDisposable
{
    private readonly Mock<ILogger<LiveKitService>> _loggerMock;
    private readonly LiveKitService _service;

    public LiveKitServiceTests()
    {
        _loggerMock = new Mock<ILogger<LiveKitService>>();
        _service = new LiveKitService(_loggerMock.Object);
    }

    public void Dispose()
    {
    }

    [Fact]
    public void ApplyPortConfiguration_NullConfig_LogsError()
    {
        // Arrange
        SystemConfig? config = null;

        // Act
        _service.ApplyPortConfiguration(config);

        // Assert
        _loggerMock.Verify(
            x => x.Log(
                LogLevel.Error,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, t) => true),
                It.IsAny<Exception>(),
                It.Is<Func<It.IsAnyType, Exception?, string>>((v, t) => true)),
            Times.Once);
    }

    [Fact]
    public void ApplyPortConfiguration_ValidConfig_LogsErrorOnWindowsDueToMissingPaths()
    {
        // Arrange
        var config = new SystemConfig
        {
            IsSetupComplete = true,
            LiveKitFallbackPort = 7881,
            LiveKitUdpStartPort = 50000,
            LiveKitUdpEndPort = 50100,
            LiveKitApiKey = "devkey",
            LiveKitApiSecret = "devsecret"
        };

        // Act
        _service.ApplyPortConfiguration(config);

        // Assert
        _loggerMock.Verify(
            x => x.Log(
                LogLevel.Error,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, t) => true),
                It.IsAny<Exception>(),
                It.Is<Func<It.IsAnyType, Exception?, string>>((v, t) => true)),
            Times.Once);
    }
}
