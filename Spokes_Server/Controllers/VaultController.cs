using System.Security.Claims;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc;
using Spokes_Server.Aggregate;
using Spokes_Server.Aggregate.Contexts;
using Spokes_Server.Core.Helpers;
using Spokes_Server.Core.Security;
using Spokes_Server.Core.Services.Security;

namespace Spokes_Server.Controllers;

[Route("spokesapi/vault")]
[ApiController]
[Authorize]
public class VaultController : SpokesControllerBase
{
    private readonly CoreContext _coreContext;
    private readonly SessionService _sessionService;
    private readonly IDataProtectionProvider _dataProtection;
    private readonly FileTokenService? _fileTokenService;
    private readonly ICryptoService _crypto;

    [ActivatorUtilitiesConstructor]
    public VaultController(
        CoreContext coreContext,
        SessionService sessionService,
        IDataProtectionProvider dataProtection,
        FileTokenService? fileTokenService = null,
        ICryptoService? crypto = null)
    {
        _coreContext = coreContext ?? throw new ArgumentNullException(nameof(coreContext));
        _sessionService = sessionService ?? throw new ArgumentNullException(nameof(sessionService));
        _dataProtection = dataProtection ?? throw new ArgumentNullException(nameof(dataProtection));
        _fileTokenService = fileTokenService;
        _crypto = crypto ?? new CryptoService();
    }

    [HttpPost("login")]
    public IActionResult Login([FromBody] VaultLoginRequest request)
    {
        if (User?.HasClaim("PendingApproval", "true") == true)
        {
            return ForbiddenError("An active linked account is required to unlock the vault.", "FORBIDDEN");
        }

        if (string.IsNullOrEmpty(request.Password))
        {
            return BadRequestError("Password is required.");
        }

        var session = _sessionService.GetActiveSession(HttpContext);
        if (session == null ||
            session.RevokedAt != null ||
            session.ExpiresAt <= DateTime.UtcNow ||
            string.IsNullOrEmpty(session.EmployeeId))
        {
            return ForbiddenError("An active device session is required to persist the vault key.", "ACTIVE_SESSION_REQUIRED");
        }

        var employee = _sessionService.GetEmployeeById(session.EmployeeId);
        if (employee == null || !employee.IsActiveUnsuspended)
        {
            return ForbiddenError("Vault persistence is not permitted while account is inactive, suspended, or banned.", "ACCOUNT_RESTRICTED");
        }

        if (string.IsNullOrEmpty(employee.EncryptedPrivateKey))
        {
            return BadRequestError("No encrypted vault key is configured for this account.", "VAULT_NOT_INITIALIZED");
        }

        if (!VaultCookieProtector.TryVerifyVaultPassword(_crypto, employee.Id, employee.EncryptedPrivateKey, request.Password, out _))
        {
            return UnauthorizedError("Invalid vault password.", "INVALID_VAULT_PASSWORD");
        }

        session.VaultNonce = Guid.NewGuid().ToString("N");
        session.HasVaultCookie = true;
        _coreContext.DeviceSessions.Save(session);

        var encryptedPassword = VaultCookieProtector.Protect(
            _dataProtection,
            employee.Id,
            session.Id,
            session.VaultNonce,
            request.Password);

        // On native mobile, the vault key is stored in Capacitor Preferences by the client JS.
        // Only set the cookie for web browsers where cookies are the sole storage mechanism.
        if (!DeviceDetectionHelper.IsMobileClient(HttpContext))
        {
            VaultCookieProtector.AppendVaultCookie(Response, encryptedPassword);
        }

        return Ok(new VaultLoginResponse(true, encryptedPassword));
    }

