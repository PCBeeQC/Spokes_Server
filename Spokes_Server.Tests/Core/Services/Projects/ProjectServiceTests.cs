using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Spokes_Server.Aggregate;
using Spokes_Server.Core.Data;
using Spokes_Server.Core.Models.Accounting;
using Spokes_Server.Core.Models.Communication;
using Spokes_Server.Core.Models.HR;
using Spokes_Server.Core.Models.Projects;
using Spokes_Server.Core.Services.Projects;
using Spokes_Server.Core.Services.Storage;
using Xunit;

namespace Spokes_Server.Tests.Core.Services.Projects;

public class ProjectServiceTests : TestDataTestBase
{
    private readonly ServiceProvider _serviceProvider;
    private readonly Database _db;
    private readonly DiskPersistenceService _persistence;
    private readonly ProjectService _service;

    public ProjectServiceTests()
    {
        var services = new ServiceCollection();
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["DataPath"] = _testDataPath , ["SPOKES_MASTER_PASSWORD"] = "UnitTest-Master-Password-2026!"})
            .Build();
        services.AddSingleton<IConfiguration>(config);
        services.AddLogging();
        services.AddSpokesDatabase();
        services.AddSingleton(new Mock<IFileService>().Object);

        _serviceProvider = services.BuildServiceProvider();
        _db = _serviceProvider.GetRequiredService<Database>();
        _persistence = _serviceProvider.GetRequiredService<DiskPersistenceService>();
        _service = new ProjectService(_db, _persistence, config, NullLogger<ProjectService>.Instance);
    }

    public override void Dispose()
    {
        _serviceProvider.Dispose();
        base.Dispose();
    }

    [Fact]
    public void DeleteProject_WhenPostedInvoiceExists_BlocksDeletion()
    {
        var proj = new Project { Id = "proj-posted", Name = "Posted Project" };
        _db.Projects.Save(proj);
        _db.Invoices.Save(new Invoice { Id = "inv-final", ProjectId = proj.Id, Status = InvoiceStatus.Final });

        var result = _service.DeleteProject(proj.Id);

        Assert.False(result.IsSuccess);
        Assert.Equal("HAS_POSTED_INVOICES", result.Code);
        Assert.NotNull(_db.Projects.GetById(proj.Id));
    }

    [Fact]
    public void DeleteProject_WhenTimesheetEntriesExist_BlocksDeletion()
    {
        var proj = new Project { Id = "proj-ts", Name = "TS Project" };
        _db.Projects.Save(proj);
        _db.Timesheets.Save(new Timesheet
        {
            Id = "ts-1",
            EmployeeId = "emp-1",
            Year = 2026,
            WeekNumber = 10,
            Entries = [new TimeEntry { ProjectId = proj.Id, Hours = 4m }]
        });

        var result = _service.DeleteProject(proj.Id);

        Assert.False(result.IsSuccess);
        Assert.Equal("HAS_TIMESHEETS", result.Code);
        Assert.NotNull(_db.Projects.GetById(proj.Id));
    }

    [Fact]
    public async Task DeleteProject_CascadesOwnedSubEntitiesAndRemovesProjectDirectory()
    {
        var proj = new Project { Id = "proj-clean", Name = "Clean Project" };
        _db.Projects.Save(proj);
        _db.Quotes.Save(new Quote { Id = "q-1", ProjectId = proj.Id });
        _db.ProjectNotes.Save(new ProjectNote { Id = "n-1", ProjectId = proj.Id });
        _db.ProjectDocuments.Save(new ProjectDocument { Id = "d-1", ProjectId = proj.Id });
        _db.ProjectReports.Save(new ProjectReport { Id = "r-1", ProjectId = proj.Id });
        _db.Invoices.Save(new Invoice { Id = "inv-draft", ProjectId = proj.Id, Status = InvoiceStatus.Draft });
        _db.ChatChannels.Save(new ChatChannel { Id = "ch-proj", ChannelType = ChatChannelType.Project, LinkedEntityId = proj.Id });
        await _persistence.FlushAllAsync();

        var projectDir = Path.Combine(_testDataPath, "Projects", proj.Id);
        Assert.True(Directory.Exists(projectDir));

        var result = _service.DeleteProject(proj.Id);
        await _persistence.FlushAllAsync();

        Assert.True(result.IsSuccess);
        Assert.Null(_db.Projects.GetById(proj.Id));
        Assert.Empty(_db.Quotes.GetByProject(proj.Id));
        Assert.Empty(_db.ProjectNotes.GetByProject(proj.Id));
        Assert.Empty(_db.ProjectDocuments.GetByProject(proj.Id));
        Assert.Empty(_db.ProjectReports.GetByProject(proj.Id));
        Assert.Null(_db.Invoices.GetById("inv-draft"));
        Assert.Null(_db.ChatChannels.GetById("ch-proj"));
        Assert.False(Directory.Exists(projectDir));
    }
}
