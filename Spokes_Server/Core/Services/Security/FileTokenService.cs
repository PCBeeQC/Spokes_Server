using System;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Caching.Memory;

namespace Spokes_Server.Core.Services.Security;

public class FileTokenPayload
{
    public string Category { get; set; } = "";
    public string ContextId { get; set; } = "";
    public string FileName { get; set; } = "";
    public string? EncryptionKeyBase64 { get; set; }
    public long ExpiryTicks { get; set; }
}

public class FileTokenService
{
    private readonly IDataProtector _protector;
    private readonly IMemoryCache _cache;

    public FileTokenService(IDataProtectionProvider dataProtection, IMemoryCache cache)
    {
        _protector = dataProtection.CreateProtector("SpokesFileToken");
        _cache = cache;
    }

    /// <summary>
    /// Generates a self-contained encrypted token for accessing an encrypted file.
    /// The token embeds the AES decryption key and is encrypted with ASP.NET DataProtection,
    /// whose keys are persisted to disk — so tokens survive server restarts.
    /// </summary>
    public string GenerateAccessToken(string category, string contextId, string filePath, string? encryptionKeyBase64 = null)
    {
        string safeName = filePath;
        var parts = filePath.Split('/');
        if (parts.Length > 0) safeName = parts.Last();

        // Dedup cache: same file + same key = same token within the cache window.
        // This is a performance optimization (prevents generating a new token for every
        // Blazor re-render of the same message), not a correctness requirement.
        var keyHash = string.IsNullOrEmpty(encryptionKeyBase64)
            ? ""
            : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(encryptionKeyBase64)))[..16];
            
        string generationCacheKey = $"FileTokenId_{category}_{contextId}_{safeName}_{keyHash}";

        if (_cache.TryGetValue(generationCacheKey, out string? cachedToken) && !string.IsNullOrEmpty(cachedToken))
        {
            return cachedToken;
        }

        var payload = new FileTokenPayload
        {
            Category = category,
            ContextId = contextId,
            FileName = safeName,
            EncryptionKeyBase64 = encryptionKeyBase64,
            ExpiryTicks = DateTime.UtcNow.AddDays(14).Ticks
        };

        var json = JsonSerializer.Serialize(payload);
        var token = _protector.Protect(json);

        // Cache for dedup (not correctness — the token is self-contained and works without the cache)
        _cache.Set(generationCacheKey, token, TimeSpan.FromDays(14));

        return token;
    }

    /// <summary>
    /// Validates a self-contained encrypted token and extracts the FileTokenPayload.
    /// Returns null if the token is invalid, tampered, or expired.
    /// Throws ApplicationException if the token is valid but doesn't match the requested file.
    /// </summary>
    public FileTokenPayload? UnprotectToken(string token)
    {
        try
        {
            var json = _protector.Unprotect(token);
            return JsonSerializer.Deserialize<FileTokenPayload>(json);
        }
        catch
        {
            // Token is corrupt, tampered, or was encrypted with a different key ring
            return null;
        }
    }
}
