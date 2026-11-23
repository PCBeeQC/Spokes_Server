using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Configuration;

namespace Spokes_Server.Core.Services.Security;

/// <summary>
/// Valid UTF-8 JSON envelope wrapping an encrypted binary payload on disk so .json files remain valid JSON
/// while concealing all inner fields, structure, and metadata.
/// </summary>
public sealed class EncryptedJsonEnvelope
{
    [JsonPropertyName("spokesEncryptedVersion")]
    public int SpokesEncryptedVersion { get; set; } = 2;

    [JsonPropertyName("payload")]
    public string Payload { get; set; } = string.Empty;
}

/// <summary>
/// Global server master-password encryption service.
/// Derives a cached 256-bit Master Key-Derivation Key (KDK) from SPOKES_MASTER_PASSWORD using 100,000-iteration
/// PBKDF2-SHA256 with a random per-instance salt, then derives unique per-ciphertext 256-bit AES-GCM keys via HKDF-SHA256.
/// Never stores an encryption key file on disk or in backups.
/// </summary>
public class EncryptionService
{
    private static readonly byte[] V2MagicBytes = "SPKENC02"u8.ToArray();
    private static readonly byte[] V2HkdfInfo = "Spokes.EncryptionService.Aes256Gcm.v2"u8.ToArray();
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private const string V2StringPrefix = "v2:";
    private const int MagicSize = 8;
    private const int SaltSize = 16;
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private const int KeySize = 32; // 256-bit AES
    private const int Pbkdf2Iterations = 100_000;
    private const int V2AadSize = MagicSize + SaltSize + SaltSize + NonceSize; // 52 bytes
    private const int V2HeaderSize = V2AadSize + TagSize; // 68 bytes
    private const int MaxCachedKdks = 32;

    private readonly IConfiguration? _configuration;
    private readonly byte[] _instanceKdfSalt = RandomNumberGenerator.GetBytes(SaltSize);
    private readonly object _kdkCacheLock = new();
    private readonly Dictionary<(string MasterPassword, string SaltHex), byte[]> _kdkCache = new();
    private readonly Queue<(string MasterPassword, string SaltHex)> _kdkCacheOrder = new();

    public EncryptionService(IConfiguration? configuration = null)
    {
        _configuration = configuration;
    }

    public static bool TryResolveConfiguredMasterPassword(
        IConfiguration? configuration,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out string? masterPassword)
    {
        var configured = configuration?["SPOKES_MASTER_PASSWORD"];
        if (string.IsNullOrEmpty(configured))
        {
            configured = Environment.GetEnvironmentVariable("SPOKES_MASTER_PASSWORD");
        }

        if (!string.IsNullOrEmpty(configured))
        {
            masterPassword = configured;
            return true;
        }

        masterPassword = null;
        return false;
    }

    private string ResolveMasterPassword()
    {
        if (TryResolveConfiguredMasterPassword(_configuration, out var masterPassword))
        {
            return masterPassword;
        }

        throw new InvalidOperationException(
            "SPOKES_MASTER_PASSWORD is not configured. Refusing to encrypt or decrypt server secrets without a master password.");
    }

    /// <summary>
    /// Checks whether a byte span begins with the SPKENC02 authenticated encryption header.
    /// </summary>
    public static bool IsEncryptedBytes(ReadOnlySpan<byte> data)
    {
        return data.Length >= V2HeaderSize &&
               CryptographicOperations.FixedTimeEquals(data[..MagicSize], V2MagicBytes);
    }

    /// <summary>
    /// Encrypts an arbitrary byte span into a self-contained SPKENC02 (PBKDF2-SHA256 + HKDF-SHA256 + AES-256-GCM) payload.
    /// </summary>
    public byte[] EncryptBytes(ReadOnlySpan<byte> plainBytes)
    {
        var masterPassword = ResolveMasterPassword();
        var masterKdk = GetOrDeriveCachedKdk(masterPassword, _instanceKdfSalt);

        var output = new byte[V2HeaderSize + plainBytes.Length];
        V2MagicBytes.CopyTo(output.AsSpan(0, MagicSize));
        _instanceKdfSalt.CopyTo(output.AsSpan(MagicSize, SaltSize));

        var hkdfSalt = output.AsSpan(MagicSize + SaltSize, SaltSize);
        RandomNumberGenerator.Fill(hkdfSalt);

        var nonce = output.AsSpan(MagicSize + SaltSize + SaltSize, NonceSize);
        RandomNumberGenerator.Fill(nonce);

        var aad = output.AsSpan(0, V2AadSize);
        var tag = output.AsSpan(V2AadSize, TagSize);
        var ciphertext = output.AsSpan(V2HeaderSize);

        Span<byte> perMessageKey = stackalloc byte[KeySize];
        try
        {
            HKDF.DeriveKey(HashAlgorithmName.SHA256, masterKdk, perMessageKey, hkdfSalt, V2HkdfInfo);
            using var aesGcm = new AesGcm(perMessageKey, TagSize);
            aesGcm.Encrypt(nonce, plainBytes, ciphertext, tag, aad);
            return output;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(perMessageKey);
        }
    }

