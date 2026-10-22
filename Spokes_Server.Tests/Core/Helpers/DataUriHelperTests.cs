using System.Text;
using Spokes_Server.Core.Helpers;

namespace Spokes_Server.Tests.Core.Helpers;

public class DataUriHelperTests
{
    [Fact]
    public void TryParse_WithValidPngDataUri_ExtractsBytesAndMime()
    {
        var rawText = "Hello World";
        var rawBytes = Encoding.UTF8.GetBytes(rawText);
        var base64 = Convert.ToBase64String(rawBytes);
        var dataUri = $"data:image/png;base64,{base64}";

        var success = DataUriHelper.TryParse(dataUri, out var bytes, out var mime);

        Assert.True(success);
        Assert.NotNull(bytes);
        Assert.Equal(rawBytes, bytes);
        Assert.Equal("image/png", mime);
    }

    [Fact]
    public void TryParse_WithValidJpegDataUri_ExtractsJpegMime()
    {
        var rawBytes = new byte[] { 0xFF, 0xD8, 0xFF };
        var base64 = Convert.ToBase64String(rawBytes);
        var dataUri = $"data:image/jpeg;base64,{base64}";

        var success = DataUriHelper.TryParse(dataUri, out var bytes, out var mime);

        Assert.True(success);
        Assert.NotNull(bytes);
        Assert.Equal(rawBytes, bytes);
        Assert.Equal("image/jpeg", mime);
    }

    [Fact]
    public void TryParse_WithRawBase64_UsesDefaultMime()
    {
        var rawBytes = new byte[] { 1, 2, 3, 4 };
        var base64 = Convert.ToBase64String(rawBytes);

        var success = DataUriHelper.TryParse(base64, out var bytes, out var mime, "application/pdf");

        Assert.True(success);
        Assert.NotNull(bytes);
        Assert.Equal(rawBytes, bytes);
        Assert.Equal("application/pdf", mime);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("data:image/png;base64,!!!NotBase64!!!")]
    public void TryParse_WithInvalidInputs_ReturnsFalse(string? input)
    {
        var success = DataUriHelper.TryParse(input, out var bytes, out var mime);

        Assert.False(success);
        Assert.Null(bytes);
        Assert.Null(mime);
    }

    [Fact]
    public void TryParseImage_WithRasterMimeTypes_SucceedsAndNormalizes()
    {
        var rawBytes = new byte[] { 1, 2, 3 };
        var base64 = Convert.ToBase64String(rawBytes);

        Assert.True(DataUriHelper.TryParseImage($"data:image/png;base64,{base64}", out _, out var mime1));
        Assert.Equal("image/png", mime1);

        Assert.True(DataUriHelper.TryParseImage($"data:image/jpg;base64,{base64}", out _, out var mime2));
        Assert.Equal("image/jpeg", mime2);

        Assert.True(DataUriHelper.TryParseImage($"data:image/webp;base64,{base64}", out _, out var mime3));
        Assert.Equal("image/webp", mime3);
    }

    [Fact]
    public void TryParseImage_WithNonImageMime_ReturnsFalse()
    {
        var rawBytes = new byte[] { 1, 2, 3 };
        var base64 = Convert.ToBase64String(rawBytes);
        var dataUri = $"data:application/pdf;base64,{base64}";

        var success = DataUriHelper.TryParseImage(dataUri, out var bytes, out var mime);

        Assert.False(success);
        Assert.Null(bytes);
        Assert.Null(mime);
    }

    [Theory]
    [InlineData("image/png", ".png")]
    [InlineData("image/jpeg", ".jpg")]
    [InlineData("image/jpg", ".jpg")]
    [InlineData("image/webp", ".webp")]
    [InlineData("application/pdf", ".pdf")]
    [InlineData("text/plain", ".txt")]
    [InlineData("unknown/type", ".bin")]
    [InlineData(null, ".bin")]
    public void GetExtensionForMimeType_ReturnsExpectedExtensions(string? mime, string expectedExt)
    {
        Assert.Equal(expectedExt, DataUriHelper.GetExtensionForMimeType(mime));
    }
}
