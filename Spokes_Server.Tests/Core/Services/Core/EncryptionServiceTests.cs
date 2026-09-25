using Microsoft.Extensions.Configuration;
using Spokes_Server.Core.Services.Core;

namespace Spokes_Server.Tests.Core.Services.Core;

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
        public void Decrypt_InvalidCipher_ReturnsEmptyString()
        {
            var service = new EncryptionService(_config);
            var result = service.Decrypt("ThisIsNotBase64!");
            Assert.Equal(string.Empty, result);
        }

        [Fact]
        public void KeyHash_IsPopulated_AndMatchesSha256OfKey()
        {
            var service = new EncryptionService(_config);
            Assert.False(string.IsNullOrWhiteSpace(service.KeyHash));

            var keyHashBytes = Convert.FromBase64String(service.KeyHash);
            Assert.Equal(32, keyHashBytes.Length);

            var keyPath = Path.Combine(_testDataDir, "security.key");
            Assert.True(File.Exists(keyPath));
            var keyBytes = File.ReadAllBytes(keyPath);

            using var sha = System.Security.Cryptography.SHA256.Create();
            var expectedHash = Convert.ToBase64String(sha.ComputeHash(keyBytes));
            Assert.Equal(expectedHash, service.KeyHash);
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
        public void Decrypt_CorruptedCiphertextValidBase64_ReturnsEmptyString()
        {
            var service = new EncryptionService(_config);
            var invalidAesBase64 = Convert.ToBase64String([1, 2, 3, 4, 5]);
            var result = service.Decrypt(invalidAesBase64);
            Assert.Equal(string.Empty, result);
        }

        [Fact]
        public void Constructor_DefaultDataPath_WhenConfigMissingDataPath()
        {
            var emptyConfig = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>()).Build();
            Directory.CreateDirectory("Data");
            try
            {
                var service = new EncryptionService(emptyConfig);
                Assert.False(string.IsNullOrWhiteSpace(service.KeyHash));
                Assert.True(File.Exists(Path.Combine("Data", "security.key")));
            }
            finally
            {
                if (File.Exists(Path.Combine("Data", "security.key")))
                {
                    try { File.Delete(Path.Combine("Data", "security.key")); } catch { }
                }
                if (Directory.Exists("Data") && Directory.GetFiles("Data").Length == 0 && Directory.GetDirectories("Data").Length == 0)
                {
                    try { Directory.Delete("Data"); } catch { }
                }
            }
        }
    }

