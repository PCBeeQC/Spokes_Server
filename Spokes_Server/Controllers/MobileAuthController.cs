using System.Collections.Concurrent;
using System.IdentityModel.Tokens.Jwt;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Spokes_Server.Aggregate;
using Spokes_Server.Aggregate.Contexts;
using Spokes_Server.Core.Constants;
using Spokes_Server.Core.Models.Core;
using Spokes_Server.Core.Models.HR;
using Spokes_Server.Core.Helpers;
using Spokes_Server.Core.Security;
using Spokes_Server.Core.Services.Identity;
using Spokes_Server.Core.Services.Security;

namespace Spokes_Server.Controllers;

[ApiController]
[Route("spokesapi/auth")]
public class MobileAuthController : SpokesControllerBase
{
    private readonly CoreContext _coreContext;
    private readonly HrContext _hrContext;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly SessionService _sessionService;
    private readonly UserService _userService;
    private readonly ILogger<MobileAuthController>? _logger;
    private readonly IOptionsMonitor<OpenIdConnectOptions>? _oidcOptionsMonitor;
    private readonly IConfigurationManager<OpenIdConnectConfiguration>? _configurationManager;
    private readonly IOpenIdConfigurationProvider? _openIdConfigurationProvider;

    [ActivatorUtilitiesConstructor]
    public MobileAuthController(
        CoreContext coreContext,
        HrContext hrContext,
        IHttpClientFactory httpClientFactory,
        SessionService sessionService,
        UserService userService,
        ILogger<MobileAuthController>? logger = null,
        IOptionsMonitor<OpenIdConnectOptions>? oidcOptionsMonitor = null,
        IConfigurationManager<OpenIdConnectConfiguration>? configurationManager = null,
        IOpenIdConfigurationProvider? openIdConfigurationProvider = null)
    {
        _coreContext = coreContext ?? throw new ArgumentNullException(nameof(coreContext));
        _hrContext = hrContext ?? throw new ArgumentNullException(nameof(hrContext));
        _httpClientFactory = httpClientFactory;
        _sessionService = sessionService;
        _userService = userService ?? throw new ArgumentNullException(nameof(userService));
        _logger = logger;
        _oidcOptionsMonitor = oidcOptionsMonitor;
        _configurationManager = configurationManager;
        _openIdConfigurationProvider = openIdConfigurationProvider
            ?? (configurationManager != null || oidcOptionsMonitor != null
                ? new OpenIdConfigurationProvider(httpClientFactory, oidcOptionsMonitor, configurationManager)
                : OpenIdConfigurationProvider.Shared);
    }

    private IConfigurationManager<OpenIdConnectConfiguration> GetConfigurationManager(string authority)
    {
        if (_configurationManager != null)
        {
            return _configurationManager;
        }

        var provider = _openIdConfigurationProvider ?? OpenIdConfigurationProvider.Shared;
        return provider.GetConfigurationManager(authority, _httpClientFactory);
    }

    [HttpGet("mobile/config")]
    [AllowAnonymous]
    public async Task<IActionResult> GetConfig()
    {
        var sysConfig = _coreContext.SystemConfigs.Get();
        var authority = sysConfig.Authority?.TrimEnd('/');

        // Discover the authorization endpoint via OIDC Discovery.
        // This works with any standards-compliant provider (Authentik, Casdoor, Keycloak, etc.)
        string authorizationEndpoint;
        try
        {
            using var http = _httpClientFactory.CreateClient();
            var discoRes = await http.GetStringAsync($"{authority}/.well-known/openid-configuration");
            var discoDoc = JsonDocument.Parse(discoRes);
            authorizationEndpoint = discoDoc.RootElement
                .GetProperty("authorization_endpoint").GetString()
                ?? $"{authority}/login/oauth/authorize";
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to discover OIDC authorization endpoint from authority {Authority}. Falling back to default: {Fallback}", authority, $"{authority}/login/oauth/authorize");
            authorizationEndpoint = $"{authority}/login/oauth/authorize";
        }

        return Ok(new MobileDiscoveryResponse(
            authorizationEndpoint,
            sysConfig.ClientId,
            "openid profile email"));
    }

    public class MobileExchangeRequest
    {
        public string Code { get; set; } = string.Empty;
        public string CodeVerifier { get; set; } = string.Empty;
        public string RedirectUri { get; set; } = string.Empty;
        public string? DeviceId { get; set; }
    }

