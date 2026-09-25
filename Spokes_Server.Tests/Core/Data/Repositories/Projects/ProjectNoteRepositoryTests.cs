using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using Spokes_Server.Core.Data;
using Spokes_Server.Core.Data.Repositories.Projects;
using Spokes_Server.Core.Models.Projects;

namespace Spokes_Server.Tests.Core.Data.Repositories.Projects;

public class ProjectNoteRepositoryTests : IDisposable
{
    private readonly string _testDataDir;
    private readonly IConfiguration _config;
    private readonly DiskPersistenceService _persistence;
    private readonly ProjectNoteRepository _repository;

    public ProjectNoteRepositoryTests()
    {
        _testDataDir = Path.Combine(Path.GetTempPath(), $"Spokes_Test_ProjectNoteRepo_{Guid.NewGuid()}");
        Directory.CreateDirectory(_testDataDir);

        var configDict = new Dictionary<string, string?> { { "DataPath", _testDataDir } };
        _config = new ConfigurationBuilder().AddInMemoryCollection(configDict).Build();

        _persistence = new DiskPersistenceService(new Mock<ILogger<DiskPersistenceService>>().Object);
        _persistence.StartAsync(CancellationToken.None).GetAwaiter().GetResult();

        _repository = new ProjectNoteRepository(_persistence, _config);
    }

    public void Dispose()
    {
        _persistence.StopAsync(CancellationToken.None).GetAwaiter().GetResult();
        _persistence.Dispose();
        if (Directory.Exists(_testDataDir))
        {
            try { Directory.Delete(_testDataDir, true); }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"Cleanup error: {ex.Message}"); }
        }
    }

    [Fact]
    public void GetByProject_ReturnsNotesForProject_OrderedByDateDescending()
    {
        // Arrange
        var projectId = "PROJ-001";
        
        var note1 = new ProjectNote { Id = "note1", ProjectId = projectId, Date = DateTime.Today.AddDays(-2) };
        var note2 = new ProjectNote { Id = "note2", ProjectId = projectId, Date = DateTime.Today };
        var note3 = new ProjectNote { Id = "note3", ProjectId = "PROJ-002", Date = DateTime.Today.AddDays(-1) };

        _repository.Save(note1);
        _repository.Save(note2);
        _repository.Save(note3);

        // Act
        var result = _repository.GetByProject(projectId);

        // Assert
        Assert.NotNull(result);
        Assert.Equal(2, result.Count);
        Assert.Equal("note2", result[0].Id);
        Assert.Equal("note1", result[1].Id);
    }

    [Fact]
    public void GetByProject_EmptyProject_ReturnsEmptyList()
    {
        // Arrange
        var note1 = new ProjectNote { Id = "note1", ProjectId = "PROJ-001" };
        _repository.Save(note1);

        // Act
        var result = _repository.GetByProject("PROJ-999");

        // Assert
        Assert.NotNull(result);
        Assert.Empty(result);
    }

    [Fact]
    public void GetByProject_NullProject_ReturnsEmptyList()
    {
        // Arrange
        var note1 = new ProjectNote { Id = "note1", ProjectId = "PROJ-001" };
        _repository.Save(note1);

        // Act
        var result = _repository.GetByProject(null!);

        // Assert
        Assert.NotNull(result);
        Assert.Empty(result);
    }

    [Fact]
    public void Save_WritesToCorrectPath()
    {
        // Arrange
        var projectId = "PROJ-001";
        var noteId = "TEST-NOTE";
        var note = new ProjectNote { Id = noteId, ProjectId = projectId };

        // Act
        _repository.Save(note);
        
        // Wait for background persistence to finish writing to disk
        var expectedPath = Path.Combine(_testDataDir, "Projects", projectId, "Notes", $"note_{noteId}.json");
        for (int i = 0; i < 10; i++)
        {
            if (File.Exists(expectedPath)) break;
            Thread.Sleep(100);
        }

        // Assert
        Assert.True(File.Exists(expectedPath), $"File should exist at {expectedPath}");
    }
}
