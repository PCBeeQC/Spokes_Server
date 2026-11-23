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
using Spokes_Server.Core.Constants;
using Spokes_Server.Core.Exceptions;
using Spokes_Server.Core.Models.Communication;
using Spokes_Server.Core.Models.HR;
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
        mockConfig.Setup(c => c["SPOKES_MASTER_PASSWORD"]).Returns("UnitTest-Master-Password-2026!");
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

    private static Employee CreateEditor(string id = "editor-1") => new()
    {
        Id = id,
        FirstName = "Editor",
        LastName = "User",
        IsActive = true,
        Permissions = [AppPermissions.Projects.View, AppPermissions.Projects.Edit]
    };

    [Fact]
    public void SearchActiveProjects_WhenActorNullSuspendedOrRestrictedNonMember_ReturnsEmpty()
    {
        var restricted = new Project
        {
            Id = "proj-restricted-note",
            Name = "Classified Build",
            IsActive = true,
            AccessPolicy = ProjectAccessPolicy.Restricted,
            AllowedUserIds = ["allowed-only"]
        };
        _db.Projects.Save(restricted);

        var suspendedEditor = CreateEditor("suspended-ed");
        suspendedEditor.IsSuspended = true;

        var nonMemberEditor = CreateEditor("non-member-ed");

        Assert.Empty(_service.SearchActiveProjects("Classified", actor: null));
        Assert.Empty(_service.SearchActiveProjects("Classified", actor: suspendedEditor));
        Assert.Empty(_service.SearchActiveProjects("Classified", actor: nonMemberEditor));
    }

    [Fact]
    public async Task AddEmailToTimelineAsync_WhenActorUnauthorizedOrRestrictedNonMember_ThrowsAccessDenied()
    {
        var restricted = new Project
        {
            Id = "proj-restricted-tl",
            Name = "Secret Timeline",
            IsActive = true,
            AccessPolicy = ProjectAccessPolicy.Restricted,
            AllowedUserIds = ["allowed-only"]
        };
        _db.Projects.Save(restricted);

        var emlPath = Path.Combine(_testDataDir, "secret.eml");
        await File.WriteAllTextAsync(emlPath, "Subject: Secret");

        var request = new AddEmailTimelineEntryRequest
        {
            ProjectId = restricted.Id,
            EmailId = "email-secret",
            MimeFilePath = emlPath,
            Title = "Email: Subject",
            FromName = "Sender",
            FromAddress = "sender@example.com",
            ToAddresses = ["to@example.com"],
            Date = DateTime.Today,
            Subject = "Subject",
            UserNotes = "Notes",
            CreatedBy = "Employee One"
        };

        await Assert.ThrowsAsync<DomainException>(() => _service.AddEmailToTimelineAsync(request, actor: null));

        var nonMember = CreateEditor("outsider");
        var ex = await Assert.ThrowsAsync<DomainException>(() => _service.AddEmailToTimelineAsync(request, actor: nonMember));
        Assert.Equal("ACCESS_DENIED", ex.ErrorCode);
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

        var editor = CreateEditor();
        var searchAlpha = _service.SearchActiveProjects("Alpha", editor);
        Assert.Single(searchAlpha);
        Assert.Equal("Alpha Robot", searchAlpha[0].DisplayName);

        var allActive = _service.SearchActiveProjects("", editor);
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

        var created = await _service.AddEmailToTimelineAsync(request, CreateEditor());

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
        var created = await _service.AddEmailToTimelineAsync(request, CreateEditor());

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
