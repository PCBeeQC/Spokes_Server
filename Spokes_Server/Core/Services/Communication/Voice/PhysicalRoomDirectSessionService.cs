namespace Spokes_Server.Core.Services.Communication.Voice;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using Spokes_Server.Core.Constants;
using Spokes_Server.Core.Data.Repositories.HR;
using Spokes_Server.Core.Models.Communication;
using Spokes_Server.Core.Models.HR;
using Spokes_Server.Core.Services.Security;

/// <summary>
/// Ephemeral in-memory coordinator for Physical Room Low-Latency Direct LAN Audio sessions.
/// Sanitizes LAN UDP candidate endpoints, enforces room enrollment and <see cref="IUserPrivacyService"/> visibility,
/// issues per-(ChannelId, RoomId) 256-bit ChaCha20-Poly1305 keys, and rotates <c>KeyEpoch</c> whenever a member leaves.
/// </summary>
public sealed class PhysicalRoomDirectSessionService : IDisposable
{
    public const int MaxEndpointsPerParticipant = 16;
    public const int SessionKeyBytes = 32;

    private readonly PhysicalVoiceRoomService _physicalRoomService;
    private readonly VoiceStateService _voiceState;
    private readonly EmployeeRepository? _employees;
    private readonly IUserPrivacyService? _userPrivacy;
    private readonly object _lock = new();

    private readonly Dictionary<(string ChannelId, string RoomId), DirectRoomSessionState> _sessions = new();
    private readonly Dictionary<(string ChannelId, string UserId), ParticipantEndpointEntry> _endpoints = new();

    /// <summary>
    /// Raised when a participant's registered LAN endpoints or session key epoch changes for `(channelId, roomId)`.
    /// </summary>
    public event Action<string, string>? DirectSessionPeersChanged;

    public PhysicalRoomDirectSessionService(
        PhysicalVoiceRoomService physicalRoomService,
        VoiceStateService voiceState,
        EmployeeRepository? employees = null,
        IUserPrivacyService? userPrivacy = null)
    {
        _physicalRoomService = physicalRoomService ?? throw new ArgumentNullException(nameof(physicalRoomService));
        _voiceState = voiceState ?? throw new ArgumentNullException(nameof(voiceState));
        _employees = employees;
        _userPrivacy = userPrivacy;

        _voiceState.VoiceMemberJoined += HandleVoiceMemberJoined;
        _voiceState.VoiceMemberLeft += HandleVoiceMemberLeft;
        _voiceState.VoiceUserMutedChanged += HandleVoiceUserMutedChanged;
        _voiceState.PhysicalRoomsChanged += HandlePhysicalRoomsChanged;
    }

    public void Dispose()
    {
        _voiceState.VoiceMemberJoined -= HandleVoiceMemberJoined;
        _voiceState.VoiceMemberLeft -= HandleVoiceMemberLeft;
        _voiceState.VoiceUserMutedChanged -= HandleVoiceUserMutedChanged;
        _voiceState.PhysicalRoomsChanged -= HandlePhysicalRoomsChanged;
    }

