using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Microsoft.AspNetCore.Localization;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Spokes_Server.Core.Data.Repositories.Core;
using Spokes_Server.Core.Middleware;
using Spokes_Server.Core.Services.Communication.Chat;
using Spokes_Server.Core.Services.Identity;
using Spokes_Server.Core.Services.UI;

namespace Spokes_Server.Core.Extensions;

public static class SpokesMiddlewareExtensions
{
    /// <summary>
    /// Captures startup correlation IDs, client timing metrics, and diagnostic headers for auth paths.
    /// </summary>
    public static IApplicationBuilder UseSpokesDiagnostics(this IApplicationBuilder app)
    {
        return app.Use(async (context, next) =>
        {
            var path = context.Request.Path.Value?.ToLowerInvariant() ?? "";

            string? startupId = null;
            string? cTimes = null;

            if (context.Request.Query.TryGetValue("startup_id", out var startupIdQuery))
            {
                var rawStr = startupIdQuery.ToString();
                if (rawStr.Length < 100 && Regex.IsMatch(rawStr, @"^[a-zA-Z0-9\-_]+$"))
                {
                    startupId = rawStr;
                }
            }
            else if (context.Request.Cookies.TryGetValue("Spokes_Startup_Id", out var startupIdCookie))
            {
                if (startupIdCookie != null && startupIdCookie.Length < 100 && Regex.IsMatch(startupIdCookie, @"^[a-zA-Z0-9\-_]+$"))
                {
                    startupId = startupIdCookie;
                }
            }

            if (context.Request.Query.TryGetValue("c_times", out var cTimesQuery))
            {
                var rawStr = cTimesQuery.ToString();
                if (rawStr.Length < 500 && Regex.IsMatch(rawStr, @"^[a-zA-Z0-9,_:\.\-]+$"))
                {
                    cTimes = rawStr;
                }
            }
            else if (context.Request.Cookies.TryGetValue("Spokes_Client_Times", out var cTimesCookie))
            {
                if (cTimesCookie != null && cTimesCookie.Length < 500 && Regex.IsMatch(cTimesCookie, @"^[a-zA-Z0-9,_:\.\-]+$"))
                {
                    cTimes = cTimesCookie;
                }
            }

            if (string.IsNullOrEmpty(startupId) && string.IsNullOrEmpty(Path.GetExtension(path))
                && !path.StartsWith("/api") && !path.StartsWith("/spokesapi") && !path.StartsWith("/hubs") && !path.StartsWith("/_blazor"))
            {
                startupId = $"st-{Guid.NewGuid().ToString("N")[..8]}";
            }

            if (!string.IsNullOrEmpty(startupId))
            {
                context.Items["StartupCorrelationId"] = startupId;
            }

            if (!string.IsNullOrEmpty(cTimes))
            {
                context.Items["ClientLocalTimings"] = cTimes;
            }

            await next(context);
        });
    }

    /// <summary>
    /// Configures caching, no-transform headers to protect SRI integrity, TLS preconnect hints, and razor.js CDN bypass.
    /// </summary>
    public static IApplicationBuilder UseSpokesResponseHeaders(this IApplicationBuilder app)
    {
        // 1. Prevent browsers from caching HTML document responses & prevent Cloudflare from altering JS/CSS
        app.Use(async (context, next) =>
        {
            context.Response.OnStarting(() =>
            {
                var path = context.Request.Path.Value?.ToLowerInvariant() ?? "";
                bool isHtml = context.Response.ContentType?.StartsWith("text/html", StringComparison.OrdinalIgnoreCase) == true;

                bool isLikelyPage = string.IsNullOrEmpty(Path.GetExtension(path))
                                    && !path.StartsWith("/api")
                                    && !path.StartsWith("/spokesapi")
                                    && !path.StartsWith("/hubs")
                                    && !path.StartsWith("/_blazor");

                if (isHtml || isLikelyPage)
                {
                    context.Response.Headers["Cache-Control"] = "no-cache, no-store, must-revalidate, no-transform";
                    context.Response.Headers["Pragma"] = "no-cache";
                    context.Response.Headers["Expires"] = "0";
                }
                else if (context.Response.ContentType?.StartsWith("application/javascript", StringComparison.OrdinalIgnoreCase) == true ||
                         context.Response.ContentType?.StartsWith("text/javascript", StringComparison.OrdinalIgnoreCase) == true ||
                         context.Response.ContentType?.StartsWith("text/css", StringComparison.OrdinalIgnoreCase) == true)
                {
                    var cc = context.Response.Headers["Cache-Control"].ToString();
                    if (!string.IsNullOrEmpty(cc) && !cc.Contains("no-transform"))
                    {
                        context.Response.Headers["Cache-Control"] = cc + ", no-transform";
                    }
                    else if (string.IsNullOrEmpty(cc))
                    {
                        context.Response.Headers["Cache-Control"] = "no-cache, must-revalidate, no-transform";
                    }
                }
                return Task.CompletedTask;
            });

            await next();
        });

        // 2. TLS preconnect hint for mobile clients
        app.Use(async (context, next) =>
        {
            context.Response.OnStarting(() =>
            {
                var ct = context.Response.ContentType;
                if (ct != null && ct.StartsWith("text/html", StringComparison.OrdinalIgnoreCase))
                {
                    var origin = $"{context.Request.Scheme}://{context.Request.Host}";
                    context.Response.Headers.Append("Link", $"<{origin}>; rel=preconnect");
                }
                return Task.CompletedTask;
            });
            await next();
        });

        // 3. Prevent CDN caching for collocated Blazor JS files
        app.Use(async (context, next) =>
        {
            context.Response.OnStarting(() =>
            {
                if (context.Request.Path.Value != null && context.Request.Path.Value.EndsWith(".razor.js", StringComparison.OrdinalIgnoreCase))
                {
                    context.Response.Headers.CacheControl = "no-cache, no-store, must-revalidate";
                    context.Response.Headers.Pragma = "no-cache";
                    context.Response.Headers.Expires = "-1";
                }
                return Task.CompletedTask;
            });
            await next();
        });

        return app;
    }

