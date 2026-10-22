using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Microsoft.Extensions.Primitives;
using Spokes_Server.Core.Data.Repositories.Core;
using Spokes_Server.Core.Models.Core;
using Yarp.ReverseProxy.Configuration;

namespace Spokes_Server.Core.ReverseProxy;

/// <summary>
/// Dynamic reverse proxy configuration provider for YARP.
/// Reads route and cluster definitions from <see cref="SystemConfigRepository"/>,
/// and dynamically signals route reloading via <see cref="IChangeToken"/> when system configuration updates.
/// </summary>
public class SpokesProxyConfigProvider : IProxyConfigProvider, IDisposable
{
    private readonly SystemConfigRepository _systemConfigRepo;
    private volatile SpokesProxyConfig _currentConfig;
    private CancellationTokenSource _changeTokenSource = new();
    private bool _disposed;

    public SpokesProxyConfigProvider(SystemConfigRepository systemConfigRepo)
    {
        _systemConfigRepo = systemConfigRepo;
        _currentConfig = BuildConfig(_systemConfigRepo.Get());
        _systemConfigRepo.OnSaved += HandleConfigSaved;
    }

    public IProxyConfig GetConfig() => _currentConfig;

    private void HandleConfigSaved(SystemConfig config)
    {
        Reload(config);
    }

    /// <summary>
    /// Forces a reload of YARP proxy routes and clusters using the specified configuration.
    /// </summary>
    public void Reload(SystemConfig config)
    {
        if (_disposed) return;

        var oldCts = Interlocked.Exchange(ref _changeTokenSource, new CancellationTokenSource());
        _currentConfig = BuildConfig(config);
        oldCts.Cancel();
        oldCts.Dispose();
    }

    public SpokesProxyConfig BuildConfig(SystemConfig systemConfig)
    {
        var casdoorUiRoutes = new[] { "login", "signup", "forget", "callback", "organizations", "users", "roles", "permissions", "models", "adapters", "enforcers", "applications", "providers", "resources", "certs", "tokens", "records", "webhooks", "syncers", "groups", "keys", "sessions", "verifications", "forms", "tickets", "ldap" };
        var yarpRoutes = new List<RouteConfig>
        {
            new RouteConfig
            {
                RouteId = "livekitRtcRoute",
                ClusterId = "livekitCluster",
                AuthorizationPolicy = "Anonymous",
                Match = new RouteMatch { Path = "/rtc/{**catch-all}" }
            },
            new RouteConfig
            {
                RouteId = "livekitTwirpRoute",
                ClusterId = "livekitCluster",
                AuthorizationPolicy = "Anonymous",
                Match = new RouteMatch { Path = "/twirp/{**catch-all}" }
            }
        };

        if (systemConfig.ProviderType == IdpType.BuiltInCasdoor)
        {
            yarpRoutes.Add(new RouteConfig
            {
                RouteId = "casdoorRoute_Static",
                ClusterId = "casdoorCluster",
                AuthorizationPolicy = "Anonymous",
                Match = new RouteMatch { Path = "/static/{**catch-all}" }
            });
            yarpRoutes.Add(new RouteConfig
            {
                RouteId = "casdoorRoute_Assets",
                ClusterId = "casdoorCluster",
                AuthorizationPolicy = "Anonymous",
                Match = new RouteMatch { Path = "/assets/{**catch-all}" }
            });
            yarpRoutes.Add(new RouteConfig
            {
                RouteId = "casdoorRoute_Api",
                ClusterId = "casdoorCluster",
                AuthorizationPolicy = "Anonymous",
                Match = new RouteMatch { Path = "/api/{**catch-all}" }
            });
            yarpRoutes.Add(new RouteConfig
            {
                RouteId = "casdoorRoute_WellKnown",
                ClusterId = "casdoorCluster",
                AuthorizationPolicy = "Anonymous",
                Match = new RouteMatch { Path = "/.well-known/{**catch-all}" }
            });
            yarpRoutes.Add(new RouteConfig
            {
                RouteId = "casdoorRoute_AuthCallback",
                ClusterId = "casdoorCluster",
                AuthorizationPolicy = "Anonymous",
                Match = new RouteMatch { Path = "/AuthCallbackHandler.js" }
            });

            foreach (var route in casdoorUiRoutes)
            {
                yarpRoutes.Add(new RouteConfig
                {
                    RouteId = $"casdoorRoute_{route}",
                    ClusterId = "casdoorCluster",
                    AuthorizationPolicy = "Anonymous",
                    Match = new RouteMatch { Path = $"/{route}/{{**catch-all}}" }
                });
            }
        }

        var clusters = new[]
        {
            new ClusterConfig
            {
                ClusterId = "casdoorCluster",
                Destinations = new Dictionary<string, DestinationConfig>(StringComparer.OrdinalIgnoreCase)
                {
                    { "casdoorDest", new DestinationConfig { Address = "http://127.0.0.1:8000" } }
                }
            },
            new ClusterConfig
            {
                ClusterId = "livekitCluster",
                Destinations = new Dictionary<string, DestinationConfig>(StringComparer.OrdinalIgnoreCase)
                {
                    { "livekitDest", new DestinationConfig { Address = "http://127.0.0.1:7880" } }
                }
            }
        };

        return new SpokesProxyConfig(yarpRoutes, clusters, new CancellationChangeToken(_changeTokenSource.Token));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _systemConfigRepo.OnSaved -= HandleConfigSaved;
        _changeTokenSource.Dispose();
    }
}

/// <summary>
/// Represents an immutable snapshot of YARP route and cluster configuration with a change notification token.
/// </summary>
public class SpokesProxyConfig : IProxyConfig
{
    public IReadOnlyList<RouteConfig> Routes { get; }
    public IReadOnlyList<ClusterConfig> Clusters { get; }
    public IChangeToken ChangeToken { get; }

    public SpokesProxyConfig(IReadOnlyList<RouteConfig> routes, IReadOnlyList<ClusterConfig> clusters, IChangeToken changeToken)
    {
        Routes = routes;
        Clusters = clusters;
        ChangeToken = changeToken;
    }
}
