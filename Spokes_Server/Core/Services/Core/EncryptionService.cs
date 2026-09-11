using Spokes_Server.Core.Services.Communication;
using Spokes_Server.Core.Services.Projects;
using Spokes_Server.Core.Services.Core;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Configuration;

namespace Spokes_Server.Core.Services.Core;

public class EncryptionService
{
    private readonly string _keyFilePath;
    private byte[]? _key;
    public string KeyHash { get; private set; } = string.Empty;

    public EncryptionService(IConfiguration configuration)
    {
        var dataPath = configuration["DataPath"] ?? "Data";
        _keyFilePath = Path.Combine(dataPath, "security.key");
        LoadOrGenerateKey();
    }

    private void LoadOrGenerateKey()
    {
        if (File.Exists(_keyFilePath))
        {
            _key = File.ReadAllBytes(_keyFilePath);
        }
        else
        {
            using (var aes = Aes.Create())
            {
                aes.GenerateKey();
                _key = aes.Key;
                File.WriteAllBytes(_keyFilePath, _key);
            }
        }

        if (_key != null)
        {
            using var sha = SHA256.Create();
            KeyHash = Convert.ToBase64String(sha.ComputeHash(_key));
        }
    }

    public string Encrypt(string plainText)
    {
        if (string.IsNullOrEmpty(plainText)) return plainText;
        if (_key == null) throw new InvalidOperationException("Encryption key not loaded.");

        using var aes = Aes.Create();
        aes.Key = _key;
        aes.GenerateIV();

        using var encryptor = aes.CreateEncryptor(aes.Key, aes.IV);
        using var ms = new MemoryStream();

        // Prepend IV to the stream
        ms.Write(aes.IV, 0, aes.IV.Length);

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
        if (_key == null) throw new InvalidOperationException("Encryption key not loaded.");

        try
        {
            var fullCipher = Convert.FromBase64String(cipherText);

            using var aes = Aes.Create();
            aes.Key = _key;

            // Extract IV from the beginning
            var iv = new byte[aes.BlockSize / 8];
            Array.Copy(fullCipher, 0, iv, 0, iv.Length);
            aes.IV = iv;

            using var decryptor = aes.CreateDecryptor(aes.Key, aes.IV);
            using var ms = new MemoryStream(fullCipher, iv.Length, fullCipher.Length - iv.Length);
            using var cs = new CryptoStream(ms, decryptor, CryptoStreamMode.Read);
            using var sr = new StreamReader(cs);

            return sr.ReadToEnd();
        }
        catch
        {
            return string.Empty; // Fail safely? Or throw?
        }
    }
}

