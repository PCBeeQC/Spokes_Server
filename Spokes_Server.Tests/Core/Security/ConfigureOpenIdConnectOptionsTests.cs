using System;
using System.IO;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using Spokes_Server.Core.Data;
using Spokes_Server.Core.Data.Repositories.Core;
using Spokes_Server.Core.Models.Core;
using Spokes_Server.Core.Security;
using Spokes_Server.Core.Extensions;
using Xunit;

namespace Spokes_Server.Tests.Core.Security;

public class ConfigureOpenIdConnectOptionsTests : IDisposable
{
    private readonly string _tempDir;
    private readonly DiskPersistenceService _persistence;
    private readonly SystemConfigRepository _systemConfigRepo;

    public ConfigureOpenIdConnectOptionsTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "OidcOptionsTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);

        var mockConfig = new Mock<IConfiguration>();
        mockConfig.Setup(c => c["DataPath"]).Returns(_tempDir);

        var mockLogger = new Mock<ILogger<DiskPersistenceService>>();
        _persistence = new DiskPersistenceService(mockLogger.Object);
        _systemConfigRepo = new SystemConfigRepository(_persistence, mockConfig.Object);
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
        catch
        {
            // Best effort cleanup
        }
    }

    private (IServiceProvider sp, IOptionsMonitorCache<OpenIdConnectOptions> cache) CreateProviderWithCache()
    {
        var services = new ServiceCollection();
        services.AddOptions();
        services.AddSingleton(_systemConfigRepo);
        var sp = services.BuildServiceProvider();
        var cache = sp.GetRequiredService<IOptionsMonitorCache<OpenIdConnectOptions>>();
        return (sp, cache);
    }

    [Fact]
    public void Configure_WithDefaultConfig_SetsFallbackAuthorityAndClientId()
    {
        var (sp, optionsCache) = CreateProviderWithCache();
        var configurator = new ConfigureOpenIdConnectOptions(sp, optionsCache);

        var options = new OpenIdConnectOptions();
        configurator.Configure(OpenIdConnectDefaults.AuthenticationScheme, options);

        Assert.Equal("https://dummy.authority.for.setup", options.Authority);
        Assert.Equal("dummy_client_id", options.ClientId);
        Assert.False(options.RequireHttpsMetadata);
        Assert.NotNull(options.ConfigurationManager);
    }

    [Fact]
    public void Configure_WithCustomConfig_AppliesSettingsDynamically()
    {
        var config = _systemConfigRepo.Get();
        config.Authority = "https://identity.example.com";
        config.ClientId = "spokes-client-id";
        config.ClientSecret = "spokes-client-secret";
        config.ProviderType = IdpType.External;
        _systemConfigRepo.Save(config);

        var (sp, optionsCache) = CreateProviderWithCache();
        var configurator = new ConfigureOpenIdConnectOptions(sp, optionsCache);

        var options = new OpenIdConnectOptions();
        configurator.Configure(OpenIdConnectDefaults.AuthenticationScheme, options);

        Assert.Equal("https://identity.example.com", options.Authority);
        Assert.Equal("spokes-client-id", options.ClientId);
        Assert.Equal("spokes-client-secret", options.ClientSecret);
        Assert.NotNull(options.ConfigurationManager);
    }

    [Fact]
    public void Configure_WithBuiltInCasdoor_UsesCasdoorLoopbackHandler()
    {
        var config = _systemConfigRepo.Get();
        config.Authority = "http://127.0.0.1:8000";
        config.ProviderType = IdpType.BuiltInCasdoor;
        _systemConfigRepo.Save(config);

        var (sp, optionsCache) = CreateProviderWithCache();
        var configurator = new ConfigureOpenIdConnectOptions(sp, optionsCache);

        var options = new OpenIdConnectOptions();
        configurator.Configure(OpenIdConnectDefaults.AuthenticationScheme, options);

        Assert.IsType<CasdoorLoopbackHandler>(options.BackchannelHttpHandler);
    }

    [Fact]
    public void SystemConfigRepo_OnSaved_InvalidatesOptionsCache()
    {
        var (sp, optionsCache) = CreateProviderWithCache();
        var cachedOptions = new OpenIdConnectOptions { ClientId = "initial_cached_client" };
        optionsCache.TryAdd(OpenIdConnectDefaults.AuthenticationScheme, cachedOptions);

        var configurator = new ConfigureOpenIdConnectOptions(sp, optionsCache);

        // When configuration is updated and saved
        var config = _systemConfigRepo.Get();
        config.ClientId = "updated_client_id";
        _systemConfigRepo.Save(config);

        // Cached entry should have been removed from cache
        Assert.False(optionsCache.TryRemove(OpenIdConnectDefaults.AuthenticationScheme));
    }

    [Fact]
    public void SystemConfigRepo_LoadFromDisk_InvalidatesOptionsCache()
    {
        var (sp, optionsCache) = CreateProviderWithCache();
        var cachedOptions = new OpenIdConnectOptions { ClientId = "initial_cached_client" };
        optionsCache.TryAdd(OpenIdConnectDefaults.AuthenticationScheme, cachedOptions);

        _ = new ConfigureOpenIdConnectOptions(sp, optionsCache);

        // When configuration is reloaded from disk (e.g., during backup restoration)
        _systemConfigRepo.LoadFromDisk();

        // Cached entry should have been removed from cache
        Assert.False(optionsCache.TryRemove(OpenIdConnectDefaults.AuthenticationScheme));
    }

    [Fact]
    public void Configure_IgnoresDifferentSchemeName()
    {
        var (sp, optionsCache) = CreateProviderWithCache();
        var configurator = new ConfigureOpenIdConnectOptions(sp, optionsCache);

        var options = new OpenIdConnectOptions { Authority = "https://untouched.org" };
        configurator.Configure("SomeOtherScheme", options);

        Assert.Equal("https://untouched.org", options.Authority);
    }

    [Fact]
    public void AddSpokesAuth_ResolvesOpenIdConnectOptionsDynamicallyFromRepository()
    {
        var config = _systemConfigRepo.Get();
        config.Authority = "https://dynamic-idp.example.com";
        config.ClientId = "dynamic-client";
        _systemConfigRepo.Save(config);

        var services = new ServiceCollection();
        services.AddSingleton(_systemConfigRepo);
        services.AddLogging();
        services.AddSpokesAuth();

        var sp = services.BuildServiceProvider();
        var optionsMonitor = sp.GetRequiredService<IOptionsMonitor<OpenIdConnectOptions>>();
        var resolvedOptions = optionsMonitor.Get(OpenIdConnectDefaults.AuthenticationScheme);

        Assert.NotNull(resolvedOptions);
        Assert.Equal("https://dynamic-idp.example.com", resolvedOptions.Authority);
        Assert.Equal("dynamic-client", resolvedOptions.ClientId);
    }
}
