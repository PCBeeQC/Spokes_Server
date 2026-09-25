using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using Spokes_Server.Core.Data;
using Spokes_Server.Core.Data.Repositories.Projects;
using Spokes_Server.Core.Models.Projects;
using Xunit;

namespace Spokes_Server.Tests.Core.Data.Repositories.Projects;

public class BoardTemplateRepositoryTests : IDisposable
{
    private readonly string _testDataDir;
    private readonly DiskPersistenceService _writer;
    private readonly BoardTemplateRepository _repo;

    public BoardTemplateRepositoryTests()
    {
        _testDataDir = Path.Combine(Path.GetTempPath(), $"Spokes_Test_BoardTemplates_{Guid.NewGuid()}");

        var mockConfig = new Mock<IConfiguration>();
        mockConfig.Setup(c => c["DataPath"]).Returns(_testDataDir);

        var mockPersistenceLogger = new Mock<ILogger<DiskPersistenceService>>();
        _writer = new DiskPersistenceService(mockPersistenceLogger.Object);

        _repo = new BoardTemplateRepository(_writer, mockConfig.Object);
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

        var repo = new BoardTemplateRepository(_writer, mockConfig.Object);

        Assert.NotNull(repo);
    }

    [Fact]
    public void Save_And_GetById_ReturnsBoardTemplate()
    {
        var template = new BoardTemplate
        {
            Id = "tmpl-1",
            Name = "Kanban Starter",
            Description = "Default Kanban board template"
        };

        _repo.Save(template);

        var found = _repo.GetById("tmpl-1");
        Assert.NotNull(found);
        Assert.Equal("Kanban Starter", found.Name);
        Assert.Equal("Default Kanban board template", found.Description);
    }

    [Fact]
    public void GetFilePath_SavesInTemplatesFolderWithIdJson()
    {
        var template = new BoardTemplate { Id = "tmpl-path", Name = "Template" };
        _repo.Save(template);
        _writer.FlushAll();

        var expectedPath = Path.Combine(_testDataDir, "Templates", "tmpl-path.json");
        Assert.True(File.Exists(expectedPath));
    }

    [Fact]
    public void GetAll_ReturnsAllTemplates()
    {
        var t1 = new BoardTemplate { Id = "t1", Name = "Template A" };
        var t2 = new BoardTemplate { Id = "t2", Name = "Template B" };

        _repo.Save(t1);
        _repo.Save(t2);

        var all = _repo.GetAll();
        Assert.Equal(2, all.Count);
        Assert.Contains(all, t => t.Id == "t1");
        Assert.Contains(all, t => t.Id == "t2");
    }

    [Fact]
    public void Delete_RemovesTemplateFromCacheAndDisk()
    {
        var template = new BoardTemplate { Id = "tmpl-del", Name = "Obsolete Template" };
        _repo.Save(template);
        _writer.FlushAll();

        var expectedPath = Path.Combine(_testDataDir, "Templates", "tmpl-del.json");
        Assert.True(File.Exists(expectedPath));

        _repo.Delete("tmpl-del");
        _writer.FlushAll();

        Assert.Null(_repo.GetById("tmpl-del"));
        Assert.False(File.Exists(expectedPath));
    }

    [Fact]
    public void LoadFromDisk_LoadsTemplatesFromDisk()
    {
        var targetDir = Path.Combine(_testDataDir, "Templates");
        Directory.CreateDirectory(targetDir);

        var template = new BoardTemplate { Id = "tmpl-disk", Name = "Disk Template" };
        File.WriteAllText(Path.Combine(targetDir, "tmpl-disk.json"), System.Text.Json.JsonSerializer.Serialize(template));

        var mockConfig = new Mock<IConfiguration>();
        mockConfig.Setup(c => c["DataPath"]).Returns(_testDataDir);

        var newRepo = new BoardTemplateRepository(_writer, mockConfig.Object);
        newRepo.LoadFromDisk();

        var loaded = newRepo.GetById("tmpl-disk");
        Assert.NotNull(loaded);
        Assert.Equal("Disk Template", loaded.Name);
    }
}
