using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Spokes_Server.Core.Services.Security;

namespace Spokes_Server.Tests.Core.Services.Security;

public class FileTokenServiceTests
{
        private readonly IMemoryCache _memoryCache;
        private readonly IDataProtectionProvider _dataProtection;

        public FileTokenServiceTests()
        {
            var services = new ServiceCollection();
            services.AddMemoryCache();
            services.AddDataProtection().UseEphemeralDataProtectionProvider();
            var serviceProvider = services.BuildServiceProvider();
            _memoryCache = serviceProvider.GetRequiredService<IMemoryCache>();
            _dataProtection = serviceProvider.GetRequiredService<IDataProtectionProvider>();
        }

        [Fact]
        public void GenerateAccessToken_WithEncryptionKey_CreatesTokenThatRoundTrips()
        {
            // Arrange
            var service = new FileTokenService(_dataProtection, _memoryCache);
            string contextId = "album456";
            string filePath = "/path/to/my_image.png";
            string encryptionKeyBase64 = "S2V5MTIz"; // Base64 for 'Key123'

            // Act
            string token = service.GenerateAccessToken("albums", contextId, filePath, encryptionKeyBase64);

            // Assert — token is a non-empty encrypted string (not a GUID anymore)
            Assert.False(string.IsNullOrEmpty(token));

            // Assert — payload round-trips correctly
            var payload = service.UnprotectToken(token);
            Assert.NotNull(payload);
            Assert.Equal("albums", payload.Category);
            Assert.Equal("album456", payload.ContextId);
            Assert.Equal("my_image.png", payload.FileName);
            Assert.Equal("S2V5MTIz", payload.EncryptionKeyBase64);
            Assert.True(payload.ExpiryTicks > DateTime.UtcNow.Ticks);
        }

        [Fact]
        public void GenerateAccessToken_WithoutEncryptionKey_CreatesPayloadWithoutKey()
        {
            // Arrange
            var service = new FileTokenService(_dataProtection, _memoryCache);

            // Act
            string token = service.GenerateAccessToken("albums", "c1", "file.txt", null);
            
            // Assert — payload round-trips correctly
            var payload = service.UnprotectToken(token);
            Assert.NotNull(payload);
            Assert.Equal("albums", payload.Category);
            Assert.Equal("c1", payload.ContextId);
            Assert.Equal("file.txt", payload.FileName);
            Assert.Null(payload.EncryptionKeyBase64);
        }

        [Fact]
        public void GenerateAccessToken_CachesToken_ReturnsSameValueOnSecondCall()
        {
            // Arrange
            var service = new FileTokenService(_dataProtection, _memoryCache);

            // Act
            string token1 = service.GenerateAccessToken("chat", "channel1", "image.jpg", "key123");
            string token2 = service.GenerateAccessToken("chat", "channel1", "image.jpg", "key123");

            // Assert — should return cached value to prevent breaking browser caches
            Assert.Equal(token1, token2);
        }

        [Fact]
        public void UnprotectToken_WithTamperedToken_ReturnsNull()
        {
            // Arrange
            var service = new FileTokenService(_dataProtection, _memoryCache);

            // Act
            var payload = service.UnprotectToken("this-is-not-a-valid-token");

            // Assert
            Assert.Null(payload);
        }

        [Fact]
        public void UnprotectToken_WithDifferentDataProtectionProvider_ReturnsNull()
        {
            // Arrange — generate token with one provider
            var service1 = new FileTokenService(_dataProtection, _memoryCache);
            string token = service1.GenerateAccessToken("chat", "ch1", "file.jpg", "key123");

            // Create a second service with a different DataProtection key ring
            var services2 = new ServiceCollection();
            services2.AddMemoryCache();
            services2.AddDataProtection().UseEphemeralDataProtectionProvider();
            var sp2 = services2.BuildServiceProvider();
            var service2 = new FileTokenService(sp2.GetRequiredService<IDataProtectionProvider>(), sp2.GetRequiredService<IMemoryCache>());

            // Act — try to unprotect with different key ring
            var payload = service2.UnprotectToken(token);

            // Assert — should fail (different encryption keys)
            Assert.Null(payload);
        }

        [Fact]
        public void GenerateAccessToken_WithDifferentKeysOrFiles_GeneratesDifferentTokens()
        {
            // Arrange
            var service = new FileTokenService(_dataProtection, _memoryCache);

            // Act
            string token1 = service.GenerateAccessToken("chat", "ch1", "file1.png", "keyA");
            string token2 = service.GenerateAccessToken("chat", "ch1", "file2.png", "keyA");
            string token3 = service.GenerateAccessToken("chat", "ch1", "file1.png", "keyB");

            // Assert
            Assert.NotEqual(token1, token2);
            Assert.NotEqual(token1, token3);
            Assert.NotEqual(token2, token3);
        }

        [Fact]
        public void GenerateAccessToken_FileNameExtraction_HandlesForwardSlashes()
        {
            // Arrange
            var service = new FileTokenService(_dataProtection, _memoryCache);

            // Act
            string token = service.GenerateAccessToken("docs", "ctx1", "a/b/c/doc.pdf");
            var payload = service.UnprotectToken(token);

            // Assert
            Assert.NotNull(payload);
            Assert.Equal("doc.pdf", payload.FileName);
        }

        [Fact]
        public void GenerateAccessToken_FileNameExtraction_NoSlashes_UsesEntirePath()
        {
            // Arrange
            var service = new FileTokenService(_dataProtection, _memoryCache);

            // Act
            string token = service.GenerateAccessToken("docs", "ctx1", "simple.txt");
            var payload = service.UnprotectToken(token);

            // Assert
            Assert.NotNull(payload);
            Assert.Equal("simple.txt", payload.FileName);
        }

        [Fact]
        public void FileTokenPayload_Properties_CanGetAndSet()
        {
            // Arrange
            var payload = new FileTokenPayload();
            long nowTicks = DateTime.UtcNow.Ticks;

            // Act
            payload.Category = "invoices";
            payload.ContextId = "inv-123";
            payload.FileName = "invoice.pdf";
            payload.EncryptionKeyBase64 = "c2VjcmV0";
            payload.ExpiryTicks = nowTicks;

            // Assert
            Assert.Equal("invoices", payload.Category);
            Assert.Equal("inv-123", payload.ContextId);
            Assert.Equal("invoice.pdf", payload.FileName);
            Assert.Equal("c2VjcmV0", payload.EncryptionKeyBase64);
            Assert.Equal(nowTicks, payload.ExpiryTicks);
        }
    }
