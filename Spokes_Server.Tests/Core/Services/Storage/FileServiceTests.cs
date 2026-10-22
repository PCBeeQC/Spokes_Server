using System.Text;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using Spokes_Server.Components.UI;
using Spokes_Server.Core.Data;
using Spokes_Server.Core.Models.Projects;
using Spokes_Server.Core.Services.Security;

namespace Spokes_Server.Tests.Core.Services.Storage;

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

            var result = await _service.DeleteAsync(url);

            Assert.True(result);
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
            var res1 = await _service.DeleteAsync(null!);
            var res2 = await _service.DeleteAsync("");
            var res3 = await _service.DeleteAsync("/otherapi/files/a/b/c");
            var res4 = await _service.DeleteAsync("/spokesapi/files/only-two-parts");
            var res5 = await _service.DeleteAsync("/spokesapi/files/cat/ctx/non-existent.txt");

            Assert.False(res1);
            Assert.False(res2);
            Assert.False(res3);
            Assert.False(res4);
            Assert.False(res5);
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

        [Fact]
        public async Task UploadThumbnailAsync_CreatesThumbnailFileWithSuffix()
        {
            var category = "test-thumb";
            var contextId = "ctx-thumb";
            var baseName = "image123.jpg";
            using var stream = new MemoryStream(Encoding.UTF8.GetBytes("fake-thumb-bytes"));

            var url = await _service.UploadThumbnailAsync(category, contextId, stream, baseName);

            Assert.EndsWith($"{baseName}_thumb.jpg", url);
            Assert.True(_service.FileExists(category, contextId, $"{baseName}_thumb.jpg"));

            using var readStream = _service.OpenRead(category, contextId, $"{baseName}_thumb.jpg");
            Assert.NotNull(readStream);
            using var reader = new StreamReader(readStream!);
            var content = await reader.ReadToEndAsync();
            Assert.Equal("fake-thumb-bytes", content);
        }

        [Fact]
        public void GetContentType_ReturnsCorrectMimeTypes()
        {
            Assert.Equal("application/pdf", _service.GetContentType("doc.pdf"));
            Assert.Equal("image/png", _service.GetContentType("pic.png"));
            Assert.Equal("image/jpeg", _service.GetContentType("photo.jpg"));
            Assert.Equal("text/plain", _service.GetContentType("notes.txt"));
            Assert.Equal("application/octet-stream", _service.GetContentType("unknown.xyz123"));
        }

        [Fact]
        public void GetAttachmentPhysicalPath_ResolvesPathCorrectly()
        {
            var normalPath = _service.GetAttachmentPhysicalPath("chan-1", "file.png", thumb: false);
            Assert.Contains(Path.Combine("Attachments", "chan-1", "file.png"), normalPath);

            var thumbPath = _service.GetAttachmentPhysicalPath("chan-1", "file.png", thumb: true);
            // Since thumb doesn't exist yet, it falls back to original
            Assert.Contains(Path.Combine("Attachments", "chan-1", "file.png"), thumbPath);
        }

        [Fact]
        public void GetEmailAttachmentPhysicalPath_ValidatesAndRejectsPathTraversal()
        {
            var validPath = _service.GetEmailAttachmentPhysicalPath("emp1", "msg1", "att1.pdf");
            Assert.Contains(Path.Combine("Employees", "emp1", "Email", "Attachments", "msg1", "att1.pdf"), validPath);

            // Path traversal should throw ArgumentException or be sanitized
            Assert.Throws<ArgumentException>(() => _service.GetEmailAttachmentPhysicalPath("emp1", "..", "att1.pdf"));
        }

        [Fact]
        public async Task DeleteFileAsync_RemovesFile()
        {
            var category = "del-test";
            var contextId = "ctx-del";
            var fileName = "to-remove.txt";
            using var stream = new MemoryStream(Encoding.UTF8.GetBytes("remove me"));
            await _service.UploadStreamAsync(category, contextId, stream, fileName, predefinedSafeName: fileName);

            Assert.True(_service.FileExists(category, contextId, fileName));

            var result = await _service.DeleteFileAsync(category, contextId, fileName);

            Assert.True(result);
            Assert.False(_service.FileExists(category, contextId, fileName));
        }

        [Fact]
        public async Task DeleteFileAsync_WhenFileDoesNotExist_ReturnsFalse()
        {
            var result = await _service.DeleteFileAsync("no-cat", "no-ctx", "missing.txt");
            Assert.False(result);
        }

        [Fact]
        public async Task DeleteFileAsync_WhenFileLocked_ThrowsIOException()
        {
            var category = "lock-test";
            var contextId = "ctx-lock";
            var fileName = "locked.txt";
            using var stream = new MemoryStream(Encoding.UTF8.GetBytes("locked content"));
            await _service.UploadStreamAsync(category, contextId, stream, fileName, predefinedSafeName: fileName);

            var path = _service.GetPhysicalPath(category, contextId, fileName);
            Assert.True(File.Exists(path));

            // Hold open lock with FileShare.None so deletion fails with IOException
            using (var lockStream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                await Assert.ThrowsAsync<IOException>(() => _service.DeleteFileAsync(category, contextId, fileName));
            }

            // Cleanup after lock released
            var cleaned = await _service.DeleteFileAsync(category, contextId, fileName);
            Assert.True(cleaned);
        }

        [Fact]
        public async Task EncryptFileAsync_And_DecryptFileAsync_TransformsAndRestoresContent()
        {
            var crypto = new CryptoService();
            var fileService = new FileService(_config, _mockLogger.Object, crypto);
            var key = crypto.GenerateAesKeyBase64();

            var filePath = Path.Combine(_testDataDir, "test-file.txt");
            var originalContent = "Confidential chat attachment data that must be encrypted on disk.";
            await File.WriteAllTextAsync(filePath, originalContent);

            Assert.False(fileService.IsFileEncrypted(filePath, key));

            await fileService.EncryptFileAsync(filePath, key);

            Assert.True(fileService.IsFileEncrypted(filePath, key));
            var encryptedBytes = await File.ReadAllBytesAsync(filePath);
            Assert.NotEqual(originalContent, Encoding.UTF8.GetString(encryptedBytes));

            await fileService.DecryptFileAsync(filePath, key);

            Assert.False(fileService.IsFileEncrypted(filePath, key));
            var restoredContent = await File.ReadAllTextAsync(filePath);
            Assert.Equal(originalContent, restoredContent);
        }

        [Fact]
        public async Task EncryptFileAsync_IsIdempotent_DoesNotDoubleEncrypt()
        {
            var crypto = new CryptoService();
            var fileService = new FileService(_config, _mockLogger.Object, crypto);
            var key = crypto.GenerateAesKeyBase64();

            var filePath = Path.Combine(_testDataDir, "idempotent-file.txt");
            var originalContent = "Original plaintext attachment data.";
            await File.WriteAllTextAsync(filePath, originalContent);

            // First encryption pass
            await fileService.EncryptFileAsync(filePath, key);
            var firstPassBytes = await File.ReadAllBytesAsync(filePath);

            // Second encryption pass with same key - should detect already encrypted and not double-encrypt
            await fileService.EncryptFileAsync(filePath, key);
            var secondPassBytes = await File.ReadAllBytesAsync(filePath);

            Assert.Equal(firstPassBytes, secondPassBytes);

            // Decrypting once must yield original plaintext
            await fileService.DecryptFileAsync(filePath, key);
            var restoredContent = await File.ReadAllTextAsync(filePath);
            Assert.Equal(originalContent, restoredContent);
        }

        [Fact]
        public void IsFileEncrypted_ReturnsFalseForUnencryptedPng()
        {
            var crypto = new CryptoService();
            var fileService = new FileService(_config, _mockLogger.Object, crypto);
            var key = crypto.GenerateAesKeyBase64();

            var filePath = Path.Combine(_testDataDir, "test.png");
            // Standard PNG header
            byte[] pngBytes = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x00, 0x00, 0x0D, 0x49, 0x48, 0x44, 0x52];
            File.WriteAllBytes(filePath, pngBytes);

            Assert.False(fileService.IsFileEncrypted(filePath, key));
        }

        [Fact]
        public async Task DeleteAsync_DeletesFile_AndCompanionThumbnail_AndPrunesEmptyFolder()
        {
            var category = "notes";
            var contextId = "proj-123";
            var fileName = "photo.jpg";
            using var stream = new MemoryStream(Encoding.UTF8.GetBytes("image data"));
            var fileUrl = await _service.UploadStreamAsync(category, contextId, stream, fileName);

            var parts = fileUrl.Split('/');
            var savedFileName = parts[^1];
            var dir = Path.Combine(_testDataDir, "Uploads", category, contextId);
            var primaryPath = Path.Combine(dir, savedFileName);

            // Create a fake thumbnail
            var thumbName = Path.GetFileNameWithoutExtension(savedFileName) + "_thumb.jpg";
            var thumbPath = Path.Combine(dir, thumbName);
            await File.WriteAllTextAsync(thumbPath, "thumb data");

            Assert.True(File.Exists(primaryPath));
            Assert.True(File.Exists(thumbPath));

            var deleted = await _service.DeleteAsync(fileUrl);
            Assert.True(deleted);

            Assert.False(File.Exists(primaryPath));
            Assert.False(File.Exists(thumbPath));
            Assert.False(Directory.Exists(dir)); // Should be pruned since it was empty
        }

        [Fact]
        public async Task DeleteDeltaFilesAsync_OnlyDeletesRemovedFiles()
        {
            var category = "notes";
            var contextId = "delta-test";
            using var s1 = new MemoryStream(Encoding.UTF8.GetBytes("file 1"));
            var url1 = await _service.UploadStreamAsync(category, contextId, s1, "f1.txt");

            using var s2 = new MemoryStream(Encoding.UTF8.GetBytes("file 2"));
            var url2 = await _service.UploadStreamAsync(category, contextId, s2, "f2.txt");

            var originalNote = new ProjectNote
            {
                Attachments =
                [
                    new NoteAttachment { FilePath = url1 },
                    new NoteAttachment { FilePath = url2 }
                ]
            };

            var updatedNote = new ProjectNote
            {
                Attachments =
                [
                    new NoteAttachment { FilePath = url1 }
                ]
            };

            await _service.DeleteDeltaFilesAsync(originalNote, updatedNote);

            var parts1 = url1.Split('/');
            var parts2 = url2.Split('/');
            var path1 = Path.Combine(_testDataDir, "Uploads", category, contextId, parts1[^1]);
            var path2 = Path.Combine(_testDataDir, "Uploads", category, contextId, parts2[^1]);

            Assert.True(File.Exists(path1));
            Assert.False(File.Exists(path2));
        }

        [Fact]
        public async Task RepositoryFileCascade_DeletesFilesOnEntityDelete()
        {
            RepositoryFileCascade.Initialize(_service, _mockLogger.Object);

            var category = "notes";
            var contextId = "cascade-test";
            using var s = new MemoryStream(Encoding.UTF8.GetBytes("note attachment"));
            var url = await _service.UploadStreamAsync(category, contextId, s, "note.txt");

            var note = new ProjectNote
            {
                Attachments = [new NoteAttachment { FilePath = url }]
            };

            var parts = url.Split('/');
            var filePath = Path.Combine(_testDataDir, "Uploads", category, contextId, parts[^1]);
            Assert.True(File.Exists(filePath));

            RepositoryFileCascade.HandleEntityDeleted(note);

            // Wait a brief moment as cascade handles it asynchronously
            await Task.Delay(200);
            Assert.False(File.Exists(filePath));
        }
    }

