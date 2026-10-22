using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using Moq.Protected;
using Spokes_Server.Core.Data;
using Spokes_Server.Core.Data.Repositories.Core;
using Spokes_Server.Core.Models.Core;
using Spokes_Server.Core.Services.Communication.Notifications;
using Spokes_Server.Core.Services.Communication.Notifications.WebPush;
using Spokes_Server.Core.Services.Licensing;
using Spokes_Server.Core.Services.Logging;
using Xunit;

namespace Spokes_Server.Tests.Core.Services.Communication.Notifications.WebPush;

public class DecomposedWebPushServicesTests : IDisposable
{
    private readonly string _testDataDir;
    private readonly IConfiguration _config;
    private readonly DiskPersistenceService _persistence;
    private readonly DeviceSessionRepository _sessions;

    public DecomposedWebPushServicesTests()
    {
        _testDataDir = Path.Combine(Path.GetTempPath(), "Spokes_Test_DecomposedWebPush_" + Guid.NewGuid().ToString());
        Directory.CreateDirectory(_testDataDir);

        var configDict = new Dictionary<string, string?> { { "DataPath", _testDataDir } };
        _config = new ConfigurationBuilder().AddInMemoryCollection(configDict).Build();
        _persistence = new DiskPersistenceService(new Mock<ILogger<DiskPersistenceService>>().Object);
        _sessions = new DeviceSessionRepository(_persistence, _config);
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
        catch
        {
            // Ignore cleanup errors
        }
    }

    [Fact]
    public void PushTokenService_CreateAvatarPushToken_ReturnsNonEmptyUrlEncodedString()
    {
        var dataProtectionMock = new Mock<IDataProtectionProvider>();
        var protectorMock = new Mock<IDataProtector>();
        protectorMock.Setup(p => p.Protect(It.IsAny<byte[]>())).Returns([10, 20, 30, 40]);
        dataProtectionMock.Setup(p => p.CreateProtector(It.IsAny<string>())).Returns(protectorMock.Object);

        var tokenService = new PushTokenService(dataProtectionMock.Object);

        var token = tokenService.CreateAvatarPushToken("user123");
        var iconUrl = tokenService.GetDefaultIconUrl("user123");

        Assert.False(string.IsNullOrEmpty(token));
        Assert.StartsWith("/spokesapi/Media/Icon?t=", iconUrl);
    }

    [Fact]
    public void PushPayloadBuilder_BuildWebPushPayloadJson_FormatsExpectedFields()
    {
        var builder = new PushPayloadBuilder();
        var request = new PushNotificationRequest
        {
            UserId = "user1",
            Title = "Alert Title",
            Body = "Alert Body",
            Category = "chat",
            IsSilent = false,
            Tag = "tag1",
            Actions = [new { action = "open", title = "Open" }]
        };
        var profile = new CompanyProfile { CompanyName = "Acme Corp" };

        var json = builder.BuildWebPushPayloadJson(request, profile, "/icon.png");

        Assert.Contains("\"title\":\"Alert Title\"", json);
        Assert.Contains("\"body\":\"Alert Body\"", json);
        Assert.Contains("\"tag\":\"tag1\"", json);
        Assert.Contains("\"serverName\":\"Acme Corp\"", json);
    }

    [Fact]
    public void PushPayloadBuilder_BuildRelayPayload_Plaintext_CreatesExpectedRelayPayload()
    {
        var builder = new PushPayloadBuilder();
        var request = new PushNotificationRequest
        {
            UserId = "user1",
            Title = "Alert Title",
            Body = "Alert Body",
            Category = "chat",
            Url = "/chat/room1",
            Badge = 5,
            ChannelName = "General",
            IsGroupChat = true
        };
        var sub = new DeviceSession { PushEndpoint = "endpoint-123" };
        var profile = new CompanyProfile { CompanyName = "Acme Corp", LicensePayload = "LIC-DATA" };
        var serverConfig = new ServerConfig
        {
            DatabaseCreationId = "srv-1",
            DatabaseCreationVersion = "v1"
        };

        var context = new PushRelayContext(profile, serverConfig, "/icon.png", "https://spokes.example.com");
        var payload = builder.BuildRelayPayload(request, sub, context);

        Assert.Equal("endpoint-123", payload.Token);
        Assert.False(payload.IsEncrypted);
        Assert.Equal("Alert Title", payload.Title);
        Assert.Equal("Alert Body", payload.Body);
        Assert.Equal("/chat/room1", payload.Url);
        Assert.Equal("Acme Corp", payload.ServerName);
        Assert.Equal("General", payload.ChannelName);
        Assert.True(payload.IsGroupChat);
        Assert.Equal("srv-1", payload.ServerId);
        Assert.Equal("LIC-DATA", payload.LicensePayload);
        Assert.Equal("v1", payload.DatabaseCreationVersion);
        Assert.Null(payload.ServerFingerprint); // no escrow key yet
        Assert.Null(payload.TrialTicket);
        Assert.Null(payload.EncryptedKey);
        Assert.Null(payload.Ciphertext);
    }

