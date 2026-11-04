using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Spokes_Server.Core.Data.Repositories.Core;
using Spokes_Server.Core.Services.Communication.Chat;

namespace Spokes_Server.Core.Services.Communication.Voice;

public class VoiceStateSyncService : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<VoiceStateSyncService> _logger;
    private readonly IConfiguration _config;

    public VoiceStateSyncService(IServiceProvider serviceProvider, ILogger<VoiceStateSyncService> logger, IConfiguration config)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
        _config = config;
    }

    protected virtual TimeSpan InitialDelay => TimeSpan.FromSeconds(5);
    protected virtual TimeSpan PollInterval => TimeSpan.FromSeconds(5);

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
            catch (Exception ex)
            {
                _logger.LogWarning($"[VoiceStateSync] Watchdog loop exception: {ex.Message}");
            }

            // Wait before checking again.
            await Task.Delay(PollInterval, stoppingToken);
        }
    }

    internal async Task SyncActiveChannelsOnceAsync(CancellationToken stoppingToken)
    {
        using var scope = _serviceProvider.CreateScope();
        var chatState = scope.ServiceProvider.GetRequiredService<ChatStateService>();
        var voiceChannelService = scope.ServiceProvider.GetRequiredService<VoiceChannelService>();
        var systemConfigs = scope.ServiceProvider.GetRequiredService<SystemConfigRepository>();
        var liveKitService = scope.ServiceProvider.GetService<LiveKitService>()
            ?? new LiveKitService(NullLogger<LiveKitService>.Instance, _config);

        var config = systemConfigs.Get();
        var apiKey = config.LiveKitApiKey ?? string.Empty;
        var apiSecret = config.LiveKitApiSecret ?? string.Empty;

        var activeChannelIds = chatState.GetAllActiveVoiceChannels();

        foreach (var channelId in activeChannelIds)
        {
            if (stoppingToken.IsCancellationRequested) break;

            var serverParticipants = chatState.GetVoiceParticipants(channelId).ToList();
            if (serverParticipants.Count == 0) continue;

            var livekitParticipantIdentities = await liveKitService.GetRoomParticipantIdentitiesAsync(
                channelId,
                apiKey,
                apiSecret,
                stoppingToken);

            // Null indicates a transient error contacting LiveKit; skip reconciliation for this room
            if (livekitParticipantIdentities == null)
            {
                continue;
            }

            var roomName = LiveKitService.GetRoomName(channelId);

            // Check who is missing from LiveKit but still registered in C#
            foreach (var userId in serverParticipants)
            {
                bool isInLiveKit = livekitParticipantIdentities.Contains(userId);
                if (!isInLiveKit)
                {
                    _logger.LogInformation($"[VoiceStateSync] User {userId} missing from LiveKit room {roomName}. Kicking from ChatStateService.");
                    // The user's WebRTC connection actually dropped (or tab closed).
                    // Remove them from C# state without issuing a redundant LiveKit eviction.
                    await voiceChannelService.LeaveVoiceAsync(channelId, userId, evictFromLiveKit: false, stoppingToken);
                    if (livekitParticipantIdentities.Contains($"{userId}:cam") && !string.IsNullOrEmpty(apiKey) && !string.IsNullOrEmpty(apiSecret))
                    {
                        await liveKitService.RemoveParticipantAsync(channelId, $"{userId}:cam", apiKey, apiSecret, stoppingToken);
                    }
                }
            }

            // Also evict any orphaned :cam companion participants whose primary identity is no longer in LiveKit
            if (!string.IsNullOrEmpty(apiKey) && !string.IsNullOrEmpty(apiSecret))
            {
                foreach (var lkIdentity in livekitParticipantIdentities)
                {
                    if (lkIdentity.EndsWith(":cam", StringComparison.OrdinalIgnoreCase))
                    {
                        var ownerId = lkIdentity[..^4];
                        if (!livekitParticipantIdentities.Contains(ownerId))
                        {
                            _logger.LogInformation("[VoiceStateSync] Evicting orphaned camera companion {CompanionId} from LiveKit room {RoomName}.", lkIdentity, roomName);
                            await liveKitService.RemoveParticipantAsync(channelId, lkIdentity, apiKey, apiSecret, stoppingToken);
                        }
                    }
                }
            }
        }
    }
}
