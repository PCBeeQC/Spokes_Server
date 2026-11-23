using Spokes_Server.Core.Models.Projects;

namespace Spokes_Server.Core.Data.Repositories.Projects;

public class ProjectGroupRepository : JsonRepository<ProjectGroup>
{
    public ProjectGroupRepository(DiskPersistenceService writer, IConfiguration config)
        : base(writer, Path.Combine(config["DataPath"] ?? "", "ProjectGroups"), "*.json")
    {
    }

    protected override string GetFilePath(ProjectGroup item) =>
        Path.Combine(_basePath, $"{item.Id}.json");
}
