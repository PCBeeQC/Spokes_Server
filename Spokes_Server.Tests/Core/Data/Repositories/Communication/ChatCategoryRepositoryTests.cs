using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using Spokes_Server.Core.Data;
using Spokes_Server.Core.Data.Repositories.Communication;
using Spokes_Server.Core.Models.Communication;
using Xunit;

namespace Spokes_Server.Tests.Core.Data.Repositories.Communication;

public class ChatCategoryRepositoryTests : IDisposable
{
    private readonly string _testDataDir;
    private readonly DiskPersistenceService _writer;
    private readonly ChatCategoryRepository _repo;

    public ChatCategoryRepositoryTests()
    {
        _testDataDir = Path.Combine(Path.GetTempPath(), $"Spokes_Test_ChatCategories_{Guid.NewGuid()}");

        var mockConfig = new Mock<IConfiguration>();
        mockConfig.Setup(c => c["DataPath"]).Returns(_testDataDir);

        var mockPersistenceLogger = new Mock<ILogger<DiskPersistenceService>>();
        _writer = new DiskPersistenceService(mockPersistenceLogger.Object);

        _repo = new ChatCategoryRepository(_writer, mockConfig.Object);
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
    public void Constructor_WithNullDataPath_UsesFallbackPath()
    {
        var mockConfig = new Mock<IConfiguration>();
        mockConfig.Setup(c => c["DataPath"]).Returns((string?)null);

        var repo = new ChatCategoryRepository(_writer, mockConfig.Object);

        Assert.NotNull(repo);
    }

    [Fact]
    public void Save_And_GetById_ReturnsCategory()
    {
        var cat = new ChatCategory { Id = "cat-custom", Name = "Special Topics", DisplayOrder = 10, IsSystem = false };

        _repo.Save(cat);

        var result = _repo.GetById("cat-custom");
        Assert.NotNull(result);
        Assert.Equal("Special Topics", result.Name);
        Assert.Equal(10, result.DisplayOrder);
        Assert.False(result.IsSystem);
    }

    [Fact]
    public void GetFilePath_SavesInChatCategoriesFolder()
    {
        var cat = new ChatCategory { Id = "cat-disk", Name = "General" };

        _repo.Save(cat);
        _writer.FlushAll();

        var expectedPath = Path.Combine(_testDataDir, "chat-categories", "cat-disk.json");
        Assert.True(File.Exists(expectedPath));
    }

    [Fact]
    public void EnsureDefaultCategories_CreatesAllDefaultCategories()
    {
        _repo.EnsureDefaultCategories();

        var all = _repo.GetAll();
        Assert.Equal(6, all.Count);

        var pub = _repo.GetById("sys_public");
        Assert.NotNull(pub);
        Assert.Equal("Public", pub.Name);
        Assert.Equal(-600, pub.DisplayOrder);
        Assert.True(pub.IsSystem);

        var proj = _repo.GetById("sys_projects");
        Assert.NotNull(proj);
        Assert.Equal("Projects", proj.Name);
        Assert.Equal(-500, proj.DisplayOrder);
        Assert.True(proj.IsSystem);

        var teams = _repo.GetById("sys_teams");
        Assert.NotNull(teams);
        Assert.Equal("Teams", teams.Name);
        Assert.Equal(-400, teams.DisplayOrder);
        Assert.True(teams.IsSystem);

        var groups = _repo.GetById("sys_groups");
        Assert.NotNull(groups);
        Assert.Equal("Group Chats", groups.Name);
        Assert.Equal(-300, groups.DisplayOrder);
        Assert.True(groups.IsSystem);

        var direct = _repo.GetById("sys_direct");
        Assert.NotNull(direct);
        Assert.Equal("Direct Messages", direct.Name);
        Assert.Equal(-200, direct.DisplayOrder);
        Assert.True(direct.IsSystem);

        var archive = _repo.GetById("sys_archive");
        Assert.NotNull(archive);
        Assert.Equal("Archived", archive.Name);
        Assert.Equal(10000, archive.DisplayOrder);
        Assert.True(archive.IsSystem);
    }

    [Fact]
    public void EnsureDefaultCategories_DoesNotOverwriteExistingCategory()
    {
        // Pre-seed an existing category with the same ID but custom name
        _repo.Save(new ChatCategory
        {
            Id = "sys_public",
            Name = "Custom Public",
            DisplayOrder = -999,
            IsSystem = false
        });

        _repo.EnsureDefaultCategories();

        var pub = _repo.GetById("sys_public");
        Assert.NotNull(pub);
        Assert.Equal("Custom Public", pub.Name);
        Assert.Equal(-999, pub.DisplayOrder);
        Assert.False(pub.IsSystem);
    }

    [Fact]
    public void Delete_RemovesCategoryFromCacheAndDisk()
    {
        var cat = new ChatCategory { Id = "cat-del", Name = "To Delete" };
        _repo.Save(cat);
        _writer.FlushAll();

        var expectedPath = Path.Combine(_testDataDir, "chat-categories", "cat-del.json");
        Assert.True(File.Exists(expectedPath));

        _repo.Delete("cat-del");
        _writer.FlushAll();

        Assert.Null(_repo.GetById("cat-del"));
        Assert.False(File.Exists(expectedPath));
    }
}
