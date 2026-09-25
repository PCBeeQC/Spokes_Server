using System.Reflection;
using System.Threading;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using Spokes_Server.Core.Services.Core;

namespace Spokes_Server.Tests.Core.Services.Core;

public class DemoResetServiceTests : TestDataTestBase
{
        public class TestableDemoResetService : DemoResetService
        {
            public TestableDemoResetService(ILogger<DemoResetService> logger, IServiceProvider services, IConfiguration config)
                : base(logger, services, config) { }

            public Task RunExecuteAsync(CancellationToken token) => ExecuteAsync(token);

            public string? GetDataPath()
            {
                return typeof(DemoResetService)
                    .GetField("_dataPath", BindingFlags.NonPublic | BindingFlags.Instance)
                    ?.GetValue(this) as string;
            }
        }

        [Fact]
        public async Task ExecuteAsync_WhenDemoModeIsFalse_ReturnsImmediately()
        {
            // Arrange
            var configDict = new Dictionary<string, string?>
            {
                { "Spokes_DemoMode", "false" },
                { "DataPath", _testDataPath }
            };
            var config = new ConfigurationBuilder().AddInMemoryCollection(configDict).Build();
            var loggerMock = new Mock<ILogger<DemoResetService>>();
            var serviceProviderMock = new Mock<IServiceProvider>();

            var service = new TestableDemoResetService(loggerMock.Object, serviceProviderMock.Object, config);

            // Act
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var executeTask = service.RunExecuteAsync(cts.Token);

            // Assert: returns immediately without waiting for delay or cancellation
            var completedTask = await Task.WhenAny(executeTask, Task.Delay(2000, cts.Token));
            Assert.Same(executeTask, completedTask);
            await executeTask;
        }

        [Fact]
        public async Task ExecuteAsync_WhenDemoModeIsUnset_ReturnsImmediately()
        {
            // Arrange - Spokes_DemoMode key missing entirely
            var configDict = new Dictionary<string, string?>();
            var config = new ConfigurationBuilder().AddInMemoryCollection(configDict).Build();
            var loggerMock = new Mock<ILogger<DemoResetService>>();
            var serviceProviderMock = new Mock<IServiceProvider>();

            var service = new TestableDemoResetService(loggerMock.Object, serviceProviderMock.Object, config);

            // Act
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var executeTask = service.RunExecuteAsync(cts.Token);

            // Assert: returns immediately
            var completedTask = await Task.WhenAny(executeTask, Task.Delay(2000, cts.Token));
            Assert.Same(executeTask, completedTask);
            await executeTask;
        }

        [Fact]
        public async Task ExecuteAsync_WhenDemoModeIsTrue_ExitsGracefullyOnCancellation()
        {
            // Arrange
            var configDict = new Dictionary<string, string?>
            {
                { "Spokes_DemoMode", "true" },
                { "DataPath", _testDataPath }
            };
            var config = new ConfigurationBuilder().AddInMemoryCollection(configDict).Build();
            var loggerMock = new Mock<ILogger<DemoResetService>>();
            var serviceProviderMock = new Mock<IServiceProvider>();

            var service = new TestableDemoResetService(loggerMock.Object, serviceProviderMock.Object, config);

            using var cts = new CancellationTokenSource();
            cts.Cancel(); // Pre-cancelled token

            // Act
            var executeTask = service.RunExecuteAsync(cts.Token);

            // Assert: exits gracefully on cancellation without unhandled exceptions
            var completedTask = await Task.WhenAny(executeTask, Task.Delay(2000));
            Assert.Same(executeTask, completedTask);
            await executeTask;
        }

        [Fact]
        public async Task ExecuteAsync_WhenDemoModeIsTrue_ExitsGracefullyWhenCancelledDuringDelay()
        {
            // Arrange
            var configDict = new Dictionary<string, string?>
            {
                { "Spokes_DemoMode", "true" },
                { "DataPath", _testDataPath }
            };
            var config = new ConfigurationBuilder().AddInMemoryCollection(configDict).Build();
            var loggerMock = new Mock<ILogger<DemoResetService>>();
            var serviceProviderMock = new Mock<IServiceProvider>();

            var service = new TestableDemoResetService(loggerMock.Object, serviceProviderMock.Object, config);

            using var cts = new CancellationTokenSource();
            cts.CancelAfter(50); // Cancels while Task.Delay is awaiting

            // Act
            var executeTask = service.RunExecuteAsync(cts.Token);

            // Assert: cancels gracefully
            var completedTask = await Task.WhenAny(executeTask, Task.Delay(2000));
            Assert.Same(executeTask, completedTask);
            await executeTask;
        }

        [Fact]
        public void Constructor_DefaultDataPath_WhenConfigMissingDataPath()
        {
            // Arrange
            var configDict = new Dictionary<string, string?>();
            var config = new ConfigurationBuilder().AddInMemoryCollection(configDict).Build();
            var loggerMock = new Mock<ILogger<DemoResetService>>();
            var serviceProviderMock = new Mock<IServiceProvider>();

            // Act
            var service = new TestableDemoResetService(loggerMock.Object, serviceProviderMock.Object, config);

            // Assert
            Assert.NotNull(service);
            Assert.Equal("Data", service.GetDataPath());
        }

        [Fact]
        public void Constructor_CustomDataPath_WhenConfigProvidesDataPath()
        {
            // Arrange
            var configDict = new Dictionary<string, string?>
            {
                { "DataPath", _testDataPath }
            };
            var config = new ConfigurationBuilder().AddInMemoryCollection(configDict).Build();
            var loggerMock = new Mock<ILogger<DemoResetService>>();
            var serviceProviderMock = new Mock<IServiceProvider>();

            // Act
            var service = new TestableDemoResetService(loggerMock.Object, serviceProviderMock.Object, config);

            // Assert
            Assert.NotNull(service);
            Assert.Equal(_testDataPath, service.GetDataPath());
        }
    }
