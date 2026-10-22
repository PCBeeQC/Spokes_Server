using Spokes_Server.Core.Data.Repositories.Core;
using Spokes_Server.Core.Models.Core;
using Spokes_Server.Tests.Helpers;
using Xunit;

namespace Spokes_Server.Tests.Core.Data.Repositories.Core;

public class DemoConfigRepositoryTests : RepositoryTestFixture<DemoConfigRepository>
{
    [Fact]
    public void GetOrCreate_CreatesDefaultConfig_WhenMissing()
    {
        var config = _repo.GetOrCreate();

        Assert.NotNull(config);
        Assert.Equal("demo-config", config.Id);

        var inCache = _repo.GetById("demo-config");
        Assert.NotNull(inCache);
    }

    [Fact]
    public void Get_WhenMissing_ReturnsDefaultInstanceWithoutSaving()
    {
        var config = _repo.Get();

        Assert.NotNull(config);
        Assert.Equal("demo-config", config.Id);
        Assert.Null(_repo.GetById("demo-config"));
        Assert.Empty(_repo.GetAll());
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
