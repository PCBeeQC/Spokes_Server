namespace Spokes_Server.Core.Services.Projects;

using Spokes_Server.Core.Data.Repositories.HR;
using Spokes_Server.Core.Data.Repositories.Projects;
using Spokes_Server.Core.Models.HR;
using Spokes_Server.Core.Services.Storage;

public class ProjectFileAccessProvider : IFileAccessProvider
{
    private readonly ProjectRepository _projects;
    private readonly TeamRepository _teams;
    public string Category => "projects";

    public ProjectFileAccessProvider(ProjectRepository projects, TeamRepository teams)
    {
        _projects = projects;
        _teams = teams;
    }

    public bool CanAccess(Employee user, string contextId)
    {
        return ProjectAccessHelper.HasAccess(_projects, _teams, user, contextId);
    }
}

public class ProjectNoteFileAccessProvider : IFileAccessProvider
{
    private readonly ProjectRepository _projects;
    private readonly TeamRepository _teams;
    public string Category => "projectnotes";

    public ProjectNoteFileAccessProvider(ProjectRepository projects, TeamRepository teams)
    {
        _projects = projects;
        _teams = teams;
    }

    public bool CanAccess(Employee user, string contextId)
    {
        return ProjectAccessHelper.HasAccess(_projects, _teams, user, contextId);
    }
}
