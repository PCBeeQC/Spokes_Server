using System;
using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using Spokes_Server.Core.Services.Security;

namespace Spokes_Server.Core.Data;

/// <summary>
/// Shared thread-safe cache for AES-GCM <c>v2:</c> encrypted repository fields.
/// Prevents redundant PBKDF2/AES-GCM re-encryption on unchanged fields during disk persistence
/// while supporting transparent on-load decryption and legacy plaintext migration detection.
/// </summary>
internal sealed class EncryptedFieldCipherCache
{
    public const string EncryptedV2Prefix = "v2:";
    public static readonly JsonSerializerOptions WriteIndentedOptions = new() { WriteIndented = true };

    private readonly EncryptionService _encryptionService;
    private readonly ConcurrentDictionary<string, (string Plaintext, string Ciphertext)> _cache = new(StringComparer.Ordinal);

    public EncryptedFieldCipherCache(EncryptionService encryptionService)
    {
        _encryptionService = encryptionService ?? throw new ArgumentNullException(nameof(encryptionService));
    }

    public EncryptionService EncryptionService => _encryptionService;

    public void Remove(string cacheKey) => _cache.TryRemove(cacheKey, out _);

    public void SetCached(string cacheKey, string plaintext, string ciphertext) =>
        _cache[cacheKey] = (plaintext, ciphertext);

    /// <summary>
    /// Decrypts a raw persisted string field loaded from disk, populating the cipher cache on <c>v2:</c> hits
    /// and signaling whether legacy plaintext (or non-v2 ciphertext) requires a migration write.
    /// </summary>
    public bool TryDecryptLoaded(string cacheKey, string? raw, out string decrypted, out bool needsMigrationWrite)
    {
        decrypted = string.Empty;
        needsMigrationWrite = false;

        if (string.IsNullOrEmpty(raw))
        {
            _cache.TryRemove(cacheKey, out _);
            return false;
        }

        if (_encryptionService.TryDecrypt(raw, out var plain))
        {
            decrypted = plain;
            if (raw.StartsWith(EncryptedV2Prefix, StringComparison.Ordinal))
            {
                _cache[cacheKey] = (plain, raw);
            }
            else
            {
                needsMigrationWrite = true;
            }
            return true;
        }

        if (!raw.StartsWith(EncryptedV2Prefix, StringComparison.Ordinal))
        {
            needsMigrationWrite = true;
        }

        return false;
    }

    /// <summary>
    /// Decrypts a JSON string property in place on <paramref name="container"/> and returns whether
    /// the persisted file needs an encrypted migration write.
    /// </summary>
    public bool DecryptStringNode(JsonObject container, string propertyName, string cacheKey)
    {
        var raw = GetStringNodeValue(container[propertyName]);
        if (TryDecryptLoaded(cacheKey, raw, out var decrypted, out var needsMigrationWrite))
        {
            container[propertyName] = decrypted;
        }
        return needsMigrationWrite;
    }

    /// <summary>
    /// Normalizes an in-memory field value: if a caller passed an already-<c>v2:</c>-encrypted string,
    /// decrypts it back to plaintext and caches the ciphertext pair.
    /// </summary>
    public string NormalizeInMemory(string cacheKey, string? current)
    {
        if (string.IsNullOrEmpty(current))
        {
            _cache.TryRemove(cacheKey, out _);
            return string.Empty;
        }

        if (current.StartsWith(EncryptedV2Prefix, StringComparison.Ordinal) &&
            _encryptionService.TryDecrypt(current, out var decrypted))
        {
            _cache[cacheKey] = (decrypted, current);
            return decrypted;
        }

        return current;
    }

    /// <summary>
    /// Returns a <c>v2:</c> encrypted string ready for disk persistence, reusing the cached ciphertext
    /// when the plaintext has not changed.
    /// </summary>
    public string EncryptForDisk(string cacheKey, string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            _cache.TryRemove(cacheKey, out _);
            return string.Empty;
        }

        if (value.StartsWith(EncryptedV2Prefix, StringComparison.Ordinal))
        {
            if (_encryptionService.TryDecrypt(value, out var decrypted))
            {
                _cache[cacheKey] = (decrypted, value);
            }
            return value;
        }

        if (_cache.TryGetValue(cacheKey, out var cached) &&
            string.Equals(cached.Plaintext, value, StringComparison.Ordinal))
        {
            return cached.Ciphertext;
        }

        var encrypted = _encryptionService.Encrypt(value);
        _cache[cacheKey] = (value, encrypted);
        return encrypted;
    }

    public static string? GetStringNodeValue(JsonNode? node) =>
        node is JsonValue jv && jv.TryGetValue<string>(out var s) ? s : null;
}
