using System;
using System.Collections.Generic;
using System.Text.Json;
using Spokes_Server.Core.Models.Communication;
using Xunit;

namespace Spokes_Server.Tests.Core.Models.Communication;

public class UserClientStateTests
{
    [Fact]
    public void UserClientState_Properties_WorkCorrectly()
    {
        var now = DateTime.UtcNow;
        var state = new UserClientState
        {
            Id = "user_42",
            UserId = "user_42",
            ActiveChannelId = "c_general",
            ChannelDrafts = new Dictionary<string, string> { ["c_general"] = "Draft text" },
            LastUpdatedAt = now
        };

        Assert.Equal("user_42", state.Id);
        Assert.Equal("user_42", state.UserId);
        Assert.Equal("c_general", state.ActiveChannelId);
        Assert.Equal("Draft text", state.ChannelDrafts["c_general"]);
        Assert.Equal(now, state.LastUpdatedAt);

        var json = JsonSerializer.Serialize(state);
        var copy = JsonSerializer.Deserialize<UserClientState>(json);
        Assert.NotNull(copy);
        Assert.Equal("user_42", copy.Id);
        Assert.Equal("c_general", copy.ActiveChannelId);
    }
}
