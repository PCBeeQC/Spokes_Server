using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Spokes_Server.Core.Services.Communication.Voice;

public sealed class VoicePipParticipantDto
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("avatarUrl")]
    public string AvatarUrl { get; set; } = string.Empty;

    [JsonPropertyName("initials")]
    public string Initials { get; set; } = string.Empty;

    [JsonPropertyName("profileColor")]
    public string ProfileColor { get; set; } = "#673ab7";

    [JsonPropertyName("isMuted")]
    public bool IsMuted { get; set; }
}

public sealed record VoicePipPresentationState(
    int ParticipantCount,
    string? FocusedParticipantId,
    string? StickySpeakerId,
    string? DisplayParticipantId,
    bool CanUseGrid,
    bool IsGridMode,
    bool HasPrimaryVideo,
    string PrimaryTargetId,
    string PrimaryVideoSource,
    bool UseFaceTimeSelfCorner,
    bool HasFilmstrip,
    string ModeClass);

public sealed record VoiceStagePresentationState(
    int VisualParticipantCount,
    bool IsFaceTimeLayout,
    string? FocusedParticipantId,
    int ActiveVideoCount);

public sealed class VoicePipMetadataPayload
{
    [JsonPropertyName("active")]
    public bool Active { get; set; }

    [JsonPropertyName("channelId")]
    public string ChannelId { get; set; } = string.Empty;

    [JsonPropertyName("channelName")]
    public string ChannelName { get; set; } = string.Empty;

    [JsonPropertyName("serverName")]
    public string ServerName { get; set; } = string.Empty;

    [JsonPropertyName("localUserId")]
    public string LocalUserId { get; set; } = string.Empty;

    [JsonPropertyName("isNativePlatform")]
    public bool IsNativePlatform { get; set; }

    [JsonPropertyName("isMuted")]
    public bool IsMuted { get; set; }

    [JsonPropertyName("isVideoOn")]
    public bool IsVideoOn { get; set; }

    [JsonPropertyName("isScreenSharing")]
    public bool IsScreenSharing { get; set; }

    [JsonPropertyName("focusedParticipantId")]
    public string FocusedParticipantId { get; set; } = string.Empty;

    [JsonPropertyName("layoutMode")]
    public string LayoutMode { get; set; } = "focus";

    [JsonPropertyName("modeClass")]
    public string ModeClass { get; set; } = "mode-avatar";

    [JsonPropertyName("displaySpeakerId")]
    public string DisplaySpeakerId { get; set; } = string.Empty;

    [JsonPropertyName("primaryVideoParticipantId")]
    public string PrimaryVideoParticipantId { get; set; } = string.Empty;

    [JsonPropertyName("primaryVideoSource")]
    public string PrimaryVideoSource { get; set; } = string.Empty;

    [JsonPropertyName("participants")]
    public List<VoicePipParticipantDto> Participants { get; set; } = [];
}
