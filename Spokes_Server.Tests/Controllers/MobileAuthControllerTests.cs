using System;
using System.Collections.Generic;
using System.IdentityModel.Tokens.Jwt;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;
using Moq;
using Spokes_Server.Aggregate;
using Spokes_Server.Controllers;
using Spokes_Server.Core.Constants;
using Spokes_Server.Core.Models.Core;
using Spokes_Server.Core.Models.HR;
using Spokes_Server.Core.Security;
using Spokes_Server.Core.Services.Security;
using Spokes_Server.Core.Helpers;
using Xunit;

namespace Spokes_Server.Tests.Controllers;

[Collection("OpenIdConfigurationCache")]
public class MobileAuthControllerTests : TestDataTestBase
{
    private readonly ServiceProvider _serviceProvider;
    private readonly Database _db;
    private readonly SessionService _sessionService;
    private readonly UserService _userService;
    private readonly MobileOAuthChallengeService _challengeService;
    private readonly RSA _rsa;
    private readonly RsaSecurityKey _securityKey;
    private readonly JsonWebKey _jwk;
    private const string TestAuthority = "http://auth.example.com";
    private const string TestClientId = "spokes-mobile-app";
    private const string TestClientSecret = "secret123";
    private const string DefaultTestNonce = "test-mobile-oidc-nonce-value-0123456789";
    private const string ValidTestVerifier = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789-._~";

    public MobileAuthControllerTests()
    {
        var services = new ServiceCollection();

        var mockConfig = new Mock<IConfiguration>();
        mockConfig.Setup(c => c["DataPath"]).Returns(_testDataPath);
        mockConfig.Setup(c => c["SPOKES_MASTER_PASSWORD"]).Returns("UnitTest-Master-Password-2026!");
        services.AddSingleton<IConfiguration>(mockConfig.Object);

        services.AddLogging(builder => builder.AddConsole());
        services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme).AddCookie();
        var mockAuth = new Mock<Microsoft.AspNetCore.Components.Authorization.AuthenticationStateProvider>();
        services.AddSingleton(mockAuth.Object);
        services.AddSingleton<EncryptionService>();
        services.AddSingleton<IOpenIdConfigurationProvider, OpenIdConfigurationProvider>();
        services.AddSingleton<MobileOAuthChallengeService>();
        services.AddSpokesDatabase();
        services.AddScoped<SessionService>();
        services.AddScoped<UserService>();

        _serviceProvider = services.BuildServiceProvider();
        _db = _serviceProvider.GetRequiredService<Database>();
        _sessionService = _serviceProvider.GetRequiredService<SessionService>();
        _userService = _serviceProvider.GetRequiredService<UserService>();
        _challengeService = _serviceProvider.GetRequiredService<MobileOAuthChallengeService>();

        // Setup RSA key and JWK for token signing and validation
        _rsa = RSA.Create(2048);
        _securityKey = new RsaSecurityKey(_rsa) { KeyId = "test-mobile-key-id" };
        _jwk = JsonWebKeyConverter.ConvertFromRSASecurityKey(_securityKey);
        _jwk.KeyId = "test-mobile-key-id";
        _jwk.Use = "sig";
        _jwk.Alg = SecurityAlgorithms.RsaSha256;

