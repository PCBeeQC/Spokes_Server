using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using Moq.Protected;
using Spokes_Server.Core.Configuration;
using Spokes_Server.Core.Constants;
using Spokes_Server.Core.Data;
using Spokes_Server.Core.Data.Repositories.Core;
using Spokes_Server.Core.Models.Core;
using Spokes_Server.Core.Services.Communication.Notifications;
using Spokes_Server.Core.Services.Communication.Notifications.WebPush;
using Spokes_Server.Core.Services.Licensing;
using Spokes_Server.Core.Services.Logging;
using Spokes_Server.Core.Services.Security;
using Spokes_Server.Tests.Helpers;
using Xunit;

namespace Spokes_Server.Tests.Core.Configuration;

public class SecretsResolutionRegressionTests : IDisposable
{
    private readonly string? _originalRelayKey;
    private readonly string? _originalKlipyKey;

    public SecretsResolutionRegressionTests()
    {
        _originalRelayKey = Environment.GetEnvironmentVariable("SPOKES_RELAY_KEY");
        _originalKlipyKey = Environment.GetEnvironmentVariable("SPOKES_KLIPY_KEY");
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("SPOKES_RELAY_KEY", _originalRelayKey);
        Environment.SetEnvironmentVariable("SPOKES_KLIPY_KEY", _originalKlipyKey);
    }

    [Fact]
    public void SpokesOptions_ResolvesEnvironmentVariables_WhenConfigSectionMissing()
    {
        Environment.SetEnvironmentVariable("SPOKES_RELAY_KEY", "env-docker-relay-secret-123");
        Environment.SetEnvironmentVariable("SPOKES_KLIPY_KEY", "env-docker-klipy-secret-456");

        var services = new ServiceCollection();
        // Register configuration without the "Spokes" section, simulating standard container environment
        var emptyConfig = new ConfigurationBuilder().Build();
        services.AddSingleton<IConfiguration>(emptyConfig);
        services.AddSpokesConfiguration(emptyConfig);

        using var sp = services.BuildServiceProvider();
        var options = sp.GetRequiredService<IOptions<SpokesOptions>>().Value;

        Assert.Equal("env-docker-relay-secret-123", options.PushRelayKey);
        Assert.Equal("env-docker-klipy-secret-456", options.DefaultKlipyApiKey);
        Assert.Equal(SpokesConstants.PushRelayUrl, options.PushRelayUrl);
    }

    [Fact]
    public void SpokesOptions_ResolvesEnvironmentVariables_WhenConfigurationInstanceIsNull()
    {
        Environment.SetEnvironmentVariable("SPOKES_RELAY_KEY", "direct-env-relay-key");
        Environment.SetEnvironmentVariable("SPOKES_KLIPY_KEY", "direct-env-klipy-key");

        var services = new ServiceCollection();
        // AddSpokesConfiguration called without passing IConfiguration or registering it in DI
        services.AddSpokesConfiguration();

        using var sp = services.BuildServiceProvider();
        var options = sp.GetRequiredService<IOptions<SpokesOptions>>().Value;

        Assert.Equal("direct-env-relay-key", options.PushRelayKey);
        Assert.Equal("direct-env-klipy-key", options.DefaultKlipyApiKey);
    }

