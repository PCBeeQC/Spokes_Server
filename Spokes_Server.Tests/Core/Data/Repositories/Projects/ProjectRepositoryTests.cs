using Spokes_Server.Core.Data.Repositories.Projects;
using Spokes_Server.Core.Models.Projects;
using Spokes_Server.Tests.Helpers;
using Xunit;

namespace Spokes_Server.Tests.Core.Data.Repositories.Projects;

public class ProjectRepositoryTests : RepositoryTestFixture<ProjectRepository>
{
    [Fact]
    public void Save_UpdatesLastEditedAndAddsToCache()
    {
        // Arrange
        var project = new Project { Name = "Test Project" };
        var initialLastEdited = project.LastEdited;

        // Act
        _repo.Save(project);

        // Assert
        var savedProject = _repo.GetById(project.Id);
        Assert.NotNull(savedProject);
        Assert.Equal("Test Project", savedProject.Name);
        Assert.True(savedProject.LastEdited >= initialLastEdited);
    }

    [Fact]
    public void Save_NullItem_ThrowsNullReferenceException()
    {
        // Act & Assert
        Assert.Throws<NullReferenceException>(() => _repo.Save(null!));
    }
}
