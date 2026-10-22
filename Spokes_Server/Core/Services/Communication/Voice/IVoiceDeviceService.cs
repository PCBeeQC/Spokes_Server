using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Spokes_Server.Core.Services.Communication.Voice;

public interface IVoiceDeviceService
{
    event Action<List<AudioDevice>, string, List<AudioDevice>, string>? OnAudioDevicesChanged;
    event Action<List<CameraDevice>, string>? OnCamerasUpdated;
    event Action<string>? OnActiveCameraChanged;
    event Action<string>? OnActiveMicrophoneChanged;
    event Action<string>? OnActiveSpeakerChanged;
    event Action<string>? OnIosMicrophoneModeChanged;
    event Action<int, int>? OnLocalVideoDimensionsChanged;
    event Action<int, int>? OnLocalScreenDimensionsChanged;

    ConcurrentDictionary<string, int> RemoteVideoHeights { get; }
    ConcurrentDictionary<string, int> RemoteVideoFps { get; }

    Task SwitchCameraAsync(string deviceId, bool isVideoOn);
    Task SwitchMicrophoneAsync(string deviceId);
    Task SwitchSpeakerAsync(string deviceId);
    Task RefreshAudioDevicesAsync();
    Task<string> GetIosMicrophoneModeAsync(bool isIos);
    Task PromptIosVoiceIsolationAsync(bool isIos, Func<Task>? onRefresh = null);
    Task SetRemoteVideoQualityAsync(string participantId, string quality);
    Task ReattachTracksAsync();
    Task RefreshVideoLayoutsAsync();

    void HandleAudioDevicesUpdated(List<AudioDevice> mics, string activeMicId, List<AudioDevice> speakers, string activeSpeakerId);
    void HandleCamerasUpdated(List<CameraDevice> cameras, string activeCameraId);
    void HandleParticipantVideoDimensions(string participantId, int height, int fps);
    void HandleLocalVideoDimensions(int maxEdge, int fps);
    void HandleLocalScreenDimensions(int maxEdge, int fps);
    void RemoveParticipantVideo(string participantId);
    void Reset();
}