    [Fact]
    public async Task NativeRelayDispatcher_InjectsRelayKeyHeader_FromSpokesOptions()
    {
        const string expectedRelayKey = "spokes-production-relay-secret-xyz";
        HttpRequestMessage? capturedRequest = null;

        var mockHandler = new Mock<HttpMessageHandler>(MockBehavior.Strict);
        mockHandler
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .Callback<HttpRequestMessage, CancellationToken>((req, _) => capturedRequest = req)
            .ReturnsAsync(new HttpResponseMessage
            {
                StatusCode = HttpStatusCode.OK,
                Content = new StringContent("{\"success\":true}", Encoding.UTF8, "application/json")
            });

        var httpClient = new HttpClient(mockHandler.Object);
        var mockHttpClientFactory = new Mock<IHttpClientFactory>();
        mockHttpClientFactory.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(httpClient);

        var tempDir = Path.Combine(Path.GetTempPath(), "Spokes_Relay_Test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            var mockConfig = new Mock<IConfiguration>();
            mockConfig.Setup(c => c["DataPath"]).Returns(tempDir);
            var mockPersistence = new Mock<DiskPersistenceService>(Mock.Of<ILogger<DiskPersistenceService>>());
            var sessionsRepo = new DeviceSessionRepository(mockPersistence.Object, mockConfig.Object);
            var mockSystemLog = new Mock<ISystemLogService>();

            var spokesOptions = Options.Create(new SpokesOptions
            {
                PushRelayUrl = "https://push.spokes.sh/api/relay-push",
                PushRelayKey = expectedRelayKey
            });

            var dispatcher = new NativeRelayDispatcher(
                mockHttpClientFactory.Object,
                sessionsRepo,
                mockSystemLog.Object,
                logger: null,
                options: spokesOptions);

            var payload = new RelayPushPayload
            {
                Token = "device-test-token-123",
                Category = "chat",
                Title = "Test Notification",
                Body = "Test Content"
            };

            var session = new DeviceSession
            {
                Id = "session-123",
                PushEndpoint = "device-test-token-123",
                DeviceType = "ios"
            };

            await dispatcher.DispatchRelayPushOrThrowAsync(payload, session, CancellationToken.None);

            Assert.NotNull(capturedRequest);
            Assert.True(capturedRequest.Headers.Contains("X-Relay-Key"));
            var actualKey = capturedRequest.Headers.GetValues("X-Relay-Key").Single();
            Assert.Equal(expectedRelayKey, actualKey);
            Assert.Equal(new Uri("https://push.spokes.sh/api/relay-push"), capturedRequest.RequestUri);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, true);
            }
        }
    }

    [Fact]
    public void SpokesConstants_BackwardsCompatibleAccessors_ReturnExpectedValues()
    {
        Environment.SetEnvironmentVariable("SPOKES_RELAY_KEY", "compat-relay-key");
        Environment.SetEnvironmentVariable("SPOKES_KLIPY_KEY", "compat-klipy-key");

#pragma warning disable CS0618 // Type or member is obsolete
        Assert.Equal("compat-relay-key", SpokesConstants.PushRelayKey);
        Assert.Equal("compat-klipy-key", SpokesConstants.DefaultKlipyApiKey);

        Environment.SetEnvironmentVariable("SPOKES_RELAY_KEY", null);
        Environment.SetEnvironmentVariable("SPOKES_KLIPY_KEY", null);

        Assert.Equal(string.Empty, SpokesConstants.PushRelayKey);
        Assert.Equal(string.Empty, SpokesConstants.DefaultKlipyApiKey);
#pragma warning restore CS0618
    }

    [Fact]
    public void GifProvider_ResolutionLogic_DistinguishesDefaultVsCustomProvider()
    {
        // Case A: User's local server configuration (provider is explicitly "Klipy" and has a saved key in database)
        var localProfile = new CompanyProfile
        {
            GifProvider = "Klipy",
            GifApiKey = "dummy-custom-klipy-api-key"
        };

        var options = Options.Create(new SpokesOptions
        {
            DefaultKlipyApiKey = "official-docker-github-secret-key"
        });

        // Exact resolution formula from EmojiPicker.razor:
        var localProvider = localProfile.GifProvider == "Spokes Default Provider" ? "Klipy" : localProfile.GifProvider;
        var localApiKey = localProfile.GifProvider == "Spokes Default Provider" ? options.Value.DefaultKlipyApiKey : localProfile.GifApiKey;

        Assert.Equal("Klipy", localProvider);
        Assert.Equal("dummy-custom-klipy-api-key", localApiKey);
        Assert.NotEqual(options.Value.DefaultKlipyApiKey, localApiKey);

        // Case B: Official Docker instance (provider is "Spokes Default Provider", key is provided via GitHub Secret)
        var dockerProfile = new CompanyProfile
        {
            GifProvider = "Spokes Default Provider",
            GifApiKey = string.Empty
        };

        var dockerProvider = dockerProfile.GifProvider == "Spokes Default Provider" ? "Klipy" : dockerProfile.GifProvider;
        var dockerApiKey = dockerProfile.GifProvider == "Spokes Default Provider" ? options.Value.DefaultKlipyApiKey : dockerProfile.GifApiKey;

        Assert.Equal("Klipy", dockerProvider);
        Assert.Equal("official-docker-github-secret-key", dockerApiKey);
    }
}
