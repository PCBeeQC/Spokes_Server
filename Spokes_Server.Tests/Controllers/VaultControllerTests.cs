using System;
using System.Linq;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using Spokes_Server.Aggregate;
using Spokes_Server.Controllers;
using Spokes_Server.Core.Data;
using Spokes_Server.Core.Data.Repositories.Core;
using Spokes_Server.Core.Data.Repositories.HR;
using Spokes_Server.Core.Models.Core;
using Xunit;

namespace Spokes_Server.Tests.Controllers
{
    public class VaultControllerTests : TestDataTestBase
    {
        private readonly Database _db;
        private readonly IDataProtectionProvider _dataProtection;
        private readonly VaultController _controller;

        public VaultControllerTests()
        {
            var mockConfig = new Mock<IConfiguration>();
            mockConfig.Setup(c => c["DataPath"]).Returns(_testDataPath);

            var mockLogger = new Mock<ILogger<DiskPersistenceService>>();
            var writer = new DiskPersistenceService(mockLogger.Object);

            var sessionRepo = new DeviceSessionRepository(writer, mockConfig.Object);
            var employeeRepo = new EmployeeRepository(writer, mockConfig.Object);
            _db = new Database(sessionRepo, employeeRepo);

            var services = new ServiceCollection();
            services.AddDataProtection().UseEphemeralDataProtectionProvider();
            var serviceProvider = services.BuildServiceProvider();
            _dataProtection = serviceProvider.GetRequiredService<IDataProtectionProvider>();

            _controller = new VaultController
            {
                ControllerContext = new ControllerContext
                {
                    HttpContext = new DefaultHttpContext()
                }
            };
        }

        #region Helpers

        private DeviceSession CreateAndSaveSession(bool hasVaultCookie = false, bool isRevoked = false)
        {
            var session = new DeviceSession
            {
                Id = Guid.NewGuid().ToString(),
                EmployeeId = "emp-test-user",
                HasVaultCookie = hasVaultCookie,
                RevokedAt = isRevoked ? DateTime.UtcNow : null
            };
            _db.DeviceSessions.Save(session);
            return session;
        }

        private void SetActiveSession(DeviceSession session)
        {
            var identity = new ClaimsIdentity(new[]
            {
                new Claim("SessionId", session.Id),
                new Claim(ClaimTypes.NameIdentifier, session.EmployeeId)
            }, "TestAuth");
            _controller.ControllerContext.HttpContext.User = new ClaimsPrincipal(identity);
        }

        private static JsonElement GetJsonResponse(IActionResult result)
        {
            var okResult = Assert.IsType<OkObjectResult>(result);
            var json = JsonSerializer.Serialize(okResult.Value);
            return JsonDocument.Parse(json).RootElement;
        }

        #endregion

        #region Controller Attributes

        [Fact]
        public void Controller_HasExpectedAttributes()
        {
            var type = typeof(VaultController);

            var routeAttr = type.GetCustomAttributes(typeof(RouteAttribute), false)
                .Cast<RouteAttribute>()
                .FirstOrDefault();
            Assert.NotNull(routeAttr);
            Assert.Equal("spokesapi/vault", routeAttr.Template);

            var apiControllerAttr = type.GetCustomAttributes(typeof(ApiControllerAttribute), false);
            Assert.NotEmpty(apiControllerAttr);

            var authorizeAttr = type.GetCustomAttributes(typeof(AuthorizeAttribute), false);
            Assert.NotEmpty(authorizeAttr);
        }

        #endregion

        #region Login Tests

        [Theory]
        [InlineData("")]
        [InlineData(null)]
        public void Login_NullOrEmptyPassword_ReturnsBadRequest(string? password)
        {
            var request = new VaultLoginRequest { Password = password! };

            var result = _controller.Login(request, _dataProtection, _db);

            var badRequest = Assert.IsType<BadRequestObjectResult>(result);
            Assert.Equal("Password is required.", badRequest.Value);
            Assert.DoesNotContain("chat_vault_key=", _controller.Response.Headers.SetCookie.ToString());
        }

