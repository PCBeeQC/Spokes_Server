using System.Text.Json;
using Spokes_Server.Core.Data.Repositories.Accounting;
using Spokes_Server.Core.Models.Accounting;
using Spokes_Server.Core.Models.Core;
using Spokes_Server.Tests.Helpers;
using Xunit;

namespace Spokes_Server.Tests.Core.Data.Repositories.Accounting;

public class SupplierRepositoryTests : RepositoryTestFixture<SupplierRepository>
{
    [Fact]
    public void Save_And_GetById_ReturnsSupplier()
    {
        var supplier = new Supplier
        {
            Id = "sup-1",
            Info = new ClientInfo
            {
                BusinessName = "Acme Corp",
                ContactPersonEmail = "info@acme.com"
            }
        };

        _repo.Save(supplier);

        var result = _repo.GetById("sup-1");
        Assert.NotNull(result);
        Assert.Equal("Acme Corp", result.Name);
        Assert.Equal("info@acme.com", result.Info.ContactPersonEmail);
    }

    [Fact]
    public void GetFilePath_SavesInSettingsSuppliersFolder()
    {
        var supplier = new Supplier
        {
            Id = "sup-disk",
            Info = new ClientInfo { BusinessName = "Global Components" }
        };

        _repo.Save(supplier);
        _writer.FlushAll();

        var expectedPath = Path.Combine(_testDataDir, "Settings", "Suppliers", "sup-disk.json");
        Assert.True(File.Exists(expectedPath));
    }

    [Fact]
    public void GetAll_ReturnsAllSavedSuppliers()
    {
        var sup1 = new Supplier { Id = "sup-a", Info = new ClientInfo { BusinessName = "Supplier A" } };
        var sup2 = new Supplier { Id = "sup-b", Info = new ClientInfo { BusinessName = "Supplier B" } };

        _repo.Save(sup1);
        _repo.Save(sup2);

        var all = _repo.GetAll();
        Assert.Equal(2, all.Count);
        Assert.Contains(all, s => s.Id == "sup-a");
        Assert.Contains(all, s => s.Id == "sup-b");
    }

    [Fact]
    public void Delete_RemovesSupplierFromCacheAndDisk()
    {
        var supplier = new Supplier
        {
            Id = "sup-del",
            Info = new ClientInfo { BusinessName = "To Delete" }
        };
        _repo.Save(supplier);
        _writer.FlushAll();

        var expectedPath = Path.Combine(_testDataDir, "Settings", "Suppliers", "sup-del.json");
        Assert.True(File.Exists(expectedPath));

        _repo.Delete("sup-del");
        _writer.FlushAll();

        Assert.Null(_repo.GetById("sup-del"));
        Assert.False(File.Exists(expectedPath));
    }

    [Fact]
    public void LoadFromDisk_PopulatesSuppliersFromExistingFiles()
    {
        var suppliersDir = Path.Combine(_testDataDir, "Settings", "Suppliers");
        Directory.CreateDirectory(suppliersDir);

        var supplier = new Supplier
        {
            Id = "sup-disk-loaded",
            Info = new ClientInfo { BusinessName = "Disk Supplier" }
        };
        File.WriteAllText(Path.Combine(suppliersDir, "sup-disk-loaded.json"), JsonSerializer.Serialize(supplier));

        var newRepo = new SupplierRepository(_writer, Config);
        newRepo.LoadFromDisk();

        var loaded = newRepo.GetById("sup-disk-loaded");
        Assert.NotNull(loaded);
        Assert.Equal("Disk Supplier", loaded.Name);
    }

    [Fact]
    public void Search_FiltersAndLimitsCorrectly()
    {
        var s1 = new Supplier { Id = "s-1", Info = new ClientInfo { BusinessName = "Acme Tools" } };
        var s2 = new Supplier { Id = "s-2", Info = new ClientInfo { BusinessName = "Acme Supplies" } };
        var s3 = new Supplier { Id = "s-3", Info = new ClientInfo { BusinessName = "Beta Hardware" } };

        _repo.Save(s1);
        _repo.Save(s2);
        _repo.Save(s3);

        var matchAcme = _repo.Search("acme");
        Assert.Equal(2, matchAcme.Count);
        Assert.All(matchAcme, s => Assert.Contains("Acme", s.Name));

        var limit1 = _repo.Search("acme", maxResults: 1);
        Assert.Single(limit1);

        var emptyQuery = _repo.Search(null);
        Assert.Equal(3, emptyQuery.Count);

        var noMatch = _repo.Search("Nonexistent");
        Assert.Empty(noMatch);
    }
}
