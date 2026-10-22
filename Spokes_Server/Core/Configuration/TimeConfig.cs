namespace Spokes_Server.Core.Configuration;

/// <summary>
/// Configuration for server time synchronization and NTP clock offset.
/// </summary>
public class TimeConfig
{
    public const string SourceHost = "Host";
    public const string SourceNtp = "Ntp";

    /// <summary>
    /// Clock time source: "Host" (local container/server clock) or "Ntp" (public NTP server).
    /// Defaults to "Host".
    /// </summary>
    public string TimeSource { get; set; } = SourceHost;

    /// <summary>
    /// Public NTP server hostname or IP address.
    /// Default is "time.cloudflare.com".
    /// </summary>
    public string NtpServer { get; set; } = "time.cloudflare.com";

    /// <summary>
    /// Last measured clock drift offset in seconds (NtpTimeUtc - HostUtcNow).
    /// </summary>
    public double LastDriftOffsetSeconds { get; set; } = 0.0;

    /// <summary>
    /// Timestamp of last successful NTP synchronization (in UTC).
    /// </summary>
    public DateTime? LastNtpSyncUtc { get; set; }

    /// <summary>
    /// Any error message from the most recent NTP synchronization attempt.
    /// </summary>
    public string? LastSyncError { get; set; }
}
