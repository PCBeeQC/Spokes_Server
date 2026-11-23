using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.JSInterop;
using Spokes_Server.Core.Helpers;
using Spokes_Server.Core.Models.Communication;
using Spokes_Server.Core.Models.HR;
using Spokes_Server.Core.Services.Communication.Chat;

namespace Spokes_Server.Core.Services.Communication.Voice;

public class GlobalVoiceService : IAsyncDisposable
{
    private readonly IVoiceJsInteropService _jsInterop;
    private readonly VoiceChannelService _voiceChannelService;
    private readonly VoiceStateService _voiceState;
    private readonly ChatStateService _chatState;
    private readonly ILogger<GlobalVoiceService> _logger;
    private readonly IVoiceDeviceService _deviceService;
    private readonly PhysicalVoiceRoomService? _physicalRoomService;
    private readonly PhysicalRoomDirectSessionService? _directSessionService;

    private DotNetObjectReference<GlobalVoiceService>? _objRef;

    public event Action? OnChange;
    public event Action? OnDiagnosticsOrSettingsChanged;
    public event Action? OnActiveSpeakersUpdated;
    public event Action? OnVideoDimensionsChanged;
    public event Action<string, string>? OnVoiceLogReceived;
    public event Action? OnRoomJoined;
    public event Action? OnRoomLeft;
    public event Action<string>? OnParticipantJoined;
    public event Action<string>? OnParticipantLeft;
    public event Action<double>? OnVoiceSensitivityChanged;
    public event Action<bool>? OnAutoGainControlChanged;
    public event Action<bool>? OnIncomingVoiceNormalizationChanged;
    public event Action<double>? OnMasterVolumeChanged;
    public event Action<string, double>? OnParticipantVolumeChanged;
    public event Action<VoiceUserSettings>? OnVoiceSettingsUpdated;
    public event Action? OnHardwareMuteBlockedUnmute;
    public event Action<bool>? OnOsPipStateChangedEvent;
    public event Action<bool>? OnNativeMobilePipChangedEvent;
    public event Action<string>? OnNativeCallActionReceived;
    public event Action<string?>? OnPipFocusChangedEvent;
    public event Action<string>? OnOutgoingCallDeclined;

    public bool IsInVoiceCall { get; private set; }
    public bool IsConnecting { get; private set; }
    public VoiceConnectingStage ConnectingStage { get; private set; } = VoiceConnectingStage.None;
    public string? ConnectingChannelId { get; private set; }
    public bool RequiresIntegrityCheck { get; private set; }
    public string? CurrentChannelId { get; private set; }
    public string? CurrentUserId { get; private set; }
    public string CurrentVoiceSessionId { get; private set; } = string.Empty;
    public string CurrentDeviceId { get; private set; } = string.Empty;
    public string CurrentDeviceName { get; private set; } = string.Empty;
    public string SavedNoiseFilter { get; private set; } = string.Empty;
    public bool IsSoftwareMuted { get; private set; }
    public bool IsHardwareMuted { get; private set; }
    public bool IsMuted { get; private set; }
    public bool IsVideoOn { get; private set; }
    public bool IsTogglingVideo { get; private set; }
    public bool IsScreenSharing { get; private set; }
    public bool IsTogglingScreenShare { get; private set; }
    public string CurrentNoiseSuppression { get; private set; } = "webrtc";

    public bool IsAndroid { get; private set; }
    public bool IsIOS { get; private set; }
    public bool IsNativePlatform { get; private set; }
    public bool IsDesktopClient { get; private set; }
    public bool IsDesktopDirectRoomSupported { get; private set; }
    public DirectRoomClientTelemetry? DirectRoomTelemetry { get; private set; }

    public string GetLocalPlatformTag() =>
        IsAndroid ? "android"
        : IsIOS ? "ios"
        : IsNativePlatform ? "mobile"
        : (IsDesktopClient || IsDesktopDirectRoomSupported) ? "desktop"
        : "web";

    private double GetEstimatedCaptureLatencyMs() =>
        IsAndroid ? 75.0 : (IsIOS ? 40.0 : 25.0);

    private bool ApplyParticipantMediaState(string userId, VoiceStateService.VoiceParticipantMediaState mediaState)
    {
        if (string.IsNullOrEmpty(userId) || LiveKitService.IsSyntheticParticipantIdentity(userId))
        {
            return false;
        }

        bool changed = false;
        bool isLocal = string.Equals(userId, CurrentUserId, StringComparison.Ordinal);

        changed |= mediaState.IsCameraOn
            ? ActiveCameraParticipants.Add(userId)
            : ActiveCameraParticipants.Remove(userId);

        changed |= mediaState.IsScreenSharing
            ? ActiveScreenShareParticipants.Add(userId)
            : ActiveScreenShareParticipants.Remove(userId);

        if (!isLocal)
        {
            if (mediaState.HasScreenAudio)
            {
                changed |= RemoteScreenAudioParticipants.Add(userId);
            }
            else if (RemoteScreenAudioParticipants.Remove(userId))
            {
                MutedRemoteScreenAudioParticipants.Remove(userId);
                changed = true;
            }
        }

        if (mediaState.VideoHeight > 0 && (mediaState.IsCameraOn || mediaState.IsScreenSharing))
        {
            _deviceService.HandleParticipantVideoDimensions(userId, mediaState.VideoHeight, mediaState.VideoFps > 0 ? mediaState.VideoFps : 30);
        }
        else if (!mediaState.IsCameraOn && !mediaState.IsScreenSharing)
        {
            _deviceService.RemoveParticipantVideo(userId);
        }

        return changed;
    }

    private void SyncCallParticipantsFromServer(string? channelId = null)
    {
        var cid = channelId ?? CurrentChannelId;
        if (string.IsNullOrEmpty(cid)) return;
        foreach (var spId in _voiceState.GetVoiceParticipants(cid))
        {
            if (!LiveKitService.IsSyntheticParticipantIdentity(spId))
            {
                CallParticipants.Add(spId);
                if (!string.Equals(spId, CurrentUserId, StringComparison.Ordinal))
                {
                    if (_voiceState.IsUserMuted(cid, spId))
                    {
                        MutedParticipants.Add(spId);
                    }
                    ApplyParticipantMediaState(spId, _voiceState.GetVoiceParticipantMediaState(cid, spId));
                }
            }
        }
    }

    public ConcurrentHashSet<string> CallParticipants { get; } = new();
    public ConcurrentHashSet<string> ActiveSpeakers { get; } = new();
    private readonly ConcurrentHashSet<string> _serverActiveSpeakers = new();
    private readonly ConcurrentHashSet<string> _locallyMutedParticipantIds = new();
    private readonly ConcurrentHashSet<string> _mutedRoommateUserIds = new();
    private bool _localIsSpeaking;
    private bool _wasISpeaking;
    public ConcurrentHashSet<string> MutedParticipants { get; } = new();
    public ConcurrentHashSet<string> RemoteScreenAudioParticipants { get; } = new();
    public ConcurrentHashSet<string> MutedRemoteScreenAudioParticipants { get; } = new();
    public ConcurrentHashSet<string> ActiveCameraParticipants { get; } = new();
    public ConcurrentHashSet<string> ActiveScreenShareParticipants { get; } = new();
    private readonly ConcurrentHashSet<string> _liveKitRemoteParticipantIds = new();
    private readonly ConcurrentHashSet<string> _mockConnectedParticipants = new();
    private bool _roomConnectedCallbackReceived;

    public bool IsParticipantMuted(string? participantId)
    {
        if (string.IsNullOrEmpty(participantId))
        {
            return false;
        }

        if (string.Equals(participantId, CurrentUserId, StringComparison.Ordinal))
        {
            return IsMuted;
        }

        return MutedParticipants.Contains(participantId);
    }

    private bool IsOwnedBySession(string? channelId, string? userId, string? sessionId)
    {
        if (string.IsNullOrWhiteSpace(channelId) ||
            string.IsNullOrWhiteSpace(userId) ||
            string.IsNullOrWhiteSpace(sessionId))
        {
            return true;
        }

        var activeSessionId = _voiceState.GetVoiceParticipantSessionId(channelId, userId);
        return string.IsNullOrWhiteSpace(activeSessionId) ||
               string.Equals(activeSessionId, sessionId.Trim(), StringComparison.Ordinal);
    }

    public bool HasAnyActiveVideoOrScreen =>
        IsVideoOn || IsScreenSharing || ActiveCameraParticipants.Count > 0 || ActiveScreenShareParticipants.Count > 0;

    public GlobalVoiceService(
        IVoiceJsInteropService jsInterop,
        VoiceChannelService voiceChannelService,
        VoiceStateService voiceState,
        ChatStateService chatState,
        ILogger<GlobalVoiceService> logger,
        IVoiceDeviceService? deviceService = null,
        PhysicalVoiceRoomService? physicalRoomService = null,
        PhysicalRoomDirectSessionService? directSessionService = null)
    {
        _jsInterop = jsInterop ?? throw new ArgumentNullException(nameof(jsInterop));
        _voiceChannelService = voiceChannelService ?? throw new ArgumentNullException(nameof(voiceChannelService));
        _voiceState = voiceState ?? throw new ArgumentNullException(nameof(voiceState));
        _chatState = chatState ?? throw new ArgumentNullException(nameof(chatState));
        _logger = logger ?? NullLogger<GlobalVoiceService>.Instance;
        _deviceService = deviceService ?? new VoiceDeviceService(_jsInterop, _logger);
        _physicalRoomService = physicalRoomService;
        _directSessionService = directSessionService;

        _voiceState.VoiceMemberJoined += OnServerVoiceMemberJoined;
        _voiceState.VoiceMemberLeft += OnServerVoiceMemberLeft;
        _voiceState.VoiceActiveSpeakersChanged += OnServerVoiceActiveSpeakersChanged;
        _voiceState.VoiceUserMutedChanged += OnServerVoiceUserMutedChanged;
        _voiceState.VoiceUserMediaStateChanged += OnServerVoiceUserMediaStateChanged;
        _voiceState.PhysicalRoomsChanged += OnServerPhysicalRoomsChanged;
        _chatState.MessageEdited += OnServerMessageEdited;
        if (_directSessionService != null)
        {
            _directSessionService.DirectSessionPeersChanged += OnDirectSessionPeersChanged;
        }
    }

