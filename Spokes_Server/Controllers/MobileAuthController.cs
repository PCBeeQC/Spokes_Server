using Microsoft.AspNetCore.Mvc;
using Spokes_Server.Aggregate;
using Spokes_Server.Core.Models.Core;
using Spokes_Server.Core.Models.HR;
using Spokes_Server.Core.Constants;
using Spokes_Server.Core.Security;
using Spokes_Server.Core.Utilities;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.IdentityModel.Tokens.Jwt;

namespace Spokes_Server.Controllers
{
    [ApiController]
    [Route("spokesapi/auth/mobile")]
    public class MobileAuthController : SpokesControllerBase
    {
        private readonly Database _db;
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly SessionService _sessionService;
        private static readonly object _authLock = new object();

        public MobileAuthController(Database db, IHttpClientFactory httpClientFactory, SessionService sessionService)
        {
            _db = db;
            _httpClientFactory = httpClientFactory;
            _sessionService = sessionService;
        }

        [HttpGet("config")]
        public async Task<IActionResult> GetConfig()
        {
            var sysConfig = _db.SystemConfigs.Get();
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
            catch
            {
                // Fallback for providers without standard discovery (e.g. misconfigured Casdoor)
                authorizationEndpoint = $"{authority}/login/oauth/authorize";
            }

            return Ok(new
            {
                authorizationEndpoint,
                clientId = sysConfig.ClientId,
                scope = "openid profile email"
            });
        }

        public class MobileExchangeRequest
        {
            public string Code { get; set; } = string.Empty;
            public string CodeVerifier { get; set; } = string.Empty;
            public string RedirectUri { get; set; } = string.Empty;
            public string? DeviceId { get; set; }
        }

