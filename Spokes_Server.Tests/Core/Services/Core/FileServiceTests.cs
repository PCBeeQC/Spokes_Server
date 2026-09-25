using System.Text;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using Spokes_Server.Core.Services.Core;
using Spokes_Server.Core.Services.Security;

namespace Spokes_Server.Tests.Core.Services.Core;

public class FileServiceTests : IDisposable
{
    private readonly string _testDataDir;
    private readonly IConfiguration _config;
    private readonly Mock<ILogger<FileService>> _mockLogger;
    private readonly Mock<ICryptoService> _mockCrypto;
    private readonly FileService _service;

    public FileServiceTests()
    {
        _testDataDir = Path.Combine(Path.GetTempPath(), "Spokes_Test_FileService_" + Guid.NewGuid().ToString());
        Directory.CreateDirectory(_testDataDir);

        var configDict = new Dictionary<string, string?> { { "DataPath", _testDataDir } };
        _config = new ConfigurationBuilder().AddInMemoryCollection(configDict).Build();

            _mockLogger = new Mock<ILogger<FileService>>();
            _mockCrypto = new Mock<ICryptoService>();
            _service = new FileService(_config, _mockLogger.Object, _mockCrypto.Object);
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

        [Fact]
        public async Task UploadStreamAsync_WithEncryptionKey_EncryptsStream()
        {
            var category = "secure-cat";
            var contextId = "ctx-sec";
            var fileName = "secret.txt";
            var encryptionKey = "my-secret-key";
            using var stream = new MemoryStream(Encoding.UTF8.GetBytes("top secret"));

            var url = await _service.UploadStreamAsync(category, contextId, stream, fileName, encryptionKey: encryptionKey);

            Assert.StartsWith($"/spokesapi/files/{category}/{contextId}/", url);
            _mockCrypto.Verify(c => c.EncryptStreamAsync(It.IsAny<Stream>(), It.IsAny<Stream>(), "my-secret-key"), Times.Once);
        }

        [Fact]
        public async Task UploadStreamAsync_WithPredefinedSafeName_UsesPredefinedName()
        {
            var category = "avatars";
            var contextId = "user-42";
            var predefinedSafeName = "custom-avatar.png";
            var content = "avatar image data";
            using var stream = new MemoryStream(Encoding.UTF8.GetBytes(content));

            var url = await _service.UploadStreamAsync(category, contextId, stream, "raw_upload.png", predefinedSafeName: predefinedSafeName);

            Assert.EndsWith("/custom-avatar.png", url);

            var physicalPath = Path.Combine(_testDataDir, "Uploads", category, contextId, predefinedSafeName);
            Assert.True(File.Exists(physicalPath));
            Assert.Equal(content, File.ReadAllText(physicalPath));
        }

        [Theory]
        [InlineData(null, "ctx-1")]
        [InlineData("", "ctx-1")]
        [InlineData("   ", "ctx-1")]
        [InlineData("cat*", "ctx-1")]
        [InlineData("cat?", "ctx-1")]
        [InlineData("cat/..", "ctx-1")]
        [InlineData("valid-cat", null)]
        [InlineData("valid-cat", "")]
        [InlineData("valid-cat", "   ")]
        [InlineData("valid-cat", "ctx*")]
        [InlineData("valid-cat", "ctx?")]
        [InlineData("valid-cat", "ctx/..")]
        public async Task UploadStreamAsync_InvalidCategoryOrContextId_ThrowsArgumentException(string? category, string? contextId)
        {
            using var stream = new MemoryStream(Encoding.UTF8.GetBytes("test"));
            await Assert.ThrowsAsync<ArgumentException>(() =>
                _service.UploadStreamAsync(category!, contextId!, stream, "test.txt"));
        }

        [Fact]
        public async Task DeleteAsync_InvalidOrNonExistentUrls_ExitsGracefully()
        {
            var exception1 = await Record.ExceptionAsync(() => _service.DeleteAsync(null!));
            var exception2 = await Record.ExceptionAsync(() => _service.DeleteAsync(""));
            var exception3 = await Record.ExceptionAsync(() => _service.DeleteAsync("/otherapi/files/a/b/c"));
            var exception4 = await Record.ExceptionAsync(() => _service.DeleteAsync("/spokesapi/files/only-two-parts"));
            var exception5 = await Record.ExceptionAsync(() => _service.DeleteAsync("/spokesapi/files/cat/ctx/non-existent.txt"));

            Assert.Null(exception1);
            Assert.Null(exception2);
            Assert.Null(exception3);
            Assert.Null(exception4);
            Assert.Null(exception5);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("..")]
        [InlineData("ctx/..")]
        [InlineData("invalid*ctx")]
        [InlineData("invalid?ctx")]
        public void CleanTempContext_InvalidContextId_DoesNothing(string? contextId)
        {
            var exception = Record.Exception(() => _service.CleanTempContext(contextId!));
            Assert.Null(exception);
        }

        [Fact]
        public void PurgeStaleTempFiles_WhenTempDirectoryDoesNotExist_ReturnsSafely()
        {
            var tempFolder = Path.Combine(_testDataDir, "Uploads", "temp");
            if (Directory.Exists(tempFolder))
            {
                Directory.Delete(tempFolder, recursive: true);
            }

            var exception = Record.Exception(() => _service.PurgeStaleTempFiles());
            Assert.Null(exception);
        }

        [Fact]
        public void GenerateSafeName_ReturnsSanitizedUniqueName()
        {
            var name1 = _service.GenerateSafeName("path/to/my file.pdf");
            var name2 = _service.GenerateSafeName("path/to/my file.pdf");

            Assert.EndsWith(".pdf", name1);
            Assert.EndsWith(".pdf", name2);
            Assert.NotEqual(name1, name2);
            Assert.DoesNotContain("/", name1);
            Assert.DoesNotContain("\\", name1);
        }

        [Fact]
        public async Task UploadAsync_WhenExceptionThrown_LogsAndRethrows()
        {
            var mockFile = new Mock<IBrowserFile>();
            mockFile.Setup(f => f.Name).Returns("test.txt");
            mockFile.Setup(f => f.OpenReadStream(It.IsAny<long>(), default)).Throws(new IOException("Disk error"));

            await Assert.ThrowsAsync<IOException>(() => _service.UploadAsync("cat", "ctx", mockFile.Object));
        }
    }

