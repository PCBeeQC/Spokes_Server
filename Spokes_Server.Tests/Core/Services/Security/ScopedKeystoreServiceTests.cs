using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;
using Microsoft.JSInterop.Infrastructure;
using Moq;
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

    private ScopedKeystoreService CreateService() => new(_dataProtection);

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
            encryptedPrivateKey: "dummy-key",
            crypto: _crypto);

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
            encryptedPrivateKey: "dummy-key",
            crypto: _crypto);

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
            encryptedPrivateKey: "dummy-key",
            crypto: _crypto);

        // Assert
        Assert.False(result);
        Assert.False(service.IsUnlocked);
    }

    [Fact]
    public void TryInitializeFromVaultCookie_WithValidCookie_UnlocksScopedAndGlobalKeystore()
    {
        // Arrange
        var service = CreateService();
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

        // Act
        var result = service.TryInitializeFromVaultCookie(
            httpContext,
            employeeId,
            encryptedKey,
            _crypto,
            globalKeystore);

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
        var service = CreateService();
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

        // Act
        var result = service.TryInitializeFromVaultCookie(
            httpContext,
            employeeId,
            encryptedKey,
            _crypto,
            globalKeystore);

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
        var service = CreateService();
        const string employeeId = "EMP-003";
        const string encryptedKey = "some-encrypted-key";

        var httpContext = new DefaultHttpContext();
        httpContext.Request.Headers["Cookie"] = "chat_vault_key=tampered-or-corrupted-cookie-value";

        var globalKeystore = new GlobalKeystoreService();

        // Act
        var result = service.TryInitializeFromVaultCookie(
            httpContext,
            employeeId,
            encryptedKey,
            _crypto,
            globalKeystore);

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
            encryptedKey,
            _crypto,
            globalKeystore: null);

        // Assert
        Assert.True(result);
        Assert.True(service.IsUnlocked);
        Assert.Equal(plainKey, service.GetPrivateKey());
    }

    [Fact]
    public async Task InitializeFromDeviceCookieAsync_InvokesJsAndUnlocksKeystores()
    {
        // Arrange
        var service = CreateService();
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

        // Act
        await service.InitializeFromDeviceCookieAsync(
            mockJs.Object,
            employeeId,
            encryptedKey,
            _crypto,
            globalKeystore);

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
            "dummy-encrypted-key",
            _crypto,
            null);

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
            "dummy-encrypted-key",
            _crypto,
            null);

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
            "dummy-encrypted-key",
            _crypto,
            null));

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
}
