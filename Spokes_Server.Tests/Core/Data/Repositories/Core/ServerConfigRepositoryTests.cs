using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using Spokes_Server.Core.Data;
using Spokes_Server.Core.Data.Repositories.Core;
using Spokes_Server.Core.Models.Core;
using Spokes_Server.Core.Services.Core;
using Xunit;

namespace Spokes_Server.Tests.Core.Data.Repositories.Core;

public class ServerConfigRepositoryTests : IDisposable
{
    private readonly string _testDataDir;
    private readonly DiskPersistenceService _writer;
    private readonly EncryptionService _encryptionService;
    private readonly IConfiguration _config;
    private readonly ServerConfigRepository _repo;

    public ServerConfigRepositoryTests()
    {
        _testDataDir = Path.Combine(Path.GetTempPath(), $"Spokes_Test_ServerConfig_{Guid.NewGuid()}");
        Directory.CreateDirectory(_testDataDir);

        var inMemorySettings = new Dictionary<string, string?>
        {
            { "DataPath", _testDataDir }
        };
        _config = new ConfigurationBuilder().AddInMemoryCollection(inMemorySettings).Build();

        var mockPersistenceLogger = new Mock<ILogger<DiskPersistenceService>>();
        _writer = new DiskPersistenceService(mockPersistenceLogger.Object);

        _encryptionService = new EncryptionService(_config);
        _repo = new ServerConfigRepository(_writer, _config, _encryptionService);
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

        var repo = new ServerConfigRepository(_writer, mockConfig.Object, _encryptionService);

        Assert.NotNull(repo);
    }

    [Fact]
    public void GetOrCreateGlobalConfig_CreatesAndSignsNewConfigWhenMissing()
    {
        var config = _repo.GetOrCreateGlobalConfig();

        Assert.NotNull(config);
        Assert.Equal("Global", config.Id);
        Assert.False(string.IsNullOrEmpty(config.DatabaseCreationVersion));
        Assert.False(string.IsNullOrEmpty(config.DatabaseCreationSignature));
        Assert.False(string.IsNullOrEmpty(config.DatabaseCreationId));
        Assert.False(string.IsNullOrEmpty(config.DatabaseCreationIdSignature));

        var inCache = _repo.GetById("Global");
        Assert.NotNull(inCache);
        Assert.Equal(config.DatabaseCreationId, inCache.DatabaseCreationId);
    }

    [Fact]
    public void GetOrCreateGlobalConfig_PreservesExistingConfigWhenAlreadyPopulated()
    {
        var existing = new ServerConfig
        {
            Id = "Global",
            DatabaseCreationVersion = "1.0.0",
            DatabaseCreationSignature = "sig-ver",
            DatabaseCreationId = "db-custom-id",
            DatabaseCreationIdSignature = "sig-id"
        };
        _repo.Save(existing);

        var retrieved = _repo.GetOrCreateGlobalConfig();

        Assert.Equal("1.0.0", retrieved.DatabaseCreationVersion);
        Assert.Equal("sig-ver", retrieved.DatabaseCreationSignature);
        Assert.Equal("db-custom-id", retrieved.DatabaseCreationId);
        Assert.Equal("sig-id", retrieved.DatabaseCreationIdSignature);
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
