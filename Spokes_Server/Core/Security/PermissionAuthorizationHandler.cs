using Microsoft.AspNetCore.Authorization;
using Spokes_Server.Aggregate;
using Spokes_Server.Core.Constants;
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
    private readonly Database _db;

    public PermissionAuthorizationHandler(Database db)
    {
        _db = db;
    }

    protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, PermissionRequirement requirement)
    {
        var employeeIdClaim = context.User.FindFirst("EmployeeId");

        // Fallback for missing claims or headless renderer
        if (employeeIdClaim == null || employeeIdClaim.Value == "system-renderer")
        {
            if (context.User.IsInRole("Admin") || context.User.HasClaim(c => c.Type == "Permission" && c.Value == requirement.Permission))
            {
                context.Succeed(requirement);
            }
            return Task.CompletedTask;
        }

        var employeeId = employeeIdClaim.Value;
        var employee = _db.Employees.GetById(employeeId);

        if (employee != null)
        {
            var isAuthorized = requirement.Permission == AppPermissions.Admin.RoleName
                ? employee.IsAdmin
                : employee.HasPermission(requirement.Permission);

            if (isAuthorized)
            {
                context.Succeed(requirement);
            }
        }

        return Task.CompletedTask;
    }
}
