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
        Assert.True(settings.AutoGainControl);
        Assert.True(settings.IncomingVoiceNormalization);
        Assert.Empty(settings.ParticipantVolumes);

        settings.MasterVolume = 120.0;
        settings.VoiceSensitivity = 65.0;
        settings.AutoGainControl = false;
        settings.IncomingVoiceNormalization = false;
        settings.ParticipantVolumes["peer_1"] = 80.0;

        Assert.Equal(120.0, settings.MasterVolume);
        Assert.Equal(65.0, settings.VoiceSensitivity);
        Assert.False(settings.AutoGainControl);
        Assert.False(settings.IncomingVoiceNormalization);
        Assert.Equal(80.0, settings.ParticipantVolumes["peer_1"]);

        var json = JsonSerializer.Serialize(settings);
        var copy = JsonSerializer.Deserialize<VoiceUserSettings>(json, VoiceUserSettings.JsonOptions);
        Assert.NotNull(copy);
        Assert.Equal(120.0, copy.MasterVolume);
        Assert.False(copy.AutoGainControl);
        Assert.False(copy.IncomingVoiceNormalization);
    }

    [Fact]
    public void VoiceUserSettings_LegacyJsonWithoutAutoVolumeFields_DefaultsToEnabled()
    {
        const string legacyPascalJson = """{"MasterVolume":115,"ParticipantVolumes":{"u1":90},"VoiceSensitivity":40}""";
        var fromPascal = JsonSerializer.Deserialize<VoiceUserSettings>(legacyPascalJson, VoiceUserSettings.JsonOptions);
        Assert.NotNull(fromPascal);
        Assert.Equal(115.0, fromPascal.MasterVolume);
        Assert.Equal(40.0, fromPascal.VoiceSensitivity);
        Assert.Equal(90.0, fromPascal.ParticipantVolumes["u1"]);
        Assert.True(fromPascal.AutoGainControl);
        Assert.True(fromPascal.IncomingVoiceNormalization);

        const string camelJson = """{"masterVolume":85,"autoGainControl":false,"incomingVoiceNormalization":false}""";
        var fromCamel = JsonSerializer.Deserialize<VoiceUserSettings>(camelJson, VoiceUserSettings.JsonOptions);
        Assert.NotNull(fromCamel);
        Assert.Equal(85.0, fromCamel.MasterVolume);
        Assert.False(fromCamel.AutoGainControl);
        Assert.False(fromCamel.IncomingVoiceNormalization);
    }
}
