using System;
using System.Net.Http;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Spokes_Server.Core.Data.Repositories.Core;
using Spokes_Server.Core.Models.Core;

namespace Spokes_Server.Core.Security;

/// <summary>
/// Configures <see cref="OpenIdConnectOptions"/> dynamically from <see cref="SystemConfigRepository"/>,
/// invalidating cached options whenever system configuration changes or is reloaded at runtime.
/// </summary>
public class ConfigureOpenIdConnectOptions : IConfigureNamedOptions<OpenIdConnectOptions>, IPostConfigureOptions<OpenIdConnectOptions>
{
    private readonly IServiceProvider _serviceProvider;
    private readonly IOptionsMonitorCache<OpenIdConnectOptions> _optionsCache;
    private readonly SystemConfig? _fallbackConfig;
    private readonly ILogger<ConfigureOpenIdConnectOptions>? _logger;
    private bool _subscribed;
    private readonly object _lock = new();

    public ConfigureOpenIdConnectOptions(
        IServiceProvider serviceProvider,
        IOptionsMonitorCache<OpenIdConnectOptions> optionsCache,
        SystemConfig? fallbackConfig = null,
        ILogger<ConfigureOpenIdConnectOptions>? logger = null)
    {
        _serviceProvider = serviceProvider;
        _optionsCache = optionsCache;
        _fallbackConfig = fallbackConfig;
        _logger = logger ?? serviceProvider.GetService<ILogger<ConfigureOpenIdConnectOptions>>();

        TrySubscribe();
    }

    private void TrySubscribe()
    {
        if (_subscribed) return;

        lock (_lock)
        {
            if (_subscribed) return;

            var repo = _serviceProvider.GetService<SystemConfigRepository>();
            if (repo != null)
            {
                repo.OnSaved += OnSystemConfigSaved;
                repo.OnLoaded += OnSystemConfigLoaded;
                _subscribed = true;
            }
        }
    }

    private void OnSystemConfigSaved(SystemConfig config)
    {
        InvalidateOptionsCache();
    }

    private void OnSystemConfigLoaded()
    {
        InvalidateOptionsCache();
    }

    private void InvalidateOptionsCache()
    {
        _optionsCache.TryRemove(OpenIdConnectDefaults.AuthenticationScheme);
        OpenIdConfigurationProvider.ClearAllCaches();
    }

    public void Configure(string? name, OpenIdConnectOptions options)
    {
        if (name != null && name != OpenIdConnectDefaults.AuthenticationScheme)
        {
            return;
        }

        Apply(options);
    }

    public void Configure(OpenIdConnectOptions options)
    {
        Configure(OpenIdConnectDefaults.AuthenticationScheme, options);
    }

    public void PostConfigure(string? name, OpenIdConnectOptions options)
    {
        if (name != null && name != OpenIdConnectDefaults.AuthenticationScheme)
        {
            return;
        }

        Apply(options);
    }

    private void Apply(OpenIdConnectOptions options)
    {
        TrySubscribe();

        var repo = _serviceProvider.GetService<SystemConfigRepository>();
        var systemConfig = repo?.Get() ?? _fallbackConfig ?? new SystemConfig();

        var authorityUrl = systemConfig.Authority;
        if (!string.IsNullOrWhiteSpace(authorityUrl) &&
            !authorityUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !authorityUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            authorityUrl = "https://" + authorityUrl;
        }

        if (!string.IsNullOrWhiteSpace(authorityUrl) && !Uri.TryCreate(authorityUrl, UriKind.Absolute, out _))
        {
            _logger?.LogCritical("Invalid OpenID Authority URL in configuration: '{AuthorityUrl}'. Falling back to dummy value to prevent crash.", authorityUrl);
            authorityUrl = null;
        }

        options.Authority = string.IsNullOrWhiteSpace(authorityUrl) ? "https://dummy.authority.for.setup" : authorityUrl;
        options.RequireHttpsMetadata = false;

        HttpMessageHandler handler = systemConfig.ProviderType == IdpType.BuiltInCasdoor
            ? new CasdoorLoopbackHandler(options.Authority)
            : new HttpClientHandler();

        options.BackchannelHttpHandler = handler;

        var backchannelClient = new HttpClient(handler);
        var documentRetriever = new AbsoluteUriDocumentRetriever(backchannelClient, options.Authority)
        {
            RequireHttps = options.RequireHttpsMetadata
        };

        options.ConfigurationManager = new ConfigurationManager<OpenIdConnectConfiguration>(
            $"{options.Authority.TrimEnd('/')}/.well-known/openid-configuration",
            new OpenIdConnectConfigurationRetriever(),
            documentRetriever
        );

        options.ClientId = string.IsNullOrWhiteSpace(systemConfig.ClientId) ? "dummy_client_id" : systemConfig.ClientId;
        options.ClientSecret = systemConfig.ClientSecret;
    }
}
