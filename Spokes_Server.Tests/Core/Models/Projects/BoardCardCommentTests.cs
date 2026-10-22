using System;
using System.Text.Json;
using Spokes_Server.Core.Models.Projects;
using Xunit;

namespace Spokes_Server.Tests.Core.Models.Projects;

public class BoardCardCommentTests
{
    [Fact]
    public void BoardCardComment_Properties_WorkCorrectly()
    {
        var now = DateTime.UtcNow;
        var comment = new BoardCardComment
        {
            Id = "comment_1",
            CardId = "card_10",
            UserId = "user_2",
            Text = "Ready for review",
            CreatedAt = now
        };

        Assert.Equal("comment_1", comment.Id);
        Assert.Equal("card_10", comment.CardId);
        Assert.Equal("user_2", comment.UserId);
        Assert.Equal("Ready for review", comment.Text);
        Assert.Equal(now, comment.CreatedAt);

        var json = JsonSerializer.Serialize(comment);
        var copy = JsonSerializer.Deserialize<BoardCardComment>(json);
        Assert.NotNull(copy);
        Assert.Equal("Ready for review", copy.Text);
    }
}
