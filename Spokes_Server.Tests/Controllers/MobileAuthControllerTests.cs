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
using Spokes_Server.Core.Utilities;
using Xunit;

namespace Spokes_Server.Tests.Controllers
{
    public class MobileAuthControllerTests : TestDataTestBase
    {
        private readonly ServiceProvider _serviceProvider;
        private readonly Database _db;
        private readonly SessionService _sessionService;
        private readonly RSA _rsa;
        private readonly RsaSecurityKey _securityKey;
        private readonly JsonWebKey _jwk;
        private const string TestAuthority = "http://auth.example.com";
        private const string TestClientId = "spokes-mobile-app";
        private const string TestClientSecret = "secret123";

        public MobileAuthControllerTests()
        {
            var services = new ServiceCollection();

            var mockConfig = new Mock<IConfiguration>();
            mockConfig.Setup(c => c["DataPath"]).Returns(_testDataPath);
            services.AddSingleton<IConfiguration>(mockConfig.Object);

            services.AddLogging(builder => builder.AddConsole());
            services.AddSingleton<Spokes_Server.Core.Services.Core.EncryptionService>();
            services.AddSpokesDatabase();
            services.AddScoped<SessionService>();

            _serviceProvider = services.BuildServiceProvider();
            _db = _serviceProvider.GetRequiredService<Database>();
            _sessionService = _serviceProvider.GetRequiredService<SessionService>();

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
            TimeSpan? lifetime = null)
        {
            var key = signingKey ?? _securityKey;
            var signingCredentials = new SigningCredentials(key, SecurityAlgorithms.RsaSha256);

            var claims = new List<Claim>();
            if (sub != null) claims.Add(new Claim("sub", sub));
            if (name != null) claims.Add(new Claim("name", name));
            if (email != null) claims.Add(new Claim("email", email));

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

        private MobileAuthController CreateController(Func<HttpRequestMessage, HttpResponseMessage>? handler = null)
        {
            var h = handler ?? CreateDefaultIdpHandler();
            var factory = CreateHttpClientFactory(h);
            var controller = new MobileAuthController(_db, factory, _sessionService)
            {
                ControllerContext = new ControllerContext
                {
                    HttpContext = new DefaultHttpContext()
                }
            };
            return controller;
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
            var controller = CreateController(req =>
            {
                return new HttpResponseMessage(HttpStatusCode.InternalServerError);
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

            var req = new MobileAuthController.MobileExchangeRequest
            {
                Code = "expired_code",
                CodeVerifier = "verifier",
                RedirectUri = "spokes://callback",
                DeviceId = "dev_1"
            };

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

            var req = new MobileAuthController.MobileExchangeRequest
            {
                Code = "code",
                CodeVerifier = "verifier",
                RedirectUri = "spokes://callback",
                DeviceId = "dev_1"
            };

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

            var req = new MobileAuthController.MobileExchangeRequest
            {
                Code = "code",
                CodeVerifier = "verifier",
                RedirectUri = "spokes://callback",
                DeviceId = "dev_1"
            };

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

            var req = new MobileAuthController.MobileExchangeRequest
            {
                Code = "code",
                CodeVerifier = "verifier",
                RedirectUri = "spokes://callback",
                DeviceId = "dev_1"
            };

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

            var req = new MobileAuthController.MobileExchangeRequest
            {
                Code = "code",
                CodeVerifier = "verifier",
                RedirectUri = "spokes://callback",
                DeviceId = "dev_1"
            };

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

            var req = new MobileAuthController.MobileExchangeRequest
            {
                Code = "code",
                CodeVerifier = "verifier",
                RedirectUri = "spokes://callback",
                DeviceId = "dev_1"
            };

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

            var req = new MobileAuthController.MobileExchangeRequest
            {
                Code = "code",
                CodeVerifier = "verifier",
                RedirectUri = "spokes://callback",
                DeviceId = "dev_1"
            };

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
            });

            var req = new MobileAuthController.MobileExchangeRequest
            {
                Code = "code",
                CodeVerifier = "verifier",
                RedirectUri = "spokes://callback",
                DeviceId = "dev_1"
            };

            var result = await controller.Exchange(req);

            Assert.IsType<OkObjectResult>(result);
            Assert.True(calledFallbackEndpoint);
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

            var req = new MobileAuthController.MobileExchangeRequest
            {
                Code = "code",
                CodeVerifier = "verifier",
                RedirectUri = "spokes://callback",
                DeviceId = "alice_phone"
            };

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

            var req = new MobileAuthController.MobileExchangeRequest
            {
                Code = "code",
                CodeVerifier = "verifier",
                RedirectUri = "spokes://callback",
                DeviceId = "bob_ipad"
            };

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

            var req = new MobileAuthController.MobileExchangeRequest
            {
                Code = "code",
                CodeVerifier = "verifier",
                RedirectUri = "spokes://callback",
                DeviceId = "charlie_device"
            };

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

            var req = new MobileAuthController.MobileExchangeRequest
            {
                Code = "code",
                CodeVerifier = "verifier",
                RedirectUri = "spokes://callback",
                DeviceId = "device_single"
            };

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

            var req = new MobileAuthController.MobileExchangeRequest
            {
                Code = "code",
                CodeVerifier = "verifier",
                RedirectUri = "spokes://callback",
                DeviceId = "device_unreg"
            };

            var result = await controller.Exchange(req);

            var unauthResult = Assert.IsType<UnauthorizedObjectResult>(result);
            var res = Assert.IsType<SpokesResult>(unauthResult.Value);
            Assert.False(res.IsSuccess);
            Assert.Equal("Account is inactive, suspended, banned, or pending approval.", res.ErrorMessage);
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

            var req = new MobileAuthController.MobileExchangeRequest
            {
                Code = "code",
                CodeVerifier = "verifier",
                RedirectUri = "spokes://callback",
                DeviceId = "device_existing"
            };

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
                OidcSub = "sub_matched_by_field",
                IsActive = true
            };
            _db.Employees.Save(emp);

            var idToken = GenerateIdToken(sub: "sub_matched_by_field", name: "PreConfigured Employee", email: "preconfigured@example.com");
            var controller = CreateController(CreateDefaultIdpHandler(idToken));

            var req = new MobileAuthController.MobileExchangeRequest
            {
                Code = "code",
                CodeVerifier = "verifier",
                RedirectUri = "spokes://callback",
                DeviceId = "device_match"
            };

            var result = await controller.Exchange(req);

            Assert.IsType<OkObjectResult>(result);

            var account = _db.OpenIdAccounts.GetBySub("sub_matched_by_field");
            Assert.NotNull(account);
            Assert.Equal(emp.Id, account.LinkedEmployeeId);
        }

