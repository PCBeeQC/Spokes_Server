using Spokes_Server.Core.Models.Core;
using Spokes_Server.Core.Models.Projects;
using Spokes_Server.Core.Models.Accounting;
using Spokes_Server.Core.Models.Communication;
using Spokes_Server.Core.Models.HR;

namespace Spokes_Server.Core.Data.Repositories.Projects;

public class ProjectGroupRepository : JsonRepository<ProjectGroup>
{
    public ProjectGroupRepository(DiskPersistenceService writer, IConfiguration config)
        : base(writer, Path.Combine(config["DataPath"] ?? "", "ProjectGroups"), "*.json")
    {
    }

    protected override string GetFilePath(ProjectGroup item)
    {
        return Path.Combine(_basePath, $"{item.Id}.json");
    }

    /// <summary>
    /// Get all project groups for a specific client by business name.
    /// </summary>
    public List<ProjectGroup> GetByClient(string clientBusinessName)
    {
        return _cache.Values
            .Where(g => g.Client.BusinessName.Equals(clientBusinessName, StringComparison.OrdinalIgnoreCase))
            .OrderBy(g => g.Name)
            .ToList();
    }

    /// <summary>
    /// Get all project groups that contain a specific project.
    /// </summary>
    public List<ProjectGroup> GetGroupsContainingProject(string projectId)
    {
        return _cache.Values
            .Where(g => g.ProjectIds.Contains(projectId))
            .ToList();
    }

    /// <summary>
    /// Get the single group a project belongs to (if any).
    /// A project should only belong to one group at a time.
    /// </summary>
    public ProjectGroup? GetGroupForProject(string projectId)
    {
        return _cache.Values
            .FirstOrDefault(g => g.ProjectIds.Contains(projectId));
    }
}


