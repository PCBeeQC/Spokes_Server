namespace Spokes_Server.Core.Services.Communication.Voice;

using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Spokes_Server.Core.Models.Core;

public class LiveKitService
{
    private readonly ILogger<LiveKitService> _logger;

    public LiveKitService(ILogger<LiveKitService> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Generates livekit.yaml and restarts the LiveKit daemon via supervisorctl.
    /// Used both on startup and when voice ports are modified in settings.
    /// </summary>
    public void ApplyPortConfiguration(SystemConfig systemConfig)
    {
        try
        {
            var voicePort = Environment.GetEnvironmentVariable("Spokes_Voice_Port") ?? "7880";

            var envFallback = Environment.GetEnvironmentVariable("Spokes_Voice_Fallback_Port");
            var envUdpStart = Environment.GetEnvironmentVariable("Spokes_Voice_UDP_Start_Port");
            var envUdpCount = Environment.GetEnvironmentVariable("Spokes_VOICE_UDP_PORT_COUNT");

            var fallbackPort = !systemConfig.IsSetupComplete && !string.IsNullOrWhiteSpace(envFallback) ? int.Parse(envFallback) : systemConfig.LiveKitFallbackPort;
            var udpStartPort = !systemConfig.IsSetupComplete && !string.IsNullOrWhiteSpace(envUdpStart) ? int.Parse(envUdpStart) : systemConfig.LiveKitUdpStartPort;
            var udpEndPort = systemConfig.LiveKitUdpEndPort;

            if (!systemConfig.IsSetupComplete && !string.IsNullOrWhiteSpace(envUdpCount))
            {
                udpEndPort = udpStartPort + int.Parse(envUdpCount) - 1;
            }

            var envLogLevel = Environment.GetEnvironmentVariable("Spokes_LiveKit_Log_Level");
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
}
