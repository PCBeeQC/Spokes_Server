using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Spokes_Server.Core.Data.Repositories.Core;
using Spokes_Server.Core.Services.Communication.Voice;

namespace Spokes_Server.Core.Services.Migrations;

/// <summary>
/// One-time migration that transitions existing self-hosted deployments from LiveKit's legacy
/// dynamic UDP port range (e.g. 500 ports such as 30000-30499 or 50000-50499) to LiveKit's
/// multi-port UDP Mux (default 8 ports, e.g. 30000-30007) while preserving any custom start port.
/// Because the narrowed range sits strictly inside the server's existing Docker/router port forward,
/// existing deployments upgrade seamlessly with zero manual reconfiguration.
/// </summary>
public class LiveKitUdpMuxMigrationStep : IMigrationStep
{
    private readonly SystemConfigRepository _systemConfigs;
    private readonly ILogger<LiveKitUdpMuxMigrationStep>? _logger;

    public string MigrationKey => "20261125_LiveKitUdpMuxPortRange";
    public string Description => "Migrates wide legacy LiveKit UDP port ranges to 8-port UDP Mux while preserving custom start ports";
    public int Order => 120;

    public LiveKitUdpMuxMigrationStep(
        SystemConfigRepository systemConfigs,
        ILogger<LiveKitUdpMuxMigrationStep>? logger = null)
    {
        _systemConfigs = systemConfigs ?? throw new ArgumentNullException(nameof(systemConfigs));
        _logger = logger;
    }

    public Task ExecuteAsync(CancellationToken cancellationToken = default)
    {
        var config = _systemConfigs.Get();
        int oldStart = config.LiveKitUdpStartPort;
        int oldEnd = config.LiveKitUdpEndPort;

        int normalizedStart = oldStart is >= LiveKitService.MinValidPort and <= LiveKitService.MaxValidPort
            ? oldStart
            : LiveKitService.DefaultUdpStartPort;

        int portCount = oldEnd - normalizedStart + 1;
        int newEnd = oldEnd;

        if (portCount <= 0)
        {
            newEnd = Math.Min(LiveKitService.MaxValidPort, normalizedStart + LiveKitService.DefaultUdpMuxPortCount - 1);
        }
        else if (portCount > LiveKitService.DefaultUdpMuxPortCount)
        {
            newEnd = Math.Min(LiveKitService.MaxValidPort, normalizedStart + LiveKitService.DefaultUdpMuxPortCount - 1);
        }
        else if (newEnd > LiveKitService.MaxValidPort)
        {
            newEnd = LiveKitService.MaxValidPort;
        }

        if (normalizedStart != oldStart || newEnd != oldEnd)
        {
            config.LiveKitUdpStartPort = normalizedStart;
            config.LiveKitUdpEndPort = newEnd;
            _systemConfigs.Save(config);

            _logger?.LogInformation(
                "[Migration] Migrated LiveKit UDP port range from {OldStart}-{OldEnd} to UDP Mux {NewStart}-{NewEnd}.",
                oldStart,
                oldEnd,
                normalizedStart,
                newEnd);
        }

        return Task.CompletedTask;
    }
}
