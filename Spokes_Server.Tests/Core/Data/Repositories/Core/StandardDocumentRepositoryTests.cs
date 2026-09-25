using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using Spokes_Server.Core.Data;
using Spokes_Server.Core.Data.Repositories.Core;
using Spokes_Server.Core.Models.Core;
using Xunit;

namespace Spokes_Server.Tests.Core.Data.Repositories.Core;

public class StandardDocumentRepositoryTests : IDisposable
{
    private readonly string _testDataDir;
    private readonly DiskPersistenceService _writer;
    private readonly StandardDocumentRepository _repo;

    public StandardDocumentRepositoryTests()
    {
        _testDataDir = Path.Combine(Path.GetTempPath(), $"Spokes_Test_StdDocs_{Guid.NewGuid()}");

        var mockConfig = new Mock<IConfiguration>();
        mockConfig.Setup(c => c["DataPath"]).Returns(_testDataDir);

        var mockPersistenceLogger = new Mock<ILogger<DiskPersistenceService>>();
        _writer = new DiskPersistenceService(mockPersistenceLogger.Object);

        _repo = new StandardDocumentRepository(_writer, mockConfig.Object);
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

        var repo = new StandardDocumentRepository(_writer, mockConfig.Object);

        Assert.NotNull(repo);
    }

    [Fact]
    public void Save_And_GetById_ReturnsDocument()
    {
        var doc = new StandardDocument { Id = "doc1", Name = "Employee Handbook" };
        _repo.Save(doc);

        var found = _repo.GetById("doc1");
        Assert.NotNull(found);
        Assert.Equal("Employee Handbook", found.Name);
    }

    [Fact]
    public void GetFilePath_SavesInSettingsStandardDocumentsFolder()
    {
        var doc = new StandardDocument { Id = "doc-disk", Name = "Safety Policy" };
        _repo.Save(doc);
        _writer.FlushAll();

        var expectedPath = Path.Combine(_testDataDir, "Settings", "StandardDocuments", "doc-disk.json");
        Assert.True(File.Exists(expectedPath));
    }

    [Fact]
    public void GetAll_ReturnsAllDocuments()
    {
        var d1 = new StandardDocument { Id = "d1", Name = "Policy A" };
        var d2 = new StandardDocument { Id = "d2", Name = "Policy B" };

        _repo.Save(d1);
        _repo.Save(d2);

        var all = _repo.GetAll();
        Assert.Equal(2, all.Count);
        Assert.Contains(all, d => d.Id == "d1");
        Assert.Contains(all, d => d.Id == "d2");
    }

    [Fact]
    public void Delete_RemovesFromCacheAndDisk()
    {
        var doc = new StandardDocument { Id = "doc-del", Name = "Obsolete" };
        _repo.Save(doc);
        _writer.FlushAll();

        var expectedPath = Path.Combine(_testDataDir, "Settings", "StandardDocuments", "doc-del.json");
        Assert.True(File.Exists(expectedPath));

        _repo.Delete("doc-del");
        _writer.FlushAll();

        Assert.Null(_repo.GetById("doc-del"));
        Assert.False(File.Exists(expectedPath));
    }

    [Fact]
    public void LoadFromDisk_PopulatesFromDisk()
    {
        var dir = Path.Combine(_testDataDir, "Settings", "StandardDocuments");
        Directory.CreateDirectory(dir);

        var doc = new StandardDocument { Id = "loaded-doc", Name = "Disk Doc" };
        File.WriteAllText(Path.Combine(dir, "loaded-doc.json"), System.Text.Json.JsonSerializer.Serialize(doc));

        var mockConfig = new Mock<IConfiguration>();
        mockConfig.Setup(c => c["DataPath"]).Returns(_testDataDir);

        var newRepo = new StandardDocumentRepository(_writer, mockConfig.Object);
        newRepo.LoadFromDisk();

        var loaded = newRepo.GetById("loaded-doc");
        Assert.NotNull(loaded);
        Assert.Equal("Disk Doc", loaded.Name);
    }
}
