using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Spokes_Server.Core.Security;
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
        var service = new FileTokenService(_dataProtection, _memoryCache);
        string contextId = "album456";
        string filePath = "/path/to/my_image.png";
        string encryptionKeyBase64 = "S2V5MTIz";

        string token = service.GenerateAccessToken("albums", contextId, filePath, encryptionKeyBase64, "user-1", "sess-1");

        Assert.False(string.IsNullOrEmpty(token));

        var payload = service.UnprotectToken(token);
        Assert.NotNull(payload);
        Assert.Equal("albums", payload.Category);
        Assert.Equal("album456", payload.ContextId);
        Assert.Equal("my_image.png", payload.FileName);
        Assert.Equal("S2V5MTIz", payload.EncryptionKeyBase64);
        Assert.Equal("user-1", payload.UserId);
        Assert.Equal("sess-1", payload.SessionId);
        Assert.True(payload.ExpiryTicks > DateTime.UtcNow.Ticks);
    }

    [Fact]
    public void GenerateAccessToken_WithoutEncryptionKey_CreatesPayloadWithoutKey()
    {
        var service = new FileTokenService(_dataProtection, _memoryCache);

        string token = service.GenerateAccessToken("albums", "c1", "file.txt", null, "user-1", "sess-1");

        var payload = service.UnprotectToken(token);
        Assert.NotNull(payload);
        Assert.Equal("albums", payload.Category);
        Assert.Equal("c1", payload.ContextId);
        Assert.Equal("file.txt", payload.FileName);
        Assert.Null(payload.EncryptionKeyBase64);
    }

    [Theory]
    [InlineData(null, "sess-1")]
    [InlineData("", "sess-1")]
    [InlineData("   ", "sess-1")]
    [InlineData("user-1", null)]
    [InlineData("user-1", "")]
    [InlineData("user-1", "   ")]
    public void GenerateAccessToken_MissingUserIdOrSessionId_ThrowsArgumentException(string? userId, string? sessionId)
    {
        var service = new FileTokenService(_dataProtection, _memoryCache);

        Assert.Throws<ArgumentException>(() =>
            service.GenerateAccessToken("chat", "ch1", "file.png", "key", userId, sessionId));
    }

    [Fact]
    public void GenerateAccessToken_CachesToken_ReturnsSameValueOnSecondCall()
    {
        var service = new FileTokenService(_dataProtection, _memoryCache);

        string token1 = service.GenerateAccessToken("chat", "channel1", "image.jpg", "key123", "user-1", "sess-1");
        string token2 = service.GenerateAccessToken("chat", "channel1", "image.jpg", "key123", "user-1", "sess-1");

        Assert.Equal(token1, token2);
    }

    [Fact]
    public void UnprotectToken_WithTamperedToken_ReturnsNull()
    {
        var service = new FileTokenService(_dataProtection, _memoryCache);
        var payload = service.UnprotectToken("this-is-not-a-valid-token");
        Assert.Null(payload);
    }

    [Fact]
    public void UnprotectToken_WithDifferentDataProtectionProvider_ReturnsNull()
    {
        var service1 = new FileTokenService(_dataProtection, _memoryCache);
        string token = service1.GenerateAccessToken("chat", "ch1", "file.jpg", "key123", "user-1", "sess-1");

        var services2 = new ServiceCollection();
        services2.AddMemoryCache();
        services2.AddDataProtection().UseEphemeralDataProtectionProvider();
        var sp2 = services2.BuildServiceProvider();
        var service2 = new FileTokenService(sp2.GetRequiredService<IDataProtectionProvider>(), sp2.GetRequiredService<IMemoryCache>());

        var payload = service2.UnprotectToken(token);
        Assert.Null(payload);
    }

    [Fact]
    public void GenerateAccessToken_WithDifferentKeysOrFiles_GeneratesDifferentTokens()
    {
        var service = new FileTokenService(_dataProtection, _memoryCache);

        string token1 = service.GenerateAccessToken("chat", "ch1", "file1.png", "keyA", "user-1", "sess-1");
        string token2 = service.GenerateAccessToken("chat", "ch1", "file2.png", "keyA", "user-1", "sess-1");
        string token3 = service.GenerateAccessToken("chat", "ch1", "file1.png", "keyB", "user-1", "sess-1");

        Assert.NotEqual(token1, token2);
        Assert.NotEqual(token1, token3);
        Assert.NotEqual(token2, token3);
    }

    [Fact]
    public void GenerateAccessToken_FileNameExtraction_HandlesForwardSlashes()
    {
        var service = new FileTokenService(_dataProtection, _memoryCache);

        string token = service.GenerateAccessToken("docs", "ctx1", "a/b/c/doc.pdf", null, "user-1", "sess-1");
        var payload = service.UnprotectToken(token);

        Assert.NotNull(payload);
        Assert.Equal("doc.pdf", payload.FileName);
    }

    [Fact]
    public void GenerateAccessToken_FileNameExtraction_NoSlashes_UsesEntirePath()
    {
        var service = new FileTokenService(_dataProtection, _memoryCache);

        string token = service.GenerateAccessToken("docs", "ctx1", "simple.txt", null, "user-1", "sess-1");
        var payload = service.UnprotectToken(token);

        Assert.NotNull(payload);
        Assert.Equal("simple.txt", payload.FileName);
    }

    [Fact]
    public void FileTokenPayload_Properties_CanGetAndSet()
    {
        var payload = new FileTokenPayload();
        long nowTicks = DateTime.UtcNow.Ticks;

        payload.Category = "invoices";
        payload.ContextId = "inv-123";
        payload.FileName = "invoice.pdf";
        payload.EncryptionKeyBase64 = "c2VjcmV0";
        payload.ExpiryTicks = nowTicks;

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
        string token = service.GenerateAccessToken("chat", "ch1", "file.png", "aesKey123", "user-1", "sess-1");

        var result = service.ValidateAccessToken("chat", "ch1", "file.png", token, "user-1", "sess-1");

        Assert.Equal(FileTokenStatus.Valid, result.Status);
        Assert.True(result.IsSuccess);
        Assert.Equal("aesKey123", result.EncryptionKey);
    }

    [Fact]
    public void ValidateAccessToken_MismatchedContextOrCategory_ReturnsInvalid()
    {
        var service = new FileTokenService(_dataProtection, _memoryCache);
        string token = service.GenerateAccessToken("chat", "ch1", "file.png", "key", "user-1", "sess-1");

        var categoryMismatch = service.ValidateAccessToken("albums", "ch1", "file.png", token, "user-1", "sess-1");
        var contextMismatch = service.ValidateAccessToken("chat", "ch2", "file.png", token, "user-1", "sess-1");
        var fileMismatch = service.ValidateAccessToken("chat", "ch1", "other.png", token, "user-1", "sess-1");

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
        string token = service.GenerateAccessToken("chat", "ch1", "*", "uploadKey", "user-1", "sess-1");

        var result = service.ValidateAccessToken("chat", "ch1", "any_uploaded_file.png", token, "user-1", "sess-1");

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

    [Theory]
    [InlineData(null, "sess-456")]
    [InlineData("", "sess-456")]
    [InlineData("user-123", null)]
    [InlineData("user-123", "")]
    public void ValidateAccessToken_MissingRequestingUserOrSession_ReturnsInvalid(string? reqUserId, string? reqSessionId)
    {
        var service = new FileTokenService(_dataProtection, _memoryCache);
        string token = service.GenerateAccessToken("chat", "ch1", "file.png", "aesKey123", "user-123", "sess-456");

        var result = service.ValidateAccessToken("chat", "ch1", "file.png", token, reqUserId, reqSessionId);

        Assert.Equal(FileTokenStatus.Invalid, result.Status);
        Assert.False(result.IsSuccess);
        Assert.Null(result.EncryptionKey);
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
    public void InvalidateUserTokens_InvalidatesBothEncryptedAndUnencryptedTokensForUser_PreservesOtherUsers()
    {
        var service = new FileTokenService(_dataProtection, _memoryCache);

        string user1EncryptedToken = service.GenerateAccessToken("albums", "alb-1", "pic.jpg", "aesKey123", "user-1", "sess-1");
        string user1UnencryptedToken = service.GenerateAccessToken("albums", "alb-pub", "pub.jpg", null, "user-1", "sess-1");
        string user2EncryptedToken = service.GenerateAccessToken("albums", "alb-2", "pic2.jpg", "aesKey456", "user-2", "sess-2");

        Assert.True(service.ValidateAccessToken("albums", "alb-1", "pic.jpg", user1EncryptedToken, "user-1", "sess-1").IsSuccess);
        Assert.True(service.ValidateAccessToken("albums", "alb-pub", "pub.jpg", user1UnencryptedToken, "user-1", "sess-1").IsSuccess);
        Assert.True(service.ValidateAccessToken("albums", "alb-2", "pic2.jpg", user2EncryptedToken, "user-2", "sess-2").IsSuccess);

        service.InvalidateUserTokens("user-1");

        var afterLockUser1Enc = service.ValidateAccessToken("albums", "alb-1", "pic.jpg", user1EncryptedToken, "user-1", "sess-1");
        var afterLockUser1Unenc = service.ValidateAccessToken("albums", "alb-pub", "pub.jpg", user1UnencryptedToken, "user-1", "sess-1");
        Assert.False(afterLockUser1Enc.IsSuccess);
        Assert.Equal(FileTokenStatus.Invalid, afterLockUser1Enc.Status);
        Assert.False(afterLockUser1Unenc.IsSuccess);
        Assert.Equal(FileTokenStatus.Invalid, afterLockUser1Unenc.Status);

        // user-2's token remains valid
        Assert.True(service.ValidateAccessToken("albums", "alb-2", "pic2.jpg", user2EncryptedToken, "user-2", "sess-2").IsSuccess);

        // Newly generated tokens for user-1 bypass the old dedup cache entry and succeed
        string newUser1Token = service.GenerateAccessToken("albums", "alb-1", "pic.jpg", "aesKey123", "user-1", "sess-1");
        Assert.NotEqual(user1EncryptedToken, newUser1Token);
        string newUser1UnencToken = service.GenerateAccessToken("albums", "alb-pub", "pub.jpg", null, "user-1", "sess-1");
        Assert.NotEqual(user1UnencryptedToken, newUser1UnencToken);
        Assert.True(service.ValidateAccessToken("albums", "alb-1", "pic.jpg", newUser1Token, "user-1", "sess-1").IsSuccess);
        Assert.True(service.ValidateAccessToken("albums", "alb-pub", "pub.jpg", newUser1UnencToken, "user-1", "sess-1").IsSuccess);
    }

    [Fact]
    public void InvalidateContextTokens_InvalidatesOnlyTargetCategoryAndContext()
    {
        var service = new FileTokenService(_dataProtection, _memoryCache);

        string ch1Token = service.GenerateAccessToken("chat", "ch-secret", "secret.png", "aesKey", "user-1", "sess-1");
        string ch2Token = service.GenerateAccessToken("chat", "ch-other", "other.png", "aesKey2", "user-1", "sess-1");

        service.InvalidateContextTokens("chat", "ch-secret");

        Assert.False(service.ValidateAccessToken("chat", "ch-secret", "secret.png", ch1Token, "user-1", "sess-1").IsSuccess);
        Assert.True(service.ValidateAccessToken("chat", "ch-other", "other.png", ch2Token, "user-1", "sess-1").IsSuccess);
    }

    [Fact]
    public void ScopedKeystoreService_Lock_OnUninitializedCircuit_DoesNotTriggerGlobalTokenWipe()
    {
        var service = new FileTokenService(_dataProtection, _memoryCache);
        string otherUserToken = service.GenerateAccessToken("albums", "alb-1", "pic.jpg", "aesKey", "user-other", "sess-other");

        var uninitializedScopedKeystore = new ScopedKeystoreService(
            _dataProtection,
            new CryptoService(),
            new GlobalKeystoreService(),
            fileTokenService: service);

        // Lock() with null employeeId on an uninitialized circuit must NOT wipe tokens globally
        uninitializedScopedKeystore.Lock();

        Assert.True(service.ValidateAccessToken("albums", "alb-1", "pic.jpg", otherUserToken, "user-other", "sess-other").IsSuccess);
    }

    [Fact]
    public void InvalidateUserEncryptedTokens_PersistsWatermarkAcrossServiceRestart_WhenSystemConfigProvided()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"Spokes_Test_FileToken_{Guid.NewGuid()}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var sc = new ServiceCollection();
            var mockConfig = new Moq.Mock<Microsoft.Extensions.Configuration.IConfiguration>();
            mockConfig.Setup(c => c["DataPath"]).Returns(tempDir);
            sc.AddSingleton<Microsoft.Extensions.Configuration.IConfiguration>(mockConfig.Object);
            sc.AddLogging();
            sc.AddSingleton<EncryptionService>();
            sc.AddSpokesDatabase();
            using var sp = sc.BuildServiceProvider();
            var systemConfigRepo = sp.GetRequiredService<Spokes_Server.Core.Data.Repositories.Core.SystemConfigRepository>();

            var serviceBeforeRestart = new FileTokenService(_dataProtection, _memoryCache, systemConfigRepo);
            string encTokenBeforeRestart = serviceBeforeRestart.GenerateAccessToken("albums", "alb-1", "pic.jpg", "aesKey123", "user-1", "sess-1");
            string unencTokenBeforeRestart = serviceBeforeRestart.GenerateAccessToken("albums", "alb-2", "pub.jpg", null, "user-1", "sess-1");
            Assert.True(serviceBeforeRestart.ValidateAccessToken("albums", "alb-1", "pic.jpg", encTokenBeforeRestart, "user-1", "sess-1").IsSuccess);
            Assert.True(serviceBeforeRestart.ValidateAccessToken("albums", "alb-2", "pub.jpg", unencTokenBeforeRestart, "user-1", "sess-1").IsSuccess);

            serviceBeforeRestart.InvalidateUserEncryptedTokens("user-1");

            // Simulate server restart: new FileTokenService instance loaded from persisted SystemConfigRepository
            var serviceAfterRestart = new FileTokenService(_dataProtection, _memoryCache, systemConfigRepo);
            Assert.False(serviceAfterRestart.ValidateAccessToken("albums", "alb-1", "pic.jpg", encTokenBeforeRestart, "user-1", "sess-1").IsSuccess);
            Assert.False(serviceAfterRestart.ValidateAccessToken("albums", "alb-2", "pub.jpg", unencTokenBeforeRestart, "user-1", "sess-1").IsSuccess);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }
    }
}
