using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Spokes_Server.Aggregate;
using Spokes_Server.Core.Data;
using Spokes_Server.Core.Data.Repositories.Core;
using Spokes_Server.Core.Models.Accounting;
using Spokes_Server.Core.Services.Maintenance;
using Spokes_Server.Core.Services.Security;
using Xunit;

namespace Spokes_Server.Tests.Core.Services.Maintenance;

public class BackupRestoreUploadServiceTests : TestDataTestBase
{
    private const string TestMasterPassword = "Synthetic-Master-Password-2026!";

    private readonly DiskPersistenceService _persistence;
    private readonly CompanyProfileRepository _companyProfiles;
    private readonly BackupEncryptionService _encryption;

    public BackupRestoreUploadServiceTests()
    {
        var config = CreateConfig(TestMasterPassword);
        _persistence = new DiskPersistenceService(NullLogger<DiskPersistenceService>.Instance);
        _companyProfiles = new CompanyProfileRepository(_persistence, config);
        _encryption = new BackupEncryptionService();
    }

    public override void Dispose()
    {
        _persistence.Dispose();
        base.Dispose();
    }

    private IConfiguration CreateConfig(string? masterPassword)
    {
        var dict = new Dictionary<string, string?>
        {
            ["DataPath"] = _testDataPath
        };
        if (masterPassword != null)
        {
            dict["SPOKES_MASTER_PASSWORD"] = masterPassword;
        }
        return new ConfigurationBuilder().AddInMemoryCollection(dict).Build();
    }

    private (BackupService BackupService, BackupRestoreUploadService UploadService) CreateServices(
        string? masterPassword = TestMasterPassword,
        IStorageHealthService? storageHealth = null)
    {
        var config = CreateConfig(masterPassword);
        var backupService = new BackupService(
            NullLogger<BackupService>.Instance,
            _companyProfiles,
            config,
            _persistence,
            null,
            _encryption,
            null,
            null);

        var uploadService = new BackupRestoreUploadService(
            backupService,
            _encryption,
            NullLogger<BackupRestoreUploadService>.Instance,
            storageHealth);

        return (backupService, uploadService);
    }

    private static byte[] CreateSyntheticZipBytes(int payloadSizeBytes = 160 * 1024, string markerText = "Acme Demo Inc. restored marker")
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            var markerEntry = zip.CreateEntry("CompanyProfile.json", CompressionLevel.NoCompression);
            using (var sw = new StreamWriter(markerEntry.Open(), Encoding.UTF8))
            {
                sw.Write($"{{\"CompanyName\":\"{markerText}\"}}");
            }

