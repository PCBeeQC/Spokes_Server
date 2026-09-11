using Spokes_Server.Core.Models.Core;
using Spokes_Server.Core.Models.Projects;
using Spokes_Server.Core.Models.Accounting;
using Spokes_Server.Core.Models.Communication;
using Spokes_Server.Core.Models.HR;

namespace Spokes_Server.Core.Data.Repositories.Projects;

public class WorkTypeRepository : JsonRepository<WorkType>
{
    public WorkTypeRepository(DiskPersistenceService writer, IConfiguration config)
        : base(writer, Path.Combine(config["DataPath"] ?? "Data", "Settings", "WorkTypes"), "*.json")
    {
    }

    protected override string GetFilePath(WorkType item)
    {
        // /Data/Settings/WorkTypes/{Id}.json
        return Path.Combine(_basePath, $"{item.Id}.json");
    }

    public override void LoadFromDisk()
    {
        base.LoadFromDisk();

        // Migration: Ensure every task has at least one sub-task
        foreach (var workType in _cache.Values)
        {
            if (!workType.SubTasks.Any())
            {
                workType.SubTasks.Add(new SubTask
                {
                    Name = "General"
                });
                // Persist the migration immediately
                Save(workType);
            }
        }
    }
}


