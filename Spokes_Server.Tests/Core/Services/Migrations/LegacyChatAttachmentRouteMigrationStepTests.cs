using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Moq;
using Spokes_Server.Aggregate.Contexts;
using Spokes_Server.Core.Data.Repositories.Communication;
using Spokes_Server.Core.Models.Communication;
using Spokes_Server.Core.Services.Migrations;
using Xunit;

namespace Spokes_Server.Tests.Core.Services.Migrations;

public class LegacyChatAttachmentRouteMigrationStepTests
{
    [Theory]
    [InlineData("/internal/attachments/chan1/photo.jpg", "/spokesapi/files/chat/chan1/photo.jpg")]
    [InlineData("/internal/attachments/thumb/chan1/photo.jpg", "/spokesapi/files/chat/chan1/photo.jpg")]
    [InlineData("/spokesapi/attachments/chan2/document.pdf", "/spokesapi/files/chat/chan2/document.pdf")]
    [InlineData("/spokesapi/attachments/thumb/chan2/document.pdf", "/spokesapi/files/chat/chan2/document.pdf")]
    public void CanonicalizeAttachmentPath_LegacyPaths_RewritesToCanonicalSpokesApiFiles(string legacyPath, string expectedCanonical)
    {
        var result = LegacyChatAttachmentRouteMigrationStep.CanonicalizeAttachmentPath(legacyPath);
        Assert.Equal(expectedCanonical, result);
    }

    [Theory]
    [InlineData("/spokesapi/files/chat/chan1/photo.jpg")]
    [InlineData("/spokesapi/files/projects/123/file.pdf")]
    [InlineData("/external/cdn/image.png")]
    [InlineData("")]
    [InlineData(null)]
    public void CanonicalizeAttachmentPath_NonLegacyPaths_ReturnsNull(string? path)
    {
        var result = LegacyChatAttachmentRouteMigrationStep.CanonicalizeAttachmentPath(path!);
        Assert.Null(result);
    }

    [Fact]
    public async Task ExecuteAsync_WhenLegacyAttachmentsExist_RewritesAndSavesMessage()
    {
        // Arrange
        var mockChatMessages = new Mock<ChatMessageRepository>();
        var mockContext = new Mock<CommunicationContext>();
        mockContext.SetupGet(c => c.ChatMessages).Returns(mockChatMessages.Object);

        var msg1 = new ChatMessage
        {
            Id = "msg-1",
            ChannelId = "chan-1",
            Content = "See legacy image: ![](/internal/attachments/chan-1/pic.jpg)",
            Attachments = new List<ChatAttachment>
            {
                new()
                {
                    Id = "att-1",
                    FileName = "pic.jpg",
                    FilePath = "/internal/attachments/chan-1/pic.jpg",
                    HasServerThumbnail = true
                },
                new()
                {
                    Id = "att-2",
                    FileName = "clean.jpg",
                    FilePath = "/spokesapi/files/chat/chan-1/clean.jpg",
                    HasServerThumbnail = true
                }
            }
        };

        var msg2 = new ChatMessage
        {
            Id = "msg-2",
            ChannelId = "chan-1",
            Content = "All clean here",
            Attachments = new List<ChatAttachment>
            {
                new()
                {
                    Id = "att-3",
                    FileName = "already_clean.png",
                    FilePath = "/spokesapi/files/chat/chan-1/already_clean.png"
                }
            }
        };

        mockChatMessages.Setup(r => r.GetAll()).Returns(new List<ChatMessage> { msg1, msg2 });

        var step = new LegacyChatAttachmentRouteMigrationStep(mockContext.Object);

        // Act
        await step.ExecuteAsync();

        // Assert
        Assert.Equal("/spokesapi/files/chat/chan-1/pic.jpg", msg1.Attachments[0].FilePath);
        Assert.Equal("/spokesapi/files/chat/chan-1/clean.jpg", msg1.Attachments[1].FilePath);
        Assert.Equal("See legacy image: ![](/spokesapi/files/chat/chan-1/pic.jpg)", msg1.Content);

        mockChatMessages.Verify(r => r.Save(msg1), Times.Once);
        mockChatMessages.Verify(r => r.Save(msg2), Times.Never);
    }

    [Fact]
    public async Task ExecuteAsync_WhenLegacySpokesApiAttachmentsExist_RewritesAndSavesMessage()
    {
        // Arrange
        var mockChatMessages = new Mock<ChatMessageRepository>();
        var mockContext = new Mock<CommunicationContext>();
        mockContext.SetupGet(c => c.ChatMessages).Returns(mockChatMessages.Object);

        var msg = new ChatMessage
        {
            Id = "msg-3",
            ChannelId = "chan-2",
            Content = "Hello",
            Attachments = new List<ChatAttachment>
            {
                new()
                {
                    Id = "att-3",
                    FileName = "report.pdf",
                    FilePath = "/spokesapi/attachments/chan-2/report.pdf"
                }
            }
        };

        mockChatMessages.Setup(r => r.GetAll()).Returns(new List<ChatMessage> { msg });

        var step = new LegacyChatAttachmentRouteMigrationStep(mockContext.Object);

        // Act
        await step.ExecuteAsync();

        // Assert
        Assert.Equal("/spokesapi/files/chat/chan-2/report.pdf", msg.Attachments[0].FilePath);
        mockChatMessages.Verify(r => r.Save(msg), Times.Once);
    }
}
