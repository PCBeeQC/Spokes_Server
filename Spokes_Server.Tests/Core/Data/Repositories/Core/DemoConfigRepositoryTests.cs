using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using Spokes_Server.Core.Data;
using Spokes_Server.Core.Data.Repositories.Core;
using Spokes_Server.Core.Models.Core;
using Xunit;

namespace Spokes_Server.Tests.Core.Data.Repositories.Core;

public class DemoConfigRepositoryTests : IDisposable
{
    private readonly string _testDataDir;
    private readonly DiskPersistenceService _writer;
    private readonly DemoConfigRepository _repo;

    public DemoConfigRepositoryTests()
    {
        _testDataDir = Path.Combine(Path.GetTempPath(), $"Spokes_Test_DemoConfig_{Guid.NewGuid()}");

        var mockConfig = new Mock<IConfiguration>();
        mockConfig.Setup(c => c["DataPath"]).Returns(_testDataDir);

        var mockPersistenceLogger = new Mock<ILogger<DiskPersistenceService>>();
        _writer = new DiskPersistenceService(mockPersistenceLogger.Object);

        _repo = new DemoConfigRepository(_writer, mockConfig.Object);
    }

    public void Dispose()
    {
        if (Directory.Exists(_testDataDir))
        {
            try { Directory.Delete(_testDataDir, true); } catch (Exception ex) { Console.WriteLine($"Cleanup failed: {ex.Message}"); }
        }
        _writer.Dispose();
    }

    [Fact]
    public void Constructor_WithNullDataPath_UsesDefaultPath()
    {
        var mockConfig = new Mock<IConfiguration>();
        mockConfig.Setup(c => c["DataPath"]).Returns((string?)null);

        var repo = new DemoConfigRepository(_writer, mockConfig.Object);

        Assert.NotNull(repo);
    }

    [Fact]
    public void Get_CreatesDefaultConfig_WhenMissing()
    {
        var config = _repo.Get();

        Assert.NotNull(config);
        Assert.Equal("demo-config", config.Id);

        var inCache = _repo.GetById("demo-config");
        Assert.NotNull(inCache);
    }

    [Fact]
    public void Get_ReturnsExistingConfig_WhenPresent()
    {
        var existing = new DemoConfig
        {
            Id = "demo-config",
            AutoLoginEmployeeIds = ["emp1", "emp2"]
        };
        _repo.Save(existing);

        var config = _repo.Get();
        Assert.NotNull(config);
        Assert.Equal(2, config.AutoLoginEmployeeIds.Count);
        Assert.Contains("emp1", config.AutoLoginEmployeeIds);
        Assert.Contains("emp2", config.AutoLoginEmployeeIds);
    }

    [Fact]
    public void GetFilePath_SavesInBackupsDemoConfigDirectory()
    {
        var config = new DemoConfig { Id = "demo-config" };
        _repo.Save(config);
        _writer.FlushAll();

        var expectedPath = Path.Combine(_testDataDir, "Backups", "DemoConfig", "demo-config.json");
        Assert.True(File.Exists(expectedPath));
    }

    [Fact]
    public void Delete_RemovesFromCacheAndDisk()
    {
        var config = new DemoConfig { Id = "demo-config" };
        _repo.Save(config);
        _writer.FlushAll();

        var expectedPath = Path.Combine(_testDataDir, "Backups", "DemoConfig", "demo-config.json");
        Assert.True(File.Exists(expectedPath));

        _repo.Delete("demo-config");
        _writer.FlushAll();

        Assert.Null(_repo.GetById("demo-config"));
        Assert.False(File.Exists(expectedPath));
    }
}
