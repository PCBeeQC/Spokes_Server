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
    private readonly SessionService? _sessionService;

    public ScopedKeystoreService(
        IDataProtectionProvider dataProtection,
        ICryptoService? crypto = null,
        GlobalKeystoreService? globalKeystore = null,
        ILogger<ScopedKeystoreService>? logger = null,
        FileTokenService? fileTokenService = null,
        DeviceSessionRepository? deviceSessions = null,
        AuthenticationStateProvider? authStateProvider = null,
        SessionService? sessionService = null)
    {
        _dataProtection = dataProtection;
        _crypto = crypto ?? new CryptoService();
        _globalKeystore = globalKeystore;
        _logger = logger;
        _fileTokenService = fileTokenService;
        _deviceSessions = deviceSessions;
        _authStateProvider = authStateProvider;
        _sessionService = sessionService;
    }

    private string? _plainPrivateKey;
    private string? _employeeId;
    private Task? _deviceCookieInitTask;
    private bool _deviceCookieChecked;

    public bool IsUnlocked => !string.IsNullOrEmpty(_plainPrivateKey);

    public event Action? OnUnlocked;
    public event Action? OnLocked;

    public void Unlock(string plainPrivateKey, string? employeeId = null)
    {
        _plainPrivateKey = plainPrivateKey;
        _deviceCookieChecked = true;
        if (!string.IsNullOrEmpty(employeeId))
        {
            _employeeId = employeeId;
        }
        OnUnlocked?.Invoke();
    }

    public void Lock(string? employeeId = null)
    {
        var targetEmployeeId = !string.IsNullOrWhiteSpace(employeeId) ? employeeId : _employeeId;
        _plainPrivateKey = null;
        _employeeId = null;
        _deviceCookieChecked = false;
        _deviceCookieInitTask = null;
        if (!string.IsNullOrWhiteSpace(targetEmployeeId))
        {
            _fileTokenService?.InvalidateUserEncryptedTokens(targetEmployeeId);
            _sessionService?.EvictGlobalKeystoreIfNoUnlockedSessions(targetEmployeeId);
        }
        OnLocked?.Invoke();
    }

    public string? GetPrivateKey() => _plainPrivateKey;

    /// <summary>
    /// Attempts to silently re-hydrate the locked scoped session directly from the
    /// chat_vault_key HttpOnly cookie on HttpContext.Request.Cookies.
    /// This reads the cookie server-side during the initial HTTP request, bypassing
    /// the JS fetch round-trip entirely. Must be called during OnInitializedAsync
    /// while HttpContext is still available (before the circuit switches to WebSocket).
    /// Honors remote device vault logouts by verifying DeviceSession.HasVaultCookie when available.
    /// </summary>
    public bool TryInitializeFromVaultCookie(
        HttpContext? httpContext,
        string employeeId,
        string encryptedPrivateKey)
    {
        if (IsUnlocked) return true;
        if (httpContext == null) return false;

        // Check for vault key cookie or header first before doing any session evaluation or cookie deletion
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

        if (_deviceSessions != null)
        {
            var sessionId = httpContext.User?.FindFirst("SessionId")?.Value;
            if (!string.IsNullOrEmpty(sessionId))
            {
                var session = _deviceSessions.GetById(sessionId);
                if (session != null &&
                    (session.RevokedAt != null ||
                     !session.HasVaultCookie ||
                     !string.Equals(session.EmployeeId, employeeId, StringComparison.OrdinalIgnoreCase)))
                {
                    TryDeleteVaultCookie(httpContext);
                    return false;
                }
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
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "[ScopedKeystore] Failed to decrypt vault key from cookie for employee {EmployeeId}", employeeId);
            // Cookie invalid or tampered — vault stays locked.
            return false;
        }
    }

    private void TryDeleteVaultCookie(HttpContext httpContext)
    {
        try
        {
            if (!httpContext.Response.HasStarted)
            {
                httpContext.Response.Cookies.Delete("chat_vault_key", new CookieOptions
                {
                    Path = "/",
                    HttpOnly = true,
                    Secure = true,
                    SameSite = SameSiteMode.Lax
                });
            }
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "[ScopedKeystore] Could not delete chat_vault_key cookie from response (headers already started)");
        }
    }

    /// <summary>
    /// Reserves a single-flight device-cookie initialization slot for a batched startup interop call
    /// (such as MainLayout's SpokesBridge.initLayout) so concurrent child components (Chat, Albums)
    /// await the batched result instead of issuing duplicate SignalR JS round-trips.
    /// </summary>
    public ExternalDeviceCookieInitScope BeginExternalDeviceCookieInit()
    {
        if (IsUnlocked || _deviceCookieChecked || _deviceCookieInitTask != null)
        {
            return default;
        }

        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _deviceCookieInitTask = tcs.Task;
        return new ExternalDeviceCookieInitScope(this, tcs);
    }

    public readonly struct ExternalDeviceCookieInitScope : IDisposable
    {
        private readonly ScopedKeystoreService? _owner;
        private readonly TaskCompletionSource<bool>? _tcs;

        internal ExternalDeviceCookieInitScope(ScopedKeystoreService owner, TaskCompletionSource<bool> tcs)
        {
            _owner = owner;
            _tcs = tcs;
        }

        public async Task CompleteAsync(IJSRuntime js, string? storedVaultKey, string? employeeId, string? encryptedPrivateKey)
        {
            if (_owner == null || _tcs == null) return;
            try
            {
                if (!_owner.IsUnlocked && !string.IsNullOrEmpty(employeeId) && !string.IsNullOrEmpty(encryptedPrivateKey))
                {
                    await _owner.TryUnlockFromProtectedDeviceKeyAsync(js, storedVaultKey, employeeId, encryptedPrivateKey);
                }
                _owner._deviceCookieChecked = true;
            }
            finally
            {
                _tcs.TrySetResult(_owner.IsUnlocked);
            }
        }

        public void Dispose()
        {
            if (_owner == null || _tcs == null || _tcs.Task.IsCompleted) return;
            if (ReferenceEquals(_owner._deviceCookieInitTask, _tcs.Task))
            {
                _owner._deviceCookieInitTask = null;
            }
            _tcs.TrySetResult(false);
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
        if (IsUnlocked || _deviceCookieChecked) return;

        var inflight = _deviceCookieInitTask;
        if (inflight != null)
        {
            await inflight;
            if (IsUnlocked || _deviceCookieChecked) return;
        }

        var initTask = InitializeFromDeviceCookieCoreAsync(js, employeeId, encryptedPrivateKey);
        _deviceCookieInitTask = initTask;
        await initTask;
    }

    private async Task InitializeFromDeviceCookieCoreAsync(IJSRuntime js, string employeeId, string encryptedPrivateKey)
    {
        try
        {
            var response = await js.InvokeAsync<JsonElement>("spokesVault.getStoredVaultKey");
            string? encryptedPwd = null;
            if (response.ValueKind != JsonValueKind.Null && response.TryGetProperty("key", out var keyProp))
            {
                encryptedPwd = keyProp.GetString();
            }

            await TryUnlockFromProtectedDeviceKeyAsync(js, encryptedPwd, employeeId, encryptedPrivateKey);
            _deviceCookieChecked = true;
        }
        catch (Exception ex)
        {
            // Silently swallow; if cookie is invalid/missing, vault stays locked.
            _deviceCookieChecked = true;
            _logger?.LogDebug(ex, "InitializeFromDeviceCookie failed");
        }
    }

    private async Task TryUnlockFromProtectedDeviceKeyAsync(IJSRuntime js, string? encryptedPwd, string employeeId, string encryptedPrivateKey)
    {
        if (IsUnlocked || string.IsNullOrEmpty(encryptedPwd)) return;

        try
        {
            if (_authStateProvider != null && _deviceSessions != null)
            {
                var authState = await _authStateProvider.GetAuthenticationStateAsync();
                var sessionId = authState.User.FindFirst("SessionId")?.Value;
                if (!string.IsNullOrEmpty(sessionId))
                {
                    var session = _deviceSessions.GetById(sessionId);
                    if (session != null &&
                        (session.RevokedAt != null ||
                         !session.HasVaultCookie ||
                         !string.Equals(session.EmployeeId, employeeId, StringComparison.OrdinalIgnoreCase)))
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
        catch (Exception ex)
        {
            // Silently swallow; if cookie is invalid/missing, vault stays locked.
            _logger?.LogDebug(ex, "TryUnlockFromProtectedDeviceKeyAsync failed");
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
        if (employee == null || !employee.IsActive || employee.IsBanned || string.IsNullOrWhiteSpace(password) || string.IsNullOrEmpty(employee.EncryptedPrivateKey))
        {
            return false;
        }

        try
        {
            var kek = _crypto.DeriveKeyFromPassword(password, employee.Id);
            var plainKey = _crypto.DecryptAes(employee.EncryptedPrivateKey, kek);

            Unlock(plainKey, employee.Id);

            if (!employee.IsSuspended)
            {
                _globalKeystore?.UnlockVault(employee.Id, employee.EncryptedPrivateKey, password, _crypto);

                if (rememberDevice)
                {
                    await SaveDeviceCookieAsync(js, password);
                }
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
