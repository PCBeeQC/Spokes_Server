using Spokes_Server.Core.Models.Core;
using Spokes_Server.Core.Models.Projects;
using Spokes_Server.Core.Models.Accounting;
using Spokes_Server.Core.Models.Communication;
using Spokes_Server.Core.Models.HR;

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

    protected override string GetFilePath(Project item)
    {
        return Path.Combine(_basePath, item.Id, "project.json");
    }
}


