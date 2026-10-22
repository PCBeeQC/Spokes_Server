using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using Spokes_Server.Aggregate;
using Spokes_Server.Core.Data.Repositories.Core;
using Spokes_Server.Core.Data.Repositories.HR;
using Spokes_Server.Core.Models.Accounting;
using Spokes_Server.Core.Models.Core;
using Spokes_Server.Core.Models.HR;
using Spokes_Server.Core.Models.Projects;
using Spokes_Server.Core.Data;
using Xunit;

namespace Spokes_Server.Tests.Aggregate;

public class DatabaseRelationshipCoordinatorTests : TestDataTestBase
{
    private readonly ServiceProvider _serviceProvider;
    private readonly Database _db;
    private readonly DatabaseRelationshipCoordinator _coordinator;

    public DatabaseRelationshipCoordinatorTests()
    {
        var services = new ServiceCollection();

        var mockConfig = new Mock<IConfiguration>();
        mockConfig.Setup(c => c["DataPath"]).Returns(_testDataPath);
        services.AddSingleton<IConfiguration>(mockConfig.Object);

        services.AddLogging(builder => builder.AddConsole());
        services.AddSingleton<EncryptionService>();
        services.AddSpokesDatabase();

        _serviceProvider = services.BuildServiceProvider();
        _db = _serviceProvider.GetRequiredService<Database>();
        _coordinator = _serviceProvider.GetRequiredService<DatabaseRelationshipCoordinator>();
        _coordinator.RegisterSubscriptions();
    }

    public override void Dispose()
    {
        _serviceProvider.Dispose();
        base.Dispose();
    }

    [Fact]
    public void Quotes_OnSaved_TriggersProjectUpdate()
    {
        var proj = new Project { Id = "proj-coord-q1", Name = "Quote Project", LastEdited = DateTime.UtcNow.AddDays(-2) };
        _db.Projects.Save(proj);
        proj.LastEdited = DateTime.UtcNow.AddDays(-2);

        var quote = new Quote { Id = "coord-q1", ProjectId = "proj-coord-q1", Title = "Estimate 1" };
        _db.Quotes.Save(quote);

        var updatedProj = _db.Projects.GetById("proj-coord-q1");
        Assert.NotNull(updatedProj);
        Assert.True(updatedProj.LastEdited > DateTime.UtcNow.AddMinutes(-1));
    }

    [Fact]
    public void Invoices_OnSaved_TriggersProjectUpdate()
    {
        var proj = new Project { Id = "proj-coord-inv1", Name = "Invoice Project", LastEdited = DateTime.UtcNow.AddDays(-2) };
        _db.Projects.Save(proj);
        proj.LastEdited = DateTime.UtcNow.AddDays(-2);

        var invoice = new Invoice { Id = "coord-inv1", ProjectId = "proj-coord-inv1", InvoiceNumber = "INV-001" };
        _db.Invoices.Save(invoice);

        var updatedProj = _db.Projects.GetById("proj-coord-inv1");
        Assert.NotNull(updatedProj);
        Assert.True(updatedProj.LastEdited > DateTime.UtcNow.AddMinutes(-1));
    }

    [Fact]
    public void ProjectNotes_OnSaved_TriggersProjectUpdate()
    {
        var proj = new Project { Id = "proj-coord-note1", Name = "Note Project", LastEdited = DateTime.UtcNow.AddDays(-2) };
        _db.Projects.Save(proj);
        proj.LastEdited = DateTime.UtcNow.AddDays(-2);

        var note = new ProjectNote { Id = "coord-note1", ProjectId = "proj-coord-note1", Title = "Meeting Note" };
        _db.ProjectNotes.Save(note);

        var updatedProj = _db.Projects.GetById("proj-coord-note1");
        Assert.NotNull(updatedProj);
        Assert.True(updatedProj.LastEdited > DateTime.UtcNow.AddMinutes(-1));
    }

    [Fact]
    public void ProjectDocuments_OnSaved_TriggersProjectUpdate()
    {
        var proj = new Project { Id = "proj-coord-doc1", Name = "Doc Project", LastEdited = DateTime.UtcNow.AddDays(-2) };
        _db.Projects.Save(proj);
        proj.LastEdited = DateTime.UtcNow.AddDays(-2);

        var doc = new ProjectDocument { Id = "coord-doc1", ProjectId = "proj-coord-doc1", Name = "Spec Doc" };
        _db.ProjectDocuments.Save(doc);

        var updatedProj = _db.Projects.GetById("proj-coord-doc1");
        Assert.NotNull(updatedProj);
        Assert.True(updatedProj.LastEdited > DateTime.UtcNow.AddMinutes(-1));
    }

