using System.Text.Json;
using Microsoft.Extensions.Localization;
using Spokes_Server.Core.Models.HR;
using Spokes_Server.Resources;

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
    public string DeviceId { get; set; } = string.Empty;
    public string DeviceName { get; set; } = string.Empty;
    public string VoiceSessionId { get; set; } = string.Empty;
    public string PreferredMicDeviceId { get; set; } = string.Empty;
    public bool InitialMuted { get; set; }

    public VoiceConnectionOptions() { }

    public VoiceConnectionOptions(
        string token,
        string url,
        string channelId,
        string userId,
        int audioBitrateKbps = 64,
        string noiseSuppression = "rnnoise",
        VoiceUserSettings? voiceSettings = null,
        string deviceId = "",
        string deviceName = "",
        string voiceSessionId = "",
        string preferredMicDeviceId = "",
        bool initialMuted = false)
    {
        Token = token;
        Url = url;
        ChannelId = channelId;
        UserId = userId;
        AudioBitrateKbps = audioBitrateKbps;
        NoiseSuppression = noiseSuppression;
        VoiceSettings = voiceSettings;
        DeviceId = deviceId;
        DeviceName = deviceName;
        VoiceSessionId = voiceSessionId;
        PreferredMicDeviceId = preferredMicDeviceId;
        InitialMuted = initialMuted;
    }
}

public sealed class VoiceParticipantMetadata
{
    public string AvatarUrl { get; set; } = string.Empty;
    public string VoiceSessionId { get; set; } = string.Empty;
    public string DeviceId { get; set; } = string.Empty;
    public string DeviceName { get; set; } = string.Empty;
    public string Platform { get; set; } = string.Empty;

    public string ToJson() => JsonSerializer.Serialize(this);

    public static VoiceParticipantMetadata Parse(string? rawMetadata)
    {
        if (string.IsNullOrWhiteSpace(rawMetadata))
        {
            return new VoiceParticipantMetadata();
        }

        var trimmed = rawMetadata.Trim();
        if (trimmed.StartsWith('{'))
        {
            try
            {
                return JsonSerializer.Deserialize<VoiceParticipantMetadata>(trimmed)
                    ?? new VoiceParticipantMetadata();
            }
            catch (JsonException)
            {
                // Fall back to legacy raw avatar URL string.
            }
        }

        return new VoiceParticipantMetadata { AvatarUrl = trimmed };
    }
}

public sealed record VoicePreJoinContext(
    string? SettingsJson,
    IReadOnlyList<string> CoLocatedUserIds,
    IReadOnlyList<string> MutedRoommateUserIds,
    string PreferredMicDeviceId,
    bool InitialMuted);

public sealed class VoiceParticipantSnapshotDto
{
    public string UserId { get; set; } = string.Empty;
    public bool IsMuted { get; set; }
    /// <summary>
    /// 3-state LiveKit microphone publication state:
    /// <c>true</c> = all published mic tracks are muted;
    /// <c>false</c> = at least one active unmuted mic track is published;
    /// <c>null</c> = 0 mic publications (defer to authoritative server <c>VoiceStateService.IsUserMuted</c>).
    /// </summary>
    public bool? IsMicMuted { get; set; }
    public string Metadata { get; set; } = string.Empty;
    public bool? IsCameraOn { get; set; }
    public bool? IsScreenSharing { get; set; }
    public bool? HasScreenAudio { get; set; }
    public int VideoHeight { get; set; }
    public int VideoFps { get; set; }
}

public sealed class VoiceRoomSnapshotDto
{
    public string ChannelId { get; set; } = string.Empty;
    public string LocalUserId { get; set; } = string.Empty;
    public bool? LocalIsMuted { get; set; }
    public bool? LocalIsVideoOn { get; set; }
    public bool? LocalIsScreenSharing { get; set; }
    public List<VoiceParticipantSnapshotDto> Participants { get; set; } = [];
}

public class PhysicalDeviceIdentity
{
    public string DeviceId { get; set; } = string.Empty;
    public string DeviceName { get; set; } = string.Empty;
}

public enum VoiceConnectingStage
{
    None = 0,
    PreparingAudio = 1,
    ConnectingToServer = 2,
    StartingMicrophone = 3,
    ConfiguringRoomAudio = 4
}

public sealed class VoiceClientBootstrapInfo
{
    public bool IsIOS { get; set; }
    public bool IsAndroid { get; set; }
    public bool IsNativePlatform { get; set; }
    public bool IsDesktopClient { get; set; }
    public bool IsDesktopDirectRoomSupported { get; set; }
    public string DeviceId { get; set; } = string.Empty;
    public string DeviceName { get; set; } = string.Empty;
    public string SavedNoiseFilter { get; set; } = string.Empty;
}

public static class VoiceConnectingStageExtensions
{
    public static string GetLocalizedStageText(this VoiceConnectingStage stage, IStringLocalizer<AppResources> Loc) => stage switch
    {
        VoiceConnectingStage.PreparingAudio => Loc["Preparing audio devices..."].Value,
        VoiceConnectingStage.ConnectingToServer => Loc["Connecting to voice server..."].Value,
        VoiceConnectingStage.StartingMicrophone => Loc["Starting microphone & noise filter..."].Value,
        VoiceConnectingStage.ConfiguringRoomAudio => Loc["Configuring room audio..."].Value,
        _ => Loc["Preparing audio devices..."].Value
    };
}
