using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Caching.Memory;
using Spokes_Server.Core.Data.Repositories.Core;

namespace Spokes_Server.Core.Services.Security;

public class FileTokenPayload
{
    [JsonPropertyName("c")]
    public string Category { get; set; } = string.Empty;
    [JsonPropertyName("x")]
    public string ContextId { get; set; } = string.Empty;
    [JsonPropertyName("f")]
    public string FileName { get; set; } = string.Empty;
    [JsonPropertyName("k")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? EncryptionKeyBase64 { get; set; }
    [JsonPropertyName("t")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool IsThumbnail { get; set; }
    [JsonPropertyName("e")]
    public long ExpiryTicks { get; set; }
    [JsonPropertyName("u")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? UserId { get; set; }
    [JsonPropertyName("s")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SessionId { get; set; }
    [JsonPropertyName("q")]
    public long TokenSequence { get; set; }
}

public class FileTokenService
{
    public static readonly TimeSpan DefaultEncryptedTokenLifetime = TimeSpan.FromHours(2);
    public static readonly TimeSpan DefaultEncryptedDedupLifetime = TimeSpan.FromMinutes(105);
    public static readonly TimeSpan DefaultUnencryptedTokenLifetime = TimeSpan.FromDays(1);
    public static readonly TimeSpan DefaultUnencryptedDedupLifetime = TimeSpan.FromHours(23);
    public static readonly TimeSpan DefaultUploadReceiptLifetime = TimeSpan.FromHours(2);

    /// <summary>
    /// Computes the canonical client-side IndexedDB media cache scope identifier for an employee ID.
    /// Single source of truth shared by <c>App.razor</c> and <c>MobileAuthController</c>.
    /// </summary>
    public static string? ComputeUserMediaScope(string? employeeId)
    {
        if (string.IsNullOrEmpty(employeeId))
        {
            return null;
        }

        var hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes(employeeId));
        return Convert.ToHexString(hashBytes, 0, 8).ToLowerInvariant();
    }

    private sealed record UploadReceiptPayload(
        string Category,
        string ContextId,
        string SafeFileName,
        string UserId,
        string Nonce);

    private readonly IDataProtector _protector;
    private readonly ITimeLimitedDataProtector _uploadReceiptProtector;
    private readonly IMemoryCache _cache;
    private readonly SystemConfigRepository? _systemConfigs;
    private readonly object _persistLock = new();
    private readonly ConcurrentDictionary<string, long> _userInvalidatedAtSequence = new();
    private readonly ConcurrentDictionary<string, long> _contextInvalidatedAtSequence = new();
    private readonly ConcurrentDictionary<string, DateTimeOffset> _consumedUploadReceipts = new(StringComparer.Ordinal);
    private long _sequence = DateTime.UtcNow.Ticks;
    private long _globalInvalidatedAtSequence;

    public FileTokenService(
        IDataProtectionProvider dataProtection,
        IMemoryCache cache,
        SystemConfigRepository? systemConfigs = null)
    {
        _protector = dataProtection.CreateProtector("SpokesFileToken");
        _uploadReceiptProtector = dataProtection.CreateProtector("Spokes.UploadReceipt.v1").ToTimeLimitedDataProtector();
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
    /// Invalidates all previously generated file access tokens for a specific category/context (e.g., channel or album).
    /// </summary>
    public void InvalidateContextTokens(string category, string contextId)
    {
        if (string.IsNullOrWhiteSpace(category) || string.IsNullOrWhiteSpace(contextId))
        {
            return;
        }

        long cutOffSequence = Interlocked.Increment(ref _sequence);
        _contextInvalidatedAtSequence[$"{category}:{contextId}"] = cutOffSequence;
    }

    public virtual string GenerateUploadReceiptToken(
        string category,
        string contextId,
        string safeFileName,
        string userId)
    {
        if (!IsValidReceiptSegment(category) ||
            !IsValidReceiptSegment(contextId) ||
            !IsValidReceiptSegment(safeFileName) ||
            string.IsNullOrWhiteSpace(userId))
        {
            throw new ArgumentException("Invalid upload receipt parameters.");
        }

        var normalizedSafeName = Path.GetFileName(safeFileName);
        if (!string.Equals(normalizedSafeName, safeFileName, StringComparison.Ordinal))
        {
            throw new ArgumentException("Safe file name must be a single path segment.", nameof(safeFileName));
        }

        var payload = new UploadReceiptPayload(
            category,
            contextId,
            normalizedSafeName,
            userId,
            Convert.ToBase64String(RandomNumberGenerator.GetBytes(16)));

        var json = JsonSerializer.Serialize(payload);
        return _uploadReceiptProtector.Protect(json, DefaultUploadReceiptLifetime);
    }

    public virtual bool TryValidateUploadReceiptToken(
        string? token,
        string expectedCategory,
        string expectedContextId,
        string expectedUserId,
        out string safeFileName,
        bool consumeSingleUse = false)
    {
        safeFileName = string.Empty;

        if (string.IsNullOrWhiteSpace(token) ||
            !IsValidReceiptSegment(expectedCategory) ||
            !IsValidReceiptSegment(expectedContextId) ||
            string.IsNullOrWhiteSpace(expectedUserId))
        {
            return false;
        }

        try
        {
            var json = _uploadReceiptProtector.Unprotect(token, out var expiresAt);
            if (expiresAt <= DateTimeOffset.UtcNow)
            {
                return false;
            }

            var payload = JsonSerializer.Deserialize<UploadReceiptPayload>(json);
            if (payload == null ||
                !string.Equals(payload.Category, expectedCategory, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(payload.ContextId, expectedContextId, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(payload.UserId, expectedUserId, StringComparison.Ordinal) ||
                !IsValidReceiptSegment(payload.SafeFileName) ||
                string.IsNullOrWhiteSpace(payload.Nonce))
            {
                return false;
            }

            var normalizedSafeName = Path.GetFileName(payload.SafeFileName);
            if (!string.Equals(normalizedSafeName, payload.SafeFileName, StringComparison.Ordinal))
            {
                return false;
            }

            if (consumeSingleUse)
            {
                var now = DateTimeOffset.UtcNow;
                if (_consumedUploadReceipts.Count > 10_000)
                {
                    foreach (var entry in _consumedUploadReceipts)
                    {
                        if (entry.Value <= now)
                        {
                            _consumedUploadReceipts.TryRemove(entry.Key, out _);
                        }
                    }
                }

                if (!_consumedUploadReceipts.TryAdd(payload.Nonce, expiresAt))
                {
                    return false;
                }
            }

            safeFileName = normalizedSafeName;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsValidReceiptSegment(string? segment) =>
        !string.IsNullOrWhiteSpace(segment) &&
        !segment.Contains("..", StringComparison.Ordinal) &&
        !segment.Contains('/') &&
        !segment.Contains('\\') &&
        segment.IndexOfAny(Path.GetInvalidFileNameChars()) < 0;

    /// <summary>
    /// Invalidates all previously generated file tokens for the specified user
    /// (or globally across all users when <paramref name="userId"/> is null/empty).
    /// Called when a user locks their Secure Vault, revokes a session, or removes their saved device vault key.
    /// Persists the revocation watermark to SystemConfig so revocations survive server restarts.
    /// </summary>
    public void InvalidateUserEncryptedTokens(string? userId = null)
    {
        long cutOffSequence = Interlocked.Increment(ref _sequence);

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
    /// Binds to the requesting user ID, session ID, and thumbnail scope to prevent cross-user/cross-session/thumbnail-to-original escalation.
    /// </summary>
    public string GenerateAccessToken(
        string category,
        string contextId,
        string filePath,
        string? encryptionKeyBase64 = null,
        string? userId = null,
        string? sessionId = null,
        bool isThumbnail = false)
    {
        if (string.IsNullOrWhiteSpace(userId))
        {
            throw new ArgumentException("A non-empty userId is required to generate a file access token.", nameof(userId));
        }

        if (string.IsNullOrWhiteSpace(sessionId))
        {
            throw new ArgumentException("A non-empty sessionId is required to generate a file access token.", nameof(sessionId));
        }

        var cleanPath = filePath;
        int qIdx = cleanPath.IndexOf('?');
        if (qIdx >= 0) cleanPath = cleanPath[..qIdx];
        int hIdx = cleanPath.IndexOf('#');
        if (hIdx >= 0) cleanPath = cleanPath[..hIdx];
        int lastSlash = cleanPath.LastIndexOf('/');
        string safeName = lastSlash >= 0 ? cleanPath[(lastSlash + 1)..] : cleanPath;

        // Dedup cache: same file + same key + same user + same session + same thumb scope = same token within the cache window.
        // This is a performance optimization (prevents generating a new token for every
        // Blazor re-render of the same message), not a correctness requirement.
        var keyHash = string.IsNullOrEmpty(encryptionKeyBase64)
            ? string.Empty
            : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(encryptionKeyBase64)))[..16];

        var userPart = $"_{userId}";
        var sessionPart = $"_{sessionId}";
        var thumbPart = isThumbnail ? "_t1" : "_t0";
        long globalCutOff = Interlocked.Read(ref _globalInvalidatedAtSequence);
        long userCutOff = _userInvalidatedAtSequence.TryGetValue(userId, out var uCut) ? uCut : 0;
        long contextCutOff = _contextInvalidatedAtSequence.TryGetValue($"{category}:{contextId}", out var cCut) ? cCut : 0;
        long effectiveWatermark = Math.Max(globalCutOff, Math.Max(userCutOff, contextCutOff));
        string generationCacheKey = $"FileTokenId_{category}_{contextId}_{safeName}_{keyHash}{userPart}{sessionPart}{thumbPart}_w{effectiveWatermark}";

        if (_cache.TryGetValue(generationCacheKey, out string? cachedToken) && !string.IsNullOrEmpty(cachedToken))
        {
            return cachedToken;
        }

        bool isUnencrypted = string.IsNullOrEmpty(encryptionKeyBase64);
        var lifetime = isUnencrypted
            ? DefaultUnencryptedTokenLifetime
            : DefaultEncryptedTokenLifetime;
        var dedupLifetime = isUnencrypted
            ? DefaultUnencryptedDedupLifetime
            : DefaultEncryptedDedupLifetime;

        var payload = new FileTokenPayload
        {
            Category = category,
            ContextId = contextId,
            FileName = safeName,
            EncryptionKeyBase64 = encryptionKeyBase64,
            IsThumbnail = isThumbnail,
            ExpiryTicks = DateTime.UtcNow.Add(lifetime).Ticks,
            UserId = userId,
            SessionId = sessionId,
            TokenSequence = Interlocked.Increment(ref _sequence)
        };

        var payloadBytes = SerializeBinaryV2(payload);
        var token = WebEncoders.Base64UrlEncode(_protector.Protect(payloadBytes));

        // Cache for dedup with renewal safety margin so newly rendered URLs never emit an about-to-expire token.
        _cache.Set(generationCacheKey, token, dedupLifetime);

        return token;
    }

    /// <summary>
    /// Canonical single source of truth for building a tokenized media/file URL with optional display filename (<c>fn=</c>)
    /// and automatic thumbnail endpoint scope detection.
    /// </summary>
    public string BuildTokenizedUrl(
        string category,
        string? contextId,
        string? targetUrl,
        string? encryptionKeyBase64,
        string? userId,
        string? sessionId,
        bool isThumbnail = false,
        string? displayFileName = null,
        bool smallThumbnail = false)
    {
        if (string.IsNullOrWhiteSpace(targetUrl))
        {
            return string.Empty;
        }

        string url = targetUrl;
        bool isThumbEndpoint =
            targetUrl.StartsWith("/spokesapi/files/thumb/", StringComparison.OrdinalIgnoreCase) ||
            targetUrl.StartsWith("/spokesapi/attachments/thumb/", StringComparison.OrdinalIgnoreCase) ||
            targetUrl.StartsWith("/internal/attachments/thumb/", StringComparison.OrdinalIgnoreCase);

        if (!string.IsNullOrWhiteSpace(contextId) &&
            !string.IsNullOrWhiteSpace(userId) &&
            !string.IsNullOrWhiteSpace(sessionId))
        {
            bool effectiveIsThumbnail = isThumbnail && isThumbEndpoint;

            var token = GenerateAccessToken(
                category,
                contextId,
                targetUrl,
                encryptionKeyBase64,
                userId,
                sessionId,
                isThumbnail: effectiveIsThumbnail);

            url = targetUrl.Contains('?', StringComparison.Ordinal)
                ? $"{targetUrl}&t={token}"
                : $"{targetUrl}?t={token}";
        }

        if (smallThumbnail && isThumbEndpoint)
        {
            var sep = url.Contains('?', StringComparison.Ordinal) ? "&" : "?";
            url = $"{url}{sep}s=sm";
        }

        if (!string.IsNullOrWhiteSpace(displayFileName) &&
            url.StartsWith("/spokesapi/files/", StringComparison.OrdinalIgnoreCase))
        {
            var sep = url.Contains('?', StringComparison.Ordinal) ? "&" : "?";
            url = $"{url}{sep}fn={Uri.EscapeDataString(displayFileName)}";
        }

        return url;
    }

    /// <summary>
    /// Parses a strictly same-origin stored Spokes file URL into its tokenizable route components.
    /// Supports <c>/spokesapi/files/[thumb/]{category}/{contextId}/{fileName}</c> as well as legacy
    /// <c>/spokesapi/attachments/[thumb/]{channelId}/{fileName}</c> and <c>/internal/attachments/[thumb/]{channelId}/{fileName}</c>.
    /// Excludes non-tokenized special routes (<c>demo-media</c>, <c>email</c>, <c>thumbnail</c>) and external URLs.
    /// </summary>
    public static bool TryParseTokenizableFileUrl(
        string? rawUrl,
        out string category,
        out string contextId,
        out string fileName,
        out bool isThumbEndpoint)
    {
        category = string.Empty;
        contextId = string.Empty;
        fileName = string.Empty;
        isThumbEndpoint = false;

        if (string.IsNullOrWhiteSpace(rawUrl))
        {
            return false;
        }

        var trimmed = rawUrl.Trim();
        if (trimmed.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("//", StringComparison.Ordinal) ||
            trimmed.StartsWith(@"\\", StringComparison.Ordinal) ||
            trimmed.Contains('\\'))
        {
            return false;
        }

        int colonIdx = trimmed.IndexOf(':');
        int firstSlashIdx = trimmed.IndexOf('/');
        if (firstSlashIdx != 0 || (colonIdx >= 0 && colonIdx < firstSlashIdx))
        {
            return false;
        }

        string pathOnly = trimmed;
        int hashIdx = pathOnly.IndexOf('#');
        if (hashIdx >= 0) pathOnly = pathOnly[..hashIdx];
        int queryIdx = pathOnly.IndexOf('?');
        if (queryIdx >= 0) pathOnly = pathOnly[..queryIdx];

        const string filesThumbPrefix = "/spokesapi/files/thumb/";
        const string filesPrefix = "/spokesapi/files/";
        const string apiAttThumbPrefix = "/spokesapi/attachments/thumb/";
        const string apiAttPrefix = "/spokesapi/attachments/";
        const string intAttThumbPrefix = "/internal/attachments/thumb/";
        const string intAttPrefix = "/internal/attachments/";

        string parsedCategory;
        string parsedContextId;
        string parsedFileName;
        bool parsedThumb;

        if (pathOnly.StartsWith(filesThumbPrefix, StringComparison.OrdinalIgnoreCase))
        {
            var rest = pathOnly[filesThumbPrefix.Length..];
            var parts = rest.Split('/');
            if (parts.Length != 3) return false;
            parsedCategory = Uri.UnescapeDataString(parts[0]);
            if (parsedCategory.Equals("demo-media", StringComparison.OrdinalIgnoreCase)) return false;
            parsedContextId = Uri.UnescapeDataString(parts[1]);
            parsedFileName = Uri.UnescapeDataString(parts[2]);
            parsedThumb = true;
        }
        else if (pathOnly.StartsWith(filesPrefix, StringComparison.OrdinalIgnoreCase))
        {
            var rest = pathOnly[filesPrefix.Length..];
            var parts = rest.Split('/');
            if (parts.Length != 3) return false;
            parsedCategory = Uri.UnescapeDataString(parts[0]);
            if (parsedCategory.Equals("demo-media", StringComparison.OrdinalIgnoreCase) ||
                parsedCategory.Equals("email", StringComparison.OrdinalIgnoreCase) ||
                parsedCategory.Equals("thumbnail", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
            parsedContextId = Uri.UnescapeDataString(parts[1]);
            parsedFileName = Uri.UnescapeDataString(parts[2]);
            parsedThumb = false;
        }
        else if (pathOnly.StartsWith(apiAttThumbPrefix, StringComparison.OrdinalIgnoreCase) ||
                 pathOnly.StartsWith(intAttThumbPrefix, StringComparison.OrdinalIgnoreCase))
        {
            var prefixLen = pathOnly.StartsWith(apiAttThumbPrefix, StringComparison.OrdinalIgnoreCase)
                ? apiAttThumbPrefix.Length
                : intAttThumbPrefix.Length;
            var parts = pathOnly[prefixLen..].Split('/');
            if (parts.Length != 2) return false;
            parsedCategory = "chat";
            parsedContextId = Uri.UnescapeDataString(parts[0]);
            parsedFileName = Uri.UnescapeDataString(parts[1]);
            parsedThumb = true;
        }
        else if (pathOnly.StartsWith(apiAttPrefix, StringComparison.OrdinalIgnoreCase) ||
                 pathOnly.StartsWith(intAttPrefix, StringComparison.OrdinalIgnoreCase))
        {
            var prefixLen = pathOnly.StartsWith(apiAttPrefix, StringComparison.OrdinalIgnoreCase)
                ? apiAttPrefix.Length
                : intAttPrefix.Length;
            var parts = pathOnly[prefixLen..].Split('/');
            if (parts.Length != 2) return false;
            if (parts[0].Equals("email", StringComparison.OrdinalIgnoreCase)) return false;
            parsedCategory = "chat";
            parsedContextId = Uri.UnescapeDataString(parts[0]);
            parsedFileName = Uri.UnescapeDataString(parts[1]);
            parsedThumb = false;
        }
        else
        {
            return false;
        }

        if (!IsValidReceiptSegment(parsedCategory) ||
            !IsValidReceiptSegment(parsedContextId) ||
            !IsValidReceiptSegment(parsedFileName) ||
            parsedFileName is "." or "..")
        {
            return false;
        }

        category = parsedCategory;
        contextId = parsedContextId;
        fileName = parsedFileName;
        isThumbEndpoint = parsedThumb;
        return true;
    }

    /// <summary>
    /// Canonical helper for signing any stored Spokes file URL with a user+session+file-scoped access token.
    /// Non-Spokes or special URLs (data:, blob:, external URLs, /spokesapi/Media/, /spokesapi/files/email/, etc.)
    /// are returned unchanged. Replaces any stale <c>t=</c> parameter while preserving other query params and fragments.
    /// </summary>
    public string SignFileUrl(
        string? rawUrl,
        string? userId,
        string? sessionId,
        string? encryptionKeyBase64 = null,
        bool isThumbnail = false,
        string? displayFileName = null,
        bool smallThumbnail = false)
    {
        if (string.IsNullOrWhiteSpace(rawUrl))
        {
            return string.Empty;
        }

        if (string.IsNullOrWhiteSpace(userId) || string.IsNullOrWhiteSpace(sessionId))
        {
            return rawUrl;
        }

        if (!TryParseTokenizableFileUrl(rawUrl, out var category, out var contextId, out var fileName, out var isThumbEndpoint))
        {
            return rawUrl;
        }

        var trimmed = rawUrl.Trim();
        string fragment = string.Empty;
        int hashIdx = trimmed.IndexOf('#');
        if (hashIdx >= 0)
        {
            fragment = trimmed[hashIdx..];
            trimmed = trimmed[..hashIdx];
        }

        string pathOnly = trimmed;
        string existingQuery = string.Empty;
        int queryIdx = trimmed.IndexOf('?');
        if (queryIdx >= 0)
        {
            pathOnly = trimmed[..queryIdx];
            existingQuery = trimmed[(queryIdx + 1)..];
        }

        var keptParams = new List<string>();
        bool hasSmallParam = false;
        bool hasFnParam = false;
        if (!string.IsNullOrEmpty(existingQuery))
        {
            foreach (var part in existingQuery.Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                int eqIdx = part.IndexOf('=');
                var key = eqIdx >= 0 ? part[..eqIdx] : part;
                if (key.Equals("t", StringComparison.OrdinalIgnoreCase))
                {
                    continue; // Replace any stale access token
                }
                if (key.Equals("s", StringComparison.OrdinalIgnoreCase))
                {
                    hasSmallParam = true;
                }
                else if (key.Equals("fn", StringComparison.OrdinalIgnoreCase))
                {
                    hasFnParam = true;
                }
                keptParams.Add(part);
            }
        }

        bool effectiveIsThumbnail = (isThumbnail || isThumbEndpoint) && isThumbEndpoint;
        var token = GenerateAccessToken(
            category,
            contextId,
            fileName,
            encryptionKeyBase64,
            userId,
            sessionId,
            isThumbnail: effectiveIsThumbnail);

        keptParams.Add($"t={token}");

        if (smallThumbnail && isThumbEndpoint && !hasSmallParam)
        {
            keptParams.Add("s=sm");
        }

        if (!string.IsNullOrWhiteSpace(displayFileName) &&
            !hasFnParam &&
            pathOnly.StartsWith("/spokesapi/files/", StringComparison.OrdinalIgnoreCase))
        {
            keptParams.Add($"fn={Uri.EscapeDataString(displayFileName)}");
        }

        return $"{pathOnly}?{string.Join("&", keptParams)}{fragment}";
    }

    private const byte BinaryPayloadVersionV2 = 0x02;

    [Flags]
    private enum BinaryPayloadFlags : byte
    {
        None = 0,
        IsThumbnail = 1 << 0,
        HasKey = 1 << 1,
        ContextIdIsGuid = 1 << 2,
        UserIdIsGuid = 1 << 3,
        SessionIdIsGuid = 1 << 4,
        KeyIsRawBytes = 1 << 5
    }

    private static bool TryParseCanonicalLowercaseGuid(string? value, out Guid guid)
    {
        if (!string.IsNullOrEmpty(value) &&
            value.Length == 36 &&
            Guid.TryParseExact(value, "D", out guid))
        {
            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                if (c >= 'A' && c <= 'F')
                {
                    guid = default;
                    return false;
                }
            }
            return true;
        }

        guid = default;
        return false;
    }

    private static byte[] SerializeBinaryV2(FileTokenPayload payload)
    {
        bool ctxIsGuid = TryParseCanonicalLowercaseGuid(payload.ContextId, out var ctxGuid);
        bool userIsGuid = TryParseCanonicalLowercaseGuid(payload.UserId, out var userGuid);
        bool sessIsGuid = TryParseCanonicalLowercaseGuid(payload.SessionId, out var sessGuid);

        bool hasKey = !string.IsNullOrEmpty(payload.EncryptionKeyBase64);
        byte[]? rawKeyBytes = null;
        bool keyIsRawBytes = false;
        if (hasKey)
        {
            var maxDecodedLen = ((payload.EncryptionKeyBase64!.Length + 3) / 4) * 3;
            var candidate = new byte[maxDecodedLen];
            if (Convert.TryFromBase64String(payload.EncryptionKeyBase64, candidate, out int written) &&
                written > 0 &&
                string.Equals(Convert.ToBase64String(candidate, 0, written), payload.EncryptionKeyBase64, StringComparison.Ordinal))
            {
                rawKeyBytes = candidate[..written];
                keyIsRawBytes = true;
            }
        }

        var flags = BinaryPayloadFlags.None;
        if (payload.IsThumbnail) flags |= BinaryPayloadFlags.IsThumbnail;
        if (hasKey) flags |= BinaryPayloadFlags.HasKey;
        if (ctxIsGuid) flags |= BinaryPayloadFlags.ContextIdIsGuid;
        if (userIsGuid) flags |= BinaryPayloadFlags.UserIdIsGuid;
        if (sessIsGuid) flags |= BinaryPayloadFlags.SessionIdIsGuid;
        if (keyIsRawBytes) flags |= BinaryPayloadFlags.KeyIsRawBytes;

        var writer = new ArrayBufferWriter<byte>(160);
        var header = writer.GetSpan(18);
        header[0] = BinaryPayloadVersionV2;
        header[1] = (byte)flags;
        BinaryPrimitives.WriteInt64LittleEndian(header.Slice(2, 8), payload.ExpiryTicks);
        BinaryPrimitives.WriteInt64LittleEndian(header.Slice(10, 8), payload.TokenSequence);
        writer.Advance(18);

        WriteUtf8Field(writer, payload.Category);
        if (ctxIsGuid) WriteGuidField(writer, ctxGuid); else WriteUtf8Field(writer, payload.ContextId);
        WriteUtf8Field(writer, payload.FileName);
        if (userIsGuid) WriteGuidField(writer, userGuid); else WriteUtf8Field(writer, payload.UserId ?? string.Empty);
        if (sessIsGuid) WriteGuidField(writer, sessGuid); else WriteUtf8Field(writer, payload.SessionId ?? string.Empty);

        if (hasKey)
        {
            if (keyIsRawBytes && rawKeyBytes != null)
            {
                WriteBytesField(writer, rawKeyBytes);
            }
            else
            {
                WriteUtf8Field(writer, payload.EncryptionKeyBase64!);
            }
        }

        return writer.WrittenSpan.ToArray();
    }

    private static FileTokenPayload? DeserializeBinaryV2(ReadOnlySpan<byte> data)
    {
        if (data.Length < 18 || data[0] != BinaryPayloadVersionV2)
        {
            return null;
        }

        var flags = (BinaryPayloadFlags)data[1];
        long expiryTicks = BinaryPrimitives.ReadInt64LittleEndian(data.Slice(2, 8));
        long tokenSequence = BinaryPrimitives.ReadInt64LittleEndian(data.Slice(10, 8));
        int offset = 18;

        if (!TryReadUtf8Field(data, ref offset, out var category)) return null;
        string contextId;
        if (flags.HasFlag(BinaryPayloadFlags.ContextIdIsGuid))
        {
            if (!TryReadGuidField(data, ref offset, out contextId)) return null;
        }
        else if (!TryReadUtf8Field(data, ref offset, out contextId))
        {
            return null;
        }

        if (!TryReadUtf8Field(data, ref offset, out var fileName)) return null;

        string userId;
        if (flags.HasFlag(BinaryPayloadFlags.UserIdIsGuid))
        {
            if (!TryReadGuidField(data, ref offset, out userId)) return null;
        }
        else if (!TryReadUtf8Field(data, ref offset, out userId))
        {
            return null;
        }

        string sessionId;
        if (flags.HasFlag(BinaryPayloadFlags.SessionIdIsGuid))
        {
            if (!TryReadGuidField(data, ref offset, out sessionId)) return null;
        }
        else if (!TryReadUtf8Field(data, ref offset, out sessionId))
        {
            return null;
        }

        string? encryptionKeyBase64 = null;
        if (flags.HasFlag(BinaryPayloadFlags.HasKey))
        {
            if (flags.HasFlag(BinaryPayloadFlags.KeyIsRawBytes))
            {
                if (!TryReadBytesField(data, ref offset, out var keySpan)) return null;
                encryptionKeyBase64 = Convert.ToBase64String(keySpan);
            }
            else
            {
                if (!TryReadUtf8Field(data, ref offset, out var keyStr)) return null;
                encryptionKeyBase64 = keyStr;
            }
        }

        if (offset != data.Length)
        {
            return null;
        }

        return new FileTokenPayload
        {
            Category = category,
            ContextId = contextId,
            FileName = fileName,
            EncryptionKeyBase64 = encryptionKeyBase64,
            IsThumbnail = flags.HasFlag(BinaryPayloadFlags.IsThumbnail),
            ExpiryTicks = expiryTicks,
            UserId = string.IsNullOrEmpty(userId) ? null : userId,
            SessionId = string.IsNullOrEmpty(sessionId) ? null : sessionId,
            TokenSequence = tokenSequence
        };
    }

    private static void WriteGuidField(ArrayBufferWriter<byte> writer, Guid guid)
    {
        var span = writer.GetSpan(16);
        guid.TryWriteBytes(span);
        writer.Advance(16);
    }

    private static void WriteUtf8Field(ArrayBufferWriter<byte> writer, string value)
    {
        var byteCount = Encoding.UTF8.GetByteCount(value);
        if (byteCount > ushort.MaxValue)
        {
            throw new ArgumentException("File token field exceeds maximum supported length.");
        }
        var span = writer.GetSpan(2 + byteCount);
        BinaryPrimitives.WriteUInt16LittleEndian(span, (ushort)byteCount);
        Encoding.UTF8.GetBytes(value, span.Slice(2, byteCount));
        writer.Advance(2 + byteCount);
    }

    private static void WriteBytesField(ArrayBufferWriter<byte> writer, ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length > ushort.MaxValue)
        {
            throw new ArgumentException("File token binary field exceeds maximum supported length.");
        }
        var span = writer.GetSpan(2 + bytes.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(span, (ushort)bytes.Length);
        bytes.CopyTo(span.Slice(2, bytes.Length));
        writer.Advance(2 + bytes.Length);
    }

    private static bool TryReadGuidField(ReadOnlySpan<byte> data, ref int offset, out string guidString)
    {
        guidString = string.Empty;
        if (offset + 16 > data.Length) return false;
        guidString = new Guid(data.Slice(offset, 16)).ToString("D");
        offset += 16;
        return true;
    }

    private static bool TryReadBytesField(ReadOnlySpan<byte> data, ref int offset, out ReadOnlySpan<byte> bytes)
    {
        bytes = default;
        if (offset + 2 > data.Length) return false;
        int len = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(offset, 2));
        offset += 2;
        if (offset + len > data.Length) return false;
        bytes = data.Slice(offset, len);
        offset += len;
        return true;
    }

    private static bool TryReadUtf8Field(ReadOnlySpan<byte> data, ref int offset, out string value)
    {
        value = string.Empty;
        if (!TryReadBytesField(data, ref offset, out var bytes)) return false;
        value = Encoding.UTF8.GetString(bytes);
        return true;
    }

    /// <summary>
    /// Validates a self-contained encrypted token and extracts the FileTokenPayload.
    /// Returns null if the token is invalid, tampered, or expired.
    /// Supports compact binary v2 (current) as well as legacy compact/verbose UTF-8 JSON (pre-upgrade server-v*).
    /// </summary>
    public FileTokenPayload? UnprotectToken(string token)
    {
        try
        {
            var protectedBytes = WebEncoders.Base64UrlDecode(token);
            var plaintextBytes = _protector.Unprotect(protectedBytes);
            if (plaintextBytes.Length == 0)
            {
                return null;
            }

            if (plaintextBytes[0] == BinaryPayloadVersionV2)
            {
                return DeserializeBinaryV2(plaintextBytes);
            }

            var json = Encoding.UTF8.GetString(plaintextBytes);
            var payload = JsonSerializer.Deserialize<FileTokenPayload>(json);
            if (payload != null && !string.IsNullOrEmpty(payload.Category))
            {
                return payload;
            }

            // Tolerant deserialization for in-flight tokens issued before compact property names
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("Category", out var catProp))
            {
                return new FileTokenPayload
                {
                    Category = catProp.GetString() ?? string.Empty,
                    ContextId = root.TryGetProperty("ContextId", out var ctxProp) ? ctxProp.GetString() ?? string.Empty : string.Empty,
                    FileName = root.TryGetProperty("FileName", out var fileProp) ? fileProp.GetString() ?? string.Empty : string.Empty,
                    EncryptionKeyBase64 = root.TryGetProperty("EncryptionKeyBase64", out var keyProp) && keyProp.ValueKind == JsonValueKind.String ? keyProp.GetString() : null,
                    IsThumbnail = root.TryGetProperty("IsThumbnail", out var thumbProp) && thumbProp.ValueKind == JsonValueKind.True,
                    ExpiryTicks = root.TryGetProperty("ExpiryTicks", out var expProp) && expProp.TryGetInt64(out var exp) ? exp : 0,
                    UserId = root.TryGetProperty("UserId", out var userProp) && userProp.ValueKind == JsonValueKind.String ? userProp.GetString() : null,
                    SessionId = root.TryGetProperty("SessionId", out var sessProp) && sessProp.ValueKind == JsonValueKind.String ? sessProp.GetString() : null,
                    TokenSequence = root.TryGetProperty("TokenSequence", out var seqProp) && seqProp.TryGetInt64(out var seq) ? seq : 0
                };
            }

            return payload;
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
        string? requestingSessionId = null,
        bool isThumbnail = false,
        bool requireToken = false)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return requireToken ? FileTokenValidationResult.Missing() : FileTokenValidationResult.None();
        }

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

        // Thumbnail-scoped tokens must never authorize full original file downloads or uploads
        if (payload.IsThumbnail && !isThumbnail)
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
    Missing,
    Invalid,
    Expired
}

public readonly record struct FileTokenValidationResult(FileTokenStatus Status, string? EncryptionKey)
{
    public bool IsSuccess => Status == FileTokenStatus.Valid || Status == FileTokenStatus.None;
    public static FileTokenValidationResult Success(string? key) => new(FileTokenStatus.Valid, key);
    public static FileTokenValidationResult None() => new(FileTokenStatus.None, null);
    public static FileTokenValidationResult Missing() => new(FileTokenStatus.Missing, null);
    public static FileTokenValidationResult Invalid() => new(FileTokenStatus.Invalid, null);
    public static FileTokenValidationResult Expired() => new(FileTokenStatus.Expired, null);
}
