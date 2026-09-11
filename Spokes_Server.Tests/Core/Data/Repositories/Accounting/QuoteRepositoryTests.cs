using Spokes_Server.Core.Services.Communication;
using Spokes_Server.Core.Services.Projects;
using Spokes_Server.Core.Services.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using Spokes_Server.Core.Data;
using Spokes_Server.Core.Data.Repositories.Core;
using Spokes_Server.Core.Data.Repositories.Projects;
using Spokes_Server.Core.Data.Repositories.Accounting;
using Spokes_Server.Core.Data.Repositories.Communication;
using Spokes_Server.Core.Data.Repositories.HR;
using Spokes_Server.Core.Models.Core;
using Spokes_Server.Core.Models.Projects;
using Spokes_Server.Core.Models.Accounting;
using Spokes_Server.Core.Models.Communication;
using Spokes_Server.Core.Models.HR;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Spokes_Server.Tests.Core.Data.Repositories.Accounting
{
    public class QuoteRepositoryTests : IDisposable
    {
        private readonly string _testDataDir;
        private readonly DiskPersistenceService _writer;
        private readonly SequenceService _sequenceService;
        private readonly QuoteRepository _repo;

        public QuoteRepositoryTests()
        {
            _testDataDir = Path.Combine(Path.GetTempPath(), "Spokes_Test_Quotes_" + Guid.NewGuid().ToString());

            var mockConfig = new Mock<IConfiguration>();
            mockConfig.Setup(c => c["DataPath"]).Returns(_testDataDir);

            var mockPersistenceLogger = new Mock<ILogger<DiskPersistenceService>>();
            _writer = new DiskPersistenceService(mockPersistenceLogger.Object);

            _sequenceService = new SequenceService(mockConfig.Object);

            var mockRepoLogger = new Mock<ILogger<QuoteRepository>>();
            _repo = new QuoteRepository(_writer, mockConfig.Object, _sequenceService, mockRepoLogger.Object);
        }

        public void Dispose()
        {
            if (Directory.Exists(_testDataDir))
            {
                try { Directory.Delete(_testDataDir, true); } catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"Cleanup error: {ex.Message}"); }
            }
            _writer.Dispose();
        }

        [Fact]
        public void Save_AddsToCache()
        {
            var quote = new Quote { Id = "q1", ProjectId = "p1", QuoteNumber = "Q-1" };
            _repo.Save(quote);

            var quotes = _repo.GetByProject("p1");
            Assert.Single(quotes);
            Assert.Equal("Q-1", quotes.First().QuoteNumber);
        }

        [Fact]
        public void GetByProject_ReturnsOrderedByDateDescending()
        {
            var q1 = new Quote { Id = "q1", ProjectId = "p2", Date = DateTime.UtcNow.AddDays(-2) };
            var q2 = new Quote { Id = "q2", ProjectId = "p2", Date = DateTime.UtcNow };
            var q3 = new Quote { Id = "q3", ProjectId = "p2", Date = DateTime.UtcNow.AddDays(-1) };

            _repo.Save(q1);
            _repo.Save(q2);
            _repo.Save(q3);

            var quotes = _repo.GetByProject("p2");
            Assert.Equal(3, quotes.Count);
            Assert.Equal("q2", quotes[0].Id);
            Assert.Equal("q3", quotes[1].Id);
            Assert.Equal("q1", quotes[2].Id);
        }

        [Fact]
        public void GenerateQuoteNumber_DelegatesToSequenceService()
        {
            var num1 = _repo.GenerateQuoteNumber("TEST");
            var num2 = _repo.GenerateQuoteNumber("TEST");

            Assert.StartsWith("TEST", num1);
            Assert.StartsWith("TEST", num2);
            Assert.NotEqual(num1, num2); // Sequence increments
        }

        [Fact]
        public void LoadFromDisk_ScansProjectQuoteFolders()
        {
            var p1QuotesDir = Path.Combine(_testDataDir, "Projects", "p1", "Quotes");
            var p2QuotesDir = Path.Combine(_testDataDir, "Projects", "p2", "Quotes");
            Directory.CreateDirectory(p1QuotesDir);
            Directory.CreateDirectory(p2QuotesDir);

            var q1 = new Quote { Id = "q1", ProjectId = "p1" };
            var q2 = new Quote { Id = "q2", ProjectId = "p2" };

            File.WriteAllText(Path.Combine(p1QuotesDir, "q1.json"), JsonSerializer.Serialize(q1));
            File.WriteAllText(Path.Combine(p2QuotesDir, "q2.json"), JsonSerializer.Serialize(q2));

            // Add a corrupted file
            File.WriteAllText(Path.Combine(p1QuotesDir, "corrupted.json"), "invalid json");

            _repo.LoadFromDisk();

            Assert.Single(_repo.GetByProject("p1"));
            Assert.Single(_repo.GetByProject("p2"));
            Assert.Equal("q1", _repo.GetByProject("p1").First().Id);
        }
    }
}




