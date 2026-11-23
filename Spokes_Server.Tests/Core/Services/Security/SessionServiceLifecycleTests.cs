namespace Spokes_Server.Tests.Core.Services.Security;

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
using Spokes_Server.Tests.Helpers;

public class SessionServiceLifecycleTests : IDisposable
{
    private readonly string _testDir;
    private readonly DiskPersistenceService _persistence;
    private readonly DeviceSessionRepository _sessionRepo;
    private readonly EmployeeRepository _employeeRepo;
    private readonly Database _db;
    private readonly SessionService _sessionService;

    public SessionServiceLifecycleTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), "Spokes_SessionLifecycleTest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testDir);

        var mockConfig = new Mock<IConfiguration>();
        mockConfig.Setup(c => c["DataPath"]).Returns(_testDir);
        mockConfig.Setup(c => c["SPOKES_MASTER_PASSWORD"]).Returns("UnitTest-Master-Password-2026!");

        _persistence = new DiskPersistenceService(Mock.Of<ILogger<DiskPersistenceService>>());
        _sessionRepo = new DeviceSessionRepository(_persistence, mockConfig.Object);
        _employeeRepo = new EmployeeRepository(_persistence, mockConfig.Object);

        _db = TestDatabaseFactory.Create(_sessionRepo, _employeeRepo);
        _sessionService = new SessionService(_sessionRepo, _employeeRepo);
    }

    public void Dispose()
    {
        _persistence.Dispose();
        if (Directory.Exists(_testDir))
        {
            try { Directory.Delete(_testDir, true); } catch (Exception ex) { System.Diagnostics.Debug.WriteLine(ex.Message); }
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
        var result = _sessionService.GetActiveSession(context);

        // Assert
        Assert.NotNull(result);
        Assert.Equal(activeSession.Id, result.Id);
    }

    [Theory]
    [InlineData("SessionId", "non-existent-session-id")]
    [InlineData(ClaimTypes.NameIdentifier, "emp-no-session-claim")]
    [InlineData("SessionId", "")]
    public void GetActiveSession_UserAuthenticatedWithInvalidOrMissingSessionIdClaim_FallsThroughAndReturnsNull(string claimType, string claimValue)
    {
        var context = CreateHttpContext();
        var identity = new ClaimsIdentity(new[]
        {
            new Claim(claimType, claimValue)
        }, "Spokes_Session_v3");
        context.User = new ClaimsPrincipal(identity);

        var result = _sessionService.GetActiveSession(context);

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
        var result = _sessionService.GetActiveSession(context);

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
        var result = _sessionService.GetActiveSession(context);

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
        var result = _sessionService.GetActiveSession(context);

        // Assert
        Assert.NotNull(result);
        Assert.Equal(session.Id, result.Id);
        Assert.Equal(headerDeviceId, result.DeviceId);
    }

    [Theory]
    [InlineData("Spokes_Refresh=non_existent_token_string")]
    [InlineData("Spokes_Refresh=")]
    public void GetActiveSession_UserUnauthenticatedWithInvalidOrEmptyRefreshCookie_ReturnsNull(string cookieHeader)
    {
        var context = CreateHttpContext();
        context.Request.Headers.Cookie = cookieHeader;

        var result = _sessionService.GetActiveSession(context);

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
        var result = _sessionService.GetActiveSession(context);

        // Assert
        Assert.Null(result);
    }

    // =========================================================================
    // 3. CreateSession - User-Agent Parsing
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
    public void CreateSession_UserAgentHeader_SetsExpectedDeviceInfo(string userAgent, string expectedDeviceInfo)
    {
        // Arrange
        var emp = SeedEmployee();
        var context = CreateHttpContext();
        context.Request.Headers["User-Agent"] = userAgent;

        // Act
        var (session, _) = _sessionService.CreateSession(context, emp.Id);

        // Assert
        Assert.Equal(expectedDeviceInfo, session.DeviceInfo);
    }

    // =========================================================================
    // 4. CreateSession - Device ID Handling & Session Reuse / Isolation
    // =========================================================================

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void CreateSession_DeviceIdIsNullOrWhitespace_CreatesNewIsolatedSessionWithNullDeviceId(string? deviceId)
    {
        // Arrange
        var emp = SeedEmployee();
        var context = CreateHttpContext();

        // Act
        var (session, _) = _sessionService.CreateSession(context, emp.Id, deviceId: deviceId);

        // Assert
        Assert.NotNull(session);
        Assert.Null(session.DeviceId);
        Assert.Equal(emp.Id, session.EmployeeId);

        var stored = _sessionRepo.GetById(session.Id);
        Assert.NotNull(stored);
        Assert.Null(stored.DeviceId);
    }

    [Fact]
    public void CreateSession_DeviceIdProvided_MatchesExistingSession_ReusesAndUpdatesSession()
    {
        // Arrange
        var emp = SeedEmployee();
        var deviceId = "persistent-device-abc";
        var (_, existingSession) = SeedSession(emp.Id, deviceId: deviceId);
        var originalTokenHash = existingSession.TokenHash;

        var context = CreateHttpContext();
        context.Request.Headers["User-Agent"] = "Mozilla/5.0 (Windows NT 10.0; Win64; x64)";

        // Act
        var (session, _) = _sessionService.CreateSession(context, emp.Id, deviceId: deviceId);

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
    public void CreateSession_DeviceIdProvided_WhitespaceIsTrimmed_MatchesExistingSession()
    {
        // Arrange
        var emp = SeedEmployee();
        var deviceId = "clean-device-id";
        var (_, existingSession) = SeedSession(emp.Id, deviceId: deviceId);

        var context = CreateHttpContext();

        // Act - provide untrimmed deviceId
        var (session, _) = _sessionService.CreateSession(context, emp.Id, deviceId: "  clean-device-id  ");

        // Assert
        Assert.Equal(existingSession.Id, session.Id);
        Assert.Equal("clean-device-id", session.DeviceId);
    }

    [Fact]
    public void CreateSession_DeviceIdProvided_MultipleMatchingSessions_ReusesFirstAndRevokesDuplicates()
    {
        // Arrange
        var emp = SeedEmployee();
        var deviceId = "duplicate-device-id";
        var (_, session1) = SeedSession(emp.Id, deviceId: deviceId);
        var (_, session2) = SeedSession(emp.Id, deviceId: deviceId);
        var (_, session3) = SeedSession(emp.Id, deviceId: deviceId);

        var context = CreateHttpContext();

        // Act
        var (session, _) = _sessionService.CreateSession(context, emp.Id, deviceId: deviceId);

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
    // 5. CreateSession - Cookie Issuance & rawToken
    // =========================================================================

    [Fact]
    public void CreateSession_IssueCookieTrue_AppendsCookieAndRawTokenIsNull()
    {
        // Arrange
        var emp = SeedEmployee();
        var context = CreateHttpContext(isHttps: false);

        // Act
        var (session, rawToken) = _sessionService.CreateSession(context, emp.Id, issueCookie: true);

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
    public void CreateSession_IssueCookieTrue_HttpsRequest_SetsSecureFlagOnCookie()
    {
        // Arrange
        var emp = SeedEmployee();
        var context = CreateHttpContext(isHttps: true);

        // Act
        var (session, rawToken) = _sessionService.CreateSession(context, emp.Id, issueCookie: true);

        // Assert
        Assert.Null(rawToken);

        var setCookieHeader = context.Response.Headers["Set-Cookie"].ToString();
        Assert.Contains("secure", setCookieHeader, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CreateSession_IssueCookieFalse_RawTokenHashMatchesSessionTokenHash()
    {
        // Arrange
        var emp = SeedEmployee();
        var context = CreateHttpContext();

        // Act
        var (session, rawToken) = _sessionService.CreateSession(context, emp.Id, issueCookie: false);

        // Assert
        Assert.NotNull(rawToken);

        using var sha256 = SHA256.Create();
        var computedHash = Convert.ToBase64String(sha256.ComputeHash(Encoding.UTF8.GetBytes(rawToken)));
        Assert.Equal(computedHash, session.TokenHash);
    }

    // =========================================================================
    // 6. CreateSession - IdToken Handling
    // =========================================================================

    [Fact]
    public void CreateSession_IdTokenProvided_UpdatesExistingSessionIdToken()
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
        var (session, _) = _sessionService.CreateSession(context, emp.Id, deviceId: deviceId, idToken: newIdToken);

        // Assert
        Assert.Equal(existingSession.Id, session.Id);
        Assert.Equal(newIdToken, session.IdToken);

        var stored = _sessionRepo.GetById(session.Id);
        Assert.NotNull(stored);
        Assert.Equal(newIdToken, stored.IdToken);
    }

    [Fact]
    public void CreateSession_IdTokenNull_PreservesExistingSessionIdToken()
    {
        // Arrange
        var emp = SeedEmployee();
        var deviceId = "device-with-preserved-idtoken";
        var (_, existingSession) = SeedSession(emp.Id, deviceId: deviceId);
        existingSession.IdToken = "existing.id.token";
        _sessionRepo.Save(existingSession);

        var context = CreateHttpContext();

        // Act
        var (session, _) = _sessionService.CreateSession(context, emp.Id, deviceId: deviceId, idToken: null);

        // Assert
        Assert.Equal(existingSession.Id, session.Id);
        Assert.Equal("existing.id.token", session.IdToken);

        var stored = _sessionRepo.GetById(session.Id);
        Assert.NotNull(stored);
        Assert.Equal("existing.id.token", stored.IdToken);
    }

    [Fact]
    public void CreateSession_DeviceIdProvided_DoesNotAdoptLegacyDevSessionAcrossMismatchedPlatform()
    {
        var emp = SeedEmployee();
        var (_, mobileLegacySession) = SeedSession(emp.Id, deviceId: "dev_mobile_legacy_1");
        mobileLegacySession.IsCapacitor = true;
        mobileLegacySession.DeviceType = DeviceType.Mobile;
        mobileLegacySession.PushPublicKey = "MOBILE_ANDROID_KEYSTORE_PUBKEY";
        _sessionRepo.Save(mobileLegacySession);

        // Desktop Electron client logs in with a desk_ deviceId
        var desktopContext = CreateHttpContext();
        desktopContext.Request.Headers["User-Agent"] = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) SpokesDesktop/1.0.0";

        var (desktopSession, _) = _sessionService.CreateSession(desktopContext, emp.Id, deviceId: "desk_electron_uuid_1");

        Assert.NotEqual(mobileLegacySession.Id, desktopSession.Id);
        Assert.Equal("desk_electron_uuid_1", desktopSession.DeviceId);
        Assert.Equal(DeviceType.Desktop, desktopSession.DeviceType);
        Assert.False(desktopSession.IsCapacitor);

        var preservedMobile = _sessionRepo.GetById(mobileLegacySession.Id);
        Assert.NotNull(preservedMobile);
        Assert.Null(preservedMobile.RevokedAt);
        Assert.Equal("dev_mobile_legacy_1", preservedMobile.DeviceId);
        Assert.Equal("MOBILE_ANDROID_KEYSTORE_PUBKEY", preservedMobile.PushPublicKey);
    }
}
