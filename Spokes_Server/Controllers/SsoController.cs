using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Spokes_Server.Aggregate;
using Spokes_Server.Aggregate.Contexts;
using Spokes_Server.Core.Models.Core;
using Spokes_Server.Core.Security;
using Spokes_Server.Core.Services.Security;

namespace Spokes_Server.Controllers;

/// <summary>
/// Controller for Single Sign-On (SSO), mobile authentication bootstrap, and logout workflows.
/// Consolidates all SSO authentication endpoints into dedicated MVC controller actions.
/// </summary>
[AllowAnonymous]
public class SsoController : SpokesControllerBase
{
    private readonly CoreContext _coreContext;
    private readonly HrContext _hrContext;
    private readonly SessionService _sessionService;
    private readonly IConfiguration _config;
    private readonly IMemoryCache _cache;
    private readonly IOptionsMonitor<OpenIdConnectOptions> _oidcOptionsMonitor;
    private readonly ILogger<SsoController> _logger;

    [ActivatorUtilitiesConstructor]
    public SsoController(
        CoreContext coreContext,
        HrContext hrContext,
        SessionService sessionService,
        IConfiguration config,
        IMemoryCache cache,
        IOptionsMonitor<OpenIdConnectOptions> oidcOptionsMonitor,
        ILogger<SsoController> logger)
    {
        _coreContext = coreContext ?? throw new ArgumentNullException(nameof(coreContext));
        _hrContext = hrContext ?? throw new ArgumentNullException(nameof(hrContext));
        _sessionService = sessionService;
        _config = config;
        _cache = cache;
        _oidcOptionsMonitor = oidcOptionsMonitor;
        _logger = logger;
    }

    /// <summary>
    /// Mobile Bootstrap Endpoint. Validates refresh token from form and issues session cookie.
    /// </summary>
    [HttpPost("/sso/login/mobile-bootstrap")]
    public async Task<IActionResult> MobileBootstrap(
        [FromForm] string? refreshToken,
        [FromForm] string? deviceId,
        [FromForm] string? returnUrl)
    {
        var requestHost = $"{Request.Scheme}://{Request.Host.Value}";
        if (Request.Headers.TryGetValue("Sec-Fetch-Site", out var fetchSite))
        {
            if (fetchSite == "cross-site" || fetchSite == "same-site")
            {
                return BadRequestError("Invalid cross-site request.", "INVALID_CROSS_SITE");
            }
        }
        if (Request.Headers.TryGetValue("Origin", out var origin) && !string.IsNullOrEmpty(origin))
        {
            if (!string.Equals(origin, requestHost, StringComparison.OrdinalIgnoreCase))
            {
                return BadRequestError("Invalid origin header.", "INVALID_ORIGIN");
            }
        }
        else if (Request.Headers.TryGetValue("Referer", out var referer) && !string.IsNullOrEmpty(referer))
        {
            if (!referer.ToString().StartsWith(requestHost, StringComparison.OrdinalIgnoreCase))
            {
                return BadRequestError("Invalid referer header.", "INVALID_REFERER");
            }
        }

        var target = RedirectHelper.GetSafeRedirectUrl(returnUrl, HttpContext);

        if (string.IsNullOrEmpty(refreshToken))
        {
            return Redirect($"/mobile-login?returnUrl={Uri.EscapeDataString(target)}");
        }

        var result = _sessionService.ValidateToken(refreshToken, deviceId);
        
        if (result != null)
        {
            await _sessionService.SignInAndExtendAsync(HttpContext, result.Value.Session, result.Value.Employee, deviceId);
            _logger.LogInformation("[SSO-BOOTSTRAP] Mobile bootstrap successful for: {FullName}", result.Value.Employee.FullName);

            if (target == "/" || target.StartsWith("/?", StringComparison.OrdinalIgnoreCase))
            {
                var clientStateService = HttpContext?.RequestServices?.GetService<Spokes_Server.Core.Services.Communication.Chat.IUserClientStateService>();
                var draftChannelId = clientStateService?.GetActiveDraftChannel(result.Value.Employee.Id);
                if (!string.IsNullOrEmpty(draftChannelId))
                {
                    clientStateService!.MarkDraftAsAutoOpened(result.Value.Employee.Id, draftChannelId);
                    var qIdx = target.IndexOf('?');
                    var queryString = qIdx >= 0 ? target[qIdx..] : "?client=mobile";
                    target = $"/chat/{draftChannelId}{queryString}";
                }
            }

            var encodedTarget = JavaScriptEncoder.Default.Encode(target);
            var html = MobileSsoHelper.CreateJsRedirectPage(encodedTarget);
            return Content(html, "text/html");
        }

        _logger.LogWarning("[SSO-BOOTSTRAP] Mobile bootstrap failed. Token invalid or expired.");
        return Redirect($"/mobile-login?returnUrl={Uri.EscapeDataString(target)}");
    }

