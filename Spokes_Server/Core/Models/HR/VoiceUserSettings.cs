namespace Spokes_Server.Core.Models.HR;

using System.Collections.Generic;

public class VoiceUserSettings
{
    public double MasterVolume { get; set; } = 100.0; // 0 to 200
    public Dictionary<string, double> ParticipantVolumes { get; set; } = []; // ParticipantId -> Volume (0 to 200)
    public double VoiceSensitivity { get; set; } = 50.0; // 0 (less sensitive, higher gate) to 100 (more sensitive, lower gate)
}
