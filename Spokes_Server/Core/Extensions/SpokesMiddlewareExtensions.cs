using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Microsoft.AspNetCore.Localization;
using System.Text.Json;
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
using Spokes_Server.Core.Helpers;
using Spokes_Server.Core.Middleware;
using Spokes_Server.Core.Services.Communication.Chat;
using Spokes_Server.Core.Services.Identity;
using Spokes_Server.Core.Services.UI;

namespace Spokes_Server.Core.Extensions;

public static class SpokesMiddlewareExtensions
{
    private static readonly JsonSerializerOptions SetupErrorJsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Captures startup correlation IDs, client timing metrics, and diagnostic headers for auth paths.
    /// </summary>
    public static IApplicationBuilder UseSpokesDiagnostics(this IApplicationBuilder app)
    {
        return app.Use(async (context, next) =>
        {
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

            if (string.IsNullOrEmpty(startupId) && SpokesRequestClassifier.IsHtmlPageNavigation(context))
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
                context.Response.Headers.TryAdd("X-Content-Type-Options", "nosniff");

                bool isHtml = context.Response.ContentType?.StartsWith("text/html", StringComparison.OrdinalIgnoreCase) == true;
                bool isLikelyPage = string.IsNullOrEmpty(context.Response.ContentType)
                                    && SpokesRequestClassifier.IsHtmlPageNavigation(context);

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
                    var publicUrl = context.RequestServices.GetService<SystemConfigRepository>()?.Get()?.ServerPublicUrl;
                    var origin = Core.Security.ServerUrlResolver.GetSafeServerBaseUrl(context, publicUrl);
                    if (!string.IsNullOrEmpty(origin))
                    {
                        context.Response.Headers.Append("Link", $"<{origin}>; rel=preconnect");
                    }
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
            var requestPath = context.Request.Path;

            if (SpokesRequestClassifier.IsBlazorTransportRequest(requestPath)
                || SpokesRequestClassifier.IsStaticAssetPath(requestPath))
            {
                await next(context);
                return;
            }

            var systemConfigs = context.RequestServices.GetRequiredService<SystemConfigRepository>();
            var sysConfig = systemConfigs.Get();
            if (!sysConfig.IsSetupComplete)
            {
                if (SpokesRequestClassifier.IsAllowedDuringSetup(context))
                {
                    await next(context);
                    return;
                }

                if (SpokesRequestClassifier.IsApiOrNonHtmlRequest(context))
                {
                    context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                    context.Response.ContentType = "application/json; charset=utf-8";
                    context.Response.Headers.CacheControl = "no-store";
                    var payload = SpokesResult.Failure("Server initial setup is not complete.", "SETUP_REQUIRED");
                    await JsonSerializer.SerializeAsync(context.Response.Body, payload, SetupErrorJsonOptions, context.RequestAborted);
                    return;
                }

                context.Response.Redirect("/setup");
                return;
            }

            if (requestPath.StartsWithSegments("/setup", StringComparison.OrdinalIgnoreCase))
            {
                if (!SpokesRequestClassifier.IsApiOrNonHtmlRequest(context))
                {
                    context.Response.Redirect("/");
                    return;
                }
            }
            else if (requestPath == "/" || string.IsNullOrEmpty(requestPath.Value))
            {
                context.Response.Cookies.Delete("Spokes_SSO_Attempt");
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
            if (context.Items.TryGetValue("Spokes.AuthRedirect", out var redirectObj) &&
                redirectObj is string redirectTarget &&
                !string.IsNullOrEmpty(redirectTarget))
            {
                if (context.Response.StatusCode != StatusCodes.Status302Found)
                {
                    context.Response.Redirect(redirectTarget);
                }
                return;
            }

            var path = context.Request.Path.Value?.ToLowerInvariant() ?? "";

            if (path.StartsWith("/spokesapi/auth/force-logout"))
            {
                await next();
                return;
            }

            var authResult = context.Features.Get<IAuthenticateResultFeature>()?.AuthenticateResult ?? await context.AuthenticateAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            bool isRevoked = (context.Items.TryGetValue("Spokes.SessionRevoked", out var revObj) && revObj is true) ||
                             authResult?.Properties?.GetParameter<bool>("IsRevoked") == true;
            if (isRevoked)
            {
                context.Response.Cookies.Delete("Spokes_Refresh", new CookieOptions
                {
                    HttpOnly = true,
                    Secure = context.Request.IsHttps,
                    SameSite = SameSiteMode.Lax,
                    Path = "/"
                });
                context.Response.Cookies.Delete("chat_vault_key", new CookieOptions
                {
                    Path = "/",
                    HttpOnly = true,
                    Secure = true,
                    SameSite = SameSiteMode.Lax
                });

                if (SpokesRequestClassifier.IsHtmlPageNavigation(context) && HttpMethods.IsGet(context.Request.Method))
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
                    await context.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
                    context.Response.StatusCode = 401;
                    return;
                }
            }

            if (context.Items.TryGetValue("Spokes.SessionRejected", out var rejObj) && rejObj is true)
            {
                context.Response.StatusCode = 401;
                return;
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
                !context.Request.Path.StartsWithSegments("/setup", StringComparison.OrdinalIgnoreCase) &&
                !path.StartsWith("/api") && 
                !path.StartsWith("/spokesapi") && 
                !path.StartsWith("/_blazor") &&
                !path.StartsWith("/_framework") &&
                !path.StartsWith("/sso") &&
                !path.StartsWith("/mobile-login") &&
                !path.StartsWith("/session-expired") &&
                !path.StartsWith("/banned") &&
                !path.StartsWith("/access-pending") &&
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
                                    var waitDeadline = Date.now() + 60;
                                    while ((!window.Capacitor || !window.Capacitor.Plugins || !window.Capacitor.Plugins.Preferences) && Date.now() < waitDeadline) {
                                        await new Promise(res => setTimeout(res, 4));
                                    }
                                    var hostname = window.location.hostname;
                                    var prefs = window.Capacitor && window.Capacitor.Plugins && window.Capacitor.Plugins.Preferences;
                                    var refreshRes = null;
                                    var devRes = null;
                                    if (prefs) {
                                        try {
                                            var pair = await Promise.all([
                                                prefs.get({ key: 'spokes_refresh_' + hostname }).catch(function() { return null; }),
                                                prefs.get({ key: 'spokes_device_id' }).catch(function() { return null; })
                                            ]);
                                            refreshRes = pair[0];
                                            devRes = pair[1];
                                            if (!refreshRes || !refreshRes.value) {
                                                refreshRes = await prefs.get({ key: 'spokes_refresh' });
                                            }
                                        } catch(e) {}
                                    }
                                    
                                    if (refreshRes && refreshRes.value) {
                                        var form = document.createElement('form');
                                        form.method = 'POST';
                                        form.action = '/sso/login/mobile-bootstrap';
                                        var rt = document.createElement('input'); rt.type = 'hidden'; rt.name = 'refreshToken'; rt.value = refreshRes.value;
                                        form.appendChild(rt);
                                        var ru = document.createElement('input'); ru.type = 'hidden'; ru.name = 'returnUrl'; ru.value = window.location.pathname + window.location.search + window.location.hash;
                                        form.appendChild(ru);
                                        try {
                                            var devId = (devRes && devRes.value) || localStorage.getItem('spokes_device_id') || '';
                                            if ((!devId || devId.startsWith('dev_')) && window.Capacitor && window.Capacitor.Plugins && window.Capacitor.Plugins.NotificationCrypto && window.Capacitor.Plugins.NotificationCrypto.getStableDeviceId) {
                                                var cryptoRes = await window.Capacitor.Plugins.NotificationCrypto.getStableDeviceId();
                                                if (cryptoRes && cryptoRes.deviceId) {
                                                    devId = cryptoRes.deviceId;
                                                    if (prefs) { try { await prefs.set({ key: 'spokes_device_id', value: devId }); } catch(e) {} }
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

    /// <summary>
    /// In Business Edition, if an authenticated user navigates directly to "/" via HTTP GET
    /// and has a custom LandingPage configured, redirects them immediately (HTTP 302)
    /// to avoid loading the Home dashboard and redirecting.
    /// </summary>
    public static IApplicationBuilder UseSpokesLandingPageRedirect(this IApplicationBuilder app)
    {
        return app.Use(async (context, next) =>
        {
            if (context.Request.Path == "/" && HttpMethods.IsGet(context.Request.Method))
            {
                if (context.User.Identity?.IsAuthenticated == true)
                {
                    var companyProfiles = context.RequestServices.GetService<CompanyProfileRepository>();
                    if (companyProfiles?.Get()?.Edition != "Family")
                    {
                        var userService = context.RequestServices.GetService<UserService>();
                        var employee = userService?.GetEmployee(context.User);
                        if (!string.IsNullOrWhiteSpace(employee?.LandingPage)
                            && employee.LandingPage != ""
                            && employee.LandingPage != "/")
                        {
                            var target = employee.LandingPage.StartsWith('/')
                                ? employee.LandingPage
                                : "/" + employee.LandingPage;
                            context.Response.Redirect(target);
                            return;
                        }
                    }
                }
            }
            await next();
        });
    }
}
