using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Spokes_Server.Aggregate;
using Spokes_Server.Core.Data;
using Spokes_Server.Core.Models.Projects;
using Spokes_Server.Core.Services.Maintenance;
using Spokes_Server.Core.Services.Storage;
using Xunit;

namespace Spokes_Server.Tests.Core.Services.Maintenance;

public class StorageMaintenanceServiceTests : TestDataTestBase
{
    private readonly ServiceProvider _serviceProvider;
    private readonly Database _db;
    private readonly StorageMaintenanceService _maintenanceService;
    private readonly string _uploadsDir;

    public StorageMaintenanceServiceTests()
    {
        var services = new ServiceCollection();

        var configDict = new Dictionary<string, string?> { ["DataPath"] = _testDataPath };
        var config = new ConfigurationBuilder().AddInMemoryCollection(configDict).Build();
        services.AddSingleton<IConfiguration>(config);
        services.AddLogging();
        services.AddSpokesDatabase();

        var mockFileService = new Mock<IFileService>();
        services.AddSingleton(mockFileService.Object);

        _serviceProvider = services.BuildServiceProvider();
        _db = _serviceProvider.GetRequiredService<Database>();

        _uploadsDir = Path.Combine(_testDataPath, "Uploads");
        Directory.CreateDirectory(_uploadsDir);

        _maintenanceService = new StorageMaintenanceService(
            _db,
            mockFileService.Object,
            config,
            NullLogger<StorageMaintenanceService>.Instance);
    }

    public override void Dispose()
    {
        _serviceProvider.Dispose();
        base.Dispose();
    }

    [Fact]
    public async Task PurgeOrphanedUploadsAsync_WhenNoFiles_ReturnsZero()
    {
        var result = await _maintenanceService.PurgeOrphanedUploadsAsync(TimeSpan.Zero);

        Assert.Equal(0, result.FilesScanned);
        Assert.Equal(0, result.OrphansDeleted);
        Assert.Equal(0, result.BytesReclaimed);
    }

    [Fact]
    public async Task PurgeOrphanedUploadsAsync_WhenOrphanExistsPastSafetyWindow_DeletesOrphan()
    {
        var orphanFolder = Path.Combine(_uploadsDir, "notes", "n1");
        Directory.CreateDirectory(orphanFolder);
        var orphanFile = Path.Combine(orphanFolder, "orphan.png");
        File.WriteAllText(orphanFile, "abandoned content");
        File.SetLastWriteTimeUtc(orphanFile, DateTime.UtcNow.AddMinutes(-30));

        var result = await _maintenanceService.PurgeOrphanedUploadsAsync(TimeSpan.FromMinutes(15));

        Assert.Equal(1, result.FilesScanned);
        Assert.Equal(1, result.OrphansDeleted);
        Assert.True(result.BytesReclaimed > 0);
        Assert.False(File.Exists(orphanFile));
        Assert.False(Directory.Exists(orphanFolder)); // Folder pruned
    }

    [Fact]
    public async Task PurgeOrphanedUploadsAsync_WhenFileIsActiveReference_PreservesFile()
    {
        var noteFolder = Path.Combine(_uploadsDir, "notes", "note-123");
        Directory.CreateDirectory(noteFolder);
        var activeFile = Path.Combine(noteFolder, "design.pdf");
        File.WriteAllText(activeFile, "active document content");
        File.SetLastWriteTimeUtc(activeFile, DateTime.UtcNow.AddHours(-1));

        // Register active reference in Database
        var note = new ProjectNote
        {
            Id = "note-123",
            ProjectId = "proj-1",
            Attachments =
            [
                new NoteAttachment
                {
                    FileName = "design.pdf",
                    FilePath = "/spokesapi/files/notes/note-123/design.pdf",
                    FileSizeBytes = 100
                }
            ]
        };
        _db.ProjectNotes.Save(note);

        var result = await _maintenanceService.PurgeOrphanedUploadsAsync(TimeSpan.FromMinutes(15));

        Assert.Equal(1, result.FilesScanned);
        Assert.Equal(0, result.OrphansDeleted);
        Assert.True(File.Exists(activeFile));
        Assert.True(Directory.Exists(noteFolder));
    }