    [Fact]
    public void Quotes_OnDeleted_TriggersProjectUpdate()
    {
        var proj = new Project { Id = "proj-coord-q-del", Name = "Quote Del Project", LastEdited = DateTime.UtcNow.AddDays(-2) };
        _db.Projects.Save(proj);
        var quote = new Quote { Id = "coord-q-del", ProjectId = "proj-coord-q-del", Title = "Estimate Del" };
        _db.Quotes.Save(quote);

        // Reset LastEdited to older date after quote save
        proj.LastEdited = DateTime.UtcNow.AddDays(-2);
        _db.Projects.Save(proj);
        var preDelDate = DateTime.UtcNow.AddDays(-2);
        proj.LastEdited = preDelDate;

        _db.Quotes.Delete("coord-q-del");

        var updatedProj = _db.Projects.GetById("proj-coord-q-del");
        Assert.NotNull(updatedProj);
        Assert.True(updatedProj.LastEdited > DateTime.UtcNow.AddMinutes(-1));
    }

    [Fact]
    public void Invoices_OnDeleted_TriggersProjectUpdate()
    {
        var proj = new Project { Id = "proj-coord-inv-del", Name = "Invoice Del Project", LastEdited = DateTime.UtcNow.AddDays(-2) };
        _db.Projects.Save(proj);
        var invoice = new Invoice { Id = "coord-inv-del", ProjectId = "proj-coord-inv-del", InvoiceNumber = "INV-DEL" };
        _db.Invoices.Save(invoice);

        // Reset LastEdited to older date after invoice save
        proj.LastEdited = DateTime.UtcNow.AddDays(-2);
        _db.Projects.Save(proj);
        proj.LastEdited = DateTime.UtcNow.AddDays(-2);

        _db.Invoices.Delete("coord-inv-del");

        var updatedProj = _db.Projects.GetById("proj-coord-inv-del");
        Assert.NotNull(updatedProj);
        Assert.True(updatedProj.LastEdited > DateTime.UtcNow.AddMinutes(-1));
    }

    [Fact]
    public void ProjectNotes_OnDeleted_TriggersProjectUpdate()
    {
        var proj = new Project { Id = "proj-coord-note-del", Name = "Note Del Project", LastEdited = DateTime.UtcNow.AddDays(-2) };
        _db.Projects.Save(proj);
        var note = new ProjectNote { Id = "coord-note-del", ProjectId = "proj-coord-note-del", Title = "Note Del" };
        _db.ProjectNotes.Save(note);

        // Reset LastEdited to older date after note save
        proj.LastEdited = DateTime.UtcNow.AddDays(-2);
        _db.Projects.Save(proj);
        proj.LastEdited = DateTime.UtcNow.AddDays(-2);

        _db.ProjectNotes.Delete("coord-note-del");

        var updatedProj = _db.Projects.GetById("proj-coord-note-del");
        Assert.NotNull(updatedProj);
        Assert.True(updatedProj.LastEdited > DateTime.UtcNow.AddMinutes(-1));
    }

    [Fact]
    public void ProjectDocuments_OnDeleted_TriggersProjectUpdate()
    {
        var proj = new Project { Id = "proj-coord-doc-del", Name = "Doc Del Project", LastEdited = DateTime.UtcNow.AddDays(-2) };
        _db.Projects.Save(proj);
        var doc = new ProjectDocument { Id = "coord-doc-del", ProjectId = "proj-coord-doc-del", Name = "Doc Del" };
        _db.ProjectDocuments.Save(doc);

        // Reset LastEdited to older date after doc save
        proj.LastEdited = DateTime.UtcNow.AddDays(-2);
        _db.Projects.Save(proj);
        proj.LastEdited = DateTime.UtcNow.AddDays(-2);

        _db.ProjectDocuments.Delete("coord-doc-del");

        var updatedProj = _db.Projects.GetById("proj-coord-doc-del");
        Assert.NotNull(updatedProj);
        Assert.True(updatedProj.LastEdited > DateTime.UtcNow.AddMinutes(-1));
    }

