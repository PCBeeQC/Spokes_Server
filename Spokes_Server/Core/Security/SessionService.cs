namespace Spokes_Server.Core.Security;

using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http;
using Spokes_Server.Core.Constants;
using Spokes_Server.Core.Models.Core;
using Spokes_Server.Core.Models.HR;
using Spokes_Server.Aggregate;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;

/// <summary>
/// Centralizes all session lifecycle operations: identity construction, session creation,
/// token validation, and cookie sign-in. Replaces the 5 duplicated claims-building blocks
/// scattered across Program.cs endpoints.
/// </summary>
public class SessionService
{
    private readonly Database _db;

    public SessionService(Database db) => _db = db;

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

        if (employee.IsAdmin)
        {
            identity.AddClaim(new Claim(ClaimTypes.Role, AppPermissions.Admin.RoleName));
        }

        var effectivePermissions = employee.PermissionGroup != null ? employee.PermissionGroup.Permissions : employee.Permissions;
        if (effectivePermissions != null)
        {
            foreach (var perm in effectivePermissions)
            {
                identity.AddClaim(new Claim("Permission", perm));
            }
        }

        return identity;
    }

    /// <summary>
    /// Creates or reuses a DeviceSession for an employee+device. Wraps IssueRefreshToken.
    /// Returns the session and the raw refresh token (non-null only when issueCookie is false).
    /// </summary>
    public (DeviceSession Session, string? RawToken) CreateSession(
        HttpContext context, string employeeId, string? deviceId,
        bool issueCookie, string? idToken = null)
    {
        var session = SessionHelper.IssueRefreshToken(
            context, _db, employeeId, out var rawToken,
            deviceId: deviceId, issueCookie: issueCookie, idToken: idToken);

        return (session, rawToken);
    }

    /// <summary>
    /// Validates a raw token (refresh token or device token) against the database.
    /// Single source of truth for all token validation — used by DeviceTokenAuthHandler,
    /// /spokesapi/auth/refresh, SessionHelper.GetActiveSession, and force-logout.
    /// </summary>
    /// <param name="rawToken">The plaintext token to validate.</param>
    /// <param name="expectedDeviceId">Optional device ID to check. If the session has a bound DeviceId (mobile),
    /// this must be provided and must match (prevents cross-device token reuse from iCloud cookie sync).</param>
    /// <param name="allowRevoked">Whether to return revoked sessions (used exclusively by force-logout).</param>
    /// <returns>The active session and employee, or null if validation fails.</returns>
    public (DeviceSession Session, Employee Employee)? ValidateToken(
        string rawToken, string? expectedDeviceId = null, bool allowRevoked = false)
    {
        if (string.IsNullOrEmpty(rawToken)) return null;

        expectedDeviceId = string.IsNullOrWhiteSpace(expectedDeviceId) ? null : expectedDeviceId.Trim();

        using var sha256 = SHA256.Create();
        var hashBytes = sha256.ComputeHash(Encoding.UTF8.GetBytes(rawToken));
        var tokenHash = Convert.ToBase64String(hashBytes);

        var session = _db.DeviceSessions.GetByTokenHash(tokenHash);
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
                // Seamless migration for legacy sessions:
                // If the session in the database has a legacy placeholder ("dev_")
                // and the authenticating client (possessing the verified raw refresh token) provides a
                // persistent device ID, adopt and persist the new ID regardless of format or prefix.
                if (!string.IsNullOrEmpty(expectedDeviceId) &&
                    session.DeviceId.StartsWith("dev_", StringComparison.OrdinalIgnoreCase))
                {
                    Console.WriteLine($"[SessionService] Migrating legacy session {session.Id} device ID from '{session.DeviceId}' to '{expectedDeviceId}'.");
                    session.DeviceId = expectedDeviceId;
                    session.LastSeenAt = DateTime.UtcNow;

                    // Deduplicate: revoke any other active duplicate sessions for this employee matching expectedDeviceId
                    var duplicateSessions = _db.DeviceSessions.GetByEmployeeId(session.EmployeeId)
                        .Where(s => s.Id != session.Id && s.RevokedAt == null && string.Equals(s.DeviceId, expectedDeviceId, StringComparison.OrdinalIgnoreCase))
                        .ToList();
                    foreach (var dup in duplicateSessions)
                    {
                        dup.RevokedAt = DateTime.UtcNow;
                        _db.DeviceSessions.Save(dup);
                    }

                    _db.DeviceSessions.Save(session);
                }
                else
                {
                    Console.WriteLine($"[SessionService] DeviceId mismatch or missing: request={expectedDeviceId}, session={session.DeviceId}. Rejecting.");
                    return null;
                }
            }
        }

        var employee = _db.Employees.GetById(session.EmployeeId);
        if (employee == null) return null;
        if (!allowRevoked && (!employee.IsActive || employee.IsSuspended || employee.IsBanned)) return null;

        return (session, employee);
    }

    /// <summary>
    /// Updates LastSeenAt on a session, throttled to once per minute to reduce DB writes.
    /// </summary>
    public void TouchLastSeen(DeviceSession session)
    {
        if (session.LastSeenAt < DateTime.UtcNow.AddMinutes(-1))
        {
            session.LastSeenAt = DateTime.UtcNow;
            _db.DeviceSessions.Save(session);
        }
    }

    /// <summary>
    /// Signs in the user with cookie auth and extends the session expiry by 90 days.
    /// Used by all session refresh/renewal paths.
    /// </summary>
    public async Task SignInAndExtendAsync(HttpContext context, DeviceSession session, Employee employee)
    {
        var identity = BuildIdentity(employee, session.Id);
        session.ExpiresAt = DateTime.UtcNow.AddDays(90);
        session.LastSeenAt = DateTime.UtcNow;
        _db.DeviceSessions.Save(session);

        await context.SignInAsync(
            CookieAuthenticationDefaults.AuthenticationScheme,
            new ClaimsPrincipal(identity),
            new AuthenticationProperties
            {
                IsPersistent = true,
                ExpiresUtc = DateTimeOffset.UtcNow.AddDays(1)
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
    }
}
