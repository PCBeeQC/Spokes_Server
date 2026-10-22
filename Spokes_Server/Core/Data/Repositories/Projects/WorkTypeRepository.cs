using Spokes_Server.Core.Models.Projects;

namespace Spokes_Server.Core.Data.Repositories.Projects;

public class WorkTypeRepository : JsonRepository<WorkType>
{
    public WorkTypeRepository(DiskPersistenceService writer, IConfiguration config)
        : base(writer, Path.Combine(config["DataPath"] ?? "Data", "Settings", "WorkTypes"), "*.json")
    {
    }

    protected override string GetFilePath(WorkType item) =>
        // /Data/Settings/WorkTypes/{Id}.json
        Path.Combine(_basePath, $"{item.Id}.json");
}
