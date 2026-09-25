using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using Spokes_Server.Core.Data;
using Spokes_Server.Core.Data.Repositories.Projects;
using Spokes_Server.Core.Models.Projects;
using Xunit;

namespace Spokes_Server.Tests.Core.Data.Repositories.Projects;

public class BoardRepositoryTests : IDisposable
{
    private readonly string _testDataDir;
    private readonly DiskPersistenceService _writer;
    private readonly BoardRepository _repo;

    public BoardRepositoryTests()
    {
        _testDataDir = Path.Combine(Path.GetTempPath(), $"Spokes_Test_Boards_{Guid.NewGuid()}");

        var mockConfig = new Mock<IConfiguration>();
        mockConfig.Setup(c => c["DataPath"]).Returns(_testDataDir);

        var mockPersistenceLogger = new Mock<ILogger<DiskPersistenceService>>();
        _writer = new DiskPersistenceService(mockPersistenceLogger.Object);

        _repo = new BoardRepository(_writer, mockConfig.Object);
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

        var repo = new BoardRepository(_writer, mockConfig.Object);

        Assert.NotNull(repo);
    }

    [Fact]
    public void Save_And_GetById_ReturnsBoard()
    {
        var board = new Board
        {
            Id = "board-1",
            Title = "Sprint 42",
            OwnerId = "emp-owner",
            IsPublic = true
        };

        _repo.Save(board);

        var found = _repo.GetById("board-1");
        Assert.NotNull(found);
        Assert.Equal("Sprint 42", found.Title);
        Assert.True(found.IsPublic);
    }

    [Fact]
    public void GetFilePath_SavesInBoardsFolderWithIdJson()
    {
        var board = new Board { Id = "board-path", Title = "Board" };
        _repo.Save(board);
        _writer.FlushAll();

        var expectedPath = Path.Combine(_testDataDir, "Boards", "board-path.json");
        Assert.True(File.Exists(expectedPath));
    }

    [Fact]
    public void GetAll_ReturnsAllBoards()
    {
        var b1 = new Board { Id = "b1", Title = "Board 1" };
        var b2 = new Board { Id = "b2", Title = "Board 2" };

        _repo.Save(b1);
        _repo.Save(b2);

        var all = _repo.GetAll();
        Assert.Equal(2, all.Count);
        Assert.Contains(all, b => b.Id == "b1");
        Assert.Contains(all, b => b.Id == "b2");
    }

    [Fact]
    public void Delete_RemovesBoardFromCacheAndDisk()
    {
        var board = new Board { Id = "board-del", Title = "Obsolete Board" };
        _repo.Save(board);
        _writer.FlushAll();

        var expectedPath = Path.Combine(_testDataDir, "Boards", "board-del.json");
        Assert.True(File.Exists(expectedPath));

        _repo.Delete("board-del");
        _writer.FlushAll();

        Assert.Null(_repo.GetById("board-del"));
        Assert.False(File.Exists(expectedPath));
    }

    [Fact]
    public void LoadFromDisk_LoadsBoardsFromDisk()
    {
        var targetDir = Path.Combine(_testDataDir, "Boards");
        Directory.CreateDirectory(targetDir);

        var board = new Board { Id = "board-disk", Title = "Disk Board" };
        File.WriteAllText(Path.Combine(targetDir, "board-disk.json"), System.Text.Json.JsonSerializer.Serialize(board));

        var mockConfig = new Mock<IConfiguration>();
        mockConfig.Setup(c => c["DataPath"]).Returns(_testDataDir);

        var newRepo = new BoardRepository(_writer, mockConfig.Object);
        newRepo.LoadFromDisk();

        var loaded = newRepo.GetById("board-disk");
        Assert.NotNull(loaded);
        Assert.Equal("Disk Board", loaded.Title);
    }
}
