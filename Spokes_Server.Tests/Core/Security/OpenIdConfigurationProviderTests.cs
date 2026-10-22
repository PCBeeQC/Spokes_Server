using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Moq;
using Spokes_Server.Core.Security;
using Xunit;

namespace Spokes_Server.Tests.Core.Security;

public class OpenIdConfigurationProviderTests
{
    [Fact]
    public void GetConfigurationManager_ReturnsCachedInstance_ForSameAuthority()
    {
        var provider = new OpenIdConfigurationProvider();
        const string authority = "https://idp.example.com";

        var manager1 = provider.GetConfigurationManager(authority);
        var manager2 = provider.GetConfigurationManager(authority);
        var managerWithTrailingSlash = provider.GetConfigurationManager(authority + "/");

        Assert.NotNull(manager1);
        Assert.Same(manager1, manager2);
        Assert.Same(manager1, managerWithTrailingSlash);
    }

    [Fact]
    public void GetConfigurationManager_UsesDefaultManager_WhenSpecified()
    {
        var mockManager = new Mock<IConfigurationManager<OpenIdConnectConfiguration>>();
        var provider = new OpenIdConfigurationProvider(defaultManager: mockManager.Object);

        var result = provider.GetConfigurationManager("https://idp.example.com");

        Assert.Same(mockManager.Object, result);
    }

    [Fact]
    public void GetConfigurationManager_UsesOidcOptionsMonitor_WhenAuthorityMatches()
    {
        const string authority = "https://auth.mycompany.com";
        var mockManager = new Mock<IConfigurationManager<OpenIdConnectConfiguration>>();

        var oidcOptions = new OpenIdConnectOptions
        {
            Authority = authority,
            ConfigurationManager = mockManager.Object
        };

        var mockMonitor = new Mock<IOptionsMonitor<OpenIdConnectOptions>>();
        mockMonitor.Setup(m => m.Get(OpenIdConnectDefaults.AuthenticationScheme))
            .Returns(oidcOptions);

        var provider = new OpenIdConfigurationProvider(oidcOptionsMonitor: mockMonitor.Object);

        var result = provider.GetConfigurationManager(authority + "/");

        Assert.Same(mockManager.Object, result);
    }

    [Fact]
    public void ClearCache_RemovesCachedInstances()
    {
        var provider = new OpenIdConfigurationProvider();
        const string authority = "https://idp.example.com";

        var manager1 = provider.GetConfigurationManager(authority);
        provider.ClearCache();
        var manager2 = provider.GetConfigurationManager(authority);

        Assert.NotNull(manager1);
        Assert.NotNull(manager2);
        Assert.NotSame(manager1, manager2);
    }

    [Fact]
    public void ClearAllCaches_ClearsAcrossAllInstances()
    {
        var provider1 = new OpenIdConfigurationProvider();
        var provider2 = new OpenIdConfigurationProvider();
        const string authority = "https://idp.example.com";

        var m1 = provider1.GetConfigurationManager(authority);
        var m2 = provider2.GetConfigurationManager(authority);

        OpenIdConfigurationProvider.ClearAllCaches();

        var m1After = provider1.GetConfigurationManager(authority);
        var m2After = provider2.GetConfigurationManager(authority);

        Assert.NotSame(m1, m1After);
        Assert.NotSame(m2, m2After);
    }

    [Fact]
    public async Task ResolveEndSessionEndpointAsync_WhenEmptyAuthority_ReturnsDefaultFallback()
    {
        var provider = new OpenIdConfigurationProvider();

        var result = await provider.ResolveEndSessionEndpointAsync("");

        Assert.Equal("/api/logout", result);
    }

    [Fact]
    public async Task ResolveEndSessionEndpointAsync_WhenConfiguredManagerThrows_ReturnsFallback()
    {
        var mockManager = new Mock<IConfigurationManager<OpenIdConnectConfiguration>>();
        mockManager.Setup(m => m.GetConfigurationAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("IDP unreachable"));

        var provider = new OpenIdConfigurationProvider(defaultManager: mockManager.Object);

        var result = await provider.ResolveEndSessionEndpointAsync("https://idp.example.com");

        Assert.Equal("https://idp.example.com/api/logout", result);
    }

    [Fact]
    public async Task ResolveEndSessionEndpointAsync_WhenDiscovered_ReturnsEndSessionEndpoint()
    {
        var config = new OpenIdConnectConfiguration
        {
            EndSessionEndpoint = "https://idp.example.com/protocol/openid-connect/logout"
        };
        var mockManager = new Mock<IConfigurationManager<OpenIdConnectConfiguration>>();
        mockManager.Setup(m => m.GetConfigurationAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(config);

        var provider = new OpenIdConfigurationProvider(defaultManager: mockManager.Object);

        var result = await provider.ResolveEndSessionEndpointAsync("https://idp.example.com");

        Assert.Equal("https://idp.example.com/protocol/openid-connect/logout", result);
    }
}
