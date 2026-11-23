using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Claims;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Spokes_Server.Core.Constants;
using Spokes_Server.Core.Data.Repositories.Core;
using Spokes_Server.Core.Data.Repositories.HR;
using Spokes_Server.Core.Models.Core;
using Spokes_Server.Core.Models.HR;
using Spokes_Server.Core.Security;
using Spokes_Server.Core.Services.Identity;
using Spokes_Server.Core.Services.Security;

namespace Spokes_Server.Core.Extensions;

public static class AuthenticationExtensions
{
    public static IServiceCollection AddSpokesAuth(this IServiceCollection services, SystemConfig? systemConfig = null)
    {
        services.AddCascadingAuthenticationState();
        services.AddScoped<Microsoft.AspNetCore.Components.Authorization.AuthenticationStateProvider, SpokesRevalidatingAuthenticationStateProvider>();
        services.AddMemoryCache();
        services.AddSingleton<IOpenIdConfigurationProvider, OpenIdConfigurationProvider>();
        services.AddSingleton<SessionService>();
        services.AddSingleton<ITicketStore, DeviceSessionTicketStore>();

        services.AddSingleton<ConfigureOpenIdConnectOptions>(sp =>
            new ConfigureOpenIdConnectOptions(
                sp,
                sp.GetRequiredService<IOptionsMonitorCache<OpenIdConnectOptions>>(),
                systemConfig));
        services.AddSingleton<IConfigureNamedOptions<OpenIdConnectOptions>>(sp =>
            sp.GetRequiredService<ConfigureOpenIdConnectOptions>());
        services.AddSingleton<IPostConfigureOptions<OpenIdConnectOptions>>(sp =>
            sp.GetRequiredService<ConfigureOpenIdConnectOptions>());

        services.AddAuthentication(options =>
        {
            options.DefaultScheme = "Smart";
            options.DefaultChallengeScheme = "Smart";
        })
        .AddCookie(options =>
        {
            options.Cookie.Name = "Spokes_Session_v3";
            options.Cookie.HttpOnly = true;
            options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
            options.Cookie.SameSite = SameSiteMode.Lax;
            options.ExpireTimeSpan = TimeSpan.FromDays(90);
            options.SlidingExpiration = true;
            options.LoginPath = "/sso/login/auto";
            options.AccessDeniedPath = "/access-denied";

            options.Events.OnValidatePrincipal = context =>
            {
                var sessionId = context.Principal?.FindFirst("SessionId")?.Value;
                var employeeId = context.Principal?.FindFirst("EmployeeId")?.Value;
                var employees = context.HttpContext.RequestServices.GetRequiredService<EmployeeRepository>();
                var deviceSessions = context.HttpContext.RequestServices.GetRequiredService<DeviceSessionRepository>();
                var path = context.HttpContext.Request.Path.Value ?? string.Empty;

                if (string.IsNullOrWhiteSpace(sessionId))
                {
                    if (IsRevokedBlockedPath(path))
                    {
                        context.HttpContext.Items["Spokes.SessionRejected"] = true;
                    }
                    context.RejectPrincipal();
                    return Task.CompletedTask;
                }

                var session = deviceSessions.GetById(sessionId);
                if (session == null || session.ExpiresAt <= DateTime.UtcNow)
                {
                    if (IsRevokedBlockedPath(path))
                    {
                        context.HttpContext.Items["Spokes.SessionRejected"] = true;
                    }
                    context.RejectPrincipal();
                    return Task.CompletedTask;
                }

                if (session.RevokedAt != null)
                {
                    context.Properties.SetParameter("IsRevoked", true);
                    context.HttpContext.Items["Spokes.SessionRevoked"] = true;
                    if (IsRevokedBlockedPath(path))
                    {
                        context.HttpContext.Items["Spokes.SessionRejected"] = true;
                        context.RejectPrincipal();
                    }
                    return Task.CompletedTask;
                }

                var sessionService = context.HttpContext.RequestServices.GetService<SessionService>();
                sessionService?.TouchLastSeen(session);

                if (string.IsNullOrEmpty(employeeId))
                {
                    var sub = context.Principal?.FindFirst("sub")?.Value
                              ?? context.Principal?.FindFirst(ClaimTypes.NameIdentifier)?.Value;

                    if (!string.IsNullOrEmpty(sub))
                    {
                        var openIdAccounts = context.HttpContext.RequestServices.GetService<OpenIdAccountRepository>();
                        var linkedEmployeeId = openIdAccounts?.GetBySub(sub)?.LinkedEmployeeId;
                        if (!string.IsNullOrEmpty(linkedEmployeeId))
                        {
                            var linkedEmployee = employees.GetById(linkedEmployeeId);
                            if (linkedEmployee != null)
                            {
                                employeeId = linkedEmployeeId;
                                if (session.EmployeeId != linkedEmployeeId)
                                {
                                    session.EmployeeId = linkedEmployeeId;
                                    deviceSessions.Save(session);
                                }
                                if (sessionService != null)
                                {
                                    var upgradedIdentity = sessionService.BuildIdentity(linkedEmployee, sessionId);
                                    context.ReplacePrincipal(new ClaimsPrincipal(upgradedIdentity));
                                    context.ShouldRenew = true;
                                }
                            }
                        }
                    }

                    if (string.IsNullOrEmpty(employeeId))
                    {
                        if (IsRestrictedPrincipalBlockedPath(path))
                        {
                            context.HttpContext.Items["Spokes.SessionRejected"] = true;
                            context.RejectPrincipal();
                            return Task.CompletedTask;
                        }

                        if (!IsAllowedPendingRoute(path))
                        {
                            context.HttpContext.Items["Spokes.AuthRedirect"] = "/access-pending";
                            context.HttpContext.Response.Redirect("/access-pending");
                        }
                        return Task.CompletedTask;
                    }
                }

                if (string.Equals(employeeId, Spokes_Server.Core.Middleware.RenderTokenMiddleware.SystemRendererId, StringComparison.OrdinalIgnoreCase))
                {
                    if (IsRevokedBlockedPath(path))
                    {
                        context.HttpContext.Items["Spokes.SessionRejected"] = true;
                    }
                    context.RejectPrincipal();
                    return Task.CompletedTask;
                }

                var isInternalRendererPrincipal = context.Principal?.HasClaim("Spokes_InternalRenderer", "true") == true
                    || context.Principal?.HasClaim("RenderScope", Spokes_Server.Core.Middleware.RenderTokenMiddleware.DocumentExportScope) == true;
                var isHeadlessSession = string.Equals(session.DeviceName, Spokes_Server.Core.Middleware.RenderTokenMiddleware.HeadlessRendererName, StringComparison.Ordinal);
                if (isInternalRendererPrincipal || isHeadlessSession)
                {
                    var method = context.HttpContext.Request.Method;
                    var isReadOnlyOrBlazorTransport = HttpMethods.IsGet(method)
                        || HttpMethods.IsHead(method)
                        || context.HttpContext.Request.Path.StartsWithSegments("/_blazor");
                    if (context.Principal?.HasClaim("Spokes_InternalRenderer", "true") != true ||
                        !context.Principal.HasClaim("RenderScope", Spokes_Server.Core.Middleware.RenderTokenMiddleware.DocumentExportScope) ||
                        !isHeadlessSession ||
                        !isReadOnlyOrBlazorTransport)
                    {
                        if (IsRevokedBlockedPath(path))
                        {
                            context.HttpContext.Items["Spokes.SessionRejected"] = true;
                        }
                        context.RejectPrincipal();
                        return Task.CompletedTask;
                    }
                }

                if (!string.IsNullOrEmpty(session.EmployeeId) && session.EmployeeId != employeeId)
                {
                    if (IsRevokedBlockedPath(path))
                    {
                        context.HttpContext.Items["Spokes.SessionRejected"] = true;
                    }
                    context.RejectPrincipal();
                    return Task.CompletedTask;
                }

                var employee = employees.GetById(employeeId);
                if (employee == null || !employee.IsActive || employee.IsSystem)
                {
                    sessionService?.RevokeAllUserSessions(employeeId);
                    if (IsRevokedBlockedPath(path))
                    {
                        context.HttpContext.Items["Spokes.SessionRejected"] = true;
                    }
                    context.RejectPrincipal();
                    return Task.CompletedTask;
                }

                if (employee.IsBanned)
                {
                    if (context.HttpContext.Request.Cookies.ContainsKey(VaultCookieProtector.RefreshCookieName))
                    {
                        VaultCookieProtector.DeleteRefreshCookie(context.HttpContext);
                    }
                    if (context.HttpContext.Request.Cookies.ContainsKey(VaultCookieProtector.VaultCookieName))
                    {
                        VaultCookieProtector.DeleteVaultCookie(context.HttpContext.Response);
                    }

                    if (isInternalRendererPrincipal || isHeadlessSession)
                    {
                        context.HttpContext.Items["Spokes.SessionRejected"] = true;
                        context.RejectPrincipal();
                        return Task.CompletedTask;
                    }

                    if (IsRestrictedPrincipalBlockedPath(path))
                    {
                        context.HttpContext.Items["Spokes.SessionRejected"] = true;
                        context.RejectPrincipal();
                        return Task.CompletedTask;
                    }

                    if (sessionService != null &&
                        (context.Principal?.HasClaim("Banned", "true") != true ||
                         context.Principal.HasClaim(c => c.Type == ClaimTypes.Role || c.Type == "Permission")))
                    {
                        var restrictedIdentity = sessionService.BuildIdentity(employee, sessionId);
                        context.ReplacePrincipal(new ClaimsPrincipal(restrictedIdentity));
                        context.ShouldRenew = true;
                    }

                    if (!IsAllowedBannedRoute(path))
                    {
                        context.HttpContext.Items["Spokes.AuthRedirect"] = "/banned";
                        context.HttpContext.Response.Redirect("/banned");
                    }
                    return Task.CompletedTask;
                }

                if (sessionService != null && !isInternalRendererPrincipal && !isHeadlessSession && context.Principal?.HasClaim("Banned", "true") == true)
                {
                    var restoredIdentity = sessionService.BuildIdentity(employee, sessionId);
                    context.ReplacePrincipal(new ClaimsPrincipal(restoredIdentity));
                    context.ShouldRenew = true;
                }

                return Task.CompletedTask;
            };

            options.Events.OnRedirectToAccessDenied = context =>
            {
                if (context.Request.Path.StartsWithSegments("/spokesapi") || 
                    context.Request.Path.StartsWithSegments("/internal") ||
                    context.Request.Path.StartsWithSegments("/_blazor"))
                {
                    context.Response.StatusCode = 403;
                    return Task.CompletedTask;
                }
                context.Response.Redirect(context.RedirectUri);
                return Task.CompletedTask;
            };

            options.Events.OnRedirectToLogin = context =>
            {
                if (context.Request.Path.StartsWithSegments("/spokesapi") || 
                    context.Request.Path.StartsWithSegments("/internal") ||
                    context.Request.Path.StartsWithSegments("/_blazor"))
                {
                    context.Response.StatusCode = 401;
                    return Task.CompletedTask;
                }
                context.Response.Redirect(context.RedirectUri);
                return Task.CompletedTask;
            };
        })
        .AddOpenIdConnect(options =>
        {
            options.Authority = "https://dummy.authority.for.setup";
            options.ClientId = "dummy_client_id";
            options.RequireHttpsMetadata = false;
            options.CallbackPath = "/signin-oidc";
            options.SignedOutCallbackPath = "/signout-callback-oidc";

            options.CorrelationCookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
            options.CorrelationCookie.SameSite = SameSiteMode.Lax;
            options.NonceCookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
            options.NonceCookie.SameSite = SameSiteMode.Lax;

            options.ResponseType = "code";
            options.ResponseMode = "query";
            options.SaveTokens = false;
            options.Scope.Add("openid");
            options.Scope.Add("profile");
            options.Scope.Add("email");
            options.TokenValidationParameters.NameClaimType = "name";

            options.Events = new OpenIdConnectEvents
            {
                OnRedirectToIdentityProvider = context =>
                {
                    var rewritten = RewriteUriToPublicUrl(context.HttpContext, context.ProtocolMessage.RedirectUri, "RedirectUri");
                    if (rewritten != null)
                    {
                        context.ProtocolMessage.RedirectUri = rewritten;
                    }
                    return Task.CompletedTask;
                },
                OnRedirectToIdentityProviderForSignOut = async context =>
                {
                    var idToken = context.Properties?.GetParameter<string>("spokes_id_token");
                    if (string.IsNullOrEmpty(idToken))
                    {
                        var sessionId = context.HttpContext.User.FindFirst("SessionId")?.Value;
                        if (!string.IsNullOrEmpty(sessionId))
                        {
                            var deviceSessions = context.HttpContext.RequestServices.GetRequiredService<DeviceSessionRepository>();
                            var session = deviceSessions.GetById(sessionId);
                            if (session != null && !string.IsNullOrEmpty(session.IdToken))
                            {
                                idToken = session.IdToken;
                            }
                        }
                    }

                    if (!string.IsNullOrEmpty(idToken))
                    {
                        context.ProtocolMessage.IdTokenHint = idToken;
                    }

                    var rewrittenPostLogout = RewriteUriToPublicUrl(context.HttpContext, context.ProtocolMessage.PostLogoutRedirectUri, "PostLogoutRedirectUri");
                    if (rewrittenPostLogout != null)
                    {
                        context.ProtocolMessage.PostLogoutRedirectUri = rewrittenPostLogout;
                    }

                    context.HandleResponse();
                    context.Response.StatusCode = 200;
                    context.Response.Headers.Remove("Location");

                    var html = MobileSsoHelper.CreateAutoSubmitLogoutFormPage(
                        context.ProtocolMessage.IssuerAddress,
                        context.ProtocolMessage.IdTokenHint,
                        context.ProtocolMessage.PostLogoutRedirectUri,
                        context.ProtocolMessage.State ?? "");

                    context.Response.ContentType = "text/html";
                    await context.Response.WriteAsync(html);
                },
                OnRemoteFailure = context =>
                {
                    var redirectUrl = "/sso/login/auto";
                    if (context.Properties?.RedirectUri != null)
                    {
                        redirectUrl += $"?returnUrl={Uri.EscapeDataString(context.Properties.RedirectUri)}&sso_error=1";
                    }
                    else
                    {
                        redirectUrl += "?sso_error=1";
                    }
                    context.Response.Redirect(redirectUrl);
                    context.HandleResponse();
                    return Task.CompletedTask;
                },
                OnTokenValidated = async context =>
                {
                    var principal = context.Principal;
                    if (principal == null) return;

                    var sub = principal.FindFirst("sub")?.Value
                              ?? principal.FindFirst(ClaimTypes.NameIdentifier)?.Value;
                    var name = principal.FindFirst("name")?.Value ?? "Unknown User";
                    var email = principal.FindFirst("email")?.Value
                                ?? principal.FindFirst(ClaimTypes.Email)?.Value
                                ?? "";

                    if (string.IsNullOrEmpty(sub)) return;

                    var userService = context.HttpContext.RequestServices.GetRequiredService<UserService>();
                    var (_, existingUser, _) = userService.ProvisionOrSyncOidcUser(sub, name, email);

                    if (existingUser != null && !existingUser.IsActive)
                    {
                        context.Fail("User account is deactivated.");
                        return;
                    }

                    ClaimsIdentity newIdentity;
                    var deviceId = context.Properties?.Items.ContainsKey("DeviceId") == true ? context.Properties.Items["DeviceId"] : null;
                    var sessionService = context.HttpContext.RequestServices.GetRequiredService<SessionService>();

                    if (existingUser != null)
                    {
                        var (session, _) = sessionService.CreateSession(
                            context.HttpContext, existingUser.Id, deviceId,
                            issueCookie: !existingUser.IsBanned, idToken: context.TokenEndpointResponse?.IdToken);

                        newIdentity = sessionService.BuildIdentity(existingUser, session.Id);
                    }
                    else
                    {
                        newIdentity = new ClaimsIdentity(CookieAuthenticationDefaults.AuthenticationScheme);
                        newIdentity.AddClaim(new Claim("sub", sub));
                        newIdentity.AddClaim(new Claim("name", name));
                        newIdentity.AddClaim(new Claim("email", email));
                        newIdentity.AddClaim(new Claim("PendingApproval", "true"));
                        
                        var (session, _) = sessionService.CreateSession(
                            context.HttpContext, sub, deviceId,
                            issueCookie: false, idToken: context.TokenEndpointResponse?.IdToken);
                        newIdentity.AddClaim(new Claim("SessionId", session.Id));
                    }

                    context.Properties ??= new AuthenticationProperties();
                    context.Properties.IsPersistent = true;
                    context.Properties.ExpiresUtc = DateTimeOffset.UtcNow.AddDays(1);

                    if (existingUser == null)
                    {
                        context.Properties.RedirectUri = "/access-pending";
                    }
                    else if (existingUser.IsBanned)
                    {
                        context.Properties.RedirectUri = "/banned";
                    }

                    context.Principal = new ClaimsPrincipal(newIdentity);

                    // Successfully validated and established session — delete attempt tracking cookie
                    context.HttpContext.Response.Cookies.Delete("Spokes_SSO_Attempt");

                    await Task.CompletedTask;
                }
            };
        })
        .AddScheme<DeviceTokenAuthOptions, DeviceTokenAuthHandler>("DeviceToken", null)
        .AddPolicyScheme("Smart", "Smart", options =>
        {
            options.ForwardDefaultSelector = context =>
            {
                var auth = context.Request.Headers.Authorization.FirstOrDefault();
                if (auth?.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) == true)
                    return "DeviceToken";

                if (context.Request.Cookies.ContainsKey("Spokes_Session_v3"))
                    return CookieAuthenticationDefaults.AuthenticationScheme;

                if (context.Request.Cookies.ContainsKey("Spokes_Refresh"))
                    return "DeviceToken";

                return CookieAuthenticationDefaults.AuthenticationScheme;
            };
        });

