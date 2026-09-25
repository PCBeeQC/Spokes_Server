namespace Spokes_Server.Tests.Core.Security;

using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using Spokes_Server.Aggregate;
using Spokes_Server.Core.Data;
using Spokes_Server.Core.Data.Repositories.Core;
using Spokes_Server.Core.Data.Repositories.HR;
using Spokes_Server.Core.Models.Core;
using Spokes_Server.Core.Models.HR;
using Spokes_Server.Core.Security;

public class SessionHelperTests : IDisposable
{
    private readonly string _testDir;
    private readonly DiskPersistenceService _persistence;
    private readonly DeviceSessionRepository _sessionRepo;
    private readonly EmployeeRepository _employeeRepo;
    private readonly Database _db;
    private readonly SessionService _sessionService;

    public SessionHelperTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), "Spokes_SessionHelperTest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testDir);

        var mockConfig = new Mock<IConfiguration>();
        mockConfig.Setup(c => c["DataPath"]).Returns(_testDir);

        _persistence = new DiskPersistenceService(Mock.Of<ILogger<DiskPersistenceService>>());
        _sessionRepo = new DeviceSessionRepository(_persistence, mockConfig.Object);
        _employeeRepo = new EmployeeRepository(_persistence, mockConfig.Object);

        _db = new Database(_sessionRepo, _employeeRepo);
        _sessionService = new SessionService(_db);
    }

    public void Dispose()
    {
        _persistence.Dispose();
        if (Directory.Exists(_testDir))
        {
            try { Directory.Delete(_testDir, true); } catch { }
        }
    }

    private DefaultHttpContext CreateHttpContext(bool isHttps = false)
    {
        var context = new DefaultHttpContext();
        context.Request.Scheme = isHttps ? "https" : "http";
        context.Request.Host = new HostString("localhost", 5000);

        var services = new ServiceCollection();
        services.AddSingleton(_sessionService);
        services.AddSingleton(_db);
        context.RequestServices = services.BuildServiceProvider();

        return context;
    }

    private Employee SeedEmployee(string? employeeId = null, bool isActive = true)
    {
        var emp = new Employee
        {
            Id = employeeId ?? "emp_" + Guid.NewGuid().ToString("N"),
            FirstName = "Test",
            LastName = "Employee",
            Email = "test.employee@example.com",
            IsActive = isActive,
            IsSuspended = false,
            IsBanned = false
        };
        _employeeRepo.Save(emp);
        return emp;
    }

    private (string rawToken, DeviceSession session) SeedSession(
        string employeeId,
        string? deviceId = null,
        bool isRevoked = false,
        DateTime? expiresAt = null)
    {
        var rawToken = "raw_token_" + Guid.NewGuid().ToString("N");
        using var sha256 = SHA256.Create();
        var tokenHash = Convert.ToBase64String(sha256.ComputeHash(Encoding.UTF8.GetBytes(rawToken)));

        var session = new DeviceSession
        {
            Id = "sess_" + Guid.NewGuid().ToString("N"),
            EmployeeId = employeeId,
            DeviceId = deviceId,
            TokenHash = tokenHash,
            DeviceInfo = "Test Device",
            CreatedAt = DateTime.UtcNow,
            ExpiresAt = expiresAt ?? DateTime.UtcNow.AddDays(90),
            LastSeenAt = DateTime.UtcNow,
            RevokedAt = isRevoked ? DateTime.UtcNow.AddHours(-1) : null
        };
        _sessionRepo.Save(session);
        return (rawToken, session);
    }

    // =========================================================================
    // 1. GetActiveSession - Authenticated Identity Flow
    // =========================================================================

    [Fact]
    public void GetActiveSession_UserAuthenticatedWithValidSessionIdClaim_ReturnsActiveSession()
    {
        // Arrange
        var emp = SeedEmployee();
        var (_, session) = SeedSession(emp.Id, "device-1");

        var context = CreateHttpContext();
        var identity = new ClaimsIdentity(new[]
        {
            new Claim("SessionId", session.Id),
            new Claim("sub", emp.Id)
        }, "Spokes_Session_v3");
        context.User = new ClaimsPrincipal(identity);

        // Act
        var result = SessionHelper.GetActiveSession(context, _db);

        // Assert
        Assert.NotNull(result);
        Assert.Equal(session.Id, result.Id);
        Assert.Equal(emp.Id, result.EmployeeId);
    }

    [Fact]
    public void GetActiveSession_UserAuthenticatedWithRevokedSession_FallsThroughAndReturnsNullWhenNoCookie()
    {
        // Arrange
        var emp = SeedEmployee();
        var (_, session) = SeedSession(emp.Id, "device-1", isRevoked: true);

        var context = CreateHttpContext();
        var identity = new ClaimsIdentity(new[]
        {
            new Claim("SessionId", session.Id),
            new Claim("sub", emp.Id)
        }, "Spokes_Session_v3");
        context.User = new ClaimsPrincipal(identity);

        // Act
        var result = SessionHelper.GetActiveSession(context, _db);

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public void GetActiveSession_UserAuthenticatedWithRevokedSession_FallsThroughAndReturnsSessionFromRefreshCookie()
    {
        // Arrange
        var emp = SeedEmployee();
        var (_, revokedSession) = SeedSession(emp.Id, "device-revoked", isRevoked: true);
        var (rawToken, activeSession) = SeedSession(emp.Id, "device-active", isRevoked: false);

        var context = CreateHttpContext();
        var identity = new ClaimsIdentity(new[]
        {
            new Claim("SessionId", revokedSession.Id),
            new Claim("sub", emp.Id)
        }, "Spokes_Session_v3");
        context.User = new ClaimsPrincipal(identity);

        // Supply valid fallback refresh cookie and matching device header
        context.Request.Headers.Cookie = $"Spokes_Refresh={rawToken}";
        context.Request.Headers["X-Device-Id"] = "device-active";

        // Act
        var result = SessionHelper.GetActiveSession(context, _db);

        // Assert
        Assert.NotNull(result);
        Assert.Equal(activeSession.Id, result.Id);
    }

    [Fact]
    public void GetActiveSession_UserAuthenticatedWithNonExistentSessionId_FallsThroughAndReturnsNull()
    {
        // Arrange
        var context = CreateHttpContext();
        var identity = new ClaimsIdentity(new[]
        {
            new Claim("SessionId", "non-existent-session-id")
        }, "Spokes_Session_v3");
        context.User = new ClaimsPrincipal(identity);

        // Act
        var result = SessionHelper.GetActiveSession(context, _db);

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public void GetActiveSession_UserAuthenticatedWithoutSessionIdClaim_FallsThroughAndReturnsNull()
    {
        // Arrange
        var emp = SeedEmployee();
        var context = CreateHttpContext();
        var identity = new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.NameIdentifier, emp.Id)
        }, "Spokes_Session_v3");
        context.User = new ClaimsPrincipal(identity);

        // Act
        var result = SessionHelper.GetActiveSession(context, _db);

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public void GetActiveSession_UserAuthenticatedWithEmptySessionIdClaim_FallsThroughAndReturnsNull()
    {
        // Arrange
        var context = CreateHttpContext();
        var identity = new ClaimsIdentity(new[]
        {
            new Claim("SessionId", "")
        }, "Spokes_Session_v3");
        context.User = new ClaimsPrincipal(identity);

        // Act
        var result = SessionHelper.GetActiveSession(context, _db);

        // Assert
        Assert.Null(result);
    }

    // =========================================================================
    // 2. GetActiveSession - Unauthenticated Refresh Cookie Fallback Flow
    // =========================================================================

    [Fact]
    public void GetActiveSession_UserUnauthenticatedWithValidCookieAndXDeviceIdHeader_ReturnsSessionViaValidateToken()
    {
        // Arrange
        var emp = SeedEmployee();
        var deviceId = "test-device-uuid-123";
        var (rawToken, session) = SeedSession(emp.Id, deviceId: deviceId);

        var context = CreateHttpContext();
        context.Request.Headers.Cookie = $"Spokes_Refresh={rawToken}";
        context.Request.Headers["X-Device-Id"] = deviceId;

        // Act
        var result = SessionHelper.GetActiveSession(context, _db);

        // Assert
        Assert.NotNull(result);
        Assert.Equal(session.Id, result.Id);
        Assert.Equal(deviceId, result.DeviceId);
    }

    [Fact]
    public void GetActiveSession_UserUnauthenticatedWithValidCookieAndSpokesDeviceCookie_ReturnsSessionViaValidateToken()
    {
        // Arrange
        var emp = SeedEmployee();
        var deviceId = "test-device-cookie-456";
        var (rawToken, session) = SeedSession(emp.Id, deviceId: deviceId);

        var context = CreateHttpContext();
        context.Request.Headers.Cookie = $"Spokes_Refresh={rawToken}; Spokes_Device={deviceId}";

        // Act
        var result = SessionHelper.GetActiveSession(context, _db);

        // Assert
        Assert.NotNull(result);
        Assert.Equal(session.Id, result.Id);
        Assert.Equal(deviceId, result.DeviceId);
    }

    [Fact]
    public void GetActiveSession_UserUnauthenticatedWithXDeviceIdHeaderPrecedenceOverSpokesDeviceCookie_UsesHeaderDeviceId()
    {
        // Arrange
        var emp = SeedEmployee();
        var headerDeviceId = "header-device-id";
        var cookieDeviceId = "cookie-device-id";
        var (rawToken, session) = SeedSession(emp.Id, deviceId: headerDeviceId);

        var context = CreateHttpContext();
        context.Request.Headers.Cookie = $"Spokes_Refresh={rawToken}; Spokes_Device={cookieDeviceId}";
        context.Request.Headers["X-Device-Id"] = headerDeviceId;

        // Act
        var result = SessionHelper.GetActiveSession(context, _db);

        // Assert
        Assert.NotNull(result);
        Assert.Equal(session.Id, result.Id);
        Assert.Equal(headerDeviceId, result.DeviceId);
    }

    [Fact]
    public void GetActiveSession_UserUnauthenticatedWithValidCookieUnboundSession_ReturnsSessionViaValidateToken()
    {
        // Arrange - Unbound desktop session where DeviceId is null
        var emp = SeedEmployee();
        var (rawToken, session) = SeedSession(emp.Id, deviceId: null);

        var context = CreateHttpContext();
        context.Request.Headers.Cookie = $"Spokes_Refresh={rawToken}";

        // Act
        var result = SessionHelper.GetActiveSession(context, _db);

        // Assert
        Assert.NotNull(result);
        Assert.Equal(session.Id, result.Id);
        Assert.Null(result.DeviceId);
    }

    [Fact]
    public void GetActiveSession_UserUnauthenticatedWithInvalidCookie_ReturnsNull()
    {
        // Arrange
        var context = CreateHttpContext();
        context.Request.Headers.Cookie = "Spokes_Refresh=non_existent_token_string";

        // Act
        var result = SessionHelper.GetActiveSession(context, _db);

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public void GetActiveSession_UserUnauthenticatedWithNoCookies_ReturnsNull()
    {
        // Arrange
        var context = CreateHttpContext();

        // Act
        var result = SessionHelper.GetActiveSession(context, _db);

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public void GetActiveSession_UserUnauthenticatedWithEmptyRefreshCookie_ReturnsNull()
    {
        // Arrange
        var context = CreateHttpContext();
        context.Request.Headers.Cookie = "Spokes_Refresh=";

        // Act
        var result = SessionHelper.GetActiveSession(context, _db);

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public void GetActiveSession_UserUnauthenticatedWithExpiredSessionCookie_ReturnsNull()
    {
        // Arrange
        var emp = SeedEmployee();
        var (rawToken, _) = SeedSession(emp.Id, deviceId: null, expiresAt: DateTime.UtcNow.AddDays(-1));

        var context = CreateHttpContext();
        context.Request.Headers.Cookie = $"Spokes_Refresh={rawToken}";

        // Act
        var result = SessionHelper.GetActiveSession(context, _db);

        // Assert
        Assert.Null(result);
    }

    // =========================================================================
    // 3. IssueRefreshToken - User-Agent Parsing
    // =========================================================================

    [Theory]
    [InlineData("Mozilla/5.0 (Windows NT 10.0; Win64; x64)", "Windows")]
    [InlineData("Mozilla/5.0 (iPhone; CPU iPhone OS 16_0 like Mac OS X)", "iOS")]
    [InlineData("Mozilla/5.0 (iPad; CPU OS 16_0 like Mac OS X)", "iOS")]
    [InlineData("Mozilla/5.0 (Linux; Android 13; Pixel 7)", "Android")]
    [InlineData("Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7)", "macOS")]
    [InlineData("Mozilla/5.0 (Windows NT 10.0; Win64; x64) Capacitor/5.0", "Windows (App)")]
    [InlineData("Mozilla/5.0 (iPhone; CPU iPhone OS 16_0 like Mac OS X) Capacitor/5.0", "iOS (App)")]
    [InlineData("Mozilla/5.0 (Linux; U; Android 13; wv)", "Android (App)")]
    [InlineData("Mozilla/5.0 (Linux; Android 13) Capacitor", "Android (App)")]
    [InlineData("Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) Capacitor/5.0", "macOS (App)")]
    [InlineData("CustomClient/1.0", "Unknown Device")]
    [InlineData("CustomClient/1.0 Capacitor/5.0", "Unknown Device (App)")]
    [InlineData("CustomClient/1.0 wv", "Unknown Device (App)")]
    public void IssueRefreshToken_UserAgentHeader_SetsExpectedDeviceInfo(string userAgent, string expectedDeviceInfo)
    {
        // Arrange
        var emp = SeedEmployee();
        var context = CreateHttpContext();
        context.Request.Headers["User-Agent"] = userAgent;

        // Act
        var session = SessionHelper.IssueRefreshToken(context, _db, emp.Id, out _);

        // Assert
        Assert.Equal(expectedDeviceInfo, session.DeviceInfo);
    }

    // =========================================================================
    // 4. IssueRefreshToken - Device ID Handling & Session Reuse / Isolation
    // =========================================================================

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void IssueRefreshToken_DeviceIdIsNullOrWhitespace_CreatesNewIsolatedSessionWithNullDeviceId(string? deviceId)
    {
        // Arrange
        var emp = SeedEmployee();
        var context = CreateHttpContext();

        // Act
        var session = SessionHelper.IssueRefreshToken(context, _db, emp.Id, out _, deviceId: deviceId);

        // Assert
        Assert.NotNull(session);
        Assert.Null(session.DeviceId);
        Assert.Equal(emp.Id, session.EmployeeId);

        var stored = _sessionRepo.GetById(session.Id);
        Assert.NotNull(stored);
        Assert.Null(stored.DeviceId);
    }

    [Fact]
    public void IssueRefreshToken_DeviceIdNull_DoesNotAdoptExistingSessions()
    {
        // Arrange - Desktop web sessions (deviceId == null) always create isolated sessions
        var emp = SeedEmployee();
        var (_, existingSession) = SeedSession(emp.Id, deviceId: null);

        var context = CreateHttpContext();

        // Act
        var newSession = SessionHelper.IssueRefreshToken(context, _db, emp.Id, out _, deviceId: null);

        // Assert
        Assert.NotEqual(existingSession.Id, newSession.Id);
        Assert.Null(newSession.DeviceId);

        // Both sessions remain active in DB
        var storedExisting = _sessionRepo.GetById(existingSession.Id);
        var storedNew = _sessionRepo.GetById(newSession.Id);
        Assert.NotNull(storedExisting);
        Assert.Null(storedExisting.RevokedAt);
        Assert.NotNull(storedNew);
        Assert.Null(storedNew.RevokedAt);
    }

    [Fact]
    public void IssueRefreshToken_DeviceIdProvided_MatchesExistingSession_ReusesAndUpdatesSession()
    {
        // Arrange
        var emp = SeedEmployee();
        var deviceId = "persistent-device-abc";
        var (_, existingSession) = SeedSession(emp.Id, deviceId: deviceId);
        var originalTokenHash = existingSession.TokenHash;

        var context = CreateHttpContext();
        context.Request.Headers["User-Agent"] = "Mozilla/5.0 (Windows NT 10.0; Win64; x64)";

        // Act
        var session = SessionHelper.IssueRefreshToken(context, _db, emp.Id, out _, deviceId: deviceId);

        // Assert
        Assert.Equal(existingSession.Id, session.Id);
        Assert.Equal(deviceId, session.DeviceId);
        Assert.NotEqual(originalTokenHash, session.TokenHash);
        Assert.Equal("Windows", session.DeviceInfo);
        Assert.True(session.ExpiresAt > DateTime.UtcNow.AddDays(89));
        Assert.True(session.LastSeenAt > DateTime.UtcNow.AddMinutes(-1));

        var stored = _sessionRepo.GetById(session.Id);
        Assert.NotNull(stored);
        Assert.Equal(session.TokenHash, stored.TokenHash);
    }

    [Fact]
    public void IssueRefreshToken_DeviceIdProvided_PreservesPushCredentialsAcrossReLogin()
    {
        // Arrange
        var emp = SeedEmployee();
        var deviceId = "mobile-device-push-1";
        var (_, existingSession) = SeedSession(emp.Id, deviceId: deviceId);

        existingSession.PushEndpoint = "https://fcm.googleapis.com/fcm/send/token123";
        existingSession.PushP256dh = "test-p256dh-key";
        existingSession.PushAuth = "test-auth-secret";
        existingSession.PushEnabled = true;
        _sessionRepo.Save(existingSession);

        var context = CreateHttpContext();

        // Act
        var session = SessionHelper.IssueRefreshToken(context, _db, emp.Id, out _, deviceId: deviceId);

        // Assert
        Assert.Equal(existingSession.Id, session.Id);
        Assert.Equal("https://fcm.googleapis.com/fcm/send/token123", session.PushEndpoint);
        Assert.Equal("test-p256dh-key", session.PushP256dh);
        Assert.Equal("test-auth-secret", session.PushAuth);
        Assert.True(session.PushEnabled);

        var stored = _sessionRepo.GetById(session.Id);
        Assert.NotNull(stored);
        Assert.Equal("https://fcm.googleapis.com/fcm/send/token123", stored.PushEndpoint);
    }

    [Fact]
    public void IssueRefreshToken_DeviceIdProvided_WhitespaceIsTrimmed_MatchesExistingSession()
    {
        // Arrange
        var emp = SeedEmployee();
        var deviceId = "clean-device-id";
        var (_, existingSession) = SeedSession(emp.Id, deviceId: deviceId);

        var context = CreateHttpContext();

        // Act - provide untrimmed deviceId
        var session = SessionHelper.IssueRefreshToken(context, _db, emp.Id, out _, deviceId: "  clean-device-id  ");

        // Assert
        Assert.Equal(existingSession.Id, session.Id);
        Assert.Equal("clean-device-id", session.DeviceId);
    }

    [Fact]
    public void IssueRefreshToken_DeviceIdProvided_NoExactMatch_AdoptsLegacyDevSession()
    {
        // Arrange
        var emp = SeedEmployee();
        var legacyDeviceId = "dev_legacy_phone_999";
        var (_, legacySession) = SeedSession(emp.Id, deviceId: legacyDeviceId);
        legacySession.PushEndpoint = "https://push.example.com/legacy";
        _sessionRepo.Save(legacySession);

        var modernDeviceId = "modern-guid-device-123";
        var context = CreateHttpContext();

        // Act
        var session = SessionHelper.IssueRefreshToken(context, _db, emp.Id, out _, deviceId: modernDeviceId);

        // Assert
        Assert.Equal(legacySession.Id, session.Id);
        Assert.Equal(modernDeviceId, session.DeviceId); // modernized device ID
        Assert.Equal("https://push.example.com/legacy", session.PushEndpoint); // push preserved

        var stored = _sessionRepo.GetById(session.Id);
        Assert.NotNull(stored);
        Assert.Equal(modernDeviceId, stored.DeviceId);
    }

    [Fact]
    public void IssueRefreshToken_DeviceIdProvided_NoExactMatchAndUnboundSessionExists_DoesNotAdoptUnboundSession()
    {
        // Arrange - Unbound desktop sessions (DeviceId is null) should NEVER be adopted
        var emp = SeedEmployee();
        var (_, unboundSession) = SeedSession(emp.Id, deviceId: null);

        var modernDeviceId = "modern-device-456";
        var context = CreateHttpContext();

        // Act
        var session = SessionHelper.IssueRefreshToken(context, _db, emp.Id, out _, deviceId: modernDeviceId);

        // Assert
        Assert.NotEqual(unboundSession.Id, session.Id);
        Assert.Equal(modernDeviceId, session.DeviceId);

        var storedUnbound = _sessionRepo.GetById(unboundSession.Id);
        Assert.NotNull(storedUnbound);
        Assert.Null(storedUnbound.DeviceId);
        Assert.Null(storedUnbound.RevokedAt);
    }

    [Fact]
    public void IssueRefreshToken_DeviceIdProvided_MultipleMatchingSessions_ReusesFirstAndRevokesDuplicates()
    {
        // Arrange
        var emp = SeedEmployee();
        var deviceId = "duplicate-device-id";
        var (_, session1) = SeedSession(emp.Id, deviceId: deviceId);
        var (_, session2) = SeedSession(emp.Id, deviceId: deviceId);
        var (_, session3) = SeedSession(emp.Id, deviceId: deviceId);

        var context = CreateHttpContext();

        // Act
        var session = SessionHelper.IssueRefreshToken(context, _db, emp.Id, out _, deviceId: deviceId);

        // Assert: One of the matching sessions was reused and kept active
        var allIds = new HashSet<string> { session1.Id, session2.Id, session3.Id };
        Assert.Contains(session.Id, allIds);
        Assert.Null(session.RevokedAt);

        // All other duplicate sessions must have been revoked
        var otherIds = allIds.Where(id => id != session.Id).ToList();
        Assert.Equal(2, otherIds.Count);
        foreach (var otherId in otherIds)
        {
            var otherSession = _sessionRepo.GetById(otherId);
            Assert.NotNull(otherSession);
            Assert.NotNull(otherSession.RevokedAt);
        }
    }

    // =========================================================================
    // 5. IssueRefreshToken - Cookie Issuance & rawToken
    // =========================================================================

    [Fact]
    public void IssueRefreshToken_IssueCookieTrue_AppendsCookieAndRawTokenIsNull()
    {
        // Arrange
        var emp = SeedEmployee();
        var context = CreateHttpContext(isHttps: false);

        // Act
        var session = SessionHelper.IssueRefreshToken(context, _db, emp.Id, out var rawToken, issueCookie: true);

        // Assert
        Assert.Null(rawToken);

        var setCookieHeader = context.Response.Headers["Set-Cookie"].ToString();
        Assert.NotEmpty(setCookieHeader);
        Assert.Contains("Spokes_Refresh=", setCookieHeader);
        Assert.Contains("path=/", setCookieHeader, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("httponly", setCookieHeader, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=lax", setCookieHeader, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("secure", setCookieHeader, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void IssueRefreshToken_IssueCookieTrue_HttpsRequest_SetsSecureFlagOnCookie()
    {
        // Arrange
        var emp = SeedEmployee();
        var context = CreateHttpContext(isHttps: true);

        // Act
        var session = SessionHelper.IssueRefreshToken(context, _db, emp.Id, out var rawToken, issueCookie: true);

        // Assert
        Assert.Null(rawToken);

        var setCookieHeader = context.Response.Headers["Set-Cookie"].ToString();
        Assert.Contains("secure", setCookieHeader, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void IssueRefreshToken_IssueCookieFalse_DoesNotSetCookieAndReturnsRawToken()
    {
        // Arrange
        var emp = SeedEmployee();
        var context = CreateHttpContext();

        // Act
        var session = SessionHelper.IssueRefreshToken(context, _db, emp.Id, out var rawToken, issueCookie: false);

        // Assert
        Assert.NotNull(rawToken);
        Assert.NotEmpty(rawToken);

        var setCookieHeader = context.Response.Headers["Set-Cookie"].ToString();
        Assert.Empty(setCookieHeader);
    }

    [Fact]
    public void IssueRefreshToken_IssueCookieFalse_RawTokenHashMatchesSessionTokenHash()
    {
        // Arrange
        var emp = SeedEmployee();
        var context = CreateHttpContext();

        // Act
        var session = SessionHelper.IssueRefreshToken(context, _db, emp.Id, out var rawToken, issueCookie: false);

        // Assert
        Assert.NotNull(rawToken);

        using var sha256 = SHA256.Create();
        var computedHash = Convert.ToBase64String(sha256.ComputeHash(Encoding.UTF8.GetBytes(rawToken)));
        Assert.Equal(computedHash, session.TokenHash);
    }

    // =========================================================================
    // 6. IssueRefreshToken - IdToken Handling
    // =========================================================================

    [Fact]
    public void IssueRefreshToken_IdTokenProvided_SetsIdTokenOnNewSession()
    {
        // Arrange
        var emp = SeedEmployee();
        var context = CreateHttpContext();
        var idToken = "sample.jwt.id_token_value";

        // Act
        var session = SessionHelper.IssueRefreshToken(context, _db, emp.Id, out _, idToken: idToken);

        // Assert
        Assert.Equal(idToken, session.IdToken);

        var stored = _sessionRepo.GetById(session.Id);
        Assert.NotNull(stored);
        Assert.Equal(idToken, stored.IdToken);
    }

    [Fact]
    public void IssueRefreshToken_IdTokenProvided_UpdatesExistingSessionIdToken()
    {
        // Arrange
        var emp = SeedEmployee();
        var deviceId = "device-with-idtoken";
        var (_, existingSession) = SeedSession(emp.Id, deviceId: deviceId);
        existingSession.IdToken = "old.id.token";
        _sessionRepo.Save(existingSession);

        var context = CreateHttpContext();
        var newIdToken = "new.id.token";

        // Act
        var session = SessionHelper.IssueRefreshToken(context, _db, emp.Id, out _, deviceId: deviceId, idToken: newIdToken);

        // Assert
        Assert.Equal(existingSession.Id, session.Id);
        Assert.Equal(newIdToken, session.IdToken);

        var stored = _sessionRepo.GetById(session.Id);
        Assert.NotNull(stored);
        Assert.Equal(newIdToken, stored.IdToken);
    }

    [Fact]
    public void IssueRefreshToken_IdTokenNull_PreservesExistingSessionIdToken()
    {
        // Arrange
        var emp = SeedEmployee();
        var deviceId = "device-with-preserved-idtoken";
        var (_, existingSession) = SeedSession(emp.Id, deviceId: deviceId);
        existingSession.IdToken = "existing.id.token";
        _sessionRepo.Save(existingSession);

        var context = CreateHttpContext();

        // Act
        var session = SessionHelper.IssueRefreshToken(context, _db, emp.Id, out _, deviceId: deviceId, idToken: null);

        // Assert
        Assert.Equal(existingSession.Id, session.Id);
        Assert.Equal("existing.id.token", session.IdToken);

        var stored = _sessionRepo.GetById(session.Id);
        Assert.NotNull(stored);
        Assert.Equal("existing.id.token", stored.IdToken);
    }
}
