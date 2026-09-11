using Spokes_Server.Core.Services.Communication;
using Spokes_Server.Core.Services.Projects;
using Spokes_Server.Core.Services.Core;
using Microsoft.Extensions.Configuration;
using Spokes_Server.Core.Services;
using System.Collections.Generic;
using System.IO;

namespace Spokes_Server.Tests.Core.Services.Core
{
    public class EncryptionServiceTests : IDisposable
    {
        private readonly string _testDataDir;
        private readonly IConfiguration _config;

        public EncryptionServiceTests()
        {
            _testDataDir = Path.Combine(Path.GetTempPath(), "Spokes_Test_Encryption_" + Guid.NewGuid().ToString());
            Directory.CreateDirectory(_testDataDir);

            var configDict = new Dictionary<string, string> { { "DataPath", _testDataDir } };
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
    }
}

