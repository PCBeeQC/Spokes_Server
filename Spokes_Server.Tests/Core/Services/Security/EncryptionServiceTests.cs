using System.Security.Cryptography;
using Microsoft.Extensions.Configuration;

namespace Spokes_Server.Tests.Core.Services.Security;

public class EncryptionServiceTests : IDisposable
{
    private readonly string _testDataDir;
    private readonly IConfiguration _config;

    public EncryptionServiceTests()
    {
        _testDataDir = Path.Combine(Path.GetTempPath(), "Spokes_Test_Encryption_" + Guid.NewGuid().ToString());
        Directory.CreateDirectory(_testDataDir);

        var configDict = new Dictionary<string, string?> { { "DataPath", _testDataDir } };
        _config = new ConfigurationBuilder().AddInMemoryCollection(configDict).Build();
    }

        public void Dispose()
        {
            if (Directory.Exists(_testDataDir))
            {
                try { Directory.Delete(_testDataDir, true); } catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"Cleanup error: {ex.Message}"); }
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
        public void Key_IsPersistent_AcrossServiceInstances()
        {
            // 1. Generate key in first instance
            var original = "Persistence Test";
            string encrypted;
            var service1 = new EncryptionService(_config);
            encrypted = service1.Encrypt(original);

            // Verify file exists
            var keyPath = Path.Combine(_testDataDir, "security.key");
            Assert.True(File.Exists(keyPath));

            // 2. Load key in second instance
            var service2 = new EncryptionService(_config);
            var decrypted = service2.Decrypt(encrypted);

            Assert.Equal(original, decrypted);
        }

        [Fact]
        public void Decrypt_InvalidCipher_ThrowsCryptographicException()
        {
            var service = new EncryptionService(_config);
            Assert.Throws<CryptographicException>(() => service.Decrypt("ThisIsNotBase64!"));
        }

        [Fact]
        public void Encrypt_FirstUse_GeneratesAndPersists256BitKey()
        {
            var service = new EncryptionService(_config);
            service.Encrypt("trigger key generation");

            var keyPath = Path.Combine(_testDataDir, "security.key");
            Assert.True(File.Exists(keyPath));
            Assert.Equal(32, File.ReadAllBytes(keyPath).Length);
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
            Assert.Throws<CryptographicException>(() => service.Decrypt(invalidAesBase64));
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
        public void Constructor_DefaultDataPath_WhenConfigMissingDataPath()
        {
            var emptyConfig = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>()).Build();
            Directory.CreateDirectory("Data");
            try
            {
                var service = new EncryptionService(emptyConfig);
                Assert.False(string.IsNullOrWhiteSpace(service.Encrypt("trigger key generation")));
                Assert.True(File.Exists(Path.Combine("Data", "security.key")));
            }
            finally
            {
                if (File.Exists(Path.Combine("Data", "security.key")))
                {
                    try { File.Delete(Path.Combine("Data", "security.key")); } catch (Exception ex) { System.Diagnostics.Debug.WriteLine(ex.Message); }
                }
                if (Directory.Exists("Data") && Directory.GetFiles("Data").Length == 0 && Directory.GetDirectories("Data").Length == 0)
                {
                    try { Directory.Delete("Data"); } catch (Exception ex) { System.Diagnostics.Debug.WriteLine(ex.Message); }
                }
            }
        }
    }

