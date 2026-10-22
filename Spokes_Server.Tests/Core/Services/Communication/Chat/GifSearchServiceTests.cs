using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using Moq.Protected;
using Spokes_Server.Core.Configuration;
using Spokes_Server.Core.Data;
using Spokes_Server.Core.Data.Repositories.Core;
using Spokes_Server.Core.Models.Core;
using Spokes_Server.Core.Services.Communication.Chat;
using Spokes_Server.Core.Services.Storage;
using Xunit;

namespace Spokes_Server.Tests.Core.Services.Communication.Chat;

public class GifSearchServiceTests : IDisposable
{
    private readonly string _tempDir;
    private readonly CompanyProfileRepository _profileRepo;
    private readonly ServerConfigRepository _serverConfigs;
    private readonly Mock<ILogger<GifSearchService>> _loggerMock;

    public GifSearchServiceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "Spokes_Gif_Test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);

        var configMock = new Mock<IConfiguration>();
        configMock.Setup(c => c["DataPath"]).Returns(_tempDir);

        var persistence = new Mock<DiskPersistenceService>(Mock.Of<ILogger<DiskPersistenceService>>());
        _profileRepo = new CompanyProfileRepository(persistence.Object, configMock.Object);
        _serverConfigs = new ServerConfigRepository(persistence.Object, configMock.Object);
        _loggerMock = new Mock<ILogger<GifSearchService>>();
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, true);
            }
        }
        catch { }
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public async Task SearchGifsAsync_WhitespaceOrNull_ReturnsEmptyResults(string? query)
    {
        var mockFactory = new Mock<IHttpClientFactory>();
        var options = Options.Create(new SpokesOptions());
        var service = new GifSearchService(mockFactory.Object, options, _profileRepo, _serverConfigs, _loggerMock.Object);

        var result = await service.SearchGifsAsync(query!);

        Assert.Empty(result.Results);
        Assert.False(result.HasMore);
        Assert.Null(result.ErrorMessage);
    }

    [Fact]
    public async Task SearchGifsAsync_ProviderNone_ReturnsConfigurationError()
    {
        var profile = _profileRepo.Get();
        profile.GifProvider = "None";
        _profileRepo.Save(profile);

        var mockFactory = new Mock<IHttpClientFactory>();
        var options = Options.Create(new SpokesOptions());
        var service = new GifSearchService(mockFactory.Object, options, _profileRepo, _serverConfigs, _loggerMock.Object);

        var result = await service.SearchGifsAsync("cat");

        Assert.Empty(result.Results);
        Assert.Equal("GIF provider not configured.", result.ErrorMessage);
    }

    [Fact]
    public async Task SearchGifsAsync_CustomProviderWithoutKey_ReturnsKeyRequiredError()
    {
        var profile = _profileRepo.Get();
        profile.GifProvider = "Klipy";
        profile.GifApiKey = "";
        _profileRepo.Save(profile);

        var mockFactory = new Mock<IHttpClientFactory>();
        var options = Options.Create(new SpokesOptions());
        var service = new GifSearchService(mockFactory.Object, options, _profileRepo, _serverConfigs, _loggerMock.Object);

        var result = await service.SearchGifsAsync("party");

        Assert.Empty(result.Results);
        Assert.Equal("GIF search is not available. No API key configured.", result.ErrorMessage);
    }

    [Fact]
    public async Task SearchGifsAsync_SpokesDefaultProvider_CallsRelayEndpointWithHeadersAndFingerprint()
    {
        var profile = _profileRepo.Get();
        profile.GifProvider = "Spokes Default Provider";
        _profileRepo.Save(profile);

        // Generate RSA key for fingerprint
        using var rsa = RSA.Create(2048);
        var config = _serverConfigs.GetOrCreateGlobalConfig();
        config.ServerMasterRsaPublicKey = rsa.ToXmlString(false);
        _serverConfigs.Save(config);

        HttpRequestMessage? capturedRequest = null;
        string? capturedBody = null;
        string klipyJson = @"{
            ""data"": [
                {
                    ""file"": {
                        ""hd"": { ""gif"": { ""url"": ""https://cdn.klipy.com/hd.gif"" } },
                        ""preview"": { ""gif"": { ""url"": ""https://cdn.klipy.com/prev.gif"", ""width"": 200, ""height"": 100 } }
                    }
                }
            ]
        }";

        var mockHandler = new Mock<HttpMessageHandler>();
        mockHandler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync", ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>())
            .Callback<HttpRequestMessage, CancellationToken>((r, _) =>
            {
                capturedRequest = r;
                if (r.Content != null)
                {
                    capturedBody = r.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                }
            })
            .ReturnsAsync(new HttpResponseMessage
            {
                StatusCode = HttpStatusCode.OK,
                Content = new StringContent(klipyJson, System.Text.Encoding.UTF8, "application/json")
            });

        var client = new HttpClient(mockHandler.Object);
        var mockFactory = new Mock<IHttpClientFactory>();
        mockFactory.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(client);

        var options = Options.Create(new SpokesOptions
        {
            GifRelayUrl = "https://push.spokes.sh/api/relay-gif",
            PushRelayKey = "test-relay-key-xyz"
        });

        var service = new GifSearchService(mockFactory.Object, options, _profileRepo, _serverConfigs, _loggerMock.Object);

        var result = await service.SearchGifsAsync("celebrate");

        Assert.NotNull(capturedRequest);
        Assert.Equal(HttpMethod.Post, capturedRequest.Method);
        Assert.Equal(new Uri("https://push.spokes.sh/api/relay-gif"), capturedRequest.RequestUri);
        Assert.True(capturedRequest.Headers.Contains("X-Relay-Key"));
        Assert.Equal("test-relay-key-xyz", capturedRequest.Headers.GetValues("X-Relay-Key").First());

        Assert.NotNull(capturedBody);
        using var jsonDoc = JsonDocument.Parse(capturedBody);
        var root = jsonDoc.RootElement;
        var queryVal = root.TryGetProperty("query", out var q) ? q.GetString() : root.GetProperty("Query").GetString();
        var pageVal = root.TryGetProperty("page", out var p) ? p.GetInt32() : root.GetProperty("Page").GetInt32();
        var fpVal = root.TryGetProperty("serverFingerprint", out var f) ? f.GetString() : root.GetProperty("ServerFingerprint").GetString();
        var serverIdVal = root.TryGetProperty("serverId", out var sid) ? sid.GetString() : root.GetProperty("ServerId").GetString();

        Assert.Equal("celebrate", queryVal);
        Assert.Equal(1, pageVal);
        Assert.False(string.IsNullOrEmpty(fpVal));
        Assert.False(string.IsNullOrEmpty(serverIdVal));

        Assert.Single(result.Results);
        Assert.Equal("https://cdn.klipy.com/hd.gif", result.Results[0].Url);
        Assert.Equal("https://cdn.klipy.com/prev.gif", result.Results[0].PreviewUrl);
        Assert.Equal(2.0, result.Results[0].AspectRatio);
    }

    [Fact]
    public async Task SearchGifsAsync_RelayReturns429_ReturnsRateLimitMessage()
    {
        var profile = _profileRepo.Get();
        profile.GifProvider = "Spokes Default Provider";
        _profileRepo.Save(profile);

        var mockHandler = new Mock<HttpMessageHandler>();
        mockHandler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync", ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage
            {
                StatusCode = HttpStatusCode.TooManyRequests
            });

        var client = new HttpClient(mockHandler.Object);
        var mockFactory = new Mock<IHttpClientFactory>();
        mockFactory.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(client);

        var options = Options.Create(new SpokesOptions());
        var service = new GifSearchService(mockFactory.Object, options, _profileRepo, _serverConfigs, _loggerMock.Object);

        var result = await service.SearchGifsAsync("party");

        Assert.Empty(result.Results);
        Assert.Equal("GIF search rate limit reached. Please wait a moment.", result.ErrorMessage);
    }

    [Fact]
    public void ParseKlipyJson_ValidJson_CalculatesAspectRatiosCorrectly()
    {
        string json = @"{
            ""data"": [
                {
                    ""file"": {
                        ""hd"": { ""gif"": { ""url"": ""https://cdn.klipy.com/1.gif"" } },
                        ""preview"": { ""gif"": { ""url"": ""https://cdn.klipy.com/1_p.gif"", ""width"": 400, ""height"": 200 } }
                    }
                },
                {
                    ""files"": {
                        ""hd"": { ""url"": ""https://cdn.klipy.com/2.gif"" },
                        ""preview"": { ""url"": ""https://cdn.klipy.com/2_p.gif"", ""width"": 100, ""height"": 100 } }
                }
            ]
        }";

        var result = GifSearchService.ParseKlipyJson(json, 0);

        Assert.Equal(2, result.Results.Count);
        Assert.Equal(2.0, result.Results[0].AspectRatio);
        Assert.Equal(1.0, result.Results[1].AspectRatio);
    }

    [Fact]
    public void ParseTenorJson_ValidJson_ExtractsUrlsAndNextCursor()
    {
        string json = @"{
            ""next"": ""pos_cursor_123"",
            ""results"": [
                {
                    ""media_formats"": {
                        ""gif"": { ""url"": ""https://tenor.com/view/1.gif"" },
                        ""tinygif"": { ""url"": ""https://tenor.com/view/1_tiny.gif"", ""dims"": [300, 150] }
                    }
                }
            ]
        }";

        var result = GifSearchService.ParseTenorJson(json, 0);

        Assert.Single(result.Results);
        Assert.Equal("https://tenor.com/view/1.gif", result.Results[0].Url);
        Assert.Equal("https://tenor.com/view/1_tiny.gif", result.Results[0].PreviewUrl);
        Assert.Equal(2.0, result.Results[0].AspectRatio);
        Assert.True(result.HasMore);
        Assert.Equal("pos_cursor_123", result.NextCursor);
    }

    [Fact]
    public void ParseGiphyJson_ValidJson_ExtractsUrlsAndPagination()
    {
        string json = @"{
            ""pagination"": { ""count"": 20, ""offset"": 0 },
            ""data"": [
                {
                    ""images"": {
                        ""original"": { ""url"": ""https://giphy.com/orig.gif"", ""width"": ""200"", ""height"": ""200"" },
                        ""fixed_height_small"": { ""url"": ""https://giphy.com/prev.gif"", ""width"": ""100"", ""height"": ""100"" }
                    }
                }
            ]
        }";

        var result = GifSearchService.ParseGiphyJson(json, 0);

        Assert.Single(result.Results);
        Assert.Equal("https://giphy.com/orig.gif", result.Results[0].Url);
        Assert.Equal("https://giphy.com/prev.gif", result.Results[0].PreviewUrl);
        Assert.Equal(1.0, result.Results[0].AspectRatio);
        Assert.True(result.HasMore);
        Assert.Equal(20, result.NewOffset);
    }

    [Fact]
    public async Task SearchGifsAsync_MultipleWhitespace_NormalizesQueryBeforeSending()
    {
        var profile = _profileRepo.Get();
        profile.GifProvider = "Spokes Default Provider";
        _profileRepo.Save(profile);

        using var rsa = RSA.Create(2048);
        var config = _serverConfigs.GetOrCreateGlobalConfig();
        config.ServerMasterRsaPublicKey = rsa.ToXmlString(false);
        _serverConfigs.Save(config);

        string? capturedBody = null;
        string klipyJson = @"{ ""data"": [] }";

        var mockHandler = new Mock<HttpMessageHandler>();
        mockHandler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync", ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>())
            .Callback<HttpRequestMessage, CancellationToken>((r, _) =>
            {
                if (r.Content != null)
                {
                    capturedBody = r.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                }
            })
            .ReturnsAsync(new HttpResponseMessage
            {
                StatusCode = HttpStatusCode.OK,
                Content = new StringContent(klipyJson, System.Text.Encoding.UTF8, "application/json")
            });

        var client = new HttpClient(mockHandler.Object);
        var mockFactory = new Mock<IHttpClientFactory>();
        mockFactory.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(client);

        var options = Options.Create(new SpokesOptions
        {
            GifRelayUrl = "https://push.spokes.sh/api/relay-gif"
        });

        var service = new GifSearchService(mockFactory.Object, options, _profileRepo, _serverConfigs, _loggerMock.Object);

        await service.SearchGifsAsync("  around    the    corner  ");

        Assert.NotNull(capturedBody);
        using var jsonDoc = JsonDocument.Parse(capturedBody);
        var root = jsonDoc.RootElement;
        var queryVal = root.TryGetProperty("query", out var q) ? q.GetString() : root.GetProperty("Query").GetString();

        Assert.Equal("around the corner", queryVal);
    }
}