    [HttpPost("renew")]
    public IActionResult Renew()
    {
        if (User?.HasClaim("PendingApproval", "true") == true)
        {
            return ForbiddenError("An active linked account is required to renew the vault session.", "FORBIDDEN");
        }

        var session = _sessionService.GetActiveSession(HttpContext);
        if (session == null ||
            session.RevokedAt != null ||
            session.ExpiresAt <= DateTime.UtcNow ||
            !session.HasVaultCookie ||
            string.IsNullOrEmpty(session.EmployeeId))
        {
            return UnauthorizedError("An active vault session is required to renew the vault key.", "VAULT_RENEWAL_FAILED");
        }

        var employee = _sessionService.GetEmployeeById(session.EmployeeId);
        if (employee == null || !employee.IsActiveUnsuspended || string.IsNullOrEmpty(employee.EncryptedPrivateKey))
        {
            return ForbiddenError("Vault persistence is not permitted while account is inactive, suspended, or banned.", "ACCOUNT_RESTRICTED");
        }

        var encryptedPwd = Request.Cookies.TryGetValue(VaultCookieProtector.VaultCookieName, out var cookieVal) && !string.IsNullOrEmpty(cookieVal)
            ? cookieVal
            : Request.Headers["X-Chat-Vault-Key"].FirstOrDefault();

        if (string.IsNullOrEmpty(encryptedPwd))
        {
            return UnauthorizedError("Vault key is required to renew.", "VAULT_RENEWAL_FAILED");
        }

        string? password;
        string? vaultNonce = null;

        try
        {
            if (!string.IsNullOrEmpty(session.VaultNonce))
            {
                var result = VaultCookieProtector.TryUnprotectAndValidate(
                    _dataProtection,
                    encryptedPwd,
                    employee.Id,
                    session.Id,
                    session.VaultNonce);
                if (result == null)
                {
                    return UnauthorizedError("Invalid or expired vault key.", "VAULT_RENEWAL_FAILED");
                }

                password = result.Payload.Password;
                vaultNonce = session.VaultNonce;
            }
            else
            {
                password = VaultCookieProtector.TryUnprotectLegacy(_dataProtection, encryptedPwd);
                if (string.IsNullOrEmpty(password))
                {
                    return UnauthorizedError("Invalid legacy vault key.", "VAULT_RENEWAL_FAILED");
                }
            }
        }
        catch (Exception)
        {
            return UnauthorizedError("Invalid or expired vault key.", "VAULT_RENEWAL_FAILED");
        }

        if (!VaultCookieProtector.TryVerifyVaultPassword(_crypto, employee.Id, employee.EncryptedPrivateKey, password, out _))
        {
            return UnauthorizedError("Invalid vault password.", "INVALID_VAULT_PASSWORD");
        }

        if (string.IsNullOrEmpty(vaultNonce))
        {
            vaultNonce = _sessionService.TryAssignLegacyVaultNonce(session);
            if (string.IsNullOrEmpty(vaultNonce))
            {
                // Another concurrent request already upgraded or invalidated this session;
                // intentionally do NOT delete the client cookie so a sibling tab's fresh v2 cookie is preserved.
                return UnauthorizedError("Vault session state changed during renewal.", "VAULT_RENEWAL_FAILED");
            }
        }

        var refreshedPassword = VaultCookieProtector.Protect(
            _dataProtection,
            employee.Id,
            session.Id,
            vaultNonce,
            password);

        if (!DeviceDetectionHelper.IsMobileClient(HttpContext))
        {
            VaultCookieProtector.AppendVaultCookie(Response, refreshedPassword);
        }

        return Ok(new VaultLoginResponse(true, refreshedPassword));
    }

    [HttpPost("logout")]
    public IActionResult Logout()
    {
        VaultCookieProtector.DeleteVaultCookie(Response);

        var session = _sessionService.GetActiveSession(HttpContext);
        if (session != null)
        {
            session.HasVaultCookie = false;
            session.VaultNonce = Guid.NewGuid().ToString("N");
            _coreContext.DeviceSessions.Save(session);
        }

        var userId = !string.IsNullOrWhiteSpace(session?.EmployeeId)
            ? session.EmployeeId
            : (User?.FindFirst("EmployeeId")?.Value ?? User?.FindFirst(ClaimTypes.NameIdentifier)?.Value);
        if (!string.IsNullOrWhiteSpace(userId))
        {
            _fileTokenService?.InvalidateUserEncryptedTokens(userId);
            _sessionService.EvictGlobalKeystoreIfNoUnlockedSessions(userId);
        }

        return Ok(new VaultLogoutResponse(true));
    }

    [HttpGet("status")]
    public IActionResult Status()
    {
        // On mobile, the vault key lives in Capacitor Preferences (not a cookie).
        // Check the DeviceSession's HasVaultCookie flag as a server-side source of truth.
        if (DeviceDetectionHelper.IsMobileClient(HttpContext))
        {
            var session = _sessionService.GetActiveSession(HttpContext);
            bool hasVault = session?.HasVaultCookie == true;
            return Ok(new VaultStatusResponse(hasVault));
        }

        // Web: check the actual cookie
        bool hasCookie = Request.Cookies.ContainsKey(VaultCookieProtector.VaultCookieName);
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
