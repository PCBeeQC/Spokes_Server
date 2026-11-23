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
using Spokes_Server.Core.Data.Repositories.HR;
using Spokes_Server.Core.Models.Core;
using Spokes_Server.Core.Models.HR;
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
    private readonly ILogger<SsoController> _logger;

    [ActivatorUtilitiesConstructor]
    public SsoController(
        CoreContext coreContext,
        HrContext hrContext,
        SessionService sessionService,
        IConfiguration config,
        IMemoryCache cache,
        ILogger<SsoController> logger)
    {
        _coreContext = coreContext ?? throw new ArgumentNullException(nameof(coreContext));
        _hrContext = hrContext ?? throw new ArgumentNullException(nameof(hrContext));
        _sessionService = sessionService;
        _config = config;
        _cache = cache;
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
        var target = RedirectHelper.GetSafeRedirectUrl(returnUrl, HttpContext);
        var configuredPublicUrl = _coreContext.SystemConfigs.Get()?.ServerPublicUrl;
        if (!ServerUrlResolver.IsTrustedMobileBootstrapRequest(HttpContext, configuredPublicUrl, out var originErrCode, out var originErrMsg))
        {
            bool isTopLevelHtmlNavigation =
                string.Equals(Request.Headers["Sec-Fetch-Mode"].ToString(), "navigate", StringComparison.OrdinalIgnoreCase) ||
                Request.Headers.Accept.ToString().Contains("text/html", StringComparison.OrdinalIgnoreCase);

            if (isTopLevelHtmlNavigation)
            {
                _logger.LogWarning("[SSO-BOOTSTRAP] Rejected untrusted top-level mobile-bootstrap navigation ({Code}); redirecting safely to /mobile-login.", originErrCode);
                return Redirect($"/mobile-login?returnUrl={Uri.EscapeDataString(target)}");
            }

            return BadRequestError(originErrMsg, originErrCode);
        }

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
            var empId = User.FindFirst("EmployeeId")?.Value;
            var emp = !string.IsNullOrEmpty(empId) ? _hrContext.Employees.GetById(empId) : null;
            return Redirect(ResolveTargetWithLandingPage(target, emp));
        }

        // Demo mode auto-login
        var demoEmployee = await TrySignInDemoUserAsync(deviceId);
        if (demoEmployee != null)
        {
            return Redirect(ResolveTargetWithLandingPage(target, demoEmployee));
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
                return Redirect(ResolveTargetWithLandingPage(target, employee));
            }

            VaultCookieProtector.DeleteRefreshCookie(HttpContext);
            _logger.LogWarning("[SSO] Silent renewal failed. Token invalid or expired.");
        }

        var userAgent = Request.Headers["User-Agent"].ToString();
        var isNativeBrowser = Request.Query.ContainsKey("native_browser");
        var isMobileQuery = string.Equals(Request.Query["client"].ToString(), "mobile", StringComparison.OrdinalIgnoreCase);
        var isMobileFlow = !isNativeBrowser && (isMobileQuery
            || target.Contains("client=mobile", StringComparison.OrdinalIgnoreCase)
            || userAgent.Contains("Capacitor", StringComparison.OrdinalIgnoreCase));
            
        var sessionExpiredUrl = string.IsNullOrEmpty(target) || target == "/"
            ? "/session-expired"
            : $"/session-expired?returnUrl={Uri.EscapeDataString(target)}";

        if (sso_error == "1")
        {
            Response.Cookies.Delete("Spokes_SSO_Attempt");
            if (isMobileFlow)
            {
                return Redirect($"/mobile-login?sso_error=1&returnUrl={Uri.EscapeDataString(target)}");
            }
            return Redirect(sessionExpiredUrl);
        }

        if (Request.Cookies.ContainsKey("Spokes_SSO_Attempt"))
        {
            Response.Cookies.Delete("Spokes_SSO_Attempt");
            if (isMobileFlow)
            {
                return Redirect($"/mobile-login?no_auto=1&returnUrl={Uri.EscapeDataString(target)}");
            }
            return Redirect(sessionExpiredUrl);
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

        if (await TrySignInDemoUserAsync(deviceId) != null)
        {
            var encodedUrl = JavaScriptEncoder.Default.Encode(url);
            var html = MobileSsoHelper.CreateJsRedirectPage(encodedUrl);
            return Content(html, "text/html");
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

    private async Task<Employee?> TrySignInDemoUserAsync(string? deviceId)
    {
        if (!_config.GetValue<bool>("Spokes_DemoMode") || Request.Query["admin_login"] == "1")
        {
            return null;
        }

        var demoConfig = _coreContext.DemoConfigs.Get();
        var employee = SelectDemoAutoLoginEmployee(demoConfig.AutoLoginEmployeeIds, _hrContext.Employees);
        if (employee == null)
        {
            return null;
        }

        var devId = deviceId ?? Request.Headers["X-Device-Id"].FirstOrDefault() ?? Request.Cookies["Spokes_Device"];
        var (deviceSession, _) = _sessionService.CreateSession(
            HttpContext, employee.Id, devId, issueCookie: true);

        await _sessionService.SignInAndExtendAsync(HttpContext, deviceSession, employee);
        return employee;
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
            var serverBase = ServerUrlResolver.GetSafeServerBaseUrl(HttpContext, _coreContext.SystemConfigs.Get().ServerPublicUrl);
            var postLogoutRedirect = $"{serverBase}/sso/mobile-logout-complete";
            var html = MobileSsoHelper.CreateAutoSubmitLogoutFormPage(data.Item2, data.Item1, postLogoutRedirect);
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
        if (!ServerUrlResolver.IsSameOriginRequest(HttpContext, _coreContext.SystemConfigs.Get()?.ServerPublicUrl))
        {
            return ForbiddenError("Cross-origin logout requests are not permitted.", "CSRF_DETECTED");
        }

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

    internal static Employee? SelectDemoAutoLoginEmployee(IReadOnlyList<string>? configuredIds, EmployeeRepository employees)
    {
        if (configuredIds == null || configuredIds.Count == 0 || employees == null)
            return null;

        var eligible = new List<Employee>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var id in configuredIds)
        {
            if (string.IsNullOrWhiteSpace(id) || !seen.Add(id)) continue;
            var emp = employees.GetById(id);
            if (emp != null && emp.IsSelectable && !emp.IsAdmin)
            {
                eligible.Add(emp);
            }
        }

        if (eligible.Count == 0)
            return null;

        return eligible[Random.Shared.Next(eligible.Count)];
    }

    private string ResolveTargetWithLandingPage(string target, Employee? employee)
    {
        if ((target == "/" || string.IsNullOrEmpty(target)) && employee != null)
        {
            if (_coreContext.CompanyProfile.Get()?.Edition != "Family"
                && !string.IsNullOrWhiteSpace(employee.LandingPage)
                && employee.LandingPage != ""
                && employee.LandingPage != "/")
            {
                return employee.LandingPage.StartsWith('/') ? employee.LandingPage : "/" + employee.LandingPage;
            }
        }
        return target;
    }
}
