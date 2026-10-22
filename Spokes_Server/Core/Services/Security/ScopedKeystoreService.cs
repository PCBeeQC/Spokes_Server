using System.Text.Json;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using Spokes_Server.Core.Data.Repositories.Core;
using Spokes_Server.Core.Models.HR;

namespace Spokes_Server.Core.Services.Security;

/// <summary>
/// A Scoped service uniquely instantiated for every active SignalR circuit (browser tab).
/// Used strictly for UI-level decryptions in Chat.razor/etc. to prevent IDP-hopped 
/// attacks from globally accessing a compromised Singleton keyset.
/// </summary>
public class ScopedKeystoreService
{
    private readonly IDataProtectionProvider _dataProtection;
    private readonly ICryptoService _crypto;
    private readonly GlobalKeystoreService? _globalKeystore;
    private readonly ILogger<ScopedKeystoreService>? _logger;
    private readonly FileTokenService? _fileTokenService;
    private readonly DeviceSessionRepository? _deviceSessions;
    private readonly AuthenticationStateProvider? _authStateProvider;

    public ScopedKeystoreService(
        IDataProtectionProvider dataProtection,
        ICryptoService? crypto = null,
        GlobalKeystoreService? globalKeystore = null,
        ILogger<ScopedKeystoreService>? logger = null,
        FileTokenService? fileTokenService = null,
        DeviceSessionRepository? deviceSessions = null,
        AuthenticationStateProvider? authStateProvider = null)
    {
        _dataProtection = dataProtection;
        _crypto = crypto ?? new CryptoService();
        _globalKeystore = globalKeystore;
        _logger = logger;
        _fileTokenService = fileTokenService;
        _deviceSessions = deviceSessions;
        _authStateProvider = authStateProvider;
    }

    private string? _plainPrivateKey;
    private string? _employeeId;

    public bool IsUnlocked => !string.IsNullOrEmpty(_plainPrivateKey);

    public event Action? OnUnlocked;
    public event Action? OnLocked;

    public void Unlock(string plainPrivateKey, string? employeeId = null)
    {
        _plainPrivateKey = plainPrivateKey;
        if (!string.IsNullOrEmpty(employeeId))
        {
            _employeeId = employeeId;
        }
        OnUnlocked?.Invoke();
    }

    public void Lock(string? employeeId = null)
    {
        var targetEmployeeId = !string.IsNullOrEmpty(employeeId) ? employeeId : _employeeId;
        _plainPrivateKey = null;
        _fileTokenService?.InvalidateUserEncryptedTokens(targetEmployeeId);
        OnLocked?.Invoke();
    }

    public string? GetPrivateKey() => _plainPrivateKey;

    /// <summary>
    /// Attempts to silently re-hydrate the locked scoped session directly from the
    /// chat_vault_key HttpOnly cookie on HttpContext.Request.Cookies.
    /// This reads the cookie server-side during the initial HTTP request, bypassing
    /// the JS fetch round-trip entirely. Must be called during OnInitializedAsync
    /// while HttpContext is still available (before the circuit switches to WebSocket).
    /// </summary>
    public bool TryInitializeFromVaultCookie(
        HttpContext? httpContext,
        string employeeId,
        string encryptedPrivateKey)
    {
        if (IsUnlocked) return true;
        if (httpContext == null) return false;

        if (!httpContext.Request.Cookies.TryGetValue("chat_vault_key", out var encryptedPwd) ||
            string.IsNullOrEmpty(encryptedPwd))
        {
            // Fallback to header for native mobile
            encryptedPwd = httpContext.Request.Headers["X-Chat-Vault-Key"].FirstOrDefault();
            if (string.IsNullOrEmpty(encryptedPwd))
            {
                return false;
            }
        }

        try
        {
            var protector = _dataProtection.CreateProtector("ChatVaultKey");
            var pwd = protector.Unprotect(encryptedPwd);

            var kek = _crypto.DeriveKeyFromPassword(pwd, employeeId);
            var plainKey = _crypto.DecryptAes(encryptedPrivateKey, kek);

            Unlock(plainKey, employeeId);

            if (_globalKeystore != null && !_globalKeystore.IsUserUnlocked(employeeId))
            {
                _globalKeystore.UnlockVault(employeeId, encryptedPrivateKey, pwd, _crypto);
            }

            return true;
        }
        catch (Exception)
        {
            // Cookie invalid or tampered — vault stays locked.
            return false;
        }
    }

