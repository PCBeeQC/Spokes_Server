using System;
using System.IO;
using System.Text;
using Spokes_Server.Core.Services.Identity;
using Xunit;

namespace Spokes_Server.Tests.Core.Services.Identity;

public class CasdoorAssetPatcherTests
{
    private const string RealCasdoorV4Sample =
        "function wM(){wS()}function Gh(){let e=window.location.origin;return(e===\"http://localhost:7002\"||e===\"http://localhost:7001\")&&(e=\"http://localhost:8000\"),e}function kk(e){if(!e)return!1;try{return new URL(e).origin===Gh()}catch{return!1}}function xM(){return Gh()}";

    [Fact]
    public void PatchScriptContent_WithMinifiedCasdoorV4Pattern_ReplacesWithCallbackAwareFunction()
    {
        // Act
        var result = CasdoorAssetPatcher.PatchScriptContent(RealCasdoorV4Sample);

        // Assert
        Assert.NotEqual(RealCasdoorV4Sample, result);
        Assert.Contains(CasdoorAssetPatcher.CallbackCheckMarker, result);
        Assert.Contains("function kk(e){if(!e)return!1;try{let _u=new URL(e);return _u.origin===Gh()&&(_u.pathname===\"/callback\"||_u.pathname.startsWith(\"/callback/\"))}catch{return!1}}", result);
        Assert.DoesNotContain("try{return new URL(e).origin===Gh()}", result);
    }

    [Fact]
    public void PatchScriptContent_WhenAlreadyPatched_ReturnsUnmodifiedString()
    {
        // Arrange
        var patched = CasdoorAssetPatcher.PatchScriptContent(RealCasdoorV4Sample);

        // Act
        var result = CasdoorAssetPatcher.PatchScriptContent(patched);

        // Assert
        Assert.Equal(patched, result);
    }

    [Fact]
    public void PatchScriptContent_WithDifferentIdentifiers_PreservesIdentifiers()
    {
        // Arrange
        const string customIdentifiers =
            "function myFunc(targetUrl){if(!targetUrl)return!1;try{return new URL(targetUrl).origin===myServerUrl()}catch{return!1}}";

        // Act
        var result = CasdoorAssetPatcher.PatchScriptContent(customIdentifiers);

        // Assert
        Assert.Contains("function myFunc(targetUrl){if(!targetUrl)return!1;try{let _u=new URL(targetUrl);return _u.origin===myServerUrl()&&(_u.pathname===\"/callback\"||_u.pathname.startsWith(\"/callback/\"))}catch{return!1}}", result);
    }

    [Fact]
    public void PatchScriptContent_WithFallbackPattern_ReplacesCorrectly()
    {
        // Arrange
        const string fallbackSample =
            "const check = () => { return new URL(uri).origin===getServer() };";

        // Act
        var result = CasdoorAssetPatcher.PatchScriptContent(fallbackSample);

        // Assert
        Assert.Contains("let _u=new URL(uri);return _u.origin===getServer()&&(_u.pathname===\"/callback\"||_u.pathname.startsWith(\"/callback/\"))", result);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("console.log('hello world'); const x = 123;")]
    public void PatchScriptContent_WithUnrelatedScript_ReturnsUnmodified(string input)
    {
        // Act
        var result = CasdoorAssetPatcher.PatchScriptContent(input);

        // Assert
        Assert.Equal(input, result);
    }

    [Fact]
    public void PatchAssets_WhenDirectoryDoesNotExist_ReturnsFalseWithoutThrowing()
    {
        // Arrange
        var nonExistentPath = Path.Combine(Path.GetTempPath(), "spokes-test-nonexistent-" + Guid.NewGuid());

        // Act
        var result = CasdoorAssetPatcher.PatchAssets(nonExistentPath);

        // Assert
        Assert.False(result);
    }

