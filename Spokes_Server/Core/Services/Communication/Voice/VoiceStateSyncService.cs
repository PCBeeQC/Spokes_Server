using System.Text.Json;
using Livekit.Server;
using Livekit.Server.Sdk.Dotnet;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;
using Spokes_Server.Core.Services;

namespace Spokes_Server.Core.Services.Communication.Voice
{
    public class VoiceStateSyncService : BackgroundService
    {
        private readonly IServiceProvider _serviceProvider;
        private readonly ILogger<VoiceStateSyncService> _logger;
        private readonly Microsoft.Extensions.Configuration.IConfiguration _config;

        public VoiceStateSyncService(IServiceProvider serviceProvider, ILogger<VoiceStateSyncService> logger, Microsoft.Extensions.Configuration.IConfiguration config)
        {
            _serviceProvider = serviceProvider;
            _logger = logger;
            _config = config;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            if (_config.GetValue<bool>("Spokes_DemoMode") || _config.GetValue<bool>("Spokes_DemoSetup"))
            {
                return;
            }

            // Give LiveKit time to boot up.
            await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    using var scope = _serviceProvider.CreateScope();
                    var chatState = scope.ServiceProvider.GetRequiredService<ChatStateService>();
                    var chatService = scope.ServiceProvider.GetRequiredService<ChatService>();
                    var db = scope.ServiceProvider.GetRequiredService<Spokes_Server.Aggregate.Database>();
                    var httpClientFactory = scope.ServiceProvider.GetRequiredService<IHttpClientFactory>();

                    var config = db.SystemConfigs.Get();
                    var apiKey = config.LiveKitApiKey;
                    var apiSecret = config.LiveKitApiSecret;

                    var voicePort = Environment.GetEnvironmentVariable("Spokes_Voice_Port") ?? "7880";
                    var liveKitUrl = $"http://127.0.0.1:{voicePort}";

                    var activeChannelIds = chatState.GetAllActiveVoiceChannels();

                    var roomServiceClient = new RoomServiceClient(liveKitUrl, apiKey, apiSecret);

                    foreach (var channelId in activeChannelIds)
                    {
                        if (stoppingToken.IsCancellationRequested) break;

                        var roomName = $"channel-{channelId}";
                        var serverParticipants = chatState.GetVoiceParticipants(channelId).ToList();

                        if (serverParticipants.Count == 0) continue;

                        List<string> livekitParticipantIdentities = new();

                        try
                        {
                            var listResponse = await roomServiceClient.ListParticipants(new ListParticipantsRequest { Room = roomName });
                            foreach (var participant in listResponse.Participants)
                            {
                                livekitParticipantIdentities.Add(participant.Identity);
                            }
                        }
                        catch (Exception ex)
                        {
                            if (ex.Message.Contains("room not found", StringComparison.OrdinalIgnoreCase) || 
                                ex.Message.Contains("could not find room", StringComparison.OrdinalIgnoreCase))
                            {
                                // Safe to assume room is empty
                            }
                            else
                            {
                                _logger.LogWarning($"[VoiceStateSync] Transient error contacting LiveKit for {roomName}: {ex.Message}");
                                continue;
                            }
                        }

                        // Check who is missing from LiveKit but still registered in C#
                        foreach (var userId in serverParticipants)
                        {
                            bool isInLiveKit = livekitParticipantIdentities.Contains(userId);
                            if (!isInLiveKit)
                            {
                                _logger.LogInformation($"[VoiceStateSync] User {userId} missing from LiveKit room {roomName}. Kicking from ChatStateService.");
                                // The user's WebRTC connection actually dropped (or tab closed). 
                                // We safely remove them from the C# state.
                                await chatService.LeaveVoiceAsync(userId, channelId);
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning($"[VoiceStateSync] Watchdog loop exception: {ex.Message}");
                }

                // Wait 5 seconds before checking again.
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
        }
    }
}
