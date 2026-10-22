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
using Spokes_Server.Core.Models.Core;
using Spokes_Server.Tests.Helpers;

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

    [Theory]
    [InlineData("/fonts/roboto/roboto-latin-400-normal.woff2")]
    [InlineData("/fonts/roboto/roboto-latin-ext-700-normal.woff2")]
    [InlineData("/fonts/Roboto-Regular.ttf")]
    [InlineData("/custom-font.woff2")]
    [InlineData("/custom-font.woff")]
    [InlineData("/css/roboto.css")]
    [InlineData("/js/spokes-vault.js")]
    [InlineData("/logo.png")]
    [InlineData("/photo.jpg")]
    [InlineData("/setup")]
    public async Task WhenSetupIncomplete_AllowlistedAssets_BypassRedirect(string path)
    {
        // Arrange
        var config = _systemConfigRepo.Get();
        config.IsSetupComplete = false;
        _systemConfigRepo.Save(config);

        var appBuilder = new ApplicationBuilder(_serviceProvider);
        appBuilder.UseSpokesSetupRedirect();
        var pipeline = appBuilder.Build();

        var context = new DefaultHttpContext();
        context.Request.Path = path;
        context.RequestServices = _serviceProvider;

        // Act
        await pipeline(context);

        // Assert
        Assert.NotEqual(302, context.Response.StatusCode);
        Assert.Null(context.Response.Headers.Location.ToString().NullIfEmpty());
    }

    [Fact]
    public async Task WhenSetupIncomplete_NonAllowlistedRoute_RedirectsToSetup()
    {
        // Arrange
        var config = _systemConfigRepo.Get();
        config.IsSetupComplete = false;
        _systemConfigRepo.Save(config);

        var appBuilder = new ApplicationBuilder(_serviceProvider);
        appBuilder.UseSpokesSetupRedirect();
        var pipeline = appBuilder.Build();

        var context = new DefaultHttpContext();
        context.Request.Path = "/dashboard";
        context.RequestServices = _serviceProvider;

        // Act
        await pipeline(context);

        // Assert
        Assert.Equal(302, context.Response.StatusCode);
        Assert.Equal("/setup", context.Response.Headers.Location.ToString());
    }

    [Fact]
    public async Task WhenSetupComplete_SetupRoute_RedirectsToRoot()
    {
        // Arrange
        var config = _systemConfigRepo.Get();
        config.IsSetupComplete = true;
        _systemConfigRepo.Save(config);

        var appBuilder = new ApplicationBuilder(_serviceProvider);
        appBuilder.UseSpokesSetupRedirect();
        var pipeline = appBuilder.Build();

        var context = new DefaultHttpContext();
        context.Request.Path = "/setup";
        context.RequestServices = _serviceProvider;

        // Act
        await pipeline(context);

        // Assert
        Assert.Equal(302, context.Response.StatusCode);
        Assert.Equal("/", context.Response.Headers.Location.ToString());
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
