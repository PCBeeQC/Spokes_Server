using System.Net.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using Moq.Protected;
using Spokes_Server.Aggregate;
using Spokes_Server.Aggregate.Contexts;
using Spokes_Server.Core.Configuration;
using Spokes_Server.Core.Constants;
using Spokes_Server.Core.Services.Licensing;

namespace Spokes_Server.Tests.Core.Services.Licensing;

public class TelemetryBackgroundServiceTests : TestDataTestBase
{
        private readonly ServiceProvider _serviceProvider;
        private readonly Database _db;
        private readonly Mock<IHttpClientFactory> _mockHttpClientFactory;
        private readonly Mock<ILogger<TelemetryBackgroundService>> _mockLogger;

        public TelemetryBackgroundServiceTests()
        {
            var services = new ServiceCollection();

            var mockConfig = new Mock<IConfiguration>();
            mockConfig.Setup(c => c["DataPath"]).Returns(_testDataPath);
            mockConfig.Setup(c => c["SPOKES_MASTER_PASSWORD"]).Returns("UnitTest-Master-Password-2026!");
            services.AddSingleton<IConfiguration>(mockConfig.Object);

            services.AddLogging(builder => builder.AddConsole());
            services.AddSingleton<EncryptionService>();
            services.AddSpokesDatabase();

            _serviceProvider = services.BuildServiceProvider();
            _db = _serviceProvider.GetRequiredService<Database>();

            _mockHttpClientFactory = new Mock<IHttpClientFactory>();
            _mockLogger = new Mock<ILogger<TelemetryBackgroundService>>();
        }

        public override void Dispose()
        {
            _serviceProvider.Dispose();
            base.Dispose();
        }

        public class TestableTelemetryBackgroundService : TelemetryBackgroundService
        {
            public bool SkipInitialDelay { get; set; } = false;

            public TestableTelemetryBackgroundService(
                CoreContext core,
                HrContext hr,
                IHttpClientFactory clientFactory,
                ILogger<TelemetryBackgroundService> logger,
                IOptions<SpokesOptions>? options = null)
                : base(core, hr, clientFactory, logger, options) { }

            protected override Task WaitInitialDelayAsync(CancellationToken stoppingToken) =>
                SkipInitialDelay ? Task.CompletedTask : base.WaitInitialDelayAsync(stoppingToken);

            public Task RunExecuteAsync(CancellationToken token) => ExecuteAsync(token);
        }

        #region Execution and Cancellation Tests

