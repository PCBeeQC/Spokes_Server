namespace Spokes_Server.Tests.Components.UI;

using Spokes_Server.Components.UI;

public class LightboxMediaItemTests
{
    [Theory]
    [InlineData("/spokesapi/files/test.png")]
    [InlineData("/internal/attachments/test.pdf")]
    [InlineData("/custom/path/image.jpg")]
    public void ThumbnailUrl_WhenHasServerThumbnailIsFalse_ReturnsFilePath(string filePath)
    {
        var item = new LightboxMediaItem
        {
            HasServerThumbnail = false,
            FilePath = filePath
        };

        Assert.Equal(filePath, item.ThumbnailUrl);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ThumbnailUrl_WhenFilePathIsNullOrWhiteSpace_ReturnsFilePath(string? filePath)
    {
        var item = new LightboxMediaItem
        {
            HasServerThumbnail = true,
            FilePath = filePath!
        };

        Assert.Equal(filePath, item.ThumbnailUrl);
    }

    [Fact]
    public void ThumbnailUrl_WhenHasServerThumbnailIsTrue_AndFilePathStartsWithSpokesApiFiles_ReturnsThumbnailPath()
    {
        var item = new LightboxMediaItem
        {
            HasServerThumbnail = true,
            FilePath = "/spokesapi/files/photo.jpg"
        };

        Assert.Equal("/spokesapi/files/thumb/photo.jpg", item.ThumbnailUrl);
    }

    [Fact]
    public void ThumbnailUrl_WhenHasServerThumbnailIsTrue_AndFilePathStartsWithInternalAttachments_ReturnsThumbUrl()
    {
        var item = new LightboxMediaItem
        {
            HasServerThumbnail = true,
            FilePath = "/internal/attachments/doc.pdf"
        };

        Assert.Equal("/internal/attachments/thumb/doc.pdf", item.ThumbnailUrl);
    }

    [Fact]
    public void ThumbnailUrl_WhenHasServerThumbnailIsTrue_AndFilePathStartsWithSpokesApiAttachments_ReturnsThumbUrl()
    {
        var item = new LightboxMediaItem
        {
            HasServerThumbnail = true,
            FilePath = "/spokesapi/attachments/chan/photo.png"
        };

        Assert.Equal("/spokesapi/attachments/thumb/chan/photo.png", item.ThumbnailUrl);
    }

    [Theory]
    [InlineData("/custom/path/img.png")]
    [InlineData("https://example.com/image.png")]
    [InlineData("/other/files/photo.jpg")]
    public void ThumbnailUrl_WhenHasServerThumbnailIsTrue_AndFilePathIsOtherPath_ReturnsFilePathUnchanged(string filePath)
    {
        var item = new LightboxMediaItem
        {
            HasServerThumbnail = true,
            FilePath = filePath
        };

        Assert.Equal(filePath, item.ThumbnailUrl);
    }
}
