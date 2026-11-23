using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using Spokes_Server.Aggregate.Contexts;
using Spokes_Server.Core.Data.Repositories.Core;
using Spokes_Server.Core.Data.Repositories.HR;
using Spokes_Server.Core.Models.Core;
using Spokes_Server.Core.Models.HR;
using Spokes_Server.Core.Security;

namespace Spokes_Server.Core.Services.Security;

/// <summary>
/// A Scoped service uniquely instantiated for every active SignalR circuit (browser tab).
/// Used strictly for UI-level decryptions in Chat.razor/etc. to prevent IDP-hopped 
/// attacks from globally accessing a compromised Singleton keyset.
/// </summary>
public class ScopedKeystoreService : IDisposable
{
    private readonly IDataProtectionProvider _dataProtection;
    private readonly ICryptoService _crypto;
    private readonly GlobalKeystoreService? _globalKeystore;
    private readonly ILogger<ScopedKeystoreService>? _logger;
    private readonly FileTokenService? _fileTokenService;
    private readonly DeviceSessionRepository? _deviceSessions;
    private readonly AuthenticationStateProvider? _authStateProvider;
    private readonly SessionService? _sessionService;
    private readonly EmployeeRepository? _employees;

    public ScopedKeystoreService(
        IDataProtectionProvider dataProtection,
        ICryptoService? crypto = null,
        GlobalKeystoreService? globalKeystore = null,
        ILogger<ScopedKeystoreService>? logger = null,
        FileTokenService? fileTokenService = null,
        DeviceSessionRepository? deviceSessions = null,
        AuthenticationStateProvider? authStateProvider = null,
        SessionService? sessionService = null,
        HrContext? hrContext = null)
    {
        _dataProtection = dataProtection;
        _crypto = crypto ?? new CryptoService();
        _globalKeystore = globalKeystore;
        _logger = logger;
        _fileTokenService = fileTokenService;
        _deviceSessions = deviceSessions;
        _authStateProvider = authStateProvider;
        _sessionService = sessionService;
        _employees = hrContext?.Employees;
    }

    private string? _plainPrivateKey;
    private string? _employeeId;
    private Task? _deviceCookieInitTask;
    private bool _deviceCookieChecked;
    private bool _pendingHttpCookieRenewal;
    private readonly ConcurrentDictionary<string, string> _decryptedKeyCache = new(StringComparer.Ordinal);
    private readonly object _rsaCacheLock = new();
    private RSA? _cachedRsa;
    private string? _cachedRsaSourceKey;

    private readonly record struct SessionUnlockOutcome(
        bool Unlocked,
        bool NeedsRenewal,
        bool IsLegacyUpgrade,
        string? Password,
        DeviceSession? Session);

    public bool IsUnlocked => !string.IsNullOrEmpty(_plainPrivateKey);
    public bool HasPendingHttpCookieRenewal => _pendingHttpCookieRenewal;

    public event Action? OnUnlocked;
    public event Action? OnLocked;

    public void Unlock(string plainPrivateKey, string? employeeId = null)
    {
        if (!string.Equals(_plainPrivateKey, plainPrivateKey, StringComparison.Ordinal))
        {
            ResetCachedCryptoState();
        }
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
        _pendingHttpCookieRenewal = false;
        ResetCachedCryptoState();
        if (!string.IsNullOrWhiteSpace(targetEmployeeId))
        {
            _fileTokenService?.InvalidateUserEncryptedTokens(targetEmployeeId);
            _sessionService?.EvictGlobalKeystoreIfNoUnlockedSessions(targetEmployeeId);
        }
        OnLocked?.Invoke();
    }

    public string? GetPrivateKey() => _plainPrivateKey;

    /// <summary>
    /// Circuit-scoped cache for decrypted symmetric keys (e.g., channel epoch keys and album keys)
    /// so repeated Blazor renders and child components never re-run 2048-bit RSA decryption.
    /// Automatically cleared on <see cref="Lock"/>, key change in <see cref="Unlock"/>, and <see cref="Dispose"/>.
    /// </summary>
    public string? GetOrDecryptKey(string cacheKey, Func<string?, string?> decryptor)
    {
        if (string.IsNullOrEmpty(cacheKey))
        {
            return decryptor(_plainPrivateKey);
        }

        if (_decryptedKeyCache.TryGetValue(cacheKey, out var cached))
        {
            return cached;
        }

        var decrypted = decryptor(_plainPrivateKey);
        if (!string.IsNullOrEmpty(decrypted))
        {
            _decryptedKeyCache[cacheKey] = decrypted;
        }

        return decrypted;
    }

