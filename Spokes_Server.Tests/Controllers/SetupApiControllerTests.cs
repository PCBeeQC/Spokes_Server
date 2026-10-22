using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using Spokes_Server.Aggregate;
using Spokes_Server.Components.Shared;
using Spokes_Server.Controllers;
using Spokes_Server.Core.Data;
using Spokes_Server.Core.Data.Repositories.Core;
using Spokes_Server.Core.Helpers;
using Spokes_Server.Core.Models.Core;
using Spokes_Server.Core.Services.Maintenance;
using Spokes_Server.Core.Services.Security;
using Xunit;

namespace Spokes_Server.Tests.Controllers;

public class SetupApiControllerTests : TestDataTestBase
{
    private readonly Database _db;
    private readonly Mock<BackupService> _backupServiceMock;
    private readonly Mock<ILogger<SetupApiController>> _loggerMock;
    private readonly SetupApiController _controller;

    public SetupApiControllerTests()
    {
        var services = new ServiceCollection();
        var mockConfig = new Mock<IConfiguration>();
        mockConfig.Setup(c => c["DataPath"]).Returns(_testDataPath);
        services.AddSingleton<IConfiguration>(mockConfig.Object);
        services.AddLogging();
        services.AddSingleton<EncryptionService>();
        services.AddSpokesDatabase();

        var sp = services.BuildServiceProvider();
        _db = sp.GetRequiredService<Database>();

        var companyProfiles = sp.GetRequiredService<CompanyProfileRepository>();
        _backupServiceMock = new Mock<BackupService>(Mock.Of<ILogger<BackupService>>(), companyProfiles, mockConfig.Object, null);
        _loggerMock = new Mock<ILogger<SetupApiController>>();

        _controller = new SetupApiController(_db, _backupServiceMock.Object, _loggerMock.Object)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext()
            }
        };
    }

    private static IFormFile CreateTestFormFile(string fileName, string content = "dummy-zip-data")
    {
        var bytes = Encoding.UTF8.GetBytes(content);
        var stream = new MemoryStream(bytes);
        return new FormFile(stream, 0, bytes.Length, "files", fileName)
        {
            Headers = new HeaderDictionary(),
            ContentType = "application/zip"
        };
    }

    [Fact]
    public async Task RestoreBackup_WhenSetupAlreadyComplete_ReturnsForbidden403()
    {
        // Arrange
        var config = _db.SystemConfigs.Get();
        config.IsSetupComplete = true;
        _db.SystemConfigs.Save(config);

        var file = CreateTestFormFile("backup.zip");
        var formCollection = new FormFileCollection { file };

        // Act
        var result = await _controller.RestoreBackup(formCollection, CancellationToken.None);

        // Assert
        var forbidden = Assert.IsType<ObjectResult>(result);
        Assert.Equal(403, forbidden.StatusCode);
        var payload = Assert.IsType<SpokesResult>(forbidden.Value);
        Assert.False(payload.IsSuccess);
        Assert.Equal("SETUP_ALREADY_COMPLETE", payload.Code);
    }

    [Fact]
    public async Task RestoreBackup_WhenNoFileProvided_ReturnsBadRequest()
    {
        // Arrange
        var config = _db.SystemConfigs.Get();
        config.IsSetupComplete = false;
        _db.SystemConfigs.Save(config);

        var formCollection = new FormFileCollection();

        // Act
        var result = await _controller.RestoreBackup(formCollection, CancellationToken.None);

        // Assert
        var badRequest = Assert.IsType<BadRequestObjectResult>(result);
        var payload = Assert.IsType<SpokesResult>(badRequest.Value);
        Assert.False(payload.IsSuccess);
        Assert.Equal("No backup file provided.", payload.Error);
    }

    [Fact]
    public async Task RestoreBackup_WhenRestoreSucceeds_ReturnsOkWithUploadResult()
    {
        // Arrange
        var config = _db.SystemConfigs.Get();
        config.IsSetupComplete = false;
        _db.SystemConfigs.Save(config);

        string? capturedTempPath = null;
        _backupServiceMock
            .Setup(b => b.RestoreBackupAsync(It.IsAny<string>(), It.IsAny<ILogger>(), It.IsAny<CancellationToken>()))
            .Callback<string, ILogger?, CancellationToken>((path, _, _) =>
            {
                capturedTempPath = path;
                Assert.True(File.Exists(path), "Temp backup file must exist during RestoreBackupAsync execution");
            })
            .ReturnsAsync(BackupRestoreResult.Succeeded(casdoorRestarted: false));

        var file = CreateTestFormFile("valid_backup.spkbackup", "encrypted-backup-bytes");
        var formCollection = new FormFileCollection { file };

        // Act
        var result = await _controller.RestoreBackup(formCollection, CancellationToken.None);

        // Assert
        var ok = Assert.IsType<OkObjectResult>(result);
        var uploadResults = Assert.IsAssignableFrom<IEnumerable<ApiFileUpload.UploadResult>>(ok.Value);
        var firstResult = Assert.Single(uploadResults);
        Assert.Equal("valid_backup.spkbackup", firstResult.FileName);

        _backupServiceMock.Verify(b => b.RestoreBackupAsync(It.IsAny<string>(), It.IsAny<ILogger>(), It.IsAny<CancellationToken>()), Times.Once);

        // Verify temp file was cleaned up in finally block
        Assert.NotNull(capturedTempPath);
        Assert.False(File.Exists(capturedTempPath), "Temp backup file must be cleaned up after request completes");
    }

    [Fact]
    public async Task RestoreBackup_WhenRestoreFails_ReturnsBadRequestWithErrorMessage()
    {
        // Arrange
        var config = _db.SystemConfigs.Get();
        config.IsSetupComplete = false;
        _db.SystemConfigs.Save(config);

        _backupServiceMock
            .Setup(b => b.RestoreBackupAsync(It.IsAny<string>(), It.IsAny<ILogger>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(BackupRestoreResult.Failed("Master password mismatch."));

        var file = CreateTestFormFile("invalid_backup.spkbackup");
        var formCollection = new FormFileCollection { file };

        // Act
        var result = await _controller.RestoreBackup(formCollection, CancellationToken.None);

        // Assert
        var badRequest = Assert.IsType<BadRequestObjectResult>(result);
        var payload = Assert.IsType<SpokesResult>(badRequest.Value);
        Assert.False(payload.IsSuccess);
        Assert.Equal("Master password mismatch.", payload.Error);
    }
}
