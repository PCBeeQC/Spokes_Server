using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using Spokes_Server.Core.Configuration;
using Spokes_Server.Core.Data;
using Spokes_Server.Core.Data.Repositories.Core;
using Spokes_Server.Core.Models.Core;
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

    [Fact]
    public void ResolveServerUrl_ReplacesLoopbackWithServerPublicUrl()
    {
        var sysConfig = _systemConfigs.Get();
        sysConfig.ServerPublicUrl = "https://spokes.family.org";
        _systemConfigs.Save(sysConfig);

        var service = new SlideshowCastService(_serverConfigs, _profileRepo, _systemConfigs, _config);

        string resolved = service.ResolveServerUrl("http://localhost:5000");

        Assert.Equal("https://spokes.family.org", resolved);
    }

    [Fact]
    public void ResolveServerUrl_PrefersHttpsPublicUrlOverHttpAccess()
    {
        var sysConfig = _systemConfigs.Get();
        sysConfig.ServerPublicUrl = "https://spokes.family.org";
        _systemConfigs.Save(sysConfig);

        var service = new SlideshowCastService(_serverConfigs, _profileRepo, _systemConfigs, _config);

        string resolved = service.ResolveServerUrl("http://192.168.1.100:5000");

        Assert.Equal("https://spokes.family.org", resolved);
    }

    [Fact]
    public void ResolveServerUrl_PreservesValidNetworkUrlWhenPublicUrlNotHttps()
    {
        var sysConfig = _systemConfigs.Get();
        sysConfig.ServerPublicUrl = "http://192.168.1.100:5000";
        _systemConfigs.Save(sysConfig);

        var service = new SlideshowCastService(_serverConfigs, _profileRepo, _systemConfigs, _config);

        string resolved = service.ResolveServerUrl("http://192.168.1.100:5000");

        Assert.Equal("http://192.168.1.100:5000", resolved);
    }

    [Fact]
    public void GetCastInitPayload_PopulatesRequiredFields()
    {
        var serverConfig = _serverConfigs.GetOrCreateGlobalConfig();
        serverConfig.DatabaseCreationId = "srv_test_abc123";
        serverConfig.DatabaseCreationVersion = "2026.10.0";
        using var rsa = RSA.Create(2048);
        serverConfig.ServerMasterRsaPublicKey = rsa.ToXmlString(false);
        _serverConfigs.Save(serverConfig);

        var profile = _profileRepo.Get();
        profile.LicensePayload = "{\"email\":\"test@spokes.sh\",\"licenseId\":\"lic-1\"}";
        _profileRepo.Save(profile);

        var sysConfig = _systemConfigs.Get();
        sysConfig.ServerPublicUrl = "https://spokes.test.lan";
        _systemConfigs.Save(sysConfig);

        var service = new SlideshowCastService(_serverConfigs, _profileRepo, _systemConfigs, _config);

        var payload = service.GetCastInitPayload("https://spokes.test.lan");

        Assert.NotNull(payload);
        Assert.Equal("5AF5A068", payload.AppId);
        Assert.Equal("srv_test_abc123", payload.ServerId);
        Assert.Equal("https://spokes.test.lan", payload.ServerUrl);
        Assert.Equal("2026.10.0", payload.DatabaseCreationVersion);
        Assert.Equal(profile.LicensePayload, payload.LicensePayload);
        Assert.False(string.IsNullOrWhiteSpace(payload.ServerFingerprint));
    }

    [Fact]
    public void GetCastInitPayload_ReflectsDemoMode()
    {
        var demoConfigDict = new Dictionary<string, string?>
        {
            ["DataPath"] = _testDataDir,
            ["Spokes_DemoMode"] = "true"
        };
        var demoConfig = new ConfigurationBuilder().AddInMemoryCollection(demoConfigDict).Build();

        var service = new SlideshowCastService(_serverConfigs, _profileRepo, _systemConfigs, demoConfig);

        var payload = service.GetCastInitPayload("https://spokes.test.lan");

        Assert.True(payload.IsDemo);
    }

    [Fact]
    public void GetCastInitPayload_WhenLicensePayloadNullAndDemoModeNotSet_IsDemoIsFalse()
    {
        var profile = _profileRepo.Get();
        profile.LicensePayload = null;
        _profileRepo.Save(profile);

        var service = new SlideshowCastService(_serverConfigs, _profileRepo, _systemConfigs, _config);

        var payload = service.GetCastInitPayload("https://spokes.test.lan");

        Assert.False(payload.IsDemo);
    }
}
