using Spokes_Server.Core.Models.HR;

namespace Spokes_Server.Core.Services.Security;

/// <summary>
/// Shared fail-closed actor authorization helper for services and UI handlers.
/// Evaluates live active, non-banned, non-suspended status and effective permissions.
/// </summary>
public static class DomainActorGuard
{
    public static void RequireEffectiveAdmin(Employee? actor, string action)
    {
        if (actor is not { IsEffectiveAdmin: true })
        {
            throw new UnauthorizedAccessException($"Active global administrator privileges are required to {action}.");
        }
    }

    public static void RequirePermission(Employee? actor, string permission, string action, bool allowSuspended = false)
    {
        if (actor == null || !actor.HasEffectivePermission(permission, allowSuspended))
        {
            throw new UnauthorizedAccessException($"Missing required permission '{permission}' to {action}.");
        }
    }

    public static void RequireAnyPermission(Employee? actor, string action, bool allowSuspended = false, params string[] permissions)
    {
        if (actor == null || permissions == null || !permissions.Any(p => actor.HasEffectivePermission(p, allowSuspended)))
        {
            throw new UnauthorizedAccessException($"Missing required authorization to {action}.");
        }
    }

    public static void RequireInteractivePermission(Employee? actor, string permission, string action)
    {
        RequirePermission(actor, permission, action, allowSuspended: false);
        if (actor!.IsSystem)
        {
            throw new UnauthorizedAccessException($"System actors cannot {action}.");
        }
    }

    public static void RequireInteractiveAnyPermission(Employee? actor, string action, params string[] permissions)
    {
        RequireAnyPermission(actor, action, allowSuspended: false, permissions);
        if (actor!.IsSystem)
        {
            throw new UnauthorizedAccessException($"System actors cannot {action}.");
        }
    }
}
