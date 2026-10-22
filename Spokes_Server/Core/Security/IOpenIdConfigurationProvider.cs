using System.Net.Http;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;

namespace Spokes_Server.Core.Security;

/// <summary>
/// Provides cached OpenIdConnect <see cref="IConfigurationManager{OpenIdConnectConfiguration}"/> instances for OIDC discovery.
/// </summary>
public interface IOpenIdConfigurationProvider
{
    /// <summary>
    /// Gets or creates a cached OpenID Connect ConfigurationManager for the specified authority.
    /// </summary>
    IConfigurationManager<OpenIdConnectConfiguration> GetConfigurationManager(string authority);

    /// <summary>
    /// Gets or creates a cached OpenID Connect ConfigurationManager for the specified authority using the provided HTTP client factory if a new instance is needed.
    /// </summary>
    IConfigurationManager<OpenIdConnectConfiguration> GetConfigurationManager(string authority, IHttpClientFactory? httpClientFactory);

    /// <summary>
    /// Clears the cached ConfigurationManager instances (primarily used for unit testing).
    /// </summary>
    void ClearCache();

    /// <summary>
    /// Resolves the OIDC end-session endpoint for the specified authority or returns a default fallback.
    /// </summary>
    Task<string> ResolveEndSessionEndpointAsync(string? authority, CancellationToken cancellationToken = default);
}