            // Add an uncompressed synthetic blob so the archive reliably spans multiple >= 64 KB chunks
            var blobEntry = zip.CreateEntry("synthetic_payload.bin", CompressionLevel.NoCompression);
            var blob = new byte[payloadSizeBytes];
            new Random(42).NextBytes(blob);
            using (var s = blobEntry.Open())
            {
                s.Write(blob, 0, blob.Length);
            }
        }
        return ms.ToArray();
    }

    private async Task<byte[]> CreateEncryptedBackupBytesAsync(byte[] plainZipBytes, string password)
    {
        using var input = new MemoryStream(plainZipBytes);
        using var output = new MemoryStream();
        await _encryption.EncryptBackupAsync(input, output, password);
        return output.ToArray();
    }

    private static string[] GetStagedFiles(string dataPath)
    {
        var stagingDir = Path.Combine(dataPath, "Backups", ".staging");
        return Directory.Exists(stagingDir) ? Directory.GetFiles(stagingDir) : [];
    }

    [Fact]
    public async Task ChunkedUpload_MultiChunkZip_AssemblesRestoresAndCleansUpStagedFile()
    {
        // Arrange
        var (_, uploadService) = CreateServices();
        var zipBytes = CreateSyntheticZipBytes(payloadSizeBytes: 150 * 1024);
        const int chunkSize = BackupRestoreUploadService.MinChunkSizeBytes; // 64 KB -> 3 chunks

        var init = uploadService.InitSession("Acme_Demo_Backup.zip", zipBytes.Length, chunkSize, BackupUploadScope.Admin, "admin-1");
        Assert.True(init.Success);
        Assert.NotNull(init.UploadId);
        Assert.True(init.TotalChunks >= 3);

        // Act: upload sequentially in chunks
        for (int i = 0; i < init.TotalChunks; i++)
        {
            int offset = i * init.ChunkSizeBytes;
            int count = Math.Min(init.ChunkSizeBytes, zipBytes.Length - offset);
            using var chunkStream = new MemoryStream(zipBytes, offset, count, writable: false);
            var chunkRes = await uploadService.AppendChunkAsync(init.UploadId!, i, chunkStream, count, BackupUploadScope.Admin, "admin-1");
            Assert.True(chunkRes.Success, chunkRes.ErrorMessage);
            Assert.Equal(offset + count, chunkRes.BytesReceived);
        }

        var complete = await uploadService.CompleteSessionAsync(init.UploadId!, BackupUploadScope.Admin, "admin-1");
        Assert.True(complete.Success, complete.ErrorMessage);
        Assert.Equal(zipBytes.Length, complete.FileSizeBytes);
        Assert.Single(GetStagedFiles(_testDataPath));

        var restore = await uploadService.RestoreStagedUploadAsync(init.UploadId!, BackupUploadScope.Admin, "admin-1");

        // Assert
        Assert.True(restore.Success, restore.ErrorMessage);
        Assert.True(File.Exists(Path.Combine(_testDataPath, "CompanyProfile.json")));
        Assert.Contains("Acme Demo Inc. restored marker", File.ReadAllText(Path.Combine(_testDataPath, "CompanyProfile.json")));
        Assert.Empty(GetStagedFiles(_testDataPath));

        // Second restore attempt with same uploadId must fail (consumed/removed)
        var replay = await uploadService.RestoreStagedUploadAsync(init.UploadId!, BackupUploadScope.Admin, "admin-1");
        Assert.False(replay.Success);
    }

    [Fact]
    public async Task ChunkZeroFailFast_InvalidNonZipArchive_RejectsImmediatelyAndDeletesStagedFile()
    {
        // Arrange
        var (_, uploadService) = CreateServices();
        var bogusBytes = Encoding.UTF8.GetBytes("THIS IS NOT A ZIP OR ENCRYPTED BACKUP FILE" + new string('X', 2048));

        var init = uploadService.InitSession("corrupted.zip", bogusBytes.Length, BackupRestoreUploadService.MinChunkSizeBytes, BackupUploadScope.Admin, "admin-1");
        Assert.True(init.Success);

        // Act
        using var chunk0 = new MemoryStream(bogusBytes);
        var chunkRes = await uploadService.AppendChunkAsync(init.UploadId!, 0, chunk0, bogusBytes.Length, BackupUploadScope.Admin, "admin-1");

        // Assert
        Assert.False(chunkRes.Success);
        Assert.Equal("INVALID_BACKUP_FORMAT", chunkRes.ErrorCode);
        Assert.Empty(GetStagedFiles(_testDataPath));

        // Session was cleaned up immediately
        var completeAfterFail = await uploadService.CompleteSessionAsync(init.UploadId!, BackupUploadScope.Admin, "admin-1");
        Assert.False(completeAfterFail.Success);
        Assert.Equal("SESSION_NOT_FOUND", completeAfterFail.ErrorCode);
    }

    [Fact]
    public async Task ChunkZeroFailFast_EncryptedArchiveWithWrongPassword_RejectsImmediatelyAndDeletesStagedFile()
    {
        // Arrange
        var plainZip = CreateSyntheticZipBytes(payloadSizeBytes: 80 * 1024);
        var encryptedBytes = await CreateEncryptedBackupBytesAsync(plainZip, "Original-Encryption-Secret-999!");
        var (_, uploadService) = CreateServices(masterPassword: "Different-Server-Password-000!");

        var init = uploadService.InitSession("Acme_Encrypted.spkbackup", encryptedBytes.Length, BackupRestoreUploadService.MinChunkSizeBytes, BackupUploadScope.Setup, null);
        Assert.True(init.Success);

        // Act: send only chunk 0
        int chunk0Len = Math.Min(init.ChunkSizeBytes, encryptedBytes.Length);
        using var chunk0 = new MemoryStream(encryptedBytes, 0, chunk0Len, writable: false);
        var chunkRes = await uploadService.AppendChunkAsync(init.UploadId!, 0, chunk0, chunk0Len, BackupUploadScope.Setup, null);

        // Assert
        Assert.False(chunkRes.Success);
        Assert.Equal("MASTER_PASSWORD_MISMATCH", chunkRes.ErrorCode);
        Assert.Empty(GetStagedFiles(_testDataPath));
    }

    [Fact]
    public async Task ChunkZeroFailFast_EncryptedArchiveWithMissingServerPassword_RejectsImmediately()
    {
        // Arrange
        var plainZip = CreateSyntheticZipBytes(payloadSizeBytes: 80 * 1024);
        var encryptedBytes = await CreateEncryptedBackupBytesAsync(plainZip, "Original-Encryption-Secret-999!");
        var (_, uploadService) = CreateServices(masterPassword: "");

        var init = uploadService.InitSession("Acme_Encrypted.spkbackup", encryptedBytes.Length, BackupRestoreUploadService.MinChunkSizeBytes, BackupUploadScope.Setup, null);
        Assert.True(init.Success);

        // Act
        int chunk0Len = Math.Min(init.ChunkSizeBytes, encryptedBytes.Length);
        using var chunk0 = new MemoryStream(encryptedBytes, 0, chunk0Len, writable: false);
        var chunkRes = await uploadService.AppendChunkAsync(init.UploadId!, 0, chunk0, chunk0Len, BackupUploadScope.Setup, null);

        // Assert
        Assert.False(chunkRes.Success);
        Assert.Equal("MASTER_PASSWORD_MISSING", chunkRes.ErrorCode);
        Assert.Empty(GetStagedFiles(_testDataPath));
    }

    [Fact]
    public async Task ChunkedUpload_EncryptedArchiveWithMatchingPassword_RestoresAndCleansUp()
    {
        // Arrange
        var plainZip = CreateSyntheticZipBytes(payloadSizeBytes: 80 * 1024, markerText: "Northstar Corp. encrypted");
        var encryptedBytes = await CreateEncryptedBackupBytesAsync(plainZip, TestMasterPassword);
        var (_, uploadService) = CreateServices(masterPassword: TestMasterPassword);

        var init = uploadService.InitSession("Northstar_Backup.spkbackup", encryptedBytes.Length, BackupRestoreUploadService.MinChunkSizeBytes, BackupUploadScope.Setup, null);
        Assert.True(init.Success);

        for (int i = 0; i < init.TotalChunks; i++)
        {
            int offset = i * init.ChunkSizeBytes;
            int count = Math.Min(init.ChunkSizeBytes, encryptedBytes.Length - offset);
            using var chunkStream = new MemoryStream(encryptedBytes, offset, count, writable: false);
            var chunkRes = await uploadService.AppendChunkAsync(init.UploadId!, i, chunkStream, count, BackupUploadScope.Setup, null);
            Assert.True(chunkRes.Success, chunkRes.ErrorMessage);
        }

        var complete = await uploadService.CompleteSessionAsync(init.UploadId!, BackupUploadScope.Setup, null);
        Assert.True(complete.Success, complete.ErrorMessage);

        // Act
        var restore = await uploadService.RestoreStagedUploadAsync(init.UploadId!, BackupUploadScope.Setup, null);

        // Assert
        Assert.True(restore.Success, restore.ErrorMessage);
        Assert.Contains("Northstar Corp. encrypted", File.ReadAllText(Path.Combine(_testDataPath, "CompanyProfile.json")));
        Assert.Empty(GetStagedFiles(_testDataPath));
    }

    [Fact]
    public async Task AppendChunk_IdempotentRetryOfPreviousChunk_PreservesByteIntegrity()
    {
        // Arrange
        var (_, uploadService) = CreateServices();
        var zipBytes = CreateSyntheticZipBytes(payloadSizeBytes: 90 * 1024);
        const int chunkSize = BackupRestoreUploadService.MinChunkSizeBytes;

        var init = uploadService.InitSession("retry_test.zip", zipBytes.Length, chunkSize, BackupUploadScope.Admin, "admin-1");
        Assert.True(init.Success);
        Assert.Equal(2, init.TotalChunks);

        // Upload chunk 0, then retry chunk 0 (simulating dropped HTTP ack after write)
        for (int attempt = 0; attempt < 2; attempt++)
        {
            using var s0 = new MemoryStream(zipBytes, 0, chunkSize, writable: false);
            var r0 = await uploadService.AppendChunkAsync(init.UploadId!, 0, s0, chunkSize, BackupUploadScope.Admin, "admin-1");
            Assert.True(r0.Success, r0.ErrorMessage);
        }

        // Upload chunk 1, then retry chunk 1
        int tailLen = zipBytes.Length - chunkSize;
        for (int attempt = 0; attempt < 2; attempt++)
        {
            using var s1 = new MemoryStream(zipBytes, chunkSize, tailLen, writable: false);
            var r1 = await uploadService.AppendChunkAsync(init.UploadId!, 1, s1, tailLen, BackupUploadScope.Admin, "admin-1");
            Assert.True(r1.Success, r1.ErrorMessage);
        }

        var complete = await uploadService.CompleteSessionAsync(init.UploadId!, BackupUploadScope.Admin, "admin-1");
        Assert.True(complete.Success);

        var restore = await uploadService.RestoreStagedUploadAsync(init.UploadId!, BackupUploadScope.Admin, "admin-1");
        Assert.True(restore.Success, restore.ErrorMessage);
    }

    [Fact]
    public async Task AppendChunk_OutOfOrderAndSizeMismatch_AreRejected()
    {
        var (_, uploadService) = CreateServices();
        var zipBytes = CreateSyntheticZipBytes(payloadSizeBytes: 90 * 1024);
        const int chunkSize = BackupRestoreUploadService.MinChunkSizeBytes;

        var init = uploadService.InitSession("order_test.zip", zipBytes.Length, chunkSize, BackupUploadScope.Admin, "admin-1");
        Assert.True(init.Success);

        // Attempting chunk 1 before chunk 0
        int tailLen = zipBytes.Length - chunkSize;
        using (var s1 = new MemoryStream(zipBytes, chunkSize, tailLen, writable: false))
        {
            var outOfOrder = await uploadService.AppendChunkAsync(init.UploadId!, 1, s1, tailLen, BackupUploadScope.Admin, "admin-1");
            Assert.False(outOfOrder.Success);
            Assert.Equal("CHUNK_OUT_OF_ORDER", outOfOrder.ErrorCode);
        }

        // Sending mismatched declared size for chunk 0
        using (var shortStream = new MemoryStream(zipBytes, 0, 1024, writable: false))
        {
            var mismatch = await uploadService.AppendChunkAsync(init.UploadId!, 0, shortStream, 1024, BackupUploadScope.Admin, "admin-1");
            Assert.False(mismatch.Success);
            Assert.Equal("CHUNK_SIZE_MISMATCH", mismatch.ErrorCode);
        }

        // Finalizing before any chunks received must fail with UPLOAD_INCOMPLETE
        var incomplete = await uploadService.CompleteSessionAsync(init.UploadId!, BackupUploadScope.Admin, "admin-1");
        Assert.False(incomplete.Success);
        Assert.Equal("UPLOAD_INCOMPLETE", incomplete.ErrorCode);

        await uploadService.CancelSessionAsync(init.UploadId!, BackupUploadScope.Admin, "admin-1");
    }

    [Fact]
    public async Task ScopeAndOwnerIsolation_BlocksCrossScopeAndCrossAdminAccess()
    {
        var (_, uploadService) = CreateServices();
        var zipBytes = CreateSyntheticZipBytes(payloadSizeBytes: 4096);

        var adminInit = uploadService.InitSession("admin_only.zip", zipBytes.Length, BackupRestoreUploadService.MinChunkSizeBytes, BackupUploadScope.Admin, "admin-A");
        Assert.True(adminInit.Success);

        // Different admin cannot upload, complete, cancel, or restore
        using (var s = new MemoryStream(zipBytes))
        {
            var wrongAdmin = await uploadService.AppendChunkAsync(adminInit.UploadId!, 0, s, zipBytes.Length, BackupUploadScope.Admin, "admin-B");
            Assert.False(wrongAdmin.Success);
            Assert.Equal("SESSION_FORBIDDEN", wrongAdmin.ErrorCode);
        }

        // Setup scope cannot touch an Admin session
        using (var s = new MemoryStream(zipBytes))
        {
            var wrongScope = await uploadService.AppendChunkAsync(adminInit.UploadId!, 0, s, zipBytes.Length, BackupUploadScope.Setup, null);
            Assert.False(wrongScope.Success);
            Assert.Equal("SESSION_FORBIDDEN", wrongScope.ErrorCode);
        }

        Assert.False(await uploadService.CancelSessionAsync(adminInit.UploadId!, BackupUploadScope.Admin, "admin-B"));

        // Owner uploads and completes
        using (var s = new MemoryStream(zipBytes))
        {
            Assert.True((await uploadService.AppendChunkAsync(adminInit.UploadId!, 0, s, zipBytes.Length, BackupUploadScope.Admin, "admin-A")).Success);
        }
        Assert.True((await uploadService.CompleteSessionAsync(adminInit.UploadId!, BackupUploadScope.Admin, "admin-A")).Success);

        // Another admin or Setup scope still cannot restore it
        Assert.False((await uploadService.RestoreStagedUploadAsync(adminInit.UploadId!, BackupUploadScope.Admin, "admin-B")).Success);
        Assert.False((await uploadService.RestoreStagedUploadAsync(adminInit.UploadId!, BackupUploadScope.Setup, null)).Success);

        // Cleanup via rightful owner
        Assert.True(await uploadService.CancelSessionAsync(adminInit.UploadId!, BackupUploadScope.Admin, "admin-A"));
        Assert.Empty(GetStagedFiles(_testDataPath));
    }

    [Fact]
    public void InitSession_ValidatesExtensionSizeConcurrencyAndStorageHeadroom()
    {
        var storageMock = new Mock<IStorageHealthService>();
        storageMock.SetupGet(s => s.TotalBytes).Returns(10L * 1024 * 1024 * 1024);
        storageMock.SetupGet(s => s.AvailableFreeBytes).Returns(2L * 1024 * 1024 * 1024);
        storageMock.SetupGet(s => s.IsUploadAllowed).Returns(true);

        var (_, uploadService) = CreateServices(storageHealth: storageMock.Object);

        // Invalid extension
        var badExt = uploadService.InitSession("malware.exe", 1024, null, BackupUploadScope.Admin, "admin-1");
        Assert.False(badExt.Success);
        Assert.Equal("INVALID_EXTENSION", badExt.ErrorCode);

        // Empty file
        var empty = uploadService.InitSession("empty.zip", 0, null, BackupUploadScope.Admin, "admin-1");
        Assert.False(empty.Success);
        Assert.Equal("EMPTY_FILE", empty.ErrorCode);

        // Exceeds free disk budget (2 GB free - 100 MB critical = 1.9 GB budget; ask for 3 GB)
        var tooLarge = uploadService.InitSession("huge.zip", 3L * 1024 * 1024 * 1024, null, BackupUploadScope.Admin, "admin-1");
        Assert.False(tooLarge.Success);
        Assert.Equal("INSUFFICIENT_STORAGE", tooLarge.ErrorCode);

        // CriticalReadOnly blocks upload immediately
        storageMock.SetupGet(s => s.IsUploadAllowed).Returns(false);
        var critBlocked = uploadService.InitSession("small.zip", 1024, null, BackupUploadScope.Admin, "admin-1");
        Assert.False(critBlocked.Success);
        Assert.Equal("INSUFFICIENT_STORAGE", critBlocked.ErrorCode);

        // Re-allow uploads and verify MaxConcurrentSessions (2) cap
        storageMock.SetupGet(s => s.IsUploadAllowed).Returns(true);
        var s1 = uploadService.InitSession("first.zip", 1024, null, BackupUploadScope.Admin, "admin-1");
        var s2 = uploadService.InitSession("second.zip", 1024, null, BackupUploadScope.Admin, "admin-1");
        var s3 = uploadService.InitSession("third.zip", 1024, null, BackupUploadScope.Admin, "admin-1");

        Assert.True(s1.Success);
        Assert.True(s2.Success);
        Assert.False(s3.Success);
        Assert.Equal("TOO_MANY_UPLOADS", s3.ErrorCode);
    }

    [Fact]
    public async Task RestoreBackupAsync_SingleFlightGuard_RejectsOverlappingRestore()
    {
        // Arrange: create a real valid zip file on disk
        var (backupService, _) = CreateServices();
        var zipPath = Path.Combine(_testDataPath, "concurrent_test.zip");
        await File.WriteAllBytesAsync(zipPath, CreateSyntheticZipBytes(payloadSizeBytes: 32 * 1024));

        var enteredRestore = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseRestore = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        // Use a synchronous IProgress<T> callback to hold RestoreBackupAsync inside its critical section
        var blockingProgress = new BlockingProgress(_ =>
        {
            enteredRestore.TrySetResult();
            releaseRestore.Task.GetAwaiter().GetResult();
        });

        // Act: start first restore on background thread
        var firstTask = Task.Run(() => backupService.RestoreBackupAsync(zipPath, null, CancellationToken.None, blockingProgress));
        await enteredRestore.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // Second restore while first holds the single-flight lock must fail cleanly
        var secondResult = await backupService.RestoreBackupAsync(zipPath, null, CancellationToken.None);

        // Unblock first restore
        releaseRestore.TrySetResult();
        var firstResult = await firstTask;

        // Assert
        Assert.False(secondResult.Success);
        Assert.Contains("already in progress", secondResult.ErrorMessage ?? "", StringComparison.OrdinalIgnoreCase);
        Assert.True(firstResult.Success, firstResult.ErrorMessage);

        // After lock is released, a new restore succeeds again
        var thirdResult = await backupService.RestoreBackupAsync(zipPath, null, CancellationToken.None);
        Assert.True(thirdResult.Success, thirdResult.ErrorMessage);
    }

    [Fact]
    public async Task RestoreBackupAsync_ReloadsDatabaseClearsAbsentDirectoriesAndReloadsSequences()
    {
        // Arrange: build real Database + repositories via AddSpokesDatabase()
        var services = new ServiceCollection();
        var config = CreateConfig(TestMasterPassword);
        services.AddSingleton<IConfiguration>(config);
        services.AddLogging();
        services.AddSingleton<EncryptionService>();
        services.AddSpokesDatabase();
        services.AddSingleton<IBackupEncryptionService, BackupEncryptionService>();
        services.AddSingleton<BackupService>();

        await using var sp = services.BuildServiceProvider();
        var db = sp.GetRequiredService<Database>();
        var persistence = sp.GetRequiredService<DiskPersistenceService>();
        var sequences = sp.GetRequiredService<SequenceService>();
        var backupService = sp.GetRequiredService<BackupService>();

        // Seed pre-restore state: 1 BankStatement, 1 stale JE, company name "Old Corp", sequence=10
        db.BankStatements.Save(new BankStatement { Id = "stale-stmt-1", GlAccountCode = "1020", SourceFileName = "old.csv" });
        db.JournalEntries.Save(new JournalEntry { Id = "stale-je-1", EntryNumber = "JE-OLD-0001", Description = "Pre-restore entry" });
        var oldProfile = db.CompanyProfile.GetOrCreate();
        oldProfile.CompanyName = "Old Pre-Restore Corp.";
        db.CompanyProfile.Save(oldProfile);
        _ = sequences.GetNextSequence("Invoice"); // 1
        await persistence.FlushAllAsync();

        Assert.Single(db.BankStatements.GetAll());
        Assert.Single(db.JournalEntries.GetAll());

        // Build a restored archive that has a new CompanyProfile, new JE, sequences.json=42, and NO BankStatements folder
        var archivePath = Path.Combine(_testDataPath, "Backups", "synthetic_restore.zip");
        Directory.CreateDirectory(Path.GetDirectoryName(archivePath)!);
        using (var fs = File.Create(archivePath))
        using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
        {
            void AddJson(string entryName, string json)
            {
                var e = zip.CreateEntry(entryName);
                using var sw = new StreamWriter(e.Open(), Encoding.UTF8);
                sw.Write(json);
            }

            AddJson("Settings/company.json", "{\"Id\":\"default\",\"CompanyName\":\"Acme Demo Restored Inc.\"}");
            AddJson("JournalEntries/restored-je-1.json", "{\"Id\":\"restored-je-1\",\"EntryNumber\":\"JE-NEW-0001\",\"Description\":\"Restored entry\"}");
            AddJson("sequences.json", "{\"Invoice\":42}");
        }

        // Act: restore using DI-resolved BackupService (must invoke 8-arg constructor with Database + DiskPersistenceService)
        var result = await backupService.RestoreBackupAsync(archivePath, null, CancellationToken.None);

        // Assert: restored data is live in memory, absent directories cleared from RAM, sequences reloaded
        Assert.True(result.Success, result.ErrorMessage);
        Assert.Empty(db.BankStatements.GetAll());
        var restoredJes = db.JournalEntries.GetAll();
        Assert.Single(restoredJes);
        Assert.Equal("restored-je-1", restoredJes[0].Id);
        Assert.Equal("Acme Demo Restored Inc.", db.CompanyProfile.Get().CompanyName);
        Assert.Equal(43, sequences.GetNextSequence("Invoice"));
    }

    private sealed class BlockingProgress(Action<BackupRestoreProgress> onReport) : IProgress<BackupRestoreProgress>
    {
        private int _called;
        public void Report(BackupRestoreProgress value)
        {
            if (Interlocked.CompareExchange(ref _called, 1, 0) == 0)
            {
                onReport(value);
            }
        }
    }
}
