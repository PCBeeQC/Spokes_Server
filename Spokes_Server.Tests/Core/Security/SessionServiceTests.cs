using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
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
using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Xunit;

namespace Spokes_Server.Tests.Core.Security;

public class SessionServiceTests : IDisposable
{
    private readonly string _testDir;
    private readonly DiskPersistenceService _persistence;
    private readonly DeviceSessionRepository _sessionRepo;
    private readonly EmployeeRepository _employeeRepo;
    private readonly Database _db;
    private readonly SessionService _service;

    public SessionServiceTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), "Spokes_SessionTest_" + Guid.NewGuid());
        Directory.CreateDirectory(_testDir);

        var mockConfig = new Mock<IConfiguration>();
        mockConfig.Setup(c => c["DataPath"]).Returns(_testDir);

        var mockLogger = new Mock<ILogger<DiskPersistenceService>>();
        _persistence = new DiskPersistenceService(mockLogger.Object);

        _sessionRepo = new DeviceSessionRepository(_persistence, mockConfig.Object);
        _employeeRepo = new EmployeeRepository(_persistence, mockConfig.Object);

        _db = new Database(_sessionRepo, _employeeRepo);
        _service = new SessionService(_db);
    }

    public void Dispose()
    {
        _persistence.Dispose();
        if (Directory.Exists(_testDir))
        {
            try { Directory.Delete(_testDir, true); } catch { }
        }
    }

    [Fact]
    public void BuildIdentity_WithAdminEmployee_AddsAdminRoleAndPermissions()
    {
        var employee = new Employee
        {
            Id = "emp1",
            FirstName = "John",
            LastName = "Doe",
            Email = "john@example.com",
            IsAdmin = true,
            Permissions = new List<string> { "ManageUsers" }
        };

        var identity = _service.BuildIdentity(employee, "session1");

        Assert.Equal("emp1", identity.FindFirst("sub")?.Value);
        Assert.Equal("John Doe", identity.FindFirst("name")?.Value);
        Assert.Equal("john@example.com", identity.FindFirst("email")?.Value);
        Assert.Equal("emp1", identity.FindFirst("EmployeeId")?.Value);
        Assert.Equal("session1", identity.FindFirst("SessionId")?.Value);
        
        Assert.Contains(identity.Claims, c => c.Type == ClaimTypes.Role && c.Value == "Admin");
        Assert.Contains(identity.Claims, c => c.Type == "Permission" && c.Value == "ManageUsers");
    }

    [Fact]
    public void BuildIdentity_WithPermissionGroup_InheritsGroupPermissions()
    {
        var employee = new Employee
        {
            Id = "emp_group_1",
            FirstName = "Jane",
            LastName = "Smith",
            Email = "jane@example.com",
            IsAdmin = false,
            Permissions = new List<string>(),
            PermissionGroup = new PermissionGroup
            {
                Name = "Editors",
                Permissions = new List<string> { "Chat.Use", "Projects.View" }
            }
        };

        var identity = _service.BuildIdentity(employee, "session_grp");

        Assert.Contains(identity.Claims, c => c.Type == "Permission" && c.Value == "Chat.Use");
        Assert.Contains(identity.Claims, c => c.Type == "Permission" && c.Value == "Projects.View");
    }

    [Fact]
    public void BuildIdentity_WithNullPermissions_DoesNotThrow()
    {
        var employee = new Employee
        {
            Id = "emp_null_perm",
            FirstName = "Jane",
            LastName = "Doe",
            Permissions = null!
        };

        var identity = _service.BuildIdentity(employee, "session_null");
        Assert.NotNull(identity);
    }

    [Fact]
    public void ValidateToken_WithNullOrEmptyToken_ReturnsNull()
    {
        Assert.Null(_service.ValidateToken(null!, "device1"));
        Assert.Null(_service.ValidateToken("", "device1"));
    }

    [Fact]
    public void ValidateToken_WithNonExistentToken_ReturnsNull()
    {
        var result = _service.ValidateToken("non_existent_token", "device1");
        Assert.Null(result);
    }

    [Fact]
    public void ValidateToken_WhenDeviceIdChecked_BehavesCorrectly()
    {
        var rawToken = "test_token_secret";
        using var sha256 = SHA256.Create();
        var tokenHash = Convert.ToBase64String(sha256.ComputeHash(Encoding.UTF8.GetBytes(rawToken)));

        var employee = new Employee
        {
            Id = "emp_test",
            FirstName = "Alice",
            LastName = "Smith",
            IsActive = true
        };
        _employeeRepo.Save(employee);

        var session = new DeviceSession
        {
            Id = "sess_1",
            EmployeeId = employee.Id,
            TokenHash = tokenHash,
            DeviceId = "device_phone",
            ExpiresAt = DateTime.UtcNow.AddDays(7)
        };
        _sessionRepo.Save(session);

        // 1. Wrong device ID provided -> rejects
        var resultWrongDevice = _service.ValidateToken(rawToken, expectedDeviceId: "device_tablet");
        Assert.Null(resultWrongDevice);

        // 2. Missing device ID for a mobile session (expectedDeviceId is null) -> rejects to prevent cross-device hijacking
        var resultEmptyDevice = _service.ValidateToken(rawToken, expectedDeviceId: null);
        Assert.Null(resultEmptyDevice);

        // 3. Matching device ID provided -> succeeds
        var resultValid = _service.ValidateToken(rawToken, expectedDeviceId: "device_phone");
        Assert.NotNull(resultValid);
        Assert.Equal(session.Id, resultValid.Value.Session.Id);
        Assert.Equal(employee.Id, resultValid.Value.Employee.Id);

        // 4. Desktop session (session.DeviceId is null) -> succeeds when expectedDeviceId is null
        var desktopRawToken = "test_desktop_token";
        var desktopTokenHash = Convert.ToBase64String(sha256.ComputeHash(Encoding.UTF8.GetBytes(desktopRawToken)));
        var desktopSession = new DeviceSession
        {
            Id = "sess_desktop",
            EmployeeId = employee.Id,
            TokenHash = desktopTokenHash,
            DeviceId = null,
            ExpiresAt = DateTime.UtcNow.AddDays(7)
        };
        _sessionRepo.Save(desktopSession);

        var resultDesktop = _service.ValidateToken(desktopRawToken, expectedDeviceId: null);
        Assert.NotNull(resultDesktop);
        Assert.Equal("sess_desktop", resultDesktop.Value.Session.Id);
    }

    [Fact]
    public void ValidateToken_WhenSessionExpiredOrRevoked_ReturnsNullUnlessAllowRevoked()
    {
        var rawToken = "test_token_expired";
        using var sha256 = SHA256.Create();
        var tokenHash = Convert.ToBase64String(sha256.ComputeHash(Encoding.UTF8.GetBytes(rawToken)));

        var employee = new Employee { Id = "emp_expired", FirstName = "Bob", IsActive = true };
        _employeeRepo.Save(employee);

        // Expired
        var expiredSession = new DeviceSession
        {
            Id = "sess_exp",
            EmployeeId = employee.Id,
            TokenHash = tokenHash,
            ExpiresAt = DateTime.UtcNow.AddMinutes(-5)
        };
        _sessionRepo.Save(expiredSession);
        Assert.Null(_service.ValidateToken(rawToken));

        // Revoked
        expiredSession.ExpiresAt = DateTime.UtcNow.AddDays(1);
        expiredSession.RevokedAt = DateTime.UtcNow;
        _sessionRepo.Save(expiredSession);
        
        // Standard check rejects revoked
        Assert.Null(_service.ValidateToken(rawToken));

        // Force-logout check with allowRevoked returns the session
        var revokedResult = _service.ValidateToken(rawToken, allowRevoked: true);
        Assert.NotNull(revokedResult);
        Assert.Equal("sess_exp", revokedResult.Value.Session.Id);
    }

    [Theory]
    [InlineData(false, false, false)] // Inactive
    [InlineData(true, true, false)]  // Suspended
    [InlineData(true, false, true)]  // Banned
    public void ValidateToken_WhenEmployeeDisabled_ReturnsNull(bool isActive, bool isSuspended, bool isBanned)
    {
        var rawToken = "test_token_disabled";
        using var sha256 = SHA256.Create();
        var tokenHash = Convert.ToBase64String(sha256.ComputeHash(Encoding.UTF8.GetBytes(rawToken)));

        var employee = new Employee
        {
            Id = "emp_dis_" + Guid.NewGuid(),
            FirstName = "Disabled",
            IsActive = isActive,
            IsSuspended = isSuspended,
            IsBanned = isBanned
        };
        _employeeRepo.Save(employee);

        var session = new DeviceSession
        {
            Id = "sess_dis_" + Guid.NewGuid(),
            EmployeeId = employee.Id,
            TokenHash = tokenHash,
            ExpiresAt = DateTime.UtcNow.AddDays(7)
        };
        _sessionRepo.Save(session);

        var result = _service.ValidateToken(rawToken);
        Assert.Null(result);
    }

    [Fact]
    public async Task SignInAndExtendAsync_ExtendsSessionExpiryAndSaves()
    {
        var employee = new Employee { Id = "emp_extend", FirstName = "Eve", IsActive = true };
        _employeeRepo.Save(employee);

        var session = new DeviceSession
        {
            Id = "sess_extend",
            EmployeeId = employee.Id,
            ExpiresAt = DateTime.UtcNow.AddHours(1),
            LastSeenAt = DateTime.UtcNow.AddDays(-1)
        };
        _sessionRepo.Save(session);

        var authMock = new Mock<IAuthenticationService>();
        var services = new ServiceCollection();
        services.AddSingleton(authMock.Object);
        var context = new DefaultHttpContext { RequestServices = services.BuildServiceProvider() };

        await _service.SignInAndExtendAsync(context, session, employee);

        var saved = _sessionRepo.GetById("sess_extend");
        Assert.NotNull(saved);
        Assert.True(saved.ExpiresAt > DateTime.UtcNow.AddDays(89));
        Assert.True(saved.LastSeenAt > DateTime.UtcNow.AddMinutes(-1));

        authMock.Verify(a => a.SignInAsync(context, CookieAuthenticationDefaults.AuthenticationScheme, It.IsAny<ClaimsPrincipal>(), It.Is<AuthenticationProperties>(p => p.IsPersistent)), Times.Once);
    }

    [Fact]
    public async Task SignInAndExtendAsync_WithDeviceId_EmitsSpokesDeviceCookie()
    {
        var employee = new Employee { Id = "emp_dev_cookie", FirstName = "Eve", IsActive = true };
        _employeeRepo.Save(employee);

        var session = new DeviceSession
        {
            Id = "sess_dev_cookie",
            EmployeeId = employee.Id,
            DeviceId = "dev_phone_123",
            ExpiresAt = DateTime.UtcNow.AddHours(1),
            LastSeenAt = DateTime.UtcNow.AddDays(-1)
        };
        _sessionRepo.Save(session);

        var authMock = new Mock<IAuthenticationService>();
        var services = new ServiceCollection();
        services.AddSingleton(authMock.Object);
        var context = new DefaultHttpContext { RequestServices = services.BuildServiceProvider() };

        await _service.SignInAndExtendAsync(context, session, employee);

        var setCookie = context.Response.Headers.SetCookie.ToString();
        Assert.Contains("Spokes_Device=dev_phone_123", setCookie);
        Assert.DoesNotContain("Spokes_Device=dev_phone_123; path=/; httponly", setCookie, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SignInAndExtendAsync_WithoutDeviceId_DoesNotEmitSpokesDeviceCookie()
    {
        var employee = new Employee { Id = "emp_nodev_cookie", FirstName = "Eve", IsActive = true };
        _employeeRepo.Save(employee);

        var session = new DeviceSession
        {
            Id = "sess_nodev_cookie",
            EmployeeId = employee.Id,
            DeviceId = null,
            ExpiresAt = DateTime.UtcNow.AddHours(1),
            LastSeenAt = DateTime.UtcNow.AddDays(-1)
        };
        _sessionRepo.Save(session);

        var authMock = new Mock<IAuthenticationService>();
        var services = new ServiceCollection();
        services.AddSingleton(authMock.Object);
        var context = new DefaultHttpContext { RequestServices = services.BuildServiceProvider() };

        await _service.SignInAndExtendAsync(context, session, employee);

        var setCookie = context.Response.Headers.SetCookie.ToString();
        Assert.DoesNotContain("Spokes_Device=", setCookie);
    }

    [Fact]
    public void TouchLastSeen_ThrottlesWithinOneMinute()
    {
        var session = new DeviceSession
        {
            Id = "sess_throttle",
            LastSeenAt = DateTime.UtcNow.AddSeconds(-30)
        };
        var initialTime = session.LastSeenAt;

        _service.TouchLastSeen(session);
        Assert.Equal(initialTime, session.LastSeenAt);

        // If older than 1 minute, it should update
        session.LastSeenAt = DateTime.UtcNow.AddMinutes(-2);
        _service.TouchLastSeen(session);
        Assert.True(session.LastSeenAt > initialTime);
    }

    [Fact]
    public void CreateSession_WithAndWithoutCookies_PersistsAndDeduplicates()
    {
        var employee = new Employee { Id = "emp_createsess", FirstName = "Frank", IsActive = true };
        _employeeRepo.Save(employee);

        var context1 = new DefaultHttpContext();
        // 1. Create native mobile session (issueCookie = false)
        var (session1, rawToken1) = _service.CreateSession(context1, employee.Id, "dev_cs_1", issueCookie: false);
        Assert.NotNull(session1);
        Assert.NotNull(rawToken1);
        Assert.Equal("dev_cs_1", session1.DeviceId);
        Assert.DoesNotContain("Spokes_Refresh=", context1.Response.Headers.SetCookie.ToString());

        // 2. Validate the newly created session
        var validated = _service.ValidateToken(rawToken1, "dev_cs_1");
        Assert.NotNull(validated);
        Assert.Equal(session1.Id, validated.Value.Session.Id);

        // 3. Create session with same (EmployeeId, DeviceId) -> must reuse existing session
        var context2 = new DefaultHttpContext();
        var (session2, rawToken2) = _service.CreateSession(context2, employee.Id, "dev_cs_1", issueCookie: true);
        Assert.Equal(session1.Id, session2.Id);
        Assert.Null(rawToken2); // When issueCookie is true, rawToken is null and cookie is set
        Assert.Contains("Spokes_Refresh=", context2.Response.Headers.SetCookie.ToString());
    }

    [Fact]
    public void ValidateToken_WithLegacyDevPrefix_MigratesToIosDeviceId()
    {
        var employee = new Employee { Id = "emp_mig_ios", FirstName = "Alice", IsActive = true };
        _employeeRepo.Save(employee);

        var rawToken = "mig_ios_token_" + Guid.NewGuid();
        using var sha256 = SHA256.Create();
        var tokenHash = Convert.ToBase64String(sha256.ComputeHash(Encoding.UTF8.GetBytes(rawToken)));

        var session = new DeviceSession
        {
            Id = "sess_mig_ios",
            EmployeeId = employee.Id,
            TokenHash = tokenHash,
            DeviceId = "dev_" + Guid.NewGuid(), // legacy placeholder
            ExpiresAt = DateTime.UtcNow.AddDays(30)
        };
        _sessionRepo.Save(session);

        var expectedIosId = "ios_" + Guid.NewGuid();

        // 1. Validating with modern iOS device ID must succeed and migrate the session
        var result = _service.ValidateToken(rawToken, expectedIosId);
        Assert.NotNull(result);
        Assert.Equal(session.Id, result.Value.Session.Id);
        Assert.Equal(expectedIosId, result.Value.Session.DeviceId);

        // Verify persisted record in repo is updated
        var persisted = _sessionRepo.GetById(session.Id);
        Assert.NotNull(persisted);
        Assert.Equal(expectedIosId, persisted.DeviceId);

        // 2. Subsequent validation with same iOS ID succeeds
        var repeatResult = _service.ValidateToken(rawToken, expectedIosId);
        Assert.NotNull(repeatResult);

        // 3. Subsequent validation with a DIFFERENT iOS ID must be rejected
        var wrongResult = _service.ValidateToken(rawToken, "ios_attacker_device");
        Assert.Null(wrongResult);
    }

    [Fact]
    public void ValidateToken_WithLegacyDevPrefix_MigratesToAndroidDeviceId()
    {
        var employee = new Employee { Id = "emp_mig_android", FirstName = "Bob", IsActive = true };
        _employeeRepo.Save(employee);

        var rawToken = "mig_and_token_" + Guid.NewGuid();
        using var sha256 = SHA256.Create();
        var tokenHash = Convert.ToBase64String(sha256.ComputeHash(Encoding.UTF8.GetBytes(rawToken)));

        var session = new DeviceSession
        {
            Id = "sess_mig_and",
            EmployeeId = employee.Id,
            TokenHash = tokenHash,
            DeviceId = "dev_old_guid_12345",
            ExpiresAt = DateTime.UtcNow.AddDays(30)
        };
        _sessionRepo.Save(session);

        var expectedAndroidId = "android_secure_hw_id_99";
        var result = _service.ValidateToken(rawToken, expectedAndroidId);
        Assert.NotNull(result);
        Assert.Equal(expectedAndroidId, result.Value.Session.DeviceId);

        var persisted = _sessionRepo.GetById(session.Id);
        Assert.Equal(expectedAndroidId, persisted?.DeviceId);
    }

    [Fact]
    public void ValidateToken_WithModernDeviceId_RejectsMismatch()
    {
        var employee = new Employee { Id = "emp_modern_chk", FirstName = "Charlie", IsActive = true };
        _employeeRepo.Save(employee);

        var rawToken = "modern_token_" + Guid.NewGuid();
        using var sha256 = SHA256.Create();
        var tokenHash = Convert.ToBase64String(sha256.ComputeHash(Encoding.UTF8.GetBytes(rawToken)));

        var session = new DeviceSession
        {
            Id = "sess_modern_chk",
            EmployeeId = employee.Id,
            TokenHash = tokenHash,
            DeviceId = "ios_original_device",
            ExpiresAt = DateTime.UtcNow.AddDays(30)
        };
        _sessionRepo.Save(session);

        // Mismatched modern device IDs must be strictly rejected
        var result = _service.ValidateToken(rawToken, "ios_different_device");
        Assert.Null(result);

        // Missing device ID must be strictly rejected
        var emptyResult = _service.ValidateToken(rawToken, "");
        Assert.Null(emptyResult);
    }

    [Fact]
    public void ValidateToken_WithUnboundSession_RemainsUnboundAndSucceeds()
    {
        var employee = new Employee { Id = "emp_unbound_chk", FirstName = "David", IsActive = true };
        _employeeRepo.Save(employee);

        var rawToken = "unbound_token_" + Guid.NewGuid();
        using var sha256 = SHA256.Create();
        var tokenHash = Convert.ToBase64String(sha256.ComputeHash(Encoding.UTF8.GetBytes(rawToken)));

        var session = new DeviceSession
        {
            Id = "sess_unbound_chk",
            EmployeeId = employee.Id,
            TokenHash = tokenHash,
            DeviceId = null, // unbound desktop session
            ExpiresAt = DateTime.UtcNow.AddDays(30)
        };
        _sessionRepo.Save(session);

        // Validating with a web device ID must succeed without mutating the session's DeviceId
        var webId = "web_browser_uuid_42";
        var result = _service.ValidateToken(rawToken, webId);
        Assert.NotNull(result);
        Assert.Null(result.Value.Session.DeviceId);

        var persisted = _sessionRepo.GetById(session.Id);
        Assert.Null(persisted?.DeviceId);

        // Standard browser page navigation without any device ID must also succeed
        var navResult = _service.ValidateToken(rawToken, null);
        Assert.NotNull(navResult);
        Assert.Null(navResult.Value.Session.DeviceId);
    }

    [Fact]
    public void ValidateToken_WithLegacyDevPrefix_MigratesToAnyOpaqueDeviceId()
    {
        var employee = new Employee { Id = "emp_mig_opaque", FirstName = "Eve", IsActive = true };
        _employeeRepo.Save(employee);

        var rawToken = "mig_opaque_token_" + Guid.NewGuid();
        using var sha256 = SHA256.Create();
        var tokenHash = Convert.ToBase64String(sha256.ComputeHash(Encoding.UTF8.GetBytes(rawToken)));

        var session = new DeviceSession
        {
            Id = "sess_mig_opaque",
            EmployeeId = employee.Id,
            TokenHash = tokenHash,
            DeviceId = "dev_legacy_mobile_id",
            ExpiresAt = DateTime.UtcNow.AddDays(30)
        };
        _sessionRepo.Save(session);

        // Migrating a legacy dev_ session to any opaque persistent UUID succeeds
        var targetUuid = "550e8400-e29b-41d4-a716-446655440000";
        var result = _service.ValidateToken(rawToken, targetUuid);
        Assert.NotNull(result);
        Assert.Equal(targetUuid, result.Value.Session.DeviceId);

        // Session in database must be persisted with the new ID
        var persisted = _sessionRepo.GetById(session.Id);
        Assert.NotNull(persisted);
        Assert.Equal(targetUuid, persisted.DeviceId);
    }

    [Fact]
    public void IssueRefreshToken_WithExistingDesktopSession_DoesNotAdoptDesktopSessionForMobileLogin()
    {
        var employee = new Employee { Id = "emp_multi_dev", FirstName = "Frank", IsActive = true };
        _employeeRepo.Save(employee);

        var desktopTokenHash = "desktop_token_hash_" + Guid.NewGuid();
        var desktopSession = new DeviceSession
        {
            Id = "sess_desktop_workstation",
            EmployeeId = employee.Id,
            TokenHash = desktopTokenHash,
            DeviceId = null, // Desktop sessions are unbound
            CreatedAt = DateTime.UtcNow.AddDays(-5),
            ExpiresAt = DateTime.UtcNow.AddDays(85),
            LastSeenAt = DateTime.UtcNow.AddMinutes(-10)
        };
        _sessionRepo.Save(desktopSession);

        var context = new DefaultHttpContext();
        context.Request.Headers["User-Agent"] = "Mozilla/5.0 (iPhone; CPU iPhone OS 17_0 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Mobile/15E148 Capacitor";

        // Mobile login occurs
        var mobileSession = SessionHelper.IssueRefreshToken(
            context, _db, employee.Id, out var rawToken,
            deviceId: "ios_hardware_uuid_1234",
            issueCookie: false);

        Assert.NotNull(mobileSession);
        Assert.NotEqual(desktopSession.Id, mobileSession.Id);
        Assert.Equal("ios_hardware_uuid_1234", mobileSession.DeviceId);
        Assert.NotEmpty(rawToken);

        // Verify desktop session was NOT touched, overwritten, or revoked
        var persistedDesktop = _sessionRepo.GetById(desktopSession.Id);
        Assert.NotNull(persistedDesktop);
        Assert.Null(persistedDesktop.DeviceId);
        Assert.Null(persistedDesktop.RevokedAt);
        Assert.Equal(desktopTokenHash, persistedDesktop.TokenHash);
    }

    [Fact]
    public void IssueRefreshToken_WithSameDevice_PreservesPushCredentials_AndDistinctDevice_ClearsPushCredentials()
    {
        var employee = new Employee { Id = "emp_push_retention", FirstName = "Grace", IsActive = true };
        _employeeRepo.Save(employee);

        var context = new DefaultHttpContext();
        context.Request.Headers["User-Agent"] = "Mozilla/5.0 (iPhone; CPU iPhone OS 17_0 like Mac OS X) AppleWebKit/605.1.15 Capacitor";

        // 1. First login on device
        var session1 = SessionHelper.IssueRefreshToken(
            context, _db, employee.Id, out _,
            deviceId: "ios_device_alpha",
            issueCookie: false);

        // Simulate push registration on session1
        session1.PushEndpoint = "https://push.apple.com/endpoint/alpha";
        session1.PushP256dh = "key_alpha";
        session1.PushAuth = "auth_alpha";
        session1.PushEnabled = true;
        _sessionRepo.Save(session1);

        // 2. Re-login on the SAME device must preserve push credentials
        var session2 = SessionHelper.IssueRefreshToken(
            context, _db, employee.Id, out _,
            deviceId: "ios_device_alpha",
            issueCookie: false);

        Assert.Equal(session1.Id, session2.Id);
        Assert.True(session2.PushEnabled);
        Assert.Equal("https://push.apple.com/endpoint/alpha", session2.PushEndpoint);

        // 3. Login on a DIFFERENT device creates a separate session and leaves alpha's push intact
        var session3 = SessionHelper.IssueRefreshToken(
            context, _db, employee.Id, out _,
            deviceId: "ios_device_beta",
            issueCookie: false);

        Assert.NotEqual(session1.Id, session3.Id);
        Assert.Equal("ios_device_beta", session3.DeviceId);
        Assert.False(session3.PushEnabled);
        Assert.Null(session3.PushEndpoint);

        // Original device alpha still has its push credentials
        var recheckedAlpha = _sessionRepo.GetById(session1.Id);
        Assert.NotNull(recheckedAlpha);
        Assert.True(recheckedAlpha.PushEnabled);
        Assert.Equal("https://push.apple.com/endpoint/alpha", recheckedAlpha.PushEndpoint);
    }

    [Fact]
    public void IssueRefreshToken_DesktopLoginWithNullDeviceId_DoesNotAdoptLegacyDevSession()
    {
        var employee = new Employee { Id = "emp_web_adopt_guard", FirstName = "Alice", IsActive = true };
        _employeeRepo.Save(employee);
        var legacySession = new DeviceSession
        {
            Id = "sess_legacy_mob",
            EmployeeId = employee.Id,
            DeviceId = "dev_old_phone_123",
            TokenHash = "hash123"
        };
        _sessionRepo.Save(legacySession);

        var context = new DefaultHttpContext();
        // Desktop web logins provide null deviceId
        var session = SessionHelper.IssueRefreshToken(context, _db, employee.Id, out _, deviceId: null, issueCookie: false);

        Assert.NotEqual(legacySession.Id, session.Id);
        Assert.Null(session.DeviceId);
        var recheckedLegacy = _sessionRepo.GetById("sess_legacy_mob");
        Assert.Equal("dev_old_phone_123", recheckedLegacy?.DeviceId);
    }

    [Fact]
    public void IssueRefreshToken_MigratingLegacyDevSession_PreservesPushCredentials()
    {
        var employee = new Employee { Id = "emp_mig_push", FirstName = "Pete", IsActive = true };
        _employeeRepo.Save(employee);
        var legacySession = new DeviceSession
        {
            Id = "sess_mig_push",
            EmployeeId = employee.Id,
            DeviceId = "dev_old_push_phone",
            TokenHash = "hash_mig_push",
            PushEndpoint = "https://push.services.mozilla.com/ep123",
            PushP256dh = "key_p256",
            PushAuth = "auth_secret",
            PushEnabled = true
        };
        _sessionRepo.Save(legacySession);

        var context = new DefaultHttpContext();
        var migrated = SessionHelper.IssueRefreshToken(context, _db, employee.Id, out _, deviceId: "ios_migrated_push", issueCookie: false);

        Assert.Equal(legacySession.Id, migrated.Id);
        Assert.Equal("ios_migrated_push", migrated.DeviceId);
        Assert.True(migrated.PushEnabled);
        Assert.Equal("https://push.services.mozilla.com/ep123", migrated.PushEndpoint);
    }

    [Fact]
    public void IssueRefreshToken_WithExistingMobileSession_DoesNotRevokeOrAdoptMobileSessionForDesktopLogin()
    {
        var employee = new Employee { Id = "emp_rev_multi", FirstName = "Dan", IsActive = true };
        _employeeRepo.Save(employee);
        var mobileSession = new DeviceSession
        {
            Id = "sess_active_mobile",
            EmployeeId = employee.Id,
            DeviceId = "ios_secure_hw_777",
            TokenHash = "mobile_hash_999",
            ExpiresAt = DateTime.UtcNow.AddDays(80)
        };
        _sessionRepo.Save(mobileSession);

        var context = new DefaultHttpContext();
        context.Request.Headers["User-Agent"] = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) Chrome/120.0.0.0";
        var desktopSession = SessionHelper.IssueRefreshToken(context, _db, employee.Id, out _, deviceId: null, issueCookie: true);

        Assert.NotNull(desktopSession);
        Assert.NotEqual(mobileSession.Id, desktopSession.Id);
        Assert.Null(desktopSession.DeviceId);

        var persistedMobile = _sessionRepo.GetById("sess_active_mobile");
        Assert.NotNull(persistedMobile);
        Assert.Equal("ios_secure_hw_777", persistedMobile.DeviceId);
        Assert.Null(persistedMobile.RevokedAt);
    }

    [Fact]
    public void IssueRefreshToken_ConcurrentDesktopLogins_CreateIsolatedSessions()
    {
        var employee = new Employee { Id = "emp_concurrent_desk", FirstName = "Workstation", IsActive = true };
        _employeeRepo.Save(employee);

        var contextA = new DefaultHttpContext();
        contextA.Request.Headers["User-Agent"] = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) Chrome/120.0.0.0";
        var sessionA = SessionHelper.IssueRefreshToken(contextA, _db, employee.Id, out var tokenA, deviceId: null, issueCookie: true);

        var contextB = new DefaultHttpContext();
        contextB.Request.Headers["User-Agent"] = "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) Firefox/121.0";
        var sessionB = SessionHelper.IssueRefreshToken(contextB, _db, employee.Id, out var tokenB, deviceId: null, issueCookie: true);

        Assert.NotEqual(sessionA.Id, sessionB.Id);
        Assert.Null(sessionA.DeviceId);
        Assert.Null(sessionB.DeviceId);

        // Session A must remain active and not revoked
        var persistedA = _sessionRepo.GetById(sessionA.Id);
        var persistedB = _sessionRepo.GetById(sessionB.Id);
        Assert.NotNull(persistedA);
        Assert.NotNull(persistedB);
        Assert.Null(persistedA.RevokedAt);
        Assert.Null(persistedB.RevokedAt);
    }

    [Fact]
    public void ValidateToken_CaseInsensitiveDeviceId_Succeeds()
    {
        var (session, rawToken) = SetupValidSession("ios_abcdef-1234-5678-9abc-def012345678");

        // Validate using uppercase UUID
        var result = _service.ValidateToken(rawToken, "IOS_ABCDEF-1234-5678-9ABC-DEF012345678");

        Assert.NotNull(result);
        Assert.Equal(session.Id, result.Value.Session.Id);
    }

    [Fact]
    public void ValidateToken_UnboundDesktopSession_SucceedsForAnyOrNullDeviceId()
    {
        var (session, rawToken) = SetupValidSession(deviceId: null);

        // An unbound session does not enforce device binding, so standard navigation (null)
        // or any client identifier succeeds without mutating the session's null DeviceId in DB.
        var resultNull = _service.ValidateToken(rawToken, null);
        var resultId = _service.ValidateToken(rawToken, "client_device_id_123");

        Assert.NotNull(resultNull);
        Assert.Null(resultNull.Value.Session.DeviceId);

        Assert.NotNull(resultId);
        Assert.Null(resultId.Value.Session.DeviceId);

        var persisted = _sessionRepo.GetById(session.Id);
        Assert.NotNull(persisted);
        Assert.Null(persisted.DeviceId);
    }

    [Fact]
    public void ValidateToken_MigratingLegacyDev_DeduplicatesTargetDeviceSessions()
    {
        var employee = new Employee { Id = "emp_dedup_mig", FirstName = "Dedup", IsActive = true };
        _employeeRepo.Save(employee);

        var (legacySession, legacyToken) = SetupValidSession("dev_legacy_phone_to_mig", employee.Id);

        // Pre-existing session on the target modern device
        var targetDeviceSession = new DeviceSession
        {
            Id = "sess_target_existing_" + Guid.NewGuid(),
            EmployeeId = employee.Id,
            DeviceId = "ios_target_hw_999",
            TokenHash = "target_hash_999",
            ExpiresAt = DateTime.UtcNow.AddDays(30)
        };
        _sessionRepo.Save(targetDeviceSession);

        // Validate and migrate legacySession to ios_target_hw_999
        var result = _service.ValidateToken(legacyToken, "ios_target_hw_999");

        Assert.NotNull(result);
        Assert.Equal(legacySession.Id, result.Value.Session.Id);
        Assert.Equal("ios_target_hw_999", result.Value.Session.DeviceId);

        // Target device session must be revoked as duplicate
        var persistedTarget = _sessionRepo.GetById(targetDeviceSession.Id);
        Assert.NotNull(persistedTarget);
        Assert.NotNull(persistedTarget.RevokedAt);
    }

    private (DeviceSession Session, string RawToken) SetupValidSession(string? deviceId = null, string? employeeId = null)
    {
        var empId = employeeId ?? "emp_" + Guid.NewGuid().ToString("N");
        if (_employeeRepo.GetById(empId) == null)
        {
            _employeeRepo.Save(new Employee { Id = empId, FirstName = "Test", LastName = "User", IsActive = true });
        }

        var rawToken = "raw_token_" + Guid.NewGuid();
        using var sha256 = SHA256.Create();
        var tokenHash = Convert.ToBase64String(sha256.ComputeHash(Encoding.UTF8.GetBytes(rawToken)));

        var session = new DeviceSession
        {
            Id = "sess_" + Guid.NewGuid().ToString("N"),
            EmployeeId = empId,
            TokenHash = tokenHash,
            DeviceId = deviceId,
            ExpiresAt = DateTime.UtcNow.AddDays(30),
            LastSeenAt = DateTime.UtcNow
        };
        _sessionRepo.Save(session);

        return (session, rawToken);
    }
}

