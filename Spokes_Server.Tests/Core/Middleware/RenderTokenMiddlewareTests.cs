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
using Spokes_Server.Core.Models.Core;
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

    private DefaultHttpContext CreateHttpContext(string? renderToken = null, System.Net.IPAddress? remoteIp = null)
    {
        var context = new DefaultHttpContext
        {
            RequestServices = _serviceProvider
        };

        context.Connection.RemoteIpAddress = remoteIp ?? System.Net.IPAddress.Loopback;

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
    public async Task InvokeAsync_NoRenderTokenInQuery_CallsNextWithoutSigningInOrSettingUser()
    {
        // Arrange
        var context = CreateHttpContext();
        var initialUser = context.User;
        var nextCalled = false;
        RequestDelegate next = ctx =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        };
        var middleware = new RenderTokenMiddleware(next);

        // Act
        await middleware.InvokeAsync(context, _tokenService, _sessionRepo);

        // Assert
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
        // Arrange
        var context = CreateHttpContext("invalid-token-12345");
        var initialUser = context.User;
        var nextCalled = false;
        RequestDelegate next = ctx =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        };
        var middleware = new RenderTokenMiddleware(next);

        // Act
        await middleware.InvokeAsync(context, _tokenService, _sessionRepo);

        // Assert
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
        // Arrange
        var context = CreateHttpContext("   ");
        var initialUser = context.User;
        var nextCalled = false;
        RequestDelegate next = ctx =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        };
        var middleware = new RenderTokenMiddleware(next);

        // Act
        await middleware.InvokeAsync(context, _tokenService, _sessionRepo);

        // Assert
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
    public async Task InvokeAsync_ValidRenderToken_PersistsSessionSetsScopedUserSignsInAndCallsNext()
    {
        // Arrange
        var token = _tokenService.CreateToken();
        var context = CreateHttpContext(token);
        var nextCalled = false;
        RequestDelegate next = ctx =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        };
        var middleware = new RenderTokenMiddleware(next);
        var beforeTime = DateTime.UtcNow;

        // Act
        await middleware.InvokeAsync(context, _tokenService, _sessionRepo);
        var afterTime = DateTime.UtcNow;

        // Assert 1: Next middleware delegate was invoked
        Assert.True(nextCalled);

        // Assert 2: Token remains valid for subsequent requests in the same render scope until explicitly revoked
        Assert.True(_tokenService.ValidateToken(token, out var sessionId));

        // Assert 3: DeviceSession was persisted in the database for "system-renderer"
        var sessions = _sessionRepo.GetByEmployeeId("system-renderer").ToList();
        Assert.Single(sessions);
        var session = sessions[0];
        Assert.Equal(sessionId, session.Id);
        Assert.Equal("system-renderer", session.EmployeeId);
        Assert.Equal("Headless Renderer", session.DeviceName);
        Assert.True(session.CreatedAt >= beforeTime.AddSeconds(-1) && session.CreatedAt <= afterTime.AddSeconds(1));
        Assert.True(session.ExpiresAt >= beforeTime.AddMinutes(4) && session.ExpiresAt <= afterTime.AddMinutes(6));

        // Assert 4: ClaimsPrincipal set on context.User
        var user = context.User;
        Assert.NotNull(user);
        Assert.NotNull(user.Identity);
        Assert.True(user.Identity.IsAuthenticated);
        Assert.Equal(CookieAuthenticationDefaults.AuthenticationScheme, user.Identity.AuthenticationType);
        Assert.Equal("headless-renderer", user.FindFirst(ClaimTypes.NameIdentifier)?.Value);
        Assert.Equal("Headless Renderer", user.FindFirst(ClaimTypes.Name)?.Value);
        Assert.Null(user.FindFirst(ClaimTypes.Role));
        Assert.False(user.IsInRole("Admin"));
        Assert.Equal("system-renderer", user.FindFirst("EmployeeId")?.Value);
        Assert.Equal(session.Id, user.FindFirst("SessionId")?.Value);
        Assert.Equal("DocumentExport", user.FindFirst("RenderScope")?.Value);

        var permissions = user.FindAll("Permission").Select(c => c.Value).ToList();
        Assert.Contains(AppPermissions.Projects.View, permissions);
        Assert.Contains(AppPermissions.Purchases.ViewPOs, permissions);
        Assert.Contains(AppPermissions.Purchases.ManagePOs, permissions);
        Assert.Contains(AppPermissions.BusinessDocuments.View, permissions);

        // Assert 5: Signed in via IAuthenticationService mock
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
    public async Task InvokeAsync_MultiRequestRenderLifecycle_AuthenticatesAllRequestsInScope_AndRejectsAfterRevoke()
    {
        // Arrange: Simulate initial GET followed by /_blazor/negotiate and /_blazor WebSocket upgrade, then RevokeToken
        var token = _tokenService.CreateToken();
        var middleware = new RenderTokenMiddleware(ctx => Task.CompletedTask);

        var contextInitialGet = CreateHttpContext(token);
        var contextBlazorNegotiate = new DefaultHttpContext { RequestServices = _serviceProvider };
        contextBlazorNegotiate.Request.Headers["X-Spokes-Render-Token"] = token;

        // Act 1: Initial GET request succeeds and creates session
        await middleware.InvokeAsync(contextInitialGet, _tokenService, _sessionRepo);
        Assert.True(contextInitialGet.User.Identity?.IsAuthenticated);

        // Act 2: Subsequent /_blazor/negotiate request with same active token also authenticates with same SessionId
        await middleware.InvokeAsync(contextBlazorNegotiate, _tokenService, _sessionRepo);
        Assert.True(contextBlazorNegotiate.User.Identity?.IsAuthenticated);
        Assert.Equal(
            contextInitialGet.User.FindFirst("SessionId")?.Value,
            contextBlazorNegotiate.User.FindFirst("SessionId")?.Value);

        // Verify only 1 session was created in database and SignInAsync was called only once
        Assert.Single(_sessionRepo.GetAll());
        _authServiceMock.Verify(a => a.SignInAsync(
            It.IsAny<HttpContext>(),
            It.IsAny<string>(),
            It.IsAny<ClaimsPrincipal>(),
            It.IsAny<AuthenticationProperties>()),
            Times.Once);

        // Act 3: Revoke token when render completes, then verify subsequent request is rejected
        _tokenService.RevokeToken(token);
        var contextAfterRevoke = CreateHttpContext(token);
        await middleware.InvokeAsync(contextAfterRevoke, _tokenService, _sessionRepo);
        Assert.False(contextAfterRevoke.User.Identity?.IsAuthenticated ?? false);
    }

    [Fact]
    public async Task InvokeAsync_NextMiddlewareThrows_PropagatesException()
    {
        // Arrange
        var expectedException = new InvalidOperationException("Downstream error");
        RequestDelegate next = ctx => throw expectedException;
        var middleware = new RenderTokenMiddleware(next);
        var context = CreateHttpContext();

        // Act & Assert
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => middleware.InvokeAsync(context, _tokenService, _sessionRepo));
        Assert.Same(expectedException, ex);
    }

    [Fact]
    public async Task InvokeAsync_RenderTokenFromNonLoopbackIp_AuthenticatesSuccessfully()
    {
        // Arrange
        var token = _tokenService.CreateToken();
        var context = CreateHttpContext(token, System.Net.IPAddress.Parse("192.168.1.100"));
        var nextCalled = false;
        RequestDelegate next = ctx =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        };
        var mockLogger = new Mock<ILogger<RenderTokenMiddleware>>();
        var middleware = new RenderTokenMiddleware(next, mockLogger.Object);

        // Act
        await middleware.InvokeAsync(context, _tokenService, _sessionRepo);

        // Assert
        Assert.True(nextCalled);
        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.True(context.User.Identity?.IsAuthenticated);
        Assert.Equal(RenderTokenMiddleware.HeadlessRendererName, context.User.Identity?.Name);
    }

    [Fact]
    public async Task InvokeAsync_RenderTokenFromHeader_AuthenticatesSuccessfully()
    {
        // Arrange
        var token = _tokenService.CreateToken();
        var context = new DefaultHttpContext { RequestServices = _serviceProvider };
        context.Connection.RemoteIpAddress = System.Net.IPAddress.Parse("10.0.0.5");
        context.Request.Headers["X-Spokes-Render-Token"] = token;

        var nextCalled = false;
        RequestDelegate next = ctx =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        };
        var mockLogger = new Mock<ILogger<RenderTokenMiddleware>>();
        var middleware = new RenderTokenMiddleware(next, mockLogger.Object);

        // Act
        await middleware.InvokeAsync(context, _tokenService, _sessionRepo);

        // Assert
        Assert.True(nextCalled);
        Assert.True(context.User.Identity?.IsAuthenticated);
        Assert.Equal(RenderTokenMiddleware.HeadlessRendererName, context.User.Identity?.Name);
    }

    [Fact]
    public async Task InvokeAsync_RenderTokenFromNullRemoteIp_AuthenticatesSuccessfully()
    {
        // Arrange
        var token = _tokenService.CreateToken();
        var context = new DefaultHttpContext { RequestServices = _serviceProvider };
        context.Connection.RemoteIpAddress = null;
        context.Request.Query = new QueryCollection(new Dictionary<string, StringValues>
        {
            { "renderToken", token }
        });

        var nextCalled = false;
        RequestDelegate next = ctx =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        };
        var mockLogger = new Mock<ILogger<RenderTokenMiddleware>>();
        var middleware = new RenderTokenMiddleware(next, mockLogger.Object);

        // Act
        await middleware.InvokeAsync(context, _tokenService, _sessionRepo);

        // Assert
        Assert.True(nextCalled);
        Assert.True(context.User.Identity?.IsAuthenticated);
    }

    [Fact]
    public async Task InvokeAsync_InvalidRenderToken_ProceedsUnauthenticated()
    {
        // Arrange
        var context = CreateHttpContext("bad-token", System.Net.IPAddress.Loopback);
        var nextCalled = false;
        RequestDelegate next = ctx =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        };
        var mockLogger = new Mock<ILogger<RenderTokenMiddleware>>();
        var middleware = new RenderTokenMiddleware(next, mockLogger.Object);

        // Act
        await middleware.InvokeAsync(context, _tokenService, _sessionRepo);

        // Assert
        Assert.True(nextCalled);
        Assert.False(context.User.Identity?.IsAuthenticated ?? false);
    }
}
