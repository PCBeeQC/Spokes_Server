using Spokes_Server.Core.Data.Repositories.Projects;
using Spokes_Server.Core.Models.Projects;
using Spokes_Server.Tests.Helpers;
using Xunit;

namespace Spokes_Server.Tests.Core.Data.Repositories.Projects;

public class ProjectNoteRepositoryTests : RepositoryTestFixture<ProjectNoteRepository>
{
    [Fact]
    public void GetByProject_ReturnsNotesForProject_OrderedByDateDescending()
    {
        // Arrange
        var projectId = "PROJ-001";
        
        var note1 = new ProjectNote { Id = "note1", ProjectId = projectId, Date = DateTime.Today.AddDays(-2) };
        var note2 = new ProjectNote { Id = "note2", ProjectId = projectId, Date = DateTime.Today };
        var note3 = new ProjectNote { Id = "note3", ProjectId = "PROJ-002", Date = DateTime.Today.AddDays(-1) };

        _repo.Save(note1);
        _repo.Save(note2);
        _repo.Save(note3);

        // Act
        var result = _repo.GetByProject(projectId);

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
        _repo.Save(note1);

        // Act
        var result = _repo.GetByProject("PROJ-999");

        // Assert
        Assert.NotNull(result);
        Assert.Empty(result);
    }

    [Fact]
    public void GetByProject_NullProject_ReturnsEmptyList()
    {
        // Arrange
        var note1 = new ProjectNote { Id = "note1", ProjectId = "PROJ-001" };
        _repo.Save(note1);

        // Act
        var result = _repo.GetByProject(null!);

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
        _repo.Save(note);
        
        // Wait for background persistence to finish writing to disk
        _writer.FlushAll();

        var expectedPath = Path.Combine(_testDataDir, "Projects", projectId, "Notes", $"note_{noteId}.json");
        // Assert
        Assert.True(File.Exists(expectedPath), $"File should exist at {expectedPath}");
    }
}
