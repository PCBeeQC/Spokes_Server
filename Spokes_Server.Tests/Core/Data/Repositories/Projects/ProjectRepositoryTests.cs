using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using Spokes_Server.Core.Data;
using Spokes_Server.Core.Data.Repositories.Projects;
using Spokes_Server.Core.Models.Projects;

namespace Spokes_Server.Tests.Core.Data.Repositories.Projects;

public class ProjectRepositoryTests : IDisposable
{
    private readonly string _testDataDir;
    private readonly IConfiguration _config;
    private readonly DiskPersistenceService _persistence;
    private readonly ProjectRepository _repository;

    public ProjectRepositoryTests()
    {
        _testDataDir = Path.Combine(Path.GetTempPath(), $"Spokes_Test_ProjectRepository_{Guid.NewGuid()}");
        Directory.CreateDirectory(_testDataDir);

        var configDict = new Dictionary<string, string> { { "DataPath", _testDataDir } };
        _config = new ConfigurationBuilder().AddInMemoryCollection(configDict).Build();

        _persistence = new DiskPersistenceService(new Mock<ILogger<DiskPersistenceService>>().Object);
        _repository = new ProjectRepository(_persistence, _config);
    }

    public void Dispose()
    {
        _persistence.Dispose();
        if (Directory.Exists(_testDataDir))
        {
            try { Directory.Delete(_testDataDir, true); }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"Cleanup error: {ex.Message}"); }
        }
    }

    [Fact]
    public void Save_UpdatesLastEditedAndAddsToCache()
    {
        // Arrange
        var project = new Project { Name = "Test Project" };
        var initialLastEdited = project.LastEdited;

        // Act
        _repository.Save(project);

        // Assert
        var savedProject = _repository.GetById(project.Id);
        Assert.NotNull(savedProject);
        Assert.Equal("Test Project", savedProject.Name);
        Assert.True(savedProject.LastEdited >= initialLastEdited);
    }

    [Fact]
    public void Save_NullItem_ThrowsNullReferenceException()
    {
        // Act & Assert
        Assert.Throws<NullReferenceException>(() => _repository.Save(null!));
    }

    [Fact]
    public async Task SaveAsync_UpdatesLastEditedAndAddsToCache()
    {
        // Arrange
        var project = new Project { Name = "Test Project Async" };
        var initialLastEdited = project.LastEdited;

        // Act
        await _repository.SaveAsync(project);

        // Assert
        var savedProject = _repository.GetById(project.Id);
        Assert.NotNull(savedProject);
        Assert.Equal("Test Project Async", savedProject.Name);
        Assert.True(savedProject.LastEdited >= initialLastEdited);
    }

    [Fact]
    public async Task SaveAsync_NullItem_ThrowsNullReferenceException()
    {
        // Act & Assert
        await Assert.ThrowsAsync<NullReferenceException>(() => _repository.SaveAsync(null!));
    }
}
