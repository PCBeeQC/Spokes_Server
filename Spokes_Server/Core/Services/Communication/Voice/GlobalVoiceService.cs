using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using MudBlazor;
using Spokes_Server.Core.Services.Communication.Chat;

namespace Spokes_Server.Core.Services.Communication.Voice;

public class CameraDevice
{
    [JsonPropertyName("deviceId")]
    public string DeviceId { get; set; } = "";

    [JsonPropertyName("label")]
    public string Label { get; set; } = "";
}

public class CameraInitResult
{
    [JsonPropertyName("cameras")]
    public List<CameraDevice> Cameras { get; set; } = [];

    [JsonPropertyName("activeCameraDeviceId")]
    public string ActiveCameraDeviceId { get; set; } = "";
}

public class AudioDevice
{
    [JsonPropertyName("deviceId")]
    public string DeviceId { get; set; } = "";

    [JsonPropertyName("label")]
    public string Label { get; set; } = "";
}

public class GlobalVoiceService : IAsyncDisposable
{
    private readonly IJSRuntime _js;
    private readonly ChatService _chatService;
    private readonly ChatStateService _chatState;
    private readonly ISnackbar _snackbar;
    private readonly Spokes_Server.Core.Services.UI.ISoundService _soundService;

    private IJSObjectReference? _voiceModule;
    private DotNetObjectReference<GlobalVoiceService>? _objRef;

    public event Action? OnChange;

    public bool IsInVoiceCall { get; private set; }
    public bool IsConnecting { get; private set; }
    public bool RequiresIntegrityCheck { get; private set; }
    public string? CurrentChannelId { get; private set; }
    public string? CurrentUserId { get; private set; }
    public bool IsMuted { get; private set; }
    public bool IsVideoOn { get; private set; }
    public List<CameraDevice> AvailableCameras { get; private set; } = [];
    public string ActiveCameraDeviceId { get; set; } = "";
    
    public List<AudioDevice> AvailableMicrophones { get; private set; } = [];
    public string ActiveMicrophoneDeviceId { get; set; } = "";
    
    public List<AudioDevice> AvailableSpeakers { get; private set; } = [];
    public string ActiveSpeakerDeviceId { get; set; } = "";

    public string CurrentNoiseSuppression { get; private set; } = "webrtc";

    public bool IsScreenSharing { get; private set; }

    public int MaxCameraHeight { get; private set; } = 2160;
    public int MaxCameraFps { get; private set; } = 30;

    public int MaxScreenHeight { get; private set; } = 2160;
    public int MaxScreenFps { get; private set; } = 30;

    public ConcurrentDictionary<string, int> RemoteVideoHeights { get; } = new();
    public ConcurrentDictionary<string, int> RemoteVideoFps { get; } = new();

    public string MaxVideoSendQuality { get; set; } = "Auto";
    public int MaxVideoSendFps { get; set; } = 30;

    public string MaxScreenShareQuality { get; set; } = "Auto";
    public int MaxScreenShareFps { get; set; } = 30;

    public HashSet<string> CallParticipants { get; } = [];
    public HashSet<string> ActiveSpeakers { get; } = [];
    public HashSet<string> MutedParticipants { get; } = [];

    public Spokes_Server.Core.Models.HR.VoiceUserSettings VoiceSettings { get; private set; } = new();

    private readonly ILogger<GlobalVoiceService> _logger;

