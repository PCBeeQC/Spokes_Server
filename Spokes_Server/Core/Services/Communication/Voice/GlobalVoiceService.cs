using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.JSInterop;
using Spokes_Server.Core.Helpers;
using Spokes_Server.Core.Models.HR;
using Spokes_Server.Core.Services.Communication.Chat;

namespace Spokes_Server.Core.Services.Communication.Voice;

public class GlobalVoiceService : IAsyncDisposable
{
    private readonly IVoiceJsInteropService _jsInterop;
    private readonly VoiceChannelService _voiceChannelService;
    private readonly ChatStateService _chatState;
    private readonly ILogger<GlobalVoiceService> _logger;
    private readonly IVoiceDeviceService _deviceService;
    private int _clientLogCount;
    private DateTime _lastClientLogReset = DateTime.UtcNow;

    private DotNetObjectReference<GlobalVoiceService>? _objRef;

    public event Action? OnChange;
    public event Action<string, string>? OnVoiceLogReceived;
    public event Action? OnRoomJoined;
    public event Action? OnRoomLeft;
    public event Action<string>? OnParticipantJoined;
    public event Action<string>? OnParticipantLeft;
    public event Action<double>? OnVoiceSensitivityChanged;
    public event Action<double>? OnMasterVolumeChanged;
    public event Action<string, double>? OnParticipantVolumeChanged;
    public event Action<VoiceUserSettings>? OnVoiceSettingsUpdated;

    public bool IsInVoiceCall { get; private set; }
    public bool IsConnecting { get; private set; }
    public bool RequiresIntegrityCheck { get; private set; }
    public string? CurrentChannelId { get; private set; }
    public string? CurrentUserId { get; private set; }
    public bool IsMuted { get; private set; }
    public bool IsVideoOn { get; private set; }
    public bool IsScreenSharing { get; private set; }
    public string CurrentNoiseSuppression { get; private set; } = "webrtc";

    public bool IsAndroid { get; private set; }
    public bool IsIOS { get; private set; }
    public bool IsNativePlatform { get; private set; }

    public ConcurrentHashSet<string> CallParticipants { get; } = new();
    public ConcurrentHashSet<string> ActiveSpeakers { get; } = new();
    public ConcurrentHashSet<string> MutedParticipants { get; } = new();
    public ConcurrentHashSet<string> RemoteScreenAudioParticipants { get; } = new();
    public ConcurrentHashSet<string> MutedRemoteScreenAudioParticipants { get; } = new();

    public GlobalVoiceService(
        IVoiceJsInteropService jsInterop,
        VoiceChannelService voiceChannelService,
        ChatStateService chatState,
        ILogger<GlobalVoiceService> logger,
        IVoiceDeviceService? deviceService = null)
    {
        _jsInterop = jsInterop ?? throw new ArgumentNullException(nameof(jsInterop));
        _voiceChannelService = voiceChannelService ?? throw new ArgumentNullException(nameof(voiceChannelService));
        _chatState = chatState ?? throw new ArgumentNullException(nameof(chatState));
        _logger = logger ?? NullLogger<GlobalVoiceService>.Instance;
        _deviceService = deviceService ?? new VoiceDeviceService(_jsInterop, _logger);

        _chatState.VoiceMemberJoined += OnServerVoiceMemberJoined;
        _chatState.VoiceMemberLeft += OnServerVoiceMemberLeft;
        _chatState.VoiceUserMutedChanged += OnServerVoiceUserMutedChanged;
    }

    private void OnServerVoiceUserMutedChanged(string channelId, string userId, bool isMuted)
    {
        if (IsInVoiceCall && CurrentChannelId == channelId && !string.IsNullOrEmpty(userId))
        {
            bool changed = isMuted ? MutedParticipants.Add(userId) : MutedParticipants.Remove(userId);
            if (isMuted) ActiveSpeakers.Remove(userId);
            if (changed)
            {
                NotifyStateChanged();
            }
        }
    }

