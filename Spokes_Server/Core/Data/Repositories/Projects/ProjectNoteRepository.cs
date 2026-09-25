using Spokes_Server.Core.Models.Projects;

namespace Spokes_Server.Core.Data.Repositories.Projects;

public class ProjectNoteRepository : JsonRepository<ProjectNote>
{
    public ProjectNoteRepository(DiskPersistenceService writer, IConfiguration config)
        : base(writer, Path.Combine(config["DataPath"] ?? "Data", "Projects"), "note_*.json")
    {
    }

    protected override string GetFilePath(ProjectNote item) =>
        // /Data/Projects/{ProjectId}/Notes/note_{Id}.json
        Path.Combine(_basePath, item.ProjectId, "Notes", $"note_{item.Id}.json");

    public List<ProjectNote> GetByProject(string projectId) =>
        GetAll().Where(n => n.ProjectId == projectId).OrderByDescending(n => n.Date).ToList();
}