    /// <summary>
    /// Registers or updates the local LAN UDP `ip:port` candidate endpoints for an active voice participant.
    /// Returns <c>true</c> if the stored candidate list changed.
    /// </summary>
    public bool UpdateParticipantEndpoints(
        string channelId,
        string userId,
        string deviceId,
        IReadOnlyList<string>? rawEndpoints,
        string? voiceSessionId = null)
    {
        if (string.IsNullOrWhiteSpace(channelId) ||
            string.IsNullOrWhiteSpace(userId) ||
            string.IsNullOrWhiteSpace(deviceId))
        {
            return false;
        }

        string? cleanSessionId = string.IsNullOrWhiteSpace(voiceSessionId) ? null : voiceSessionId.Trim();
        string? activeSessionId = _voiceState.GetVoiceParticipantSessionId(channelId, userId);
        if (cleanSessionId != null &&
            !string.IsNullOrWhiteSpace(activeSessionId) &&
            !string.Equals(cleanSessionId, activeSessionId, StringComparison.Ordinal))
        {
            return false;
        }

        var participants = _voiceState.GetVoiceParticipants(channelId);
        if (!participants.Contains(userId))
        {
            return false;
        }

        if (!TryAuthorizeCaller(userId, out _))
        {
            return false;
        }

        var room = _physicalRoomService.GetRoomForUserDevice(userId, deviceId);
        if (room == null || room.AudioMode == PhysicalRoomAudioMode.ServerCleanup)
        {
            RemoveParticipantEndpoints(channelId, userId, cleanSessionId);
            return false;
        }

        string normalizedDeviceId = PhysicalVoiceRoomService.NormalizeAndClampDeviceId(deviceId);
        string[] sanitized = SanitizeLanEndpoints(rawEndpoints);
        if (sanitized.Length == 0)
        {
            return RemoveParticipantEndpoints(channelId, userId, cleanSessionId);
        }

        string? resolvedSessionId = cleanSessionId ?? activeSessionId;
        bool changed = false;
        lock (_lock)
        {
            var key = (channelId, userId);
            if (_endpoints.TryGetValue(key, out var existing) &&
                string.Equals(existing.DeviceId, normalizedDeviceId, StringComparison.Ordinal) &&
                string.Equals(existing.RoomId, room.Id, StringComparison.Ordinal) &&
                existing.Endpoints.SequenceEqual(sanitized, StringComparer.Ordinal))
            {
                if (!string.Equals(existing.VoiceSessionId, resolvedSessionId, StringComparison.Ordinal))
                {
                    _endpoints[key] = existing with { VoiceSessionId = resolvedSessionId };
                }
                return false;
            }

            _endpoints[key] = new ParticipantEndpointEntry(normalizedDeviceId, room.Id, sanitized, resolvedSessionId);
            changed = true;
        }

        if (changed)
        {
            DirectSessionPeersChanged?.Invoke(channelId, room.Id);
        }

        return changed;
    }

    /// <summary>
    /// Removes any registered LAN UDP endpoints for `(channelId, userId)` and rotates the room session key if needed.
    /// When <paramref name="expectedSessionId"/> is provided, removal is ignored if a newer session owns the participant slot.
    /// </summary>
    public bool RemoveParticipantEndpoints(string channelId, string userId, string? expectedSessionId = null)
    {
        if (string.IsNullOrWhiteSpace(channelId) || string.IsNullOrWhiteSpace(userId))
        {
            return false;
        }

        string? expectedClean = string.IsNullOrWhiteSpace(expectedSessionId) ? null : expectedSessionId.Trim();
        if (expectedClean != null)
        {
            string? activeSessionId = _voiceState.GetVoiceParticipantSessionId(channelId, userId);
            if (!string.IsNullOrWhiteSpace(activeSessionId) &&
                !string.Equals(activeSessionId, expectedClean, StringComparison.Ordinal))
            {
                return false;
            }
        }

        string? affectedRoomId = null;
        lock (_lock)
        {
            if (_endpoints.TryGetValue((channelId, userId), out var existing))
            {
                if (expectedClean != null &&
                    !string.IsNullOrWhiteSpace(existing.VoiceSessionId) &&
                    !string.Equals(existing.VoiceSessionId, expectedClean, StringComparison.Ordinal))
                {
                    return false;
                }

                _endpoints.Remove((channelId, userId));
                affectedRoomId = existing.RoomId;
                if (_sessions.TryGetValue((channelId, existing.RoomId), out var session) &&
                    session.AuthorizedMembers.Remove(userId))
                {
                    RotateSessionKeyLocked(session);
                }
            }
        }

        if (affectedRoomId != null)
        {
            DirectSessionPeersChanged?.Invoke(channelId, affectedRoomId);
            return true;
        }

        return false;
    }

