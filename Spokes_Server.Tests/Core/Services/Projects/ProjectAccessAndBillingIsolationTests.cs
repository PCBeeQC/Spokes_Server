using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using Spokes_Server.Aggregate;
using Spokes_Server.Core.Constants;
using Spokes_Server.Core.Data;
using Spokes_Server.Core.Extensions;
using Spokes_Server.Core.Models.Accounting;
using Spokes_Server.Core.Models.HR;
using Spokes_Server.Core.Models.Projects;
using Spokes_Server.Core.Services.Accounting;
using Spokes_Server.Core.Services.HR;
using Spokes_Server.Core.Services.Projects;
using Xunit;

namespace Spokes_Server.Tests.Core.Services.Projects;

public class ProjectAccessAndBillingIsolationTests : TestDataTestBase
{
    private readonly ServiceProvider _serviceProvider;
    private readonly Database _db;
    private readonly BillingOrchestrationService _billing;
    private readonly HRService _hrService;

    public ProjectAccessAndBillingIsolationTests()
    {
        var services = new ServiceCollection();
        var mockConfig = new Mock<IConfiguration>();
        mockConfig.Setup(c => c["DataPath"]).Returns(_testDataPath);
        mockConfig.Setup(c => c["SPOKES_MASTER_PASSWORD"]).Returns("UnitTest-Master-Password-2026!");
        services.AddSingleton(mockConfig.Object);
        services.AddLogging();
        services.AddSingleton<EncryptionService>();
        services.AddSpokesDatabase();
        services.AddScoped<BillingOrchestrationService>();
        services.AddScoped<HRService>(sp => new HRService(
            sp.GetRequiredService<Spokes_Server.Core.Data.Repositories.HR.TimesheetRepository>(),
            sp.GetRequiredService<Spokes_Server.Core.Data.Repositories.HR.EmployeeRepository>(),
            sp.GetRequiredService<Spokes_Server.Core.Data.Repositories.HR.TeamRepository>(),
            sp.GetService<ILogger<HRService>>(),
            sp.GetRequiredService<Spokes_Server.Core.Data.Repositories.Projects.ProjectRepository>(),
            sp.GetRequiredService<Spokes_Server.Core.Data.Repositories.Accounting.InvoiceRepository>()));

        _serviceProvider = services.BuildServiceProvider();
        _db = _serviceProvider.GetRequiredService<Database>();
        _billing = _serviceProvider.GetRequiredService<BillingOrchestrationService>();
        _hrService = _serviceProvider.GetRequiredService<HRService>();
    }

    public override void Dispose()
    {
        _serviceProvider.Dispose();
        base.Dispose();
    }

    [Fact]
    public void ProjectAccessHelper_SeparatesOperationalReadAndEditFromAccountingFinancialRead()
    {
        var accountant = new Employee
        {
            Id = "acc-1",
            IsActive = true,
            Permissions = [AppPermissions.Finance.AccessAccounting, AppPermissions.Projects.View]
        };

        var restrictedProject = new Project
        {
            Id = "proj-secret",
            Name = "Confidential Acquisition",
            AccessPolicy = ProjectAccessPolicy.Restricted
        };

        Assert.False(ProjectAccessHelper.CanAccess(accountant, restrictedProject, _db.Teams, allowAccountingFinancialRead: false));
        Assert.True(ProjectAccessHelper.CanAccess(accountant, restrictedProject, _db.Teams, allowAccountingFinancialRead: true));
        Assert.False(ProjectAccessHelper.CanEditProject(accountant, restrictedProject, _db.Teams));
    }

