using System.Collections.Generic;
using System.IO;
using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using Spokes_Server.Controllers;
using Spokes_Server.Core.Constants;
using Spokes_Server.Core.Data;
using Spokes_Server.Core.Data.Repositories.Core;
using Spokes_Server.Core.Data.Repositories.HR;
using Spokes_Server.Core.Models.HR;
using Spokes_Server.Core.Services.Core;

namespace Spokes_Server.Tests.Controllers;

public class BackupControllerTests : TestDataTestBase
{
    private readonly DiskPersistenceService _persistence;
    private readonly EmployeeRepository _employeeRepo;
    private readonly BackupService _backupService;
    private readonly UserService _userService;
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
        var openIdRepo = new OpenIdAccountRepository(_persistence, config);
        var companyRepo = new CompanyProfileRepository(_persistence, config);

        var mockAuthState = new Mock<Microsoft.AspNetCore.Components.Authorization.AuthenticationStateProvider>();
        _userService = new UserService(mockAuthState.Object, _employeeRepo, openIdRepo);

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

        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(config);
        var serviceProvider = services.BuildServiceProvider();

        return new BackupController(_backupService, _userService)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    RequestServices = serviceProvider,
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
        Assert.IsType<ForbidResult>(result);
    }

    [Fact]
    public void DownloadBackup_WhenUserNotFound_ReturnsUnauthorized()
    {
        // Arrange - unauthenticated user
        var controller = CreateController();

        // Act
        var result = controller.DownloadBackup("Spokes_Backup_1.zip");

        // Assert
        var unauthorizedResult = Assert.IsType<UnauthorizedObjectResult>(result);
        Assert.Equal("User profile not active or not found", unauthorizedResult.Value);
    }

    [Fact]
    public void DownloadBackup_WhenUserClaimsExistButEmployeeMissing_ReturnsUnauthorized()
    {
        // Arrange - authenticated user with non-existent employee ID
        var user = CreateUserPrincipal("missing_emp");
        var controller = CreateController(user);

        // Act
        var result = controller.DownloadBackup("Spokes_Backup_1.zip");

        // Assert
        var unauthorizedResult = Assert.IsType<UnauthorizedObjectResult>(result);
        Assert.Equal("User profile not active or not found", unauthorizedResult.Value);
    }

    [Fact]
    public void DownloadBackup_WhenUserIsInactive_ReturnsUnauthorized()
    {
        // Arrange
        var emp = new Employee { Id = "emp_inactive", IsActive = false, IsAdmin = true };
        _employeeRepo.Save(emp);
        var controller = CreateController(CreateUserPrincipal("emp_inactive"));

        // Act
        var result = controller.DownloadBackup("Spokes_Backup_1.zip");

        // Assert
        var unauthorizedResult = Assert.IsType<UnauthorizedObjectResult>(result);
        Assert.Equal("User profile not active or not found", unauthorizedResult.Value);
    }

    [Fact]
    public void DownloadBackup_WhenUserIsSuspended_ReturnsUnauthorized()
    {
        // Arrange
        var emp = new Employee { Id = "emp_suspended", IsActive = true, IsSuspended = true, IsAdmin = true };
        _employeeRepo.Save(emp);
        var controller = CreateController(CreateUserPrincipal("emp_suspended"));

        // Act
        var result = controller.DownloadBackup("Spokes_Backup_1.zip");

        // Assert
        var unauthorizedResult = Assert.IsType<UnauthorizedObjectResult>(result);
        Assert.Equal("User profile not active or not found", unauthorizedResult.Value);
    }

    [Fact]
    public void DownloadBackup_WhenUserIsBanned_ReturnsUnauthorized()
    {
        // Arrange
        var emp = new Employee { Id = "emp_banned", IsActive = true, IsSuspended = false, IsBanned = true, IsAdmin = true };
        _employeeRepo.Save(emp);
        var controller = CreateController(CreateUserPrincipal("emp_banned"));

        // Act
        var result = controller.DownloadBackup("Spokes_Backup_1.zip");

        // Assert
        var unauthorizedResult = Assert.IsType<UnauthorizedObjectResult>(result);
        Assert.Equal("User profile not active or not found", unauthorizedResult.Value);
    }

    [Fact]
    public void DownloadBackup_WhenUserNotAdminAndLacksManageSettingsPermission_ReturnsForbid()
    {
        // Arrange
        var emp = new Employee { Id = "emp_noperm", IsActive = true, IsAdmin = false };
        _employeeRepo.Save(emp);
        var controller = CreateController(CreateUserPrincipal("emp_noperm"));

        // Act
        var result = controller.DownloadBackup("Spokes_Backup_1.zip");

        // Assert
        Assert.IsType<ForbidResult>(result);
    }

    [Fact]
    public void DownloadBackup_WhenUserHasOtherPermissionsButNotManageSettings_ReturnsForbid()
    {
        // Arrange
        var emp = new Employee
        {
            Id = "emp_wrongperm",
            IsActive = true,
            IsAdmin = false,
            Permissions = new List<string> { "Accounting.Invoices.Manage", "HR.Employees.View" }
        };
        _employeeRepo.Save(emp);
        var controller = CreateController(CreateUserPrincipal("emp_wrongperm"));

        // Act
        var result = controller.DownloadBackup("Spokes_Backup_1.zip");

        // Assert
        Assert.IsType<ForbidResult>(result);
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
        Assert.Equal("Invalid file name", badRequestResult.Value);
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
        Assert.IsType<NotFoundResult>(result);
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
        Assert.IsType<NotFoundResult>(result);
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
}
