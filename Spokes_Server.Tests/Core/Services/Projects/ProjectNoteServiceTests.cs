using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using Spokes_Server.Aggregate;
using Spokes_Server.Core.Models.Communication;
using Spokes_Server.Core.Models.Projects;
using Spokes_Server.Core.Services.Projects;
using Spokes_Server.Core.Services.Storage;
using Xunit;

namespace Spokes_Server.Tests.Core.Services.Projects;

public class ProjectNoteServiceTests : IDisposable
{
    private readonly string _testDataDir;
    private readonly ServiceProvider _serviceProvider;
    private readonly Database _db;
    private readonly Mock<IFileService> _mockFileService;
    private readonly ProjectNoteService _service;

    public ProjectNoteServiceTests()
    {
        _testDataDir = Path.Combine(Path.GetTempPath(), $"Spokes_Test_ProjectNotes_{Guid.NewGuid()}");
        Directory.CreateDirectory(_testDataDir);

        var services = new ServiceCollection();
        var mockConfig = new Mock<IConfiguration>();
        mockConfig.Setup(c => c["DataPath"]).Returns(_testDataDir);
        services.AddSingleton<IConfiguration>(mockConfig.Object);

        services.AddLogging(builder => builder.AddConsole());
        services.AddSingleton<EncryptionService>();
        services.AddSpokesDatabase();

        _mockFileService = new Mock<IFileService>();
        services.AddSingleton<IFileService>(_mockFileService.Object);

        services.AddSingleton<ProjectNoteService>();

        _serviceProvider = services.BuildServiceProvider();
        _db = _serviceProvider.GetRequiredService<Database>();
        _service = _serviceProvider.GetRequiredService<ProjectNoteService>();
    }

    public void Dispose()
    {
        _serviceProvider.Dispose();
        if (Directory.Exists(_testDataDir))
        {
            try { Directory.Delete(_testDataDir, true); } catch { }
        }
    }

    [Fact]
    public void SaveNote_And_GetNotesForProject_Works()
    {
        var note = new ProjectNote
        {
            Id = "note-1",
            ProjectId = "proj-1",
            Title = "Meeting notes",
            Content = "Discussed roadmap"
        };

        _service.SaveNote(note);

        var notes = _service.GetNotesForProject("proj-1");
        Assert.Single(notes);
        Assert.Equal("Meeting notes", notes[0].Title);

        var emptyNotes = _service.GetNotesForProject("proj-2");
        Assert.Empty(emptyNotes);
    }

    [Fact]
    public void DeleteNote_RemovesNote()
    {
        var note = new ProjectNote { Id = "note-del", ProjectId = "proj-1", Title = "To Delete" };
        _service.SaveNote(note);

        _service.DeleteNote("note-del");

        var notes = _service.GetNotesForProject("proj-1");
        Assert.Empty(notes);
    }

    [Fact]
    public void SearchActiveProjects_FiltersProperly()
    {
        var p1 = new Project { Id = "p-1", Name = "Alpha Robot", IsActive = true, CreatedAt = DateTime.UtcNow.AddDays(-1) };
        var p2 = new Project { Id = "p-2", Name = "Beta Drone", IsActive = true, CreatedAt = DateTime.UtcNow };
        var p3 = new Project { Id = "p-3", Name = "Inactive Alpha", IsActive = false, CreatedAt = DateTime.UtcNow };

        _db.Projects.Save(p1);
        _db.Projects.Save(p2);
        _db.Projects.Save(p3);

        var searchAlpha = _service.SearchActiveProjects("Alpha");
        Assert.Single(searchAlpha);
        Assert.Equal("Alpha Robot", searchAlpha[0].DisplayName);

        var allActive = _service.SearchActiveProjects("");
        Assert.Equal(2, allActive.Count);
        // Ordered by descending CreatedAt
        Assert.Equal("Beta Drone", allActive[0].DisplayName);
    }

    [Fact]
    public async Task AddEmailToTimelineAsync_WithRequestObject_CopiesFile_AndCreatesTimelineNote()
    {
        var project = new Project { Id = "p-timeline", Name = "Timeline Project", IsActive = true };
        _db.Projects.Save(project);

        var dummyEml = Path.Combine(_testDataDir, "test-email.eml");
        File.WriteAllText(dummyEml, "Subject: Test\r\n\r\nBody");

        _mockFileService
            .Setup(f => f.UploadStreamAsync("projectnotes", "p-timeline", It.IsAny<Stream>(), "msg-123.eml", null, null))
            .ReturnsAsync("/uploads/projectnotes/p-timeline/msg-123.eml");

        var request = new AddEmailTimelineEntryRequest
        {
            ProjectId = "p-timeline",
            MimeFilePath = dummyEml,
            EmailId = "msg-123",
            Title = "Important Update",
            Date = new DateTime(2026, 1, 1),
            UserNotes = "User comments here",
            FromName = "Alice",
            FromAddress = "alice@example.com",
            ToAddresses = ["bob@example.com"],
            Subject = "Update",
            CreatedBy = "Charlie"
        };

        var created = await _service.AddEmailToTimelineAsync(request);

        Assert.NotNull(created);
        Assert.Equal("Important Update", created.Title);
        Assert.Equal("/uploads/projectnotes/p-timeline/msg-123.eml", created.EmailHtmlPath);
        Assert.True(created.IsEmail);
        Assert.Equal("Email", created.Category);

        var updatedProject = _db.Projects.GetById("p-timeline");
        Assert.NotNull(updatedProject);
        Assert.Contains("Email", updatedProject.NoteCategories);
    }

    [Fact]
    public async Task AddEmailToTimelineAsync_WithEmailMessage_CopiesFile_AndCreatesTimelineNote()
    {
        var project = new Project { Id = "p-timeline-2", Name = "Timeline Project 2", IsActive = true };
        _db.Projects.Save(project);

        var dummyEml = Path.Combine(_testDataDir, "test-email-2.eml");
        File.WriteAllText(dummyEml, "Subject: Discussion\r\n\r\nBody");

        _mockFileService
            .Setup(f => f.UploadStreamAsync("projectnotes", "p-timeline-2", It.IsAny<Stream>(), "msg-456.eml", null, null))
            .ReturnsAsync("/uploads/projectnotes/p-timeline-2/msg-456.eml");

        var email = new EmailMessage
        {
            Id = "msg-456",
            FromName = "Dana",
            FromAddress = "dana@example.com",
            ToAddresses = ["ed@example.com"],
            Subject = "Discussion Topic"
        };

        var request = AddEmailTimelineEntryRequest.FromEmailMessage(
            projectId: "p-timeline-2",
            mimeFilePath: dummyEml,
            email: email,
            title: "Discussion Note",
            date: new DateTime(2026, 2, 1),
            userNotes: "Attached for reference",
            createdBy: "Frank");
        var created = await _service.AddEmailToTimelineAsync(request);

        Assert.NotNull(created);
        Assert.Equal("Discussion Note", created.Title);
        Assert.Equal("/uploads/projectnotes/p-timeline-2/msg-456.eml", created.EmailHtmlPath);
        Assert.True(created.IsEmail);
        Assert.Contains("Dana <dana@example.com>", created.Content);

        var updatedProject = _db.Projects.GetById("p-timeline-2");
        Assert.NotNull(updatedProject);
        Assert.Contains("Email", updatedProject.NoteCategories);
    }
}
