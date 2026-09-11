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
    public class ExpenseReportRepositoryTests : IDisposable
    {
        private readonly string _testDataDir;
        private readonly DiskPersistenceService _writer;
        private readonly SequenceService _sequenceService;
        private readonly ExpenseReportRepository _repo;

        public ExpenseReportRepositoryTests()
        {
            _testDataDir = Path.Combine(Path.GetTempPath(), "Spokes_Test_Expenses_" + Guid.NewGuid().ToString());

            var mockConfig = new Mock<IConfiguration>();
            mockConfig.Setup(c => c["DataPath"]).Returns(_testDataDir);

            var mockPersistenceLogger = new Mock<ILogger<DiskPersistenceService>>();
            _writer = new DiskPersistenceService(mockPersistenceLogger.Object);

            _sequenceService = new SequenceService(mockConfig.Object);

            _repo = new ExpenseReportRepository(_writer, mockConfig.Object, _sequenceService);
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
        public void Save_GeneratesReportNumberIfMissing()
        {
            var item = new ExpenseReport { Id = "exp1", ReportNumber = null };

            _repo.Save(item);

            var storedItems = _repo.GetAll();
            Assert.Single(storedItems);
            Assert.NotNull(storedItems.First().ReportNumber);
            Assert.StartsWith("EXP", storedItems.First().ReportNumber);
        }

        [Fact]
        public void Save_DoesNotOverwriteExistingReportNumber()
        {
            var item = new ExpenseReport { Id = "exp2", ReportNumber = "CUSTOM-123" };

            _repo.Save(item);

            var storedItems = _repo.GetAll();
            Assert.Single(storedItems);
            Assert.Equal("CUSTOM-123", storedItems.First().ReportNumber);
        }

        [Fact]
        public void LoadFromDisk_ReadsFromExpectedPath()
        {
            var expectedDir = Path.Combine(_testDataDir, "Expenses");
            Directory.CreateDirectory(expectedDir);

            var item = new ExpenseReport { Id = "exp3", ReportNumber = "EXP-999" };
            File.WriteAllText(Path.Combine(expectedDir, "exp3.json"), System.Text.Json.JsonSerializer.Serialize(item));

            _repo.LoadFromDisk();

            var storedItems = _repo.GetAll();
            Assert.Contains(storedItems, i => i.Id == "exp3" && i.ReportNumber == "EXP-999");
        }
    }
}
