using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Spokes_Server.Core.Configuration;
using Spokes_Server.Core.Data.Repositories.Core;

namespace Spokes_Server.Core.Services.Maintenance.Time;

/// <summary>
/// Authoritative clock provider for Spokes. Implements TimeProvider and ISpokesClock,
/// providing host-clock or NTP-compensated time and timezone resolution.
/// </summary>
public class SpokesClockService : TimeProvider, ISpokesClock, IHostedService, IDisposable
{
    private readonly CompanyProfileRepository _companyProfiles;
    private readonly ILogger<SpokesClockService> _logger;
    private readonly NtpClient _ntpClient;

    private TimeSpan _clockOffset = TimeSpan.Zero;
    private CancellationTokenSource? _cts;
    private Task? _backgroundTask;

    public static ISpokesClock Instance { get; internal set; } = new DefaultSystemClock();

    public SpokesClockService(
        CompanyProfileRepository companyProfiles,
        ILogger<SpokesClockService> logger,
        NtpClient? ntpClient = null)
    {
        _companyProfiles = companyProfiles;
        _logger = logger;
        _ntpClient = ntpClient ?? new NtpClient();
        Instance = this;

        // Restore saved drift offset from database if NTP was active
        try
        {
            var profile = _companyProfiles.Get();
            if (profile?.Time != null && profile.Time.TimeSource == TimeConfig.SourceNtp)
            {
                _clockOffset = TimeSpan.FromSeconds(profile.Time.LastDriftOffsetSeconds);
            }
        }
        catch
        {
            // Ignore during startup before db ready
        }
    }

    public TimeSpan ClockOffset
    {
        get
        {
            var profile = _companyProfiles.Get();
            if (profile?.Time?.TimeSource == TimeConfig.SourceNtp)
            {
                return _clockOffset;
            }
            return TimeSpan.Zero;
        }
    }

    public DateTime UtcNow => DateTime.UtcNow + ClockOffset;

    public DateTime Now
    {
        get
        {
            var tz = GetCompanyTimeZoneInfo();
            return TimeZoneInfo.ConvertTimeFromUtc(UtcNow, tz);
        }
    }

    public override DateTimeOffset GetUtcNow() => new(UtcNow, TimeSpan.Zero);

    public override TimeZoneInfo LocalTimeZone => GetCompanyTimeZoneInfo();

    public TimeZoneInfo GetCompanyTimeZoneInfo()
    {
        try
        {
            var tzId = _companyProfiles.Get()?.TimeZoneId;
            if (!string.IsNullOrWhiteSpace(tzId))
            {
                return TimeZoneInfo.FindSystemTimeZoneById(tzId);
            }
        }
        catch
        {
            // Fallback to local system timezone if lookup fails
        }
        return TimeZoneInfo.Local;
    }

    public async Task<NtpSyncResult> SyncWithNtpAsync(string? ntpServer = null, CancellationToken ct = default)
    {
        var profile = _companyProfiles.Get();
        var server = !string.IsNullOrWhiteSpace(ntpServer)
            ? ntpServer.Trim()
            : profile?.Time?.NtpServer ?? "time.cloudflare.com";

        _logger.LogInformation("Querying NTP server {Server} for time synchronization...", server);
        var result = await _ntpClient.QueryAsync(server, 4000, ct);

        if (profile != null)
        {
            profile.Time ??= new TimeConfig();
            profile.Time.NtpServer = server;

            if (result.Success)
            {
                _clockOffset = result.Offset;
                profile.Time.LastDriftOffsetSeconds = result.Offset.TotalSeconds;
                profile.Time.LastNtpSyncUtc = DateTime.UtcNow;
                profile.Time.LastSyncError = null;

                _logger.LogInformation("NTP sync successful. Measured offset: {OffsetMs:F2} ms (round-trip {DelayMs:F2} ms)",
                    result.Offset.TotalMilliseconds, result.RoundTripDelayMs);
            }
            else
            {
                profile.Time.LastSyncError = result.ErrorMessage;
                _logger.LogWarning("NTP sync to {Server} failed: {Error}", server, result.ErrorMessage);
            }

            try
            {
                _companyProfiles.Save(profile);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to persist NTP sync results to CompanyProfile.");
            }
        }

        return result;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _cts = new CancellationTokenSource();
        _backgroundTask = PeriodicSyncLoopAsync(_cts.Token);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_cts != null)
        {
            _cts.Cancel();
            if (_backgroundTask != null)
            {
                await Task.WhenAny(_backgroundTask, Task.Delay(1000, cancellationToken));
            }
        }
    }

    private async Task PeriodicSyncLoopAsync(CancellationToken ct)
    {
        // Initial delay after server boot before first sync
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(15), ct);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        while (!ct.IsCancellationRequested)
        {
            try
            {
                var profile = _companyProfiles.Get();
                if (profile?.Time?.TimeSource == TimeConfig.SourceNtp)
                {
                    await SyncWithNtpAsync(profile.Time.NtpServer, ct);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Error occurred during periodic NTP synchronization.");
            }

            try
            {
                // Sync every 30 minutes
                await Task.Delay(TimeSpan.FromMinutes(30), ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _cts?.Dispose();
    }

    private class DefaultSystemClock : ISpokesClock
    {
        public DateTime UtcNow => DateTime.UtcNow;
        public DateTime Now => DateTime.Now;
        public TimeSpan ClockOffset => TimeSpan.Zero;
        public Task<NtpSyncResult> SyncWithNtpAsync(string? ntpServer = null, CancellationToken ct = default) =>
            Task.FromResult(new NtpSyncResult(false, TimeSpan.Zero, DateTime.UtcNow, 0, "No active clock service registered."));
    }
}
