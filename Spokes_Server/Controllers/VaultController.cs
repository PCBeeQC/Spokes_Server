using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using System;
using System.Linq;

namespace Spokes_Server.Controllers;

[Route("spokesapi/vault")]
[ApiController]
[Authorize]
public class VaultController : SpokesControllerBase
{
    /// <summary>
    /// Determines if the request originates from a native mobile client.
    /// Checks for X-Spokes-Client header (preferred) with fallback to User-Agent.
    /// </summary>
    private bool IsMobileClient()
    {
        // Preferred: explicit header set by client JS (reliable, no UA parsing)
        var clientHeader = Request.Headers["X-Spokes-Client"].ToString();
        if (string.Equals(clientHeader, "mobile", StringComparison.OrdinalIgnoreCase))
            return true;

        // Fallback: User-Agent heuristics (for older clients that don't send the header yet)
        var userAgent = Request.Headers["User-Agent"].ToString() ?? string.Empty;
        return userAgent.Contains("Capacitor", StringComparison.OrdinalIgnoreCase);
    }

    [HttpPost("login")]
    public IActionResult Login([FromBody] VaultLoginRequest request, [FromServices] Microsoft.AspNetCore.DataProtection.IDataProtectionProvider dataProtection, [FromServices] Spokes_Server.Aggregate.Database db)
    {
        if (string.IsNullOrEmpty(request.Password))
        {
            return BadRequest("Password is required.");
        }

        var protector = dataProtection.CreateProtector("ChatVaultKey");
        var encryptedPassword = protector.Protect(request.Password);

        // On native mobile, the vault key is stored in Capacitor Preferences by the client JS.
        // Only set the cookie for web browsers where cookies are the sole storage mechanism.
        if (!IsMobileClient())
        {
            var cookieOptions = new CookieOptions
            {
                HttpOnly = true,
                Secure = true,
                SameSite = SameSiteMode.Lax,
                Path = "/",
                Expires = DateTimeOffset.UtcNow.AddDays(30)
            };
            Response.Cookies.Append("chat_vault_key", encryptedPassword, cookieOptions);
        }

        var session = Spokes_Server.Core.Security.SessionHelper.GetActiveSession(HttpContext, db);
        if (session != null)
        {
            session.HasVaultCookie = true;
            db.DeviceSessions.Save(session);
        }

        return Ok(new { success = true, vaultKey = encryptedPassword });
    }

    [HttpPost("logout")]
    public IActionResult Logout([FromServices] Spokes_Server.Aggregate.Database db)
    {
        Response.Cookies.Delete("chat_vault_key", new CookieOptions
        {
            Path = "/",
            HttpOnly = true,
            Secure = true,
            SameSite = SameSiteMode.Lax
        });

        var session = Spokes_Server.Core.Security.SessionHelper.GetActiveSession(HttpContext, db);
        if (session != null)
        {
            session.HasVaultCookie = false;
            db.DeviceSessions.Save(session);
        }

        return Ok(new { success = true });
    }

    [HttpGet("status")]
    public IActionResult Status([FromServices] Spokes_Server.Aggregate.Database db)
    {
        // On mobile, the vault key lives in Capacitor Preferences (not a cookie).
        // Check the DeviceSession's HasVaultCookie flag as a server-side source of truth.
        if (IsMobileClient())
        {
            var session = Spokes_Server.Core.Security.SessionHelper.GetActiveSession(HttpContext, db);
            bool hasVault = session?.HasVaultCookie == true;
            return Ok(new { hasCookie = hasVault });
        }

        // Web: check the actual cookie
        bool hasCookie = Request.Cookies.ContainsKey("chat_vault_key");
        return Ok(new { hasCookie });
    }
}

public class VaultLoginRequest
{
    public string Password { get; set; } = string.Empty;
}
