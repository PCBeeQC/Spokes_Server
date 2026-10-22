using System.Collections.Generic;
using System.Text.Json;
using Spokes_Server.Core.Models.HR;
using Xunit;

namespace Spokes_Server.Tests.Core.Models.HR;

public class VoiceUserSettingsTests
{
    [Fact]
    public void VoiceUserSettings_DefaultsAndProperties_WorkCorrectly()
    {
        var settings = new VoiceUserSettings();
        Assert.Equal(100.0, settings.MasterVolume);
        Assert.Equal(50.0, settings.VoiceSensitivity);
        Assert.Empty(settings.ParticipantVolumes);

        settings.MasterVolume = 120.0;
        settings.VoiceSensitivity = 65.0;
        settings.ParticipantVolumes["peer_1"] = 80.0;

        Assert.Equal(120.0, settings.MasterVolume);
        Assert.Equal(65.0, settings.VoiceSensitivity);
        Assert.Equal(80.0, settings.ParticipantVolumes["peer_1"]);

        var json = JsonSerializer.Serialize(settings);
        var copy = JsonSerializer.Deserialize<VoiceUserSettings>(json);
        Assert.NotNull(copy);
        Assert.Equal(120.0, copy.MasterVolume);
    }
}