    [Fact]
    public void PushPayloadBuilder_BuildRelayPayload_Encrypted_PopulatesEncryptionFields()
    {
        using var rsa = System.Security.Cryptography.RSA.Create(2048);
        var publicKeyBase64 = Convert.ToBase64String(rsa.ExportSubjectPublicKeyInfo());

        var builder = new PushPayloadBuilder();
        var request = new PushNotificationRequest
        {
            UserId = "user1",
            Title = "Secret Title",
            Body = "Secret Body",
            Category = "chat"
        };
        var sub = new DeviceSession
        {
            PushEndpoint = "endpoint-enc",
            PushPublicKey = publicKeyBase64
        };
        var profile = new CompanyProfile { CompanyName = "Acme Corp", LicensePayload = "LIC-DATA" };
        var serverConfig = new ServerConfig
        {
            DatabaseCreationId = "srv-1",
            DatabaseCreationVersion = "v1"
        };

        var context = new PushRelayContext(profile, serverConfig, "/icon.png", "https://spokes.example.com");
        var payload = builder.BuildRelayPayload(request, sub, context);

        Assert.Equal("endpoint-enc", payload.Token);
        Assert.True(payload.IsEncrypted);
        Assert.NotNull(payload.EncryptedKey);
        Assert.NotNull(payload.Nonce);
        Assert.NotNull(payload.Ciphertext);
        Assert.NotNull(payload.Tag);
        Assert.Null(payload.Title);
        Assert.Null(payload.Body);
        Assert.Null(payload.ServerName);
        Assert.Null(payload.ChannelName);
        Assert.Null(payload.OriginUrl);
        Assert.Null(payload.ServerIconUrl);

        // Verify round-trip decryption of innerPayload
        var encryptedKeyBytes = Convert.FromBase64String(payload.EncryptedKey!);
        var aesKey = rsa.Decrypt(encryptedKeyBytes, System.Security.Cryptography.RSAEncryptionPadding.OaepSHA1);

        var nonce = Convert.FromBase64String(payload.Nonce!);
        var ciphertext = Convert.FromBase64String(payload.Ciphertext!);
        var tag = Convert.FromBase64String(payload.Tag!);
        var plaintextBytes = new byte[ciphertext.Length];

        using var aesGcm = new System.Security.Cryptography.AesGcm(aesKey, 16);
        aesGcm.Decrypt(nonce, ciphertext, tag, plaintextBytes);

        var innerJson = System.Text.Encoding.UTF8.GetString(plaintextBytes);
        Assert.Contains("\"Title\":\"Secret Title\"", innerJson);
        Assert.Contains("\"Body\":\"Secret Body\"", innerJson);
        Assert.Contains("\"ServerName\":\"Acme Corp\"", innerJson);
        Assert.Contains("\"OriginUrl\":\"https://spokes.example.com\"", innerJson);
    }

    [Fact]
    public async Task NativeRelayDispatcher_DispatchRelayPushAsync_WhenHttpSucceeds_ReturnsTrue()
    {
        var handlerMock = new Mock<HttpMessageHandler>();
        handlerMock.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync", ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.OK));

        var httpClient = new HttpClient(handlerMock.Object);
        var factoryMock = new Mock<IHttpClientFactory>();
        factoryMock.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(httpClient);

        var systemLogMock = new Mock<ISystemLogService>();

        var dispatcher = new NativeRelayDispatcher(factoryMock.Object, _sessions, systemLogMock.Object);

        var payload = new RelayPushPayload { Token = "test_token", Title = "Hello" };
        var sub = new DeviceSession { Id = "sub1", PushEndpoint = "endpoint", DeviceType = "Mobile" };
        _sessions.Save(sub);

        var result = await dispatcher.DispatchRelayPushAsync(payload, sub);

        Assert.True(result);
    }

    [Fact]
    public async Task NativeRelayDispatcher_DispatchRelayPushAsync_WhenGone_ClearsSubscriptionAndReturnsFalse()
    {
        var handlerMock = new Mock<HttpMessageHandler>();
        handlerMock.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync", ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.Gone));

        var httpClient = new HttpClient(handlerMock.Object);
        var factoryMock = new Mock<IHttpClientFactory>();
        factoryMock.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(httpClient);

        var systemLogMock = new Mock<ISystemLogService>();

        var dispatcher = new NativeRelayDispatcher(factoryMock.Object, _sessions, systemLogMock.Object);

        var payload = new RelayPushPayload { Token = "test_token" };
        var sub = new DeviceSession { Id = "sub1", PushEndpoint = "endpoint", DeviceType = "Mobile" };
        _sessions.Save(sub);

        var result = await dispatcher.DispatchRelayPushAsync(payload, sub);

        Assert.False(result);
        Assert.Null(sub.PushEndpoint);
    }

