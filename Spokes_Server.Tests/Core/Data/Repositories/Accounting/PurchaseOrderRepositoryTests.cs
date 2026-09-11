using Spokes_Server.Core.Data;
using Spokes_Server.Core.Data.Repositories.Accounting;
using Spokes_Server.Core.Models.Accounting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using System;
using System.Linq;
using Xunit;

namespace Spokes_Server.Tests.Core.Data.Repositories.Accounting
{
    public class PurchaseOrderRepositoryTests : IDisposable
    {
        private readonly string _testDataDir;
        private readonly DiskPersistenceService _writer;
        private readonly PurchaseOrderRepository _repo;

        public PurchaseOrderRepositoryTests()
        {
            _testDataDir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "Spokes_Test_Purchases_" + Guid.NewGuid().ToString());

            var mockConfig = new Mock<IConfiguration>();
            mockConfig.Setup(c => c["DataPath"]).Returns(_testDataDir);

            var mockPersistenceLogger = new Mock<ILogger<DiskPersistenceService>>();
            _writer = new DiskPersistenceService(mockPersistenceLogger.Object);

            _repo = new PurchaseOrderRepository(_writer, mockConfig.Object);
        }

        public void Dispose()
        {
            if (System.IO.Directory.Exists(_testDataDir))
            {
                try { System.IO.Directory.Delete(_testDataDir, true); } catch (Exception ex) { Console.WriteLine($"Cleanup failed: {ex.Message}"); }
            }
            _writer.Dispose();
        }

        [Fact]
        public void Save_GeneratesSequentialPoNumberIfMissing()
        {
            var testDate = new DateTime(2025, 12, 1);

            var po1 = new PurchaseOrder { Id = "po1", Date = testDate, PoNumber = null };
            var po2 = new PurchaseOrder { Id = "po2", Date = testDate, PoNumber = "" };

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
    }
}
