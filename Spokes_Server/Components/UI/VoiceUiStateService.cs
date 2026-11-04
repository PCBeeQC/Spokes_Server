using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using MudBlazor;
using Spokes_Server.Core.Models.HR;
using Spokes_Server.Core.Services.Communication.Voice;
using Spokes_Server.Core.Services.UI;
using Spokes_Server.Resources;

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
    private readonly IStringLocalizer<AppResources>? _loc;

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
    public bool HasLoadedVoiceSettings { get; private set; }

    public bool IsCallStageVisible { get; private set; }
    public bool IsInAppPipMinimized { get; private set; }
    public bool IsOsPipActive { get; private set; }
    public bool IsNativeMobilePipMode { get; private set; }
    public string? FocusedParticipantId { get; private set; }
    public bool IsGridLayoutForcedByUser { get; private set; }

    private bool _prevShouldAutoFaceTime;
    private int _prevCameraCount;
    private HashSet<string> _prevScreenSharers = new(StringComparer.Ordinal);
    private VoicePipMetadataPayload? _lastPipMetadataPayload;

    public bool ShouldShowInAppPip =>
        _voiceService.IsInVoiceCall &&
        (IsNativeMobilePipMode || (!IsCallStageVisible && !IsOsPipActive));

    public bool IsInAppPipCollapsedPill =>
        ShouldShowInAppPip && IsInAppPipMinimized && !IsNativeMobilePipMode;

    public event Action? OnPipStateChanged;
    public event Action? OnReturnToCallStageRequested;

    public VoiceUiStateService(
        ISnackbar snackbar,
        ISoundService soundService,
        IJSRuntime js,
        GlobalVoiceService voiceService,
        IVoiceJsInteropService jsInterop,
        IVoiceDeviceService deviceService,
        ILogger<VoiceUiStateService> logger,
        IStringLocalizer<AppResources>? loc = null)
    {
        _snackbar = snackbar ?? throw new ArgumentNullException(nameof(snackbar));
        _soundService = soundService ?? throw new ArgumentNullException(nameof(soundService));
        _js = js ?? throw new ArgumentNullException(nameof(js));
        _voiceService = voiceService ?? throw new ArgumentNullException(nameof(voiceService));
        _jsInterop = jsInterop ?? throw new ArgumentNullException(nameof(jsInterop));
        _deviceService = deviceService ?? throw new ArgumentNullException(nameof(deviceService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _loc = loc;

        _voiceService.OnChange += HandleVoiceServiceStateChanged;
        _voiceService.OnVoiceLogReceived += ShowVoiceLog;
        _voiceService.OnRoomLeft += HandleRoomLeft;
        _voiceService.OnVoiceSensitivityChanged += HandleVoiceSensitivityChanged;
        _voiceService.OnMasterVolumeChanged += HandleMasterVolumeChanged;
        _voiceService.OnParticipantVolumeChanged += HandleParticipantVolumeChanged;
        _voiceService.OnVoiceSettingsUpdated += HandleVoiceSettingsUpdated;
        _voiceService.OnScreenShareAudioStateChanged += HandleScreenShareAudioStateChanged;
        _voiceService.OnHardwareMuteBlockedUnmute += HandleHardwareMuteBlockedUnmute;
        _voiceService.OnOsPipStateChangedEvent += HandleOsPipStateChanged;
        _voiceService.OnNativeMobilePipChangedEvent += HandleNativeMobilePipChanged;
        _voiceService.OnNativeCallActionReceived += HandleNativeCallAction;
        _voiceService.OnPipFocusChangedEvent += HandlePipFocusChanged;

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

    private void HandleHardwareMuteBlockedUnmute()
    {
        var message = _loc?["Microphone is muted by hardware switch"].Value ?? "Microphone is muted by hardware switch";
        _snackbar.Add(message, Severity.Warning);
    }

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
        MaxVideoSendQuality = ClampCameraQuality(MaxVideoSendQuality, maxEdge);
        if (MaxVideoSendFps > 30 && fps < 50)
        {
            MaxVideoSendFps = 30;
        }
    }

    public void ApplyLocalScreenDimensions(int maxEdge, int fps)
    {
        MaxScreenHeight = maxEdge;
        MaxScreenFps = fps;
        MaxScreenShareQuality = ClampQuality(MaxScreenShareQuality, maxEdge);
    }

    private static string ClampCameraQuality(string currentQuality, int maxEdge)
    {
        if (currentQuality == "Auto")
        {
            return currentQuality;
        }

        if (currentQuality == "2160p" && maxEdge < 3000)
        {
            return maxEdge >= 1800 ? "1080p" : "Auto";
        }

        if (currentQuality == "1080p" && maxEdge < 1800)
        {
            return "Auto";
        }

        return currentQuality;
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
        IsCallStageVisible = false;
        IsInAppPipMinimized = false;
        IsOsPipActive = false;
        IsNativeMobilePipMode = false;
        FocusedParticipantId = null;
        IsGridLayoutForcedByUser = false;
        _prevShouldAutoFaceTime = false;
        _prevCameraCount = 0;
        _prevScreenSharers.Clear();
        _lastPipMetadataPayload = null;
        OnPipStateChanged?.Invoke();
    }

    public void SetCallStageVisible(bool visible)
    {
        bool changed = IsCallStageVisible != visible;
        IsCallStageVisible = visible;
        if (visible && IsInAppPipMinimized)
        {
            IsInAppPipMinimized = false;
            changed = true;
        }
        if (changed)
        {
            OnPipStateChanged?.Invoke();
        }
    }

    public void SetFocusedParticipant(string? participantId, bool userInitiated = false)
    {
        var normalized = string.IsNullOrWhiteSpace(participantId) ? null : participantId;
        if (userInitiated)
        {
            IsGridLayoutForcedByUser = normalized == null;
        }
        if (FocusedParticipantId == normalized) return;
        FocusedParticipantId = normalized;
        OnPipStateChanged?.Invoke();
    }

    public void ToggleFocusParticipant(string participantId)
    {
        if (string.IsNullOrWhiteSpace(participantId)) return;
        if (FocusedParticipantId == participantId)
        {
            SetFocusedParticipant(null, userInitiated: true);
        }
        else
        {
            SetFocusedParticipant(participantId, userInitiated: true);
        }
    }

    private void HandlePipFocusChanged(string? participantId)
    {
        SetFocusedParticipant(participantId, userInitiated: true);
    }

    private void HandleVoiceServiceStateChanged()
    {
        ReconcileStageFocusState();
    }

    private void ReconcileStageFocusState()
    {
        var prevFocused = FocusedParticipantId;

        if (!_voiceService.IsInVoiceCall)
        {
            FocusedParticipantId = null;
            IsGridLayoutForcedByUser = false;
            _prevShouldAutoFaceTime = false;
            _prevCameraCount = 0;
            _prevScreenSharers.Clear();
            if (prevFocused != FocusedParticipantId)
            {
                OnPipStateChanged?.Invoke();
            }
            return;
        }

        if (FocusedParticipantId != null && !_voiceService.CallParticipants.Contains(FocusedParticipantId))
        {
            FocusedParticipantId = null;
        }

        // 1. Clean exit when a focused screen share stops in a multi-person call
        var currentScreenSharers = _voiceService.ActiveScreenShareParticipants
            .Where(p => _voiceService.CallParticipants.Contains(p))
            .ToHashSet(StringComparer.Ordinal);
        if (_voiceService.IsScreenSharing && !string.IsNullOrEmpty(_voiceService.CurrentUserId))
        {
            currentScreenSharers.Add(_voiceService.CurrentUserId);
        }

        if (FocusedParticipantId != null
            && _voiceService.CallParticipants.Count > 2
            && _prevScreenSharers.Contains(FocusedParticipantId)
            && !currentScreenSharers.Contains(FocusedParticipantId))
        {
            FocusedParticipantId = null;
        }

        _prevScreenSharers = currentScreenSharers;

        // 2. 2-Person Call ("FaceTime") Auto-Focus
        bool isTwoPersonCall = _voiceService.CallParticipants.Count == 2;
        bool hasAnyCamera = _voiceService.CallParticipants.Any(p =>
            (p == _voiceService.CurrentUserId && (_voiceService.IsVideoOn || _voiceService.IsScreenSharing)) ||
            _voiceService.ActiveCameraParticipants.Contains(p) ||
            currentScreenSharers.Contains(p));
        bool shouldAuto = isTwoPersonCall && hasAnyCamera;

        int activeCameras = _voiceService.CallParticipants.Count(p =>
            (p == _voiceService.CurrentUserId && (_voiceService.IsVideoOn || _voiceService.IsScreenSharing)) ||
            _voiceService.ActiveCameraParticipants.Contains(p) ||
            currentScreenSharers.Contains(p));

        if (shouldAuto && !_prevShouldAutoFaceTime && !IsGridLayoutForcedByUser && FocusedParticipantId == null)
        {
            var otherPerson = _voiceService.CallParticipants.FirstOrDefault(p => p != _voiceService.CurrentUserId);
            if (otherPerson != null)
            {
                FocusedParticipantId = otherPerson;
            }
        }
        else if (!shouldAuto && _prevShouldAutoFaceTime)
        {
            if ((_voiceService.CallParticipants.Count != 2 || !hasAnyCamera) && currentScreenSharers.Count == 0)
            {
                FocusedParticipantId = null;
                IsGridLayoutForcedByUser = false;
            }
        }
        else if (!hasAnyCamera && _prevCameraCount > 0 && FocusedParticipantId != null)
        {
            FocusedParticipantId = null;
            IsGridLayoutForcedByUser = false;
        }

        _prevCameraCount = activeCameras;
        _prevShouldAutoFaceTime = shouldAuto;

        if (prevFocused != FocusedParticipantId)
        {
            OnPipStateChanged?.Invoke();
        }
    }

    public void SetInAppPipMinimized(bool minimized)
    {
        if (IsInAppPipMinimized == minimized) return;
        IsInAppPipMinimized = minimized;
        OnPipStateChanged?.Invoke();
    }

    public void RequestReturnToCallStage()
    {
        IsInAppPipMinimized = false;
        OnReturnToCallStageRequested?.Invoke();
        OnPipStateChanged?.Invoke();
    }

    public async Task<bool> ToggleOsPipAsync()
    {
        if (!_voiceService.IsInVoiceCall) return false;
        try
        {
            if (!_jsInterop.IsModuleLoaded)
            {
                await _voiceService.EnsureModuleInitializedAsync();
            }
            if (_lastPipMetadataPayload != null)
            {
                _lastPipMetadataPayload.FocusedParticipantId = FocusedParticipantId ?? string.Empty;
                await _jsInterop.SyncPipMetadataAsync(_lastPipMetadataPayload);
            }
            return await _jsInterop.ToggleOsPipAsync();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "ToggleOsPipAsync warning: {Message}", ex.Message);
            return false;
        }
    }

    public async Task<bool> IsOsPipSupportedAsync()
    {
        try
        {
            if (!_jsInterop.IsModuleLoaded)
            {
                await _voiceService.EnsureModuleInitializedAsync();
            }
            return await _jsInterop.IsOsPipSupportedAsync();
        }
        catch
        {
            return false;
        }
    }

    public async Task SyncPipMetadataAsync(VoicePipMetadataPayload payload)
    {
        _lastPipMetadataPayload = payload;
        try
        {
            if (!_jsInterop.IsModuleLoaded)
            {
                await _voiceService.EnsureModuleInitializedAsync();
            }
            if (!_jsInterop.IsModuleLoaded) return;
            await _jsInterop.SyncPipMetadataAsync(payload);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "SyncPipMetadataAsync skipped: {Message}", ex.Message);
        }
    }

    public async Task SetInAppPipActiveAsync(bool active)
    {
        if (!_jsInterop.IsModuleLoaded) return;
        try
        {
            await _jsInterop.SetInAppPipActiveAsync(active);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "SetInAppPipActiveAsync skipped: {Message}", ex.Message);
        }
    }

    private void HandleOsPipStateChanged(bool isOpen)
    {
        if (IsOsPipActive == isOpen) return;
        IsOsPipActive = isOpen;
        OnPipStateChanged?.Invoke();
    }

    private void HandleNativeMobilePipChanged(bool isInPip)
    {
        if (IsNativeMobilePipMode == isInPip) return;
        IsNativeMobilePipMode = isInPip;
        OnPipStateChanged?.Invoke();
    }

    private void HandleNativeCallAction(string action)
    {
        if (action == "toggleCamera")
        {
            _ = _voiceService.ToggleVideoAsync(MaxVideoSendQuality, MaxVideoSendFps, ActiveCameraDeviceId);
        }
        else if (action == "returnToCall")
        {
            RequestReturnToCallStage();
        }
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
            HasLoadedVoiceSettings = true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load device voice settings from localStorage");
            VoiceSettings = new();
        }
    }

    public void Dispose()
    {
        _voiceService.OnChange -= HandleVoiceServiceStateChanged;
        _voiceService.OnVoiceLogReceived -= ShowVoiceLog;
        _voiceService.OnRoomLeft -= HandleRoomLeft;
        _voiceService.OnVoiceSensitivityChanged -= HandleVoiceSensitivityChanged;
        _voiceService.OnMasterVolumeChanged -= HandleMasterVolumeChanged;
        _voiceService.OnParticipantVolumeChanged -= HandleParticipantVolumeChanged;
        _voiceService.OnVoiceSettingsUpdated -= HandleVoiceSettingsUpdated;
        _voiceService.OnScreenShareAudioStateChanged -= HandleScreenShareAudioStateChanged;
        _voiceService.OnHardwareMuteBlockedUnmute -= HandleHardwareMuteBlockedUnmute;
        _voiceService.OnOsPipStateChangedEvent -= HandleOsPipStateChanged;
        _voiceService.OnNativeMobilePipChangedEvent -= HandleNativeMobilePipChanged;
        _voiceService.OnNativeCallActionReceived -= HandleNativeCallAction;
        _voiceService.OnPipFocusChangedEvent -= HandlePipFocusChanged;

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
