using System.Security.Cryptography;
using Spokes_Server.Core.Services.Security;

namespace Spokes_Server.Tests.Core.Services.Security;

public class CryptoServiceTests
{
    private readonly CryptoService _sut = new();

    [Fact]
    public void GenerateRsaKeyPair_ReturnsValidXmlKeyPair()
    {
        // Act
        var (publicKey, privateKey) = _sut.GenerateRsaKeyPair();

        // Assert
        Assert.False(string.IsNullOrWhiteSpace(publicKey));
        Assert.False(string.IsNullOrWhiteSpace(privateKey));

        Assert.Contains("<RSAKeyValue>", publicKey);
        Assert.Contains("</RSAKeyValue>", publicKey);

        Assert.Contains("<RSAKeyValue>", privateKey);
        Assert.Contains("</RSAKeyValue>", privateKey);

        // Private key contains private exponent <D>, public key must not
        Assert.Contains("<D>", privateKey);
        Assert.DoesNotContain("<D>", publicKey);
    }

    [Fact]
    public void EncryptRsa_And_DecryptRsa_RoundTripsPlainText()
    {
        // Arrange
        var (publicKey, privateKey) = _sut.GenerateRsaKeyPair();
        const string plainText = "Sensitive payload for RSA test 12345!";

        // Act
        var cipherText = _sut.EncryptRsa(plainText, publicKey);
        var decryptedText = _sut.DecryptRsa(cipherText, privateKey);

        // Assert
        Assert.NotEmpty(cipherText);
        Assert.NotEqual(plainText, cipherText);
        Assert.Equal(plainText, decryptedText);
    }

    [Fact]
    public void GenerateAesKeyBase64_Returns32ByteBase64String()
    {
        // Act
        var base64Key = _sut.GenerateAesKeyBase64();

        // Assert
        Assert.False(string.IsNullOrWhiteSpace(base64Key));
        var keyBytes = Convert.FromBase64String(base64Key);
        Assert.Equal(32, keyBytes.Length); // 256 bits
    }

    [Fact]
    public void EncryptAes_And_DecryptAes_RoundTripsPlainText()
    {
        // Arrange
        var key = _sut.GenerateAesKeyBase64();
        const string plainText = "Hello, Spokes Secure World! 🚴‍♀️🔐";

        // Act
        var cipherText = _sut.EncryptAes(plainText, key);
        var decryptedText = _sut.DecryptAes(cipherText, key);

        // Assert
        Assert.NotEmpty(cipherText);
        Assert.NotEqual(plainText, cipherText);
        Assert.Equal(plainText, decryptedText);
    }

    [Fact]
    public void EncryptAes_GeneratesUniqueCiphertextForEachCall()
    {
        // Arrange
        var key = _sut.GenerateAesKeyBase64();
        const string plainText = "Same plain text repeated";

        // Act
        var cipher1 = _sut.EncryptAes(plainText, key);
        var cipher2 = _sut.EncryptAes(plainText, key);

        // Assert
        Assert.NotEqual(cipher1, cipher2);

        // Verify distinct IVs (first 16 bytes of decoded ciphertext)
        var bytes1 = Convert.FromBase64String(cipher1);
        var bytes2 = Convert.FromBase64String(cipher2);
        var iv1 = bytes1[..16];
        var iv2 = bytes2[..16];
        Assert.NotEqual(iv1, iv2);

        // Both decrypt to identical plain text
        Assert.Equal(plainText, _sut.DecryptAes(cipher1, key));
        Assert.Equal(plainText, _sut.DecryptAes(cipher2, key));
    }

    [Fact]
    public async Task EncryptStreamAsync_And_DecryptStreamAsync_RoundTripsBinaryData()
    {
        // Arrange
        var key = _sut.GenerateAesKeyBase64();
        var originalBytes = new byte[10 * 1024]; // 10 KB
        RandomNumberGenerator.Fill(originalBytes);

        using var inputStream = new MemoryStream(originalBytes);
        using var encryptedStream = new MemoryStream();
        using var decryptedStream = new MemoryStream();

        // Act
        await _sut.EncryptStreamAsync(inputStream, encryptedStream, key);

        encryptedStream.Position = 0;
        await _sut.DecryptStreamAsync(encryptedStream, decryptedStream, key);

        // Assert
        var decryptedBytes = decryptedStream.ToArray();
        Assert.Equal(originalBytes, decryptedBytes);
    }

    [Fact]
    public async Task DecryptStreamAsync_WhenStreamShorterThan16Bytes_ThrowsInvalidDataException()
    {
        // Arrange
        var key = _sut.GenerateAesKeyBase64();
        var shortBytes = new byte[10];
        RandomNumberGenerator.Fill(shortBytes);

        using var shortStream = new MemoryStream(shortBytes);
        using var outputStream = new MemoryStream();

        // Act & Assert
        var ex = await Assert.ThrowsAsync<InvalidDataException>(
            () => _sut.DecryptStreamAsync(shortStream, outputStream, key)
        );
        Assert.Contains("no IV", ex.Message);
    }

    [Fact]
    public void DeriveKeyFromPassword_ProducesDeterministic32ByteKey()
    {
        // Arrange
        const string password = "SuperSecretPassword#2026";
        const string salt1 = "salt_alpha_987";
        const string salt2 = "salt_beta_123";

        // Act
        var key1a = _sut.DeriveKeyFromPassword(password, salt1, iterations: 10000);
        var key1b = _sut.DeriveKeyFromPassword(password, salt1, iterations: 10000);
        var key2 = _sut.DeriveKeyFromPassword(password, salt2, iterations: 10000);

        var keyBytes1a = Convert.FromBase64String(key1a);
        var keyBytes2 = Convert.FromBase64String(key2);

        // Assert
        Assert.Equal(32, keyBytes1a.Length); // 256 bits
        Assert.Equal(32, keyBytes2.Length); // 256 bits
        Assert.Equal(key1a, key1b); // Deterministic with same password & salt
        Assert.NotEqual(key1a, key2); // Different salt yields different key
    }
}
