using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Spokes_Server.Core.Exceptions;

namespace Spokes_Server.Core.Services.Maintenance;

public interface IBackupEncryptionService
{
    Task EncryptBackupAsync(Stream inputZipStream, Stream outputEncryptedStream, string masterPassword, IProgress<long>? progress = null, CancellationToken cancellationToken = default);
    Task DecryptBackupAsync(Stream inputEncryptedStream, Stream outputZipStream, string masterPassword, CancellationToken cancellationToken = default);
    bool IsEncryptedArchive(Stream stream);
    bool IsEncryptedFile(string filePath);
    bool VerifyMasterPassword(Stream inputEncryptedStream, string masterPassword);
}

public class BackupEncryptionService : IBackupEncryptionService
{
    private static readonly byte[] MagicBytes = Encoding.ASCII.GetBytes("SPKENC01");
    private const int MagicLength = 8;
    private const int SaltLength = 16;
    private const int TagLength = 32;
    private const int IvLength = 16;
    private const int Pbkdf2Iterations = 100000;

    public bool IsEncryptedArchive(Stream stream)
    {
        if (stream == null || !stream.CanRead) return false;

        var initialPosition = stream.CanSeek ? stream.Position : 0;
        try
        {
            var header = new byte[MagicLength];
            int read = stream.Read(header, 0, MagicLength);
            if (read < MagicLength) return false;

            return CryptographicOperations.FixedTimeEquals(header, MagicBytes);
        }
        finally
        {
            if (stream.CanSeek)
            {
                stream.Position = initialPosition;
            }
        }
    }

