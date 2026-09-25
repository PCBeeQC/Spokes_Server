namespace Spokes_Server.Tests.Core.Models.UI;

using Spokes_Server.Core.Models.UI;

public class LightboxMediaItemTests
{
    [Fact]
    public void Defaults_AreSetCorrectly()
    {
        var item = new LightboxMediaItem();

        Assert.False(string.IsNullOrWhiteSpace(item.Id));
        Assert.True(Guid.TryParse(item.Id, out _));
        Assert.Equal(string.Empty, item.FileName);
        Assert.Equal(string.Empty, item.FilePath);
        Assert.Equal(string.Empty, item.ContentType);
        Assert.Null(item.AddedAt);
        Assert.Equal(0L, item.FileSizeBytes);
        Assert.False(item.HasServerThumbnail);
    }

    [Fact]
    public void Properties_CanBeSetAndRetrieved()
    {
        var now = DateTime.UtcNow;
        var item = new LightboxMediaItem
        {
            Id = "custom-id-123",
            FileName = "photo.png",
            FilePath = "/images/photo.png",
            ContentType = "image/png",
            AddedAt = now,
            FileSizeBytes = 1024L,
            HasServerThumbnail = true
        };

        Assert.Equal("custom-id-123", item.Id);
        Assert.Equal("photo.png", item.FileName);
        Assert.Equal("/images/photo.png", item.FilePath);
        Assert.Equal("image/png", item.ContentType);
        Assert.Equal(now, item.AddedAt);
        Assert.Equal(1024L, item.FileSizeBytes);
        Assert.True(item.HasServerThumbnail);
    }

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
    public void ThumbnailUrl_WhenHasServerThumbnailIsTrue_AndFilePathStartsWithInternalAttachments_ReturnsThumbnailPath()
    {
        var item = new LightboxMediaItem
        {
            HasServerThumbnail = true,
            FilePath = "/internal/attachments/doc.pdf"
        };

        Assert.Equal("/internal/attachments/thumb/doc.pdf", item.ThumbnailUrl);
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
