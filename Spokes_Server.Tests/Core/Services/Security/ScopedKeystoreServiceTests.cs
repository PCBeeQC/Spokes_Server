using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using Microsoft.JSInterop.Infrastructure;
using Moq;
using Spokes_Server.Core.Data.Repositories.Core;
using Spokes_Server.Core.Models.Core;
using Spokes_Server.Core.Data;
using Spokes_Server.Core.Services.Maintenance;
using Spokes_Server.Core.Services.Security;

namespace Spokes_Server.Tests.Core.Services.Security;

public class ScopedKeystoreServiceTests
{
    private readonly IDataProtectionProvider _dataProtection;
    private readonly CryptoService _crypto = new();

    public ScopedKeystoreServiceTests()
    {
        var services = new ServiceCollection();
        services.AddDataProtection().UseEphemeralDataProtectionProvider();
        var sp = services.BuildServiceProvider();
        _dataProtection = sp.GetRequiredService<IDataProtectionProvider>();
    }

    private ScopedKeystoreService CreateService(GlobalKeystoreService? globalKeystore = null) => new(_dataProtection, _crypto, globalKeystore);

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

        var protector = _dataProtection.CreateProtector("ChatVaultKey");
        var protectedPassword = protector.Protect(password);

        var httpContext = new DefaultHttpContext();
        httpContext.Request.Headers["Cookie"] = $"chat_vault_key={protectedPassword}";

        var globalKeystore = new GlobalKeystoreService();
        var service = CreateService(globalKeystore);

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

        var protector = _dataProtection.CreateProtector("ChatVaultKey");
        var protectedPassword = protector.Protect(password);

        var httpContext = new DefaultHttpContext();
        httpContext.Request.Headers["X-Chat-Vault-Key"] = protectedPassword;

        var globalKeystore = new GlobalKeystoreService();
        var service = CreateService(globalKeystore);

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
        var service = CreateService();
        const string password = "VaultPasswordNoGlobal#2026";
        const string employeeId = "EMP-004";
        const string plainKey = "<RSAKeyValue><Modulus>no-global-modulus</Modulus></RSAKeyValue>";

        var kek = _crypto.DeriveKeyFromPassword(password, employeeId);
        var encryptedKey = _crypto.EncryptAes(plainKey, kek);

        var protector = _dataProtection.CreateProtector("ChatVaultKey");
        var protectedPassword = protector.Protect(password);

        var httpContext = new DefaultHttpContext();
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

        var protector = _dataProtection.CreateProtector("ChatVaultKey");
        var protectedPassword = protector.Protect(password);

        using var jsonDoc = JsonDocument.Parse($"{{\"key\":\"{protectedPassword}\"}}");
        var jsonElement = jsonDoc.RootElement.Clone();

        var mockJs = new Mock<IJSRuntime>();
        mockJs
            .Setup(js => js.InvokeAsync<JsonElement>("spokesVault.getStoredVaultKey", It.IsAny<object?[]>()))
            .Returns(new ValueTask<JsonElement>(jsonElement));

        var globalKeystore = new GlobalKeystoreService();
        var service = CreateService(globalKeystore);

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
        await service.ClearDeviceCookieAsync(mockJs.Object, "EMP-009");

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
        var ex = await Record.ExceptionAsync(() => service.ClearDeviceCookieAsync(mockJs.Object, "EMP-010"));

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
        var protectedPassword = _dataProtection.CreateProtector("ChatVaultKey").Protect(password);

        var mockJs = new Mock<IJSRuntime>();
        var globalKeystore = new GlobalKeystoreService();
        var service = CreateService(globalKeystore);

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
}
