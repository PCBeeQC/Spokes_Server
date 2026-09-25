using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using Spokes_Server.Core.Data;
using Spokes_Server.Core.Data.Repositories.Accounting;
using Spokes_Server.Core.Models.Accounting;
using Spokes_Server.Core.Models.Projects;
using Xunit;

namespace Spokes_Server.Tests.Core.Data.Repositories.Accounting;

public class SupplierRepositoryTests : IDisposable
{
    private readonly string _testDataDir;
    private readonly DiskPersistenceService _writer;
    private readonly SupplierRepository _repo;

    public SupplierRepositoryTests()
    {
        _testDataDir = Path.Combine(Path.GetTempPath(), $"Spokes_Test_Suppliers_{Guid.NewGuid()}");

        var mockConfig = new Mock<IConfiguration>();
        mockConfig.Setup(c => c["DataPath"]).Returns(_testDataDir);

        var mockPersistenceLogger = new Mock<ILogger<DiskPersistenceService>>();
        _writer = new DiskPersistenceService(mockPersistenceLogger.Object);

        _repo = new SupplierRepository(_writer, mockConfig.Object);
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

        var repo = new SupplierRepository(_writer, mockConfig.Object);

        Assert.NotNull(repo);
    }

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

        var mockConfig = new Mock<IConfiguration>();
        mockConfig.Setup(c => c["DataPath"]).Returns(_testDataDir);

        var newRepo = new SupplierRepository(_writer, mockConfig.Object);
        newRepo.LoadFromDisk();

        var loaded = newRepo.GetById("sup-disk-loaded");
        Assert.NotNull(loaded);
        Assert.Equal("Disk Supplier", loaded.Name);
    }
}
