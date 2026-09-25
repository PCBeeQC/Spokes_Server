using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using Spokes_Server.Core.Data;
using Spokes_Server.Core.Data.Repositories.Accounting;
using Spokes_Server.Core.Models.Accounting;
using Xunit;

namespace Spokes_Server.Tests.Core.Data.Repositories.Accounting;

public class CommissionLedgerRecordRepositoryTests : IDisposable
{
    private readonly string _testDataDir;
    private readonly DiskPersistenceService _writer;
    private readonly Mock<IConfiguration> _mockConfig;
    private readonly CommissionLedgerRecordRepository _repo;

    public CommissionLedgerRecordRepositoryTests()
    {
        _testDataDir = Path.Combine(Path.GetTempPath(), $"Spokes_Test_CommissionLedger_{Guid.NewGuid()}");

            _mockConfig = new Mock<IConfiguration>();
            _mockConfig.Setup(c => c["DataPath"]).Returns(_testDataDir);

            var mockLogger = new Mock<ILogger<DiskPersistenceService>>();
            _writer = new DiskPersistenceService(mockLogger.Object);

            _repo = new CommissionLedgerRecordRepository(_writer, _mockConfig.Object);
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(_testDataDir))
                {
                    Directory.Delete(_testDataDir, true);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Cleanup failed: {ex.Message}");
            }
            _writer.Dispose();
        }

        [Fact]
        public void Constructor_WithNullDataPath_InitializesCorrectly()
        {
            var config = new Mock<IConfiguration>();
            config.Setup(c => c["DataPath"]).Returns((string?)null);

            var repo = new CommissionLedgerRecordRepository(_writer, config.Object);

            Assert.NotNull(repo);
            Assert.Empty(repo.GetAll());
        }

        [Fact]
        public void GetFilePath_UsesRecordIdAsJsonFilename()
        {
            var record = new CommissionLedgerRecord
            {
                Id = "clr-1",
                ProjectId = "p1",
                BeneficiaryId = "b1",
                BaseAmount = 1000m,
                CommissionAmount = 100m
            };

            _repo.Save(record);
            _writer.FlushAll();

            var expectedFile = Path.Combine(_testDataDir, "CommissionLedgerRecords", "clr-1.json");
            Assert.True(File.Exists(expectedFile));
        }

        [Fact]
        public void GetByProject_ReturnsMatchingRecords_OrderedByDateGeneratedDescending()
        {
            var now = DateTime.UtcNow;
            var r1 = new CommissionLedgerRecord { Id = "clr-1", ProjectId = "p1", DateGenerated = now.AddHours(-2) };
            var r2 = new CommissionLedgerRecord { Id = "clr-2", ProjectId = "p1", DateGenerated = now };
            var r3 = new CommissionLedgerRecord { Id = "clr-3", ProjectId = "p2", DateGenerated = now.AddHours(-1) };

            _repo.Save(r1);
            _repo.Save(r2);
            _repo.Save(r3);

            var result = _repo.GetByProject("p1");

            Assert.Equal(2, result.Count);
            Assert.Equal("clr-2", result[0].Id);
            Assert.Equal("clr-1", result[1].Id);
        }

        [Fact]
        public void GetByProject_WhenNoMatches_ReturnsEmptyList()
        {
            var r1 = new CommissionLedgerRecord { Id = "clr-1", ProjectId = "p1" };
            _repo.Save(r1);

            var result = _repo.GetByProject("unknown-project");

            Assert.NotNull(result);
            Assert.Empty(result);
        }

        [Fact]
        public void GetByBeneficiary_ReturnsMatchingRecords_OrderedByDateGeneratedDescending()
        {
            var now = DateTime.UtcNow;
            var r1 = new CommissionLedgerRecord { Id = "clr-1", BeneficiaryId = "b1", DateGenerated = now.AddDays(-1) };
            var r2 = new CommissionLedgerRecord { Id = "clr-2", BeneficiaryId = "b1", DateGenerated = now };
            var r3 = new CommissionLedgerRecord { Id = "clr-3", BeneficiaryId = "b2", DateGenerated = now.AddHours(-1) };

            _repo.Save(r1);
            _repo.Save(r2);
            _repo.Save(r3);

            var result = _repo.GetByBeneficiary("b1");

            Assert.Equal(2, result.Count);
            Assert.Equal("clr-2", result[0].Id);
            Assert.Equal("clr-1", result[1].Id);
        }

        [Fact]
        public void GetByBeneficiary_WhenNoMatches_ReturnsEmptyList()
        {
            var r1 = new CommissionLedgerRecord { Id = "clr-1", BeneficiaryId = "b1" };
            _repo.Save(r1);

            var result = _repo.GetByBeneficiary("unknown-beneficiary");

            Assert.NotNull(result);
            Assert.Empty(result);
        }

        [Fact]
        public void SaveAndDelete_RoundTripsCorrectly()
        {
            var record = new CommissionLedgerRecord
            {
                Id = "clr-rt",
                ProjectId = "p-rt",
                BeneficiaryId = "b-rt",
                BaseAmount = 500m,
                CommissionAmount = 50m
            };

            _repo.Save(record);
            var allAfterSave = _repo.GetAll();
            Assert.Single(allAfterSave);
            Assert.Equal("clr-rt", allAfterSave.First().Id);

            _repo.Delete("clr-rt");
            var allAfterDelete = _repo.GetAll();
            Assert.Empty(allAfterDelete);
        }
    }

