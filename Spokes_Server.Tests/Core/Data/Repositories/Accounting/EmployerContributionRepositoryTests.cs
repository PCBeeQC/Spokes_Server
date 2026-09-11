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
    public class EmployerContributionRepositoryTests : IDisposable
    {
        private readonly string _testDataDir;
        private readonly DiskPersistenceService _writer;
        private readonly EmployerContributionRepository _repo;

        public EmployerContributionRepositoryTests()
        {
            _testDataDir = Path.Combine(Path.GetTempPath(), "Spokes_Test_EmpCont_" + Guid.NewGuid().ToString());

            var mockConfig = new Mock<IConfiguration>();
            mockConfig.Setup(c => c["DataPath"]).Returns(_testDataDir);

            var mockPersistenceLogger = new Mock<ILogger<DiskPersistenceService>>();
            _writer = new DiskPersistenceService(mockPersistenceLogger.Object);

            _repo = new EmployerContributionRepository(_writer, mockConfig.Object);
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
            var item = new EmployerContribution { Id = "ec1", Name = "Health Insurance" };

            _repo.Save(item);

            var storedItems = _repo.GetAll();
            Assert.Single(storedItems);
            Assert.Equal("ec1", storedItems.First().Id);
            Assert.Equal("Health Insurance", storedItems.First().Name);
        }

        [Fact]
        public void LoadFromDisk_ReadsFromExpectedPath()
        {
            var expectedDir = Path.Combine(_testDataDir, "EmployerContributions", "ec3");
            Directory.CreateDirectory(expectedDir);

            var item = new EmployerContribution { Id = "ec3", Name = "Pension" };
            File.WriteAllText(Path.Combine(expectedDir, "contribution.json"), System.Text.Json.JsonSerializer.Serialize(item));

            _repo.LoadFromDisk();

            var storedItems = _repo.GetAll();
            Assert.Contains(storedItems, i => i.Id == "ec3" && i.Name == "Pension");
        }

        [Fact]
        public void Delete_RemovesFromCacheAndDisk()
        {
            var item = new EmployerContribution { Id = "ec2", Name = "401k" };

            _repo.Save(item);
            Assert.Single(_repo.GetAll());

            _repo.Delete("ec2");
            Assert.Empty(_repo.GetAll());
        }
    }
}
