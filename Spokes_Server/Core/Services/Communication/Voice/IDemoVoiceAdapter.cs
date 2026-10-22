using System.Collections.Generic;

namespace Spokes_Server.Core.Services.Communication.Voice;

/// <summary>
/// Provides mock voice channel connection adapter for demo mode without establishing WebRTC signaling.
/// </summary>
public interface IDemoVoiceAdapter
{
    void ConnectMock(string channelId, string userId, HashSet<string> mockParticipants);
    void DisconnectMock();
}
