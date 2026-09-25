using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using Spokes_Server.Core.Data;
using Spokes_Server.Core.Data.Repositories.Core;
using Spokes_Server.Core.Models.Core;
using Xunit;

namespace Spokes_Server.Tests.Core.Data.Repositories.Core;

public class SystemConfigRepositoryTests : IDisposable
{
    private readonly string _testDataDir;
    private readonly DiskPersistenceService _writer;
    private readonly SystemConfigRepository _repo;

    public SystemConfigRepositoryTests()
    {
        _testDataDir = Path.Combine(Path.GetTempPath(), $"Spokes_Test_SysConfig_{Guid.NewGuid()}");

        var mockConfig = new Mock<IConfiguration>();
        mockConfig.Setup(c => c["DataPath"]).Returns(_testDataDir);

        var mockPersistenceLogger = new Mock<ILogger<DiskPersistenceService>>();
        _writer = new DiskPersistenceService(mockPersistenceLogger.Object);

        _repo = new SystemConfigRepository(_writer, mockConfig.Object);
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

        var repo = new SystemConfigRepository(_writer, mockConfig.Object);

        Assert.NotNull(repo);
    }

    [Fact]
    public void Get_WhenEmpty_CreatesAndSavesDefaultConfig()
    {
        var config = _repo.Get();

        Assert.NotNull(config);
        Assert.Equal("system_config", config.Id);
        Assert.False(config.IsSetupComplete);

        var inRepo = _repo.GetById("system_config");
        Assert.NotNull(inRepo);
    }

    [Fact]
    public void Get_WhenConfigExists_ReturnsExistingConfig()
    {
        var existing = new SystemConfig
        {
            Id = "custom_id",
            ServerPublicUrl = "https://spokes.example.com",
            IsSetupComplete = true
        };
        _repo.Save(existing);

        var config = _repo.Get();

        Assert.NotNull(config);
        Assert.Equal("https://spokes.example.com", config.ServerPublicUrl);
        Assert.True(config.IsSetupComplete);
    }

    [Fact]
    public void GetFilePath_SavesInSettingsFolder()
    {
        var config = new SystemConfig { ServerPublicUrl = "https://test.local" };
        _repo.Save(config);
        _writer.FlushAll();

        var expectedPath = Path.Combine(_testDataDir, "Settings", "system_config.json");
        Assert.True(File.Exists(expectedPath));
    }

    [Fact]
    public void Delete_RemovesConfig()
    {
        var config = _repo.Get();
        _writer.FlushAll();

        var expectedPath = Path.Combine(_testDataDir, "Settings", "system_config.json");
        Assert.True(File.Exists(expectedPath));

        _repo.Delete(config.Id);
        _writer.FlushAll();

        Assert.Null(_repo.GetById(config.Id));
        Assert.False(File.Exists(expectedPath));
    }

    [Fact]
    public void LoadFromDisk_PopulatesFromDisk()
    {
        var dir = Path.Combine(_testDataDir, "Settings");
        Directory.CreateDirectory(dir);

        var cfg = new SystemConfig { Id = "disk_cfg", ServerPublicUrl = "https://loaded.from.disk" };
        File.WriteAllText(Path.Combine(dir, "system_config.json"), System.Text.Json.JsonSerializer.Serialize(cfg));

        var mockConfig = new Mock<IConfiguration>();
        mockConfig.Setup(c => c["DataPath"]).Returns(_testDataDir);

        var newRepo = new SystemConfigRepository(_writer, mockConfig.Object);
        newRepo.LoadFromDisk();

        var loaded = newRepo.Get();
        Assert.NotNull(loaded);
        Assert.Equal("https://loaded.from.disk", loaded.ServerPublicUrl);
    }
}