    /// <summary>
    /// Enforces wizard redirection if setup is incomplete, or redirects /setup to root when complete.
    /// </summary>
    public static IApplicationBuilder UseSpokesSetupRedirect(this IApplicationBuilder app)
    {
        return app.Use(async (context, next) =>
        {
            var path = context.Request.Path.Value?.ToLowerInvariant() ?? "";

            if (path.StartsWith("/_blazor") ||
                path.StartsWith("/_framework") ||
                path.StartsWith("/css") ||
                path.StartsWith("/fonts") ||
                path.StartsWith("/_content") ||
                path.StartsWith("/js") ||
                path.EndsWith(".png") ||
                path.EndsWith(".ico") ||
                path.EndsWith(".svg") ||
                path.EndsWith(".woff2") ||
                path.EndsWith(".woff") ||
                path.EndsWith(".ttf") ||
                path.EndsWith(".jpg") ||
                path.EndsWith(".jpeg") ||
                path.EndsWith(".webp"))
            {
                await next(context);
                return;
            }

            var systemConfigs = context.RequestServices.GetRequiredService<SystemConfigRepository>();
            var sysConfig = systemConfigs.Get();
            if (!sysConfig.IsSetupComplete)
            {
                if (!path.StartsWith("/setup"))
                {
                    context.Response.Redirect("/setup");
                    return;
                }
            }
            else
            {
                if (path.StartsWith("/setup"))
                {
                    context.Response.Redirect("/");
                    return;
                }

                if (path == "/" || path == "")
                {
                    context.Response.Cookies.Delete("Spokes_SSO_Attempt");
                }
            }

            await next(context);
        });
    }

    /// <summary>
    /// Checks for server-side session revocation marked by OnValidatePrincipal and handles redirect/401.
    /// </summary>
    public static IApplicationBuilder UseSpokesSessionRevocation(this IApplicationBuilder app)
    {
        return app.Use(async (context, next) =>
        {
            var path = context.Request.Path.Value?.ToLowerInvariant() ?? "";

            if (path.StartsWith("/spokesapi/auth/force-logout"))
            {
                await next();
                return;
            }

            var authResult = await context.AuthenticateAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            if (authResult?.Properties?.GetParameter<bool>("IsRevoked") == true)
            {
                bool isHtmlPage = !path.StartsWith("/api") &&
                                  !path.StartsWith("/spokesapi") &&
                                  !path.StartsWith("/internal") &&
                                  !path.StartsWith("/hubs") &&
                                  !path.StartsWith("/_blazor");
                if (isHtmlPage && context.Request.Method == "GET")
                {
                    await context.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
                    var redirectUrl = context.Request.PathBase + context.Request.Path + context.Request.QueryString;
                    try
                    {
                        await context.SignOutAsync(OpenIdConnectDefaults.AuthenticationScheme, new AuthenticationProperties { RedirectUri = redirectUrl });
                    }
                    catch (Exception ex)
                    {
                        var logger = context.RequestServices.GetService<ILoggerFactory>()?.CreateLogger("SpokesSessionRevocation");
                        logger?.LogWarning(ex, "OIDC signout fallback during session revocation");
                        context.Response.Redirect("/sso/login");
                    }
                    return;
                }
                else
                {
                    context.Response.StatusCode = 401;
                    return;
                }
            }
            await next();
        });
    }

