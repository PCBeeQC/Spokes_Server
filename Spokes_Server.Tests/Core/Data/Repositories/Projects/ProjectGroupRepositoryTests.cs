using Spokes_Server.Core.Data.Repositories.Projects;
using Spokes_Server.Core.Models.Core;
using Spokes_Server.Core.Models.Projects;
using Spokes_Server.Tests.Helpers;
using Xunit;

namespace Spokes_Server.Tests.Core.Data.Repositories.Projects;

public class ProjectGroupRepositoryTests : RepositoryTestFixture<ProjectGroupRepository>
{
    [Fact]
    public void SaveAndGetById_PersistsProjectGroupWithProjectIds()
    {
        var group = new ProjectGroup
        {
            Id = "g1",
            Name = "Group 1",
            Client = new ClientInfo { BusinessName = "Acme Corp" },
            ProjectIds = ["p1", "p2"]
        };

        _repo.Save(group);

        var loaded = _repo.GetById("g1");
        Assert.NotNull(loaded);
        Assert.Equal("Group 1", loaded!.Name);
        Assert.Equal("Acme Corp", loaded.Client.BusinessName);
        Assert.Equal(["p1", "p2"], loaded.ProjectIds);
    }
}
