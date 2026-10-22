using Spokes_Server.Core.Data;
using Spokes_Server.Core.Data.Repositories.Core;
using Spokes_Server.Core.Models.Core;
using Spokes_Server.Core.Services.Licensing;
using Spokes_Server.Tests.Helpers;
using Xunit;

namespace Spokes_Server.Tests.Core.Data.Repositories.Core;

public class ServerConfigRepositoryTests : RepositoryTestFixture<ServerConfigRepository>
{
    public ServerConfigRepositoryTests()
        : base((writer, config) => new ServerConfigRepository(writer, config), "ServerConfig")
    {
    }

    [Fact]
    public void GetOrCreateGlobalConfig_CreatesNewConfigWhenMissing()
    {
        var config = _repo.GetOrCreateGlobalConfig();

        Assert.NotNull(config);
        Assert.Equal("Global", config.Id);
        Assert.False(string.IsNullOrWhiteSpace(config.DatabaseCreationId));

        var inCache = _repo.GetById("Global");
        Assert.NotNull(inCache);
        Assert.Equal("Global", inCache.Id);
        Assert.Equal(config.DatabaseCreationId, inCache.DatabaseCreationId);
    }

    [Fact]
    public void GetOrCreateGlobalConfig_PreservesExistingConfigWhenAlreadyPopulated()
    {
        var existing = new ServerConfig
        {
            Id = "Global",
            DatabaseCreationVersion = "1.0.0",
            DatabaseCreationId = "db-custom-id",
            RelayTrialTicket = new RelayTrialTicket { Fingerprint = "fp", IssuedAt = "2026-10-01T00:00:00Z", ExpiresAt = "2026-12-01T00:00:00Z", Signature = "sig" }
        };
        _repo.Save(existing);

        var retrieved = _repo.GetOrCreateGlobalConfig();

        Assert.Equal("1.0.0", retrieved.DatabaseCreationVersion);
        Assert.Equal("db-custom-id", retrieved.DatabaseCreationId);
        Assert.NotNull(retrieved.RelayTrialTicket);
        Assert.Equal("2026-12-01T00:00:00Z", retrieved.RelayTrialTicket!.ExpiresAt);
    }

    [Fact]
    public void GetFilePath_SavesInGlobalServerConfigJson()
    {
        var config = new ServerConfig { Id = "Global" };
        _repo.Save(config);
        _writer.FlushAll();

        var expectedPath = Path.Combine(_testDataDir, "Global", "ServerConfig.json");
        Assert.True(File.Exists(expectedPath));
    }

    [Fact]
    public void Delete_RemovesFromCacheAndDisk()
    {
        var config = new ServerConfig { Id = "Global" };
        _repo.Save(config);
        _writer.FlushAll();

        var expectedPath = Path.Combine(_testDataDir, "Global", "ServerConfig.json");
        Assert.True(File.Exists(expectedPath));

        _repo.Delete("Global");
        _writer.FlushAll();

        Assert.Null(_repo.GetById("Global"));
        Assert.False(File.Exists(expectedPath));
    }
}
