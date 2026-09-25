using Spokes_Server.Core.Helpers;
using Spokes_Server.Core.Models.Communication;

namespace Spokes_Server.Tests.Core.Helpers;

public class ChatPreviewFormatterTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void FormatPreviewContent_WhenRawContentIsEmpty_AndAttachmentsNullOrEmpty_ReturnsDefaultAttachment(string? rawContent)
    {
        // Act
        var resultNull = ChatPreviewFormatter.FormatPreviewContent(rawContent, null);
        var resultEmpty = ChatPreviewFormatter.FormatPreviewContent(rawContent, new List<ChatAttachment>());

        // Assert
        Assert.Equal("📎 Attachment", resultNull);
        Assert.Equal("📎 Attachment", resultEmpty);
    }

    [Theory]
    [InlineData("VoiceMessage_20260914.m4a", "audio/mp4", "🎤 Voice Memo")]
    [InlineData("VoiceMessage_test.wav", "", "🎤 Voice Memo")]
    [InlineData("song.mp3", "audio/mpeg", "🎵 Sent audio")]
    [InlineData("audio.wav", "audio/wav", "🎵 Sent audio")]
    [InlineData("photo.png", "image/png", "📷 Sent an image")]
    [InlineData("photo.jpeg", "image/jpeg", "📷 Sent an image")]
    [InlineData("video.mp4", "video/mp4", "🎞️ Sent a video")]
    [InlineData("clip.mov", "video/quicktime", "🎞️ Sent a video")]
    [InlineData("document.pdf", "application/pdf", "📎 Sent a file")]
    [InlineData("spreadsheet.xlsx", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", "📎 Sent a file")]
    public void FormatPreviewContent_WhenRawContentIsEmpty_FormatsAttachmentsProperly(string fileName, string contentType, string expected)
    {
        // Arrange
        var attachments = new List<ChatAttachment>
        {
            new() { FileName = fileName, ContentType = contentType }
        };

        // Act
        var result = ChatPreviewFormatter.FormatPreviewContent("", attachments);

        // Assert
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("![gif](https://media.giphy.com/media/abc/giphy.gif)", "Sent a GIF")]
    [InlineData("![GIF](https://media.giphy.com/media/abc/giphy.gif)", "Sent a GIF")]
    [InlineData("[gif]", "Sent a GIF")]
    [InlineData("[GIF]", "Sent a GIF")]
    [InlineData("Check this out: ![gif](https://giphy.com/media/abc/giphy.gif) haha", "Check this out: Sent a GIF haha")]
    [InlineData("Check this out: [gif] haha", "Check this out: Sent a GIF haha")]
    public void FormatPreviewContent_ReplacesGifs_WithSentAGIF(string rawContent, string expected)
    {
        // Act
        var result = ChatPreviewFormatter.FormatPreviewContent(rawContent);

        // Assert
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("#[Kickoff Meeting](event:123)", "📅 Kickoff Meeting")]
    [InlineData("#[Summer Trip](album:456)", "shared Summer Trip album")]
    [InlineData("#[Website Redesign](project:789)", "shared Website Redesign project")]
    [InlineData("#[Q-100](quote:1)", "shared Q-100 quote")]
    [InlineData("#[INV-200](invoice:2)", "shared INV-200 invoice")]
    [InlineData("#[Custom Item](ticket:99)", "shared Custom Item ticket")]
    [InlineData("#[Quarterly Review](EVENT:42)", "📅 Quarterly Review")]
    [InlineData("See #[Sprint 1](project:10) for details", "See shared Sprint 1 project for details")]
    public void FormatPreviewContent_ReplacesRichTags_WithReadableText(string rawContent, string expected)
    {
        // Act
        var result = ChatPreviewFormatter.FormatPreviewContent(rawContent);

        // Assert
        Assert.Equal(expected, result);
    }

    [Fact]
    public void FormatPreviewContent_WhenPlainText_ReturnsOriginalText()
    {
        // Arrange
        const string input = "Hello world, this is a plain message.";

        // Act
        var result = ChatPreviewFormatter.FormatPreviewContent(input);

        // Assert
        Assert.Equal(input, result);
    }

    [Fact]
    public void FormatPreviewContent_WhenRawContentHasTextAndAttachments_ReturnsFormattedTextIgnoringAttachments()
    {
        // Arrange
        var attachments = new List<ChatAttachment>
        {
            new() { FileName = "photo.jpg", ContentType = "image/jpeg" }
        };

        // Act
        var result = ChatPreviewFormatter.FormatPreviewContent("Message with image", attachments);

        // Assert
        Assert.Equal("Message with image", result);
    }

    [Fact]
    public void FormatAndTruncatePreview_TruncatesLongString_AndAppendsEllipsis()
    {
        // Arrange
        const string input = "This is a long message that exceeds the maximum length";
        const int maxLength = 20;

        // Act
        var result = ChatPreviewFormatter.FormatAndTruncatePreview(input, maxLength: maxLength);

        // Assert
        Assert.Equal(maxLength, result.Length);
        Assert.Equal("This is a long me...", result);
        Assert.EndsWith("...", result);
    }

    [Theory]
    [InlineData("Short message", 30)]
    [InlineData("ExactLengthString!", 18)]
    public void FormatAndTruncatePreview_WhenShortOrExact_DoesNotTruncate(string input, int maxLength)
    {
        // Act
        var result = ChatPreviewFormatter.FormatAndTruncatePreview(input, maxLength: maxLength);

        // Assert
        Assert.Equal(input, result);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void FormatAndTruncatePreview_WhenRawContentNullOrEmpty_ReturnsAttachmentPreview(string? rawContent)
    {
        // Act
        var resultWithoutAttachments = ChatPreviewFormatter.FormatAndTruncatePreview(rawContent);
        var resultWithVoiceMemo = ChatPreviewFormatter.FormatAndTruncatePreview(rawContent, new List<ChatAttachment>
        {
            new() { FileName = "VoiceMessage_123.m4a", ContentType = "audio/mp4" }
        });

        // Assert
        Assert.Equal("📎 Attachment", resultWithoutAttachments);
        Assert.Equal("🎤 Voice Memo", resultWithVoiceMemo);
    }

    [Fact]
    public void FormatAndTruncatePreview_FormatsRichTagsAndGifsBeforeTruncating()
    {
        // Arrange
        const string input = "#[Super Long Project Title That Exceeds Limit](project:999) extra text";
        const int maxLength = 30;

        // Act
        var result = ChatPreviewFormatter.FormatAndTruncatePreview(input, maxLength: maxLength);

        // Assert
        Assert.Equal(maxLength, result.Length);
        Assert.EndsWith("...", result);
        Assert.StartsWith("shared Super Long Project", result);
    }
}
