using System.Net;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Primitives;
using Moq;
using Spokes_Server.Core.Constants;
using Spokes_Server.Core.Data;
using Spokes_Server.Core.Data.Repositories.Core;
using Spokes_Server.Core.Middleware;
using Spokes_Server.Core.Services.Documents;
using Spokes_Server.Tests.Helpers;

namespace Spokes_Server.Tests.Core.Middleware;

public class RenderTokenMiddlewareTests : TestDataTestBase
{
    private readonly DiskPersistenceService _persistence;
    private readonly DeviceSessionRepository _sessionRepo;
    private readonly RenderTokenService _tokenService;
    private readonly Mock<IAuthenticationService> _authServiceMock;
    private readonly ServiceProvider _serviceProvider;

    public RenderTokenMiddlewareTests()
    {
        var mockConfig = new Mock<IConfiguration>();
        mockConfig.Setup(c => c["DataPath"]).Returns(_testDataPath);

        var mockLogger = new Mock<ILogger<DiskPersistenceService>>();
        _persistence = new DiskPersistenceService(mockLogger.Object);

        _sessionRepo = new DeviceSessionRepository(_persistence, mockConfig.Object);
        _tokenService = new RenderTokenService();

        _authServiceMock = new Mock<IAuthenticationService>();
        _authServiceMock
            .Setup(a => a.SignInAsync(
                It.IsAny<HttpContext>(),
                It.IsAny<string>(),
                It.IsAny<ClaimsPrincipal>(),
                It.IsAny<AuthenticationProperties>()))
            .Returns(Task.CompletedTask);

        var services = new ServiceCollection();
        services.AddSingleton(_authServiceMock.Object);
        _serviceProvider = services.BuildServiceProvider();
    }

    public override void Dispose()
    {
        _serviceProvider.Dispose();
        _persistence.Dispose();
        base.Dispose();
    }

    private DefaultHttpContext CreateHttpContext(
        string? renderToken = null,
        IPAddress? remoteIp = null,
        string path = "/projects/proj-1/quotes/quote-1")
    {
        var context = new DefaultHttpContext
        {
            RequestServices = _serviceProvider
        };

        context.Connection.RemoteIpAddress = remoteIp ?? IPAddress.Loopback;
        context.Request.Path = path;

        if (renderToken != null)
        {
            context.Request.Query = new QueryCollection(new Dictionary<string, StringValues>
            {
                { "renderToken", renderToken }
            });
        }

        return context;
    }

    [Fact]
    public void ScopedPermissions_ExcludesWriteAndManagePermissions()
    {
        Assert.Contains(AppPermissions.Projects.View, RenderTokenMiddleware.ScopedPermissions);
        Assert.Contains(AppPermissions.Purchases.ViewPOs, RenderTokenMiddleware.ScopedPermissions);
        Assert.Contains(AppPermissions.BusinessDocuments.View, RenderTokenMiddleware.ScopedPermissions);
        Assert.DoesNotContain(AppPermissions.Purchases.ManagePOs, RenderTokenMiddleware.ScopedPermissions);
        Assert.Equal(3, RenderTokenMiddleware.ScopedPermissions.Length);
    }

    [Fact]
    public async Task InvokeAsync_NoRenderTokenInQuery_CallsNextWithoutSigningInOrSettingUser()
    {
        var context = CreateHttpContext();
        var initialUser = context.User;
        var nextCalled = false;
        RequestDelegate next = _ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        };
        var middleware = new RenderTokenMiddleware(next);

        await middleware.InvokeAsync(context, _tokenService, _sessionRepo);

