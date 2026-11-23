using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;

namespace Spokes_Server.Tests.Core.Services.Security;

[Collection("EnvironmentVariableTests")]
public class EncryptionServiceTests : IDisposable
{
    private const string TestMasterPassword = "TestMasterPassword#2026!";
    private readonly string _testDataDir;
    private readonly IConfiguration _config;

    public EncryptionServiceTests()
    {
        _testDataDir = Path.Combine(Path.GetTempPath(), "Spokes_Test_Encryption_" + Guid.NewGuid().ToString());
        Directory.CreateDirectory(_testDataDir);

        var configDict = new Dictionary<string, string?>
        {
            { "DataPath", _testDataDir },
            { "SPOKES_MASTER_PASSWORD", TestMasterPassword }
        };
        _config = new ConfigurationBuilder().AddInMemoryCollection(configDict).Build();
    }

    public void Dispose()
    {
        if (Directory.Exists(_testDataDir))
        {
            try { Directory.Delete(_testDataDir, true); } catch (Exception ex) { Debug.WriteLine($"Cleanup error: {ex.Message}"); }
        }
    }

    [Fact]
    public void EncryptDecrypt_Cycle_ReturnsOriginalText()
    {
        var service = new EncryptionService(_config);
        var original = "Hello World! This is a secret message.";

        var encrypted = service.Encrypt(original);
        Assert.NotEqual(original, encrypted);

        var decrypted = service.Decrypt(encrypted);
        Assert.Equal(original, decrypted);
    }

    [Fact]
    public void EncryptDecrypt_EmptyString_ReturnsEmptyString()
    {
        var service = new EncryptionService(_config);

        Assert.Equal(string.Empty, service.Encrypt(string.Empty));
        Assert.Equal(string.Empty, service.Decrypt(string.Empty));
    }

    [Fact]
    public void Key_IsDeterministicFromMasterPassword_AndNeverWritesSecurityKeyFileToDisk()
    {
        var original = "Master Password Persistence Test";
        var service1 = new EncryptionService(_config);
        var encrypted = service1.Encrypt(original);

        // Verify no security.key file is ever created on disk
        var keyPath = Path.Combine(_testDataDir, "security.key");
        Assert.False(File.Exists(keyPath));

        // Second instance with same master password decrypts cleanly
        var service2 = new EncryptionService(_config);
        var decrypted = service2.Decrypt(encrypted);

        Assert.Equal(original, decrypted);
    }

