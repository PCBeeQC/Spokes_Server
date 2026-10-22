using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Spokes_Server.Core.Models.HR;
using Spokes_Server.Core.Services.Communication.Voice;

namespace Spokes_Server.Components.UI;

/// <summary>
/// Presentation-layer state service managing audio/video device selection,
/// resolution profiles, iOS voice isolation status, user audio settings,
/// sound effects, and MudBlazor snackbar toast coordination for voice calls.
/// </summary>
public interface IVoiceUiStateService
{
    List<CameraDevice> AvailableCameras { get; set; }
    string ActiveCameraDeviceId { get; set; }

    List<AudioDevice> AvailableMicrophones { get; set; }
    string ActiveMicrophoneDeviceId { get; set; }

    List<AudioDevice> AvailableSpeakers { get; set; }
    string ActiveSpeakerDeviceId { get; set; }

    int MaxCameraHeight { get; set; }
    int MaxCameraFps { get; set; }

    int MaxScreenHeight { get; set; }
    int MaxScreenFps { get; set; }

    string MaxVideoSendQuality { get; set; }
    int MaxVideoSendFps { get; set; }

    string MaxScreenShareQuality { get; set; }
    int MaxScreenShareFps { get; set; }
    string ScreenShareMode { get; set; }
    bool IsScreenShareAudioEnabled { get; set; }

    string IosMicrophoneMode { get; set; }
    bool IsIosVoiceIsolationActive { get; }

    VoiceUserSettings VoiceSettings { get; set; }

    void UpdateAudioDevices(List<AudioDevice> mics, string activeMicId, List<AudioDevice> speakers, string activeSpeakerId);
    void ApplyLocalVideoDimensions(int maxEdge, int fps);
    void ApplyLocalScreenDimensions(int maxEdge, int fps);
    void ResetDeviceState();

    void ShowVoiceLog(string message, string severityStr);
    Task PlayTestSpeakerSoundAsync(string? deviceId = null, IVoiceJsInteropService? jsInterop = null);

    Task LoadDeviceVoiceSettingsAsync();
}
