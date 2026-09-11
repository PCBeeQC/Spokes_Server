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
    public class RecurringExpenseRepositoryTests : IDisposable
    {
        private readonly string _testDataDir;
        private readonly DiskPersistenceService _writer;
        private readonly RecurringExpenseRepository _repo;

        public RecurringExpenseRepositoryTests()
        {
            _testDataDir = Path.Combine(Path.GetTempPath(), "Spokes_Test_Recurring_" + Guid.NewGuid().ToString());

            var mockConfig = new Mock<IConfiguration>();
            mockConfig.Setup(c => c["DataPath"]).Returns(_testDataDir);

            var mockPersistenceLogger = new Mock<ILogger<DiskPersistenceService>>();
            _writer = new DiskPersistenceService(mockPersistenceLogger.Object);

            _repo = new RecurringExpenseRepository(_writer, mockConfig.Object);
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
        public void Save_AddsToCache()
        {
            var item = new RecurringExpense { Id = "re1", Name = "Office Rent", Amount = 1500 };

            _repo.Save(item);

            var storedItems = _repo.GetAll();
            Assert.Single(storedItems);
            Assert.Equal("re1", storedItems.First().Id);
            Assert.Equal("Office Rent", storedItems.First().Name);
        }

        [Fact]
        public void LoadFromDisk_ReadsFromExpectedPath()
        {
            var expectedDir = Path.Combine(_testDataDir, "RecurringExpenses", "re2");
            Directory.CreateDirectory(expectedDir);

            var item = new RecurringExpense { Id = "re2", Name = "Internet" };
            File.WriteAllText(Path.Combine(expectedDir, "expense.json"), System.Text.Json.JsonSerializer.Serialize(item));

            _repo.LoadFromDisk();

            var storedItems = _repo.GetAll();
            Assert.Contains(storedItems, i => i.Id == "re2" && i.Name == "Internet");
        }

        [Fact]
        public void Delete_RemovesFromCache()
        {
            var item = new RecurringExpense { Id = "re3", Name = "Cleaning" };

            _repo.Save(item);
            Assert.Single(_repo.GetAll());

            _repo.Delete("re3");
            Assert.Empty(_repo.GetAll());
        }
    }
}