    private void OnServerVoiceMemberJoined(string channelId, string userId)
    {
        if (IsInVoiceCall && CurrentChannelId == channelId && !string.IsNullOrEmpty(userId) && userId != CurrentUserId && !userId.StartsWith("bot:", StringComparison.OrdinalIgnoreCase))
        {
            if (CallParticipants.Add(userId))
            {
                NotifyStateChanged();
            }
        }
    }

    private void OnServerVoiceMemberLeft(string channelId, string userId)
    {
        if (IsInVoiceCall && CurrentChannelId == channelId && !string.IsNullOrEmpty(userId))
        {
            if (userId != CurrentUserId)
            {
                bool removed = CallParticipants.Remove(userId);
                ActiveSpeakers.Remove(userId);
                MutedParticipants.Remove(userId);
                _deviceService.RemoveParticipantVideo(userId);
                if (removed)
                {
                    NotifyStateChanged();
                }
            }
        }
    }

    public async Task EnsureModuleInitializedAsync() => await InitializeModuleAsync();

    private async Task InitializeModuleAsync()
    {
        if (_jsInterop.IsModuleLoaded) return;
        _objRef = DotNetObjectReference.Create(this);
        try
        {
            await _jsInterop.InitializeAsync(_objRef);
            IsIOS = await _jsInterop.IsAppleMobileDeviceAsync();
            IsAndroid = await _jsInterop.IsAndroidDeviceAsync();
            IsNativePlatform = await _jsInterop.IsNativePlatformAsync();
        }
        catch (Exception e)
        {
            _logger.LogError(e, "GlobalVoiceService JS load failed: {Message}", e.Message);
            throw;
        }
    }

    public void ApplyMockConnectedState(string channelId, string userId, IEnumerable<string> mockParticipants)
    {
        CurrentChannelId = channelId;
        CurrentUserId = userId;
        IsInVoiceCall = true;
        IsConnecting = false;
        CallParticipants.Clear();
        foreach (var p in mockParticipants)
        {
            CallParticipants.Add(p);
        }
        NotifyStateChanged();
    }

    public void ApplyMockDisconnectedState()
    {
        CurrentChannelId = null;
        CurrentUserId = null;
        IsInVoiceCall = false;
        IsConnecting = false;
        CallParticipants.Clear();
        ActiveSpeakers.Clear();
        NotifyStateChanged();
    }

    public async Task ConnectAsync(VoiceConnectionOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (IsConnecting)
        {
            _logger.LogInformation("ConnectAsync called while already connecting. Ignoring duplicate attempt.");
            return;
        }

        if (IsInVoiceCall && CurrentChannelId == options.ChannelId)
        {
            _logger.LogInformation("ConnectAsync called while already connected to channel {ChannelId}. Ignoring duplicate attempt.", options.ChannelId);
            return;
        }

        IsConnecting = true;
        RequiresIntegrityCheck = false;
        NotifyStateChanged();

        try
        {
            if (!_jsInterop.IsModuleLoaded) await InitializeModuleAsync();

            var noiseSuppression = options.NoiseSuppression;
            if (IsIOS && noiseSuppression != "none")
            {
                noiseSuppression = "webrtc";
            }

            CurrentNoiseSuppression = noiseSuppression;

            if (IsInVoiceCall)
            {
                await DisconnectAsync();
            }

            CurrentChannelId = options.ChannelId;
            CurrentUserId = options.UserId;
            CallParticipants.Clear();
            ActiveSpeakers.Clear();
            MutedParticipants.Clear();
            CallParticipants.Add(options.UserId);

            var serverParticipants = _chatState.GetVoiceParticipants(options.ChannelId);
            foreach (var spId in serverParticipants)
            {
                CallParticipants.Add(spId);
            }

            var serverMuted = _chatState.GetMutedUsers(options.ChannelId);
            foreach (var smId in serverMuted)
            {
                MutedParticipants.Add(smId);
            }

            IsMuted = false;
            IsVideoOn = false;
            IsScreenSharing = false;

            NotifyStateChanged();

            if (_objRef == null)
            {
                _objRef = DotNetObjectReference.Create(this);
            }

            IsInVoiceCall = await _jsInterop.ConnectToVoiceAsync(options.Token, options.Url, _objRef, options.AudioBitrateKbps, CurrentNoiseSuppression);

            if (IsInVoiceCall)
            {
                await _voiceChannelService.JoinVoiceAsync(options.ChannelId, options.UserId);
                _ = _voiceChannelService.SetUserMutedAsync(options.ChannelId, options.UserId, IsMuted);
                if (options.VoiceSettings != null)
                {
                    try
                    {
                        var settingsJson = System.Text.Json.JsonSerializer.Serialize(options.VoiceSettings);
                        await _jsInterop.InitVoiceSettingsAsync(settingsJson);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to initialize voice settings in JS");
                    }
                }

                if (IsIOS)
                {
                    try
                    {
                        await _deviceService.GetIosMicrophoneModeAsync(IsIOS);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to query iOS microphone mode on join");
                    }
                }
            }
        }
        finally
        {
            IsConnecting = false;
            NotifyStateChanged();
        }
    }