    public bool IsEncryptedFile(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath)) return false;

        try
        {
            using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            return IsEncryptedArchive(fs);
        }
        catch
        {
            return false;
        }
    }

    public bool VerifyMasterPassword(Stream inputEncryptedStream, string masterPassword)
    {
        ArgumentNullException.ThrowIfNull(inputEncryptedStream);
        ArgumentException.ThrowIfNullOrWhiteSpace(masterPassword);

        var initialPosition = inputEncryptedStream.CanSeek ? inputEncryptedStream.Position : 0;
        try
        {
            var magic = new byte[MagicLength];
            if (inputEncryptedStream.Read(magic, 0, MagicLength) < MagicLength || !CryptographicOperations.FixedTimeEquals(magic, MagicBytes))
            {
                return false;
            }

            var salt = new byte[SaltLength];
            if (inputEncryptedStream.Read(salt, 0, SaltLength) < SaltLength) return false;

            var expectedTag = new byte[TagLength];
            if (inputEncryptedStream.Read(expectedTag, 0, TagLength) < TagLength) return false;

            var (_, verifyKey) = DeriveKeys(masterPassword, salt);
            using var hmac = new HMACSHA256(verifyKey);
            var actualTag = hmac.ComputeHash(salt);

            return CryptographicOperations.FixedTimeEquals(expectedTag, actualTag);
        }
        finally
        {
            if (inputEncryptedStream.CanSeek)
            {
                inputEncryptedStream.Position = initialPosition;
            }
        }
    }

    public async Task EncryptBackupAsync(Stream inputZipStream, Stream outputEncryptedStream, string masterPassword, IProgress<long>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(inputZipStream);
        ArgumentNullException.ThrowIfNull(outputEncryptedStream);
        ArgumentException.ThrowIfNullOrWhiteSpace(masterPassword);

        // 1. Generate cryptographic salt
        var salt = RandomNumberGenerator.GetBytes(SaltLength);

        // 2. Derive 256-bit AES encryption key and 256-bit HMAC verification key
        var (aesKey, verifyKey) = DeriveKeys(masterPassword, salt);

        // 3. Compute verification tag
        using var hmac = new HMACSHA256(verifyKey);
        var tag = hmac.ComputeHash(salt);

        // 4. Write Header: Magic (8) + Salt (16) + Tag (32)
        await outputEncryptedStream.WriteAsync(MagicBytes, cancellationToken);
        await outputEncryptedStream.WriteAsync(salt, cancellationToken);
        await outputEncryptedStream.WriteAsync(tag, cancellationToken);

        // 5. Generate IV and write to stream
        var iv = RandomNumberGenerator.GetBytes(IvLength);
        await outputEncryptedStream.WriteAsync(iv, cancellationToken);

        // 6. Encrypt the zip stream with AES-256
        using var aes = Aes.Create();
        aes.Key = aesKey;
        aes.IV = iv;
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.PKCS7;

        using var encryptor = aes.CreateEncryptor();
        await using (var cryptoStream = new CryptoStream(outputEncryptedStream, encryptor, CryptoStreamMode.Write, leaveOpen: true))
        {
            var buffer = new byte[81920];
            int bytesRead;
            long totalRead = 0;
            while ((bytesRead = await inputZipStream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken)) > 0)
            {
                await cryptoStream.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken);
                totalRead += bytesRead;
                progress?.Report(totalRead);
            }
            await cryptoStream.FlushFinalBlockAsync(cancellationToken);
        }
    }

    public async Task DecryptBackupAsync(Stream inputEncryptedStream, Stream outputZipStream, string masterPassword, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(inputEncryptedStream);
        ArgumentNullException.ThrowIfNull(outputZipStream);
        ArgumentException.ThrowIfNullOrWhiteSpace(masterPassword);

        // 1. Read and verify Magic Header
        var magic = new byte[MagicLength];
        var magicRead = await ReadExactAsync(inputEncryptedStream, magic, MagicLength, cancellationToken);
        if (magicRead < MagicLength || !CryptographicOperations.FixedTimeEquals(magic, MagicBytes))
        {
            throw new InvalidDataException("Invalid encrypted backup archive: missing or corrupted SPKENC01 header.");
        }

        // 2. Read Salt
        var salt = new byte[SaltLength];
        var saltRead = await ReadExactAsync(inputEncryptedStream, salt, SaltLength, cancellationToken);
        if (saltRead < SaltLength)
        {
            throw new InvalidDataException("Corrupted backup file: incomplete salt header.");
        }

        // 3. Read Verification Tag
        var expectedTag = new byte[TagLength];
        var tagRead = await ReadExactAsync(inputEncryptedStream, expectedTag, TagLength, cancellationToken);
        if (tagRead < TagLength)
        {
            throw new InvalidDataException("Corrupted backup file: incomplete verification tag header.");
        }

        // 4. Derive keys and pre-verify master password in <1ms
        var (aesKey, verifyKey) = DeriveKeys(masterPassword, salt);
        using var hmac = new HMACSHA256(verifyKey);
        var actualTag = hmac.ComputeHash(salt);

        if (!CryptographicOperations.FixedTimeEquals(expectedTag, actualTag))
        {
            throw new InvalidMasterPasswordException("The server's configured master password does not match the password used to encrypt this backup archive.");
        }

        // 5. Read IV
        var iv = new byte[IvLength];
        var ivRead = await ReadExactAsync(inputEncryptedStream, iv, IvLength, cancellationToken);
        if (ivRead < IvLength)
        {
            throw new InvalidDataException("Corrupted backup file: incomplete IV header.");
        }

        // 6. Decrypt payload into output zip stream
        using var aes = Aes.Create();
        aes.Key = aesKey;
        aes.IV = iv;
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.PKCS7;

        using var decryptor = aes.CreateDecryptor();
        await using (var cryptoStream = new CryptoStream(inputEncryptedStream, decryptor, CryptoStreamMode.Read, leaveOpen: true))
        {
            await cryptoStream.CopyToAsync(outputZipStream, cancellationToken);
        }
    }

    private static (byte[] aesKey, byte[] verifyKey) DeriveKeys(string password, byte[] salt)
    {
        using var pbkdf2 = new Rfc2898DeriveBytes(password, salt, Pbkdf2Iterations, HashAlgorithmName.SHA256);
        var derivedBytes = pbkdf2.GetBytes(64); // 32 bytes AES key + 32 bytes Verify key
        var aesKey = new byte[32];
        var verifyKey = new byte[32];
        Buffer.BlockCopy(derivedBytes, 0, aesKey, 0, 32);
        Buffer.BlockCopy(derivedBytes, 32, verifyKey, 0, 32);
        return (aesKey, verifyKey);
    }

    private static async Task<int> ReadExactAsync(Stream stream, byte[] buffer, int count, CancellationToken cancellationToken)
    {
        int totalRead = 0;
        while (totalRead < count)
        {
            int read = await stream.ReadAsync(buffer.AsMemory(totalRead, count - totalRead), cancellationToken);
            if (read == 0) break;
            totalRead += read;
        }
        return totalRead;
    }
}
