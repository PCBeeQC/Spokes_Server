using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using Spokes_Server.Core.Data;
using Spokes_Server.Core.Data.Repositories.Communication;
using Spokes_Server.Core.Models.Communication;
using Xunit;

namespace Spokes_Server.Tests.Core.Data.Repositories.Communication;

public class AlbumRepositoryTests : IDisposable
{
    private readonly string _testDataDir;
    private readonly DiskPersistenceService _writer;
    private readonly Mock<IConfiguration> _mockConfig;
    private readonly AlbumRepository _repo;

    public AlbumRepositoryTests()
    {
        _testDataDir = Path.Combine(Path.GetTempPath(), $"Spokes_Test_Albums_{Guid.NewGuid()}");

        _mockConfig = new Mock<IConfiguration>();
        _mockConfig.Setup(c => c["DataPath"]).Returns(_testDataDir);

        var mockLogger = new Mock<ILogger<DiskPersistenceService>>();
        _writer = new DiskPersistenceService(mockLogger.Object);

        _repo = new AlbumRepository(_writer, _mockConfig.Object);
    }

    public void Dispose()
    {
        if (Directory.Exists(_testDataDir))
        {
            try { Directory.Delete(_testDataDir, true); } catch { }
        }
        _writer.Dispose();
    }

    [Fact]
    public void LoadFromDisk_WithAsteriskPattern_LoadsAlbums()
    {
        var albumsDir = Path.Combine(_testDataDir, "Albums");
        Directory.CreateDirectory(albumsDir);
        File.WriteAllText(Path.Combine(albumsDir, "album3.json"), "{\"Id\":\"album3\",\"Title\":\"Loaded Album\"}");
        File.WriteAllText(Path.Combine(albumsDir, "album4.json"), "{\"Id\":\"album4\",\"Title\":\"Another Album\"}");

        var repo2 = new AlbumRepository(_writer, _mockConfig.Object);
        repo2.LoadFromDisk();
        
        var loadedAlbums = repo2.GetAll();
        Assert.True(loadedAlbums.Count >= 2, "Should load at least the manually written albums");
        Assert.Contains(loadedAlbums, a => a.Id == "album3");
        Assert.Contains(loadedAlbums, a => a.Id == "album4");
    }

    [Fact]
    public void Constructor_WithNullDataPath_UsesDefaultPath()
    {
        var mockConfig = new Mock<IConfiguration>();
        mockConfig.Setup(c => c["DataPath"]).Returns((string?)null);

        var repo = new AlbumRepository(_writer, mockConfig.Object);

        Assert.NotNull(repo);
    }

    [Fact]
    public void GetFilePath_SavesInAlbumsFolder()
    {
        var album = new Album { Id = "album-disk", Title = "Summer BBQ" };

        _repo.Save(album);
        _writer.FlushAll();

        var expectedPath = Path.Combine(_testDataDir, "Albums", "album-disk.json");
        Assert.True(File.Exists(expectedPath));
    }

    [Fact]
    public void Save_And_GetById_ReturnsAlbum()
    {
        var album = new Album { Id = "album-1", Title = "Company Event 2026", Description = "Photos from picnic" };

        _repo.Save(album);

        var found = _repo.GetById("album-1");
        Assert.NotNull(found);
        Assert.Equal("Company Event 2026", found.Title);
        Assert.Equal("Photos from picnic", found.Description);
    }

    [Fact]
    public void GetAll_ReturnsAllSavedAlbums()
    {
        var album1 = new Album { Id = "a1", Title = "Album 1" };
        var album2 = new Album { Id = "a2", Title = "Album 2" };

        _repo.Save(album1);
        _repo.Save(album2);

        var all = _repo.GetAll();
        Assert.Equal(2, all.Count);
        Assert.Contains(all, a => a.Id == "a1");
        Assert.Contains(all, a => a.Id == "a2");
    }

    [Fact]
    public void Delete_RemovesAlbumFromCacheAndDisk()
    {
        var album = new Album { Id = "album-del", Title = "To Delete" };
        _repo.Save(album);
        _writer.FlushAll();

        var expectedPath = Path.Combine(_testDataDir, "Albums", "album-del.json");
        Assert.True(File.Exists(expectedPath));

        _repo.Delete("album-del");
        _writer.FlushAll();

        Assert.Null(_repo.GetById("album-del"));
        Assert.False(File.Exists(expectedPath));
    }
}