    private void OnServerMessageEdited(ChatMessage edited)
    {
        if (edited == null ||
            !IsInVoiceCall ||
            !string.Equals(CurrentChannelId, edited.ChannelId, StringComparison.Ordinal) ||
            !string.Equals(edited.MessageType, ChatMessageType.CallInvite, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(edited.CallStatus, CallStatus.Declined, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        // Only auto-disconnect the caller in a 1-on-1 DM call when sitting alone in the voice room
        var serverParticipants = _voiceState.GetVoiceParticipants(edited.ChannelId);
        int humanCount = serverParticipants.Count(p => !LiveKitService.IsSyntheticParticipantIdentity(p));
        int localCount = CallParticipants.Count(p => !LiveKitService.IsSyntheticParticipantIdentity(p));
        if (humanCount <= 1 && localCount <= 1)
        {
            OnOutgoingCallDeclined?.Invoke(edited.ChannelId);
        }
    }

    private void OnDirectSessionPeersChanged(string channelId, string roomId)
    {
        if (_suppressSelfDirectSessionPeersChanged)
        {
            return;
        }
        if (IsInVoiceCall && string.Equals(CurrentChannelId, channelId, StringComparison.Ordinal))
        {
            _ = SyncDirectRoomSessionOnlyAsync();
        }
    }

    private void OnServerPhysicalRoomsChanged()
    {
        if (IsInVoiceCall)
        {
            _ = RefreshPhysicalRoomStateAsync();
            NotifyDiagnosticsOrSettingsChanged();
        }
    }

    private void OnServerVoiceActiveSpeakersChanged(string channelId, string[] speakers)
    {
        if (!IsInVoiceCall ||
            string.IsNullOrEmpty(CurrentChannelId) ||
            !string.Equals(CurrentChannelId, channelId, StringComparison.Ordinal))
        {
            return;
        }

        var nextServerSpeakers = (speakers ?? Array.Empty<string>())
            .Where(id => !string.IsNullOrWhiteSpace(id) &&
                         !LiveKitService.IsSyntheticParticipantIdentity(id))
            .ToHashSet(StringComparer.Ordinal);

        _serverActiveSpeakers.ReplaceWith(nextServerSpeakers);
        RecomputeActiveSpeakers();
    }

    private void OnServerVoiceUserMutedChanged(string channelId, string userId, bool isMuted)
    {
        if (IsInVoiceCall && CurrentChannelId == channelId && !string.IsNullOrEmpty(userId))
        {
            bool changed = isMuted ? MutedParticipants.Add(userId) : MutedParticipants.Remove(userId);
            if (isMuted)
            {
                _serverActiveSpeakers.Remove(userId);
                if (string.Equals(userId, CurrentUserId, StringComparison.Ordinal))
                {
                    _localIsSpeaking = false;
                }
            }
            RecomputeActiveSpeakers();
            if (userId != CurrentUserId)
            {
                _ = SyncDirectRoomSessionOnlyAsync();
            }
            if (changed)
            {
                NotifyStateChanged();
            }
        }
    }

    private void OnServerVoiceUserMediaStateChanged(string channelId, string userId, VoiceStateService.VoiceParticipantMediaState mediaState)
    {
        if (!IsInVoiceCall ||
            !string.Equals(CurrentChannelId, channelId, StringComparison.Ordinal) ||
            string.IsNullOrEmpty(userId) ||
            string.Equals(userId, CurrentUserId, StringComparison.Ordinal) ||
            LiveKitService.IsSyntheticParticipantIdentity(userId))
        {
            return;
        }

        if (ApplyParticipantMediaState(userId, mediaState))
        {
            NotifyStateChanged();
        }
    }

    private void OnServerVoiceMemberJoined(string channelId, string userId)
    {
        if (IsInVoiceCall && CurrentChannelId == channelId && !string.IsNullOrEmpty(userId) && userId != CurrentUserId && !LiveKitService.IsSyntheticParticipantIdentity(userId))
        {
            CallParticipants.Add(userId);
            if (_voiceState.IsUserMuted(channelId, userId))
            {
                MutedParticipants.Add(userId);
            }
            ApplyParticipantMediaState(userId, _voiceState.GetVoiceParticipantMediaState(channelId, userId));
            RecomputeActiveSpeakers();
            _ = RefreshPhysicalRoomStateAsync();
            NotifyStateChanged();
        }
    }

    private void OnServerVoiceMemberLeft(string channelId, string userId)
    {
        if (!IsInVoiceCall || CurrentChannelId != channelId || string.IsNullOrEmpty(userId) || userId == CurrentUserId)
        {
            return;
        }

        RemoveRemoteParticipantUiState(userId);
        _ = RefreshPhysicalRoomStateAsync();
        NotifyStateChanged();
    }

    private void RemoveRemoteParticipantUiState(string userId)
    {
        _liveKitRemoteParticipantIds.Remove(userId);
        CallParticipants.Remove(userId);
        _serverActiveSpeakers.Remove(userId);
        bool speakerRemoved = ActiveSpeakers.Remove(userId);
        MutedParticipants.Remove(userId);
        RemoteScreenAudioParticipants.Remove(userId);
        MutedRemoteScreenAudioParticipants.Remove(userId);
        ActiveCameraParticipants.Remove(userId);
        ActiveScreenShareParticipants.Remove(userId);
        _deviceService.RemoveParticipantVideo(userId);
        if (speakerRemoved)
        {
            NotifyActiveSpeakersUpdated();
        }
    }

    public async Task<(string DeviceId, string DeviceName)> GetOrCreatePhysicalDeviceIdentityAsync()
    {
        if (!string.IsNullOrWhiteSpace(CurrentDeviceId))
        {
            return (CurrentDeviceId, CurrentDeviceName);
        }

        try
        {
            if (!_jsInterop.IsModuleLoaded)
            {
                await InitializeModuleAsync();
                if (!string.IsNullOrWhiteSpace(CurrentDeviceId))
                {
                    return (CurrentDeviceId, CurrentDeviceName);
                }
            }

            var identity = await _jsInterop.GetOrCreatePhysicalDeviceIdentityAsync();
            if (identity != null && !string.IsNullOrWhiteSpace(identity.DeviceId))
            {
                CurrentDeviceId = identity.DeviceId;
                CurrentDeviceName = string.IsNullOrWhiteSpace(identity.DeviceName) ? "This Device" : identity.DeviceName;
                return (CurrentDeviceId, CurrentDeviceName);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to query physical device identity from JS interop");
        }

        return (CurrentDeviceId, CurrentDeviceName);
    }

    public async Task RefreshPhysicalRoomStateAsync()
    {
        if (!IsInVoiceCall || string.IsNullOrEmpty(CurrentChannelId) || string.IsNullOrEmpty(CurrentUserId) || !_jsInterop.IsModuleLoaded || _physicalRoomService == null)
        {
            return;
        }

        try
        {
            var activeGroups = _physicalRoomService.GetActiveCoLocatedRoomGroups(CurrentChannelId);
            var coLocatedUserIds = activeGroups.Values
                .SelectMany(g => g.ActiveUserIds)
                .Distinct(StringComparer.Ordinal)
                .ToArray();

            string[] mutedRoommates = Array.Empty<string>();
            if (!string.IsNullOrWhiteSpace(CurrentDeviceId))
            {
                var myEnrollment = _physicalRoomService.GetEnrollment(CurrentUserId, CurrentDeviceId);
                if (myEnrollment != null && myEnrollment.MuteUsersInMyRoom)
                {
                    mutedRoommates = _physicalRoomService.GetActiveRoommatesInChannel(CurrentChannelId, CurrentUserId, CurrentDeviceId)
                        .ToArray();
                }
            }

            _mutedRoommateUserIds.ReplaceWith(mutedRoommates);
            RecomputeActiveSpeakers();
            await _jsInterop.SyncPhysicalRoomStateAsync(coLocatedUserIds, mutedRoommates);
            await SyncDirectRoomSessionOnlyAsync();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to sync physical room state with JS interop");
        }
    }

    private bool _checkedDesktopDirectSupport;
    private bool _suppressSelfDirectSessionPeersChanged;
    private string[]? _cachedDirectEndpoints;
    private string _cachedDirectEndpointsSessionId = string.Empty;
    private readonly SemaphoreSlim _directSyncLock = new(1, 1);
    private string? _lastSyncedDirectPayloadSignature;

    private static string BuildDirectSessionPayloadSignature(PhysicalRoomDirectSessionDescriptor? descriptor, bool isMuted)
    {
        if (descriptor == null)
        {
            return "none";
        }

        var peerPart = descriptor.Peers.Count == 0
            ? string.Empty
            : string.Join(",", descriptor.Peers
                .OrderBy(p => p.UserId, StringComparer.Ordinal)
                .Select(p => $"{p.UserId}:{p.PeerToken}:{string.Join(";", p.Endpoints)}"));

        return $"{descriptor.ChannelId}|{descriptor.RoomId}|{(int)descriptor.AudioMode}|{descriptor.KeyEpoch}|{descriptor.LocalPeerToken}|{descriptor.SessionKeyBase64}|{(descriptor.IsSuspendedForMixedRoomClients ? 1 : 0)}|{(isMuted ? 1 : 0)}|{peerPart}";
    }

    private async Task<bool> SyncDescriptorToJsIfChangedAsync(PhysicalRoomDirectSessionDescriptor? descriptor, bool isMuted)
    {
        var nextSig = BuildDirectSessionPayloadSignature(descriptor, isMuted);
        if (descriptor == null && _lastSyncedDirectPayloadSignature == null)
        {
            // Direct LAN was never started in this voice session; skip redundant stop RPC.
            _lastSyncedDirectPayloadSignature = nextSig;
            return false;
        }

        if (string.Equals(_lastSyncedDirectPayloadSignature, nextSig, StringComparison.Ordinal))
        {
            return false;
        }

        await _jsInterop.SyncDirectRoomSessionAsync(descriptor, isMuted);
        _lastSyncedDirectPayloadSignature = nextSig;
        return true;
    }

    private async Task SyncDirectRoomSessionOnlyAsync()
    {
        if (!IsInVoiceCall ||
            string.IsNullOrEmpty(CurrentChannelId) ||
            string.IsNullOrEmpty(CurrentUserId) ||
            !_jsInterop.IsModuleLoaded)
        {
            return;
        }

        await _directSyncLock.WaitAsync();
        try
        {
            if (!IsInVoiceCall ||
                string.IsNullOrEmpty(CurrentChannelId) ||
                string.IsNullOrEmpty(CurrentUserId) ||
                !_jsInterop.IsModuleLoaded)
            {
                return;
            }

            if (!_checkedDesktopDirectSupport)
            {
                _checkedDesktopDirectSupport = true;
                IsDesktopDirectRoomSupported = await _jsInterop.IsDesktopDirectRoomSupportedAsync();
            }

            if (!IsDesktopDirectRoomSupported ||
                _physicalRoomService == null ||
                _directSessionService == null ||
                string.IsNullOrWhiteSpace(CurrentDeviceId))
            {
                _cachedDirectEndpoints = null;
                bool clearedTelemetry = DirectRoomTelemetry != null;
                DirectRoomTelemetry = null;
                bool synced = await SyncDescriptorToJsIfChangedAsync(null, IsMuted);
                if (synced || clearedTelemetry)
                {
                    NotifyDiagnosticsOrSettingsChanged();
                }
                return;
            }

            var channelId = CurrentChannelId;
            var userId = CurrentUserId;
            var deviceId = CurrentDeviceId;
            var sessionId = CurrentVoiceSessionId;

            if (!IsOwnedBySession(channelId, userId, sessionId))
            {
                return;
            }

            var myRoom = _physicalRoomService.GetRoomForUserDevice(userId, deviceId);
            var myEnrollment = _physicalRoomService.GetEnrollment(userId, deviceId);
            if (myRoom == null ||
                myEnrollment == null ||
                myRoom.AudioMode == PhysicalRoomAudioMode.ServerCleanup ||
                myEnrollment.MuteUsersInMyRoom)
            {
                _cachedDirectEndpoints = null;
                _directSessionService.RemoveParticipantEndpoints(channelId, userId, sessionId);
                bool clearedTelemetry = DirectRoomTelemetry != null;
                DirectRoomTelemetry = null;
                bool synced = await SyncDescriptorToJsIfChangedAsync(null, IsMuted);
                if (synced || clearedTelemetry)
                {
                    NotifyDiagnosticsOrSettingsChanged();
                }
                return;
            }

            string[]? endpoints = (_cachedDirectEndpoints is { Length: > 0 } &&
                                   string.Equals(_cachedDirectEndpointsSessionId, sessionId, StringComparison.Ordinal))
                ? _cachedDirectEndpoints
                : null;

            if (endpoints == null)
            {
                endpoints = await _jsInterop.PrepareDirectRoomEndpointsAsync();
                if (!IsInVoiceCall ||
                    !string.Equals(CurrentChannelId, channelId, StringComparison.Ordinal) ||
                    !string.Equals(CurrentVoiceSessionId, sessionId, StringComparison.Ordinal) ||
                    !IsOwnedBySession(channelId, userId, sessionId))
                {
                    return;
                }
                if (endpoints is { Length: > 0 })
                {
                    _cachedDirectEndpoints = endpoints;
                    _cachedDirectEndpointsSessionId = sessionId;
                }
            }

            if (endpoints != null && endpoints.Length > 0)
            {
                _suppressSelfDirectSessionPeersChanged = true;
                try
                {
                    _directSessionService.UpdateParticipantEndpoints(channelId, userId, deviceId, endpoints, sessionId);
                }
                finally
                {
                    _suppressSelfDirectSessionPeersChanged = false;
                }
                var descriptor = _directSessionService.BuildSessionDescriptorForParticipant(channelId, userId, deviceId);
                bool clearedTelemetry = false;
                if (descriptor == null || descriptor.Peers.Count == 0)
                {
                    clearedTelemetry = DirectRoomTelemetry != null;
                    DirectRoomTelemetry = null;
                }
                bool synced = await SyncDescriptorToJsIfChangedAsync(descriptor, IsMuted);
                if (synced || clearedTelemetry)
                {
                    NotifyDiagnosticsOrSettingsChanged();
                }
            }
            else
            {
                _cachedDirectEndpoints = null;
                _directSessionService.RemoveParticipantEndpoints(channelId, userId, sessionId);
                bool clearedTelemetry = DirectRoomTelemetry != null;
                DirectRoomTelemetry = null;
                bool synced = await SyncDescriptorToJsIfChangedAsync(null, IsMuted);
                if (synced || clearedTelemetry)
                {
                    NotifyDiagnosticsOrSettingsChanged();
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to sync direct room LAN session with JS interop");
        }
        finally
        {
            _directSyncLock.Release();
        }
    }

    /// <summary>
    /// Evaluates the live operating or fallback state for a Physical Room configured for Low-Latency Direct mode.
    /// </summary>
    public DirectRoomOperatingStatus GetPhysicalRoomDirectStatus(string? roomId)
    {
        if (string.IsNullOrWhiteSpace(roomId) || _physicalRoomService == null)
        {
            return DirectRoomOperatingStatus.NotApplicable;
        }

        var room = _physicalRoomService.GetRoomById(roomId);
        if (room == null || room.AudioMode == PhysicalRoomAudioMode.ServerCleanup)
        {
            return DirectRoomOperatingStatus.NotApplicable;
        }

        if (!IsInVoiceCall ||
            string.IsNullOrWhiteSpace(CurrentChannelId) ||
            string.IsNullOrWhiteSpace(CurrentUserId) ||
            string.IsNullOrWhiteSpace(CurrentDeviceId))
        {
            if (_checkedDesktopDirectSupport && !IsDesktopDirectRoomSupported)
            {
                return DirectRoomOperatingStatus.FallbackNotDesktopClient;
            }

            return DirectRoomOperatingStatus.NotApplicable;
        }

        var myRoom = _physicalRoomService.GetRoomForUserDevice(CurrentUserId, CurrentDeviceId);
        var myEnrollment = _physicalRoomService.GetEnrollment(CurrentUserId, CurrentDeviceId);
        if (myRoom == null || !string.Equals(myRoom.Id, roomId, StringComparison.Ordinal) || myEnrollment == null)
        {
            return DirectRoomOperatingStatus.NotApplicable;
        }

        if (myEnrollment.MuteUsersInMyRoom)
        {
            return DirectRoomOperatingStatus.MutedRoommatesLocal;
        }

        if (!IsDesktopDirectRoomSupported)
        {
            return DirectRoomOperatingStatus.FallbackNotDesktopClient;
        }

        var evaluatedStatus = _directSessionService?.EvaluateDesktopRoomFallbackStatus(CurrentChannelId, CurrentUserId, CurrentDeviceId)
            ?? DirectRoomOperatingStatus.StandbyWaitingForRoommate;

        if (evaluatedStatus == DirectRoomOperatingStatus.FallbackNonDesktopRoommateInRoom)
        {
            return DirectRoomOperatingStatus.FallbackNonDesktopRoommateInRoom;
        }

        if (DirectRoomTelemetry?.ActiveDirectPeerUserIds is { Length: > 0 })
        {
            return DirectRoomOperatingStatus.ActiveDirectLan;
        }

        return evaluatedStatus;
    }

    public async Task EnsureModuleInitializedAsync() => await InitializeModuleAsync();

    public async Task PrepareConnectionAsync(string url, string? token = null)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return;
        }

        try
        {
            if (!_jsInterop.IsModuleLoaded)
            {
                await InitializeModuleAsync();
            }

            await _jsInterop.PrepareVoiceConnectionAsync(url, token);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[VoiceService] Pre-warming LiveKit connection failed: {Message}", ex.Message);
        }
    }

    private readonly SemaphoreSlim _moduleInitLock = new(1, 1);

    private async Task InitializeModuleAsync()
    {
        if (_jsInterop.IsModuleLoaded) return;
        await _moduleInitLock.WaitAsync();
        try
        {
            if (_jsInterop.IsModuleLoaded) return;
            _objRef ??= DotNetObjectReference.Create(this);
            await _jsInterop.InitializeAsync(_objRef);
            if (!_jsInterop.IsModuleLoaded)
            {
                return;
            }
            var bootstrap = await _jsInterop.GetClientVoiceBootstrapAsync();
            if (bootstrap != null)
            {
                IsIOS = bootstrap.IsIOS;
                IsAndroid = bootstrap.IsAndroid;
                IsNativePlatform = bootstrap.IsNativePlatform;
                IsDesktopClient = bootstrap.IsDesktopClient || bootstrap.IsDesktopDirectRoomSupported;
                IsDesktopDirectRoomSupported = bootstrap.IsDesktopDirectRoomSupported;
                _checkedDesktopDirectSupport = true;
                SavedNoiseFilter = string.IsNullOrWhiteSpace(bootstrap.SavedNoiseFilter)
                    ? "rnnoise"
                    : bootstrap.SavedNoiseFilter;
                if (string.IsNullOrWhiteSpace(CurrentDeviceId) && !string.IsNullOrWhiteSpace(bootstrap.DeviceId))
                {
                    CurrentDeviceId = bootstrap.DeviceId;
                    CurrentDeviceName = string.IsNullOrWhiteSpace(bootstrap.DeviceName) ? "This Device" : bootstrap.DeviceName;
                }
            }
            else
            {
                IsIOS = await _jsInterop.IsAppleMobileDeviceAsync();
                IsAndroid = await _jsInterop.IsAndroidDeviceAsync();
                IsNativePlatform = await _jsInterop.IsNativePlatformAsync();
                IsDesktopDirectRoomSupported = await _jsInterop.IsDesktopDirectRoomSupportedAsync();
                _checkedDesktopDirectSupport = true;
            }
        }
        catch (Exception e)
        {
            _logger.LogError(e, "GlobalVoiceService JS load failed: {Message}", e.Message);
            throw;
        }
        finally
        {
            _moduleInitLock.Release();
        }
    }

    public void ApplyMockConnectedState(string channelId, string userId, IEnumerable<string> mockParticipants)
    {
        CurrentChannelId = channelId;
        CurrentUserId = userId;
        IsInVoiceCall = true;
        IsConnecting = false;
        CallParticipants.Clear();
        _mockConnectedParticipants.Clear();
        foreach (var p in mockParticipants)
        {
            CallParticipants.Add(p);
            _mockConnectedParticipants.Add(p);
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
        _mockConnectedParticipants.Clear();
        _serverActiveSpeakers.Clear();
        _locallyMutedParticipantIds.Clear();
        _mutedRoommateUserIds.Clear();
        _localIsSpeaking = false;
        _wasISpeaking = false;
        ActiveSpeakers.Clear();
        NotifyStateChanged();
    }

    private bool _connectInProgress;

    /// <summary>
    /// Marks <see cref="IsConnecting"/> immediately when a UI join action starts so the connecting stage
    /// has a single authoritative source of truth across Chat, GlobalVoiceBar, and VoicePipOverlay.
    /// Returns false if a connection attempt is already active or already connected to <paramref name="channelId"/>.
    /// </summary>
    public bool TryBeginConnecting(string channelId)
    {
        if (string.IsNullOrWhiteSpace(channelId))
        {
            return false;
        }
        if (IsConnecting || _connectInProgress)
        {
            return false;
        }
        if (IsInVoiceCall && string.Equals(CurrentChannelId, channelId, StringComparison.Ordinal))
        {
            return false;
        }

        bool needsAsyncPrep = !_jsInterop.IsModuleLoaded
            || IsInVoiceCall
            || string.IsNullOrWhiteSpace(CurrentDeviceId);
        IsConnecting = true;
        ConnectingChannelId = channelId;
        ConnectingStage = needsAsyncPrep
            ? VoiceConnectingStage.PreparingAudio
            : VoiceConnectingStage.ConnectingToServer;
        RequiresIntegrityCheck = false;
        NotifyStateChanged();
        return true;
    }

    /// <summary>
    /// Releases a pre-connect reservation created by <see cref="TryBeginConnecting"/> when the caller
    /// exits before entering <see cref="ConnectAsync"/> (e.g. demo mode, read-only user, or config error).
    /// </summary>
    public void CancelPreparingConnect(string channelId)
    {
        if (_connectInProgress)
        {
            return;
        }
        if (!IsConnecting || !string.Equals(ConnectingChannelId, channelId, StringComparison.Ordinal))
        {
            return;
        }

        IsConnecting = false;
        ConnectingStage = VoiceConnectingStage.None;
        ConnectingChannelId = null;
        NotifyStateChanged();
    }

    public async Task ConnectAsync(VoiceConnectionOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _mockConnectedParticipants.Clear();

        if (_connectInProgress)
        {
            _logger.LogInformation("ConnectAsync called while already connecting. Ignoring duplicate attempt.");
            return;
        }

        bool alreadyPreparingThisChannel = IsConnecting
            && string.Equals(ConnectingChannelId, options.ChannelId, StringComparison.Ordinal);
        if (IsConnecting && !alreadyPreparingThisChannel)
        {
            _logger.LogInformation("ConnectAsync called while already connecting to another channel. Ignoring duplicate attempt.");
            return;
        }

        if (IsInVoiceCall && CurrentChannelId == options.ChannelId)
        {
            _logger.LogInformation("ConnectAsync called while already connected to channel {ChannelId}. Ignoring duplicate attempt.", options.ChannelId);
            return;
        }

        bool hasAsyncPreConnectWork = !_jsInterop.IsModuleLoaded
            || IsInVoiceCall
            || (string.IsNullOrWhiteSpace(options.DeviceId) && string.IsNullOrWhiteSpace(CurrentDeviceId));
        _connectInProgress = true;
        IsConnecting = true;
        ConnectingChannelId = options.ChannelId;
        ConnectingStage = hasAsyncPreConnectWork
            ? VoiceConnectingStage.PreparingAudio
            : VoiceConnectingStage.ConnectingToServer;
        RequiresIntegrityCheck = false;
        if (!alreadyPreparingThisChannel && hasAsyncPreConnectWork)
        {
            NotifyStateChanged();
        }

        bool prePopulatedSessionContext = false;
        try
        {
            if (!_jsInterop.IsModuleLoaded) await InitializeModuleAsync();
            if (!_jsInterop.IsModuleLoaded)
            {
                throw new InvalidOperationException("Voice module failed to initialize because the connection was interrupted. Please try again.");
            }

            var noiseSuppression = options.NoiseSuppression;
            if (IsIOS && noiseSuppression != "none")
            {
                noiseSuppression = "webrtc";
            }

            CurrentNoiseSuppression = noiseSuppression;

            if (IsInVoiceCall)
            {
                await DisconnectAsync();
                IsConnecting = true;
                ConnectingChannelId = options.ChannelId;
                ConnectingStage = VoiceConnectingStage.PreparingAudio;
            }

            CurrentChannelId = options.ChannelId;
            CurrentUserId = options.UserId;
            CurrentVoiceSessionId = string.IsNullOrWhiteSpace(options.VoiceSessionId)
                ? Guid.NewGuid().ToString("N")
                : options.VoiceSessionId.Trim();
            if (!string.IsNullOrWhiteSpace(options.DeviceId))
            {
                CurrentDeviceId = options.DeviceId;
                CurrentDeviceName = string.IsNullOrWhiteSpace(options.DeviceName) ? "This Device" : options.DeviceName;
            }
            else if (string.IsNullOrWhiteSpace(CurrentDeviceId))
            {
                await GetOrCreatePhysicalDeviceIdentityAsync();
            }
            CallParticipants.Clear();
            _serverActiveSpeakers.Clear();
            _locallyMutedParticipantIds.Clear();
            _mutedRoommateUserIds.Clear();
            _localIsSpeaking = false;
            _wasISpeaking = false;
            ActiveSpeakers.Clear();
            MutedParticipants.Clear();
            _liveKitRemoteParticipantIds.Clear();
            CallParticipants.Add(options.UserId);
            SyncCallParticipantsFromServer(options.ChannelId);
            ApplyLocallyMutedParticipants(options.VoiceSettings);

            foreach (var smId in _voiceState.GetMutedUsers(options.ChannelId))
            {
                MutedParticipants.Add(smId);
            }

            IsSoftwareMuted = options.InitialMuted;
            IsHardwareMuted = false;
            IsMuted = options.InitialMuted;
            if (IsMuted)
            {
                MutedParticipants.Add(options.UserId);
            }
            IsVideoOn = false;
            IsScreenSharing = false;

            _objRef ??= DotNetObjectReference.Create(this);

            string? settingsJson = null;
            if (options.VoiceSettings != null)
            {
                try
                {
                    settingsJson = System.Text.Json.JsonSerializer.Serialize(options.VoiceSettings);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to serialize voice settings for pre-join hydration");
                }
            }

            string[] preJoinCoLocated = Array.Empty<string>();
            string[] preJoinMutedRoommates = Array.Empty<string>();
            if (_physicalRoomService != null)
            {
                (preJoinCoLocated, preJoinMutedRoommates) = _physicalRoomService.PreviewClientPhysicalRoomStateForJoin(
                    options.ChannelId,
                    options.UserId,
                    CurrentDeviceId);
            }
            _mutedRoommateUserIds.ReplaceWith(preJoinMutedRoommates);

            // Advance stage BEFORE pre-populating server session metadata so any VoiceState event subscriber
            // (e.g. Chat.razor) sees ConnectingToServer immediately and coalesces with NotifyStateChanged().
            bool stageChanged = ConnectingStage != VoiceConnectingStage.ConnectingToServer;
            ConnectingStage = VoiceConnectingStage.ConnectingToServer;
            PopulateLocalServerSessionContext(options.ChannelId, options.UserId);
            prePopulatedSessionContext = true;
            if (stageChanged || !alreadyPreparingThisChannel)
            {
                NotifyStateChanged();
            }

            var preJoinContext = new VoicePreJoinContext(
                settingsJson,
                preJoinCoLocated,
                preJoinMutedRoommates,
                options.PreferredMicDeviceId ?? string.Empty,
                IsMuted);

            _roomConnectedCallbackReceived = false;
            IsInVoiceCall = await _jsInterop.ConnectToVoiceAsync(
                options.Token,
                options.Url,
                _objRef,
                options.AudioBitrateKbps,
                CurrentNoiseSuppression,
                preJoinContext);

            if (!IsInVoiceCall && _roomConnectedCallbackReceived)
            {
                IsInVoiceCall = true;
            }

            if (IsInVoiceCall &&
                _voiceState.IsVoiceSessionClosed(CurrentVoiceSessionId) &&
                IsOwnedBySession(options.ChannelId, options.UserId, CurrentVoiceSessionId))
            {
                _voiceState.ReopenVoiceSession(options.ChannelId, options.UserId, CurrentVoiceSessionId);
            }

            if (IsInVoiceCall)
            {
                SyncCallParticipantsFromServer(options.ChannelId);
                OnServerVoiceActiveSpeakersChanged(options.ChannelId, _voiceState.GetActiveSpeakers(options.ChannelId));
            }

            if (IsInVoiceCall &&
                !_voiceState.IsVoiceSessionClosed(CurrentVoiceSessionId) &&
                IsOwnedBySession(options.ChannelId, options.UserId, CurrentVoiceSessionId))
            {
                ConnectingStage = VoiceConnectingStage.ConfiguringRoomAudio;

                if (IsSoftwareMuted || IsMuted)
                {
                    await _jsInterop.SetMicrophoneEnabledAsync(false);
                }
                await _voiceChannelService.JoinVoiceAsync(options.ChannelId, options.UserId);
                if (_physicalRoomService != null &&
                    !string.IsNullOrWhiteSpace(CurrentDeviceId) &&
                    !string.IsNullOrWhiteSpace(CurrentDeviceName))
                {
                    await _physicalRoomService.SyncEnrolledDeviceNameAsync(options.UserId, CurrentDeviceId, CurrentDeviceName);
                }
                await RefreshPhysicalRoomStateAsync();
                _ = _voiceChannelService.SetUserMutedAsync(options.ChannelId, options.UserId, IsMuted);

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
            else
            {
                IsInVoiceCall = false;
                await _jsInterop.DisconnectFromVoiceAsync();
            }
        }
        finally
        {
            if (!IsInVoiceCall)
            {
                if (prePopulatedSessionContext)
                {
                    _voiceState.TryNotifyVoiceMemberLeft(options.ChannelId, options.UserId, CurrentVoiceSessionId, markSessionClosed: false);
                }
                if (!string.IsNullOrEmpty(CurrentChannelId))
                {
                    ResetState();
                }
            }
            _connectInProgress = false;
            IsConnecting = false;
            ConnectingStage = VoiceConnectingStage.None;
            ConnectingChannelId = null;
            NotifyStateChanged();
        }
    }

    public async Task DisconnectAsync()
    {
        var uid = CurrentUserId;
        var cid = CurrentChannelId;
        var sessionId = CurrentVoiceSessionId;
        var wasInCall = IsInVoiceCall;

        if (!string.IsNullOrEmpty(uid) && !string.IsNullOrEmpty(cid) && IsOwnedBySession(cid, uid, sessionId))
        {
            _directSessionService?.RemoveParticipantEndpoints(cid, uid, sessionId);
        }

        ResetState();
        NotifyStateChanged();

        await _jsInterop.DisconnectFromVoiceAsync();

        if (!string.IsNullOrEmpty(uid) && !string.IsNullOrEmpty(cid) && IsOwnedBySession(cid, uid, sessionId))
        {
            await LeaveServerVoiceSessionAsync(cid, uid, sessionId, removeEndpoints: false, "[VoiceService] Failed to leave voice channel: {Message}");
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
            if (!IsMuted)
            {
                IsSoftwareMuted = true;
                ApplyEffectiveLocalMuteState(syncChannel: true);
                NotifyStateChanged();

                if (_jsInterop.IsModuleLoaded)
                {
                    await _jsInterop.SetMicrophoneEnabledAsync(false);
                }
            }
            else if (IsSoftwareMuted)
            {
                IsSoftwareMuted = false;
                ApplyEffectiveLocalMuteState(syncChannel: true);
                NotifyStateChanged();

                if (_jsInterop.IsModuleLoaded)
                {
                    await _jsInterop.SetMicrophoneEnabledAsync(true);
                }

                if (IsHardwareMuted)
                {
                    OnHardwareMuteBlockedUnmute?.Invoke();
                }
            }
            else if (IsHardwareMuted)
            {
                // User attempted to unmute in UI while physical headset boom/switch is hardware-muted.
                ApplyEffectiveLocalMuteState(syncChannel: true);
                OnHardwareMuteBlockedUnmute?.Invoke();
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Voice] ToggleMute failed: {Message}", ex.Message);
        }
        NotifyStateChanged();
    }

    private void ApplyEffectiveLocalMuteState(bool syncChannel)
    {
        bool effectiveMuted = IsSoftwareMuted || IsHardwareMuted;
        IsMuted = effectiveMuted;
        bool ownsSession = IsOwnedBySession(CurrentChannelId, CurrentUserId, CurrentVoiceSessionId);

        if (effectiveMuted)
        {
            _localIsSpeaking = false;
            _wasISpeaking = false;
            if (CurrentUserId != null)
            {
                _serverActiveSpeakers.Remove(CurrentUserId);
                MutedParticipants.Add(CurrentUserId);
            }
            RecomputeActiveSpeakers();
            if (syncChannel && ownsSession && CurrentChannelId != null && CurrentUserId != null)
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
            RecomputeActiveSpeakers();
            if (syncChannel && ownsSession && CurrentChannelId != null && CurrentUserId != null)
            {
                _ = _voiceChannelService.SetUserMutedAsync(CurrentChannelId, CurrentUserId, false);
            }
        }
    }

    [JSInvokable]
    public Task RequestExternalDisconnect() => DisconnectAsync();

    public async Task ToggleVideoAsync(string quality = "Auto", int fps = 30, string activeCameraDeviceId = "")
    {
        if (IsTogglingVideo) return;
        IsTogglingVideo = true;
        NotifyStateChanged();
        try
        {
            bool newVideoState = !IsVideoOn;
            if (newVideoState)
            {
                var result = await _jsInterop.SetCameraEnabledAsync(newVideoState, quality, fps, activeCameraDeviceId);
                var cameras = result?.Cameras?.Take(100).ToList() ?? [];
                var activeId = result?.ActiveCameraDeviceId ?? "";
                _deviceService.HandleCamerasUpdated(cameras, activeId);
                IsVideoOn = true;
                if (!string.IsNullOrEmpty(CurrentChannelId) && !string.IsNullOrEmpty(CurrentUserId))
                {
                    ActiveCameraParticipants.Add(CurrentUserId);
                    _voiceState.TrySetVoiceParticipantMediaState(CurrentChannelId, CurrentUserId, CurrentVoiceSessionId, isCameraOn: true);
                }
            }
            else
            {
                await _jsInterop.SetCameraEnabledAsync(newVideoState, quality, fps, activeCameraDeviceId);
                _deviceService.HandleCamerasUpdated([], "");
                IsVideoOn = false;
                if (!string.IsNullOrEmpty(CurrentUserId))
                {
                    ActiveCameraParticipants.Remove(CurrentUserId);
                    if (!string.IsNullOrEmpty(CurrentChannelId))
                    {
                        _voiceState.TrySetVoiceParticipantMediaState(CurrentChannelId, CurrentUserId, CurrentVoiceSessionId, isCameraOn: false);
                    }
                    if (!IsScreenSharing && !ActiveScreenShareParticipants.Contains(CurrentUserId))
                    {
                        _deviceService.RemoveParticipantVideo(CurrentUserId);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Voice] ToggleVideo failed: {Message}", ex.Message);
            IsVideoOn = false;
            if (!string.IsNullOrEmpty(CurrentUserId))
            {
                ActiveCameraParticipants.Remove(CurrentUserId);
            }
        }
        finally
        {
            IsTogglingVideo = false;
            NotifyStateChanged();
        }
    }

    public async Task UpdateVideoSettingsAsync(string quality, int fps, string activeCameraDeviceId = "")
    {
        if (!IsVideoOn || IsTogglingVideo) return;
        await _deviceService.UpdateCameraSettingsAsync(quality, fps, activeCameraDeviceId, IsVideoOn);
    }

    [JSInvokable]
    public void OnAudioDevicesUpdated(List<AudioDevice> mics, string activeMicId, List<AudioDevice> speakers, string activeSpeakerId)
    {
        _deviceService.HandleAudioDevicesUpdated(mics, activeMicId, speakers, activeSpeakerId);
    }

    [JSInvokable]
    public void OnLocalAudioPipelineTelemetry(string filterType, double estimatedCaptureLatencyMs, string platform, double serverToClientDelayMs = 0.0)
    {
        if (!string.IsNullOrWhiteSpace(CurrentChannelId) && !string.IsNullOrWhiteSpace(CurrentUserId))
        {
            _voiceState.SetVoiceParticipantAudioTelemetry(CurrentChannelId, CurrentUserId, filterType, estimatedCaptureLatencyMs, platform, serverToClientDelayMs);
        }
    }

    [JSInvokable]
    public void OnAvSkewTelemetry(double avSkewMs, double audioJitterMs, double videoJitterMs, double videoDecodeMs, string platform)
    {
        if (Math.Abs(avSkewMs) > 60.0)
        {
            _logger.LogInformation(
                "[VoiceTelemetry] A/V skew={AvSkewMs:F1}ms (audioJB={AudioJbMs:F1}ms, videoJB={VideoJbMs:F1}ms, videoDec={VideoDecMs:F1}ms, platform={Platform})",
                avSkewMs, audioJitterMs, videoJitterMs, videoDecodeMs, platform);
        }
    }

    public async Task SetNoiseSuppressionAsync(string type)
    {
        SavedNoiseFilter = type;
        if (CurrentNoiseSuppression == type) return;
        CurrentNoiseSuppression = type;
        if (!string.IsNullOrWhiteSpace(CurrentChannelId) && !string.IsNullOrWhiteSpace(CurrentUserId))
        {
            var existingTelemetry = _voiceState.GetVoiceParticipantAudioTelemetry(CurrentChannelId, CurrentUserId);
            _voiceState.SetVoiceParticipantAudioTelemetry(
                CurrentChannelId,
                CurrentUserId,
                type,
                existingTelemetry?.EstimatedCaptureLatencyMs ?? GetEstimatedCaptureLatencyMs(),
                existingTelemetry?.Platform ?? GetLocalPlatformTag());
        }
        if (_jsInterop.IsModuleLoaded)
        {
            await _jsInterop.SetNoiseSuppressionTypeAsync(type);
            // Native mobile (Android/iOS SpokesNativeVoice.setNoiseFilter) handles any required mic
            // capture-source rebuild internally; reinitializing again causes a duplicate mic toggle.
            if (!IsSoftwareMuted && IsInVoiceCall && !IsNativePlatform)
            {
                await _jsInterop.ReinitializeMicrophoneAsync();
            }
        }
        NotifyDiagnosticsOrSettingsChanged();
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
        NotifyDiagnosticsOrSettingsChanged();
    }

    private void ApplyLocallyMutedParticipants(VoiceUserSettings? settings)
    {
        var locallyMuted = new HashSet<string>(StringComparer.Ordinal);
        if (settings?.ParticipantVolumes != null)
        {
            foreach (var (participantId, volume) in settings.ParticipantVolumes)
            {
                if (!string.IsNullOrWhiteSpace(participantId) && volume <= 0.001)
                {
                    locallyMuted.Add(participantId);
                }
            }
        }
        _locallyMutedParticipantIds.ReplaceWith(locallyMuted);
    }

    public async Task SetParticipantVolumeAsync(string participantId, double volumePercent)
    {
        if (string.IsNullOrEmpty(participantId)) return;
        if (volumePercent <= 0.001)
        {
            _locallyMutedParticipantIds.Add(participantId);
        }
        else
        {
            _locallyMutedParticipantIds.Remove(participantId);
        }
        RecomputeActiveSpeakers();
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
        NotifyDiagnosticsOrSettingsChanged();
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
        NotifyDiagnosticsOrSettingsChanged();
    }

    public async Task SetAutoGainControlAsync(bool enabled)
    {
        OnAutoGainControlChanged?.Invoke(enabled);
        if (_jsInterop.IsModuleLoaded)
        {
            try
            {
                await _jsInterop.SetAutoGainControlAsync(enabled);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to set auto gain control in JS");
            }
        }
        NotifyDiagnosticsOrSettingsChanged();
    }

    public async Task SetIncomingVoiceNormalizationAsync(bool enabled)
    {
        OnIncomingVoiceNormalizationChanged?.Invoke(enabled);
        if (_jsInterop.IsModuleLoaded)
        {
            try
            {
                await _jsInterop.SetIncomingVoiceNormalizationAsync(enabled);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to set incoming voice normalization in JS");
            }
        }
        NotifyDiagnosticsOrSettingsChanged();
    }

    public async Task UpdateVoiceSettingsAsync(VoiceUserSettings settings)
    {
        if (settings != null)
        {
            ApplyLocallyMutedParticipants(settings);
            RecomputeActiveSpeakers();
        }
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
        NotifyDiagnosticsOrSettingsChanged();
    }

    [JSInvokable]
    public void OnParticipantMuteChanged(string identity, bool isMuted)
    {
        if (string.IsNullOrEmpty(identity)) return;
        if (!IsServerVerifiedChannelParticipant(identity)) return;
        bool changed = isMuted ? MutedParticipants.Add(identity) : MutedParticipants.Remove(identity);
        if (isMuted)
        {
            _serverActiveSpeakers.Remove(identity);
        }
        RecomputeActiveSpeakers();
        if (changed)
        {
            NotifyStateChanged();
        }
    }

    public async Task ToggleScreenShareAsync(string quality = "Auto", int fps = 30, string mode = "motion", bool captureAudio = false)
    {
        if (IsTogglingScreenShare) return;
        IsTogglingScreenShare = true;
        NotifyStateChanged();
        try
        {
            bool newScreenState = !IsScreenSharing;
            if (_jsInterop.IsModuleLoaded)
            {
                await _jsInterop.SetScreenShareEnabledAsync(newScreenState, quality, fps, mode, captureAudio);
            }
            IsScreenSharing = newScreenState;
            if (!string.IsNullOrEmpty(CurrentUserId))
            {
                if (newScreenState)
                {
                    ActiveScreenShareParticipants.Add(CurrentUserId);
                }
                else
                {
                    ActiveScreenShareParticipants.Remove(CurrentUserId);
                    if (!IsVideoOn && !ActiveCameraParticipants.Contains(CurrentUserId))
                    {
                        _deviceService.RemoveParticipantVideo(CurrentUserId);
                    }
                }

                if (!string.IsNullOrEmpty(CurrentChannelId))
                {
                    _voiceState.TrySetVoiceParticipantMediaState(
                        CurrentChannelId,
                        CurrentUserId,
                        CurrentVoiceSessionId,
                        isScreenSharing: newScreenState,
                        hasScreenAudio: newScreenState ? null : false);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Voice] ToggleScreenShare failed: {Message}", ex.Message);
            IsScreenSharing = false;
            if (!string.IsNullOrEmpty(CurrentUserId))
            {
                ActiveScreenShareParticipants.Remove(CurrentUserId);
            }
        }
        finally
        {
            IsTogglingScreenShare = false;
            NotifyStateChanged();
        }
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
        if (!string.IsNullOrEmpty(CurrentChannelId) && !string.IsNullOrEmpty(CurrentUserId))
        {
            _voiceState.TrySetVoiceParticipantMediaState(
                CurrentChannelId,
                CurrentUserId,
                CurrentVoiceSessionId,
                hasScreenAudio: active);
        }
        OnScreenShareAudioStateChanged?.Invoke(active);
        NotifyStateChanged();
    }

    [JSInvokable]
    public void OnRemoteScreenAudioChanged(string identity, bool hasAudio)
    {
        if (string.IsNullOrEmpty(identity)) return;
        if (!IsServerVerifiedChannelParticipant(identity)) return;
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

    public void ClearLocalSpeakingOnCircuitDown()
    {
        if (!IsInVoiceCall || string.IsNullOrEmpty(CurrentChannelId) || string.IsNullOrEmpty(CurrentUserId))
        {
            return;
        }

        if (_wasISpeaking || _localIsSpeaking)
        {
            _wasISpeaking = false;
            _localIsSpeaking = false;
            _serverActiveSpeakers.Remove(CurrentUserId);
            _voiceState.SetUserSpeaking(CurrentChannelId, CurrentUserId, false);
            RecomputeActiveSpeakers();
        }
    }

    public async Task VerifyServerStateIntegrityAsync()
    {
        if (RequiresIntegrityCheck && IsInVoiceCall && !string.IsNullOrEmpty(CurrentChannelId) && !string.IsNullOrEmpty(CurrentUserId))
        {
            RequiresIntegrityCheck = false;
            var serverParticipants = _voiceState.GetVoiceParticipants(CurrentChannelId);
            if (!serverParticipants.Contains(CurrentUserId))
            {
                bool liveKitConnected = false;
                if (_jsInterop.IsModuleLoaded)
                {
                    try
                    {
                        liveKitConnected = await _jsInterop.IsRoomConnectedAsync();
                    }
                    catch (Exception ex) when (ex is JSDisconnectedException or TaskCanceledException or OperationCanceledException)
                    {
                        _logger.LogDebug(ex, "[VoiceService] Circuit still settling during integrity check; deferring verification.");
                        RequiresIntegrityCheck = true;
                        return;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug(ex, "[VoiceService] Failed to query LiveKit room connection during integrity check.");
                    }
                }

                if (liveKitConnected)
                {
                    _logger.LogInformation(
                        "[VoiceService] Local LiveKit room is still connected for {UserId} in {ChannelId} despite missing server state; self-healing server session.",
                        CurrentUserId, CurrentChannelId);
                    await SelfHealLocalSessionOnServerAsync();
                    NotifyStateChanged();
                    return;
                }

                await DisconnectAsync();
            }
            else
            {
                CallParticipants.Clear();
                CallParticipants.Add(CurrentUserId);
                SyncCallParticipantsFromServer(CurrentChannelId);
                foreach (var lkRemoteId in _liveKitRemoteParticipantIds)
                {
                    if (!LiveKitService.IsSyntheticParticipantIdentity(lkRemoteId) &&
                        IsServerVerifiedChannelParticipant(lkRemoteId))
                    {
                        CallParticipants.Add(lkRemoteId);
                    }
                }
                NotifyStateChanged();
            }
        }
    }

    private async Task SelfHealLocalSessionOnServerAsync()
    {
        if (!IsInVoiceCall || string.IsNullOrEmpty(CurrentChannelId) || string.IsNullOrEmpty(CurrentUserId))
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(CurrentVoiceSessionId))
        {
            _voiceState.ReopenVoiceSession(CurrentChannelId, CurrentUserId, CurrentVoiceSessionId);
        }

        var platformName = PopulateLocalServerSessionContext(CurrentChannelId, CurrentUserId);
        var metadata = new VoiceParticipantMetadata
        {
            VoiceSessionId = CurrentVoiceSessionId,
            DeviceId = CurrentDeviceId,
            DeviceName = CurrentDeviceName,
            Platform = platformName
        };

        await _voiceChannelService.ReconcileMemberStateAsync(
            CurrentChannelId,
            CurrentUserId,
            metadata,
            IsMuted);

        await RefreshPhysicalRoomStateAsync();
    }

    private string PopulateLocalServerSessionContext(string channelId, string userId)
    {
        var platformName = GetLocalPlatformTag();
        var captureLatencyMs = GetEstimatedCaptureLatencyMs();
        _voiceState.SetVoiceParticipantSessionContext(
            channelId,
            userId,
            CurrentDeviceId,
            CurrentDeviceName,
            CurrentNoiseSuppression,
            captureLatencyMs,
            platformName,
            IsMuted,
            CurrentVoiceSessionId);
        return platformName;
    }

    private async Task LeaveServerVoiceSessionAsync(string channelId, string userId, string? sessionId, bool removeEndpoints, string failureLogTemplate)
    {
        if (removeEndpoints)
        {
            _directSessionService?.RemoveParticipantEndpoints(channelId, userId, sessionId);
        }

        try
        {
            if (!string.IsNullOrWhiteSpace(sessionId))
            {
                await _voiceChannelService.TryLeaveVoiceAsync(channelId, userId, sessionId, evictFromLiveKit: true);
            }
            else
            {
                await _voiceChannelService.LeaveVoiceAsync(channelId, userId);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, failureLogTemplate, ex.Message);
        }
    }

    private void ResetState()
    {
        IsInVoiceCall = false;
        IsConnecting = false;
        ConnectingStage = VoiceConnectingStage.None;
        ConnectingChannelId = null;
        RequiresIntegrityCheck = false;
        CurrentChannelId = null;
        CurrentUserId = null;
        CurrentVoiceSessionId = string.Empty;
        IsSoftwareMuted = false;
        IsHardwareMuted = false;
        IsMuted = false;
        _localIsSpeaking = false;
        _wasISpeaking = false;
        IsVideoOn = false;
        IsTogglingVideo = false;
        IsScreenSharing = false;
        IsTogglingScreenShare = false;
        _liveKitRemoteParticipantIds.Clear();
        _mockConnectedParticipants.Clear();
        CallParticipants.Clear();
        _serverActiveSpeakers.Clear();
        _locallyMutedParticipantIds.Clear();
        _mutedRoommateUserIds.Clear();
        ActiveSpeakers.Clear();
        MutedParticipants.Clear();
        RemoteScreenAudioParticipants.Clear();
        MutedRemoteScreenAudioParticipants.Clear();
        ActiveCameraParticipants.Clear();
        ActiveScreenShareParticipants.Clear();
        DirectRoomTelemetry = null;
        _cachedDirectEndpoints = null;
        _cachedDirectEndpointsSessionId = string.Empty;
        _lastSyncedDirectPayloadSignature = null;
        _deviceService.Reset();
    }

    private void NotifyStateChanged() => OnChange?.Invoke();
    private void NotifyDiagnosticsOrSettingsChanged() => OnDiagnosticsOrSettingsChanged?.Invoke();

    private bool IsServerVerifiedChannelParticipant(string? identity)
    {
        if (string.IsNullOrWhiteSpace(identity) || (!IsInVoiceCall && !IsConnecting) || string.IsNullOrEmpty(CurrentChannelId))
        {
            return false;
        }

        if (string.Equals(identity, CurrentUserId, StringComparison.Ordinal))
        {
            return true;
        }

        return _voiceState.IsKnownVoiceChannelParticipant(CurrentChannelId, identity) ||
               _mockConnectedParticipants.Contains(identity);
    }

    [JSInvokable]
    public Task SyncRoomSnapshotAsync(VoiceRoomSnapshotDto? snapshot)
    {
        if (snapshot == null || (!IsInVoiceCall && !IsConnecting) || string.IsNullOrEmpty(CurrentChannelId))
        {
            return Task.CompletedTask;
        }

        var liveRemoteIds = new HashSet<string>(StringComparer.Ordinal);
        bool changed = false;
        bool physicalRoomChanged = false;
        var newlyJoined = new List<string>();

        foreach (var participant in snapshot.Participants)
        {
            var pid = participant.UserId;
            if (string.IsNullOrWhiteSpace(pid) ||
                string.Equals(pid, CurrentUserId, StringComparison.Ordinal) ||
                LiveKitService.IsSyntheticParticipantIdentity(pid) ||
                !IsServerVerifiedChannelParticipant(pid))
            {
                continue;
            }

            var meta = VoiceParticipantMetadata.Parse(participant.Metadata);
            if (_voiceState.IsVoiceSessionClosed(meta.VoiceSessionId))
            {
                continue;
            }

            liveRemoteIds.Add(pid);
            _liveKitRemoteParticipantIds.Add(pid);

            if (CallParticipants.Add(pid))
            {
                changed = true;
                physicalRoomChanged = true;
                newlyJoined.Add(pid);
            }

            bool effectiveRemoteMuted = participant.IsMicMuted.HasValue
                ? participant.IsMicMuted.Value
                : (participant.IsMuted || _voiceState.IsUserMuted(CurrentChannelId, pid));
            bool muteChanged = effectiveRemoteMuted
                ? MutedParticipants.Add(pid)
                : MutedParticipants.Remove(pid);
            if (muteChanged)
            {
                changed = true;
                physicalRoomChanged = true;
            }

            if (effectiveRemoteMuted)
            {
                _serverActiveSpeakers.Remove(pid);
                if (ActiveSpeakers.Remove(pid))
                {
                    changed = true;
                    NotifyActiveSpeakersUpdated();
                }
            }

            if (participant.IsCameraOn.HasValue)
            {
                changed |= participant.IsCameraOn.Value
                    ? ActiveCameraParticipants.Add(pid)
                    : ActiveCameraParticipants.Remove(pid);
            }
            if (participant.IsScreenSharing.HasValue)
            {
                changed |= participant.IsScreenSharing.Value
                    ? ActiveScreenShareParticipants.Add(pid)
                    : ActiveScreenShareParticipants.Remove(pid);
            }
            if (participant.HasScreenAudio.HasValue)
            {
                if (participant.HasScreenAudio.Value)
                {
                    changed |= RemoteScreenAudioParticipants.Add(pid);
                }
                else if (RemoteScreenAudioParticipants.Remove(pid))
                {
                    MutedRemoteScreenAudioParticipants.Remove(pid);
                    changed = true;
                }
            }
            if (participant.VideoHeight > 0 && (ActiveCameraParticipants.Contains(pid) || ActiveScreenShareParticipants.Contains(pid)))
            {
                _deviceService.HandleParticipantVideoDimensions(pid, participant.VideoHeight, participant.VideoFps > 0 ? participant.VideoFps : 30);
            }
            else if (participant.IsCameraOn == false && participant.IsScreenSharing == false)
            {
                _deviceService.RemoveParticipantVideo(pid);
            }
        }

        // Remove remote participants that were previously confirmed connected in LiveKit and have now departed.
        foreach (var trackedRemoteId in _liveKitRemoteParticipantIds.ToArray())
        {
            if (!liveRemoteIds.Contains(trackedRemoteId))
            {
                RemoveRemoteParticipantUiState(trackedRemoteId);
                changed = true;
                physicalRoomChanged = true;
                OnParticipantLeft?.Invoke(trackedRemoteId);
            }
        }

        if (!string.IsNullOrEmpty(CurrentUserId))
        {
            if (snapshot.LocalIsMuted.HasValue && snapshot.LocalIsMuted.Value != IsMuted && !IsHardwareMuted)
            {
                IsSoftwareMuted = snapshot.LocalIsMuted.Value;
                IsMuted = IsSoftwareMuted || IsHardwareMuted;
                changed = true;
            }

            bool effectiveLocalMuted = IsSoftwareMuted || IsHardwareMuted;
            if (effectiveLocalMuted)
            {
                MutedParticipants.Add(CurrentUserId);
            }
            else
            {
                MutedParticipants.Remove(CurrentUserId);
            }

            if (!string.IsNullOrEmpty(CurrentChannelId) &&
                _voiceState.IsUserMuted(CurrentChannelId, CurrentUserId) != effectiveLocalMuted)
            {
                _ = _voiceChannelService.TrySetUserMutedAsync(CurrentChannelId, CurrentUserId, effectiveLocalMuted, CurrentVoiceSessionId);
            }

            if (snapshot.LocalIsVideoOn.HasValue && IsVideoOn != snapshot.LocalIsVideoOn.Value && !IsTogglingVideo)
            {
                IsVideoOn = snapshot.LocalIsVideoOn.Value;
                _ = IsVideoOn ? ActiveCameraParticipants.Add(CurrentUserId) : ActiveCameraParticipants.Remove(CurrentUserId);
                _voiceState.TrySetVoiceParticipantMediaState(CurrentChannelId, CurrentUserId, CurrentVoiceSessionId, isCameraOn: IsVideoOn);
                changed = true;
            }
            if (snapshot.LocalIsScreenSharing.HasValue && IsScreenSharing != snapshot.LocalIsScreenSharing.Value && !IsTogglingScreenShare)
            {
                IsScreenSharing = snapshot.LocalIsScreenSharing.Value;
                _ = IsScreenSharing ? ActiveScreenShareParticipants.Add(CurrentUserId) : ActiveScreenShareParticipants.Remove(CurrentUserId);
                _voiceState.TrySetVoiceParticipantMediaState(CurrentChannelId, CurrentUserId, CurrentVoiceSessionId, isScreenSharing: IsScreenSharing);
                changed = true;
            }
        }

        foreach (var joinedId in newlyJoined)
        {
            OnParticipantJoined?.Invoke(joinedId);
        }

        if (physicalRoomChanged)
        {
            _ = RefreshPhysicalRoomStateAsync();
        }

        if (changed)
        {
            NotifyStateChanged();
        }

        return Task.CompletedTask;
    }

    [JSInvokable]
    public async Task OnRoomReconnectedAsync(VoiceRoomSnapshotDto? snapshot)
    {
        if (!IsInVoiceCall || string.IsNullOrEmpty(CurrentChannelId) || string.IsNullOrEmpty(CurrentUserId))
        {
            return;
        }

        _logger.LogInformation("[VoiceService] LiveKit room reconnected for {UserId} in channel {ChannelId}; reconciling full state.", CurrentUserId, CurrentChannelId);

        if (snapshot != null)
        {
            await SyncRoomSnapshotAsync(snapshot);
        }

        _lastSyncedDirectPayloadSignature = null;
        await SelfHealLocalSessionOnServerAsync();

        if (IsMuted && _jsInterop.IsModuleLoaded)
        {
            try
            {
                await _jsInterop.SetMicrophoneEnabledAsync(false);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "[VoiceService] Failed to re-enforce local mute after reconnect.");
            }
        }

        NotifyStateChanged();
    }

    [JSInvokable]
    public void OnRoomConnected(string[] participantIdentities)
    {
        _roomConnectedCallbackReceived = true;
        int prevParticipantCount = CallParticipants.Count;
        foreach (var id in participantIdentities)
        {
            if (!LiveKitService.IsSyntheticParticipantIdentity(id) && IsServerVerifiedChannelParticipant(id))
            {
                if (!string.Equals(id, CurrentUserId, StringComparison.Ordinal))
                {
                    _liveKitRemoteParticipantIds.Add(id);
                }
                CallParticipants.Add(id);
            }
        }

        SyncCallParticipantsFromServer();
        RecomputeActiveSpeakers();
        OnRoomJoined?.Invoke();
        // When ConnectAsync is actively finalizing a solo join, let its finally block emit the single
        // consolidated connected notification instead of broadcasting an intermediate spinner-to-stage flip.
        if (!IsConnecting || CallParticipants.Count != prevParticipantCount)
        {
            NotifyStateChanged();
        }
    }

    [JSInvokable]
    public void OnParticipantConnected(string identity)
    {
        if (string.IsNullOrEmpty(identity) || identity == CurrentUserId || LiveKitService.IsSyntheticParticipantIdentity(identity)) return;
        if (!IsServerVerifiedChannelParticipant(identity)) return;

        _liveKitRemoteParticipantIds.Add(identity);
        CallParticipants.Add(identity);
        SyncCallParticipantsFromServer();
        RecomputeActiveSpeakers();
        OnParticipantJoined?.Invoke(identity);
        NotifyStateChanged();
    }

    [JSInvokable]
    public void EnsureParticipantSlot(string identity)
    {
        if (string.IsNullOrEmpty(identity) || LiveKitService.IsSyntheticParticipantIdentity(identity)) return;
        if (!IsServerVerifiedChannelParticipant(identity)) return;

        if (!string.Equals(identity, CurrentUserId, StringComparison.Ordinal))
        {
            _liveKitRemoteParticipantIds.Add(identity);
        }

        if (CallParticipants.Add(identity))
        {
            NotifyStateChanged();
        }
    }

    [JSInvokable]
    public void OnParticipantDisconnected(string identity)
    {
        if (string.IsNullOrEmpty(identity) || identity == CurrentUserId || LiveKitService.IsSyntheticParticipantIdentity(identity)) return;

        RemoveRemoteParticipantUiState(identity);
        OnParticipantLeft?.Invoke(identity);
        NotifyStateChanged();
    }

    [JSInvokable]
    public void OnLocalSpeakingChanged(bool isSpeaking)
    {
        if (!IsInVoiceCall || string.IsNullOrEmpty(CurrentChannelId) || string.IsNullOrEmpty(CurrentUserId))
        {
            return;
        }

        bool effectiveSpeaking = isSpeaking && !IsMuted;
        _localIsSpeaking = effectiveSpeaking;

        if (effectiveSpeaking != _wasISpeaking)
        {
            _wasISpeaking = effectiveSpeaking;
            _ = _voiceChannelService.SetUserSpeakingAsync(CurrentChannelId, CurrentUserId, effectiveSpeaking);
        }

        RecomputeActiveSpeakers();
    }

    private void RecomputeActiveSpeakers()
    {
        var nextSpeakers = new HashSet<string>(StringComparer.Ordinal);

        if (IsInVoiceCall && !string.IsNullOrEmpty(CurrentChannelId))
        {
            bool localDspManaged = !string.IsNullOrEmpty(CurrentUserId) &&
                                   _voiceState.IsPhysicalRoomDspManaged(CurrentChannelId, CurrentUserId);

            foreach (var id in _serverActiveSpeakers)
            {
                if (string.IsNullOrWhiteSpace(id)) continue;
                if (LiveKitService.IsSyntheticParticipantIdentity(id)) continue;
                if (IsParticipantMuted(id)) continue;
                if (!IsServerVerifiedChannelParticipant(id)) continue;
                if (!CallParticipants.Contains(id)) continue;

                bool isSelf = string.Equals(id, CurrentUserId, StringComparison.Ordinal);
                if (isSelf)
                {
                    if (localDspManaged)
                    {
                        nextSpeakers.Add(id);
                    }
                    continue;
                }

                if (_locallyMutedParticipantIds.Contains(id)) continue;
                if (_mutedRoommateUserIds.Contains(id)) continue;

                nextSpeakers.Add(id);
            }

            // Outside an active 2+ Physical Room DSP session, mirror our own local mic VAD
            // immediately for zero-latency self-feedback. Inside an active 2+ Physical Room,
            // server post-isolation DSP state is authoritative for everyone including self so
            // desk bleed from a roommate never flashes our own tile.
            if (!string.IsNullOrEmpty(CurrentUserId) &&
                !IsMuted &&
                !localDspManaged &&
                _localIsSpeaking &&
                CallParticipants.Contains(CurrentUserId))
            {
                nextSpeakers.Add(CurrentUserId);
            }
        }

        bool changed = ActiveSpeakers.Count != nextSpeakers.Count ||
                       nextSpeakers.Any(id => !ActiveSpeakers.Contains(id));
        if (!changed)
        {
            return;
        }

        ActiveSpeakers.ReplaceWith(nextSpeakers);
        NotifyActiveSpeakersUpdated();
    }

    private void NotifyActiveSpeakersUpdated()
    {
        OnActiveSpeakersUpdated?.Invoke();
        if (_jsInterop.IsModuleLoaded && !string.IsNullOrEmpty(CurrentChannelId))
        {
            _ = SyncSpeakingParticipantsSafeAsync(CurrentChannelId, ActiveSpeakers.ToArray());
        }
    }

    private async Task SyncSpeakingParticipantsSafeAsync(string channelId, string[] speakerIds)
    {
        try
        {
            await _jsInterop.SyncSpeakingParticipantsAsync(channelId, speakerIds);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "SyncSpeakingParticipantsSafeAsync skipped: {Message}", ex.Message);
        }
    }

    [JSInvokable]
    public Task OnRoomDisconnected() => OnRoomDisconnectedWithReason(null);

    private static bool IsDuplicateIdentityDisconnect(string? reason) =>
        !string.IsNullOrWhiteSpace(reason) &&
        (string.Equals(reason, "DUPLICATE_IDENTITY", StringComparison.OrdinalIgnoreCase) ||
         string.Equals(reason, "DuplicateIdentity", StringComparison.OrdinalIgnoreCase) ||
         string.Equals(reason, "2", StringComparison.Ordinal));

    [JSInvokable]
    public async Task OnRoomDisconnectedWithReason(string? reason)
    {
        var wasInCall = IsInVoiceCall;
        var cid = CurrentChannelId;
        var uid = CurrentUserId;
        var sessionId = CurrentVoiceSessionId;
        bool displacedByDuplicateIdentity = IsDuplicateIdentityDisconnect(reason);
        bool ownsServerSession = IsOwnedBySession(cid, uid, sessionId);

        if (!string.IsNullOrEmpty(uid) && !string.IsNullOrEmpty(cid))
        {
            if (displacedByDuplicateIdentity || !ownsServerSession)
            {
                _logger.LogInformation("[VoiceService] Local LiveKit session displaced (duplicate={Duplicate}, ownsSession={Owns}) in channel {ChannelId}; preserving replacement session.", displacedByDuplicateIdentity, ownsServerSession, cid);
            }
            else
            {
                await LeaveServerVoiceSessionAsync(cid, uid, sessionId, removeEndpoints: true, "[VoiceService] Failed to leave voice channel on room disconnect: {Message}");
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
        bool prevSoftwareMuted = IsSoftwareMuted;
        bool prevMuted = IsMuted;
        bool prevInMutedSet = !string.IsNullOrEmpty(CurrentUserId) && MutedParticipants.Contains(CurrentUserId);

        IsSoftwareMuted = isMuted;
        ApplyEffectiveLocalMuteState(syncChannel: true);

        bool nowInMutedSet = !string.IsNullOrEmpty(CurrentUserId) && MutedParticipants.Contains(CurrentUserId);
        if (IsSoftwareMuted != prevSoftwareMuted || IsMuted != prevMuted || nowInMutedSet != prevInMutedSet)
        {
            NotifyStateChanged();
        }
    }

    [JSInvokable]
    public void OnLocalHardwareMuteChanged(bool isHardwareMuted)
    {
        if (IsHardwareMuted == isHardwareMuted) return;
        IsHardwareMuted = isHardwareMuted;
        ApplyEffectiveLocalMuteState(syncChannel: true);
        NotifyStateChanged();
    }

    public async ValueTask DisposeAsync()
    {
        _voiceState.VoiceMemberJoined -= OnServerVoiceMemberJoined;
        _voiceState.VoiceMemberLeft -= OnServerVoiceMemberLeft;
        _voiceState.VoiceActiveSpeakersChanged -= OnServerVoiceActiveSpeakersChanged;
        _voiceState.VoiceUserMutedChanged -= OnServerVoiceUserMutedChanged;
        _voiceState.VoiceUserMediaStateChanged -= OnServerVoiceUserMediaStateChanged;
        _voiceState.PhysicalRoomsChanged -= OnServerPhysicalRoomsChanged;
        _chatState.MessageEdited -= OnServerMessageEdited;
        if (_directSessionService != null)
        {
            _directSessionService.DirectSessionPeersChanged -= OnDirectSessionPeersChanged;
        }

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
    public void OnVoiceConnectingStageChanged(string stage)
    {
        if (!IsConnecting) return;
        var next = stage switch
        {
            "preparing" => VoiceConnectingStage.PreparingAudio,
            "connecting" => VoiceConnectingStage.ConnectingToServer,
            "microphone" => VoiceConnectingStage.StartingMicrophone,
            "room" => VoiceConnectingStage.ConfiguringRoomAudio,
            _ => ConnectingStage
        };
        if (next != ConnectingStage)
        {
            ConnectingStage = next;
            NotifyStateChanged();
        }
    }

    /// <summary>
    /// Pulls a fresh diagnostic telemetry snapshot on demand from the Spokes Desktop Direct LAN engine
    /// (called strictly when VoiceSettingsDialog opens, Live Telemetry / Recording is active, or a diagnostic report is captured).
    /// </summary>
    public async Task<DirectRoomClientTelemetry?> RefreshDirectRoomTelemetryAsync()
    {
        if (!IsInVoiceCall || !IsDesktopDirectRoomSupported || !_jsInterop.IsModuleLoaded)
        {
            return null;
        }

        try
        {
            var telemetry = await _jsInterop.GetDirectRoomAudioTelemetryAsync();
            if (!IsInVoiceCall)
            {
                return null;
            }
            ApplyDirectRoomTelemetry(telemetry);
            return DirectRoomTelemetry;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to pull Direct LAN room audio telemetry from JS interop");
            return DirectRoomTelemetry;
        }
    }

    [JSInvokable]
    public void OnDirectRoomAudioStatusChanged(DirectRoomClientTelemetry? telemetry) =>
        ApplyDirectRoomTelemetry(telemetry);

    private void ApplyDirectRoomTelemetry(DirectRoomClientTelemetry? telemetry)
    {
        var previousTelemetry = DirectRoomTelemetry;
        DirectRoomClientTelemetry? nextTelemetry;

        if (!IsInVoiceCall)
        {
            nextTelemetry = null;
        }
        else if (!string.IsNullOrWhiteSpace(CurrentChannelId) &&
                 !string.IsNullOrWhiteSpace(CurrentUserId) &&
                 !string.IsNullOrWhiteSpace(CurrentDeviceId) &&
                 _directSessionService?.EvaluateDesktopRoomFallbackStatus(CurrentChannelId, CurrentUserId, CurrentDeviceId) == DirectRoomOperatingStatus.FallbackNonDesktopRoommateInRoom)
        {
            nextTelemetry = null;
        }
        else
        {
            nextTelemetry = telemetry;
        }

        DirectRoomTelemetry = nextTelemetry;
        if (HasDirectRoomUiStatusChanged(previousTelemetry, nextTelemetry))
        {
            NotifyDiagnosticsOrSettingsChanged();
        }
    }

    private static bool HasDirectRoomUiStatusChanged(DirectRoomClientTelemetry? previous, DirectRoomClientTelemetry? next)
    {
        if (ReferenceEquals(previous, next)) return false;
        if (previous == null || next == null) return true;
        if (!string.Equals(previous.RoomId, next.RoomId, StringComparison.Ordinal)) return true;

        var prevPeers = previous.ActiveDirectPeerUserIds ?? Array.Empty<string>();
        var nextPeers = next.ActiveDirectPeerUserIds ?? Array.Empty<string>();
        if (prevPeers.Length != nextPeers.Length) return true;
        for (int i = 0; i < prevPeers.Length; i++)
        {
            if (!string.Equals(prevPeers[i], nextPeers[i], StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    [JSInvokable]
    public void OnParticipantVideoDimensions(string participantId, int height, int fps)
    {
        if (!string.IsNullOrEmpty(participantId) &&
            (CallParticipants.Contains(participantId) || IsServerVerifiedChannelParticipant(participantId)))
        {
            if (_deviceService.HandleParticipantVideoDimensions(participantId, height, fps))
            {
                OnVideoDimensionsChanged?.Invoke();
            }
        }
    }

    [JSInvokable]
    public void OnLocalVideoDimensions(int maxEdge, int fps)
    {
        if (_deviceService.HandleLocalVideoDimensions(maxEdge, fps))
        {
            OnVideoDimensionsChanged?.Invoke();
        }
    }

    [JSInvokable]
    public void OnLocalScreenDimensions(int maxEdge, int fps)
    {
        if (_deviceService.HandleLocalScreenDimensions(maxEdge, fps))
        {
            OnVideoDimensionsChanged?.Invoke();
        }
    }

    [JSInvokable]
    public void OnVoiceLog(string message, string severityStr)
    {
        OnVoiceLogReceived?.Invoke(message, severityStr);
    }

    [JSInvokable]
    public void OnVideoTrackStateChanged(string participantId, string source, bool isActive)
    {
        if (string.IsNullOrEmpty(participantId)) return;
        if (isActive && !IsServerVerifiedChannelParticipant(participantId)) return;
        bool isScreen = string.Equals(source, "screenshare", StringComparison.OrdinalIgnoreCase);
        bool changed = isScreen
            ? (isActive ? ActiveScreenShareParticipants.Add(participantId) : ActiveScreenShareParticipants.Remove(participantId))
            : (isActive ? ActiveCameraParticipants.Add(participantId) : ActiveCameraParticipants.Remove(participantId));

        if (!string.IsNullOrEmpty(CurrentUserId) && string.Equals(participantId, CurrentUserId, StringComparison.Ordinal))
        {
            if (isScreen && IsScreenSharing != isActive)
            {
                IsScreenSharing = isActive;
                changed = true;
            }
            else if (!isScreen && IsVideoOn != isActive)
            {
                IsVideoOn = isActive;
                changed = true;
            }

            if (!string.IsNullOrEmpty(CurrentChannelId))
            {
                _voiceState.TrySetVoiceParticipantMediaState(
                    CurrentChannelId,
                    CurrentUserId,
                    CurrentVoiceSessionId,
                    isCameraOn: isScreen ? null : isActive,
                    isScreenSharing: isScreen ? isActive : null);
            }
        }

        if (!isActive && !ActiveCameraParticipants.Contains(participantId) && !ActiveScreenShareParticipants.Contains(participantId))
        {
            _deviceService.RemoveParticipantVideo(participantId);
            changed = true;
        }

        if (changed)
        {
            NotifyStateChanged();
        }
    }

    [JSInvokable]
    public void OnOsPipStateChanged(bool isOpen)
    {
        OnOsPipStateChangedEvent?.Invoke(isOpen);
        NotifyStateChanged();
    }

    [JSInvokable]
    public void OnNativeMobilePipChanged(bool isInPip)
    {
        OnNativeMobilePipChangedEvent?.Invoke(isInPip);
        NotifyStateChanged();
    }

    [JSInvokable]
    public void OnPipFocusedParticipantChanged(string? participantId)
    {
        if (!string.IsNullOrWhiteSpace(participantId) && !IsServerVerifiedChannelParticipant(participantId)) return;
        OnPipFocusChangedEvent?.Invoke(string.IsNullOrWhiteSpace(participantId) ? null : participantId);
        NotifyStateChanged();
    }

    [JSInvokable]
    public async Task OnNativeCallAction(string action)
    {
        if (string.IsNullOrWhiteSpace(action)) return;
        switch (action)
        {
            case "toggleMute":
                await ToggleMuteAsync();
                break;
            case "hangup":
                await DisconnectAsync();
                break;
            case "toggleCamera":
            case "toggleScreenShare":
            case "returnToCall":
                OnNativeCallActionReceived?.Invoke(action);
                break;
        }
    }
}
