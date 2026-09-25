using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using Spokes_Server.Core.Data;
using Spokes_Server.Core.Data.Repositories.Core;
using Spokes_Server.Core.Models.Core;
using Xunit;

namespace Spokes_Server.Tests.Core.Data.Repositories.Core;

public class DocumentTemplateRepositoryTests : IDisposable
{
    private readonly string _testDataDir;
    private readonly DiskPersistenceService _writer;
    private readonly DocumentTemplateRepository _repo;

    public DocumentTemplateRepositoryTests()
    {
        _testDataDir = Path.Combine(Path.GetTempPath(), $"Spokes_Test_DocTemplates_{Guid.NewGuid()}");

        var mockConfig = new Mock<IConfiguration>();
        mockConfig.Setup(c => c["DataPath"]).Returns(_testDataDir);

        var mockPersistenceLogger = new Mock<ILogger<DiskPersistenceService>>();
        _writer = new DiskPersistenceService(mockPersistenceLogger.Object);

        _repo = new DocumentTemplateRepository(_writer, mockConfig.Object);
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

        var repo = new DocumentTemplateRepository(_writer, mockConfig.Object);

        Assert.NotNull(repo);
    }

    [Fact]
    public void GetDefault_ReturnsDefaultTemplateWhenConfigured()
    {
        var t1 = new DocumentTemplate { Id = "t1", Name = "Standard", IsDefault = false };
        var t2 = new DocumentTemplate { Id = "t2", Name = "Official Default", IsDefault = true };

        _repo.Save(t1);
        _repo.Save(t2);

        var def = _repo.GetDefault();
        Assert.NotNull(def);
        Assert.Equal("t2", def.Id);
        Assert.True(def.IsDefault);
    }

    [Fact]
    public void GetDefault_ReturnsFirstTemplateWhenNoDefaultConfigured()
    {
        var t1 = new DocumentTemplate { Id = "t1", Name = "Only Template", IsDefault = false };
        _repo.Save(t1);

        var def = _repo.GetDefault();
        Assert.NotNull(def);
        Assert.Equal("t1", def.Id);
    }

    [Fact]
    public void GetDefault_ReturnsNewInstanceWhenEmpty()
    {
        var def = _repo.GetDefault();
        Assert.NotNull(def);
        Assert.NotNull(def.Id);
    }

    [Fact]
    public void GetFilePath_SavesInSettingsTemplatesFolder()
    {
        var template = new DocumentTemplate { Id = "tmpl-disk", Name = "Disk Template" };
        _repo.Save(template);
        _writer.FlushAll();

        var expectedPath = Path.Combine(_testDataDir, "Settings", "Templates", "tmpl-disk.json");
        Assert.True(File.Exists(expectedPath));
    }

    [Fact]
    public void Delete_RemovesFromCacheAndDisk()
    {
        var template = new DocumentTemplate { Id = "tmpl-del", Name = "To Delete" };
        _repo.Save(template);
        _writer.FlushAll();

        var expectedPath = Path.Combine(_testDataDir, "Settings", "Templates", "tmpl-del.json");
        Assert.True(File.Exists(expectedPath));

        _repo.Delete("tmpl-del");
        _writer.FlushAll();

        Assert.Null(_repo.GetById("tmpl-del"));
        Assert.False(File.Exists(expectedPath));
    }
}
