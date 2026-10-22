using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using MudBlazor;
using Spokes_Server.Core.Models.HR;
using Spokes_Server.Core.Services.Communication.Voice;
using Spokes_Server.Core.Services.UI;

namespace Spokes_Server.Components.UI;

/// <summary>
/// Dedicated Blazor UI state service in the presentation layer.
/// Coordinates UI presentation state, MudBlazor snackbar toast alerts,
/// and audio effects by listening to voice signaling events from GlobalVoiceService.
/// </summary>
public class VoiceUiStateService : IVoiceUiStateService, IDisposable
{
    private readonly ISnackbar _snackbar;
    private readonly ISoundService _soundService;
    private readonly IJSRuntime _js;
    private readonly GlobalVoiceService _voiceService;
    private readonly IVoiceJsInteropService _jsInterop;
    private readonly ILogger<VoiceUiStateService> _logger;
    private readonly IVoiceDeviceService _deviceService;

    public List<CameraDevice> AvailableCameras { get; set; } = [];
    public string ActiveCameraDeviceId { get; set; } = "";

    public List<AudioDevice> AvailableMicrophones { get; set; } = [];
    public string ActiveMicrophoneDeviceId { get; set; } = "";

    public List<AudioDevice> AvailableSpeakers { get; set; } = [];
    public string ActiveSpeakerDeviceId { get; set; } = "";

    public int MaxCameraHeight { get; set; } = 2160;
    public int MaxCameraFps { get; set; } = 30;

    public int MaxScreenHeight { get; set; } = 2160;
    public int MaxScreenFps { get; set; } = 30;

    public string MaxVideoSendQuality { get; set; } = "Auto";
    public int MaxVideoSendFps { get; set; } = 30;

    public string MaxScreenShareQuality { get; set; } = "Auto";
    public int MaxScreenShareFps { get; set; } = 30;
    public string ScreenShareMode { get; set; } = "motion";
    public bool IsScreenShareAudioEnabled { get; set; } = false;

    public string IosMicrophoneMode { get; set; } = "unsupported";
    public bool IsIosVoiceIsolationActive => IosMicrophoneMode == "voiceIsolation";

    public VoiceUserSettings VoiceSettings { get; set; } = new();

