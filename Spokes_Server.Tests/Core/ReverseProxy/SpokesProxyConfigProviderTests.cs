using System;
using System.IO;
using System.Linq;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Primitives;
using Moq;
using Spokes_Server.Core.Data;
using Spokes_Server.Core.Data.Repositories.Core;
using Spokes_Server.Core.Models.Core;
using Spokes_Server.Core.ReverseProxy;
using Spokes_Server.Tests;
using Xunit;

namespace Spokes_Server.Tests.Core.ReverseProxy;

public class SpokesProxyConfigProviderTests : TestDataTestBase
{
    private readonly SystemConfigRepository _systemConfigRepo;

    public SpokesProxyConfigProviderTests()
    {
        var mockConfig = new Mock<IConfiguration>();
        mockConfig.Setup(c => c["DataPath"]).Returns(_testDataPath);

        var writer = new DiskPersistenceService(new Mock<ILogger<DiskPersistenceService>>().Object);
        _systemConfigRepo = new SystemConfigRepository(writer, mockConfig.Object);
    }

    [Fact]
    public void GetConfig_WithBuiltInCasdoor_IncludesCasdoorAndLiveKitRoutes()
    {
        var config = new SystemConfig
        {
            ProviderType = IdpType.BuiltInCasdoor
        };
        _systemConfigRepo.Save(config);

        using var provider = new SpokesProxyConfigProvider(_systemConfigRepo);
        var proxyConfig = provider.GetConfig();

        Assert.NotNull(proxyConfig);
        Assert.NotNull(proxyConfig.ChangeToken);
        Assert.False(proxyConfig.ChangeToken.HasChanged);

        var routeIds = proxyConfig.Routes.Select(r => r.RouteId).ToList();
        Assert.Contains("livekitRtcRoute", routeIds);
        Assert.Contains("livekitTwirpRoute", routeIds);
        Assert.Contains("casdoorRoute_Static", routeIds);
        Assert.Contains("casdoorRoute_Assets", routeIds);
        Assert.Contains("casdoorRoute_Api", routeIds);
        Assert.Contains("casdoorRoute_groups", routeIds);
        Assert.Contains("casdoorRoute_sessions", routeIds);

        var clusterIds = proxyConfig.Clusters.Select(c => c.ClusterId).ToList();
        Assert.Contains("casdoorCluster", clusterIds);
        Assert.Contains("livekitCluster", clusterIds);
    }

    [Fact]
    public void GetConfig_WithExternalIdp_ExcludesCasdoorRoutes()
    {
        var config = new SystemConfig
        {
            ProviderType = IdpType.External
        };
        _systemConfigRepo.Save(config);

        using var provider = new SpokesProxyConfigProvider(_systemConfigRepo);
        var proxyConfig = provider.GetConfig();

        var routeIds = proxyConfig.Routes.Select(r => r.RouteId).ToList();
        Assert.Contains("livekitRtcRoute", routeIds);
        Assert.Contains("livekitTwirpRoute", routeIds);
        Assert.DoesNotContain("casdoorRoute_Static", routeIds);
        Assert.DoesNotContain("casdoorRoute_Assets", routeIds);
        Assert.DoesNotContain("casdoorRoute_Api", routeIds);
    }

    [Fact]
    public void Reload_SignalsChangeTokenAndUpdatesRoutes()
    {
        var initialConfig = new SystemConfig
        {
            ProviderType = IdpType.BuiltInCasdoor
        };
        _systemConfigRepo.Save(initialConfig);

        using var provider = new SpokesProxyConfigProvider(_systemConfigRepo);
        var initialProxyConfig = provider.GetConfig();

        bool changeTokenFired = false;
        initialProxyConfig.ChangeToken.RegisterChangeCallback(_ => changeTokenFired = true, null);

        var updatedConfig = new SystemConfig
        {
            ProviderType = IdpType.External
        };
        provider.Reload(updatedConfig);

        Assert.True(changeTokenFired);
        Assert.True(initialProxyConfig.ChangeToken.HasChanged);

        var newProxyConfig = provider.GetConfig();
        Assert.NotSame(initialProxyConfig, newProxyConfig);
        Assert.False(newProxyConfig.ChangeToken.HasChanged);

        var routeIds = newProxyConfig.Routes.Select(r => r.RouteId).ToList();
        Assert.DoesNotContain("casdoorRoute_Static", routeIds);
        Assert.DoesNotContain("casdoorRoute_Assets", routeIds);
    }

    [Fact]
    public void RepositorySave_AutomaticallyTriggersProxyReload()
    {
        var initialConfig = new SystemConfig
        {
            ProviderType = IdpType.BuiltInCasdoor
        };
        _systemConfigRepo.Save(initialConfig);

        using var provider = new SpokesProxyConfigProvider(_systemConfigRepo);
        var initialProxyConfig = provider.GetConfig();

        bool changeTokenFired = false;
        initialProxyConfig.ChangeToken.RegisterChangeCallback(_ => changeTokenFired = true, null);

        // Act - Save updated config to repository
        var updatedConfig = _systemConfigRepo.Get();
        updatedConfig.ProviderType = IdpType.External;
        _systemConfigRepo.Save(updatedConfig);

        // Assert
        Assert.True(changeTokenFired);
        var newProxyConfig = provider.GetConfig();
        var routeIds = newProxyConfig.Routes.Select(r => r.RouteId).ToList();
        Assert.DoesNotContain("casdoorRoute_Static", routeIds);
        Assert.DoesNotContain("casdoorRoute_Assets", routeIds);
    }
}
