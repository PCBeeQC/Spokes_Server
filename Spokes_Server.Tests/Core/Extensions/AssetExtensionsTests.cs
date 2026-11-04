using System.Collections.Generic;
using Spokes_Server.Core.Extensions;
using Xunit;

namespace Spokes_Server.Tests.Core.Extensions;

public class AssetExtensionsTests
{
    [Fact]
    public void AppVersion_IsNotNullOrEmpty()
    {
        // Assert
        Assert.False(string.IsNullOrWhiteSpace(AssetExtensions.AppVersion));
    }

    [Fact]
    public void Bypassed_WithDynamicDictionaryIndexer_ResolvesUrlAndAppendsVersion()
    {
        // Arrange
        var assets = new Dictionary<string, string>
        {
            ["scripts/main.razor.js"] = "scripts/main.razor.fingerprinted.js"
        };
        var path = "scripts/main.razor.js";

        // Act
        var result = assets.Bypassed(path);

        // Assert
        Assert.Equal($"scripts/main.razor.fingerprinted.js?v={AssetExtensions.AppVersion}", result);
    }

    [Fact]
    public void Bypassed_WhenIndexerThrowsOrNotFound_UsesRawPathWithAppVersion()
    {
        // Arrange
        var plainObject = new object();
        var path = "scripts/plain.js";

        // Act
        var result = plainObject.Bypassed(path);

        // Assert
        Assert.Equal($"scripts/plain.js?v={AssetExtensions.AppVersion}", result);
    }

    [Fact]
    public void Bypassed_WhenKeyNotFoundInDictionary_UsesRawPathWithAppVersion()
    {
        // Arrange
        var assets = new Dictionary<string, string>();
        var path = "scripts/missing.js";

        // Act
        var result = assets.Bypassed(path);

        // Assert
        Assert.Equal($"scripts/missing.js?v={AssetExtensions.AppVersion}", result);
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    public void Bypassed_WhenResolvedUrlIsNullOrEmpty_UsesRawPathWithAppVersion(string? resolvedValue)
    {
        // Arrange
        var assets = new Dictionary<string, string?>
        {
            ["scripts/empty.js"] = resolvedValue
        };
        var path = "scripts/empty.js";

        // Act
        var result = assets.Bypassed(path);

        // Assert
        Assert.Equal($"scripts/empty.js?v={AssetExtensions.AppVersion}", result);
    }

    [Fact]
    public void Bypassed_WhenIndexerReturnsNonString_UsesRawPathWithAppVersion()
    {
        // Arrange
        var assets = new Dictionary<string, object>
        {
            ["scripts/number.js"] = 12345
        };
        var path = "scripts/number.js";

        // Act
        var result = assets.Bypassed(path);

        // Assert
        Assert.Equal($"scripts/number.js?v={AssetExtensions.AppVersion}", result);
    }

    [Theory]
    [InlineData("css/app.css")]
    [InlineData("css/chat.css")]
    [InlineData("css/roboto.css")]
    public void StaticWebAssets_EndpointsJson_HasFingerprintedLowercaseCssRoutes(string assetPath)
    {
        var baseDir = AppContext.BaseDirectory;
        var endpointsFiles = System.IO.Directory.GetFiles(baseDir, "*staticwebassets*.endpoints.json");
        if (endpointsFiles.Length == 0)
        {
            var projectDir = System.IO.Path.GetFullPath(System.IO.Path.Combine(baseDir, "..", "..", "..", "..", "Spokes_Server"));
            endpointsFiles = System.IO.Directory.GetFiles(projectDir, "*staticwebassets*.endpoints.json", System.IO.SearchOption.AllDirectories);
        }

        Assert.NotEmpty(endpointsFiles);
        var json = System.IO.File.ReadAllText(endpointsFiles[0]);
        Assert.Contains($"\"Value\":\"{assetPath}\"", json);
    }
}
