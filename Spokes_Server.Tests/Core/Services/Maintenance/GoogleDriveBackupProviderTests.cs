using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using Moq.Protected;
using Spokes_Server.Core.Configuration;
using Spokes_Server.Core.Data;
using Spokes_Server.Core.Data.Repositories.Core;
using Spokes_Server.Core.Models.Core;
using Spokes_Server.Core.Services.Licensing;
using Spokes_Server.Core.Services.Maintenance.Destinations;
using Xunit;

namespace Spokes_Server.Tests.Core.Services.Maintenance;

public class GoogleDriveBackupProviderTests : IDisposable
{
    private readonly string _testDir;
    private readonly CompanyProfileRepository _profiles;
    private readonly ServerConfigRepository _serverConfigs;
    private readonly DiskPersistenceService _persistence;
    private readonly IConfiguration _config;
    private readonly LicenseValidationService _licenseValidation;

    public GoogleDriveBackupProviderTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), "Spokes_GDriveTest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testDir);

        var configDict = new Dictionary<string, string?>
        {
            ["DataPath"] = _testDir,
            ["Spokes:PushRelayUrl"] = "https://push.spokes.sh",
            ["Spokes:PushRelayKey"] = "test-relay-key"
        };
        _config = new ConfigurationBuilder().AddInMemoryCollection(configDict).Build();

        _persistence = new DiskPersistenceService(new Mock<ILogger<DiskPersistenceService>>().Object);
        _profiles = new CompanyProfileRepository(_persistence, _config);
        _serverConfigs = new ServerConfigRepository(_persistence, _config);

        var versionMeta = new VersionMetadata("2026.10.0");
        _licenseValidation = new LicenseValidationService(
            new Mock<ILogger<LicenseValidationService>>().Object,
            versionMeta,
            configuration: _config);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_testDir))
            {
                Directory.Delete(_testDir, true);
            }
        }
        catch { }
    }

    private static IHttpClientFactory CreateMockHttpClientFactory(HttpResponseMessage response)
    {
        var handlerMock = new Mock<HttpMessageHandler>();
        handlerMock
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(response);

        var httpClient = new HttpClient(handlerMock.Object);
        var factoryMock = new Mock<IHttpClientFactory>();
        factoryMock.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(httpClient);
        return factoryMock.Object;
    }

    [Fact]
    public async Task GetRelayAuthUrlAsync_WhenSpokesPassExpired_ReturnsNull()
    {
        // Expired trial setup: ticket expired in past
        var serverConfig = _serverConfigs.GetOrCreateGlobalConfig();
        using var rsa = RSA.Create(2048);
        serverConfig.ServerMasterRsaPublicKey = rsa.ToXmlString(false);
        var fingerprint = RelayTrialTicketService.ComputeFingerprint(serverConfig.ServerMasterRsaPublicKey);
        serverConfig.RelayTrialTicket = new RelayTrialTicket
        {
            Fingerprint = fingerprint!,
            IssuedAt = DateTime.UtcNow.AddMonths(-3).ToString("o"),
            ExpiresAt = DateTime.UtcNow.AddDays(-5).ToString("o"),
            Signature = "dummy"
        };
        _serverConfigs.Save(serverConfig);

        var profile = _profiles.Get();
        profile.LicensePayload = null; // No paid license, relies on trial
        _profiles.Save(profile);

        var httpFactory = CreateMockHttpClientFactory(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"authUrl\":\"https://accounts.google.com/test\"}", Encoding.UTF8, "application/json")
        });

        var provider = new GoogleDriveBackupProvider(_profiles, _serverConfigs, _licenseValidation, httpFactory, _config);

        var result = await provider.GetRelayAuthUrlAsync("https://spokes.lan/callback");

        Assert.Null(result);
    }

    [Fact]
    public async Task GetFreshAccessTokenAsync_WhenRefreshTokenMissing_ReturnsNull()
    {
        var profile = _profiles.Get();
        profile.Backup.GoogleDrive.RefreshToken = string.Empty;
        _profiles.Save(profile);

        var httpFactory = CreateMockHttpClientFactory(new HttpResponseMessage(HttpStatusCode.OK));
        var provider = new GoogleDriveBackupProvider(_profiles, _serverConfigs, _licenseValidation, httpFactory, _config);

        var token = await provider.GetFreshAccessTokenAsync();

        Assert.Null(token);
    }

    [Fact]
    public async Task GetFreshAccessTokenAsync_WhenSpokesPassExpired_StillRefreshesTokenForExistingSetup()
    {
        // Setup: Expired trial ticket
        var serverConfig = _serverConfigs.GetOrCreateGlobalConfig();
        using var rsa = RSA.Create(2048);
        serverConfig.ServerMasterRsaPublicKey = rsa.ToXmlString(false);
        var fingerprint = RelayTrialTicketService.ComputeFingerprint(serverConfig.ServerMasterRsaPublicKey);
        serverConfig.RelayTrialTicket = new RelayTrialTicket
        {
            Fingerprint = fingerprint!,
            IssuedAt = DateTime.UtcNow.AddMonths(-3).ToString("o"),
            ExpiresAt = DateTime.UtcNow.AddDays(-5).ToString("o"),
            Signature = "dummy"
        };
        _serverConfigs.Save(serverConfig);

        // User already has Google Drive connected
        var profile = _profiles.Get();
        profile.LicensePayload = null;
        profile.Backup.GoogleDrive.RefreshToken = "valid_refresh_token_xyz";
        _profiles.Save(profile);

        var relayResponse = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"accessToken\":\"fresh_google_access_token_123\"}", Encoding.UTF8, "application/json")
        };
        var httpFactory = CreateMockHttpClientFactory(relayResponse);

        var provider = new GoogleDriveBackupProvider(_profiles, _serverConfigs, _licenseValidation, httpFactory, _config);

        // When pass is expired, existing Google Drive backup should still obtain fresh access token
        var token = await provider.GetFreshAccessTokenAsync();

        Assert.Equal("fresh_google_access_token_123", token);
    }
}
