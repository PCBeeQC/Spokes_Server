using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using Microsoft.JSInterop.Infrastructure;
using Moq;
using Spokes_Server.Core.Data;
using Spokes_Server.Core.Data.Repositories.Core;
using Spokes_Server.Core.Models.Core;
using Spokes_Server.Core.Security;
using Spokes_Server.Core.Services.Maintenance;
using Spokes_Server.Core.Services.Security;

namespace Spokes_Server.Tests.Core.Services.Security;

public class ScopedKeystoreServiceTests : TestDataTestBase
{
    private readonly IDataProtectionProvider _dataProtection;
    private readonly CryptoService _crypto = new();
    private readonly DiskPersistenceService _persistence;
    private readonly IConfiguration _config;

    public ScopedKeystoreServiceTests()
    {
        var services = new ServiceCollection();
        services.AddDataProtection().UseEphemeralDataProtectionProvider();
        var sp = services.BuildServiceProvider();
        _dataProtection = sp.GetRequiredService<IDataProtectionProvider>();

        _persistence = new DiskPersistenceService(Mock.Of<ILogger<DiskPersistenceService>>());
        _config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { { "DataPath", _testDataPath }, { "SPOKES_MASTER_PASSWORD", "UnitTest-Master-Password-2026!" } })
            .Build();
    }

    public override void Dispose()
    {
        _persistence.Dispose();
        base.Dispose();
    }

    private ScopedKeystoreService CreateService(
        GlobalKeystoreService? globalKeystore = null,
        DeviceSessionRepository? deviceSessions = null,
        AuthenticationStateProvider? authStateProvider = null)
        => new(_dataProtection, _crypto, globalKeystore, deviceSessions: deviceSessions, authStateProvider: authStateProvider);

    private (DeviceSessionRepository Repo, DeviceSession Session, ClaimsPrincipal Principal, string ProtectedToken, AuthenticationStateProvider AuthStateProvider) CreateBoundSession(
        string employeeId,
        string password,
        bool hasVaultCookie = true,
        bool isRevoked = false,
        bool isExpired = false,
        string? tokenEmployeeId = null,
        string? tokenSessionId = null,
        string? tokenNonce = null)
    {
        var repo = new DeviceSessionRepository(_persistence, _config);
        var session = new DeviceSession
        {
            Id = "sess-" + Guid.NewGuid().ToString("N"),
            EmployeeId = employeeId,
            HasVaultCookie = hasVaultCookie,
            VaultNonce = "nonce-" + Guid.NewGuid().ToString("N"),
            RevokedAt = isRevoked ? DateTime.UtcNow : null,
            ExpiresAt = isExpired ? DateTime.UtcNow.AddMinutes(-10) : DateTime.UtcNow.AddDays(7)
        };
        repo.Save(session);

        var principal = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim("SessionId", session.Id),
            new Claim("EmployeeId", employeeId)
        ], "TestAuth"));

        var mockAuth = new Mock<AuthenticationStateProvider>();
        mockAuth.Setup(a => a.GetAuthenticationStateAsync())
            .ReturnsAsync(new AuthenticationState(principal));

        var protectedToken = VaultCookieProtector.Protect(
            _dataProtection,
            tokenEmployeeId ?? employeeId,
            tokenSessionId ?? session.Id,
            tokenNonce ?? session.VaultNonce,
            password);

        return (repo, session, principal, protectedToken, mockAuth.Object);
    }

    [Fact]
    public void InitialState_IsLocked_AndEventsFire()
    {
        // Arrange
        var service = CreateService();
        var unlockedFired = false;
        var lockedFired = false;

        service.OnUnlocked += () => unlockedFired = true;
        service.OnLocked += () => lockedFired = true;

        // Assert initial state
        Assert.False(service.IsUnlocked);
        Assert.Null(service.GetPrivateKey());

        // Act 1: Unlock
        const string testKey = "<RSAKeyValue><Modulus>test</Modulus></RSAKeyValue>";
        service.Unlock(testKey);

        // Assert 1
        Assert.True(service.IsUnlocked);
        Assert.Equal(testKey, service.GetPrivateKey());
        Assert.True(unlockedFired);
        Assert.False(lockedFired);

        // Act 2: Lock
        service.Lock();

        // Assert 2
        Assert.False(service.IsUnlocked);
        Assert.Null(service.GetPrivateKey());
        Assert.True(lockedFired);
    }

    [Fact]
    public void TryInitializeFromVaultCookie_WhenAlreadyUnlocked_ReturnsTrue()
    {
        // Arrange
        var service = CreateService();
        service.Unlock("existing-key");

        // Act
        var result = service.TryInitializeFromVaultCookie(
            httpContext: null,
            employeeId: "emp-1",
            encryptedPrivateKey: "dummy-key");

        // Assert
        Assert.True(result);
        Assert.True(service.IsUnlocked);
        Assert.Equal("existing-key", service.GetPrivateKey());
    }

    [Fact]
    public void TryInitializeFromVaultCookie_WhenHttpContextNull_ReturnsFalse()
    {
        // Arrange
        var service = CreateService();

        // Act
        var result = service.TryInitializeFromVaultCookie(
            httpContext: null,
            employeeId: "emp-1",
            encryptedPrivateKey: "dummy-key");

        // Assert
        Assert.False(result);
        Assert.False(service.IsUnlocked);
        Assert.Null(service.GetPrivateKey());
    }

    [Fact]
    public void TryInitializeFromVaultCookie_WhenCookieOrHeaderMissing_ReturnsFalse()
    {
        // Arrange
        var service = CreateService();
        var httpContext = new DefaultHttpContext();

        // Act
        var result = service.TryInitializeFromVaultCookie(
            httpContext,
            employeeId: "emp-1",
            encryptedPrivateKey: "dummy-key");

        // Assert
        Assert.False(result);
        Assert.False(service.IsUnlocked);
    }

    [Fact]
    public void TryInitializeFromVaultCookie_WithValidCookie_UnlocksScopedAndGlobalKeystore()
    {
        // Arrange
        const string password = "VaultPassword#2026";
        const string employeeId = "EMP-001";
        const string plainKey = "<RSAKeyValue><Modulus>scoped-test-modulus</Modulus></RSAKeyValue>";

        var kek = _crypto.DeriveKeyFromPassword(password, employeeId);
        var encryptedKey = _crypto.EncryptAes(plainKey, kek);

        var (sessionsRepo, _, principal, protectedPassword, _) = CreateBoundSession(employeeId, password);

        var httpContext = new DefaultHttpContext { User = principal };
        httpContext.Request.Headers["Cookie"] = $"chat_vault_key={protectedPassword}";

        var globalKeystore = new GlobalKeystoreService();
        var service = CreateService(globalKeystore, deviceSessions: sessionsRepo);

        // Act
        var result = service.TryInitializeFromVaultCookie(
            httpContext,
            employeeId,
            encryptedKey);

        // Assert
        Assert.True(result);
        Assert.True(service.IsUnlocked);
        Assert.Equal(plainKey, service.GetPrivateKey());
        Assert.True(globalKeystore.IsUserUnlocked(employeeId));
        Assert.Equal(plainKey, globalKeystore.GetPrivateKey(employeeId));
    }

    [Fact]
    public void TryInitializeFromVaultCookie_WithValidHeader_UnlocksSuccessfully()
    {
        // Arrange
        const string password = "VaultPasswordHeader#2026";
        const string employeeId = "EMP-002";
        const string plainKey = "<RSAKeyValue><Modulus>header-test-modulus</Modulus></RSAKeyValue>";

        var kek = _crypto.DeriveKeyFromPassword(password, employeeId);
        var encryptedKey = _crypto.EncryptAes(plainKey, kek);

        var (sessionsRepo, _, principal, protectedPassword, _) = CreateBoundSession(employeeId, password);

        var httpContext = new DefaultHttpContext { User = principal };
        httpContext.Request.Headers["X-Chat-Vault-Key"] = protectedPassword;

        var globalKeystore = new GlobalKeystoreService();
        var service = CreateService(globalKeystore, deviceSessions: sessionsRepo);

        // Act
        var result = service.TryInitializeFromVaultCookie(
            httpContext,
            employeeId,
            encryptedKey);

        // Assert
        Assert.True(result);
        Assert.True(service.IsUnlocked);
        Assert.Equal(plainKey, service.GetPrivateKey());
        Assert.True(globalKeystore.IsUserUnlocked(employeeId));
        Assert.Equal(plainKey, globalKeystore.GetPrivateKey(employeeId));
    }

    [Fact]
    public void TryInitializeFromVaultCookie_WithTamperedCookie_ReturnsFalse()
    {
        // Arrange
        const string employeeId = "EMP-003";
        const string encryptedKey = "some-encrypted-key";

        var httpContext = new DefaultHttpContext();
        httpContext.Request.Headers["Cookie"] = "chat_vault_key=tampered-or-corrupted-cookie-value";

        var globalKeystore = new GlobalKeystoreService();
        var service = CreateService(globalKeystore);

        // Act
        var result = service.TryInitializeFromVaultCookie(
            httpContext,
            employeeId,
            encryptedKey);

        // Assert
        Assert.False(result);
        Assert.False(service.IsUnlocked);
        Assert.Null(service.GetPrivateKey());
        Assert.False(globalKeystore.IsUserUnlocked(employeeId));
    }

    [Fact]
    public void TryInitializeFromVaultCookie_WithoutGlobalKeystore_UnlocksScopedKeystoreOnly()
    {
        // Arrange
        const string password = "VaultPasswordNoGlobal#2026";
        const string employeeId = "EMP-004";
        const string plainKey = "<RSAKeyValue><Modulus>no-global-modulus</Modulus></RSAKeyValue>";

        var kek = _crypto.DeriveKeyFromPassword(password, employeeId);
        var encryptedKey = _crypto.EncryptAes(plainKey, kek);

        var (sessionsRepo, _, principal, protectedPassword, _) = CreateBoundSession(employeeId, password);
        var service = CreateService(deviceSessions: sessionsRepo);

        var httpContext = new DefaultHttpContext { User = principal };
        httpContext.Request.Headers["Cookie"] = $"chat_vault_key={protectedPassword}";

        // Act
        var result = service.TryInitializeFromVaultCookie(
            httpContext,
            employeeId,
            encryptedKey);

        // Assert
        Assert.True(result);
        Assert.True(service.IsUnlocked);
        Assert.Equal(plainKey, service.GetPrivateKey());
    }

    [Fact]
    public async Task InitializeFromDeviceCookieAsync_InvokesJsAndUnlocksKeystores()
    {
        // Arrange
        const string password = "DevicePassword#2026";
        const string employeeId = "EMP-005";
        const string plainKey = "<RSAKeyValue><Modulus>device-test-modulus</Modulus></RSAKeyValue>";

        var kek = _crypto.DeriveKeyFromPassword(password, employeeId);
        var encryptedKey = _crypto.EncryptAes(plainKey, kek);

        var (sessionsRepo, _, _, protectedPassword, authProvider) = CreateBoundSession(employeeId, password);

        using var jsonDoc = JsonDocument.Parse($"{{\"key\":\"{protectedPassword}\"}}");
        var jsonElement = jsonDoc.RootElement.Clone();

        var mockJs = new Mock<IJSRuntime>();
        mockJs
            .Setup(js => js.InvokeAsync<JsonElement>("spokesVault.getStoredVaultKey", It.IsAny<object?[]>()))
            .Returns(new ValueTask<JsonElement>(jsonElement));

        var globalKeystore = new GlobalKeystoreService();
        var service = CreateService(globalKeystore, deviceSessions: sessionsRepo, authStateProvider: authProvider);

        // Act
        await service.InitializeFromDeviceCookieAsync(
            mockJs.Object,
            employeeId,
            encryptedKey);

        // Assert
        Assert.True(service.IsUnlocked);
        Assert.Equal(plainKey, service.GetPrivateKey());
        Assert.True(globalKeystore.IsUserUnlocked(employeeId));
        Assert.Equal(plainKey, globalKeystore.GetPrivateKey(employeeId));

        mockJs.Verify(
            js => js.InvokeAsync<JsonElement>("spokesVault.getStoredVaultKey", It.IsAny<object?[]>()),
            Times.Once);
    }

    [Fact]
    public async Task InitializeFromDeviceCookieAsync_WhenAlreadyUnlocked_DoesNotInvokeJs()
    {
        // Arrange
        var service = CreateService();
        service.Unlock("already-unlocked-key");

        var mockJs = new Mock<IJSRuntime>();

        // Act
        await service.InitializeFromDeviceCookieAsync(
            mockJs.Object,
            "EMP-006",
            "dummy-encrypted-key");

        // Assert
        Assert.True(service.IsUnlocked);
        Assert.Equal("already-unlocked-key", service.GetPrivateKey());
        mockJs.Verify(
            js => js.InvokeAsync<JsonElement>(It.IsAny<string>(), It.IsAny<object?[]>()),
            Times.Never);
    }

    [Fact]
    public async Task InitializeFromDeviceCookieAsync_WhenJsReturnsNullOrMissingKey_VaultStaysLocked()
    {
        // Arrange
        var service = CreateService();
        using var jsonDoc = JsonDocument.Parse("{}");
        var jsonElement = jsonDoc.RootElement.Clone();

        var mockJs = new Mock<IJSRuntime>();
        mockJs
            .Setup(js => js.InvokeAsync<JsonElement>("spokesVault.getStoredVaultKey", It.IsAny<object?[]>()))
            .Returns(new ValueTask<JsonElement>(jsonElement));

        // Act
        await service.InitializeFromDeviceCookieAsync(
            mockJs.Object,
            "EMP-007",
            "dummy-encrypted-key");

        // Assert
        Assert.False(service.IsUnlocked);
        Assert.Null(service.GetPrivateKey());
    }

    [Fact]
    public async Task InitializeFromDeviceCookieAsync_WhenJsThrows_SwallowsExceptionAndVaultStaysLocked()
    {
        // Arrange
        var service = CreateService();
        var mockJs = new Mock<IJSRuntime>();
        mockJs
            .Setup(js => js.InvokeAsync<JsonElement>("spokesVault.getStoredVaultKey", It.IsAny<object?[]>()))
            .ThrowsAsync(new JSException("Device preferences unavailable"));

        // Act
        var ex = await Record.ExceptionAsync(() => service.InitializeFromDeviceCookieAsync(
            mockJs.Object,
            "EMP-008",
            "dummy-encrypted-key"));

        // Assert
        Assert.Null(ex);
        Assert.False(service.IsUnlocked);
    }

    [Fact]
    public async Task ClearDeviceCookieAsync_InvokesClearJsFunction()
    {
        // Arrange
        var service = CreateService();
        var mockJs = new Mock<IJSRuntime>();
        mockJs
            .Setup(js => js.InvokeAsync<IJSVoidResult>("spokesVault.clearStoredVaultKey", It.IsAny<object?[]>()))
            .Returns(new ValueTask<IJSVoidResult>(default(IJSVoidResult)!));

        // Act
        await service.ClearDeviceCookieAsync(mockJs.Object);

        // Assert
        mockJs.Verify(
            js => js.InvokeAsync<IJSVoidResult>("spokesVault.clearStoredVaultKey", It.IsAny<object?[]>()),
            Times.Once);
    }

    [Fact]
    public async Task ClearDeviceCookieAsync_WhenJsThrows_SwallowsException()
    {
        // Arrange
        var service = CreateService();
        var mockJs = new Mock<IJSRuntime>();
        mockJs
            .Setup(js => js.InvokeAsync<IJSVoidResult>("spokesVault.clearStoredVaultKey", It.IsAny<object?[]>()))
            .ThrowsAsync(new JSException("Clear failed in native layer"));

        // Act
        var ex = await Record.ExceptionAsync(() => service.ClearDeviceCookieAsync(mockJs.Object));

        // Assert
        Assert.Null(ex);
    }

    [Fact]
    public void TryInitializeFromVaultCookie_WhenResponseHasStartedAndVaultNotActive_DoesNotThrowAndReturnsFalse()
    {
        // Arrange
        var persistence = new DiskPersistenceService(Mock.Of<ILogger<DiskPersistenceService>>());
        var mockConfig = new Mock<IConfiguration>();
        var sessionsRepo = new DeviceSessionRepository(persistence, mockConfig.Object);
        var session = new DeviceSession
        {
            Id = "session-1",
            EmployeeId = "EMP-001",
            HasVaultCookie = false
        };
        sessionsRepo.Save(session);

        var service = new ScopedKeystoreService(_dataProtection, _crypto, deviceSessions: sessionsRepo);

        var httpContext = new DefaultHttpContext();
        var claims = new[] { new System.Security.Claims.Claim("SessionId", "session-1") };
        httpContext.User = new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity(claims, "test"));
        httpContext.Request.Headers["Cookie"] = "chat_vault_key=dummy-cookie-value";

        var mockResponseFeature = new Mock<Microsoft.AspNetCore.Http.Features.IHttpResponseFeature>();
        mockResponseFeature.Setup(f => f.HasStarted).Returns(true);
        mockResponseFeature.Setup(f => f.Headers).Returns(new HeaderDictionary());
        httpContext.Features.Set<Microsoft.AspNetCore.Http.Features.IHttpResponseFeature>(mockResponseFeature.Object);
        Assert.True(httpContext.Response.HasStarted);

        // Act & Assert (must not throw InvalidOperationException: Headers are read-only, response has already started)
        var result = service.TryInitializeFromVaultCookie(
            httpContext,
            "EMP-001",
            "dummy-encrypted-key");

        Assert.False(result);
        Assert.False(service.IsUnlocked);
    }

    [Fact]
    public void TryInitializeFromVaultCookie_WhenCookieMissing_DoesNotAttemptDeleteEvenIfResponseStarted()
    {
        // Arrange
        var persistence = new DiskPersistenceService(Mock.Of<ILogger<DiskPersistenceService>>());
        var mockConfig = new Mock<IConfiguration>();
        var sessionsRepo = new DeviceSessionRepository(persistence, mockConfig.Object);
        var session = new DeviceSession
        {
            Id = "session-2",
            EmployeeId = "EMP-002",
            HasVaultCookie = false
        };
        sessionsRepo.Save(session);

        var service = new ScopedKeystoreService(_dataProtection, _crypto, deviceSessions: sessionsRepo);

        var httpContext = new DefaultHttpContext();
        var claims = new[] { new System.Security.Claims.Claim("SessionId", "session-2") };
        httpContext.User = new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity(claims, "test"));
        // No chat_vault_key cookie

        var mockResponseFeature = new Mock<Microsoft.AspNetCore.Http.Features.IHttpResponseFeature>();
        mockResponseFeature.Setup(f => f.HasStarted).Returns(true);
        mockResponseFeature.Setup(f => f.Headers).Returns(new HeaderDictionary());
        httpContext.Features.Set<Microsoft.AspNetCore.Http.Features.IHttpResponseFeature>(mockResponseFeature.Object);
        Assert.True(httpContext.Response.HasStarted);

        // Act
        var result = service.TryInitializeFromVaultCookie(
            httpContext,
            "EMP-002",
            "dummy-encrypted-key");

        Assert.False(result);
        Assert.False(service.IsUnlocked);
    }

    [Fact]
    public async Task BeginExternalDeviceCookieInit_CoordinatesConcurrentCallersWithoutDuplicateJsRoundTrips()
    {
        const string password = "BatchedDevicePassword#2026";
        const string employeeId = "EMP-BATCH-01";
        const string plainKey = "<RSAKeyValue><Modulus>batched-device-modulus</Modulus></RSAKeyValue>";

        var kek = _crypto.DeriveKeyFromPassword(password, employeeId);
        var encryptedKey = _crypto.EncryptAes(plainKey, kek);
        var (sessionsRepo, _, _, protectedPassword, authProvider) = CreateBoundSession(employeeId, password);

        var mockJs = new Mock<IJSRuntime>();
        var globalKeystore = new GlobalKeystoreService();
        var service = CreateService(globalKeystore, deviceSessions: sessionsRepo, authStateProvider: authProvider);

        Task childWaiterTask;
        using (var externalScope = service.BeginExternalDeviceCookieInit())
        {
            // Child component (e.g. Chat.razor) awaits InitializeFromDeviceCookieAsync while MainLayout initLayout is in flight
            childWaiterTask = service.InitializeFromDeviceCookieAsync(mockJs.Object, employeeId, encryptedKey);
            Assert.False(childWaiterTask.IsCompleted);

            await externalScope.CompleteAsync(mockJs.Object, protectedPassword, employeeId, encryptedKey);
        }

        await childWaiterTask;

        Assert.True(service.IsUnlocked);
        Assert.Equal(plainKey, service.GetPrivateKey());
        Assert.True(globalKeystore.IsUserUnlocked(employeeId));
        mockJs.Verify(
            js => js.InvokeAsync<JsonElement>("spokesVault.getStoredVaultKey", It.IsAny<object?[]>()),
            Times.Never);
    }

    [Fact]
    public async Task BeginExternalDeviceCookieInit_WhenDisposedWithoutCompletion_AllowsFallbackJsCheck()
    {
        var service = CreateService();
        using var jsonDoc = JsonDocument.Parse("{}");
        var jsonElement = jsonDoc.RootElement.Clone();

        var mockJs = new Mock<IJSRuntime>();
        mockJs
            .Setup(js => js.InvokeAsync<JsonElement>("spokesVault.getStoredVaultKey", It.IsAny<object?[]>()))
            .Returns(new ValueTask<JsonElement>(jsonElement));

        using (var externalScope = service.BeginExternalDeviceCookieInit())
        {
            // Simulate SpokesBridge.initLayout throwing before CompleteAsync is called
        }

        await service.InitializeFromDeviceCookieAsync(mockJs.Object, "EMP-FALLBACK", "dummy-encrypted-key");
        // Second call within the same circuit session is deduplicated
        await service.InitializeFromDeviceCookieAsync(mockJs.Object, "EMP-FALLBACK", "dummy-encrypted-key");

        mockJs.Verify(
            js => js.InvokeAsync<JsonElement>("spokesVault.getStoredVaultKey", It.IsAny<object?[]>()),
            Times.Once);
    }

    [Fact]
    public void TryInitializeFromVaultCookie_RejectsLegacyUnboundTokenAndFailsClosedWithoutSession()
    {
        const string password = "LegacyPassword#2026";
        const string employeeId = "EMP-LEGACY";
        const string plainKey = "<RSAKeyValue><Modulus>legacy-modulus</Modulus></RSAKeyValue>";
        var encryptedKey = _crypto.EncryptAes(plainKey, _crypto.DeriveKeyFromPassword(password, employeeId));

        // 1. Legacy unbound "ChatVaultKey" payload must be rejected even with valid active session
        var (sessionsRepo, _, principal, validBoundToken, _) = CreateBoundSession(employeeId, password);
        var legacyProtected = _dataProtection.CreateProtector("ChatVaultKey").Protect(password);
        var serviceWithSessions = CreateService(deviceSessions: sessionsRepo);

        var legacyHttp = new DefaultHttpContext { User = principal };
        legacyHttp.Request.Headers["Cookie"] = $"chat_vault_key={legacyProtected}";
        Assert.False(serviceWithSessions.TryInitializeFromVaultCookie(legacyHttp, employeeId, encryptedKey));
        Assert.False(serviceWithSessions.IsUnlocked);

        // 2. Valid v2 token must fail closed when DeviceSessionRepository is unavailable
        var serviceNoRepo = CreateService(deviceSessions: null);
        var validHttp = new DefaultHttpContext { User = principal };
        validHttp.Request.Headers["Cookie"] = $"chat_vault_key={validBoundToken}";
        Assert.False(serviceNoRepo.TryInitializeFromVaultCookie(validHttp, employeeId, encryptedKey));
        Assert.False(serviceNoRepo.IsUnlocked);
    }

    [Theory]
    [InlineData(false, false, false, false, false)] // HasVaultCookie == false
    [InlineData(true, false, false, false, true)]   // Mismatched EmployeeId
    [InlineData(true, true, false, false, false)]   // Revoked session
    [InlineData(true, false, true, false, false)]   // Expired session
    [InlineData(true, false, false, true, false)]   // Rotated/mismatched VaultNonce
    public void TryInitializeFromVaultCookie_InvalidSessionOrBinding_ReturnsFalse(
        bool hasVaultCookie,
        bool isRevoked,
        bool isExpired,
        bool wrongNonce,
        bool wrongEmployee)
    {
        const string password = "BoundPassword#2026";
        const string employeeId = "EMP-BOUND";
        const string plainKey = "<RSAKeyValue><Modulus>bound-modulus</Modulus></RSAKeyValue>";
        var encryptedKey = _crypto.EncryptAes(plainKey, _crypto.DeriveKeyFromPassword(password, employeeId));

        var (sessionsRepo, _, principal, protectedToken, _) = CreateBoundSession(
            employeeId,
            password,
            hasVaultCookie: hasVaultCookie,
            isRevoked: isRevoked,
            isExpired: isExpired,
            tokenNonce: wrongNonce ? "stale-nonce" : null,
            tokenEmployeeId: wrongEmployee ? "EMP-OTHER" : null);

        var service = CreateService(deviceSessions: sessionsRepo);
        var httpContext = new DefaultHttpContext { User = principal };
        httpContext.Request.Headers["Cookie"] = $"chat_vault_key={protectedToken}";

        var unlocked = service.TryInitializeFromVaultCookie(httpContext, employeeId, encryptedKey);

        Assert.False(unlocked);
        Assert.False(service.IsUnlocked);
        Assert.Null(service.GetPrivateKey());
    }

    [Fact]
    public async Task InitializeFromDeviceCookieAsync_RotatedVaultNonce_ClearsDeviceCookieAndStaysLocked()
    {
        const string password = "DeviceNoncePassword#2026";
        const string employeeId = "EMP-DEV-NONCE";
        const string plainKey = "<RSAKeyValue><Modulus>device-nonce-modulus</Modulus></RSAKeyValue>";
        var encryptedKey = _crypto.EncryptAes(plainKey, _crypto.DeriveKeyFromPassword(password, employeeId));

        var (sessionsRepo, session, _, staleToken, authProvider) = CreateBoundSession(employeeId, password);
        session.VaultNonce = "rotated-nonce";
        sessionsRepo.Save(session);

        using var jsonDoc = JsonDocument.Parse($"{{\"key\":\"{staleToken}\"}}");
        var mockJs = new Mock<IJSRuntime>();
        mockJs
            .Setup(js => js.InvokeAsync<JsonElement>("spokesVault.getStoredVaultKey", It.IsAny<object?[]>()))
            .Returns(new ValueTask<JsonElement>(jsonDoc.RootElement.Clone()));
        mockJs
            .Setup(js => js.InvokeAsync<IJSVoidResult>("spokesVault.clearStoredVaultKey", It.IsAny<object?[]>()))
            .Returns(new ValueTask<IJSVoidResult>(default(IJSVoidResult)!));

        var service = CreateService(deviceSessions: sessionsRepo, authStateProvider: authProvider);

        await service.InitializeFromDeviceCookieAsync(mockJs.Object, employeeId, encryptedKey);

        Assert.False(service.IsUnlocked);
        mockJs.Verify(
            js => js.InvokeAsync<IJSVoidResult>("spokesVault.clearStoredVaultKey", It.IsAny<object?[]>()),
            Times.Once);
    }

    [Fact]
    public void TryInitializeFromVaultCookie_WhenGlobalKeystoreAlreadyVerified_SkipsRedundantKeyDerivationAndRejectsWrongPassword()
    {
        const string password = "FastVerifierPassword#2026";
        const string employeeId = "EMP-FAST-VERIFIER";
        const string plainKey = "<RSAKeyValue><Modulus>fast-verifier-modulus</Modulus></RSAKeyValue>";

        var kek = _crypto.DeriveKeyFromPassword(password, employeeId);
        var encryptedKey = _crypto.EncryptAes(plainKey, kek);
        var (sessionsRepo, _, principal, protectedToken, _) = CreateBoundSession(employeeId, password);

        var globalKeystore = new GlobalKeystoreService();
        var mockCrypto = new Mock<ICryptoService>();
        mockCrypto.Setup(c => c.DeriveKeyFromPassword(password, employeeId, It.IsAny<int>())).Returns(kek);
        mockCrypto.Setup(c => c.DecryptAes(encryptedKey, kek)).Returns(plainKey);

        // First circuit unlocks via PBKDF2 once and populates GlobalKeystoreService verifier
        var firstCircuitKeystore = new ScopedKeystoreService(
            _dataProtection,
            mockCrypto.Object,
            globalKeystore,
            deviceSessions: sessionsRepo);
        var httpContext1 = new DefaultHttpContext { User = principal };
        httpContext1.Request.Headers["Cookie"] = $"chat_vault_key={protectedToken}";

        Assert.True(firstCircuitKeystore.TryInitializeFromVaultCookie(httpContext1, employeeId, encryptedKey));
        Assert.Equal(plainKey, firstCircuitKeystore.GetPrivateKey());
        Assert.True(globalKeystore.IsUserUnlocked(employeeId));
        mockCrypto.Verify(c => c.DeriveKeyFromPassword(password, employeeId, It.IsAny<int>()), Times.Once);

        // Second circuit on the same validated session unlocks via constant-time HMAC verifier without re-running PBKDF2
        var secondCircuitKeystore = new ScopedKeystoreService(
            _dataProtection,
            mockCrypto.Object,
            globalKeystore,
            deviceSessions: sessionsRepo);
        var httpContext2 = new DefaultHttpContext { User = principal };
        httpContext2.Request.Headers["Cookie"] = $"chat_vault_key={protectedToken}";

        Assert.True(secondCircuitKeystore.TryInitializeFromVaultCookie(httpContext2, employeeId, encryptedKey));
        Assert.Equal(plainKey, secondCircuitKeystore.GetPrivateKey());
        mockCrypto.Verify(c => c.DeriveKeyFromPassword(password, employeeId, It.IsAny<int>()), Times.Once);

        // Wrong password token on another session must not match the verifier and must fail closed
        var (wrongSessionsRepo, _, wrongPrincipal, wrongProtectedToken, _) = CreateBoundSession(employeeId, "WrongPassword#999");
        var wrongCircuitKeystore = new ScopedKeystoreService(
            _dataProtection,
            _crypto,
            globalKeystore,
            deviceSessions: wrongSessionsRepo);
        var wrongHttpContext = new DefaultHttpContext { User = wrongPrincipal };
        wrongHttpContext.Request.Headers["Cookie"] = $"chat_vault_key={wrongProtectedToken}";

        Assert.False(wrongCircuitKeystore.TryInitializeFromVaultCookie(wrongHttpContext, employeeId, encryptedKey));
        Assert.False(wrongCircuitKeystore.IsUnlocked);
    }

    [Fact]
    public void GetOrDecryptKey_AndDecryptRsaWithCachedKey_CachePerCircuitAndClearOnLockAndDispose()
    {
        var (pubKey, privKey) = _crypto.GenerateRsaKeyPair();
        var plainAesKey = _crypto.GenerateAesKeyBase64();
        var cipherAesKey = _crypto.EncryptRsa(plainAesKey, pubKey);

        using var service = CreateService();
        service.Unlock(privKey, "emp-cache");

        int decryptorCalls = 0;
        var first = service.GetOrDecryptKey("alb:1:emp-cache", _ =>
        {
            decryptorCalls++;
            return service.DecryptRsaWithCachedKey(cipherAesKey);
        });
        var second = service.GetOrDecryptKey("alb:1:emp-cache", _ =>
        {
            decryptorCalls++;
            return service.DecryptRsaWithCachedKey(cipherAesKey);
        });

        Assert.Equal(plainAesKey, first);
        Assert.Equal(plainAesKey, second);
        Assert.Equal(1, decryptorCalls);

        // Lock clears both cached symmetric keys and cached RSA handle
        service.Lock("emp-cache");
        Assert.Null(service.DecryptRsaWithCachedKey(cipherAesKey));

        service.Unlock(privKey, "emp-cache");
        var afterReunlock = service.GetOrDecryptKey("alb:1:emp-cache", _ =>
        {
            decryptorCalls++;
            return service.DecryptRsaWithCachedKey(cipherAesKey);
        });
        Assert.Equal(plainAesKey, afterReunlock);
        Assert.Equal(2, decryptorCalls);
    }

    [Fact]
    public void TryInitializeFromVaultCookie_LegacyV1SessionWithoutVaultNonce_UpgradesOnceOnHttpAndRejectsFutureV1()
    {
        const string password = "LegacyUpgradePassword#2026";
        const string employeeId = "EMP-LEGACY-UPGRADE";
        const string plainKey = "<RSAKeyValue><Modulus>legacy-upgrade-modulus</Modulus></RSAKeyValue>";
        var encryptedKey = _crypto.EncryptAes(plainKey, _crypto.DeriveKeyFromPassword(password, employeeId));

        var sessionsRepo = new DeviceSessionRepository(_persistence, _config);
        var legacySession = new DeviceSession
        {
            Id = "sess-legacy-" + Guid.NewGuid().ToString("N"),
            EmployeeId = employeeId,
            HasVaultCookie = true,
            VaultNonce = string.Empty, // Matches server-v2026.11.0 JSON on disk
            ExpiresAt = DateTime.UtcNow.AddDays(60)
        };
        sessionsRepo.Save(legacySession);

        var principal = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim("SessionId", legacySession.Id),
            new Claim("EmployeeId", employeeId)
        ], "TestAuth"));
        var legacyToken = _dataProtection.CreateProtector(VaultCookieProtector.LegacyProtectorPurpose).Protect(password);

        // 1. First HTTP request unlocks via one-time v1 upgrade, persists a VaultNonce, and emits a v2 Set-Cookie
        var service = CreateService(deviceSessions: sessionsRepo);
        var httpContext = new DefaultHttpContext { User = principal };
        httpContext.Request.Headers["Cookie"] = $"chat_vault_key={legacyToken}";

        Assert.True(service.TryInitializeFromVaultCookie(httpContext, employeeId, encryptedKey));
        Assert.True(service.IsUnlocked);
        Assert.Equal(plainKey, service.GetPrivateKey());
        Assert.False(service.HasPendingHttpCookieRenewal);

        var upgradedSession = sessionsRepo.GetById(legacySession.Id)!;
        Assert.False(string.IsNullOrEmpty(upgradedSession.VaultNonce));
        var setCookie = httpContext.Response.Headers.SetCookie.ToString();
        Assert.Contains("chat_vault_key=", setCookie);
        var upgradedCookieValue = setCookie
            .Split("chat_vault_key=", 2)[1]
            .Split(';', 2)[0];
        var validated = VaultCookieProtector.TryUnprotectAndValidate(
            _dataProtection,
            Uri.UnescapeDataString(upgradedCookieValue),
            employeeId,
            legacySession.Id,
            upgradedSession.VaultNonce);
        Assert.NotNull(validated);
        Assert.Equal(password, validated!.Payload.Password);
        Assert.False(validated.NeedsRenewal);

        // 2. Now that VaultNonce is persisted, replaying the old v1 token is permanently rejected
        var replayService = CreateService(deviceSessions: sessionsRepo);
        var replayHttp = new DefaultHttpContext { User = principal };
        replayHttp.Request.Headers["Cookie"] = $"chat_vault_key={legacyToken}";
        Assert.False(replayService.TryInitializeFromVaultCookie(replayHttp, employeeId, encryptedKey));
        Assert.False(replayService.IsUnlocked);
    }

    [Fact]
    public async Task TryInitializeFromVaultCookie_WhenResponseHasStartedAndRenewalNeeded_DefersToJsRenew()
    {
        const string password = "SlidingWsPassword#2026";
        const string employeeId = "EMP-WS-RENEW";
        const string plainKey = "<RSAKeyValue><Modulus>ws-renew-modulus</Modulus></RSAKeyValue>";
        var encryptedKey = _crypto.EncryptAes(plainKey, _crypto.DeriveKeyFromPassword(password, employeeId));

        var sessionsRepo = new DeviceSessionRepository(_persistence, _config);
        var session = new DeviceSession
        {
            Id = "sess-ws-" + Guid.NewGuid().ToString("N"),
            EmployeeId = employeeId,
            HasVaultCookie = true,
            VaultNonce = "nonce-ws-1",
            ExpiresAt = DateTime.UtcNow.AddDays(60)
        };
        sessionsRepo.Save(session);

        var principal = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim("SessionId", session.Id),
            new Claim("EmployeeId", employeeId)
        ], "TestAuth"));

        // Token with 20 days remaining (< 45-day sliding renewal window)
        var agingToken = VaultCookieProtector.Protect(
            _dataProtection,
            employeeId,
            session.Id,
            session.VaultNonce,
            password,
            lifetime: TimeSpan.FromDays(20));

        var httpContext = new DefaultHttpContext { User = principal };
        httpContext.Request.Headers["Cookie"] = $"chat_vault_key={agingToken}";
        var mockResponseFeature = new Mock<Microsoft.AspNetCore.Http.Features.IHttpResponseFeature>();
        mockResponseFeature.Setup(f => f.HasStarted).Returns(true);
        mockResponseFeature.Setup(f => f.Headers).Returns(new HeaderDictionary());
        httpContext.Features.Set<Microsoft.AspNetCore.Http.Features.IHttpResponseFeature>(mockResponseFeature.Object);

        var service = CreateService(deviceSessions: sessionsRepo);
        Assert.True(service.TryInitializeFromVaultCookie(httpContext, employeeId, encryptedKey));
        Assert.True(service.IsUnlocked);
        Assert.True(service.HasPendingHttpCookieRenewal);

        var mockJs = new Mock<IJSRuntime>();
        mockJs
            .Setup(js => js.InvokeAsync<IJSVoidResult>("spokesVault.renew", It.IsAny<object?[]>()))
            .Returns(new ValueTask<IJSVoidResult>(default(IJSVoidResult)!));

        await service.RenewPendingHttpCookieAsync(mockJs.Object);
        Assert.False(service.HasPendingHttpCookieRenewal);

        // Second call is a no-op
        await service.RenewPendingHttpCookieAsync(mockJs.Object);
        mockJs.Verify(js => js.InvokeAsync<IJSVoidResult>("spokesVault.renew", It.IsAny<object?[]>()), Times.Once);
    }

    [Fact]
    public async Task InitializeFromDeviceCookieAsync_LegacyV1AndAgingV2_RefreshStoredMobileKey()
    {
        const string password = "MobileRefreshPassword#2026";
        const string employeeId = "EMP-MOB-REFRESH";
        const string plainKey = "<RSAKeyValue><Modulus>mobile-refresh-modulus</Modulus></RSAKeyValue>";
        var encryptedKey = _crypto.EncryptAes(plainKey, _crypto.DeriveKeyFromPassword(password, employeeId));

        var sessionsRepo = new DeviceSessionRepository(_persistence, _config);
        var session = new DeviceSession
        {
            Id = "sess-mob-" + Guid.NewGuid().ToString("N"),
            EmployeeId = employeeId,
            HasVaultCookie = true,
            VaultNonce = string.Empty,
            ExpiresAt = DateTime.UtcNow.AddDays(60)
        };
        sessionsRepo.Save(session);

        var principal = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim("SessionId", session.Id),
            new Claim("EmployeeId", employeeId)
        ], "TestAuth"));
        var mockAuth = new Mock<AuthenticationStateProvider>();
        mockAuth.Setup(a => a.GetAuthenticationStateAsync()).ReturnsAsync(new AuthenticationState(principal));

        // 1. Legacy v1 stored key upgrades once and writes a fresh 90-day v2 token to Capacitor Preferences
        var legacyToken = _dataProtection.CreateProtector(VaultCookieProtector.LegacyProtectorPurpose).Protect(password);
        using var legacyJson = JsonDocument.Parse($"{{\"key\":\"{legacyToken}\"}}");

        string? storedAfterUpgrade = null;
        var mockJs1 = new Mock<IJSRuntime>();
        mockJs1
            .Setup(js => js.InvokeAsync<JsonElement>("spokesVault.getStoredVaultKey", It.IsAny<object?[]>()))
            .Returns(new ValueTask<JsonElement>(legacyJson.RootElement.Clone()));
        mockJs1
            .Setup(js => js.InvokeAsync<IJSVoidResult>("spokesVault.storeVaultKey", It.IsAny<object?[]>()))
            .Callback<string, object?[]>((_, args) => storedAfterUpgrade = args?[0]?.ToString())
            .Returns(new ValueTask<IJSVoidResult>(default(IJSVoidResult)!));

        var service1 = CreateService(deviceSessions: sessionsRepo, authStateProvider: mockAuth.Object);
        await service1.InitializeFromDeviceCookieAsync(mockJs1.Object, employeeId, encryptedKey);

        Assert.True(service1.IsUnlocked);
        Assert.False(string.IsNullOrEmpty(storedAfterUpgrade));
        var persistedSession = sessionsRepo.GetById(session.Id)!;
        Assert.False(string.IsNullOrEmpty(persistedSession.VaultNonce));
        var validatedUpgrade = VaultCookieProtector.TryUnprotectAndValidate(
            _dataProtection,
            storedAfterUpgrade!,
            employeeId,
            session.Id,
            persistedSession.VaultNonce);
        Assert.NotNull(validatedUpgrade);
        Assert.False(validatedUpgrade!.NeedsRenewal);

        // 2. An aging v2 token (< 45 days left) slides seamlessly while keeping the same VaultNonce
        var originalNonce = persistedSession.VaultNonce;
        var agingV2 = VaultCookieProtector.Protect(
            _dataProtection,
            employeeId,
            session.Id,
            originalNonce,
            password,
            lifetime: TimeSpan.FromDays(10));
        using var agingJson = JsonDocument.Parse($"{{\"key\":\"{agingV2}\"}}");

        string? storedAfterSlide = null;
        var mockJs2 = new Mock<IJSRuntime>();
        mockJs2
            .Setup(js => js.InvokeAsync<JsonElement>("spokesVault.getStoredVaultKey", It.IsAny<object?[]>()))
            .Returns(new ValueTask<JsonElement>(agingJson.RootElement.Clone()));
        mockJs2
            .Setup(js => js.InvokeAsync<IJSVoidResult>("spokesVault.storeVaultKey", It.IsAny<object?[]>()))
            .Callback<string, object?[]>((_, args) => storedAfterSlide = args?[0]?.ToString())
            .Returns(new ValueTask<IJSVoidResult>(default(IJSVoidResult)!));

        var service2 = CreateService(deviceSessions: sessionsRepo, authStateProvider: mockAuth.Object);
        await service2.InitializeFromDeviceCookieAsync(mockJs2.Object, employeeId, encryptedKey);

        Assert.True(service2.IsUnlocked);
        Assert.False(string.IsNullOrEmpty(storedAfterSlide));
        Assert.Equal(originalNonce, sessionsRepo.GetById(session.Id)!.VaultNonce);
        var validatedSlide = VaultCookieProtector.TryUnprotectAndValidate(
            _dataProtection,
            storedAfterSlide!,
            employeeId,
            session.Id,
            originalNonce);
        Assert.NotNull(validatedSlide);
        Assert.False(validatedSlide!.NeedsRenewal);
    }
}
