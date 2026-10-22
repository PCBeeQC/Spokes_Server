using System.Collections.Generic;
using System.Text.Json;
using Spokes_Server.Core.Models.Communication.Notifications;
using Xunit;

namespace Spokes_Server.Tests.Core.Models.Communication.Notifications;

public class NotificationSoundDefinitionTests
{
    [Fact]
    public void NotificationSoundDefinition_PropertiesAndSupportsCategory_WorkCorrectly()
    {
        var sound = new NotificationSoundDefinition
        {
            Id = "chime",
            DisplayName = "Chime",
            FileName = "chime.wav",
            Description = "Gentle chime sound",
            SupportedCategories = new List<string> { "chat", "call" },
            IsSpecial = false
        };

        Assert.Equal("chime", sound.Id);
        Assert.Equal("Chime", sound.DisplayName);
        Assert.Equal("chime.wav", sound.FileName);
        Assert.Equal("Gentle chime sound", sound.Description);
        Assert.False(sound.IsSpecial);

        Assert.True(sound.SupportsCategory("chat"));
        Assert.True(sound.SupportsCategory("call"));
        Assert.False(sound.SupportsCategory("calendar"));

        var wildcardSound = new NotificationSoundDefinition
        {
            Id = "any",
            SupportedCategories = new List<string> { "*" }
        };
        Assert.True(wildcardSound.SupportsCategory("anything"));

        var json = JsonSerializer.Serialize(sound);
        var copy = JsonSerializer.Deserialize<NotificationSoundDefinition>(json);
        Assert.NotNull(copy);
        Assert.Equal(sound.Id, copy.Id);
    }
}
