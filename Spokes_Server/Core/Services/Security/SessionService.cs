using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Localization;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Spokes_Server.Aggregate.Contexts;
using Spokes_Server.Core.Constants;
using Spokes_Server.Core.Data.Repositories.Core;
using Spokes_Server.Core.Data.Repositories.HR;
using Spokes_Server.Core.Helpers;
using Spokes_Server.Core.Models.Core;
using Spokes_Server.Core.Models.HR;
using Spokes_Server.Core.Security;

namespace Spokes_Server.Core.Services.Security;

/// <summary>
/// Centralizes all session lifecycle operations: identity construction, session creation,
/// token validation, cookie sign-in, and IdP logout lifecycle orchestration.
/// </summary>
public class SessionService
{
    private static readonly ConcurrentDictionary<string, object> _employeeSessionLocks = new(StringComparer.OrdinalIgnoreCase);

    private readonly DeviceSessionRepository _deviceSessions;
    private readonly EmployeeRepository _employees;
    private readonly SystemConfigRepository? _systemConfigs;
    private readonly IOptionsMonitor<OpenIdConnectOptions>? _oidcOptionsMonitor;
    private readonly IMemoryCache? _cache;
    private readonly IOpenIdConfigurationProvider? _openIdConfigurationProvider;
    private readonly ILogger<SessionService>? _logger;
    private readonly GlobalKeystoreService? _globalKeystore;
    private readonly FileTokenService? _fileTokenService;

    public event Action<string>? OnSessionRevoked;
    public event Action<string>? OnSessionBanned;
    public event Action<string>? OnSessionVaultRevoked;
    public event Action<string, bool>? OnUserSuspensionChanged;

    [ActivatorUtilitiesConstructor]
    public SessionService(
        CoreContext coreContext,
        HrContext hrContext,
        IOptionsMonitor<OpenIdConnectOptions>? oidcOptionsMonitor = null,
        IMemoryCache? cache = null,
        ILogger<SessionService>? logger = null,
        IConfigurationManager<OpenIdConnectConfiguration>? injectedConfigManager = null,
        IOpenIdConfigurationProvider? openIdConfigurationProvider = null,
        GlobalKeystoreService? globalKeystore = null,
        FileTokenService? fileTokenService = null)
        : this(coreContext?.DeviceSessions!, hrContext?.Employees!, coreContext?.SystemConfigs, oidcOptionsMonitor, cache, logger, injectedConfigManager, openIdConfigurationProvider, globalKeystore, fileTokenService)
    {
    }

    public SessionService(
        CoreContext coreContext,
        HrContext hrContext)
        : this(coreContext, hrContext, null, null, null, null, null, null, null)
    {
    }

    internal SessionService(
        DeviceSessionRepository deviceSessions,
        EmployeeRepository employees,
        SystemConfigRepository? systemConfigs = null,
        IOptionsMonitor<OpenIdConnectOptions>? oidcOptionsMonitor = null,
        IMemoryCache? cache = null,
        ILogger<SessionService>? logger = null,
        IConfigurationManager<OpenIdConnectConfiguration>? injectedConfigManager = null,
        IOpenIdConfigurationProvider? openIdConfigurationProvider = null,
        GlobalKeystoreService? globalKeystore = null,
        FileTokenService? fileTokenService = null)
    {
        _deviceSessions = deviceSessions ?? throw new ArgumentNullException(nameof(deviceSessions));
        _employees = employees ?? throw new ArgumentNullException(nameof(employees));
        _systemConfigs = systemConfigs;
        _oidcOptionsMonitor = oidcOptionsMonitor;
        _cache = cache;
        _logger = logger;
        _globalKeystore = globalKeystore;
        _fileTokenService = fileTokenService;
        _openIdConfigurationProvider = openIdConfigurationProvider
            ?? (injectedConfigManager != null || oidcOptionsMonitor != null
                ? new OpenIdConfigurationProvider(null, oidcOptionsMonitor, injectedConfigManager)
                : OpenIdConfigurationProvider.Shared);
    }

    internal Employee? GetEmployeeById(string? employeeId)
        => string.IsNullOrEmpty(employeeId) ? null : _employees.GetById(employeeId);

