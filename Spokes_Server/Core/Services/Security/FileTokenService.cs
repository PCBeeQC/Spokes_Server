using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Caching.Memory;

namespace Spokes_Server.Core.Services.Security;

public class FileTokenPayload
{
    public string Category { get; set; } = string.Empty;
    public string ContextId { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public string? EncryptionKeyBase64 { get; set; }
    public long ExpiryTicks { get; set; }
    public string? UserId { get; set; }
    public string? SessionId { get; set; }
    public long TokenSequence { get; set; }
}

public class FileTokenService
{
    private readonly IDataProtector _protector;
    private readonly IMemoryCache _cache;
    private readonly ConcurrentDictionary<string, long> _userInvalidatedAtSequence = new();
    private long _sequence = DateTime.UtcNow.Ticks;
    private long _globalInvalidatedAtSequence;
    private long _invalidationVersion;

    public FileTokenService(IDataProtectionProvider dataProtection, IMemoryCache cache)
    {
        _protector = dataProtection.CreateProtector("SpokesFileToken");
        _cache = cache;
    }

    /// <summary>
    /// Invalidates all previously generated encrypted file tokens for the specified user
    /// (or globally across all users when <paramref name="userId"/> is null/empty).
    /// Called when a user locks their Secure Vault or removes their saved device vault key.
    /// </summary>
    public void InvalidateUserEncryptedTokens(string? userId = null)
    {
        long cutOffSequence = Interlocked.Increment(ref _sequence);
        Interlocked.Increment(ref _invalidationVersion);

        if (string.IsNullOrEmpty(userId))
        {
            Interlocked.Exchange(ref _globalInvalidatedAtSequence, cutOffSequence);
        }
        else
        {
            _userInvalidatedAtSequence[userId] = cutOffSequence;
        }
    }

    /// <summary>
    /// Generates a self-contained encrypted token for accessing an encrypted file.
    /// The token embeds the AES decryption key and is encrypted with ASP.NET DataProtection,
    /// whose keys are persisted to disk — so tokens survive server restarts.
    /// Binds to the requesting user ID and session ID to prevent cross-user/cross-session decryption bypass.
    /// </summary>
    public string GenerateAccessToken(
        string category,
        string contextId,
        string filePath,
        string? encryptionKeyBase64 = null,
        string? userId = null,
        string? sessionId = null)
    {
        int lastSlash = filePath.LastIndexOf('/');
        string safeName = lastSlash >= 0 ? filePath[(lastSlash + 1)..] : filePath;

        // Dedup cache: same file + same key + same user + same session = same token within the cache window.
        // This is a performance optimization (prevents generating a new token for every
        // Blazor re-render of the same message), not a correctness requirement.
        var keyHash = string.IsNullOrEmpty(encryptionKeyBase64)
            ? string.Empty
            : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(encryptionKeyBase64)))[..16];

        var userPart = string.IsNullOrEmpty(userId) ? "" : $"_{userId}";
        var sessionPart = string.IsNullOrEmpty(sessionId) ? "" : $"_{sessionId}";
        var invPart = string.IsNullOrEmpty(encryptionKeyBase64) ? "" : $"_v{Interlocked.Read(ref _invalidationVersion)}";
        string generationCacheKey = $"FileTokenId_{category}_{contextId}_{safeName}_{keyHash}{userPart}{sessionPart}{invPart}";

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
            ExpiryTicks = DateTime.UtcNow.AddDays(14).Ticks,
            UserId = userId,
            SessionId = sessionId,
            TokenSequence = Interlocked.Increment(ref _sequence)
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

    /// <summary>
    /// Validates a self-contained DataProtection-encrypted token and extracts the AES encryption key.
    /// Does NOT perform authentication — that's handled by caller/auth middleware.
    /// Validates user and session identity if bound into the token payload.
    /// Returns a structured validation result.
    /// </summary>
    public FileTokenValidationResult ValidateAccessToken(
        string category,
        string contextId,
        string fileName,
        string? token,
        string? requestingUserId = null,
        string? requestingSessionId = null)
    {
        if (string.IsNullOrEmpty(token)) return FileTokenValidationResult.None();

        var payload = UnprotectToken(token);
        if (payload == null)
        {
            return FileTokenValidationResult.Invalid();
        }

        // Validate token matches this specific file (or wildcard for uploads)
        if (payload.Category != category || payload.ContextId != contextId || (payload.FileName != fileName && fileName != "*" && payload.FileName != "*"))
        {
            return FileTokenValidationResult.Invalid();
        }

        // Check expiry
        if (DateTime.UtcNow.Ticks > payload.ExpiryTicks)
        {
            return FileTokenValidationResult.Expired();
        }

        // Validate user identity binding if present in token
        if (!string.IsNullOrEmpty(payload.UserId))
        {
            if (string.IsNullOrEmpty(requestingUserId) || !string.Equals(payload.UserId, requestingUserId, StringComparison.Ordinal))
            {
                return FileTokenValidationResult.Invalid();
            }
        }

        // Validate session identity binding if present in token
        if (!string.IsNullOrEmpty(payload.SessionId))
        {
            if (string.IsNullOrEmpty(requestingSessionId) || !string.Equals(payload.SessionId, requestingSessionId, StringComparison.Ordinal))
            {
                return FileTokenValidationResult.Invalid();
            }
        }

        // Check if encrypted tokens have been invalidated due to a vault lock
        if (!string.IsNullOrEmpty(payload.EncryptionKeyBase64))
        {
            long globalCutOff = Interlocked.Read(ref _globalInvalidatedAtSequence);
            if (globalCutOff > 0 && payload.TokenSequence <= globalCutOff)
            {
                return FileTokenValidationResult.Invalid();
            }

            var effectiveUserId = !string.IsNullOrEmpty(payload.UserId) ? payload.UserId : requestingUserId;
            if (!string.IsNullOrEmpty(effectiveUserId) &&
                _userInvalidatedAtSequence.TryGetValue(effectiveUserId, out long userCutOff) &&
                payload.TokenSequence <= userCutOff)
            {
                return FileTokenValidationResult.Invalid();
            }
        }

        // Return the AES key (may be null for non-encrypted files)
        return FileTokenValidationResult.Success(payload.EncryptionKeyBase64);
    }
}

public enum FileTokenStatus
{
    Valid,
    None,
    Invalid,
    Expired
}

public readonly record struct FileTokenValidationResult(FileTokenStatus Status, string? EncryptionKey)
{
    public bool IsSuccess => Status == FileTokenStatus.Valid || Status == FileTokenStatus.None;
    public static FileTokenValidationResult Success(string? key) => new(FileTokenStatus.Valid, key);
    public static FileTokenValidationResult None() => new(FileTokenStatus.None, null);
    public static FileTokenValidationResult Invalid() => new(FileTokenStatus.Invalid, null);
    public static FileTokenValidationResult Expired() => new(FileTokenStatus.Expired, null);
}
