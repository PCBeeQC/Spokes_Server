using System.Reflection;
using System.Threading;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using Spokes_Server.Aggregate;
using Spokes_Server.Core.Services.Core;
using Spokes_Server.Core.Services.Licensing;
using Spokes_Server.Tests;

namespace Spokes_Server.Tests.Core.Services.Core;

public class ServerUpdateServiceTests : TestDataTestBase
    {
        private readonly ServiceProvider _serviceProvider;
        private readonly Database _db;
        private readonly EncryptionService _encryptionService;
        private readonly Mock<ILogger<ServerUpdateService>> _mockLogger;
        private readonly Mock<ILogger<LicenseValidationService>> _mockLicenseLogger;

        public ServerUpdateServiceTests()
        {
            var services = new ServiceCollection();

            var mockConfig = new Mock<IConfiguration>();
            mockConfig.Setup(c => c["DataPath"]).Returns(_testDataPath);
            services.AddSingleton<IConfiguration>(mockConfig.Object);

            services.AddLogging(builder => builder.AddConsole());
            services.AddSingleton<EncryptionService>();
            services.AddSpokesDatabase();

            _serviceProvider = services.BuildServiceProvider();
            _db = _serviceProvider.GetRequiredService<Database>();
            _encryptionService = _serviceProvider.GetRequiredService<EncryptionService>();

            _mockLogger = new Mock<ILogger<ServerUpdateService>>();
            _mockLicenseLogger = new Mock<ILogger<LicenseValidationService>>();
        }

        public override void Dispose()
        {
            _serviceProvider.Dispose();
            base.Dispose();
        }

        public class TestableServerUpdateService : ServerUpdateService
        {
            public TestableServerUpdateService(
                ILogger<ServerUpdateService> logger,
                Database db,
                LicenseValidationService license,
                VersionMetadata version)
                : base(logger, db, license, version) { }

            public Task RunExecuteAsync(CancellationToken token) => ExecuteAsync(token);
        }

        private LicenseValidationService CreateLicenseService(VersionMetadata version)
        {
            return new LicenseValidationService(_mockLicenseLogger.Object, _encryptionService, version);
        }

        private TestableServerUpdateService CreateService(VersionMetadata? version = null)
        {
            var ver = version ?? new VersionMetadata("dev");
            var license = CreateLicenseService(ver);
            return new TestableServerUpdateService(_mockLogger.Object, _db, license, ver);
        }

        #region Constructor Tests

        [Fact]
        public void Constructor_SetsProperties()
        {
            var version = new VersionMetadata("server-v2026.9.16");
            var license = CreateLicenseService(version);

            var service = new ServerUpdateService(_mockLogger.Object, _db, license, version);

            Assert.Equal(LicenseValidationService.AppVersion, service.CurrentVersion);
            Assert.Equal("server", service.Channel);
            Assert.Null(service.LatestVersion);
            Assert.False(service.IsUpdateAvailable);
        }

        [Fact]
        public void TestableSubclass_SetsProperties()
        {
            var version = new VersionMetadata("server-v2026.9.16");
            var service = CreateService(version);

            Assert.Equal(LicenseValidationService.AppVersion, service.CurrentVersion);
            Assert.Equal("server", service.Channel);
            Assert.Null(service.LatestVersion);
            Assert.False(service.IsUpdateAvailable);
        }

        [Theory]
        [InlineData("test-v2026.1.0", "test")]
        [InlineData("beta-v2026.1.0", "beta")]
        [InlineData("server-v2026.1.0", "server")]
        [InlineData("dev", "unknown")]
        [InlineData("", "unknown")]
        public void Constructor_InitializesChannelFromVersionMetadata(string rawVersion, string expectedChannel)
        {
            var version = new VersionMetadata(rawVersion);
            var service = CreateService(version);

            Assert.Equal(expectedChannel, service.Channel);
        }

        #endregion

        #region ExecuteAsync Tests

        [Fact]
        public async Task ExecuteAsync_WhenChannelIsUnknown_ReturnsImmediately()
        {
            var version = new VersionMetadata("dev");
            Assert.Equal("unknown", version.Channel);

            var service = CreateService(version);

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var executeTask = service.RunExecuteAsync(cts.Token);

            var completedTask = await Task.WhenAny(executeTask, Task.Delay(2000, cts.Token));
            Assert.Same(executeTask, completedTask);
            await executeTask;

            Assert.True(executeTask.IsCompletedSuccessfully);
        }

        [Fact]
        public async Task ExecuteAsync_WhenChannelIsKnown_ExitsGracefullyOnCancellation()
        {
            var version = new VersionMetadata("server-v2026.1.0");
            // Set Channel to "stable" via reflection to verify known channel handling explicitly
            var field = typeof(VersionMetadata).GetField("<Channel>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic);
            field?.SetValue(version, "stable");

            Assert.Equal("stable", version.Channel);

            var service = CreateService(version);

            using var cts = new CancellationTokenSource();
            cts.Cancel();

            var executeTask = service.RunExecuteAsync(cts.Token);
            await executeTask;

            Assert.True(executeTask.IsCompleted);
        }

        [Theory]
        [InlineData("server-v2026.1.0", "server")]
        [InlineData("beta-v2026.1.0", "beta")]
        [InlineData("test-v2026.1.0", "test")]
        public async Task ExecuteAsync_WhenChannelIsKnownChannel_ExitsGracefullyOnCancellation(string rawVersion, string expectedChannel)
        {
            var version = new VersionMetadata(rawVersion);
            Assert.Equal(expectedChannel, version.Channel);

            var service = CreateService(version);

            using var cts = new CancellationTokenSource();
            cts.Cancel();

            var executeTask = service.RunExecuteAsync(cts.Token);
            await executeTask;

            Assert.True(executeTask.IsCompleted);
        }

        #endregion

        #region CheckForUpdatesAsync Tests

        [Fact]
        public async Task CheckForUpdatesAsync_WhenChannelIsUnknown_ReturnsImmediately()
        {
            var version = new VersionMetadata("dev");
            Assert.Equal("unknown", version.Channel);

            var service = CreateService(version);

            var exception = await Record.ExceptionAsync(() => service.CheckForUpdatesAsync());
            Assert.Null(exception);

            Assert.Null(service.LatestVersion);
            Assert.False(service.IsUpdateAvailable);
        }

        [Fact]
        public async Task CheckForUpdatesAsync_WhenChannelIsKnownAndCancelled_ThrowsOperationCanceledException()
        {
            var version = new VersionMetadata("server-v2026.1.0");
            var service = CreateService(version);

            using var cts = new CancellationTokenSource();
            cts.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.CheckForUpdatesAsync(cts.Token));
        }

        #endregion

        #region TriggerWatchtowerUpdateAsync Tests

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public async Task TriggerWatchtowerUpdateAsync_WhenTokenNotConfigured_ReturnsSafely(string? tokenValue)
        {
            var originalToken = Environment.GetEnvironmentVariable("WATCHTOWER_HTTP_API_TOKEN");
            try
            {
                Environment.SetEnvironmentVariable("WATCHTOWER_HTTP_API_TOKEN", tokenValue);
                var service = CreateService();

                var exception = await Record.ExceptionAsync(() => service.TriggerWatchtowerUpdateAsync());
                Assert.Null(exception);
            }
            finally
            {
                Environment.SetEnvironmentVariable("WATCHTOWER_HTTP_API_TOKEN", originalToken);
            }
        }

        #endregion

        #region Events Tests

        [Fact]
        public void OnUpdateAvailable_CanSubscribeAndUnsubscribe()
        {
            var service = CreateService();
            bool eventFired = false;
            Action handler = () => eventFired = true;

            service.OnUpdateAvailable += handler;
            service.OnUpdateAvailable -= handler;

            Assert.False(eventFired);
        }

        #endregion
    }
