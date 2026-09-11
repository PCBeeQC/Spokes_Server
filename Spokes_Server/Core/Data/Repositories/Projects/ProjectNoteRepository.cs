using Spokes_Server.Core.Models.Core;
using Spokes_Server.Core.Models.Projects;
using Spokes_Server.Core.Models.Accounting;
using Spokes_Server.Core.Models.Communication;
using Spokes_Server.Core.Models.HR;

namespace Spokes_Server.Core.Data.Repositories.Projects;

public class ProjectNoteRepository : JsonRepository<ProjectNote>
{
    private readonly string _rootDataPath;

    public ProjectNoteRepository(DiskPersistenceService writer, IConfiguration config)
        : base(writer, Path.Combine(config["DataPath"] ?? "Data", "Projects"), "note_*.json")
    {
        _rootDataPath = config["DataPath"] ?? "Data";
    }

    protected override string GetFilePath(ProjectNote item)
    {
        // /Data/Projects/{ProjectId}/Notes/note_{Id}.json
        return Path.Combine(_rootDataPath, "Projects", item.ProjectId, "Notes", $"note_{item.Id}.json");
    }

    public List<ProjectNote> GetByProject(string projectId)
    {
        return GetAll().Where(n => n.ProjectId == projectId).OrderByDescending(n => n.Date).ToList();
    }
}


