using Spokes_Server.Core.Data.Repositories.Core;
using Spokes_Server.Core.Models.Core;
using Spokes_Server.Tests.Helpers;
using Xunit;

namespace Spokes_Server.Tests.Core.Data.Repositories.Core;

public class SystemConfigRepositoryTests : RepositoryTestFixture<SystemConfigRepository>
{
    [Fact]
    public void GetOrCreate_WhenEmpty_CreatesAndSavesDefaultConfig()
    {
        var config = _repo.GetOrCreate();

        Assert.NotNull(config);
        Assert.Equal("system_config", config.Id);
        Assert.False(config.IsSetupComplete);

        var inRepo = _repo.GetById("system_config");
        Assert.NotNull(inRepo);
    }

    [Fact]
    public void Get_WhenEmpty_ReturnsDefaultMemoryInstanceWithoutSaving()
    {
        var config = _repo.Get();

        Assert.NotNull(config);
        Assert.Equal("system_config", config.Id);
        Assert.False(config.IsSetupComplete);
        Assert.Null(_repo.GetById("system_config"));
        Assert.Empty(_repo.GetAll());
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
        var config = _repo.GetOrCreate();
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

        var newRepo = new SystemConfigRepository(_writer, Config);
        newRepo.LoadFromDisk();

        var loaded = newRepo.Get();
        Assert.NotNull(loaded);
        Assert.Equal("https://loaded.from.disk", loaded.ServerPublicUrl);
    }
}
