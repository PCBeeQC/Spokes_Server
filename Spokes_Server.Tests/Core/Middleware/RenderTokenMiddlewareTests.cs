using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Primitives;
using Moq;
using Spokes_Server.Aggregate;
using Spokes_Server.Core.Constants;
using Spokes_Server.Core.Data;
using Spokes_Server.Core.Data.Repositories.Core;
using Spokes_Server.Core.Middleware;
using Spokes_Server.Core.Models.Core;
using Spokes_Server.Core.Services.Documents;

namespace Spokes_Server.Tests.Core.Middleware;

public class RenderTokenMiddlewareTests : TestDataTestBase
{
    private readonly DiskPersistenceService _persistence;
    private readonly DeviceSessionRepository _sessionRepo;
    private readonly Database _db;
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
        _db = new Database(_sessionRepo, null!);

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

    private DefaultHttpContext CreateHttpContext(string? renderToken = null)
    {
        var context = new DefaultHttpContext
        {
            RequestServices = _serviceProvider
        };

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
        await middleware.InvokeAsync(context, _tokenService, _db);

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
        await middleware.InvokeAsync(context, _tokenService, _db);

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
        await middleware.InvokeAsync(context, _tokenService, _db);

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
    public async Task InvokeAsync_ValidRenderToken_ConsumesTokenPersistsSessionSetsAdminUserSignsInAndCallsNext()
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
        await middleware.InvokeAsync(context, _tokenService, _db);
        var afterTime = DateTime.UtcNow;

        // Assert 1: Next middleware delegate was invoked
        Assert.True(nextCalled);

        // Assert 2: Token is consumed and cannot be reused
        Assert.False(_tokenService.ValidateAndConsume(token));

        // Assert 3: DeviceSession was persisted in the database for "system-renderer"
        var sessions = _sessionRepo.GetByEmployeeId("system-renderer").ToList();
        Assert.Single(sessions);
        var session = sessions[0];
        Assert.False(string.IsNullOrWhiteSpace(session.Id));
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
        Assert.Equal("Admin", user.FindFirst(ClaimTypes.Role)?.Value);
        Assert.True(user.IsInRole("Admin"));
        Assert.Equal("system-renderer", user.FindFirst("EmployeeId")?.Value);
        Assert.Equal(session.Id, user.FindFirst("SessionId")?.Value);

        var permissions = user.FindAll("Permission").Select(c => c.Value).ToList();
        Assert.Contains(AppPermissions.Projects.View, permissions);
        Assert.Contains(AppPermissions.Purchases.ManagePOs, permissions);

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
    public async Task InvokeAsync_ReusedRenderToken_OnlySignsInOnceAndRejectsSecondRequest()
    {
        // Arrange
        var token = _tokenService.CreateToken();
        var middleware = new RenderTokenMiddleware(ctx => Task.CompletedTask);

        var context1 = CreateHttpContext(token);
        var context2 = CreateHttpContext(token);
        var initialUser2 = context2.User;

        // Act 1: First invocation with valid token succeeds
        await middleware.InvokeAsync(context1, _tokenService, _db);
        Assert.True(context1.User.Identity?.IsAuthenticated);

        // Act 2: Second invocation with same token fails (token consumed)
        await middleware.InvokeAsync(context2, _tokenService, _db);

        // Assert
        Assert.Same(initialUser2, context2.User);
        Assert.False(context2.User.Identity?.IsAuthenticated ?? false);

        // Verify only 1 session was created in database
        Assert.Single(_sessionRepo.GetAll());

        // Verify SignInAsync was called exactly once across both requests
        _authServiceMock.Verify(a => a.SignInAsync(
            It.IsAny<HttpContext>(),
            It.IsAny<string>(),
            It.IsAny<ClaimsPrincipal>(),
            It.IsAny<AuthenticationProperties>()),
            Times.Once);
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
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => middleware.InvokeAsync(context, _tokenService, _db));
        Assert.Same(expectedException, ex);
    }
}
