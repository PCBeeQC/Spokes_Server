using Spokes_Server.Core.Services.Communication;
using Spokes_Server.Core.Services.Projects;
using Spokes_Server.Core.Services.Core;
using Microsoft.Extensions.Logging;
using Moq;
using Spokes_Server.Core.Data;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Spokes_Server.Tests.Core.Data
{
    public class DiskPersistenceServiceTests : IDisposable
    {
        private readonly string _testDataDir;
        private readonly DiskPersistenceService _service;

        public DiskPersistenceServiceTests()
        {
            _testDataDir = Path.Combine(Path.GetTempPath(), "Spokes_Test_Disk_" + Guid.NewGuid().ToString());
            var mockLogger = new Mock<ILogger<DiskPersistenceService>>();
            _service = new DiskPersistenceService(mockLogger.Object);
        }

        public void Dispose()
        {
            if (Directory.Exists(_testDataDir))
            {
                try 
                { 
                    Directory.Delete(_testDataDir, true); 
                } 
                catch (Exception ex) 
                { 
                    System.Diagnostics.Debug.WriteLine($"Failed to delete test directory: {ex.Message}"); 
                }
            }
            _service.Dispose();
        }

        [Fact]
        public async Task QueueWrite_WritesFileToDisk()
        {
            var filePath = Path.Combine(_testDataDir, "test.json");
            var data = new { Name = "Test Data", Value = 42 };

            _service.QueueWrite(filePath, data);

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await _service.StartAsync(cts.Token);

            // Wait for queue processing
            await Task.Delay(500);
            await _service.StopAsync(CancellationToken.None);

            Assert.True(File.Exists(filePath));
            var content = await File.ReadAllTextAsync(filePath);
            Assert.Contains("Test Data", content);
            Assert.Contains("42", content);
        }

        [Fact]
        public async Task QueueDelete_DeletesFileFromDisk()
        {
            Directory.CreateDirectory(_testDataDir);
            var filePath = Path.Combine(_testDataDir, "test2.json");
            await File.WriteAllTextAsync(filePath, "dummy content");

            _service.QueueDelete(filePath);

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await _service.StartAsync(cts.Token);

            // Wait for queue processing
            await Task.Delay(500);
            await _service.StopAsync(CancellationToken.None);

            Assert.False(File.Exists(filePath));
        }

        [Fact]
        public async Task QueueWrite_OverwritesExistingFile()
        {
            Directory.CreateDirectory(_testDataDir);
            var filePath = Path.Combine(_testDataDir, "test3.json");
            await File.WriteAllTextAsync(filePath, "old content");

            var data = new { Name = "New Data" };
            _service.QueueWrite(filePath, data);

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await _service.StartAsync(cts.Token);

            await Task.Delay(500);
            await _service.StopAsync(CancellationToken.None);

            Assert.True(File.Exists(filePath));
            var content = await File.ReadAllTextAsync(filePath);
            Assert.Contains("New Data", content);
            Assert.DoesNotContain("old content", content);
        }
    }
}