    /// <summary>
    /// Builds an authorized <see cref="PhysicalRoomDirectSessionDescriptor"/> for `(channelId, userId, deviceId)`,
    /// or returns <c>null</c> if the participant's room uses <see cref="PhysicalRoomAudioMode.ServerCleanup"/>,
    /// has <c>MuteUsersInMyRoom</c> enabled, or the caller is not an active authorized member of the room and call.
    /// </summary>
    public PhysicalRoomDirectSessionDescriptor? BuildSessionDescriptorForParticipant(
        string channelId,
        string userId,
        string deviceId)
    {
        if (!TryResolveDirectRoomParticipation(channelId, userId, deviceId, out var caller, out var room, out var enrollment) ||
            enrollment!.MuteUsersInMyRoom)
        {
            return null;
        }

        if (!TryGetActiveRoomGroup(channelId, userId, room!.Id, out var activeGroup))
        {
            return null;
        }

        lock (_lock)
        {
            var sessionKey = (channelId, room.Id);
            if (!_sessions.TryGetValue(sessionKey, out var session))
            {
                session = new DirectRoomSessionState();
                _sessions[sessionKey] = session;
            }

            SyncSessionMembershipAndRotateIfDepartedLocked(session, activeGroup.ActiveUserIds);

            uint localToken = session.GetOrAssignPeerToken(userId);
            var endpointPeers = CollectDirectPeersLocked(
                channelId,
                userId,
                caller,
                room.Id,
                activeGroup.ActiveUserIds,
                out bool suspendedForMixedClients);

            var peers = new List<DirectAudioPeerDescriptor>();
            if (!suspendedForMixedClients)
            {
                foreach (var (peerUserId, endpoints) in endpointPeers)
                {
                    uint peerToken = session.GetOrAssignPeerToken(peerUserId);
                    peers.Add(new DirectAudioPeerDescriptor(peerUserId, peerToken, endpoints));
                }
            }

            return new PhysicalRoomDirectSessionDescriptor(
                channelId,
                room.Id,
                room.AudioMode,
                session.KeyEpoch,
                Convert.ToBase64String(session.SessionKey),
                localToken,
                peers,
                suspendedForMixedClients);
        }
    }

    /// <summary>
    /// Evaluates why a Spokes Desktop participant in a low-latency Physical Room is currently on standby or fallback
    /// when no direct LAN peers are actively streaming.
    /// </summary>
    public DirectRoomOperatingStatus EvaluateDesktopRoomFallbackStatus(
        string channelId,
        string userId,
        string deviceId)
    {
        if (!TryResolveDirectRoomParticipation(channelId, userId, deviceId, out var caller, out var room, out var enrollment))
        {
            return DirectRoomOperatingStatus.NotApplicable;
        }

        if (enrollment!.MuteUsersInMyRoom)
        {
            return DirectRoomOperatingStatus.MutedRoommatesLocal;
        }

        if (!TryGetActiveRoomGroup(channelId, userId, room!.Id, out var activeGroup))
        {
            return DirectRoomOperatingStatus.StandbyWaitingForRoommate;
        }

        lock (_lock)
        {
            var endpointPeers = CollectDirectPeersLocked(
                channelId,
                userId,
                caller,
                room.Id,
                activeGroup.ActiveUserIds,
                out bool hasNonDesktopUnmutedRoommate);

            if (hasNonDesktopUnmutedRoommate)
            {
                return DirectRoomOperatingStatus.FallbackNonDesktopRoommateInRoom;
            }

            return endpointPeers.Count > 0
                ? DirectRoomOperatingStatus.FallbackDifferentNetworkOrFirewall
                : DirectRoomOperatingStatus.StandbyWaitingForRoommate;
        }
    }

    private bool TryResolveDirectRoomParticipation(
        string channelId,
        string userId,
        string deviceId,
        out Employee? caller,
        out PhysicalVoiceRoom? room,
        out PhysicalRoomDeviceEnrollment? enrollment)
    {
        caller = null;
        room = null;
        enrollment = null;

        if (string.IsNullOrWhiteSpace(channelId) ||
            string.IsNullOrWhiteSpace(userId) ||
            string.IsNullOrWhiteSpace(deviceId))
        {
            return false;
        }

        var callParticipants = _voiceState.GetVoiceParticipants(channelId);
        if (!callParticipants.Contains(userId) || !TryAuthorizeCaller(userId, out caller))
        {
            return false;
        }

        string normalizedDeviceId = PhysicalVoiceRoomService.NormalizeAndClampDeviceId(deviceId);
        room = _physicalRoomService.GetRoomForUserDevice(userId, normalizedDeviceId);
        enrollment = _physicalRoomService.GetEnrollment(userId, normalizedDeviceId);
        return room != null && enrollment != null && room.AudioMode != PhysicalRoomAudioMode.ServerCleanup;
    }

    private bool TryGetActiveRoomGroup(
        string channelId,
        string userId,
        string roomId,
        out (PhysicalVoiceRoom Room, List<string> ActiveUserIds) activeGroup)
    {
        var roomEnrollments = _physicalRoomService.GetChannelRoomEnrollments(channelId);
        return roomEnrollments.TryGetValue(roomId, out activeGroup!) &&
               activeGroup.ActiveUserIds.Contains(userId, StringComparer.Ordinal);
    }

