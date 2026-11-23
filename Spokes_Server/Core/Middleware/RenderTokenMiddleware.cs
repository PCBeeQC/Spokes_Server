using System.Net;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Spokes_Server.Core.Constants;
using Spokes_Server.Core.Data.Repositories.Core;
using Spokes_Server.Core.Data.Repositories.HR;
using Spokes_Server.Core.Models.Core;
using Spokes_Server.Core.Services.Documents;

namespace Spokes_Server.Core.Middleware;

public class RenderTokenMiddleware
{
    public const string SystemRendererId = "system-renderer";
    public const string HeadlessRendererName = "Headless Renderer";
    public const string DocumentExportScope = "DocumentExport";

    /// <summary>
    /// Returns true strictly for read-only permissions permitted inside a delegated
    /// headless document export session. Write, manage, email, and Admin policies are always denied.
    /// </summary>
    public static bool IsAllowedDocumentExportPermission(string? permission)
    {
        return string.Equals(permission, AppPermissions.Projects.View, StringComparison.Ordinal) ||
               string.Equals(permission, AppPermissions.Purchases.ViewPOs, StringComparison.Ordinal) ||
               string.Equals(permission, AppPermissions.BusinessDocuments.View, StringComparison.Ordinal) ||
               string.Equals(permission, AppPermissions.Finance.AccessAccounting, StringComparison.Ordinal);
    }

    private readonly RequestDelegate _next;
    private readonly ILogger<RenderTokenMiddleware>? _logger;

    public RenderTokenMiddleware(RequestDelegate next, ILogger<RenderTokenMiddleware>? logger = null)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context, RenderTokenService tokenService, DeviceSessionRepository deviceSessions)
    {
        string? token = null;
        if (context.Request.Query.TryGetValue("renderToken", out var tokenValues) && !string.IsNullOrWhiteSpace(tokenValues))
        {
            token = tokenValues.ToString();
        }

        if (!string.IsNullOrEmpty(token))
        {
            var rawRemoteIp = context.Items.TryGetValue("Spokes.RawRemoteIp", out var rawIpObj) && rawIpObj is IPAddress capturedIp
                ? capturedIp
                : context.Connection.RemoteIpAddress;

            if (rawRemoteIp == null || !IPAddress.IsLoopback(rawRemoteIp))
            {
                _logger?.LogWarning("Audit: Rejected non-loopback renderToken attempt from {RemoteIp}", rawRemoteIp);
                await _next(context);
                return;
            }

            if (tokenService.TryConsumeToken(token, context.Request.Path.Value, out var sessionId, out var initiatorEmployeeId))
            {
                if (string.IsNullOrWhiteSpace(initiatorEmployeeId) ||
                    string.Equals(initiatorEmployeeId, SystemRendererId, StringComparison.OrdinalIgnoreCase))
                {
                    _logger?.LogWarning("Audit: Rejected renderToken with missing or disallowed system initiator from {RemoteIp}", rawRemoteIp);
                    await _next(context);
                    return;
                }

                var employeeRepo = context.RequestServices?.GetService<EmployeeRepository>();
                var initiator = employeeRepo?.GetById(initiatorEmployeeId);
                if (initiator == null || !initiator.IsActive || initiator.IsBanned || initiator.IsSystem)
                {
                    _logger?.LogWarning(
                        "Audit: Rejected renderToken for ineligible initiator {InitiatorEmployeeId} from {RemoteIp}",
                        initiatorEmployeeId,
                        rawRemoteIp);
                    await _next(context);
                    return;
                }

                // Delegate strictly to the initiating real employee in read-only DocumentExport scope
                Claim[] claims =
                [
                    new Claim(ClaimTypes.NameIdentifier, initiator.Id),
                    new Claim("sub", initiator.Id),
                    new Claim(ClaimTypes.Name, HeadlessRendererName),
                    new Claim("EmployeeId", initiator.Id),
                    new Claim("SessionId", sessionId),
                    new Claim("Spokes_InternalRenderer", "true"),
                    new Claim("RenderScope", DocumentExportScope)
                ];

                var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
                var principal = new ClaimsPrincipal(identity);

                var existingSession = deviceSessions.GetById(sessionId);
                if (existingSession == null)
                {
                    var session = new DeviceSession
                    {
                        Id = sessionId,
                        EmployeeId = initiator.Id,
                        DeviceName = HeadlessRendererName,
                        CreatedAt = DateTime.UtcNow,
                        ExpiresAt = DateTime.UtcNow.AddMinutes(5)
                    };
                    deviceSessions.Save(session);

                    // Sign in on the initial request so the headless browser also receives a session cookie
                    // for any sub-resources that do not carry custom headers.
                    await context.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, principal, new AuthenticationProperties
                    {
                        IsPersistent = false,
                        ExpiresUtc = DateTimeOffset.UtcNow.AddMinutes(5)
                    });

                    _logger?.LogInformation(
                        "Audit: Authenticated valid delegated renderToken for employee {EmployeeId} session {SessionId} from {RemoteIp}",
                        initiator.Id,
                        sessionId,
                        rawRemoteIp);
                }

                context.User = principal;
            }
            else
            {
                _logger?.LogWarning("Audit: Rejected invalid, mismatched, or expired renderToken from {RemoteIp}", rawRemoteIp);
            }
        }

        await _next(context);
    }
}