    [Fact]
    public async Task PurgeOrphanedUploadsAsync_WhenFileIsWithinSafetyWindow_PreservesFile()
    {
        var noteFolder = Path.Combine(_uploadsDir, "notes", "in-flight");
        Directory.CreateDirectory(noteFolder);
        var inFlightFile = Path.Combine(noteFolder, "uploading.bin");
        File.WriteAllText(inFlightFile, "in-flight content");
        File.SetLastWriteTimeUtc(inFlightFile, DateTime.UtcNow.AddMinutes(-5)); // Modified 5 mins ago

        var result = await _maintenanceService.PurgeOrphanedUploadsAsync(TimeSpan.FromMinutes(15));

        Assert.Equal(1, result.FilesScanned);
        Assert.Equal(0, result.OrphansDeleted);
        Assert.True(File.Exists(inFlightFile));
    }

    [Fact]
    public async Task PurgeOrphanedUploadsAsync_WhenFileIsTemp_PreservesFile()
    {
        var tempFolder = Path.Combine(_uploadsDir, "temp");
        Directory.CreateDirectory(tempFolder);
        var tempFile = Path.Combine(tempFolder, "chunk1.tmp");
        File.WriteAllText(tempFile, "chunk data");
        File.SetLastWriteTimeUtc(tempFile, DateTime.UtcNow.AddHours(-2));

        var result = await _maintenanceService.PurgeOrphanedUploadsAsync(TimeSpan.FromMinutes(15));

        Assert.Equal(1, result.FilesScanned);
        Assert.Equal(0, result.OrphansDeleted);
        Assert.True(File.Exists(tempFile));
    }

    [Fact]
    public async Task PurgeOrphanedUploadsAsync_PreservesCompanionThumbnail_WhenParentIsActive()
    {
        var noteFolder = Path.Combine(_uploadsDir, "notes", "note-thumb");
        Directory.CreateDirectory(noteFolder);
        var imgFile = Path.Combine(noteFolder, "photo.jpg");
        var thumbFile = Path.Combine(noteFolder, "photo_thumb.jpg");
        File.WriteAllText(imgFile, "photo image");
        File.WriteAllText(thumbFile, "thumbnail image");
        File.SetLastWriteTimeUtc(imgFile, DateTime.UtcNow.AddHours(-1));
        File.SetLastWriteTimeUtc(thumbFile, DateTime.UtcNow.AddHours(-1));

        var note = new ProjectNote
        {
            Id = "note-thumb",
            ProjectId = "proj-1",
            Attachments =
            [
                new NoteAttachment
                {
                    FileName = "photo.jpg",
                    FilePath = "/spokesapi/files/notes/note-thumb/photo.jpg",
                    FileSizeBytes = 100
                }
            ]
        };
        _db.ProjectNotes.Save(note);

        var result = await _maintenanceService.PurgeOrphanedUploadsAsync(TimeSpan.FromMinutes(15));

        Assert.Equal(2, result.FilesScanned);
        Assert.Equal(0, result.OrphansDeleted);
        Assert.True(File.Exists(imgFile));
        Assert.True(File.Exists(thumbFile));
    }

    [Fact]
    public async Task PurgeOrphanedUploadsAsync_PreservesCanonicalCompanionThumbnail_WithOriginalExtension_WhenParentIsActive()
    {
        var chatFolder = Path.Combine(_uploadsDir, "chat", "chan-thumb");
        Directory.CreateDirectory(chatFolder);
        var imgFile = Path.Combine(chatFolder, "639258792452643189_33f81df7.jpg");
        var canonicalThumbFile = Path.Combine(chatFolder, "639258792452643189_33f81df7.jpg_thumb.jpg");
        File.WriteAllText(imgFile, "photo image");
        File.WriteAllText(canonicalThumbFile, "canonical thumbnail image");
        File.SetLastWriteTimeUtc(imgFile, DateTime.UtcNow.AddHours(-1));
        File.SetLastWriteTimeUtc(canonicalThumbFile, DateTime.UtcNow.AddHours(-1));

        var note = new ProjectNote
        {
            Id = "note-chat-thumb",
            ProjectId = "proj-1",
            Attachments =
            [
                new NoteAttachment
                {
                    FileName = "photo.jpg",
                    FilePath = "/spokesapi/files/chat/chan-thumb/639258792452643189_33f81df7.jpg",
                    FileSizeBytes = 100
                }
            ]
        };
        _db.ProjectNotes.Save(note);

        var result = await _maintenanceService.PurgeOrphanedUploadsAsync(TimeSpan.FromMinutes(15));

        Assert.Equal(2, result.FilesScanned);
        Assert.Equal(0, result.OrphansDeleted);
        Assert.True(File.Exists(imgFile));
        Assert.True(File.Exists(canonicalThumbFile));
    }
}
