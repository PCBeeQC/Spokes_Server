using System.Text.Json;
using Spokes_Server.Core.Data.Repositories.Communication;
using Spokes_Server.Core.Models.Communication;
using Spokes_Server.Tests.Helpers;
using Xunit;

namespace Spokes_Server.Tests.Core.Data.Repositories.Communication;

public class PublicContactRepositoryTests : RepositoryTestFixture<PublicContactRepository>
{
    [Fact]
    public void Save_And_GetById_ReturnsContact()
    {
        var contact = new ContactPerson { Id = "pub-1", Name = "General Support", Email = "support@example.com" };

        _repo.Save(contact);

        var result = _repo.GetById("pub-1");
        Assert.NotNull(result);
        Assert.Equal("General Support", result.Name);
        Assert.Equal("support@example.com", result.Email);
    }

    [Fact]
    public void GetFilePath_SavesInPublicContactsDirectory()
    {
        var contact = new ContactPerson { Id = "pub-disk", Name = "Disk Contact" };

        _repo.Save(contact);
        _writer.FlushAll();

        var expectedPath = Path.Combine(_testDataDir, "PublicContacts", "pub-disk.json");
        Assert.True(File.Exists(expectedPath));
    }

    [Fact]
    public void GetAll_ReturnsAllContacts()
    {
        var c1 = new ContactPerson { Id = "c1", Name = "Contact 1" };
        var c2 = new ContactPerson { Id = "c2", Name = "Contact 2" };

        _repo.Save(c1);
        _repo.Save(c2);

        var all = _repo.GetAll();
        Assert.Equal(2, all.Count);
        Assert.Contains(all, c => c.Id == "c1");
        Assert.Contains(all, c => c.Id == "c2");
    }

    [Fact]
    public void Delete_RemovesContactFromCacheAndDisk()
    {
        var contact = new ContactPerson { Id = "pub-del", Name = "To Delete" };
        _repo.Save(contact);
        _writer.FlushAll();

        var expectedPath = Path.Combine(_testDataDir, "PublicContacts", "pub-del.json");
        Assert.True(File.Exists(expectedPath));

        _repo.Delete("pub-del");
        _writer.FlushAll();

        Assert.Null(_repo.GetById("pub-del"));
        Assert.False(File.Exists(expectedPath));
    }

    [Fact]
    public void LoadFromDisk_PopulatesContactsFromDisk()
    {
        var dir = Path.Combine(_testDataDir, "PublicContacts");
        Directory.CreateDirectory(dir);

        var contact = new ContactPerson { Id = "loaded-pub", Name = "Disk Loaded" };
        File.WriteAllText(Path.Combine(dir, "loaded-pub.json"), JsonSerializer.Serialize(contact));

        var newRepo = new PublicContactRepository(_writer, Config);
        newRepo.LoadFromDisk();

        var loaded = newRepo.GetById("loaded-pub");
        Assert.NotNull(loaded);
        Assert.Equal("Disk Loaded", loaded.Name);
    }
}
