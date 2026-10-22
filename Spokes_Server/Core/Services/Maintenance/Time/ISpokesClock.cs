using System;
using System.Threading;
using System.Threading.Tasks;

namespace Spokes_Server.Core.Services.Maintenance.Time;

/// <summary>
/// Authoritative clock provider for Spokes, adjusting for NTP clock drift and configured timezones.
/// </summary>
public interface ISpokesClock
{
    /// <summary>
    /// Current UTC time, adjusted by measured NTP clock offset if NTP time source is active.
    /// </summary>
    DateTime UtcNow { get; }

    /// <summary>
    /// Current time evaluated in the configured company timezone.
    /// </summary>
    DateTime Now { get; }

    /// <summary>
    /// Measured clock drift offset between the host system clock and the authoritative time source.
    /// </summary>
    TimeSpan ClockOffset { get; }

    /// <summary>
    /// Performs an immediate synchronization with the configured or specified NTP server.
    /// </summary>
    Task<NtpSyncResult> SyncWithNtpAsync(string? ntpServer = null, CancellationToken ct = default);
}
