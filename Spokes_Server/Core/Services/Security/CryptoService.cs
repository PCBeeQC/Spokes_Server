using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;

namespace Spokes_Server.Core.Services.Security;

public interface ICryptoService
{
    // Asymmetric (RSA)
    (string PublicKey, string PrivateKey) GenerateRsaKeyPair();
    string EncryptRsa(string plainText, string publicKeyXml);
    string DecryptRsa(string cipherText, string privateKeyXml);

    // Symmetric (AES)
    string GenerateAesKeyBase64();
    string EncryptAes(string plainText, string base64Key);
    string DecryptAes(string cipherText, string base64Key);

    // Stream (AES)
    Task EncryptStreamAsync(Stream inputStream, Stream outputStream, string base64Key);
    Task DecryptStreamAsync(Stream inputStream, Stream outputStream, string base64Key);

    // KDF
    string DeriveKeyFromPassword(string password, string salt, int iterations = 100000);
}

public class CryptoService : ICryptoService
{
    private const int AesKeySize = 256;
    private const int RsaKeySize = 2048;

    public (string PublicKey, string PrivateKey) GenerateRsaKeyPair()
    {
        using var rsa = RSA.Create(RsaKeySize);
        return (rsa.ToXmlString(false), rsa.ToXmlString(true));
    }

    public string EncryptRsa(string plainText, string publicKeyXml)
    {
        using var rsa = RSA.Create();
        rsa.FromXmlString(publicKeyXml);
        var bytes = Encoding.UTF8.GetBytes(plainText);
        var encrypted = rsa.Encrypt(bytes, RSAEncryptionPadding.OaepSHA256);
        return Convert.ToBase64String(encrypted);
    }

    public string DecryptRsa(string cipherText, string privateKeyXml)
    {
        using var rsa = RSA.Create();
        rsa.FromXmlString(privateKeyXml);
        var bytes = Convert.FromBase64String(cipherText);
        var decrypted = rsa.Decrypt(bytes, RSAEncryptionPadding.OaepSHA256);
        return Encoding.UTF8.GetString(decrypted);
    }

    public string GenerateAesKeyBase64()
    {
        using var aes = Aes.Create();
        aes.KeySize = AesKeySize;
        aes.GenerateKey();
        return Convert.ToBase64String(aes.Key);
    }

    public string EncryptAes(string plainText, string base64Key)
    {
        var key = Convert.FromBase64String(base64Key);
        using var aes = Aes.Create();
        aes.Key = key;
        aes.GenerateIV();

        using var encryptor = aes.CreateEncryptor();
        using var ms = new MemoryStream();
        // Prepend IV to the ciphertext
        ms.Write(aes.IV, 0, aes.IV.Length);

        using (var cs = new CryptoStream(ms, encryptor, CryptoStreamMode.Write))
        using (var sw = new StreamWriter(cs))
        {
            sw.Write(plainText);
        }

        return Convert.ToBase64String(ms.ToArray());
    }

    public string DecryptAes(string cipherText, string base64Key)
    {
        var buffer = Convert.FromBase64String(cipherText);
        var key = Convert.FromBase64String(base64Key);

        using var aes = Aes.Create();
        aes.Key = key;

        // Extract IV (first 16 bytes for AES)
        var iv = new byte[16];
        Array.Copy(buffer, 0, iv, 0, 16);
        aes.IV = iv;

        using var decryptor = aes.CreateDecryptor();
        using var ms = new MemoryStream(buffer, 16, buffer.Length - 16);
        using var cs = new CryptoStream(ms, decryptor, CryptoStreamMode.Read);
        using var sr = new StreamReader(cs);

        return sr.ReadToEnd();
    }

    public async Task EncryptStreamAsync(Stream inputStream, Stream outputStream, string base64Key)
    {
        var key = Convert.FromBase64String(base64Key);
        using var aes = Aes.Create();
        aes.Key = key;
        aes.GenerateIV();

        // Write IV first
        await outputStream.WriteAsync(aes.IV, 0, aes.IV.Length);

        using var encryptor = aes.CreateEncryptor();
        await using var cs = new CryptoStream(outputStream, encryptor, CryptoStreamMode.Write, leaveOpen: true);
        await inputStream.CopyToAsync(cs);
    }

    public async Task DecryptStreamAsync(Stream inputStream, Stream outputStream, string base64Key)
    {
        var key = Convert.FromBase64String(base64Key);
        using var aes = Aes.Create();
        aes.Key = key;

        var iv = new byte[16];
        var bytesRead = await inputStream.ReadAsync(iv, 0, 16);
        if (bytesRead < 16) throw new InvalidDataException("Invalid encrypted stream (no IV).");

        aes.IV = iv;

        using var decryptor = aes.CreateDecryptor();
        await using var cs = new CryptoStream(inputStream, decryptor, CryptoStreamMode.Read, leaveOpen: true);
        await cs.CopyToAsync(outputStream);
    }

    public string DeriveKeyFromPassword(string password, string salt, int iterations = 100000)
    {
        var saltBytes = Encoding.UTF8.GetBytes(salt);
        using var pbkdf2 = new Rfc2898DeriveBytes(password, saltBytes, iterations, HashAlgorithmName.SHA256);
        var key = pbkdf2.GetBytes(32); // 256 bits
        return Convert.ToBase64String(key);
    }
}