    /// <summary>
    /// Decrypts and authenticates an SPKENC02 binary payload.
    /// Only caches the PBKDF2-derived Master KDK after the 128-bit AES-GCM authentication tag succeeds.
    /// </summary>
    public byte[] DecryptBytes(ReadOnlySpan<byte> cipherBytes)
    {
        if (!IsEncryptedBytes(cipherBytes))
        {
            throw new CryptographicException("Ciphertext is missing a valid SPKENC02 header.");
        }

        var aad = cipherBytes[..V2AadSize];
        var kdfSalt = cipherBytes.Slice(MagicSize, SaltSize);
        var hkdfSalt = cipherBytes.Slice(MagicSize + SaltSize, SaltSize);
        var nonce = cipherBytes.Slice(MagicSize + SaltSize + SaltSize, NonceSize);
        var tag = cipherBytes.Slice(V2AadSize, TagSize);
        var ciphertext = cipherBytes[V2HeaderSize..];

        var masterPassword = ResolveMasterPassword();
        var cacheKey = (masterPassword, Convert.ToHexString(kdfSalt));
        bool wasCached = TryGetCachedKdk(cacheKey, out var masterKdk);
        if (!wasCached)
        {
            masterKdk = Rfc2898DeriveBytes.Pbkdf2(masterPassword, kdfSalt, Pbkdf2Iterations, HashAlgorithmName.SHA256, KeySize);
        }

        Span<byte> perMessageKey = stackalloc byte[KeySize];
        bool authenticated = false;
        try
        {
            HKDF.DeriveKey(HashAlgorithmName.SHA256, masterKdk, perMessageKey, hkdfSalt, V2HkdfInfo);
            var plainBytes = new byte[ciphertext.Length];
            using var aesGcm = new AesGcm(perMessageKey, TagSize);
            aesGcm.Decrypt(nonce, ciphertext, tag, plainBytes, aad);
            authenticated = true;

            if (!wasCached)
            {
                CacheVerifiedKdk(cacheKey, masterKdk);
            }

            return plainBytes;
        }
        catch (CryptographicException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new CryptographicException("Decryption failed.", ex);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(perMessageKey);
            if (!authenticated && !wasCached)
            {
                CryptographicOperations.ZeroMemory(masterKdk);
            }
        }
    }

    public string Encrypt(string plainText)
    {
        if (string.IsNullOrEmpty(plainText)) return plainText;

        var plainBytes = StrictUtf8.GetBytes(plainText);
        var encryptedBytes = EncryptBytes(plainBytes);
        return string.Concat(V2StringPrefix, Convert.ToBase64String(encryptedBytes));
    }

    public string Decrypt(string cipherText)
    {
        if (string.IsNullOrEmpty(cipherText)) return cipherText;

        try
        {
            if (!cipherText.StartsWith(V2StringPrefix, StringComparison.Ordinal))
            {
                throw new CryptographicException("Ciphertext is missing a valid v2: prefix.");
            }

            var cipherBytes = Convert.FromBase64String(cipherText.Substring(V2StringPrefix.Length));
            var plainBytes = DecryptBytes(cipherBytes);
            return StrictUtf8.GetString(plainBytes);
        }
        catch (InvalidOperationException)
        {
            throw;
        }
        catch (CryptographicException)
        {
            throw;
        }
        catch (FormatException ex)
        {
            throw new CryptographicException("Ciphertext is not valid Base64.", ex);
        }
        catch (Exception ex)
        {
            throw new CryptographicException("Decryption failed.", ex);
        }
    }

    public bool CanDecrypt(string? cipherText) =>
        !string.IsNullOrEmpty(cipherText) && TryDecrypt(cipherText, out _);

    public bool TryDecrypt(string? cipherText, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out string? plainText)
    {
        if (cipherText == null)
        {
            plainText = null;
            return false;
        }

        if (cipherText.Length == 0)
        {
            plainText = string.Empty;
            return true;
        }

        try
        {
            plainText = Decrypt(cipherText);
            return true;
        }
        catch (InvalidOperationException)
        {
            throw;
        }
        catch (Exception)
        {
            plainText = null;
            return false;
        }
    }

    /// <summary>
    /// Serializes <paramref name="value"/> to JSON once, encrypts the entire payload with AES-256-GCM (SPKENC02),
    /// and wraps it in a valid UTF-8 <see cref="EncryptedJsonEnvelope"/> suitable for atomic .json persistence.
    /// </summary>
    public byte[] EncryptJsonEnvelopeToUtf8Bytes<T>(T value, JsonSerializerOptions? options = null)
    {
        var innerJsonBytes = JsonSerializer.SerializeToUtf8Bytes(value, options);
        var cipherBytes = EncryptBytes(innerJsonBytes);
        var envelope = new EncryptedJsonEnvelope
        {
            SpokesEncryptedVersion = 2,
            Payload = Convert.ToBase64String(cipherBytes)
        };
        return JsonSerializer.SerializeToUtf8Bytes(envelope);
    }

