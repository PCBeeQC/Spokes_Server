using Spokes_Server.Core.Models.Communication;

namespace Spokes_Server.Tests.Core.Models.Communication;

public class ChatMessageTests
{

    #region Equals & GetHashCode Tests

    [Fact]
    public void Equals_And_GetHashCode_Behavior()
    {
        var sharedId = "msg-hash-test-id";
        var msg1 = new ChatMessage { Id = sharedId, Content = "Content 1" };
        var msg2 = new ChatMessage { Id = sharedId, Content = "Content 2" };
        var msg3 = new ChatMessage { Id = "id-2" };
        var nullIdMsg = new ChatMessage { Id = null! };

        Assert.True(msg1.Equals(msg1));
        Assert.True(msg1.Equals(msg2));
        Assert.True(msg2.Equals(msg1));
        Assert.Equal(msg1.GetHashCode(), msg2.GetHashCode());
        Assert.False(msg1.Equals(msg3));
        Assert.False(msg3.Equals(msg1));
        Assert.False(msg1.Equals(null));
        Assert.False(msg1.Equals("a string"));
        Assert.False(msg1.Equals(new object()));
        Assert.Null(Record.Exception(() => nullIdMsg.GetHashCode()));
    }

    #endregion

    #region ChatAttachment Tests

    [Fact]
    public void ChatAttachment_ThumbnailUrl_WhenHasServerThumbnailIsFalse_SelfHealableImageUsesThumb_NonImageReturnsFilePath()
    {
        var imageAttachment = new ChatAttachment
        {
            HasServerThumbnail = false,
            FilePath = "/spokesapi/files/photo.png"
        };

        Assert.True(imageAttachment.CanUseServerThumbnail);
        Assert.Equal("/spokesapi/files/thumb/photo.png", imageAttachment.ThumbnailUrl);

        var videoAttachment = new ChatAttachment
        {
            HasServerThumbnail = false,
            FilePath = "/spokesapi/files/clip.mp4"
        };

        Assert.False(videoAttachment.CanUseServerThumbnail);
        Assert.Equal("/spokesapi/files/clip.mp4", videoAttachment.ThumbnailUrl);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ChatAttachment_ThumbnailUrl_WhenFilePathIsNullOrWhitespace_ReturnsFilePath(string? filePath)
    {
        var attachment = new ChatAttachment
        {
            HasServerThumbnail = true,
            FilePath = filePath!
        };

        Assert.Equal(filePath, attachment.ThumbnailUrl);
    }

    [Fact]
    public void ChatAttachment_ThumbnailUrl_WhenHasServerThumbnailIsTrue_SpokesApiFiles_ReplacesPrefix()
    {
        var attachment = new ChatAttachment
        {
            HasServerThumbnail = true,
            FilePath = "/spokesapi/files/images/doc.png"
        };

        Assert.Equal("/spokesapi/files/thumb/images/doc.png", attachment.ThumbnailUrl);
    }

    [Fact]
    public void ChatAttachment_ThumbnailUrl_WhenHasServerThumbnailIsTrue_InternalAttachmentsPrefix_ReturnsThumbUrl()
    {
        var attachment = new ChatAttachment
        {
            HasServerThumbnail = true,
            FilePath = "/internal/attachments/avatar.jpg"
        };

        Assert.Equal("/internal/attachments/thumb/avatar.jpg", attachment.ThumbnailUrl);
    }

    [Fact]
    public void ChatAttachment_ThumbnailUrl_WhenHasServerThumbnailIsTrue_SpokesApiAttachmentsPrefix_ReturnsThumbUrl()
    {
        var attachment = new ChatAttachment
        {
            HasServerThumbnail = true,
            FilePath = "/spokesapi/attachments/channel1/photo.jpg"
        };

        Assert.Equal("/spokesapi/attachments/thumb/channel1/photo.jpg", attachment.ThumbnailUrl);
    }

    [Fact]
    public void ChatAttachment_ThumbnailUrl_WhenHasServerThumbnailIsTrue_OtherPrefix_ReturnsFilePath()
    {
        var attachment = new ChatAttachment
        {
            HasServerThumbnail = true,
            FilePath = "/external/cdn/photo.png"
        };

        Assert.Equal("/external/cdn/photo.png", attachment.ThumbnailUrl);
    }

    #endregion

}
