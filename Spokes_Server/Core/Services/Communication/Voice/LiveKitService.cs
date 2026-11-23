using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Livekit.Server.Sdk.Dotnet;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Spokes_Server.Core.Models.Core;

namespace Spokes_Server.Core.Services.Communication.Voice;

public sealed record LiveKitParticipantSnapshot(
    string Identity,
    string Sid,
    string Metadata,
    bool? IsMicMuted,
    bool IsConnected,
    bool IsCameraOn = false,
    bool IsScreenSharing = false,
    bool HasScreenAudio = false);

public class LiveKitService
{
    private readonly ILogger<LiveKitService> _logger;
    private readonly IConfiguration? _configuration;

    public LiveKitService(ILogger<LiveKitService> logger, IConfiguration? configuration = null)
    {
        _logger = logger;
        _configuration = configuration;
    }

    private string? GetSetting(string key) =>
        _configuration?[key] ?? Environment.GetEnvironmentVariable(key);

    public static string NormalizeLogLevel(string? level)
    {
        if (string.IsNullOrWhiteSpace(level))
        {
            return "info";
        }

        return level.Trim().ToLowerInvariant() switch
        {
            "error" => "error",
            "warn" or "warning" => "warn",
            "debug" => "debug",
            _ => "info"
        };
    }

    /// <summary>
    /// Builds the livekit.yaml configuration payload with structured component-level logging
    /// and log sampling so routine SDP payloads, WebSocket pings, and Twirp polling do not flood stdout.
    /// </summary>
    public virtual string BuildConfigurationYaml(SystemConfig systemConfig)
    {
        ArgumentNullException.ThrowIfNull(systemConfig);

        var voicePort = GetSetting("Spokes_Voice_Port") ?? "7880";

        var envFallback = GetSetting("Spokes_Voice_Fallback_Port");
        var envUdpStart = GetSetting("Spokes_Voice_UDP_Start_Port");
        var envUdpCount = GetSetting("Spokes_VOICE_UDP_PORT_COUNT");

        var fallbackPort = !systemConfig.IsSetupComplete && !string.IsNullOrWhiteSpace(envFallback) ? int.Parse(envFallback) : systemConfig.LiveKitFallbackPort;
        var udpStartPort = !systemConfig.IsSetupComplete && !string.IsNullOrWhiteSpace(envUdpStart) ? int.Parse(envUdpStart) : systemConfig.LiveKitUdpStartPort;
        var udpEndPort = systemConfig.LiveKitUdpEndPort;

        if (!systemConfig.IsSetupComplete && !string.IsNullOrWhiteSpace(envUdpCount))
        {
            udpEndPort = udpStartPort + int.Parse(envUdpCount) - 1;
        }

        var envLogLevel = GetSetting("Spokes_LiveKit_Log_Level");
        var rawLogLevel = !systemConfig.IsSetupComplete && !string.IsNullOrWhiteSpace(envLogLevel)
            ? envLogLevel
            : systemConfig.LiveKitLogLevel;
        var logLevel = NormalizeLogLevel(rawLogLevel);

        var loggingYaml = logLevel switch
        {
            "error" => @"logging:
  level: error
  pion_level: error
  sample: true
  component_levels:
    api: error",
            "warn" => @"logging:
  level: warn
  pion_level: error
  sample: true
  component_levels:
    api: error",
            "debug" => @"logging:
  level: info
  pion_level: error
  sample: true
  component_levels:
    room: debug
    pub: debug
    sub: debug
    transport: debug
    sfu: info
    api: warn",
            _ => @"logging:
  level: info
  pion_level: error
  sample: true
  component_levels:
    api: warn"
        };

        var webhookYaml = string.Empty;
        if (!string.IsNullOrWhiteSpace(systemConfig.LiveKitApiKey))
        {
            var webhookUrl = ResolveInternalWebhookUrl();
            webhookYaml = $@"webhook:
  api_key: {systemConfig.LiveKitApiKey}
  urls:
    - {webhookUrl}
";
        }

        return $@"
port: {voicePort}
rtc:
  tcp_port: {fallbackPort}
  port_range_start: {udpStartPort}
  port_range_end: {udpEndPort}
  use_external_ip: true
keys:
  {systemConfig.LiveKitApiKey}: {systemConfig.LiveKitApiSecret}
{webhookYaml}{loggingYaml}";
    }

    internal string ResolveInternalWebhookUrl()
    {
        var explicitUrl = GetSetting("Spokes_LiveKit_Webhook_Url");
        if (!string.IsNullOrWhiteSpace(explicitUrl))
        {
            return explicitUrl.Trim();
        }

        var port = GetSetting("Spokes_HOST_PORT") ?? GetSetting("Spokes_Host_Port");
        if (!int.TryParse(port, out var parsedPort) || parsedPort <= 0)
        {
            parsedPort = TryParsePortFromUrls(GetSetting("ASPNETCORE_URLS")) ?? 8080;
        }

        return $"http://127.0.0.1:{parsedPort}/spokesapi/voice/livekit-webhook";
    }

