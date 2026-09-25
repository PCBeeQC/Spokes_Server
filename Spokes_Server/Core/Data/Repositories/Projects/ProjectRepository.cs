using Spokes_Server.Core.Models.Projects;

namespace Spokes_Server.Core.Data.Repositories.Projects;

public class ProjectRepository : JsonRepository<Project>
{
    public ProjectRepository(DiskPersistenceService writer, IConfiguration config)
        : base(writer, Path.Combine(config["DataPath"] ?? "Data", "Projects"), "project.json")
    {
    }

    public override void Save(Project item)
    {
        item.LastEdited = DateTime.UtcNow;
        base.Save(item);
    }

    public override Task SaveAsync(Project item)
    {
        item.LastEdited = DateTime.UtcNow;
        return base.SaveAsync(item);
    }

    protected override string GetFilePath(Project item) =>
        Path.Combine(_basePath, item.Id, "project.json");
}
