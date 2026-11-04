namespace Spokes_Server.Core.Services.Communication.Voice.Dsp;

using System;
using System.Collections.Generic;

/// <summary>
/// Real-time gate state of a participant's microphone stream in a <see cref="Spokes_Server.Core.Models.Communication.PhysicalVoiceRoom"/>.
/// </summary>
public enum AcousticGateState
{
    Open = 0,
    GatedBleed = 1,
    DoubleTalkSubtraction = 2
}

/// <summary>
/// Represents the learned acoustic relationship from speaker <see cref="SourceUserId"/> (Mic i)
/// to listener <see cref="TargetUserId"/> (Mic j) within the same physical room.
/// </summary>
public sealed record PairwiseAcousticCalibration(
    string SourceUserId,
    string TargetUserId,
    int DelaySamples48k,
    double DelayMs,
    float BleedGainLinear,
    double BleedAttenuationDb,
    double Confidence,
    bool IsReciprocalPrior = false,
    double PeakEnvelopeCorrelation = 0.0,
    double PeakWaveformCorrelation = 0.0,
    bool IsLiveVerified = true);

/// <summary>
/// Decomposed physical acoustics vs client DSP/clock offset between two participants once both directions are observed.
/// </summary>
public sealed record BidirectionalAcousticDecomposition(
    string UserAId,
    string UserBId,
    double AirPropagationMs,
    double EstimatedDistanceMeters,
    double RelativeDspOffsetMs,
    bool BothDirectionsDirectlyLocked);

/// <summary>
/// Live per-participant DSP metrics within an active physical room processor.
/// </summary>
public sealed record ParticipantRoomDspTelemetry(
    string UserId,
    AcousticGateState CurrentGateState,
    float CurrentGain,
    double RecentVocalRmsDbFs,
    int EffectiveLookaheadMs,
    double ClockDriftCompensatedMs,
    int TotalSubFramesProcessed,
    int SpeechSubFrames,
    int GatedBleedSubFrames,
    int DoubleTalkSubFrames,
    int RejectedClickTransients,
    double AchievedBleedSuppressionDb,
    double PostDspResidualDbFs,
    double ClientProcessingDelayMs = 0.0,
    double ServerToClientDelayMs = 0.0);

/// <summary>
/// Compact 250ms timeline bucket capturing room activity, gate transitions, and anomalies for agent debugging.
/// </summary>
public sealed record AcousticTimelineBucket(
    double StartTimeSeconds,
    double EndTimeSeconds,
    string? DominantSpeakerUserId,
    double DominantVocalDbFs,
    IReadOnlyDictionary<string, AcousticGateState> ParticipantGateStates,
    int ClickTransientsRejected,
    string? EventSummary);

/// <summary>
/// Complete diagnostic snapshot for a physical voice room at a point in time or over a capture window.
/// </summary>
public sealed record PhysicalRoomDiagnosticSnapshot(
    DateTime CapturedAtUtc,
    double CaptureDurationSeconds,
    int SampleRate,
    IReadOnlyList<ParticipantRoomDspTelemetry> Participants,
    IReadOnlyList<PairwiseAcousticCalibration> Calibrations,
    IReadOnlyList<BidirectionalAcousticDecomposition> Decompositions,
    IReadOnlyList<AcousticTimelineBucket> TimelineBuckets,
    IReadOnlyList<string> Warnings);

/// <summary>
/// Quantitative benchmark result for a simulated scenario in automated evaluation tests.
/// </summary>
public sealed record AcousticBenchmarkScenarioResult(
    string ScenarioName,
    string Description,
    double GroundTruthDelayMs,
    double DetectedDelayMs,
    double LatencyErrorMs,
    double ConvergenceTimeMs,
    double BleedSuppressionErleDb,
    double DoubleTalkSiSdrBeforeDb,
    double DoubleTalkSiSdrAfterDb,
    double DoubleTalkSiSdrImprovementDb,
    double LogSpectralDistortionDb,
    double PrimarySpeechRetentionPercent,
    double FalseGatePercent,
    int ClicksInjected,
    int ClicksRejected,
    PhysicalRoomDiagnosticSnapshot Snapshot);
