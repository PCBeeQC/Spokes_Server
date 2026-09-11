using Microsoft.JSInterop;
using Spokes_Server.Core.Services;
using Spokes_Server.Core.Data;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Text.Json.Serialization;

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
    public List<CameraDevice> Cameras { get; set; } = new();

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
    private readonly MudBlazor.ISnackbar _snackbar;

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
    public List<CameraDevice> AvailableCameras { get; private set; } = new();
    public string ActiveCameraDeviceId { get; set; } = "";
    
    public List<AudioDevice> AvailableMicrophones { get; private set; } = new();
    public string ActiveMicrophoneDeviceId { get; set; } = "";
    
    public List<AudioDevice> AvailableSpeakers { get; private set; } = new();
    public string ActiveSpeakerDeviceId { get; set; } = "";

    public string CurrentNoiseSuppression { get; private set; } = "webrtc";

    public bool IsScreenSharing { get; private set; }

    public int MaxCameraHeight { get; private set; } = 2160;
    public int MaxCameraFps { get; private set; } = 30;

    public int MaxScreenHeight { get; private set; } = 2160;
    public int MaxScreenFps { get; private set; } = 30;

    public System.Collections.Concurrent.ConcurrentDictionary<string, int> RemoteVideoHeights { get; } = new();
    public System.Collections.Concurrent.ConcurrentDictionary<string, int> RemoteVideoFps { get; } = new();

    public string MaxVideoSendQuality { get; set; } = "Auto";
    public int MaxVideoSendFps { get; set; } = 30;

    public string MaxScreenShareQuality { get; set; } = "Auto";
    public int MaxScreenShareFps { get; set; } = 30;

    public HashSet<string> CallParticipants { get; } = new();
    public HashSet<string> ActiveSpeakers { get; } = new();

    private readonly ILogger<GlobalVoiceService> _logger;

    public GlobalVoiceService(IJSRuntime js, ChatService chatService, ChatStateService chatState, MudBlazor.ISnackbar snackbar, ILogger<GlobalVoiceService> logger)
    {
        _js = js;
        _chatService = chatService;
        _chatState = chatState;
        _snackbar = snackbar;
        _logger = logger;
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

    public async Task ConnectAsync(string token, string url, string channelId, string userId, int audioBitrateKbps = 64, string noiseSuppression = "rnnoise")
    {
        IsConnecting = true;
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
            CallParticipants.Add(userId);

            var serverParticipants = _chatState.GetVoiceParticipants(channelId);
            foreach (var spId in serverParticipants)
            {
                CallParticipants.Add(spId);
            }

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

        ResetState();
        NotifyStateChanged();
    }

    public async Task ToggleMute()
    {
        try
        {
            bool newMuteState = !IsMuted;
            if (_voiceModule != null)
            {
                await _voiceModule.InvokeVoidAsync("setMicrophoneEnabled", !newMuteState);
            }
            IsMuted = newMuteState;
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
            if (_voiceModule != null && IsInVoiceCall)
            {
                await _voiceModule.InvokeVoidAsync("switchActiveMicrophone", deviceId);
                ActiveMicrophoneDeviceId = deviceId;
                NotifyStateChanged();
            }
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
            if (_voiceModule != null && IsInVoiceCall)
            {
                await _voiceModule.InvokeVoidAsync("switchActiveSpeaker", deviceId);
                ActiveSpeakerDeviceId = deviceId;
                NotifyStateChanged();
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Voice] SwitchSpeaker failed: {ex.Message}");
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
        CurrentChannelId = null;
        CurrentUserId = null;
        IsMuted = false;
        IsVideoOn = false;
        AvailableCameras.Clear();
        ActiveCameraDeviceId = "";
        IsScreenSharing = false;
        CallParticipants.Clear();
        ActiveSpeakers.Clear();
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

        NotifyStateChanged();
    }

    [JSInvokable]
    public void OnParticipantConnected(string identity)
    {
        // --- SECONDARY SYNC GUARD ---
        if (!string.IsNullOrEmpty(CurrentChannelId))
        {
            var serverParticipants = _chatState.GetVoiceParticipants(CurrentChannelId);
            if (serverParticipants.Contains(identity))
            {
                CallParticipants.Add(identity);
            }
        }


        NotifyStateChanged();
    }

    [JSInvokable]
    public void OnParticipantDisconnected(string identity)
    {
        CallParticipants.Remove(identity);
        ActiveSpeakers.Remove(identity);
        RemoteVideoHeights.TryRemove(identity, out _);
        RemoteVideoFps.TryRemove(identity, out _);
        NotifyStateChanged();
    }

    private bool _wasISpeaking = false;

    [JSInvokable]
    public void OnActiveSpeakersChanged(string[] speakerSids)
    {
        ActiveSpeakers.Clear();
        foreach (var id in speakerSids) ActiveSpeakers.Add(id);

        if (CurrentChannelId != null && CurrentUserId != null)
        {
            bool amISpeaking = ActiveSpeakers.Contains(CurrentUserId);
            if (amISpeaking != _wasISpeaking)
            {
                _wasISpeaking = amISpeaking;
                _ = _chatService.UpdateActiveSpeakersAsync(CurrentChannelId, amISpeaking ? new[] { CurrentUserId } : Array.Empty<string>());
            }
        }
        NotifyStateChanged();
    }

    [JSInvokable]
    public async Task OnRoomDisconnected()
    {
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
        ResetState();
        NotifyStateChanged();
    }

    [JSInvokable]
    public void OnLocalMuted(bool isMuted)
    {
        IsMuted = isMuted;
        NotifyStateChanged();
    }


    public async ValueTask DisposeAsync()
    {
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
        if (message.Length > 500) message = message.Substring(0, 500) + "...[TRUNCATED]";
        
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
            "success" => MudBlazor.Severity.Success,
            "error" => MudBlazor.Severity.Error,
            "warning" => MudBlazor.Severity.Warning,
            "info" => MudBlazor.Severity.Info,
            _ => MudBlazor.Severity.Normal
        };
        
        _snackbar.Add(message, severity);
    }
}