    /// <summary>
    /// Auto-Login Endpoint. Attempts silent renewal, demo mode auto-login, or challenges OIDC IdP.
    /// </summary>
    [HttpGet("/sso/login/auto")]
    public async Task<IActionResult> LoginAuto(
        [FromQuery] string? returnUrl,
        [FromQuery] string? deviceId,
        [FromQuery] string? sso_error)
    {
        _logger.LogInformation("[SSO] /sso/login/auto invoked. returnUrl: {ReturnUrl}", returnUrl);
        var target = RedirectHelper.GetSafeRedirectUrl(returnUrl, HttpContext);

        if (User.Identity?.IsAuthenticated == true)
        {
            return Redirect(target);
        }

        // Demo mode auto-login
        if (_config.GetValue<bool>("Spokes_DemoMode") && Request.Query["admin_login"] != "1")
        {
            var demoConfig = _coreContext.DemoConfigs.Get();
            if (demoConfig.AutoLoginEmployeeIds.Any())
            {
                var random = new Random();
                var employeeId = demoConfig.AutoLoginEmployeeIds[random.Next(demoConfig.AutoLoginEmployeeIds.Count)];
                var employee = _hrContext.Employees.GetById(employeeId);
                
                if (employee != null && employee.IsActive && !employee.IsSuspended && !employee.IsBanned)
                {
                    var devId = deviceId ?? Request.Headers["X-Device-Id"].FirstOrDefault() ?? Request.Cookies["Spokes_Device"];
                    
                    var (deviceSession, _) = _sessionService.CreateSession(
                        HttpContext, employee.Id, devId, issueCookie: true);
                    
                    await _sessionService.SignInAndExtendAsync(HttpContext, deviceSession, employee);
                    return Redirect(target);
                }
            }
        }

        // Refresh token auto-renewal
        if (Request.Cookies.TryGetValue("Spokes_Refresh", out var refreshToken) && !string.IsNullOrEmpty(refreshToken))
        {
            _logger.LogInformation("[SSO] Found Spokes_Refresh cookie. Attempting silent renewal...");
            var reqDeviceId = deviceId ?? Request.Headers["X-Device-Id"].FirstOrDefault() ?? Request.Cookies["Spokes_Device"];
            var result = _sessionService.ValidateToken(refreshToken, reqDeviceId);

            if (result != null)
            {
                var (session, employee) = result.Value;
                _sessionService.AdoptLegacySessionDeviceId(session, reqDeviceId);
                _logger.LogInformation("[SSO] Silent renewal successful for: {FullName}", employee.FullName);

                await _sessionService.SignInAndExtendAsync(HttpContext, session, employee, reqDeviceId);

                var refreshCookieOptions = new CookieOptions
                {
                    HttpOnly = true,
                    Secure = Request.IsHttps,
                    SameSite = SameSiteMode.Lax,
                    Path = "/",
                    Expires = DateTimeOffset.UtcNow.AddDays(90)
                };
                Response.Cookies.Append("Spokes_Refresh", refreshToken, refreshCookieOptions);
                Response.Cookies.Delete("Spokes_SSO_Attempt");
                return Redirect(target);
            }

            var refreshDeleteCookieOptions = new CookieOptions
            {
                HttpOnly = true,
                Secure = Request.IsHttps,
                SameSite = SameSiteMode.Lax,
                Path = "/"
            };
            Response.Cookies.Delete("Spokes_Refresh", refreshDeleteCookieOptions);
            _logger.LogWarning("[SSO] Silent renewal failed. Token invalid or expired.");
        }

        var userAgent = Request.Headers["User-Agent"].ToString();
        var isNativeBrowser = Request.Query.ContainsKey("native_browser");
        var isMobileFlow = !isNativeBrowser && (target.Contains("client=mobile", StringComparison.OrdinalIgnoreCase) 
            || userAgent.Contains("Capacitor", StringComparison.OrdinalIgnoreCase));
            
        if (sso_error == "1")
        {
            Response.Cookies.Delete("Spokes_SSO_Attempt");
            if (isMobileFlow)
            {
                return Redirect($"/mobile-login?sso_error=1&returnUrl={Uri.EscapeDataString(target)}");
            }
            return Redirect("/session-expired");
        }

        if (Request.Cookies.ContainsKey("Spokes_SSO_Attempt"))
        {
            Response.Cookies.Delete("Spokes_SSO_Attempt");
            if (isMobileFlow)
            {
                return Redirect($"/mobile-login?no_auto=1&returnUrl={Uri.EscapeDataString(target)}");
            }
            return Redirect("/session-expired");
        }

        if (isMobileFlow)
        {
            _logger.LogInformation("[SSO] Detected mobile client. Redirecting to Blazor /mobile-login page.");
            return Redirect($"/mobile-login?returnUrl={Uri.EscapeDataString(target)}");
        }

        Response.Cookies.Append("Spokes_SSO_Attempt", "1", new CookieOptions
        {
            HttpOnly = true,
            MaxAge = TimeSpan.FromMinutes(5),
            SameSite = SameSiteMode.Lax,
            Secure = Request.IsHttps
        });

        var challengeProps = new AuthenticationProperties
        {
            RedirectUri = target,
            IsPersistent = true
        };
        if (!string.IsNullOrEmpty(deviceId))
            challengeProps.Items["DeviceId"] = deviceId;

        return Challenge(challengeProps, OpenIdConnectDefaults.AuthenticationScheme);
    }