    public GlobalVoiceService(IJSRuntime js, ChatService chatService, ChatStateService chatState, ISnackbar snackbar, ILogger<GlobalVoiceService> logger, Spokes_Server.Core.Services.UI.ISoundService soundService)
    {
        _js = js;
        _chatService = chatService;
        _chatState = chatState;
        _snackbar = snackbar;
        _logger = logger;
        _soundService = soundService;

        if (_chatState != null)
        {
            _chatState.VoiceMemberJoined += OnServerVoiceMemberJoined;
            _chatState.VoiceMemberLeft += OnServerVoiceMemberLeft;
            _chatState.VoiceUserMutedChanged += OnServerVoiceUserMutedChanged;
        }
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
        if (IsInVoiceCall && CurrentChannelId == channelId && !string.IsNullOrEmpty(userId))
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
                RemoteVideoHeights.TryRemove(userId, out _);
                RemoteVideoFps.TryRemove(userId, out _);
                if (removed)
                {
                    NotifyStateChanged();
                }
            }
        }
    }

    public bool IsAndroid { get; private set; }
    public bool IsIOS { get; private set; }

    private async Task InitializeModuleAsync()
    {
        if (_voiceModule != null) return;
        _objRef = DotNetObjectReference.Create(this);
        // We load the module once, globally.
        try
        {
            _voiceModule = await _js.InvokeAsync<IJSObjectReference>("import", "./js/voiceInterop.js");
            bool isApple = await _voiceModule.InvokeAsync<bool>("isAppleMobileDevice");
            IsIOS = isApple;
            IsAndroid = await _voiceModule.InvokeAsync<bool>("isAndroidDevice");
        }
        catch (Exception e)
        {
            Console.WriteLine($"GlobalVoiceService JS load failed: {e.Message}");
            throw;
        }
    }

    public void ConnectMockForDemo(string channelId, string userId, HashSet<string> mockParticipants)
    {
        CurrentChannelId = channelId;
        CurrentUserId = userId;
        IsInVoiceCall = true;
        IsConnecting = false;
        CallParticipants.Clear();
        foreach (var p in mockParticipants)
        {
            CallParticipants.Add(p);
            _chatState.NotifyVoiceMemberJoined(channelId, p);
        }
        NotifyStateChanged();
    }

    public void DisconnectMockForDemo()
    {
        if (CurrentChannelId != null)
        {
            foreach (var p in CallParticipants)
            {
                _chatState.NotifyVoiceMemberLeft(CurrentChannelId, p);
            }
        }
        CurrentChannelId = null;
        CurrentUserId = null;
        IsInVoiceCall = false;
        IsConnecting = false;
        CallParticipants.Clear();
        ActiveSpeakers.Clear();
        NotifyStateChanged();
    }

    public async Task LoadDeviceVoiceSettingsAsync()
    {
        try
        {
            var json = await _js.InvokeAsync<string?>("localStorage.getItem", "spokes_voice_settings_v1");
            if (!string.IsNullOrEmpty(json))
            {
                VoiceSettings = System.Text.Json.JsonSerializer.Deserialize<Spokes_Server.Core.Models.HR.VoiceUserSettings>(json) ?? new();
            }
            else
            {
                VoiceSettings = new();
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load device voice settings from localStorage");
            VoiceSettings = new();
        }
    }

    public async Task ConnectAsync(string token, string url, string channelId, string userId, int audioBitrateKbps = 64, string noiseSuppression = "rnnoise")
    {
        if (IsConnecting)
        {
            _logger.LogInformation("ConnectAsync called while already connecting. Ignoring duplicate attempt.");
            return;
        }

        if (IsInVoiceCall && CurrentChannelId == channelId)
        {
            _logger.LogInformation("ConnectAsync called while already connected to channel {ChannelId}. Ignoring duplicate attempt.", channelId);
            return;
        }

        IsConnecting = true;
        RequiresIntegrityCheck = false;
        NotifyStateChanged();

        try
        {
            if (_voiceModule == null) await InitializeModuleAsync();

            if (IsIOS && noiseSuppression != "none")
            {
                noiseSuppression = "webrtc";
            }

            CurrentNoiseSuppression = noiseSuppression;

            // If already connected to another channel, leave first
            if (IsInVoiceCall)
            {
                await DisconnectAsync();
            }

            CurrentChannelId = channelId;
            CurrentUserId = userId;
            CallParticipants.Clear();
            ActiveSpeakers.Clear();
            MutedParticipants.Clear();
            CallParticipants.Add(userId);

            var serverParticipants = _chatState.GetVoiceParticipants(channelId);
            foreach (var spId in serverParticipants)
            {
                CallParticipants.Add(spId);
            }

            var serverMuted = _chatState.GetMutedUsers(channelId);
            foreach (var smId in serverMuted)
            {
                MutedParticipants.Add(smId);
            }

            await LoadDeviceVoiceSettingsAsync();

            IsMuted = false;
            IsVideoOn = false;
            IsScreenSharing = false;

            // Notify UI layout to mount video/audio dom containers BEFORE JS connect
            NotifyStateChanged();

            if (_voiceModule == null)
            {
                throw new InvalidOperationException("Voice module not initialized.");
            }

            IsInVoiceCall = await _voiceModule.InvokeAsync<bool>("connectToVoice", token, url, _objRef, audioBitrateKbps, CurrentNoiseSuppression);

            if (IsInVoiceCall)
            {
                await _chatService.JoinVoiceAsync(userId, channelId);
                _ = _chatService.SetUserMutedAsync(channelId, userId, IsMuted);
                try
                {
                    var settingsJson = System.Text.Json.JsonSerializer.Serialize(VoiceSettings);
                    await _voiceModule.InvokeVoidAsync("initVoiceSettings", settingsJson);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to initialize voice settings in JS");
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

        if (_voiceModule != null)
        {
            try
            {
                await _voiceModule.InvokeVoidAsync("disconnectFromVoice");
            }
            catch (JSDisconnectedException ex)
            {
                Console.WriteLine($"[VoiceService] Disconnect JSDisconnectedException (browser closed): {ex.Message}");
            }
            catch (TaskCanceledException ex)
            {
                Console.WriteLine($"[VoiceService] Disconnect TaskCanceledException (circuit cancelled): {ex.Message}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[VoiceService] Disconnect Exception: {ex.Message}");
            }
        }

        if (!string.IsNullOrEmpty(uid) && !string.IsNullOrEmpty(cid))
        {
            try
            {
                await _chatService.LeaveVoiceAsync(uid, cid);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[VoiceService] Failed to leave voice channel: {ex.Message}");
            }
        }

        if (wasInCall)
        {
            _ = _soundService.PlayChannelLeaveAsync();
        }

        ResetState();
        NotifyStateChanged();
    }

    public async Task ToggleMute()
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
                    _ = _chatService.SetUserSpeakingAsync(CurrentChannelId, CurrentUserId, false);
                    _ = _chatService.SetUserMutedAsync(CurrentChannelId, CurrentUserId, true);
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
                    _ = _chatService.SetUserMutedAsync(CurrentChannelId, CurrentUserId, false);
                }
            }
            NotifyStateChanged();

            if (_voiceModule != null)
            {
                await _voiceModule.InvokeVoidAsync("setMicrophoneEnabled", !newMuteState);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Voice] ToggleMute failed: {ex.Message}");
        }
        NotifyStateChanged();
    }

    public async Task ToggleVideo()
    {
        try
        {
            bool newVideoState = !IsVideoOn;
            if (_voiceModule != null)
            {
                if (newVideoState)
                {
                    var result = await _voiceModule.InvokeAsync<CameraInitResult>("setCameraEnabled", newVideoState, MaxVideoSendQuality, MaxVideoSendFps, ActiveCameraDeviceId);
                    AvailableCameras = result?.Cameras?.Take(100).ToList() ?? new();
                    ActiveCameraDeviceId = result?.ActiveCameraDeviceId ?? "";
                }
                else
                {
                    await _voiceModule.InvokeVoidAsync("setCameraEnabled", newVideoState, MaxVideoSendQuality, MaxVideoSendFps, ActiveCameraDeviceId);
                    AvailableCameras.Clear();
                    // We intentionally preserve ActiveCameraDeviceId here so the quality toggle restores the correct camera
                }
            }
            IsVideoOn = newVideoState;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Voice] ToggleVideo failed: {ex.Message}");
            IsVideoOn = false;
        }
        NotifyStateChanged();
    }

    public async Task SwitchCameraAsync(string deviceId)
    {
        try
        {
            if (_voiceModule != null && IsVideoOn)
            {
                await _voiceModule.InvokeVoidAsync("switchActiveCamera", deviceId);
                ActiveCameraDeviceId = deviceId;
                NotifyStateChanged();
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Voice] SwitchCamera failed: {ex.Message}");
        }
    }

    public async Task SwitchMicrophoneAsync(string deviceId)
    {
        try
        {
            ActiveMicrophoneDeviceId = deviceId;
            if (_voiceModule == null) await InitializeModuleAsync();
            if (_voiceModule != null)
            {
                await _voiceModule.InvokeVoidAsync("switchActiveMicrophone", deviceId);
            }
            NotifyStateChanged();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Voice] SwitchMicrophone failed: {ex.Message}");
        }
    }

    public async Task SwitchSpeakerAsync(string deviceId)
    {
        try
        {
            ActiveSpeakerDeviceId = deviceId;
            if (_voiceModule == null) await InitializeModuleAsync();
            if (_voiceModule != null)
            {
                await _voiceModule.InvokeVoidAsync("switchActiveSpeaker", deviceId);
            }
            NotifyStateChanged();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Voice] SwitchSpeaker failed: {ex.Message}");
        }
    }

    public async Task RefreshAudioDevicesAsync()
    {
        try
        {
            if (_voiceModule == null) await InitializeModuleAsync();
            if (_voiceModule != null)
            {
                await _voiceModule.InvokeVoidAsync("updateAudioDevices");
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to refresh audio devices");
        }
    }

    [JSInvokable]
    public void OnAudioDevicesUpdated(List<AudioDevice> mics, string activeMicId, List<AudioDevice> speakers, string activeSpeakerId)
    {
        AvailableMicrophones = mics?.Take(100).ToList() ?? new();
        ActiveMicrophoneDeviceId = string.IsNullOrEmpty(activeMicId) ? (AvailableMicrophones.FirstOrDefault()?.DeviceId ?? "") : activeMicId;
        
        AvailableSpeakers = speakers?.Take(100).ToList() ?? new();
        ActiveSpeakerDeviceId = string.IsNullOrEmpty(activeSpeakerId) ? (AvailableSpeakers.FirstOrDefault()?.DeviceId ?? "") : activeSpeakerId;
        
        NotifyStateChanged();
    }

    public async Task SetNoiseSuppressionAsync(string type)
    {
        if (CurrentNoiseSuppression == type) return;
        CurrentNoiseSuppression = type;
        if (_voiceModule != null)
        {
            await _voiceModule.InvokeVoidAsync("setNoiseSuppressionType", type);
        }
        
        // If the microphone is currently active, we need to restart the track to apply the new model
        if (!IsMuted && IsInVoiceCall)
        {
            await ToggleMute(); // turn off
            await ToggleMute(); // turn on with new processor
        }
        NotifyStateChanged();
    }

    public async Task SetMasterVolumeAsync(double volumePercent)
    {
        VoiceSettings.MasterVolume = volumePercent;
        if (_voiceModule != null && IsInVoiceCall)
        {
            try
            {
                await _voiceModule.InvokeVoidAsync("setMasterVolume", volumePercent);
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
        VoiceSettings.ParticipantVolumes[participantId] = volumePercent;
        if (_voiceModule != null && IsInVoiceCall)
        {
            try
            {
                await _voiceModule.InvokeVoidAsync("setParticipantVolume", participantId, volumePercent);
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
        VoiceSettings.VoiceSensitivity = Math.Clamp(sensitivity, 0.0, 100.0);
        if (_voiceModule != null && IsInVoiceCall)
        {
            try
            {
                await _voiceModule.InvokeVoidAsync("setVoiceSensitivity", VoiceSettings.VoiceSensitivity);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to set voice sensitivity in JS");
            }
        }
        NotifyStateChanged();
    }

    public async Task UpdateVoiceSettingsAsync(Spokes_Server.Core.Models.HR.VoiceUserSettings settings)
    {
        VoiceSettings = settings ?? new();
        if (_voiceModule != null && IsInVoiceCall)
        {
            try
            {
                var json = System.Text.Json.JsonSerializer.Serialize(VoiceSettings);
                await _voiceModule.InvokeVoidAsync("initVoiceSettings", json);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to update voice settings in JS");
            }
        }
        NotifyStateChanged();
    }

    public async Task PlayTestSpeakerSoundAsync(string? deviceId = null)
    {
        var targetDevice = !string.IsNullOrEmpty(deviceId) ? deviceId : ActiveSpeakerDeviceId;
        try
        {
            if (_voiceModule == null) await InitializeModuleAsync();
            if (_voiceModule != null)
            {
                await _voiceModule.InvokeVoidAsync("playTestSpeakerSound", targetDevice);
                return;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to play test sound via voice module, falling back to sound service");
        }

        try
        {
            await _soundService.PlaySoundAsync("spokesnotif1", true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to play test sound");
        }
    }

    [JSInvokable]
    public void OnParticipantMuteChanged(string identity, bool isMuted)
    {
        if (string.IsNullOrEmpty(identity)) return;
        bool changed = isMuted ? MutedParticipants.Add(identity) : MutedParticipants.Remove(identity);
        if (isMuted) ActiveSpeakers.Remove(identity);
        if (changed)
        {
            NotifyStateChanged();
        }
    }


    public async Task ToggleScreenShare()
    {
        try
        {
            bool newScreenState = !IsScreenSharing;
            if (_voiceModule != null)
            {
                await _voiceModule.InvokeVoidAsync("setScreenShareEnabled", newScreenState, MaxScreenShareQuality, MaxScreenShareFps);
            }
            IsScreenSharing = newScreenState;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Voice] ToggleScreenShare failed: {ex.Message}");
            IsScreenSharing = false;
        }
        NotifyStateChanged();
    }

    /// <summary>
    /// Re-attach all active tracks to freshly created DOM containers.
    /// Called when the Chat component remounts after channel navigation.
    /// </summary>
    public async Task ReattachTracksAsync()
    {
        if (_voiceModule != null && IsInVoiceCall)
        {
            await _voiceModule.InvokeVoidAsync("reattachAllTracks");
        }
    }
    
    /// <summary>
    /// Forces iOS Safari to reconstruct the video decoding pipeline for WebRTC tracks.
    /// Used when the layout shifts or when a new camera is activated to prevent freezes.
    /// </summary>
    public async Task RefreshVideoLayoutsAsync()
    {
        if (_voiceModule != null && IsInVoiceCall)
        {
            await _voiceModule.InvokeVoidAsync("refreshVideoLayouts");
        }
    }

    public async Task SetRemoteVideoQualityAsync(string participantId, string quality)
    {
        if (_voiceModule != null && IsInVoiceCall)
        {
            await _voiceModule.InvokeVoidAsync("setRemoteVideoQuality", participantId, quality);
        }
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
                // The watchdog kicked us while we were offline! We must forcefully disconnect
                // our local client state to match the server reality.
                await DisconnectAsync();
            }
            else
            {
                // We are still in the call. Sync the local participant list with the server
                // in case anyone joined or left while we were disconnected.
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
        AvailableCameras.Clear();
        ActiveCameraDeviceId = "";
        IsScreenSharing = false;
        CallParticipants.Clear();
        ActiveSpeakers.Clear();
        MutedParticipants.Clear();
        RemoteVideoHeights.Clear();
        RemoteVideoFps.Clear();
        MaxCameraHeight = 2160;
        MaxCameraFps = 30;
    }

    private void NotifyStateChanged() => OnChange?.Invoke();

    [JSInvokable]
    public void OnRoomConnected(string[] participantIdentities)
    {
        foreach (var id in participantIdentities)
        {
            CallParticipants.Add(id);
        }

        // ----- CRITICAL SYNC FIX: DO NOT REMOVE -----
        // App open on multiple computers/tabs can cause LiveKit's existingParticipants 
        // to be momentarily empty during the Javascript `invokeMethodAsync('OnRoomConnected')` 
        // race condition, causing connecting users to not see existing participants' rectangles.
        // We ALWAYS sync with the server's authoritative `_chatState.GetVoiceParticipants` 
        // as a fallback. Future agents: Do not "cleanup" or remove this server-side sync!
        if (!string.IsNullOrEmpty(CurrentChannelId))
        {
            var serverParticipants = _chatState.GetVoiceParticipants(CurrentChannelId);
            foreach (var spId in serverParticipants)
            {
                CallParticipants.Add(spId);
            }
        }
        // ---------------------------------------------

        _ = _soundService.PlayChannelJoinAsync();

        NotifyStateChanged();
    }

    [JSInvokable]
    public void OnParticipantConnected(string identity)
    {
        if (string.IsNullOrEmpty(identity)) return;

        bool isNew = CallParticipants.Add(identity);

        if (!string.IsNullOrEmpty(CurrentChannelId))
        {
            var serverParticipants = _chatState.GetVoiceParticipants(CurrentChannelId);
            foreach (var spId in serverParticipants)
            {
                CallParticipants.Add(spId);
            }
        }

        if (isNew)
        {
            _ = _soundService.PlayChannelJoinAsync();
        }

        NotifyStateChanged();
    }

    [JSInvokable]
    public void EnsureParticipantSlot(string identity)
    {
        if (string.IsNullOrEmpty(identity)) return;

        if (CallParticipants.Add(identity))
        {
            NotifyStateChanged();
        }
    }

    [JSInvokable]
    public void OnParticipantDisconnected(string identity)
    {
        CallParticipants.Remove(identity);
        ActiveSpeakers.Remove(identity);
        RemoteVideoHeights.TryRemove(identity, out _);
        RemoteVideoFps.TryRemove(identity, out _);
        
        _ = _soundService.PlayChannelLeaveAsync();
        
        NotifyStateChanged();
    }

    private bool _wasISpeaking = false;

    [JSInvokable]
    public void OnActiveSpeakersChanged(string[] speakerSids)
    {
        ActiveSpeakers.Clear();
        foreach (var id in speakerSids)
        {
            if (id == CurrentUserId && IsMuted) continue;
            ActiveSpeakers.Add(id);
        }

        if (CurrentChannelId != null && CurrentUserId != null)
        {
            bool amISpeaking = ActiveSpeakers.Contains(CurrentUserId) && !IsMuted;
            if (amISpeaking != _wasISpeaking)
            {
                _wasISpeaking = amISpeaking;
                _ = _chatService.SetUserSpeakingAsync(CurrentChannelId, CurrentUserId, amISpeaking);
            }
        }
        NotifyStateChanged();
    }

    [JSInvokable]
    public async Task OnRoomDisconnected()
    {
        var wasInCall = IsInVoiceCall;

        if (!string.IsNullOrEmpty(CurrentUserId) && !string.IsNullOrEmpty(CurrentChannelId))
        {
            try
            {
                await _chatService.LeaveVoiceAsync(CurrentUserId, CurrentChannelId);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[VoiceService] Failed to leave voice channel on room disconnect: {ex.Message}");
            }
        }

        if (wasInCall)
        {
            _ = _soundService.PlayChannelLeaveAsync();
        }

        ResetState();
        NotifyStateChanged();
    }

    [JSInvokable]
    public void OnLocalMuted(bool isMuted)
    {
        IsMuted = isMuted;
        if (isMuted)
        {
            if (CurrentUserId != null) ActiveSpeakers.Remove(CurrentUserId);
            _wasISpeaking = false;
            if (CurrentChannelId != null && CurrentUserId != null)
            {
                _ = _chatService.SetUserSpeakingAsync(CurrentChannelId, CurrentUserId, false);
            }
        }
        NotifyStateChanged();
    }


    public async ValueTask DisposeAsync()
    {
        if (_chatState != null)
        {
            _chatState.VoiceMemberJoined -= OnServerVoiceMemberJoined;
            _chatState.VoiceMemberLeft -= OnServerVoiceMemberLeft;
            _chatState.VoiceUserMutedChanged -= OnServerVoiceUserMutedChanged;
        }

        if (IsInVoiceCall)
        {
            try
            {
                await DisconnectAsync();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"GlobalVoiceService Dispose Error: {ex.Message}");
            }
        }

        if (_voiceModule != null)
        {
            try
            {
                await _voiceModule.DisposeAsync();
            }
            catch (JSDisconnectedException ex)
            {
                Console.WriteLine($"[VoiceService] Dispose JSDisconnectedException (browser closed): {ex.Message}");
            }
            catch (TaskCanceledException ex)
            {
                Console.WriteLine($"[VoiceService] Dispose TaskCanceledException (circuit cancelled): {ex.Message}");
            }
        }
        _objRef?.Dispose();
    }

    [JSInvokable]
    public void OnParticipantVideoDimensions(string participantId, int height, int fps)
    {
        if (!string.IsNullOrEmpty(participantId) && CallParticipants.Contains(participantId))
        {
            RemoteVideoHeights[participantId] = height;
            RemoteVideoFps[participantId] = fps;
            NotifyStateChanged();
        }
    }

    [JSInvokable]
    public void OnLocalVideoDimensions(int maxEdge, int fps)
    {
        MaxCameraHeight = maxEdge;
        MaxCameraFps = fps;

        if (maxEdge < 3000 && MaxVideoSendQuality == "2160p")
        {
            MaxVideoSendQuality = "1440p";
        }
        if (maxEdge < 2400 && (MaxVideoSendQuality == "2160p" || MaxVideoSendQuality == "1440p"))
        {
            MaxVideoSendQuality = "1080p";
        }
        if (maxEdge < 1800 && (MaxVideoSendQuality == "2160p" || MaxVideoSendQuality == "1440p" || MaxVideoSendQuality == "1080p"))
        {
            MaxVideoSendQuality = "720p";
        }
        if (maxEdge < 1000 && (MaxVideoSendQuality == "2160p" || MaxVideoSendQuality == "1440p" || MaxVideoSendQuality == "1080p" || MaxVideoSendQuality == "720p"))
        {
            MaxVideoSendQuality = "360p";
        }

        NotifyStateChanged();
    }

    [JSInvokable]
    public void OnLocalScreenDimensions(int maxEdge, int fps)
    {
        MaxScreenHeight = maxEdge;
        MaxScreenFps = fps;

        if (maxEdge < 3000 && MaxScreenShareQuality == "2160p")
        {
            MaxScreenShareQuality = "1440p";
        }
        if (maxEdge < 2400 && (MaxScreenShareQuality == "2160p" || MaxScreenShareQuality == "1440p"))
        {
            MaxScreenShareQuality = "1080p";
        }
        if (maxEdge < 1800 && (MaxScreenShareQuality == "2160p" || MaxScreenShareQuality == "1440p" || MaxScreenShareQuality == "1080p"))
        {
            MaxScreenShareQuality = "720p";
        }

        NotifyStateChanged();
    }

    private int _logCount = 0;
    private DateTime _lastLogReset = DateTime.UtcNow;

    [JSInvokable]
    public void LogToServer(string level, string message)
    {
        if ((DateTime.UtcNow - _lastLogReset).TotalMinutes > 1)
        {
            _logCount = 0;
            _lastLogReset = DateTime.UtcNow;
        }

        if (_logCount >= 10) return;
        _logCount++;

        if (string.IsNullOrWhiteSpace(message)) return;
        if (message.Length > 500) message = message[..500] + "...[TRUNCATED]";
        
        // Prevent log injection/forging
        message = message.Replace("\r", "").Replace("\n", " ");
        level = level?.Replace("\r", "").Replace("\n", " ") ?? "INFO";
        
        switch (level.ToUpper())
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
        var severity = severityStr.ToLower() switch
        {
            "success" => Severity.Success,
            "error" => Severity.Error,
            "warning" => Severity.Warning,
            "info" => Severity.Info,
            _ => Severity.Normal
        };
        
        _snackbar.Add(message, severity);
    }
}