        services.AddOptions<CookieAuthenticationOptions>(CookieAuthenticationDefaults.AuthenticationScheme)
            .Configure<ITicketStore>((options, ticketStore) =>
            {
                options.SessionStore = ticketStore;
            });

        services.AddScoped<IAuthorizationHandler, PermissionAuthorizationHandler>();

        services.AddAuthorization(options =>
        {
            options.AddPolicy(AppPermissions.Admin.PolicyName, policy =>
                policy.Requirements.Add(new PermissionRequirement(AppPermissions.Admin.RoleName)));

            foreach (var group in AppPermissions.GetAll())
            {
                foreach (var permission in group.Value)
                {
                    options.AddPolicy(permission, policy =>
                        policy.Requirements.Add(new PermissionRequirement(permission)));
                }
            }
        });

        return services;
    }

    private static string? RewriteUriToPublicUrl(HttpContext httpContext, string? targetUri, string label)
    {
        if (targetUri == null) return null;
        var sysConfig = httpContext.RequestServices.GetRequiredService<SystemConfigRepository>().Get();
        if (string.IsNullOrWhiteSpace(sysConfig.ServerPublicUrl)) return null;

        try
        {
            var redirectUri = new UriBuilder(targetUri);
            var publicUri = new Uri(sysConfig.ServerPublicUrl);
            redirectUri.Scheme = publicUri.Scheme;
            redirectUri.Host = publicUri.Host;
            redirectUri.Port = publicUri.IsDefaultPort ? -1 : publicUri.Port;
            return redirectUri.ToString();
        }
        catch (Exception ex)
        {
            httpContext.RequestServices.GetService<ILoggerFactory>()?.CreateLogger("OpenIdConnect")?.LogWarning(ex, "[OIDC] Failed to rewrite {Label}: {Message}", label, ex.Message);
            return null;
        }
    }

    private static bool IsBlockedTransportPath(string path) =>
        path.StartsWith("/_blazor", StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith("/hubs", StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith("/media", StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith("/internal", StringComparison.OrdinalIgnoreCase);

    private static bool IsRevokedBlockedPath(string path)
    {
        if (string.IsNullOrEmpty(path)) return false;

        if (path.StartsWith("/spokesapi", StringComparison.OrdinalIgnoreCase))
        {
            return !path.Equals("/spokesapi/auth/force-logout", StringComparison.OrdinalIgnoreCase);
        }

        return IsBlockedTransportPath(path);
    }

    private static bool IsRestrictedPrincipalBlockedPath(string path)
    {
        if (string.IsNullOrEmpty(path)) return false;

        if (path.StartsWith("/spokesapi", StringComparison.OrdinalIgnoreCase))
        {
            return !path.Equals("/spokesapi/system/logo", StringComparison.OrdinalIgnoreCase) &&
                   !path.Equals("/spokesapi/system/icon", StringComparison.OrdinalIgnoreCase) &&
                   !path.Equals("/spokesapi/auth/force-logout", StringComparison.OrdinalIgnoreCase);
        }

        return IsBlockedTransportPath(path);
    }

    private static bool IsSharedRestrictedAllowedRoute(string path) =>
        path.Equals("/logout", StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith("/branding", StringComparison.OrdinalIgnoreCase) ||
        path.Equals("/spokesapi/system/logo", StringComparison.OrdinalIgnoreCase) ||
        path.Equals("/spokesapi/system/icon", StringComparison.OrdinalIgnoreCase) ||
        path.Equals("/spokesapi/auth/force-logout", StringComparison.OrdinalIgnoreCase) ||
        IsStaticAssetPath(path);

    private static bool IsAllowedPendingRoute(string path) =>
        path.Equals("/access-pending", StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith("/sso", StringComparison.OrdinalIgnoreCase) ||
        IsSharedRestrictedAllowedRoute(path);

    private static bool IsAllowedBannedRoute(string path) =>
        path.Equals("/banned", StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith("/sso/logout", StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith("/sso/mobile-logout", StringComparison.OrdinalIgnoreCase) ||
        IsSharedRestrictedAllowedRoute(path);

    private static bool IsStaticAssetPath(string path)
    {
        return path.StartsWith("/css/", StringComparison.OrdinalIgnoreCase) ||
               path.StartsWith("/js/", StringComparison.OrdinalIgnoreCase) ||
               path.StartsWith("/lib/", StringComparison.OrdinalIgnoreCase) ||
               path.StartsWith("/fonts/", StringComparison.OrdinalIgnoreCase) ||
               path.StartsWith("/_content/", StringComparison.OrdinalIgnoreCase) ||
               path.StartsWith("/_framework/", StringComparison.OrdinalIgnoreCase) ||
               path.Equals("/favicon.ico", StringComparison.OrdinalIgnoreCase);
    }
}