        [Fact]
        public async Task Exchange_ExistingEmployeeMatchedByEmail_LinksAccount()
        {
            var emp = new Employee
            {
                FirstName = "Email",
                LastName = "Match",
                Email = "MATCHME@example.com",
                OidcSub = null,
                IsActive = true
            };
            _db.Employees.Save(emp);

            // Token has same email in lowercase
            var idToken = GenerateIdToken(sub: "sub_by_email", name: "Email Match", email: "matchme@example.com");
            var controller = CreateController(CreateDefaultIdpHandler(idToken));

            var req = new MobileAuthController.MobileExchangeRequest
            {
                Code = "code",
                CodeVerifier = "verifier",
                RedirectUri = "spokes://callback",
                DeviceId = "device_email_match"
            };

            var result = await controller.Exchange(req);

            Assert.IsType<OkObjectResult>(result);

            var account = _db.OpenIdAccounts.GetBySub("sub_by_email");
            Assert.NotNull(account);
            Assert.Equal(emp.Id, account.LinkedEmployeeId);
        }

        [Fact]
        public async Task Exchange_ExistingEmployeeWithEmptyEmail_UpdatesEmailFromToken()
        {
            var emp = new Employee
            {
                FirstName = "NoEmail",
                LastName = "User",
                Email = "",
                OidcSub = "sub_no_email",
                IsActive = true
            };
            _db.Employees.Save(emp);

            var idToken = GenerateIdToken(sub: "sub_no_email", name: "NoEmail User", email: "populated@example.com");
            var controller = CreateController(CreateDefaultIdpHandler(idToken));

            var req = new MobileAuthController.MobileExchangeRequest
            {
                Code = "code",
                CodeVerifier = "verifier",
                RedirectUri = "spokes://callback",
                DeviceId = "device_pop_email"
            };

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

            var req = new MobileAuthController.MobileExchangeRequest
            {
                Code = "code",
                CodeVerifier = "verifier",
                RedirectUri = "spokes://callback",
                DeviceId = "dev_inactive"
            };

            var result = await controller.Exchange(req);

            var unauth = Assert.IsType<UnauthorizedObjectResult>(result);
            var res = Assert.IsType<SpokesResult>(unauth.Value);
            Assert.False(res.IsSuccess);
            Assert.Equal("Account is inactive, suspended, banned, or pending approval.", res.ErrorMessage);
        }

        [Fact]
        public async Task Exchange_SuspendedEmployee_ReturnsUnauthorized()
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

            var req = new MobileAuthController.MobileExchangeRequest
            {
                Code = "code",
                CodeVerifier = "verifier",
                RedirectUri = "spokes://callback",
                DeviceId = "dev_suspended"
            };

            var result = await controller.Exchange(req);

            var unauth = Assert.IsType<UnauthorizedObjectResult>(result);
            var res = Assert.IsType<SpokesResult>(unauth.Value);
            Assert.False(res.IsSuccess);
            Assert.Equal("Account is inactive, suspended, banned, or pending approval.", res.ErrorMessage);
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

            var req = new MobileAuthController.MobileExchangeRequest
            {
                Code = "code",
                CodeVerifier = "verifier",
                RedirectUri = "spokes://callback",
                DeviceId = "dev_banned"
            };

            var result = await controller.Exchange(req);

            var unauth = Assert.IsType<UnauthorizedObjectResult>(result);
            var res = Assert.IsType<SpokesResult>(unauth.Value);
            Assert.False(res.IsSuccess);
            Assert.Equal("Account is inactive, suspended, banned, or pending approval.", res.ErrorMessage);
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

            var req = new MobileAuthController.MobileExchangeRequest
            {
                Code = "valid_code",
                CodeVerifier = "valid_verifier",
                RedirectUri = "spokes://callback",
                DeviceId = "my_iphone_15"
            };

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

            var req = new MobileAuthController.MobileExchangeRequest
            {
                Code = "code",
                CodeVerifier = "verifier",
                RedirectUri = "spokes://callback",
                DeviceId = "my_reused_device"
            };

            var result = await controller.Exchange(req);

            Assert.IsType<OkObjectResult>(result);

            var sessions = _db.DeviceSessions.GetAll().Where(s => s.EmployeeId == emp.Id).ToList();
            var session = Assert.Single(sessions);
            Assert.Equal("session_existing_dev", session.Id);
            Assert.NotEqual("old_token_hash", session.TokenHash);
            Assert.Equal(idToken, session.IdToken);
        }

        #endregion
    }
}
