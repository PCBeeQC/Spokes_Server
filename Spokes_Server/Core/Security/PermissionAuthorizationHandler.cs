using System;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Spokes_Server.Aggregate;
using Spokes_Server.Aggregate.Contexts;
using Spokes_Server.Core.Constants;
using Spokes_Server.Core.Data.Repositories.Core;
using Spokes_Server.Core.Data.Repositories.HR;
using Spokes_Server.Core.Models.HR;

namespace Spokes_Server.Core.Security;

public class PermissionRequirement : IAuthorizationRequirement
{
    public string Permission { get; }
    public PermissionRequirement(string permission)
    {
        Permission = permission;
    }
}

public class PermissionAuthorizationHandler : AuthorizationHandler<PermissionRequirement>
{
    private readonly EmployeeRepository _employees;
    private readonly TeamRepository? _teams;
    private readonly CompanyProfileRepository? _companyProfile;

    [ActivatorUtilitiesConstructor]
    public PermissionAuthorizationHandler(HrContext hrContext, CompanyProfileRepository? companyProfile = null)
    {
        _employees = hrContext.Employees;
        _teams = hrContext.Teams;
        _companyProfile = companyProfile;
    }

    internal PermissionAuthorizationHandler(EmployeeRepository employees, TeamRepository? teams = null, CompanyProfileRepository? companyProfile = null)
    {
        _employees = employees;
        _teams = teams;
        _companyProfile = companyProfile;
    }

    protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, PermissionRequirement requirement)
    {
        if (requirement.Permission == AppPermissions.Email.Use &&
            string.Equals(_companyProfile?.Get()?.Edition, "Family", StringComparison.OrdinalIgnoreCase))
        {
            return Task.CompletedTask;
        }

        var employeeIdClaim = context.User.FindFirst("EmployeeId");

        // Headless document renderer uses scoped claim-based evaluation
        if (employeeIdClaim?.Value == "system-renderer")
        {
            if (context.User.IsInRole("Admin") || context.User.HasClaim(c => c.Type == "Permission" && c.Value == requirement.Permission)
                || (requirement.Permission == AppPermissions.Purchases.ViewPOs &&
                    context.User.HasClaim(c => c.Type == "Permission" && c.Value == AppPermissions.Purchases.ManagePOs)))
            {
                context.Succeed(requirement);
            }
            return Task.CompletedTask;
        }

        if (employeeIdClaim == null || string.IsNullOrWhiteSpace(employeeIdClaim.Value))
        {
            return Task.CompletedTask;
        }

        var employeeId = employeeIdClaim.Value;
        var employee = _employees.GetById(employeeId);

        if (employee != null)
        {
            if (!employee.IsActive || employee.IsBanned)
            {
                return Task.CompletedTask;
            }

            if (employee.IsSuspended && !AppPermissions.IsReadOnlyAllowed(requirement.Permission))
            {
                return Task.CompletedTask;
            }

            var isAuthorized = requirement.Permission == AppPermissions.Admin.RoleName
                ? (!employee.IsSuspended && employee.IsAdmin)
                : (employee.IsSuspended
                    ? HasUnderlyingPermission(employee, requirement.Permission)
                    : employee.HasPermission(requirement.Permission));

            if (!isAuthorized && requirement.Permission == AppPermissions.Purchases.ViewPOs)
            {
                isAuthorized = employee.IsSuspended
                    ? HasUnderlyingPermission(employee, AppPermissions.Purchases.ManagePOs)
                    : employee.HasPermission(AppPermissions.Purchases.ManagePOs);
            }

            if (!isAuthorized && requirement.Permission == AppPermissions.Timesheets.ViewReport)
            {
                isAuthorized = _teams?.GetAll().Any(t => t.LeaderId == employee.Id) == true;
            }

            if (isAuthorized)
            {
                context.Succeed(requirement);
            }
        }

        return Task.CompletedTask;
    }

    private static bool HasUnderlyingPermission(Employee employee, string permission)
    {
        if (employee.IsAdmin) return true;

        if (employee.PermissionGroupIds.Count > 0 &&
            employee.PermissionGroups.Any(g => g != null && g.Permissions.Contains(permission)))
        {
            return true;
        }

        return employee.Permissions != null && employee.Permissions.Contains(permission);
    }
}
