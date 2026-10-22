using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Spokes_Server.Core.Exceptions;
using Spokes_Server.Core.Services.Maintenance;
using Xunit;

namespace Spokes_Server.Tests.Core.Services.Maintenance;

public class BackupEncryptionServiceTests
{
    private readonly BackupEncryptionService _encryptionService = new();

    [Fact]
    public async Task EncryptAndDecrypt_RoundTrip_MatchesOriginalData()
    {
        var masterPassword = "TestMasterPassword123!@#";
        var originalContent = "This is secret Spokes backup archive content that must remain confidential.";
        var originalBytes = Encoding.UTF8.GetBytes(originalContent);

        using var sourceStream = new MemoryStream(originalBytes);
        using var encryptedStream = new MemoryStream();

        await _encryptionService.EncryptBackupAsync(sourceStream, encryptedStream, masterPassword);

        // Verify encrypted bytes are different and have the SPKENC01 magic header
        var encryptedBytes = encryptedStream.ToArray();
        Assert.True(encryptedBytes.Length > originalBytes.Length);
        Assert.Equal("SPKENC01"u8.ToArray(), encryptedBytes.Take(8).ToArray());

        // Decrypt
        encryptedStream.Position = 0;
        using var decryptedStream = new MemoryStream();
        await _encryptionService.DecryptBackupAsync(encryptedStream, decryptedStream, masterPassword);

        var decryptedContent = Encoding.UTF8.GetString(decryptedStream.ToArray());
        Assert.Equal(originalContent, decryptedContent);
    }

    [Fact]
    public async Task Decrypt_WithWrongMasterPassword_ThrowsInvalidMasterPasswordExceptionImmediately()
    {
        var correctPassword = "CorrectPassword456!";
        var wrongPassword = "WrongPassword789!";
        var originalContent = "Confidential server state";

        using var sourceStream = new MemoryStream(Encoding.UTF8.GetBytes(originalContent));
        using var encryptedStream = new MemoryStream();

        await _encryptionService.EncryptBackupAsync(sourceStream, encryptedStream, correctPassword);

        encryptedStream.Position = 0;
        using var decryptedStream = new MemoryStream();

        var ex = await Assert.ThrowsAsync<InvalidMasterPasswordException>(() =>
            _encryptionService.DecryptBackupAsync(encryptedStream, decryptedStream, wrongPassword));

        Assert.Equal("INVALID_MASTER_PASSWORD", ex.ErrorCode);
    }

    [Fact]
    public async Task IsEncryptedFile_IdentifiesHeaderCorrectly()
    {
        var tempFile = Path.GetTempFileName();
        try
        {
            // Case 1: Plain text / regular zip without magic
            await File.WriteAllBytesAsync(tempFile, new byte[] { 0x50, 0x4B, 0x03, 0x04, 0x00, 0x00 });
            Assert.False(_encryptionService.IsEncryptedFile(tempFile));

            // Case 2: SPKENC01 encrypted file
            using (var src = new MemoryStream(new byte[] { 1, 2, 3, 4, 5 }))
            using (var dst = File.Create(tempFile))
            {
                await _encryptionService.EncryptBackupAsync(src, dst, "StrongPass123");
            }

            Assert.True(_encryptionService.IsEncryptedFile(tempFile));
        }
        finally
        {
            if (File.Exists(tempFile))
            {
                File.Delete(tempFile);
            }
        }
    }

    [Fact]
    public async Task EncryptAndDecrypt_EmptyPassword_ThrowsArgumentException()
    {
        using var src = new MemoryStream(new byte[] { 1, 2, 3 });
        using var dst = new MemoryStream();

        await Assert.ThrowsAsync<ArgumentException>(() =>
            _encryptionService.EncryptBackupAsync(src, dst, ""));
    }

    [Fact]
    public async Task EncryptBackupAsync_ReportsProgress()
    {
        var masterPassword = "TestMasterPassword123!@#";
        var originalBytes = new byte[250000]; // ~250KB to cross 80KB buffer multiple times
        Random.Shared.NextBytes(originalBytes);

        using var sourceStream = new MemoryStream(originalBytes);
        using var encryptedStream = new MemoryStream();

        var reportedBytes = new List<long>();
        var progress = new Progress<long>(b => reportedBytes.Add(b));

        await _encryptionService.EncryptBackupAsync(sourceStream, encryptedStream, masterPassword, progress);

        Assert.NotEmpty(reportedBytes);
        Assert.Equal(originalBytes.Length, reportedBytes.Last());
    }
}