    [Fact]
    public void PushLicenseEligibilityService_WhenDependenciesProvided_DelegatesToLicenseValidationService()
    {
        var companyProfileRepo = new CompanyProfileRepository(_persistence, _config);
        var serverConfigRepo = new ServerConfigRepository(_persistence, _config);
        var licenseValidation = new LicenseValidationService(
            new Mock<ILogger<LicenseValidationService>>().Object,
            new VersionMetadata("2026.8.102"));

        var eligibility = new PushLicenseEligibilityService(
            companyProfileRepo,
            serverConfigRepo,
            licenseValidation);

        // Community / unactivated edition by default
        var isAllowed = eligibility.IsNativePushAllowed();
        var status = eligibility.GetNativePushStatus();

        Assert.NotNull(status);
        Assert.False(string.IsNullOrEmpty(status.Status));
    }

    [Fact]
    public void PushLicenseEligibilityService_WhenDependenciesNull_ThrowsArgumentNullException()
    {
        var companyProfileRepo = new CompanyProfileRepository(_persistence, _config);
        var serverConfigRepo = new ServerConfigRepository(_persistence, _config);
        var licenseValidation = new LicenseValidationService(
            new Mock<ILogger<LicenseValidationService>>().Object,
            new VersionMetadata("2026.8.102"));

        Assert.Throws<ArgumentNullException>(() => new PushLicenseEligibilityService(null!, serverConfigRepo, licenseValidation));
        Assert.Throws<ArgumentNullException>(() => new PushLicenseEligibilityService(companyProfileRepo, null!, licenseValidation));
        Assert.Throws<ArgumentNullException>(() => new PushLicenseEligibilityService(companyProfileRepo, serverConfigRepo, null!));
    }

    private static string NewEscrowPublicKeyXml()
    {
        using var rsa = System.Security.Cryptography.RSA.Create(2048);
        return rsa.ToXmlString(false);
    }

    private static string TicketJson(string fingerprint, string expiresAt = "2026-12-05T10:00:00Z") =>
        $"{{\"fingerprint\":\"{fingerprint}\",\"issuedAt\":\"2026-10-05T10:00:00Z\",\"expiresAt\":\"{expiresAt}\",\"signature\":\"relay-sig\"}}";

