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
    [InlineData("./js/voiceInterop.js")]
    [InlineData("./js/voicePip.js")]
    [InlineData("./js/mediaZoomGesture.js")]
    [InlineData("./Components/Dialogs/Common/MediaViewerDialog.razor.js")]
    public void ResolveModulePath_AppendsContentHashForProjectAssets_AndLeavesSyntheticTestPathsUnchanged(string assetPath)
    {
        var resolved = AssetExtensions.ResolveModulePath(assetPath);
        Assert.StartsWith($"{assetPath}?v=", resolved);

        var hashPart = resolved.Substring($"{assetPath}?v=".Length);
        Assert.False(string.IsNullOrWhiteSpace(hashPart));
        Assert.NotEqual(AssetExtensions.AppVersion, hashPart);

        // Synthetic test paths not on disk are left unchanged for unit test mocks
        Assert.Equal("./testModule.js", AssetExtensions.ResolveModulePath("./testModule.js"));
    }

    [Fact]
    public void BuildImportMapJson_IncludesAllWwwrootJsAndCollocatedRazorJsModulesWithContentHashes()
    {
        var json = AssetExtensions.BuildImportMapJson();
        Assert.False(string.IsNullOrWhiteSpace(json));

        using var doc = System.Text.Json.JsonDocument.Parse(json);
        Assert.True(doc.RootElement.TryGetProperty("imports", out var imports));

        string[] expectedSpecifiers =
        [
            "./js/voiceInterop.js",
            "/js/voiceInterop.js",
            "./js/voicePip.js",
            "/js/voicePip.js",
            "./js/mediaZoomGesture.js",
            "/js/mediaZoomGesture.js",
            "./js/noiseSuppressionManager.js",
            "./js/customRNNoiseProcessor.js",
            "./js/voiceCaptureConditioner.js",
            "/js/voiceLevelerWorklet.js",
            "/lib/noise-suppression/deepfilternet-processor.js",
            "/lib/noise-suppression/rnnoise-processor.js",
            "./Components/Dialogs/Common/MediaViewerDialog.razor.js",
            "./Components/Pages/Chat/Chat.razor.js",
            "./Components/Shared/GlobalVoiceBar.razor.js"
        ];

        foreach (var spec in expectedSpecifiers)
        {
            Assert.True(imports.TryGetProperty(spec, out var targetEl), $"Expected '{spec}' in importmap.");
            var target = targetEl.GetString();
            Assert.False(string.IsNullOrWhiteSpace(target));
            Assert.Contains("?v=", target);
        }

        string[] excludedClassicScripts =
        [
            "./js/spokes-boot.js",
            "/js/spokes-boot.js",
            "./js/spokes-app.js",
            "./js/spokesbridge.js",
            "./js/mediaLoader.js",
            "./service-worker.js",
            "./lib/quill/quill.js",
            "./lib/cropperjs/cropper.min.js",
            "./lib/livekit/livekit-client.umd.min.js",
            "./lib/noise-suppression/generated/rnnoise-sync.js"
        ];

        foreach (var spec in excludedClassicScripts)
        {
            Assert.False(imports.TryGetProperty(spec, out _), $"Did not expect classic/UMD script '{spec}' in scoped importmap.");
        }
    }
}
