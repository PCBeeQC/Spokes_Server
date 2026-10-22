using System;
using System.Net.Http;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Spokes_Server.Core.Services.Identity;

public class CasdoorConnectionCredentials
{
    public string BaseUrl { get; set; } = "http://127.0.0.1:8000";
    public string ClientId { get; set; } = "";
    public string ClientSecret { get; set; } = "";
}

public class CasdoorApplicationBrandingRequest
{
    public CasdoorConnectionCredentials Credentials { get; set; } = new();
    public string CompanyName { get; set; } = "";
    public string LogoUrl { get; set; } = "";
    public string FaviconUrl { get; set; } = "";
    public string ServerPublicUrl { get; set; } = "";
}

public class CasdoorApiClient
{
    private readonly IHttpClientFactory? _httpClientFactory;
    private readonly ILogger<CasdoorApiClient>? _logger;

    public CasdoorApiClient(IHttpClientFactory? httpClientFactory = null, ILogger<CasdoorApiClient>? logger = null)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public async Task<bool> UpdateApplicationBrandingAsync(
        CasdoorApplicationBrandingRequest request,
        CancellationToken cancellationToken = default)
    {
        if (request == null) throw new ArgumentNullException(nameof(request));

        try
        {
            using var http = _httpClientFactory != null
                ? _httpClientFactory.CreateClient("Casdoor")
                : new HttpClient();

            http.BaseAddress = new Uri(request.Credentials.BaseUrl);

            var clientId = request.Credentials.ClientId;
            var clientSecret = request.Credentials.ClientSecret;

            var getRes = await http.GetAsync($"/api/get-application?id=admin/spokes&clientId={clientId}&clientSecret={clientSecret}", cancellationToken);
            if (!getRes.IsSuccessStatusCode)
            {
                _logger?.LogWarning("[CasdoorApiClient] Failed to get application: HTTP {StatusCode}", getRes.StatusCode);
                return false;
            }

            var jsonStr = await getRes.Content.ReadAsStringAsync(cancellationToken);
            var root = JsonNode.Parse(jsonStr);
            var data = root?["data"];
            if (data == null)
            {
                _logger?.LogWarning("[CasdoorApiClient] No data payload found in Casdoor get-application response");
                return false;
            }

            data["displayName"] = request.CompanyName;
            data["logo"] = request.LogoUrl;
            data["favicon"] = request.FaviconUrl;

            var signinRedirectUri = request.ServerPublicUrl.TrimEnd('/') + "/signin-oidc";
            var signoutRedirectUri = request.ServerPublicUrl.TrimEnd('/') + "/signout-callback-oidc";
            var mobileLogoutRedirectUri = request.ServerPublicUrl.TrimEnd('/') + "/sso/mobile-logout-complete";
            data["redirectUris"] = new JsonArray { signinRedirectUri, signoutRedirectUri, mobileLogoutRedirectUri, "spokes://auth-callback" };

            data["clientId"] = clientId;
            data["clientSecret"] = clientSecret;

            var content = new StringContent(data.ToJsonString(), System.Text.Encoding.UTF8, "application/json");
            var postRes = await http.PostAsync($"/api/update-application?id=admin/spokes&clientId={clientId}&clientSecret={clientSecret}", content, cancellationToken);
            return postRes.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "[CasdoorApiClient] Exception while updating Casdoor application branding");
            return false;
        }
    }
}
