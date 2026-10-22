using Spokes_Server.Core.Data.Repositories.Accounting;
using Spokes_Server.Core.Models.Accounting;
using Spokes_Server.Core.Models.Core;
using Spokes_Server.Tests.Helpers;
using Xunit;

namespace Spokes_Server.Tests.Core.Data.Repositories.Accounting;

public class PurchaseOrderRepositoryTests : RepositoryTestFixture<PurchaseOrderRepository>
{
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

    [Fact]
    public void Save_WithExplicitSequenceService_PersistsAndIncrementsSequence()
    {
        var seqService = new Spokes_Server.Core.Data.SequenceService(_config);
        var repo = new PurchaseOrderRepository(_writer, _config, seqService);

        var date = new DateTime(2026, 4, 1);
        var po1 = new PurchaseOrder { Id = "po-seq-1", Date = date };
        var po2 = new PurchaseOrder { Id = "po-seq-2", Date = date };

        repo.Save(po1);
        repo.Save(po2);

        Assert.Equal("PO2604-01", po1.PoNumber);
        Assert.Equal("PO2604-02", po2.PoNumber);
        Assert.True(File.Exists(seqService.FilePath));
    }
}