    /// <summary>
    /// Attempts to silently re-hydrate the locked scoped session from the device cookie.
    /// On mobile (Capacitor), reads from Preferences via static JS functions.
    /// On web, the vault key comes from the HttpOnly cookie handled by TryInitializeFromVaultCookie.
    /// Honors remote device vault logouts by verifying DeviceSession.HasVaultCookie when available.
    /// Optionally unlocks GlobalKeystoreService for background push notification decryption.
    /// </summary>
    public async Task InitializeFromDeviceCookieAsync(IJSRuntime js, string employeeId, string encryptedPrivateKey)
    {
        if (IsUnlocked) return;

        try
        {
            var response = await js.InvokeAsync<JsonElement>("spokesVault.getStoredVaultKey");
            if (response.ValueKind != JsonValueKind.Null && response.TryGetProperty("key", out var keyProp))
            {
                var encryptedPwd = keyProp.GetString();
                if (!string.IsNullOrEmpty(encryptedPwd))
                {
                    if (_authStateProvider != null && _deviceSessions != null)
                    {
                        var authState = await _authStateProvider.GetAuthenticationStateAsync();
                        var sessionId = authState.User.FindFirst("SessionId")?.Value;
                        if (!string.IsNullOrEmpty(sessionId))
                        {
                            var session = _deviceSessions.GetById(sessionId);
                            if (session != null && !session.HasVaultCookie)
                            {
                                // The server explicitly logged this device out of the vault,
                                // so clear the stale local preference instead of reviving the session.
                                await ClearDeviceCookieAsync(js, employeeId);
                                return;
                            }
                        }
                    }

                    // Decrypt the token securely from the server side
                    var protector = _dataProtection.CreateProtector("ChatVaultKey");
                    var pwd = protector.Unprotect(encryptedPwd);

                    // Derive and decrypt to prove validity
                    var kek = _crypto.DeriveKeyFromPassword(pwd, employeeId);
                    var plainKey = _crypto.DecryptAes(encryptedPrivateKey, kek);

                    Unlock(plainKey, employeeId);

                    // Also unlock GlobalKeystoreService for background push notification decryption.
                    // On mobile, the SSR cookie-based unlock in App.razor doesn't fire,
                    // so this is the primary unlock path for background services.
                    if (_globalKeystore != null && !_globalKeystore.IsUserUnlocked(employeeId))
                    {
                        _globalKeystore.UnlockVault(employeeId, encryptedPrivateKey, pwd, _crypto);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            // Silently swallow; if cookie is invalid/missing, vault stays locked.
            _logger?.LogDebug(ex, "InitializeFromDeviceCookie failed");
        }
    }

    /// <summary>
    /// Unlocks the scoped and global keystores using the provided vault password,
    /// and optionally persists the device vault cookie via /spokesapi/vault/login.
    /// </summary>
    public async Task<bool> UnlockWithPasswordAsync(
        IJSRuntime js,
        Employee? employee,
        string password,
        bool rememberDevice = true)
    {
        if (employee == null || string.IsNullOrWhiteSpace(password) || string.IsNullOrEmpty(employee.EncryptedPrivateKey))
        {
            return false;
        }

        try
        {
            var kek = _crypto.DeriveKeyFromPassword(password, employee.Id);
            var plainKey = _crypto.DecryptAes(employee.EncryptedPrivateKey, kek);

            Unlock(plainKey, employee.Id);
            _globalKeystore?.UnlockVault(employee.Id, employee.EncryptedPrivateKey, password, _crypto);

            if (rememberDevice)
            {
                await SaveDeviceCookieAsync(js, password);
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Persists the vault password to the browser HttpOnly cookie (on web) and Capacitor Preferences (on mobile)
    /// via window.spokesVault.login.
    /// </summary>
    public async Task<bool> SaveDeviceCookieAsync(IJSRuntime js, string password)
    {
        try
        {
            var response = await js.InvokeAsync<JsonElement>("spokesVault.login", password);
            return response.ValueKind == JsonValueKind.Object &&
                   response.TryGetProperty("success", out var s) &&
                   s.ValueKind == JsonValueKind.True;
        }
        catch (Exception ex)
        {
            // Non-fatal if JS/cookie sync fails; scoped keystore is already unlocked in circuit memory
            _logger?.LogDebug(ex, "SaveDeviceCookieAsync failed");
            return false;
        }
    }

    /// <summary>
    /// Queries whether the current device session has a saved vault cookie via window.spokesVault.getStatus.
    /// </summary>
    public async Task<bool> HasDeviceCookieAsync(IJSRuntime js)
    {
        try
        {
            var response = await js.InvokeAsync<JsonElement>("spokesVault.getStatus");
            if (response.ValueKind == JsonValueKind.Object &&
                response.TryGetProperty("hasCookie", out var hc) &&
                (hc.ValueKind == JsonValueKind.True || hc.ValueKind == JsonValueKind.False))
            {
                return hc.GetBoolean();
            }
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "HasDeviceCookieAsync failed");
        }
        return false;
    }

    /// <summary>
    /// Logs the current device out of the vault (/spokesapi/vault/logout + Capacitor Preferences cleanup),
    /// locks the scoped keystore for this circuit, and invalidates encrypted file tokens for the user.
    /// Does NOT lock GlobalKeystoreService so other unlocked devices continue receiving decrypted push notifications.
    /// </summary>
    public async Task LogoutDeviceVaultAsync(IJSRuntime js, string employeeId)
    {
        try
        {
            await js.InvokeVoidAsync("spokesVault.logout");
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "LogoutDeviceVaultAsync failed");
        }

        Lock(employeeId);
    }

    public async Task ClearDeviceCookieAsync(IJSRuntime js, string employeeId)
    {
        try
        {
            await js.InvokeVoidAsync("spokesVault.clearStoredVaultKey");
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "ClearDeviceCookieAsync failed");
        }
    }
}
