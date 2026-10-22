using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using Spokes_Server.Core.Data;
using Xunit;

namespace Spokes_Server.Tests.Core.Services.Maintenance;

public class DatabaseBootstrapperHostedServiceTests
{
    [Fact]
    public async Task StartingAsync_CompletesSuccessfully_WithMinimalServices()
    {
        var services = new ServiceCollection();
        var sp = services.BuildServiceProvider();

        var logger = Mock.Of<ILogger<DatabaseBootstrapperHostedService>>();
        var bootstrapper = new DatabaseBootstrapperHostedService(sp, logger);

        var ex = await Record.ExceptionAsync(() => bootstrapper.StartingAsync(CancellationToken.None));
        Assert.Null(ex);
    }

    [Fact]
    public async Task StoppingAsync_CompletesSuccessfully_WithMinimalServices()
    {
        var services = new ServiceCollection();
        var sp = services.BuildServiceProvider();

        var logger = Mock.Of<ILogger<DatabaseBootstrapperHostedService>>();
        var bootstrapper = new DatabaseBootstrapperHostedService(sp, logger);

        var ex = await Record.ExceptionAsync(() => bootstrapper.StoppingAsync(CancellationToken.None));
        Assert.Null(ex);
    }

    [Fact]
    public async Task LifecycleMethods_StartStartedStopStopped_CompleteSynchronously()
    {
        var services = new ServiceCollection();
        var sp = services.BuildServiceProvider();

        var logger = Mock.Of<ILogger<DatabaseBootstrapperHostedService>>();
        var bootstrapper = new DatabaseBootstrapperHostedService(sp, logger);

        await bootstrapper.StartAsync(CancellationToken.None);
        await bootstrapper.StartedAsync(CancellationToken.None);
        await bootstrapper.StopAsync(CancellationToken.None);
        await bootstrapper.StoppedAsync(CancellationToken.None);
    }

    [Fact]
    public async Task StoppingAsync_FlushesDiskPersistenceService_WhenRegistered()
    {
        var services = new ServiceCollection();
        var tempDir = Path.Combine(Path.GetTempPath(), "spokes-test-" + Guid.NewGuid());
        Directory.CreateDirectory(tempDir);
        try
        {
            var persistenceLogger = Mock.Of<ILogger<DiskPersistenceService>>();
            var diskService = new DiskPersistenceService(persistenceLogger);
            services.AddSingleton<Microsoft.Extensions.Hosting.IHostedService>(diskService);

            var sp = services.BuildServiceProvider();
            var logger = Mock.Of<ILogger<DatabaseBootstrapperHostedService>>();
            var bootstrapper = new DatabaseBootstrapperHostedService(sp, logger);

            var testFile = Path.Combine(tempDir, "flush-test.txt");
            diskService.QueueWrite(testFile, "hello");

            await bootstrapper.StoppingAsync(CancellationToken.None);

            Assert.True(File.Exists(testFile));
            Assert.Contains("hello", await File.ReadAllTextAsync(testFile));
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }
    }
}