    /// <summary>
    /// Explicit Login Endpoint. Directly triggers OIDC challenge or demo auto-login.
    /// </summary>
    [HttpGet("/sso/login")]
    public async Task<IActionResult> Login(
        [FromQuery] string? returnUrl,
        [FromQuery] string? deviceId)
    {
        var url = RedirectHelper.GetSafeRedirectUrl(returnUrl, HttpContext);

        if (_config.GetValue<bool>("Spokes_DemoMode") && Request.Query["admin_login"] != "1")
        {
            var demoConfig = _coreContext.DemoConfigs.Get();
            if (demoConfig.AutoLoginEmployeeIds.Any())
            {
                var random = new Random();
                var employeeId = demoConfig.AutoLoginEmployeeIds[random.Next(demoConfig.AutoLoginEmployeeIds.Count)];
                var employee = _hrContext.Employees.GetById(employeeId);
                
                if (employee != null && employee.IsActive && !employee.IsSuspended && !employee.IsBanned)
                {
                    var devId = deviceId ?? Request.Headers["X-Device-Id"].FirstOrDefault() ?? Request.Cookies["Spokes_Device"];
                    
                    var (deviceSession, _) = _sessionService.CreateSession(
                        HttpContext, employee.Id, devId, issueCookie: true);
                    
                    await _sessionService.SignInAndExtendAsync(HttpContext, deviceSession, employee);

                    var encodedUrl = JavaScriptEncoder.Default.Encode(url);
                    var html = MobileSsoHelper.CreateJsRedirectPage(encodedUrl);
                    return Content(html, "text/html");
                }
            }
        }

        var props = new AuthenticationProperties
        {
            RedirectUri = url,
            IsPersistent = true
        };
        if (!string.IsNullOrEmpty(deviceId))
            props.Items["DeviceId"] = deviceId;

        return Challenge(props, OpenIdConnectDefaults.AuthenticationScheme);
    }

    /// <summary>
    /// Mobile logout form bridge page for in-browser IdP end-session sign-out.
    /// </summary>
    [HttpGet("/sso/mobile-logout-post")]
    public IActionResult MobileLogoutPost([FromQuery] string reqId)
    {
        var key = $"logout_{reqId}";
        if (_cache.TryGetValue<Tuple<string, string>>(key, out var data) && data != null)
        {
            _cache.Remove(key);
            string idToken = data.Item1;
            string endSessionUrl = data.Item2;

            var serverBase = $"{Request.Scheme}://{Request.Host}";
            var postLogoutRedirect = $"{serverBase}/sso/mobile-logout-complete";

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
                    <form id='logoutForm' method='GET' action='{HtmlEncoder.Default.Encode(endSessionUrl ?? "")}'>
                        <input type='hidden' name='id_token_hint' value='{HtmlEncoder.Default.Encode(idToken)}' />
                        <input type='hidden' name='post_logout_redirect_uri' value='{HtmlEncoder.Default.Encode(postLogoutRedirect)}' />
                    </form>
                    <script>document.getElementById('logoutForm').submit();</script>
                </body>
                </html>";
            return Content(html, "text/html");
        }

        return BadRequestError("Invalid or expired logout request.", "INVALID_LOGOUT_REQUEST");
    }

    /// <summary>
    /// Mobile logout completion callback redirecting to deep-link spokes://logout-complete.
    /// </summary>
    [HttpGet("/sso/mobile-logout-complete")]
    public IActionResult MobileLogoutComplete() => Redirect("spokes://logout-complete");

    /// <summary>
    /// Logout Endpoint. Revokes active session, clears authentication and refresh cookies, and triggers IdP signout.
    /// </summary>
    [HttpPost("/logout")]
    public async Task<IActionResult> Logout()
    {
        var isMobile = false;
        if (Request.HasFormContentType)
        {
            var form = await Request.ReadFormAsync();
            isMobile = form["client"].ToString().Contains("mobile", StringComparison.OrdinalIgnoreCase);
        }

        var handoff = await _sessionService.InitiateLogoutAsync(
            HttpContext,
            revokeActiveSession: true,
            isMobileExplicit: isMobile ? true : null,
            redirectUri: "/");

        return ProcessLogoutHandoff(handoff);
    }
}
