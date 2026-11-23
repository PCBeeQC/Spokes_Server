namespace Spokes_Server.Core.Services.Projects;

using Spokes_Server.Core.Constants;
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
        return CanReadProject(_projects, _teams, user, contextId);
    }

    public bool CanUpload(Employee user, string contextId) =>
        CanUploadProjectFile(user, CanAccess(user, contextId));

    internal static bool CanUploadProjectFile(Employee user, bool canReadContext)
    {
        if (!FileAccessPermissionHelper.IsWritableUser(user) || !canReadContext) return false;
        return user.IsAdmin
            || user.HasPermission(AppPermissions.Projects.Edit)
            || user.HasPermission(AppPermissions.Projects.Create);
    }

    internal static bool CanReadProject(ProjectRepository projects, TeamRepository teams, Employee user, string projectId)
    {
        if (!FileAccessPermissionHelper.IsReadableUser(user))
        {
            return false;
        }

        return ProjectAccessHelper.HasAccess(projects, teams, user, projectId);
    }
}

public class ProjectNoteFileAccessProvider : IFileAccessProvider
{
    private readonly ProjectRepository _projects;
    private readonly TeamRepository _teams;
    private readonly ProjectNoteRepository? _notes;
    public string Category => "projectnotes";

    public ProjectNoteFileAccessProvider(
        ProjectRepository projects,
        TeamRepository teams,
        ProjectNoteRepository? notes = null)
    {
        _projects = projects;
        _teams = teams;
        _notes = notes;
    }

    public bool CanAccess(Employee user, string contextId)
    {
        if (ProjectFileAccessProvider.CanReadProject(_projects, _teams, user, contextId))
            return true;

        if (_notes != null && !string.IsNullOrWhiteSpace(contextId))
        {
            var note = _notes.GetById(contextId);
            if (note != null && !string.IsNullOrWhiteSpace(note.ProjectId))
            {
                return ProjectFileAccessProvider.CanReadProject(_projects, _teams, user, note.ProjectId);
            }
        }

        return false;
    }

    public bool CanUpload(Employee user, string contextId) =>
        ProjectFileAccessProvider.CanUploadProjectFile(user, CanAccess(user, contextId));
}
