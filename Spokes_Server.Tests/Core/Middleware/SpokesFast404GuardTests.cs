using System.IO;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Spokes_Server.Core.Extensions;
using Xunit;

namespace Spokes_Server.Tests.Core.Middleware;

public class SpokesFast404GuardTests
{
    private static RequestDelegate CreatePipeline(RequestDelegate next)
    {
        var appBuilder = new ApplicationBuilder(new EmptyServiceProvider());
        appBuilder.UseSpokesFast404Guard();
        appBuilder.Run(next);
        return appBuilder.Build();
    }

    [Theory]
    [InlineData("/spokesapi/nonexistent/endpoint")]
    [InlineData("/internal/attachments/missing/photo.jpg")]
    [InlineData("/api/unknown/service")]
    [InlineData("/assets/missing-style.css")]
    [InlineData("/fonts/missing-font.woff2")]
    [InlineData("/images/broken.png")]
    [InlineData("/documents/missing.pdf")]
    public async Task WhenUnmappedApiOrAssetHitsBlazorFallback_Returns404Fast(string path)
    {
        // Arrange
        bool nextCalled = false;
        var pipeline = CreatePipeline(context =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        });

        var context = new DefaultHttpContext();
        context.Request.Path = path;
        context.Response.Body = new MemoryStream();

        // Simulate endpoint matched to Blazor SSR root fallback (<App />)
        var blazorEndpoint = new Endpoint(
            _ => Task.CompletedTask,
            new EndpointMetadataCollection(new Microsoft.AspNetCore.Components.Endpoints.ComponentTypeMetadata(typeof(Spokes_Server.Components.App))),
            "App (InteractiveServer)"
        );
        context.SetEndpoint(blazorEndpoint);

        // Act
        await pipeline(context);

        // Assert
        Assert.False(nextCalled, "Fast-404 guard should have short-circuited without invoking next().");
        Assert.Equal(StatusCodes.Status404NotFound, context.Response.StatusCode);
    }

    [Fact]
    public async Task WhenStaticAssetHasNoEndpoint_Returns404Fast()
    {
        // Arrange
        bool nextCalled = false;
        var pipeline = CreatePipeline(context =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        });

        var context = new DefaultHttpContext();
        context.Request.Path = "/images/missing-icon.png";
        context.Response.Body = new MemoryStream();
        context.SetEndpoint(null);

        // Act
        await pipeline(context);

        // Assert
        Assert.False(nextCalled);
        Assert.Equal(StatusCodes.Status404NotFound, context.Response.StatusCode);
    }

    [Theory]
    [InlineData("/spokesapi/files/chat/c1/img.jpg")]
    [InlineData("/internal/attachments/c1/img.jpg")]
    [InlineData("/spokesapi/branding/default/logo.png")]
    public async Task WhenValidControllerEndpointMatched_PassesThroughToNext(string path)
    {
        // Arrange
        bool nextCalled = false;
        var pipeline = CreatePipeline(context =>
        {
            nextCalled = true;
            context.Response.StatusCode = StatusCodes.Status200OK;
            return Task.CompletedTask;
        });

        var context = new DefaultHttpContext();
        context.Request.Path = path;

        // Controller endpoints have action descriptors or controller names in DisplayName, not App / ComponentType
        var controllerEndpoint = new Endpoint(
            _ => Task.CompletedTask,
            new EndpointMetadataCollection(),
            "Spokes_Server.Controllers.FileController.GetFile (Spokes_Server)"
        );
        context.SetEndpoint(controllerEndpoint);

        // Act
        await pipeline(context);

        // Assert
        Assert.True(nextCalled, "Valid controller endpoints must pass through to execution.");
        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
    }

    [Theory]
    [InlineData("/chat")]
    [InlineData("/chat/channel-123")]
    [InlineData("/settings")]
    [InlineData("/login")]
    [InlineData("/")]
    public async Task WhenLegitimateBlazorNavigationPage_PassesThroughToBlazor(string pagePath)
    {
        // Arrange
        bool nextCalled = false;
        var pipeline = CreatePipeline(context =>
        {
            nextCalled = true;
            context.Response.StatusCode = StatusCodes.Status200OK;
            return Task.CompletedTask;
        });

        var context = new DefaultHttpContext();
        context.Request.Path = pagePath;

        var blazorEndpoint = new Endpoint(
            _ => Task.CompletedTask,
            new EndpointMetadataCollection(new Microsoft.AspNetCore.Components.Endpoints.ComponentTypeMetadata(typeof(Spokes_Server.Components.App))),
            "App (InteractiveServer)"
        );
        context.SetEndpoint(blazorEndpoint);

        // Act
        await pipeline(context);

        // Assert
        Assert.True(nextCalled, "Legitimate page navigation URLs must reach Blazor SSR renderer.");
        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
    }

    private class EmptyServiceProvider : System.IServiceProvider
    {
        public object? GetService(System.Type serviceType) => null;
    }
}
