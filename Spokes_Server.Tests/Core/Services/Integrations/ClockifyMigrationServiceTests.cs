using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using Spokes_Server.Aggregate;
using Spokes_Server.Core.Constants;
using Spokes_Server.Core.Data;
using Spokes_Server.Core.Models.HR;
using Spokes_Server.Core.Services.Integrations;
using Spokes_Server.Tests.Aggregate;
using Xunit;

namespace Spokes_Server.Tests.Core.Services.Integrations;

public class ClockifyMigrationServiceTests : TestDataTestBase
{
    private readonly ServiceProvider _serviceProvider;
    private readonly Database _db;
    private readonly ClockifyMigrationService _migrationService;
    private readonly Mock<ILogger<ClockifyMigrationService>> _mockLogger;

    public ClockifyMigrationServiceTests()
    {
        var services = new ServiceCollection();

        var mockConfig = new Mock<IConfiguration>();
        mockConfig.Setup(c => c["DataPath"]).Returns(_testDataPath);
        services.AddSingleton<IConfiguration>(mockConfig.Object);

        services.AddLogging();
        services.AddSingleton<Spokes_Server.Core.Services.Core.EncryptionService>();
        services.AddSpokesDatabase();

        _serviceProvider = services.BuildServiceProvider();
        _db = _serviceProvider.GetRequiredService<Database>();

        _mockLogger = new Mock<ILogger<ClockifyMigrationService>>();
        _migrationService = new ClockifyMigrationService(_db, _mockLogger.Object);
    }

    public override void Dispose()
    {
        _serviceProvider.Dispose();
        base.Dispose();
    }

    [Fact]
    public void DataMigrationPage_HasAuthorizePolicyAttribute()
    {
        var pageType = typeof(Spokes_Server.Components.Pages.Admin.DataMigration);
        var authorizeAttributes = pageType.GetCustomAttributes<AuthorizeAttribute>().ToList();

        Assert.NotEmpty(authorizeAttributes);
        Assert.Contains(authorizeAttributes, attr => attr.Policy == AppPermissions.Admin.ManageSettings);
    }

    [Fact]
    public async Task ExecuteMigrationAsync_NullEmployee_ThrowsUnauthorizedAccessException()
    {
        var records = new List<ClockifyRecord>();
        var userMaps = new List<ClockifyUserMap>();
        var projectMaps = new List<ClockifyProjectMap>();

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            _migrationService.ExecuteMigrationAsync(records, userMaps, projectMaps, null!));
    }

    [Fact]
    public async Task ExecuteMigrationAsync_StandardEmployeeWithoutPermission_ThrowsUnauthorizedAccessException()
    {
        var standardUser = new Employee
        {
            Id = "emp_standard",
            FirstName = "Standard",
            LastName = "User",
            Email = "standard@example.com",
            IsAdmin = false,
            Permissions = new List<string>() // No ManageSettings
        };

        var records = new List<ClockifyRecord>();
        var userMaps = new List<ClockifyUserMap>();
        var projectMaps = new List<ClockifyProjectMap>();

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            _migrationService.ExecuteMigrationAsync(records, userMaps, projectMaps, standardUser));
    }

    [Fact]
    public async Task ExecuteMigrationAsync_AdminUser_SucceedsAndSetsApprovedBy()
    {
        var adminUser = new Employee
        {
            Id = "emp_admin",
            FirstName = "Admin",
            LastName = "User",
            Email = "admin@example.com",
            IsAdmin = true
        };
        _db.Employees.Save(adminUser);

        var records = new List<ClockifyRecord>
        {
            new ClockifyRecord
            {
                Project = "Test Project",
                User = "Worker Bee",
                Email = "worker@example.com",
                StartDateString = "2026-09-01",
                DurationDecimal = 8.0m,
                Description = "Implemented feature",
                Billable = "Yes"
            }
        };

        var userMaps = new List<ClockifyUserMap>
        {
            new ClockifyUserMap
            {
                ClockifyName = "Worker Bee",
                ClockifyEmail = "worker@example.com",
                CreateNew = true
            }
        };

        var projectMaps = new List<ClockifyProjectMap>
        {
            new ClockifyProjectMap
            {
                OriginalName = "Test Project",
                ExtractedCode = "TP-100",
                IsNew = true
            }
        };

        await _migrationService.ExecuteMigrationAsync(records, userMaps, projectMaps, adminUser);

        // Verify that projects were created
        var projects = _db.Projects.GetAll();
        Assert.Contains(projects, p => p.Name == "Test Project");

        // Verify timesheet was created with Status = "Approved" and ApprovedBy = adminUser.Id
        var timesheets = _db.Timesheets.GetAll();
        Assert.NotEmpty(timesheets);
        var timesheet = timesheets.First();
        Assert.Equal("Approved", timesheet.Status);
        Assert.Equal(adminUser.Id, timesheet.ApprovedBy);
        Assert.Single(timesheet.Entries);
        Assert.Equal(8.0m, timesheet.Entries[0].Hours);
    }

    [Fact]
    public async Task ExecuteMigrationAsync_EmployeeWithManageSettings_SucceedsAndSetsApprovedBy()
    {
        var managerUser = new Employee
        {
            Id = "emp_manager",
            FirstName = "Settings",
            LastName = "Manager",
            Email = "manager@example.com",
            IsAdmin = false,
            Permissions = new List<string> { AppPermissions.Admin.ManageSettings }
        };
        _db.Employees.Save(managerUser);

        var records = new List<ClockifyRecord>
        {
            new ClockifyRecord
            {
                Project = "Client App",
                User = "Coder",
                Email = "coder@example.com",
                StartDateString = "2026-09-02",
                DurationDecimal = 4.5m,
                Description = "Bugfix",
                Billable = "Yes"
            }
        };

        var userMaps = new List<ClockifyUserMap>
        {
            new ClockifyUserMap
            {
                ClockifyName = "Coder",
                ClockifyEmail = "coder@example.com",
                CreateNew = true
            }
        };

        var projectMaps = new List<ClockifyProjectMap>
        {
            new ClockifyProjectMap
            {
                OriginalName = "Client App",
                ExtractedCode = "CA-200",
                IsNew = true
            }
        };

        await _migrationService.ExecuteMigrationAsync(records, userMaps, projectMaps, managerUser);

        var timesheets = _db.Timesheets.GetAll();
        Assert.NotEmpty(timesheets);
        var timesheet = timesheets.First();
        Assert.Equal("Approved", timesheet.Status);
        Assert.Equal(managerUser.Id, timesheet.ApprovedBy);
    }
}
