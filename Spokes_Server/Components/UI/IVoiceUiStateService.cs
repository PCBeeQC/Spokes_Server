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
    bool HasLoadedVoiceSettings { get; }

    bool IsCallStageVisible { get; }
    bool IsInAppPipMinimized { get; }
    bool IsOsPipActive { get; }
    bool IsNativeMobilePipMode { get; }
    string? FocusedParticipantId { get; }
    string? FullscreenParticipantId { get; }
    string? StickySpeakerId { get; }
    bool ShouldShowInAppPip { get; }
    bool IsInAppPipCollapsedPill { get; }

    event Action? OnPipStateChanged;
    event Action? OnReturnToCallStageRequested;

    VoicePipPresentationState ComputePipPresentationState(bool isNativeMobileClient = false, IReadOnlySet<string>? verifiedParticipants = null);
    VoiceStagePresentationState ComputeStagePresentationState(IReadOnlySet<string>? verifiedParticipants = null, string? ringingCalleeId = null);
    void TogglePipGridOrFocusLayout();
    void SetCallStageVisible(bool visible);
    void SetFocusedParticipant(string? participantId, bool userInitiated = false);
    void ToggleFocusParticipant(string participantId);
    void SetFullscreenParticipant(string? participantId);
    Task EnterParticipantFullscreenAsync<T>(string participantId, Microsoft.JSInterop.DotNetObjectReference<T> dotNetHelper) where T : class => Task.CompletedTask;
    Task SetParticipantFullscreenRotationAsync(int rotationDeg) => Task.CompletedTask;
    Task ResetParticipantFullscreenZoomAsync() => Task.CompletedTask;
    Task ExitParticipantFullscreenAsync(bool goBack = true) => Task.CompletedTask;
    void SetInAppPipMinimized(bool minimized);
    void RequestReturnToCallStage();
    Task<bool> ToggleOsPipAsync();
    Task<bool> IsOsPipSupportedAsync();
    Task SyncPipMetadataAsync(VoicePipMetadataPayload payload);
    Task SyncSpeakingParticipantsAsync(string? channelId, string[] speakerIds) => Task.CompletedTask;
    Task SetInAppPipActiveAsync(bool active);

    void UpdateAudioDevices(List<AudioDevice> mics, string activeMicId, List<AudioDevice> speakers, string activeSpeakerId);
    void ApplyLocalVideoDimensions(int maxEdge, int fps);
    void ApplyLocalScreenDimensions(int maxEdge, int fps);
    void ResetDeviceState();

    void ShowVoiceLog(string message, string severityStr);
    Task PlayTestSpeakerSoundAsync(string? deviceId = null);

    Task LoadDeviceVoiceSettingsAsync();
}
