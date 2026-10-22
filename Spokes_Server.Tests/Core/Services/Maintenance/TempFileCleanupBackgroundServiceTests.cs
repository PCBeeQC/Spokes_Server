using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Spokes_Server.Tests.Core.Services.Maintenance;

public class TempFileCleanupBackgroundServiceTests
{
    private readonly Mock<IFileService> _mockFileService;
    private readonly Mock<ILogger<TempFileCleanupBackgroundService>> _mockLogger;

    public TempFileCleanupBackgroundServiceTests()
    {
        _mockFileService = new Mock<IFileService>();
        _mockLogger = new Mock<ILogger<TempFileCleanupBackgroundService>>();
    }

    [Fact]
    public async Task ExecuteAsync_PurgesStaleTempFilesOnStartup()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var service = new TempFileCleanupBackgroundService(
            _mockFileService.Object,
            _mockLogger.Object,
            TimeSpan.FromHours(1));

        await service.StartAsync(cts.Token);
        await service.WaitForIterationAsync(cts.Token);
        await service.StopAsync(CancellationToken.None);

        _mockFileService.Verify(f => f.PurgeStaleTempFiles(null), Times.AtLeastOnce());
    }

    [Fact]
    public async Task ExecuteAsync_HandlesExceptionsGracefully()
    {
        _mockFileService.Setup(f => f.PurgeStaleTempFiles(null))
            .Throws(new InvalidOperationException("Disk error"));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var service = new TempFileCleanupBackgroundService(
            _mockFileService.Object,
            _mockLogger.Object,
            TimeSpan.FromHours(1));

        await service.StartAsync(cts.Token);
        await service.WaitForIterationAsync(cts.Token);
        await service.StopAsync(CancellationToken.None);

        _mockFileService.Verify(f => f.PurgeStaleTempFiles(null), Times.AtLeastOnce());
    }

    [Fact]
    public async Task ExecuteAsync_NotifiesOnIterationCompleted()
    {
        var iterationCount = 0;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var service = new TempFileCleanupBackgroundService(
            _mockFileService.Object,
            _mockLogger.Object,
            TimeSpan.FromHours(1));

        service.OnIterationCompleted += () => Interlocked.Increment(ref iterationCount);

        await service.StartAsync(cts.Token);
        await service.WaitForIterationAsync(cts.Token);
        await service.StopAsync(CancellationToken.None);

        Assert.True(iterationCount >= 1);
        _mockFileService.Verify(f => f.PurgeStaleTempFiles(null), Times.AtLeastOnce());
    }
}