        // Setup standard default system config
        var sysConfig = new SystemConfig
        {
            Authority = TestAuthority,
            ClientId = TestClientId,
            ClientSecret = TestClientSecret,
            ProviderType = IdpType.External
        };
        _db.SystemConfigs.Save(sysConfig);
    }

    public override void Dispose()
    {
        _serviceProvider.GetService<IOpenIdConfigurationProvider>()?.ClearCache();
        _rsa.Dispose();
        _serviceProvider.Dispose();
        base.Dispose();
    }

    #region Helpers

    private class MockHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _handler;

        public MockHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> handler)
        {
            _handler = handler;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(_handler(request));
        }
    }

    private IHttpClientFactory CreateHttpClientFactory(Func<HttpRequestMessage, HttpResponseMessage> handler)
    {
        var mockFactory = new Mock<IHttpClientFactory>();
        mockFactory.Setup(f => f.CreateClient(It.IsAny<string>()))
            .Returns(() => new HttpClient(new MockHttpMessageHandler(handler)));
        return mockFactory.Object;
    }

    private string GenerateIdToken(
        string? sub = "sub_12345",
        string? name = "Jane Doe",
        string? email = "jane@example.com",
        string? audience = TestClientId,
        SecurityKey? signingKey = null,
        TimeSpan? lifetime = null,
        string? nonce = DefaultTestNonce)
    {
        var key = signingKey ?? _securityKey;
        var signingCredentials = new SigningCredentials(key, SecurityAlgorithms.RsaSha256);

        var claims = new List<Claim>();
        if (sub != null) claims.Add(new Claim("sub", sub));
        if (name != null) claims.Add(new Claim("name", name));
        if (email != null) claims.Add(new Claim("email", email));
        if (nonce != null) claims.Add(new Claim("nonce", nonce));

        var token = new JwtSecurityToken(
            issuer: TestAuthority,
            audience: audience,
            claims: claims,
            notBefore: DateTime.UtcNow.AddMinutes(-5),
            expires: DateTime.UtcNow.Add(lifetime ?? TimeSpan.FromHours(1)),
            signingCredentials: signingCredentials
        );

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    private Func<HttpRequestMessage, HttpResponseMessage> CreateDefaultIdpHandler(string? idTokenToReturn = null)
    {
        var idToken = idTokenToReturn ?? GenerateIdToken();

        var discoveryDoc = JsonSerializer.Serialize(new
        {
            issuer = TestAuthority,
            authorization_endpoint = $"{TestAuthority}/oauth/authorize",
            token_endpoint = $"{TestAuthority}/oauth/token",
            jwks_uri = $"{TestAuthority}/.well-known/jwks.json"
        });

        var jwksDoc = JsonSerializer.Serialize(new
        {
            keys = new[] { _jwk }
        });

        var tokenDoc = JsonSerializer.Serialize(new
        {
            access_token = "mock_access_token_123",
            id_token = idToken,
            token_type = "Bearer",
            expires_in = 3600
        });

        return req =>
        {
            var uri = req.RequestUri!.ToString();

            if (uri.Contains(".well-known/openid-configuration"))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(discoveryDoc, Encoding.UTF8, "application/json")
                };
            }

            if (uri.Contains(".well-known/jwks.json"))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(jwksDoc, Encoding.UTF8, "application/json")
                };
            }

            if (req.Method == HttpMethod.Post && (uri.Contains("/oauth/token") || uri.Contains("/api/login/oauth/access_token")))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(tokenDoc, Encoding.UTF8, "application/json")
                };
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound)
            {
                Content = new StringContent("Not Found")
            };
        };
    }

    private MobileAuthController CreateController(Func<HttpRequestMessage, HttpResponseMessage>? handler = null, ILogger<MobileAuthController>? logger = null)
    {
        _serviceProvider.GetService<IOpenIdConfigurationProvider>()?.ClearCache();
        var h = handler ?? CreateDefaultIdpHandler();
        var factory = CreateHttpClientFactory(h);
        var oidcProvider = _serviceProvider.GetRequiredService<IOpenIdConfigurationProvider>();
        var controller = new MobileAuthController(
            _db.CoreContext,
            factory,
            _sessionService,
            _userService,
            logger,
            openIdConfigurationProvider: oidcProvider,
            challengeService: _challengeService)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    RequestServices = _serviceProvider
                }
            }
        };
        return controller;
    }

    private MobileAuthController.MobileExchangeRequest CreateValidExchangeRequest(
        string code = "code",
        string deviceId = "dev_1",
        string? codeVerifier = null,
        string? redirectUri = null)
    {
        var state = _challengeService.SeedChallengeForTesting(deviceId, DefaultTestNonce);
        return new MobileAuthController.MobileExchangeRequest
        {
            Code = code,
            CodeVerifier = codeVerifier ?? ValidTestVerifier,
            RedirectUri = redirectUri ?? MobileAuthController.AllowedMobileRedirectUri,
            DeviceId = deviceId,
            State = state
        };
    }

    #endregion

    #region GetConfig Tests

    [Fact]
    public async Task GetConfig_DiscoverySucceeds_ReturnsDiscoveredAuthorizationEndpointAndClientInfo()
    {
        var discoveryPayload = JsonSerializer.Serialize(new
        {
            authorization_endpoint = "https://custom-idp.com/oauth/v2/auth"
        });

        var controller = CreateController(req =>
        {
            if (req.RequestUri!.ToString().Contains(".well-known/openid-configuration"))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(discoveryPayload, Encoding.UTF8, "application/json")
                };
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var result = await controller.GetConfig();

        var okResult = Assert.IsType<OkObjectResult>(result);
        var json = JsonSerializer.Serialize(okResult.Value);
        var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.Equal("https://custom-idp.com/oauth/v2/auth", root.GetProperty("authorizationEndpoint").GetString());
        Assert.Equal(TestClientId, root.GetProperty("clientId").GetString());
        Assert.Equal("openid profile email", root.GetProperty("scope").GetString());
    }

    [Fact]
    public async Task GetConfig_DiscoveryResponseMissingAuthorizationEndpoint_FallsBackToDefaultEndpoint()
    {
        var discoveryPayload = JsonSerializer.Serialize(new
        {
            some_other_field = "test"
        });

        var controller = CreateController(req =>
        {
            if (req.RequestUri!.ToString().Contains(".well-known/openid-configuration"))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(discoveryPayload, Encoding.UTF8, "application/json")
                };
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var result = await controller.GetConfig();

        var okResult = Assert.IsType<OkObjectResult>(result);
        var json = JsonSerializer.Serialize(okResult.Value);
        var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.Equal($"{TestAuthority}/login/oauth/authorize", root.GetProperty("authorizationEndpoint").GetString());
        Assert.Equal(TestClientId, root.GetProperty("clientId").GetString());
        Assert.Equal("openid profile email", root.GetProperty("scope").GetString());
    }

    [Fact]
    public async Task GetConfig_DiscoveryFails_FallsBackToDefaultEndpoint()
    {
        var mockLogger = new Mock<ILogger<MobileAuthController>>();
        var controller = CreateController(req =>
        {
            return new HttpResponseMessage(HttpStatusCode.InternalServerError);
        }, mockLogger.Object);

        var result = await controller.GetConfig();

        var okResult = Assert.IsType<OkObjectResult>(result);
        var json = JsonSerializer.Serialize(okResult.Value);
        var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.Equal($"{TestAuthority}/login/oauth/authorize", root.GetProperty("authorizationEndpoint").GetString());
        Assert.Equal(TestClientId, root.GetProperty("clientId").GetString());
        Assert.Equal("openid profile email", root.GetProperty("scope").GetString());

        mockLogger.Verify(
            x => x.Log(
                LogLevel.Warning,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, t) => v.ToString()!.Contains("Failed to discover OIDC authorization endpoint")),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }

    [Fact]
    public async Task GetConfig_AuthorityHasTrailingSlash_TrimsSlashCorrectly()
    {
        var sysConfig = _db.SystemConfigs.Get();
        sysConfig.Authority = "http://auth.example.com/";
        _db.SystemConfigs.Save(sysConfig);

        var controller = CreateController(req =>
        {
            // Return 500 so fallback endpoint is used
            return new HttpResponseMessage(HttpStatusCode.InternalServerError);
        });

        var result = await controller.GetConfig();

        var okResult = Assert.IsType<OkObjectResult>(result);
        var json = JsonSerializer.Serialize(okResult.Value);
        var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.Equal("http://auth.example.com/login/oauth/authorize", root.GetProperty("authorizationEndpoint").GetString());
    }

    #endregion

    #region Exchange Validation & Early Exit Tests

    [Fact]
    public async Task Exchange_NullRequest_ReturnsBadRequest()
    {
        var controller = CreateController();

        var result = await controller.Exchange(null!);

        var badReq = Assert.IsType<BadRequestObjectResult>(result);
        var res = Assert.IsType<SpokesResult>(badReq.Value);
        Assert.False(res.IsSuccess);
        Assert.Equal("DeviceId is required for mobile token exchange.", res.ErrorMessage);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Exchange_NullOrWhitespaceDeviceId_ReturnsBadRequest(string? deviceId)
    {
        var controller = CreateController();
        var req = new MobileAuthController.MobileExchangeRequest
        {
            Code = "code123",
            CodeVerifier = "verifier123",
            RedirectUri = "spokes://callback",
            DeviceId = deviceId
        };

        var result = await controller.Exchange(req);

        var badReq = Assert.IsType<BadRequestObjectResult>(result);
        var res = Assert.IsType<SpokesResult>(badReq.Value);
        Assert.False(res.IsSuccess);
        Assert.Equal("DeviceId is required for mobile token exchange.", res.ErrorMessage);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task Exchange_AuthorityNotConfigured_ReturnsBadRequest(string? authority)
    {
        var sysConfig = _db.SystemConfigs.Get();
        sysConfig.Authority = authority!;
        _db.SystemConfigs.Save(sysConfig);

        var controller = CreateController();
        var req = new MobileAuthController.MobileExchangeRequest
        {
            Code = "code123",
            CodeVerifier = "verifier123",
            RedirectUri = "spokes://callback",
            DeviceId = "device_1"
        };

        var result = await controller.Exchange(req);

        var badReq = Assert.IsType<BadRequestObjectResult>(result);
        var res = Assert.IsType<SpokesResult>(badReq.Value);
        Assert.False(res.IsSuccess);
        Assert.Equal("Authority not configured", res.ErrorMessage);
    }

    [Fact]
    public async Task Exchange_IdpTokenEndpointReturnsError_ReturnsBadRequest()
    {
        var controller = CreateController(req =>
        {
            var uri = req.RequestUri!.ToString();
            if (uri.Contains(".well-known/openid-configuration"))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(JsonSerializer.Serialize(new
                    {
                        issuer = TestAuthority,
                        token_endpoint = $"{TestAuthority}/oauth/token"
                    }), Encoding.UTF8, "application/json")
                };
            }
            if (req.Method == HttpMethod.Post && uri.Contains("/oauth/token"))
            {
                return new HttpResponseMessage(HttpStatusCode.BadRequest)
                {
                    Content = new StringContent("invalid_grant: code expired", Encoding.UTF8, "text/plain")
                };
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var req = CreateValidExchangeRequest(code: "expired_code", deviceId: "dev_1");

        var result = await controller.Exchange(req);

        var badReq = Assert.IsType<BadRequestObjectResult>(result);
        var res = Assert.IsType<SpokesResult>(badReq.Value);
        Assert.False(res.IsSuccess);
        Assert.Contains("Failed to exchange code: invalid_grant: code expired", res.ErrorMessage);
    }

    [Fact]
    public async Task Exchange_IdpResponseMissingIdToken_ReturnsBadRequest()
    {
        var controller = CreateController(req =>
        {
            var uri = req.RequestUri!.ToString();
            if (uri.Contains(".well-known/openid-configuration"))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(JsonSerializer.Serialize(new
                    {
                        issuer = TestAuthority,
                        token_endpoint = $"{TestAuthority}/oauth/token"
                    }), Encoding.UTF8, "application/json")
                };
            }
            if (req.Method == HttpMethod.Post && uri.Contains("/oauth/token"))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(JsonSerializer.Serialize(new
                    {
                        access_token = "access_token_only",
                        token_type = "Bearer"
                    }), Encoding.UTF8, "application/json")
                };
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var req = CreateValidExchangeRequest();

        var result = await controller.Exchange(req);

        var badReq = Assert.IsType<BadRequestObjectResult>(result);
        var res = Assert.IsType<SpokesResult>(badReq.Value);
        Assert.False(res.IsSuccess);
        Assert.Equal("IDP did not return an id_token", res.ErrorMessage);
    }

    [Fact]
    public async Task Exchange_IdpReturnsInvalidIdTokenFormat_ReturnsBadRequest()
    {
        var controller = CreateController(req =>
        {
            var uri = req.RequestUri!.ToString();
            if (uri.Contains(".well-known/openid-configuration"))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(JsonSerializer.Serialize(new
                    {
                        issuer = TestAuthority,
                        token_endpoint = $"{TestAuthority}/oauth/token"
                    }), Encoding.UTF8, "application/json")
                };
            }
            if (req.Method == HttpMethod.Post && uri.Contains("/oauth/token"))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(JsonSerializer.Serialize(new
                    {
                        id_token = "not-a-jwt"
                    }), Encoding.UTF8, "application/json")
                };
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var req = CreateValidExchangeRequest();

        var result = await controller.Exchange(req);

        var badReq = Assert.IsType<BadRequestObjectResult>(result);
        var res = Assert.IsType<SpokesResult>(badReq.Value);
        Assert.False(res.IsSuccess);
        Assert.Equal("Invalid id_token format", res.ErrorMessage);
    }

    [Fact]
    public async Task Exchange_TokenValidationFails_InvalidSignature_ReturnsBadRequest()
    {
        // Sign the token with an untrusted different RSA key
        using var untrustedRsa = RSA.Create(2048);
        var untrustedKey = new RsaSecurityKey(untrustedRsa) { KeyId = "untrusted-key" };
        var untrustedToken = GenerateIdToken(signingKey: untrustedKey);

        var controller = CreateController(CreateDefaultIdpHandler(untrustedToken));

        var req = CreateValidExchangeRequest();

        var result = await controller.Exchange(req);

        var badReq = Assert.IsType<BadRequestObjectResult>(result);
        var res = Assert.IsType<SpokesResult>(badReq.Value);
        Assert.False(res.IsSuccess);
        Assert.Contains("Token validation failed:", res.ErrorMessage);
    }

    [Fact]
    public async Task Exchange_TokenValidationFails_WrongAudience_ReturnsBadRequest()
    {
        // Token has an audience mismatch
        var wrongAudienceToken = GenerateIdToken(audience: "wrong-audience-client");
        var controller = CreateController(CreateDefaultIdpHandler(wrongAudienceToken));

        var req = CreateValidExchangeRequest();

        var result = await controller.Exchange(req);

        var badReq = Assert.IsType<BadRequestObjectResult>(result);
        var res = Assert.IsType<SpokesResult>(badReq.Value);
        Assert.False(res.IsSuccess);
        Assert.Contains("Token validation failed:", res.ErrorMessage);
    }

    [Fact]
    public async Task Exchange_TokenMissingSubClaim_ReturnsBadRequest()
    {
        // Token has no sub claim
        var noSubToken = GenerateIdToken(sub: null);
        var controller = CreateController(CreateDefaultIdpHandler(noSubToken));

        var req = CreateValidExchangeRequest();

        var result = await controller.Exchange(req);

        var badReq = Assert.IsType<BadRequestObjectResult>(result);
        var res = Assert.IsType<SpokesResult>(badReq.Value);
        Assert.False(res.IsSuccess);
        Assert.Equal("id_token missing sub claim", res.ErrorMessage);
    }

    [Fact]
    public async Task Exchange_BuiltInCasdoor_UsesCasdoorTokenEndpointWithoutDiscovery()
    {
        var sysConfig = _db.SystemConfigs.Get();
        sysConfig.ProviderType = IdpType.BuiltInCasdoor;
        _db.SystemConfigs.Save(sysConfig);

        var defaultHandler = CreateDefaultIdpHandler();
        var calledCasdoorEndpoint = false;

        var controller = CreateController(req =>
        {
            if (req.RequestUri!.ToString().Contains("/api/login/oauth/access_token"))
            {
                calledCasdoorEndpoint = true;
            }
            return defaultHandler(req);
        });

        var req = CreateValidExchangeRequest();

        var result = await controller.Exchange(req);

        Assert.IsType<OkObjectResult>(result);
        Assert.True(calledCasdoorEndpoint);
    }

    [Fact]
    public async Task Exchange_ExternalProviderDiscoveryFails_FallsBackToDefaultTokenEndpoint()
    {
        var sysConfig = _db.SystemConfigs.Get();
        sysConfig.ProviderType = IdpType.External;
        _db.SystemConfigs.Save(sysConfig);

        var defaultHandler = CreateDefaultIdpHandler();
        var calledFallbackEndpoint = false;
        var discoveryAttemptCount = 0;
        var mockLogger = new Mock<ILogger<MobileAuthController>>();

        var controller = CreateController(req =>
        {
            var uri = req.RequestUri!.ToString();

            // Fail the first discovery attempt (for token endpoint resolution in step 1)
            // but allow subsequent discovery requests (for ConfigurationManager in step 2)
            if (uri.Contains(".well-known/openid-configuration"))
            {
                discoveryAttemptCount++;
                if (discoveryAttemptCount == 1)
                {
                    return new HttpResponseMessage(HttpStatusCode.InternalServerError);
                }
            }

            if (uri.Contains("/api/login/oauth/access_token"))
            {
                calledFallbackEndpoint = true;
            }

            return defaultHandler(req);
        }, mockLogger.Object);

        var req = CreateValidExchangeRequest();

        var result = await controller.Exchange(req);

        Assert.IsType<OkObjectResult>(result);
        Assert.True(calledFallbackEndpoint);

        mockLogger.Verify(
            x => x.Log(
                LogLevel.Warning,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, t) => v.ToString()!.Contains("Failed to discover OIDC token endpoint")),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }

    #endregion

    #region User Provisioning & Permissions Tests

    [Fact]
    public async Task Exchange_FirstTimeLogin_AutoCreatesFirstEmployeeAsAdmin()
    {
        // Ensure no employees exist initially
        Assert.Empty(_db.Employees.GetAll());

        var idToken = GenerateIdToken(sub: "new_sub_admin", name: "Alice Admin", email: "alice@example.com");
        var controller = CreateController(CreateDefaultIdpHandler(idToken));

        var req = CreateValidExchangeRequest(deviceId: "alice_phone");

        var result = await controller.Exchange(req);

        var okResult = Assert.IsType<OkObjectResult>(result);
        var json = JsonSerializer.Serialize(okResult.Value);
        var doc = JsonDocument.Parse(json);
        Assert.True(doc.RootElement.TryGetProperty("refreshToken", out var tokenProp));
        Assert.False(string.IsNullOrEmpty(tokenProp.GetString()));

        // Verify OpenIdAccount was created
        var account = _db.OpenIdAccounts.GetBySub("new_sub_admin");
        Assert.NotNull(account);
        Assert.Equal("Alice Admin", account.Name);
        Assert.Equal("alice@example.com", account.Email);
        Assert.False(string.IsNullOrEmpty(account.LinkedEmployeeId));

        // Verify Employee was created as Admin
        var emp = _db.Employees.GetById(account.LinkedEmployeeId!);
        Assert.NotNull(emp);
        Assert.Equal("Alice", emp.FirstName);
        Assert.Equal("Admin", emp.LastName);
        Assert.Equal("alice@example.com", emp.Email);
        Assert.True(emp.IsAdmin);
        Assert.True(emp.IsActive);
    }

    [Fact]
    public async Task Exchange_FirstTimeLogin_SubsequentEmployee_AssignsDefaultPermissionGroup()
    {
        // Create an existing admin employee
        var adminEmp = new Employee
        {
            FirstName = "Existing",
            LastName = "Admin",
            Email = "admin@example.com",
            IsAdmin = true,
            IsActive = true
        };
        _db.Employees.Save(adminEmp);

        // Configure default permission group in company profile
        var profile = new CompanyProfile
        {
            DefaultPermissionGroupId = "dev-group-id",
            PermissionGroups = new List<PermissionGroup>
            {
                new PermissionGroup
                {
                    Id = "dev-group-id",
                    Name = "Developers",
                    Permissions = new List<string> { AppPermissions.Chat.Use, AppPermissions.Projects.View }
                }
            }
        };
        _db.CompanyProfile.Save(profile);

        var idToken = GenerateIdToken(sub: "sub_bob", name: "Bob Developer", email: "bob@example.com");
        var controller = CreateController(CreateDefaultIdpHandler(idToken));

        var req = CreateValidExchangeRequest(deviceId: "bob_ipad");

        var result = await controller.Exchange(req);

        Assert.IsType<OkObjectResult>(result);

        var account = _db.OpenIdAccounts.GetBySub("sub_bob");
        Assert.NotNull(account);

        var emp = _db.Employees.GetById(account.LinkedEmployeeId!);
        Assert.NotNull(emp);
        Assert.Equal("Bob", emp.FirstName);
        Assert.Equal("Developer", emp.LastName);
        Assert.False(emp.IsAdmin);
        Assert.Equal("dev-group-id", emp.PermissionGroupId);
    }

    [Fact]
    public async Task Exchange_FirstTimeLogin_SubsequentEmployee_FallbackPermissions_WhenNoDefaultGroupConfigured()
    {
        // Existing admin exists
        _db.Employees.Save(new Employee { FirstName = "Admin", Email = "admin@ex.com", IsAdmin = true, IsActive = true });

        // Company profile with no default permission group
        var profile = new CompanyProfile
        {
            DefaultPermissionGroupId = "",
            PermissionGroups = new List<PermissionGroup>()
        };
        _db.CompanyProfile.Save(profile);

        var idToken = GenerateIdToken(sub: "sub_charlie", name: "Charlie Member", email: "charlie@example.com");
        var controller = CreateController(CreateDefaultIdpHandler(idToken));

        var req = CreateValidExchangeRequest(deviceId: "charlie_device");

        var result = await controller.Exchange(req);

        Assert.IsType<OkObjectResult>(result);

        var account = _db.OpenIdAccounts.GetBySub("sub_charlie");
        Assert.NotNull(account);

        var emp = _db.Employees.GetById(account.LinkedEmployeeId!);
        Assert.NotNull(emp);
        Assert.False(emp.IsAdmin);
        Assert.Contains(AppPermissions.Chat.Use, emp.Permissions);
        Assert.Contains(AppPermissions.Calendar.View, emp.Permissions);
    }

    [Fact]
    public async Task Exchange_FirstTimeLogin_SingleName_SplitsFirstNameWithEmptyLastName()
    {
        var idToken = GenerateIdToken(sub: "sub_single", name: "Cher", email: "cher@example.com");
        var controller = CreateController(CreateDefaultIdpHandler(idToken));

        var req = CreateValidExchangeRequest(deviceId: "device_single");

        var result = await controller.Exchange(req);

        Assert.IsType<OkObjectResult>(result);

        var account = _db.OpenIdAccounts.GetBySub("sub_single");
        Assert.NotNull(account);

        var emp = _db.Employees.GetById(account.LinkedEmployeeId!);
        Assert.NotNull(emp);
        Assert.Equal("Cher", emp.FirstName);
        Assert.Equal("", emp.LastName);
    }

    [Fact]
    public async Task Exchange_FirstTimeLogin_AutoCreateDisabled_ReturnsUnauthorized()
    {
        var profile = new CompanyProfile
        {
            AutoCreateEmployeeOnFirstLogin = false
        };
        _db.CompanyProfile.Save(profile);

        var idToken = GenerateIdToken(sub: "sub_unregistered", name: "Unregistered User", email: "unreg@example.com");
        var controller = CreateController(CreateDefaultIdpHandler(idToken));

        var req = CreateValidExchangeRequest(deviceId: "device_unreg");

        var result = await controller.Exchange(req);

        var unauthResult = Assert.IsType<UnauthorizedObjectResult>(result);
        var res = Assert.IsType<SpokesResult>(unauthResult.Value);
        Assert.False(res.IsSuccess);
        Assert.Equal("Account is inactive, banned, or pending approval.", res.ErrorMessage);
    }

    #endregion

    #region Existing User Matching & Updates Tests

    [Fact]
    public async Task Exchange_ExistingOpenIdAccount_UpdatesLastLoginAndEmail()
    {
        var emp = new Employee
        {
            FirstName = "Existing",
            LastName = "User",
            Email = "old@example.com",
            IsActive = true
        };
        _db.Employees.Save(emp);

        var oldLoginTime = DateTime.UtcNow.AddDays(-5);
        var account = new OpenIdAccount
        {
            Sub = "sub_existing_acc",
            Name = "Old Name",
            Email = "old@example.com",
            LinkedEmployeeId = emp.Id,
            FirstSeenAt = oldLoginTime,
            LastLoginAt = oldLoginTime
        };
        _db.OpenIdAccounts.Save(account);

        var idToken = GenerateIdToken(sub: "sub_existing_acc", name: "Updated Name", email: "updated@example.com");
        var controller = CreateController(CreateDefaultIdpHandler(idToken));

        var req = CreateValidExchangeRequest(deviceId: "device_existing");

        var result = await controller.Exchange(req);

        Assert.IsType<OkObjectResult>(result);

        var updatedAccount = _db.OpenIdAccounts.GetBySub("sub_existing_acc");
        Assert.NotNull(updatedAccount);
        Assert.Equal("Updated Name", updatedAccount.Name);
        Assert.Equal("updated@example.com", updatedAccount.Email);
        Assert.True(updatedAccount.LastLoginAt > oldLoginTime);
    }

    [Fact]
    public async Task Exchange_ExistingEmployeeMatchedByOidcSub_LinksAccount()
    {
        var emp = new Employee
        {
            FirstName = "PreConfigured",
            LastName = "Employee",
            Email = "preconfigured@example.com",
            MigratedOidcSub = "sub_matched_by_field",
            IsActive = true
        };
        _db.Employees.Save(emp);

        var migrationStep = new Spokes_Server.Core.Services.Migrations.LegacyOidcSubMigrationStep(_db.Employees, _db.OpenIdAccounts);
        await migrationStep.ExecuteAsync();

        var idToken = GenerateIdToken(sub: "sub_matched_by_field", name: "PreConfigured Employee", email: "preconfigured@example.com");
        var controller = CreateController(CreateDefaultIdpHandler(idToken));

        var req = CreateValidExchangeRequest(deviceId: "device_match");

        var result = await controller.Exchange(req);

        Assert.IsType<OkObjectResult>(result);

        var account = _db.OpenIdAccounts.GetBySub("sub_matched_by_field");
        Assert.NotNull(account);
        Assert.Equal(emp.Id, account.LinkedEmployeeId);
    }

    [Fact]
    public async Task Exchange_ExistingEmployeeWithSameEmail_CreatesSeparateAccountAndNeverAutoLinksByEmail()
    {
        var emp = new Employee
        {
            FirstName = "Email",
            LastName = "Match",
            Email = "MATCHME@example.com",
            IsAdmin = true,
            IsActive = true
        };
        _db.Employees.Save(emp);

        // Unlinked sub with matching email must provision a brand-new non-admin Employee instead of taking over emp.Id
        var idToken = GenerateIdToken(sub: "sub_by_email", name: "Email Match", email: "matchme@example.com");
        var controller = CreateController(CreateDefaultIdpHandler(idToken));

        var req = CreateValidExchangeRequest(deviceId: "device_email_match");

        var result = await controller.Exchange(req);

        Assert.IsType<OkObjectResult>(result);

        var account = _db.OpenIdAccounts.GetBySub("sub_by_email");
        Assert.NotNull(account);
        Assert.False(string.IsNullOrEmpty(account.LinkedEmployeeId));
        Assert.NotEqual(emp.Id, account.LinkedEmployeeId);
        var provisionedEmp = _db.Employees.GetById(account.LinkedEmployeeId);
        Assert.NotNull(provisionedEmp);
        Assert.False(provisionedEmp!.IsAdmin);
    }

    [Fact]
    public async Task Exchange_ExistingEmployeeWithEmptyEmail_UpdatesEmailFromToken()
    {
        var emp = new Employee
        {
            FirstName = "NoEmail",
            LastName = "User",
            Email = "",
            IsActive = true
        };
        _db.Employees.Save(emp);

        _db.OpenIdAccounts.Save(new OpenIdAccount
        {
            Sub = "sub_no_email",
            LinkedEmployeeId = emp.Id
        });

        var idToken = GenerateIdToken(sub: "sub_no_email", name: "NoEmail User", email: "populated@example.com");
        var controller = CreateController(CreateDefaultIdpHandler(idToken));

        var req = CreateValidExchangeRequest(deviceId: "device_pop_email");

        var result = await controller.Exchange(req);

        Assert.IsType<OkObjectResult>(result);

        var updatedEmp = _db.Employees.GetById(emp.Id);
        Assert.NotNull(updatedEmp);
        Assert.Equal("populated@example.com", updatedEmp.Email);
    }

    #endregion

    #region Inactive, Suspended, & Banned Status Tests

    [Fact]
    public async Task Exchange_InactiveEmployee_ReturnsUnauthorized()
    {
        var emp = new Employee
        {
            FirstName = "Inactive",
            LastName = "User",
            Email = "inactive@example.com",
            IsActive = false
        };
        _db.Employees.Save(emp);

        var account = new OpenIdAccount
        {
            Sub = "sub_inactive",
            LinkedEmployeeId = emp.Id
        };
        _db.OpenIdAccounts.Save(account);

        var idToken = GenerateIdToken(sub: "sub_inactive", email: "inactive@example.com");
        var controller = CreateController(CreateDefaultIdpHandler(idToken));

        var req = CreateValidExchangeRequest(deviceId: "dev_inactive");

        var result = await controller.Exchange(req);

        var unauth = Assert.IsType<UnauthorizedObjectResult>(result);
        var res = Assert.IsType<SpokesResult>(unauth.Value);
        Assert.False(res.IsSuccess);
        Assert.Equal("Account is inactive, banned, or pending approval.", res.ErrorMessage);
    }

    [Fact]
    public async Task Exchange_SuspendedEmployee_AllowsReadOnlyExchange()
    {
        var emp = new Employee
        {
            FirstName = "Suspended",
            LastName = "User",
            Email = "suspended@example.com",
            IsActive = true,
            IsSuspended = true
        };
        _db.Employees.Save(emp);

        var account = new OpenIdAccount
        {
            Sub = "sub_suspended",
            LinkedEmployeeId = emp.Id
        };
        _db.OpenIdAccounts.Save(account);

        var idToken = GenerateIdToken(sub: "sub_suspended", email: "suspended@example.com");
        var controller = CreateController(CreateDefaultIdpHandler(idToken));

        var req = CreateValidExchangeRequest(deviceId: "dev_suspended");

        var result = await controller.Exchange(req);

        Assert.IsType<OkObjectResult>(result);
        Assert.NotEmpty(_db.DeviceSessions.GetByEmployeeId(emp.Id));
    }

    [Fact]
    public async Task Exchange_BannedEmployee_ReturnsUnauthorized()
    {
        var emp = new Employee
        {
            FirstName = "Banned",
            LastName = "User",
            Email = "banned@example.com",
            IsActive = true,
            IsBanned = true
        };
        _db.Employees.Save(emp);

        var account = new OpenIdAccount
        {
            Sub = "sub_banned",
            LinkedEmployeeId = emp.Id
        };
        _db.OpenIdAccounts.Save(account);

        var idToken = GenerateIdToken(sub: "sub_banned", email: "banned@example.com");
        var controller = CreateController(CreateDefaultIdpHandler(idToken));

        var req = CreateValidExchangeRequest(deviceId: "dev_banned");

        var result = await controller.Exchange(req);

        var unauth = Assert.IsType<UnauthorizedObjectResult>(result);
        var res = Assert.IsType<SpokesResult>(unauth.Value);
        Assert.False(res.IsSuccess);
        Assert.Equal("Account is inactive, banned, or pending approval.", res.ErrorMessage);
    }

    #endregion

    #region Session Creation & Success Tests

    [Fact]
    public async Task Exchange_SuccessfulLogin_CreatesDeviceSessionAndReturnsRefreshToken()
    {
        var emp = new Employee
        {
            FirstName = "Mobile",
            LastName = "User",
            Email = "mobile@example.com",
            IsActive = true
        };
        _db.Employees.Save(emp);

        var account = new OpenIdAccount
        {
            Sub = "sub_mobile_user",
            LinkedEmployeeId = emp.Id
        };
        _db.OpenIdAccounts.Save(account);

        var idToken = GenerateIdToken(sub: "sub_mobile_user", email: "mobile@example.com");
        var controller = CreateController(CreateDefaultIdpHandler(idToken));

        var req = CreateValidExchangeRequest(code: "valid_code", deviceId: "my_iphone_15", codeVerifier: ValidTestVerifier);

        var result = await controller.Exchange(req);

        var okResult = Assert.IsType<OkObjectResult>(result);
        var json = JsonSerializer.Serialize(okResult.Value);
        var doc = JsonDocument.Parse(json);

        var refreshToken = doc.RootElement.GetProperty("refreshToken").GetString();
        Assert.False(string.IsNullOrEmpty(refreshToken));

        // Verify DeviceSession persisted in DB
        var sessions = _db.DeviceSessions.GetAll().Where(s => s.EmployeeId == emp.Id).ToList();
        var session = Assert.Single(sessions);
        Assert.Equal("my_iphone_15", session.DeviceId);
        Assert.Equal(idToken, session.IdToken);
        Assert.False(string.IsNullOrEmpty(session.TokenHash));
    }

    [Fact]
    public async Task Exchange_ExistingDeviceSessionForSameDevice_ReusesSessionWithUpdatedToken()
    {
        var emp = new Employee
        {
            FirstName = "Existing",
            LastName = "MobileUser",
            Email = "mobileuser@example.com",
            IsActive = true
        };
        _db.Employees.Save(emp);

        var account = new OpenIdAccount
        {
            Sub = "sub_reuse_session",
            LinkedEmployeeId = emp.Id
        };
        _db.OpenIdAccounts.Save(account);

        // Pre-existing session on the same device
        var initialSession = new DeviceSession
        {
            Id = "session_existing_dev",
            EmployeeId = emp.Id,
            DeviceId = "my_reused_device",
            TokenHash = "old_token_hash",
            ExpiresAt = DateTime.UtcNow.AddDays(30)
        };
        _db.DeviceSessions.Save(initialSession);

        var idToken = GenerateIdToken(sub: "sub_reuse_session", email: "mobileuser@example.com");
        var controller = CreateController(CreateDefaultIdpHandler(idToken));

        var req = CreateValidExchangeRequest(deviceId: "my_reused_device");

        var result = await controller.Exchange(req);

        Assert.IsType<OkObjectResult>(result);

        var sessions = _db.DeviceSessions.GetAll().Where(s => s.EmployeeId == emp.Id).ToList();
        var session = Assert.Single(sessions);
        Assert.Equal("session_existing_dev", session.Id);
        Assert.NotEqual("old_token_hash", session.TokenHash);
        Assert.Equal(idToken, session.IdToken);
    }

    [Fact]
    public async Task RefreshSession_MissingParameters_ReturnsBadRequest()
    {
        var controller = CreateController();
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext()
        };

        var result = await controller.RefreshSession(new MobileRefreshRequest());

        var badRequest = Assert.IsType<BadRequestObjectResult>(result);
        var payload = Assert.IsType<SpokesResult>(badRequest.Value);
        Assert.False(payload.IsSuccess);
    }

    [Fact]
    public async Task RefreshSession_ValidToken_ReturnsOk()
    {
        var emp = new Employee { Id = "emp_refresh", FirstName = "Refresh", LastName = "User" };
        _db.Employees.Save(emp);

        var httpContext = new DefaultHttpContext
        {
            RequestServices = _serviceProvider
        };
        var (session, rawToken) = _sessionService.CreateSession(httpContext, emp.Id, "device_refresh_test", issueCookie: false);

        var controller = CreateController();
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = httpContext
        };

        var req = new MobileRefreshRequest
        {
            RefreshToken = rawToken,
            DeviceId = "device_refresh_test"
        };

        var result = await controller.RefreshSession(req);

        var ok = Assert.IsType<OkObjectResult>(result);
        Assert.NotNull(ok.Value);
    }

    [Fact]
    public void CurrentSession_StrictWithoutAuth_ReturnsNotFound()
    {
        var controller = CreateController();
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext()
        };

        var result = controller.CurrentSession(strict: "1");

        var notFound = Assert.IsType<NotFoundObjectResult>(result);
        var payload = Assert.IsType<SpokesResult>(notFound.Value);
        Assert.False(payload.IsSuccess);
    }

    [Fact]
    public async Task Exchange_ReusesCachedConfigurationManager_AcrossCalls()
    {
        var jwksRequestCount = 0;
        var defaultHandler = CreateDefaultIdpHandler();

        var controller1 = CreateController(req =>
        {
            var uri = req.RequestUri!.ToString();
            if (uri.Contains(".well-known/jwks.json"))
            {
                Interlocked.Increment(ref jwksRequestCount);
            }
            return defaultHandler(req);
        });

        var req1 = CreateValidExchangeRequest(code: "code_1", deviceId: "dev_1");
        var result1 = await controller1.Exchange(req1);
        Assert.IsType<OkObjectResult>(result1);
        Assert.Equal(1, jwksRequestCount);

        // A second controller (simulating a subsequent request) should reuse the cached ConfigurationManager
        // rather than re-querying JWKS signing keys
        var controller2 = new MobileAuthController(_db.CoreContext, CreateHttpClientFactory(req =>
        {
            var uri = req.RequestUri!.ToString();
            if (uri.Contains(".well-known/jwks.json"))
            {
                Interlocked.Increment(ref jwksRequestCount);
            }
            return defaultHandler(req);
        }), _sessionService, _userService, openIdConfigurationProvider: _serviceProvider.GetRequiredService<IOpenIdConfigurationProvider>())
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { RequestServices = _serviceProvider }
            }
        };

        var req2 = CreateValidExchangeRequest(code: "code_2", deviceId: "dev_2");
        var result2 = await controller2.Exchange(req2);
        Assert.IsType<OkObjectResult>(result2);
        // JWKS should NOT have been fetched again because ConfigurationManager is cached
        Assert.Equal(1, jwksRequestCount);
    }

    [Fact]
    public async Task Exchange_UsesInjectedConfigurationManager_WhenProvided()
    {
        var mockConfigManager = new Mock<Microsoft.IdentityModel.Protocols.IConfigurationManager<Microsoft.IdentityModel.Protocols.OpenIdConnect.OpenIdConnectConfiguration>>();
        var oidcConfig = new Microsoft.IdentityModel.Protocols.OpenIdConnect.OpenIdConnectConfiguration();
        oidcConfig.SigningKeys.Add(_securityKey);
        mockConfigManager
            .Setup(m => m.GetConfigurationAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(oidcConfig);

        var factory = CreateHttpClientFactory(CreateDefaultIdpHandler());
        var controller = new MobileAuthController(
            _db.CoreContext,
            factory,
            _sessionService,
            userService: _userService,
            logger: null,
            oidcOptionsMonitor: null,
            configurationManager: mockConfigManager.Object)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { RequestServices = _serviceProvider }
            }
        };

        var req = CreateValidExchangeRequest(code: "code_injected", deviceId: "dev_injected");

        var result = await controller.Exchange(req);
        Assert.IsType<OkObjectResult>(result);
        mockConfigManager.Verify(m => m.GetConfigurationAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Exchange_UsesOidcOptionsMonitor_WhenAuthorityMatches()
    {
        var mockConfigManager = new Mock<Microsoft.IdentityModel.Protocols.IConfigurationManager<Microsoft.IdentityModel.Protocols.OpenIdConnect.OpenIdConnectConfiguration>>();
        var oidcConfig = new Microsoft.IdentityModel.Protocols.OpenIdConnect.OpenIdConnectConfiguration();
        oidcConfig.SigningKeys.Add(_securityKey);
        mockConfigManager
            .Setup(m => m.GetConfigurationAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(oidcConfig);

        var oidcOptions = new Microsoft.AspNetCore.Authentication.OpenIdConnect.OpenIdConnectOptions
        {
            Authority = TestAuthority,
            ConfigurationManager = mockConfigManager.Object
        };
        var mockMonitor = new Mock<Microsoft.Extensions.Options.IOptionsMonitor<Microsoft.AspNetCore.Authentication.OpenIdConnect.OpenIdConnectOptions>>();
        mockMonitor.Setup(m => m.Get(Microsoft.AspNetCore.Authentication.OpenIdConnect.OpenIdConnectDefaults.AuthenticationScheme))
            .Returns(oidcOptions);

        var factory = CreateHttpClientFactory(CreateDefaultIdpHandler());
        var controller = new MobileAuthController(
            _db.CoreContext,
            factory,
            _sessionService,
            userService: _userService,
            logger: null,
            oidcOptionsMonitor: mockMonitor.Object)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { RequestServices = _serviceProvider }
            }
        };

        var req = CreateValidExchangeRequest(code: "code_monitor", deviceId: "dev_monitor");

        var result = await controller.Exchange(req);
        Assert.IsType<OkObjectResult>(result);
        mockConfigManager.Verify(m => m.GetConfigurationAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Exchange_WhenRawTokenNullOrEmpty_Returns500InternalServerError()
    {
        var mockSessionService = new Mock<SessionService>(_db.CoreContext, _db.HrContext);
        mockSessionService.Setup(s => s.CreateSession(
            It.IsAny<HttpContext>(),
            It.IsAny<string>(),
            It.IsAny<string?>(),
            It.IsAny<bool>(),
            It.IsAny<string?>()))
            .Returns((new DeviceSession { Id = "sess_1" }, (string?)null));

        var factory = CreateHttpClientFactory(CreateDefaultIdpHandler());
        var controller = new MobileAuthController(
            _db.CoreContext,
            factory,
            mockSessionService.Object,
            userService: _userService,
            logger: null)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { RequestServices = _serviceProvider }
            }
        };

        var req = CreateValidExchangeRequest(code: "valid_code", deviceId: "dev_1");

        var result = await controller.Exchange(req);
        var objectResult = Assert.IsType<ObjectResult>(result);
        Assert.Equal(500, objectResult.StatusCode);
    }

    #endregion
}