        [Fact]
        public void Login_WebClient_ValidPassword_EncryptsPasswordSetsCookieAndReturnsVaultKey()
        {
            const string password = "MySecretVaultPassword123!";
            var request = new VaultLoginRequest { Password = password };

            var result = _controller.Login(request, _dataProtection, _db);

            var json = GetJsonResponse(result);
            Assert.True(json.GetProperty("success").GetBoolean());
            var vaultKey = json.GetProperty("vaultKey").GetString();
            Assert.False(string.IsNullOrEmpty(vaultKey));

            // Verify DataProtection round-trip decryption
            var protector = _dataProtection.CreateProtector("ChatVaultKey");
            var decrypted = protector.Unprotect(vaultKey!);
            Assert.Equal(password, decrypted);

            // Verify Set-Cookie header for web client
            var setCookie = _controller.Response.Headers.SetCookie.ToString();
            Assert.Contains("chat_vault_key=", setCookie);
            Assert.Contains(vaultKey!, setCookie);
            Assert.Contains("httponly", setCookie, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("samesite=lax", setCookie, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("path=/", setCookie, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("secure", setCookie, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void Login_WebClient_WithActiveSession_SetsHasVaultCookieTrueAndPersists()
        {
            var session = CreateAndSaveSession(hasVaultCookie: false);
            SetActiveSession(session);

            var request = new VaultLoginRequest { Password = "ValidPassword123" };
            var result = _controller.Login(request, _dataProtection, _db);

            var json = GetJsonResponse(result);
            Assert.True(json.GetProperty("success").GetBoolean());

            var persisted = _db.DeviceSessions.GetById(session.Id);
            Assert.NotNull(persisted);
            Assert.True(persisted.HasVaultCookie);
        }

        [Fact]
        public void Login_MobileClientViaHeader_DoesNotSetCookie_ReturnsVaultKeyAndUpdatesSession()
        {
            _controller.Request.Headers["X-Spokes-Client"] = "mobile";
            var session = CreateAndSaveSession(hasVaultCookie: false);
            SetActiveSession(session);

            const string password = "MobilePassword456";
            var request = new VaultLoginRequest { Password = password };
            var result = _controller.Login(request, _dataProtection, _db);

            var json = GetJsonResponse(result);
            Assert.True(json.GetProperty("success").GetBoolean());
            var vaultKey = json.GetProperty("vaultKey").GetString();
            Assert.False(string.IsNullOrEmpty(vaultKey));

            // Verify cookie is NOT set for native mobile
            var setCookie = _controller.Response.Headers.SetCookie.ToString();
            Assert.DoesNotContain("chat_vault_key=", setCookie);

            // Verify session flag is still updated and persisted
            var persisted = _db.DeviceSessions.GetById(session.Id);
            Assert.NotNull(persisted);
            Assert.True(persisted.HasVaultCookie);
        }

        [Fact]
        public void Login_MobileClientViaUserAgent_DoesNotSetCookie()
        {
            _controller.Request.Headers["User-Agent"] = "Mozilla/5.0 (Linux; Android 14; Mobile) AppleWebKit/537.36 Capacitor/6.0.0";
            var session = CreateAndSaveSession(hasVaultCookie: false);
            SetActiveSession(session);

            var request = new VaultLoginRequest { Password = "MobilePassword789" };
            var result = _controller.Login(request, _dataProtection, _db);

            var json = GetJsonResponse(result);
            Assert.True(json.GetProperty("success").GetBoolean());

            var setCookie = _controller.Response.Headers.SetCookie.ToString();
            Assert.DoesNotContain("chat_vault_key=", setCookie);

            var persisted = _db.DeviceSessions.GetById(session.Id);
            Assert.NotNull(persisted);
            Assert.True(persisted.HasVaultCookie);
        }

        [Fact]
        public void Login_WithoutActiveSession_SucceedsWithoutError()
        {
            var request = new VaultLoginRequest { Password = "NoSessionPassword" };

            var result = _controller.Login(request, _dataProtection, _db);

            var json = GetJsonResponse(result);
            Assert.True(json.GetProperty("success").GetBoolean());
            Assert.False(string.IsNullOrEmpty(json.GetProperty("vaultKey").GetString()));
            Assert.Contains("chat_vault_key=", _controller.Response.Headers.SetCookie.ToString());
        }

        #endregion

        #region Logout Tests

        [Fact]
        public void Logout_DeletesCookieAndReturnsOk()
        {
            var result = _controller.Logout(_db);

            var json = GetJsonResponse(result);
            Assert.True(json.GetProperty("success").GetBoolean());

            var setCookie = _controller.Response.Headers.SetCookie.ToString();
            Assert.Contains("chat_vault_key=", setCookie);
            Assert.Contains("expires=Thu, 01 Jan 1970", setCookie, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("path=/", setCookie, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("httponly", setCookie, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("secure", setCookie, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("samesite=lax", setCookie, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void Logout_WithActiveSession_SetsHasVaultCookieFalseAndPersists()
        {
            var session = CreateAndSaveSession(hasVaultCookie: true);
            SetActiveSession(session);

            var result = _controller.Logout(_db);

            var json = GetJsonResponse(result);
            Assert.True(json.GetProperty("success").GetBoolean());

            var persisted = _db.DeviceSessions.GetById(session.Id);
            Assert.NotNull(persisted);
            Assert.False(persisted.HasVaultCookie);
        }

        [Fact]
        public void Logout_WithoutActiveSession_DeletesCookieAndReturnsOk()
        {
            var result = _controller.Logout(_db);

            var json = GetJsonResponse(result);
            Assert.True(json.GetProperty("success").GetBoolean());
            Assert.Contains("chat_vault_key=", _controller.Response.Headers.SetCookie.ToString());
        }

        #endregion

        #region Status Tests

        [Fact]
        public void Status_WebClient_WithCookiePresent_ReturnsHasCookieTrue()
        {
            _controller.Request.Headers["Cookie"] = "chat_vault_key=some_encrypted_token";

            var result = _controller.Status(_db);

            var json = GetJsonResponse(result);
            Assert.True(json.GetProperty("hasCookie").GetBoolean());
        }

        [Fact]
        public void Status_WebClient_WithoutCookie_ReturnsHasCookieFalse()
        {
            _controller.Request.Headers["Cookie"] = "other_session=xyz123";

            var result = _controller.Status(_db);

            var json = GetJsonResponse(result);
            Assert.False(json.GetProperty("hasCookie").GetBoolean());
        }

        [Fact]
        public void Status_WebClient_NoCookiesAtAll_ReturnsHasCookieFalse()
        {
            var result = _controller.Status(_db);

            var json = GetJsonResponse(result);
            Assert.False(json.GetProperty("hasCookie").GetBoolean());
        }

        [Fact]
        public void Status_WebClient_IgnoresSessionHasVaultCookieFlag()
        {
            // Even if session has HasVaultCookie = true, web client only checks Request.Cookies
            var session = CreateAndSaveSession(hasVaultCookie: true);
            SetActiveSession(session);

            var result = _controller.Status(_db);

            var json = GetJsonResponse(result);
            Assert.False(json.GetProperty("hasCookie").GetBoolean());
        }

        [Fact]
        public void Status_MobileClientViaHeader_ActiveSessionWithVaultCookie_ReturnsHasCookieTrue()
        {
            _controller.Request.Headers["X-Spokes-Client"] = "mobile";
            var session = CreateAndSaveSession(hasVaultCookie: true);
            SetActiveSession(session);

            var result = _controller.Status(_db);

            var json = GetJsonResponse(result);
            Assert.True(json.GetProperty("hasCookie").GetBoolean());
        }

        [Fact]
        public void Status_MobileClientViaHeader_CaseInsensitive_ReturnsHasCookieTrue()
        {
            _controller.Request.Headers["X-Spokes-Client"] = "MOBILE";
            var session = CreateAndSaveSession(hasVaultCookie: true);
            SetActiveSession(session);

            var result = _controller.Status(_db);

            var json = GetJsonResponse(result);
            Assert.True(json.GetProperty("hasCookie").GetBoolean());
        }

        [Fact]
        public void Status_MobileClientViaUserAgent_ActiveSessionWithoutVaultCookie_ReturnsHasCookieFalse()
        {
            _controller.Request.Headers["User-Agent"] = "MyApp/1.0 Capacitor iOS";
            var session = CreateAndSaveSession(hasVaultCookie: false);
            SetActiveSession(session);

            var result = _controller.Status(_db);

            var json = GetJsonResponse(result);
            Assert.False(json.GetProperty("hasCookie").GetBoolean());
        }

        [Fact]
        public void Status_MobileClient_NoActiveSession_ReturnsHasCookieFalse()
        {
            _controller.Request.Headers["X-Spokes-Client"] = "mobile";

            var result = _controller.Status(_db);

            var json = GetJsonResponse(result);
            Assert.False(json.GetProperty("hasCookie").GetBoolean());
        }

        [Fact]
        public void Status_MobileClient_IgnoresRequestCookie()
        {
            // For mobile, status checks session flag, not request cookies
            _controller.Request.Headers["X-Spokes-Client"] = "mobile";
            _controller.Request.Headers["Cookie"] = "chat_vault_key=cookie_present";
            var session = CreateAndSaveSession(hasVaultCookie: false);
            SetActiveSession(session);

            var result = _controller.Status(_db);

            var json = GetJsonResponse(result);
            Assert.False(json.GetProperty("hasCookie").GetBoolean());
        }

        [Fact]
        public void Status_MobileClient_RevokedSession_ReturnsHasCookieFalse()
        {
            _controller.Request.Headers["X-Spokes-Client"] = "mobile";
            var session = CreateAndSaveSession(hasVaultCookie: true, isRevoked: true);
            SetActiveSession(session);

            var result = _controller.Status(_db);

            var json = GetJsonResponse(result);
            Assert.False(json.GetProperty("hasCookie").GetBoolean());
        }

        #endregion
    }
}