    /// <summary>
    /// Decrypts an RSA-OAEP-SHA256 ciphertext using the circuit's unlocked private key,
    /// reusing a cached parsed <see cref="RSA"/> key handle instead of reparsing RSA XML per call.
    /// </summary>
    public string? DecryptRsaWithCachedKey(string cipherText)
    {
        var privateKey = _plainPrivateKey;
        if (string.IsNullOrEmpty(privateKey) || string.IsNullOrEmpty(cipherText))
        {
            return null;
        }

        if (_crypto is CryptoService && privateKey.Contains("<RSAKeyValue>", StringComparison.Ordinal))
        {
            lock (_rsaCacheLock)
            {
                if (_cachedRsa == null || !string.Equals(_cachedRsaSourceKey, privateKey, StringComparison.Ordinal))
                {
                    _cachedRsa?.Dispose();
                    var rsa = RSA.Create();
                    rsa.FromXmlString(privateKey);
                    _cachedRsa = rsa;
                    _cachedRsaSourceKey = privateKey;
                }

                var bytes = Convert.FromBase64String(cipherText);
                var decrypted = _cachedRsa.Decrypt(bytes, RSAEncryptionPadding.OaepSHA256);
                return Encoding.UTF8.GetString(decrypted);
            }
        }

        return _crypto.DecryptRsa(cipherText, privateKey);
    }

    public void InvalidateCachedKey(string cacheKey)
    {
        if (!string.IsNullOrEmpty(cacheKey))
        {
            _decryptedKeyCache.TryRemove(cacheKey, out _);
        }
    }

    public void ClearDecryptedKeyCache() => _decryptedKeyCache.Clear();

    private void ResetCachedCryptoState()
    {
        _decryptedKeyCache.Clear();
        lock (_rsaCacheLock)
        {
            _cachedRsa?.Dispose();
            _cachedRsa = null;
            _cachedRsaSourceKey = null;
        }
    }

    public void Dispose()
    {
        _plainPrivateKey = null;
        _employeeId = null;
        _pendingHttpCookieRenewal = false;
        ResetCachedCryptoState();
    }

    /// <summary>
    /// Attempts to silently re-hydrate the locked scoped session directly from the
    /// chat_vault_key HttpOnly cookie on HttpContext.Request.Cookies.
    /// This reads the cookie server-side during the initial HTTP request, bypassing
    /// the JS fetch round-trip entirely. Must be called during OnInitializedAsync
    /// while HttpContext is still available (before the circuit switches to WebSocket).
    /// Honors remote device vault logouts by verifying DeviceSession.HasVaultCookie when available,
    /// performs sliding renewal when past half-life, and supports one-time v1 -> v2 upgrade for
    /// un-migrated server-v2026.11.0 sessions (HasVaultCookie == true with empty VaultNonce).
    /// </summary>
    public bool TryInitializeFromVaultCookie(
        HttpContext? httpContext,
        string employeeId,
        string encryptedPrivateKey)
    {
        if (IsUnlocked) return true;
        if (httpContext == null) return false;

        // Check for vault key cookie or header first before doing any session evaluation or cookie deletion
        bool fromCookie = true;
        if (!httpContext.Request.Cookies.TryGetValue(VaultCookieProtector.VaultCookieName, out var encryptedPwd) ||
            string.IsNullOrEmpty(encryptedPwd))
        {
            // Fallback to header for native mobile
            fromCookie = false;
            encryptedPwd = httpContext.Request.Headers["X-Chat-Vault-Key"].FirstOrDefault();
            if (string.IsNullOrEmpty(encryptedPwd))
            {
                return false;
            }
        }

        var sessionId = httpContext.User?.Identity?.IsAuthenticated == true
            ? httpContext.User.FindFirst("SessionId")?.Value
            : null;

        var outcome = TryUnprotectAndUnlockWithSession(encryptedPwd, sessionId, employeeId, encryptedPrivateKey);
        if (!outcome.Unlocked)
        {
            TryDeleteVaultCookie(httpContext);
            return false;
        }

        if (outcome.NeedsRenewal && fromCookie && outcome.Session != null && !string.IsNullOrEmpty(outcome.Password))
        {
            if (!httpContext.Response.HasStarted)
            {
                var refreshedToken = TryBuildRefreshedV2Token(outcome, employeeId);
                if (!string.IsNullOrEmpty(refreshedToken))
                {
                    VaultCookieProtector.AppendVaultCookie(httpContext.Response, refreshedToken);
                }
            }
            else
            {
                // On Blazor WebSocket circuits Response.HasStarted is already true; defer cookie issuance
                // (and legacy VaultNonce assignment) to POST /spokesapi/vault/renew via MainLayout.OnAfterRenderAsync.
                _pendingHttpCookieRenewal = true;
            }
        }

        return true;
    }

