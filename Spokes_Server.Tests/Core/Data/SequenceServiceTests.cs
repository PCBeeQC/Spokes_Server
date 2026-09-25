using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Moq;
using Spokes_Server.Core.Data;

namespace Spokes_Server.Tests.Core.Data
{
    public class SequenceServiceTests : IDisposable
    {
        private readonly string _testDataDir;

        public SequenceServiceTests()
        {
            _testDataDir = Path.Combine(Path.GetTempPath(), $"Spokes_Test_Sequences_{Guid.NewGuid()}");
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

        [Fact]
        public async Task GetNextSequence_ThreadSafety_GeneratesUniqueSequentialNumbersUnderConcurrency()
        {
            var service = CreateService();
            const int count = 50;
            var results = new ConcurrentBag<int>();

            var tasks = Enumerable.Range(0, count).Select(_ => Task.Run(() =>
            {
                results.Add(service.GetNextSequence("ConcurrentEntity"));
            }));

            await Task.WhenAll(tasks);

            Assert.Equal(count, results.Count);
            var sorted = results.OrderBy(x => x).ToList();
            Assert.Equal(Enumerable.Range(1, count), sorted);
        }

        [Fact]
        public void GenerateNumber_PaddingVerification()
        {
            var filepath = Path.Combine(_testDataDir, "sequences.json");
            var initialData = JsonSerializer.Serialize(new Dictionary<string, int>
            {
                { "Pad1", 0 },
                { "Pad2", 98 },
                { "Pad3", 998 },
                { "Pad4", 9998 }
            });
            File.WriteAllText(filepath, initialData);

            var service = CreateService();
            var datePart = DateTime.Today.ToString("yyMM");

            Assert.Equal($"TEST-{datePart}-0001", service.GenerateNumber("Pad1", "TEST"));
            Assert.Equal($"TEST-{datePart}-0099", service.GenerateNumber("Pad2", "TEST"));
            Assert.Equal($"TEST-{datePart}-0999", service.GenerateNumber("Pad3", "TEST"));
            Assert.Equal($"TEST-{datePart}-9999", service.GenerateNumber("Pad4", "TEST"));
        }

        [Fact]
        public void GenerateNumber_PreservesDistinctSequencesPerEntityType()
        {
            var service = CreateService();
            var datePart = DateTime.Today.ToString("yyMM");

            var inv1 = service.GenerateNumber("INV", "INV");
            var inv2 = service.GenerateNumber("INV", "INV");
            var po1 = service.GenerateNumber("PO", "PO");
            var inv3 = service.GenerateNumber("INV", "INV");
            var po2 = service.GenerateNumber("PO", "PO");

            Assert.Equal($"INV-{datePart}-0001", inv1);
            Assert.Equal($"INV-{datePart}-0002", inv2);
            Assert.Equal($"PO-{datePart}-0001", po1);
            Assert.Equal($"INV-{datePart}-0003", inv3);
            Assert.Equal($"PO-{datePart}-0002", po2);
        }

        [Fact]
        public void DefaultDataPath_Fallback_WhenConfigEmpty()
        {
            var fallbackPath = Path.Combine("Data", "sequences.json");
            try
            {
                var mockConfig = new Mock<IConfiguration>();
                mockConfig.Setup(c => c["DataPath"]).Returns((string?)null);

                var service = new SequenceService(mockConfig.Object);

                var field = typeof(SequenceService).GetField("_filePath", BindingFlags.NonPublic | BindingFlags.Instance);
                Assert.Equal(fallbackPath, field?.GetValue(service));
                Assert.Equal(1, service.GetNextSequence("FallbackEntity"));
            }
            finally
            {
                if (File.Exists(fallbackPath))
                {
                    try { File.Delete(fallbackPath); } catch { }
                }
            }
        }

        [Fact]
        public void Save_HandlesWriteErrorGracefully()
        {
            var mockConfig = new Mock<IConfiguration>();
            mockConfig.Setup(c => c["DataPath"]).Returns(Path.Combine(_testDataDir, "non_existent_subfolder", "nested"));
            var service = new SequenceService(mockConfig.Object);

            // Should not throw even if directory doesn't exist for Save()
            var seq = service.GetNextSequence("ErrorTest");
            Assert.Equal(1, seq);
        }
    }
}