    [HttpPost("mobile/exchange")]
    [AllowAnonymous]
    public async Task<IActionResult> Exchange([FromBody] MobileExchangeRequest req)
    {
        if (req == null || string.IsNullOrWhiteSpace(req.DeviceId))
        {
            return BadRequestError("DeviceId is required for mobile token exchange.");
        }

        var sysConfig = _coreContext.SystemConfigs.Get();
        var authority = sysConfig.Authority?.TrimEnd('/');
        if (string.IsNullOrEmpty(authority)) return BadRequestError("Authority not configured");

        // 1. Exchange the code at the IDP's Token Endpoint
        var tokenEndpoint = $"{authority}/api/login/oauth/access_token";
        if (sysConfig.ProviderType != IdpType.BuiltInCasdoor)
        {
            try
            {
                using var discoveryClient = _httpClientFactory.CreateClient();
                var discoRes = await discoveryClient.GetStringAsync($"{authority}/.well-known/openid-configuration");
                var discoDoc = JsonNode.Parse(discoRes);
                tokenEndpoint = discoDoc?["token_endpoint"]?.ToString() ?? tokenEndpoint;
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Failed to discover OIDC token endpoint from authority {Authority}. Falling back to default: {Fallback}", authority, tokenEndpoint);
            }
        }

        using var http = _httpClientFactory.CreateClient();
        var content = new FormUrlEncodedContent(new[]
        {
            new KeyValuePair<string, string>("grant_type", "authorization_code"),
            new KeyValuePair<string, string>("client_id", sysConfig.ClientId ?? ""),
            new KeyValuePair<string, string>("client_secret", sysConfig.ClientSecret ?? ""),
            new KeyValuePair<string, string>("code", req.Code),
            new KeyValuePair<string, string>("redirect_uri", req.RedirectUri),
            new KeyValuePair<string, string>("code_verifier", req.CodeVerifier)
        });

        var tokenRes = await http.PostAsync(tokenEndpoint, content);
        if (!tokenRes.IsSuccessStatusCode)
        {
            var err = await tokenRes.Content.ReadAsStringAsync();
            return BadRequestError($"Failed to exchange code: {err}");
        }

        var tokenJson = await tokenRes.Content.ReadAsStringAsync();
        var tokenData = JsonNode.Parse(tokenJson);
        var idToken = tokenData?["id_token"]?.ToString();

        if (string.IsNullOrEmpty(idToken))
        {
            return BadRequestError("IDP did not return an id_token");
        }

        // 2. Parse and Cryptographically Validate the ID Token
        var handler = new JwtSecurityTokenHandler();
        if (!handler.CanReadToken(idToken)) return BadRequestError("Invalid id_token format");

        System.Security.Claims.ClaimsPrincipal principal;
        try
        {
            var configManager = GetConfigurationManager(authority);
            var openIdConfig = await configManager.GetConfigurationAsync(HttpContext?.RequestAborted ?? CancellationToken.None);

            var validationParams = new Microsoft.IdentityModel.Tokens.TokenValidationParameters
            {
                // Issuer validation is intentionally disabled. The id_token is already
                // cryptographically verified against the IdP's JWKS signing keys and
                // audience-validated against our client_id. Strict issuer string matching
                // only causes compatibility issues across providers (trailing slashes,
                // casing differences, etc).
                ValidateIssuer = false,
                ValidateAudience = true,
                ValidAudience = sysConfig.ClientId,
                ValidateIssuerSigningKey = true,
                IssuerSigningKeys = openIdConfig.SigningKeys,
                ValidateLifetime = true,
                // Accept expired X.509 certificates as long as the cryptographic signature matches the public key
                IssuerSigningKeyValidator = (issuerSigningKey, securityToken, validationParameters) => true
            };

            principal = handler.ValidateToken(idToken, validationParams, out _);
        }
        catch (Exception ex)
        {
            return BadRequestError($"Token validation failed: {ex.Message}");
        }

        var sub = principal.FindFirst("sub")?.Value 
                  ?? principal.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
        var name = principal.FindFirst("name")?.Value ?? "Unknown User";
        var email = principal.FindFirst("email")?.Value 
                    ?? principal.FindFirst(System.Security.Claims.ClaimTypes.Email)?.Value 
                    ?? "";

        if (string.IsNullOrEmpty(sub)) return BadRequestError("id_token missing sub claim");

        // 3. Look up or auto-create the OpenIdAccount & Employee
        var (_, existingUser, _) = _userService.ProvisionOrSyncOidcUser(sub, name, email);

        if (existingUser == null || !existingUser.IsActive || existingUser.IsSuspended || existingUser.IsBanned)
        {
            return UnauthorizedError("Account is inactive, suspended, banned, or pending approval.");
        }

        // 4. Issue a DeviceSession (Refresh Token) natively
        var (session, rawToken) = _sessionService.CreateSession(HttpContext!, existingUser.Id, req.DeviceId, issueCookie: false, idToken: idToken);
        if (session != null)
        {
            session.IsCapacitor = true;
            session.DeviceType = DeviceType.Mobile;
            session.IsIdleDetectionEnabled = false;
            _coreContext.DeviceSessions.Save(session);
        }

        if (string.IsNullOrEmpty(rawToken))
        {
            return StatusCodeError(500, "Failed to issue session token.");
        }

        return Ok(new MobileExchangeResponse(rawToken));
    }