        [Fact]
        public async Task ExecuteAsync_WithPreCancelledToken_CancelsCleanlyWithoutUnhandledException()
        {
            var service = new TestableTelemetryBackgroundService(
                _db.CoreContext,
                _db.HrContext,
                _mockHttpClientFactory.Object,
                _mockLogger.Object);

            using var cts = new CancellationTokenSource();
            cts.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.RunExecuteAsync(cts.Token));
        }

        [Fact]
        public async Task ExecuteAsync_WithTokenCancelledAfterBriefDelay_CancelsCleanlyWithoutUnhandledException()
        {
            var service = new TestableTelemetryBackgroundService(
                _db.CoreContext,
                _db.HrContext,
                _mockHttpClientFactory.Object,
                _mockLogger.Object);

            using var cts = new CancellationTokenSource();
            cts.CancelAfter(50);

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.RunExecuteAsync(cts.Token));
        }

        [Fact]
        public async Task StartAsync_And_StopAsync_LifecycleTerminatesCleanly()
        {
            var service = new TestableTelemetryBackgroundService(
                _db.CoreContext,
                _db.HrContext,
                _mockHttpClientFactory.Object,
                _mockLogger.Object);

            using var startCts = new CancellationTokenSource();
            await service.StartAsync(startCts.Token);

            Assert.NotNull(service.ExecuteTask);
            Assert.False(service.ExecuteTask.IsCompleted);

            using var stopCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await service.StopAsync(stopCts.Token);

            Assert.True(service.ExecuteTask.IsCompleted);
        }

        [Fact]
        public async Task ExecuteAsync_WhenEnabled_SendsRequestToConfiguredTelemetryRelayUrl()
        {
            var profile = _db.CoreContext.CompanyProfile.GetOrCreate();
            profile.SendUsageStatistics = true;
            _db.CoreContext.CompanyProfile.Save(profile);

            HttpRequestMessage? capturedRequest = null;
            var handlerMock = new Mock<HttpMessageHandler>();
            handlerMock
                .Protected()
                .Setup<Task<HttpResponseMessage>>(
                    "SendAsync",
                    ItExpr.IsAny<HttpRequestMessage>(),
                    ItExpr.IsAny<CancellationToken>())
                .Callback<HttpRequestMessage, CancellationToken>((req, _) => capturedRequest = req)
                .ReturnsAsync(new HttpResponseMessage(System.Net.HttpStatusCode.OK));

            var httpClient = new HttpClient(handlerMock.Object);
            _mockHttpClientFactory.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(httpClient);

            var customOptions = Options.Create(new SpokesOptions
            {
                TelemetryRelayUrl = "https://custom-relay.example.com/api/relay-telemetry"
            });

            var service = new TestableTelemetryBackgroundService(
                _db.CoreContext,
                _db.HrContext,
                _mockHttpClientFactory.Object,
                _mockLogger.Object,
                customOptions)
            {
                SkipInitialDelay = true
            };

            using var cts = new CancellationTokenSource();
            // Start the service and then cancel once the request is processed
            var runTask = service.RunExecuteAsync(cts.Token);
            
            // Wait briefly for the request to be captured
            var timeout = DateTime.UtcNow.AddSeconds(3);
            while (capturedRequest == null && DateTime.UtcNow < timeout)
            {
                await Task.Delay(20);
            }
            cts.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runTask);

            Assert.NotNull(capturedRequest);
            Assert.Equal(new Uri("https://custom-relay.example.com/api/relay-telemetry"), capturedRequest.RequestUri);
        }

        [Fact]
        public async Task ExecuteAsync_WhenOptionsNull_SendsRequestToDefaultSpokesConstantsTelemetryRelayUrl()
        {
            var profile = _db.CoreContext.CompanyProfile.GetOrCreate();
            profile.SendUsageStatistics = true;
            _db.CoreContext.CompanyProfile.Save(profile);

            HttpRequestMessage? capturedRequest = null;
            var handlerMock = new Mock<HttpMessageHandler>();
            handlerMock
                .Protected()
                .Setup<Task<HttpResponseMessage>>(
                    "SendAsync",
                    ItExpr.IsAny<HttpRequestMessage>(),
                    ItExpr.IsAny<CancellationToken>())
                .Callback<HttpRequestMessage, CancellationToken>((req, _) => capturedRequest = req)
                .ReturnsAsync(new HttpResponseMessage(System.Net.HttpStatusCode.OK));

            var httpClient = new HttpClient(handlerMock.Object);
            _mockHttpClientFactory.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(httpClient);

            var service = new TestableTelemetryBackgroundService(
                _db.CoreContext,
                _db.HrContext,
                _mockHttpClientFactory.Object,
                _mockLogger.Object,
                options: null)
            {
                SkipInitialDelay = true
            };

            using var cts = new CancellationTokenSource();
            var runTask = service.RunExecuteAsync(cts.Token);

            var timeout = DateTime.UtcNow.AddSeconds(3);
            while (capturedRequest == null && DateTime.UtcNow < timeout)
            {
                await Task.Delay(20);
            }
            cts.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runTask);

            Assert.NotNull(capturedRequest);
            Assert.Equal(new Uri(SpokesConstants.TelemetryRelayUrl), capturedRequest.RequestUri);
            Assert.Equal(new Uri("https://push.spokes.sh/api/relay-telemetry"), capturedRequest.RequestUri);
        }

        #endregion
    }
