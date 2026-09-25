using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using Spokes_Server.Core.Data;
using Spokes_Server.Core.Data.Repositories.Accounting;
using Spokes_Server.Core.Models.Accounting;
using Xunit;

namespace Spokes_Server.Tests.Core.Data.Repositories.Accounting;

public class ExpenseReportRepositoryTests : IDisposable
{
    private readonly string _testDataDir;
    private readonly DiskPersistenceService _writer;
    private readonly SequenceService _sequenceService;
    private readonly ExpenseReportRepository _repo;

    public ExpenseReportRepositoryTests()
    {
        _testDataDir = Path.Combine(Path.GetTempPath(), $"Spokes_Test_Expenses_{Guid.NewGuid()}");

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
            File.WriteAllText(Path.Combine(expectedDir, "exp3.json"), JsonSerializer.Serialize(item));

            _repo.LoadFromDisk();

            var storedItems = _repo.GetAll();
            Assert.Contains(storedItems, i => i.Id == "exp3" && i.ReportNumber == "EXP-999");
        }

        [Fact]
        public void Constructor_WithNullDataPath_UsesDefaultDataFolder()
        {
            var mockConfig = new Mock<IConfiguration>();
            mockConfig.Setup(c => c["DataPath"]).Returns((string?)null);

            var mockPersistenceLogger = new Mock<ILogger<DiskPersistenceService>>();
            using var writer = new DiskPersistenceService(mockPersistenceLogger.Object);
            var sequenceService = new SequenceService(mockConfig.Object);

            var repo = new ExpenseReportRepository(writer, mockConfig.Object, sequenceService);

            Assert.NotNull(repo);
        }

        [Fact]
        public void GetFilePath_WritesDirectJsonFileInExpensesFolder()
        {
            var item = new ExpenseReport { Id = "exp-100" };

            _repo.Save(item);
            _writer.FlushAll();

            var expectedPath = Path.Combine(_testDataDir, "Expenses", "exp-100.json");
            Assert.True(File.Exists(expectedPath));
        }

        [Fact]
        public void Save_WhenReportNumberIsEmptyString_GeneratesNewNumber()
        {
            var item = new ExpenseReport { Id = "exp-empty", ReportNumber = string.Empty };

            _repo.Save(item);

            var storedItems = _repo.GetAll();
            var savedItem = storedItems.FirstOrDefault(i => i.Id == "exp-empty");
            Assert.NotNull(savedItem);
            Assert.False(string.IsNullOrEmpty(savedItem.ReportNumber));
            Assert.StartsWith("EXP", savedItem.ReportNumber);
        }

        [Fact]
        public void Delete_RemovesFromCacheAndDisk()
        {
            var item = new ExpenseReport { Id = "exp-del" };

            _repo.Save(item);
            _writer.FlushAll();
            var expectedPath = Path.Combine(_testDataDir, "Expenses", "exp-del.json");
            Assert.True(File.Exists(expectedPath));
            Assert.Contains(_repo.GetAll(), r => r.Id == "exp-del");

            _repo.Delete("exp-del");
            _writer.FlushAll();

            Assert.DoesNotContain(_repo.GetAll(), r => r.Id == "exp-del");
            Assert.False(File.Exists(expectedPath));
        }
    }

