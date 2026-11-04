using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Caching.Memory;
using Spokes_Server.Core.Data.Repositories.Core;

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
    public static readonly TimeSpan DefaultEncryptedTokenLifetime = TimeSpan.FromHours(2);
    public static readonly TimeSpan DefaultUnencryptedTokenLifetime = TimeSpan.FromDays(1);

    private readonly IDataProtector _protector;
    private readonly IMemoryCache _cache;
    private readonly SystemConfigRepository? _systemConfigs;
    private readonly object _persistLock = new();
    private readonly ConcurrentDictionary<string, long> _userInvalidatedAtSequence = new();
    private readonly ConcurrentDictionary<string, long> _contextInvalidatedAtSequence = new();
    private long _sequence = DateTime.UtcNow.Ticks;
    private long _globalInvalidatedAtSequence;
    private long _invalidationVersion;

    public FileTokenService(
        IDataProtectionProvider dataProtection,
        IMemoryCache cache,
        SystemConfigRepository? systemConfigs = null)
    {
        _protector = dataProtection.CreateProtector("SpokesFileToken");
        _cache = cache;
        _systemConfigs = systemConfigs;

        if (_systemConfigs != null)
        {
            var config = _systemConfigs.Get();
            if (config != null)
            {
                _globalInvalidatedAtSequence = config.GlobalFileTokenInvalidatedAtSequence;
                long maxPersisted = _globalInvalidatedAtSequence;
                if (config.UserFileTokenInvalidatedAtSequence != null)
                {
                    foreach (var kvp in config.UserFileTokenInvalidatedAtSequence)
                    {
                        _userInvalidatedAtSequence[kvp.Key] = kvp.Value;
                        if (kvp.Value > maxPersisted)
                        {
                            maxPersisted = kvp.Value;
                        }
                    }
                }

                if (maxPersisted >= _sequence)
                {
                    _sequence = maxPersisted + 1;
                }
            }
        }
    }

    /// <summary>
    /// Invalidates all previously generated file access tokens (both encrypted and unencrypted) for the specified user.
    /// </summary>
    public void InvalidateUserTokens(string userId)
    {
        if (!string.IsNullOrWhiteSpace(userId))
        {
            InvalidateUserEncryptedTokens(userId);
        }
    }

    /// <summary>
    /// Invalidates all previously generated file access tokens for a specific category/context (e.g., channel or album).
    /// </summary>
    public void InvalidateContextTokens(string category, string contextId)
    {
        if (string.IsNullOrWhiteSpace(category) || string.IsNullOrWhiteSpace(contextId))
        {
            return;
        }

        long cutOffSequence = Interlocked.Increment(ref _sequence);
        Interlocked.Increment(ref _invalidationVersion);
        _contextInvalidatedAtSequence[$"{category}:{contextId}"] = cutOffSequence;
    }

    /// <summary>
    /// Invalidates all previously generated file tokens for the specified user
    /// (or globally across all users when <paramref name="userId"/> is null/empty).
    /// Called when a user locks their Secure Vault, revokes a session, or removes their saved device vault key.
    /// Persists the revocation watermark to SystemConfig so revocations survive server restarts.
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

        if (_systemConfigs != null)
        {
            lock (_persistLock)
            {
                var config = _systemConfigs.Get();
                if (config != null)
                {
                    config.GlobalFileTokenInvalidatedAtSequence = Interlocked.Read(ref _globalInvalidatedAtSequence);
                    config.UserFileTokenInvalidatedAtSequence ??= new Dictionary<string, long>();
                    if (!string.IsNullOrEmpty(userId))
                    {
                        config.UserFileTokenInvalidatedAtSequence[userId] = cutOffSequence;
                    }
                    _systemConfigs.Save(config);
                }
            }
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
        if (string.IsNullOrWhiteSpace(userId))
        {
            throw new ArgumentException("A non-empty userId is required to generate a file access token.", nameof(userId));
        }

        if (string.IsNullOrWhiteSpace(sessionId))
        {
            throw new ArgumentException("A non-empty sessionId is required to generate a file access token.", nameof(sessionId));
        }

        int lastSlash = filePath.LastIndexOf('/');
        string safeName = lastSlash >= 0 ? filePath[(lastSlash + 1)..] : filePath;

        // Dedup cache: same file + same key + same user + same session = same token within the cache window.
        // This is a performance optimization (prevents generating a new token for every
        // Blazor re-render of the same message), not a correctness requirement.
        var keyHash = string.IsNullOrEmpty(encryptionKeyBase64)
            ? string.Empty
            : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(encryptionKeyBase64)))[..16];

        var userPart = $"_{userId}";
        var sessionPart = $"_{sessionId}";
        var invPart = $"_v{Interlocked.Read(ref _invalidationVersion)}";
        string generationCacheKey = $"FileTokenId_{category}_{contextId}_{safeName}_{keyHash}{userPart}{sessionPart}{invPart}";

        if (_cache.TryGetValue(generationCacheKey, out string? cachedToken) && !string.IsNullOrEmpty(cachedToken))
        {
            return cachedToken;
        }

        var lifetime = string.IsNullOrEmpty(encryptionKeyBase64)
            ? DefaultUnencryptedTokenLifetime
            : DefaultEncryptedTokenLifetime;

        var payload = new FileTokenPayload
        {
            Category = category,
            ContextId = contextId,
            FileName = safeName,
            EncryptionKeyBase64 = encryptionKeyBase64,
            ExpiryTicks = DateTime.UtcNow.Add(lifetime).Ticks,
            UserId = userId,
            SessionId = sessionId,
            TokenSequence = Interlocked.Increment(ref _sequence)
        };

        var json = JsonSerializer.Serialize(payload);
        var token = _protector.Protect(json);

        // Cache for dedup (not correctness — the token is self-contained and works without the cache)
        _cache.Set(generationCacheKey, token, lifetime);

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

        if (string.IsNullOrWhiteSpace(requestingUserId) || string.IsNullOrWhiteSpace(requestingSessionId))
        {
            return FileTokenValidationResult.Invalid();
        }

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

        // Mandatory user and session identity binding
        if (string.IsNullOrWhiteSpace(payload.UserId) ||
            !string.Equals(payload.UserId, requestingUserId, StringComparison.Ordinal))
        {
            return FileTokenValidationResult.Invalid();
        }

        if (string.IsNullOrWhiteSpace(payload.SessionId) ||
            !string.Equals(payload.SessionId, requestingSessionId, StringComparison.Ordinal))
        {
            return FileTokenValidationResult.Invalid();
        }

        // Apply global, per-user, and per-context revocation cutoffs to ALL tokens (encrypted and unencrypted)
        long globalCutOff = Interlocked.Read(ref _globalInvalidatedAtSequence);
        if (globalCutOff > 0 && payload.TokenSequence <= globalCutOff)
        {
            return FileTokenValidationResult.Invalid();
        }

        if (_userInvalidatedAtSequence.TryGetValue(payload.UserId, out long userCutOff) &&
            payload.TokenSequence <= userCutOff)
        {
            return FileTokenValidationResult.Invalid();
        }

        if (_contextInvalidatedAtSequence.TryGetValue($"{category}:{contextId}", out long contextCutOff) &&
            payload.TokenSequence <= contextCutOff)
        {
            return FileTokenValidationResult.Invalid();
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
