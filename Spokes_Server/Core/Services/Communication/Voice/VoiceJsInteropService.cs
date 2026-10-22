using System;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using Spokes_Server.Core.Helpers;

namespace Spokes_Server.Core.Services.Communication.Voice;

public class VoiceJsInteropService : JsModuleBase, IVoiceJsInteropService
{
    private readonly ILogger<VoiceJsInteropService>? _logger;

    public bool IsModuleLoaded => moduleTask != null && moduleTask.IsValueCreated && moduleTask.Value.IsCompletedSuccessfully && moduleTask.Value.Result != null;

    public VoiceJsInteropService(IJSRuntime? js, ILogger<VoiceJsInteropService>? logger = null)
        : base(js, "./js/voiceInterop.js")
    {
        _logger = logger;
    }

    public async Task InitializeAsync<T>(DotNetObjectReference<T> objRef) where T : class
    {
        if (moduleTask == null || IsModuleLoaded) return;
        try
        {
            await moduleTask.Value;
        }
        catch (JSDisconnectedException ex)
        {
            _logger?.LogDebug(ex, "GlobalVoiceService JS load disconnected: {Message}", ex.Message);
        }
        catch (TaskCanceledException ex)
        {
            _logger?.LogDebug(ex, "GlobalVoiceService JS load canceled: {Message}", ex.Message);
        }
        catch (Exception e)
        {
            _logger?.LogError(e, "GlobalVoiceService JS load failed: {Message}", e.Message);
            throw;
        }
    }

    public async Task<bool> IsAppleMobileDeviceAsync()
    {
        if (!IsModuleLoaded) return false;
        return await SafeInvokeAsync<bool>("isAppleMobileDevice");
    }

    public async Task<bool> IsAndroidDeviceAsync()
    {
        if (!IsModuleLoaded) return false;
        return await SafeInvokeAsync<bool>("isAndroidDevice");
    }

    public async Task<bool> IsNativePlatformAsync()
    {
        if (!IsModuleLoaded) return false;
        return await SafeInvokeAsync<bool>("isNativePlatformApp");
    }

    public async Task<bool> ConnectToVoiceAsync<T>(string token, string url, DotNetObjectReference<T> objRef, int audioBitrateKbps, string noiseSuppression) where T : class
    {
        if (!IsModuleLoaded) throw new InvalidOperationException("Voice module not initialized.");
        return await SafeInvokeAsync<bool>("connectToVoice", token, url, objRef, audioBitrateKbps, noiseSuppression);
    }

    public async Task DisconnectFromVoiceAsync()
    {
        if (!IsModuleLoaded) return;
        await SafeInvokeVoidAsync("disconnectFromVoice");
    }

    public async Task SetMicrophoneEnabledAsync(bool enabled)
    {
        if (!IsModuleLoaded) return;
        await SafeInvokeVoidAsync("setMicrophoneEnabled", enabled);
    }

    public async Task<CameraInitResult?> SetCameraEnabledAsync(bool enabled, string quality, int fps, string deviceId)
    {
        if (!IsModuleLoaded) return null;
        if (enabled)
        {
            return await SafeInvokeAsync<CameraInitResult>("setCameraEnabled", enabled, quality, fps, deviceId);
        }
        else
        {
            await SafeInvokeVoidAsync("setCameraEnabled", enabled, quality, fps, deviceId);
            return null;
        }
    }

    public async Task SwitchActiveCameraAsync(string deviceId)
    {
        if (!IsModuleLoaded) return;
        await SafeInvokeVoidAsync("switchActiveCamera", deviceId);
    }

    public async Task SwitchActiveMicrophoneAsync(string deviceId)
    {
        if (!IsModuleLoaded) return;
        await SafeInvokeVoidAsync("switchActiveMicrophone", deviceId);
    }

    public async Task SwitchActiveSpeakerAsync(string deviceId)
    {
        if (!IsModuleLoaded) return;
        await SafeInvokeVoidAsync("switchActiveSpeaker", deviceId);
    }

    public async Task UpdateAudioDevicesAsync()
    {
        if (!IsModuleLoaded) return;
        await SafeInvokeVoidAsync("updateAudioDevices");
    }

    public async Task SetNoiseSuppressionTypeAsync(string type)
    {
        if (!IsModuleLoaded) return;
        await SafeInvokeVoidAsync("setNoiseSuppressionType", type);
    }

    public async Task<string> GetIosMicrophoneModeAsync()
    {
        if (!IsModuleLoaded) return "unsupported";
        return await SafeInvokeAsync<string>("getIosMicrophoneMode") ?? "unsupported";
    }

    public async Task PromptIosVoiceIsolationAsync()
    {
        if (!IsModuleLoaded) return;
        await SafeInvokeVoidAsync("promptIosVoiceIsolation");
    }

    public async Task SetMasterVolumeAsync(double volumePercent)
    {
        if (!IsModuleLoaded) return;
        await SafeInvokeVoidAsync("setMasterVolume", volumePercent);
    }

    public async Task SetParticipantVolumeAsync(string participantId, double volumePercent)
    {
        if (!IsModuleLoaded) return;
        await SafeInvokeVoidAsync("setParticipantVolume", participantId, volumePercent);
    }

    public async Task SetVoiceSensitivityAsync(double sensitivity)
    {
        if (!IsModuleLoaded) return;
        await SafeInvokeVoidAsync("setVoiceSensitivity", sensitivity);
    }

    public async Task InitVoiceSettingsAsync(string settingsJson)
    {
        if (!IsModuleLoaded) return;
        await SafeInvokeVoidAsync("initVoiceSettings", settingsJson);
    }

    public async Task PlayTestSpeakerSoundAsync(string targetDevice)
    {
        if (!IsModuleLoaded) return;
        await SafeInvokeVoidAsync("playTestSpeakerSound", targetDevice);
    }

    public async Task SetScreenShareEnabledAsync(bool enabled, string quality, int fps, string mode = "motion", bool captureAudio = false)
    {
        if (!IsModuleLoaded) return;
        await SafeInvokeVoidAsync("setScreenShareEnabled", enabled, quality, fps, mode, captureAudio);
    }

    public async Task<bool> SetScreenShareAudioEnabledAsync(bool enabled)
    {
        if (!IsModuleLoaded) return false;
        return await SafeInvokeAsync<bool>("setScreenShareAudioEnabled", enabled);
    }

    public async Task UpdateScreenShareSettingsAsync(string quality, int fps, string mode)
    {
        if (!IsModuleLoaded) return;
        await SafeInvokeVoidAsync("updateScreenShareSettings", quality, fps, mode);
    }

    public async Task SetRemoteScreenAudioMutedAsync(string participantId, bool isMuted)
    {
        if (!IsModuleLoaded) return;
        await SafeInvokeVoidAsync("setRemoteScreenAudioMuted", participantId, isMuted);
    }

    public async Task ReattachAllTracksAsync()
    {
        if (!IsModuleLoaded) return;
        await SafeInvokeVoidAsync("reattachAllTracks");
    }

    public async Task RefreshVideoLayoutsAsync()
    {
        if (!IsModuleLoaded) return;
        await SafeInvokeVoidAsync("refreshVideoLayouts");
    }

    public async Task SetRemoteVideoQualityAsync(string participantId, string quality)
    {
        if (!IsModuleLoaded) return;
        await SafeInvokeVoidAsync("setRemoteVideoQuality", participantId, quality);
    }
}