    /// <summary>
    /// Builds a consistent ClaimsIdentity from an Employee + SessionId.
    /// Single source of truth for claims — used by all auth paths.
    /// </summary>
    public ClaimsIdentity BuildIdentity(Employee employee, string sessionId)
    {
        var identity = new ClaimsIdentity(CookieAuthenticationDefaults.AuthenticationScheme);
        identity.AddClaim(new Claim("sub", employee.Id));
        identity.AddClaim(new Claim("name", employee.FullName));
        identity.AddClaim(new Claim("email", employee.Email ?? ""));
        identity.AddClaim(new Claim("EmployeeId", employee.Id));
        identity.AddClaim(new Claim("SessionId", sessionId));

        if (!employee.IsBanned)
        {
            if (employee.IsAdmin)
            {
                identity.AddClaim(new Claim(ClaimTypes.Role, AppPermissions.Admin.RoleName));
            }

            foreach (var perm in employee.GetEffectivePermissionSet(allowSuspended: true))
            {
                identity.AddClaim(new Claim("Permission", perm));
            }
        }
        else
        {
            identity.AddClaim(new Claim("Banned", "true"));
        }

        return identity;
    }

    /// <summary>
    /// Creates or reuses a DeviceSession for an employee+device.
    /// Returns the session and the raw refresh token (non-null only when issueCookie is false).
    /// Manages device matching, session deduplication, device adoption, and cookie issuance.
    /// </summary>
    public virtual (DeviceSession Session, string? RawToken) CreateSession(
        HttpContext context,
        string employeeId,
        string? deviceId = null,
        bool issueCookie = true,
        string? idToken = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(employeeId);
        string? rawToken = null;
        var tokenBytes = RandomNumberGenerator.GetBytes(64);
        var refreshToken = Convert.ToBase64String(tokenBytes);
        if (!issueCookie) rawToken = refreshToken;

        var tokenHash = Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(refreshToken)));

        var (deviceInfo, isCapacitor) = DeviceDetectionHelper.ParseDeviceInfo(context);
        var isMobileDevice = isCapacitor || DeviceDetectionHelper.IsMobileDevice(context.Request.Headers.UserAgent.ToString());

        if (string.IsNullOrWhiteSpace(deviceId))
        {
            deviceId = null;
        }
        else
        {
            deviceId = deviceId.Trim();
        }

        DeviceSession deviceSession;
        var empLock = _employeeSessionLocks.GetOrAdd(employeeId, _ => new object());
        lock (empLock)
        {
            // Look for active sessions matching this employee.
            // Prefer an exact device match; if none exists, adopt any legacy dev_ or unbound session.
            var employeeSessions = _deviceSessions.GetAll()
                .Where(s => s.EmployeeId == employeeId && s.RevokedAt == null)
                .ToList();

            // Only match existing sessions if a device ID is present.
            // Desktop web sessions (deviceId == null) always create isolated sessions to allow multi-workstation concurrency.
            var matchingSessions = !string.IsNullOrEmpty(deviceId)
                ? employeeSessions.Where(s => !string.IsNullOrEmpty(s.DeviceId) && string.Equals(s.DeviceId, deviceId, StringComparison.OrdinalIgnoreCase)).ToList()
                : [];

            if (matchingSessions.Count == 0 && !string.IsNullOrEmpty(deviceId))
            {
                // If no exact match, check for a legacy dev_ session to adopt.
                // NEVER adopt unbound desktop sessions (where DeviceId is null or empty).
                var expectedDeviceType = isMobileDevice ? DeviceType.Mobile : DeviceType.Desktop;
                var legacySession = employeeSessions.FirstOrDefault(s =>
                    !string.IsNullOrEmpty(s.DeviceId)
                    && s.DeviceId.StartsWith("dev_", StringComparison.OrdinalIgnoreCase)
                    && s.IsCapacitor == isCapacitor
                    && string.Equals(s.DeviceType, expectedDeviceType, StringComparison.OrdinalIgnoreCase));
                if (legacySession != null)
                {
                    matchingSessions.Add(legacySession);
                }
            }

            if (matchingSessions.Count > 0)
            {
                deviceSession = matchingSessions[0];
                var effectiveIsCapacitor = isCapacitor || deviceSession.IsCapacitorApp;
                var effectiveIsMobile = isMobileDevice || effectiveIsCapacitor || string.Equals(deviceSession.DeviceType, DeviceType.Mobile, StringComparison.OrdinalIgnoreCase);

                deviceSession.TokenHash = tokenHash;
                deviceSession.DeviceInfo = effectiveIsCapacitor && !deviceInfo.Contains("(App)", StringComparison.OrdinalIgnoreCase)
                    ? $"{deviceInfo} (App)"
                    : deviceInfo;
                deviceSession.DeviceId = deviceId; // Ensure adopted session receives the modernized device ID
                if (effectiveIsMobile)
                {
                    deviceSession.DeviceType = DeviceType.Mobile;
                }
                if (effectiveIsCapacitor)
                {
                    deviceSession.IsCapacitor = true;
                }
                deviceSession.ExpiresAt = DateTime.UtcNow.AddDays(90);
                deviceSession.LastSeenAt = DateTime.UtcNow;
                if (!string.IsNullOrEmpty(idToken)) deviceSession.IdToken = idToken;

                // Preserve existing push credentials across same-device re-logins and legacy dev_ migrations
                _deviceSessions.Save(deviceSession);

                // Revoke any duplicates that were created previously
                foreach (var duplicate in matchingSessions.Skip(1))
                {
                    duplicate.RevokedAt = DateTime.UtcNow;
                    _deviceSessions.Save(duplicate);
                }
            }
            else
            {
                deviceSession = new DeviceSession
                {
                    EmployeeId = employeeId,
                    TokenHash = tokenHash,
                    DeviceInfo = deviceInfo,
                    DeviceId = deviceId,
                    IsCapacitor = isCapacitor,
                    DeviceType = isMobileDevice ? DeviceType.Mobile : DeviceType.Desktop,
                    CreatedAt = DateTime.UtcNow,
                    ExpiresAt = DateTime.UtcNow.AddDays(90),
                    LastSeenAt = DateTime.UtcNow
                };
                if (!string.IsNullOrEmpty(idToken)) deviceSession.IdToken = idToken;
                _deviceSessions.Save(deviceSession);
            }
        }

        if (issueCookie)
        {
            var refreshCookieOptions = new CookieOptions
            {
                HttpOnly = true, // Desktop JS never reads this cookie; mobile uses Preferences
                Secure = context.Request.IsHttps,
                SameSite = SameSiteMode.Lax,
                Path = "/",
                Expires = DateTimeOffset.UtcNow.AddDays(90)
            };
            context.Response.Cookies.Append("Spokes_Refresh", refreshToken, refreshCookieOptions);
        }

        return (deviceSession, rawToken);
    }

    /// <summary>
    /// Returns true if the referenced <see cref="DeviceSession"/> exists, is neither revoked nor expired,
    /// and (when <paramref name="employeeId"/> is provided) belongs to that employee.
    /// </summary>
    public bool IsSessionActive(string? sessionId, string? employeeId = null)
    {
        if (string.IsNullOrWhiteSpace(sessionId)) return false;

        var session = _deviceSessions.GetById(sessionId);
        if (session == null || session.RevokedAt != null || session.ExpiresAt <= DateTime.UtcNow)
        {
            return false;
        }

        if (!string.IsNullOrEmpty(employeeId) &&
            !string.Equals(session.EmployeeId, employeeId, StringComparison.Ordinal))
        {
            return false;
        }

        return true;
    }

    /// <summary>
    /// Validates whether a claims principal's bound session is still active. When a <c>SessionId</c>
    /// claim is present, it must resolve to an active, unrevoked, unexpired <see cref="DeviceSession"/>.
    /// Production authentication schemes (<c>Cookies</c>, <c>DeviceToken</c>) fail closed if
    /// <c>SessionId</c> is missing.
    /// </summary>
    public bool IsPrincipalSessionValid(ClaimsPrincipal? user, string employeeId) =>
        IsPrincipalSessionValid(
            user?.FindFirst("SessionId")?.Value,
            user?.Identity?.AuthenticationType,
            employeeId);

    /// <summary>
    /// Validates whether the captured session ID and authentication scheme represent an active session.
    /// </summary>
    public bool IsPrincipalSessionValid(string? sessionId, string? authenticationType, string employeeId)
    {
        if (!string.IsNullOrEmpty(sessionId))
        {
            return IsSessionActive(sessionId, employeeId);
        }

        return !string.Equals(authenticationType, Microsoft.AspNetCore.Authentication.Cookies.CookieAuthenticationDefaults.AuthenticationScheme, StringComparison.OrdinalIgnoreCase) &&
               !string.Equals(authenticationType, "DeviceToken", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Resolves the currently active DeviceSession from the HttpContext, checking
    /// authenticated session claims first, then falling back to the refresh cookie.
    /// </summary>
    public DeviceSession? GetActiveSession(HttpContext context, bool allowRevoked = false)
    {
        // 1. First, rely on the active authentication context (Spokes_Session_v3 cookie)
        // This is sent by both Capacitor Mobile apps and Standard Desktop browsers
        if (context.User.Identity?.IsAuthenticated == true)
        {
            var sessionId = context.User.FindFirst("SessionId")?.Value;
            if (!string.IsNullOrEmpty(sessionId))
            {
                var session = _deviceSessions.GetById(sessionId);
                if (session != null && (allowRevoked || (session.RevokedAt == null && session.ExpiresAt > DateTime.UtcNow)))
                    return session;
            }
        }

        // 2. Fallback to the long-lived Spokes_Refresh cookie for desktop web edge-cases
        if (context.Request.Cookies.TryGetValue("Spokes_Refresh", out var token) && !string.IsNullOrEmpty(token))
        {
            var deviceId = context.Request.Headers["X-Device-Id"].FirstOrDefault() ?? context.Request.Cookies["Spokes_Device"];
            var result = ValidateToken(token, deviceId, allowRevoked: allowRevoked);
            return result?.Session;
        }

        return null;
    }

    /// <summary>
    /// Validates a raw token (refresh token or device token) against the database.
    /// Single source of truth for all token validation — used by DeviceTokenAuthHandler,
    /// /spokesapi/auth/refresh, SessionService.GetActiveSession, and force-logout.
    /// </summary>
    public (DeviceSession Session, Employee Employee)? ValidateToken(
        string rawToken, string? expectedDeviceId = null, bool allowRevoked = false)
    {
        if (string.IsNullOrEmpty(rawToken)) return null;

        expectedDeviceId = string.IsNullOrWhiteSpace(expectedDeviceId) ? null : expectedDeviceId.Trim();

        var tokenHash = Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(rawToken)));

        var session = _deviceSessions.GetByTokenHash(tokenHash);
        if (session == null || session.ExpiresAt <= DateTime.UtcNow)
            return null;

        if (!allowRevoked && session.RevokedAt != null)
            return null;

        // Validate deviceId matches to prevent cross-device token use from iCloud cookie sync.
        // If the session was issued to a device (has non-empty DeviceId), expectedDeviceId is strictly required.
        if (!string.IsNullOrEmpty(session.DeviceId))
        {
            if (string.IsNullOrEmpty(expectedDeviceId) || !string.Equals(session.DeviceId, expectedDeviceId, StringComparison.OrdinalIgnoreCase))
            {
                // Allow legacy sessions with "dev_" placeholder prefix to validate when a persistent device ID is provided.
                // Adoption and persistence of the new device ID is handled explicitly via AdoptLegacySessionDeviceId
                // during authentication lifecycle steps, keeping ValidateToken purely functional without database mutation side-effects.
                if (!string.IsNullOrEmpty(expectedDeviceId) &&
                    session.DeviceId.StartsWith("dev_", StringComparison.OrdinalIgnoreCase))
                {
                    // Valid legacy session candidate
                }
                else
                {
                    _logger?.LogWarning("DeviceId mismatch or missing: request={ExpectedDeviceId}, session={SessionDeviceId}. Rejecting", expectedDeviceId, session.DeviceId);
                    return null;
                }
            }
        }

        var employee = _employees.GetById(session.EmployeeId);
        if (employee == null) return null;
        if (!allowRevoked && (!employee.IsActive || employee.IsBanned)) return null;

        return (session, employee);
    }

    /// <summary>
    /// Explicitly migrates a legacy session (with a placeholder "dev_" DeviceId) to adopt a persistent device ID.
    /// Deduplicates active sessions for the same employee and device, persisting changes to database.
    /// Should be called during authentication lifecycle steps (e.g. login, token renewal, refresh) rather than read validation.
    /// </summary>
    public bool AdoptLegacySessionDeviceId(DeviceSession session, string? expectedDeviceId)
    {
        if (session == null || string.IsNullOrWhiteSpace(session.EmployeeId) || string.IsNullOrWhiteSpace(expectedDeviceId)) return false;
        expectedDeviceId = expectedDeviceId.Trim();

        var empLock = _employeeSessionLocks.GetOrAdd(session.EmployeeId, _ => new object());
        lock (empLock)
        {
            if (!string.IsNullOrEmpty(session.DeviceId) &&
                session.DeviceId.StartsWith("dev_", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(session.DeviceId, expectedDeviceId, StringComparison.OrdinalIgnoreCase))
            {
                _logger?.LogInformation("Migrating legacy session {SessionId} device ID from '{OldDeviceId}' to '{NewDeviceId}'", session.Id, session.DeviceId, expectedDeviceId);
                session.DeviceId = expectedDeviceId;
                session.LastSeenAt = DateTime.UtcNow;

                // Deduplicate: revoke any other active duplicate sessions for this employee matching expectedDeviceId
                var duplicateSessions = _deviceSessions.GetByEmployeeId(session.EmployeeId)
                    .Where(s => s.Id != session.Id && s.RevokedAt == null && string.Equals(s.DeviceId, expectedDeviceId, StringComparison.OrdinalIgnoreCase))
                    .ToList();
                foreach (var dup in duplicateSessions)
                {
                    dup.RevokedAt = DateTime.UtcNow;
                    _deviceSessions.Save(dup);
                }

                _deviceSessions.Save(session);
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Atomically assigns and persists a fresh <see cref="DeviceSession.VaultNonce"/> for an un-migrated
    /// legacy (server-v2026.11.0) session during its one-time v1 -> v2 vault token upgrade.
    /// Returns null if the session was revoked, expired, logged out of the vault, or already upgraded concurrently.
    /// </summary>
    public string? TryAssignLegacyVaultNonce(DeviceSession session) =>
        TryAssignLegacyVaultNonce(_deviceSessions, session);

    internal static string? TryAssignLegacyVaultNonce(DeviceSessionRepository deviceSessions, DeviceSession session)
    {
        if (deviceSessions == null || session == null || string.IsNullOrWhiteSpace(session.Id) || string.IsNullOrWhiteSpace(session.EmployeeId))
        {
            return null;
        }

        var empLock = _employeeSessionLocks.GetOrAdd(session.EmployeeId, _ => new object());
        lock (empLock)
        {
            var live = deviceSessions.GetById(session.Id);
            if (live == null ||
                live.RevokedAt != null ||
                live.ExpiresAt <= DateTime.UtcNow ||
                !live.HasVaultCookie ||
                !string.IsNullOrEmpty(live.VaultNonce))
            {
                return null;
            }

            live.VaultNonce = Guid.NewGuid().ToString("N");
            deviceSessions.Save(live);
            session.VaultNonce = live.VaultNonce;
            return live.VaultNonce;
        }
    }

    /// <summary>
    /// Evicts the user's decrypted private key from <see cref="GlobalKeystoreService"/>
    /// when no remaining active, unrevoked, unexpired <see cref="DeviceSession"/> has <c>HasVaultCookie == true</c>.
    /// </summary>
    public void EvictGlobalKeystoreIfNoUnlockedSessions(string? employeeId, GlobalKeystoreService? fallbackKeystore = null)
    {
        if (string.IsNullOrWhiteSpace(employeeId)) return;

        var keystore = _globalKeystore ?? fallbackKeystore;
        if (keystore == null) return;

        var now = DateTime.UtcNow;
        bool hasRemainingUnlockedSession = _deviceSessions
            .GetByEmployeeId(employeeId)
            .Any(s => s.RevokedAt == null && s.ExpiresAt > now && s.HasVaultCookie);

        if (!hasRemainingUnlockedSession)
        {
            keystore.LockVault(employeeId);
        }
    }

    /// <summary>
    /// Revokes all active sessions for the specified employee, clears push credentials,
    /// unconditionally evicts cached vault keys and file tokens, and triggers remote logout across all active circuits.
    /// </summary>
    public void RevokeAllUserSessions(string employeeId)
    {
        if (string.IsNullOrWhiteSpace(employeeId)) return;

        var activeSessions = _deviceSessions.GetByEmployeeId(employeeId)
            .Where(s => s.RevokedAt == null)
            .ToList();

        var now = DateTime.UtcNow;
        foreach (var session in activeSessions)
        {
            session.RevokedAt = now;
            session.HasVaultCookie = false;
            session.VaultNonce = Guid.NewGuid().ToString("N");
            session.PushExplicitlyDisabled = false;
            if (session.HasPush)
            {
                _deviceSessions.ClearPushFields(session);
            }
            else
            {
                _deviceSessions.Save(session);
            }
            TriggerRemoteLogout(session.Id);
        }

        _globalKeystore?.LockVault(employeeId);
        _fileTokenService?.InvalidateUserEncryptedTokens(employeeId);
    }

    /// <summary>
    /// Quarantines all active sessions for a newly banned employee: clears refresh token hashes,
    /// vault unlock state, push credentials, cached keystore keys, and encrypted file tokens while
    /// preserving unrevoked DeviceSession state and IdToken strictly for Static SSR /banned containment
    /// and clean RP-initiated IdP sign-out.
    /// </summary>
    public void QuarantineUserSessionsForBan(string employeeId)
    {
        if (string.IsNullOrWhiteSpace(employeeId)) return;

        var activeSessions = _deviceSessions.GetByEmployeeId(employeeId)
            .Where(s => s.RevokedAt == null)
            .ToList();

        foreach (var session in activeSessions)
        {
            session.HasVaultCookie = false;
            session.VaultNonce = Guid.NewGuid().ToString("N");
            session.TokenHash = string.Empty;
            session.PushExplicitlyDisabled = false;
            if (session.HasPush)
            {
                _deviceSessions.ClearPushFields(session);
            }
            else
            {
                _deviceSessions.Save(session);
            }
            TriggerRemoteBanRedirect(session.Id);
        }

        _globalKeystore?.LockVault(employeeId);
        _fileTokenService?.InvalidateUserEncryptedTokens(employeeId);
    }

    /// <summary>
    /// Updates LastSeenAt on a session, throttled to once per minute to reduce DB writes.
    /// </summary>
    public void TouchLastSeen(DeviceSession session)
    {
        if (session.LastSeenAt < DateTime.UtcNow.AddMinutes(-1))
        {
            session.LastSeenAt = DateTime.UtcNow;
            _deviceSessions.Save(session);
        }
    }

    /// <summary>
    /// Updates LastSeenAt on the underlying DeviceSession, throttled to at most once per minute
    /// to avoid excessive disk persistence operations.
    /// </summary>
    public void TouchSession(string? sessionId)
    {
        if (string.IsNullOrEmpty(sessionId)) return;

        var session = _deviceSessions.GetById(sessionId);
        if (session != null && session.LastSeenAt < DateTime.UtcNow.AddMinutes(-1))
        {
            session.LastSeenAt = DateTime.UtcNow;
            _deviceSessions.Save(session);
        }
    }

    /// <summary>
    /// Flushes the current LastSeenAt timestamps for all active connected sessions to the database.
    /// Called during graceful server shutdown.
    /// </summary>
    public void FlushActiveSessions(IEnumerable<string> sessionIds)
    {
        var now = DateTime.UtcNow;
        foreach (var sessionId in sessionIds.Where(s => !string.IsNullOrEmpty(s)).Distinct())
        {
            var session = _deviceSessions.GetById(sessionId);
            if (session != null)
            {
                session.LastSeenAt = now;
                _deviceSessions.Save(session);
            }
        }
    }

    /// <summary>
    /// Triggers a global broadcast to log out all active connections for the specified SessionId.
    /// </summary>
    public void TriggerRemoteLogout(string sessionId)
    {
        if (!string.IsNullOrEmpty(sessionId))
        {
            OnSessionRevoked?.Invoke(sessionId);
        }
    }

    /// <summary>
    /// Triggers a global broadcast to redirect all active circuits for the specified SessionId to /banned.
    /// </summary>
    public void TriggerRemoteBanRedirect(string sessionId)
    {
        if (!string.IsNullOrEmpty(sessionId))
        {
            OnSessionBanned?.Invoke(sessionId);
        }
    }

    /// <summary>
    /// Triggers a global broadcast to lock the vault and purge persisted encrypted media cache
    /// on all active circuits for the specified SessionId (e.g., after a vault password change).
    /// </summary>
    public void TriggerRemoteVaultLock(string sessionId)
    {
        if (!string.IsNullOrEmpty(sessionId))
        {
            OnSessionVaultRevoked?.Invoke(sessionId);
        }
    }

    /// <summary>
    /// Triggers a global broadcast to notify active circuits when an employee's read-only suspension state changes.
    /// </summary>
    public void NotifyUserSuspensionChanged(string employeeId, bool isSuspended)
    {
        if (string.IsNullOrWhiteSpace(employeeId))
        {
            return;
        }

        if (isSuspended)
        {
            foreach (var session in _deviceSessions.GetByEmployeeId(employeeId))
            {
                if (session.RevokedAt != null)
                {
                    continue;
                }

                session.HasVaultCookie = false;
                session.VaultNonce = Guid.NewGuid().ToString("N");
                _deviceSessions.Save(session);
            }

            _globalKeystore?.LockVault(employeeId);
            _fileTokenService?.InvalidateUserEncryptedTokens(employeeId);
        }

        OnUserSuspensionChanged?.Invoke(employeeId, isSuspended);
    }

    /// <summary>
    /// Signs in the user with cookie auth and extends the session expiry by 90 days.
    /// Used by all session refresh/renewal paths.
    /// </summary>
    public async Task SignInAndExtendAsync(HttpContext context, DeviceSession session, Employee employee, string? deviceId = null)
    {
        if (!string.IsNullOrEmpty(deviceId))
        {
            AdoptLegacySessionDeviceId(session, deviceId);
        }

        var identity = BuildIdentity(employee, session.Id);
        
        session.ExpiresAt = DateTime.UtcNow.AddDays(90);
        session.LastSeenAt = DateTime.UtcNow;
        _deviceSessions.Save(session);

        await context.SignInAsync(
            CookieAuthenticationDefaults.AuthenticationScheme,
            new ClaimsPrincipal(identity),
            new AuthenticationProperties
            {
                IsPersistent = true,
                ExpiresUtc = DateTimeOffset.UtcNow.AddDays(90)
            });

        if (!string.IsNullOrEmpty(session.DeviceId))
        {
            context.Response.Cookies.Append("Spokes_Device", session.DeviceId, new CookieOptions
            {
                HttpOnly = false, // Accessible to native plugins via WebView cookie sync
                Secure = context.Request.IsHttps,
                SameSite = SameSiteMode.Lax,
                Path = "/",
                Expires = DateTimeOffset.UtcNow.AddDays(90)
            });
        }

        if (!string.IsNullOrWhiteSpace(employee.PreferredLanguage))
        {
            context.Response.Cookies.Append(
                CookieRequestCultureProvider.DefaultCookieName,
                CookieRequestCultureProvider.MakeCookieValue(new RequestCulture(culture: "en-US", uiCulture: employee.PreferredLanguage)),
                new CookieOptions
                {
                    HttpOnly = false,
                    Secure = context.Request.IsHttps,
                    SameSite = SameSiteMode.Lax,
                    Path = "/",
                    Expires = DateTimeOffset.UtcNow.AddDays(90),
                    IsEssential = true
                });
        }
    }

    /// <summary>
    /// Resolves the OIDC EndSessionEndpoint from the configured authority or fallback.
    /// Delegated to <see cref="IOpenIdConfigurationProvider"/>.
    /// </summary>
    public Task<string> ResolveEndSessionEndpointAsync(CancellationToken cancellationToken = default)
    {
        var oidcOptions = _oidcOptionsMonitor?.Get(OpenIdConnectDefaults.AuthenticationScheme);

        var authority = oidcOptions?.Authority 
                        ?? _systemConfigs?.Get()?.Authority 
                        ?? "";

        var provider = _openIdConfigurationProvider ?? OpenIdConfigurationProvider.Shared;
        return provider.ResolveEndSessionEndpointAsync(authority, cancellationToken);
    }

    /// <summary>
    /// Consolidates mobile and standard IdP logout handling: revokes the session (if requested),
    /// deletes session and security cookies, discovers the IdP end-session endpoint, and
    /// returns the appropriate handoff result (Mobile HTML with native storage cleanup or standard SignOut/Redirect).
    /// </summary>
    public async Task<LogoutHandoffResult> InitiateLogoutAsync(
        HttpContext context,
        DeviceSession? session = null,
        bool revokeActiveSession = false,
        bool? isMobileExplicit = null,
        string redirectUri = "/")
    {
        // 1. Resolve session if not provided
        session ??= GetActiveSession(context, allowRevoked: true);

        // 2. Revoke session if requested (e.g. user-initiated logout via POST /logout)
        if (revokeActiveSession && session != null)
        {
            session.RevokedAt ??= DateTime.UtcNow;
            session.HasVaultCookie = false;
            session.VaultNonce = Guid.NewGuid().ToString("N");
            session.PushExplicitlyDisabled = false;
            if (session.HasPush)
            {
                _deviceSessions.ClearPushFields(session);
            }
            else
            {
                _deviceSessions.Save(session);
            }

            if (!string.IsNullOrEmpty(session.EmployeeId))
            {
                (_fileTokenService ?? context.RequestServices?.GetService<FileTokenService>())?.InvalidateUserEncryptedTokens(session.EmployeeId);
                EvictGlobalKeystoreIfNoUnlockedSessions(
                    session.EmployeeId,
                    _globalKeystore == null ? context.RequestServices?.GetService<GlobalKeystoreService>() : null);
            }
        }
        else if (session != null && !string.IsNullOrEmpty(session.EmployeeId))
        {
            EvictGlobalKeystoreIfNoUnlockedSessions(session.EmployeeId);
        }

        // 3. Clear session and security cookies
        context.Response.Cookies.Delete("Spokes_SSO_Attempt");

        if (context.Request.Cookies.ContainsKey(VaultCookieProtector.RefreshCookieName))
        {
            VaultCookieProtector.DeleteRefreshCookie(context);
        }

        VaultCookieProtector.DeleteVaultCookie(context.Response);
        if (!context.Response.HasStarted)
        {
            context.Response.Headers["Clear-Site-Data"] = "\"cache\"";
        }

        // 4. Resolve id_token for IdP logout
        var hasAuthService = context.RequestServices?.GetService<IAuthenticationService>() != null;
        string? idToken = null;
        if (hasAuthService)
        {
            var authResult = await context.AuthenticateAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            idToken = await context.GetTokenAsync(CookieAuthenticationDefaults.AuthenticationScheme, "id_token");
            if (string.IsNullOrEmpty(idToken))
            {
                idToken = authResult?.Properties?.GetTokenValue("id_token") 
                          ?? authResult?.Properties?.GetParameter<string>("spokes_id_token");
            }
        }
        if (string.IsNullOrEmpty(idToken) && session != null && !string.IsNullOrEmpty(session.IdToken))
        {
            idToken = session.IdToken;
        }

        // 5. Determine whether client is mobile
        var isMobile = isMobileExplicit ?? DeviceDetectionHelper.IsMobileClient(context);

        // 6. Handle missing id_token case
        if (string.IsNullOrEmpty(idToken))
        {
            _logger?.LogInformation("[Logout] Skipping IdP logout because id_token is missing. Redirecting locally.");
            if (hasAuthService)
            {
                await context.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            }

            if (isMobile)
            {
                var serverBaseUrl = ServerUrlResolver.GetSafeServerBaseUrl(context, _systemConfigs?.Get()?.ServerPublicUrl);
                return LogoutHandoffResult.MobileLocal(serverBaseUrl);
            }

            return LogoutHandoffResult.LocalRedirect("/sso/login");
        }

        // 7. Handle mobile client with id_token
        if (isMobile)
        {
            _logger?.LogInformation("[Logout] Mobile client logout detected. Performing IdP logout via system browser.");
            if (hasAuthService)
            {
                await context.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            }

            var endSessionUrl = await ResolveEndSessionEndpointAsync();

            var reqId = Guid.NewGuid().ToString("N");
            _cache?.Set($"logout_{reqId}", new Tuple<string, string>(idToken, endSessionUrl), TimeSpan.FromMinutes(5));

            var serverBaseUrl = ServerUrlResolver.GetSafeServerBaseUrl(context, _systemConfigs?.Get()?.ServerPublicUrl);
            return new LogoutHandoffResult
            {
                HandoffType = LogoutHandoffType.MobileIdpLogout,
                LogoutRequestId = reqId,
                ServerBaseUrl = serverBaseUrl
            };
        }

        // 8. Standard desktop client logout
        _logger?.LogInformation("[Logout] Standard client logout detected. Performing global OIDC sign-out.");
        if (redirectUri == "/sso/login")
        {
            if (hasAuthService)
            {
                await context.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
                try
                {
                    await context.SignOutAsync(OpenIdConnectDefaults.AuthenticationScheme, new AuthenticationProperties { RedirectUri = redirectUri });
                    return LogoutHandoffResult.LocalRedirect(redirectUri, handledViaSignOutAsync: true);
                }
                catch (Exception ex)
                {
                    _logger?.LogDebug(ex, "[Logout] OIDC signout fallback");
                    return LogoutHandoffResult.LocalRedirect(redirectUri, handledViaSignOutAsync: false);
                }
            }
            return LogoutHandoffResult.LocalRedirect(redirectUri, handledViaSignOutAsync: false);
        }

        return LogoutHandoffResult.SignOut(redirectUri);
    }
}