    public async Task DisconnectAsync()
    {
        var uid = CurrentUserId;
        var cid = CurrentChannelId;
        var wasInCall = IsInVoiceCall;

        ResetState();
        NotifyStateChanged();

        await _jsInterop.DisconnectFromVoiceAsync();

        if (!string.IsNullOrEmpty(uid) && !string.IsNullOrEmpty(cid))
        {
            try
            {
                await _voiceChannelService.LeaveVoiceAsync(cid, uid);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[VoiceService] Failed to leave voice channel: {Message}", ex.Message);
            }
        }

        if (wasInCall)
        {
            OnRoomLeft?.Invoke();
        }
    }

    public async Task ToggleMuteAsync()
    {
        try
        {
            bool newMuteState = !IsMuted;
            IsMuted = newMuteState;
            if (newMuteState)
            {
                if (CurrentUserId != null)
                {
                    ActiveSpeakers.Remove(CurrentUserId);
                    MutedParticipants.Add(CurrentUserId);
                }
                _wasISpeaking = false;
                if (CurrentChannelId != null && CurrentUserId != null)
                {
                    _ = _voiceChannelService.SetUserSpeakingAsync(CurrentChannelId, CurrentUserId, false);
                    _ = _voiceChannelService.SetUserMutedAsync(CurrentChannelId, CurrentUserId, true);
                }
            }
            else
            {
                if (CurrentUserId != null)
                {
                    MutedParticipants.Remove(CurrentUserId);
                }
                if (CurrentChannelId != null && CurrentUserId != null)
                {
                    _ = _voiceChannelService.SetUserMutedAsync(CurrentChannelId, CurrentUserId, false);
                }
            }
            NotifyStateChanged();

            if (_jsInterop.IsModuleLoaded)
            {
                await _jsInterop.SetMicrophoneEnabledAsync(!newMuteState);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Voice] ToggleMute failed: {Message}", ex.Message);
        }
        NotifyStateChanged();
    }

