using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using Spokes_Server.Core.Data;
using Spokes_Server.Core.Data.Repositories.HR;
using Spokes_Server.Core.Models.HR;
using Xunit;

namespace Spokes_Server.Tests.Core.Data.Repositories.HR;

public class TeamRepositoryTests : IDisposable
{
    private readonly string _testDataDir;
    private readonly DiskPersistenceService _writer;
    private readonly TeamRepository _repo;

    public TeamRepositoryTests()
    {
        _testDataDir = Path.Combine(Path.GetTempPath(), $"Spokes_Test_Teams_{Guid.NewGuid()}");

        var mockConfig = new Mock<IConfiguration>();
        mockConfig.Setup(c => c["DataPath"]).Returns(_testDataDir);

        var mockPersistenceLogger = new Mock<ILogger<DiskPersistenceService>>();
        _writer = new DiskPersistenceService(mockPersistenceLogger.Object);

        _repo = new TeamRepository(_writer, mockConfig.Object);
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

        var repo = new TeamRepository(_writer, mockConfig.Object);

        Assert.NotNull(repo);
    }

    [Fact]
    public void Save_And_GetById_ReturnsTeam()
    {
        var team = new Team
        {
            Id = "t1",
            Name = "Engineering",
            Description = "Core development team",
            LeaderId = "emp-leader",
            Color = "#FF5722"
        };

        _repo.Save(team);

        var found = _repo.GetById("t1");
        Assert.NotNull(found);
        Assert.Equal("Engineering", found.Name);
        Assert.Equal("emp-leader", found.LeaderId);
        Assert.Equal("#FF5722", found.Color);
    }

    [Fact]
    public void GetFilePath_SavesInTeamsFolderWithIdJson()
    {
        var team = new Team { Id = "team-disk", Name = "Operations" };
        _repo.Save(team);
        _writer.FlushAll();

        var expectedPath = Path.Combine(_testDataDir, "Teams", "team-disk.json");
        Assert.True(File.Exists(expectedPath));
    }

    [Fact]
    public void GetAll_ReturnsAllTeams()
    {
        var t1 = new Team { Id = "t1", Name = "Team A" };
        var t2 = new Team { Id = "t2", Name = "Team B" };

        _repo.Save(t1);
        _repo.Save(t2);

        var all = _repo.GetAll();
        Assert.Equal(2, all.Count);
        Assert.Contains(all, t => t.Id == "t1");
        Assert.Contains(all, t => t.Id == "t2");
    }

    [Fact]
    public void Delete_RemovesFromCacheAndDisk()
    {
        var team = new Team { Id = "team-del", Name = "Obsolete Team" };
        _repo.Save(team);
        _writer.FlushAll();

        var expectedPath = Path.Combine(_testDataDir, "Teams", "team-del.json");
        Assert.True(File.Exists(expectedPath));

        _repo.Delete("team-del");
        _writer.FlushAll();

        Assert.Null(_repo.GetById("team-del"));
        Assert.False(File.Exists(expectedPath));
    }

    [Fact]
    public void LoadFromDisk_PopulatesTeams()
    {
        var teamsDir = Path.Combine(_testDataDir, "Teams");
        Directory.CreateDirectory(teamsDir);

        var team = new Team { Id = "loaded-team", Name = "Loaded Team" };
        File.WriteAllText(Path.Combine(teamsDir, "loaded-team.json"), System.Text.Json.JsonSerializer.Serialize(team));

        var mockConfig = new Mock<IConfiguration>();
        mockConfig.Setup(c => c["DataPath"]).Returns(_testDataDir);

        var newRepo = new TeamRepository(_writer, mockConfig.Object);
        newRepo.LoadFromDisk();

        var loaded = newRepo.GetById("loaded-team");
        Assert.NotNull(loaded);
        Assert.Equal("Loaded Team", loaded.Name);
    }
}
