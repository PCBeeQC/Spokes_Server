using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Spokes_Server.Aggregate;
using Spokes_Server.Core.Data;
using Spokes_Server.Core.Data.Repositories.Core;
using Spokes_Server.Core.Data.Repositories.HR;
using Spokes_Server.Core.Models.Core;
using Spokes_Server.Core.Models.HR;
using Spokes_Server.Core.Security;

namespace Spokes_Server.Tests.Core.Security;

/// <summary>
/// Testable subclass of DeviceTokenAuthHandler that exposes protected methods for direct testing.
/// </summary>
public class TestableDeviceTokenAuthHandler : DeviceTokenAuthHandler
{
    public TestableDeviceTokenAuthHandler(
        IOptionsMonitor<DeviceTokenAuthOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder)
        : base(options, logger, encoder)
    {
    }

    public Task<AuthenticateResult> CallHandleAuthenticateAsync() => HandleAuthenticateAsync();

    public Task CallHandleChallengeAsync(AuthenticationProperties properties) => HandleChallengeAsync(properties);
}

public class DeviceTokenAuthHandlerTests : TestDataTestBase
{
    private readonly DiskPersistenceService _persistence;
    private readonly DeviceSessionRepository _sessionRepo;
    private readonly EmployeeRepository _employeeRepo;
    private readonly Database _db;
    private readonly SessionService _sessionService;
    private readonly Mock<IAuthenticationService> _mockAuthService;
    private readonly Mock<IOptionsMonitor<DeviceTokenAuthOptions>> _optionsMonitor;
    private readonly ILoggerFactory _loggerFactory;

    public DeviceTokenAuthHandlerTests()
    {
        var mockConfig = new Mock<IConfiguration>();
        mockConfig.Setup(c => c["DataPath"]).Returns(_testDataPath);

        _persistence = new DiskPersistenceService(Mock.Of<ILogger<DiskPersistenceService>>());
        _sessionRepo = new DeviceSessionRepository(_persistence, mockConfig.Object);
        _employeeRepo = new EmployeeRepository(_persistence, mockConfig.Object);
        _db = new Database(_sessionRepo, _employeeRepo);
        _sessionService = new SessionService(_db);

        _mockAuthService = new Mock<IAuthenticationService>();
        _mockAuthService
            .Setup(a => a.SignInAsync(
                It.IsAny<HttpContext>(),
                It.IsAny<string>(),
                It.IsAny<ClaimsPrincipal>(),
                It.IsAny<AuthenticationProperties>()))
            .Returns(Task.CompletedTask);

        _optionsMonitor = new Mock<IOptionsMonitor<DeviceTokenAuthOptions>>();
        _optionsMonitor.Setup(m => m.Get(It.IsAny<string>())).Returns(new DeviceTokenAuthOptions());

        _loggerFactory = NullLoggerFactory.Instance;
    }

    public override void Dispose()
    {
        _persistence.Dispose();
        base.Dispose();
    }

    private DefaultHttpContext CreateHttpContext(string method = "GET", string path = "/", string? queryString = null, bool isHttps = false)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = method;
        context.Request.Path = path;
        if (!string.IsNullOrEmpty(queryString))
        {
            context.Request.QueryString = new QueryString(queryString);
        }
        context.Request.Host = new HostString("localhost", 5000);
        context.Request.Scheme = isHttps ? "https" : "http";

        var services = new ServiceCollection();
        services.AddSingleton(_sessionService);
        services.AddSingleton(_db);
        services.AddSingleton(_mockAuthService.Object);
        context.RequestServices = services.BuildServiceProvider();

