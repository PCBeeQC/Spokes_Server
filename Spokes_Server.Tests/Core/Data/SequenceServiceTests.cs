using Spokes_Server.Core.Services.Communication;
using Spokes_Server.Core.Services.Projects;
using Spokes_Server.Core.Services.Core;
using Microsoft.Extensions.Configuration;
using Moq;
using Spokes_Server.Core.Data;
using System.IO;
using System.Text.Json;

namespace Spokes_Server.Tests.Core.Data
{
    public class SequenceServiceTests : IDisposable
    {
        private readonly string _testDataDir;

        public SequenceServiceTests()
        {
            _testDataDir = Path.Combine(Path.GetTempPath(), "Spokes_Test_Sequences_" + Guid.NewGuid().ToString());
            Directory.CreateDirectory(_testDataDir);
        }

        public void Dispose()
        {
            if (Directory.Exists(_testDataDir))
            {
                try { Directory.Delete(_testDataDir, true); } catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"Cleanup error: {ex.Message}"); }
            }
        }

        private SequenceService CreateService()
        {
            var mockConfig = new Mock<IConfiguration>();
            mockConfig.Setup(c => c["DataPath"]).Returns(_testDataDir);
            return new SequenceService(mockConfig.Object);
        }

        [Fact]
        public void GetNextSequence_IncrementsFromZero()
        {
            var service = CreateService();

            Assert.Equal(1, service.GetNextSequence("TestEntity"));
            Assert.Equal(2, service.GetNextSequence("TestEntity"));
            Assert.Equal(1, service.GetNextSequence("OtherEntity"));
        }

        [Fact]
        public void GenerateNumber_FormatsCorrectly()
        {
            var service = CreateService();
            var datePart = DateTime.Today.ToString("yyMM");

            var num1 = service.GenerateNumber("F", "F");
            Assert.Equal($"F-{datePart}-0001", num1);

            var num2 = service.GenerateNumber("F", "F");
            Assert.Equal($"F-{datePart}-0002", num2);
        }

        [Fact]
        public void Load_ReadsExistingSequencesFromDisk()
        {
            // Setup initial file
            var filepath = Path.Combine(_testDataDir, "sequences.json");
            var initialData = "{ \"ExistingEntity\": 5 }";
            File.WriteAllText(filepath, initialData);

            // Create service (should load from file in constructor)
            var service = CreateService();

            // Next sequence should be 6
            Assert.Equal(6, service.GetNextSequence("ExistingEntity"));
        }

        [Fact]
        public void Load_HandlesInvalidJsonGracefully()
        {
            var filepath = Path.Combine(_testDataDir, "sequences.json");
            File.WriteAllText(filepath, "invalid json");

            // Should not throw, should start from 0
            var service = CreateService();
            Assert.Equal(1, service.GetNextSequence("AnyEntity"));
        }

        [Fact]
        public void Save_WritesToDiskCorrectly()
        {
            var service = CreateService();
            service.GetNextSequence("SaveTest");

            var filepath = Path.Combine(_testDataDir, "sequences.json");
            Assert.True(File.Exists(filepath));

            var json = File.ReadAllText(filepath);
            Assert.Contains("\"SaveTest\": 1", json);

            service.GetNextSequence("SaveTest");

            // Re-read file
            json = File.ReadAllText(filepath);
            Assert.Contains("\"SaveTest\": 2", json);
        }
    }
}


