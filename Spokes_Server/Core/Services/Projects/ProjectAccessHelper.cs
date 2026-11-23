using Spokes_Server.Core.Constants;
using Spokes_Server.Core.Data.Repositories.HR;
using Spokes_Server.Core.Data.Repositories.Projects;
using Spokes_Server.Core.Models.HR;
using Spokes_Server.Core.Models.Projects;

namespace Spokes_Server.Core.Services.Projects;

/// <summary>
/// Authoritative domain helper for evaluating read/view project access permissions across UI, files, and chat.
/// Consistently enforces:
/// 1. Active employee state (inactive or banned employees are denied; suspended employees retain read-only visibility).
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
    public static bool CanAccess(
        Employee? user,
        Project? project,
        TeamRepository? teams = null,
        bool allowAccountingFinancialRead = false) =>
        EvaluateCanAccess(
            user,
            project,
            includePrimaryTeam: true,
            hasTeamAccess: () => teams != null && project!.AllowedTeamIds != null && project.AllowedTeamIds.Count > 0 &&
                                 teams.GetAll().Any(t => t.LeaderId == user!.Id && project.AllowedTeamIds.Contains(t.Id)),
            allowAccountingFinancialRead);

    /// <summary>
    /// Overload that evaluates access using pre-resolved team IDs (e.g. from ChatAuthorizationService).
    /// </summary>
    public static bool CanAccess(
        Employee? user,
        Project? project,
        IEnumerable<string>? userTeamIds,
        bool allowAccountingFinancialRead = false) =>
        EvaluateCanAccess(
            user,
            project,
            includePrimaryTeam: userTeamIds == null,
            hasTeamAccess: () => userTeamIds != null && project!.AllowedTeamIds != null && project.AllowedTeamIds.Count > 0 &&
                                 userTeamIds.Any(tid => project.AllowedTeamIds.Contains(tid)),
            allowAccountingFinancialRead);

    private static bool EvaluateCanAccess(
        Employee? user,
        Project? project,
        bool includePrimaryTeam,
        Func<bool> hasTeamAccess,
        bool allowAccountingFinancialRead)
    {
        if (!CanEvaluateProjectAccess(user, project, allowAccountingFinancialRead))
        {
            return false;
        }

        if (user!.IsAdmin)
        {
            return true;
        }

        if (allowAccountingFinancialRead && user.HasEffectivePermission(AppPermissions.Finance.AccessAccounting, allowSuspended: true))
        {
            return true;
        }

        if (HasDirectOrPublicProjectAccess(user, project!, includePrimaryTeam))
        {
            return true;
        }

        return hasTeamAccess();
    }

    /// <summary>
    /// Evaluates whether an active, unsuspended human employee can contribute to a project
    /// (requires direct/team/public project access without accounting-only read bypass).
    /// </summary>
    public static bool CanContribute(Employee? user, Project? project, TeamRepository? teams = null)
    {
        if (user is not { IsActiveUnsuspended: true, IsSystem: false } || project == null)
        {
            return false;
        }

        return CanAccess(user, project, teams, allowAccountingFinancialRead: false);
    }

    /// <summary>
    /// Evaluates whether an active, unsuspended human employee can contribute to a project
    /// using pre-resolved team IDs.
    /// </summary>
    public static bool CanContribute(Employee? user, Project? project, IEnumerable<string>? userTeamIds)
    {
        if (user is not { IsActiveUnsuspended: true, IsSystem: false } || project == null)
        {
            return false;
        }

        return CanAccess(user, project, userTeamIds, allowAccountingFinancialRead: false);
    }

    /// <summary>
    /// Evaluates whether an employee can contribute to a project (timesheets, weekly plans, expenses)
    /// without requiring <see cref="AppPermissions.Projects.View"/> (since standard employees and team
    /// leaders do not hold Projects.View by default).
    /// </summary>
    public static bool CanAccessProjectForContribution(
        Employee? user,
        Project? project,
        TeamRepository? teams = null,
        bool allowSuspended = false)
    {
        if (user == null || !user.IsActive || user.IsBanned || user.IsSystem || project == null)
        {
            return false;
        }

        if (!allowSuspended && user.IsSuspended)
        {
            return false;
        }

        if (allowSuspended ? user.IsAdmin : user.IsEffectiveAdmin)
        {
            return true;
        }

        if (HasDirectOrPublicProjectAccess(user, project))
        {
            return true;
        }

        if (teams != null && project.AllowedTeamIds != null && project.AllowedTeamIds.Count > 0)
        {
            return teams.GetAll().Any(t => t.LeaderId == user.Id && project.AllowedTeamIds.Contains(t.Id));
        }

        return false;
    }

    public static bool CanLogTimeToProject(Employee? user, Project? project, TeamRepository? teams = null)
    {
        if (user is not { IsActiveUnsuspended: true, IsSystem: false } || project == null)
        {
            return false;
        }

        if (!user.HasPermission(AppPermissions.Timesheets.AccessTimesheet))
        {
            return false;
        }

        return CanAccessProjectForContribution(user, project, teams, allowSuspended: false);
    }

    /// <summary>
    /// Evaluates access given repositories and projectId.
    /// </summary>
    public static bool HasAccess(
        ProjectRepository projects,
        TeamRepository teams,
        Employee user,
        string projectId,
        bool allowAccountingFinancialRead = false)
    {
        if (user == null || !user.IsActive || user.IsBanned) return false;
        if (user.IsAdmin) return true;
        if (string.IsNullOrWhiteSpace(projectId)) return false;
        var project = projects.GetById(projectId);
        if (project == null) return false;
        return CanAccess(user, project, teams, allowAccountingFinancialRead);
    }

    /// <summary>
    /// Evaluates whether an active, non-suspended employee can edit a specific project.
    /// Requires <see cref="AppPermissions.Projects.Edit"/> and non-accounting project visibility.
    /// </summary>
    public static bool CanEditProject(Employee? user, Project? project, TeamRepository? teams = null)
    {
        if (user is not { IsActiveUnsuspended: true } || project == null)
        {
            return false;
        }

        return user.HasPermission(AppPermissions.Projects.Edit)
            && CanAccess(user, project, teams, allowAccountingFinancialRead: false);
    }

    /// <summary>
    /// Overload that evaluates edit authorization using pre-resolved team IDs.
    /// </summary>
    public static bool CanEditProject(Employee? user, Project? project, IEnumerable<string>? userTeamIds)
    {
        if (user is not { IsActiveUnsuspended: true } || project == null)
        {
            return false;
        }

        return user.HasPermission(AppPermissions.Projects.Edit)
            && CanAccess(user, project, userTeamIds, allowAccountingFinancialRead: false);
    }

    private static bool CanEvaluateProjectAccess(Employee? user, Project? project, bool allowAccountingFinancialRead)
    {
        if (user == null || !user.IsActive || user.IsBanned || project == null)
        {
            return false;
        }

        if (user.IsAdmin)
        {
            return true;
        }

        if (allowAccountingFinancialRead && user.HasEffectivePermission(AppPermissions.Finance.AccessAccounting, allowSuspended: true))
        {
            return true;
        }

        return user.HasEffectivePermission(AppPermissions.Projects.View, allowSuspended: true);
    }

    private static bool HasDirectOrPublicProjectAccess(Employee user, Project project, bool includePrimaryTeam = true)
    {
        if (project.AccessPolicy == ProjectAccessPolicy.Public) return true;
        if (project.AllowedUserIds != null && project.AllowedUserIds.Contains(user.Id)) return true;
        if (!string.IsNullOrEmpty(project.ProjectManagerId) && project.ProjectManagerId == user.Id) return true;
        if (includePrimaryTeam && !string.IsNullOrEmpty(user.TeamId) && project.AllowedTeamIds != null && project.AllowedTeamIds.Contains(user.TeamId)) return true;
        return false;
    }
}
