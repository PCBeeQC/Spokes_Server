using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using Xunit;
using System;
using Microsoft.Extensions.DependencyInjection;

namespace Spokes_Server.Tests.Integration
{
    public class SpokesTestWebApplicationFactory : WebApplicationFactory<Program>
    {
        public string TempDbPath { get; }

        public SpokesTestWebApplicationFactory()
        {
            TempDbPath = Path.Combine(Path.GetTempPath(), "Spokes_Integration_Test_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(TempDbPath);

            // Set environment variables required for Program.cs bootloader
            Environment.SetEnvironmentVariable("DataPath", TempDbPath);
            Environment.SetEnvironmentVariable("SPOKES_MASTER_PASSWORD", "TestMasterPassword123!");

            // Initialize minimal config to bypass setup middleware
            var settingsDir = Path.Combine(TempDbPath, "Settings");
            Directory.CreateDirectory(settingsDir);
            File.WriteAllText(Path.Combine(settingsDir, "system_config.json"), "{\"IsSetupComplete\": true}");
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.ConfigureAppConfiguration((context, config) =>
            {
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    { "DataPath", TempDbPath }
                });
            });
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            Environment.SetEnvironmentVariable("SPOKES_MASTER_PASSWORD", null);
            Environment.SetEnvironmentVariable("DataPath", null);
            if (Directory.Exists(TempDbPath))
            {
                try
                {
                    Directory.Delete(TempDbPath, true);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Cleanup error: {ex.Message}");
                }
            }
        }
    }

    public class BasicRoutingTests : IClassFixture<SpokesTestWebApplicationFactory>
    {
        private readonly SpokesTestWebApplicationFactory _factory;

        public BasicRoutingTests(SpokesTestWebApplicationFactory factory)
        {
            _factory = factory;
        }

        [Fact]
        public async Task AnonymousEndpoint_ReturnsSuccess()
        {
            // Arrange
            var client = _factory.CreateClient();

            // Act
            var response = await client.PostAsync("/spokesapi/push/offline?subscriptionId=test-sub-123", null);

            // Assert
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        [Fact]
        public async Task ProtectedEndpoint_WithoutAuth_RedirectsOrReturnsUnauthorized()
        {
            // Arrange
            var client = _factory.CreateClient(new WebApplicationFactoryClientOptions
            {
                AllowAutoRedirect = false
            });

            // Act
            var response = await client.GetAsync("/spokesapi/push/status");

            // Assert: API routes under /spokesapi must return 401 Unauthorized, never 302 Redirect
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }

        [Fact]
        public async Task MobileBootstrap_WithInvalidToken_RedirectsToMobileLogin()
        {
            // Arrange
            var client = _factory.CreateClient(new WebApplicationFactoryClientOptions
            {
                AllowAutoRedirect = false
            });

            var content = new FormUrlEncodedContent(new[]
            {
                new KeyValuePair<string, string>("refreshToken", "invalid_token"),
                new KeyValuePair<string, string>("deviceId", "test_device"),
                new KeyValuePair<string, string>("returnUrl", "/target-page")
            });

            // Act
            var response = await client.PostAsync("/sso/login/mobile-bootstrap", content);

            // Assert
            Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
            Assert.Equal("/mobile-login?returnUrl=%2Ftarget-page", response.Headers.Location?.OriginalString);
        }

        [Fact]
        public async Task CurrentSession_StrictWithoutAuth_ReturnsNotFound()
        {
            var client = _factory.CreateClient();
            var response = await client.GetAsync("/spokesapi/auth/current-session?strict=1");
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }

        [Fact]
        public async Task AuthRefresh_WithInvalidToken_ReturnsUnauthorized()
        {
            var client = _factory.CreateClient();
            var content = new StringContent("{\"refreshToken\":\"invalid_token\",\"deviceId\":\"dev1\"}", System.Text.Encoding.UTF8, "application/json");
            var response = await client.PostAsync("/spokesapi/auth/refresh", content);
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }

        [Fact]
        public async Task AuthRefresh_WithEmptyBody_ReturnsBadRequest()
        {
            var client = _factory.CreateClient();
            var content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json");
            var response = await client.PostAsync("/spokesapi/auth/refresh", content);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }

        [Fact]
        public async Task SsoLoginAuto_WhenLoopCookiePresentOnMobile_RedirectsToMobileLoginWithNoAuto()
        {
            var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
            var request = new HttpRequestMessage(HttpMethod.Get, "/sso/login/auto?returnUrl=%2Fdashboard");
            request.Headers.Add("Cookie", "Spokes_SSO_Attempt=1");
            request.Headers.Add("User-Agent", "Mozilla/5.0 (iPhone; CPU iPhone OS 17_0 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Mobile/15E148 Capacitor");

            var response = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
            Assert.StartsWith("/mobile-login?no_auto=1", response.Headers.Location?.OriginalString);
        }

        [Fact]
        public async Task SsoLoginAuto_WhenLoopCookiePresentOnWeb_RedirectsToSessionExpired()
        {
            var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
            var request = new HttpRequestMessage(HttpMethod.Get, "/sso/login/auto?returnUrl=%2Fdashboard");
            request.Headers.Add("Cookie", "Spokes_SSO_Attempt=1");

            var response = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
            Assert.Equal("/session-expired", response.Headers.Location?.OriginalString);
        }

        [Fact]
        public async Task AuthRefresh_WithValidTokenAndDevice_ReturnsOkAndSetsCookie()
        {
            using var scope = _factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<Spokes_Server.Aggregate.Database>();
            
            var rawToken = "refresh_valid_" + Guid.NewGuid();
            using var sha256 = System.Security.Cryptography.SHA256.Create();
            var hash = Convert.ToBase64String(sha256.ComputeHash(System.Text.Encoding.UTF8.GetBytes(rawToken)));
            
            var employee = new Spokes_Server.Core.Models.HR.Employee
            {
                Id = "emp_refresh_" + Guid.NewGuid(),
                FirstName = "Mobile",
                LastName = "User",
                IsActive = true
            };
            db.Employees.Save(employee);

            var session = new Spokes_Server.Core.Models.Core.DeviceSession
            {
                Id = "sess_refresh_" + Guid.NewGuid(),
                EmployeeId = employee.Id,
                TokenHash = hash,
                DeviceId = "dev_mobile_1",
                ExpiresAt = DateTime.UtcNow.AddDays(30)
            };
            db.DeviceSessions.Save(session);

            var client = _factory.CreateClient();
            var content = new StringContent(
                System.Text.Json.JsonSerializer.Serialize(new { refreshToken = rawToken, deviceId = "dev_mobile_1" }),
                System.Text.Encoding.UTF8,
                "application/json");

            var response = await client.PostAsync("/spokesapi/auth/refresh", content);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            var body = await response.Content.ReadAsStringAsync();
            Assert.Contains("86400", body);
            Assert.True(response.Headers.Contains("Set-Cookie"));
        }

        [Fact]
        public async Task MobileBootstrap_WithValidToken_SignsInAndSanitizesReturnUrl()
        {
            using var scope = _factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<Spokes_Server.Aggregate.Database>();

            var rawToken = "bootstrap_valid_" + Guid.NewGuid();
            using var sha256 = System.Security.Cryptography.SHA256.Create();
            var hash = Convert.ToBase64String(sha256.ComputeHash(System.Text.Encoding.UTF8.GetBytes(rawToken)));

            var employee = new Spokes_Server.Core.Models.HR.Employee
            {
                Id = "emp_boot_" + Guid.NewGuid(),
                FirstName = "Boot",
                LastName = "Tester",
                IsActive = true
            };
            db.Employees.Save(employee);

            var session = new Spokes_Server.Core.Models.Core.DeviceSession
            {
                Id = "sess_boot_" + Guid.NewGuid(),
                EmployeeId = employee.Id,
                TokenHash = hash,
                DeviceId = "dev_boot_1",
                ExpiresAt = DateTime.UtcNow.AddDays(30)
            };
            db.DeviceSessions.Save(session);

            var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
            var content = new FormUrlEncodedContent(new[]
            {
                new KeyValuePair<string, string>("refreshToken", rawToken),
                new KeyValuePair<string, string>("deviceId", "dev_boot_1"),
                new KeyValuePair<string, string>("returnUrl", "https://evil.com/phish")
            });

            var response = await client.PostAsync("/sso/login/mobile-bootstrap", content);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);

            var html = await response.Content.ReadAsStringAsync();
            Assert.Contains("window.location.replace('/')", html);
            Assert.True(response.Headers.Contains("Set-Cookie"));
        }

        [Fact]
        public async Task ForceLogout_WhenRevoked_DoesNotReturn401FromRevocationMiddleware()
        {
            using var scope = _factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<Spokes_Server.Aggregate.Database>();

            var employee = new Spokes_Server.Core.Models.HR.Employee
            {
                Id = "emp_rev_" + Guid.NewGuid(),
                FirstName = "Revoked",
                LastName = "User",
                IsActive = true
            };
            db.Employees.Save(employee);

            var rawToken = "tok_rev_" + Guid.NewGuid();
            using var sha256 = System.Security.Cryptography.SHA256.Create();
            var hash = Convert.ToBase64String(sha256.ComputeHash(System.Text.Encoding.UTF8.GetBytes(rawToken)));

            var session = new Spokes_Server.Core.Models.Core.DeviceSession
            {
                Id = "sess_rev_" + Guid.NewGuid(),
                EmployeeId = employee.Id,
                TokenHash = hash,
                DeviceId = "dev_rev_1",
                IdToken = "test_id_token",
                ExpiresAt = DateTime.UtcNow.AddDays(30)
            };
            db.DeviceSessions.Save(session);

            // 1. Bootstrap session to obtain authentic Spokes_Session_v3 cookie
            var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
            var bootstrapContent = new FormUrlEncodedContent(new[]
            {
                new KeyValuePair<string, string>("refreshToken", rawToken),
                new KeyValuePair<string, string>("deviceId", "dev_rev_1"),
                new KeyValuePair<string, string>("returnUrl", "/")
            });
            var bootstrapRes = await client.PostAsync("/sso/login/mobile-bootstrap", bootstrapContent);
            var cookie = ExtractCookieValue(bootstrapRes, "Spokes_Session_v3");
            Assert.NotEmpty(cookie);

            // 2. Revoke the session in database
            session.RevokedAt = DateTime.UtcNow.AddMinutes(-1);
            db.DeviceSessions.Save(session);

            // 3. Request force-logout carrying BOTH the revoked session cookie and refresh cookie
            var request = new HttpRequestMessage(HttpMethod.Get, "/spokesapi/auth/force-logout?client=mobile");
            request.Headers.Add("Cookie", $"Spokes_Session_v3={cookie}; Spokes_Refresh={rawToken}; Spokes_Device=dev_rev_1");

            var response = await client.SendAsync(request);
            // Must NOT be 401 Unauthorized from the post-auth revocation middleware
            Assert.NotEqual(HttpStatusCode.Unauthorized, response.StatusCode);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var html = await response.Content.ReadAsStringAsync();
            Assert.Contains("Preferences.remove", html);
        }

        [Fact]
        public async Task AuthRefresh_WithMissingDeviceId_ReturnsBadRequest()
        {
            var client = _factory.CreateClient();
            var content = new StringContent(
                System.Text.Json.JsonSerializer.Serialize(new { refreshToken = "some_valid_or_dummy_token" }),
                System.Text.Encoding.UTF8,
                "application/json");

            var response = await client.PostAsync("/spokesapi/auth/refresh", content);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }

        [Fact]
        public async Task CurrentSession_StrictWithAuthenticatedSession_ReturnsOkAndSessionId()
        {
            using var scope = _factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<Spokes_Server.Aggregate.Database>();

            var employee = new Spokes_Server.Core.Models.HR.Employee
            {
                Id = "emp_curr_" + Guid.NewGuid(),
                FirstName = "Current",
                LastName = "User",
                IsActive = true
            };
            db.Employees.Save(employee);

            var rawToken = "tok_curr_" + Guid.NewGuid();
            using var sha256 = System.Security.Cryptography.SHA256.Create();
            var hash = Convert.ToBase64String(sha256.ComputeHash(System.Text.Encoding.UTF8.GetBytes(rawToken)));

            var session = new Spokes_Server.Core.Models.Core.DeviceSession
            {
                Id = "sess_curr_" + Guid.NewGuid(),
                EmployeeId = employee.Id,
                TokenHash = hash,
                DeviceId = "dev_curr_1",
                ExpiresAt = DateTime.UtcNow.AddDays(30)
            };
            db.DeviceSessions.Save(session);

            var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
            var bootstrapContent = new FormUrlEncodedContent(new[]
            {
                new KeyValuePair<string, string>("refreshToken", rawToken),
                new KeyValuePair<string, string>("deviceId", "dev_curr_1"),
                new KeyValuePair<string, string>("returnUrl", "/")
            });
            var bootstrapRes = await client.PostAsync("/sso/login/mobile-bootstrap", bootstrapContent);
            var cookie = ExtractCookieValue(bootstrapRes, "Spokes_Session_v3");
            Assert.NotEmpty(cookie);

            var request = new HttpRequestMessage(HttpMethod.Get, "/spokesapi/auth/current-session?strict=1");
            request.Headers.Add("Cookie", $"Spokes_Session_v3={cookie}");

            var response = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            var json = await response.Content.ReadAsStringAsync();
            Assert.Contains("\"active\":true", json);
            Assert.Contains(session.Id, json);
        }

        [Fact]
        public async Task DeviceTokenAuth_WithValidBearerAndDeviceId_ReturnsOk()
        {
            using var scope = _factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<Spokes_Server.Aggregate.Database>();

            var employee = new Spokes_Server.Core.Models.HR.Employee
            {
                Id = "emp_bearer_" + Guid.NewGuid(),
                FirstName = "Bearer",
                LastName = "User",
                IsActive = true
            };
            db.Employees.Save(employee);

            var rawToken = "tok_bearer_" + Guid.NewGuid();
            using var sha256 = System.Security.Cryptography.SHA256.Create();
            var hash = Convert.ToBase64String(sha256.ComputeHash(System.Text.Encoding.UTF8.GetBytes(rawToken)));

            var session = new Spokes_Server.Core.Models.Core.DeviceSession
            {
                Id = "sess_bearer_" + Guid.NewGuid(),
                EmployeeId = employee.Id,
                TokenHash = hash,
                DeviceId = "dev_bearer_1",
                ExpiresAt = DateTime.UtcNow.AddDays(30)
            };
            db.DeviceSessions.Save(session);

            var client = _factory.CreateClient();
            var request = new HttpRequestMessage(HttpMethod.Get, "/spokesapi/push/status");
            request.Headers.Add("Authorization", $"Bearer {rawToken}");
            request.Headers.Add("X-Device-Id", "dev_bearer_1");

            var response = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        [Fact]
        public async Task RevocationMiddleware_WithRevokedCookieSession_RejectsApiWith401()
        {
            using var scope = _factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<Spokes_Server.Aggregate.Database>();

            var employee = new Spokes_Server.Core.Models.HR.Employee
            {
                Id = "emp_midrev_" + Guid.NewGuid(),
                FirstName = "MidRev",
                LastName = "User",
                IsActive = true
            };
            db.Employees.Save(employee);

            var rawToken = "tok_midrev_" + Guid.NewGuid();
            using var sha256 = System.Security.Cryptography.SHA256.Create();
            var hash = Convert.ToBase64String(sha256.ComputeHash(System.Text.Encoding.UTF8.GetBytes(rawToken)));

            var session = new Spokes_Server.Core.Models.Core.DeviceSession
            {
                Id = "sess_midrev_" + Guid.NewGuid(),
                EmployeeId = employee.Id,
                TokenHash = hash,
                DeviceId = "dev_midrev_1",
                ExpiresAt = DateTime.UtcNow.AddDays(30)
            };
            db.DeviceSessions.Save(session);

            // 1. Bootstrap session to get cookie
            var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
            var bootstrapContent = new FormUrlEncodedContent(new[]
            {
                new KeyValuePair<string, string>("refreshToken", rawToken),
                new KeyValuePair<string, string>("deviceId", "dev_midrev_1"),
                new KeyValuePair<string, string>("returnUrl", "/")
            });
            var bootstrapRes = await client.PostAsync("/sso/login/mobile-bootstrap", bootstrapContent);
            var cookie = ExtractCookieValue(bootstrapRes, "Spokes_Session_v3");
            Assert.NotEmpty(cookie);

            // 2. Mark session revoked in database
            session.RevokedAt = DateTime.UtcNow;
            db.DeviceSessions.Save(session);

            // 3. Request API endpoint with the session cookie -> Post-auth revocation middleware must reject with 401
            var request = new HttpRequestMessage(HttpMethod.Get, "/spokesapi/push/status");
            request.Headers.Add("Cookie", $"Spokes_Session_v3={cookie}");

            var response = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }

        [Fact]
        public async Task MobileAuthExchange_WithMissingDeviceId_ReturnsBadRequest()
        {
            var client = _factory.CreateClient();
            var content = new StringContent(
                System.Text.Json.JsonSerializer.Serialize(new
                {
                    code = "dummy_code",
                    codeVerifier = "dummy_verifier",
                    redirectUri = "spokes://login",
                    deviceId = ""
                }),
                System.Text.Encoding.UTF8,
                "application/json");

            var response = await client.PostAsync("/spokesapi/auth/mobile/exchange", content);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            var body = await response.Content.ReadAsStringAsync();
            Assert.Contains("DeviceId is required", body);
        }

        [Fact]
        public async Task CurrentSession_StrictWithOnlyRefreshCookie_ReturnsNotFound_WhileNonStrictReturnsOk()
        {
            using var scope = _factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<Spokes_Server.Aggregate.Database>();

            var employee = new Spokes_Server.Core.Models.HR.Employee
            {
                Id = "emp_refreshonly_" + Guid.NewGuid(),
                FirstName = "Refresh",
                LastName = "Only",
                IsActive = true
            };
            db.Employees.Save(employee);

            var rawToken = "tok_refreshonly_" + Guid.NewGuid();
            using var sha256 = System.Security.Cryptography.SHA256.Create();
            var hash = Convert.ToBase64String(sha256.ComputeHash(System.Text.Encoding.UTF8.GetBytes(rawToken)));

            var session = new Spokes_Server.Core.Models.Core.DeviceSession
            {
                Id = "sess_refreshonly_" + Guid.NewGuid(),
                EmployeeId = employee.Id,
                TokenHash = hash,
                DeviceId = "dev_refr_1",
                ExpiresAt = DateTime.UtcNow.AddDays(30)
            };
            db.DeviceSessions.Save(session);

            var client = _factory.CreateClient();

            // 1. Strict probe with ONLY refresh token/device cookie must return 404 (bypassing refresh token fallback)
            var strictRequest = new HttpRequestMessage(HttpMethod.Get, "/spokesapi/auth/current-session?strict=1");
            strictRequest.Headers.Add("Cookie", $"Spokes_Refresh={rawToken}; Spokes_Device=dev_refr_1");
            var strictResponse = await client.SendAsync(strictRequest);
            Assert.Equal(HttpStatusCode.NotFound, strictResponse.StatusCode);

            // 2. Non-strict probe with refresh token/device cookie must return 200 OK
            var normalRequest = new HttpRequestMessage(HttpMethod.Get, "/spokesapi/auth/current-session");
            normalRequest.Headers.Add("Cookie", $"Spokes_Refresh={rawToken}; Spokes_Device=dev_refr_1");
            var normalResponse = await client.SendAsync(normalRequest);
            Assert.Equal(HttpStatusCode.OK, normalResponse.StatusCode);
            var normalJson = await normalResponse.Content.ReadAsStringAsync();
            Assert.Contains("\"active\":true", normalJson);
            Assert.Contains(session.Id, normalJson);
        }

        [Fact]
        public async Task ForceLogout_WithCapacitorUserAgent_ReturnsMobileLogoutHtml()
        {
            using var scope = _factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<Spokes_Server.Aggregate.Database>();

            var employee = new Spokes_Server.Core.Models.HR.Employee
            {
                Id = "emp_capua_" + Guid.NewGuid(),
                FirstName = "Cap",
                LastName = "UA",
                IsActive = true
            };
            db.Employees.Save(employee);

            var rawToken = "tok_capua_" + Guid.NewGuid();
            using var sha256 = System.Security.Cryptography.SHA256.Create();
            var hash = Convert.ToBase64String(sha256.ComputeHash(System.Text.Encoding.UTF8.GetBytes(rawToken)));

            var session = new Spokes_Server.Core.Models.Core.DeviceSession
            {
                Id = "sess_capua_" + Guid.NewGuid(),
                EmployeeId = employee.Id,
                TokenHash = hash,
                DeviceId = "dev_capua_1",
                IdToken = "test_id_token",
                RevokedAt = DateTime.UtcNow.AddMinutes(-1),
                ExpiresAt = DateTime.UtcNow.AddDays(30)
            };
            db.DeviceSessions.Save(session);

            var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
            var request = new HttpRequestMessage(HttpMethod.Get, "/spokesapi/auth/force-logout");
            request.Headers.Add("Cookie", $"Spokes_Refresh={rawToken}; Spokes_Device=dev_capua_1");
            request.Headers.Add("User-Agent", "Mozilla/5.0 (Linux; Android 14) Capacitor/1.0");

            var response = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var html = await response.Content.ReadAsStringAsync();
            Assert.Contains("Preferences.remove", html);
        }

        [Fact]
        public async Task OnValidatePrincipal_WhenEmployeeDeactivated_RejectsExistingCookieSession()
        {
            using var scope = _factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<Spokes_Server.Aggregate.Database>();

            var employee = new Spokes_Server.Core.Models.HR.Employee
            {
                Id = "emp_deact_" + Guid.NewGuid(),
                FirstName = "Deact",
                LastName = "User",
                IsActive = true
            };
            db.Employees.Save(employee);

            var rawToken = "tok_deact_" + Guid.NewGuid();
            using var sha256 = System.Security.Cryptography.SHA256.Create();
            var hash = Convert.ToBase64String(sha256.ComputeHash(System.Text.Encoding.UTF8.GetBytes(rawToken)));

            var session = new Spokes_Server.Core.Models.Core.DeviceSession
            {
                Id = "sess_deact_" + Guid.NewGuid(),
                EmployeeId = employee.Id,
                TokenHash = hash,
                DeviceId = "dev_deact_1",
                ExpiresAt = DateTime.UtcNow.AddDays(30)
            };
            db.DeviceSessions.Save(session);

            // 1. Bootstrap session to get cookie
            var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
            var bootstrapContent = new FormUrlEncodedContent(new[]
            {
                new KeyValuePair<string, string>("refreshToken", rawToken),
                new KeyValuePair<string, string>("deviceId", "dev_deact_1"),
                new KeyValuePair<string, string>("returnUrl", "/")
            });
            var bootstrapRes = await client.PostAsync("/sso/login/mobile-bootstrap", bootstrapContent);
            var cookie = ExtractCookieValue(bootstrapRes, "Spokes_Session_v3");
            Assert.NotEmpty(cookie);

            // 2. Confirm authenticated access works initially
            var initialReq = new HttpRequestMessage(HttpMethod.Get, "/spokesapi/push/status");
            initialReq.Headers.Add("Cookie", $"Spokes_Session_v3={cookie}");
            var initialRes = await client.SendAsync(initialReq);
            Assert.Equal(HttpStatusCode.OK, initialRes.StatusCode);

            // 3. Deactivate employee in database
            employee.IsActive = false;
            db.Employees.Save(employee);

            // 4. Next request with the same cookie must be rejected by OnValidatePrincipal
            var rejectedReq = new HttpRequestMessage(HttpMethod.Get, "/spokesapi/push/status");
            rejectedReq.Headers.Add("Cookie", $"Spokes_Session_v3={cookie}");
            var rejectedRes = await client.SendAsync(rejectedReq);
            Assert.Equal(HttpStatusCode.Unauthorized, rejectedRes.StatusCode);
        }

        [Fact]
        public async Task ForceLogout_WithActiveSession_RedirectsToHomeAsCsrfGuard()
        {
            using var scope = _factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<Spokes_Server.Aggregate.Database>();

            var employee = new Spokes_Server.Core.Models.HR.Employee
            {
                Id = "emp_csrf_" + Guid.NewGuid(),
                FirstName = "Csrf",
                LastName = "Guard",
                IsActive = true
            };
            db.Employees.Save(employee);

            var rawToken = "tok_csrf_" + Guid.NewGuid();
            using var sha256 = System.Security.Cryptography.SHA256.Create();
            var hash = Convert.ToBase64String(sha256.ComputeHash(System.Text.Encoding.UTF8.GetBytes(rawToken)));

            var session = new Spokes_Server.Core.Models.Core.DeviceSession
            {
                Id = "sess_csrf_" + Guid.NewGuid(),
                EmployeeId = employee.Id,
                TokenHash = hash,
                DeviceId = "dev_csrf_1",
                ExpiresAt = DateTime.UtcNow.AddDays(30)
            };
            db.DeviceSessions.Save(session);

            var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
            var bootstrapContent = new FormUrlEncodedContent(new[]
            {
                new KeyValuePair<string, string>("refreshToken", rawToken),
                new KeyValuePair<string, string>("deviceId", "dev_csrf_1"),
                new KeyValuePair<string, string>("returnUrl", "/")
            });
            var bootstrapRes = await client.PostAsync("/sso/login/mobile-bootstrap", bootstrapContent);
            var cookie = ExtractCookieValue(bootstrapRes, "Spokes_Session_v3");
            Assert.NotEmpty(cookie);

            // Access force-logout with an ACTIVE (unrevoked) session -> must bounce to "/" as CSRF guard
            var request = new HttpRequestMessage(HttpMethod.Get, "/spokesapi/auth/force-logout");
            request.Headers.Add("Cookie", $"Spokes_Session_v3={cookie}");

            var response = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
            Assert.Equal("/", response.Headers.Location?.OriginalString);

            // Assert session was NOT revoked
            var currentSession = db.DeviceSessions.GetById(session.Id);
            Assert.NotNull(currentSession);
            Assert.Null(currentSession.RevokedAt);
        }

        [Fact]
        public async Task RevocationMiddleware_WithRevokedCookieSessionOnInternalApi_RejectsWith401()
        {
            using var scope = _factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<Spokes_Server.Aggregate.Database>();

            var employee = new Spokes_Server.Core.Models.HR.Employee
            {
                Id = "emp_internal_" + Guid.NewGuid(),
                FirstName = "Internal",
                LastName = "Api",
                IsActive = true
            };
            db.Employees.Save(employee);

            var rawToken = "tok_internal_" + Guid.NewGuid();
            using var sha256 = System.Security.Cryptography.SHA256.Create();
            var hash = Convert.ToBase64String(sha256.ComputeHash(System.Text.Encoding.UTF8.GetBytes(rawToken)));

            var session = new Spokes_Server.Core.Models.Core.DeviceSession
            {
                Id = "sess_internal_" + Guid.NewGuid(),
                EmployeeId = employee.Id,
                TokenHash = hash,
                DeviceId = "dev_internal_1",
                ExpiresAt = DateTime.UtcNow.AddDays(30)
            };
            db.DeviceSessions.Save(session);

            var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
            var bootstrapContent = new FormUrlEncodedContent(new[]
            {
                new KeyValuePair<string, string>("refreshToken", rawToken),
                new KeyValuePair<string, string>("deviceId", "dev_internal_1"),
                new KeyValuePair<string, string>("returnUrl", "/")
            });
            var bootstrapRes = await client.PostAsync("/sso/login/mobile-bootstrap", bootstrapContent);
            var cookie = ExtractCookieValue(bootstrapRes, "Spokes_Session_v3");
            Assert.NotEmpty(cookie);

            // Mark revoked in DB
            session.RevokedAt = DateTime.UtcNow;
            db.DeviceSessions.Save(session);

            // Calling /internal endpoint with revoked cookie session must return 401 Unauthorized
            var request = new HttpRequestMessage(HttpMethod.Get, "/internal/attachments/dummy");
            request.Headers.Add("Cookie", $"Spokes_Session_v3={cookie}");

            var response = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }

        [Fact]
        public async Task MobileBootstrap_WithHashFragmentInReturnUrl_PreservesHashFragment()
        {
            using var scope = _factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<Spokes_Server.Aggregate.Database>();

            var employee = new Spokes_Server.Core.Models.HR.Employee
            {
                Id = "emp_hash_" + Guid.NewGuid(),
                FirstName = "Hash",
                LastName = "Fragment",
                IsActive = true
            };
            db.Employees.Save(employee);

            var rawToken = "tok_hash_" + Guid.NewGuid();
            using var sha256 = System.Security.Cryptography.SHA256.Create();
            var hash = Convert.ToBase64String(sha256.ComputeHash(System.Text.Encoding.UTF8.GetBytes(rawToken)));

            var session = new Spokes_Server.Core.Models.Core.DeviceSession
            {
                Id = "sess_hash_" + Guid.NewGuid(),
                EmployeeId = employee.Id,
                TokenHash = hash,
                DeviceId = "dev_hash_1",
                ExpiresAt = DateTime.UtcNow.AddDays(30)
            };
            db.DeviceSessions.Save(session);

            var client = _factory.CreateClient();
            var content = new FormUrlEncodedContent(new[]
            {
                new KeyValuePair<string, string>("refreshToken", rawToken),
                new KeyValuePair<string, string>("deviceId", "dev_hash_1"),
                new KeyValuePair<string, string>("returnUrl", "/projects#tasks")
            });

            var response = await client.PostAsync("/sso/login/mobile-bootstrap", content);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var html = await response.Content.ReadAsStringAsync();
            Assert.Contains("window.location.replace('/projects#tasks')", html);
        }

        [Fact]
        public async Task MobileBootstrap_WithSecFetchSiteCrossSite_ReturnsBadRequest()
        {
            var client = _factory.CreateClient();
            var request = new HttpRequestMessage(HttpMethod.Post, "/sso/login/mobile-bootstrap");
            request.Headers.Add("Sec-Fetch-Site", "cross-site");
            request.Content = new FormUrlEncodedContent(new[]
            {
                new KeyValuePair<string, string>("refreshToken", "some_token"),
                new KeyValuePair<string, string>("deviceId", "some_device"),
                new KeyValuePair<string, string>("returnUrl", "/projects")
            });

            var response = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }

        [Fact]
        public async Task OnValidatePrincipal_WithSystemRendererEmployeeId_PermitsPrincipalValidation()
        {
            using var scope = _factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<Spokes_Server.Aggregate.Database>();
            var sessionService = scope.ServiceProvider.GetRequiredService<Spokes_Server.Core.Security.SessionService>();
            var ticketStore = scope.ServiceProvider.GetRequiredService<Microsoft.AspNetCore.Authentication.Cookies.ITicketStore>();

            // Virtual system-renderer has no employee in db.Employees
            var session = new Spokes_Server.Core.Models.Core.DeviceSession
            {
                Id = "sess_sysrend_" + Guid.NewGuid(),
                EmployeeId = "system-renderer",
                TokenHash = "dummy_hash",
                ExpiresAt = DateTime.UtcNow.AddDays(30)
            };
            db.DeviceSessions.Save(session);

            var identity = new System.Security.Claims.ClaimsIdentity(new[]
            {
                new System.Security.Claims.Claim("EmployeeId", "system-renderer"),
                new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.Name, "System Renderer"),
                new System.Security.Claims.Claim("SessionId", session.Id)
            }, Microsoft.AspNetCore.Authentication.Cookies.CookieAuthenticationDefaults.AuthenticationScheme);

            var principal = new System.Security.Claims.ClaimsPrincipal(identity);
            var ticket = new Microsoft.AspNetCore.Authentication.AuthenticationTicket(
                principal,
                new Microsoft.AspNetCore.Authentication.AuthenticationProperties { AllowRefresh = true },
                Microsoft.AspNetCore.Authentication.Cookies.CookieAuthenticationDefaults.AuthenticationScheme);

            var sessionKey = await ticketStore.StoreAsync(ticket);

            var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
            var request = new HttpRequestMessage(HttpMethod.Get, "/internal/attachments/dummy-system-renderer-check");
            request.Headers.Add("Cookie", $"Spokes_Session_v3={sessionKey}");

            var response = await client.SendAsync(request);
            // Must not be rejected by OnValidatePrincipal with 401
            Assert.NotEqual(HttpStatusCode.Unauthorized, response.StatusCode);
        }

        [Fact]
        public async Task RevocationMiddleware_WithRevokedCookieSessionOnHtmlPage_RedirectsToSignOut()
        {
            using var scope = _factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<Spokes_Server.Aggregate.Database>();
            var ticketStore = scope.ServiceProvider.GetRequiredService<Microsoft.AspNetCore.Authentication.Cookies.ITicketStore>();

            var employee = new Spokes_Server.Core.Models.HR.Employee
            {
                Id = "emp_revhtml_" + Guid.NewGuid(),
                FirstName = "Rev",
                LastName = "Html",
                IsActive = true
            };
            db.Employees.Save(employee);

            var session = new Spokes_Server.Core.Models.Core.DeviceSession
            {
                Id = "sess_revhtml_" + Guid.NewGuid(),
                EmployeeId = employee.Id,
                TokenHash = "dummy_hash",
                RevokedAt = DateTime.UtcNow.AddMinutes(-5),
                ExpiresAt = DateTime.UtcNow.AddDays(30)
            };
            db.DeviceSessions.Save(session);

            var identity = new System.Security.Claims.ClaimsIdentity(new[]
            {
                new System.Security.Claims.Claim("EmployeeId", employee.Id),
                new System.Security.Claims.Claim("SessionId", session.Id)
            }, Microsoft.AspNetCore.Authentication.Cookies.CookieAuthenticationDefaults.AuthenticationScheme);

            var principal = new System.Security.Claims.ClaimsPrincipal(identity);
            var ticket = new Microsoft.AspNetCore.Authentication.AuthenticationTicket(
                principal,
                new Microsoft.AspNetCore.Authentication.AuthenticationProperties { AllowRefresh = true },
                Microsoft.AspNetCore.Authentication.Cookies.CookieAuthenticationDefaults.AuthenticationScheme);

            var sessionKey = await ticketStore.StoreAsync(ticket);

            var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
            var request = new HttpRequestMessage(HttpMethod.Get, "/");
            request.Headers.Add("Cookie", $"Spokes_Session_v3={sessionKey}");

            var response = await client.SendAsync(request);
            // Should initiate OIDC SignOut redirect or fallback to /sso/login, never return 401 or OK
            Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
            Assert.Contains("/sso/login", response.Headers.Location?.OriginalString);
        }

        [Fact]
        public async Task DesktopSilentRenewal_WithValidRefreshCookieOnPageNavigation_IssuesSessionCookie()
        {
            using var scope = _factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<Spokes_Server.Aggregate.Database>();

            var employee = new Spokes_Server.Core.Models.HR.Employee
            {
                Id = "emp_deskren_" + Guid.NewGuid(),
                FirstName = "Desktop",
                LastName = "Renewal",
                IsActive = true
            };
            db.Employees.Save(employee);

            var rawToken = "tok_deskren_" + Guid.NewGuid();
            using var sha256 = System.Security.Cryptography.SHA256.Create();
            var hash = Convert.ToBase64String(sha256.ComputeHash(System.Text.Encoding.UTF8.GetBytes(rawToken)));

            var session = new Spokes_Server.Core.Models.Core.DeviceSession
            {
                Id = "sess_deskren_" + Guid.NewGuid(),
                EmployeeId = employee.Id,
                TokenHash = hash,
                DeviceId = null, // Desktop sessions have null DeviceId
                ExpiresAt = DateTime.UtcNow.AddDays(30)
            };
            db.DeviceSessions.Save(session);

            var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
            var request = new HttpRequestMessage(HttpMethod.Get, "/");
            request.Headers.Add("Cookie", $"Spokes_Refresh={rawToken}");

            var response = await client.SendAsync(request);
            // DeviceTokenAuthHandler authenticates and calls SignInAndExtendAsync which issues Spokes_Session_v3
            var sessionCookie = ExtractCookieValue(response, "Spokes_Session_v3");
            Assert.False(string.IsNullOrEmpty(sessionCookie), "Expected Spokes_Session_v3 cookie to be issued on desktop silent renewal");
        }

        [Fact]
        public async Task DeviceTokenAuth_ChallengeOnDesktopPage_PreservesReturnUrl()
        {
            var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
            var request = new HttpRequestMessage(HttpMethod.Get, "/projects/proj-101");
            request.Headers.Add("Cookie", "Spokes_Refresh=invalid_expired_token");

            var response = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
            var location = response.Headers.Location?.ToString();
            Assert.NotNull(location);
            Assert.Contains("/sso/login/auto?returnUrl=", location);
            Assert.Contains("%2Fprojects%2Fproj-101", location);
        }

        [Fact]
        public async Task DeviceTokenAuth_WithBearerAndMissingDeviceId_ReturnsUnauthorized()
        {
            using var scope = _factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<Spokes_Server.Aggregate.Database>();

            var employee = new Spokes_Server.Core.Models.HR.Employee
            {
                Id = "emp_mobmissdev_" + Guid.NewGuid(),
                FirstName = "Mob",
                LastName = "MissDev",
                IsActive = true
            };
            db.Employees.Save(employee);

            var rawToken = "tok_mobmissdev_" + Guid.NewGuid();
            using var sha256 = System.Security.Cryptography.SHA256.Create();
            var hash = Convert.ToBase64String(sha256.ComputeHash(System.Text.Encoding.UTF8.GetBytes(rawToken)));

            var session = new Spokes_Server.Core.Models.Core.DeviceSession
            {
                Id = "sess_mobmissdev_" + Guid.NewGuid(),
                EmployeeId = employee.Id,
                TokenHash = hash,
                DeviceId = "dev_mob_bound_1", // Session is bound to mobile device
                ExpiresAt = DateTime.UtcNow.AddDays(30)
            };
            db.DeviceSessions.Save(session);

            var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
            var request = new HttpRequestMessage(HttpMethod.Get, "/spokesapi/push/status");
            request.Headers.Add("Authorization", $"Bearer {rawToken}");
            // Deliberately omit X-Device-Id and Spokes_Device cookie

            var response = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }

        [Fact]
        public async Task ForceLogout_OnDesktop_WhenRevoked_DeletesCookiesAndRedirectsToLogin()
        {
            using var scope = _factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<Spokes_Server.Aggregate.Database>();
            var ticketStore = scope.ServiceProvider.GetRequiredService<Microsoft.AspNetCore.Authentication.Cookies.ITicketStore>();

            var employee = new Spokes_Server.Core.Models.HR.Employee
            {
                Id = "emp_deskfl_" + Guid.NewGuid(),
                FirstName = "Desk",
                LastName = "FL",
                IsActive = true
            };
            db.Employees.Save(employee);

            var rawToken = "tok_deskfl_" + Guid.NewGuid();
            using var sha256 = System.Security.Cryptography.SHA256.Create();
            var hash = Convert.ToBase64String(sha256.ComputeHash(System.Text.Encoding.UTF8.GetBytes(rawToken)));

            var session = new Spokes_Server.Core.Models.Core.DeviceSession
            {
                Id = "sess_deskfl_" + Guid.NewGuid(),
                EmployeeId = employee.Id,
                TokenHash = hash,
                RevokedAt = DateTime.UtcNow.AddMinutes(-5),
                ExpiresAt = DateTime.UtcNow.AddDays(30)
            };
            db.DeviceSessions.Save(session);

            var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
            var request = new HttpRequestMessage(HttpMethod.Get, "/spokesapi/auth/force-logout");
            request.Headers.Add("Cookie", $"Spokes_Refresh={rawToken}; chat_vault_key=sample_vault_key");
            request.Headers.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) Chrome/120.0.0.0"); // Desktop browser

            var response = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
            Assert.Contains("/sso/login", response.Headers.Location?.OriginalString);
        }

        [Fact]
        public async Task MobileSplashInterceptor_WhenCapacitorWithoutSessionCookie_ReturnsBootstrapHtml()
        {
            var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
            var request = new HttpRequestMessage(HttpMethod.Get, "/");
            request.Headers.Add("User-Agent", "Mozilla/5.0 (iPhone; CPU iPhone OS 17_0 like Mac OS X) AppleWebKit/605.1.15 Mobile/15E148 Capacitor");

            var response = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var html = await response.Content.ReadAsStringAsync();
            Assert.Contains("/sso/login/mobile-bootstrap", html);
            Assert.Contains("spokes_refresh", html);
        }

        [Fact]
        public async Task MobileAuthConfig_ReturnsDiscoveryConfiguration()
        {
            var client = _factory.CreateClient();
            var response = await client.GetAsync("/spokesapi/auth/mobile/config");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var json = await response.Content.ReadAsStringAsync();
            Assert.Contains("authorizationEndpoint", json);
            Assert.Contains("clientId", json);
            Assert.Contains("scope", json);
        }

        [Fact]
        public async Task SsoLoginAuto_WithValidRefreshCookie_SilentlyRenewsAndRedirectsToTarget()
        {
            using var scope = _factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<Spokes_Server.Aggregate.Database>();

            var employee = new Spokes_Server.Core.Models.HR.Employee
            {
                Id = "emp_ssoauto_" + Guid.NewGuid(),
                FirstName = "Auto",
                LastName = "User",
                IsActive = true
            };
            db.Employees.Save(employee);

            var rawToken = "tok_ssoauto_" + Guid.NewGuid();
            using var sha256 = System.Security.Cryptography.SHA256.Create();
            var hash = Convert.ToBase64String(sha256.ComputeHash(System.Text.Encoding.UTF8.GetBytes(rawToken)));

            var session = new Spokes_Server.Core.Models.Core.DeviceSession
            {
                Id = "sess_ssoauto_" + Guid.NewGuid(),
                EmployeeId = employee.Id,
                TokenHash = hash,
                DeviceId = null, // Desktop session
                ExpiresAt = DateTime.UtcNow.AddDays(30)
            };
            db.DeviceSessions.Save(session);

            var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
            var request = new HttpRequestMessage(HttpMethod.Get, "/sso/login/auto?returnUrl=%2Fdashboard");
            request.Headers.Add("Cookie", $"Spokes_Refresh={rawToken}");

            var response = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
            Assert.Equal("/dashboard", response.Headers.Location?.OriginalString);
            var sessionCookie = ExtractCookieValue(response, "Spokes_Session_v3");
            Assert.False(string.IsNullOrEmpty(sessionCookie), "Expected Spokes_Session_v3 cookie to be issued by /sso/login/auto");
        }

        [Fact]
        public async Task MobileSplashInterceptor_WhenCapacitorWithSessionCookie_PassesThroughToBlazorApp()
        {
            using var scope = _factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<Spokes_Server.Aggregate.Database>();
            var employee = new Spokes_Server.Core.Models.HR.Employee
            {
                Id = "emp_splpass_" + Guid.NewGuid(),
                FirstName = "Splash",
                LastName = "Pass",
                IsActive = true
            };
            db.Employees.Save(employee);

            var rawToken = "tok_splpass_" + Guid.NewGuid();
            using var sha256 = System.Security.Cryptography.SHA256.Create();
            var hash = Convert.ToBase64String(sha256.ComputeHash(System.Text.Encoding.UTF8.GetBytes(rawToken)));

            var session = new Spokes_Server.Core.Models.Core.DeviceSession
            {
                Id = "sess_splpass_" + Guid.NewGuid(),
                EmployeeId = employee.Id,
                TokenHash = hash,
                DeviceId = "dev_splpass_1",
                ExpiresAt = DateTime.UtcNow.AddDays(30)
            };
            db.DeviceSessions.Save(session);

            var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
            var bootstrapContent = new FormUrlEncodedContent(new[]
            {
                new KeyValuePair<string, string>("refreshToken", rawToken),
                new KeyValuePair<string, string>("deviceId", "dev_splpass_1"),
                new KeyValuePair<string, string>("returnUrl", "/")
            });
            var bootstrapRes = await client.PostAsync("/sso/login/mobile-bootstrap", bootstrapContent);
            var sessionCookie = ExtractCookieValue(bootstrapRes, "Spokes_Session_v3");
            Assert.NotEmpty(sessionCookie);

            var request = new HttpRequestMessage(HttpMethod.Get, "/");
            request.Headers.Add("User-Agent", "Mozilla/5.0 (iPhone; CPU iPhone OS 17_0 like Mac OS X) AppleWebKit/605.1.15 Mobile/15E148 Capacitor");
            request.Headers.Add("Cookie", $"Spokes_Session_v3={sessionCookie}");

            var response = await client.SendAsync(request);
            var html = await response.Content.ReadAsStringAsync();
            Assert.DoesNotContain("/sso/login/mobile-bootstrap", html);
        }

        [Fact]
        public async Task MobileSplashInterceptor_WhenCapacitorWithUnauthenticatedSessionCookie_RendersBootstrapHtmlAndPreservesSessionCookie()
        {
            var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

            var request = new HttpRequestMessage(HttpMethod.Get, "/");
            request.Headers.Add("User-Agent", "Mozilla/5.0 (iPhone; CPU iPhone OS 17_0 like Mac OS X) AppleWebKit/605.1.15 Mobile/15E148 Capacitor");
            request.Headers.Add("Cookie", "Spokes_Session_v3=some_sliding_session_cookie");

            var response = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var html = await response.Content.ReadAsStringAsync();
            Assert.Contains("/sso/login/mobile-bootstrap", html);
            Assert.Contains("spokes_refresh_", html);

            var cookies = response.Headers.Contains("Set-Cookie")
                ? response.Headers.GetValues("Set-Cookie").ToList()
                : new List<string>();
            // Verify Spokes_Session_v3 is NOT deleted to protect sliding session cookies
            Assert.DoesNotContain(cookies, c => c.StartsWith("Spokes_Session_v3=;"));
        }

        [Fact]
        public async Task SsoLoginAuto_WithInvalidRefreshCookie_DeletesRefreshCookieAndProceedsToFallback()
        {
            var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
            var request = new HttpRequestMessage(HttpMethod.Get, "/sso/login/auto?returnUrl=%2Fdashboard");
            request.Headers.Add("User-Agent", "Mozilla/5.0 (iPhone; CPU iPhone OS 17_0 like Mac OS X) AppleWebKit/605.1.15 Mobile/15E148 Capacitor");
            request.Headers.Add("Cookie", "Spokes_Refresh=invalid_expired_token");

            var response = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
            Assert.Contains("/mobile-login", response.Headers.Location?.OriginalString);
            var cookies = response.Headers.GetValues("Set-Cookie").ToList();
            Assert.Contains(cookies, c => c.StartsWith("Spokes_Refresh=;"));
        }

        [Fact]
        public async Task MobileAuthExchange_WithUnconfiguredAuthority_ReturnsBadRequest()
        {
            var client = _factory.CreateClient();
            var content = new StringContent(
                System.Text.Json.JsonSerializer.Serialize(new
                {
                    deviceId = "dev_test_unconf_1",
                    code = "auth_code_123",
                    codeVerifier = "code_verifier_123",
                    redirectUri = "https://app.spokes.com/auth"
                }),
                System.Text.Encoding.UTF8,
                "application/json");

            var response = await client.PostAsync("/spokesapi/auth/mobile/exchange", content);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            var body = await response.Content.ReadAsStringAsync();
            Assert.Contains("Authority not configured", body);
        }

        [Fact]
        public async Task AnonymousDesktopRequest_ToRoot_ChallengesAndRedirects()
        {
            var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
            var response = await client.GetAsync("/");
            Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        }

        [Fact]
        public async Task SsoLoginAuto_WithValidRefreshCookieAndMatchingDeviceId_SilentlyRenews()
        {
            using var scope = _factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<Spokes_Server.Aggregate.Database>();

            var employee = new Spokes_Server.Core.Models.HR.Employee
            {
                Id = "emp_ssomatch_" + Guid.NewGuid(),
                FirstName = "Match",
                LastName = "Device",
                IsActive = true
            };
            db.Employees.Save(employee);

            var rawToken = "tok_ssomatch_" + Guid.NewGuid();
            using var sha256 = System.Security.Cryptography.SHA256.Create();
            var hash = Convert.ToBase64String(sha256.ComputeHash(System.Text.Encoding.UTF8.GetBytes(rawToken)));

            var session = new Spokes_Server.Core.Models.Core.DeviceSession
            {
                Id = "sess_ssomatch_" + Guid.NewGuid(),
                EmployeeId = employee.Id,
                TokenHash = hash,
                DeviceId = "dev_sso_match_1",
                ExpiresAt = DateTime.UtcNow.AddDays(30)
            };
            db.DeviceSessions.Save(session);

            var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
            var request = new HttpRequestMessage(HttpMethod.Get, "/sso/login/auto?deviceId=dev_sso_match_1&returnUrl=%2Fdashboard");
            request.Headers.Add("Cookie", $"Spokes_Refresh={rawToken}");

            var response = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
            Assert.Equal("/dashboard", response.Headers.Location?.OriginalString);
            var sessionCookie = ExtractCookieValue(response, "Spokes_Session_v3");
            Assert.NotEmpty(sessionCookie);
        }

        [Fact]
        public async Task MobileBootstrap_WithLegacyDevSession_MigratesToIosDeviceIdAndSignsIn()
        {
            using var scope = _factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<Spokes_Server.Aggregate.Database>();
            var employee = new Spokes_Server.Core.Models.HR.Employee
            {
                Id = "emp_mig_boot_" + Guid.NewGuid(),
                FirstName = "Migrate",
                LastName = "Bootstrap",
                IsActive = true
            };
            db.Employees.Save(employee);

            var rawToken = "tok_mig_boot_" + Guid.NewGuid();
            using var sha256 = System.Security.Cryptography.SHA256.Create();
            var hash = Convert.ToBase64String(sha256.ComputeHash(System.Text.Encoding.UTF8.GetBytes(rawToken)));

            var session = new Spokes_Server.Core.Models.Core.DeviceSession
            {
                Id = "sess_mig_boot_" + Guid.NewGuid(),
                EmployeeId = employee.Id,
                TokenHash = hash,
                DeviceId = "dev_legacy_session_id",
                ExpiresAt = DateTime.UtcNow.AddDays(30)
            };
            db.DeviceSessions.Save(session);

            var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
            var nativeIosId = "ios_keychain_uuid_9999";
            var content = new FormUrlEncodedContent(new[]
            {
                new KeyValuePair<string, string>("refreshToken", rawToken),
                new KeyValuePair<string, string>("deviceId", nativeIosId),
                new KeyValuePair<string, string>("returnUrl", "/projects")
            });

            var response = await client.PostAsync("/sso/login/mobile-bootstrap", content);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            // Verify session cookie was issued
            var sessionCookie = ExtractCookieValue(response, "Spokes_Session_v3");
            Assert.NotEmpty(sessionCookie);

            // Verify database record was migrated to native iOS ID
            var updatedSession = db.DeviceSessions.GetById(session.Id);
            Assert.NotNull(updatedSession);
            Assert.Equal(nativeIosId, updatedSession.DeviceId);
        }

        [Fact]
        public async Task AuthRefresh_WithLegacyDevSession_MigratesToIosDeviceId()
        {
            using var scope = _factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<Spokes_Server.Aggregate.Database>();
            var employee = new Spokes_Server.Core.Models.HR.Employee
            {
                Id = "emp_refresh_mig_" + Guid.NewGuid(),
                FirstName = "Refresh",
                LastName = "Migrate",
                IsActive = true
            };
            db.Employees.Save(employee);

            var rawToken = "tok_refresh_mig_" + Guid.NewGuid();
            using var sha256 = System.Security.Cryptography.SHA256.Create();
            var hash = Convert.ToBase64String(sha256.ComputeHash(System.Text.Encoding.UTF8.GetBytes(rawToken)));

            var session = new Spokes_Server.Core.Models.Core.DeviceSession
            {
                Id = "sess_refresh_mig_" + Guid.NewGuid(),
                EmployeeId = employee.Id,
                TokenHash = hash,
                DeviceId = "dev_legacy_refresh_id",
                ExpiresAt = DateTime.UtcNow.AddDays(30)
            };
            db.DeviceSessions.Save(session);

            var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
            var nativeIosId = "ios_keychain_uuid_8888";
            var requestJson = System.Text.Json.JsonSerializer.Serialize(new
            {
                refreshToken = rawToken,
                deviceId = nativeIosId
            });
            var content = new StringContent(requestJson, System.Text.Encoding.UTF8, "application/json");

            var response = await client.PostAsync("/spokesapi/auth/refresh", content);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            // Verify session cookie was issued
            var sessionCookie = ExtractCookieValue(response, "Spokes_Session_v3");
            Assert.NotEmpty(sessionCookie);

            // Verify database record was migrated to native iOS ID
            var updatedSession = db.DeviceSessions.GetById(session.Id);
            Assert.NotNull(updatedSession);
            Assert.Equal(nativeIosId, updatedSession.DeviceId);
        }

        [Fact]
        public async Task AuthRefresh_WithMismatchedDeviceId_ReturnsUnauthorized()
        {
            using var scope = _factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<Spokes_Server.Aggregate.Database>();
            var employee = new Spokes_Server.Core.Models.HR.Employee
            {
                Id = "emp_refresh_mismatch_" + Guid.NewGuid(),
                FirstName = "Refresh",
                LastName = "Mismatch",
                IsActive = true
            };
            db.Employees.Save(employee);

            var rawToken = "tok_refresh_mismatch_" + Guid.NewGuid();
            using var sha256 = System.Security.Cryptography.SHA256.Create();
            var hash = Convert.ToBase64String(sha256.ComputeHash(System.Text.Encoding.UTF8.GetBytes(rawToken)));

            var session = new Spokes_Server.Core.Models.Core.DeviceSession
            {
                Id = "sess_refresh_mismatch_" + Guid.NewGuid(),
                EmployeeId = employee.Id,
                TokenHash = hash,
                DeviceId = "ios_original_bound_id",
                ExpiresAt = DateTime.UtcNow.AddDays(30)
            };
            db.DeviceSessions.Save(session);

            var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

            // Post with a mismatched device ID
            var requestJson = System.Text.Json.JsonSerializer.Serialize(new
            {
                refreshToken = rawToken,
                deviceId = "ios_attacker_bound_id"
            });
            var content = new StringContent(requestJson, System.Text.Encoding.UTF8, "application/json");

            var response = await client.PostAsync("/spokesapi/auth/refresh", content);

            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);

            // Database record must NOT have changed
            var persistedSession = db.DeviceSessions.GetById(session.Id);
            Assert.NotNull(persistedSession);
            Assert.Equal("ios_original_bound_id", persistedSession.DeviceId);
        }

        [Fact]
        public async Task AuthRefresh_WithLegacyDevSession_MigratesToAndroidDeviceId()
        {
            using var scope = _factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<Spokes_Server.Aggregate.Database>();
            var emp = new Spokes_Server.Core.Models.HR.Employee { Id = "emp_and_mig_" + Guid.NewGuid(), FirstName = "And", IsActive = true };
            db.Employees.Save(emp);
            var rawToken = "tok_and_mig_" + Guid.NewGuid();
            using var sha256 = System.Security.Cryptography.SHA256.Create();
            var hash = Convert.ToBase64String(sha256.ComputeHash(System.Text.Encoding.UTF8.GetBytes(rawToken)));
            var session = new Spokes_Server.Core.Models.Core.DeviceSession
            {
                Id = "sess_and_mig_" + Guid.NewGuid(),
                EmployeeId = emp.Id,
                TokenHash = hash,
                DeviceId = "dev_legacy_phone",
                ExpiresAt = DateTime.UtcNow.AddDays(30)
            };
            db.DeviceSessions.Save(session);

            var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
            var payload = System.Text.Json.JsonSerializer.Serialize(new { refreshToken = rawToken, deviceId = "android_secure_uuid_5678" });
            var response = await client.PostAsync("/spokesapi/auth/refresh", new StringContent(payload, System.Text.Encoding.UTF8, "application/json"));

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.NotEmpty(ExtractCookieValue(response, "Spokes_Session_v3"));
            Assert.Equal("android_secure_uuid_5678", db.DeviceSessions.GetById(session.Id)?.DeviceId);
        }

        [Theory]
        [InlineData("")]
        [InlineData("invalid_nonexistent_token_string")]
        public async Task MobileBootstrap_WithMissingOrInvalidToken_RedirectsToMobileLogin(string token)
        {
            var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
            var content = new FormUrlEncodedContent(new[]
            {
                new KeyValuePair<string, string>("refreshToken", token),
                new KeyValuePair<string, string>("deviceId", "ios_hw_1234"),
                new KeyValuePair<string, string>("returnUrl", "/projects")
            });
            var response = await client.PostAsync("/sso/login/mobile-bootstrap", content);
            Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
            Assert.Contains("/mobile-login?returnUrl=%2Fprojects", response.Headers.Location?.OriginalString);
            Assert.Empty(ExtractCookieValue(response, "Spokes_Session_v3"));
        }

        [Fact]
        public async Task MobileBootstrap_WithMismatchedModernDeviceId_RejectsAndRedirectsToLogin()
        {
            using var scope = _factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<Spokes_Server.Aggregate.Database>();
            var emp = new Spokes_Server.Core.Models.HR.Employee { Id = "emp_boot_rej_" + Guid.NewGuid(), FirstName = "Rej", IsActive = true };
            db.Employees.Save(emp);
            var rawToken = "tok_boot_rej_" + Guid.NewGuid();
            using var sha256 = System.Security.Cryptography.SHA256.Create();
            var hash = Convert.ToBase64String(sha256.ComputeHash(System.Text.Encoding.UTF8.GetBytes(rawToken)));
            var session = new Spokes_Server.Core.Models.Core.DeviceSession
            {
                Id = "sess_boot_rej_" + Guid.NewGuid(),
                EmployeeId = emp.Id,
                TokenHash = hash,
                DeviceId = "bound_phone_uuid_123",
                ExpiresAt = DateTime.UtcNow.AddDays(30)
            };
            db.DeviceSessions.Save(session);

            var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
            var content = new FormUrlEncodedContent(new[]
            {
                new KeyValuePair<string, string>("refreshToken", rawToken),
                new KeyValuePair<string, string>("deviceId", "attacker_device_uuid_999"),
                new KeyValuePair<string, string>("returnUrl", "/dashboard")
            });
            var response = await client.PostAsync("/sso/login/mobile-bootstrap", content);
            Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
            Assert.Contains("/mobile-login", response.Headers.Location?.OriginalString);
            Assert.Empty(ExtractCookieValue(response, "Spokes_Session_v3"));
            Assert.Equal("bound_phone_uuid_123", db.DeviceSessions.GetById(session.Id)?.DeviceId);
        }

        [Fact]
        public async Task DesktopNavigation_WithUnboundSession_RemainsUnboundWithoutDeviceCookie()
        {
            using var scope = _factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<Spokes_Server.Aggregate.Database>();
            var emp = new Spokes_Server.Core.Models.HR.Employee { Id = "emp_unbound_nav_" + Guid.NewGuid(), FirstName = "Desk", IsActive = true };
            db.Employees.Save(emp);

            var rawToken = "tok_unbound_nav_" + Guid.NewGuid();
            using var sha256 = System.Security.Cryptography.SHA256.Create();
            var hash = Convert.ToBase64String(sha256.ComputeHash(System.Text.Encoding.UTF8.GetBytes(rawToken)));
            var session = new Spokes_Server.Core.Models.Core.DeviceSession
            {
                Id = "sess_unbound_nav_" + Guid.NewGuid(),
                EmployeeId = emp.Id,
                TokenHash = hash,
                DeviceId = null,
                ExpiresAt = DateTime.UtcNow.AddDays(30)
            };
            db.DeviceSessions.Save(session);

            var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
            var request = new HttpRequestMessage(HttpMethod.Get, "/sso/login/auto?returnUrl=%2F");
            request.Headers.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) Chrome/120.0.0.0");
            request.Headers.Add("Cookie", $"Spokes_Refresh={rawToken}");
            var response = await client.SendAsync(request);

            Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
            var cookies = response.Headers.TryGetValues("Set-Cookie", out var cList) ? cList.ToList() : new List<string>();
            Assert.DoesNotContain(cookies, c => c.StartsWith("Spokes_Device="));
            Assert.Null(db.DeviceSessions.GetById(session.Id)?.DeviceId);
        }

        private static string ExtractCookieValue(HttpResponseMessage response, string cookieName)
        {
            if (response.Headers.TryGetValues("Set-Cookie", out var cookies))
            {
                foreach (var c in cookies)
                {
                    if (c.StartsWith(cookieName + "="))
                    {
                        var val = c.Substring((cookieName + "=").Length);
                        var semi = val.IndexOf(';');
                        return semi >= 0 ? val.Substring(0, semi) : val;
                    }
                }
            }
            return string.Empty;
        }
    }
}
