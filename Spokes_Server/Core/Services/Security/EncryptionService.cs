using System.Security.Cryptography;
using Microsoft.Extensions.Configuration;

namespace Spokes_Server.Core.Services.Security;

public class EncryptionService
{
    private readonly string _keyFilePath;
    private readonly object _lock = new();
    private byte[]? _key;

    public EncryptionService(IConfiguration? configuration = null)
    {
        var dataPath = configuration?["DataPath"] ?? "Data";
        _keyFilePath = Path.Combine(dataPath, "security.key");
    }

    private void EnsureKeyLoaded()
    {
        if (_key != null) return;
        lock (_lock)
        {
            if (_key != null) return;
            LoadOrGenerateKey();
        }
    }

    private void LoadOrGenerateKey()
    {
        if (File.Exists(_keyFilePath))
        {
            _key = File.ReadAllBytes(_keyFilePath);
        }
        else
        {
            var dir = Path.GetDirectoryName(_keyFilePath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            using (var aes = Aes.Create())
            {
                aes.GenerateKey();
                _key = aes.Key;
                File.WriteAllBytes(_keyFilePath, _key);
            }
        }
    }

    public string Encrypt(string plainText)
    {
        if (string.IsNullOrEmpty(plainText)) return plainText;
        EnsureKeyLoaded();
        if (_key == null) throw new InvalidOperationException("Encryption key not loaded.");

        using var aes = Aes.Create();
        aes.Key = _key;
        aes.GenerateIV();

        using var encryptor = aes.CreateEncryptor(aes.Key, aes.IV);
        using var ms = new MemoryStream();

        // Prepend IV to the stream
        ms.Write(aes.IV);

        using (var cs = new CryptoStream(ms, encryptor, CryptoStreamMode.Write))
        using (var sw = new StreamWriter(cs))
        {
            sw.Write(plainText);
        }

        return Convert.ToBase64String(ms.ToArray());
    }

    public string Decrypt(string cipherText)
    {
        if (string.IsNullOrEmpty(cipherText)) return cipherText;
        EnsureKeyLoaded();
        if (_key == null) throw new InvalidOperationException("Encryption key not loaded.");

        try
        {
            var fullCipher = Convert.FromBase64String(cipherText);

            using var aes = Aes.Create();
            aes.Key = _key;

            var ivLength = aes.BlockSize / 8;
            if (fullCipher.Length < ivLength)
            {
                throw new CryptographicException("Ciphertext is too short to contain a valid initialization vector.");
            }

            // Extract IV from the beginning
            var iv = new byte[ivLength];
            Array.Copy(fullCipher, 0, iv, 0, iv.Length);
            aes.IV = iv;

            using var decryptor = aes.CreateDecryptor(aes.Key, aes.IV);
            using var ms = new MemoryStream(fullCipher, iv.Length, fullCipher.Length - iv.Length);
            using var cs = new CryptoStream(ms, decryptor, CryptoStreamMode.Read);
            using var sr = new StreamReader(cs);

            return sr.ReadToEnd();
        }
        catch (CryptographicException)
        {
            throw;
        }
        catch (FormatException ex)
        {
            throw new CryptographicException("Ciphertext is not valid Base64.", ex);
        }
        catch (Exception ex)
        {
            throw new CryptographicException("Decryption failed.", ex);
        }
    }

    public bool TryDecrypt(string? cipherText, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out string? plainText)
    {
        if (cipherText == null)
        {
            plainText = null;
            return false;
        }

        if (cipherText.Length == 0)
        {
            plainText = string.Empty;
            return true;
        }

        try
        {
            plainText = Decrypt(cipherText);
            return true;
        }
        catch (Exception)
        {
            plainText = null;
            return false;
        }
    }
}