    [Fact]
    public void Decrypt_DifferentMasterPassword_ThrowsCryptographicException()
    {
        var service1 = new EncryptionService(_config);
        var encrypted = service1.Encrypt("SensitiveSmtpSecret123!");

        var wrongConfig = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                { "DataPath", _testDataDir },
                { "SPOKES_MASTER_PASSWORD", "DifferentMasterPassword#999!" }
            })
            .Build();
        var service2 = new EncryptionService(wrongConfig);

        Assert.ThrowsAny<CryptographicException>(() => service2.Decrypt(encrypted));
    }

    [Fact]
    public void Encrypt_SamePlainTextTwice_ProducesUniqueCiphertextsWithRandomSaltAndIv()
    {
        var service = new EncryptionService(_config);
        var plain = "SamePassword";

        var cipher1 = service.Encrypt(plain);
        var cipher2 = service.Encrypt(plain);

        Assert.NotEqual(cipher1, cipher2);
        Assert.Equal(plain, service.Decrypt(cipher1));
        Assert.Equal(plain, service.Decrypt(cipher2));
    }

    [Fact]
    public void Decrypt_InvalidCipher_ThrowsCryptographicException()
    {
        var service = new EncryptionService(_config);
        Assert.ThrowsAny<CryptographicException>(() => service.Decrypt("ThisIsNotBase64!"));
    }

    [Fact]
    public void Encrypt_NullInput_ReturnsNull()
    {
        var service = new EncryptionService(_config);
        var result = service.Encrypt(null!);
        Assert.Null(result);
    }

    [Fact]
    public void Decrypt_NullInput_ReturnsNull()
    {
        var service = new EncryptionService(_config);
        var result = service.Decrypt(null!);
        Assert.Null(result);
    }

    [Fact]
    public void Decrypt_CorruptedCiphertextValidBase64_ThrowsCryptographicException()
    {
        var service = new EncryptionService(_config);
        var invalidAesBase64 = Convert.ToBase64String([1, 2, 3, 4, 5]);
        Assert.ThrowsAny<CryptographicException>(() => service.Decrypt(invalidAesBase64));
    }

    [Fact]
    public void TryDecrypt_ValidCipher_ReturnsTrueAndPlainText()
    {
        var service = new EncryptionService(_config);
        var original = "SecretPassword123!";
        var cipher = service.Encrypt(original);
        var success = service.TryDecrypt(cipher, out var plainText);

        Assert.True(success);
        Assert.Equal(original, plainText);
    }

    [Fact]
    public void TryDecrypt_CorruptedCipher_ReturnsFalseAndNull()
    {
        var service = new EncryptionService(_config);
        var invalidAesBase64 = Convert.ToBase64String([1, 2, 3, 4, 5]);
        var success = service.TryDecrypt(invalidAesBase64, out var plainText);

        Assert.False(success);
        Assert.Null(plainText);
    }

    [Fact]
    public void TryDecrypt_NullInput_ReturnsFalseAndNull()
    {
        var service = new EncryptionService(_config);
        var success = service.TryDecrypt(null, out var plainText);

        Assert.False(success);
        Assert.Null(plainText);
    }

    [Fact]
    public void TryDecrypt_EmptyString_ReturnsTrueAndEmptyString()
    {
        var service = new EncryptionService(_config);
        var success = service.TryDecrypt(string.Empty, out var plainText);

        Assert.True(success);
        Assert.Equal(string.Empty, plainText);
    }

    [Fact]
    public void Decrypt_MissingV2Prefix_ThrowsCryptographicException_AndCanDecryptReturnsFalse()
    {
        var service = new EncryptionService(_config);
        var validCipher = service.Encrypt("V2Password123!");

        Assert.True(service.CanDecrypt(validCipher));
        Assert.False(service.CanDecrypt(null));
        Assert.False(service.CanDecrypt(string.Empty));
        Assert.False(service.CanDecrypt("plaintext-without-v2-prefix"));

        Assert.ThrowsAny<CryptographicException>(() => service.Decrypt("plaintext-without-v2-prefix"));
        Assert.False(service.TryDecrypt("plaintext-without-v2-prefix", out var plain));
        Assert.Null(plain);
    }

    [Theory]
    [InlineData(0)]   // Magic header byte (AAD)
    [InlineData(10)]  // KDF salt byte (AAD)
    [InlineData(26)]  // HKDF salt byte (AAD)
    [InlineData(42)]  // Nonce byte (AAD)
    [InlineData(55)]  // Auth tag byte
    [InlineData(70)]  // Ciphertext payload byte
    public void DecryptBytes_TamperedHeaderOrCiphertext_ThrowsCryptographicException(int tamperedByteIndex)
    {
        var service = new EncryptionService(_config);
        var plainBytes = Encoding.UTF8.GetBytes("Authenticated payload protected by AES-256-GCM");
        var encrypted = service.EncryptBytes(plainBytes);

        encrypted[tamperedByteIndex] ^= 0x01;

        Assert.ThrowsAny<CryptographicException>(() => service.DecryptBytes(encrypted));
    }

    [Fact]
    public void EncryptBytes_EmptyPayload_RoundTripsCleanly()
    {
        var service = new EncryptionService(_config);
        var encrypted = service.EncryptBytes([]);

        Assert.True(EncryptionService.IsEncryptedBytes(encrypted));
        var decrypted = service.DecryptBytes(encrypted);
        Assert.Empty(decrypted);
    }

    [Fact]
    public void JsonEnvelope_EncryptAndDecrypt_HidesPlaintextAndRejectsWrongPassword()
    {
        var service1 = new EncryptionService(_config);
        var sample = new SampleSecretState("emp-secret-99", "Confidential Project Launch");

        var envelopeBytes = service1.EncryptJsonEnvelopeToUtf8Bytes(sample);
        var onDiskText = Encoding.UTF8.GetString(envelopeBytes);

        // File remains valid JSON, but conceals inner property names and values
        using var parsedDoc = JsonDocument.Parse(onDiskText);
        Assert.Equal(2, parsedDoc.RootElement.GetProperty("spokesEncryptedVersion").GetInt32());
        Assert.DoesNotContain("emp-secret-99", onDiskText);
        Assert.DoesNotContain("Confidential Project Launch", onDiskText);
        Assert.DoesNotContain("UserId", onDiskText);

        // Fresh service instance with same master password decrypts envelope cleanly
        var service2 = new EncryptionService(_config);
        var restored = service2.DecryptJsonEnvelopeOrLegacy<SampleSecretState>(envelopeBytes, out bool wasLegacy);
        Assert.False(wasLegacy);
        Assert.NotNull(restored);
        Assert.Equal(sample, restored);

        // Wrong master password throws CryptographicException (never silently returns default/empty state)
        var wrongConfig = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                { "DataPath", _testDataDir },
                { "SPOKES_MASTER_PASSWORD", "WrongPassword!" }
            })
            .Build();
        var wrongService = new EncryptionService(wrongConfig);
        Assert.ThrowsAny<CryptographicException>(() =>
            wrongService.DecryptJsonEnvelopeOrLegacy<SampleSecretState>(envelopeBytes, out _));

        // Unwrapped legacy JSON falls back cleanly and flags wasLegacyUnwrapped = true
        var legacyBytes = JsonSerializer.SerializeToUtf8Bytes(sample);
        var legacyRestored = service2.DecryptJsonEnvelopeOrLegacy<SampleSecretState>(legacyBytes, out bool wasLegacyFallback);
        Assert.True(wasLegacyFallback);
        Assert.Equal(sample, legacyRestored);
    }

    [Fact]
    public void HighThroughput_1000SequentialOperations_CompletesQuicklyWithUniqueCiphertexts()
    {
        var service = new EncryptionService(_config);
        var ciphertexts = new HashSet<string>(1000);

        var sw = Stopwatch.StartNew();
        for (int i = 0; i < 1000; i++)
        {
            var cipher = service.Encrypt("HighThroughputSecretPayload");
            Assert.True(ciphertexts.Add(cipher));
            Assert.Equal("HighThroughputSecretPayload", service.Decrypt(cipher));
        }
        sw.Stop();

        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(3), $"Expected < 3s for 1,000 encrypt+decrypt ops, took {sw.Elapsed.TotalMilliseconds:F1}ms");
    }

    [Fact]
    public void FailClosed_WhenMasterPasswordUnset_ThrowsInvalidOperationExceptionAcrossAllCryptoOps()
    {
        var previousEnv = Environment.GetEnvironmentVariable("SPOKES_MASTER_PASSWORD");
        try
        {
            Environment.SetEnvironmentVariable("SPOKES_MASTER_PASSWORD", null);

            // Configure Spokes_MasterKey (NOT SPOKES_MASTER_PASSWORD) to ensure legacy fallback no longer applies
            var unsetConfig = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    { "DataPath", _testDataDir },
                    { "Spokes_MasterKey", "LegacyIgnoredKey!" }
                })
                .Build();

            var validCipher = new EncryptionService(_config).Encrypt("SensitivePayload");
            var failClosedService = new EncryptionService(unsetConfig);

            Assert.Throws<InvalidOperationException>(() => failClosedService.Encrypt("SecretValue"));
            Assert.Throws<InvalidOperationException>(() => failClosedService.EncryptBytes(Encoding.UTF8.GetBytes("SecretBytes")));
            Assert.Throws<InvalidOperationException>(() => failClosedService.EncryptJsonEnvelopeToUtf8Bytes(new SampleSecretState("u1", "Title")));
            Assert.Throws<InvalidOperationException>(() => failClosedService.Decrypt(validCipher));
            Assert.Throws<InvalidOperationException>(() => failClosedService.TryDecrypt(validCipher, out _));
        }
        finally
        {
            Environment.SetEnvironmentVariable("SPOKES_MASTER_PASSWORD", previousEnv);
        }
    }

    private sealed record SampleSecretState(string UserId, string Title);
}
