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
using Spokes_Server.Core.Services.Communication.Chat;
using Spokes_Server.Core.Services.Licensing;
using Xunit;

namespace Spokes_Server.Tests.Core.Services.Communication.Chat;

public class SlideshowCastServiceTests : IDisposable
{
    private readonly string _testDataDir;
    private readonly IConfiguration _config;
    private readonly DiskPersistenceService _persistence;
    private readonly ServerConfigRepository _serverConfigs;
    private readonly CompanyProfileRepository _profileRepo;
    private readonly SystemConfigRepository _systemConfigs;

    public SlideshowCastServiceTests()
    {
        _testDataDir = Path.Combine(Path.GetTempPath(), $"Spokes_Test_Cast_{Guid.NewGuid()}");
        Directory.CreateDirectory(_testDataDir);

        var configDict = new Dictionary<string, string?>
        {
            ["DataPath"] = _testDataDir,
            ["SPOKES_MASTER_PASSWORD"] = "UnitTest-Master-Password-2026!",
            ["Spokes:GoogleCastAppId"] = "5AF5A068"
        };
        _config = new ConfigurationBuilder().AddInMemoryCollection(configDict).Build();
        _persistence = new DiskPersistenceService(new Mock<ILogger<DiskPersistenceService>>().Object);
        _serverConfigs = new ServerConfigRepository(_persistence, _config);
        _profileRepo = new CompanyProfileRepository(_persistence, _config);
        _systemConfigs = new SystemConfigRepository(_persistence, _config);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_testDataDir))
            {
                Directory.Delete(_testDataDir, true);
            }
        }
        catch { }
    }

    private SlideshowCastService CreateService(
        HttpMessageHandler? handler = null,
        IConfiguration? configOverride = null,
        SpokesOptions? options = null)
    {
        var effectiveConfig = configOverride ?? _config;
        var factoryMock = new Mock<IHttpClientFactory>();
        var effectiveHandler = handler ?? new StubHttpMessageHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"authorized\":true,\"castTicket\":\"ct1.default.sig\"}", Encoding.UTF8, "application/json")
            }));
        factoryMock.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(() => new HttpClient(effectiveHandler, disposeHandler: false));

        var relayClient = new SpokesRelayClient(
            factoryMock.Object,
            Options.Create(options ?? new SpokesOptions { PushRelayKey = "test-relay-key" }),
            effectiveConfig,
            new RelayTrialTicketService(_serverConfigs));

        return new SlideshowCastService(_serverConfigs, _profileRepo, _systemConfigs, effectiveConfig, relayClient);
    }

    [Fact]
    public void ResolveServerUrl_ReplacesLoopbackWithServerPublicUrl()
    {
        var sysConfig = _systemConfigs.Get();
        sysConfig.ServerPublicUrl = "https://spokes.family.org";
        _systemConfigs.Save(sysConfig);

        var service = CreateService();

        string resolved = service.ResolveServerUrl("http://localhost:5000");

        Assert.Equal("https://spokes.family.org", resolved);
    }

    [Fact]
    public void ResolveServerUrl_PrefersHttpsPublicUrlOverHttpAccess()
    {
        var sysConfig = _systemConfigs.Get();
        sysConfig.ServerPublicUrl = "https://spokes.family.org";
        _systemConfigs.Save(sysConfig);

        var service = CreateService();

        string resolved = service.ResolveServerUrl("http://192.168.1.100:5000");

        Assert.Equal("https://spokes.family.org", resolved);
    }

    [Fact]
    public void ResolveServerUrl_PreservesValidNetworkUrlWhenPublicUrlNotHttps()
    {
        var sysConfig = _systemConfigs.Get();
        sysConfig.ServerPublicUrl = "http://192.168.1.100:5000";
        _systemConfigs.Save(sysConfig);

        var service = CreateService();

        string resolved = service.ResolveServerUrl("http://192.168.1.100:5000");

        Assert.Equal("http://192.168.1.100:5000", resolved);
    }

    [Fact]
    public async Task GetCastInitPayloadAsync_ExchangesLicenseServerToServerWithRelayKey_AndPersistsReturnedTrialTicket()
    {
        var serverConfig = _serverConfigs.GetOrCreateGlobalConfig();
        serverConfig.DatabaseCreationId = "srv_cast_exchange";
        serverConfig.DatabaseCreationVersion = "2026.11.0";
        using var rsa = RSA.Create(2048);
        serverConfig.ServerMasterRsaPublicKey = rsa.ToXmlString(false);
        _serverConfigs.Save(serverConfig);

        string fp = RelayTrialTicketService.ComputeFingerprint(serverConfig.ServerMasterRsaPublicKey)!;
        var profile = _profileRepo.Get();
        profile.LicensePayload = "{\"licenseId\":\"lic-secret\"}";
        _profileRepo.Save(profile);

        HttpRequestMessage? capturedRequest = null;
        string? capturedBody = null;
        var handler = new StubHttpMessageHandler(async (req, ct) =>
        {
            capturedRequest = req;
            capturedBody = await req.Content!.ReadAsStringAsync(ct);
            var responseJson = JsonSerializer.Serialize(new
            {
                authorized = true,
                castTicket = "ct1.signedpayload.signedsig",
                expiresAtUtc = "2026-11-15T00:10:00Z",
                trialTicket = new RelayTrialTicket
                {
                    Fingerprint = fp,
                    IssuedAt = "2026-11-15T00:00:00Z",
                    ExpiresAt = "2026-12-15T00:00:00Z",
                    Signature = "relay-sig-1"
                }
            });
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responseJson, Encoding.UTF8, "application/json")
            };
        });

        var service = CreateService(handler, options: new SpokesOptions { PushRelayKey = "cast-relay-key-999" });

        var payload = await service.GetCastInitPayloadAsync("https://spokes.test.lan");

        Assert.NotNull(capturedRequest);
        Assert.Equal("https://push.spokes.sh/api/relay-cast/issue-ticket", capturedRequest!.RequestUri?.ToString());
        Assert.True(capturedRequest.Headers.TryGetValues(SpokesRelayClient.RelayKeyHeaderName, out var relayKeys));
        Assert.Equal("cast-relay-key-999", relayKeys.Single());
        Assert.NotNull(capturedBody);
        Assert.Contains("lic-secret", capturedBody!, StringComparison.Ordinal);

        Assert.Equal("5AF5A068", payload.AppId);
        Assert.Equal("srv_cast_exchange", payload.ServerId);
        Assert.Equal("https://spokes.test.lan", payload.ServerUrl);
        Assert.Equal("2026.11.0", payload.DatabaseCreationVersion);
        Assert.Equal(fp, payload.ServerFingerprint);
        Assert.Equal("ct1.signedpayload.signedsig", payload.CastTicket);
        Assert.Null(payload.AuthorizationError);
        Assert.Null(payload.AuthorizationErrorCode);

        // Regression (#92): sensitive LicensePayload / TrialTicket must never exist on SlideshowCastInitPayload
        Assert.Null(typeof(SlideshowCastInitPayload).GetProperty("LicensePayload"));
        Assert.Null(typeof(SlideshowCastInitPayload).GetProperty("TrialTicket"));

        var updatedConfig = _serverConfigs.GetOrCreateGlobalConfig();
        Assert.NotNull(updatedConfig.RelayTrialTicket);
        Assert.Equal("relay-sig-1", updatedConfig.RelayTrialTicket!.Signature);
    }

    [Fact]
    public async Task GetCastInitPayloadAsync_WhenRelayRejectsWithTrialExpired_SetsLicenseErrorAndStoresTrialTicket()
    {
        var serverConfig = _serverConfigs.GetOrCreateGlobalConfig();
        using var rsa = RSA.Create(2048);
        serverConfig.ServerMasterRsaPublicKey = rsa.ToXmlString(false);
        _serverConfigs.Save(serverConfig);
        string fp = RelayTrialTicketService.ComputeFingerprint(serverConfig.ServerMasterRsaPublicKey)!;

        var handler = new StubHttpMessageHandler((_, _) =>
        {
            var errJson = JsonSerializer.Serialize(new
            {
                title = "Forbidden",
                status = 403,
                detail = "Trial Expired: upgrade at spokes.sh",
                code = "TRIAL_EXPIRED",
                trialTicket = new RelayTrialTicket
                {
                    Fingerprint = fp,
                    IssuedAt = "2026-10-01T00:00:00Z",
                    ExpiresAt = "2026-10-31T00:00:00Z",
                    Signature = "expired-relay-sig"
                }
            });
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden)
            {
                Content = new StringContent(errJson, Encoding.UTF8, "application/problem+json")
            });
        });

        var service = CreateService(handler);

        var payload = await service.GetCastInitPayloadAsync("https://spokes.test.lan");

        Assert.Null(payload.CastTicket);
        Assert.Equal("TRIAL_EXPIRED", payload.AuthorizationErrorCode);
        Assert.Equal("Trial Expired: upgrade at spokes.sh", payload.AuthorizationError);
        Assert.Equal("expired-relay-sig", _serverConfigs.GetOrCreateGlobalConfig().RelayTrialTicket?.Signature);
    }

    [Fact]
    public async Task GetCastInitPayloadAsync_WhenCloudflareReturnsHtml403_DoesNotFalselyClaimSpokesPassRequired()
    {
        var handler = new StubHttpMessageHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden)
            {
                Content = new StringContent("<!DOCTYPE html><title>Attention Required! | Cloudflare</title>", Encoding.UTF8, "text/html")
            }));

        var service = CreateService(handler);

        var payload = await service.GetCastInitPayloadAsync("https://spokes.test.lan");

        Assert.Null(payload.CastTicket);
        Assert.Equal("RELAY_UNAVAILABLE", payload.AuthorizationErrorCode);
        Assert.NotNull(payload.AuthorizationError);
        Assert.DoesNotContain("Spokes Pass", payload.AuthorizationError!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("403", payload.AuthorizationError!, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, "RELAY_AUTH_FAILED")]
    [InlineData(HttpStatusCode.TooManyRequests, "RATE_LIMITED")]
    [InlineData(HttpStatusCode.ServiceUnavailable, "RELAY_UNAVAILABLE")]
    public async Task GetCastInitPayloadAsync_NonLicenseRelayHttpErrors_SetAccurateErrorCodeWithoutSpokesPassMessage(
        HttpStatusCode statusCode,
        string expectedErrorCode)
    {
        var handler = new StubHttpMessageHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(statusCode)));

        var service = CreateService(handler);

        var payload = await service.GetCastInitPayloadAsync("https://spokes.test.lan");

        Assert.Null(payload.CastTicket);
        Assert.Equal(expectedErrorCode, payload.AuthorizationErrorCode);
        Assert.NotNull(payload.AuthorizationError);
        Assert.DoesNotContain("Spokes Pass", payload.AuthorizationError!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GetCastInitPayloadAsync_InDemoMode_SkipsRelayCallAndSetsIsDemoTrue()
    {
        var demoConfig = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DataPath"] = _testDataDir,
                ["SPOKES_MASTER_PASSWORD"] = "UnitTest-Master-Password-2026!",
                ["Spokes_DemoMode"] = "true"
            })
            .Build();

        bool relayCalled = false;
        var handler = new StubHttpMessageHandler((_, _) =>
        {
            relayCalled = true;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError));
        });

        var service = CreateService(handler, demoConfig);

        Assert.True(service.IsDemoMode());
        var payload = await service.GetCastInitPayloadAsync("https://spokes.test.lan");

        Assert.True(payload.IsDemo);
        Assert.False(relayCalled);
        Assert.Null(payload.AuthorizationError);
    }

    [Fact]
    public void IsDemoMode_WhenLicensePayloadNullAndDemoModeNotSet_IsFalse()
    {
        var profile = _profileRepo.Get();
        profile.LicensePayload = null;
        _profileRepo.Save(profile);

        var service = CreateService();

        Assert.False(service.IsDemoMode());
    }

    private sealed class StubHttpMessageHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => responder(request, cancellationToken);
    }
}
