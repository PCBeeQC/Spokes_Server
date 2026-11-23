using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Spokes_Server.Core.Data.Repositories.Core;
using Spokes_Server.Core.Data.Repositories.HR;
using Spokes_Server.Core.Models.Core;
using Spokes_Server.Core.Models.HR;
using Spokes_Server.Core.Models.Projects;
using System.Security.Claims;
using Spokes_Server.Core.Constants;
using Spokes_Server.Core.Helpers;
using Spokes_Server.Core.Middleware;
using Spokes_Server.Core.Services.Communication.Voice;
using Spokes_Server.Core.Services.Projects;
using Spokes_Server.Core.Services.Security;

namespace Spokes_Server.Core.Services.Identity;


public sealed record OidcProvisionResult(OpenIdAccount Account, Employee? Employee, bool IsFirstTimeLogin);

public class UserService
{
    private static readonly object _authLock = new();

    private readonly AuthenticationStateProvider _authStateProvider;
    private readonly EmployeeRepository _employees;
    private readonly OpenIdAccountRepository _openIdAccounts;
    private readonly CompanyProfileRepository? _companyProfiles;
    private readonly TeamRepository? _teams;
    private readonly SessionService? _sessionService;
    private readonly VoiceChannelService? _voiceChannelService;

    // Cache the employee for the duration of the circuit/scope
    private Employee? _cachedEmployee;
    private string? _cachedSessionId;
    private string? _cachedAuthType;

    public UserService(
        AuthenticationStateProvider authStateProvider,
        EmployeeRepository employees,
        OpenIdAccountRepository openIdAccounts)
        : this(authStateProvider, employees, openIdAccounts, null, null, null, null)
    {
    }

    public UserService(
        AuthenticationStateProvider authStateProvider,
        EmployeeRepository employees,
        OpenIdAccountRepository openIdAccounts,
        CompanyProfileRepository? companyProfiles,
        TeamRepository? teams)
        : this(authStateProvider, employees, openIdAccounts, companyProfiles, teams, null, null)
    {
    }

    public UserService(
        AuthenticationStateProvider authStateProvider,
        EmployeeRepository employees,
        OpenIdAccountRepository openIdAccounts,
        CompanyProfileRepository? companyProfiles,
        TeamRepository? teams,
        SessionService? sessionService)
        : this(authStateProvider, employees, openIdAccounts, companyProfiles, teams, sessionService, null)
    {
    }

    [ActivatorUtilitiesConstructor]
    public UserService(
        AuthenticationStateProvider authStateProvider,
        EmployeeRepository employees,
        OpenIdAccountRepository openIdAccounts,
        CompanyProfileRepository? companyProfiles,
        TeamRepository? teams,
        SessionService? sessionService,
        VoiceChannelService? voiceChannelService = null)
    {
        _authStateProvider = authStateProvider ?? throw new ArgumentNullException(nameof(authStateProvider));
        _employees = employees;
        _openIdAccounts = openIdAccounts;
        _companyProfiles = companyProfiles;
        _teams = teams;
        _sessionService = sessionService;
        _voiceChannelService = voiceChannelService;
    }


    public virtual async Task<Employee?> GetEmployeeAsync()
    {
        if (_cachedEmployee != null)
        {
            var refreshed = _employees.GetById(_cachedEmployee.Id);
            if (refreshed == null || !refreshed.IsActive ||
                !IsPrincipalSessionValid(_cachedSessionId, _cachedAuthType, refreshed.Id))
            {
                _cachedEmployee = null;
                _cachedSessionId = null;
                _cachedAuthType = null;
                return null;
            }
            _cachedEmployee = refreshed;
            return _cachedEmployee;
        }

        var authState = await _authStateProvider.GetAuthenticationStateAsync();
        var user = authState.User;

        if (user.Identity == null || !user.Identity.IsAuthenticated) return null;

        _cachedEmployee = GetEmployee(user);
        if (_cachedEmployee != null)
        {
            _cachedSessionId = user.FindFirst("SessionId")?.Value;
            _cachedAuthType = user.Identity?.AuthenticationType;
        }
        return _cachedEmployee;
    }

