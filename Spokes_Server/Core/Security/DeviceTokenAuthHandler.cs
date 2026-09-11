namespace Spokes_Server.Core.Security;

using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using System.Text.Encodings.Web;

/// <summary>
/// Options for the DeviceToken authentication scheme.
/// Currently empty — all configuration comes from SessionService and the database.
/// </summary>
public class DeviceTokenAuthOptions : AuthenticationSchemeOptions { }

/// <summary>
/// ASP.NET Core authentication handler that validates device tokens from two sources:
///   1. Authorization: Bearer header (mobile native HTTP requests via OkHttp/URLSession)
///   2. Spokes_Refresh cookie (desktop browser silent session renewal)
///
/// This replaces the inline middleware in Program.cs that manually parsed headers,
/// hashed tokens, and set context.User. Using a proper AuthenticationHandler integrates
/// with [Authorize], policy evaluation, Challenge/Forbid, and scheme selection.
///
/// Registered as the "DeviceToken" scheme and selected by the "Smart" PolicyScheme
/// when a Bearer header or Spokes_Refresh cookie is present but no Spokes_Session_v3 cookie exists.
/// </summary>
public class DeviceTokenAuthHandler : AuthenticationHandler<DeviceTokenAuthOptions>
{
    public DeviceTokenAuthHandler(
        IOptionsMonitor<DeviceTokenAuthOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder)
        : base(options, logger, encoder) { }

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        string? rawToken = null;
        bool isBearerAuth = false;

        // 1. Try Authorization: Bearer header (mobile native HTTP requests)
        var bearerHeader = Request.Headers.Authorization.FirstOrDefault();
        if (!string.IsNullOrEmpty(bearerHeader) && bearerHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            rawToken = bearerHeader["Bearer ".Length..].Trim();
            isBearerAuth = true;
        }

        // 2. Try Spokes_Refresh cookie (desktop silent re-auth)
        if (rawToken == null)
        {
            Request.Cookies.TryGetValue("Spokes_Refresh", out rawToken);
        }

        if (string.IsNullOrEmpty(rawToken))
        {
            return AuthenticateResult.NoResult(); // Fall through to cookie auth
        }

        // Gate: only process Bearer tokens on /spokesapi routes,
        // and only process Refresh cookies on GET page navigations
        var path = Request.Path.Value?.ToLowerInvariant() ?? "";
        if (isBearerAuth)
        {
            if (!path.StartsWith("/spokesapi") && !path.StartsWith("/internal"))
                return AuthenticateResult.NoResult();
        }
        else
        {
            // Desktop refresh cookie path: only on GET page navigations
            if (Request.Method != "GET"
                || path.StartsWith("/spokesapi")
                || path.StartsWith("/api")
                || path.StartsWith("/_blazor")
                || path.StartsWith("/static")
                || path.StartsWith("/sso")
                || path.Contains('.'))
            {
                return AuthenticateResult.NoResult();
            }
        }

        // 3. Validate the token
        var sessionService = Context.RequestServices.GetRequiredService<SessionService>();
        var deviceId = Request.Headers["X-Device-Id"].FirstOrDefault() ?? Request.Cookies["Spokes_Device"];
        var result = sessionService.ValidateToken(rawToken, deviceId);
        if (result == null)
        {
            if (!isBearerAuth)
            {
                // Must match the original cookie options (Path, HttpOnly, SameSite, Secure)
                // or the browser will silently ignore the delete and keep sending the stale cookie.
                Response.Cookies.Delete("Spokes_Refresh", new CookieOptions
                {
                    HttpOnly = true,
                    Secure = Request.IsHttps,
                    SameSite = SameSiteMode.Lax,
                    Path = "/"
                });
            }
            return AuthenticateResult.NoResult();
        }

        var (session, employee) = result.Value;

        // 4. Update LastSeenAt (throttled to 1 minute)
        sessionService.TouchLastSeen(session);

        if (isBearerAuth)
        {
            // Mobile API: authenticate request without touching cookies
            Console.WriteLine($"[DeviceTokenAuth] API Bearer auth successful for: {employee.FullName}");
        }
        else
        {
            // Desktop: silent renewal — issue a fresh Spokes_Session_v3 cookie
            Console.WriteLine($"[DeviceTokenAuth] Silent renewal successful for: {employee.FullName}");
            await sessionService.SignInAndExtendAsync(Context, session, employee);
        }

        // 5. Build the principal and return success
        var identity = sessionService.BuildIdentity(employee, session.Id);
        var principal = new System.Security.Claims.ClaimsPrincipal(identity);
        var ticket = new AuthenticationTicket(principal, Scheme.Name);

        return AuthenticateResult.Success(ticket);
    }

    protected override async Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        var path = Request.Path.Value?.ToLowerInvariant() ?? "";
        
        // If it's an API request, return 401 Unauthorized
        if (path.StartsWith("/spokesapi") || path.StartsWith("/api") || path.StartsWith("/internal") || path.StartsWith("/_blazor"))
        {
            Response.StatusCode = 401;
        }
        else
        {
            // For desktop browser requests, redirect to login while preserving deep links
            var requestedUrl = properties?.RedirectUri ?? (Request.PathBase + Request.Path + Request.QueryString);
            var safeReturnUrl = RedirectHelper.GetSafeRedirectUrl(requestedUrl, Context);
            var target = string.IsNullOrEmpty(safeReturnUrl) || safeReturnUrl == "/"
                ? "/sso/login/auto"
                : $"/sso/login/auto?returnUrl={Uri.EscapeDataString(safeReturnUrl)}";
            Response.Redirect(target);
        }
        
        await Task.CompletedTask;
    }
}
