using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using Spokes_Server.Components.Theme;
using Spokes_Server.Core.Data;
using Spokes_Server.Core.Data.Repositories.Core;
using Spokes_Server.Core.Extensions;
using Spokes_Server.Core.Security;
using Spokes_Server.Tests.Helpers;
using Xunit;

namespace Spokes_Server.Tests.Core.Middleware;

public class SpokesSetupRedirectMiddlewareTests : TestDataTestBase
{
    private readonly DiskPersistenceService _persistence;
    private readonly SystemConfigRepository _systemConfigRepo;
    private readonly ServiceProvider _serviceProvider;

    public SpokesSetupRedirectMiddlewareTests()
    {
        var mockConfig = new Mock<IConfiguration>();
        mockConfig.Setup(c => c["DataPath"]).Returns(_testDataPath);

        var mockLogger = new Mock<ILogger<DiskPersistenceService>>();
        _persistence = new DiskPersistenceService(mockLogger.Object);
        _systemConfigRepo = new SystemConfigRepository(_persistence, mockConfig.Object);

        var services = new ServiceCollection();
        services.AddSingleton(_systemConfigRepo);
        _serviceProvider = services.BuildServiceProvider();
    }

    public override void Dispose()
    {
        _serviceProvider.Dispose();
        _persistence.Dispose();
        base.Dispose();
    }

    private RequestDelegate BuildSetupPipeline(System.Action<HttpContext>? onNext = null)
    {
        var appBuilder = new ApplicationBuilder(_serviceProvider);
        appBuilder.UseSpokesSetupRedirect();
        appBuilder.Run(ctx =>
        {
            onNext?.Invoke(ctx);
            ctx.Response.StatusCode = StatusCodes.Status200OK;
            return Task.CompletedTask;
        });
        return appBuilder.Build();
    }

    [Theory]
    [InlineData("/fonts/roboto/roboto-latin-400-normal.woff2")]
    [InlineData("/fonts/roboto/roboto-latin-ext-700-normal.woff2")]
    [InlineData("/fonts/Roboto-Regular.ttf")]
    [InlineData("/custom-font.woff2")]
    [InlineData("/custom-font.woff")]
    [InlineData("/css/roboto.css")]
    [InlineData("/js/spokes-vault.js")]
    [InlineData("/js/upload.js")]
    [InlineData("/lib/quill/quill.js")]
    [InlineData("/lib/quill/quill.abc123.js")]
    [InlineData("/lib/cropperjs/cropper.min.css")]
    [InlineData("/lib/livekit/livekit-client.umd.min.js")]
    [InlineData("/lib/noise-suppression/deepfilternet3/v3/pkg/df_bg.wasm")]
    [InlineData("/service-worker.js")]
    [InlineData("/sounds/call-join.wav")]
    [InlineData("/icons/app-icon.svg")]
    [InlineData("/Components/Pages/Chat/Chat.razor.js")]
    [InlineData("/_content/MudBlazor/MudBlazor.min.css")]
    [InlineData("/_framework/blazor.web.js")]
    [InlineData("/logo.png")]
    [InlineData("/photo.jpg")]
    [InlineData("/setup")]
    [InlineData("/branding/default/logo.png")]
    [InlineData("/branding/default/icon.png")]
    [InlineData("/spokesapi/branding/default/logo.png")]
    [InlineData("/spokesapi/Media/Logo")]
    [InlineData("/spokesapi/Media/Icon")]
    [InlineData("/spokesapi/Manifest")]
    [InlineData("/spokesapi/culture/set")]
    [InlineData("/robots.txt")]
    [InlineData("/api/tls/ask")]
    [InlineData("/spokesapi/tls/ask")]
    public async Task WhenSetupIncomplete_AllowlistedAssets_BypassRedirect(string path)
    {
        // Arrange
        var config = _systemConfigRepo.Get();
        config.IsSetupComplete = false;
        _systemConfigRepo.Save(config);

        bool nextCalled = false;
        var pipeline = BuildSetupPipeline(_ => nextCalled = true);

        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Get;
        context.Request.Path = path;
        context.RequestServices = _serviceProvider;

        // Act
        await pipeline(context);

        // Assert
        Assert.True(nextCalled, $"Expected '{path}' to pass through UseSpokesSetupRedirect.");
        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.Null(context.Response.Headers.Location.ToString().NullIfEmpty());
    }

    [Theory]
    [InlineData("POST", "/setup/api/restore-backup")]
    [InlineData("POST", "/_blazor/negotiate")]
    [InlineData("POST", "/_blazor")]
    [InlineData("GET", "/_blazor")]
    [InlineData("GET", "/_blazor/initializers")]
    [InlineData("DELETE", "/_blazor/disconnect")]
    public async Task WhenSetupIncomplete_SetupUploadAndBlazorTransports_BypassRedirect(string method, string path)
    {
        // Arrange
        var config = _systemConfigRepo.Get();
        config.IsSetupComplete = false;
        _systemConfigRepo.Save(config);

        bool nextCalled = false;
        var pipeline = BuildSetupPipeline(_ => nextCalled = true);

        var context = new DefaultHttpContext();
        context.Request.Method = method;
        context.Request.Path = path;
        context.RequestServices = _serviceProvider;

        // Act
        await pipeline(context);

        // Assert
        Assert.True(nextCalled, $"Expected '{method} {path}' to pass through during setup.");
        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
    }

