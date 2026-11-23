using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using Spokes_Server.Core.Configuration;
using Spokes_Server.Core.Data;
using Spokes_Server.Core.Data.Repositories.Core;
using Spokes_Server.Core.Services.Licensing;
using Xunit;

namespace Spokes_Server.Tests.Core.Services.Licensing;

public sealed class SpokesRelayClientTests : IDisposable
{
    private readonly string _testDir;
    private readonly IConfiguration _config;
    private readonly ServerConfigRepository _serverConfigs;
    private readonly string? _originalEnvRelayKey;

    public SpokesRelayClientTests()
    {
        _originalEnvRelayKey = Environment.GetEnvironmentVariable("SPOKES_RELAY_KEY");
        _testDir = Path.Combine(Path.GetTempPath(), $"Spokes_RelayClient_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testDir);

        _config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DataPath"] = _testDir,
                ["SPOKES_MASTER_PASSWORD"] = "UnitTest-Master-Password-2026!"
            })
            .Build();

        var persistence = new DiskPersistenceService(Mock.Of<ILogger<DiskPersistenceService>>());
        _serverConfigs = new ServerConfigRepository(persistence, _config);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("SPOKES_RELAY_KEY", _originalEnvRelayKey);
        try
        {
            if (Directory.Exists(_testDir))
            {
                Directory.Delete(_testDir, true);
            }
        }
        catch { }
    }

    [Fact]
    public async Task PostJsonAsync_AlwaysAttachesRelayKeyHeader_FromOptionsOrConfig()
    {
        HttpRequestMessage? captured = null;
        var handler = new StubHandler((req, _) =>
        {
            captured = req;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"authorized\":true}", Encoding.UTF8, "application/json")
            });
        });

        var factory = CreateFactory(handler);
        var options = Options.Create(new SpokesOptions { PushRelayKey = "relay-secret-abc" });
        var client = new SpokesRelayClient(factory, options, _config);

        using var resp = await client.PostJsonAsync("https://push.spokes.sh/api/relay-cast/issue-ticket", new { test = true });

        Assert.True(resp.IsSuccessStatusCode);
        Assert.NotNull(captured);
        Assert.True(captured!.Headers.TryGetValues(SpokesRelayClient.RelayKeyHeaderName, out var vals));
        Assert.Equal("relay-secret-abc", vals.Single());
    }

    [Fact]
    public void ResolveUrlsAndKey_HonorsOverridesAndCanonicalOrigin()
    {
        var cfg = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Spokes:PushRelayUrl"] = "https://relay.custom.org:8443/api/relay-push",
                ["Spokes:CastRelayIssueTicketUrl"] = "https://cast.override.org/custom-issue",
                ["SPOKES_RELAY_KEY"] = "cfg-key-123"
            })
            .Build();

        var client = new SpokesRelayClient(Mock.Of<IHttpClientFactory>(), options: null, configuration: cfg);

        Assert.Equal("https://relay.custom.org:8443", client.ResolveBaseOrigin());
        Assert.Equal("https://cast.override.org/custom-issue", client.ResolveCastIssueTicketUrl());
        Assert.Equal("https://relay.custom.org:8443/api/relay-backup/google-token", client.ResolveEndpointUrl("/api/relay-backup/google-token"));
        Assert.Equal("cfg-key-123", client.ResolveRelayKey());
    }

    [Fact]
    public async Task PostJsonAsync_PersistsTrialTicketFromExtensionsOn403()
    {
        var serverConfig = _serverConfigs.GetOrCreateGlobalConfig();
        using var rsa = RSA.Create(2048);
        serverConfig.ServerMasterRsaPublicKey = rsa.ToXmlString(false);
        _serverConfigs.Save(serverConfig);
        string fp = RelayTrialTicketService.ComputeFingerprint(serverConfig.ServerMasterRsaPublicKey)!;

        var problemJson = JsonSerializer.Serialize(new
        {
            title = "Forbidden",
            status = 403,
            detail = "Trial Expired: upgrade at spokes.sh",
            extensions = new
            {
                code = "TRIAL_EXPIRED",
                trialTicket = new RelayTrialTicket
                {
                    Fingerprint = fp,
                    IssuedAt = "2026-09-01T00:00:00Z",
                    ExpiresAt = "2026-11-01T00:00:00Z",
                    Signature = "ext-sig-1"
                }
            }
        });

        var handler = new StubHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden)
        {
            Content = new StringContent(problemJson, Encoding.UTF8, "application/problem+json")
        }));

        var client = new SpokesRelayClient(
            CreateFactory(handler),
            Options.Create(new SpokesOptions { PushRelayKey = "k" }),
            _config,
            new RelayTrialTicketService(_serverConfigs));

        using var resp = await client.PostJsonAsync("https://push.spokes.sh/api/relay-cast/issue-ticket", new { serverId = "s1" });

        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
        Assert.Equal("TRIAL_EXPIRED", resp.Code);
        Assert.Equal("Trial Expired: upgrade at spokes.sh", resp.Detail);
        Assert.Equal("ext-sig-1", _serverConfigs.GetOrCreateGlobalConfig().RelayTrialTicket?.Signature);
    }

    [Fact]
    public async Task PostJsonAsync_NonJsonHtml403Block_ReturnsCleanResponseWithoutThrowing()
    {
        const string html = "<!DOCTYPE html><html><head><title>Attention Required! | Cloudflare</title></head><body>Blocked</body></html>";
        var handler = new StubHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden)
        {
            Content = new StringContent(html, Encoding.UTF8, "text/html")
        }));

        var client = new SpokesRelayClient(CreateFactory(handler), Options.Create(new SpokesOptions()));

        using var resp = await client.PostJsonAsync("https://push.spokes.sh/api/relay-cast/issue-ticket", new { serverId = "s1" });

        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
        Assert.False(resp.IsSuccessStatusCode);
        Assert.Equal("text/html", resp.ContentType);
        Assert.Null(resp.Json);
        Assert.Null(resp.Code);
        Assert.Null(resp.Detail);
        Assert.Contains("Cloudflare", resp.RawBody, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("TRIAL_EXPIRED", true)]
    [InlineData("LICENSE_EXPIRED", true)]
    [InlineData("LICENSE_INVALID", true)]
    [InlineData("trial_expired", true)]
    [InlineData("CAST_TICKET_INVALID", false)]
    [InlineData("RELAY_AUTH_FAILED", false)]
    [InlineData(null, false)]
    [InlineData("", false)]
    public void IsLicenseProblemCode_ClassifiesCorrectly(string? code, bool expected)
    {
        Assert.Equal(expected, SpokesRelayClient.IsLicenseProblemCode(code));
    }

    private static IHttpClientFactory CreateFactory(HttpMessageHandler handler)
    {
        var mock = new Mock<IHttpClientFactory>();
        mock.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(() => new HttpClient(handler, disposeHandler: false));
        return mock.Object;
    }

    private sealed class StubHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => responder(request, cancellationToken);
    }
}
