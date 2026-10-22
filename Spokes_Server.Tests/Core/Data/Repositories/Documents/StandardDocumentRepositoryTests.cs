using Spokes_Server.Core.Data.Repositories.Documents;
using Spokes_Server.Core.Models.Documents;
using Spokes_Server.Tests.Helpers;
using Xunit;

namespace Spokes_Server.Tests.Core.Data.Repositories.Documents;

public class StandardDocumentRepositoryTests : RepositoryTestFixture<StandardDocumentRepository>
{
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

        var newRepo = new StandardDocumentRepository(_writer, Config);
        newRepo.LoadFromDisk();

        var loaded = newRepo.GetById("loaded-doc");
        Assert.NotNull(loaded);
        Assert.Equal("Disk Doc", loaded.Name);
    }
}