    public VoiceUiStateService(
        ISnackbar snackbar,
        ISoundService soundService,
        IJSRuntime js,
        GlobalVoiceService voiceService,
        IVoiceJsInteropService jsInterop,
        IVoiceDeviceService deviceService,
        ILogger<VoiceUiStateService> logger)
    {
        _snackbar = snackbar ?? throw new ArgumentNullException(nameof(snackbar));
        _soundService = soundService ?? throw new ArgumentNullException(nameof(soundService));
        _js = js ?? throw new ArgumentNullException(nameof(js));
        _voiceService = voiceService ?? throw new ArgumentNullException(nameof(voiceService));
        _jsInterop = jsInterop ?? throw new ArgumentNullException(nameof(jsInterop));
        _deviceService = deviceService ?? throw new ArgumentNullException(nameof(deviceService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        _voiceService.OnVoiceLogReceived += ShowVoiceLog;
        _voiceService.OnRoomLeft += HandleRoomLeft;
        _voiceService.OnVoiceSensitivityChanged += HandleVoiceSensitivityChanged;
        _voiceService.OnMasterVolumeChanged += HandleMasterVolumeChanged;
        _voiceService.OnParticipantVolumeChanged += HandleParticipantVolumeChanged;
        _voiceService.OnVoiceSettingsUpdated += HandleVoiceSettingsUpdated;
        _voiceService.OnScreenShareAudioStateChanged += HandleScreenShareAudioStateChanged;

        _deviceService.OnAudioDevicesChanged += UpdateAudioDevices;
        _deviceService.OnLocalVideoDimensionsChanged += ApplyLocalVideoDimensions;
        _deviceService.OnLocalScreenDimensionsChanged += ApplyLocalScreenDimensions;
        _deviceService.OnCamerasUpdated += HandleCamerasUpdated;
        _deviceService.OnIosMicrophoneModeChanged += HandleIosMicrophoneModeChanged;
        _deviceService.OnActiveCameraChanged += HandleActiveCameraChanged;
        _deviceService.OnActiveMicrophoneChanged += HandleActiveMicrophoneChanged;
        _deviceService.OnActiveSpeakerChanged += HandleActiveSpeakerChanged;
    }

    private void HandleIosMicrophoneModeChanged(string mode) => IosMicrophoneMode = mode;
    private void HandleVoiceSensitivityChanged(double sensitivity) => VoiceSettings.VoiceSensitivity = sensitivity;
    private void HandleMasterVolumeChanged(double volume) => VoiceSettings.MasterVolume = volume;
    private void HandleParticipantVolumeChanged(string id, double volume) => VoiceSettings.ParticipantVolumes[id] = volume;
    private void HandleActiveCameraChanged(string id) => ActiveCameraDeviceId = id;
    private void HandleActiveMicrophoneChanged(string id) => ActiveMicrophoneDeviceId = id;
    private void HandleActiveSpeakerChanged(string id) => ActiveSpeakerDeviceId = id;
    private void HandleVoiceSettingsUpdated(VoiceUserSettings settings) => VoiceSettings = settings;
    private void HandleScreenShareAudioStateChanged(bool active) => IsScreenShareAudioEnabled = active;

    private void HandleCamerasUpdated(List<CameraDevice> cameras, string activeId)
    {
        AvailableCameras = cameras;
        ActiveCameraDeviceId = activeId;
    }

    private void HandleRoomLeft()
    {
        _ = PlayChannelLeaveSafeAsync();
        ResetDeviceState();
    }

    private async Task PlayChannelLeaveSafeAsync()
    {
        try
        {
            await _soundService.PlayChannelLeaveAsync();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to play channel leave sound: {Message}", ex.Message);
        }
    }

    public void UpdateAudioDevices(List<AudioDevice> mics, string activeMicId, List<AudioDevice> speakers, string activeSpeakerId)
    {
        AvailableMicrophones = mics?.Take(100).ToList() ?? [];
        ActiveMicrophoneDeviceId = string.IsNullOrEmpty(activeMicId) ? (AvailableMicrophones.FirstOrDefault()?.DeviceId ?? "") : activeMicId;

        AvailableSpeakers = speakers?.Take(100).ToList() ?? [];
        ActiveSpeakerDeviceId = string.IsNullOrEmpty(activeSpeakerId) ? (AvailableSpeakers.FirstOrDefault()?.DeviceId ?? "") : activeSpeakerId;
    }

    public void ApplyLocalVideoDimensions(int maxEdge, int fps)
    {
        MaxCameraHeight = maxEdge;
        MaxCameraFps = fps;
        MaxVideoSendQuality = ClampQuality(MaxVideoSendQuality, maxEdge);
    }

    public void ApplyLocalScreenDimensions(int maxEdge, int fps)
    {
        MaxScreenHeight = maxEdge;
        MaxScreenFps = fps;
        MaxScreenShareQuality = ClampQuality(MaxScreenShareQuality, maxEdge);
    }

    private static string ClampQuality(string currentQuality, int maxEdge)
    {
        if (currentQuality == "Auto")
        {
            return currentQuality;
        }

        int currentRank = currentQuality switch
        {
            "360p" => 1,
            "720p" => 2,
            "1080p" => 3,
            "1440p" => 4,
            "2160p" => 5,
            _ => 0
        };

        var (maxAllowedRank, maxAllowedQuality) = maxEdge switch
        {
            < 1000 => (1, "360p"),
            < 1800 => (2, "720p"),
            < 2400 => (3, "1080p"),
            < 3000 => (4, "1440p"),
            _ => (5, "2160p")
        };

        return currentRank > maxAllowedRank ? maxAllowedQuality : currentQuality;
    }

    public void ResetDeviceState()
    {
        AvailableCameras.Clear();
        ActiveCameraDeviceId = "";
        MaxCameraHeight = 2160;
        MaxCameraFps = 30;
        MaxScreenHeight = 2160;
        MaxScreenFps = 30;
        MaxVideoSendQuality = "Auto";
        MaxVideoSendFps = 30;
        MaxScreenShareQuality = "Auto";
        MaxScreenShareFps = 30;
        ScreenShareMode = "motion";
        IsScreenShareAudioEnabled = false;
        IosMicrophoneMode = "unsupported";
    }

    public void ShowVoiceLog(string message, string severityStr)
    {
        var severity = severityStr?.ToLower() switch
        {
            "success" => Severity.Success,
            "error" => Severity.Error,
            "warning" => Severity.Warning,
            "info" => Severity.Info,
            _ => Severity.Normal
        };

        _snackbar.Add(message, severity);
    }



    public async Task PlayTestSpeakerSoundAsync(string? deviceId = null, IVoiceJsInteropService? jsInterop = null)
    {
        var targetDevice = !string.IsNullOrEmpty(deviceId) ? deviceId : ActiveSpeakerDeviceId;
        var interop = jsInterop ?? _jsInterop;

        try
        {
            if (!interop.IsModuleLoaded)
            {
                await _voiceService.EnsureModuleInitializedAsync();
            }

            if (interop.IsModuleLoaded)
            {
                await interop.PlayTestSpeakerSoundAsync(targetDevice);
                return;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to play test sound via voice module, falling back to sound service");
        }

        await _soundService.PlaySoundAsync("spokesnotif1", true);
    }

    public async Task LoadDeviceVoiceSettingsAsync()
    {
        try
        {
            var json = await _js.InvokeAsync<string?>("localStorage.getItem", "spokes_voice_settings_v1");
            VoiceSettings = !string.IsNullOrEmpty(json)
                ? System.Text.Json.JsonSerializer.Deserialize<VoiceUserSettings>(json) ?? new()
                : new();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load device voice settings from localStorage");
            VoiceSettings = new();
        }
    }

    public void Dispose()
    {
        _voiceService.OnVoiceLogReceived -= ShowVoiceLog;
        _voiceService.OnRoomLeft -= HandleRoomLeft;
        _voiceService.OnVoiceSensitivityChanged -= HandleVoiceSensitivityChanged;
        _voiceService.OnMasterVolumeChanged -= HandleMasterVolumeChanged;
        _voiceService.OnParticipantVolumeChanged -= HandleParticipantVolumeChanged;
        _voiceService.OnVoiceSettingsUpdated -= HandleVoiceSettingsUpdated;
        _voiceService.OnScreenShareAudioStateChanged -= HandleScreenShareAudioStateChanged;

        _deviceService.OnAudioDevicesChanged -= UpdateAudioDevices;
        _deviceService.OnLocalVideoDimensionsChanged -= ApplyLocalVideoDimensions;
        _deviceService.OnLocalScreenDimensionsChanged -= ApplyLocalScreenDimensions;
        _deviceService.OnCamerasUpdated -= HandleCamerasUpdated;
        _deviceService.OnIosMicrophoneModeChanged -= HandleIosMicrophoneModeChanged;
        _deviceService.OnActiveCameraChanged -= HandleActiveCameraChanged;
        _deviceService.OnActiveMicrophoneChanged -= HandleActiveMicrophoneChanged;
        _deviceService.OnActiveSpeakerChanged -= HandleActiveSpeakerChanged;
    }
}
