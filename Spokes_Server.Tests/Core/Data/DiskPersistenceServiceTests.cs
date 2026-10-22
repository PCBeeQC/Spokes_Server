using Microsoft.Extensions.Logging;
using Moq;
using Spokes_Server.Core.Data;

namespace Spokes_Server.Tests.Core.Data;

public class DiskPersistenceServiceTests : IDisposable
{
    private readonly string _testDataDir;
    private readonly DiskPersistenceService _service;

    public DiskPersistenceServiceTests()
    {
        _testDataDir = Path.Combine(Path.GetTempPath(), $"Spokes_Test_Disk_{Guid.NewGuid()}");
        var mockLogger = new Mock<ILogger<DiskPersistenceService>>();
        _service = new DiskPersistenceService(mockLogger.Object);
    }

    public void Dispose()
    {
        if (Directory.Exists(_testDataDir))
        {
            try 
            { 
                Directory.Delete(_testDataDir, true); 
            } 
            catch (Exception ex) 
            { 
                System.Diagnostics.Debug.WriteLine($"Failed to delete test directory: {ex.Message}"); 
            }
        }
        _service.Dispose();
    }

    [Fact]
    public async Task QueueWrite_WritesFileToDisk()
    {
        var filePath = Path.Combine(_testDataDir, "test.json");
        var data = new { Name = "Test Data", Value = 42 };

        _service.QueueWrite(filePath, data);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await _service.StartAsync(cts.Token);
        await _service.StopAsync(CancellationToken.None);

        Assert.True(File.Exists(filePath));
        var content = await File.ReadAllTextAsync(filePath);
        Assert.Contains("Test Data", content);
        Assert.Contains("42", content);
    }

    [Fact]
    public async Task QueueDelete_DeletesFileFromDisk()
    {
        Directory.CreateDirectory(_testDataDir);
        var filePath = Path.Combine(_testDataDir, "test2.json");
        await File.WriteAllTextAsync(filePath, "dummy content");

        _service.QueueDelete(filePath);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await _service.StartAsync(cts.Token);
        await _service.StopAsync(CancellationToken.None);

        Assert.False(File.Exists(filePath));
    }

    [Fact]
    public async Task QueueWrite_OverwritesExistingFile()
    {
        Directory.CreateDirectory(_testDataDir);
        var filePath = Path.Combine(_testDataDir, "test3.json");
        await File.WriteAllTextAsync(filePath, "old content");

        var data = new { Name = "New Data" };
        _service.QueueWrite(filePath, data);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await _service.StartAsync(cts.Token);
        await _service.StopAsync(CancellationToken.None);

        Assert.True(File.Exists(filePath));
        var content = await File.ReadAllTextAsync(filePath);
        Assert.Contains("New Data", content);
        Assert.DoesNotContain("old content", content);
    }

    [Fact]
    public void FlushAll_ProcessesAllQueuedWrites_WithoutStartingBackgroundService()
    {
        var file1 = Path.Combine(_testDataDir, "flush1.json");
        var file2 = Path.Combine(_testDataDir, "flush2.json");
        var data1 = new { Name = "First", Value = 1 };
        var data2 = new { Name = "Second", Value = 2 };

        _service.QueueWrite(file1, data1);
        _service.QueueWrite(file2, data2);

        _service.FlushAll();

        Assert.True(File.Exists(file1));
        Assert.True(File.Exists(file2));
        var content1 = File.ReadAllText(file1);
        var content2 = File.ReadAllText(file2);
        Assert.Contains("First", content1);
        Assert.Contains("Second", content2);
    }

    [Fact]
    public async Task FlushAllAsync_ProcessesAllQueuedWrites_WithoutStartingBackgroundService()
    {
        var file1 = Path.Combine(_testDataDir, "flush_async1.json");
        var file2 = Path.Combine(_testDataDir, "flush_async2.json");
        var data1 = new { Name = "FirstAsync", Value = 10 };
        var data2 = new { Name = "SecondAsync", Value = 20 };

        _service.QueueWrite(file1, data1);
        _service.QueueWrite(file2, data2);

        await _service.FlushAllAsync();

        Assert.True(File.Exists(file1));
        Assert.True(File.Exists(file2));
        var content1 = await File.ReadAllTextAsync(file1);
        var content2 = await File.ReadAllTextAsync(file2);
        Assert.Contains("FirstAsync", content1);
        Assert.Contains("SecondAsync", content2);
    }

