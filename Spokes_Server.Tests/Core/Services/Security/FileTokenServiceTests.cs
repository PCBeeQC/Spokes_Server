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
    public void InvalidateUserTokens_InvalidatesBothEncryptedAndUnencryptedTokensForUser_PreservesOtherUsers()
    {
        var service = new FileTokenService(_dataProtection, _memoryCache);

        string user1EncryptedToken = service.GenerateAccessToken("albums", "alb-1", "pic.jpg", "aesKey123", "user-1", "sess-1");
        string user1UnencryptedToken = service.GenerateAccessToken("albums", "alb-pub", "pub.jpg", null, "user-1", "sess-1");
        string user2EncryptedToken = service.GenerateAccessToken("albums", "alb-2", "pic2.jpg", "aesKey456", "user-2", "sess-2");

        Assert.True(service.ValidateAccessToken("albums", "alb-1", "pic.jpg", user1EncryptedToken, "user-1", "sess-1").IsSuccess);
        Assert.True(service.ValidateAccessToken("albums", "alb-pub", "pub.jpg", user1UnencryptedToken, "user-1", "sess-1").IsSuccess);
        Assert.True(service.ValidateAccessToken("albums", "alb-2", "pic2.jpg", user2EncryptedToken, "user-2", "sess-2").IsSuccess);

        service.InvalidateUserEncryptedTokens("user-1");

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
            mockConfig.Setup(c => c["SPOKES_MASTER_PASSWORD"]).Returns("UnitTest-Master-Password-2026!");
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

    [Fact]
    public void ValidateAccessToken_RequireTokenTrue_WithNullOrEmptyToken_ReturnsMissingFailure()
    {
        var service = new FileTokenService(_dataProtection, _memoryCache);

        var nullResult = service.ValidateAccessToken("chat", "ch1", "file.png", null, "user-1", "sess-1", requireToken: true);
        var emptyResult = service.ValidateAccessToken("chat", "ch1", "file.png", "   ", "user-1", "sess-1", requireToken: true);

        Assert.Equal(FileTokenStatus.Missing, nullResult.Status);
        Assert.False(nullResult.IsSuccess);
        Assert.Equal(FileTokenStatus.Missing, emptyResult.Status);
        Assert.False(emptyResult.IsSuccess);
    }

    [Fact]
    public void InvalidateUserAndContextTokens_DoNotBustUnrelatedUserOrContextDedupTokens()
    {
        var service = new FileTokenService(_dataProtection, _memoryCache);

        string userAToken = service.GenerateAccessToken("chat", "ch-a", "photo.jpg", null, "user-a", "sess-a");
        string userBToken = service.GenerateAccessToken("chat", "ch-b", "photo.jpg", null, "user-b", "sess-b");

        // Invalidating user-a must NOT rotate user-b's deduplicated cached token
        service.InvalidateUserEncryptedTokens("user-a");

        string userBTokenAfterUserALock = service.GenerateAccessToken("chat", "ch-b", "photo.jpg", null, "user-b", "sess-b");
        Assert.Equal(userBToken, userBTokenAfterUserALock);

        // Invalidating chat/ch-a must NOT rotate chat/ch-b's deduplicated cached token
        service.InvalidateContextTokens("chat", "ch-a");
        string userBTokenAfterOtherCtxRevoke = service.GenerateAccessToken("chat", "ch-b", "photo.jpg", null, "user-b", "sess-b");
        Assert.Equal(userBToken, userBTokenAfterOtherCtxRevoke);

        // Meanwhile user-a's regenerated token IS freshly rotated
        string userARotated = service.GenerateAccessToken("chat", "ch-a", "photo.jpg", null, "user-a", "sess-a");
        Assert.NotEqual(userAToken, userARotated);
        Assert.True(service.ValidateAccessToken("chat", "ch-a", "photo.jpg", userARotated, "user-a", "sess-a", requireToken: true).IsSuccess);
    }

    [Fact]
    public void SignFileUrl_SignsSpokesRoutesPreservingQueryAndFragment_AndLeavesExternalUrlsUntouched()
    {
        var service = new FileTokenService(_dataProtection, _memoryCache);

        var signed = service.SignFileUrl("/spokesapi/files/projects/proj-1/receipt.jpg?s=sm&fn=receipt.jpg#page=1", "emp-1", "sess-1");
        Assert.StartsWith("/spokesapi/files/projects/proj-1/receipt.jpg?", signed);
        Assert.Contains("s=sm", signed);
        Assert.Contains("fn=receipt.jpg", signed);
        Assert.Contains("t=", signed);
        Assert.EndsWith("#page=1", signed);

        // Re-signing replaces stale t= instead of duplicating it
        var resigned = service.SignFileUrl("/spokesapi/files/projects/proj-1/receipt.jpg?t=stale&s=sm", "emp-1", "sess-1");
        Assert.DoesNotContain("t=stale", resigned);
        Assert.Contains("s=sm", resigned);
        Assert.Equal(1, resigned.Split("t=").Length - 1);

        // Non-tokenizable and external routes pass through untouched
        Assert.Equal("https://example.com/pic.png", service.SignFileUrl("https://example.com/pic.png", "emp-1", "sess-1"));
        Assert.Equal("data:image/png;base64,AAA", service.SignFileUrl("data:image/png;base64,AAA", "emp-1", "sess-1"));
        Assert.Equal("/spokesapi/files/demo-media/sample.jpg", service.SignFileUrl("/spokesapi/files/demo-media/sample.jpg", "emp-1", "sess-1"));
        Assert.Equal("/spokesapi/files/email/msg1/att1", service.SignFileUrl("/spokesapi/files/email/msg1/att1", "emp-1", "sess-1"));
    }

    [Fact]
    public void GenerateAccessToken_BinaryV2_IsCompactAndRoundTripsGuidsAndAesKey()
    {
        var service = new FileTokenService(_dataProtection, _memoryCache);
        var channelId = Guid.NewGuid().ToString("D");
        var userId = Guid.NewGuid().ToString("D");
        var sessionId = Guid.NewGuid().ToString("D");
        var aesKeyBytes = new byte[32];
        for (int i = 0; i < aesKeyBytes.Length; i++) aesKeyBytes[i] = (byte)(i * 7 + 3);
        var aesKeyBase64 = Convert.ToBase64String(aesKeyBytes);

        string token = service.GenerateAccessToken(
            "chat",
            channelId,
            "1731982012345_thumb.jpg",
            aesKeyBase64,
            userId,
            sessionId,
            isThumbnail: true);

        // Binary v2 + Base64Url encoding should be dramatically shorter than ~500-char legacy JSON tokens
        Assert.True(token.Length < 300, $"Expected compact binary v2 token (<300 chars), got {token.Length} chars.");

        var payload = service.UnprotectToken(token);
        Assert.NotNull(payload);
        Assert.Equal("chat", payload!.Category);
        Assert.Equal(channelId, payload.ContextId);
        Assert.Equal("1731982012345_thumb.jpg", payload.FileName);
        Assert.Equal(userId, payload.UserId);
        Assert.Equal(sessionId, payload.SessionId);
        Assert.Equal(aesKeyBase64, payload.EncryptionKeyBase64);
        Assert.True(payload.IsThumbnail);

        var validation = service.ValidateAccessToken("chat", channelId, "1731982012345_thumb.jpg", token, userId, sessionId, requireToken: true, isThumbnail: true);
        Assert.Equal(FileTokenStatus.Valid, validation.Status);
        Assert.Equal(aesKeyBase64, validation.EncryptionKey);

        // Thumbnail-scoped token must still be rejected for full-resolution non-thumbnail requests
        var nonThumbValidation = service.ValidateAccessToken("chat", channelId, "1731982012345_thumb.jpg", token, userId, sessionId, requireToken: true, isThumbnail: false);
        Assert.Equal(FileTokenStatus.Invalid, nonThumbValidation.Status);

        // Uppercase GUIDs must round-trip with exact case preserved under Ordinal validation
        var upperId = Guid.NewGuid().ToString("D").ToUpperInvariant();
        string upperToken = service.GenerateAccessToken("chat", upperId, "doc.pdf", null, upperId, upperId);
        var upperPayload = service.UnprotectToken(upperToken);
        Assert.NotNull(upperPayload);
        Assert.Equal(upperId, upperPayload!.ContextId);
        Assert.Equal(upperId, upperPayload.UserId);
        Assert.Equal(upperId, upperPayload.SessionId);
        Assert.Equal(FileTokenStatus.Valid, service.ValidateAccessToken("chat", upperId, "doc.pdf", upperToken, upperId, upperId, requireToken: true).Status);
    }

    [Fact]
    public void UnprotectToken_StillDecodesLegacyJsonV1Tokens_ForRollingUpgradeCompatibility()
    {
        var service = new FileTokenService(_dataProtection, _memoryCache);
        var protector = _dataProtection.CreateProtector("SpokesFileToken");
        var expiryTicks = DateTime.UtcNow.AddHours(1).Ticks;
        var legacyJson = System.Text.Json.JsonSerializer.Serialize(new FileTokenPayload
        {
            Category = "chat",
            ContextId = "legacy-channel-1",
            FileName = "photo.jpg",
            EncryptionKeyBase64 = "bGVnYWN5LWtleQ==",
            ExpiryTicks = expiryTicks,
            UserId = "legacy-user",
            SessionId = "legacy-session",
            IsThumbnail = true,
            TokenSequence = 0
        });
        var legacyToken = protector.Protect(legacyJson);

        var payload = service.UnprotectToken(legacyToken);
        Assert.NotNull(payload);
        Assert.Equal("chat", payload!.Category);
        Assert.Equal("legacy-channel-1", payload.ContextId);
        Assert.Equal("photo.jpg", payload.FileName);
        Assert.Equal("bGVnYWN5LWtleQ==", payload.EncryptionKeyBase64);
        Assert.Equal("legacy-user", payload.UserId);
        Assert.Equal("legacy-session", payload.SessionId);
        Assert.True(payload.IsThumbnail);
    }

    [Fact]
    public void ComputeUserMediaScope_ReturnsDeterministic16CharLowerHex_AndNullForEmpty()
    {
        Assert.Null(FileTokenService.ComputeUserMediaScope(null));
        Assert.Null(FileTokenService.ComputeUserMediaScope(string.Empty));

        var scope1 = FileTokenService.ComputeUserMediaScope("emp-123");
        var scope2 = FileTokenService.ComputeUserMediaScope("emp-123");
        var scopeOther = FileTokenService.ComputeUserMediaScope("emp-456");

        Assert.NotNull(scope1);
        Assert.Equal(16, scope1!.Length);
        Assert.Equal(scope1, scope2);
        Assert.Equal(scope1.ToLowerInvariant(), scope1);
        Assert.NotEqual(scope1, scopeOther);
    }
}