    [Fact]
    public async Task WhenSetupIncomplete_EndpointWithAllowDuringSetupMetadata_BypassesRedirect()
    {
        // Arrange
        var config = _systemConfigRepo.Get();
        config.IsSetupComplete = false;
        _systemConfigRepo.Save(config);

        bool nextCalled = false;
        var pipeline = BuildSetupPipeline(_ => nextCalled = true);

        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Get;
        context.Request.Path = "/spokesapi/custom-setup-endpoint";
        context.RequestServices = _serviceProvider;
        context.SetEndpoint(new Endpoint(
            _ => Task.CompletedTask,
            new EndpointMetadataCollection(new AllowDuringSetupAttribute()),
            "CustomSetupEndpoint"));

        // Act
        await pipeline(context);

        // Assert
        Assert.True(nextCalled);
        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
    }

    [Theory]
    [InlineData("GET", "/spokesapi/push/unread-count")]
    [InlineData("GET", "/spokesapi/presence/state")]
    [InlineData("GET", "/spokesapi/files/chat/1/secret.pdf")]
    [InlineData("GET", "/spokesapi/backups/archive.zip")]
    [InlineData("GET", "/spokesapi/Media/Avatar/emp1")]
    [InlineData("GET", "/internal/attachments/c1/img.jpg")]
    [InlineData("GET", "/api/v1/projects")]
    [InlineData("POST", "/hubs/chat/negotiate")]
    [InlineData("GET", "/hubs/chat")]
    public async Task WhenSetupIncomplete_NonSetupApiAndHubRoutes_Return503SetupRequiredJson(string method, string path)
    {
        // Arrange
        var config = _systemConfigRepo.Get();
        config.IsSetupComplete = false;
        _systemConfigRepo.Save(config);

        bool nextCalled = false;
        var pipeline = BuildSetupPipeline(_ => nextCalled = true);

        var context = new DefaultHttpContext();
        context.Request.Method = method;
        context.Request.Path = path;
        context.RequestServices = _serviceProvider;
        context.Response.Body = new MemoryStream();

        // Act
        await pipeline(context);

        // Assert
        Assert.False(nextCalled, $"Expected '{method} {path}' to be blocked while setup is incomplete.");
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, context.Response.StatusCode);
        Assert.Equal("application/json; charset=utf-8", context.Response.ContentType);
        Assert.Null(context.Response.Headers.Location.ToString().NullIfEmpty());

        context.Response.Body.Position = 0;
        using var jsonDoc = await JsonDocument.ParseAsync(context.Response.Body);
        Assert.False(jsonDoc.RootElement.GetProperty("isSuccess").GetBoolean());
        Assert.Equal("SETUP_REQUIRED", jsonDoc.RootElement.GetProperty("code").GetString());
        Assert.Equal("Server initial setup is not complete.", jsonDoc.RootElement.GetProperty("error").GetString());
    }

    [Theory]
    [InlineData("/")]
    [InlineData("/dashboard")]
    [InlineData("/albums")]
    [InlineData("/sso/login")]
    [InlineData("/setup-guide")]
    public async Task WhenSetupIncomplete_NonAllowlistedRoute_RedirectsToSetup(string path)
    {
        // Arrange
        var config = _systemConfigRepo.Get();
        config.IsSetupComplete = false;
        _systemConfigRepo.Save(config);

        var pipeline = BuildSetupPipeline();

        var context = new DefaultHttpContext();
        context.Request.Path = path;
        context.RequestServices = _serviceProvider;

        // Act
        await pipeline(context);

        // Assert
        Assert.Equal(StatusCodes.Status302Found, context.Response.StatusCode);
        Assert.Equal("/setup", context.Response.Headers.Location.ToString());
    }

    [Fact]
    public async Task WhenSetupComplete_SetupRoute_RedirectsToRoot()
    {
        // Arrange
        var config = _systemConfigRepo.Get();
        config.IsSetupComplete = true;
        _systemConfigRepo.Save(config);

        var pipeline = BuildSetupPipeline();

        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Get;
        context.Request.Path = "/setup";
        context.RequestServices = _serviceProvider;

        // Act
        await pipeline(context);

        // Assert
        Assert.Equal(StatusCodes.Status302Found, context.Response.StatusCode);
        Assert.Equal("/", context.Response.Headers.Location.ToString());
    }

    [Fact]
    public async Task WhenSetupComplete_SetupApiRoute_PassesThroughToControllerForTypedForbiddenJson()
    {
        // Arrange
        var config = _systemConfigRepo.Get();
        config.IsSetupComplete = true;
        _systemConfigRepo.Save(config);

        bool nextCalled = false;
        var pipeline = BuildSetupPipeline(_ => nextCalled = true);

        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Post;
        context.Request.Path = "/setup/api/restore-backup";
        context.RequestServices = _serviceProvider;

        // Act
        await pipeline(context);

        // Assert
        Assert.True(nextCalled);
        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
    }

    [Fact]
    public void SpokesTheme_ConfiguresRobotoTypography()
    {
        // Act
        var theme = SpokesTheme.CreateTheme();

        // Assert
        Assert.NotNull(theme);
        Assert.NotNull(theme.Typography.Default.FontFamily);
        Assert.Contains("Roboto", theme.Typography.Default.FontFamily);
        Assert.Equal("Roboto", theme.Typography.Default.FontFamily[0]);
    }
}

file static class StringExtensions
{
    public static string? NullIfEmpty(this string? s) => string.IsNullOrEmpty(s) ? null : s;
}
