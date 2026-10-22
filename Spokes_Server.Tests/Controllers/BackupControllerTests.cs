using System.Collections.Generic;
using System.IO;
using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using Microsoft.AspNetCore.Authorization;
using Spokes_Server.Controllers;
using Spokes_Server.Core.Constants;
using Spokes_Server.Core.Data;
using Spokes_Server.Core.Data.Repositories.Core;
using Spokes_Server.Core.Data.Repositories.HR;
using Spokes_Server.Core.Models.HR;
using Spokes_Server.Core.Helpers;
using Spokes_Server.Core.Security;

namespace Spokes_Server.Tests.Controllers;

public class BackupControllerTests : TestDataTestBase
{
    private readonly DiskPersistenceService _persistence;
    private readonly EmployeeRepository _employeeRepo;
    private readonly BackupService _backupService;
    private readonly string _backupsDir;

    public BackupControllerTests()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            { "DataPath", _testDataPath },
            { "Spokes_DemoMode", "false" }
        }).Build();

        var persistenceLogger = new Mock<ILogger<DiskPersistenceService>>();
        _persistence = new DiskPersistenceService(persistenceLogger.Object);

        _employeeRepo = new EmployeeRepository(_persistence, config);
        var companyRepo = new CompanyProfileRepository(_persistence, config);

        var backupLogger = new Mock<ILogger<BackupService>>();
        _backupService = new BackupService(backupLogger.Object, companyRepo, config);

        _backupsDir = Path.Combine(_testDataPath, "Backups");
        Directory.CreateDirectory(_backupsDir);
    }

    public override void Dispose()
    {
        _persistence.Dispose();
        base.Dispose();
    }

    private BackupController CreateController(ClaimsPrincipal? user = null, bool demoMode = false)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            { "DataPath", _testDataPath },
            { "Spokes_DemoMode", demoMode ? "true" : "false" }
        }).Build();

        return new BackupController(_backupService, config)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = user ?? new ClaimsPrincipal(new ClaimsIdentity())
                }
            }
        };
    }

    private static ClaimsPrincipal CreateUserPrincipal(string employeeId)
    {
        var identity = new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.NameIdentifier, employeeId),
            new Claim("EmployeeId", employeeId)
        }, "TestAuth");

        return new ClaimsPrincipal(identity);
    }

    [Fact]
    public void DownloadBackup_WhenDemoModeIsTrue_ReturnsForbid()
    {
        // Arrange
        var admin = new Employee { Id = "admin1", IsAdmin = true, IsActive = true };
        _employeeRepo.Save(admin);
        var controller = CreateController(CreateUserPrincipal("admin1"), demoMode: true);

        // Act
        var result = controller.DownloadBackup("Spokes_Backup_1.zip");

        // Assert
        var forbidden = Assert.IsType<ObjectResult>(result);
        Assert.Equal(403, forbidden.StatusCode);
        var payload = Assert.IsType<SpokesResult>(forbidden.Value);
        Assert.False(payload.IsSuccess);
    }

    [Fact]
    public void Constructor_WithDependencies_InitializesSuccessfully()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>()).Build();
        var controller = new BackupController(_backupService, config);
        Assert.NotNull(controller);
    }

    [Fact]
    public async Task DownloadBackup_PolicyAuthorization_AllowsAdmin()
    {
        var admin = new Employee { Id = "admin1", IsAdmin = true, IsActive = true };
        _employeeRepo.Save(admin);
        var handler = new PermissionAuthorizationHandler(_employeeRepo);
        var requirement = new PermissionRequirement(AppPermissions.Admin.ManageSettings);
        var context = new AuthorizationHandlerContext(new[] { requirement }, CreateUserPrincipal("admin1"), null);

        await handler.HandleAsync(context);

        Assert.True(context.HasSucceeded);
    }

    [Fact]
    public async Task DownloadBackup_PolicyAuthorization_AllowsUserWithManageSettingsPermission()
    {
        var emp = new Employee { Id = "emp_settings", IsActive = true, Permissions = new List<string> { AppPermissions.Admin.ManageSettings } };
        _employeeRepo.Save(emp);
        var handler = new PermissionAuthorizationHandler(_employeeRepo);
        var requirement = new PermissionRequirement(AppPermissions.Admin.ManageSettings);
        var context = new AuthorizationHandlerContext(new[] { requirement }, CreateUserPrincipal("emp_settings"), null);

        await handler.HandleAsync(context);

        Assert.True(context.HasSucceeded);
    }

    [Fact]
    public async Task DownloadBackup_PolicyAuthorization_RejectsUserWithoutManageSettingsPermission()
    {
        var emp = new Employee { Id = "emp_noperm", IsActive = true, IsAdmin = false };
        _employeeRepo.Save(emp);
        var handler = new PermissionAuthorizationHandler(_employeeRepo);
        var requirement = new PermissionRequirement(AppPermissions.Admin.ManageSettings);
        var context = new AuthorizationHandlerContext(new[] { requirement }, CreateUserPrincipal("emp_noperm"), null);

        await handler.HandleAsync(context);

        Assert.False(context.HasSucceeded);
    }

    [Theory]
    [InlineData(false, false, false)] // Inactive
    [InlineData(true, true, false)]  // Suspended
    [InlineData(true, false, true)]  // Banned
    public async Task DownloadBackup_PolicyAuthorization_RejectsInactiveSuspendedOrBannedUser(bool isActive, bool isSuspended, bool isBanned)
    {
        var emp = new Employee
        {
            Id = "emp_disallowed",
            IsActive = isActive,
            IsSuspended = isSuspended,
            IsBanned = isBanned,
            IsAdmin = true,
            Permissions = new List<string> { AppPermissions.Admin.ManageSettings }
        };
        _employeeRepo.Save(emp);
        var handler = new PermissionAuthorizationHandler(_employeeRepo);
        var requirement = new PermissionRequirement(AppPermissions.Admin.ManageSettings);
        var context = new AuthorizationHandlerContext(new[] { requirement }, CreateUserPrincipal("emp_disallowed"), null);

        await handler.HandleAsync(context);

        Assert.False(context.HasSucceeded);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("../Spokes_Backup_1.zip")]
    [InlineData("Spokes_.._Backup.zip")]
    [InlineData("folder/Spokes_Backup_1.zip")]
    [InlineData("Spokes_Backup_1.zip/")]
    [InlineData("folder\\Spokes_Backup_1.zip")]
    [InlineData("\\Spokes_Backup_1.zip")]
    [InlineData("..\\Spokes_Backup_1.zip")]
    public void DownloadBackup_WhenFileNameIsInvalid_ReturnsBadRequest(string? invalidFileName)
    {
        // Arrange
        var admin = new Employee { Id = "admin1", IsAdmin = true, IsActive = true };
        _employeeRepo.Save(admin);
        var controller = CreateController(CreateUserPrincipal("admin1"));

        // Act
        var result = controller.DownloadBackup(invalidFileName!);

        // Assert
        var badRequestResult = Assert.IsType<BadRequestObjectResult>(result);
        var payload = Assert.IsType<SpokesResult>(badRequestResult.Value);
        Assert.False(payload.IsSuccess);
        Assert.Equal("Invalid file name", payload.Error);
    }

    [Fact]
    public void DownloadBackup_WhenNoBackupsExist_ReturnsNotFound()
    {
        // Arrange
        var admin = new Employee { Id = "admin1", IsAdmin = true, IsActive = true };
        _employeeRepo.Save(admin);
        var controller = CreateController(CreateUserPrincipal("admin1"));

        // Act
        var result = controller.DownloadBackup("Spokes_Backup_NonExistent.zip");

        // Assert
        var notFound = Assert.IsType<NotFoundObjectResult>(result);
        var payload = Assert.IsType<SpokesResult>(notFound.Value);
        Assert.False(payload.IsSuccess);
    }

    [Fact]
    public void DownloadBackup_WhenRequestedBackupNotInBackupsList_ReturnsNotFound()
    {
        // Arrange
        var admin = new Employee { Id = "admin1", IsAdmin = true, IsActive = true };
        _employeeRepo.Save(admin);
        var controller = CreateController(CreateUserPrincipal("admin1"));

        // Existing backup
        File.WriteAllText(Path.Combine(_backupsDir, "Spokes_Backup_Existing.zip"), "test backup content");

        // Act - request a different backup
        var result = controller.DownloadBackup("Spokes_Backup_Different.zip");

        // Assert
        var notFoundResult = Assert.IsType<NotFoundObjectResult>(result);
        var payloadResult = Assert.IsType<SpokesResult>(notFoundResult.Value);
        Assert.False(payloadResult.IsSuccess);
    }

    [Fact]
    public void DownloadBackup_WhenAdminAndBackupExists_ReturnsPhysicalFileResult()
    {
        // Arrange
        var admin = new Employee { Id = "admin1", IsAdmin = true, IsActive = true };
        _employeeRepo.Save(admin);
        var controller = CreateController(CreateUserPrincipal("admin1"));

        var fileName = "Spokes_Backup_2026-09-14.zip";
        var filePath = Path.Combine(_backupsDir, fileName);
        File.WriteAllText(filePath, "sample backup data");

        // Act
        var result = controller.DownloadBackup(fileName);

        // Assert
        var physicalFile = Assert.IsType<PhysicalFileResult>(result);
        Assert.Equal("application/zip", physicalFile.ContentType);
        Assert.Equal(fileName, physicalFile.FileDownloadName);
        Assert.Equal(filePath, physicalFile.FileName);
        Assert.True(physicalFile.EnableRangeProcessing);
    }

    [Fact]
    public void DownloadBackup_WhenUserHasManageSettingsPermissionAndBackupExists_ReturnsPhysicalFileResult()
    {
        // Arrange - non-admin with AppPermissions.Admin.ManageSettings
        var user = new Employee
        {
            Id = "manager1",
            IsAdmin = false,
            IsActive = true,
            Permissions = new List<string> { AppPermissions.Admin.ManageSettings }
        };
        _employeeRepo.Save(user);
        var controller = CreateController(CreateUserPrincipal("manager1"));

        var fileName = "Spokes_Backup_2026-09-14.zip";
        var filePath = Path.Combine(_backupsDir, fileName);
        File.WriteAllText(filePath, "sample backup data");

        // Act
        var result = controller.DownloadBackup(fileName);

        // Assert
        var physicalFile = Assert.IsType<PhysicalFileResult>(result);
        Assert.Equal("application/zip", physicalFile.ContentType);
        Assert.Equal(fileName, physicalFile.FileDownloadName);
        Assert.Equal(filePath, physicalFile.FileName);
        Assert.True(physicalFile.EnableRangeProcessing);
    }

    [Fact]
    public void DownloadBackup_WhenFileNameMatchesCaseInsensitively_ReturnsPhysicalFileResult()
    {
        // Arrange
        var admin = new Employee { Id = "admin1", IsAdmin = true, IsActive = true };
        _employeeRepo.Save(admin);
        var controller = CreateController(CreateUserPrincipal("admin1"));

        var actualFileName = "Spokes_Backup_CaseTest.zip";
        var filePath = Path.Combine(_backupsDir, actualFileName);
        File.WriteAllText(filePath, "sample backup data");

        // Act - request in lowercase
        var result = controller.DownloadBackup("spokes_backup_casetest.zip");

        // Assert
        var physicalFile = Assert.IsType<PhysicalFileResult>(result);
        Assert.Equal("application/zip", physicalFile.ContentType);
        Assert.Equal(actualFileName, physicalFile.FileDownloadName);
        Assert.Equal(filePath, physicalFile.FileName);
        Assert.True(physicalFile.EnableRangeProcessing);
    }

    [Fact]
    public void DownloadBackup_WhenManualBackupExists_ReturnsPhysicalFileResult()
    {
        // Arrange
        var admin = new Employee { Id = "admin1", IsAdmin = true, IsActive = true };
        _employeeRepo.Save(admin);
        var controller = CreateController(CreateUserPrincipal("admin1"));

        var fileName = "Spokes_Backup_Manual_2026-09-14.zip";
        var filePath = Path.Combine(_backupsDir, fileName);
        File.WriteAllText(filePath, "sample manual backup data");

        // Act
        var result = controller.DownloadBackup(fileName);

        // Assert
        var physicalFile = Assert.IsType<PhysicalFileResult>(result);
        Assert.Equal("application/zip", physicalFile.ContentType);
        Assert.Equal(fileName, physicalFile.FileDownloadName);
        Assert.Equal(filePath, physicalFile.FileName);
        Assert.True(physicalFile.EnableRangeProcessing);
    }

    [Fact]
    public void DownloadBackup_HasAuthorizePolicyAttribute()
    {
        var classAuthorize = typeof(BackupController).GetCustomAttributes(typeof(Microsoft.AspNetCore.Authorization.AuthorizeAttribute), true);
        Assert.NotEmpty(classAuthorize);

        var method = typeof(BackupController).GetMethod(nameof(BackupController.DownloadBackup));
        Assert.NotNull(method);

        var authAttrs = method.GetCustomAttributes(typeof(Microsoft.AspNetCore.Authorization.AuthorizeAttribute), true)
            .Cast<Microsoft.AspNetCore.Authorization.AuthorizeAttribute>()
            .ToList();

        Assert.NotEmpty(authAttrs);
        Assert.Equal(AppPermissions.Admin.ManageSettings, authAttrs[0].Policy);
    }
}
