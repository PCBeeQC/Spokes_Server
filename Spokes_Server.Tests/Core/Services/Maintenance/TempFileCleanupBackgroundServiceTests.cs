using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using Spokes_Server.Aggregate;
using Spokes_Server.Core.Data;
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

    [Fact]
    public async Task ExecuteAsync_WhenStorageMaintenanceProvided_RunsOrphanAndSessionPurge()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"Spokes_TempMaint_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        try
        {
            var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
            var config = new Microsoft.Extensions.Configuration.ConfigurationBuilder()
                .AddInMemoryCollection(new System.Collections.Generic.Dictionary<string, string?> { ["DataPath"] = tempRoot , ["SPOKES_MASTER_PASSWORD"] = "UnitTest-Master-Password-2026!"})
                .Build();
            services.AddSingleton<Microsoft.Extensions.Configuration.IConfiguration>(config);
            services.AddLogging();
            services.AddSingleton<EncryptionService>();
            services.AddSpokesDatabase();
            services.AddSingleton(_mockFileService.Object);
            using var sp = services.BuildServiceProvider();
            var db = sp.GetRequiredService<Spokes_Server.Aggregate.Database>();

            var mockMaint = new Mock<Spokes_Server.Core.Services.Maintenance.StorageMaintenanceService>(
                db,
                config,
                Microsoft.Extensions.Logging.Abstractions.NullLogger<Spokes_Server.Core.Services.Maintenance.StorageMaintenanceService>.Instance);
            mockMaint.Setup(m => m.PurgeOrphanedUploadsAsync(It.IsAny<TimeSpan?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new Spokes_Server.Core.Services.Maintenance.OrphanCleanupResult());
            mockMaint.Setup(m => m.PurgeOrphanedPdfsAsync(It.IsAny<TimeSpan?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new Spokes_Server.Core.Services.Maintenance.OrphanCleanupResult());

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var service = new TempFileCleanupBackgroundService(
                _mockFileService.Object,
                _mockLogger.Object,
                mockMaint.Object);

            await service.StartAsync(cts.Token);
            await service.WaitForIterationAsync(cts.Token);
            await service.StopAsync(CancellationToken.None);

            mockMaint.Verify(m => m.PurgeStaleDeviceSessions(null), Times.AtLeastOnce());
            mockMaint.Verify(m => m.PurgeOrphanedUploadsAsync(null, It.IsAny<CancellationToken>()), Times.AtLeastOnce());
            mockMaint.Verify(m => m.PurgeOrphanedPdfsAsync(null, It.IsAny<CancellationToken>()), Times.AtLeastOnce());
        }
        finally
        {
            if (Directory.Exists(tempRoot)) try { Directory.Delete(tempRoot, true); } catch { }
        }
    }
}
