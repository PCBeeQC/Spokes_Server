using System;
using System.Threading.Tasks;
using Microsoft.JSInterop;

namespace Spokes_Server.Core.Services.Communication.Voice;

public interface IVoiceJsInteropService : IAsyncDisposable
{
    bool IsModuleLoaded { get; }
    Task InitializeAsync<T>(DotNetObjectReference<T> objRef) where T : class;
    Task<bool> IsAppleMobileDeviceAsync();
    Task<bool> IsAndroidDeviceAsync();
    Task<bool> IsNativePlatformAsync();
    Task<bool> ConnectToVoiceAsync<T>(string token, string url, DotNetObjectReference<T> objRef, int audioBitrateKbps, string noiseSuppression) where T : class;
    Task DisconnectFromVoiceAsync();
    Task SetMicrophoneEnabledAsync(bool enabled);
    Task<CameraInitResult?> SetCameraEnabledAsync(bool enabled, string quality, int fps, string deviceId);
    Task SwitchActiveCameraAsync(string deviceId);
    Task SwitchActiveMicrophoneAsync(string deviceId);
    Task SwitchActiveSpeakerAsync(string deviceId);
    Task UpdateAudioDevicesAsync();
    Task SetNoiseSuppressionTypeAsync(string type);
    Task<string> GetIosMicrophoneModeAsync();
    Task PromptIosVoiceIsolationAsync();
    Task SetMasterVolumeAsync(double volumePercent);
    Task SetParticipantVolumeAsync(string participantId, double volumePercent);
    Task SetVoiceSensitivityAsync(double sensitivity);
    Task InitVoiceSettingsAsync(string settingsJson);
    Task PlayTestSpeakerSoundAsync(string targetDevice);
    Task SetScreenShareEnabledAsync(bool enabled, string quality, int fps, string mode = "motion", bool captureAudio = false);
    Task<bool> SetScreenShareAudioEnabledAsync(bool enabled);
    Task UpdateScreenShareSettingsAsync(string quality, int fps, string mode);
    Task SetRemoteScreenAudioMutedAsync(string participantId, bool isMuted);
    Task ReattachAllTracksAsync();
    Task RefreshVideoLayoutsAsync();
    Task SetRemoteVideoQualityAsync(string participantId, string quality);
}
