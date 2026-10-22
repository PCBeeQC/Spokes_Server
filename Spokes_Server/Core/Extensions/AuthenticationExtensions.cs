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

            options.Events.OnValidatePrincipal = context =>
            {
                var sessionId = context.Principal?.FindFirst("SessionId")?.Value;
                var employeeId = context.Principal?.FindFirst("EmployeeId")?.Value;
                var employees = context.HttpContext.RequestServices.GetRequiredService<EmployeeRepository>();
                var deviceSessions = context.HttpContext.RequestServices.GetRequiredService<DeviceSessionRepository>();

                if (!string.IsNullOrEmpty(employeeId) && employeeId != "system-renderer")
                {
                    var employee = employees.GetById(employeeId);
                    if (employee == null || !employee.IsActive)
                    {
                        context.RejectPrincipal();
                        return Task.CompletedTask;
                    }

                    if (employee.IsBanned)
                    {
                        var path = context.HttpContext.Request.Path.Value ?? "";
                        if (!path.Equals("/banned", StringComparison.OrdinalIgnoreCase) &&
                            !path.Equals("/logout", StringComparison.OrdinalIgnoreCase) &&
                            !path.StartsWith("/_blazor", StringComparison.OrdinalIgnoreCase) &&
                            !path.StartsWith("/spokesapi", StringComparison.OrdinalIgnoreCase))
                        {
                            context.HttpContext.Response.Redirect("/banned");
                        }
                        return Task.CompletedTask;
                    }
                }

                if (!string.IsNullOrEmpty(sessionId))
                {
                    var session = deviceSessions.GetById(sessionId);
                    if (session != null)
                    {
                        if (session.RevokedAt != null)
                        {
                            context.Properties.SetParameter("IsRevoked", true);
                        }
                        else
                        {
                            var sessionService = context.HttpContext.RequestServices.GetRequiredService<SessionService>();
                            sessionService.TouchLastSeen(session);
                        }
                    }
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
                    var systemConfigs = context.HttpContext.RequestServices.GetRequiredService<SystemConfigRepository>();
                    var sysConfig = systemConfigs.Get();
                    if (!string.IsNullOrWhiteSpace(sysConfig.ServerPublicUrl) && context.ProtocolMessage.RedirectUri != null)
                    {
                        try
                        {
                            var redirectUri = new UriBuilder(context.ProtocolMessage.RedirectUri);
                            var publicUri = new Uri(sysConfig.ServerPublicUrl);

                            redirectUri.Scheme = publicUri.Scheme;
                            redirectUri.Host = publicUri.Host;
                            redirectUri.Port = publicUri.IsDefaultPort ? -1 : publicUri.Port;

                            context.ProtocolMessage.RedirectUri = redirectUri.ToString();
                        }
                        catch (Exception ex)
                        {
                            context.HttpContext.RequestServices.GetService<ILoggerFactory>()?.CreateLogger("OpenIdConnect")?.LogWarning(ex, "[OIDC] Failed to rewrite RedirectUri: {Message}", ex.Message);
                        }
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

                    var systemConfigs = context.HttpContext.RequestServices.GetRequiredService<SystemConfigRepository>();
                    var sysConfig = systemConfigs.Get();
                    if (!string.IsNullOrWhiteSpace(sysConfig.ServerPublicUrl) && context.ProtocolMessage.PostLogoutRedirectUri != null)
                    {
                        try
                        {
                            var redirectUri = new UriBuilder(context.ProtocolMessage.PostLogoutRedirectUri);
                            var publicUri = new Uri(sysConfig.ServerPublicUrl);

                            redirectUri.Scheme = publicUri.Scheme;
                            redirectUri.Host = publicUri.Host;
                            redirectUri.Port = publicUri.IsDefaultPort ? -1 : publicUri.Port;
                            context.ProtocolMessage.PostLogoutRedirectUri = redirectUri.ToString();
                        }
                        catch (Exception ex)
                        {
                            context.HttpContext.RequestServices.GetService<ILoggerFactory>()?.CreateLogger("OpenIdConnect")?.LogWarning(ex, "[OIDC] Failed to rewrite PostLogoutRedirectUri: {Message}", ex.Message);
                        }
                    }

                    context.HandleResponse();
                    context.Response.StatusCode = 200;
                    context.Response.Headers.Remove("Location");

                    var html = $@"
                        <!DOCTYPE html>
                        <html>
                        <head>
                            <meta charset='utf-8' />
                            <meta name='viewport' content='width=device-width, initial-scale=1, maximum-scale=1, user-scalable=0'>
                            <title>Logging out...</title>
                            <style>
                                body {{ background-color: #1e1e2d; color: white; display: flex; justify-content: center; align-items: center; height: 100vh; margin: 0; font-family: 'Roboto', sans-serif; }}
                                .spinner {{ width: 40px; height: 40px; border: 4px solid rgba(255,255,255,0.3); border-top: 4px solid #594ae2; border-radius: 50%; animation: spin 1s linear infinite; margin-bottom: 20px; }}
                                @keyframes spin {{ 0% {{ transform: rotate(0deg); }} 100% {{ transform: rotate(360deg); }} }}
                            </style>
                        </head>
                        <body>
                            <div style='display:flex;flex-direction:column;align-items:center;text-align:center;padding:40px;'>
                                <div class='spinner'></div>
                                <p>Securely signing out...</p>
                            </div>
                            <form id='logoutForm' method='GET' action='{System.Text.Encodings.Web.HtmlEncoder.Default.Encode(context.ProtocolMessage.IssuerAddress ?? "")}'>
                                <input type='hidden' name='id_token_hint' value='{System.Text.Encodings.Web.HtmlEncoder.Default.Encode(context.ProtocolMessage.IdTokenHint ?? "")}' />
                                <input type='hidden' name='post_logout_redirect_uri' value='{System.Text.Encodings.Web.HtmlEncoder.Default.Encode(context.ProtocolMessage.PostLogoutRedirectUri ?? "")}' />
                                <input type='hidden' name='state' value='{System.Text.Encodings.Web.HtmlEncoder.Default.Encode(context.ProtocolMessage.State ?? "")}' />
                            </form>
                            <script>document.getElementById('logoutForm').submit();</script>
                        </body>
                        </html>";

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
                            issueCookie: true, idToken: context.TokenEndpointResponse?.IdToken);

                        newIdentity = sessionService.BuildIdentity(existingUser, session.Id);
                    }
                    else
                    {
                        newIdentity = new ClaimsIdentity(CookieAuthenticationDefaults.AuthenticationScheme);
                        newIdentity.AddClaim(new Claim("sub", sub));
                        newIdentity.AddClaim(new Claim("name", name));
                        newIdentity.AddClaim(new Claim("email", email));
                        
                        var (session, _) = sessionService.CreateSession(
                            context.HttpContext, sub, deviceId,
                            issueCookie: true, idToken: context.TokenEndpointResponse?.IdToken);
                        newIdentity.AddClaim(new Claim("SessionId", session.Id));
                    }

                    context.Properties ??= new AuthenticationProperties();
                    context.Properties.IsPersistent = true;
                    context.Properties.ExpiresUtc = DateTimeOffset.UtcNow.AddDays(1);

                    if (existingUser?.IsBanned == true)
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
}
