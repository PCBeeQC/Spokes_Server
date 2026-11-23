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
    private readonly Spokes_Server.Core.Services.Security.SessionService? _sessionService;

    [ActivatorUtilitiesConstructor]
    public PermissionAuthorizationHandler(
        HrContext hrContext,
        CompanyProfileRepository? companyProfile = null,
        Spokes_Server.Core.Services.Security.SessionService? sessionService = null)
    {
        _employees = hrContext.Employees;
        _teams = hrContext.Teams;
        _companyProfile = companyProfile;
        _sessionService = sessionService;
    }

    internal PermissionAuthorizationHandler(
        EmployeeRepository employees,
        TeamRepository? teams = null,
        CompanyProfileRepository? companyProfile = null,
        Spokes_Server.Core.Services.Security.SessionService? sessionService = null)
    {
        _employees = employees;
        _teams = teams;
        _companyProfile = companyProfile;
        _sessionService = sessionService;
    }

    protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, PermissionRequirement requirement)
    {
        if (requirement.Permission == AppPermissions.Email.Use &&
            string.Equals(_companyProfile?.Get()?.Edition, "Family", StringComparison.OrdinalIgnoreCase))
        {
            return Task.CompletedTask;
        }

        var employeeIdClaim = context.User.FindFirst("EmployeeId");

        if (employeeIdClaim == null || string.IsNullOrWhiteSpace(employeeIdClaim.Value))
        {
            return Task.CompletedTask;
        }

        if (string.Equals(employeeIdClaim.Value, Spokes_Server.Core.Middleware.RenderTokenMiddleware.SystemRendererId, StringComparison.OrdinalIgnoreCase))
        {
            return Task.CompletedTask;
        }

        var isDelegatedRenderSession =
            context.User.HasClaim("RenderScope", Spokes_Server.Core.Middleware.RenderTokenMiddleware.DocumentExportScope) ||
            context.User.HasClaim("Spokes_InternalRenderer", "true");
        if (isDelegatedRenderSession &&
            !Spokes_Server.Core.Middleware.RenderTokenMiddleware.IsAllowedDocumentExportPermission(requirement.Permission))
        {
            return Task.CompletedTask;
        }

        var employeeId = employeeIdClaim.Value;
        var employee = _employees.GetById(employeeId);

        if (employee != null)
        {
            if (_sessionService != null && !_sessionService.IsPrincipalSessionValid(context.User, employee.Id))
            {
                return Task.CompletedTask;
            }

            if (!employee.IsActive || employee.IsBanned)
            {
                return Task.CompletedTask;
            }

            var allowSuspended = requirement.Permission != AppPermissions.Admin.RoleName &&
                                 AppPermissions.IsReadOnlyAllowed(requirement.Permission);
            if (employee.IsSuspended && !allowSuspended)
            {
                return Task.CompletedTask;
            }

            var isAuthorized = requirement.Permission == AppPermissions.Admin.RoleName
                ? employee.IsEffectiveAdmin
                : employee.HasEffectivePermission(requirement.Permission, allowSuspended);

            if (!isAuthorized && requirement.Permission == AppPermissions.Purchases.ViewPOs)
            {
                isAuthorized = employee.HasEffectivePermission(AppPermissions.Purchases.ManagePOs, allowSuspended);
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
