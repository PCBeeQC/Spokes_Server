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

        return $@"
port: {voicePort}
rtc:
  tcp_port: {fallbackPort}
  port_range_start: {udpStartPort}
  port_range_end: {udpEndPort}
  use_external_ip: true
keys:
  {systemConfig.LiveKitApiKey}: {systemConfig.LiveKitApiSecret}
{loggingYaml}
audio:
  active_level: 36
  min_percentile: 20
  update_interval: 100
  smooth_intervals: 2
";
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
    /// Queries the LiveKit daemon for the active participant identities in a channel's room.
    /// Returns an empty list if the room does not exist, or <c>null</c> if a transient error prevented querying LiveKit.
    /// </summary>
    public virtual async Task<IReadOnlyList<string>?> GetRoomParticipantIdentitiesAsync(
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
            return listResponse.Participants.Select(p => p.Identity).ToList();
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
            await roomServiceClient.RemoveParticipant(new RoomParticipantIdentity
            {
                Room = roomName,
                Identity = userId
            });
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
    /// Updates track subscriptions at the LiveKit SFU level for a participant in a voice channel.
    /// Used by server-side media pipelines (such as Physical Rooms) to unsubscribe human participants
    /// from raw microphone tracks while cleaned replacement tracks ("processed-mic:{userId}") are active.
    /// </summary>
    public virtual async Task UpdateParticipantSubscriptionsAsync(
        string channelId,
        string participantIdentity,
        IEnumerable<string> trackSids,
        bool subscribe,
        string apiKey,
        string apiSecret,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(channelId) || string.IsNullOrWhiteSpace(participantIdentity))
        {
            return;
        }

        try
        {
            var roomServiceClient = CreateRoomServiceClient(apiKey, apiSecret);
            var req = new UpdateSubscriptionsRequest
            {
                Room = GetRoomName(channelId),
                Identity = participantIdentity,
                Subscribe = subscribe
            };
            req.TrackSids.AddRange(trackSids);
            if (req.TrackSids.Count > 0)
            {
                await roomServiceClient.UpdateSubscriptions(req);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[LiveKit] Failed to update subscriptions for {Participant} in channel-{ChannelId}: {Message}", participantIdentity, channelId, ex.Message);
        }
    }
}
