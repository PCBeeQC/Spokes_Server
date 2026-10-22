using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Spokes_Server.Core.Services.Communication.Presence;

/// <summary>
/// Background watchdog that sweeps active connections every 15 seconds and transitions
/// connections that have exceeded the 5-minute inactivity threshold to Idle, firing
/// OnUserPresenceChanged so all connected clients update presence badges in real time.
/// </summary>
public class PresenceWatchdogHostedService : BackgroundService
{
    private readonly PresenceStateService _presenceState;
    private readonly ILogger<PresenceWatchdogHostedService>? _logger;
    private static readonly TimeSpan SweepInterval = TimeSpan.FromSeconds(15);

    public PresenceWatchdogHostedService(
        PresenceStateService presenceState,
        ILogger<PresenceWatchdogHostedService>? logger = null)
    {
        _presenceState = presenceState;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                _presenceState.SweepStaleConnections();
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Error while sweeping stale presence connections.");
            }

            try
            {
                await Task.Delay(SweepInterval, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }
}
