using System;
using System.Diagnostics;
using System.IO;
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

    /// <summary>
    /// Generates livekit.yaml and restarts the LiveKit daemon via supervisorctl.
    /// Used both on startup and when voice ports are modified in settings.
    /// </summary>
    public void ApplyPortConfiguration(SystemConfig systemConfig)
    {
        try
        {
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
            var logLevel = !string.IsNullOrWhiteSpace(envLogLevel) ? envLogLevel : "info";

            var livekitYaml = $@"
port: {voicePort}
rtc:
  tcp_port: {fallbackPort}
  port_range_start: {udpStartPort}
  port_range_end: {udpEndPort}
  use_external_ip: true
keys:
  {systemConfig.LiveKitApiKey}: {systemConfig.LiveKitApiSecret}
logging:
  level: {logLevel}
audio:
  active_level: 36
  min_percentile: 20
  update_interval: 100
  smooth_intervals: 2
";

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

    /// <summary>
    /// Evicts a participant from a LiveKit room to prevent WebRTC lingering after leaving a voice channel.
    /// </summary>
    public virtual async Task RemoveParticipantAsync(string channelId, string userId, string apiKey, string apiSecret, CancellationToken cancellationToken = default)
    {
        try
        {
            var voicePort = GetSetting("Spokes_Voice_Port") ?? "7880";
            var liveKitUrl = $"http://127.0.0.1:{voicePort}";
            var roomServiceClient = new Livekit.Server.Sdk.Dotnet.RoomServiceClient(liveKitUrl, apiKey, apiSecret);
            var roomName = $"channel-{channelId}";
            await roomServiceClient.RemoveParticipant(new Livekit.Server.Sdk.Dotnet.RoomParticipantIdentity
            {
                Room = roomName,
                Identity = userId
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[LiveKit] Failed to evict user {UserId} from LiveKit room channel-{ChannelId}: {Message}", userId, channelId, ex.Message);
        }
    }
}
