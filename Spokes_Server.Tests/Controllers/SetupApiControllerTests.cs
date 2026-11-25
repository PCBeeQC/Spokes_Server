using System;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Spokes_Server.Aggregate;
using Spokes_Server.Controllers;
using Spokes_Server.Core.Data;
using Spokes_Server.Core.Data.Repositories.Core;
using Spokes_Server.Core.Helpers;
using Spokes_Server.Core.Services.Maintenance;
using Spokes_Server.Core.Services.Security;
using Xunit;

namespace Spokes_Server.Tests.Controllers;

public class SetupApiControllerTests : TestDataTestBase
{
    private const string TestMasterPassword = "UnitTest-Master-Password-2026!";

    private readonly Database _db;
    private readonly Mock<BackupService> _backupServiceMock;
    private readonly BackupEncryptionService _encryption;
    private readonly BackupRestoreUploadService _uploadService;
    private readonly Mock<ILogger<SetupApiController>> _loggerMock;
    private readonly SetupApiController _controller;

    public SetupApiControllerTests()
    {
        var services = new ServiceCollection();
        var mockConfig = new Mock<IConfiguration>();
        mockConfig.Setup(c => c["DataPath"]).Returns(_testDataPath);
        mockConfig.Setup(c => c["SPOKES_MASTER_PASSWORD"]).Returns(TestMasterPassword);
        services.AddSingleton<IConfiguration>(mockConfig.Object);
        services.AddLogging();
        services.AddSingleton<EncryptionService>();
        services.AddSpokesDatabase();

        var sp = services.BuildServiceProvider();
        _db = sp.GetRequiredService<Database>();

        var companyProfiles = sp.GetRequiredService<CompanyProfileRepository>();
        _encryption = new BackupEncryptionService();
        _backupServiceMock = new Mock<BackupService>(
            Mock.Of<ILogger<BackupService>>(),
            companyProfiles,
            mockConfig.Object,
            null,
            null,
            _encryption,
            null,
            null);

        _uploadService = new BackupRestoreUploadService(
            _backupServiceMock.Object,
            _encryption,
            NullLogger<BackupRestoreUploadService>.Instance);

        _loggerMock = new Mock<ILogger<SetupApiController>>();

        _controller = new SetupApiController(_db, _uploadService, _loggerMock.Object)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext()
            }
        };
    }

    private static byte[] CreateMinimalZipBytes()
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = zip.CreateEntry("synthetic_company.json");
            using var sw = new StreamWriter(entry.Open(), Encoding.UTF8);
            sw.Write("{\"CompanyName\":\"Acme Demo Inc.\"}");
        }
        return ms.ToArray();
    }

    [Fact]
    public async Task ChunkedRestoreEndpoints_WhenSetupAlreadyComplete_ReturnForbidden403()
    {
        // Arrange
        var config = _db.SystemConfigs.Get();
        config.IsSetupComplete = true;
        _db.SystemConfigs.Save(config);

        // Act & Assert across all 4 setup endpoints
        var initResult = _controller.InitRestoreUpload(new BackupUploadInitRequest("backup.zip", 1024));
        var initForbidden = Assert.IsType<ObjectResult>(initResult);
        Assert.Equal(403, initForbidden.StatusCode);
        Assert.Equal("SETUP_ALREADY_COMPLETE", Assert.IsType<SpokesResult>(initForbidden.Value).Code);

        var chunkResult = await _controller.UploadRestoreChunk("fake-id", 0, CancellationToken.None);
        var chunkForbidden = Assert.IsType<ObjectResult>(chunkResult);
        Assert.Equal(403, chunkForbidden.StatusCode);
        Assert.Equal("SETUP_ALREADY_COMPLETE", Assert.IsType<SpokesResult>(chunkForbidden.Value).Code);

        var completeResult = await _controller.CompleteRestoreUpload("fake-id", CancellationToken.None);
        var completeForbidden = Assert.IsType<ObjectResult>(completeResult);
        Assert.Equal(403, completeForbidden.StatusCode);
        Assert.Equal("SETUP_ALREADY_COMPLETE", Assert.IsType<SpokesResult>(completeForbidden.Value).Code);

        var cancelResult = await _controller.CancelRestoreUpload("fake-id");
        var cancelForbidden = Assert.IsType<ObjectResult>(cancelResult);
        Assert.Equal(403, cancelForbidden.StatusCode);
        Assert.Equal("SETUP_ALREADY_COMPLETE", Assert.IsType<SpokesResult>(cancelForbidden.Value).Code);
    }

    [Fact]
    public void InitRestoreUpload_WhenPayloadNullOrExtensionInvalid_ReturnsBadRequest()
    {
        var config = _db.SystemConfigs.Get();
        config.IsSetupComplete = false;
        _db.SystemConfigs.Save(config);

        var nullRes = _controller.InitRestoreUpload(null);
        Assert.IsType<BadRequestObjectResult>(nullRes);

        var badExtRes = _controller.InitRestoreUpload(new BackupUploadInitRequest("notes.txt", 1024));
        var badExtObj = Assert.IsType<BadRequestObjectResult>(badExtRes);
        var payload = Assert.IsType<SpokesResult>(badExtObj.Value);
        Assert.Equal("INVALID_EXTENSION", payload.Code);
    }

    [Fact]
    public async Task UploadRestoreChunk_WhenChunkZeroIsInvalidArchive_FailsFastWithBadRequest()
    {
        var config = _db.SystemConfigs.Get();
        config.IsSetupComplete = false;
        _db.SystemConfigs.Save(config);

        var bogus = Encoding.UTF8.GetBytes("not-a-valid-zip-or-encrypted-backup");
        var initOk = Assert.IsType<OkObjectResult>(_controller.InitRestoreUpload(new BackupUploadInitRequest("bad.zip", bogus.Length)));
        using var initDoc = JsonDocument.Parse(JsonSerializer.Serialize(initOk.Value));
        var uploadId = initDoc.RootElement.GetProperty("uploadId").GetString()!;

        _controller.HttpContext.Request.Body = new MemoryStream(bogus);
        _controller.HttpContext.Request.ContentLength = bogus.Length;

        var chunkRes = await _controller.UploadRestoreChunk(uploadId, 0, CancellationToken.None);
        var badReq = Assert.IsType<BadRequestObjectResult>(chunkRes);
        var payload = Assert.IsType<SpokesResult>(badReq.Value);
        Assert.False(payload.IsSuccess);
        Assert.Equal("INVALID_BACKUP_FORMAT", payload.Code);
    }

    [Fact]
    public async Task UploadRestoreChunk_WhenEncryptedBackupPasswordMismatches_FailsFastWithBadRequest()
    {
        var config = _db.SystemConfigs.Get();
        config.IsSetupComplete = false;
        _db.SystemConfigs.Save(config);

        using var input = new MemoryStream(CreateMinimalZipBytes());
        using var encryptedMs = new MemoryStream();
        await _encryption.EncryptBackupAsync(input, encryptedMs, "Wrong-Different-Master-Password!");
        var encryptedBytes = encryptedMs.ToArray();

        var initOk = Assert.IsType<OkObjectResult>(_controller.InitRestoreUpload(new BackupUploadInitRequest("wrong_key.spkbackup", encryptedBytes.Length)));
        using var initDoc = JsonDocument.Parse(JsonSerializer.Serialize(initOk.Value));
        var uploadId = initDoc.RootElement.GetProperty("uploadId").GetString()!;

        _controller.HttpContext.Request.Body = new MemoryStream(encryptedBytes);
        _controller.HttpContext.Request.ContentLength = encryptedBytes.Length;

        var chunkRes = await _controller.UploadRestoreChunk(uploadId, 0, CancellationToken.None);
        var badReq = Assert.IsType<BadRequestObjectResult>(chunkRes);
        var payload = Assert.IsType<SpokesResult>(badReq.Value);
        Assert.False(payload.IsSuccess);
        Assert.Equal("MASTER_PASSWORD_MISMATCH", payload.Code);
    }

    [Fact]
    public async Task ChunkedRestoreFlow_InitChunkCompleteAndRestoreStagedUpload_SucceedsAndCleansUpTempFile()
    {
        // Arrange
        var config = _db.SystemConfigs.Get();
        config.IsSetupComplete = false;
        _db.SystemConfigs.Save(config);

        string? capturedTempPath = null;
        _backupServiceMock
            .Setup(b => b.RestoreBackupAsync(
                It.IsAny<string>(),
                It.IsAny<ILogger?>(),
                It.IsAny<CancellationToken>(),
                It.IsAny<IProgress<BackupRestoreProgress>?>(),
                It.IsAny<string?>()))
            .Callback<string, ILogger?, CancellationToken, IProgress<BackupRestoreProgress>?, string?>((path, _, _, _, _) =>
            {
                capturedTempPath = path;
                Assert.True(File.Exists(path), "Staged backup file must exist during RestoreBackupAsync execution");
            })
            .ReturnsAsync(BackupRestoreResult.Succeeded(casdoorRestarted: false));

        var zipBytes = CreateMinimalZipBytes();

        // Act: Init -> Chunk 0 -> Complete -> RestoreStagedUploadAsync
        var initOk = Assert.IsType<OkObjectResult>(_controller.InitRestoreUpload(new BackupUploadInitRequest("valid_backup.zip", zipBytes.Length)));
        using var initDoc = JsonDocument.Parse(JsonSerializer.Serialize(initOk.Value));
        var uploadId = initDoc.RootElement.GetProperty("uploadId").GetString()!;

        _controller.HttpContext.Request.Body = new MemoryStream(zipBytes);
        _controller.HttpContext.Request.ContentLength = zipBytes.Length;
        Assert.IsType<OkObjectResult>(await _controller.UploadRestoreChunk(uploadId, 0, CancellationToken.None));

        var completeOk = Assert.IsType<OkObjectResult>(await _controller.CompleteRestoreUpload(uploadId, CancellationToken.None));
        using var completeDoc = JsonDocument.Parse(JsonSerializer.Serialize(completeOk.Value));
        Assert.Equal("valid_backup.zip", completeDoc.RootElement.GetProperty("fileName").GetString());
        Assert.Equal(zipBytes.Length, completeDoc.RootElement.GetProperty("fileSizeBytes").GetInt64());

        var restoreResult = await _uploadService.RestoreStagedUploadAsync(uploadId, BackupUploadScope.Setup, null);

        // Assert
        Assert.True(restoreResult.Success);
        _backupServiceMock.Verify(b => b.RestoreBackupAsync(
            It.IsAny<string>(),
            It.IsAny<ILogger?>(),
            It.IsAny<CancellationToken>(),
            It.IsAny<IProgress<BackupRestoreProgress>?>(),
            It.IsAny<string?>()), Times.Once);

        Assert.NotNull(capturedTempPath);
        Assert.False(File.Exists(capturedTempPath), "Staged backup file must be cleaned up after restore completes");
    }

    [Fact]
    public async Task CancelRestoreUpload_WhenSetupIncomplete_RemovesSessionAndReturnsOk()
    {
        var config = _db.SystemConfigs.Get();
        config.IsSetupComplete = false;
        _db.SystemConfigs.Save(config);

        var initOk = Assert.IsType<OkObjectResult>(_controller.InitRestoreUpload(new BackupUploadInitRequest("cancel_me.zip", 4096)));
        using var initDoc = JsonDocument.Parse(JsonSerializer.Serialize(initOk.Value));
        var uploadId = initDoc.RootElement.GetProperty("uploadId").GetString()!;

        Assert.IsType<OkResult>(await _controller.CancelRestoreUpload(uploadId));

        // Subsequent complete must report SESSION_NOT_FOUND (404)
        var afterCancel = await _controller.CompleteRestoreUpload(uploadId, CancellationToken.None);
        var notFound = Assert.IsType<NotFoundObjectResult>(afterCancel);
        Assert.Equal("SESSION_NOT_FOUND", Assert.IsType<SpokesResult>(notFound.Value).Code);
    }
}