    /// <summary>
    /// Reads and decrypts a JSON document produced by <see cref="EncryptJsonEnvelopeToUtf8Bytes{T}"/> (or raw SPKENC02 bytes),
    /// falling back to direct JSON deserialization when the file is a legacy unwrapped JSON document.
    /// Throws <see cref="CryptographicException"/> if an encrypted envelope is present but tampered or decrypted with the wrong password.
    /// </summary>
    public T? DecryptJsonEnvelopeOrLegacy<T>(
        ReadOnlySpan<byte> fileBytes,
        out bool wasLegacyUnwrapped,
        JsonSerializerOptions? options = null)
    {
        if (IsEncryptedBytes(fileBytes))
        {
            wasLegacyUnwrapped = false;
            var decryptedBytes = DecryptBytes(fileBytes);
            return JsonSerializer.Deserialize<T>(decryptedBytes, options);
        }

        // Strip UTF-8 BOM if present
        if (fileBytes.Length >= 3 && fileBytes[0] == 0xEF && fileBytes[1] == 0xBB && fileBytes[2] == 0xBF)
        {
            fileBytes = fileBytes[3..];
        }

        if (TryReadEncryptedEnvelope(fileBytes, out var version, out var payload))
        {
            wasLegacyUnwrapped = false;
            if (version != 2 || string.IsNullOrEmpty(payload))
            {
                throw new CryptographicException($"Unsupported or malformed encrypted JSON envelope (version {version}).");
            }

            byte[] cipherBytes;
            try
            {
                cipherBytes = Convert.FromBase64String(payload);
            }
            catch (FormatException ex)
            {
                throw new CryptographicException("Encrypted JSON envelope payload is not valid Base64.", ex);
            }

            var decryptedJsonBytes = DecryptBytes(cipherBytes);
            return JsonSerializer.Deserialize<T>(decryptedJsonBytes, options);
        }

        wasLegacyUnwrapped = true;
        return JsonSerializer.Deserialize<T>(fileBytes, options);
    }

    private byte[] GetOrDeriveCachedKdk(string masterPassword, ReadOnlySpan<byte> kdfSalt)
    {
        var cacheKey = (masterPassword, Convert.ToHexString(kdfSalt));
        if (TryGetCachedKdk(cacheKey, out var cached))
        {
            return cached;
        }

        var derived = Rfc2898DeriveBytes.Pbkdf2(masterPassword, kdfSalt, Pbkdf2Iterations, HashAlgorithmName.SHA256, KeySize);
        return CacheVerifiedKdk(cacheKey, derived);
    }

    private bool TryGetCachedKdk((string MasterPassword, string SaltHex) cacheKey, out byte[] kdk)
    {
        lock (_kdkCacheLock)
        {
            if (_kdkCache.TryGetValue(cacheKey, out var existing))
            {
                kdk = existing;
                return true;
            }
        }

        kdk = [];
        return false;
    }

    private byte[] CacheVerifiedKdk((string MasterPassword, string SaltHex) cacheKey, byte[] candidateKdk)
    {
        lock (_kdkCacheLock)
        {
            if (_kdkCache.TryGetValue(cacheKey, out var existing))
            {
                if (!ReferenceEquals(existing, candidateKdk))
                {
                    CryptographicOperations.ZeroMemory(candidateKdk);
                }
                return existing;
            }

            while (_kdkCache.Count >= MaxCachedKdks && _kdkCacheOrder.TryDequeue(out var oldestKey))
            {
                _kdkCache.Remove(oldestKey);
            }

            _kdkCache[cacheKey] = candidateKdk;
            _kdkCacheOrder.Enqueue(cacheKey);
            return candidateKdk;
        }
    }

    private static bool TryReadEncryptedEnvelope(ReadOnlySpan<byte> utf8Json, out int version, out string? payload)
    {
        version = 0;
        payload = null;
        bool hasVersionProperty = false;

        var reader = new Utf8JsonReader(utf8Json);
        if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
        {
            return false;
        }

        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject && reader.CurrentDepth == 0)
            {
                break;
            }

            if (reader.TokenType == JsonTokenType.PropertyName && reader.CurrentDepth == 1)
            {
                if (reader.ValueTextEquals("spokesEncryptedVersion"u8))
                {
                    hasVersionProperty = true;
                    if (!reader.Read())
                    {
                        version = -1;
                        break;
                    }

                    if (reader.TokenType != JsonTokenType.Number || !reader.TryGetInt32(out version))
                    {
                        version = -1;
                        if (reader.TokenType is JsonTokenType.StartObject or JsonTokenType.StartArray)
                        {
                            reader.Skip();
                        }
                    }
                }
                else if (reader.ValueTextEquals("payload"u8))
                {
                    if (!reader.Read())
                    {
                        break;
                    }

                    if (reader.TokenType == JsonTokenType.String)
                    {
                        payload = reader.GetString();
                    }
                    else if (reader.TokenType is JsonTokenType.StartObject or JsonTokenType.StartArray)
                    {
                        reader.Skip();
                    }
                }
                else
                {
                    if (!reader.Read())
                    {
                        break;
                    }
                    reader.Skip();
                }
            }
        }

        return hasVersionProperty;
    }
}