    private (NativeRelayDispatcher Dispatcher, ServerConfigRepository Configs) CreateDispatcherReturning(HttpStatusCode status, string body, string escrowKeyXml)
    {
        var handlerMock = new Mock<HttpMessageHandler>();
        handlerMock.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync", ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(() => new HttpResponseMessage(status) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") });

        var factoryMock = new Mock<IHttpClientFactory>();
        factoryMock.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(() => new HttpClient(handlerMock.Object));

        var configs = new ServerConfigRepository(_persistence, _config);
        var config = configs.GetOrCreateGlobalConfig();
        config.ServerMasterRsaPublicKey = escrowKeyXml;
        configs.Save(config);

        var dispatcher = new NativeRelayDispatcher(factoryMock.Object, _sessions, new Mock<ISystemLogService>().Object,
            trialTickets: new RelayTrialTicketService(configs));
        return (dispatcher, configs);
    }

    [Fact]
    public void PushPayloadBuilder_BuildRelayPayload_IncludesFingerprintAndOwnTicket()
    {
        var keyXml = NewEscrowPublicKeyXml();
        var fingerprint = RelayTrialTicketService.ComputeFingerprint(keyXml)!;
        var ticket = new RelayTrialTicket { Fingerprint = fingerprint, IssuedAt = "2026-10-05T10:00:00Z", ExpiresAt = "2026-12-05T10:00:00Z", Signature = "relay-sig" };
        var serverConfig = new ServerConfig { DatabaseCreationId = "srv-1", DatabaseCreationVersion = "v1", ServerMasterRsaPublicKey = keyXml, RelayTrialTicket = ticket };
        var context = new PushRelayContext(new CompanyProfile(), serverConfig, "/icon.png", null);

        var payload = new PushPayloadBuilder().BuildRelayPayload(new PushNotificationRequest { UserId = "u", Title = "t", Body = "b" }, new DeviceSession { PushEndpoint = "e" }, context);

        Assert.Equal(fingerprint, payload.ServerFingerprint);
        Assert.Same(ticket, payload.TrialTicket);
    }

    [Fact]
    public void PushPayloadBuilder_BuildRelayPayload_OmitsTicketForAnotherKey()
    {
        var keyXml = NewEscrowPublicKeyXml();
        var foreign = new RelayTrialTicket { Fingerprint = RelayTrialTicketService.ComputeFingerprint(NewEscrowPublicKeyXml())!, ExpiresAt = "2026-12-05T10:00:00Z" };
        var serverConfig = new ServerConfig { ServerMasterRsaPublicKey = keyXml, RelayTrialTicket = foreign };
        var context = new PushRelayContext(new CompanyProfile(), serverConfig, "/icon.png", null);

        var payload = new PushPayloadBuilder().BuildRelayPayload(new PushNotificationRequest { UserId = "u", Title = "t", Body = "b" }, new DeviceSession { PushEndpoint = "e" }, context);

        Assert.NotNull(payload.ServerFingerprint);
        Assert.Null(payload.TrialTicket);
    }

    [Fact]
    public async Task NativeRelayDispatcher_SuccessWithTicket_PersistsTicket()
    {
        var keyXml = NewEscrowPublicKeyXml();
        var fingerprint = RelayTrialTicketService.ComputeFingerprint(keyXml)!;
        var (dispatcher, configs) = CreateDispatcherReturning(HttpStatusCode.OK, $"{{\"success\":true,\"result\":\"msg-id\",\"trialTicket\":{TicketJson(fingerprint)}}}", keyXml);

        await dispatcher.DispatchRelayPushOrThrowAsync(new RelayPushPayload { Token = "t" }, new DeviceSession { Id = "s1", DeviceType = "Mobile" });

        var stored = configs.GetOrCreateGlobalConfig().RelayTrialTicket;
        Assert.NotNull(stored);
        Assert.Equal(fingerprint, stored!.Fingerprint);
        Assert.Equal("2026-12-05T10:00:00Z", stored.ExpiresAt);
        Assert.Equal("relay-sig", stored.Signature);
    }

    [Fact]
    public async Task NativeRelayDispatcher_TrialExpiredProblem_PersistsTicketAndThrowsCodedException()
    {
        var keyXml = NewEscrowPublicKeyXml();
        var fingerprint = RelayTrialTicketService.ComputeFingerprint(keyXml)!;
        var problem = $"{{\"type\":\"about:blank\",\"title\":\"Forbidden\",\"status\":403,\"detail\":\"License Expired: trial over.\",\"code\":\"TRIAL_EXPIRED\",\"trialTicket\":{TicketJson(fingerprint, "2026-01-01T00:00:00Z")}}}";
        var (dispatcher, configs) = CreateDispatcherReturning(HttpStatusCode.Forbidden, problem, keyXml);

        var ex = await Assert.ThrowsAsync<RelayRejectedException>(() =>
            dispatcher.DispatchRelayPushOrThrowAsync(new RelayPushPayload { Token = "t" }, new DeviceSession { Id = "s1", DeviceType = "Mobile" }));

        Assert.Equal("TRIAL_EXPIRED", ex.ErrorCode);
        Assert.Equal("License Expired: trial over.", ex.Message);
        Assert.Equal("2026-01-01T00:00:00Z", configs.GetOrCreateGlobalConfig().RelayTrialTicket?.ExpiresAt);
    }

    [Fact]
    public async Task NativeRelayDispatcher_TicketForAnotherKey_IsNotPersisted()
    {
        var keyXml = NewEscrowPublicKeyXml();
        var foreignFingerprint = RelayTrialTicketService.ComputeFingerprint(NewEscrowPublicKeyXml())!;
        var (dispatcher, configs) = CreateDispatcherReturning(HttpStatusCode.OK, $"{{\"success\":true,\"trialTicket\":{TicketJson(foreignFingerprint)}}}", keyXml);

        await dispatcher.DispatchRelayPushOrThrowAsync(new RelayPushPayload { Token = "t" }, new DeviceSession { Id = "s1", DeviceType = "Mobile" });

        Assert.Null(configs.GetOrCreateGlobalConfig().RelayTrialTicket);
    }

    [Fact]
    public async Task NativeRelayDispatcher_ErrorWithoutCode_ThrowsHttpRequestException()
    {
        var (dispatcher, _) = CreateDispatcherReturning(HttpStatusCode.InternalServerError, "upstream failure", NewEscrowPublicKeyXml());

        await Assert.ThrowsAsync<HttpRequestException>(() =>
            dispatcher.DispatchRelayPushOrThrowAsync(new RelayPushPayload { Token = "t" }, new DeviceSession { Id = "s1", DeviceType = "Mobile" }));
    }
}
