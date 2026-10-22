using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Spokes_Server.Aggregate;
using Spokes_Server.Aggregate.Contexts;
using Spokes_Server.Core.Constants;
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

    [ActivatorUtilitiesConstructor]
    public PermissionAuthorizationHandler(HrContext hrContext)
    {
        _employees = hrContext.Employees;
        _teams = hrContext.Teams;
    }

    internal PermissionAuthorizationHandler(EmployeeRepository employees, TeamRepository? teams = null)
    {
        _employees = employees;
        _teams = teams;
    }

    protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, PermissionRequirement requirement)
    {
        var employeeIdClaim = context.User.FindFirst("EmployeeId");

        // Fallback for missing claims or headless renderer
        if (employeeIdClaim == null || employeeIdClaim.Value == "system-renderer")
        {
            if (context.User.IsInRole("Admin") || context.User.HasClaim(c => c.Type == "Permission" && c.Value == requirement.Permission)
                || (requirement.Permission == AppPermissions.Purchases.ViewPOs &&
                    context.User.HasClaim(c => c.Type == "Permission" && c.Value == AppPermissions.Purchases.ManagePOs)))
            {
                context.Succeed(requirement);
            }
            return Task.CompletedTask;
        }

        var employeeId = employeeIdClaim.Value;
        var employee = _employees.GetById(employeeId);

        if (employee != null)
        {
            if (!employee.IsActive || employee.IsSuspended || employee.IsBanned)
            {
                return Task.CompletedTask;
            }

            var isAuthorized = requirement.Permission == AppPermissions.Admin.RoleName
                ? employee.IsAdmin
                : employee.HasPermission(requirement.Permission);

            if (!isAuthorized && requirement.Permission == AppPermissions.Purchases.ViewPOs)
            {
                isAuthorized = employee.HasPermission(AppPermissions.Purchases.ManagePOs);
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
}
