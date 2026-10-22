using System.Security.Claims;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc;
using Spokes_Server.Aggregate;
using Spokes_Server.Aggregate.Contexts;
using Spokes_Server.Core.Security;
using Spokes_Server.Core.Services.Security;

namespace Spokes_Server.Controllers;

[Route("spokesapi/vault")]
[ApiController]
[Authorize]
public class VaultController : SpokesControllerBase
{
    private const string VaultCookieName = "chat_vault_key";
    private readonly CoreContext _coreContext;
    private readonly SessionService _sessionService;
    private readonly IDataProtectionProvider _dataProtection;
    private readonly FileTokenService? _fileTokenService;

    [ActivatorUtilitiesConstructor]
    public VaultController(
        CoreContext coreContext,
        SessionService sessionService,
        IDataProtectionProvider dataProtection,
        FileTokenService? fileTokenService = null)
    {
        _coreContext = coreContext ?? throw new ArgumentNullException(nameof(coreContext));
        _sessionService = sessionService ?? throw new ArgumentNullException(nameof(sessionService));
        _dataProtection = dataProtection ?? throw new ArgumentNullException(nameof(dataProtection));
        _fileTokenService = fileTokenService;
    }

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
    public IActionResult Login([FromBody] VaultLoginRequest request)
    {
        if (string.IsNullOrEmpty(request.Password))
        {
            return BadRequestError("Password is required.");
        }

        var protector = _dataProtection.CreateProtector("ChatVaultKey");
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
            Response.Cookies.Append(VaultCookieName, encryptedPassword, cookieOptions);
        }

        var session = _sessionService.GetActiveSession(HttpContext);
        if (session != null)
        {
            session.HasVaultCookie = true;
            _coreContext.DeviceSessions.Save(session);
        }

        return Ok(new VaultLoginResponse(true, encryptedPassword));
    }

    [HttpPost("logout")]
    public IActionResult Logout()
    {
        Response.Cookies.Delete(VaultCookieName, new CookieOptions
        {
            Path = "/",
            HttpOnly = true,
            Secure = true,
            SameSite = SameSiteMode.Lax
        });

        var session = _sessionService.GetActiveSession(HttpContext);
        if (session != null)
        {
            session.HasVaultCookie = false;
            _coreContext.DeviceSessions.Save(session);
            _fileTokenService?.InvalidateUserEncryptedTokens(session.EmployeeId);
        }
        else
        {
            var userId = User?.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (!string.IsNullOrEmpty(userId))
            {
                _fileTokenService?.InvalidateUserEncryptedTokens(userId);
            }
        }

        return Ok(new VaultLogoutResponse(true));
    }

    [HttpGet("status")]
    public IActionResult Status()
    {
        // On mobile, the vault key lives in Capacitor Preferences (not a cookie).
        // Check the DeviceSession's HasVaultCookie flag as a server-side source of truth.
        if (IsMobileClient())
        {
            var session = _sessionService.GetActiveSession(HttpContext);
            bool hasVault = session?.HasVaultCookie == true;
            return Ok(new VaultStatusResponse(hasVault));
        }

        // Web: check the actual cookie
        bool hasCookie = Request.Cookies.ContainsKey(VaultCookieName);
        return Ok(new VaultStatusResponse(hasCookie));
    }
}

public class VaultLoginRequest
{
    public string Password { get; set; } = string.Empty;
}

public record VaultLoginResponse(
    [property: JsonPropertyName("success")] bool Success,
    [property: JsonPropertyName("vaultKey")] string VaultKey);

public record VaultLogoutResponse(
    [property: JsonPropertyName("success")] bool Success);

public record VaultStatusResponse(
    [property: JsonPropertyName("hasCookie")] bool HasCookie);
