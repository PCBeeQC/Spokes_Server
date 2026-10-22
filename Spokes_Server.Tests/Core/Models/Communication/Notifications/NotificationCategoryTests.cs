using System.Text.Json;
using Spokes_Server.Core.Models.Communication.Notifications;
using Xunit;

namespace Spokes_Server.Tests.Core.Models.Communication.Notifications;

public class NotificationCategoryTests
{
    [Fact]
    public void NotificationCategoryDefinition_Properties_WorkCorrectly()
    {
        var cat = new NotificationCategoryDefinition
        {
            Id = "chat_message",
            DisplayName = "Chat Messages",
            Description = "Notifications for direct and channel messages",
            DefaultSoundId = "bell",
            DefaultSoundFileName = "bell.wav",
            SortOrder = 1
        };

        Assert.Equal("chat_message", cat.Id);
        Assert.Equal("Chat Messages", cat.DisplayName);
        Assert.Equal("Notifications for direct and channel messages", cat.Description);
        Assert.Equal("bell", cat.DefaultSoundId);
        Assert.Equal("bell.wav", cat.DefaultSoundFileName);
        Assert.Equal(1, cat.SortOrder);

        var json = JsonSerializer.Serialize(cat);
        var copy = JsonSerializer.Deserialize<NotificationCategoryDefinition>(json);
        Assert.NotNull(copy);
        Assert.Equal(cat.Id, copy.Id);
        Assert.Equal(cat.DisplayName, copy.DisplayName);
    }
}
