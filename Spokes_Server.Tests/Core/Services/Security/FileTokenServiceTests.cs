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

        [Fact]
        public void ValidateAccessToken_NullOrEmptyToken_ReturnsNone()
        {
            var service = new FileTokenService(_dataProtection, _memoryCache);

            var nullResult = service.ValidateAccessToken("chat", "ch1", "file.png", null);
            var emptyResult = service.ValidateAccessToken("chat", "ch1", "file.png", string.Empty);

            Assert.Equal(FileTokenStatus.None, nullResult.Status);
            Assert.True(nullResult.IsSuccess);
            Assert.Null(nullResult.EncryptionKey);

            Assert.Equal(FileTokenStatus.None, emptyResult.Status);
            Assert.True(emptyResult.IsSuccess);
        }

        [Fact]
        public void ValidateAccessToken_ValidToken_ReturnsSuccessWithKey()
        {
            var service = new FileTokenService(_dataProtection, _memoryCache);
            string token = service.GenerateAccessToken("chat", "ch1", "file.png", "aesKey123");

            var result = service.ValidateAccessToken("chat", "ch1", "file.png", token);

            Assert.Equal(FileTokenStatus.Valid, result.Status);
            Assert.True(result.IsSuccess);
            Assert.Equal("aesKey123", result.EncryptionKey);
        }

        [Fact]
        public void ValidateAccessToken_MismatchedContextOrCategory_ReturnsInvalid()
        {
            var service = new FileTokenService(_dataProtection, _memoryCache);
            string token = service.GenerateAccessToken("chat", "ch1", "file.png", "key");

            var categoryMismatch = service.ValidateAccessToken("albums", "ch1", "file.png", token);
            var contextMismatch = service.ValidateAccessToken("chat", "ch2", "file.png", token);
            var fileMismatch = service.ValidateAccessToken("chat", "ch1", "other.png", token);

            Assert.Equal(FileTokenStatus.Invalid, categoryMismatch.Status);
            Assert.False(categoryMismatch.IsSuccess);
            Assert.Equal(FileTokenStatus.Invalid, contextMismatch.Status);
            Assert.False(contextMismatch.IsSuccess);
            Assert.Equal(FileTokenStatus.Invalid, fileMismatch.Status);
            Assert.False(fileMismatch.IsSuccess);
        }

        [Fact]
        public void ValidateAccessToken_WildcardFileName_SucceedsForUploadPrelude()
        {
            var service = new FileTokenService(_dataProtection, _memoryCache);
            string token = service.GenerateAccessToken("chat", "ch1", "*", "uploadKey");

            var result = service.ValidateAccessToken("chat", "ch1", "any_uploaded_file.png", token);

            Assert.Equal(FileTokenStatus.Valid, result.Status);
            Assert.True(result.IsSuccess);
            Assert.Equal("uploadKey", result.EncryptionKey);
        }

        [Fact]
        public void ValidateAccessToken_WithMatchingUserIdAndSessionId_ReturnsSuccess()
        {
            var service = new FileTokenService(_dataProtection, _memoryCache);
            string token = service.GenerateAccessToken("chat", "ch1", "file.png", "aesKey123", "user-123", "sess-456");

            var result = service.ValidateAccessToken("chat", "ch1", "file.png", token, "user-123", "sess-456");

            Assert.Equal(FileTokenStatus.Valid, result.Status);
            Assert.True(result.IsSuccess);
            Assert.Equal("aesKey123", result.EncryptionKey);
        }

        [Fact]
        public void ValidateAccessToken_WithMismatchedUserId_ReturnsInvalid()
        {
            var service = new FileTokenService(_dataProtection, _memoryCache);
            string token = service.GenerateAccessToken("chat", "ch1", "file.png", "aesKey123", "user-123", "sess-456");

            var result = service.ValidateAccessToken("chat", "ch1", "file.png", token, "user-imposter", "sess-456");

            Assert.Equal(FileTokenStatus.Invalid, result.Status);
            Assert.False(result.IsSuccess);
            Assert.Null(result.EncryptionKey);
        }

        [Fact]
        public void ValidateAccessToken_WithMismatchedSessionId_ReturnsInvalid()
        {
            var service = new FileTokenService(_dataProtection, _memoryCache);
            string token = service.GenerateAccessToken("chat", "ch1", "file.png", "aesKey123", "user-123", "sess-456");

            var result = service.ValidateAccessToken("chat", "ch1", "file.png", token, "user-123", "sess-other");

            Assert.Equal(FileTokenStatus.Invalid, result.Status);
            Assert.False(result.IsSuccess);
            Assert.Null(result.EncryptionKey);
        }

        [Fact]
        public void ValidateAccessToken_TokenWithoutUserOrSession_SucceedsForAnyUser()
        {
            var service = new FileTokenService(_dataProtection, _memoryCache);
            string token = service.GenerateAccessToken("chat", "ch1", "file.png", "aesKey123");

            var result = service.ValidateAccessToken("chat", "ch1", "file.png", token, "user-any", "sess-any");

            Assert.Equal(FileTokenStatus.Valid, result.Status);
            Assert.True(result.IsSuccess);
            Assert.Equal("aesKey123", result.EncryptionKey);
        }

        [Fact]
        public void GenerateAccessToken_WithUserAndSession_PayloadContainsThem()
        {
            var service = new FileTokenService(_dataProtection, _memoryCache);
            string token = service.GenerateAccessToken("albums", "alb-1", "pic.jpg", "key", "u-1", "s-1");

            var payload = service.UnprotectToken(token);

            Assert.NotNull(payload);
            Assert.Equal("u-1", payload.UserId);
            Assert.Equal("s-1", payload.SessionId);
        }

        [Fact]
        public void InvalidateUserEncryptedTokens_InvalidatesEncryptedTokensForUser_PreservesOtherUsersAndUnencryptedTokens()
        {
            var service = new FileTokenService(_dataProtection, _memoryCache);

            string user1EncryptedToken = service.GenerateAccessToken("albums", "alb-1", "pic.jpg", "aesKey123", "user-1", "sess-1");
            string user1UnencryptedToken = service.GenerateAccessToken("albums", "alb-pub", "pub.jpg", null, "user-1", "sess-1");
            string user2EncryptedToken = service.GenerateAccessToken("albums", "alb-2", "pic2.jpg", "aesKey456", "user-2", "sess-2");

            // Before invalidation, all tokens are valid
            Assert.True(service.ValidateAccessToken("albums", "alb-1", "pic.jpg", user1EncryptedToken, "user-1", "sess-1").IsSuccess);
            Assert.True(service.ValidateAccessToken("albums", "alb-pub", "pub.jpg", user1UnencryptedToken, "user-1", "sess-1").IsSuccess);
            Assert.True(service.ValidateAccessToken("albums", "alb-2", "pic2.jpg", user2EncryptedToken, "user-2", "sess-2").IsSuccess);

            // Lock user-1's vault
            service.InvalidateUserEncryptedTokens("user-1");

            // user-1's previously issued encrypted token is now invalid
            var afterLockUser1 = service.ValidateAccessToken("albums", "alb-1", "pic.jpg", user1EncryptedToken, "user-1", "sess-1");
            Assert.False(afterLockUser1.IsSuccess);
            Assert.Equal(FileTokenStatus.Invalid, afterLockUser1.Status);

            // user-1's unencrypted token and user-2's encrypted token remain valid
            Assert.True(service.ValidateAccessToken("albums", "alb-pub", "pub.jpg", user1UnencryptedToken, "user-1", "sess-1").IsSuccess);
            Assert.True(service.ValidateAccessToken("albums", "alb-2", "pic2.jpg", user2EncryptedToken, "user-2", "sess-2").IsSuccess);

            // When user-1 unlocks their vault again, newly generated tokens succeed (and bypass the old dedup cache entry)
            string newUser1Token = service.GenerateAccessToken("albums", "alb-1", "pic.jpg", "aesKey123", "user-1", "sess-1");
            Assert.NotEqual(user1EncryptedToken, newUser1Token);
            var afterReunlock = service.ValidateAccessToken("albums", "alb-1", "pic.jpg", newUser1Token, "user-1", "sess-1");
            Assert.True(afterReunlock.IsSuccess);
            Assert.Equal("aesKey123", afterReunlock.EncryptionKey);
        }
    }
