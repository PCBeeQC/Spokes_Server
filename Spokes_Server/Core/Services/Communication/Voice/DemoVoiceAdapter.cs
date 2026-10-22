using System.Collections.Generic;
using System.Linq;
using Spokes_Server.Core.Services.Communication.Chat;

namespace Spokes_Server.Core.Services.Communication.Voice;

public class DemoVoiceAdapter : IDemoVoiceAdapter
{
    private readonly GlobalVoiceService _voiceService;
    private readonly ChatStateService _chatState;

    public DemoVoiceAdapter(GlobalVoiceService voiceService, ChatStateService chatState)
    {
        _voiceService = voiceService;
        _chatState = chatState;
    }

    public void ConnectMock(string channelId, string userId, HashSet<string> mockParticipants)
    {
        _voiceService.ApplyMockConnectedState(channelId, userId, mockParticipants);
        foreach (var p in mockParticipants)
        {
            _chatState.NotifyVoiceMemberJoined(channelId, p);
        }
    }

    public void DisconnectMock()
    {
        var channelId = _voiceService.CurrentChannelId;
        var participants = _voiceService.CallParticipants.ToList();
        if (!string.IsNullOrEmpty(channelId))
        {
            foreach (var p in participants)
            {
                _chatState.NotifyVoiceMemberLeft(channelId, p);
            }
        }
        _voiceService.ApplyMockDisconnectedState();
    }
}
