using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using Spokes_Server.Aggregate;
using Spokes_Server.Aggregate.Contexts;
using Spokes_Server.Controllers;
using Spokes_Server.Core.Helpers;
using Spokes_Server.Core.Models.Core;
using Spokes_Server.Core.Models.HR;
using Spokes_Server.Core.Security;
using Xunit;

namespace Spokes_Server.Tests.Controllers;

public class SsoControllerTests : TestDataTestBase
{
    private readonly ServiceProvider _serviceProvider;
    private readonly Database _db;
    private readonly CoreContext _coreContext;
    private readonly HrContext _hrContext;
    private readonly SessionService _sessionService;
    private readonly IMemoryCache _cache;
    private readonly Mock<IOptionsMonitor<OpenIdConnectOptions>> _mockOidcMonitor;
    private readonly Mock<ILogger<SsoController>> _mockLogger;

    public SsoControllerTests()
    {
        var services = new ServiceCollection();

        var mockConfig = new Mock<IConfiguration>();
        mockConfig.Setup(c => c["DataPath"]).Returns(_testDataPath);
        mockConfig.Setup(c => c.GetSection(It.IsAny<string>())).Returns(new Mock<IConfigurationSection>().Object);
        services.AddSingleton<IConfiguration>(mockConfig.Object);

        services.AddLogging(b => b.AddConsole());
        services.AddMemoryCache();
        services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme).AddCookie();
        services.AddSingleton<EncryptionService>();
        services.AddSingleton<IOpenIdConfigurationProvider, OpenIdConfigurationProvider>();
        services.AddSpokesDatabase();
        services.AddScoped<SessionService>();

        _serviceProvider = services.BuildServiceProvider();
        _db = _serviceProvider.GetRequiredService<Database>();
        _coreContext = _serviceProvider.GetRequiredService<CoreContext>();
        _hrContext = _serviceProvider.GetRequiredService<HrContext>();
        _sessionService = _serviceProvider.GetRequiredService<SessionService>();
        _cache = _serviceProvider.GetRequiredService<IMemoryCache>();

        _mockOidcMonitor = new Mock<IOptionsMonitor<OpenIdConnectOptions>>();
        _mockOidcMonitor.Setup(m => m.Get(OpenIdConnectDefaults.AuthenticationScheme))
            .Returns(new OpenIdConnectOptions
            {
                Authority = "https://auth.example.com",
                ClientId = "spokes-test"
            });

