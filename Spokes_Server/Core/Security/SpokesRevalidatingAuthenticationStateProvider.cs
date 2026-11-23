using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Server;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Spokes_Server.Aggregate.Contexts;
using Spokes_Server.Core.Services.Security;

namespace Spokes_Server.Core.Security;

/// <summary>
/// Periodically revalidates Blazor Server interactive circuits against live <c>HrContext</c>
/// employee status (<c>IsActive</c>, <c>!IsBanned</c>) and active <see cref="SessionService"/>
/// session state so revoked, banned, or deleted users lose interactive circuit access promptly.
/// </summary>
public sealed class SpokesRevalidatingAuthenticationStateProvider : RevalidatingServerAuthenticationStateProvider
{
    private readonly IServiceScopeFactory _scopeFactory;

    public SpokesRevalidatingAuthenticationStateProvider(
        ILoggerFactory loggerFactory,
        IServiceScopeFactory scopeFactory)
        : base(loggerFactory)
    {
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
    }

    protected override TimeSpan RevalidationInterval => TimeSpan.FromSeconds(30);

    protected override Task<bool> ValidateAuthenticationStateAsync(
        AuthenticationState authenticationState,
        CancellationToken cancellationToken)
    {
        var user = authenticationState.User;
        if (user?.Identity == null || !user.Identity.IsAuthenticated)
        {
            return Task.FromResult(false);
        }

        var employeeId = user.FindFirst("EmployeeId")?.Value;
        if (string.IsNullOrWhiteSpace(employeeId) ||
            string.Equals(employeeId, Spokes_Server.Core.Middleware.RenderTokenMiddleware.SystemRendererId, StringComparison.OrdinalIgnoreCase))
        {
            return Task.FromResult(false);
        }

        using var scope = _scopeFactory.CreateScope();
        var hrContext = scope.ServiceProvider.GetRequiredService<HrContext>();
        var sessionService = scope.ServiceProvider.GetRequiredService<SessionService>();

        var employee = hrContext.Employees.GetById(employeeId);
        if (employee == null || !employee.IsActive || employee.IsBanned)
        {
            return Task.FromResult(false);
        }

        return Task.FromResult(sessionService.IsPrincipalSessionValid(user, employee.Id));
    }
}