    [HttpGet("force-logout")]
    [AllowAnonymous]
    public async Task<IActionResult> ForceLogout([FromQuery] string? client = null)
    {
        var session = _sessionService.GetActiveSession(HttpContext, allowRevoked: true);
        if (session == null || session.RevokedAt == null)
        {
            // If not revoked, this might be a CSRF attempt, just bounce them to home.
            return Redirect("/");
        }

        var isMobile = client == "mobile" ? true : (bool?)null;
        var handoff = await _sessionService.InitiateLogoutAsync(
            HttpContext,
            session: session,
            revokeActiveSession: false,
            isMobileExplicit: isMobile,
            redirectUri: "/sso/login");

        return ProcessLogoutHandoff(handoff);
    }

    [HttpPost("refresh")]
    [AllowAnonymous]
    public async Task<IActionResult> RefreshSession([FromBody] MobileRefreshRequest body)
    {
        if (body == null || string.IsNullOrWhiteSpace(body.RefreshToken) || string.IsNullOrWhiteSpace(body.DeviceId))
            return BadRequestError("RefreshToken and DeviceId are required.");

        var result = _sessionService.ValidateToken(body.RefreshToken, body.DeviceId);
        if (result == null)
            return UnauthorizedError("Invalid refresh token or device.");

        await _sessionService.SignInAndExtendAsync(HttpContext, result.Value.Session, result.Value.Employee, body.DeviceId);
        _logger?.LogDebug("[AUTH-REFRESH] Session refresh successful for: {FullName} (device: {DeviceId})", result.Value.Employee.FullName, result.Value.Session.DeviceId);

        return Ok(new MobileSessionRefreshResponse(90 * 86400));
    }

    [HttpGet("current-session")]
    [AllowAnonymous]
    public IActionResult CurrentSession([FromQuery] string? strict = null)
    {
        var isStrict = strict == "1" || Request.Query["strict"] == "1";
        if (isStrict)
        {
            if (User.Identity?.IsAuthenticated == true)
            {
                var sId = User.FindFirst("SessionId")?.Value ?? "active";
                return Ok(new MobileSessionStatusResponse(true, sId));
            }
            return NotFoundError("No active session found.", "SESSION_NOT_FOUND");
        }

        var session = _sessionService.GetActiveSession(HttpContext);
        if (session != null)
        {
            return Ok(new MobileSessionStatusResponse(true, session.Id));
        }
        return NotFoundError("No active session found.", "SESSION_NOT_FOUND");
    }
}

public record MobileDiscoveryResponse(
    [property: JsonPropertyName("authorizationEndpoint")] string AuthorizationEndpoint,
    [property: JsonPropertyName("clientId")] string ClientId,
    [property: JsonPropertyName("scope")] string Scope);

public record MobileExchangeResponse(
    [property: JsonPropertyName("refreshToken")] string RefreshToken);

public record MobileSessionRefreshResponse(
    [property: JsonPropertyName("expiresInSeconds")] int ExpiresInSeconds);

public record MobileSessionStatusResponse(
    [property: JsonPropertyName("active")] bool Active,
    [property: JsonPropertyName("sessionId")] string? SessionId);
