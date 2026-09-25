using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using Spokes_Server.Core.Data;
using Spokes_Server.Core.Data.Repositories.Accounting;
using Spokes_Server.Core.Models.Accounting;
using Spokes_Server.Core.Models.Projects;
using Xunit;

namespace Spokes_Server.Tests.Core.Data.Repositories.Accounting;

public class PurchaseOrderRepositoryTests : IDisposable
{
    private readonly string _testDataDir;
    private readonly DiskPersistenceService _writer;
    private readonly PurchaseOrderRepository _repo;

    public PurchaseOrderRepositoryTests()
    {
        _testDataDir = Path.Combine(Path.GetTempPath(), $"Spokes_Test_Purchases_{Guid.NewGuid()}");

        var mockConfig = new Mock<IConfiguration>();
        mockConfig.Setup(c => c["DataPath"]).Returns(_testDataDir);

        var mockPersistenceLogger = new Mock<ILogger<DiskPersistenceService>>();
        _writer = new DiskPersistenceService(mockPersistenceLogger.Object);

        _repo = new PurchaseOrderRepository(_writer, mockConfig.Object);
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
    public void Save_GeneratesSequentialPoNumberIfMissing()
    {
        var testDate = new DateTime(2025, 12, 1);

        var po1 = new PurchaseOrder { Id = "po1", Date = testDate, PoNumber = null };
        var po2 = new PurchaseOrder { Id = "po2", Date = testDate, PoNumber = string.Empty };

        _repo.Save(po1);
        _repo.Save(po2);

        var storedItems = _repo.GetAll();
        var savedPo1 = storedItems.First(p => p.Id == "po1");
        var savedPo2 = storedItems.First(p => p.Id == "po2");

        Assert.Equal("PO2512-01", savedPo1.PoNumber);
        Assert.Equal("PO2512-02", savedPo2.PoNumber);
    }

    [Fact]
    public void Save_DoesNotOverwriteExistingPoNumber()
    {
        var testDate = new DateTime(2025, 12, 1);
        var po = new PurchaseOrder { Id = "po3", Date = testDate, PoNumber = "CUSTOM-PO" };

        _repo.Save(po);

        var storedItems = _repo.GetAll();
        Assert.Single(storedItems);
        Assert.Equal("CUSTOM-PO", storedItems.First().PoNumber);
    }

    [Fact]
    public void Constructor_WithNullDataPath_UsesDefaultPath()
    {
        var mockConfig = new Mock<IConfiguration>();
        mockConfig.Setup(c => c["DataPath"]).Returns((string?)null);

        var repo = new PurchaseOrderRepository(_writer, mockConfig.Object);

        Assert.NotNull(repo);
    }

    [Fact]
    public void GetFilePath_SavesPurchaseOrderWithExpectedFilename()
    {
        var po = new PurchaseOrder { Id = "po-path-test", Date = new DateTime(2026, 1, 15) };

        _repo.Save(po);
        _writer.FlushAll();

        var expectedPath = Path.Combine(_testDataDir, "Purchases", "po-path-test.json");
        Assert.True(File.Exists(expectedPath));
    }

    [Fact]
    public void GetById_ReturnsCorrectPurchaseOrder()
    {
        var po = new PurchaseOrder
        {
            Id = "po-find",
            PoNumber = "PO-001",
            Supplier = new ClientInfo { BusinessName = "Supplier A" }
        };
        _repo.Save(po);

        var found = _repo.GetById("po-find");

        Assert.NotNull(found);
        Assert.Equal("Supplier A", found.Supplier.BusinessName);
    }

    [Fact]
    public void Delete_RemovesPurchaseOrderFromCacheAndDisk()
    {
        var po = new PurchaseOrder { Id = "po-delete", PoNumber = "PO-DEL" };
        _repo.Save(po);
        _writer.FlushAll();

        var expectedPath = Path.Combine(_testDataDir, "Purchases", "po-delete.json");
        Assert.True(File.Exists(expectedPath));

        _repo.Delete("po-delete");
        _writer.FlushAll();

        Assert.Null(_repo.GetById("po-delete"));
        Assert.False(File.Exists(expectedPath));
    }

    [Fact]
    public void GeneratePoNumber_HandlesDifferentDatesAndSequences()
    {
        var dateDec = new DateTime(2025, 12, 10);
        var dateJan = new DateTime(2026, 1, 5);

        var poDec = new PurchaseOrder { Id = "poDec", Date = dateDec };
        var poJan = new PurchaseOrder { Id = "poJan", Date = dateJan };

        _repo.Save(poDec);
        _repo.Save(poJan);

        Assert.Equal("PO2512-01", _repo.GetById("poDec")?.PoNumber);
        Assert.Equal("PO2601-01", _repo.GetById("poJan")?.PoNumber);
    }
}
