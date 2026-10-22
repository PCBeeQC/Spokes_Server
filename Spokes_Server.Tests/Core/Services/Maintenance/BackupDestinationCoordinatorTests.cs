using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using Spokes_Server.Core.Configuration;
using Spokes_Server.Core.Data;
using Spokes_Server.Core.Data.Repositories.Core;
using Spokes_Server.Core.Models.Core;
using Spokes_Server.Core.Services.Maintenance.Destinations;
using Xunit;

namespace Spokes_Server.Tests.Core.Services.Maintenance;

public class BackupDestinationCoordinatorTests : IDisposable
{
    private readonly string _testDir;
    private readonly string _mountDir;
    private readonly CompanyProfileRepository _profiles;
    private readonly DiskPersistenceService _persistence;

    public BackupDestinationCoordinatorTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), "Spokes_DestTest_" + Guid.NewGuid().ToString("N"));
        _mountDir = Path.Combine(_testDir, "nas_mount");
        Directory.CreateDirectory(_testDir);
        Directory.CreateDirectory(_mountDir);

        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            { "DataPath", _testDir }
        }).Build();

        _persistence = new DiskPersistenceService(new Mock<ILogger<DiskPersistenceService>>().Object);
        _profiles = new CompanyProfileRepository(_persistence, config);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_testDir))
            {
                Directory.Delete(_testDir, true);
            }
        }
        catch { }
    }

    [Fact]
    public async Task LocalMountBackupProvider_UploadAndPrune_FunctionsCorrectly()
    {
        var profile = _profiles.Get();
        profile.Backup.SecondaryMount.IsEnabled = true;
        profile.Backup.SecondaryMount.MountPath = _mountDir;
        profile.Backup.SecondaryMount.RetentionCount = 2;
        _profiles.Save(profile);

        var provider = new LocalMountBackupProvider(_profiles);

        // Test connection
        var testResult = await provider.TestConnectionAsync();
        Assert.True(testResult.Success);

        // Create dummy backups
        var backup1 = Path.Combine(_testDir, "Spokes_Backup_20261015_010000.zip");
        var backup2 = Path.Combine(_testDir, "Spokes_Backup_20261015_020000.zip");
        var backup3 = Path.Combine(_testDir, "Spokes_Backup_20261015_030000.zip");

        await File.WriteAllTextAsync(backup1, "data1");
        await File.WriteAllTextAsync(backup2, "data2");
        await File.WriteAllTextAsync(backup3, "data3");

        var res1 = await provider.UploadAsync(backup1, "Spokes_Backup_20261015_010000.zip");
        Assert.True(res1.Success);

        var res2 = await provider.UploadAsync(backup2, "Spokes_Backup_20261015_020000.zip");
        Assert.True(res2.Success);

        var res3 = await provider.UploadAsync(backup3, "Spokes_Backup_20261015_030000.zip");
        Assert.True(res3.Success);

        var listed = await provider.ListBackupsAsync();
        Assert.Equal(3, listed.Count);

        // Prune down to retention 2
        await provider.PruneBackupsAsync(2);

        var afterPrune = await provider.ListBackupsAsync();
        Assert.Equal(2, afterPrune.Count);
    }

    [Fact]
    public async Task BackupDestinationCoordinator_DispatchesOnlyEnabledDestinations()
    {
        var profile = _profiles.Get();
        profile.Backup.SecondaryMount.IsEnabled = true;
        profile.Backup.SecondaryMount.MountPath = _mountDir;
        profile.Backup.S3.IsEnabled = false;
        profile.Backup.GoogleDrive.IsEnabled = false;
        _profiles.Save(profile);

        var mountProvider = new LocalMountBackupProvider(_profiles);
        var coordinator = new BackupDestinationCoordinator(new IBackupDestinationProvider[] { mountProvider }, _profiles);

        var localBackup = Path.Combine(_testDir, "Spokes_Backup_20261015_040000.spkbackup");
        await File.WriteAllTextAsync(localBackup, "encrypted_backup_bytes");

        var results = await coordinator.DispatchUploadAsync(localBackup, Path.GetFileName(localBackup));

        Assert.Single(results);
        Assert.True(results.ContainsKey(BackupDestinationType.LocalMount));
        Assert.True(results[BackupDestinationType.LocalMount].Success);
    }

    [Fact]
    public async Task LocalMountBackupProvider_UploadAsync_ReportsProgress()
    {
        var profile = _profiles.Get();
        profile.Backup.SecondaryMount.IsEnabled = true;
        profile.Backup.SecondaryMount.MountPath = _mountDir;
        _profiles.Save(profile);

        var provider = new LocalMountBackupProvider(_profiles);

        var localBackup = Path.Combine(_testDir, "Spokes_Backup_Progress_Test.zip");
        var testData = new byte[300 * 1024]; // 300 KB
        Random.Shared.NextBytes(testData);
        await File.WriteAllBytesAsync(localBackup, testData);

        var reports = new List<BackupUploadProgress>();
        var progressMock = new Mock<IProgress<BackupUploadProgress>>();
        progressMock.Setup(p => p.Report(It.IsAny<BackupUploadProgress>())).Callback<BackupUploadProgress>(reports.Add);

        var res = await provider.UploadAsync(localBackup, "Spokes_Backup_Progress_Test.zip", progressMock.Object);

        Assert.True(res.Success);
        Assert.NotEmpty(reports);
        Assert.Equal(100.0, reports.Last().Percent);
        Assert.Equal(testData.Length, reports.Last().BytesTransferred);
    }

    [Fact]
    public async Task BackupDestinationCoordinator_TracksStatusAndFiresEvents()
    {
        var profile = _profiles.Get();
        profile.Backup.SecondaryMount.IsEnabled = true;
        profile.Backup.SecondaryMount.MountPath = _mountDir;
        _profiles.Save(profile);

        var mountProvider = new LocalMountBackupProvider(_profiles);
        var coordinator = new BackupDestinationCoordinator(new IBackupDestinationProvider[] { mountProvider }, _profiles);

        var recordedStatuses = new List<DestinationUploadStatus>();
        coordinator.OnDestinationStatusChanged += (type, status) =>
        {
            recordedStatuses.Add(new DestinationUploadStatus
            {
                DestinationType = type,
                State = status.State,
                Percent = status.Percent,
                BytesTransferred = status.BytesTransferred,
                TotalBytes = status.TotalBytes,
                ErrorMessage = status.ErrorMessage
            });
        };

        var localBackup = Path.Combine(_testDir, "Spokes_Backup_Status_Test.zip");
        await File.WriteAllTextAsync(localBackup, "some test backup content");

        var results = await coordinator.DispatchUploadAsync(localBackup, Path.GetFileName(localBackup));

        Assert.True(results[BackupDestinationType.LocalMount].Success);
        Assert.NotEmpty(recordedStatuses);

        var finalStatus = coordinator.GetStatus(BackupDestinationType.LocalMount);
        Assert.Equal(RemoteUploadState.Success, finalStatus.State);
        Assert.Equal(100.0, finalStatus.Percent);
        Assert.Null(finalStatus.ErrorMessage);
        Assert.NotNull(finalStatus.LastCompletedAt);
    }

    [Fact]
    public async Task BackupDestinationCoordinator_FailedUpload_UpdatesStatusToFailed()
    {
        var profile = _profiles.Get();
        profile.Backup.SecondaryMount.IsEnabled = true;
        _profiles.Save(profile);

        var mockProvider = new Mock<IBackupDestinationProvider>();
        mockProvider.Setup(p => p.DestinationType).Returns(BackupDestinationType.LocalMount);
        mockProvider.Setup(p => p.UploadAsync(
            It.IsAny<string>(),
            It.IsAny<string>(),
            It.IsAny<IProgress<BackupUploadProgress>?>(),
            It.IsAny<CancellationToken>()))
            .ReturnsAsync(BackupUploadResult.Failed("Disk is full"));

        var coordinator = new BackupDestinationCoordinator(new[] { mockProvider.Object }, _profiles);

        var localBackup = Path.Combine(_testDir, "Spokes_Backup_Fail_Test.zip");
        await File.WriteAllTextAsync(localBackup, "dummy data");

        var results = await coordinator.DispatchUploadAsync(localBackup, Path.GetFileName(localBackup));

        Assert.False(results[BackupDestinationType.LocalMount].Success);
        var finalStatus = coordinator.GetStatus(BackupDestinationType.LocalMount);
        Assert.Equal(RemoteUploadState.Failed, finalStatus.State);
        Assert.Equal("Disk is full", finalStatus.ErrorMessage);
    }

    [Fact]
    public async Task BackupDestinationCoordinator_DispatchesMultipleDestinationsInParallel()
    {
        var profile = _profiles.Get();
        profile.Backup.SecondaryMount.IsEnabled = true;
        profile.Backup.S3.IsEnabled = true;
        _profiles.Save(profile);

        int activeConcurrent = 0;
        int maxConcurrent = 0;
        object lockObj = new();

        var mountProvider = new Mock<IBackupDestinationProvider>();
        mountProvider.Setup(p => p.DestinationType).Returns(BackupDestinationType.LocalMount);
        mountProvider.Setup(p => p.UploadAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<IProgress<BackupUploadProgress>?>(), It.IsAny<CancellationToken>()))
            .Returns(async () =>
            {
                lock (lockObj)
                {
                    activeConcurrent++;
                    if (activeConcurrent > maxConcurrent) maxConcurrent = activeConcurrent;
                }
                await Task.Delay(100);
                lock (lockObj) activeConcurrent--;
                return BackupUploadResult.Succeeded("mount", 1024);
            });

        var s3Provider = new Mock<IBackupDestinationProvider>();
        s3Provider.Setup(p => p.DestinationType).Returns(BackupDestinationType.S3);
        s3Provider.Setup(p => p.UploadAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<IProgress<BackupUploadProgress>?>(), It.IsAny<CancellationToken>()))
            .Returns(async () =>
            {
                lock (lockObj)
                {
                    activeConcurrent++;
                    if (activeConcurrent > maxConcurrent) maxConcurrent = activeConcurrent;
                }
                await Task.Delay(100);
                lock (lockObj) activeConcurrent--;
                return BackupUploadResult.Succeeded("s3", 1024);
            });

        var coordinator = new BackupDestinationCoordinator(new[] { mountProvider.Object, s3Provider.Object }, _profiles);

        var localBackup = Path.Combine(_testDir, "Spokes_Backup_Parallel_Test.zip");
        await File.WriteAllTextAsync(localBackup, "parallel test data");

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var results = await coordinator.DispatchUploadAsync(localBackup, Path.GetFileName(localBackup));
        sw.Stop();

        Assert.Equal(2, results.Count);
        Assert.True(results[BackupDestinationType.LocalMount].Success);
        Assert.True(results[BackupDestinationType.S3].Success);
        Assert.Equal(RemoteUploadState.Success, coordinator.GetStatus(BackupDestinationType.LocalMount).State);
        Assert.Equal(RemoteUploadState.Success, coordinator.GetStatus(BackupDestinationType.S3).State);

        // Verify both providers ran concurrently in parallel
        Assert.True(maxConcurrent >= 2, $"Expected concurrent execution of at least 2 providers, but max was {maxConcurrent}");
        Assert.True(sw.ElapsedMilliseconds < 2000, $"Expected parallel execution under 2000ms, took {sw.ElapsedMilliseconds}ms");
    }

    [Fact]
    public async Task BackupDestinationCoordinator_ParallelUpload_OneFails_OthersSucceed()
    {
        var profile = _profiles.Get();
        profile.Backup.SecondaryMount.IsEnabled = true;
        profile.Backup.S3.IsEnabled = true;
        _profiles.Save(profile);

        var failingProvider = new Mock<IBackupDestinationProvider>();
        failingProvider.Setup(p => p.DestinationType).Returns(BackupDestinationType.LocalMount);
        failingProvider.Setup(p => p.UploadAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<IProgress<BackupUploadProgress>?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Simulated connection timeout"));

        var succeedingProvider = new Mock<IBackupDestinationProvider>();
        succeedingProvider.Setup(p => p.DestinationType).Returns(BackupDestinationType.S3);
        succeedingProvider.Setup(p => p.UploadAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<IProgress<BackupUploadProgress>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(BackupUploadResult.Succeeded("s3-remote-id", 2048));

        var coordinator = new BackupDestinationCoordinator(new[] { failingProvider.Object, succeedingProvider.Object }, _profiles);

        var localBackup = Path.Combine(_testDir, "Spokes_Backup_Isolation_Test.zip");
        await File.WriteAllTextAsync(localBackup, "isolation test data");

        var results = await coordinator.DispatchUploadAsync(localBackup, Path.GetFileName(localBackup));

        Assert.Equal(2, results.Count);
        Assert.False(results[BackupDestinationType.LocalMount].Success);
        Assert.Equal("Simulated connection timeout", results[BackupDestinationType.LocalMount].ErrorMessage);
        Assert.True(results[BackupDestinationType.S3].Success);

        Assert.Equal(RemoteUploadState.Failed, coordinator.GetStatus(BackupDestinationType.LocalMount).State);
        Assert.Equal(RemoteUploadState.Success, coordinator.GetStatus(BackupDestinationType.S3).State);
    }

    [Theory]
    [InlineData(null, "/")]
    [InlineData("", "/")]
    [InlineData("   ", "/")]
    [InlineData("/", "/")]
    [InlineData("backups", "/backups")]
    [InlineData("/backups", "/backups")]
    [InlineData("/backups/", "/backups")]
    [InlineData("backups/nested/", "/backups/nested")]
    public void SftpBackupProvider_PathNormalization_ProducesCleanPaths(string? input, string expectedDir)
    {
        var normalized = SftpBackupProvider.NormalizeRemoteDirectory(input);
        Assert.Equal(expectedDir, normalized);

        var combined = SftpBackupProvider.CombineRemotePath(normalized, "Spokes_Backup_Test.zip");
        Assert.DoesNotContain("//", combined);
        if (expectedDir == "/")
        {
            Assert.Equal("/Spokes_Backup_Test.zip", combined);
        }
        else
        {
            Assert.Equal($"{expectedDir}/Spokes_Backup_Test.zip", combined);
        }
    }

    [Theory]
    [InlineData(null, "")]
    [InlineData("", "")]
    [InlineData("   ", "")]
    [InlineData("/", "")]
    [InlineData("backups", "backups/")]
    [InlineData("/backups", "backups/")]
    [InlineData("/backups/", "backups/")]
    [InlineData("backups/subfolder/", "backups/subfolder/")]
    public void S3BackupProvider_NormalizePrefix_HandlesAllFormats(string? input, string expected)
    {
        var normalized = S3BackupProvider.NormalizePrefix(input);
        Assert.Equal(expected, normalized);
    }

    [Fact]
    public async Task LocalMountBackupProvider_PruneBackups_DeletesStaleTempFiles()
    {
        var profile = _profiles.Get();
        profile.Backup.SecondaryMount.IsEnabled = true;
        profile.Backup.SecondaryMount.MountPath = _mountDir;
        profile.Backup.SecondaryMount.RetentionCount = 5;
        _profiles.Save(profile);

        var provider = new LocalMountBackupProvider(_profiles);

        // Create a completed backup and two abandoned .tmp files
        var validBackup = Path.Combine(_mountDir, "Spokes_Backup_20261015_010000.zip");
        var abandonedTemp1 = Path.Combine(_mountDir, "Spokes_Backup_20261015_020000.zip.tmp");
        var abandonedTemp2 = Path.Combine(_mountDir, "Spokes_Backup_20261015_030000.spkbackup.tmp");

        await File.WriteAllTextAsync(validBackup, "valid backup");
        await File.WriteAllTextAsync(abandonedTemp1, "partial backup 1");
        await File.WriteAllTextAsync(abandonedTemp2, "partial backup 2");

        Assert.True(File.Exists(validBackup));
        Assert.True(File.Exists(abandonedTemp1));
        Assert.True(File.Exists(abandonedTemp2));

        await provider.PruneBackupsAsync(5);

        Assert.True(File.Exists(validBackup), "Valid backup should remain");
        Assert.False(File.Exists(abandonedTemp1), "Abandoned temp 1 should be pruned");
        Assert.False(File.Exists(abandonedTemp2), "Abandoned temp 2 should be pruned");
    }
}