    private static int? TryParsePortFromUrls(string? urls)
    {
        if (string.IsNullOrWhiteSpace(urls))
        {
            return null;
        }

        foreach (var raw in urls.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var normalized = raw.Replace("://+", "://127.0.0.1").Replace("://*", "://127.0.0.1");
            if (Uri.TryCreate(normalized, UriKind.Absolute, out var uri) && uri.Port > 0)
            {
                return uri.Port;
            }
        }

        return null;
    }

    /// <summary>
    /// Generates livekit.yaml and restarts the LiveKit daemon via supervisorctl.
    /// Used both on startup and when voice ports or LiveKit log levels are modified in settings.
    /// </summary>
    public void ApplyPortConfiguration(SystemConfig systemConfig)
    {
        try
        {
            var livekitYaml = BuildConfigurationYaml(systemConfig);

            File.WriteAllText("/app/livekit.yaml", livekitYaml);

            var stopProc = Process.Start(new ProcessStartInfo
            {
                FileName = "supervisorctl",
                Arguments = "stop livekit",
                UseShellExecute = false,
                CreateNoWindow = true
            });
            stopProc?.WaitForExit();

            Process.Start(new ProcessStartInfo
            {
                FileName = "supervisorctl",
                Arguments = "start livekit",
                UseShellExecute = false,
                CreateNoWindow = true
            });

            _logger.LogInformation("[LiveKit] livekit.yaml generated and LiveKit daemon reloaded via supervisorctl.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[LiveKit] Failed to generate configuration or reload LiveKit daemon.");
        }
    }

    public static string GetRoomName(string channelId) => $"channel-{channelId}";

    public static bool TryExtractChannelIdFromRoomName(string? roomName, out string channelId)
    {
        const string prefix = "channel-";
        if (!string.IsNullOrWhiteSpace(roomName) &&
            roomName.StartsWith(prefix, StringComparison.Ordinal) &&
            roomName.Length > prefix.Length)
        {
            channelId = roomName[prefix.Length..];
            return true;
        }

        channelId = string.Empty;
        return false;
    }

    /// <summary>
    /// Identifies LiveKit service identities (bots) that are not human caller slots.
    /// </summary>
    public static bool IsSyntheticParticipantIdentity(string? identity) =>
        !string.IsNullOrEmpty(identity) &&
        identity.StartsWith("bot:", StringComparison.OrdinalIgnoreCase);

    private RoomServiceClient CreateRoomServiceClient(string apiKey, string apiSecret)
    {
        var voicePort = GetSetting("Spokes_Voice_Port") ?? "7880";
        var liveKitUrl = $"http://127.0.0.1:{voicePort}";
        return new RoomServiceClient(liveKitUrl, apiKey, apiSecret);
    }