    [Fact]
    public void BillingOrchestrationService_AllowsAccountingFinancialReadOnRestrictedProjects_AndExcludesDraftReports()
    {
        var restrictedProject = new Project
        {
            Id = "proj-restricted-bill",
            Name = "Restricted Client",
            AccessPolicy = ProjectAccessPolicy.Restricted
        };
        _db.Projects.Save(restrictedProject);

        var workType = new WorkType { Id = "wt-dev", Name = "Development", DefaultRate = 150m };
        _db.WorkTypes.Save(workType);
        var workTypes = new Dictionary<string, WorkType> { [workType.Id] = workType };

        var worker = new Employee { Id = "emp-worker", FirstName = "Worker", IsActive = true };
        var accountant = new Employee
        {
            Id = "emp-acc",
            FirstName = "Accountant",
            IsActive = true,
            Permissions = [AppPermissions.Finance.AccessAccounting]
        };
        var projectViewer = new Employee
        {
            Id = "emp-proj-viewer",
            FirstName = "Viewer",
            IsActive = true,
            Permissions = [AppPermissions.Projects.View]
        };
        _db.Employees.Save(worker);
        _db.Employees.Save(accountant);
        _db.Employees.Save(projectViewer);

        var draftInvoice = new Invoice
        {
            Id = "inv-target",
            ProjectId = restrictedProject.Id,
            Status = InvoiceStatus.Draft
        };

        var sheet = new Timesheet
        {
            Id = "ts-1",
            EmployeeId = worker.Id,
            Entries =
            [
                new TimeEntry
                {
                    Id = "te-1",
                    ProjectId = restrictedProject.Id,
                    WorkTypeId = workType.Id,
                    Date = DateTime.Today,
                    Hours = 3m,
                    IsBillable = true
                }
            ]
        };
        _db.Timesheets.Save(sheet);

        var draftReport = new ExpenseReport
        {
            Id = "er-draft",
            EmployeeId = worker.Id,
            EmployeeName = worker.FullName,
            DateSubmitted = null,
            LineItems =
            [
                new ExpenseItem
                {
                    Id = "exp-draft-1",
                    ProjectId = restrictedProject.Id,
                    Description = "Draft Lunch",
                    Amount = 40m
                }
            ]
        };
        var submittedReport = new ExpenseReport
        {
            Id = "er-sub",
            EmployeeId = worker.Id,
            EmployeeName = worker.FullName,
            DateSubmitted = DateTime.UtcNow,
            LineItems =
            [
                new ExpenseItem
                {
                    Id = "exp-sub-1",
                    ProjectId = restrictedProject.Id,
                    Description = "Submitted Travel",
                    Amount = 120m
                }
            ]
        };
        _db.ExpenseReports.Save(draftReport);
        _db.ExpenseReports.Save(submittedReport);

        var accLabor = _billing.LoadUnbilledLabor([restrictedProject], draftInvoice, workTypes, accountant);
        Assert.Single(accLabor);
        Assert.Equal(3m, accLabor[0].AvailableHours);

        var accExpenses = _billing.LoadUnbilledExpenses([restrictedProject], draftInvoice, 0.15m, accountant);
        Assert.Single(accExpenses);
        Assert.Equal("exp-sub-1", accExpenses[0].Id);

        Assert.Empty(_billing.LoadUnbilledLabor([restrictedProject], draftInvoice, workTypes, projectViewer));
        Assert.Empty(_billing.LoadUnbilledExpenses([restrictedProject], draftInvoice, 0.15m, projectViewer));
    }

    [Fact]
    public void ProcessAndSaveInvoice_RejectsCrossProjectInjection_DraftExpense_AndUnapprovedFinalization()
    {
        var projA = new Project { Id = "proj-a", Name = "Project A", AccessPolicy = ProjectAccessPolicy.Restricted };
        var projB = new Project { Id = "proj-b", Name = "Project B", AccessPolicy = ProjectAccessPolicy.Restricted };
        _db.Projects.Save(projA);
        _db.Projects.Save(projB);

        var wt = new WorkType { Id = "wt-1", Name = "Consulting", DefaultRate = 100m };
        _db.WorkTypes.Save(wt);

        var billingClerk = new Employee
        {
            Id = "emp-clerk",
            IsActive = true,
            Permissions = [AppPermissions.Finance.AccessAccounting]
        };
        var suspendedApprover = new Employee
        {
            Id = "emp-susp",
            IsActive = true,
            IsSuspended = true,
            IsAdmin = true
        };
        _db.Employees.Save(billingClerk);
        _db.Employees.Save(suspendedApprover);

        var foreignSheet = new Timesheet
        {
            Id = "ts-foreign",
            EmployeeId = "emp-worker",
            Entries =
            [
                new TimeEntry { Id = "te-b", ProjectId = projB.Id, WorkTypeId = wt.Id, Hours = 2m, IsBillable = true }
            ]
        };
        _db.Timesheets.Save(foreignSheet);

        var draftExpense = new ExpenseReport
        {
            Id = "er-draft-only",
            EmployeeId = "emp-worker",
            EmployeeName = "Worker",
            DateSubmitted = null,
            LineItems = [new ExpenseItem { Id = "exp-draft", ProjectId = projA.Id, Description = "Hidden Draft", Amount = 50m }]
        };
        _db.ExpenseReports.Save(draftExpense);

        // Suspended admin cannot save even draft invoices
        Assert.Throws<UnauthorizedAccessException>(() => _billing.ProcessAndSaveInvoice(new BillingSubmissionRequest
        {
            Actor = suspendedApprover,
            Invoice = new Invoice { Id = "inv-1", ProjectId = projA.Id, Status = InvoiceStatus.Draft }
        }));

        // Accounting clerk without ApproveInvoices cannot finalize an invoice
        Assert.Throws<UnauthorizedAccessException>(() => _billing.ProcessAndSaveInvoice(new BillingSubmissionRequest
        {
            Actor = billingClerk,
            Invoice = new Invoice { Id = "inv-2", ProjectId = projA.Id, Status = InvoiceStatus.Final }
        }));

        // Cross-project time entry injection fails
        Assert.Throws<UnauthorizedAccessException>(() => _billing.ProcessAndSaveInvoice(new BillingSubmissionRequest
        {
            Actor = billingClerk,
            Invoice = new Invoice { Id = "inv-3", ProjectId = projA.Id, Status = InvoiceStatus.Draft },
            LaborGroups =
            [
                new BillingLaborGroup
                {
                    ProjectId = projB.Id,
                    WorkTypeId = wt.Id,
                    HoursToBill = 2m,
                    Rate = 100m,
                    Entries = [new BillingLaborEntryRef { Entry = foreignSheet.Entries[0], ParentSheet = foreignSheet }]
                }
            ]
        }));

        // Draft expense report injection fails
        Assert.Throws<UnauthorizedAccessException>(() => _billing.ProcessAndSaveInvoice(new BillingSubmissionRequest
        {
            Actor = billingClerk,
            Invoice = new Invoice { Id = "inv-4", ProjectId = projA.Id, Status = InvoiceStatus.Draft },
            SelectedExpenses =
            [
                new BillingExpenseItem { Id = "exp-draft", ProjectId = projA.Id, Source = "ExpenseReport", UnitCost = 50m, Quantity = 1m }
            ]
        }));
    }