        return context;
    }

    private async Task<TestableDeviceTokenAuthHandler> CreateHandlerAsync(HttpContext context)
    {
        var handler = new TestableDeviceTokenAuthHandler(_optionsMonitor.Object, _loggerFactory, UrlEncoder.Default);
        var scheme = new AuthenticationScheme("DeviceToken", "DeviceToken", typeof(TestableDeviceTokenAuthHandler));
        await handler.InitializeAsync(scheme, context);
        return handler;
    }

    private (string rawToken, DeviceSession session, Employee employee) SeedEmployeeAndSession(
        string? deviceId = null,
        bool isExpired = false,
        bool isRevoked = false,
        bool isInactive = false)
    {
        var rawToken = "token_" + Guid.NewGuid().ToString("N");
        using var sha256 = SHA256.Create();
        var hash = Convert.ToBase64String(sha256.ComputeHash(Encoding.UTF8.GetBytes(rawToken)));

        var employee = new Employee
        {
            Id = "emp_" + Guid.NewGuid().ToString("N"),
            FirstName = "Test",
            LastName = "User",
            Email = "test.user@example.com",
            IsActive = !isInactive,
            IsAdmin = false,
            Permissions = new List<string> { "Chat.Use", "Projects.View" }
        };
        _employeeRepo.Save(employee);

        var session = new DeviceSession
        {
            Id = "sess_" + Guid.NewGuid().ToString("N"),
            EmployeeId = employee.Id,
            TokenHash = hash,
            DeviceId = deviceId,
            CreatedAt = DateTime.UtcNow.AddDays(-2),
            LastSeenAt = DateTime.UtcNow.AddMinutes(-10), // > 1 minute ago so TouchLastSeen modifies it
            ExpiresAt = isExpired ? DateTime.UtcNow.AddDays(-1) : DateTime.UtcNow.AddDays(30),
            RevokedAt = isRevoked ? DateTime.UtcNow.AddDays(-1) : null
        };
        _sessionRepo.Save(session);

        return (rawToken, session, employee);
    }

    // =========================================================================
    // 1. HandleAuthenticateAsync: Missing credentials
    // =========================================================================

    [Fact]
    public async Task HandleAuthenticateAsync_NeitherBearerNorRefreshCookiePresent_ReturnsNoResult()
    {
        var context = CreateHttpContext(method: "GET", path: "/dashboard");
        var handler = await CreateHandlerAsync(context);

        var result = await handler.CallHandleAuthenticateAsync();

        Assert.NotNull(result);
        Assert.True(result.None);
    }

    [Fact]
    public async Task HandleAuthenticateAsync_AuthorizationHeaderNotBearer_ReturnsNoResult()
    {
        var context = CreateHttpContext(method: "GET", path: "/dashboard");
        context.Request.Headers.Authorization = "Basic dXNlcjpwYXNz";
        var handler = await CreateHandlerAsync(context);

        var result = await handler.CallHandleAuthenticateAsync();

        Assert.NotNull(result);
        Assert.True(result.None);
    }

    [Fact]
    public async Task HandleAuthenticateAsync_SpokesRefreshCookieEmpty_ReturnsNoResult()
    {
        var context = CreateHttpContext(method: "GET", path: "/dashboard");
        context.Request.Headers.Cookie = "Spokes_Refresh=";
        var handler = await CreateHandlerAsync(context);

        var result = await handler.CallHandleAuthenticateAsync();

        Assert.NotNull(result);
        Assert.True(result.None);
    }

    // =========================================================================
    // 2. HandleAuthenticateAsync: Bearer Header (Mobile API)
    // =========================================================================

    [Fact]
    public async Task HandleAuthenticateAsync_BearerHeaderOnNonApiRoute_ReturnsNoResult()
    {
        var (rawToken, _, _) = SeedEmployeeAndSession();
        var context = CreateHttpContext(method: "GET", path: "/dashboard");
        context.Request.Headers.Authorization = $"Bearer {rawToken}";
        var handler = await CreateHandlerAsync(context);

        var result = await handler.CallHandleAuthenticateAsync();

        Assert.NotNull(result);
        Assert.True(result.None);
    }

    [Fact]
    public async Task HandleAuthenticateAsync_BearerHeaderOnSpokesApiRoute_WhenTokenValidationFails_ReturnsNoResult()
    {
        var context = CreateHttpContext(method: "GET", path: "/spokesapi/projects");
        context.Request.Headers.Authorization = "Bearer non_existent_token";
        var handler = await CreateHandlerAsync(context);

        var result = await handler.CallHandleAuthenticateAsync();

        Assert.NotNull(result);
        Assert.True(result.None);
        Assert.False(context.Response.Headers.ContainsKey("Set-Cookie"));
    }

    [Fact]
    public async Task HandleAuthenticateAsync_BearerHeaderOnInternalRoute_WhenTokenValidationFails_ReturnsNoResult()
    {
        var context = CreateHttpContext(method: "GET", path: "/internal/health");
        context.Request.Headers.Authorization = "Bearer non_existent_token";
        var handler = await CreateHandlerAsync(context);

        var result = await handler.CallHandleAuthenticateAsync();

        Assert.NotNull(result);
        Assert.True(result.None);
        Assert.False(context.Response.Headers.ContainsKey("Set-Cookie"));
    }

    [Fact]
    public async Task HandleAuthenticateAsync_BearerHeaderOnSpokesApiRoute_WhenTokenValidationSucceeds_TouchesLastSeenAndReturnsSuccessWithTicket()
    {
        var (rawToken, session, employee) = SeedEmployeeAndSession();
        var originalLastSeen = session.LastSeenAt;
        var context = CreateHttpContext(method: "GET", path: "/spokesapi/projects");
        context.Request.Headers.Authorization = $"Bearer {rawToken}";
        var handler = await CreateHandlerAsync(context);

        var result = await handler.CallHandleAuthenticateAsync();

        Assert.NotNull(result);
        Assert.True(result.Succeeded);
        Assert.NotNull(result.Ticket);
        Assert.Equal("DeviceToken", result.Ticket.AuthenticationScheme);

        // Verify claims
        Assert.Equal(employee.Id, result.Ticket.Principal.FindFirst("sub")?.Value);
        Assert.Equal(employee.FullName, result.Ticket.Principal.FindFirst("name")?.Value);
        Assert.Equal(session.Id, result.Ticket.Principal.FindFirst("SessionId")?.Value);

        // Verify TouchLastSeen touched the session in repository
        var updatedSession = _sessionRepo.GetById(session.Id);
        Assert.NotNull(updatedSession);
        Assert.True(updatedSession.LastSeenAt > originalLastSeen);

        // Verify mobile bearer auth does NOT call SignInAndExtendAsync
        _mockAuthService.Verify(
            a => a.SignInAsync(It.IsAny<HttpContext>(), It.IsAny<string>(), It.IsAny<ClaimsPrincipal>(), It.IsAny<AuthenticationProperties>()),
            Times.Never);
    }

    [Fact]
    public async Task HandleAuthenticateAsync_BearerHeaderOnInternalRoute_WhenTokenValidationSucceeds_ReturnsSuccess()
    {
        var (rawToken, session, employee) = SeedEmployeeAndSession();
        var context = CreateHttpContext(method: "GET", path: "/internal/sync");
        context.Request.Headers.Authorization = $"Bearer {rawToken}";
        var handler = await CreateHandlerAsync(context);

        var result = await handler.CallHandleAuthenticateAsync();

        Assert.NotNull(result);
        Assert.True(result.Succeeded);
        Assert.Equal(employee.Id, result.Ticket?.Principal.FindFirst("sub")?.Value);
    }

    [Fact]
    public async Task HandleAuthenticateAsync_BearerHeaderCaseInsensitive_ReturnsSuccess()
    {
        var (rawToken, session, employee) = SeedEmployeeAndSession();
        var context = CreateHttpContext(method: "GET", path: "/spokesapi/tasks");
        context.Request.Headers.Authorization = $"bearer {rawToken}";
        var handler = await CreateHandlerAsync(context);

        var result = await handler.CallHandleAuthenticateAsync();

        Assert.NotNull(result);
        Assert.True(result.Succeeded);
        Assert.Equal(employee.Id, result.Ticket?.Principal.FindFirst("sub")?.Value);
    }

    [Fact]
    public async Task HandleAuthenticateAsync_BearerHeaderWithDeviceIdHeader_WhenDeviceIdMatches_ReturnsSuccess()
    {
        var deviceId = "mobile-phone-device-1";
        var (rawToken, session, employee) = SeedEmployeeAndSession(deviceId: deviceId);
        var context = CreateHttpContext(method: "GET", path: "/spokesapi/projects");
        context.Request.Headers.Authorization = $"Bearer {rawToken}";
        context.Request.Headers["X-Device-Id"] = deviceId;
        var handler = await CreateHandlerAsync(context);

        var result = await handler.CallHandleAuthenticateAsync();

        Assert.NotNull(result);
        Assert.True(result.Succeeded);
        Assert.Equal(employee.Id, result.Ticket?.Principal.FindFirst("sub")?.Value);
    }

    [Fact]
    public async Task HandleAuthenticateAsync_BearerHeaderWithDeviceIdHeader_WhenDeviceIdMismatched_ReturnsNoResult()
    {
        var (rawToken, session, employee) = SeedEmployeeAndSession(deviceId: "actual-mobile-device");
        var context = CreateHttpContext(method: "GET", path: "/spokesapi/projects");
        context.Request.Headers.Authorization = $"Bearer {rawToken}";
        context.Request.Headers["X-Device-Id"] = "different-mobile-device";
        var handler = await CreateHandlerAsync(context);

        var result = await handler.CallHandleAuthenticateAsync();

        Assert.NotNull(result);
        Assert.True(result.None);
    }

    [Fact]
    public async Task HandleAuthenticateAsync_BearerHeaderOnSpokesApiRoute_WhenEmployeeInactive_ReturnsNoResult()
    {
        var (rawToken, _, _) = SeedEmployeeAndSession(isInactive: true);
        var context = CreateHttpContext(method: "GET", path: "/spokesapi/projects");
        context.Request.Headers.Authorization = $"Bearer {rawToken}";
        var handler = await CreateHandlerAsync(context);

        var result = await handler.CallHandleAuthenticateAsync();

        Assert.NotNull(result);
        Assert.True(result.None);
    }

    // =========================================================================
    // 3. HandleAuthenticateAsync: Spokes_Refresh Cookie (Desktop Silent Re-auth)
    // =========================================================================

    [Fact]
    public async Task HandleAuthenticateAsync_SpokesRefreshOnNonGetRequest_ReturnsNoResult()
    {
        var (rawToken, _, _) = SeedEmployeeAndSession();
        var context = CreateHttpContext(method: "POST", path: "/dashboard");
        context.Request.Headers.Cookie = $"Spokes_Refresh={rawToken}";
        var handler = await CreateHandlerAsync(context);

        var result = await handler.CallHandleAuthenticateAsync();

        Assert.NotNull(result);
        Assert.True(result.None);
    }

    [Theory]
    [InlineData("/spokesapi/projects")]
    [InlineData("/spokesapi/auth/login")]
    [InlineData("/api/v1/status")]
    [InlineData("/_blazor/initializers")]
    [InlineData("/static/images/logo.png")]
    [InlineData("/sso/login")]
    [InlineData("/sso/callback")]
    public async Task HandleAuthenticateAsync_SpokesRefreshOnApiOrExcludedRoute_ReturnsNoResult(string path)
    {
        var (rawToken, _, _) = SeedEmployeeAndSession();
        var context = CreateHttpContext(method: "GET", path: path);
        context.Request.Headers.Cookie = $"Spokes_Refresh={rawToken}";
        var handler = await CreateHandlerAsync(context);

        var result = await handler.CallHandleAuthenticateAsync();

        Assert.NotNull(result);
        Assert.True(result.None);
    }

    [Theory]
    [InlineData("/site.css")]
    [InlineData("/scripts/app.js")]
    [InlineData("/favicon.ico")]
    [InlineData("/images/avatar.png")]
    public async Task HandleAuthenticateAsync_SpokesRefreshOnPathWithExtension_ReturnsNoResult(string path)
    {
        var (rawToken, _, _) = SeedEmployeeAndSession();
        var context = CreateHttpContext(method: "GET", path: path);
        context.Request.Headers.Cookie = $"Spokes_Refresh={rawToken}";
        var handler = await CreateHandlerAsync(context);

        var result = await handler.CallHandleAuthenticateAsync();

        Assert.NotNull(result);
        Assert.True(result.None);
    }

    [Fact]
    public async Task HandleAuthenticateAsync_SpokesRefreshOnValidGetNavigation_WhenTokenValidationFails_DeletesCookieAndReturnsNoResult()
    {
        var context = CreateHttpContext(method: "GET", path: "/dashboard", isHttps: true);
        context.Request.Headers.Cookie = "Spokes_Refresh=stale_token_123";
        var handler = await CreateHandlerAsync(context);

        var result = await handler.CallHandleAuthenticateAsync();

        Assert.NotNull(result);
        Assert.True(result.None);

        // Verify Spokes_Refresh cookie deletion was queued in response
        var setCookie = context.Response.Headers.SetCookie.ToString();
        Assert.Contains("Spokes_Refresh=", setCookie);
        Assert.True(setCookie.Contains("expires=") || setCookie.Contains("max-age=0"));
        Assert.Contains("path=/", setCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("httponly", setCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("secure", setCookie, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task HandleAuthenticateAsync_SpokesRefreshOnValidGetNavigation_WhenEmployeeInactive_DeletesCookieAndReturnsNoResult()
    {
        var (rawToken, _, _) = SeedEmployeeAndSession(isInactive: true);
        var context = CreateHttpContext(method: "GET", path: "/dashboard");
        context.Request.Headers.Cookie = $"Spokes_Refresh={rawToken}";
        var handler = await CreateHandlerAsync(context);

        var result = await handler.CallHandleAuthenticateAsync();

        Assert.NotNull(result);
        Assert.True(result.None);

        var setCookie = context.Response.Headers.SetCookie.ToString();
        Assert.Contains("Spokes_Refresh=", setCookie);
    }

    [Fact]
    public async Task HandleAuthenticateAsync_SpokesRefreshOnValidGetNavigation_WhenTokenValidationSucceeds_TouchesLastSeenSignsInAndReturnsSuccess()
    {
        var (rawToken, session, employee) = SeedEmployeeAndSession();
        var originalLastSeen = session.LastSeenAt;
        var context = CreateHttpContext(method: "GET", path: "/dashboard");
        context.Request.Headers.Cookie = $"Spokes_Refresh={rawToken}";
        var handler = await CreateHandlerAsync(context);

        var result = await handler.CallHandleAuthenticateAsync();

        Assert.NotNull(result);
        Assert.True(result.Succeeded);
        Assert.NotNull(result.Ticket);
        Assert.Equal("DeviceToken", result.Ticket.AuthenticationScheme);

        // Verify claims
        Assert.Equal(employee.Id, result.Ticket.Principal.FindFirst("sub")?.Value);
        Assert.Equal(employee.FullName, result.Ticket.Principal.FindFirst("name")?.Value);
        Assert.Equal(session.Id, result.Ticket.Principal.FindFirst("SessionId")?.Value);

        // Verify TouchLastSeen touched the session in repository
        var updatedSession = _sessionRepo.GetById(session.Id);
        Assert.NotNull(updatedSession);
        Assert.True(updatedSession.LastSeenAt > originalLastSeen);

        // Verify desktop renewal called SignInAndExtendAsync which signs in via cookie auth
        _mockAuthService.Verify(
            a => a.SignInAsync(
                context,
                CookieAuthenticationDefaults.AuthenticationScheme,
                It.IsAny<ClaimsPrincipal>(),
                It.Is<AuthenticationProperties>(p => p.IsPersistent)),
            Times.Once);

        // Verify session expiry was extended to ~90 days
        Assert.True(updatedSession.ExpiresAt > DateTime.UtcNow.AddDays(80));
    }

    [Fact]
    public async Task HandleAuthenticateAsync_SpokesRefreshWithDeviceCookie_WhenDeviceIdMatches_ReturnsSuccess()
    {
        var deviceId = "desktop-device-browser-1";
        var (rawToken, session, employee) = SeedEmployeeAndSession(deviceId: deviceId);
        var context = CreateHttpContext(method: "GET", path: "/dashboard");
        context.Request.Headers.Cookie = $"Spokes_Refresh={rawToken}; Spokes_Device={deviceId}";
        var handler = await CreateHandlerAsync(context);

        var result = await handler.CallHandleAuthenticateAsync();

        Assert.NotNull(result);
        Assert.True(result.Succeeded);
        Assert.Equal(employee.Id, result.Ticket?.Principal.FindFirst("sub")?.Value);
    }

    [Fact]
    public async Task HandleAuthenticateAsync_SpokesRefreshWithDeviceCookie_WhenDeviceIdMismatched_DeletesCookieAndReturnsNoResult()
    {
        var (rawToken, session, employee) = SeedEmployeeAndSession(deviceId: "actual-device-1");
        var context = CreateHttpContext(method: "GET", path: "/dashboard");
        context.Request.Headers.Cookie = $"Spokes_Refresh={rawToken}; Spokes_Device=wrong-device-2";
        var handler = await CreateHandlerAsync(context);

        var result = await handler.CallHandleAuthenticateAsync();

        Assert.NotNull(result);
        Assert.True(result.None);

        var setCookie = context.Response.Headers.SetCookie.ToString();
        Assert.Contains("Spokes_Refresh=", setCookie);
    }

    // =========================================================================
    // 4. HandleChallengeAsync
    // =========================================================================

    [Theory]
    [InlineData("/spokesapi/projects")]
    [InlineData("/api/v1/status")]
    [InlineData("/internal/health")]
    [InlineData("/_blazor/negotiate")]
    public async Task HandleChallengeAsync_ApiRoutes_SetsStatusCode401(string path)
    {
        var context = CreateHttpContext(method: "GET", path: path);
        var handler = await CreateHandlerAsync(context);

        await handler.CallHandleChallengeAsync(new AuthenticationProperties());

        Assert.Equal(401, context.Response.StatusCode);
        Assert.True(string.IsNullOrEmpty(context.Response.Headers.Location));
    }

    [Fact]
    public async Task HandleChallengeAsync_DesktopRootRoute_RedirectsToAutoLoginWithoutReturnUrl()
    {
        var context = CreateHttpContext(method: "GET", path: "/");
        var handler = await CreateHandlerAsync(context);

        await handler.CallHandleChallengeAsync(new AuthenticationProperties());

        Assert.Equal(302, context.Response.StatusCode);
        Assert.Equal("/sso/login/auto", context.Response.Headers.Location.ToString());
    }

    [Fact]
    public async Task HandleChallengeAsync_DesktopDeepLinkRoute_RedirectsToAutoLoginWithEncodedReturnUrl()
    {
        var context = CreateHttpContext(method: "GET", path: "/dashboard", queryString: "?tab=overview&team=eng");
        var handler = await CreateHandlerAsync(context);

        await handler.CallHandleChallengeAsync(new AuthenticationProperties());

        Assert.Equal(302, context.Response.StatusCode);
        var expectedReturnUrl = Uri.EscapeDataString("/dashboard?tab=overview&team=eng");
        Assert.Equal($"/sso/login/auto?returnUrl={expectedReturnUrl}", context.Response.Headers.Location.ToString());
    }

    [Fact]
    public async Task HandleChallengeAsync_DesktopRouteWithPropertiesRedirectUri_RedirectsToAutoLoginWithPropertiesUri()
    {
        var context = CreateHttpContext(method: "GET", path: "/");
        var handler = await CreateHandlerAsync(context);
        var properties = new AuthenticationProperties { RedirectUri = "/invoices/INV-2024-001" };

        await handler.CallHandleChallengeAsync(properties);

        Assert.Equal(302, context.Response.StatusCode);
        var expectedReturnUrl = Uri.EscapeDataString("/invoices/INV-2024-001");
        Assert.Equal($"/sso/login/auto?returnUrl={expectedReturnUrl}", context.Response.Headers.Location.ToString());
    }

    [Fact]
    public async Task HandleChallengeAsync_DesktopRouteWithUnsafeRedirectUri_SanitizesAndRedirectsWithoutUnsafeUrl()
    {
        var context = CreateHttpContext(method: "GET", path: "/");
        var handler = await CreateHandlerAsync(context);
        var properties = new AuthenticationProperties { RedirectUri = "//evil.com/phish" };

        await handler.CallHandleChallengeAsync(properties);

        Assert.Equal(302, context.Response.StatusCode);
        // Fallback is "/", so safeReturnUrl == "/" resulting in "/sso/login/auto" without query string
        Assert.Equal("/sso/login/auto", context.Response.Headers.Location.ToString());
    }
}
