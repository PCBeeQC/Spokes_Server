using Microsoft.AspNetCore.Components.Authorization;
using Spokes_Server.Core.Data.Repositories.Core;
using Spokes_Server.Core.Data.Repositories.HR;
using Spokes_Server.Core.Models.Core;
using Spokes_Server.Core.Models.HR;
using Spokes_Server.Core.Models.Projects;
using System.Security.Claims;
using Spokes_Server.Core.Constants;
using Spokes_Server.Core.Helpers;
using Spokes_Server.Core.Middleware;
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

    // Cache the employee for the duration of the circuit/scope
    private Employee? _cachedEmployee;

    public UserService(
        AuthenticationStateProvider authStateProvider,
        EmployeeRepository employees,
        OpenIdAccountRepository openIdAccounts)
        : this(authStateProvider, employees, openIdAccounts, null, null, null)
    {
    }

    public UserService(
        AuthenticationStateProvider authStateProvider,
        EmployeeRepository employees,
        OpenIdAccountRepository openIdAccounts,
        CompanyProfileRepository? companyProfiles,
        TeamRepository? teams)
        : this(authStateProvider, employees, openIdAccounts, companyProfiles, teams, null)
    {
    }

    public UserService(
        AuthenticationStateProvider authStateProvider,
        EmployeeRepository employees,
        OpenIdAccountRepository openIdAccounts,
        CompanyProfileRepository? companyProfiles,
        TeamRepository? teams,
        SessionService? sessionService)
    {
        _authStateProvider = authStateProvider ?? throw new ArgumentNullException(nameof(authStateProvider));
        _employees = employees;
        _openIdAccounts = openIdAccounts;
        _companyProfiles = companyProfiles;
        _teams = teams;
        _sessionService = sessionService;
    }


    public virtual async Task<Employee?> GetEmployeeAsync()
    {
        if (_cachedEmployee != null)
        {
            if (_cachedEmployee.Id != "system-renderer")
            {
                _cachedEmployee = _employees.GetById(_cachedEmployee.Id) ?? _cachedEmployee;
            }
            return _cachedEmployee;
        }

        var authState = await _authStateProvider.GetAuthenticationStateAsync();
        var user = authState.User;

        if (user.Identity == null || !user.Identity.IsAuthenticated) return null;

        var sub = user.FindFirst("sub")?.Value
                  ?? user.FindFirst(ClaimTypes.NameIdentifier)?.Value;

        // Provide a virtual employee for the PDF generation headless browser
        // This prevents MainLayout from constantly redirecting it to /access-pending
        if (sub == "headless-renderer" && user.HasClaim("Spokes_InternalRenderer", "true"))
        {
            _cachedEmployee = CreateSystemRendererEmployee(user);
            return _cachedEmployee;
        }

        _cachedEmployee = GetEmployee(user);
        return _cachedEmployee;
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

        if (user.HasClaim("Spokes_InternalRenderer", "true") &&
            (user.FindFirst("EmployeeId")?.Value == "system-renderer" ||
             user.FindFirst("sub")?.Value == "headless-renderer" ||
             user.FindFirst(ClaimTypes.NameIdentifier)?.Value == "headless-renderer"))
        {
            return CreateSystemRendererEmployee(user);
        }

        var employeeIdClaim = user.FindFirst("EmployeeId")?.Value;
        if (!string.IsNullOrEmpty(employeeIdClaim))
        {
            var emp = _employees.GetById(employeeIdClaim);
            if (emp != null) return emp;
        }

        var sub = user.FindFirst("sub")?.Value
                  ?? user.FindFirst(ClaimTypes.NameIdentifier)?.Value;

        if (!string.IsNullOrEmpty(sub))
        {
            var emp = GetBySub(sub);
            if (emp != null) return emp;
        }

        return null;
    }

    public Employee? GetCurrentUser(ClaimsPrincipal user) => GetEmployee(user);

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
    public OidcProvisionResult ProvisionOrSyncOidcUser(string sub, string name, string email, bool emailVerified = true)
    {
        _ = emailVerified;
        if (string.IsNullOrEmpty(sub))
        {
            throw new ArgumentException("Subject (sub) cannot be null or empty.", nameof(sub));
        }

        lock (_authLock)
        {
            var openIdAccount = _openIdAccounts.GetBySub(sub);
            var isFirstTimeLogin = openIdAccount == null;

            if (openIdAccount == null)
            {
                openIdAccount = new OpenIdAccount
                {
                    Sub = sub,
                    Name = name,
                    Email = email,
                    FirstSeenAt = DateTime.UtcNow,
                    LastLoginAt = DateTime.UtcNow
                };
                _openIdAccounts.Save(openIdAccount);
            }
            else
            {
                openIdAccount.LastLoginAt = DateTime.UtcNow;
                openIdAccount.Name = name;
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
                    var parts = name.Split(' ', 2);
                    var newEmp = new Employee
                    {
                        FirstName = parts.Length > 0 ? parts[0] : name,
                        LastName = parts.Length > 1 ? parts[1] : "",
                        Email = email,
                        IsActive = true,
                        IsAdmin = !_employees.GetAll().Any(e => e.IsAdmin)
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

        emp.IsActive = isActive;
        _employees.Save(emp);

        if (!isActive)
        {
            _sessionService?.RevokeAllUserSessions(employeeId);
        }
    }

    /// <summary>
    /// Verifies whether the caller is allowed to manage the target employee profile.
    /// Non-admin users with ManageUsers cannot modify or deactivate Administrator accounts.
    /// </summary>
    public static bool CanManageEmployeeTarget(Employee? caller, Employee? target)
    {
        if (caller == null || target == null) return false;
        if (caller.IsAdmin) return true;
        if (!caller.HasPermission(AppPermissions.Admin.ManageUsers)) return false;
        return !target.IsAdmin;
    }

    /// <summary>
    /// Updates the read-only suspension status of an employee.
    /// </summary>
    public void SetUserSuspensionState(string employeeId, bool isSuspended)
    {
        var emp = _employees.GetById(employeeId);
        if (emp == null) return;

        emp.IsSuspended = isSuspended;
        _employees.Save(emp);
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
    /// Toggles the read-only suspension state of a target user on behalf of an authorized moderator/admin.
    /// </summary>
    public SpokesResult<bool> ToggleModerationSuspension(string actorEmployeeId, string targetEmployeeId)
    {
        var actor = _employees.GetById(actorEmployeeId);
        if (actor is not { IsActive: true, IsSuspended: false, IsBanned: false } ||
            (!actor.IsAdmin && !actor.HasPermission(AppPermissions.Chat.Moderator)))
        {
            return SpokesResult<bool>.Failure("User is not authorized to perform moderation actions.", "ACCESS_DENIED");
        }

        var target = _employees.GetById(targetEmployeeId);
        if (target == null)
        {
            return SpokesResult<bool>.Failure("Target user not found.", "NOT_FOUND");
        }

        if (target.IsSystem || target.IsAdmin || target.HasPermission(AppPermissions.Chat.Moderator) || actor.Id == target.Id)
        {
            return SpokesResult<bool>.Failure("Cannot suspend another moderator or admin.", "FORBIDDEN_TARGET");
        }

        var newState = !target.IsSuspended;
        SetUserSuspensionState(target.Id, newState);
        return SpokesResult<bool>.Success(newState);
    }

    /// <summary>
    /// Toggles the ban state of a target user on behalf of an authorized moderator/admin,
    /// quarantining all active sessions to /banned immediately when banned.
    /// </summary>
    public SpokesResult<bool> ToggleModerationBan(string actorEmployeeId, string targetEmployeeId)
    {
        var actor = _employees.GetById(actorEmployeeId);
        if (actor is not { IsActive: true, IsSuspended: false, IsBanned: false } ||
            (!actor.IsAdmin && !actor.HasPermission(AppPermissions.Chat.Moderator)))
        {
            return SpokesResult<bool>.Failure("User is not authorized to perform moderation actions.", "ACCESS_DENIED");
        }

        var target = _employees.GetById(targetEmployeeId);
        if (target == null)
        {
            return SpokesResult<bool>.Failure("Target user not found.", "NOT_FOUND");
        }

        if (target.IsSystem || target.IsAdmin || target.HasPermission(AppPermissions.Chat.Moderator) || actor.Id == target.Id)
        {
            return SpokesResult<bool>.Failure("Cannot ban another moderator or admin.", "FORBIDDEN_TARGET");
        }

        var newState = !target.IsBanned;
        SetUserBanState(target.Id, newState);
        return SpokesResult<bool>.Success(newState);
    
    }

    /// <summary>
    /// Returns the effective permission set held by the caller.
    /// </summary>
    public static HashSet<string> GetCallerEffectivePermissions(Employee? caller)
    {
        if (caller == null) return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (caller.IsAdmin)
        {
            return AppPermissions.GetAll()
                .SelectMany(g => g.Value)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        }

        var source = caller.PermissionGroup != null
            ? caller.PermissionGroup.Permissions
            : caller.Permissions;

        return new HashSet<string>(source ?? [], StringComparer.OrdinalIgnoreCase);
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
        if (caller?.IsAdmin == true) return requestedPermissions.Distinct(StringComparer.OrdinalIgnoreCase).ToList();

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
        if (caller == null) return false;
        if (caller.IsAdmin || group == null) return true;
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
    public async Task<bool> CanViewProjectAsync(Project project)
    {
        var employee = await GetEmployeeAsync();
        return ProjectAccessHelper.CanAccess(employee, project, _teams);
    }

    /// <summary>
    /// Synchronously evaluates whether a given employee has access to view a specific project.
    /// </summary>
    public bool CanViewProject(Employee? employee, Project project)
    {
        return ProjectAccessHelper.CanAccess(employee, project, _teams);
    }


    private static Employee CreateSystemRendererEmployee(ClaimsPrincipal user)
    {
        var permissions = user.FindAll("Permission")
            .Select(c => c.Value)
            .Distinct()
            .ToList();

        if (permissions.Count == 0)
        {
            permissions = [.. RenderTokenMiddleware.ScopedPermissions];
        }

        return new Employee
        {
            Id = RenderTokenMiddleware.SystemRendererId,
            FirstName = "System",
            LastName = "Renderer",
            Email = "renderer@system.local",
            IsActive = true,
            IsAdmin = false,
            IsSystem = true,
            Permissions = permissions
        };
    }

    public void ClearCache() => _cachedEmployee = null;
}