    [Fact]
    public void HRService_EnforcesRestrictedProjectAndBilledEntryAndSuspensionBoundaries()
    {
        var manager = new Employee
        {
            Id = "mgr-1",
            IsActive = true,
            FirstName = "Team",
            LastName = "Manager"
        };
        var suspendedAdmin = new Employee
        {
            Id = "admin-susp",
            IsActive = true,
            IsSuspended = true,
            IsAdmin = true
        };
        var worker = new Employee
        {
            Id = "worker-1",
            IsActive = true,
            TeamId = "team-1",
            FirstName = "Subordinate"
        };
        var team = new Team { Id = "team-1", Name = "Delivery", LeaderId = manager.Id };
        _db.Employees.Save(manager);
        _db.Employees.Save(suspendedAdmin);
        _db.Employees.Save(worker);
        _db.Teams.Save(team);

        var openProj = new Project { Id = "proj-open", Name = "Open", AccessPolicy = ProjectAccessPolicy.Public };
        var secretProj = new Project { Id = "proj-sec", Name = "Secret", AccessPolicy = ProjectAccessPolicy.Restricted };
        _db.Projects.Save(openProj);
        _db.Projects.Save(secretProj);

        var sheet = new Timesheet
        {
            Id = "ts-bulk-iso",
            EmployeeId = worker.Id,
            Status = TimesheetStatus.Approved,
            Entries =
            [
                new TimeEntry { Id = "te-secret", ProjectId = secretProj.Id, WorkTypeId = "wt-1", Hours = 4m },
                new TimeEntry { Id = "te-open-billed", ProjectId = openProj.Id, WorkTypeId = "wt-1", Hours = 2m }
            ]
        };
        _db.Timesheets.Save(sheet);

        var invoice = new Invoice
        {
            Id = "inv-billed",
            ProjectId = openProj.Id,
            Status = InvoiceStatus.Final,
            BilledTimeEntryIds = ["te-open-billed"]
        };
        _db.Invoices.Save(invoice);

        // Suspended admin cannot approve or bulk edit
        Assert.False(_hrService.CanOverrideTimesheetLock(suspendedAdmin, worker.Id));
        var approveSuspended = _hrService.ApproveTimesheet(sheet.Id, suspendedAdmin.Id);
        Assert.False(approveSuspended.IsSuccess);

        // Manager cannot edit time entries from a Restricted project they cannot access
        var secretResult = _hrService.BulkUpdateTimeEntries(new BulkTimesheetEditRequest
        {
            EntryIds = ["te-secret"],
            UpdateBillable = true,
            IsBillable = false
        }, manager.Id);
        Assert.False(secretResult.IsSuccess);
        Assert.Equal("FORBIDDEN", secretResult.Code);

        // Manager cannot move an open project entry into an inaccessible Restricted target project
        var targetSecretResult = _hrService.BulkUpdateTimeEntries(new BulkTimesheetEditRequest
        {
            EntryIds = ["te-open-billed"],
            ExcludedEntryIds = new HashSet<string>(StringComparer.Ordinal),
            UpdateProject = true,
            ProjectId = secretProj.Id
        }, manager.Id);
        Assert.False(targetSecretResult.IsSuccess);
        Assert.Equal("FORBIDDEN", targetSecretResult.Code);

        // Non-accounting manager cannot modify already-billed time entries when not excluded
        var billedEditResult = _hrService.BulkUpdateTimeEntries(new BulkTimesheetEditRequest
        {
            EntryIds = ["te-open-billed"],
            UpdateBillable = true,
            IsBillable = false
        }, manager.Id);
        Assert.False(billedEditResult.IsSuccess);
        Assert.Equal("FORBIDDEN", billedEditResult.Code);
    }
}
