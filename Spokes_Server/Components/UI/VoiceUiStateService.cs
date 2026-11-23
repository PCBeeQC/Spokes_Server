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
    private static readonly TimeSpan SpeakerHysteresis = TimeSpan.FromMilliseconds(1500);
    public string? FocusedParticipantId { get; private set; }
    public string? FullscreenParticipantId { get; private set; }
    public string? StickySpeakerId { get; private set; }
    private DateTime _stickySpeakerUntilUtc = DateTime.MinValue;
    private bool _isFocusedParticipantAutoAssigned;
    private int _prevStageParticipantCount;
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
        _voiceService.OnActiveSpeakersUpdated += HandleActiveSpeakersUpdated;
        _voiceService.OnVoiceLogReceived += ShowVoiceLog;
        _voiceService.OnRoomLeft += HandleRoomLeft;
        _voiceService.OnVoiceSensitivityChanged += HandleVoiceSensitivityChanged;
        _voiceService.OnAutoGainControlChanged += HandleAutoGainControlChanged;
        _voiceService.OnIncomingVoiceNormalizationChanged += HandleIncomingVoiceNormalizationChanged;
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
    private void HandleAutoGainControlChanged(bool enabled) => VoiceSettings.AutoGainControl = enabled;
    private void HandleIncomingVoiceNormalizationChanged(bool enabled) => VoiceSettings.IncomingVoiceNormalization = enabled;
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
        FullscreenParticipantId = null;
        StickySpeakerId = null;
        _stickySpeakerUntilUtc = DateTime.MinValue;
        _isFocusedParticipantAutoAssigned = false;
        _prevStageParticipantCount = 0;
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
        _isFocusedParticipantAutoAssigned = false;
        if (FocusedParticipantId == normalized) return;
        FocusedParticipantId = normalized;
        OnPipStateChanged?.Invoke();
    }

    public void ToggleFocusParticipant(string participantId)
    {
        if (string.IsNullOrWhiteSpace(participantId)) return;

        var activeParticipants = _voiceService.CallParticipants
            .Where(id => !string.IsNullOrEmpty(id) && !LiveKitService.IsSyntheticParticipantIdentity(id))
            .ToList();

        // <= 2 participants always stay in FaceTime layout:
        // - Solo (1 participant): full-bleed self, tapping is a no-op.
        // - 2 participants: tapping the corner tile swaps focus to that participant;
        //   tapping the already-focused full-bleed tile keeps FaceTime layout intact.
        if (activeParticipants.Count <= 2)
        {
            if (activeParticipants.Count <= 1)
            {
                return;
            }

            var currentFocused = !string.IsNullOrEmpty(FocusedParticipantId) && activeParticipants.Contains(FocusedParticipantId)
                ? FocusedParticipantId
                : (activeParticipants.FirstOrDefault(p => !string.Equals(p, _voiceService.CurrentUserId, StringComparison.Ordinal))
                    ?? activeParticipants[0]);

            if (!string.Equals(currentFocused, participantId, StringComparison.Ordinal))
            {
                SetFocusedParticipant(participantId, userInitiated: true);
            }
            return;
        }

        if (FocusedParticipantId == participantId)
        {
            SetFocusedParticipant(null, userInitiated: true);
        }
        else
        {
            SetFocusedParticipant(participantId, userInitiated: true);
        }
    }

    public void SetFullscreenParticipant(string? participantId)
    {
        var normalized = string.IsNullOrWhiteSpace(participantId) ? null : participantId;
        if (normalized != null && string.Equals(normalized, _voiceService.CurrentUserId, StringComparison.Ordinal))
        {
            return;
        }
        if (FullscreenParticipantId == normalized) return;
        var prev = FullscreenParticipantId;
        FullscreenParticipantId = normalized;
        if (normalized == null && prev != null)
        {
            _ = _jsInterop.ExitParticipantFullscreenAsync(goBack: false);
        }
        OnPipStateChanged?.Invoke();
    }

    public async Task EnterParticipantFullscreenAsync<T>(string participantId, DotNetObjectReference<T> dotNetHelper) where T : class
    {
        if (string.IsNullOrWhiteSpace(participantId) || string.Equals(participantId, _voiceService.CurrentUserId, StringComparison.Ordinal))
        {
            return;
        }
        SetFullscreenParticipant(participantId);
        try
        {
            if (!_jsInterop.IsModuleLoaded)
            {
                await _voiceService.EnsureModuleInitializedAsync();
            }
            await _jsInterop.EnterParticipantFullscreenAsync(participantId, dotNetHelper);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "EnterParticipantFullscreenAsync warning: {Message}", ex.Message);
        }
    }

    public async Task SetParticipantFullscreenRotationAsync(int rotationDeg)
    {
        try
        {
            if (_jsInterop.IsModuleLoaded)
            {
                await _jsInterop.SetParticipantFullscreenRotationAsync(rotationDeg);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "SetParticipantFullscreenRotationAsync warning: {Message}", ex.Message);
        }
    }

    public async Task ResetParticipantFullscreenZoomAsync()
    {
        try
        {
            if (_jsInterop.IsModuleLoaded)
            {
                await _jsInterop.ResetParticipantFullscreenZoomAsync();
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "ResetParticipantFullscreenZoomAsync warning: {Message}", ex.Message);
        }
    }

    public async Task ExitParticipantFullscreenAsync(bool goBack = true)
    {
        FullscreenParticipantId = null;
        OnPipStateChanged?.Invoke();
        try
        {
            if (_jsInterop.IsModuleLoaded)
            {
                await _jsInterop.ExitParticipantFullscreenAsync(goBack);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "ExitParticipantFullscreenAsync warning: {Message}", ex.Message);
        }
    }

    private void HandlePipFocusChanged(string? participantId)
    {
        SetFocusedParticipant(participantId, userInitiated: true);
    }

    private void HandleVoiceServiceStateChanged()
    {
        UpdateStickySpeaker();
        ReconcileStageFocusState();
    }

    private void HandleActiveSpeakersUpdated()
    {
        var previousSticky = StickySpeakerId;
        UpdateStickySpeaker();
        if (!string.Equals(previousSticky, StickySpeakerId, StringComparison.Ordinal))
        {
            OnPipStateChanged?.Invoke();
        }
    }

    private void UpdateStickySpeaker()
    {
        if (!_voiceService.IsInVoiceCall)
        {
            StickySpeakerId = null;
            _stickySpeakerUntilUtc = DateTime.MinValue;
            return;
        }

        var now = DateTime.UtcNow;
        var remoteSpeaking = _voiceService.ActiveSpeakers
            .FirstOrDefault(id => id != _voiceService.CurrentUserId && !LiveKitService.IsSyntheticParticipantIdentity(id));

        if (!string.IsNullOrEmpty(remoteSpeaking))
        {
            if (!string.IsNullOrEmpty(StickySpeakerId) && _voiceService.ActiveSpeakers.Contains(StickySpeakerId))
            {
                _stickySpeakerUntilUtc = now + SpeakerHysteresis;
            }
            else if (string.IsNullOrEmpty(StickySpeakerId) || now >= _stickySpeakerUntilUtc || !_voiceService.CallParticipants.Contains(StickySpeakerId))
            {
                StickySpeakerId = remoteSpeaking;
                _stickySpeakerUntilUtc = now + SpeakerHysteresis;
            }
        }
        else if (!string.IsNullOrEmpty(_voiceService.CurrentUserId) && _voiceService.ActiveSpeakers.Contains(_voiceService.CurrentUserId))
        {
            if (string.IsNullOrEmpty(StickySpeakerId) || now >= _stickySpeakerUntilUtc || !_voiceService.CallParticipants.Contains(StickySpeakerId))
            {
                StickySpeakerId = _voiceService.CurrentUserId;
                _stickySpeakerUntilUtc = now + SpeakerHysteresis;
            }
        }
    }

    public VoicePipPresentationState ComputePipPresentationState(bool isNativeMobileClient = false, IReadOnlySet<string>? verifiedParticipants = null)
    {
        var localUserId = _voiceService.CurrentUserId ?? string.Empty;
        bool isParticipantIncluded(string id) =>
            !string.IsNullOrEmpty(id) &&
            !LiveKitService.IsSyntheticParticipantIdentity(id) &&
            (string.Equals(id, localUserId, StringComparison.Ordinal) || verifiedParticipants == null || verifiedParticipants.Contains(id));

        var participants = _voiceService.CallParticipants
            .Where(isParticipantIncluded)
            .ToList();
        int participantCount = participants.Count;

        bool isSingleStagePip = IsNativeMobilePipMode || isNativeMobileClient || _voiceService.IsNativePlatform;
        string? focusedId = !string.IsNullOrEmpty(FocusedParticipantId) && participants.Contains(FocusedParticipantId)
            ? FocusedParticipantId
            : null;
        string? stickyId = !string.IsNullOrEmpty(StickySpeakerId) && participants.Contains(StickySpeakerId)
            ? StickySpeakerId
            : null;

        bool canUseGrid = !isSingleStagePip && participantCount > 1;
        bool isGridMode = string.IsNullOrEmpty(focusedId) && canUseGrid;

        bool hasCamera(string id) => string.Equals(id, localUserId, StringComparison.Ordinal)
            ? _voiceService.IsVideoOn
            : _voiceService.ActiveCameraParticipants.Contains(id);

        bool hasScreen(string id) => string.Equals(id, localUserId, StringComparison.Ordinal)
            ? _voiceService.IsScreenSharing
            : _voiceService.ActiveScreenShareParticipants.Contains(id);

        string primaryVideoParticipantId = string.Empty;
        string primaryVideoSource = string.Empty;

        if (!string.IsNullOrEmpty(focusedId) && !IsNativeMobilePipMode)
        {
            if (hasScreen(focusedId))
            {
                primaryVideoParticipantId = focusedId;
                primaryVideoSource = "screenshare";
            }
            else if (hasCamera(focusedId))
            {
                primaryVideoParticipantId = focusedId;
                primaryVideoSource = "camera";
            }
        }

        if (string.IsNullOrEmpty(primaryVideoParticipantId) && (string.IsNullOrEmpty(focusedId) || isSingleStagePip))
        {
            var remoteScreen = participants.FirstOrDefault(id => id != localUserId && hasScreen(id));
            if (remoteScreen != null)
            {
                primaryVideoParticipantId = remoteScreen;
                primaryVideoSource = "screenshare";
            }
            else if (!string.IsNullOrEmpty(stickyId) && stickyId != localUserId && hasCamera(stickyId))
            {
                primaryVideoParticipantId = stickyId;
                primaryVideoSource = "camera";
            }
            else
            {
                var remoteCam = participants.FirstOrDefault(id => id != localUserId && hasCamera(id));
                if (remoteCam != null)
                {
                    primaryVideoParticipantId = remoteCam;
                    primaryVideoSource = "camera";
                }
                else if (!string.IsNullOrEmpty(localUserId) && hasScreen(localUserId))
                {
                    primaryVideoParticipantId = localUserId;
                    primaryVideoSource = "screenshare";
                }
                else if ((isSingleStagePip || participantCount <= 1) && !string.IsNullOrEmpty(localUserId) && hasCamera(localUserId))
                {
                    primaryVideoParticipantId = localUserId;
                    primaryVideoSource = "camera";
                }
            }
        }

        bool hasPrimaryVideo = !string.IsNullOrEmpty(primaryVideoParticipantId);

        string? displayParticipantId = (!string.IsNullOrEmpty(focusedId) && !IsNativeMobilePipMode)
            ? focusedId
            : (hasPrimaryVideo
                ? primaryVideoParticipantId
                : (stickyId
                    ?? participants.FirstOrDefault(id => id != localUserId)
                    ?? (string.IsNullOrEmpty(localUserId) ? null : localUserId)));

        string primaryTargetId = displayParticipantId ?? string.Empty;

        bool useFaceTimeSelfCorner = _voiceService.IsVideoOn
            && hasPrimaryVideo
            && !string.Equals(primaryTargetId, localUserId, StringComparison.Ordinal)
            && (isSingleStagePip || (!IsOsPipActive && participantCount == 2));

        bool hasFilmstrip = !isGridMode
            && !isSingleStagePip
            && (participantCount > 2 || (participantCount == 2 && !useFaceTimeSelfCorner));

        string modeClass = isGridMode ? "mode-grid" : (hasPrimaryVideo ? "mode-video" : "mode-avatar");

        return new VoicePipPresentationState(
            ParticipantCount: participantCount,
            FocusedParticipantId: focusedId,
            StickySpeakerId: stickyId,
            DisplayParticipantId: displayParticipantId,
            CanUseGrid: canUseGrid,
            IsGridMode: isGridMode,
            HasPrimaryVideo: hasPrimaryVideo,
            PrimaryTargetId: primaryTargetId,
            PrimaryVideoSource: primaryVideoSource,
            UseFaceTimeSelfCorner: useFaceTimeSelfCorner,
            HasFilmstrip: hasFilmstrip,
            ModeClass: modeClass);
    }

    public VoiceStagePresentationState ComputeStagePresentationState(IReadOnlySet<string>? verifiedParticipants = null, string? ringingCalleeId = null)
    {
        var localUserId = _voiceService.CurrentUserId ?? string.Empty;
        var participants = _voiceService.CallParticipants
            .Where(id => !string.IsNullOrEmpty(id)
                && !LiveKitService.IsSyntheticParticipantIdentity(id)
                && (string.Equals(id, localUserId, StringComparison.Ordinal)
                    || verifiedParticipants == null
                    || verifiedParticipants.Contains(id)))
            .ToList();

        bool hasRingingCallee = !string.IsNullOrWhiteSpace(ringingCalleeId)
            && !participants.Contains(ringingCalleeId);
        int visualCount = participants.Count + (hasRingingCallee ? 1 : 0);

        int activeVideoCount = participants.Count(p =>
            (string.Equals(p, localUserId, StringComparison.Ordinal) && (_voiceService.IsVideoOn || _voiceService.IsScreenSharing)) ||
            _voiceService.ActiveCameraParticipants.Contains(p) ||
            _voiceService.ActiveScreenShareParticipants.Contains(p));

        if (visualCount == 0)
        {
            return new VoiceStagePresentationState(
                VisualParticipantCount: 0,
                IsFaceTimeLayout: false,
                FocusedParticipantId: null,
                ActiveVideoCount: activeVideoCount);
        }

        if (visualCount <= 2)
        {
            string? effectiveFocusedId;
            if (hasRingingCallee)
            {
                effectiveFocusedId = !string.IsNullOrEmpty(FocusedParticipantId) && participants.Contains(FocusedParticipantId)
                    ? FocusedParticipantId
                    : ringingCalleeId;
            }
            else if (participants.Count == 1)
            {
                effectiveFocusedId = participants[0];
            }
            else
            {
                effectiveFocusedId = !string.IsNullOrEmpty(FocusedParticipantId) && participants.Contains(FocusedParticipantId)
                    ? FocusedParticipantId
                    : (participants.FirstOrDefault(p => !string.Equals(p, localUserId, StringComparison.Ordinal))
                        ?? participants[0]);
            }

            return new VoiceStagePresentationState(
                VisualParticipantCount: visualCount,
                IsFaceTimeLayout: true,
                FocusedParticipantId: effectiveFocusedId,
                ActiveVideoCount: activeVideoCount);
        }

        string? gridFocusedId = !string.IsNullOrEmpty(FocusedParticipantId) && participants.Contains(FocusedParticipantId)
            ? FocusedParticipantId
            : null;

        return new VoiceStagePresentationState(
            VisualParticipantCount: visualCount,
            IsFaceTimeLayout: false,
            FocusedParticipantId: gridFocusedId,
            ActiveVideoCount: activeVideoCount);
    }

    public void TogglePipGridOrFocusLayout()
    {
        if (!string.IsNullOrEmpty(FocusedParticipantId))
        {
            SetFocusedParticipant(null, userInitiated: true);
            return;
        }

        var nextFocus = _voiceService.ActiveScreenShareParticipants.FirstOrDefault(id => id != _voiceService.CurrentUserId)
            ?? (_voiceService.IsScreenSharing ? _voiceService.CurrentUserId : null)
            ?? StickySpeakerId
            ?? _voiceService.CallParticipants.FirstOrDefault(id => id != _voiceService.CurrentUserId && !LiveKitService.IsSyntheticParticipantIdentity(id))
            ?? _voiceService.CurrentUserId;
        SetFocusedParticipant(nextFocus, userInitiated: true);
    }

    private void ReconcileStageFocusState()
    {
        var prevFocused = FocusedParticipantId;
        var prevFullscreen = FullscreenParticipantId;

        if (!_voiceService.IsInVoiceCall)
        {
            FocusedParticipantId = null;
            FullscreenParticipantId = null;
            _isFocusedParticipantAutoAssigned = false;
            _prevStageParticipantCount = 0;
            _prevScreenSharers.Clear();
            if (prevFocused != FocusedParticipantId || prevFullscreen != FullscreenParticipantId)
            {
                OnPipStateChanged?.Invoke();
            }
            return;
        }

        var activeParticipants = _voiceService.CallParticipants
            .Where(id => !string.IsNullOrEmpty(id) && !LiveKitService.IsSyntheticParticipantIdentity(id))
            .ToList();

        if (FocusedParticipantId != null && !activeParticipants.Contains(FocusedParticipantId))
        {
            FocusedParticipantId = null;
            _isFocusedParticipantAutoAssigned = false;
        }

        // 1. Clean exit when a focused screen share stops in a multi-person call
        var currentScreenSharers = _voiceService.ActiveScreenShareParticipants
            .Where(p => activeParticipants.Contains(p))
            .ToHashSet(StringComparer.Ordinal);
        if (_voiceService.IsScreenSharing && !string.IsNullOrEmpty(_voiceService.CurrentUserId))
        {
            currentScreenSharers.Add(_voiceService.CurrentUserId);
        }

        if (FullscreenParticipantId != null)
        {
            bool stillHasStream = activeParticipants.Contains(FullscreenParticipantId)
                && !string.Equals(FullscreenParticipantId, _voiceService.CurrentUserId, StringComparison.Ordinal)
                && (_voiceService.ActiveCameraParticipants.Contains(FullscreenParticipantId)
                    || currentScreenSharers.Contains(FullscreenParticipantId));
            if (!stillHasStream)
            {
                FullscreenParticipantId = null;
                _ = _jsInterop.ExitParticipantFullscreenAsync(goBack: false);
            }
        }

        if (FocusedParticipantId != null
            && activeParticipants.Count > 2
            && _prevScreenSharers.Contains(FocusedParticipantId)
            && !currentScreenSharers.Contains(FocusedParticipantId))
        {
            FocusedParticipantId = null;
            _isFocusedParticipantAutoAssigned = false;
        }

        _prevScreenSharers = currentScreenSharers;

        // 2. Canonical <= 2 FaceTime vs > 2 Grid stage focus reconciliation
        int count = activeParticipants.Count;
        if (count <= 1)
        {
            FocusedParticipantId = null;
            _isFocusedParticipantAutoAssigned = false;
        }
        else if (count == 2)
        {
            if (FocusedParticipantId == null)
            {
                var otherPerson = activeParticipants.FirstOrDefault(p => !string.Equals(p, _voiceService.CurrentUserId, StringComparison.Ordinal))
                    ?? activeParticipants[0];
                FocusedParticipantId = otherPerson;
                _isFocusedParticipantAutoAssigned = true;
            }
        }
        else if (_prevStageParticipantCount <= 2)
        {
            // Transitioned from <= 2 FaceTime into > 2 Grid: clear FaceTime focus unless actively screen-sharing
            if (FocusedParticipantId == null || !currentScreenSharers.Contains(FocusedParticipantId))
            {
                FocusedParticipantId = null;
                _isFocusedParticipantAutoAssigned = false;
            }
        }
        else if (_isFocusedParticipantAutoAssigned && currentScreenSharers.Count == 0)
        {
            FocusedParticipantId = null;
            _isFocusedParticipantAutoAssigned = false;
        }

        _prevStageParticipantCount = count;

        if (prevFocused != FocusedParticipantId || prevFullscreen != FullscreenParticipantId)
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
            var opened = await _jsInterop.ToggleOsPipAsync();
            if (opened)
            {
                await _jsInterop.SyncSpeakingParticipantsAsync(_voiceService.CurrentChannelId, _voiceService.ActiveSpeakers.ToArray());
            }
            return opened;
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

    public async Task SyncSpeakingParticipantsAsync(string? channelId, string[] speakerIds)
    {
        if (!_jsInterop.IsModuleLoaded) return;
        try
        {
            await _jsInterop.SyncSpeakingParticipantsAsync(channelId, speakerIds ?? Array.Empty<string>());
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "SyncSpeakingParticipantsAsync skipped: {Message}", ex.Message);
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
        else if (action == "toggleScreenShare")
        {
            _ = _voiceService.ToggleScreenShareAsync(MaxScreenShareQuality, MaxScreenShareFps, ScreenShareMode, IsScreenShareAudioEnabled);
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

    public async Task PlayTestSpeakerSoundAsync(string? deviceId = null)
    {
        var targetDevice = !string.IsNullOrEmpty(deviceId) ? deviceId : ActiveSpeakerDeviceId;

        try
        {
            if (!_jsInterop.IsModuleLoaded)
            {
                await _voiceService.EnsureModuleInitializedAsync();
            }

            if (_jsInterop.IsModuleLoaded)
            {
                await _jsInterop.PlayTestSpeakerSoundAsync(targetDevice);
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
                ? System.Text.Json.JsonSerializer.Deserialize<VoiceUserSettings>(json, VoiceUserSettings.JsonOptions) ?? new()
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
        _voiceService.OnActiveSpeakersUpdated -= HandleActiveSpeakersUpdated;
        _voiceService.OnVoiceLogReceived -= ShowVoiceLog;
        _voiceService.OnRoomLeft -= HandleRoomLeft;
        _voiceService.OnVoiceSensitivityChanged -= HandleVoiceSensitivityChanged;
        _voiceService.OnAutoGainControlChanged -= HandleAutoGainControlChanged;
        _voiceService.OnIncomingVoiceNormalizationChanged -= HandleIncomingVoiceNormalizationChanged;
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

    /// <summary>
    /// Resolves an audio device ID to its human-readable label for MudSelect ToStringFunc rendering.
    /// Prevents MudBlazor from displaying raw Chromium/OS device hash IDs before child items register.
    /// </summary>
    public static string ResolveAudioDeviceLabel(IReadOnlyList<AudioDevice> devices, string? deviceId) =>
        ResolveDeviceLabelCore(devices, static d => d.DeviceId, static d => d.Label, deviceId);

    /// <summary>
    /// Resolves a camera device ID to its human-readable label for MudSelect ToStringFunc rendering.
    /// </summary>
    public static string ResolveCameraDeviceLabel(IReadOnlyList<CameraDevice> devices, string? deviceId) =>
        ResolveDeviceLabelCore(devices, static d => d.DeviceId, static d => d.Label, deviceId);

    private static string ResolveDeviceLabelCore<T>(
        IReadOnlyList<T> devices,
        Func<T, string> getId,
        Func<T, string> getLabel,
        string? deviceId)
    {
        if (devices.Count > 0)
        {
            if (!string.IsNullOrEmpty(deviceId))
            {
                for (int i = 0; i < devices.Count; i++)
                {
                    if (string.Equals(getId(devices[i]), deviceId, StringComparison.Ordinal))
                    {
                        var label = getLabel(devices[i]);
                        if (!string.IsNullOrWhiteSpace(label))
                        {
                            return label;
                        }
                    }
                }
            }

            if (string.IsNullOrWhiteSpace(deviceId) || string.Equals(deviceId, "default", StringComparison.Ordinal))
            {
                for (int i = 0; i < devices.Count; i++)
                {
                    if (string.Equals(getId(devices[i]), "default", StringComparison.Ordinal))
                    {
                        var label = getLabel(devices[i]);
                        if (!string.IsNullOrWhiteSpace(label))
                        {
                            return label;
                        }
                    }
                }
            }

            var firstLabel = getLabel(devices[0]);
            if (!string.IsNullOrWhiteSpace(firstLabel))
            {
                return firstLabel;
            }
        }

        return "System Default";
    }
}
