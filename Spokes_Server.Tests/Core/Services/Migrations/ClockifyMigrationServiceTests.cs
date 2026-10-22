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
using Spokes_Server.Core.Services.Migrations;
using Spokes_Server.Tests.Aggregate;
using Xunit;

namespace Spokes_Server.Tests.Core.Services.Migrations;

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
        services.AddSingleton<EncryptionService>();
        services.AddSpokesDatabase();

        _serviceProvider = services.BuildServiceProvider();
        _db = _serviceProvider.GetRequiredService<Database>();

        _mockLogger = new Mock<ILogger<ClockifyMigrationService>>();
        _migrationService = new ClockifyMigrationService(_db.HrContext, _db.ProjectsContext, _mockLogger.Object);
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

    [Fact]
    public async Task ParseCsvAsync_ValidClockifyCsv_ParsesRecordsAndCalculatesDateRange()
    {
        var csvContent = "Project,Description,Task,User,Email,Start Date,Duration (decimal),Billable\r\n" +
                         "Project Alpha,Fixed login bug,Bugfix,Jane Doe,jane@example.com,2026-05-10,3.75,Yes\r\n" +
                         "Project Beta,\"Design review, client meeting\",Meeting,John Smith,john@example.com,2026-05-15,1.5,No";

        using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(csvContent));
        var result = await _migrationService.ParseCsvAsync(stream, "clockify_export.csv");

        Assert.Equal("clockify_export.csv", result.FileName);
        Assert.Equal(2, result.Records.Count);

        var first = result.Records[0];
        Assert.Equal("Project Alpha", first.Project);
        Assert.Equal("Fixed login bug", first.Description);
        Assert.Equal("Bugfix", first.Task);
        Assert.Equal("Jane Doe", first.User);
        Assert.Equal("jane@example.com", first.Email);
        Assert.Equal("2026-05-10", first.StartDateString);
        Assert.Equal(3.75m, first.DurationDecimal);
        Assert.Equal("Yes", first.Billable);

        var second = result.Records[1];
        Assert.Equal("Project Beta", second.Project);
        Assert.Equal("Design review, client meeting", second.Description);
        Assert.Equal("Meeting", second.Task);
        Assert.Equal("John Smith", second.User);
        Assert.Equal("john@example.com", second.Email);
        Assert.Equal("2026-05-15", second.StartDateString);
        Assert.Equal(1.5m, second.DurationDecimal);
        Assert.Equal("No", second.Billable);

        Assert.Equal(new DateTime(2026, 5, 10), result.MinDate);
        Assert.Equal(new DateTime(2026, 5, 15), result.MaxDate);
        Assert.Equal("2026", result.DateRangeDisplay);
    }

    [Fact]
    public async Task ExecuteMigrationAsync_WithCancellationToken_CanCancelExecution()
    {
        var adminUser = new Employee
        {
            Id = "emp_admin_cancel",
            FirstName = "Admin",
            LastName = "Cancel",
            Email = "admin_cancel@example.com",
            IsAdmin = true
        };
        _db.Employees.Save(adminUser);

        using var cts = new System.Threading.CancellationTokenSource();
        cts.Cancel();

        var records = new List<ClockifyRecord>();
        var userMaps = new List<ClockifyUserMap>();
        var projectMaps = new List<ClockifyProjectMap>();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            _migrationService.ExecuteMigrationAsync(records, userMaps, projectMaps, adminUser, cts.Token));
    }
}