        Assert.True(nextCalled);
        Assert.Same(initialUser, context.User);
        Assert.False(context.User.Identity?.IsAuthenticated ?? false);
        _authServiceMock.Verify(a => a.SignInAsync(
            It.IsAny<HttpContext>(),
            It.IsAny<string>(),
            It.IsAny<ClaimsPrincipal>(),
            It.IsAny<AuthenticationProperties>()),
            Times.Never);
        Assert.Empty(_sessionRepo.GetAll());
    }

    [Fact]
    public async Task InvokeAsync_InvalidRenderToken_CallsNextWithoutSigningInOrSettingUser()
    {
        var context = CreateHttpContext("invalid-token-12345");
        var initialUser = context.User;
        var nextCalled = false;
        RequestDelegate next = _ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        };
        var middleware = new RenderTokenMiddleware(next);

        await middleware.InvokeAsync(context, _tokenService, _sessionRepo);

        Assert.True(nextCalled);
        Assert.Same(initialUser, context.User);
        Assert.False(context.User.Identity?.IsAuthenticated ?? false);
        _authServiceMock.Verify(a => a.SignInAsync(
            It.IsAny<HttpContext>(),
            It.IsAny<string>(),
            It.IsAny<ClaimsPrincipal>(),
            It.IsAny<AuthenticationProperties>()),
            Times.Never);
        Assert.Empty(_sessionRepo.GetAll());
    }

    [Fact]
    public async Task InvokeAsync_EmptyRenderToken_CallsNextWithoutSigningInOrSettingUser()
    {
        var context = CreateHttpContext("   ");
        var initialUser = context.User;
        var nextCalled = false;
        RequestDelegate next = _ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        };
        var middleware = new RenderTokenMiddleware(next);

        await middleware.InvokeAsync(context, _tokenService, _sessionRepo);

        Assert.True(nextCalled);
        Assert.Same(initialUser, context.User);
        Assert.False(context.User.Identity?.IsAuthenticated ?? false);
        _authServiceMock.Verify(a => a.SignInAsync(
            It.IsAny<HttpContext>(),
            It.IsAny<string>(),
            It.IsAny<ClaimsPrincipal>(),
            It.IsAny<AuthenticationProperties>()),
            Times.Never);
        Assert.Empty(_sessionRepo.GetAll());
    }

    [Fact]
    public async Task InvokeAsync_ValidLoopbackRenderToken_ConsumesTokenPersistsSessionSetsScopedReadOnlyUserAndSignsIn()
    {
        const string renderPath = "/projects/proj-1/quotes/quote-1";
        var token = _tokenService.CreateToken(renderPath);
        var context = CreateHttpContext(token, IPAddress.Loopback, renderPath);
        var nextCalled = false;
        RequestDelegate next = _ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        };
        var middleware = new RenderTokenMiddleware(next);
        var beforeTime = DateTime.UtcNow;

        await middleware.InvokeAsync(context, _tokenService, _sessionRepo);
        var afterTime = DateTime.UtcNow;

        Assert.True(nextCalled);

        // Single-use token is immediately consumed so it cannot be replayed
        Assert.False(_tokenService.ValidateToken(token, out _));

        // DeviceSession was persisted in the database for "system-renderer"
        var sessions = _sessionRepo.GetByEmployeeId("system-renderer").ToList();
        Assert.Single(sessions);
        var session = sessions[0];
        Assert.Equal("system-renderer", session.EmployeeId);
        Assert.Equal("Headless Renderer", session.DeviceName);
        Assert.True(session.CreatedAt >= beforeTime.AddSeconds(-1) && session.CreatedAt <= afterTime.AddSeconds(1));
        Assert.True(session.ExpiresAt >= beforeTime.AddMinutes(4) && session.ExpiresAt <= afterTime.AddMinutes(6));

        // RevokeToken still resolves the consumed session ID for finally-block cleanup
        Assert.True(_tokenService.RevokeToken(token, out var cleanupSessionId));
        Assert.Equal(session.Id, cleanupSessionId);

        // ClaimsPrincipal set on context.User with read-only permissions only
        var user = context.User;
        Assert.NotNull(user.Identity);
        Assert.True(user.Identity.IsAuthenticated);
        Assert.Equal(CookieAuthenticationDefaults.AuthenticationScheme, user.Identity.AuthenticationType);
        Assert.Equal("headless-renderer", user.FindFirst(ClaimTypes.NameIdentifier)?.Value);
        Assert.Equal("Headless Renderer", user.FindFirst(ClaimTypes.Name)?.Value);
        Assert.Null(user.FindFirst(ClaimTypes.Role));
        Assert.False(user.IsInRole("Admin"));
        Assert.Equal("system-renderer", user.FindFirst("EmployeeId")?.Value);
        Assert.Equal(session.Id, user.FindFirst("SessionId")?.Value);
        Assert.Equal("true", user.FindFirst("Spokes_InternalRenderer")?.Value);
        Assert.Equal("DocumentExport", user.FindFirst("RenderScope")?.Value);

        var permissions = user.FindAll("Permission").Select(c => c.Value).ToList();
        Assert.Contains(AppPermissions.Projects.View, permissions);
        Assert.Contains(AppPermissions.Purchases.ViewPOs, permissions);
        Assert.Contains(AppPermissions.BusinessDocuments.View, permissions);
        Assert.DoesNotContain(AppPermissions.Purchases.ManagePOs, permissions);

        _authServiceMock.Verify(a => a.SignInAsync(
            context,
            CookieAuthenticationDefaults.AuthenticationScheme,
            It.Is<ClaimsPrincipal>(p => ReferenceEquals(p, user)),
            It.Is<AuthenticationProperties>(props =>
                !props.IsPersistent &&
                props.ExpiresUtc.HasValue &&
                props.ExpiresUtc.Value >= beforeTime.AddMinutes(4) &&
                props.ExpiresUtc.Value <= afterTime.AddMinutes(6))),
            Times.Once);
    }

    [Fact]
    public async Task InvokeAsync_SingleUseToken_RejectsSecondReplayFromLoopback()
    {
        const string renderPath = "/projects/proj-1/invoices/inv-42";
        var token = _tokenService.CreateToken(renderPath);
        var middleware = new RenderTokenMiddleware(_ => Task.CompletedTask);

        var firstContext = CreateHttpContext(token, IPAddress.Loopback, renderPath);
        await middleware.InvokeAsync(firstContext, _tokenService, _sessionRepo);
        Assert.True(firstContext.User.Identity?.IsAuthenticated);

        // Second request attempting to replay the same renderToken must be rejected
        var replayContext = CreateHttpContext(token, IPAddress.Loopback, renderPath);
        await middleware.InvokeAsync(replayContext, _tokenService, _sessionRepo);
        Assert.False(replayContext.User.Identity?.IsAuthenticated ?? false);
        Assert.Single(_sessionRepo.GetAll());
    }

    [Fact]
    public async Task InvokeAsync_PathBoundToken_RejectsMismatchedPathAndAllowsBoundPath()
    {
        var token = _tokenService.CreateToken("/projects/proj-1/quotes/quote-allowed");
        var middleware = new RenderTokenMiddleware(_ => Task.CompletedTask);

        // Probe against a different document path is rejected and does NOT consume the token
        var wrongPathContext = CreateHttpContext(token, IPAddress.Loopback, "/projects/proj-1/quotes/quote-other");
        await middleware.InvokeAsync(wrongPathContext, _tokenService, _sessionRepo);
        Assert.False(wrongPathContext.User.Identity?.IsAuthenticated ?? false);
        Assert.Empty(_sessionRepo.GetAll());
        Assert.True(_tokenService.ValidateToken(token, out _));

        // Legitimate request for the exact bound path succeeds
        var matchingPathContext = CreateHttpContext(token, IPAddress.Loopback, "/projects/proj-1/quotes/quote-allowed");
        await middleware.InvokeAsync(matchingPathContext, _tokenService, _sessionRepo);
        Assert.True(matchingPathContext.User.Identity?.IsAuthenticated);
        Assert.Single(_sessionRepo.GetAll());
    }

    [Fact]
    public async Task InvokeAsync_RenderTokenInHeaderOnly_IsIgnoredEvenFromLoopback()
    {
        const string renderPath = "/projects/proj-1/quotes/quote-hdr";
        var token = _tokenService.CreateToken(renderPath);
        var context = new DefaultHttpContext { RequestServices = _serviceProvider };
        context.Connection.RemoteIpAddress = IPAddress.Loopback;
        context.Request.Path = renderPath;
        context.Request.Headers["X-Spokes-Render-Token"] = token;

        var nextCalled = false;
        var middleware = new RenderTokenMiddleware(_ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        });

        await middleware.InvokeAsync(context, _tokenService, _sessionRepo);

        Assert.True(nextCalled);
        Assert.False(context.User.Identity?.IsAuthenticated ?? false);
        Assert.Empty(_sessionRepo.GetAll());
        Assert.True(_tokenService.ValidateToken(token, out _));
    }

    [Theory]
    [InlineData("192.168.1.100")]
    [InlineData("10.0.0.5")]
    [InlineData("8.8.8.8")]
    public async Task InvokeAsync_RenderTokenFromNonLoopbackIp_RejectsAndDoesNotConsumeToken(string ipString)
    {
        const string renderPath = "/projects/proj-1/quotes/quote-ext";
        var token = _tokenService.CreateToken(renderPath);
        var context = CreateHttpContext(token, IPAddress.Parse(ipString), renderPath);
        var nextCalled = false;
        var mockLogger = new Mock<ILogger<RenderTokenMiddleware>>();
        var middleware = new RenderTokenMiddleware(_ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        }, mockLogger.Object);

        await middleware.InvokeAsync(context, _tokenService, _sessionRepo);

        Assert.True(nextCalled);
        Assert.False(context.User.Identity?.IsAuthenticated ?? false);
        Assert.Empty(_sessionRepo.GetAll());
        Assert.True(_tokenService.ValidateToken(token, out _));
    }

    [Fact]
    public async Task InvokeAsync_SpoofedLoopbackViaForwardedHeaders_WhenRawRemoteIpIsExternal_Rejects()
    {
        const string renderPath = "/projects/proj-1/quotes/quote-spoof";
        var token = _tokenService.CreateToken(renderPath);
        // Simulate UseForwardedHeaders overwriting Connection.RemoteIpAddress to 127.0.0.1
        // while pre-ForwardedHeaders middleware captured the real TCP peer IP (172.18.0.5).
        var context = CreateHttpContext(token, IPAddress.Loopback, renderPath);
        context.Items["Spokes.RawRemoteIp"] = IPAddress.Parse("172.18.0.5");

        var middleware = new RenderTokenMiddleware(_ => Task.CompletedTask);

        await middleware.InvokeAsync(context, _tokenService, _sessionRepo);

        Assert.False(context.User.Identity?.IsAuthenticated ?? false);
        Assert.Empty(_sessionRepo.GetAll());
        Assert.True(_tokenService.ValidateToken(token, out _));
    }

    [Fact]
    public async Task InvokeAsync_RenderTokenFromIpv6Loopback_AuthenticatesSuccessfully()
    {
        const string renderPath = "/business/documents/doc-v6";
        var token = _tokenService.CreateToken(renderPath);
        var context = CreateHttpContext(token, IPAddress.IPv6Loopback, renderPath);
        var middleware = new RenderTokenMiddleware(_ => Task.CompletedTask);

        await middleware.InvokeAsync(context, _tokenService, _sessionRepo);

        Assert.True(context.User.Identity?.IsAuthenticated);
        Assert.Equal(RenderTokenMiddleware.HeadlessRendererName, context.User.Identity?.Name);
    }

    [Fact]
    public async Task InvokeAsync_RenderTokenFromNullRemoteIp_RejectsAndProceedsUnauthenticated()
    {
        const string renderPath = "/projects/proj-1/quotes/quote-nullip";
        var token = _tokenService.CreateToken(renderPath);
        var context = new DefaultHttpContext { RequestServices = _serviceProvider };
        context.Connection.RemoteIpAddress = null;
        context.Request.Path = renderPath;
        context.Request.Query = new QueryCollection(new Dictionary<string, StringValues>
        {
            { "renderToken", token }
        });

        var nextCalled = false;
        var middleware = new RenderTokenMiddleware(_ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        });

        await middleware.InvokeAsync(context, _tokenService, _sessionRepo);

        Assert.True(nextCalled);
        Assert.False(context.User.Identity?.IsAuthenticated ?? false);
        Assert.Empty(_sessionRepo.GetAll());
    }

    [Fact]
    public async Task InvokeAsync_NextMiddlewareThrows_PropagatesException()
    {
        var expectedException = new InvalidOperationException("Downstream error");
        RequestDelegate next = _ => throw expectedException;
        var middleware = new RenderTokenMiddleware(next);
        var context = CreateHttpContext();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            middleware.InvokeAsync(context, _tokenService, _sessionRepo));
        Assert.Same(expectedException, ex);
    }
}