    /// <summary>
    /// Intercepts mobile native splash cold starts and auto-reopens drafted chat channels.
    /// </summary>
    public static IApplicationBuilder UseSpokesMobileInterceptors(this IApplicationBuilder app)
    {
        // 1. Mobile PWA / Native Splash Screen Interceptor
        app.Use(async (context, next) =>
        {
            var path = context.Request.Path.Value?.ToLowerInvariant() ?? "";
            
            if (context.Request.Method == "GET" && 
                !path.StartsWith("/api") && 
                !path.StartsWith("/spokesapi") && 
                !path.StartsWith("/_blazor") &&
                !path.StartsWith("/_framework") &&
                !path.StartsWith("/sso") &&
                !path.StartsWith("/mobile-login") &&
                !path.StartsWith("/session-expired") &&
                !path.StartsWith("/login") &&
                !path.StartsWith("/signup") &&
                !path.StartsWith("/forget") &&
                !path.StartsWith("/callback") &&
                !path.Contains('.'))
            {
                var userAgent = context.Request.Headers["User-Agent"].ToString();
                bool isCapacitor = userAgent.Contains("Capacitor", StringComparison.OrdinalIgnoreCase) || context.Request.Query["client"] == "mobile";
                bool isAuthenticated = context.User.Identity?.IsAuthenticated == true;
                
                if (isCapacitor && !isAuthenticated)
                {
                    context.Response.ContentType = "text/html; charset=utf-8";
                    await context.Response.WriteAsync(@"
                        <!DOCTYPE html>
                        <html>
                        <head>
                            <meta charset='utf-8' />
                            <meta name='viewport' content='width=device-width, initial-scale=1.0, maximum-scale=1.0, user-scalable=no, viewport-fit=cover'>
                            <style>
                                body { background-color: #1e1e2d; margin: 0; padding: 0; }
                            </style>
                        </head>
                        <body>
                            <script>
                                (async function() {
                                    try {
                                        var sp = new URLSearchParams(window.location.search);
                                        var sid = sp.get('startup_id');
                                        var ct = sp.get('c_times');
                                        if (sid || ct) {
                                            sessionStorage.setItem('spokes_startup_meta', JSON.stringify({ startupId: sid || '', clientTimings: ct || '' }));
                                        }
                                    } catch(e) {}
                                    if (!window.Capacitor || !window.Capacitor.Plugins || !window.Capacitor.Plugins.Preferences) {
                                        await new Promise(res => setTimeout(res, 50));
                                    }
                                    var hostname = window.location.hostname;
                                    var refreshRes = null;
                                    try {
                                        refreshRes = await window.Capacitor.Plugins.Preferences.get({ key: 'spokes_refresh_' + hostname });
                                        if (!refreshRes || !refreshRes.value) {
                                            refreshRes = await window.Capacitor.Plugins.Preferences.get({ key: 'spokes_refresh' });
                                        }
                                    } catch(e) {}
                                    
                                    if (refreshRes && refreshRes.value) {
                                        var form = document.createElement('form');
                                        form.method = 'POST';
                                        form.action = '/sso/login/mobile-bootstrap';
                                        var rt = document.createElement('input'); rt.type = 'hidden'; rt.name = 'refreshToken'; rt.value = refreshRes.value;
                                        form.appendChild(rt);
                                        var ru = document.createElement('input'); ru.type = 'hidden'; ru.name = 'returnUrl'; ru.value = window.location.pathname + window.location.search + window.location.hash;
                                        form.appendChild(ru);
                                        try {
                                            var devRes = await window.Capacitor.Plugins.Preferences.get({ key: 'spokes_device_id' });
                                            var devId = (devRes && devRes.value) || localStorage.getItem('spokes_device_id') || '';
                                            if ((!devId || devId.startsWith('dev_')) && window.Capacitor.Plugins && window.Capacitor.Plugins.NotificationCrypto && window.Capacitor.Plugins.NotificationCrypto.getStableDeviceId) {
                                                var cryptoRes = await window.Capacitor.Plugins.NotificationCrypto.getStableDeviceId();
                                                if (cryptoRes && cryptoRes.deviceId) {
                                                    devId = cryptoRes.deviceId;
                                                    try { await window.Capacitor.Plugins.Preferences.set({ key: 'spokes_device_id', value: devId }); } catch(e) {}
                                                }
                                            }
                                            if (devId) {
                                                var di = document.createElement('input'); di.type = 'hidden'; di.name = 'deviceId'; di.value = devId;
                                                form.appendChild(di);
                                            }
                                        } catch(e) {}
                                        // Boot diagnostic: lets the app page report this cookie detour and its cost.
                                        try { sessionStorage.setItem('spokes_bootstrap', JSON.stringify({ start: Math.round(performance.timeOrigin) })); } catch(e) {}
                                        document.body.appendChild(form);
                                        form.submit();
                                        return;
                                    }
                                    
                                    window.location.href = '/sso/login/auto?client=mobile&returnUrl=' + encodeURIComponent(window.location.pathname + window.location.search + window.location.hash);
                                })();
                            </script>
                        </body>
                        </html>
                    ");
                    return;
                }
            }
            
            await next();
        });

        return app;
    }

    /// <summary>
    /// Reopens active chat draft for mobile clients on root navigation.
    /// </summary>
    public static IApplicationBuilder UseSpokesMobileDraftAutoReopen(this IApplicationBuilder app)
    {
        return app.Use(async (context, next) =>
        {
            if (HttpMethods.IsGet(context.Request.Method) &&
                (context.Request.Path == "/" || context.Request.Path == "/chat" || string.IsNullOrEmpty(context.Request.Path.Value)))
            {
                var isMobile = string.Equals(context.Request.Query["client"].ToString(), "mobile", StringComparison.OrdinalIgnoreCase)
                    || context.Request.Headers.UserAgent.ToString().Contains("Capacitor", StringComparison.OrdinalIgnoreCase);

                if (isMobile && context.User.Identity?.IsAuthenticated == true)
                {
                    var userService = context.RequestServices.GetRequiredService<UserService>();
                    var employee = userService.GetEmployee(context.User);
                    if (employee != null)
                    {
                        var clientStateService = context.RequestServices.GetRequiredService<UserClientStateService>();
                        var draftChannelId = clientStateService.GetActiveDraftChannel(employee.Id);
                        if (!string.IsNullOrEmpty(draftChannelId))
                        {
                            clientStateService.MarkDraftAsAutoOpened(employee.Id, draftChannelId);
                            
                            var queryString = context.Request.QueryString.HasValue ? context.Request.QueryString.Value : "";
                            context.Response.Redirect($"/chat/{draftChannelId}{queryString}");
                            return;
                        }
                    }
                }
            }

            await next();
        });
    }

    /// <summary>
    /// Handles Casdoor RP-initiated logout callback redirects when state parameter is dropped.
    /// </summary>
    public static IApplicationBuilder UseSpokesCasdoorSignoutCallbackRedirect(this IApplicationBuilder app)
    {
        return app.Use(async (context, next) =>
        {
            if (context.Request.Path == "/signout-callback-oidc")
            {
                context.Response.Redirect("/");
                return;
            }
            await next();
        });
    }

    /// <summary>
    /// Configures ASP.NET Core RequestLocalization with standard culture providers
    /// and a fallback provider checking authenticated user's Employee.PreferredLanguage.
    /// </summary>
    public static IApplicationBuilder UseSpokesLocalization(this IApplicationBuilder app)
    {
        var supportedUICultures = new[]
        {
            new CultureInfo("en-US"),
            new CultureInfo("en"),
            new CultureInfo("de-DE"),
            new CultureInfo("de"),
            new CultureInfo("fr-FR"),
            new CultureInfo("fr")
        };

        var options = new RequestLocalizationOptions
        {
            DefaultRequestCulture = new RequestCulture(culture: "en-US", uiCulture: "en-US"),
            SupportedCultures = new[] { new CultureInfo("en-US") },
            SupportedUICultures = supportedUICultures
        };

        // User preference fallback provider:
        // If cookie is absent on a new device, read Employee.PreferredLanguage from DB
        options.RequestCultureProviders.Insert(2, new CustomRequestCultureProvider(context =>
        {
            if (context.User.Identity?.IsAuthenticated == true)
            {
                var userService = context.RequestServices.GetService<UserService>();
                var employee = userService?.GetEmployee(context.User);
                if (employee != null && !string.IsNullOrWhiteSpace(employee.PreferredLanguage))
                {
                    return Task.FromResult<ProviderCultureResult?>(
                        new ProviderCultureResult(culture: "en-US", uiCulture: employee.PreferredLanguage));
                }
            }
            return Task.FromResult<ProviderCultureResult?>(null);
        }));

        app.UseRequestLocalization(options);

        // Apply Company DateFormat to incoming requests and thread culture
        app.Use(async (context, next) =>
        {
            var companyProfiles = context.RequestServices.GetService<CompanyProfileRepository>();
            var dateFormat = companyProfiles?.Get()?.DateFormat;
            CultureCircuitHandler.ApplyDateFormat(dateFormat);
            await next();
        });

        return app;
    }
}