    [Fact]
    public void FlushAll_ProcessesAllQueuedDeletes_WithoutStartingBackgroundService()
    {
        Directory.CreateDirectory(_testDataDir);
        var file1 = Path.Combine(_testDataDir, "del1.json");
        var file2 = Path.Combine(_testDataDir, "del2.json");
        File.WriteAllText(file1, "content1");
        File.WriteAllText(file2, "content2");

        _service.QueueDelete(file1);
        _service.QueueDelete(file2);

        _service.FlushAll();

        Assert.False(File.Exists(file1));
        Assert.False(File.Exists(file2));
    }


    [Fact]
    public void QueueWrite_CreatesParentDirectoriesIfMissing()
    {
        var nestedPath = Path.Combine(_testDataDir, "sub", "nested", "file.json");
        var data = new { Message = "Nested Directory Test" };

        _service.QueueWrite(nestedPath, data);
        _service.FlushAll();

        Assert.True(File.Exists(nestedPath));
        var content = File.ReadAllText(nestedPath);
        Assert.Contains("Nested Directory Test", content);
    }

    [Fact]
    public async Task ExecuteAsync_FlushesRemainingItemsOnExit()
    {
        var file1 = Path.Combine(_testDataDir, "exit_flush1.json");
        var file2 = Path.Combine(_testDataDir, "exit_flush2.json");

        _service.QueueWrite(file1, new { Value = "Remaining1" });
        _service.QueueWrite(file2, new { Value = "Remaining2" });

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await _service.StartAsync(cts.Token);
        await _service.StopAsync(CancellationToken.None);

        Assert.True(File.Exists(file1));
        Assert.True(File.Exists(file2));
        var content1 = await File.ReadAllTextAsync(file1);
        var content2 = await File.ReadAllTextAsync(file2);
        Assert.Contains("Remaining1", content1);
        Assert.Contains("Remaining2", content2);
    }

    [Fact]
    public void QueueWrite_Success_CallsRecordPersistenceSuccess()
    {
        var mockHealth = new Mock<IStorageHealthNotifier>();
        var mockLog = new Mock<IStoragePersistenceLogger>();
        var mockLogger = new Mock<ILogger<DiskPersistenceService>>();
        using var service = new DiskPersistenceService(mockLogger.Object, mockHealth.Object, mockLog.Object);

        var filePath = Path.Combine(_testDataDir, "health_success.json");
        service.QueueWrite(filePath, new { Foo = "Bar" });
        service.FlushAll();

        Assert.True(File.Exists(filePath));
        mockHealth.Verify(h => h.RecordPersistenceSuccess(), Times.Once);
        mockHealth.Verify(h => h.RecordPersistenceFailure(), Times.Never);
        mockLog.Verify(l => l.LogError(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>()), Times.Never);
    }

    [Fact]
    public void QueueWrite_Failure_CallsRecordPersistenceFailureAndLogsError()
    {
        var mockHealth = new Mock<IStorageHealthNotifier>();
        var mockLog = new Mock<IStoragePersistenceLogger>();
        var mockLogger = new Mock<ILogger<DiskPersistenceService>>();
        using var service = new DiskPersistenceService(mockLogger.Object, mockHealth.Object, mockLog.Object);

        // Path with invalid characters or illegal null character will throw IOException / ArgumentException on File.Create
        var invalidPath = Path.Combine(_testDataDir, "invalid\0name.json");
        service.QueueWrite(invalidPath, new { Foo = "Bar" });
        service.FlushAll();

        mockHealth.Verify(h => h.RecordPersistenceFailure(), Times.Once);
        mockLog.Verify(l => l.LogError("Storage", It.IsAny<string>(), It.IsAny<string?>()), Times.Once);
    }
}

