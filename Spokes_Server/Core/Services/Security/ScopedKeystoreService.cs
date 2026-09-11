using System;
using System.Threading.Tasks;
using Microsoft.JSInterop;
using Microsoft.AspNetCore.DataProtection;

namespace Spokes_Server.Core.Services.Security;

/// <summary>
/// A Scoped service uniquely instantiated for every active SignalR circuit (browser tab).
/// Used strictly for UI-level decryptions in Chat.razor/etc. to prevent IDP-hopped 
/// attacks from globally accessing a compromised Singleton keyset.
/// </summary>
public class ScopedKeystoreService
{
    private readonly Microsoft.AspNetCore.DataProtection.IDataProtectionProvider _dataProtection;

    public ScopedKeystoreService(Microsoft.AspNetCore.DataProtection.IDataProtectionProvider dataProtection)
    {
        _dataProtection = dataProtection;
    }
    private string? _plainPrivateKey;

    public bool IsUnlocked => !string.IsNullOrEmpty(_plainPrivateKey);

    public event Action? OnUnlocked;
    public event Action? OnLocked;

    public void Unlock(string plainPrivateKey)
    {
        _plainPrivateKey = plainPrivateKey;
        OnUnlocked?.Invoke();
    }

    public void Lock()
    {
        _plainPrivateKey = null;
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
        Microsoft.AspNetCore.Http.HttpContext? httpContext,
        string employeeId,
        string encryptedPrivateKey,
        ICryptoService crypto,
        GlobalKeystoreService? globalKeystore = null)
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

            var kek = crypto.DeriveKeyFromPassword(pwd, employeeId);
            var plainKey = crypto.DecryptAes(encryptedPrivateKey, kek);

            Unlock(plainKey);

            if (globalKeystore != null && !globalKeystore.IsUserUnlocked(employeeId))
            {
                globalKeystore.UnlockVault(employeeId, encryptedPrivateKey, pwd, crypto);
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
    /// Optionally unlocks GlobalKeystoreService for background push notification decryption.
    /// </summary>
    public async Task InitializeFromDeviceCookieAsync(IJSRuntime js, string employeeId, string encryptedPrivateKey, ICryptoService crypto, GlobalKeystoreService? globalKeystore = null)
    {
        if (IsUnlocked) return;

        try
        {
            var response = await js.InvokeAsync<System.Text.Json.JsonElement>("spokesVault.getStoredVaultKey");
            if (response.ValueKind != System.Text.Json.JsonValueKind.Null && response.TryGetProperty("key", out var keyProp))
            {
                var encryptedPwd = keyProp.GetString();
                if (!string.IsNullOrEmpty(encryptedPwd))
                {
                    // Decrypt the token securely from the server side
                    var protector = _dataProtection.CreateProtector("ChatVaultKey");
                    var pwd = protector.Unprotect(encryptedPwd);

                    // Derive and decrypt to prove validity
                    var kek = crypto.DeriveKeyFromPassword(pwd, employeeId);
                    var plainKey = crypto.DecryptAes(encryptedPrivateKey, kek);

                    Unlock(plainKey);

                    // Also unlock GlobalKeystoreService for background push notification decryption.
                    // On mobile, the SSR cookie-based unlock in App.razor doesn't fire,
                    // so this is the primary unlock path for background services.
                    if (globalKeystore != null && !globalKeystore.IsUserUnlocked(employeeId))
                    {
                        globalKeystore.UnlockVault(employeeId, encryptedPrivateKey, pwd, crypto);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            // Silently swallow; if cookie is invalid/missing, vault stays locked.
            System.Diagnostics.Debug.WriteLine($"InitializeFromDeviceCookie failed: {ex.Message}");
        }
    }

    public async Task ClearDeviceCookieAsync(Microsoft.JSInterop.IJSRuntime js, string employeeId)
    {
        try
        {
            await js.InvokeVoidAsync("spokesVault.clearStoredVaultKey");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"ClearDeviceCookieAsync failed: {ex.Message}");
        }
    }
}
