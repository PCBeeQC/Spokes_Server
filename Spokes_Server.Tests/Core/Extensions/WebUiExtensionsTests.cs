using System;
using System.IO.Compression;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.ResponseCompression;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MudBlazor;
using Spokes_Server.Core.Extensions;
using Xunit;

namespace Spokes_Server.Tests.Core.Extensions;

public class WebUiExtensionsTests
{
    [Fact]
    public void SnackbarConfiguration_IsScopedPerSession_NotSharedAcrossScopes()
    {
        var services = new ServiceCollection();
        var config = new ConfigurationBuilder().Build();
        services.AddSingleton<IConfiguration>(config);
        services.AddLogging();

        // Run Spokes server WebUi service registration
        services.AddSpokesWebUi();

        var provider = services.BuildServiceProvider();

        using var scope1 = provider.CreateScope();
        using var scope2 = provider.CreateScope();

        var snackbarConfig1 = scope1.ServiceProvider.GetRequiredService<SnackbarConfiguration>();
        var snackbarConfig2 = scope2.ServiceProvider.GetRequiredService<SnackbarConfiguration>();

        // Ensure distinct instances per scope
        Assert.NotSame(snackbarConfig1, snackbarConfig2);

        // Mutating scope1 (e.g. mobile client connecting and setting TopCenter)
        snackbarConfig1.PositionClass = Defaults.Classes.Position.TopCenter;

        // Verify scope2 remains untouched at default BottomLeft
        Assert.Equal(Defaults.Classes.Position.TopCenter, snackbarConfig1.PositionClass);
        Assert.Equal(Defaults.Classes.Position.BottomLeft, snackbarConfig2.PositionClass);
    }

    [Fact]
    public void ResponseCompression_IncludesRfc9239TextJavascriptAndOptimalLevels()
    {
        var services = new ServiceCollection();
        var config = new ConfigurationBuilder().Build();
        services.AddSingleton<IConfiguration>(config);
        services.AddLogging();

        services.AddSpokesWebUi();

        using var provider = services.BuildServiceProvider();
        var compressionOpts = provider.GetRequiredService<IOptions<ResponseCompressionOptions>>().Value;
        var brotliOpts = provider.GetRequiredService<IOptions<BrotliCompressionProviderOptions>>().Value;
        var gzipOpts = provider.GetRequiredService<IOptions<GzipCompressionProviderOptions>>().Value;

        Assert.True(compressionOpts.EnableForHttps);
        Assert.Contains("text/javascript", compressionOpts.MimeTypes);
        Assert.Contains("application/javascript", compressionOpts.MimeTypes);
        Assert.Contains("application/manifest+json", compressionOpts.MimeTypes);
        Assert.Contains("image/svg+xml", compressionOpts.MimeTypes);
        Assert.Contains("audio/wav", compressionOpts.MimeTypes);
        Assert.Equal(CompressionLevel.Optimal, brotliOpts.Level);
        Assert.Equal(CompressionLevel.Optimal, gzipOpts.Level);
    }
}
