using System;
using System.Collections.Generic;

namespace Spokes_Server.Core.Services.Communication.Voice;

public enum VoiceBotSelfTestStatus
{
    Pass,
    Warning,
    Fail
}

public sealed record VoiceBotSelfTestStep(
    string Name,
    VoiceBotSelfTestStatus Status,
    long DurationMs,
    string Detail);

public sealed record VoiceBotTransportStatsSnapshot(
    string TransportProtocol,
    string LocalEndpoint,
    string RemoteEndpoint,
    string CandidateType,
    double? RoundTripTimeMs,
    ulong OutboundPacketsSent,
    ulong OutboundBytesSent);

public sealed record VoiceBotSelfTestResult(
    VoiceBotSelfTestStatus Status,
    string Summary,
    long TotalDurationMs,
    DateTime TestedAtUtc,
    string TransportProtocol,
    string LocalEndpoint,
    string RemoteEndpoint,
    string CandidateType,
    double? RoundTripTimeMs,
    ulong OutboundPacketsSent,
    ulong OutboundBytesSent,
    int LoadedSoundCount,
    IReadOnlyList<VoiceBotSelfTestStep> Steps);
