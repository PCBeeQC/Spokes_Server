using Livekit.Server;
using Livekit.Server.Sdk.Dotnet;
using Microsoft.Extensions.Configuration;

using Spokes_Server.Aggregate;

namespace Spokes_Server.Core.Services.Communication.Voice
{
    public class VoiceChannelService
    {
        private readonly string _apiKey;
        private readonly string _apiSecret;

        public VoiceChannelService(Database db)
        {
            var config = db.SystemConfigs.Get();
            _apiKey = config.LiveKitApiKey;
            _apiSecret = config.LiveKitApiSecret;
        }

        public string GenerateJoinToken(string channelId, string userId, string userName, string userAvatarUrl = "")
        {
            string roomName = $"channel-{channelId}";

            var token = new AccessToken(_apiKey, _apiSecret)
                .WithIdentity(userId)
                .WithName(userName)
                .WithMetadata(userAvatarUrl);



            token.WithGrants(new VideoGrants
            {
                RoomJoin = true,
                Room = roomName,
                CanPublish = true,
                CanPublishData = true,
                CanSubscribe = true
            });

            return token.ToJwt();
        }
    }
}
