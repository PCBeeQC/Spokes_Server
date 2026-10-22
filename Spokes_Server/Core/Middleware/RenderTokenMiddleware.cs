using System.Net;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Spokes_Server.Core.Constants;
using Spokes_Server.Core.Data.Repositories.Core;
using Spokes_Server.Core.Models.Core;
using Spokes_Server.Core.Services.Documents;

namespace Spokes_Server.Core.Middleware;

public class RenderTokenMiddleware
{
    public const string SystemRendererId = "system-renderer";
    public const string HeadlessRendererName = "Headless Renderer";

    public static readonly string[] ScopedPermissions =
    [
        AppPermissions.Projects.View,
        AppPermissions.Purchases.ViewPOs,
        AppPermissions.Purchases.ManagePOs,
        AppPermissions.BusinessDocuments.View
    ];

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
        if (context.Request.Headers.TryGetValue("X-Spokes-Render-Token", out var headerValues) && !string.IsNullOrWhiteSpace(headerValues))
        {
            token = headerValues.ToString();
        }
        else if (context.Request.Query.TryGetValue("renderToken", out var tokenValues) && !string.IsNullOrWhiteSpace(tokenValues))
        {
            token = tokenValues.ToString();
        }

        if (!string.IsNullOrEmpty(token))
        {
            var remoteIp = context.Connection.RemoteIpAddress;

            if (tokenService.ValidateToken(token, out var sessionId))
            {
                // Create a scoped document-rendering identity for this request (no Admin role)
                Claim[] claims =
                [
                    new Claim(ClaimTypes.NameIdentifier, "headless-renderer"),
                    new Claim(ClaimTypes.Name, HeadlessRendererName),
                    new Claim("EmployeeId", SystemRendererId),
                    new Claim("SessionId", sessionId),
                    new Claim("Spokes_InternalRenderer", "true"),
                    new Claim("RenderScope", "DocumentExport"),
                    .. ScopedPermissions.Select(p => new Claim("Permission", p))
                ];

                var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
                var principal = new ClaimsPrincipal(identity);

                var existingSession = deviceSessions.GetById(sessionId);
                if (existingSession == null)
                {
                    var session = new DeviceSession
                    {
                        Id = sessionId,
                        EmployeeId = SystemRendererId,
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

                    _logger?.LogInformation("Audit: Authenticated valid renderToken for headless rendering session {SessionId} from {RemoteIp}", sessionId, remoteIp);
                }

                context.User = principal;
            }
            else
            {
                _logger?.LogWarning("Audit: Rejected invalid or expired renderToken from {RemoteIp}", remoteIp);
            }
        }

        await _next(context);
    }
}