    private List<(string UserId, string[] Endpoints)> CollectDirectPeersLocked(
        string channelId,
        string userId,
        Employee? caller,
        string roomId,
        IReadOnlyCollection<string> activeUserIds,
        out bool hasNonDesktopUnmutedRoommate)
    {
        var endpointPeers = new List<(string UserId, string[] Endpoints)>();
        hasNonDesktopUnmutedRoommate = false;

        foreach (var peerUserId in activeUserIds)
        {
            if (string.Equals(peerUserId, userId, StringComparison.Ordinal) ||
                !PhysicalVoiceRoomService.IsUserVisibleToActor(caller, userId, peerUserId, _employees, _userPrivacy))
            {
                continue;
            }

            // Do not send direct packets to a roommate who has opted into MuteUsersInMyRoom on their active device
            var peerDev = _voiceState.GetVoiceParticipantDevice(channelId, peerUserId);
            if (peerDev != null && !string.IsNullOrWhiteSpace(peerDev.Value.DeviceId))
            {
                var peerEnrollment = _physicalRoomService.GetEnrollment(
                    peerUserId,
                    PhysicalVoiceRoomService.NormalizeAndClampDeviceId(peerDev.Value.DeviceId));
                if (peerEnrollment?.MuteUsersInMyRoom == true)
                {
                    continue;
                }
            }

            if (_endpoints.TryGetValue((channelId, peerUserId), out var peerEntry) &&
                string.Equals(peerEntry.RoomId, roomId, StringComparison.Ordinal) &&
                peerEntry.Endpoints.Length > 0)
            {
                endpointPeers.Add((peerUserId, peerEntry.Endpoints));
            }
            else
            {
                var peerTelemetry = _voiceState.GetVoiceParticipantAudioTelemetry(channelId, peerUserId);
                bool isDesktopClient = IsDesktopClientPlatform(peerTelemetry?.Platform);
                if (!isDesktopClient && !_voiceState.IsUserMuted(channelId, peerUserId))
                {
                    // An unmuted co-located roommate in this same Physical Room is connected on a non-Desktop client
                    // (e.g., Web or Mobile). Suspend direct LAN peer streaming for this room so all roommates seamlessly unify onto
                    // Server Audio Cleanup without uncancelled acoustic cross-bleed, while keeping local UDP sockets bound for instant resumption.
                    hasNonDesktopUnmutedRoommate = true;
                }
            }
        }

        return endpointPeers;
    }

    /// <summary>
    /// Validates and normalizes raw `ip:port` candidate strings, retaining only private/LAN/link-local/loopback UDP endpoints.
    /// </summary>
    public static string[] SanitizeLanEndpoints(IReadOnlyList<string>? rawEndpoints)
    {
        if (rawEndpoints == null || rawEndpoints.Count == 0)
        {
            return Array.Empty<string>();
        }

        var result = new List<string>(Math.Min(rawEndpoints.Count, MaxEndpointsPerParticipant));
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var raw in rawEndpoints)
        {
            if (result.Count >= MaxEndpointsPerParticipant)
            {
                break;
            }

            if (TryNormalizeLanEndpoint(raw, out var normalized) && seen.Add(normalized))
            {
                result.Add(normalized);
            }
        }