    /// <summary>
    /// If the circuit unlocked from an HTTP cookie that needed sliding renewal or a one-time v1 -> v2 upgrade
    /// while the WebSocket response had already started, triggers POST /spokesapi/vault/renew once over JS.
    /// </summary>
    public async Task RenewPendingHttpCookieAsync(IJSRuntime js)
    {
        if (!_pendingHttpCookieRenewal || !IsUnlocked) return;
        _pendingHttpCookieRenewal = false;
        try
        {
            await js.InvokeVoidAsync("spokesVault.renew");
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "[ScopedKeystore] Background vault cookie renewal skipped");
        }
    }

    private string? TryBuildRefreshedV2Token(SessionUnlockOutcome outcome, string employeeId)
    {
        if (outcome.Session == null || string.IsNullOrEmpty(outcome.Password))
        {
            return null;
        }

        string? vaultNonce = outcome.IsLegacyUpgrade
            ? (_deviceSessions != null ? SessionService.TryAssignLegacyVaultNonce(_deviceSessions, outcome.Session) : null)
            : outcome.Session.VaultNonce;

        if (string.IsNullOrEmpty(vaultNonce))
        {
            return null;
        }

        return VaultCookieProtector.Protect(
            _dataProtection,
            employeeId,
            outcome.Session.Id,
            vaultNonce,
            outcome.Password);
    }

    private SessionUnlockOutcome TryUnprotectAndUnlockWithSession(
        string? encryptedPwd,
        string? sessionId,
        string employeeId,
        string encryptedPrivateKey)
    {
        if (string.IsNullOrEmpty(encryptedPwd) ||
            _deviceSessions == null ||
            string.IsNullOrWhiteSpace(sessionId) ||
            string.IsNullOrEmpty(employeeId) ||
            string.IsNullOrEmpty(encryptedPrivateKey))
        {
            return default;
        }

        var session = _deviceSessions.GetById(sessionId);
        if (session == null ||
            session.RevokedAt != null ||
            session.ExpiresAt <= DateTime.UtcNow ||
            !session.HasVaultCookie ||
            !string.Equals(session.EmployeeId, employeeId, StringComparison.Ordinal))
        {
            return default;
        }

        if (_employees != null)
        {
            var liveEmployee = _employees.GetById(employeeId);
            if (liveEmployee is not { IsActive: true, IsSuspended: false, IsBanned: false })
            {
                return default;
            }
        }

        try
        {
            if (!string.IsNullOrEmpty(session.VaultNonce))
            {
                var result = VaultCookieProtector.TryUnprotectAndValidate(
                    _dataProtection,
                    encryptedPwd,
                    employeeId,
                    session.Id,
                    session.VaultNonce);

                if (result == null)
                {
                    return default;
                }

                if (!TryResolvePlainPrivateKey(employeeId, encryptedPrivateKey, result.Payload.Password, cacheOnMiss: true, out var plainKey))
                {
                    return default;
                }

                Unlock(plainKey, employeeId);
                return new SessionUnlockOutcome(true, result.NeedsRenewal, IsLegacyUpgrade: false, result.Payload.Password, session);
            }
            else
            {
                // Strict one-time upgrade path for pre-v2 (server-v2026.11.0) sessions loaded from disk
                // with HasVaultCookie == true and no persisted VaultNonce.
                var legacyPassword = VaultCookieProtector.TryUnprotectLegacy(_dataProtection, encryptedPwd);
                if (string.IsNullOrEmpty(legacyPassword))
                {
                    return default;
                }

                if (!TryResolvePlainPrivateKey(employeeId, encryptedPrivateKey, legacyPassword, cacheOnMiss: true, out var plainKey))
                {
                    return default;
                }

                Unlock(plainKey, employeeId);
                return new SessionUnlockOutcome(true, NeedsRenewal: true, IsLegacyUpgrade: true, legacyPassword, session);
            }
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "[ScopedKeystore] Failed to decrypt vault key from bound session payload for employee {EmployeeId}", employeeId);
            return default;
        }
    }

    private bool TryResolvePlainPrivateKey(string employeeId, string encryptedPrivateKey, string password, bool cacheOnMiss, out string plainKey)
    {
        if (_globalKeystore != null &&
            _globalKeystore.TryGetVerifiedPrivateKey(employeeId, encryptedPrivateKey, password, out var cachedKey) &&
            !string.IsNullOrEmpty(cachedKey))
        {
            plainKey = cachedKey;
            return true;
        }

        if (!VaultCookieProtector.TryVerifyVaultPassword(_crypto, employeeId, encryptedPrivateKey, password, out var decrypted))
        {
            plainKey = string.Empty;
            return false;
        }

        if (cacheOnMiss)
        {
            _globalKeystore?.StoreVerifiedPrivateKey(employeeId, encryptedPrivateKey, password, decrypted);
        }

        plainKey = decrypted;
        return true;
    }

    private void TryDeleteVaultCookie(HttpContext httpContext)
    {
        try
        {
            if (!httpContext.Response.HasStarted)
            {
                VaultCookieProtector.DeleteVaultCookie(httpContext.Response);
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

        string? sessionId = null;
        if (_authStateProvider != null)
        {
            try
            {
                var authState = await _authStateProvider.GetAuthenticationStateAsync();
                if (authState.User?.Identity?.IsAuthenticated == true)
                {
                    sessionId = authState.User.FindFirst("SessionId")?.Value;
                }
            }
            catch (Exception ex)
            {
                _logger?.LogDebug(ex, "Failed to resolve authentication state while checking device vault key");
            }
        }

        var outcome = TryUnprotectAndUnlockWithSession(encryptedPwd, sessionId, employeeId, encryptedPrivateKey);
        if (!outcome.Unlocked)
        {
            await ClearDeviceCookieAsync(js);
            return;
        }

        if (outcome.NeedsRenewal)
        {
            var refreshedToken = TryBuildRefreshedV2Token(outcome, employeeId);
            if (!string.IsNullOrEmpty(refreshedToken))
            {
                try
                {
                    await js.InvokeVoidAsync("spokesVault.storeVaultKey", refreshedToken);
                }
                catch (Exception ex)
                {
                    _logger?.LogDebug(ex, "[ScopedKeystore] Failed to persist refreshed device vault key");
                }
            }
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
        if (employee == null || string.IsNullOrWhiteSpace(password))
        {
            return false;
        }

        var effectiveEmployee = _employees != null ? _employees.GetById(employee.Id) : employee;
        if (effectiveEmployee == null || !effectiveEmployee.IsActive || effectiveEmployee.IsBanned || string.IsNullOrEmpty(effectiveEmployee.EncryptedPrivateKey))
        {
            return false;
        }

        try
        {
            if (!TryResolvePlainPrivateKey(effectiveEmployee.Id, effectiveEmployee.EncryptedPrivateKey, password, cacheOnMiss: false, out var plainKey))
            {
                return false;
            }

            Unlock(plainKey, effectiveEmployee.Id);

            if (!effectiveEmployee.IsSuspended)
            {
                _globalKeystore?.StoreVerifiedPrivateKey(effectiveEmployee.Id, effectiveEmployee.EncryptedPrivateKey, password, plainKey);

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

    public async Task ClearDeviceCookieAsync(IJSRuntime js)
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
