using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using Spokes_Server.Core.Data.Repositories.Projects;
using Spokes_Server.Core.Models.Projects;
using Spokes_Server.Tests.Helpers;
using Xunit;

namespace Spokes_Server.Tests.Core.Data.Repositories.Projects;

public class ProjectDocumentRepositoryTests : RepositoryTestBase
{
    private readonly Mock<ILogger<ProjectDocumentRepository>> _mockLogger;
    private readonly ProjectDocumentRepository _repository;

    public ProjectDocumentRepositoryTests() : base("ProjectDocumentRepository")
    {
        _mockLogger = new Mock<ILogger<ProjectDocumentRepository>>();
        _repository = new ProjectDocumentRepository(_writer, Config, _mockLogger.Object);
    }

    [Fact]
    public void GetByProject_ValidProjectId_ReturnsSortedDocuments()
    {
        // Arrange
        var projectId = "project-1";
        var doc1 = new ProjectDocument { Id = "doc-1", ProjectId = projectId, CreatedAt = DateTime.UtcNow.AddDays(-2) };
        var doc2 = new ProjectDocument { Id = "doc-2", ProjectId = projectId, CreatedAt = DateTime.UtcNow.AddDays(-1) };
        var doc3 = new ProjectDocument { Id = "doc-3", ProjectId = "project-2", CreatedAt = DateTime.UtcNow };

        _repository.Save(doc1);
        _repository.Save(doc2);
        _repository.Save(doc3);

        // Act
        var result = _repository.GetByProject(projectId);

        // Assert
        Assert.Equal(2, result.Count);
        // Verify sorting: Descending by CreatedAt, so doc2 comes first
        Assert.Equal("doc-2", result[0].Id);
        Assert.Equal("doc-1", result[1].Id);
    }

    [Fact]
    public void GetByProject_NonExistentProjectId_ReturnsEmpty()
    {
        // Arrange
        var doc1 = new ProjectDocument { Id = "doc-1", ProjectId = "project-1" };
        _repository.Save(doc1);

        // Act
        var result = _repository.GetByProject("non-existent-project");

        // Assert
        Assert.Empty(result);
    }

    [Fact]
    public void GetByProject_NullProjectId_ReturnsEmpty()
    {
        // Arrange
        var doc1 = new ProjectDocument { Id = "doc-1", ProjectId = "project-1" };
        _repository.Save(doc1);

        // Act
        var result = _repository.GetByProject(null!);

        // Assert
        Assert.Empty(result);
    }

    [Fact]
    public void GetBusinessDocuments_ReturnsOnlyDocumentsWithNullOrEmptyProjectId()
    {
        // Arrange
        var bizDoc1 = new ProjectDocument { Id = "biz-1", ProjectId = string.Empty, CreatedAt = DateTime.UtcNow.AddDays(-2) };
        var bizDoc2 = new ProjectDocument { Id = "biz-2", ProjectId = null!, CreatedAt = DateTime.UtcNow.AddDays(-1) };
        var projDoc = new ProjectDocument { Id = "proj-1", ProjectId = "project-1" };

        _repository.Save(bizDoc1);
        _repository.Save(bizDoc2);
        _repository.Save(projDoc);

        // Act
        var result = _repository.GetBusinessDocuments();

        // Assert
        Assert.Equal(2, result.Count);
        // Sorted descending by CreatedAt, so bizDoc2 comes first
        Assert.Equal("biz-2", result[0].Id);
        Assert.Equal("biz-1", result[1].Id);
    }

    [Fact]
    public void GetBusinessDocuments_WhenNoBusinessDocuments_ReturnsEmpty()
    {
        // Arrange
        var projDoc = new ProjectDocument { Id = "proj-1", ProjectId = "project-1" };
        _repository.Save(projDoc);

        // Act
        var result = _repository.GetBusinessDocuments();

        // Assert
        Assert.Empty(result);
    }

    [Fact]
    public void LoadFromDisk_ValidFiles_PopulatesCache()
    {
        // Arrange
        var projDoc = new ProjectDocument { Id = "proj-1", ProjectId = "project-1" };
        var bizDoc = new ProjectDocument { Id = "biz-1", ProjectId = string.Empty };

        // Save using one repo instance
        _repository.Save(projDoc);
        _repository.Save(bizDoc);
        
        // Allow disk writer to flush
        _writer.FlushAll();

        // Create a NEW repo instance to test loading from disk
        var newRepo = new ProjectDocumentRepository(_writer, Config, _mockLogger.Object);

        // Act
        newRepo.LoadFromDisk();
        var allDocs = newRepo.GetAll();

        // Assert
        Assert.Equal(2, allDocs.Count);
        Assert.Contains(allDocs, d => d.Id == "proj-1");
        Assert.Contains(allDocs, d => d.Id == "biz-1");
    }

    [Fact]
    public void LoadFromDisk_MissingDirectories_DoesNotThrow()
    {
        // Arrange
        // Create a repo on an empty directory structure
        var emptyDir = Path.Combine(Path.GetTempPath(), $"Spokes_Empty_{Guid.NewGuid()}");
        Directory.CreateDirectory(emptyDir);
        
        var mockConfig = new Mock<IConfiguration>();
        mockConfig.Setup(c => c["DataPath"]).Returns(emptyDir);
        var emptyRepo = new ProjectDocumentRepository(_writer, mockConfig.Object, _mockLogger.Object);

        // Act & Assert
        // This should not throw, it should simply return with empty cache
        var exception = Record.Exception(() => emptyRepo.LoadFromDisk());
        Assert.Null(exception);
        Assert.Empty(emptyRepo.GetAll());
        
        // Cleanup
        Directory.Delete(emptyDir, true);
    }

    [Fact]
    public void LoadFromDisk_MalformedJson_LogsErrorAndContinues()
    {
        // Arrange
        // Create directories manually
        var bizDir = Path.Combine(_testDataDir, "BusinessDocuments");
        Directory.CreateDirectory(bizDir);

        // Create one valid file and one malformed file
        var validDoc = new ProjectDocument { Id = "valid-biz-1", ProjectId = string.Empty };
        var validJson = JsonSerializer.Serialize(validDoc);
        File.WriteAllText(Path.Combine(bizDir, "valid-biz-1.json"), validJson);
        File.WriteAllText(Path.Combine(bizDir, "malformed.json"), "{ invalid json");

        // Act
        _repository.LoadFromDisk();
        var result = _repository.GetAll();

        // Assert
        Assert.Single(result);
        Assert.Equal("valid-biz-1", result[0].Id);
        
        // Should have logged an error for the malformed file
        _mockLogger.Verify(
            x => x.Log(
                LogLevel.Error,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, t) => v.ToString().Contains("Error loading project document")),
                It.IsAny<Exception>(),
                It.Is<Func<It.IsAnyType, Exception, string>>((v, t) => true)),
            Times.Once);
    }
}
