namespace Spokes_Server.Core.Models.Communication;

using System;
using System.Collections.Generic;

/// <summary>
/// Describes an authorized co-located roommate's LAN UDP candidates and numeric token for direct room audio.
/// </summary>
public sealed record DirectAudioPeerDescriptor(
    string UserId,
    uint PeerToken,
    string[] Endpoints);

/// <summary>
/// Describes the live operating or fallback state of a Physical Room configured for low-latency direct LAN audio.
/// </summary>
public enum DirectRoomOperatingStatus
{
    /// <summary>Room uses ServerCleanup or the caller is not currently connected in this room.</summary>
    NotApplicable = 0,

    /// <summary>Spokes Desktop Direct LAN UDP audio is actively connected with co-located LAN roommate(s).</summary>
    ActiveDirectLan = 1,

    /// <summary>This device has "Mute users in my room" enabled (hearing roommates naturally in the room).</summary>
    MutedRoommatesLocal = 2,

    /// <summary>This device is Web or Mobile (not Spokes Desktop); using Server Audio Cleanup as fallback.</summary>
    FallbackNotDesktopClient = 3,

    /// <summary>Connected on Spokes Desktop; waiting for another Spokes Desktop roommate on the same LAN to join.</summary>
    StandbyWaitingForRoommate = 4,

    /// <summary>An active unmuted roommate in this room is connected from Web or Mobile; room unified onto Server Audio Cleanup fallback.</summary>
    FallbackNonDesktopRoommateInRoom = 5,

    /// <summary>Spokes Desktop roommates are in the room, but UDP LAN handshake could not connect (different network or firewall); using Server Audio Cleanup fallback.</summary>
    FallbackDifferentNetworkOrFirewall = 6
}

/// <summary>
/// Ephemeral signaling descriptor pushed from <see cref="Spokes_Server.Core.Services.Communication.Voice.PhysicalRoomDirectSessionService"/>
/// to authorized Spokes Desktop participants in a Physical Voice Room configured for low-latency direct LAN audio.
/// </summary>
public sealed record PhysicalRoomDirectSessionDescriptor(
    string ChannelId,
    string RoomId,
    PhysicalRoomAudioMode AudioMode,
    uint KeyEpoch,
    string SessionKeyBase64,
    uint LocalPeerToken,
    IReadOnlyList<DirectAudioPeerDescriptor> Peers,
    bool IsSuspendedForMixedRoomClients = false);

/// <summary>
/// Per-peer diagnostic metrics reported by the Spokes Desktop native direct LAN audio engine.
/// </summary>
public sealed class DirectRoomPeerDiagnosticEntry
{
    public string UserId { get; set; } = string.Empty;

    public double RttMs { get; set; }

    public double LagMs { get; set; }

    public double LookaheadMs { get; set; }

    public double BleedDb { get; set; }

    public double Confidence { get; set; }

    public double LevelerGainDb { get; set; }

    public double PeakInputDbfs { get; set; } = -100.0;

    public long ClippedSamples { get; set; }

    public double BleedGatedPct { get; set; }

    public long JitterUnderruns { get; set; }

    public long JitterOverflows { get; set; }

    public long JitterTrims { get; set; }

    public int MaxBurstHops { get; set; }
}

/// <summary>
/// Live client-side telemetry reported by the Spokes Desktop native direct LAN audio engine.
/// </summary>
public sealed class DirectRoomClientTelemetry
{
    public string RoomId { get; set; } = string.Empty;

    public double EstimatedEndToEndMs { get; set; }

    public double MaxPeerRttMs { get; set; }

    public double JitterBufferMs { get; set; }

    public string[] ActiveDirectPeerUserIds { get; set; } = Array.Empty<string>();

    public double AchievedBleedSuppressionDb { get; set; }

    public long TxPacketCount { get; set; }

    public long RxAudioPacketCount { get; set; }

    public long AuthDropCount { get; set; }

    public double LocalPeakDbfs { get; set; } = -100.0;

    public long LocalClippedSamples { get; set; }

    public double CapturePeriodMs { get; set; }

    public double PlaybackPeriodMs { get; set; }

    public int CaptureNativeRateHz { get; set; }

    public int CaptureNativeChannels { get; set; }

    public bool CaptureUsingAudioClient3 { get; set; }

    public bool CaptureRawMode { get; set; }

    public long CaptureDiscontinuities { get; set; }

    public int PlaybackNativeRateHz { get; set; }

    public int PlaybackNativeChannels { get; set; }

    public bool PlaybackUsingAudioClient3 { get; set; }

    public long RefUnderrunCount { get; set; }

    public long RefOverrunCount { get; set; }

    public Dictionary<string, DirectRoomPeerDiagnosticEntry> PeerDiagnostics { get; set; } = new(StringComparer.Ordinal);
}
