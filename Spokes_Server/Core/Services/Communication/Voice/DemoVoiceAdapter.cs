using System.Collections.Generic;
using System.Linq;

namespace Spokes_Server.Core.Services.Communication.Voice;

public class DemoVoiceAdapter : IDemoVoiceAdapter
{
    private readonly GlobalVoiceService _voiceService;
    private readonly VoiceStateService _voiceState;

    public DemoVoiceAdapter(GlobalVoiceService voiceService, VoiceStateService voiceState)
    {
        _voiceService = voiceService;
        _voiceState = voiceState;
    }

    public void ConnectMock(string channelId, string userId, HashSet<string> mockParticipants)
    {
        _voiceService.ApplyMockConnectedState(channelId, userId, mockParticipants);
        foreach (var p in mockParticipants)
        {
            _voiceState.NotifyVoiceMemberJoined(channelId, p);
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
                _voiceState.NotifyVoiceMemberLeft(channelId, p);
            }
        }
        _voiceService.ApplyMockDisconnectedState();
    }
}
