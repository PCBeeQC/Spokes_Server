using Spokes_Server.Core.Data.Repositories.Projects;
using Spokes_Server.Core.Models.Projects;
using Spokes_Server.Tests.Helpers;
using Xunit;

namespace Spokes_Server.Tests.Core.Data.Repositories.Projects;

public class BoardCardRepositoryTests : RepositoryTestFixture<BoardCardRepository>
{
    [Fact]
    public void Save_And_GetById_ReturnsBoardCard()
    {
        var card = new BoardCard
        {
            Id = "card-1",
            BoardId = "board-100",
            Title = "Implement Feature X"
        };

        _repo.Save(card);

        var found = _repo.GetById("card-1");
        Assert.NotNull(found);
        Assert.Equal("Implement Feature X", found.Title);
        Assert.Equal("board-100", found.BoardId);
    }

    [Fact]
    public void GetFilePath_SavesInBoardCardsFolderWithIdJson()
    {
        var card = new BoardCard { Id = "card-path", BoardId = "board-100", Title = "Card" };
        _repo.Save(card);
        _writer.FlushAll();

        var expectedPath = Path.Combine(_testDataDir, "BoardCards", "card-path.json");
        Assert.True(File.Exists(expectedPath));
    }

    [Fact]
    public void GetByBoardId_FiltersByBoardIdCorrectly()
    {
        var c1 = new BoardCard { Id = "c1", BoardId = "b1", Title = "Card 1" };
        var c2 = new BoardCard { Id = "c2", BoardId = "b1", Title = "Card 2" };
        var c3 = new BoardCard { Id = "c3", BoardId = "b2", Title = "Card 3" };

        _repo.Save(c1);
        _repo.Save(c2);
        _repo.Save(c3);

        var results = _repo.GetByBoardId("b1");
        Assert.Equal(2, results.Count);
        Assert.Contains(results, c => c.Id == "c1");
        Assert.Contains(results, c => c.Id == "c2");
        Assert.DoesNotContain(results, c => c.Id == "c3");
    }

    [Fact]
    public void Delete_RemovesCardFromCacheAndDisk()
    {
        var card = new BoardCard { Id = "card-del", BoardId = "b1" };
        _repo.Save(card);
        _writer.FlushAll();

        var expectedPath = Path.Combine(_testDataDir, "BoardCards", "card-del.json");
        Assert.True(File.Exists(expectedPath));

        _repo.Delete("card-del");
        _writer.FlushAll();

        Assert.Null(_repo.GetById("card-del"));
        Assert.False(File.Exists(expectedPath));
    }

    [Fact]
    public void LoadFromDisk_LoadsCardsFromDisk()
    {
        var targetDir = Path.Combine(_testDataDir, "BoardCards");
        Directory.CreateDirectory(targetDir);

        var card = new BoardCard { Id = "card-disk", BoardId = "b-disk", Title = "From Disk" };
        File.WriteAllText(Path.Combine(targetDir, "card-disk.json"), System.Text.Json.JsonSerializer.Serialize(card));

        var newRepo = new BoardCardRepository(_writer, Config);
        newRepo.LoadFromDisk();

        var loaded = newRepo.GetById("card-disk");
        Assert.NotNull(loaded);
        Assert.Equal("From Disk", loaded.Title);
    }
}
