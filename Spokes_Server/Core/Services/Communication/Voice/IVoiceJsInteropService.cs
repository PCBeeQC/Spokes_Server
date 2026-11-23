using System;
using System.Threading.Tasks;
using Microsoft.JSInterop;
using Spokes_Server.Core.Models.Communication;

namespace Spokes_Server.Core.Services.Communication.Voice;

public interface IVoiceJsInteropService : IAsyncDisposable
{
    bool IsModuleLoaded { get; }
    Task InitializeAsync<T>(DotNetObjectReference<T> objRef) where T : class;
    Task<bool> IsAppleMobileDeviceAsync();
    Task<bool> IsAndroidDeviceAsync();
    Task<bool> IsNativePlatformAsync();
    Task<bool> ConnectToVoiceAsync<T>(string token, string url, DotNetObjectReference<T> objRef, int audioBitrateKbps, string noiseSuppression, VoicePreJoinContext? preJoinContext = null) where T : class;
    Task PrepareVoiceConnectionAsync(string url, string? token = null) => Task.CompletedTask;
    Task<bool> IsRoomConnectedAsync() => Task.FromResult(false);
    Task DisconnectFromVoiceAsync();
    Task SetMicrophoneEnabledAsync(bool enabled);
    Task<CameraInitResult?> SetCameraEnabledAsync(bool enabled, string quality, int fps, string deviceId);
    Task UpdateCameraSettingsAsync(string quality, int fps, string deviceId);
    Task<string> SwitchActiveCameraAsync(string? deviceId = null);
    Task SwitchActiveMicrophoneAsync(string deviceId);
    Task SwitchActiveSpeakerAsync(string deviceId);
    Task UpdateAudioDevicesAsync();
    Task SetNoiseSuppressionTypeAsync(string type);
    Task ReinitializeMicrophoneAsync();
    Task<string> GetIosMicrophoneModeAsync();
    Task PromptIosVoiceIsolationAsync();
    Task SetMasterVolumeAsync(double volumePercent);
    Task SetParticipantVolumeAsync(string participantId, double volumePercent);
    Task SetVoiceSensitivityAsync(double sensitivity);
    Task SetAutoGainControlAsync(bool enabled) => Task.CompletedTask;
    Task SetIncomingVoiceNormalizationAsync(bool enabled) => Task.CompletedTask;
    Task InitVoiceSettingsAsync(string settingsJson);
    Task PlayTestSpeakerSoundAsync(string targetDevice);
    Task SetScreenShareEnabledAsync(bool enabled, string quality, int fps, string mode = "motion", bool captureAudio = false);
    Task<bool> SetScreenShareAudioEnabledAsync(bool enabled);
    Task UpdateScreenShareSettingsAsync(string quality, int fps, string mode);
    Task SetRemoteScreenAudioMutedAsync(string participantId, bool isMuted);
    Task ReattachAllTracksAsync();
    Task RefreshVideoLayoutsAsync();
    Task SetRemoteVideoQualityAsync(string participantId, string quality);
    Task<PhysicalDeviceIdentity?> GetOrCreatePhysicalDeviceIdentityAsync();
    Task<VoiceClientBootstrapInfo?> GetClientVoiceBootstrapAsync() => Task.FromResult<VoiceClientBootstrapInfo?>(null);
    Task SyncPhysicalRoomStateAsync(string[] coLocatedUserIds, string[] mutedRoommateUserIds);
    Task<bool> IsDesktopDirectRoomSupportedAsync();
    Task<string[]?> PrepareDirectRoomEndpointsAsync();
    Task SyncDirectRoomSessionAsync(PhysicalRoomDirectSessionDescriptor? session, bool isLocalMuted);
    Task<DirectRoomClientTelemetry?> GetDirectRoomAudioTelemetryAsync() => Task.FromResult<DirectRoomClientTelemetry?>(null);
    Task SyncPipMetadataAsync(VoicePipMetadataPayload payload);
    Task SyncSpeakingParticipantsAsync(string? channelId, string[] speakerIds) => Task.CompletedTask;
    Task SetInAppPipActiveAsync(bool active);
    Task RefreshInAppPipAsync();
    Task<bool> ToggleOsPipAsync();
    Task<bool> IsOsPipSupportedAsync();
    Task EnterParticipantFullscreenAsync<T>(string participantId, DotNetObjectReference<T> dotNetHelper) where T : class => Task.CompletedTask;
    Task SetParticipantFullscreenRotationAsync(int rotationDeg) => Task.CompletedTask;
    Task ResetParticipantFullscreenZoomAsync() => Task.CompletedTask;
    Task ExitParticipantFullscreenAsync(bool goBack = true) => Task.CompletedTask;
}
