using System;
using System.Security.Claims;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Spokes_Server.Aggregate;
using Spokes_Server.Core.Constants;
using Spokes_Server.Core.Models.Core;
using Spokes_Server.Core.Services.Documents;

namespace Spokes_Server.Core.Middleware;

public class RenderTokenMiddleware
{
    private readonly RequestDelegate _next;

    public RenderTokenMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context, RenderTokenService tokenService, Database db)
    {
        if (context.Request.Query.TryGetValue("renderToken", out var tokenValues))
        {
            var token = tokenValues.ToString();

            if (tokenService.ValidateAndConsume(token))
            {
                var sessionId = Guid.NewGuid().ToString();

                var session = new DeviceSession
                {
                    Id = sessionId,
                    EmployeeId = "system-renderer",
                    DeviceName = "Headless Renderer",
                    CreatedAt = DateTime.UtcNow,
                    ExpiresAt = DateTime.UtcNow.AddMinutes(5)
                };
                db.DeviceSessions.Save(session);

                // Create a temporary admin identity for this request
                var claims = new[]
                {
                    new Claim(ClaimTypes.NameIdentifier, "headless-renderer"),
                    new Claim(ClaimTypes.Name, "Headless Renderer"),
                    new Claim(ClaimTypes.Role, "Admin"),
                    new Claim("EmployeeId", "system-renderer"),
                    new Claim("SessionId", sessionId),
                    new Claim("Permission", AppPermissions.Projects.View),
                    new Claim("Permission", AppPermissions.Purchases.ManagePOs)
                };

                var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
                var principal = new ClaimsPrincipal(identity);

                // Sign in immediately so the headless browser gets a cookie
                // and can authenticate the subsequent Blazor SignalR websocket connection.
                await context.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, principal, new AuthenticationProperties
                {
                    IsPersistent = false,
                    ExpiresUtc = DateTimeOffset.UtcNow.AddMinutes(5)
                });

                context.User = principal;
            }
        }

        await _next(context);
    }
}