    [Fact]
    public void PatchAssets_WhenAssetsExist_PatchesFilesAndIsIdempotent()
    {
        // Arrange
        var tempDir = Path.Combine(Path.GetTempPath(), "spokes-casdoor-assets-" + Guid.NewGuid());
        Directory.CreateDirectory(tempDir);

        try
        {
            var indexFile = Path.Combine(tempDir, "index-test123.js");
            var loginFile = Path.Combine(tempDir, "LoginPage-test456.js");
            var otherFile = Path.Combine(tempDir, "other-vendor.js");
            var htmlFile = Path.Combine(tempDir, "index.html");

            File.WriteAllText(indexFile, RealCasdoorV4Sample, Encoding.UTF8);
            File.WriteAllText(loginFile, RealCasdoorV4LoginPageSample, Encoding.UTF8);
            File.WriteAllText(otherFile, RealCasdoorV4Sample, Encoding.UTF8); // should not be touched (not index-*.js or LoginPage-*.js)
            File.WriteAllText(htmlFile, RealCasdoorV4IndexHtmlSample, Encoding.UTF8);

            // Act 1: Initial patch
            var firstRun = CasdoorAssetPatcher.PatchAssets(tempDir);

            // Assert 1
            Assert.True(firstRun);
            var indexContent = File.ReadAllText(indexFile, Encoding.UTF8);
            Assert.Contains(CasdoorAssetPatcher.CallbackCheckMarker, indexContent);

            var loginContent = File.ReadAllText(loginFile, Encoding.UTF8);
            Assert.Contains(CasdoorAssetPatcher.LoginPageBypassMarker, loginContent);

            var htmlContent = File.ReadAllText(htmlFile, Encoding.UTF8);
            Assert.Contains("src=\"/assets/index-CqsIVomY.js\"", htmlContent);
            Assert.DoesNotContain("?v=", htmlContent);

            var otherContent = File.ReadAllText(otherFile, Encoding.UTF8);
            Assert.Equal(RealCasdoorV4Sample, otherContent); // Non-matching file untouched

            // Act 2: Second run (idempotency check)
            var secondRun = CasdoorAssetPatcher.PatchAssets(tempDir);

            // Assert 2
            Assert.False(secondRun); // nothing modified
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    private const string RealCasdoorV4LoginPageSample =
        "const o=()=>{let e={};e.type=t?.responseType??(i===\"device\"?\"device\":\"login\"),e.type!==\"device\"&&Dt(t==null?void 0:t.redirectUri)&&(e.type=\"login\");return e};";

    private const string RealCasdoorV4IndexHtmlSample =
        "<!DOCTYPE html><html lang=\"en\"><head><meta charset=\"UTF-8\" /><script type=\"module\" crossorigin src=\"/assets/index-CqsIVomY.js\"></script><link rel=\"stylesheet\" crossorigin href=\"/assets/index-BYx.css\"></head><body><div id=\"root\"></div></body></html>";

    [Fact]
    public void PatchLoginPageContent_WithCasdoorV4Pattern_ReplacesWithBypassMarker()
    {
        // Act
        var result = CasdoorAssetPatcher.PatchLoginPageContent(RealCasdoorV4LoginPageSample);

        // Assert
        Assert.NotEqual(RealCasdoorV4LoginPageSample, result);
        Assert.Contains(CasdoorAssetPatcher.LoginPageBypassMarker, result);
        Assert.Contains("e.type!==\"device\"&&!1&&(e.type=\"login\")", result);
        Assert.DoesNotContain("Dt(t==null?void 0:t.redirectUri)", result);
    }

    [Fact]
    public void PatchLoginPageContent_WhenAlreadyPatched_ReturnsUnmodifiedString()
    {
        // Arrange
        var patched = CasdoorAssetPatcher.PatchLoginPageContent(RealCasdoorV4LoginPageSample);

        // Act
        var result = CasdoorAssetPatcher.PatchLoginPageContent(patched);

        // Assert
        Assert.Equal(patched, result);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("console.log('LoginPage without redirect logic');")]
    public void PatchLoginPageContent_WithUnrelatedScript_ReturnsUnmodified(string input)
    {
        // Act
        var result = CasdoorAssetPatcher.PatchLoginPageContent(input);

        // Assert
        Assert.Equal(input, result);
    }

    [Fact]
    public void PatchIndexHtmlContent_WithCleanScriptTag_ReturnsUnmodifiedString()
    {
        // Act
        var result = CasdoorAssetPatcher.PatchIndexHtmlContent(RealCasdoorV4IndexHtmlSample);

        // Assert
        Assert.Equal(RealCasdoorV4IndexHtmlSample, result);
        Assert.Contains("<script type=\"module\" crossorigin src=\"/assets/index-CqsIVomY.js\"></script>", result);
    }

    [Fact]
    public void PatchIndexHtmlContent_WhenStampedWithVersionQuery_StripsQueryParameterToPreventModuleSplit()
    {
        // Arrange
        var versionedHtml = RealCasdoorV4IndexHtmlSample.Replace("src=\"/assets/index-CqsIVomY.js\"", "src=\"/assets/index-CqsIVomY.js?v=spokes-v4\"");

        // Act
        var result = CasdoorAssetPatcher.PatchIndexHtmlContent(versionedHtml);

        // Assert
        Assert.Equal(RealCasdoorV4IndexHtmlSample, result);
        Assert.DoesNotContain("?v=", result);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("<html><head><title>No scripts</title></head></html>")]
    public void PatchIndexHtmlContent_WithUnrelatedHtml_ReturnsUnmodified(string input)
    {
        // Act
        var result = CasdoorAssetPatcher.PatchIndexHtmlContent(input);

        // Assert
        Assert.Equal(input, result);
    }
}
