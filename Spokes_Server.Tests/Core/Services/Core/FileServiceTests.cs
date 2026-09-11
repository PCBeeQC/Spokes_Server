using Spokes_Server.Core.Services.Communication;
using Spokes_Server.Core.Services.Projects;
using Spokes_Server.Core.Services.Core;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using Spokes_Server.Core.Services;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;

namespace Spokes_Server.Tests.Core.Services.Core
{
    public class FileServiceTests : IDisposable
    {
        private readonly string _testDataDir;
        private readonly IConfiguration _config;
        private readonly Mock<ILogger<FileService>> _mockLogger;
        private readonly FileService _service;

        public FileServiceTests()
        {
            _testDataDir = Path.Combine(Path.GetTempPath(), "Spokes_Test_FileService_" + Guid.NewGuid().ToString());
            Directory.CreateDirectory(_testDataDir);

            var configDict = new Dictionary<string, string> { { "DataPath", _testDataDir } };
            _config = new ConfigurationBuilder().AddInMemoryCollection(configDict).Build();

            _mockLogger = new Mock<ILogger<FileService>>();
            _service = new FileService(_config, _mockLogger.Object, new Mock<ICryptoService>().Object);
        }

        public void Dispose()
        {
            if (Directory.Exists(_testDataDir))
            {
                try { Directory.Delete(_testDataDir, true); } catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"Cleanup error: {ex.Message}"); }
            }
        }

        [Fact]
        public async Task UploadStreamAsync_CreatesFile_AndReturnsUrl()
        {
            var category = "test-cat";
            var contextId = "ctx-1";
            var fileName = "test.txt";
            var content = "Hello File System!";
            using var stream = new MemoryStream(Encoding.UTF8.GetBytes(content));

            var url = await _service.UploadStreamAsync(category, contextId, stream, fileName);

            Assert.StartsWith($"/spokesapi/files/{category}/{contextId}/", url);

            // Verify physical file exists
            var parts = url.Split('/');
            var safeName = parts[^1];
            var physicalPath = Path.Combine(_testDataDir, "Uploads", category, contextId, safeName);

            Assert.True(File.Exists(physicalPath));
            Assert.Equal(content, File.ReadAllText(physicalPath));
        }

        [Fact]
        public async Task UploadAsync_CalculatesSafeName_AndSavesFile()
        {
            var mockFile = new Mock<IBrowserFile>();
            mockFile.Setup(f => f.Name).Returns("my image.png");
            mockFile.Setup(f => f.OpenReadStream(It.IsAny<long>(), default))
                    .Returns(new MemoryStream(Encoding.UTF8.GetBytes("fake-image-bytes")));

            var url = await _service.UploadAsync("images", "user-123", mockFile.Object);

            Assert.EndsWith(".png", url);
            Assert.Contains("/images/user-123/", url);
        }

        [Fact]
        public async Task Delete_RemovesPhysicalFile()
        {
            var category = "to-delete";
            var contextId = "ctx-999";
            var fileName = "bye.txt";
            var folder = Path.Combine(_testDataDir, "Uploads", category, contextId);
            Directory.CreateDirectory(folder);
            var fullPath = Path.Combine(folder, fileName);
            File.WriteAllText(fullPath, "Delete me");

            var url = $"/spokesapi/files/{category}/{contextId}/{fileName}";

            await _service.DeleteAsync(url);

            Assert.False(File.Exists(fullPath));
        }

        [Fact]
        public async Task Upload_SanitizesInputs_ToPreventTraversal()
        {
            var content = "Trapped?";
            using var stream = new MemoryStream(Encoding.UTF8.GetBytes(content));

            // Attempt traversal in category
            await Assert.ThrowsAsync<ArgumentException>(() =>
                _service.UploadStreamAsync("../secrets", "ctx", stream, "test.txt"));

            // Attempt traversal in contextId
            await Assert.ThrowsAsync<ArgumentException>(() =>
                _service.UploadStreamAsync("cat", "ctx/../sub", stream, "test.txt"));
        }

        [Fact]
        public void GetPhysicalPath_SanitizesFilenames()
        {
            var path = _service.GetPhysicalPath("cat/../traversal", "ctx", "file.txt");

            // Path.GetFileName("cat/../traversal") should be "traversal"
            Assert.Contains(Path.Combine("Uploads", "traversal", "ctx", "file.txt"), path);
            Assert.DoesNotContain("..", path);
        }

        [Fact]
        public void CleanTempContext_DeletesEntireContextDirectory()
        {
            var contextId = Guid.NewGuid().ToString();
            var tempDir = Path.Combine(_testDataDir, "Uploads", "temp", contextId);
            Directory.CreateDirectory(tempDir);
            File.WriteAllText(Path.Combine(tempDir, "file.txt"), "temp content");

            Assert.True(Directory.Exists(tempDir));

            _service.CleanTempContext(contextId);

            Assert.False(Directory.Exists(tempDir));
        }

        [Fact]
        public void PurgeStaleTempFiles_DeletesOnlyStaleDirectories()
        {
            var oldCtx = Guid.NewGuid().ToString();
            var newCtx = Guid.NewGuid().ToString();
            var oldDir = Path.Combine(_testDataDir, "Uploads", "temp", oldCtx);
            var newDir = Path.Combine(_testDataDir, "Uploads", "temp", newCtx);

            Directory.CreateDirectory(oldDir);
            Directory.CreateDirectory(newDir);

            // Set oldDir LastWriteTime to 2 hours ago
            Directory.SetLastWriteTimeUtc(oldDir, DateTime.UtcNow.AddHours(-2));
            // Set newDir LastWriteTime to now
            Directory.SetLastWriteTimeUtc(newDir, DateTime.UtcNow);

            _service.PurgeStaleTempFiles(TimeSpan.FromHours(1));

            Assert.False(Directory.Exists(oldDir));
            Assert.True(Directory.Exists(newDir));
        }
    }
}

