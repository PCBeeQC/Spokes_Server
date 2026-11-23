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
using Spokes_Server.Core.Models.HR;
using Spokes_Server.Core.Security;
using Spokes_Server.Core.Services.Security;
using Xunit;
using Spokes_Server.Tests.Helpers;

namespace Spokes_Server.Tests.Controllers;

public class VaultControllerTests : TestDataTestBase
{
    private const string TestEmployeeId = "emp-test-user";
    private readonly Database _db;
    private readonly IDataProtectionProvider _dataProtection;
    private readonly CryptoService _crypto = new();
    private readonly VaultController _controller;

    public VaultControllerTests()
    {
        var mockConfig = new Mock<IConfiguration>();
        mockConfig.Setup(c => c["DataPath"]).Returns(_testDataPath);
        mockConfig.Setup(c => c["SPOKES_MASTER_PASSWORD"]).Returns("UnitTest-Master-Password-2026!");

        var mockLogger = new Mock<ILogger<DiskPersistenceService>>();
        var writer = new DiskPersistenceService(mockLogger.Object);

        var sessionRepo = new DeviceSessionRepository(writer, mockConfig.Object);
        var employeeRepo = new EmployeeRepository(writer, mockConfig.Object);
        _db = TestDatabaseFactory.Create(sessionRepo, employeeRepo);

        var services = new ServiceCollection();
        services.AddDataProtection().UseEphemeralDataProtectionProvider();
        var serviceProvider = services.BuildServiceProvider();
        _dataProtection = serviceProvider.GetRequiredService<IDataProtectionProvider>();

        _controller = new VaultController(_db.CoreContext, new SessionService(sessionRepo, employeeRepo), _dataProtection, crypto: _crypto)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext()
            }
        };
    }

    #region Helpers

    private Employee SeedEmployeeWithVaultKey(
        string password,
        string employeeId = TestEmployeeId,
        bool isActive = true,
        bool isSuspended = false,
        bool isBanned = false)
    {
        var (pubKey, privKey) = _crypto.GenerateRsaKeyPair();
        var kek = _crypto.DeriveKeyFromPassword(password, employeeId);
        var emp = new Employee
        {
            Id = employeeId,
            FirstName = "Vault",
            LastName = "User",
            PublicKey = pubKey,
            EncryptedPrivateKey = _crypto.EncryptAes(privKey, kek),
            IsActive = isActive,
            IsSuspended = isSuspended,
            IsBanned = isBanned
        };
        _db.Employees.Save(emp);
        return emp;
    }

    private DeviceSession CreateAndSaveSession(
        bool hasVaultCookie = false,
        bool isRevoked = false,
        DateTime? expiresAt = null)
    {
        var session = new DeviceSession
        {
            Id = Guid.NewGuid().ToString(),
            EmployeeId = TestEmployeeId,
            HasVaultCookie = hasVaultCookie,
            RevokedAt = isRevoked ? DateTime.UtcNow : null,
            ExpiresAt = expiresAt ?? DateTime.UtcNow.AddDays(30)
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

        var result = _controller.Login(request);

        var badRequest = Assert.IsType<BadRequestObjectResult>(result);
        var error = (badRequest.Value as Spokes_Server.Core.Helpers.SpokesResult)?.ErrorMessage ?? badRequest.Value?.ToString();
        Assert.Equal("Password is required.", error);
        Assert.DoesNotContain("chat_vault_key=", _controller.Response.Headers.SetCookie.ToString());
    }

    [Fact]
    public void Login_WebClient_ValidPassword_EncryptsPasswordSetsCookieAndReturnsVaultKey()
    {
        const string password = "MySecretVaultPassword123!";
        SeedEmployeeWithVaultKey(password);
        var session = CreateAndSaveSession(hasVaultCookie: false);
        SetActiveSession(session);
        var request = new VaultLoginRequest { Password = password };

        var result = _controller.Login(request);

        var json = GetJsonResponse(result);
        Assert.True(json.GetProperty("success").GetBoolean());
        var vaultKey = json.GetProperty("vaultKey").GetString();
        Assert.False(string.IsNullOrEmpty(vaultKey));

        var persisted = _db.DeviceSessions.GetById(session.Id)!;
        Assert.True(persisted.HasVaultCookie);
        Assert.False(string.IsNullOrEmpty(persisted.VaultNonce));

        // Verify ChatVaultKey.v2 structured DataProtection round-trip decryption bound to session & nonce
        var payload = VaultCookieProtector.UnprotectAndValidate(
            _dataProtection,
            vaultKey!,
            TestEmployeeId,
            session.Id,
            persisted.VaultNonce);
        Assert.NotNull(payload);
        Assert.Equal(password, payload!.Password);

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
    public void Login_MobileClientViaHeader_DoesNotSetCookie_ReturnsVaultKeyAndUpdatesSession()
    {
        const string password = "MobilePassword456";
        SeedEmployeeWithVaultKey(password);
        _controller.Request.Headers["X-Spokes-Client"] = "mobile";
        var session = CreateAndSaveSession(hasVaultCookie: false);
        SetActiveSession(session);

        var request = new VaultLoginRequest { Password = password };
        var result = _controller.Login(request);

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
        const string password = "MobilePassword789";
        SeedEmployeeWithVaultKey(password);
        _controller.Request.Headers["User-Agent"] = "Mozilla/5.0 (Linux; Android 14; Mobile) AppleWebKit/537.36 Capacitor/6.0.0";
        var session = CreateAndSaveSession(hasVaultCookie: false);
        SetActiveSession(session);

        var request = new VaultLoginRequest { Password = password };
        var result = _controller.Login(request);

        var json = GetJsonResponse(result);
        Assert.True(json.GetProperty("success").GetBoolean());

        var setCookie = _controller.Response.Headers.SetCookie.ToString();
        Assert.DoesNotContain("chat_vault_key=", setCookie);

        var persisted = _db.DeviceSessions.GetById(session.Id);
        Assert.NotNull(persisted);
        Assert.True(persisted.HasVaultCookie);
    }

    [Fact]
    public void Login_WithoutActiveSession_ReturnsForbiddenAndSetsNoCookie()
    {
        SeedEmployeeWithVaultKey("ValidPasswordButNoSession");
        var request = new VaultLoginRequest { Password = "ValidPasswordButNoSession" };

        var result = _controller.Login(request);

        var objectResult = Assert.IsAssignableFrom<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status403Forbidden, objectResult.StatusCode);
        Assert.DoesNotContain("chat_vault_key=", _controller.Response.Headers.SetCookie.ToString());
    }

    [Fact]
    public void Login_WrongPassword_ReturnsUnauthorizedWithoutSettingCookieOrHasVaultCookie()
    {
        SeedEmployeeWithVaultKey("CorrectPassword#2026");
        var session = CreateAndSaveSession(hasVaultCookie: false);
        var initialNonce = session.VaultNonce;
        SetActiveSession(session);

        var result = _controller.Login(new VaultLoginRequest { Password = "WrongPassword#2026" });

        var unauthorized = Assert.IsType<UnauthorizedObjectResult>(result);
        Assert.Equal(StatusCodes.Status401Unauthorized, unauthorized.StatusCode);
        Assert.DoesNotContain("chat_vault_key=", _controller.Response.Headers.SetCookie.ToString());

        var persisted = _db.DeviceSessions.GetById(session.Id)!;
        Assert.False(persisted.HasVaultCookie);
        Assert.Equal(initialNonce, persisted.VaultNonce);
    }

    [Fact]
    public void Login_RevokedOrExpiredSession_ReturnsForbiddenAndSetsNoCookie()
    {
        const string password = "ValidPassword#2026";
        SeedEmployeeWithVaultKey(password);

        var revokedSession = CreateAndSaveSession(hasVaultCookie: false, isRevoked: true);
        SetActiveSession(revokedSession);
        var revokedResult = Assert.IsAssignableFrom<ObjectResult>(_controller.Login(new VaultLoginRequest { Password = password }));
        Assert.Equal(StatusCodes.Status403Forbidden, revokedResult.StatusCode);

        var expiredSession = CreateAndSaveSession(hasVaultCookie: false, expiresAt: DateTime.UtcNow.AddMinutes(-1));
        SetActiveSession(expiredSession);
        var expiredResult = Assert.IsAssignableFrom<ObjectResult>(_controller.Login(new VaultLoginRequest { Password = password }));
        Assert.Equal(StatusCodes.Status403Forbidden, expiredResult.StatusCode);

        Assert.DoesNotContain("chat_vault_key=", _controller.Response.Headers.SetCookie.ToString());
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, true, false)]
    [InlineData(true, false, true)]
    public void Login_RestrictedAccountState_ReturnsForbidden(bool isActive, bool isSuspended, bool isBanned)
    {
        const string password = "ValidPassword#2026";
        SeedEmployeeWithVaultKey(password, isActive: isActive, isSuspended: isSuspended, isBanned: isBanned);
        var session = CreateAndSaveSession(hasVaultCookie: false);
        SetActiveSession(session);

        var result = Assert.IsAssignableFrom<ObjectResult>(_controller.Login(new VaultLoginRequest { Password = password }));
        Assert.Equal(StatusCodes.Status403Forbidden, result.StatusCode);
        Assert.False(_db.DeviceSessions.GetById(session.Id)!.HasVaultCookie);
    }

    #endregion

    #region Logout Tests

    [Fact]
    public void Logout_DeletesCookieAndReturnsOk()
    {
        var result = _controller.Logout();

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
    public void Logout_WithActiveSession_SetsHasVaultCookieFalseAndRotatesVaultNonce()
    {
        var session = CreateAndSaveSession(hasVaultCookie: true);
        var originalNonce = session.VaultNonce;
        SetActiveSession(session);

        var result = _controller.Logout();

        var json = GetJsonResponse(result);
        Assert.True(json.GetProperty("success").GetBoolean());

        var persisted = _db.DeviceSessions.GetById(session.Id);
        Assert.NotNull(persisted);
        Assert.False(persisted.HasVaultCookie);
        Assert.NotEqual(originalNonce, persisted.VaultNonce);
    }

    #endregion

    #region Status Tests

    [Fact]
    public void Status_WebClient_WithCookiePresent_ReturnsHasCookieTrue()
    {
        _controller.Request.Headers["Cookie"] = "chat_vault_key=some_encrypted_token";

        var result = _controller.Status();

        var json = GetJsonResponse(result);
        Assert.True(json.GetProperty("hasCookie").GetBoolean());
    }

    [Fact]
    public void Status_WebClient_WithoutCookie_ReturnsHasCookieFalse()
    {
        _controller.Request.Headers["Cookie"] = "other_session=xyz123";

        var result = _controller.Status();

        var json = GetJsonResponse(result);
        Assert.False(json.GetProperty("hasCookie").GetBoolean());
    }

    [Fact]
    public void Status_WebClient_NoCookiesAtAll_ReturnsHasCookieFalse()
    {
        var result = _controller.Status();

        var json = GetJsonResponse(result);
        Assert.False(json.GetProperty("hasCookie").GetBoolean());
    }

    [Fact]
    public void Status_WebClient_IgnoresSessionHasVaultCookieFlag()
    {
        // Even if session has HasVaultCookie = true, web client only checks Request.Cookies
        var session = CreateAndSaveSession(hasVaultCookie: true);
        SetActiveSession(session);

        var result = _controller.Status();

        var json = GetJsonResponse(result);
        Assert.False(json.GetProperty("hasCookie").GetBoolean());
    }

    [Fact]
    public void Status_MobileClientViaHeader_ActiveSessionWithVaultCookie_ReturnsHasCookieTrue()
    {
        _controller.Request.Headers["X-Spokes-Client"] = "mobile";
        var session = CreateAndSaveSession(hasVaultCookie: true);
        SetActiveSession(session);

        var result = _controller.Status();

        var json = GetJsonResponse(result);
        Assert.True(json.GetProperty("hasCookie").GetBoolean());
    }

    [Fact]
    public void Status_MobileClientViaHeader_CaseInsensitive_ReturnsHasCookieTrue()
    {
        _controller.Request.Headers["X-Spokes-Client"] = "MOBILE";
        var session = CreateAndSaveSession(hasVaultCookie: true);
        SetActiveSession(session);

        var result = _controller.Status();

        var json = GetJsonResponse(result);
        Assert.True(json.GetProperty("hasCookie").GetBoolean());
    }

    [Fact]
    public void Status_MobileClientViaUserAgent_ActiveSessionWithoutVaultCookie_ReturnsHasCookieFalse()
    {
        _controller.Request.Headers["User-Agent"] = "MyApp/1.0 Capacitor iOS";
        var session = CreateAndSaveSession(hasVaultCookie: false);
        SetActiveSession(session);

        var result = _controller.Status();

        var json = GetJsonResponse(result);
        Assert.False(json.GetProperty("hasCookie").GetBoolean());
    }

    [Fact]
    public void Status_MobileClient_NoActiveSession_ReturnsHasCookieFalse()
    {
        _controller.Request.Headers["X-Spokes-Client"] = "mobile";

        var result = _controller.Status();

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

        var result = _controller.Status();

        var json = GetJsonResponse(result);
        Assert.False(json.GetProperty("hasCookie").GetBoolean());
    }

    [Fact]
    public void Status_MobileClient_RevokedSession_ReturnsHasCookieFalse()
    {
        _controller.Request.Headers["X-Spokes-Client"] = "mobile";
        var session = CreateAndSaveSession(hasVaultCookie: true, isRevoked: true);
        SetActiveSession(session);

        var result = _controller.Status();

        var json = GetJsonResponse(result);
        Assert.False(json.GetProperty("hasCookie").GetBoolean());
    }

    #endregion

    #region Renew Tests

    [Fact]
    public void Renew_AgingV2Cookie_IssuesFresh90DayCookieAndPreservesVaultNonce()
    {
        const string password = "RenewV2Password#2026";
        SeedEmployeeWithVaultKey(password);
        var session = CreateAndSaveSession(hasVaultCookie: true);
        session.VaultNonce = "stable-nonce-1";
        _db.DeviceSessions.Save(session);
        SetActiveSession(session);

        var agingToken = VaultCookieProtector.Protect(
            _dataProtection,
            TestEmployeeId,
            session.Id,
            session.VaultNonce,
            password,
            lifetime: TimeSpan.FromDays(15));
        _controller.Request.Headers["Cookie"] = $"chat_vault_key={agingToken}";

        var result = _controller.Renew();

        var json = GetJsonResponse(result);
        Assert.True(json.GetProperty("success").GetBoolean());
        var refreshedKey = json.GetProperty("vaultKey").GetString();
        Assert.False(string.IsNullOrEmpty(refreshedKey));

        var persisted = _db.DeviceSessions.GetById(session.Id)!;
        Assert.Equal("stable-nonce-1", persisted.VaultNonce);

        var validated = VaultCookieProtector.TryUnprotectAndValidate(
            _dataProtection,
            refreshedKey!,
            TestEmployeeId,
            session.Id,
            persisted.VaultNonce);
        Assert.NotNull(validated);
        Assert.Equal(password, validated!.Payload.Password);
        Assert.False(validated.NeedsRenewal);

        var setCookie = _controller.Response.Headers.SetCookie.ToString();
        Assert.Contains("chat_vault_key=", setCookie);
        Assert.Contains(refreshedKey!, setCookie);
    }

    [Fact]
    public void Renew_LegacyV1CookieWithoutVaultNonce_UpgradesOnceAndRejectsRepeatWithoutDeletingCookie()
    {
        const string password = "RenewLegacyV1Password#2026";
        SeedEmployeeWithVaultKey(password);
        var session = CreateAndSaveSession(hasVaultCookie: true);
        session.VaultNonce = string.Empty;
        _db.DeviceSessions.Save(session);
        SetActiveSession(session);

        var legacyToken = _dataProtection.CreateProtector(VaultCookieProtector.LegacyProtectorPurpose).Protect(password);
        _controller.Request.Headers["Cookie"] = $"chat_vault_key={legacyToken}";

        // First call succeeds, persists a fresh VaultNonce, and emits a v2 cookie
        var firstResult = _controller.Renew();
        var firstJson = GetJsonResponse(firstResult);
        Assert.True(firstJson.GetProperty("success").GetBoolean());
        var upgradedKey = firstJson.GetProperty("vaultKey").GetString();
        Assert.False(string.IsNullOrEmpty(upgradedKey));

        var persisted = _db.DeviceSessions.GetById(session.Id)!;
        Assert.False(string.IsNullOrEmpty(persisted.VaultNonce));
        Assert.NotNull(VaultCookieProtector.TryUnprotectAndValidate(
            _dataProtection,
            upgradedKey!,
            TestEmployeeId,
            session.Id,
            persisted.VaultNonce));

        // Second call replaying the old v1 cookie fails closed (401) and does NOT delete the cookie
        _controller.Response.Headers.Remove("Set-Cookie");
        var secondResult = _controller.Renew();
        var unauthorized = Assert.IsType<UnauthorizedObjectResult>(secondResult);
        Assert.Equal(StatusCodes.Status401Unauthorized, unauthorized.StatusCode);
        Assert.DoesNotContain("chat_vault_key=", _controller.Response.Headers.SetCookie.ToString());
    }

    [Theory]
    [InlineData(false, false, false)] // HasVaultCookie == false
    [InlineData(true, true, false)]   // Revoked session
    [InlineData(true, false, true)]   // Expired session
    public void Renew_InvalidSessionState_ReturnsUnauthorizedWithoutSettingCookie(
        bool hasVaultCookie,
        bool isRevoked,
        bool isExpired)
    {
        const string password = "RenewInvalidPassword#2026";
        SeedEmployeeWithVaultKey(password);
        var session = CreateAndSaveSession(
            hasVaultCookie: hasVaultCookie,
            isRevoked: isRevoked,
            expiresAt: isExpired ? DateTime.UtcNow.AddMinutes(-5) : DateTime.UtcNow.AddDays(30));
        session.VaultNonce = "nonce-invalid-state";
        _db.DeviceSessions.Save(session);
        SetActiveSession(session);

        var token = VaultCookieProtector.Protect(_dataProtection, TestEmployeeId, session.Id, session.VaultNonce, password);
        _controller.Request.Headers["Cookie"] = $"chat_vault_key={token}";

        var result = _controller.Renew();

        var unauthorized = Assert.IsType<UnauthorizedObjectResult>(result);
        Assert.Equal(StatusCodes.Status401Unauthorized, unauthorized.StatusCode);
        Assert.DoesNotContain("chat_vault_key=", _controller.Response.Headers.SetCookie.ToString());
    }

    #endregion
}
