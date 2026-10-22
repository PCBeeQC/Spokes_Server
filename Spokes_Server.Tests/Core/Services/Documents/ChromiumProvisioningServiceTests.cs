using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Moq;
using Spokes_Server.Core.Services.Documents;
using Xunit;

namespace Spokes_Server.Tests.Core.Services.Documents;

public class ChromiumProvisioningServiceTests
{
    private readonly Mock<IBrowserManager> _browserManagerMock;
    private readonly Mock<ILogger<ChromiumProvisioningService>> _loggerMock;

    public ChromiumProvisioningServiceTests()
    {
        _browserManagerMock = new Mock<IBrowserManager>();
        _loggerMock = new Mock<ILogger<ChromiumProvisioningService>>();
    }

    private class TestableChromiumProvisioningService : ChromiumProvisioningService
    {
        public TestableChromiumProvisioningService(IBrowserManager browserManager, ILogger<ChromiumProvisioningService> logger)
            : base(browserManager, logger)
        {
        }

        public Task RunExecuteAsync(CancellationToken token) => ExecuteAsync(token);
    }

    [Fact]
    public async Task ExecuteAsync_WhenSuccessful_EnsuresProvisionedAndLogsCompletion()
    {
        _browserManagerMock
            .Setup(b => b.EnsureProvisionedAsync(It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var service = new TestableChromiumProvisioningService(_browserManagerMock.Object, _loggerMock.Object);

        await service.RunExecuteAsync(CancellationToken.None);

        _browserManagerMock.Verify(b => b.EnsureProvisionedAsync(It.IsAny<CancellationToken>()), Times.Once);
        _loggerMock.Verify(
            x => x.Log(
                LogLevel.Information,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, t) => v.ToString()!.Contains("Chromium ready for PDF generation")),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }

    [Fact]
    public async Task ExecuteAsync_WhenCancellationRequested_ExitsGracefullyWithoutThrowing()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        _browserManagerMock
            .Setup(b => b.EnsureProvisionedAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException(cts.Token));

        var service = new TestableChromiumProvisioningService(_browserManagerMock.Object, _loggerMock.Object);

        var task = service.RunExecuteAsync(cts.Token);
        await task;

        Assert.True(task.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task ExecuteAsync_WhenExceptionThrown_LogsErrorAndDoesNotBubble()
    {
        var expectedEx = new InvalidOperationException("Chromium download failed");
        _browserManagerMock
            .Setup(b => b.EnsureProvisionedAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(expectedEx);

        var service = new TestableChromiumProvisioningService(_browserManagerMock.Object, _loggerMock.Object);

        await service.RunExecuteAsync(CancellationToken.None);

        _loggerMock.Verify(
            x => x.Log(
                LogLevel.Error,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, t) => v.ToString()!.Contains("Failed to provision Chromium")),
                expectedEx,
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }

    [Fact]
    public async Task Lifecycle_StartAsyncAndStopAsync_ExecutesAndShutsDownCleanly()
    {
        _browserManagerMock
            .Setup(b => b.EnsureProvisionedAsync(It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var service = new ChromiumProvisioningService(_browserManagerMock.Object, _loggerMock.Object);

        await service.StartAsync(CancellationToken.None);
        await service.StopAsync(CancellationToken.None);

        _browserManagerMock.Verify(b => b.EnsureProvisionedAsync(It.IsAny<CancellationToken>()), Times.Once);
    }
}
