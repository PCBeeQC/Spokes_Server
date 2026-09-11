namespace Spokes_Server.Core.Security;

using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Spokes_Server.Core.Models.Core;
using Spokes_Server.Aggregate;
using System.Security.Cryptography;
using System.Text;

using System.Linq;

public static class SessionHelper
{
    public static DeviceSession? GetActiveSession(HttpContext context, Database db)
    {
        // 1. First, rely on the active authentication context (Spokes_Session_v3 cookie)
        // This is sent by both Capacitor Mobile apps and Standard Desktop browsers
        if (context.User.Identity?.IsAuthenticated == true)
        {
            var sessionId = context.User.FindFirst("SessionId")?.Value;
            if (!string.IsNullOrEmpty(sessionId))
            {
                var session = db.DeviceSessions.GetById(sessionId);
                if (session?.RevokedAt == null) return session;
            }
        }

        // 2. Fallback to the long-lived Spokes_Refresh cookie for desktop web edge-cases
        if (context.Request.Cookies.TryGetValue("Spokes_Refresh", out var token) && !string.IsNullOrEmpty(token))
        {
            var sessionService = context.RequestServices.GetRequiredService<SessionService>();
            var deviceId = context.Request.Headers["X-Device-Id"].FirstOrDefault() ?? context.Request.Cookies["Spokes_Device"];
            var result = sessionService.ValidateToken(token, deviceId);
            return result?.Session;
        }
        
        return null;
    }

    public static DeviceSession IssueRefreshToken(HttpContext context, Database db, string employeeId, out string? rawToken, string? deviceId = null, bool issueCookie = true, string? idToken = null)
    {
        rawToken = null;
        var tokenBytes = new byte[64];
        using (var rng = RandomNumberGenerator.Create())
        {
            rng.GetBytes(tokenBytes);
        }
        var refreshToken = Convert.ToBase64String(tokenBytes);
        if (!issueCookie) rawToken = refreshToken;

        using var sha256 = SHA256.Create();
        var hashBytes = sha256.ComputeHash(Encoding.UTF8.GetBytes(refreshToken));
        var tokenHash = Convert.ToBase64String(hashBytes);

        var userAgent = context.Request.Headers["User-Agent"].ToString();
        var deviceInfo = "Unknown Device";
        if (userAgent.Contains("Windows")) deviceInfo = "Windows";
        else if (userAgent.Contains("iPhone") || userAgent.Contains("iPad")) deviceInfo = "iOS";
        else if (userAgent.Contains("Android")) deviceInfo = "Android";
        else if (userAgent.Contains("Mac OS")) deviceInfo = "macOS";

        if (userAgent.Contains("Capacitor") || userAgent.Contains("wv")) deviceInfo += " (App)";

        if (string.IsNullOrWhiteSpace(deviceId))
        {
            deviceId = null;
        }
        else
        {
            deviceId = deviceId.Trim();
        }

        // Look for active sessions matching this employee.
        // Prefer an exact device match; if none exists, adopt any legacy dev_ or unbound session.
        var employeeSessions = db.DeviceSessions.GetAll()
            .Where(s => s.EmployeeId == employeeId && s.RevokedAt == null)
            .ToList();

        // Only match existing sessions if a device ID is present.
        // Desktop web sessions (deviceId == null) always create isolated sessions to allow multi-workstation concurrency.
        var matchingSessions = !string.IsNullOrEmpty(deviceId)
            ? employeeSessions.Where(s => !string.IsNullOrEmpty(s.DeviceId) && string.Equals(s.DeviceId, deviceId, StringComparison.OrdinalIgnoreCase)).ToList()
            : new List<DeviceSession>();

        if (!matchingSessions.Any() && !string.IsNullOrEmpty(deviceId))
        {
            // If no exact match, check for a legacy dev_ session to adopt.
            // NEVER adopt unbound desktop sessions (where DeviceId is null or empty).
            var legacySession = employeeSessions.FirstOrDefault(s => !string.IsNullOrEmpty(s.DeviceId) && s.DeviceId.StartsWith("dev_", StringComparison.OrdinalIgnoreCase));
            if (legacySession != null)
            {
                matchingSessions.Add(legacySession);
            }
        }

        DeviceSession deviceSession;
        if (matchingSessions.Any())
        {
            deviceSession = matchingSessions.First();
            deviceSession.TokenHash = tokenHash;
            deviceSession.DeviceInfo = deviceInfo;
            deviceSession.DeviceId = deviceId; // Ensure adopted session receives the modernized device ID
            deviceSession.ExpiresAt = DateTime.UtcNow.AddDays(90);
            deviceSession.LastSeenAt = DateTime.UtcNow;
            if (!string.IsNullOrEmpty(idToken)) deviceSession.IdToken = idToken;

            // Preserve existing push credentials across same-device re-logins and legacy dev_ migrations
            db.DeviceSessions.Save(deviceSession);

            // Revoke any duplicates that were created previously
            foreach (var duplicate in matchingSessions.Skip(1))
            {
                duplicate.RevokedAt = DateTime.UtcNow;
                db.DeviceSessions.Save(duplicate);
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
                CreatedAt = DateTime.UtcNow,
                ExpiresAt = DateTime.UtcNow.AddDays(90),
                LastSeenAt = DateTime.UtcNow
            };
            if (!string.IsNullOrEmpty(idToken)) deviceSession.IdToken = idToken;
            db.DeviceSessions.Save(deviceSession);
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

        return deviceSession;
    }
}