        return result.ToArray();
    }

    public static bool TryNormalizeLanEndpoint(string? raw, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        string candidate = raw.Trim();
        if (candidate.Length > 96)
        {
            return false;
        }

        // Strip optional IPv6 scope zone index (e.g. [fe80::1%eth0]:50000 -> [fe80::1]:50000)
        int pctIdx = candidate.IndexOf('%');
        if (pctIdx > 0)
        {
            int closeBracketIdx = candidate.IndexOf(']', pctIdx);
            if (closeBracketIdx > pctIdx)
            {
                candidate = string.Concat(candidate.AsSpan(0, pctIdx), candidate.AsSpan(closeBracketIdx));
            }
        }

        if (!IPEndPoint.TryParse(candidate, out var ep))
        {
            return false;
        }

        if (ep.Port < 1024 || ep.Port > 65535)
        {
            return false;
        }

        IPAddress ip = ep.Address.IsIPv4MappedToIPv6 ? ep.Address.MapToIPv4() : ep.Address;
        if (!IsPermittedLanOrLoopbackAddress(ip))
        {
            return false;
        }

        normalized = new IPEndPoint(ip, ep.Port).ToString();
        return true;
    }

    private static bool IsPermittedLanOrLoopbackAddress(IPAddress ip)
    {
        if (IPAddress.Any.Equals(ip) || IPAddress.IPv6Any.Equals(ip) || IPAddress.Broadcast.Equals(ip))
        {
            return false;
        }

        if (IPAddress.IsLoopback(ip))
        {
            return true;
        }

        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            byte[] b = ip.GetAddressBytes();
            // Reject multicast 224.0.0.0/4 and reserved 240.0.0.0/4
            if (b[0] >= 224)
            {
                return false;
            }

            // 10.0.0.0/8
            if (b[0] == 10) return true;
            // 172.16.0.0/12
            if (b[0] == 172 && b[1] >= 16 && b[1] <= 31) return true;
            // 192.168.0.0/16
            if (b[0] == 192 && b[1] == 168) return true;
            // 169.254.0.0/16 (RFC 3927 link-local)
            if (b[0] == 169 && b[1] == 254) return true;
            // 100.64.0.0/10 (Tailscale / LAN CGNAT overlay)
            if (b[0] == 100 && b[1] >= 64 && b[1] <= 127) return true;

            return false;
        }

        if (ip.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (ip.IsIPv6Multicast)
            {
                return false;
            }

            if (ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal)
            {
                return true;
            }

            byte[] b = ip.GetAddressBytes();
            // fc00::/7 Unique Local Address (ULA)
            if ((b[0] & 0xFE) == 0xFC)
            {
                return true;
            }

            return false;
        }

        return false;
    }

    private void HandleVoiceMemberJoined(string channelId, string userId) =>
        NotifyLowLatencyRoomIfMemberActive(channelId, userId);

    private void HandleVoiceUserMutedChanged(string channelId, string userId, bool isMuted) =>
        NotifyLowLatencyRoomIfMemberActive(channelId, userId);

    private void NotifyLowLatencyRoomIfMemberActive(string channelId, string userId)
    {
        if (string.IsNullOrWhiteSpace(channelId) || string.IsNullOrWhiteSpace(userId))
        {
            return;
        }

        var roomEnrollments = _physicalRoomService.GetChannelRoomEnrollments(channelId);
        foreach (var kvp in roomEnrollments)
        {
            if (kvp.Value.Room.AudioMode != PhysicalRoomAudioMode.ServerCleanup &&
                kvp.Value.ActiveUserIds.Contains(userId, StringComparer.Ordinal))
            {
                DirectSessionPeersChanged?.Invoke(channelId, kvp.Key);
            }
        }
    }

    private void HandleVoiceMemberLeft(string channelId, string userId)
    {
        if (string.IsNullOrWhiteSpace(channelId) || string.IsNullOrWhiteSpace(userId))
        {
            return;
        }

        var affectedRoomIds = new HashSet<string>(StringComparer.Ordinal);
        lock (_lock)
        {
            if (_endpoints.Remove((channelId, userId), out var removed))
            {
                affectedRoomIds.Add(removed.RoomId);
            }

            foreach (var ep in _endpoints)
            {
                if (string.Equals(ep.Key.ChannelId, channelId, StringComparison.Ordinal))
                {
                    affectedRoomIds.Add(ep.Value.RoomId);
                }
            }

            var emptySessions = new List<(string ChannelId, string RoomId)>();
            foreach (var kvp in _sessions)
            {
                if (!string.Equals(kvp.Key.ChannelId, channelId, StringComparison.Ordinal))
                {
                    continue;
                }

                affectedRoomIds.Add(kvp.Key.RoomId);
                if (kvp.Value.AuthorizedMembers.Remove(userId))
                {
                    if (kvp.Value.AuthorizedMembers.Count == 0)
                    {
                        emptySessions.Add(kvp.Key);
                    }
                    else
                    {
                        RotateSessionKeyLocked(kvp.Value);
                    }
                }
            }

            foreach (var emptyKey in emptySessions)
            {
                _sessions.Remove(emptyKey);
            }
        }

        foreach (var roomId in affectedRoomIds)
        {
            DirectSessionPeersChanged?.Invoke(channelId, roomId);
        }
    }

    private void HandlePhysicalRoomsChanged()
    {
        var notifications = new List<(string ChannelId, string RoomId)>();

        lock (_lock)
        {
            var staleEndpoints = new List<(string ChannelId, string UserId)>();
            foreach (var kvp in _endpoints)
            {
                var room = _physicalRoomService.GetRoomForUserDevice(kvp.Key.UserId, kvp.Value.DeviceId);
                if (room == null ||
                    !string.Equals(room.Id, kvp.Value.RoomId, StringComparison.Ordinal) ||
                    room.AudioMode == PhysicalRoomAudioMode.ServerCleanup)
                {
                    staleEndpoints.Add(kvp.Key);
                }
            }

            foreach (var staleKey in staleEndpoints)
            {
                if (_endpoints.Remove(staleKey, out var removed))
                {
                    notifications.Add((staleKey.ChannelId, removed.RoomId));
                }
            }

            var emptySessions = new List<(string ChannelId, string RoomId)>();
            foreach (var kvp in _sessions)
            {
                var room = _physicalRoomService.GetRoomById(kvp.Key.RoomId);
                if (room == null || room.AudioMode == PhysicalRoomAudioMode.ServerCleanup)
                {
                    emptySessions.Add(kvp.Key);
                    notifications.Add(kvp.Key);
                    continue;
                }

                var enrollments = _physicalRoomService.GetChannelRoomEnrollments(kvp.Key.ChannelId);
                var activeUsers = enrollments.TryGetValue(kvp.Key.RoomId, out var group)
                    ? group.ActiveUserIds
                    : [];

                if (activeUsers.Count == 0)
                {
                    emptySessions.Add(kvp.Key);
                    continue;
                }

                if (SyncSessionMembershipAndRotateIfDepartedLocked(kvp.Value, activeUsers))
                {
                    notifications.Add(kvp.Key);
                }
            }

            foreach (var emptyKey in emptySessions)
            {
                _sessions.Remove(emptyKey);
            }
        }

        foreach (var (channelId, roomId) in notifications.Distinct())
        {
            DirectSessionPeersChanged?.Invoke(channelId, roomId);
        }
    }

    private static bool SyncSessionMembershipAndRotateIfDepartedLocked(
        DirectRoomSessionState session,
        IReadOnlyCollection<string> currentActiveUsers)
    {
        bool anyDeparted = false;
        var departed = session.AuthorizedMembers
            .Where(existing => !currentActiveUsers.Contains(existing, StringComparer.Ordinal))
            .ToList();

        foreach (var userId in departed)
        {
            session.AuthorizedMembers.Remove(userId);
            anyDeparted = true;
        }

        foreach (var userId in currentActiveUsers)
        {
            session.AuthorizedMembers.Add(userId);
        }

        if (anyDeparted && session.AuthorizedMembers.Count > 0)
        {
            RotateSessionKeyLocked(session);
            return true;
        }

        return false;
    }

    private static void RotateSessionKeyLocked(DirectRoomSessionState session)
    {
        session.KeyEpoch = session.KeyEpoch == uint.MaxValue ? 1u : session.KeyEpoch + 1u;
        RandomNumberGenerator.Fill(session.SessionKey);
    }

    private bool TryAuthorizeCaller(string userId, out Employee? caller)
    {
        caller = null;
        if (_employees == null)
        {
            return true;
        }

        caller = _employees.GetById(userId);
        return caller is { IsActiveUnsuspended: true } && caller.HasPermission(AppPermissions.Chat.Use);
    }

    private static bool IsDesktopClientPlatform(string? platform) =>
        string.Equals(platform, "desktop", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(platform, "electron", StringComparison.OrdinalIgnoreCase);

    private sealed record ParticipantEndpointEntry(string DeviceId, string RoomId, string[] Endpoints, string? VoiceSessionId);

    private sealed class DirectRoomSessionState
    {
        private uint _nextPeerToken = 1;

        public uint KeyEpoch { get; set; } = 1;

        public byte[] SessionKey { get; } = RandomNumberGenerator.GetBytes(SessionKeyBytes);

        public HashSet<string> AuthorizedMembers { get; } = new(StringComparer.Ordinal);

        public Dictionary<string, uint> PeerTokensByUser { get; } = new(StringComparer.Ordinal);

        public uint GetOrAssignPeerToken(string userId)
        {
            if (PeerTokensByUser.TryGetValue(userId, out uint token))
            {
                return token;
            }

            token = _nextPeerToken++;
            if (_nextPeerToken == 0)
            {
                _nextPeerToken = 1;
            }

            PeerTokensByUser[userId] = token;
            return token;
        }
    }
}
