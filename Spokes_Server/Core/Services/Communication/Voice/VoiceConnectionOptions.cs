using Spokes_Server.Core.Models.HR;

namespace Spokes_Server.Core.Services.Communication.Voice;

/// <summary>
/// Encapsulates connection parameters for joining a voice channel.
/// </summary>
public class VoiceConnectionOptions
{
    public string Token { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
    public string ChannelId { get; set; } = string.Empty;
    public string UserId { get; set; } = string.Empty;
    public int AudioBitrateKbps { get; set; } = 64;
    public string NoiseSuppression { get; set; } = "rnnoise";
    public VoiceUserSettings? VoiceSettings { get; set; }

    public VoiceConnectionOptions() { }

    public VoiceConnectionOptions(
        string token,
        string url,
        string channelId,
        string userId,
        int audioBitrateKbps = 64,
        string noiseSuppression = "rnnoise",
        VoiceUserSettings? voiceSettings = null)
    {
        Token = token;
        Url = url;
        ChannelId = channelId;
        UserId = userId;
        AudioBitrateKbps = audioBitrateKbps;
        NoiseSuppression = noiseSuppression;
        VoiceSettings = voiceSettings;
    }
}
