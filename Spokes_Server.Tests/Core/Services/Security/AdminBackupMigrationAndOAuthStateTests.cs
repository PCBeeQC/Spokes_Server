using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Spokes_Server.Aggregate;
using Spokes_Server.Controllers;
using Spokes_Server.Core.Constants;
using Spokes_Server.Core.Extensions;
using Spokes_Server.Core.Helpers;
using Spokes_Server.Core.Models.Accounting;
using Spokes_Server.Core.Models.HR;
using Spokes_Server.Core.Services.Accounting;
using Spokes_Server.Core.Services.HR;
using Spokes_Server.Core.Services.Identity;
using Spokes_Server.Core.Services.Maintenance;
using Spokes_Server.Core.Services.Migrations;
using Spokes_Server.Core.Services.Security;

namespace Spokes_Server.Tests.Core.Services.Security;

public class AdminBackupMigrationAndOAuthStateTests : TestDataTestBase
{
    private readonly ServiceProvider _provider;
    private readonly Database _db;
    private readonly IConfiguration _config;
    private readonly IAccountingMigrationService _accountingMigration;
    private readonly ClockifyMigrationService _clockifyMigration;

    public AdminBackupMigrationAndOAuthStateTests()
    {
        _config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DataPath"] = _testDataPath, ["SPOKES_MASTER_PASSWORD"] = "UnitTest-Master-Password-2026!"
            })
            .Build();

        var services = new ServiceCollection();
        services.AddLocalization();
        services.AddLogging();
        services.AddSingleton(_config);
        services.AddSingleton<EncryptionService>();
        services.AddSpokesDatabase();
        services.AddSingleton<IJurisdictionTemplateService, JurisdictionTemplateService>();
        services.AddScoped<ILedgerService, LedgerService>();
        services.AddScoped<ITaxRemittanceService, TaxRemittanceService>();
        services.AddScoped<IPayrollCalculationEngine, PayrollCalculationEngine>();
        services.AddScoped<IBusinessOverheadService, BusinessOverheadService>();
        services.AddScoped<IAccountingService, AccountingService>();
        services.AddScoped<IAccountingMigrationService, AccountingMigrationService>();
        services.AddScoped<ClockifyMigrationService>();

        _provider = services.BuildServiceProvider();
        _db = _provider.GetRequiredService<Database>();
        _db.Initialize();
        _accountingMigration = _provider.GetRequiredService<IAccountingMigrationService>();
        _clockifyMigration = _provider.GetRequiredService<ClockifyMigrationService>();
    }

    public override void Dispose()
    {
        _provider.Dispose();
        base.Dispose();
    }

    private BackupController CreateBackupController(ClaimsPrincipal user, bool includeUserService = true)
    {
        UserService? userService = null;
        if (includeUserService)
        {
            var mockAuth = new Mock<AuthenticationStateProvider>();
            mockAuth.Setup(a => a.GetAuthenticationStateAsync()).ReturnsAsync(new AuthenticationState(user));
            userService = new UserService(mockAuth.Object, _db.Employees, _db.OpenIdAccounts);
        }

        var backupService = new BackupService(NullLogger<BackupService>.Instance, _db.CompanyProfile, _config);
        var controller = new BackupController(backupService, _config, userService)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { User = user }
            }
        };
        return controller;
    }

    private static ClaimsPrincipal CreatePrincipal(string employeeId) =>
        new(new ClaimsIdentity(
        [
            new Claim(ClaimTypes.NameIdentifier, employeeId),
            new Claim("EmployeeId", employeeId)
        ], "TestAuth"));

    [Fact]
    public void OAuthStateNonceService_SingleUseConsumeAndReplayRejection()
    {
        var service = new OAuthStateNonceService();
        var nonce = service.IssueNonce("emp-1", "sess-1", "gdrive");

        Assert.True(service.TryConsumeNonce("emp-1", "sess-1", "gdrive", nonce));
        Assert.False(service.TryConsumeNonce("emp-1", "sess-1", "gdrive", nonce));
    }

    [Fact]
    public void OAuthStateNonceService_WrongBindings_FailAndInvalidateNonce()
    {
        var service = new OAuthStateNonceService();

        var n1 = service.IssueNonce("emp-1", "sess-1", "gdrive");
        Assert.False(service.TryConsumeNonce("emp-2", "sess-1", "gdrive", n1));
        Assert.False(service.TryConsumeNonce("emp-1", "sess-1", "gdrive", n1));

        var n2 = service.IssueNonce("emp-1", "sess-1", "gdrive");
        Assert.False(service.TryConsumeNonce("emp-1", "sess-other", "gdrive", n2));
        Assert.False(service.TryConsumeNonce("emp-1", "sess-1", "gdrive", n2));

        var n3 = service.IssueNonce("emp-1", "sess-1", "gdrive");
        Assert.False(service.TryConsumeNonce("emp-1", "sess-1", "github", n3));
        Assert.False(service.TryConsumeNonce("emp-1", "sess-1", "gdrive", n3));
    }

    [Fact]
    public void OAuthStateNonceService_ExpiredNonce_ReturnsFalse()
    {
        var service = new OAuthStateNonceService();
        var nonce = service.IssueNonce("emp-1", "sess-1", "gdrive", TimeSpan.FromMilliseconds(1));
        Thread.Sleep(25);

        Assert.False(service.TryConsumeNonce("emp-1", "sess-1", "gdrive", nonce));
    }

    [Fact]
    public void BackupController_DownloadBackup_EnforcesActiveUnsuspendedEffectiveAdmin()
    {
        var backupsDir = Path.Combine(_testDataPath, "Backups");
        Directory.CreateDirectory(backupsDir);
        const string fileName = "Spokes_Backup_2026-11-14.zip";
        File.WriteAllText(Path.Combine(backupsDir, fileName), "zip-content");

        var activeAdmin = new Employee { Id = "admin-ok", IsActive = true, IsAdmin = true };
        var suspendedAdmin = new Employee { Id = "admin-susp", IsActive = true, IsAdmin = true, IsSuspended = true };
        var settingsManager = new Employee { Id = "mgr-only", IsActive = true, IsAdmin = false, Permissions = [AppPermissions.Admin.ManageSettings] };
        _db.Employees.Save(activeAdmin);
        _db.Employees.Save(suspendedAdmin);
        _db.Employees.Save(settingsManager);

        static void AssertForbidden(IActionResult result)
        {
            var obj = Assert.IsType<ObjectResult>(result);
            Assert.Equal(403, obj.StatusCode);
            var payload = Assert.IsType<SpokesResult>(obj.Value);
            Assert.False(payload.IsSuccess);
        }

        // Unauthenticated
        AssertForbidden(CreateBackupController(new ClaimsPrincipal(new ClaimsIdentity())).DownloadBackup(fileName));

        // Missing UserService fails closed
        AssertForbidden(CreateBackupController(CreatePrincipal(activeAdmin.Id), includeUserService: false).DownloadBackup(fileName));

        // Suspended admin denied
        AssertForbidden(CreateBackupController(CreatePrincipal(suspendedAdmin.Id)).DownloadBackup(fileName));

        // Non-admin with ManageSettings denied
        AssertForbidden(CreateBackupController(CreatePrincipal(settingsManager.Id)).DownloadBackup(fileName));

        // Active unsuspended admin allowed
        var ok = CreateBackupController(CreatePrincipal(activeAdmin.Id)).DownloadBackup(fileName);
        Assert.IsType<PhysicalFileResult>(ok);
    }

    [Fact]
    public void UserService_FilterAllowedPermissions_DoesNotBypassForSuspendedAdmin()
    {
        var suspendedAdmin = new Employee
        {
            Id = "susp-admin",
            IsActive = true,
            IsAdmin = true,
            IsSuspended = true,
            Permissions = [AppPermissions.Chat.Use]
        };

        var filtered = UserService.FilterAllowedPermissions(
            suspendedAdmin,
            [AppPermissions.Chat.Use, AppPermissions.Admin.ManageSettings]);

        Assert.DoesNotContain(AppPermissions.Admin.ManageSettings, filtered);
    }

    [Fact]
    public async Task ClockifyMigrationService_ExcludesSystemUsersAndDeniesNonAdminExecution()
    {
        var systemEmp = new Employee
        {
            Id = "sys-1",
            FirstName = "System",
            LastName = "Bot",
            Email = "sysbot@example.com",
            IsActive = true,
            IsSystem = true
        };
        var normalEmp = new Employee
        {
            Id = "emp-1",
            FirstName = "Alice",
            LastName = "Dev",
            Email = "alice@example.com",
            IsActive = true,
            IsSystem = false
        };
        var suspendedAdmin = new Employee
        {
            Id = "admin-susp",
            IsActive = true,
            IsAdmin = true,
            IsSuspended = true
        };
        var settingsManager = new Employee
        {
            Id = "mgr-1",
            IsActive = true,
            IsAdmin = false,
            Permissions = [AppPermissions.Admin.ManageSettings]
        };
        _db.Employees.Save(systemEmp);
        _db.Employees.Save(normalEmp);
        _db.Employees.Save(suspendedAdmin);
        _db.Employees.Save(settingsManager);

        var records = new List<ClockifyRecord>
        {
            new() { Project = "P1", User = "System Bot", Email = "sysbot@example.com", StartDateString = "2026-10-01", DurationDecimal = 2m, Description = "Sync", Billable = "No" },
            new() { Project = "P1", User = "Alice Dev", Email = "alice@example.com", StartDateString = "2026-10-02", DurationDecimal = 3m, Description = "Work", Billable = "Yes" }
        };

        var maps = _clockifyMigration.MapUsers(records);
        var sysMap = Assert.Single(maps, m => m.ClockifyEmail == "sysbot@example.com");
        var aliceMap = Assert.Single(maps, m => m.ClockifyEmail == "alice@example.com");

        Assert.Null(sysMap.SpokesEmployeeId);
        Assert.Equal("emp-1", aliceMap.SpokesEmployeeId);

        var projectMaps = new List<ClockifyProjectMap>
        {
            new() { OriginalName = "P1", ExtractedCode = "P1", IsNew = true }
        };

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            _clockifyMigration.ExecuteMigrationAsync(records, maps, projectMaps, null!));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            _clockifyMigration.ExecuteMigrationAsync(records, maps, projectMaps, suspendedAdmin));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            _clockifyMigration.ExecuteMigrationAsync(records, maps, projectMaps, settingsManager));
    }

    [Fact]
    public void AccountingMigrationService_DeniesNullSuspendedAndNonAdminActors()
    {
        var activeAdmin = new Employee { Id = "admin-1", IsActive = true, IsAdmin = true };
        var suspendedAdmin = new Employee { Id = "admin-susp", IsActive = true, IsAdmin = true, IsSuspended = true };
        var accountant = new Employee
        {
            Id = "acct-1",
            IsActive = true,
            IsAdmin = false,
            Permissions = [AppPermissions.Finance.AccessAccounting, AppPermissions.Admin.ManageSettings]
        };

        var request = new AccountingMigrationPreviewRequest
        {
            Mode = AccountingMigrationMode.ExistingWorkspaceDelta,
            AsOfDate = new DateTime(2026, 6, 30),
            Files =
            [
                new MigrationUploadedFile
                {
                    FileName = "tb.csv",
                    Content = Encoding.UTF8.GetBytes("Account,Description,Debit,Credit\n1020,Bank,1000.00,0.00\n4000,Rev,0.00,1000.00\n")
                }
            ]
        };

        Assert.Throws<UnauthorizedAccessException>(() => _accountingMigration.BuildPreview(request, null));
        Assert.Throws<UnauthorizedAccessException>(() => _accountingMigration.BuildPreview(request, suspendedAdmin));
        Assert.Throws<UnauthorizedAccessException>(() => _accountingMigration.BuildPreview(request, accountant));

        var preview = _accountingMigration.BuildPreview(request, activeAdmin);
        Assert.Throws<UnauthorizedAccessException>(() => _accountingMigration.ExecuteMigration(preview, "u1", "User", null));
        Assert.Throws<UnauthorizedAccessException>(() => _accountingMigration.ExecuteMigration(preview, "u1", "User", suspendedAdmin));
        Assert.Throws<UnauthorizedAccessException>(() => _accountingMigration.ExecuteMigration(preview, "u1", "User", accountant));

        var executed = _accountingMigration.ExecuteMigration(preview, activeAdmin.Id, "Admin", activeAdmin);
        Assert.Throws<UnauthorizedAccessException>(() => _accountingMigration.RollbackMigrationBatch(executed.Batch.Id, "User", null));
        Assert.Throws<UnauthorizedAccessException>(() => _accountingMigration.RollbackMigrationBatch(executed.Batch.Id, "User", suspendedAdmin));
        Assert.Throws<UnauthorizedAccessException>(() => _accountingMigration.RollbackMigrationBatch(executed.Batch.Id, "User", accountant));
    }
}
