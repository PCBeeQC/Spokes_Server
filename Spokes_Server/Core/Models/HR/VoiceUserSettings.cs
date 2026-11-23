namespace Spokes_Server.Core.Models.HR;

using System.Collections.Generic;
using System.Text.Json;

public class VoiceUserSettings
{
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public double MasterVolume { get; set; } = 100.0; // 0 to 200
    public Dictionary<string, double> ParticipantVolumes { get; set; } = []; // ParticipantId -> Volume (0 to 200)
    public double VoiceSensitivity { get; set; } = 50.0; // 0 (less sensitive, higher gate) to 100 (more sensitive, lower gate)
    public bool AutoGainControl { get; set; } = true; // Automatic microphone loudness adjustment (sender side)
    public bool IncomingVoiceNormalization { get; set; } = true; // Automatic incoming speaker volume normalization (receiver side)
}
