namespace Spokes_Server.Core.Services.Projects;

using System.Linq;
using Spokes_Server.Core.Constants;
using Spokes_Server.Core.Data.Repositories.HR;
using Spokes_Server.Core.Data.Repositories.Projects;
using Spokes_Server.Core.Models.HR;
using Spokes_Server.Core.Models.Projects;
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

    public bool CanUpload(Employee user, string contextId)
    {
        if (!FileAccessPermissionHelper.IsWritableUser(user)) return false;
        if (!CanAccess(user, contextId)) return false;
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

        if (!user.IsSuspended)
        {
            return ProjectAccessHelper.HasAccess(projects, teams, user, projectId);
        }

        if (user.IsAdmin) return true;
        if (string.IsNullOrWhiteSpace(projectId)) return false;

        var project = projects.GetById(projectId);
        if (project == null) return false;

        if (!FileAccessPermissionHelper.HasAssignedPermission(user, AppPermissions.Projects.View)) return false;
        if (project.AccessPolicy == ProjectAccessPolicy.Public) return true;
        if (project.AllowedUserIds != null && project.AllowedUserIds.Contains(user.Id)) return true;
        if (!string.IsNullOrEmpty(project.ProjectManagerId) && project.ProjectManagerId == user.Id) return true;
        if (!string.IsNullOrEmpty(user.TeamId) && project.AllowedTeamIds != null && project.AllowedTeamIds.Contains(user.TeamId)) return true;

        if (teams != null && project.AllowedTeamIds != null && project.AllowedTeamIds.Count > 0)
        {
            if (teams.GetAll().Any(t => t.LeaderId == user.Id && project.AllowedTeamIds.Contains(t.Id)))
            {
                return true;
            }
        }

        return false;
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

    public bool CanUpload(Employee user, string contextId)
    {
        if (!FileAccessPermissionHelper.IsWritableUser(user)) return false;
        if (!CanAccess(user, contextId)) return false;
        return user.IsAdmin
            || user.HasPermission(AppPermissions.Projects.Edit)
            || user.HasPermission(AppPermissions.Projects.Create);
    }
}