    public async Task ToggleVideoAsync(string quality = "Auto", int fps = 30, string activeCameraDeviceId = "")
    {
        try
        {
            bool newVideoState = !IsVideoOn;
            if (newVideoState)
            {
                var result = await _jsInterop.SetCameraEnabledAsync(newVideoState, quality, fps, activeCameraDeviceId);
                var cameras = result?.Cameras?.Take(100).ToList() ?? [];
                var activeId = result?.ActiveCameraDeviceId ?? "";
                _deviceService.HandleCamerasUpdated(cameras, activeId);
            }
            else
            {
                await _jsInterop.SetCameraEnabledAsync(newVideoState, quality, fps, activeCameraDeviceId);
                _deviceService.HandleCamerasUpdated([], "");
            }
            IsVideoOn = newVideoState;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Voice] ToggleVideo failed: {Message}", ex.Message);
            IsVideoOn = false;
        }
        NotifyStateChanged();
    }

    [JSInvokable]
    public void OnAudioDevicesUpdated(List<AudioDevice> mics, string activeMicId, List<AudioDevice> speakers, string activeSpeakerId)
    {
        _deviceService.HandleAudioDevicesUpdated(mics, activeMicId, speakers, activeSpeakerId);
        NotifyStateChanged();
    }

    public async Task SetNoiseSuppressionAsync(string type)
    {
        if (CurrentNoiseSuppression == type) return;
        CurrentNoiseSuppression = type;
        if (_jsInterop.IsModuleLoaded)
        {
            await _jsInterop.SetNoiseSuppressionTypeAsync(type);
        }
        
        if (!IsMuted && IsInVoiceCall)
        {
            await ToggleMuteAsync();
            await ToggleMuteAsync();
        }
        NotifyStateChanged();
    }

    public async Task SetMasterVolumeAsync(double volumePercent)
    {
        OnMasterVolumeChanged?.Invoke(volumePercent);
        if (_jsInterop.IsModuleLoaded && IsInVoiceCall)
        {
            try
            {
                await _jsInterop.SetMasterVolumeAsync(volumePercent);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to set master volume in JS");
            }
        }
        NotifyStateChanged();
    }

    public async Task SetParticipantVolumeAsync(string participantId, double volumePercent)
    {
        if (string.IsNullOrEmpty(participantId)) return;
        OnParticipantVolumeChanged?.Invoke(participantId, volumePercent);
        if (_jsInterop.IsModuleLoaded && IsInVoiceCall)
        {
            try
            {
                await _jsInterop.SetParticipantVolumeAsync(participantId, volumePercent);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to set participant volume in JS");
            }
        }
        NotifyStateChanged();
    }

    public async Task SetVoiceSensitivityAsync(double sensitivity)
    {
        var clamped = Math.Clamp(sensitivity, 0.0, 100.0);
        OnVoiceSensitivityChanged?.Invoke(clamped);
        if (_jsInterop.IsModuleLoaded && IsInVoiceCall)
        {
            try
            {
                await _jsInterop.SetVoiceSensitivityAsync(clamped);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to set voice sensitivity in JS");
            }
        }
        NotifyStateChanged();
    }

    public async Task UpdateVoiceSettingsAsync(VoiceUserSettings settings)
    {
        if (_jsInterop.IsModuleLoaded && IsInVoiceCall && settings != null)
        {
            try
            {
                var json = System.Text.Json.JsonSerializer.Serialize(settings);
                await _jsInterop.InitVoiceSettingsAsync(json);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to update voice settings in JS");
            }
        }
        if (settings != null)
        {
            OnVoiceSettingsUpdated?.Invoke(settings);
        }
        NotifyStateChanged();
    }

    [JSInvokable]
    public void OnParticipantMuteChanged(string identity, bool isMuted)
    {
        HandleParticipantMuteChangedInternal(identity, isMuted);
    }

    public async Task ToggleScreenShareAsync(string quality = "Auto", int fps = 30, string mode = "motion", bool captureAudio = false)
    {
        try
        {
            bool newScreenState = !IsScreenSharing;
            if (_jsInterop.IsModuleLoaded)
            {
                await _jsInterop.SetScreenShareEnabledAsync(newScreenState, quality, fps, mode, captureAudio);
            }
            IsScreenSharing = newScreenState;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Voice] ToggleScreenShare failed: {Message}", ex.Message);
            IsScreenSharing = false;
        }
        NotifyStateChanged();
    }

    public async Task<bool> SetScreenShareAudioEnabledAsync(bool enabled)
    {
        try
        {
            if (IsScreenSharing && _jsInterop.IsModuleLoaded)
            {
                return await _jsInterop.SetScreenShareAudioEnabledAsync(enabled);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Voice] SetScreenShareAudioEnabled failed: {Message}", ex.Message);
        }
        return false;
    }

    public event Action<bool>? OnScreenShareAudioStateChanged;

    [JSInvokable]
    public void OnScreenShareAudioActive(bool active)
    {
        OnScreenShareAudioStateChanged?.Invoke(active);
        NotifyStateChanged();
    }

    public event Action<string, bool>? OnRemoteScreenAudioStateChanged;

    [JSInvokable]
    public void OnRemoteScreenAudioChanged(string identity, bool hasAudio)
    {
        HandleRemoteScreenAudioChangedInternal(identity, hasAudio);
    }

    private void HandleRemoteScreenAudioChangedInternal(string identity, bool hasAudio)
    {
        if (string.IsNullOrEmpty(identity)) return;
        bool changed;
        if (hasAudio)
        {
            changed = RemoteScreenAudioParticipants.Add(identity);
        }
        else
        {
            changed = RemoteScreenAudioParticipants.Remove(identity);
            MutedRemoteScreenAudioParticipants.Remove(identity);
        }

        if (changed)
        {
            OnRemoteScreenAudioStateChanged?.Invoke(identity, hasAudio);
            NotifyStateChanged();
        }
    }

    public async Task ToggleRemoteScreenAudioMuteAsync(string participantId)
    {
        if (string.IsNullOrEmpty(participantId)) return;
        bool isCurrentlyMuted = MutedRemoteScreenAudioParticipants.Contains(participantId);
        bool targetMute = !isCurrentlyMuted;
        if (targetMute)
        {
            MutedRemoteScreenAudioParticipants.Add(participantId);
        }
        else
        {
            MutedRemoteScreenAudioParticipants.Remove(participantId);
        }

        if (_jsInterop.IsModuleLoaded && IsInVoiceCall)
        {
            try
            {
                await _jsInterop.SetRemoteScreenAudioMutedAsync(participantId, targetMute);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[Voice] SetRemoteScreenAudioMuted failed: {Message}", ex.Message);
            }
        }
        NotifyStateChanged();
    }

    public async Task UpdateScreenShareSettingsAsync(string quality, int fps, string mode)
    {
        try
        {
            if (IsScreenSharing && _jsInterop.IsModuleLoaded)
            {
                await _jsInterop.UpdateScreenShareSettingsAsync(quality, fps, mode);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Voice] UpdateScreenShareSettings failed: {Message}", ex.Message);
        }
        NotifyStateChanged();
    }

    public void MarkForIntegrityCheck()
    {
        RequiresIntegrityCheck = true;
        NotifyStateChanged();
    }

    public async Task VerifyServerStateIntegrityAsync()
    {
        if (RequiresIntegrityCheck && IsInVoiceCall && !string.IsNullOrEmpty(CurrentChannelId) && !string.IsNullOrEmpty(CurrentUserId))
        {
            RequiresIntegrityCheck = false;
            var serverParticipants = _chatState.GetVoiceParticipants(CurrentChannelId);
            if (!serverParticipants.Contains(CurrentUserId))
            {
                await DisconnectAsync();
            }
            else
            {
                CallParticipants.Clear();
                CallParticipants.Add(CurrentUserId);
                foreach (var spId in serverParticipants)
                {
                    CallParticipants.Add(spId);
                }
                NotifyStateChanged();
            }
        }
    }

    private void ResetState()
    {
        IsInVoiceCall = false;
        IsConnecting = false;
        RequiresIntegrityCheck = false;
        CurrentChannelId = null;
        CurrentUserId = null;
        IsMuted = false;
        IsVideoOn = false;
        IsScreenSharing = false;
        CallParticipants.Clear();
        ActiveSpeakers.Clear();
        MutedParticipants.Clear();
        RemoteScreenAudioParticipants.Clear();
        MutedRemoteScreenAudioParticipants.Clear();
        _deviceService.Reset();
    }

    private void NotifyStateChanged() => OnChange?.Invoke();

    [JSInvokable]
    public void OnRoomConnected(string[] participantIdentities)
    {
        HandleRoomConnectedInternal(participantIdentities);
    }

    private void HandleRoomConnectedInternal(string[] participantIdentities)
    {
        foreach (var id in participantIdentities)
        {
            if (!id.StartsWith("bot:", StringComparison.OrdinalIgnoreCase))
            {
                CallParticipants.Add(id);
            }
        }

        if (!string.IsNullOrEmpty(CurrentChannelId))
        {
            var serverParticipants = _chatState.GetVoiceParticipants(CurrentChannelId);
            foreach (var spId in serverParticipants)
            {
                if (!spId.StartsWith("bot:", StringComparison.OrdinalIgnoreCase))
                {
                    CallParticipants.Add(spId);
                }
            }
        }

        OnRoomJoined?.Invoke();
        NotifyStateChanged();
    }

    [JSInvokable]
    public void OnParticipantConnected(string identity)
    {
        HandleParticipantConnectedInternal(identity);
    }

    private void HandleParticipantConnectedInternal(string identity)
    {
        if (string.IsNullOrEmpty(identity) || identity == CurrentUserId || identity.StartsWith("bot:", StringComparison.OrdinalIgnoreCase)) return;

        CallParticipants.Add(identity);

        if (!string.IsNullOrEmpty(CurrentChannelId))
        {
            var serverParticipants = _chatState.GetVoiceParticipants(CurrentChannelId);
            foreach (var spId in serverParticipants)
            {
                if (!spId.StartsWith("bot:", StringComparison.OrdinalIgnoreCase))
                {
                    CallParticipants.Add(spId);
                }
            }
        }

        OnParticipantJoined?.Invoke(identity);
        NotifyStateChanged();
    }

    [JSInvokable]
    public void EnsureParticipantSlot(string identity)
    {
        HandleEnsureParticipantSlotInternal(identity);
    }

    private void HandleEnsureParticipantSlotInternal(string identity)
    {
        if (string.IsNullOrEmpty(identity) || identity.StartsWith("bot:", StringComparison.OrdinalIgnoreCase)) return;

        if (CallParticipants.Add(identity))
        {
            NotifyStateChanged();
        }
    }

    [JSInvokable]
    public void OnParticipantDisconnected(string identity)
    {
        HandleParticipantDisconnectedInternal(identity);
    }

    private void HandleParticipantDisconnectedInternal(string identity)
    {
        if (string.IsNullOrEmpty(identity) || identity == CurrentUserId || identity.StartsWith("bot:", StringComparison.OrdinalIgnoreCase)) return;

        CallParticipants.Remove(identity);
        ActiveSpeakers.Remove(identity);
        RemoteScreenAudioParticipants.Remove(identity);
        MutedRemoteScreenAudioParticipants.Remove(identity);
        _deviceService.RemoveParticipantVideo(identity);

        OnParticipantLeft?.Invoke(identity);
        NotifyStateChanged();
    }

    private bool _wasISpeaking = false;

    [JSInvokable]
    public void OnActiveSpeakersChanged(string[] speakerSids)
    {
        HandleActiveSpeakersChangedInternal(speakerSids);
    }

    private void HandleActiveSpeakersChangedInternal(string[] speakerSids)
    {
        ActiveSpeakers.ReplaceWith(speakerSids.Where(id => !(id == CurrentUserId && IsMuted) && !id.StartsWith("bot:", StringComparison.OrdinalIgnoreCase)));

        if (CurrentChannelId != null && CurrentUserId != null)
        {
            bool amISpeaking = ActiveSpeakers.Contains(CurrentUserId) && !IsMuted;
            if (amISpeaking != _wasISpeaking)
            {
                _wasISpeaking = amISpeaking;
                _ = _voiceChannelService.SetUserSpeakingAsync(CurrentChannelId, CurrentUserId, amISpeaking);
            }
        }
        NotifyStateChanged();
    }

    [JSInvokable]
    public async Task OnRoomDisconnected()
    {
        await HandleRoomDisconnectedInternalAsync();
    }

    private async Task HandleRoomDisconnectedInternalAsync()
    {
        var wasInCall = IsInVoiceCall;

        if (!string.IsNullOrEmpty(CurrentUserId) && !string.IsNullOrEmpty(CurrentChannelId))
        {
            try
            {
                await _voiceChannelService.LeaveVoiceAsync(CurrentChannelId, CurrentUserId);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[VoiceService] Failed to leave voice channel on room disconnect: {Message}", ex.Message);
            }
        }

        if (wasInCall)
        {
            OnRoomLeft?.Invoke();
        }

        ResetState();
        NotifyStateChanged();
    }

    [JSInvokable]
    public void OnLocalMuted(bool isMuted)
    {
        HandleLocalMutedInternal(isMuted);
    }

    private void HandleLocalMutedInternal(bool isMuted)
    {
        IsMuted = isMuted;
        if (isMuted)
        {
            if (CurrentUserId != null) ActiveSpeakers.Remove(CurrentUserId);
            _wasISpeaking = false;
            if (CurrentChannelId != null && CurrentUserId != null)
            {
                _ = _voiceChannelService.SetUserSpeakingAsync(CurrentChannelId, CurrentUserId, false);
            }
        }
        NotifyStateChanged();
    }

    private void HandleParticipantMuteChangedInternal(string identity, bool isMuted)
    {
        if (string.IsNullOrEmpty(identity)) return;
        bool changed = isMuted ? MutedParticipants.Add(identity) : MutedParticipants.Remove(identity);
        if (isMuted) ActiveSpeakers.Remove(identity);
        if (changed)
        {
            NotifyStateChanged();
        }
    }

    public async ValueTask DisposeAsync()
    {
        _chatState.VoiceMemberJoined -= OnServerVoiceMemberJoined;
        _chatState.VoiceMemberLeft -= OnServerVoiceMemberLeft;
        _chatState.VoiceUserMutedChanged -= OnServerVoiceUserMutedChanged;

        if (IsInVoiceCall)
        {
            try
            {
                await DisconnectAsync();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "GlobalVoiceService Dispose Error: {Message}", ex.Message);
            }
        }

        await _jsInterop.DisposeAsync();
        _objRef?.Dispose();
    }

    [JSInvokable]
    public void OnParticipantVideoDimensions(string participantId, int height, int fps)
    {
        if (!string.IsNullOrEmpty(participantId) && CallParticipants.Contains(participantId))
        {
            _deviceService.HandleParticipantVideoDimensions(participantId, height, fps);
            NotifyStateChanged();
        }
    }

    [JSInvokable]
    public void OnLocalVideoDimensions(int maxEdge, int fps)
    {
        _deviceService.HandleLocalVideoDimensions(maxEdge, fps);
        NotifyStateChanged();
    }

    [JSInvokable]
    public void OnLocalScreenDimensions(int maxEdge, int fps)
    {
        _deviceService.HandleLocalScreenDimensions(maxEdge, fps);
        NotifyStateChanged();
    }

    [JSInvokable]
    public void LogToServer(string level, string message)
    {
        if ((DateTime.UtcNow - _lastClientLogReset).TotalMinutes > 1)
        {
            _clientLogCount = 0;
            _lastClientLogReset = DateTime.UtcNow;
        }

        if (_clientLogCount >= 10) return;
        _clientLogCount++;

        if (string.IsNullOrWhiteSpace(message)) return;
        if (message.Length > 500) message = message[..500] + "...[TRUNCATED]";

        message = message.Replace("\r", "").Replace("\n", " ");
        level = level?.Replace("\r", "").Replace("\n", " ") ?? "INFO";

        switch (level.ToUpperInvariant())
        {
            case "ERROR": _logger.LogError("[Client-Side ERROR] {Message}", message); break;
            case "WARN":
            case "WARNING": _logger.LogWarning("[Client-Side WARNING] {Message}", message); break;
            default: _logger.LogInformation("[Client-Side INFO] {Message}", message); break;
        }
    }

    [JSInvokable]
    public void OnVoiceLog(string message, string severityStr)
    {
        OnVoiceLogReceived?.Invoke(message, severityStr);
    }
}