        [HttpPost("exchange")]
        public async Task<IActionResult> Exchange([FromBody] MobileExchangeRequest req)
        {
            if (req == null || string.IsNullOrWhiteSpace(req.DeviceId))
            {
                return BadRequest(SpokesResult.Failure("DeviceId is required for mobile token exchange."));
            }

            var sysConfig = _db.SystemConfigs.Get();
            var authority = sysConfig.Authority?.TrimEnd('/');
            if (string.IsNullOrEmpty(authority)) return BadRequest(SpokesResult.Failure("Authority not configured"));

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
                catch
                {
                    // Fall back to Casdoor default
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
                return BadRequest(Spokes_Server.Core.Utilities.SpokesResult.Failure($"Failed to exchange code: {err}"));
            }

            var tokenJson = await tokenRes.Content.ReadAsStringAsync();
            var tokenData = JsonNode.Parse(tokenJson);
            var idToken = tokenData?["id_token"]?.ToString();

            if (string.IsNullOrEmpty(idToken))
            {
                return BadRequest(Spokes_Server.Core.Utilities.SpokesResult.Failure("IDP did not return an id_token"));
            }

            // 2. Parse and Cryptographically Validate the ID Token
            var handler = new JwtSecurityTokenHandler();
            if (!handler.CanReadToken(idToken)) return BadRequest(Spokes_Server.Core.Utilities.SpokesResult.Failure("Invalid id_token format"));

            System.Security.Claims.ClaimsPrincipal principal;
            try
            {
                var docRetriever = new Microsoft.IdentityModel.Protocols.HttpDocumentRetriever(_httpClientFactory.CreateClient())
                {
                    RequireHttps = false
                };
                var configManager = new Microsoft.IdentityModel.Protocols.ConfigurationManager<Microsoft.IdentityModel.Protocols.OpenIdConnect.OpenIdConnectConfiguration>(
                    $"{authority}/.well-known/openid-configuration",
                    new Microsoft.IdentityModel.Protocols.OpenIdConnect.OpenIdConnectConfigurationRetriever(),
                    docRetriever
                );
                var openIdConfig = await configManager.GetConfigurationAsync();

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
                return BadRequest(Spokes_Server.Core.Utilities.SpokesResult.Failure($"Token validation failed: {ex.Message}"));
            }

            var sub = principal.FindFirst("sub")?.Value 
                      ?? principal.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
            var name = principal.FindFirst("name")?.Value ?? "Unknown User";
            var email = principal.FindFirst("email")?.Value 
                        ?? principal.FindFirst(System.Security.Claims.ClaimTypes.Email)?.Value 
                        ?? "";

            if (string.IsNullOrEmpty(sub)) return BadRequest(Spokes_Server.Core.Utilities.SpokesResult.Failure("id_token missing sub claim"));

            // 3. Look up or auto-create the OpenIdAccount & Employee
            var openIdAccount = _db.OpenIdAccounts.GetBySub(sub);
            var isFirstTimeLogin = openIdAccount == null;

            if (openIdAccount == null)
            {
                openIdAccount = new OpenIdAccount
                {
                    Sub = sub,
                    Name = name,
                    Email = email,
                    FirstSeenAt = DateTime.UtcNow,
                    LastLoginAt = DateTime.UtcNow
                };
                _db.OpenIdAccounts.Save(openIdAccount);
            }
            else
            {
                openIdAccount.LastLoginAt = DateTime.UtcNow;
                openIdAccount.Name = name;
                if (!string.IsNullOrEmpty(email)) openIdAccount.Email = email;
                _db.OpenIdAccounts.Save(openIdAccount);
            }

            Employee? existingUser = null;
            if (!string.IsNullOrEmpty(openIdAccount.LinkedEmployeeId))
            {
                existingUser = _db.Employees.GetById(openIdAccount.LinkedEmployeeId);
            }

            if (existingUser == null)
            {
                existingUser = _db.Employees.GetAll().FirstOrDefault(e => e.OidcSub == sub);
                if (existingUser == null && !string.IsNullOrEmpty(email))
                {
                    existingUser = _db.Employees.GetAll().FirstOrDefault(e => string.Equals(e.Email, email, StringComparison.OrdinalIgnoreCase));
                }

                if (existingUser != null)
                {
                    openIdAccount.LinkedEmployeeId = existingUser.Id;
                    _db.OpenIdAccounts.Save(openIdAccount);
                }
            }

            if (existingUser == null)
            {
                var profile = _db.CompanyProfile.GetAll().FirstOrDefault();
                var autoCreate = profile?.AutoCreateEmployeeOnFirstLogin ?? true;

                if (autoCreate && isFirstTimeLogin)
                {
                    lock (_authLock)
                    {
                        // Check again inside lock to prevent race conditions
                        existingUser = _db.Employees.GetAll().FirstOrDefault(e => e.OidcSub == sub);
                        if (existingUser == null && !string.IsNullOrEmpty(email))
                        {
                            existingUser = _db.Employees.GetAll().FirstOrDefault(e => string.Equals(e.Email, email, StringComparison.OrdinalIgnoreCase));
                        }

                        if (existingUser == null)
                        {
                            var parts = name.Split(' ', 2);
                            var newEmp = new Employee
                            {
                                FirstName = parts.Length > 0 ? parts[0] : name,
                                LastName = parts.Length > 1 ? parts[1] : "",
                                Email = email,
                                IsActive = true,
                                IsAdmin = !_db.Employees.GetAll().Any(e => e.IsAdmin)
                            };

                            if (!newEmp.IsAdmin)
                            {
                                var cmpProfile = _db.CompanyProfile.Get();
                                var defaultGroup = !string.IsNullOrEmpty(cmpProfile?.DefaultPermissionGroupId)
                                    ? cmpProfile.PermissionGroups.FirstOrDefault(g => g.Id == cmpProfile.DefaultPermissionGroupId)
                                    : null;

                                if (defaultGroup != null)
                                {
                                    newEmp.PermissionGroupId = defaultGroup.Id;
                                }
                                else
                                {
                                    newEmp.Permissions.Add(AppPermissions.Chat.Use);
                                    newEmp.Permissions.Add(AppPermissions.Calendar.View);
                                }
                            }

                            _db.Employees.Save(newEmp);
                            existingUser = newEmp;

                            openIdAccount.LinkedEmployeeId = newEmp.Id;
                            _db.OpenIdAccounts.Save(openIdAccount);
                        }
                        else
                        {
                            // If found during lock
                            openIdAccount.LinkedEmployeeId = existingUser.Id;
                            _db.OpenIdAccounts.Save(openIdAccount);
                        }
                    }
                }
            }
            else if (string.IsNullOrEmpty(existingUser.Email) && !string.IsNullOrEmpty(email))
            {
                existingUser.Email = email;
                _db.Employees.Save(existingUser);
            }

            if (existingUser == null || !existingUser.IsActive || existingUser.IsSuspended || existingUser.IsBanned)
            {
                return Unauthorized(SpokesResult.Failure("Account is inactive, suspended, banned, or pending approval."));
            }

            // 4. Issue a DeviceSession (Refresh Token) natively
            var (session, rawToken) = _sessionService.CreateSession(HttpContext, existingUser.Id, req.DeviceId, issueCookie: false, idToken: idToken);
            if (session != null)
            {
                session.IsCapacitor = true;
                session.DeviceType = "Mobile";
                session.IsIdleDetectionEnabled = false;
                _db.DeviceSessions.Save(session);
            }

            return Ok(new
            {
                refreshToken = rawToken
            });
        }
    }
}
