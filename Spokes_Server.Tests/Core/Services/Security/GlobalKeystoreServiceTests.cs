using Moq;
using Spokes_Server.Core.Services.Security;

namespace Spokes_Server.Tests.Core.Services.Security;

public class GlobalKeystoreServiceTests
{
    private readonly GlobalKeystoreService _sut = new();
    private readonly CryptoService _cryptoService = new();

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("non-existent-employee")]
    public void IsUserUnlocked_InitiallyOrNull_ReturnsFalse(string? employeeId)
    {
        // Act
        var result = _sut.IsUserUnlocked(employeeId!);

        // Assert
        Assert.False(result);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("non-existent-employee")]
    public void GetPrivateKey_InitiallyOrNull_ReturnsNull(string? employeeId)
    {
        // Act
        var result = _sut.GetPrivateKey(employeeId!);

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public void UnlockVault_WithValidPasswordAndRsaXml_StoresKeyAndReturnsTrue()
    {
        // Arrange
        const string employeeId = "emp-001";
        const string password = "ValidPassword123!";
        var (_, privateKey) = _cryptoService.GenerateRsaKeyPair();
        var kek = _cryptoService.DeriveKeyFromPassword(password, employeeId);
        var encryptedPrivateKey = _cryptoService.EncryptAes(privateKey, kek);

        // Act
        var result = _sut.UnlockVault(employeeId, encryptedPrivateKey, password, _cryptoService);

        // Assert
        Assert.True(result);
        Assert.True(_sut.IsUserUnlocked(employeeId));
        Assert.Equal(privateKey, _sut.GetPrivateKey(employeeId));
    }

    [Fact]
    public void UnlockVault_WithInvalidPassword_ReturnsFalse()
    {
        // Arrange
        const string employeeId = "emp-002";
        const string password = "CorrectPassword123!";
        const string wrongPassword = "WrongPassword999!";
        var (_, privateKey) = _cryptoService.GenerateRsaKeyPair();
        var kek = _cryptoService.DeriveKeyFromPassword(password, employeeId);
        var encryptedPrivateKey = _cryptoService.EncryptAes(privateKey, kek);

        // Act
        var result = _sut.UnlockVault(employeeId, encryptedPrivateKey, wrongPassword, _cryptoService);

        // Assert
        Assert.False(result);
        Assert.False(_sut.IsUserUnlocked(employeeId));
        Assert.Null(_sut.GetPrivateKey(employeeId));
    }

    [Fact]
    public void UnlockVault_WhenDecryptedContentDoesNotContainRsaKeyValue_ReturnsFalse()
    {
        // Arrange
        const string employeeId = "emp-003";
        const string password = "TestPassword!";
        const string nonRsaContent = "Plain text secret without RSA tag";
        var kek = _cryptoService.DeriveKeyFromPassword(password, employeeId);
        var encryptedContent = _cryptoService.EncryptAes(nonRsaContent, kek);

        // Act
        var result = _sut.UnlockVault(employeeId, encryptedContent, password, _cryptoService);

        // Assert
        Assert.False(result);
        Assert.False(_sut.IsUserUnlocked(employeeId));
        Assert.Null(_sut.GetPrivateKey(employeeId));
    }

    [Fact]
    public void UnlockVault_WhenCryptoServiceThrows_ReturnsFalse()
    {
        // Arrange
        const string employeeId = "emp-004";
        var mockCrypto = new Mock<ICryptoService>();
        mockCrypto
            .Setup(c => c.DeriveKeyFromPassword(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>()))
            .Throws(new System.Security.Cryptography.CryptographicException("KDF failure"));

        // Act
        var result = _sut.UnlockVault(employeeId, "any-encrypted-data", "any-password", mockCrypto.Object);

        // Assert
        Assert.False(result);
        Assert.False(_sut.IsUserUnlocked(employeeId));
        Assert.Null(_sut.GetPrivateKey(employeeId));
    }

    [Fact]
    public void LockVault_RemovesKeyFromMemory()
    {
        // Arrange
        const string employeeId = "emp-005";
        const string password = "VaultPassword!";
        var (_, privateKey) = _cryptoService.GenerateRsaKeyPair();
        var kek = _cryptoService.DeriveKeyFromPassword(password, employeeId);
        var encryptedPrivateKey = _cryptoService.EncryptAes(privateKey, kek);

        var unlockResult = _sut.UnlockVault(employeeId, encryptedPrivateKey, password, _cryptoService);
        Assert.True(unlockResult);
        Assert.True(_sut.IsUserUnlocked(employeeId));
        Assert.NotNull(_sut.GetPrivateKey(employeeId));

        // Act
        _sut.LockVault(employeeId);

        // Assert
        Assert.False(_sut.IsUserUnlocked(employeeId));
        Assert.Null(_sut.GetPrivateKey(employeeId));
    }

    [Fact]
    public void LockVault_WhenKeyDoesNotExist_DoesNotThrow()
    {
        // Act & Assert
        var exception = Record.Exception(() => _sut.LockVault("non-existent-user"));
        Assert.Null(exception);
    }

    [Fact]
    public void MultipleUsers_VaultsAreIsolated()
    {
        // Arrange
        const string user1 = "user-1";
        const string user2 = "user-2";
        const string pass1 = "Pass1!";
        const string pass2 = "Pass2!";

        var (_, privKey1) = _cryptoService.GenerateRsaKeyPair();
        var (_, privKey2) = _cryptoService.GenerateRsaKeyPair();

        var kek1 = _cryptoService.DeriveKeyFromPassword(pass1, user1);
        var kek2 = _cryptoService.DeriveKeyFromPassword(pass2, user2);

        var enc1 = _cryptoService.EncryptAes(privKey1, kek1);
        var enc2 = _cryptoService.EncryptAes(privKey2, kek2);

        // Act
        _sut.UnlockVault(user1, enc1, pass1, _cryptoService);
        _sut.UnlockVault(user2, enc2, pass2, _cryptoService);

        // Assert
        Assert.True(_sut.IsUserUnlocked(user1));
        Assert.True(_sut.IsUserUnlocked(user2));
        Assert.Equal(privKey1, _sut.GetPrivateKey(user1));
        Assert.Equal(privKey2, _sut.GetPrivateKey(user2));

        // Lock user1 only
        _sut.LockVault(user1);

        Assert.False(_sut.IsUserUnlocked(user1));
        Assert.Null(_sut.GetPrivateKey(user1));
        Assert.True(_sut.IsUserUnlocked(user2));
        Assert.Equal(privKey2, _sut.GetPrivateKey(user2));
    }
}
