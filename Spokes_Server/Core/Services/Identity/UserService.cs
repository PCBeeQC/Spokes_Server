using Microsoft.AspNetCore.Components.Authorization;
using Spokes_Server.Core.Data.Repositories.Core;
using Spokes_Server.Core.Data.Repositories.HR;
using Spokes_Server.Core.Models.Core;
using Spokes_Server.Core.Models.HR;
using Spokes_Server.Core.Models.Projects;
using System.Security.Claims;
using Spokes_Server.Core.Constants;
using Spokes_Server.Core.Middleware;
using Spokes_Server.Core.Services.Projects;

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

    // Cache the employee for the duration of the circuit/scope
    private Employee? _cachedEmployee;

    public UserService(
        AuthenticationStateProvider authStateProvider,
        EmployeeRepository employees,
        OpenIdAccountRepository openIdAccounts,
        CompanyProfileRepository? companyProfiles = null,
        TeamRepository? teams = null)
    {
        _authStateProvider = authStateProvider ?? throw new ArgumentNullException(nameof(authStateProvider));
        _employees = employees;
        _openIdAccounts = openIdAccounts;
        _companyProfiles = companyProfiles;
        _teams = teams;
    }


    public virtual async Task<Employee?> GetEmployeeAsync()
    {
        if (_cachedEmployee != null) return _cachedEmployee;

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
    /// during OIDC token validation or mobile exchange. Thread-safe to prevent race conditions during first login.
    /// </summary>
    public OidcProvisionResult ProvisionOrSyncOidcUser(string sub, string name, string email)
    {
        if (string.IsNullOrEmpty(sub))
        {
            throw new ArgumentException("Subject (sub) cannot be null or empty.", nameof(sub));
        }

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
            if (!string.IsNullOrEmpty(email))
            {
                existingUser = _employees.GetAll().FirstOrDefault(e => string.Equals(e.Email, email, StringComparison.OrdinalIgnoreCase));
            }

            if (existingUser != null)
            {
                openIdAccount.LinkedEmployeeId = existingUser.Id;
                _openIdAccounts.Save(openIdAccount);
            }
        }

        if (existingUser == null)
        {
            var profile = _companyProfiles?.Get();
            var autoCreate = profile?.AutoCreateEmployeeOnFirstLogin ?? true;

            if (autoCreate && isFirstTimeLogin)
            {
                lock (_authLock)
                {
                    // Check again inside lock to prevent race conditions
                    if (!string.IsNullOrEmpty(email))
                    {
                        existingUser = _employees.GetAll().FirstOrDefault(e => string.Equals(e.Email, email, StringComparison.OrdinalIgnoreCase));
                    }

                    if (existingUser == null)
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
                    else
                    {
                        // If found during lock
                        openIdAccount.LinkedEmployeeId = existingUser.Id;
                        _openIdAccounts.Save(openIdAccount);
                    }
                }
            }
        }
        else if (string.IsNullOrEmpty(existingUser.Email) && !string.IsNullOrEmpty(email))
        {
            existingUser.Email = email;
            _employees.Save(existingUser);
        }

        return new OidcProvisionResult(openIdAccount, existingUser, isFirstTimeLogin);
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
