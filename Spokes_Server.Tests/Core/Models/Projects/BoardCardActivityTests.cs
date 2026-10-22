using System;
using System.Text.Json;
using Spokes_Server.Core.Models.Projects;
using Xunit;

namespace Spokes_Server.Tests.Core.Models.Projects;

public class BoardCardActivityTests
{
    [Fact]
    public void BoardCardActivity_Properties_WorkCorrectly()
    {
        var now = DateTime.UtcNow;
        var act = new BoardCardActivity
        {
            Id = "act_1",
            CardId = "card_10",
            UserId = "user_1",
            Action = "moved",
            Details = "Moved from ToDo to InProgress",
            CreatedAt = now
        };

        Assert.Equal("act_1", act.Id);
        Assert.Equal("card_10", act.CardId);
        Assert.Equal("user_1", act.UserId);
        Assert.Equal("moved", act.Action);
        Assert.Equal("Moved from ToDo to InProgress", act.Details);
        Assert.Equal(now, act.CreatedAt);

        var json = JsonSerializer.Serialize(act);
        var copy = JsonSerializer.Deserialize<BoardCardActivity>(json);
        Assert.NotNull(copy);
        Assert.Equal("moved", copy.Action);
    }
}
