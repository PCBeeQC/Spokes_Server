using Spokes_Server.Core.Constants;
using Spokes_Server.Core.Data.Repositories.HR;
using Spokes_Server.Core.Data.Repositories.Projects;
using Spokes_Server.Core.Middleware;
using Spokes_Server.Core.Models.HR;
using Spokes_Server.Core.Models.Projects;

namespace Spokes_Server.Core.Services.Projects;

/// <summary>
/// Authoritative domain helper for evaluating project access permissions across UI, files, and chat.
/// Consistently enforces:
/// 1. Active employee state (inactive, suspended, or banned employees are denied).
/// 2. Admin role (admins have unrestricted access).
/// 3. Foundational global permission (AppPermissions.Projects.View).
/// 4. Internal headless document renderer (system-renderer with Projects.View).
/// 5. Public access policy (ProjectAccessPolicy.Public).
/// 6. Direct assignment in AllowedUserIds.
/// 7. Designated Project Manager (project.ProjectManagerId).
/// 8. Primary team membership in AllowedTeamIds.
/// 9. Leadership of a team in AllowedTeamIds.
/// </summary>
public static class ProjectAccessHelper
{
    public static bool CanAccess(Employee? user, Project? project, TeamRepository? teams = null)
    {
        if (user == null || !user.IsActive || user.IsSuspended || user.IsBanned || project == null)
        {
            return false;
        }

        // Admins have access to every project
        if (user.IsAdmin) return true;

        // Must have the foundational Projects.View global permission
        if (!user.HasPermission(AppPermissions.Projects.View)) return false;

        // Internal headless renderer has read access across projects for PDF export
        if (IsSystemRenderer(user)) return true;

        // Projects visible to everyone (Public)
        if (project.AccessPolicy == ProjectAccessPolicy.Public) return true;

        // Directly assigned users
        if (project.AllowedUserIds != null && project.AllowedUserIds.Contains(user.Id)) return true;

        // Designated Project Manager
        if (!string.IsNullOrEmpty(project.ProjectManagerId) && project.ProjectManagerId == user.Id) return true;

        // Primary team member
        if (!string.IsNullOrEmpty(user.TeamId) && project.AllowedTeamIds != null && project.AllowedTeamIds.Contains(user.TeamId)) return true;

        // Leader of an allowed team
        if (teams != null && project.AllowedTeamIds != null && project.AllowedTeamIds.Count > 0)
        {
            if (teams.GetAll().Any(t => t.LeaderId == user.Id && project.AllowedTeamIds.Contains(t.Id)))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Overload that evaluates access using pre-resolved team IDs (e.g. from ChatAuthorizationService).
    /// </summary>
    public static bool CanAccess(Employee? user, Project? project, IEnumerable<string>? userTeamIds)
    {
        if (user == null || !user.IsActive || user.IsSuspended || user.IsBanned || project == null)
        {
            return false;
        }

        // Admins have access to every project
        if (user.IsAdmin) return true;

        // Must have the foundational Projects.View global permission
        if (!user.HasPermission(AppPermissions.Projects.View)) return false;

        // Internal headless renderer has read access across projects for PDF export
        if (IsSystemRenderer(user)) return true;

        // Projects visible to everyone (Public)
        if (project.AccessPolicy == ProjectAccessPolicy.Public) return true;

        // Directly assigned users
        if (project.AllowedUserIds != null && project.AllowedUserIds.Contains(user.Id)) return true;

        // Designated Project Manager
        if (!string.IsNullOrEmpty(project.ProjectManagerId) && project.ProjectManagerId == user.Id) return true;

        // Primary or led team membership via userTeamIds
        if (userTeamIds != null && project.AllowedTeamIds != null && project.AllowedTeamIds.Count > 0)
        {
            if (userTeamIds.Any(tid => project.AllowedTeamIds.Contains(tid)))
            {
                return true;
            }
        }
        else if (!string.IsNullOrEmpty(user.TeamId) && project.AllowedTeamIds != null && project.AllowedTeamIds.Contains(user.TeamId))
        {
            return true;
        }

        return false;
    }

    /// <summary>
    /// Evaluates access given repositories and projectId.
    /// </summary>
    public static bool HasAccess(ProjectRepository projects, TeamRepository teams, Employee user, string projectId)
    {
        if (user == null || !user.IsActive || user.IsSuspended || user.IsBanned) return false;
        if (user.IsAdmin) return true;
        if (string.IsNullOrWhiteSpace(projectId)) return false;
        var project = projects.GetById(projectId);
        if (project == null) return false;
        return CanAccess(user, project, teams);
    }

    private static bool IsSystemRenderer(Employee user) =>
        user.IsSystem && string.Equals(user.Id, RenderTokenMiddleware.SystemRendererId, StringComparison.Ordinal);
}
