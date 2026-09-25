using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using Spokes_Server.Aggregate;
using Spokes_Server.Core.Data;
using Spokes_Server.Core.Models.Accounting;
using Spokes_Server.Core.Models.Projects;
using Spokes_Server.Core.Services.Core;
using Spokes_Server.Core.Services.Migrations;

namespace Spokes_Server.Tests.Core.Services.Migrations;

public class LegacyAttachmentMigrationServiceTests : TestDataTestBase
{
    private readonly ServiceProvider _serviceProvider;
    private readonly Database _db;
    private readonly Mock<IFileService> _mockFileService;
    private readonly Mock<ILogger<LegacyAttachmentMigrationService>> _mockLogger;
    private readonly LegacyAttachmentMigrationService _service;

    public LegacyAttachmentMigrationServiceTests()
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

        _mockFileService = new Mock<IFileService>();
        _mockLogger = new Mock<ILogger<LegacyAttachmentMigrationService>>();

        _service = new LegacyAttachmentMigrationService(_db, _mockFileService.Object, _mockLogger.Object);
    }

    public override void Dispose()
    {
        _serviceProvider.Dispose();
        base.Dispose();
    }

    [Fact]
    public void Constructor_WithValidDependencies_InitializesCorrectly()
    {
        Assert.NotNull(_service);
    }

    [Fact]
    public async Task MigrateLegacyBase64AttachmentsAsync_MigratesProjectNoteBase64Attachment()
    {
        // Arrange
        const string base64Data = "data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg==";
        const string expectedNewUrl = "/spokesapi/files/projects/p1/screenshot.png";

        var note = new ProjectNote
        {
            Id = "note-p1",
            ProjectId = "p1",
            Title = "Note With Base64",
            Attachments =
            [
                new NoteAttachment
                {
                    Id = "att-1",
                    FileName = "screenshot.png",
                    FilePath = base64Data
                }
            ]
        };
        _db.ProjectNotes.Save(note);

        _mockFileService
            .Setup(f => f.UploadStreamAsync("projects", "p1", It.IsAny<Stream>(), "screenshot.png", It.IsAny<string?>(), It.IsAny<string?>()))
            .ReturnsAsync(expectedNewUrl);

        // Act
        await _service.MigrateLegacyBase64AttachmentsAsync();

        // Assert
        var updatedNote = _db.ProjectNotes.GetById("note-p1");
        Assert.NotNull(updatedNote);
        Assert.Single(updatedNote.Attachments);
        Assert.Equal(expectedNewUrl, updatedNote.Attachments[0].FilePath);

        _mockFileService.Verify(
            f => f.UploadStreamAsync("projects", "p1", It.IsAny<Stream>(), "screenshot.png", It.IsAny<string?>(), It.IsAny<string?>()),
            Times.Once);
    }

    [Fact]
    public async Task MigrateLegacyBase64AttachmentsAsync_MigratesBillBase64Attachment()
    {
        // Arrange
        const string base64Data = "data:application/pdf;base64,cGRmZGF0YQ==";
        const string expectedNewUrl = "/spokesapi/files/bills/b1/invoice.pdf";

        var bill = new Bill
        {
            Id = "b1",
            AttachmentName = "invoice.pdf",
            AttachmentPath = base64Data
        };
        _db.Bills.Save(bill);

        _mockFileService
            .Setup(f => f.UploadStreamAsync("bills", "b1", It.IsAny<Stream>(), "invoice.pdf", It.IsAny<string?>(), It.IsAny<string?>()))
            .ReturnsAsync(expectedNewUrl);

        // Act
        await _service.MigrateLegacyBase64AttachmentsAsync();

        // Assert
        var updatedBill = _db.Bills.GetById("b1");
        Assert.NotNull(updatedBill);
        Assert.Equal(expectedNewUrl, updatedBill.AttachmentPath);

        _mockFileService.Verify(
            f => f.UploadStreamAsync("bills", "b1", It.IsAny<Stream>(), "invoice.pdf", It.IsAny<string?>(), It.IsAny<string?>()),
            Times.Once);
    }

    [Fact]
    public async Task MigrateLegacyBase64AttachmentsAsync_SkipsNonLegacyAttachments()
    {
        // Arrange
        var note = new ProjectNote
        {
            Id = "note-nonlegacy",
            ProjectId = "p2",
            Attachments =
            [
                new NoteAttachment
                {
                    Id = "att-existing",
                    FileName = "existing.png",
                    FilePath = "/spokesapi/files/existing.png"
                },
                new NoteAttachment
                {
                    Id = "att-empty",
                    FileName = "empty.png",
                    FilePath = ""
                },
                new NoteAttachment
                {
                    Id = "att-whitespace",
                    FileName = "whitespace.png",
                    FilePath = "   "
                },
                new NoteAttachment
                {
                    Id = "att-relpath",
                    FileName = "file.png",
                    FilePath = "Uploads/Projects/file.png"
                }
            ]
        };
        _db.ProjectNotes.Save(note);

        var bill = new Bill
        {
            Id = "bill-nonlegacy",
            AttachmentName = "existing.pdf",
            AttachmentPath = "/spokesapi/files/bills/existing.pdf"
        };
        _db.Bills.Save(bill);

        var billEmptyPath = new Bill
        {
            Id = "bill-empty",
            AttachmentName = "empty.pdf",
            AttachmentPath = ""
        };
        _db.Bills.Save(billEmptyPath);

        // Act
        await _service.MigrateLegacyBase64AttachmentsAsync();

        // Assert
        _mockFileService.Verify(
            f => f.UploadStreamAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<Stream>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>()),
            Times.Never);

        var updatedNote = _db.ProjectNotes.GetById("note-nonlegacy");
        Assert.NotNull(updatedNote);
        Assert.Equal("/spokesapi/files/existing.png", updatedNote.Attachments[0].FilePath);
        Assert.Equal("", updatedNote.Attachments[1].FilePath);
        Assert.Equal("   ", updatedNote.Attachments[2].FilePath);
        Assert.Equal("Uploads/Projects/file.png", updatedNote.Attachments[3].FilePath);

        var updatedBill = _db.Bills.GetById("bill-nonlegacy");
        Assert.NotNull(updatedBill);
        Assert.Equal("/spokesapi/files/bills/existing.pdf", updatedBill.AttachmentPath);
    }

    [Fact]
    public async Task MigrateLegacyBase64AttachmentsAsync_HandlesFallbackFileNameAndExtensions()
    {
        // Arrange
        // 1. Bill with empty AttachmentName and data URI data:text/plain;base64,dGVzdA==
        var bill = new Bill
        {
            Id = "bill-fallback-1",
            AttachmentName = "",
            AttachmentPath = "data:text/plain;base64,dGVzdA=="
        };
        _db.Bills.Save(bill);

        // 2. ProjectNote with empty FileName and data:text/plain;base64,dGVzdA==
        var noteEmptyName = new ProjectNote
        {
            Id = "note-fallback-txt",
            ProjectId = "p-txt",
            Attachments =
            [
                new NoteAttachment
                {
                    Id = "att-empty-name",
                    FileName = "",
                    FilePath = "data:text/plain;base64,dGVzdA=="
                }
            ]
        };
        _db.ProjectNotes.Save(noteEmptyName);

        // 3. ProjectNote with FileName missing extension and data:image/jpeg;base64,/9j/4AAQSkZJRg==
        var noteMissingExt = new ProjectNote
        {
            Id = "note-fallback-jpg",
            ProjectId = "p-jpg",
            Attachments =
            [
                new NoteAttachment
                {
                    Id = "att-no-ext",
                    FileName = "scanned_document",
                    FilePath = "data:image/jpeg;base64,/9j/4AAQSkZJRg=="
                }
            ]
        };
        _db.ProjectNotes.Save(noteMissingExt);

        // 4. ProjectNote with empty FileName and unrecognized mime data:application/octet-stream;base64,dGVzdA==
        var noteBinExt = new ProjectNote
        {
            Id = "note-fallback-bin",
            ProjectId = "p-bin",
            Attachments =
            [
                new NoteAttachment
                {
                    Id = "att-bin",
                    FileName = "",
                    FilePath = "data:application/octet-stream;base64,dGVzdA=="
                }
            ]
        };
        _db.ProjectNotes.Save(noteBinExt);

        _mockFileService
            .Setup(f => f.UploadStreamAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<Stream>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>()))
            .ReturnsAsync((string cat, string ctx, Stream s, string name, string? k, string? p) => $"/spokesapi/files/{cat}/{ctx}/{name}");

        // Act
        await _service.MigrateLegacyBase64AttachmentsAsync();

        // Assert
        // Verify Bill fallback name: $"bill_invoice_{bill.Id}.pdf"
        _mockFileService.Verify(
            f => f.UploadStreamAsync("bills", "bill-fallback-1", It.IsAny<Stream>(), $"bill_invoice_{bill.Id}.pdf", It.IsAny<string?>(), It.IsAny<string?>()),
            Times.Once);

        // Verify ProjectNote empty file name fallback: "migrated_file.txt"
        _mockFileService.Verify(
            f => f.UploadStreamAsync("projects", "p-txt", It.IsAny<Stream>(), "migrated_file.txt", It.IsAny<string?>(), It.IsAny<string?>()),
            Times.Once);

        // Verify ProjectNote filename appended extension: "scanned_document.jpg"
        _mockFileService.Verify(
            f => f.UploadStreamAsync("projects", "p-jpg", It.IsAny<Stream>(), "scanned_document.jpg", It.IsAny<string?>(), It.IsAny<string?>()),
            Times.Once);

        // Verify ProjectNote unrecognized mime fallback: "migrated_file.bin"
        _mockFileService.Verify(
            f => f.UploadStreamAsync("projects", "p-bin", It.IsAny<Stream>(), "migrated_file.bin", It.IsAny<string?>(), It.IsAny<string?>()),
            Times.Once);
    }

    [Fact]
    public async Task MigrateLegacyBase64AttachmentsAsync_WhenUploadFails_LogsErrorAndContinues()
    {
        // Arrange
        var note = new ProjectNote
        {
            Id = "note-fail",
            ProjectId = "p-fail",
            Attachments =
            [
                new NoteAttachment
                {
                    Id = "att-fail",
                    FileName = "fail.png",
                    FilePath = "data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg=="
                }
            ]
        };
        _db.ProjectNotes.Save(note);

        var bill = new Bill
        {
            Id = "bill-fail",
            AttachmentName = "fail.pdf",
            AttachmentPath = "data:application/pdf;base64,cGRmZGF0YQ=="
        };
        _db.Bills.Save(bill);

        _mockFileService
            .Setup(f => f.UploadStreamAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<Stream>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>()))
            .ThrowsAsync(new IOException("Disk error"));

        // Act
        var exception = await Record.ExceptionAsync(() => _service.MigrateLegacyBase64AttachmentsAsync());

        // Assert
        Assert.Null(exception);

        _mockLogger.Verify(
            x => x.Log(
                LogLevel.Error,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, t) => v.ToString()!.Contains("Failed to migrate attachment fail.png in ProjectNote note-fail")),
                It.IsAny<Exception>(),
                It.Is<Func<It.IsAnyType, Exception?, string>>((v, t) => true)),
            Times.Once);

        _mockLogger.Verify(
            x => x.Log(
                LogLevel.Error,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, t) => v.ToString()!.Contains("Failed to migrate attachment in Bill bill-fail")),
                It.IsAny<Exception>(),
                It.Is<Func<It.IsAnyType, Exception?, string>>((v, t) => true)),
            Times.Once);

        // Verify entities were not updated to null or corrupted
        var untouchedNote = _db.ProjectNotes.GetById("note-fail");
        Assert.NotNull(untouchedNote);
        Assert.StartsWith("data:image/png;base64,", untouchedNote.Attachments[0].FilePath);

        var untouchedBill = _db.Bills.GetById("bill-fail");
        Assert.NotNull(untouchedBill);
        Assert.Equal("data:application/pdf;base64,cGRmZGF0YQ==", untouchedBill.AttachmentPath);
    }

    [Fact]
    public async Task MigrateLegacyBase64AttachmentsAsync_WhenBase64Malformed_LogsErrorAndContinues()
    {
        // Arrange
        var note = new ProjectNote
        {
            Id = "note-corrupt",
            ProjectId = "p-corrupt",
            Attachments =
            [
                new NoteAttachment
                {
                    Id = "att-corrupt",
                    FileName = "corrupt.png",
                    FilePath = "data:image/png;base64,!!!NotAValidBase64String!!!"
                }
            ]
        };
        _db.ProjectNotes.Save(note);

        // Act
        await _service.MigrateLegacyBase64AttachmentsAsync();

        // Assert - Error logged, file service not called with upload
        _mockFileService.Verify(
            f => f.UploadStreamAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<Stream>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>()),
            Times.Never);

        _mockLogger.Verify(
            x => x.Log(
                LogLevel.Error,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, t) => v.ToString()!.Contains("Failed to migrate attachment corrupt.png in ProjectNote note-corrupt")),
                It.IsAny<Exception>(),
                It.Is<Func<It.IsAnyType, Exception?, string>>((v, t) => true)),
            Times.Once);

        var noteAfter = _db.ProjectNotes.GetById("note-corrupt");
        Assert.NotNull(noteAfter);
        Assert.Equal("data:image/png;base64,!!!NotAValidBase64String!!!", noteAfter.Attachments[0].FilePath);
    }

    [Fact]
    public async Task MigrateLegacyBase64AttachmentsAsync_WhenUploadReturnsNullOrEmpty_DoesNotSaveEntity()
    {
        // Arrange
        var note = new ProjectNote
        {
            Id = "note-empty-result",
            ProjectId = "p-empty",
            Attachments =
            [
                new NoteAttachment
                {
                    Id = "att-empty-res",
                    FileName = "photo.png",
                    FilePath = "data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg=="
                }
            ]
        };
        _db.ProjectNotes.Save(note);

        var bill = new Bill
        {
            Id = "bill-empty-result",
            AttachmentName = "doc.pdf",
            AttachmentPath = "data:application/pdf;base64,cGRmZGF0YQ=="
        };
        _db.Bills.Save(bill);

        // Upload returns null/empty
        _mockFileService
            .Setup(f => f.UploadStreamAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<Stream>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>()))
            .ReturnsAsync(string.Empty);

        // Act
        await _service.MigrateLegacyBase64AttachmentsAsync();

        // Assert - Paths should remain unchanged
        var noteAfter = _db.ProjectNotes.GetById("note-empty-result");
        Assert.NotNull(noteAfter);
        Assert.StartsWith("data:image/png;base64,", noteAfter.Attachments[0].FilePath);

        var billAfter = _db.Bills.GetById("bill-empty-result");
        Assert.NotNull(billAfter);
        Assert.Equal("data:application/pdf;base64,cGRmZGF0YQ==", billAfter.AttachmentPath);
    }

    [Fact]
    public async Task MigrateLegacyBase64AttachmentsAsync_ProjectNoteWithNullOrEmptyAttachments_SkipsCleanly()
    {
        // Arrange
        var noteEmpty = new ProjectNote
        {
            Id = "note-empty-list",
            ProjectId = "p-empty-list",
            Attachments = []
        };
        _db.ProjectNotes.Save(noteEmpty);

        var noteNull = new ProjectNote
        {
            Id = "note-null-list",
            ProjectId = "p-null-list",
            Attachments = null!
        };
        _db.ProjectNotes.Save(noteNull);

        // Act & Assert
        var exception = await Record.ExceptionAsync(() => _service.MigrateLegacyBase64AttachmentsAsync());
        Assert.Null(exception);

        _mockFileService.Verify(
            f => f.UploadStreamAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<Stream>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>()),
            Times.Never);
    }

    [Fact]
    public async Task MigrateLegacyBase64AttachmentsAsync_MultipleDataUriSeparators_ReturnsNullAndDoesNotUpdate()
    {
        // Arrange - multiple ;base64, tokens means parts.Length > 2, which MigrateBase64StringAsync rejects
        var note = new ProjectNote
        {
            Id = "note-multi-part",
            ProjectId = "p-multi",
            Attachments =
            [
                new NoteAttachment
                {
                    Id = "att-multi",
                    FileName = "multi.png",
                    FilePath = "data:image/png;base64,extra;base64,dGVzdA=="
                }
            ]
        };
        _db.ProjectNotes.Save(note);

        // Act
        await _service.MigrateLegacyBase64AttachmentsAsync();

        // Assert
        _mockFileService.Verify(
            f => f.UploadStreamAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<Stream>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>()),
            Times.Never);

        var noteAfter = _db.ProjectNotes.GetById("note-multi-part");
        Assert.NotNull(noteAfter);
        Assert.Equal("data:image/png;base64,extra;base64,dGVzdA==", noteAfter.Attachments[0].FilePath);
    }
}
