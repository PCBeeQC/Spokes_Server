using Spokes_Server.Core.Services.Communication;
using Spokes_Server.Core.Services.Projects;
using Spokes_Server.Core.Services.Core;
using Microsoft.AspNetCore.Components.Authorization;
using Spokes_Server.Core.Data.Repositories.Core;
using Spokes_Server.Core.Data.Repositories.Projects;
using Spokes_Server.Core.Data.Repositories.Accounting;
using Spokes_Server.Core.Data.Repositories.Communication;
using Spokes_Server.Core.Data.Repositories.HR;
using Spokes_Server.Core.Models.Core;
using Spokes_Server.Core.Models.Projects;
using Spokes_Server.Core.Models.Accounting;
using Spokes_Server.Core.Models.Communication;
using Spokes_Server.Core.Models.HR;
using System.Security.Claims;
using Spokes_Server.Core.Constants;

namespace Spokes_Server.Core.Services.Core;

public class UserService
{
    private readonly AuthenticationStateProvider _authStateProvider;
    private readonly EmployeeRepository _employees;
    private readonly OpenIdAccountRepository _openIdAccounts;

    // Cache the employee for the duration of the circuit/scope
    private Employee? _cachedEmployee;

    public UserService(AuthenticationStateProvider authStateProvider, EmployeeRepository employees, OpenIdAccountRepository openIdAccounts)
    {
        _authStateProvider = authStateProvider;
        _employees = employees;
        _openIdAccounts = openIdAccounts;
    }

    public async Task<Employee?> GetEmployeeAsync()
    {
        if (_cachedEmployee != null) return _cachedEmployee;

        var authState = await _authStateProvider.GetAuthenticationStateAsync();
        var user = authState.User;

        if (user.Identity == null || !user.Identity.IsAuthenticated) return null;

        var sub = user.FindFirst("sub")?.Value
                  ?? user.FindFirst(ClaimTypes.NameIdentifier)?.Value;

        // Provide a virtual employee for the PDF generation headless browser
        // This prevents MainLayout from constantly redirecting it to /access-pending
        if (sub == "headless-renderer")
        {
            _cachedEmployee = new Employee
            {
                Id = "system-renderer",
                FirstName = "System",
                LastName = "Renderer",
                Email = "renderer@system.local",
                IsActive = true,
                IsAdmin = true
            };
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
    /// look up an employee by their OIDC Subject (sub), handling both
    /// new OpenID accounts (via linkage) and legacy direct mapping.
    /// </summary>
    public Employee? GetBySub(string sub)
    {
        // Try to find via OpenIdAccount first (New Way)
        var openIdAccount = _openIdAccounts.GetBySub(sub);
        if (openIdAccount != null && !string.IsNullOrEmpty(openIdAccount.LinkedEmployeeId))
        {
            return _employees.GetById(openIdAccount.LinkedEmployeeId);
        }

        // Self-healing legacy linkage
        var legacyEmployee = _employees.GetAll().FirstOrDefault(e => e.OidcSub == sub);
        if (legacyEmployee != null)
        {
            if (openIdAccount != null)
            {
                openIdAccount.LinkedEmployeeId = legacyEmployee.Id;
                _openIdAccounts.Save(openIdAccount);
            }
            else
            {
                openIdAccount = new OpenIdAccount
                {
                    Sub = sub,
                    LinkedEmployeeId = legacyEmployee.Id,
                    FirstSeenAt = DateTime.UtcNow,
                    LastLoginAt = DateTime.UtcNow
                };
                _openIdAccounts.Save(openIdAccount);
            }
            return legacyEmployee;
        }

        return null;
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
        return employee?.IsAdmin ?? false;
    }

    /// <summary>
    /// Evaluates whether the current user has access to view a specific project based on global permissions and project-level policies.
    /// </summary>
    public async Task<bool> CanViewProjectAsync(Project project)
    {
        var employee = await GetEmployeeAsync();
        if (employee == null) return false;

        // Admins have access to every project regardless of access list
        if (employee.IsAdmin) return true;

        // Must have the foundational Projects.View global permission
        if (!employee.HasPermission(AppPermissions.Projects.View)) return false;

        // Projects visible to everyone (Public)
        if (project.AccessPolicy == "Public") return true;

        // Projects restricted to specific users
        if (project.AllowedUserIds != null && project.AllowedUserIds.Contains(employee.Id)) return true;

        // Projects restricted to a team the user is part of
        if (employee.TeamId != null && project.AllowedTeamIds != null && project.AllowedTeamIds.Contains(employee.TeamId)) return true;

        // Fallback catch-all for PM
        if (!string.IsNullOrEmpty(project.ProjectManagerId) && project.ProjectManagerId == employee.Id) return true;

        return false;
    }

    public void ClearCache()
    {
        _cachedEmployee = null;
    }
}



