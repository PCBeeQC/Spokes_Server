using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Spokes_Server.Core.Services.Security;

namespace Spokes_Server.Core.Security;

internal sealed record VaultCookiePayload(
    string EmployeeId,
    string SessionId,
    string VaultNonce,
    string Password);

internal sealed record VaultUnprotectResult(
    VaultCookiePayload Payload,
    bool NeedsRenewal);

/// <summary>
/// Centralizes DataProtection wrapping, verification, sliding renewal evaluation, and HttpOnly cookie options
/// for persisted chat vault and refresh security cookies.
/// </summary>
internal static class VaultCookieProtector
{
    public const string ProtectorPurpose = "ChatVaultKey.v2";
    public const string LegacyProtectorPurpose = "ChatVaultKey";
    public const string VaultCookieName = "chat_vault_key";
    public const string RefreshCookieName = "Spokes_Refresh";
    public static readonly TimeSpan CookieLifetime = TimeSpan.FromDays(90);
    public static readonly TimeSpan SlidingRenewalWindow = TimeSpan.FromDays(45);

    public static CookieOptions CreateVaultCookieOptions(DateTimeOffset? expires = null) => new()
    {
        Path = "/",
        HttpOnly = true,
        Secure = true,
        SameSite = SameSiteMode.Lax,
        Expires = expires
    };

    public static CookieOptions CreateRefreshCookieOptions(bool isHttps, DateTimeOffset? expires = null) => new()
    {
        Path = "/",
        HttpOnly = true,
        Secure = isHttps,
        SameSite = SameSiteMode.Lax,
        Expires = expires
    };

    public static void AppendVaultCookie(HttpResponse response, string protectedValue) =>
        response.Cookies.Append(VaultCookieName, protectedValue, CreateVaultCookieOptions(DateTimeOffset.UtcNow.Add(CookieLifetime)));

    public static void DeleteVaultCookie(HttpResponse response) =>
        response.Cookies.Delete(VaultCookieName, CreateVaultCookieOptions());

    public static void DeleteRefreshCookie(HttpContext context) =>
        context.Response.Cookies.Delete(RefreshCookieName, CreateRefreshCookieOptions(context.Request.IsHttps));

    public static string Protect(
        IDataProtectionProvider dataProtection,
        string employeeId,
        string sessionId,
        string vaultNonce,
        string password,
        TimeSpan? lifetime = null)
    {
        ArgumentNullException.ThrowIfNull(dataProtection);

        var protector = dataProtection
            .CreateProtector(ProtectorPurpose)
            .ToTimeLimitedDataProtector();

        var payload = new VaultCookiePayload(employeeId, sessionId, vaultNonce, password);
        return protector.Protect(JsonSerializer.Serialize(payload), lifetime ?? CookieLifetime);
    }

    public static VaultUnprotectResult? TryUnprotectAndValidate(
        IDataProtectionProvider dataProtection,
        string protectedValue,
        string expectedEmployeeId,
        string expectedSessionId,
        string expectedVaultNonce,
        DateTimeOffset? utcNow = null)
    {
        if (string.IsNullOrEmpty(protectedValue) ||
            string.IsNullOrEmpty(expectedEmployeeId) ||
            string.IsNullOrEmpty(expectedSessionId) ||
            string.IsNullOrEmpty(expectedVaultNonce))
        {
            return null;
        }

        var protector = dataProtection
            .CreateProtector(ProtectorPurpose)
            .ToTimeLimitedDataProtector();

        var json = protector.Unprotect(protectedValue, out var expiration);
        var payload = JsonSerializer.Deserialize<VaultCookiePayload>(json);
        if (payload == null ||
            string.IsNullOrEmpty(payload.Password) ||
            !string.Equals(payload.EmployeeId, expectedEmployeeId, StringComparison.Ordinal) ||
            !string.Equals(payload.SessionId, expectedSessionId, StringComparison.Ordinal) ||
            !string.Equals(payload.VaultNonce, expectedVaultNonce, StringComparison.Ordinal))
        {
            return null;
        }

        var now = utcNow ?? DateTimeOffset.UtcNow;
        var needsRenewal = (expiration - now) <= SlidingRenewalWindow;
        return new VaultUnprotectResult(payload, needsRenewal);
    }

    public static VaultCookiePayload? UnprotectAndValidate(
        IDataProtectionProvider dataProtection,
        string protectedValue,
        string expectedEmployeeId,
        string expectedSessionId,
        string expectedVaultNonce) =>
        TryUnprotectAndValidate(
            dataProtection,
            protectedValue,
            expectedEmployeeId,
            expectedSessionId,
            expectedVaultNonce)?.Payload;

    /// <summary>
    /// Read-only decoder for pre-v2 ("ChatVaultKey") tokens during one-time upgrade of legacy
    /// server-v2026.11.0 sessions whose persisted DeviceSession has HasVaultCookie == true and no VaultNonce.
    /// Callers must never invoke this when DeviceSession.VaultNonce is already non-empty.
    /// </summary>
    public static string? TryUnprotectLegacy(
        IDataProtectionProvider dataProtection,
        string protectedValue)
    {
        if (string.IsNullOrEmpty(protectedValue))
        {
            return null;
        }

        var legacyProtector = dataProtection.CreateProtector(LegacyProtectorPurpose);
        var password = legacyProtector.Unprotect(protectedValue);
        return string.IsNullOrEmpty(password) ? null : password;
    }

    /// <summary>
    /// Proves that <paramref name="password"/> decrypts <paramref name="encryptedPrivateKey"/> into a valid
    /// RSA private key XML document for <paramref name="employeeId"/>.
    /// </summary>
    public static bool TryVerifyVaultPassword(
        ICryptoService crypto,
        string employeeId,
        string encryptedPrivateKey,
        string password,
        out string plainPrivateKey)
    {
        plainPrivateKey = string.Empty;
        if (crypto == null ||
            string.IsNullOrEmpty(employeeId) ||
            string.IsNullOrEmpty(encryptedPrivateKey) ||
            string.IsNullOrEmpty(password))
        {
            return false;
        }

        try
        {
            var kek = crypto.DeriveKeyFromPassword(password, employeeId);
            var decrypted = crypto.DecryptAes(encryptedPrivateKey, kek);
            if (string.IsNullOrEmpty(decrypted) || !decrypted.Contains("<RSAKeyValue>", StringComparison.Ordinal))
            {
                return false;
            }

            plainPrivateKey = decrypted;
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
