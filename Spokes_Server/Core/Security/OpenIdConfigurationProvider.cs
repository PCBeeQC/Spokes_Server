using System;
using System.Collections.Concurrent;
using System.Net.Http;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;

namespace Spokes_Server.Core.Security;

/// <summary>
/// Thread-safe singleton provider managing cached OIDC <see cref="IConfigurationManager{OpenIdConnectConfiguration}"/> instances.
/// Eliminates duplicated static dictionaries and cross-class cache clearing across controllers and services.
/// </summary>
public class OpenIdConfigurationProvider : IOpenIdConfigurationProvider
{
    private static readonly ConcurrentBag<WeakReference<OpenIdConfigurationProvider>> _instances = new();
    private static readonly Lazy<OpenIdConfigurationProvider> _sharedInstance = new(() => new OpenIdConfigurationProvider());

    /// <summary>
    /// Fallback shared instance for non-DI environments or legacy callers.
    /// </summary>
    public static OpenIdConfigurationProvider Shared => _sharedInstance.Value;

    private readonly ConcurrentDictionary<string, IConfigurationManager<OpenIdConnectConfiguration>> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly IHttpClientFactory? _httpClientFactory;
    private readonly IOptionsMonitor<OpenIdConnectOptions>? _oidcOptionsMonitor;
    private readonly IConfigurationManager<OpenIdConnectConfiguration>? _defaultManager;

    public OpenIdConfigurationProvider(
        IHttpClientFactory? httpClientFactory = null,
        IOptionsMonitor<OpenIdConnectOptions>? oidcOptionsMonitor = null,
        IConfigurationManager<OpenIdConnectConfiguration>? defaultManager = null)
    {
        _httpClientFactory = httpClientFactory;
        _oidcOptionsMonitor = oidcOptionsMonitor;
        _defaultManager = defaultManager;
        _instances.Add(new WeakReference<OpenIdConfigurationProvider>(this));
    }

    /// <inheritdoc />
    public IConfigurationManager<OpenIdConnectConfiguration> GetConfigurationManager(string authority)
    {
        return GetConfigurationManager(authority, null);
    }

    /// <inheritdoc />
    public IConfigurationManager<OpenIdConnectConfiguration> GetConfigurationManager(string authority, IHttpClientFactory? httpClientFactory)
    {
        if (_defaultManager != null)
        {
            return _defaultManager;
        }

        var normalizedAuthority = (authority ?? string.Empty).TrimEnd('/');

        var oidcOptions = _oidcOptionsMonitor?.Get(OpenIdConnectDefaults.AuthenticationScheme);
        if (oidcOptions?.ConfigurationManager != null &&
            string.Equals((oidcOptions.Authority ?? string.Empty).TrimEnd('/'), normalizedAuthority, StringComparison.OrdinalIgnoreCase))
        {
            return oidcOptions.ConfigurationManager;
        }

        return _cache.GetOrAdd(normalizedAuthority, auth =>
        {
            var factory = httpClientFactory ?? _httpClientFactory;
            var httpClient = factory != null
                ? factory.CreateClient()
                : new HttpClient();

            var docRetriever = new HttpDocumentRetriever(httpClient)
            {
                RequireHttps = false
            };

            return new ConfigurationManager<OpenIdConnectConfiguration>(
                $"{auth}/.well-known/openid-configuration",
                new OpenIdConnectConfigurationRetriever(),
                docRetriever
            );
        });
    }

    /// <inheritdoc />
    public void ClearCache()
    {
        _cache.Clear();
    }

    /// <inheritdoc />
    public async Task<string> ResolveEndSessionEndpointAsync(string? authority, CancellationToken cancellationToken = default)
    {
        var normalizedAuthority = (authority ?? string.Empty).Trim();
        var fallback = string.IsNullOrEmpty(normalizedAuthority)
            ? "/api/logout"
            : (normalizedAuthority.TrimEnd('/') + "/api/logout").Trim();

        if (string.IsNullOrEmpty(normalizedAuthority))
        {
            return fallback;
        }

        try
        {
            var configManager = GetConfigurationManager(normalizedAuthority);
            var config = await configManager.GetConfigurationAsync(cancellationToken);
            return config?.EndSessionEndpoint ?? fallback;
        }
        catch (Exception)
        {
            return fallback;
        }
    }

    /// <summary>
    /// Clears cached configuration managers across all tracked provider instances.
    /// </summary>
    public static void ClearAllCaches()
    {
        if (_sharedInstance.IsValueCreated)
        {
            _sharedInstance.Value.ClearCache();
        }

        foreach (var weakRef in _instances)
        {
            if (weakRef.TryGetTarget(out var instance))
            {
                instance.ClearCache();
            }
        }
    }
}