    [Fact]
    public void Timesheets_OnSaved_DoesNotTriggerProjectUpdate()
    {
        var originalTime1 = DateTime.UtcNow.AddDays(-5);
        var originalTime2 = DateTime.UtcNow.AddDays(-4);
        var proj1 = new Project { Id = "proj-coord-ts1", Name = "TS Project 1", LastEdited = originalTime1 };
        var proj2 = new Project { Id = "proj-coord-ts2", Name = "TS Project 2", LastEdited = originalTime2 };
        _db.Projects.Save(proj1);
        _db.Projects.Save(proj2);
        proj1.LastEdited = originalTime1;
        proj2.LastEdited = originalTime2;

        var ts = new Timesheet
        {
            Id = "coord-ts1",
            EmployeeId = "emp-ts",
            Entries = new List<TimeEntry>
            {
                new TimeEntry { ProjectId = "proj-coord-ts1", Hours = 4 },
                new TimeEntry { ProjectId = "proj-coord-ts2", Hours = 4 }
            }
        };
        _db.Timesheets.Save(ts);

        var updated1 = _db.Projects.GetById("proj-coord-ts1");
        var updated2 = _db.Projects.GetById("proj-coord-ts2");

        Assert.NotNull(updated1);
        Assert.NotNull(updated2);
        Assert.Equal(originalTime1, updated1.LastEdited);
        Assert.Equal(originalTime2, updated2.LastEdited);
    }

    [Fact]
    public void CompanyProfile_OnSaved_UpdatesPermissionGroupReferences_OnAllMatchingEmployees()
    {
        var profile = _db.CompanyProfile.Get();
        var pg1 = new PermissionGroup { Id = "pg-coord-1", Name = "Original Name" };
        profile.PermissionGroups = new List<PermissionGroup> { pg1 };
        _db.CompanyProfile.Save(profile);

        var emp = new Employee
        {
            Id = "emp-coord-1",
            FirstName = "Dave",
            PermissionGroupId = "pg-coord-1"
        };
        _db.Employees.Save(emp);

        // Update PermissionGroup name in CompanyProfile
        pg1.Name = "Updated Group Name";
        _db.CompanyProfile.Save(profile);

        var updatedEmp = _db.Employees.GetById("emp-coord-1");
        Assert.NotNull(updatedEmp);
        Assert.NotNull(updatedEmp.PermissionGroup);
        Assert.Equal("Updated Group Name", updatedEmp.PermissionGroup.Name);
    }

    [Fact]
    public void Employees_OnSaved_WhenPermissionGroupIdMatchesProfile_LinksPermissionGroup()
    {
        var profile = _db.CompanyProfile.Get();
        var pg = new PermissionGroup { Id = "pg-coord-role", Name = "Engineering" };
        profile.PermissionGroups = new List<PermissionGroup> { pg };
        _db.CompanyProfile.Save(profile);

        var emp = new Employee
        {
            Id = "emp-coord-2",
            FirstName = "Eve",
            PermissionGroupId = "pg-coord-role"
        };
        _db.Employees.Save(emp);

        var fetchedEmp = _db.Employees.GetById("emp-coord-2");
        Assert.NotNull(fetchedEmp);
        Assert.NotNull(fetchedEmp.PermissionGroup);
        Assert.Equal("Engineering", fetchedEmp.PermissionGroup.Name);
    }

    [Fact]
    public void Employees_OnSaved_WhenPermissionGroupIdCleared_SetsPermissionGroupToNull()
    {
        var profile = _db.CompanyProfile.Get();
        var pg = new PermissionGroup { Id = "pg-coord-role2", Name = "Sales" };
        profile.PermissionGroups = new List<PermissionGroup> { pg };
        _db.CompanyProfile.Save(profile);

        var emp = new Employee
        {
            Id = "emp-coord-3",
            FirstName = "Frank",
            PermissionGroupId = "pg-coord-role2"
        };
        _db.Employees.Save(emp);
        Assert.NotNull(_db.Employees.GetById("emp-coord-3")?.PermissionGroup);

        emp.PermissionGroupId = null;
        _db.Employees.Save(emp);

        var clearedEmp = _db.Employees.GetById("emp-coord-3");
        Assert.NotNull(clearedEmp);
        Assert.Null(clearedEmp.PermissionGroup);
    }

    [Fact]
    public async Task StartAsync_And_IdempotentRegisterSubscriptions_WorksCorrectly()
    {
        // Calling RegisterSubscriptions multiple times is a no-op
        _coordinator.RegisterSubscriptions();
        _coordinator.RegisterSubscriptions();

        // StartAsync can be awaited cleanly
        await _coordinator.StartAsync(CancellationToken.None);
        await _coordinator.StopAsync(CancellationToken.None);
    }
}