    public async Task<Employee?> GetEmployeeAsync(bool allowSuspended)
    {
        var employee = await GetEmployeeAsync();
        if (employee == null || !employee.IsActive || employee.IsBanned)
        {
            return null;
        }

        if (!allowSuspended && employee.IsSuspended)
        {
            return null;
        }

        return employee;
    }

    public async Task<string?> GetSessionIdAsync()
    {
        var authState = await _authStateProvider.GetAuthenticationStateAsync();
        var user = authState.User;
        if (user.Identity == null || !user.Identity.IsAuthenticated) return null;

        return user.FindFirst("SessionId")?.Value;
    }

    public Employee? GetEmployee(ClaimsPrincipal user)
    {
        if (user.Identity == null || !user.Identity.IsAuthenticated) return null;

        Employee? resolved = null;

        var employeeIdClaim = user.FindFirst("EmployeeId")?.Value;
        if (!string.IsNullOrEmpty(employeeIdClaim))
        {
            if (string.Equals(employeeIdClaim, RenderTokenMiddleware.SystemRendererId, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            resolved = _employees.GetById(employeeIdClaim);
        }

        if (resolved == null)
        {
            var sub = user.FindFirst("sub")?.Value
                      ?? user.FindFirst(ClaimTypes.NameIdentifier)?.Value;

            if (!string.IsNullOrEmpty(sub))
            {
                if (string.Equals(sub, "headless-renderer", StringComparison.OrdinalIgnoreCase))
                {
                    return null;
                }

                resolved = GetBySub(sub);
            }
        }

        if (resolved == null || !resolved.IsActive || resolved.IsSystem)
        {
            return null;
        }

        if (!IsPrincipalSessionValid(
                user.FindFirst("SessionId")?.Value,
                user.Identity?.AuthenticationType,
                resolved.Id))
        {
            return null;
        }

        return resolved;
    }

    private bool IsPrincipalSessionValid(string? sessionId, string? authenticationType, string employeeId) =>
        _sessionService == null || _sessionService.IsPrincipalSessionValid(sessionId, authenticationType, employeeId);

    /// <summary>
    /// Looks up an employee by their OIDC Subject (sub) via OpenID account linkages.
    /// This query is strictly read-only and does not mutate repository state.
    /// </summary>
    public Employee? GetBySub(string sub)
    {
        var openIdAccount = _openIdAccounts.GetBySub(sub);
        if (openIdAccount != null && !string.IsNullOrEmpty(openIdAccount.LinkedEmployeeId))
        {
            return _employees.GetById(openIdAccount.LinkedEmployeeId);
        }

        return null;
    }

    /// <summary>
    /// Synchronizes or auto-provisions an OpenID account and its corresponding employee profile
    /// during OIDC token validation or mobile exchange. Identity binding is strictly sub -> LinkedEmployeeId
    /// (never auto-linking to an existing Employee by email or display name).
    /// </summary>
    public OidcProvisionResult ProvisionOrSyncOidcUser(string sub, string name, string email)
    {
        if (string.IsNullOrEmpty(sub))
        {
            throw new ArgumentException("Subject (sub) cannot be null or empty.", nameof(sub));
        }

        lock (_authLock)
        {
            var resolvedName = ResolveDisplayName(name, email);
            var openIdAccount = _openIdAccounts.GetBySub(sub);
            var isFirstTimeLogin = openIdAccount == null;

            if (openIdAccount == null)
            {
                openIdAccount = new OpenIdAccount
                {
                    Sub = sub,
                    Name = resolvedName,
                    Email = email,
                    FirstSeenAt = DateTime.UtcNow,
                    LastLoginAt = DateTime.UtcNow
                };
                _openIdAccounts.Save(openIdAccount);
            }
            else
            {
                openIdAccount.LastLoginAt = DateTime.UtcNow;
                if (!string.IsNullOrWhiteSpace(name))
                {
                    openIdAccount.Name = name.Trim();
                }
                else if (string.IsNullOrWhiteSpace(openIdAccount.Name))
                {
                    openIdAccount.Name = resolvedName;
                }
                if (!string.IsNullOrEmpty(email)) openIdAccount.Email = email;
                _openIdAccounts.Save(openIdAccount);
            }

            Employee? existingUser = null;
            if (!string.IsNullOrEmpty(openIdAccount.LinkedEmployeeId))
            {
                existingUser = _employees.GetById(openIdAccount.LinkedEmployeeId);
            }

            if (existingUser == null)
            {
                var profile = _companyProfiles?.Get();
                var autoCreate = profile?.AutoCreateEmployeeOnFirstLogin ?? true;

                if (autoCreate && isFirstTimeLogin)
                {
                    var parts = resolvedName.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
                    var newEmp = new Employee
                    {
                        FirstName = parts.Length > 0 ? parts[0] : resolvedName,
                        LastName = parts.Length > 1 ? parts[1] : "",
                        Email = email,
                        IsActive = true,
                        IsAdmin = !_employees.GetAll().Any()
                    };

                    if (!newEmp.IsAdmin)
                    {
                        var defaultGroup = profile != null && !string.IsNullOrEmpty(profile.DefaultPermissionGroupId)
                            ? profile.PermissionGroups.FirstOrDefault(g => g.Id == profile.DefaultPermissionGroupId)
                            : null;

                        if (defaultGroup != null)
                        {
                            newEmp.PermissionGroupId = defaultGroup.Id;
                        }
                        else
                        {
                            newEmp.Permissions.Add(AppPermissions.Chat.Use);
                            newEmp.Permissions.Add(AppPermissions.Calendar.View);
                        }
                    }

                    _employees.Save(newEmp);
                    existingUser = newEmp;

                    openIdAccount.LinkedEmployeeId = newEmp.Id;
                    _openIdAccounts.Save(openIdAccount);
                }
            }
            else if (string.IsNullOrEmpty(existingUser.Email) && !string.IsNullOrEmpty(email))
            {
                existingUser.Email = email;
                _employees.Save(existingUser);
            }

            return new OidcProvisionResult(openIdAccount, existingUser, isFirstTimeLogin);
        }
    }

    /// <summary>
    /// Updates the ban status of an employee and immediately quarantines all active device sessions
    /// and circuits to /banned when banned.
    /// </summary>
    public void SetUserBanState(string employeeId, bool isBanned)
    {
        var emp = _employees.GetById(employeeId);
        if (emp == null) return;

        emp.IsBanned = isBanned;
        _employees.Save(emp);

        if (isBanned)
        {
            _sessionService?.QuarantineUserSessionsForBan(employeeId);
            _ = _voiceChannelService?.EvictUserFromAllVoiceChannelsAsync(employeeId);
        }
    }

    /// <summary>
    /// Updates the active status of an employee and immediately revokes all active device sessions,
    /// file tokens, and cached keystore state when deactivated.
    /// </summary>
    public void SetUserActiveState(string employeeId, bool isActive)
    {
        var emp = _employees.GetById(employeeId);
        if (emp == null) return;

        if (!isActive && IsSoleActiveAdmin(_employees, employeeId))
        {
            throw new InvalidOperationException("Cannot deactivate the sole active administrator account.");
        }

        emp.IsActive = isActive;
        _employees.Save(emp);

        if (!isActive)
        {
            _sessionService?.RevokeAllUserSessions(employeeId);
            _ = _voiceChannelService?.EvictUserFromAllVoiceChannelsAsync(employeeId);
        }
    }

    /// <summary>
    /// Returns true if <paramref name="targetEmployeeId"/> identifies the only active, non-banned,
    /// non-system global administrator account on the server.
    /// </summary>
    public static bool IsSoleActiveAdmin(EmployeeRepository? employees, string? targetEmployeeId)
    {
        if (employees == null || string.IsNullOrWhiteSpace(targetEmployeeId))
            return false;

        var target = employees.GetById(targetEmployeeId);
        if (target == null || !target.IsAdmin || !target.IsActive || target.IsBanned || target.IsSystem)
            return false;

        return !employees.GetAll().Any(e =>
            !string.Equals(e.Id, target.Id, StringComparison.Ordinal) &&
            e.IsAdmin &&
            e.IsActive &&
            !e.IsBanned &&
            !e.IsSuspended &&
            !e.IsSystem);
    }

    /// <summary>
    /// Verifies whether the caller is allowed to manage the target employee profile.
    /// Non-admin users with ManageUsers or Hr.ManageEmployees cannot modify or deactivate Administrator accounts.
    /// Suspended, banned, or inactive callers cannot manage any employee target.
    /// </summary>
    public static bool CanManageEmployeeTarget(Employee? caller, Employee? target)
    {
        if (caller is not { IsActiveUnsuspended: true } || target == null) return false;
        if (caller.IsEffectiveAdmin) return true;
        if (!caller.HasPermission(AppPermissions.Admin.ManageUsers) &&
            !caller.HasPermission(AppPermissions.Hr.ManageEmployees))
            return false;
        return !target.IsAdmin;
    }

    /// <summary>
    /// Updates the read-only suspension status of an employee, evicting active voice sessions when suspended
    /// and broadcasting the suspension transition to active circuits.
    /// </summary>
    public void SetUserSuspensionState(string employeeId, bool isSuspended)
    {
        var emp = _employees.GetById(employeeId);
        if (emp == null) return;

        bool changed = emp.IsSuspended != isSuspended;
        emp.IsSuspended = isSuspended;
        _employees.Save(emp);

        if (_cachedEmployee?.Id == employeeId)
        {
            _cachedEmployee = emp;
        }

        if (isSuspended)
        {
            _ = _voiceChannelService?.EvictUserFromAllVoiceChannelsAsync(employeeId);
        }

        if (changed)
        {
            _sessionService?.NotifyUserSuspensionChanged(employeeId, isSuspended);
        }
    }

    /// <summary>
    /// Verifies whether a moderator or administrator can apply chat moderation actions (suspend/ban)
    /// against the target user. Moderators and administrators are exempt from chat moderation suspension/bans.
    /// </summary>
    public static bool CanModerateUserTarget(Employee? caller, Employee? target)
    {
        if (caller is not { IsActive: true, IsSuspended: false, IsBanned: false } || target == null || target.IsSystem)
            return false;

        if (!caller.IsAdmin && !caller.HasPermission(AppPermissions.Chat.Moderator))
            return false;

        if (caller.Id == target.Id)
            return false;

        return !target.IsAdmin && !target.HasPermission(AppPermissions.Chat.Moderator);
    }

    /// <summary>
    /// Toggles the read-only suspension state of a target user on behalf of an authorized moderator/admin,
    /// enforcing live target and actor authorization checks before mutating state.
    /// </summary>
    public SpokesResult<bool> ToggleModerationSuspension(string actorEmployeeId, string targetEmployeeId)
    {
        if (!TryValidateModerationAction(actorEmployeeId, targetEmployeeId, "suspend", out var target, out var failure))
        {
            return failure!;
        }

        var newState = !target!.IsSuspended;
        SetUserSuspensionState(target.Id, newState);
        return SpokesResult<bool>.Success(newState);
    }

    /// <summary>
    /// Toggles the ban state of a target user on behalf of an authorized moderator/admin,
    /// quarantining all active sessions to /banned immediately when banned.
    /// </summary>
    public SpokesResult<bool> ToggleModerationBan(string actorEmployeeId, string targetEmployeeId)
    {
        if (!TryValidateModerationAction(actorEmployeeId, targetEmployeeId, "ban", out var target, out var failure))
        {
            return failure!;
        }

        var newState = !target!.IsBanned;
        SetUserBanState(target.Id, newState);
        return SpokesResult<bool>.Success(newState);
    }

    private bool TryValidateModerationAction(
        string actorEmployeeId,
        string targetEmployeeId,
        string actionVerb,
        out Employee? target,
        out SpokesResult<bool>? failure)
    {
        target = null;
        var actor = _employees.GetById(actorEmployeeId);
        if (actor is not { IsActive: true, IsSuspended: false, IsBanned: false } ||
            (!actor.IsAdmin && !actor.HasPermission(AppPermissions.Chat.Moderator)))
        {
            failure = SpokesResult<bool>.Failure("User is not authorized to perform moderation actions.", "ACCESS_DENIED");
            return false;
        }

        target = _employees.GetById(targetEmployeeId);
        if (target == null)
        {
            failure = SpokesResult<bool>.Failure("Target user not found.", "NOT_FOUND");
            return false;
        }

        if (target.IsSystem || target.IsAdmin || target.HasPermission(AppPermissions.Chat.Moderator) || actor.Id == target.Id)
        {
            failure = SpokesResult<bool>.Failure($"Cannot {actionVerb} another moderator or admin.", "FORBIDDEN_TARGET");
            return false;
        }

        failure = null;
        return true;
    }

    /// <summary>
    /// Returns the effective permission set held by the caller.
    /// </summary>
    public static HashSet<string> GetCallerEffectivePermissions(Employee? caller)
    {
        if (caller == null || !caller.IsActive || caller.IsBanned)
        {
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }

        if (caller.IsEffectiveAdmin)
        {
            return AppPermissions.GetAll()
                .SelectMany(g => g.Value)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        }

        return caller.GetEffectivePermissionSet(allowSuspended: false);
    }

    /// <summary>
    /// Filters a requested list of permissions so a non-admin caller cannot grant permissions
    /// they do not personally hold (while preserving permissions the target already held).
    /// </summary>
    public static List<string> FilterAllowedPermissions(
        Employee? caller,
        IEnumerable<string>? requestedPermissions,
        IEnumerable<string>? existingTargetPermissions = null)
    {
        if (requestedPermissions == null) return [];
        if (caller?.IsEffectiveAdmin == true) return requestedPermissions.Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        var allowed = GetCallerEffectivePermissions(caller);
        if (existingTargetPermissions != null)
        {
            foreach (var existing in existingTargetPermissions)
            {
                allowed.Add(existing);
            }
        }

        return requestedPermissions
            .Where(p => allowed.Contains(p))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Checks whether the caller is allowed to assign the specified permission group.
    /// </summary>
    public static bool CanAssignPermissionGroup(Employee? caller, PermissionGroup? group, string? currentTargetGroupId = null)
    {
        if (caller is not { IsActiveUnsuspended: true }) return false;
        if (caller.IsEffectiveAdmin || group == null) return true;
        if (!string.IsNullOrEmpty(currentTargetGroupId) && string.Equals(group.Id, currentTargetGroupId, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var callerPerms = GetCallerEffectivePermissions(caller);
        return (group.Permissions ?? []).All(p => callerPerms.Contains(p));
    }

    public async Task<bool> HasPermissionAsync(string permission)
    {
        var employee = await GetEmployeeAsync();
        if (employee == null) return false;

        return employee.HasPermission(permission);
    }

    public async Task<bool> IsAdminAsync()
    {
        var employee = await GetEmployeeAsync();
        return employee != null && employee.IsActive && !employee.IsSuspended && !employee.IsBanned && employee.IsAdmin;
    }

    /// <summary>
    /// Evaluates whether the current user has access to view a specific project based on global permissions and project-level policies.
    /// </summary>
    public async Task<bool> CanViewProjectAsync(Project project, bool allowAccountingFinancialRead = false)
    {
        var employee = await GetEmployeeAsync();
        return ProjectAccessHelper.CanAccess(employee, project, _teams, allowAccountingFinancialRead);
    }

    /// <summary>
    /// Synchronously evaluates whether a given employee has access to view a specific project.
    /// </summary>
    public bool CanViewProject(Employee? employee, Project project, bool allowAccountingFinancialRead = false)
    {
        return ProjectAccessHelper.CanAccess(employee, project, _teams, allowAccountingFinancialRead);
    }

    /// <summary>
    /// Evaluates whether the current user is active, unsuspended, and authorized to edit a specific project.
    /// </summary>
    public async Task<bool> CanEditProjectAsync(Project project)
    {
        var employee = await GetEmployeeAsync();
        return ProjectAccessHelper.CanEditProject(employee, project, _teams);
    }

    /// <summary>
    /// Synchronously evaluates whether a given employee is authorized to edit a specific project.
    /// </summary>
    public bool CanEditProject(Employee? employee, Project project)
    {
        return ProjectAccessHelper.CanEditProject(employee, project, _teams);
    }

    internal static string ResolveDisplayName(string? name, string? email)
    {
        if (!string.IsNullOrWhiteSpace(name))
        {
            return name.Trim();
        }

        if (!string.IsNullOrWhiteSpace(email))
        {
            var atIndex = email.IndexOf('@');
            var localPart = (atIndex > 0 ? email[..atIndex] : email).Trim();
            if (!string.IsNullOrWhiteSpace(localPart))
            {
                return localPart;
            }
        }

        return "User";
    }

    public void ClearCache()
    {
        _cachedEmployee = null;
        _cachedSessionId = null;
        _cachedAuthType = null;
    }
}