    /// <summary>
    /// Determines whether an exception thrown by the LiveKit Twirp SDK indicates that the target
    /// participant or room is already absent from the LiveKit server.
    /// </summary>
    public static bool IsParticipantOrRoomAbsentError(Exception? ex)
    {
        for (var current = ex; current != null; current = current.InnerException)
        {
            var message = current.Message;
            if (string.IsNullOrWhiteSpace(message))
            {
                continue;
            }

            if (message.Contains("participant does not exist", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("requested participant not found", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("room does not exist", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("room not found", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("could not find room", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Converts a protobuf <see cref="ParticipantInfo"/> from LiveKit Server SDK or webhooks
    /// into a normalized <see cref="LiveKitParticipantSnapshot"/>.
    /// </summary>
    public static LiveKitParticipantSnapshot ToParticipantSnapshot(ParticipantInfo participant)
    {
        ArgumentNullException.ThrowIfNull(participant);

        var tracks = participant.Tracks?.ToList() ?? [];
        var micTracks = tracks
            .Where(t => t.Type == TrackType.Audio && (t.Source == TrackSource.Microphone || t.Source == TrackSource.Unknown))
            .ToList();

        bool? isMicMuted = micTracks.Count == 0
            ? null
            : micTracks.All(t => t.Muted);

        bool isCameraOn = tracks.Any(t =>
            t.Type == TrackType.Video &&
            (t.Source == TrackSource.Camera || t.Source == TrackSource.Unknown) &&
            !t.Muted);

        bool isScreenSharing = tracks.Any(t =>
            t.Type == TrackType.Video &&
            t.Source == TrackSource.ScreenShare &&
            !t.Muted);

        bool hasScreenAudio = tracks.Any(t =>
            t.Type == TrackType.Audio &&
            t.Source == TrackSource.ScreenShareAudio &&
            !t.Muted);

        bool isConnected = participant.State != ParticipantInfo.Types.State.Disconnected;
        return new LiveKitParticipantSnapshot(
            participant.Identity ?? string.Empty,
            participant.Sid ?? string.Empty,
            participant.Metadata ?? string.Empty,
            isMicMuted,
            isConnected,
            isCameraOn,
            isScreenSharing,
            hasScreenAudio);
    }

    /// <summary>
    /// Queries the LiveKit daemon for all active rooms with prefix <c>channel-</c> that currently have participants.
    /// Returns an empty list if no rooms are active or if a transient error prevented querying LiveKit.
    /// </summary>
    public virtual async Task<IReadOnlyList<string>> GetActiveVoiceChannelIdsAsync(
        string apiKey,
        string apiSecret,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(apiKey) || string.IsNullOrWhiteSpace(apiSecret))
        {
            return [];
        }

        try
        {
            var roomServiceClient = CreateRoomServiceClient(apiKey, apiSecret);
            var listResponse = await roomServiceClient.ListRooms(new ListRoomsRequest());
            var channelIds = new List<string>();
            if (listResponse?.Rooms != null)
            {
                foreach (var room in listResponse.Rooms)
                {
                    if (room != null &&
                        room.NumParticipants > 0 &&
                        TryExtractChannelIdFromRoomName(room.Name, out var channelId))
                    {
                        channelIds.Add(channelId);
                    }
                }
            }
            return channelIds;
        }
        catch (Exception ex)
        {
            _logger.LogDebug("[LiveKit] Transient error listing active rooms: {Message}", ex.Message);
            return [];
        }
    }

    /// <summary>
    /// Queries the LiveKit daemon for full participant state snapshots (identity, metadata, mic mute state) in a channel's room.
    /// Returns an empty list if the room does not exist, or <c>null</c> if a transient error prevented querying LiveKit.
    /// </summary>
    public virtual async Task<IReadOnlyList<LiveKitParticipantSnapshot>?> GetRoomParticipantStatesAsync(
        string channelId,
        string apiKey,
        string apiSecret,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(channelId) || string.IsNullOrWhiteSpace(apiKey) || string.IsNullOrWhiteSpace(apiSecret))
        {
            return [];
        }

        var roomName = GetRoomName(channelId);
        try
        {
            var roomServiceClient = CreateRoomServiceClient(apiKey, apiSecret);
            var listResponse = await roomServiceClient.ListParticipants(new ListParticipantsRequest { Room = roomName });
            return listResponse.Participants
                .Select(ToParticipantSnapshot)
                .Where(p => p.IsConnected && !string.IsNullOrWhiteSpace(p.Identity))
                .ToList();
        }
        catch (Exception ex) when (IsParticipantOrRoomAbsentError(ex))
        {
            return [];
        }
        catch (Exception ex)
        {
            _logger.LogWarning("[LiveKit] Transient error listing participants for room {RoomName}: {Message}", roomName, ex.Message);
            return null;
        }
    }

    /// <summary>
    /// Evicts a participant from a LiveKit room to prevent WebRTC lingering after leaving a voice channel.
    /// Treats "participant/room does not exist" as an idempotent success since the client has already disconnected.
    /// </summary>
    public virtual async Task RemoveParticipantAsync(string channelId, string userId, string apiKey, string apiSecret, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(channelId) || string.IsNullOrWhiteSpace(userId))
        {
            return;
        }

        try
        {
            var roomServiceClient = CreateRoomServiceClient(apiKey, apiSecret);
            var roomName = GetRoomName(channelId);
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(5));
            await roomServiceClient.RemoveParticipant(new RoomParticipantIdentity
            {
                Room = roomName,
                Identity = userId
            }).WaitAsync(timeoutCts.Token);
        }
        catch (Exception ex) when (IsParticipantOrRoomAbsentError(ex))
        {
            _logger.LogDebug("[LiveKit] Participant {UserId} is already absent from room channel-{ChannelId}; eviction is a no-op.", userId, channelId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[LiveKit] Failed to evict user {UserId} from LiveKit room channel-{ChannelId}: {Message}", userId, channelId, ex.Message);
        }
    }

    /// <summary>
    /// Resolves the browser/client WebSocket URL for LiveKit signaling from configuration,
    /// persisted system settings, or the current navigation base URI.
    /// </summary>
    public static string ResolveClientWebSocketUrl(
        IConfiguration? configuration,
        SystemConfig? systemConfig,
        string? fallbackBaseUri)
    {
        var configuredUrl = configuration?["LiveKit:ServerUrl"];
        if (!string.IsNullOrWhiteSpace(configuredUrl))
        {
            return configuredUrl.Trim();
        }

        var baseUriString = !string.IsNullOrWhiteSpace(systemConfig?.ServerPublicUrl)
            ? systemConfig.ServerPublicUrl
            : (fallbackBaseUri ?? "http://localhost/");
        if (!Uri.TryCreate(baseUriString, UriKind.Absolute, out var baseUri))
        {
            return "ws://localhost";
        }

        var wsScheme = string.Equals(baseUri.Scheme, "https", StringComparison.OrdinalIgnoreCase) ? "wss" : "ws";
        return $"{wsScheme}://{baseUri.Authority}";
    }
}
