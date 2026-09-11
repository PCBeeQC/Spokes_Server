using Spokes_Server.Core.Data;
using Spokes_Server.Core.Data.Repositories.Accounting;
using Spokes_Server.Core.Models.Accounting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using System;
using System.IO;
using System.Linq;
using Xunit;

namespace Spokes_Server.Tests.Core.Data.Repositories.Accounting
{
    public class BillRepositoryTests : IDisposable
    {
        private readonly string _testDataDir;
        private readonly DiskPersistenceService _writer;
        private readonly BillRepository _repo;

        public BillRepositoryTests()
        {
            _testDataDir = Path.Combine(Path.GetTempPath(), "Spokes_Test_Bills_" + Guid.NewGuid().ToString());

            var mockConfig = new Mock<IConfiguration>();
            mockConfig.Setup(c => c["DataPath"]).Returns(_testDataDir);

            var mockPersistenceLogger = new Mock<ILogger<DiskPersistenceService>>();
            _writer = new DiskPersistenceService(mockPersistenceLogger.Object);

            _repo = new BillRepository(_writer, mockConfig.Object);
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
        public void GetByPo_ReturnsMatchingBills()
        {
            var b1 = new Bill { Id = "b1", PurchaseOrderId = "po1" };
            var b2 = new Bill { Id = "b2", PurchaseOrderId = "po2" };
            var b3 = new Bill { Id = "b3", PurchaseOrderId = "po1" };

            _repo.Save(b1);
            _repo.Save(b2);
            _repo.Save(b3);

            var result = _repo.GetByPo("po1");

            Assert.Equal(2, result.Count);
            Assert.Contains(result, b => b.Id == "b1");
            Assert.Contains(result, b => b.Id == "b3");
            Assert.DoesNotContain(result, b => b.Id == "b2");
        }

        [Fact]
        public void GetUnpaid_ReturnsOnlyUnpaidBills()
        {
            var b1 = new Bill { Id = "b1", Status = "Draft" };
            var b2 = new Bill { Id = "b2", Status = "Paid" };
            var b3 = new Bill { Id = "b3", Status = "Void" };
            var b4 = new Bill { Id = "b4", Status = "Pending" };

            _repo.Save(b1);
            _repo.Save(b2);
            _repo.Save(b3);
            _repo.Save(b4);

            var result = _repo.GetUnpaid();

            Assert.Equal(2, result.Count);
            Assert.Contains(result, b => b.Id == "b1");
            Assert.Contains(result, b => b.Id == "b4");
            Assert.DoesNotContain(result, b => b.Id == "b2");
            Assert.DoesNotContain(result, b => b.Id == "b3");
        }
    }
}
