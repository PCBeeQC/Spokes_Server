using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Caching.Memory;
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
using Spokes_Server.Core.Services.Licensing;
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
        configMock.Setup(c => c["SPOKES_MASTER_PASSWORD"]).Returns("UnitTest-Master-Password-2026!");

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
        var service = new GifSearchService(mockFactory.Object, new SpokesRelayClient(mockFactory.Object, options), _profileRepo, _serverConfigs, _loggerMock.Object);

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
        var service = new GifSearchService(mockFactory.Object, new SpokesRelayClient(mockFactory.Object, options), _profileRepo, _serverConfigs, _loggerMock.Object);

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
        var service = new GifSearchService(mockFactory.Object, new SpokesRelayClient(mockFactory.Object, options), _profileRepo, _serverConfigs, _loggerMock.Object);

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

        var service = new GifSearchService(mockFactory.Object, new SpokesRelayClient(mockFactory.Object, options), _profileRepo, _serverConfigs, _loggerMock.Object);

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
        var service = new GifSearchService(mockFactory.Object, new SpokesRelayClient(mockFactory.Object, options), _profileRepo, _serverConfigs, _loggerMock.Object);

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

        var service = new GifSearchService(mockFactory.Object, new SpokesRelayClient(mockFactory.Object, options), _profileRepo, _serverConfigs, _loggerMock.Object);

        await service.SearchGifsAsync("  around    the    corner  ");

        Assert.NotNull(capturedBody);
        using var jsonDoc = JsonDocument.Parse(capturedBody);
        var root = jsonDoc.RootElement;
        var queryVal = root.TryGetProperty("query", out var q) ? q.GetString() : root.GetProperty("Query").GetString();

        Assert.Equal("around the corner", queryVal);
    }

    [Fact]
    public void ParseKlipyJson_RealV1Schema_PrefersSmWebpPreviewHdWebpFullSkipsAdsAndHonorsHasNext()
    {
        string json = @"{
            ""result"": true,
            ""data"": {
                ""current_page"": 2,
                ""per_page"": 20,
                ""has_next"": false,
                ""data"": [
                    {
                        ""id"": ""ad-1"",
                        ""type"": ""ad"",
                        ""file"": {
                            ""hd"": { ""webp"": { ""url"": ""https://static.klipy.com/ad.webp"", ""width"": 480, ""height"": 240 } }
                        }
                    },
                    {
                        ""id"": ""gif-1"",
                        ""type"": ""gif"",
                        ""file"": {
                            ""hd"": {
                                ""gif"": { ""url"": ""https://static.klipy.com/1/hd.gif"", ""width"": 480, ""height"": 240 },
                                ""webp"": { ""url"": ""https://static.klipy.com/1/hd.webp"", ""width"": 480, ""height"": 240 }
                            },
                            ""md"": {
                                ""webp"": { ""url"": ""https://static.klipy.com/1/md.webp"", ""width"": 320, ""height"": 160 }
                            },
                            ""sm"": {
                                ""gif"": { ""url"": ""https://static.klipy.com/1/sm.gif"", ""width"": 220, ""height"": 110 },
                                ""webp"": { ""url"": ""https://static.klipy.com/1/sm.webp"", ""width"": 220, ""height"": 110 }
                            }
                        }
                    },
                    {
                        ""id"": ""gif-2"",
                        ""file"": {
                            ""md"": {
                                ""gif"": { ""url"": ""https://static.klipy.com/2/md.gif"", ""width"": 300, ""height"": 300 }
                            },
                            ""xs"": {
                                ""webp"": { ""url"": ""https://static.klipy.com/2/xs.webp"", ""width"": 120, ""height"": 120 }
                            }
                        }
                    }
                ]
            }
        }";

        var result = GifSearchService.ParseKlipyJson(json, currentOffset: 20, currentPage: 2);

        Assert.Equal(2, result.Results.Count);
        Assert.Equal("https://static.klipy.com/1/hd.webp", result.Results[0].Url);
        Assert.Equal("https://static.klipy.com/1/sm.webp", result.Results[0].PreviewUrl);
        Assert.Equal(2.0, result.Results[0].AspectRatio);

        // Falls back cleanly when hd is absent
        Assert.Equal("https://static.klipy.com/2/md.gif", result.Results[1].Url);
        Assert.Equal("https://static.klipy.com/2/xs.webp", result.Results[1].PreviewUrl);
        Assert.Equal(1.0, result.Results[1].AspectRatio);

        // Honors explicit has_next=false instead of guessing from item count
        Assert.False(result.HasMore);
        Assert.Equal(string.Empty, result.NextCursor);
    }

    [Fact]
    public void ParseKlipyJson_HasNextTrue_AdvancesExplicitPageCursor()
    {
        string json = @"{
            ""result"": true,
            ""data"": {
                ""current_page"": 1,
                ""has_next"": true,
                ""data"": [
                    {
                        ""file"": {
                            ""hd"": { ""webp"": { ""url"": ""https://static.klipy.com/a/hd.webp"", ""width"": 400, ""height"": 200 } },
                            ""sm"": { ""webp"": { ""url"": ""https://static.klipy.com/a/sm.webp"", ""width"": 200, ""height"": 100 } }
                        }
                    }
                ]
            }
        }";

        var result = GifSearchService.ParseKlipyJson(json, currentOffset: 0, currentPage: 1);

        Assert.True(result.HasMore);
        Assert.Equal("2", result.NextCursor);
    }

    [Fact]
    public async Task SearchGifsAsync_CachesResultsInMemory_AndAvoidsDuplicateRelayCall()
    {
        var profile = _profileRepo.Get();
        profile.GifProvider = "Spokes Default Provider";
        _profileRepo.Save(profile);

        int calls = 0;
        const string klipyJson = @"{
            ""data"": {
                ""current_page"": 1,
                ""has_next"": true,
                ""data"": [
                    {
                        ""file"": {
                            ""hd"": { ""webp"": { ""url"": ""https://static.klipy.com/c/hd.webp"", ""width"": 300, ""height"": 300 } },
                            ""sm"": { ""webp"": { ""url"": ""https://static.klipy.com/c/sm.webp"", ""width"": 150, ""height"": 150 } }
                        }
                    }
                ]
            }
        }";

        var mockHandler = new Mock<HttpMessageHandler>();
        mockHandler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync", ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(() =>
            {
                Interlocked.Increment(ref calls);
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(klipyJson, System.Text.Encoding.UTF8, "application/json")
                };
            });

        var client = new HttpClient(mockHandler.Object);
        var mockFactory = new Mock<IHttpClientFactory>();
        mockFactory.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(client);

        using var memoryCache = new MemoryCache(new MemoryCacheOptions());
        var service = new GifSearchService(
            mockFactory.Object,
            new SpokesRelayClient(mockFactory.Object, Options.Create(new SpokesOptions())),
            _profileRepo,
            _serverConfigs,
            _loggerMock.Object,
            memoryCache,
            retryDelay: TimeSpan.Zero);

        var first = await service.SearchGifsAsync("happy");
        var second = await service.SearchGifsAsync("  HAPPY  ");

        Assert.Equal(1, calls);
        Assert.Single(first.Results);
        Assert.Single(second.Results);
        Assert.Equal("2", second.NextCursor);
    }

    [Fact]
    public async Task SearchGifsAsync_AppendReturns422_TreatsGracefullyAsEndOfResults()
    {
        var profile = _profileRepo.Get();
        profile.GifProvider = "Spokes Default Provider";
        _profileRepo.Save(profile);

        var mockHandler = new Mock<HttpMessageHandler>();
        mockHandler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync", ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.UnprocessableEntity));

        var client = new HttpClient(mockHandler.Object);
        var mockFactory = new Mock<IHttpClientFactory>();
        mockFactory.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(client);

        var service = new GifSearchService(
            mockFactory.Object,
            new SpokesRelayClient(mockFactory.Object, Options.Create(new SpokesOptions())),
            _profileRepo,
            _serverConfigs,
            _loggerMock.Object,
            retryDelay: TimeSpan.Zero);

        var result = await service.SearchGifsAsync("deep scroll", offset: 100, cursor: "6", append: true);

        Assert.Null(result.ErrorMessage);
        Assert.False(result.HasMore);
        Assert.Empty(result.Results);
    }

    [Fact]
    public async Task SearchGifsAsync_Transient504Then200_RetriesAndSucceeds()
    {
        var profile = _profileRepo.Get();
        profile.GifProvider = "Spokes Default Provider";
        _profileRepo.Save(profile);

        int calls = 0;
        const string okJson = @"{ ""data"": [ { ""file"": { ""hd"": { ""webp"": { ""url"": ""https://static.klipy.com/r/hd.webp"" } }, ""sm"": { ""webp"": { ""url"": ""https://static.klipy.com/r/sm.webp"" } } } } ] }";

        var mockHandler = new Mock<HttpMessageHandler>();
        mockHandler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync", ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(() =>
            {
                if (Interlocked.Increment(ref calls) == 1)
                {
                    return new HttpResponseMessage(HttpStatusCode.GatewayTimeout);
                }

                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(okJson, System.Text.Encoding.UTF8, "application/json")
                };
            });

        var client = new HttpClient(mockHandler.Object);
        var mockFactory = new Mock<IHttpClientFactory>();
        mockFactory.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(client);

        var service = new GifSearchService(
            mockFactory.Object,
            new SpokesRelayClient(mockFactory.Object, Options.Create(new SpokesOptions())),
            _profileRepo,
            _serverConfigs,
            _loggerMock.Object,
            retryDelay: TimeSpan.Zero);

        var result = await service.SearchGifsAsync("cold start");

        Assert.Equal(2, calls);
        Assert.Null(result.ErrorMessage);
        Assert.Single(result.Results);
    }

    [Fact]
    public async Task SearchGifsAsync_CallerCancelled_PropagatesOperationCanceledException()
    {
        var profile = _profileRepo.Get();
        profile.GifProvider = "Spokes Default Provider";
        _profileRepo.Save(profile);

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var mockFactory = new Mock<IHttpClientFactory>();
        var service = new GifSearchService(
            mockFactory.Object,
            new SpokesRelayClient(mockFactory.Object, Options.Create(new SpokesOptions())),
            _profileRepo,
            _serverConfigs,
            _loggerMock.Object,
            retryDelay: TimeSpan.Zero);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.SearchGifsAsync("cancelled", cancellationToken: cts.Token));
    }
}
