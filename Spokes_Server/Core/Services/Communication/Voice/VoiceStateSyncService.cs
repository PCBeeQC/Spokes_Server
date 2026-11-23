using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Spokes_Server.Core.Data.Repositories.Core;

namespace Spokes_Server.Core.Services.Communication.Voice;

public class VoiceStateSyncService : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<VoiceStateSyncService> _logger;
    private readonly IConfiguration _config;
    private readonly ConcurrentDictionary<(string ChannelId, string UserId), int> _consecutiveMissingCounts = new();
    private readonly ConcurrentDictionary<(string ChannelId, string UserId, string SessionId), int> _consecutiveClosedSessionCounts = new();

    public VoiceStateSyncService(IServiceProvider serviceProvider, ILogger<VoiceStateSyncService> logger, IConfiguration config)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
        _config = config;
    }

    protected virtual TimeSpan InitialDelay => TimeSpan.FromSeconds(10);
    protected virtual TimeSpan PollInterval => TimeSpan.FromSeconds(10);
    protected virtual TimeSpan ActivityGracePeriod => TimeSpan.FromSeconds(15);
    protected virtual int ConsecutiveMissesBeforeEviction => 2;
    protected virtual int ConsecutiveClosedSessionChecksBeforeEviction => 1;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_config.GetValue<bool>("Spokes_DemoMode") || _config.GetValue<bool>("Spokes_DemoSetup"))
        {
            return;
        }

        // Give LiveKit time to boot up.
        await Task.Delay(InitialDelay, stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await SyncActiveChannelsOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning($"[VoiceStateSync] Watchdog loop exception: {ex.Message}");
            }

            // Wait before checking again.
            await Task.Delay(PollInterval, stoppingToken);
        }

        stoppingToken.ThrowIfCancellationRequested();
    }

    internal async Task SyncActiveChannelsOnceAsync(CancellationToken stoppingToken)
    {
        using var scope = _serviceProvider.CreateScope();
        var voiceState = scope.ServiceProvider.GetRequiredService<VoiceStateService>();
        var voiceChannelService = scope.ServiceProvider.GetRequiredService<VoiceChannelService>();
        var systemConfigs = scope.ServiceProvider.GetRequiredService<SystemConfigRepository>();
        var liveKitService = scope.ServiceProvider.GetService<LiveKitService>()
            ?? new LiveKitService(NullLogger<LiveKitService>.Instance, _config);

        var config = systemConfigs.Get();
        var apiKey = config.LiveKitApiKey ?? string.Empty;
        var apiSecret = config.LiveKitApiSecret ?? string.Empty;

        var activeChannelIds = voiceState.GetAllActiveVoiceChannels();
        var activeChannelSet = new HashSet<string>(activeChannelIds, StringComparer.Ordinal);
        var liveKitChannelIds = await liveKitService.GetActiveVoiceChannelIdsAsync(apiKey, apiSecret, stoppingToken) ?? [];
        var liveKitChannelSet = new HashSet<string>(liveKitChannelIds, StringComparer.Ordinal);
        foreach (var lkChannelId in liveKitChannelIds)
        {
            if (!string.IsNullOrWhiteSpace(lkChannelId))
            {
                activeChannelSet.Add(lkChannelId);
            }
        }

        foreach (var key in _consecutiveMissingCounts.Keys.ToList())
        {
            if (!activeChannelSet.Contains(key.ChannelId))
            {
                _consecutiveMissingCounts.TryRemove(key, out _);
            }
        }
        foreach (var key in _consecutiveClosedSessionCounts.Keys.ToList())
        {
            if (!activeChannelSet.Contains(key.ChannelId))
            {
                _consecutiveClosedSessionCounts.TryRemove(key, out _);
            }
        }

        foreach (var channelId in activeChannelSet)
        {
            if (stoppingToken.IsCancellationRequested) break;

            var serverParticipants = voiceState.GetVoiceParticipants(channelId).ToList();
            if (serverParticipants.Count == 0 && !liveKitChannelSet.Contains(channelId)) continue;

            var livekitStates = await liveKitService.GetRoomParticipantStatesAsync(
                channelId,
                apiKey,
                apiSecret,
                stoppingToken);

            // Null indicates a transient error contacting LiveKit; skip reconciliation for this room
            if (livekitStates == null)
            {
                continue;
            }

            var roomName = LiveKitService.GetRoomName(channelId);
            var currentParticipantSet = new HashSet<string>(serverParticipants, StringComparer.Ordinal);
            foreach (var key in _consecutiveMissingCounts.Keys.ToList())
            {
                if (string.Equals(key.ChannelId, channelId, StringComparison.Ordinal) &&
                    !currentParticipantSet.Contains(key.UserId))
                {
                    _consecutiveMissingCounts.TryRemove(key, out _);
                }
            }

            var livekitHumansByIdentity = new Dictionary<string, LiveKitParticipantSnapshot>(StringComparer.Ordinal);
            foreach (var snapshot in livekitStates)
            {
                if (string.IsNullOrWhiteSpace(snapshot.Identity))
                {
                    continue;
                }

                if (!LiveKitService.IsSyntheticParticipantIdentity(snapshot.Identity))
                {
                    livekitHumansByIdentity[snapshot.Identity] = snapshot;
                }
            }

            // Reconcile LiveKit -> C#: ensure connected LiveKit participants (and their device/mute metadata)
            // are accurately reflected in VoiceStateService without disrupting steady-state callers.
            var nowUtc = DateTime.UtcNow;
            foreach (var kvp in livekitHumansByIdentity)
            {
                var lkUserId = kvp.Key;
                var lkSnapshot = kvp.Value;
                var metadata = VoiceParticipantMetadata.Parse(lkSnapshot.Metadata);
                if (voiceState.IsVoiceSessionClosed(metadata.VoiceSessionId))
                {
                    if (!voiceState.IsVoiceSessionExplicitlyClosed(metadata.VoiceSessionId))
                    {
                        voiceState.ReopenVoiceSession(channelId, lkUserId, metadata.VoiceSessionId, lkSnapshot.Sid);
                    }
                    else
                    {
                        var closedKey = (channelId, lkUserId, metadata.VoiceSessionId.Trim());
                        var closedAtUtc = voiceState.GetVoiceSessionClosedAtUtc(metadata.VoiceSessionId);
                        if (ActivityGracePeriod > TimeSpan.Zero &&
                            closedAtUtc.HasValue &&
                            (nowUtc - closedAtUtc.Value) < ActivityGracePeriod)
                        {
                            _logger.LogDebug(
                                "[VoiceStateSync] Participant {UserId} in channel {ChannelId} has closed session {SessionId} within {GraceSeconds}s reconnect grace; deferring LiveKit eviction.",
                                lkUserId, channelId, metadata.VoiceSessionId, ActivityGracePeriod.TotalSeconds);
                            continue;
                        }

                        int closedHits = _consecutiveClosedSessionCounts.AddOrUpdate(closedKey, 1, (_, prev) => prev + 1);
                        if (closedHits < ConsecutiveClosedSessionChecksBeforeEviction)
                        {
                            continue;
                        }

                        _consecutiveClosedSessionCounts.TryRemove(closedKey, out _);
                        if (!string.IsNullOrEmpty(apiKey) && !string.IsNullOrEmpty(apiSecret))
                        {
                            await liveKitService.RemoveParticipantAsync(channelId, lkUserId, apiKey, apiSecret, stoppingToken);
                        }
                        continue;
                    }
                }

                if (!string.IsNullOrWhiteSpace(metadata.VoiceSessionId))
                {
                    _consecutiveClosedSessionCounts.TryRemove((channelId, lkUserId, metadata.VoiceSessionId.Trim()), out _);
                }
                _consecutiveMissingCounts.TryRemove((channelId, lkUserId), out _);

                bool missingInServer = !currentParticipantSet.Contains(lkUserId);
                bool muteDrifted = lkSnapshot.IsMicMuted.HasValue && voiceState.IsUserMuted(channelId, lkUserId) != lkSnapshot.IsMicMuted.Value;
                bool deviceMissing = !string.IsNullOrWhiteSpace(metadata.DeviceId) && voiceState.GetVoiceParticipantDevice(channelId, lkUserId) == null;
                bool sessionChanged = !string.IsNullOrWhiteSpace(metadata.VoiceSessionId) &&
                                      !string.Equals(voiceState.GetVoiceParticipantSessionId(channelId, lkUserId), metadata.VoiceSessionId, StringComparison.Ordinal);

                if (missingInServer || muteDrifted || deviceMissing || sessionChanged)
                {
                    await voiceChannelService.ReconcileMemberStateAsync(
                        channelId,
                        lkUserId,
                        metadata,
                        lkSnapshot.IsMicMuted,
                        stoppingToken);
                }
                if (!string.IsNullOrWhiteSpace(lkSnapshot.Sid))
                {
                    voiceState.SetVoiceParticipantSid(channelId, lkUserId, metadata.VoiceSessionId, lkSnapshot.Sid);
                }

                var currentMedia = voiceState.GetVoiceParticipantMediaState(channelId, lkUserId);
                if (currentMedia.IsCameraOn != lkSnapshot.IsCameraOn ||
                    currentMedia.IsScreenSharing != lkSnapshot.IsScreenSharing ||
                    currentMedia.HasScreenAudio != lkSnapshot.HasScreenAudio)
                {
                    voiceState.TrySetVoiceParticipantMediaState(
                        channelId,
                        lkUserId,
                        metadata.VoiceSessionId,
                        isCameraOn: lkSnapshot.IsCameraOn,
                        isScreenSharing: lkSnapshot.IsScreenSharing,
                        hasScreenAudio: lkSnapshot.HasScreenAudio);
                }
            }

            // Check who is missing from LiveKit but still registered in C#.
            // Require activity grace + consecutive misses across slow polls so transient ICE/full-reconnect gaps
            // or webhook ordering jitter never evict an active caller from VoiceStateService.
            foreach (var userId in serverParticipants)
            {
                var missKey = (channelId, userId);
                if (livekitHumansByIdentity.ContainsKey(userId))
                {
                    _consecutiveMissingCounts.TryRemove(missKey, out _);
                    continue;
                }

                var lastActivityUtc = voiceState.GetVoiceParticipantLastActivityUtc(channelId, userId);
                if (ActivityGracePeriod > TimeSpan.Zero &&
                    lastActivityUtc.HasValue &&
                    (nowUtc - lastActivityUtc.Value) < ActivityGracePeriod)
                {
                    _consecutiveMissingCounts.TryRemove(missKey, out _);
                    _logger.LogDebug("[VoiceStateSync] User {UserId} in channel {ChannelId} missing from LiveKit poll but still within {GraceSeconds}s activity grace; skipping miss increment.", userId, channelId, ActivityGracePeriod.TotalSeconds);
                    continue;
                }

                int misses = _consecutiveMissingCounts.AddOrUpdate(missKey, 1, (_, prev) => prev + 1);
                if (misses < ConsecutiveMissesBeforeEviction)
                {
                    _logger.LogDebug("[VoiceStateSync] User {UserId} transiently missing from LiveKit room {RoomName} (miss {MissCount}/{Threshold}); waiting before eviction.", userId, roomName, misses, ConsecutiveMissesBeforeEviction);
                    continue;
                }

                _consecutiveMissingCounts.TryRemove(missKey, out _);
                var expectedSessionId = voiceState.GetVoiceParticipantSessionId(channelId, userId);
                _logger.LogInformation("[VoiceStateSync] User {UserId} missing from LiveKit room {RoomName} across {MissCount} checks. Cleaning up stale C# participant.", userId, roomName, misses);

                if (!string.IsNullOrWhiteSpace(expectedSessionId))
                {
                    await voiceChannelService.TryLeaveVoiceAsync(channelId, userId, expectedSessionId, evictFromLiveKit: false, stoppingToken);
                }
                else
                {
                    await voiceChannelService.LeaveVoiceAsync(channelId, userId, evictFromLiveKit: false, stoppingToken);
                }
            }
        }
    }
}