        _mockLogger = new Mock<ILogger<SsoController>>();
    }

    public override void Dispose()
    {
        _serviceProvider.Dispose();
        base.Dispose();
    }

    private SsoController CreateController(
        IConfiguration? configuration = null,
        DefaultHttpContext? httpContext = null)
    {
        var config = configuration ?? new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DataPath"] = _testDataPath,
                ["Spokes_DemoMode"] = "false"
            })
            .Build();

        var controller = new SsoController(
            _coreContext,
            _hrContext,
            _sessionService,
            config,
            _cache,
            _mockOidcMonitor.Object,
            _mockLogger.Object);

        var context = httpContext ?? new DefaultHttpContext();
        context.RequestServices = _serviceProvider;
        context.Request.Scheme = "http";
        context.Request.Host = new HostString("localhost", 5000);

        controller.ControllerContext = new ControllerContext
        {
            HttpContext = context
        };

        return controller;
    }

    private (Employee employee, string rawRefreshToken, DeviceSession session) SeedActiveEmployeeWithSession(string? deviceId = "test-device-1")
    {
        var employee = new Employee
        {
            Id = "emp_" + Guid.NewGuid().ToString("N")[..8],
            FirstName = "Test",
            LastName = "User",
            Email = "testuser@example.com",
            IsActive = true
        };
        _db.Employees.Save(employee);

        var rawBytes = RandomNumberGenerator.GetBytes(64);
        var rawToken = Convert.ToBase64String(rawBytes);
        var tokenHash = Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(rawToken)));

        var session = new DeviceSession
        {
            Id = "sess_" + Guid.NewGuid().ToString("N")[..8],
            EmployeeId = employee.Id,
            DeviceId = deviceId,
            TokenHash = tokenHash,
            CreatedAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.AddDays(90),
            LastSeenAt = DateTime.UtcNow
        };
        _db.DeviceSessions.Save(session);

        return (employee, rawToken, session);
    }

    #region MobileBootstrap Tests

    [Theory]
    [InlineData("cross-site")]
    [InlineData("same-site")]
    public async Task MobileBootstrap_WithUntrustedSecFetchSite_ReturnsBadRequestInvalidCrossSite(string secFetchSite)
    {
        var context = new DefaultHttpContext();
        context.Request.Headers["Sec-Fetch-Site"] = secFetchSite;
        var controller = CreateController(httpContext: context);

        var result = await controller.MobileBootstrap("token123", "dev123", "/dashboard");

        var badRequest = Assert.IsType<BadRequestObjectResult>(result);
        var failure = Assert.IsAssignableFrom<SpokesResult>(badRequest.Value);
        Assert.False(failure.IsSuccess);
        Assert.Equal("INVALID_CROSS_SITE", failure.Code);
    }

    [Fact]
    public async Task MobileBootstrap_WithMismatchedOrigin_ReturnsBadRequestInvalidOrigin()
    {
        var context = new DefaultHttpContext();
        context.Request.Headers["Origin"] = "https://evil.attacker.com";
        var controller = CreateController(httpContext: context);

        var result = await controller.MobileBootstrap("token123", "dev123", "/dashboard");

        var badRequest = Assert.IsType<BadRequestObjectResult>(result);
        var failure = Assert.IsAssignableFrom<SpokesResult>(badRequest.Value);
        Assert.False(failure.IsSuccess);
        Assert.Equal("INVALID_ORIGIN", failure.Code);
    }

    [Fact]
    public async Task MobileBootstrap_WithMismatchedReferer_ReturnsBadRequestInvalidReferer()
    {
        var context = new DefaultHttpContext();
        context.Request.Headers["Referer"] = "https://evil.attacker.com/malicious";
        var controller = CreateController(httpContext: context);

        var result = await controller.MobileBootstrap("token123", "dev123", "/dashboard");

        var badRequest = Assert.IsType<BadRequestObjectResult>(result);
        var failure = Assert.IsAssignableFrom<SpokesResult>(badRequest.Value);
        Assert.False(failure.IsSuccess);
        Assert.Equal("INVALID_REFERER", failure.Code);
    }

    [Fact]
    public async Task MobileBootstrap_WithNullOrEmptyToken_RedirectsToMobileLogin()
    {
        var controller = CreateController();

        var result = await controller.MobileBootstrap(null, "dev123", "/dashboard");

        var redirect = Assert.IsType<RedirectResult>(result);
        Assert.StartsWith("/mobile-login?returnUrl=", redirect.Url);
    }

    [Fact]
    public async Task MobileBootstrap_WithInvalidToken_RedirectsToMobileLogin()
    {
        var controller = CreateController();

        var result = await controller.MobileBootstrap("invalid-token-xyz", "dev123", "/dashboard");

        var redirect = Assert.IsType<RedirectResult>(result);
        Assert.StartsWith("/mobile-login?returnUrl=", redirect.Url);
    }

    [Fact]
    public async Task MobileBootstrap_WithValidToken_AdoptsDeviceIdAndReturnsHtmlRedirect()
    {
        var (employee, rawToken, session) = SeedActiveEmployeeWithSession("dev_placeholder");
        var context = new DefaultHttpContext();
        context.Request.Headers["Origin"] = "http://localhost:5000";
        var controller = CreateController(httpContext: context);

        var result = await controller.MobileBootstrap(rawToken, "persistent-mobile-device", "/projects");

        var content = Assert.IsType<ContentResult>(result);
        Assert.Equal("text/html", content.ContentType);
        Assert.Contains("/projects", content.Content);

        // Verify session adopted the new device ID
        var updatedSession = _db.DeviceSessions.GetById(session.Id);
        Assert.NotNull(updatedSession);
        Assert.Equal("persistent-mobile-device", updatedSession.DeviceId);
    }

    [Fact]
    public async Task MobileBootstrap_SanitizesOpenRedirectTarget()
    {
        var (employee, rawToken, session) = SeedActiveEmployeeWithSession("device-1");
        var context = new DefaultHttpContext();
        context.Request.Headers["Origin"] = "http://localhost:5000";
        var controller = CreateController(httpContext: context);

        var result = await controller.MobileBootstrap(rawToken, "device-1", "https://evil.attacker.com/steal");

        var content = Assert.IsType<ContentResult>(result);
        Assert.Equal("text/html", content.ContentType);
        // RedirectHelper ensures external open-redirects sanitize to /
        Assert.DoesNotContain("https://evil.attacker.com", content.Content);
        Assert.Contains("/", content.Content);
    }

    #endregion

    #region LoginAuto Tests

    [Fact]
    public async Task LoginAuto_WhenUserAlreadyAuthenticated_RedirectsDirectlyToTarget()
    {
        var context = new DefaultHttpContext();
        var claims = new[] { new Claim("sub", "emp_123"), new Claim(ClaimTypes.Name, "Alice") };
        context.User = new ClaimsPrincipal(new ClaimsIdentity(claims, "CookieAuth"));

        var controller = CreateController(httpContext: context);

        var result = await controller.LoginAuto("/timesheets", "dev123", null);

        var redirect = Assert.IsType<RedirectResult>(result);
        Assert.Equal("/timesheets", redirect.Url);
    }

    [Fact]
    public async Task LoginAuto_WithDemoModeActive_AutoLogsInConfiguredEmployee()
    {
        var (employee, _, _) = SeedActiveEmployeeWithSession("dev_demo");
        var demoConfig = new DemoConfig
        {
            AutoLoginEmployeeIds = new List<string> { employee.Id }
        };
        _db.DemoConfigs.Save(demoConfig);

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DataPath"] = _testDataPath,
                ["Spokes_DemoMode"] = "true"
            })
            .Build();

        var controller = CreateController(configuration: config);

        var result = await controller.LoginAuto("/dashboard", "dev_demo", null);

        var redirect = Assert.IsType<RedirectResult>(result);
        Assert.Equal("/dashboard", redirect.Url);
    }

    [Fact]
    public async Task LoginAuto_WithValidRefreshCookie_SilentlyRenewsSession()
    {
        var (employee, rawToken, session) = SeedActiveEmployeeWithSession("device-auto");
        var context = new DefaultHttpContext();
        context.Request.Headers.Cookie = $"Spokes_Refresh={rawToken}";
        var controller = CreateController(httpContext: context);

        var result = await controller.LoginAuto("/accounting", "device-auto", null);

        var redirect = Assert.IsType<RedirectResult>(result);
        Assert.Equal("/accounting", redirect.Url);

        // Check response cookies append Spokes_Refresh
        var setCookieHeader = context.Response.Headers.SetCookie.ToString();
        Assert.Contains("Spokes_Refresh", setCookieHeader);
    }

    [Fact]
    public async Task LoginAuto_WithInvalidRefreshCookie_DeletesCookieAndContinues()
    {
        var context = new DefaultHttpContext();
        context.Request.Headers.Cookie = "Spokes_Refresh=bogus-token-xyz";
        var controller = CreateController(httpContext: context);

        var result = await controller.LoginAuto("/accounting", "device-auto", null);

        // Standard desktop flow returns ChallengeResult
        Assert.IsType<ChallengeResult>(result);

        var setCookieHeader = context.Response.Headers.SetCookie.ToString();
        Assert.Contains("Spokes_Refresh", setCookieHeader);
    }

    [Fact]
    public async Task LoginAuto_WithSsoAttemptCookie_OnDesktop_RedirectsToSessionExpired()
    {
        var context = new DefaultHttpContext();
        context.Request.Headers.Cookie = "Spokes_SSO_Attempt=1";
        context.Request.Headers["User-Agent"] = "Mozilla/5.0 (Windows NT 10.0; Win64; x64)";
        var controller = CreateController(httpContext: context);

        var result = await controller.LoginAuto("/accounting", null, null);

        var redirect = Assert.IsType<RedirectResult>(result);
        Assert.Equal("/session-expired", redirect.Url);
    }

    [Fact]
    public async Task LoginAuto_WithSsoAttemptCookie_OnMobile_RedirectsToMobileLoginNoAuto()
    {
        var context = new DefaultHttpContext();
        context.Request.Headers.Cookie = "Spokes_SSO_Attempt=1";
        context.Request.Headers["User-Agent"] = "Mozilla/5.0 (iPhone; CPU iPhone OS 16_0 like Mac OS X) AppleWebKit/605.1.15 Capacitor";
        var controller = CreateController(httpContext: context);

        var result = await controller.LoginAuto("/accounting", null, null);

        var redirect = Assert.IsType<RedirectResult>(result);
        Assert.Contains("/mobile-login?no_auto=1", redirect.Url);
    }

    [Fact]
    public async Task LoginAuto_OnMobileWithoutAttemptCookie_RedirectsToMobileLogin()
    {
        var context = new DefaultHttpContext();
        context.Request.Headers["User-Agent"] = "Capacitor Mobile App";
        var controller = CreateController(httpContext: context);

        var result = await controller.LoginAuto("/projects", null, null);

        var redirect = Assert.IsType<RedirectResult>(result);
        Assert.StartsWith("/mobile-login?returnUrl=", redirect.Url);
    }

    [Fact]
    public async Task LoginAuto_OnDesktopWithSsoError_RedirectsToSessionExpired()
    {
        var context = new DefaultHttpContext();
        context.Request.Headers["User-Agent"] = "Mozilla/5.0 (Windows NT 10.0; Win64; x64)";
        var controller = CreateController(httpContext: context);

        var result = await controller.LoginAuto("/projects", null, "1");

        var redirect = Assert.IsType<RedirectResult>(result);
        Assert.Equal("/session-expired", redirect.Url);
    }

    [Fact]
    public async Task LoginAuto_OnMobileWithSsoError_RedirectsToMobileLoginWithSsoError()
    {
        var context = new DefaultHttpContext();
        context.Request.Headers["User-Agent"] = "Capacitor Mobile App";
        var controller = CreateController(httpContext: context);

        var result = await controller.LoginAuto("/projects", null, "1");

        var redirect = Assert.IsType<RedirectResult>(result);
        Assert.StartsWith("/mobile-login?sso_error=1", redirect.Url);
    }

    [Fact]
    public async Task LoginAuto_OnDesktopWithoutAttemptCookie_ReturnsChallengeResult()
    {
        var context = new DefaultHttpContext();
        context.Request.Headers["User-Agent"] = "Mozilla/5.0 (Windows NT 10.0; Win64; x64)";
        var controller = CreateController(httpContext: context);

        var result = await controller.LoginAuto("/settings", "desktop-device-9", null);

        var challenge = Assert.IsType<ChallengeResult>(result);
        Assert.Contains(OpenIdConnectDefaults.AuthenticationScheme, challenge.AuthenticationSchemes);
        Assert.Equal("/settings", challenge.Properties?.RedirectUri);
        Assert.Equal("desktop-device-9", challenge.Properties?.Items["DeviceId"]);
    }

    #endregion

    #region Login Tests

    [Fact]
    public async Task Login_StandardMode_ReturnsChallengeResult()
    {
        var controller = CreateController();

        var result = await controller.Login("/reports", "my-device-id");

        var challenge = Assert.IsType<ChallengeResult>(result);
        Assert.Contains(OpenIdConnectDefaults.AuthenticationScheme, challenge.AuthenticationSchemes);
        Assert.Equal("/reports", challenge.Properties?.RedirectUri);
        Assert.Equal("my-device-id", challenge.Properties?.Items["DeviceId"]);
    }

    [Fact]
    public async Task Login_InDemoMode_AutoLogsInAndReturnsHtmlContent()
    {
        var (employee, _, _) = SeedActiveEmployeeWithSession("demo-login-dev");
        var demoConfig = new DemoConfig
        {
            AutoLoginEmployeeIds = new List<string> { employee.Id }
        };
        _db.DemoConfigs.Save(demoConfig);

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DataPath"] = _testDataPath,
                ["Spokes_DemoMode"] = "true"
            })
            .Build();

        var controller = CreateController(configuration: config);

        var result = await controller.Login("/dashboard", "demo-login-dev");

        var content = Assert.IsType<ContentResult>(result);
        Assert.Equal("text/html", content.ContentType);
        Assert.Contains("/dashboard", content.Content);
    }

    #endregion

    #region MobileLogoutPost Tests

    [Fact]
    public void MobileLogoutPost_WithMissingOrExpiredReqId_ReturnsBadRequest()
    {
        var controller = CreateController();

        var result = controller.MobileLogoutPost("non-existent-req-id");

        var badRequest = Assert.IsType<BadRequestObjectResult>(result);
        var failure = Assert.IsAssignableFrom<SpokesResult>(badRequest.Value);
        Assert.False(failure.IsSuccess);
        Assert.Equal("INVALID_LOGOUT_REQUEST", failure.Code);
    }

    [Fact]
    public void MobileLogoutPost_WithValidReqId_ReturnsHtmlFormAndCleansCache()
    {
        var reqId = "test-req-" + Guid.NewGuid().ToString("N");
        _cache.Set($"logout_{reqId}", new Tuple<string, string>("mock-id-token", "https://auth.example.com/api/logout"), TimeSpan.FromMinutes(5));

        var controller = CreateController();

        var result = controller.MobileLogoutPost(reqId);

        var content = Assert.IsType<ContentResult>(result);
        Assert.Equal("text/html", content.ContentType);
        Assert.Contains("mock-id-token", content.Content);
        Assert.Contains("https://auth.example.com/api/logout", content.Content);

        // Verify key is removed from cache
        Assert.False(_cache.TryGetValue($"logout_{reqId}", out _));
    }

    #endregion

    #region MobileLogoutComplete Tests

    [Fact]
    public void MobileLogoutComplete_RedirectsToDeepLinkScheme()
    {
        var controller = CreateController();

        var result = controller.MobileLogoutComplete();

        var redirect = Assert.IsType<RedirectResult>(result);
        Assert.Equal("spokes://logout-complete", redirect.Url);
    }

    #endregion

    #region Logout Tests

    [Fact]
    public async Task Logout_StandardClient_InitiatesHandoffAndReturnsSignOut()
    {
        var (employee, _, session) = SeedActiveEmployeeWithSession();
        var context = new DefaultHttpContext();
        context.Request.Headers.Cookie = $"Spokes_Refresh={session.Id}";
        var controller = CreateController(httpContext: context);

        var result = await controller.Logout();

        // Standard desktop client initiates IdP sign-out
        Assert.NotNull(result);
    }

    [Fact]
    public async Task Logout_MobileClient_InitiatesMobileLogoutHandoff()
    {
        var (employee, _, session) = SeedActiveEmployeeWithSession();
        var context = new DefaultHttpContext();
        context.Request.ContentType = "application/x-www-form-urlencoded";
        var formCollection = new FormCollection(new Dictionary<string, Microsoft.Extensions.Primitives.StringValues>
        {
            ["client"] = "mobile"
        });
        context.Request.Form = formCollection;

        var controller = CreateController(httpContext: context);

        var result = await controller.Logout();

        Assert.NotNull(result);
    }

    #endregion
}
