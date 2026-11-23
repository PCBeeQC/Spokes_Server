namespace Spokes_Server.Core.Services.Communication.Voice.Dsp;

using System;
using System.Collections.Generic;
using System.Linq;
using static Spokes_Server.Core.Services.Communication.Voice.Dsp.AcousticRoomProcessor;

/// <summary>
/// Rolling 250ms acoustic timeline bucket recorder, calibration/physics decomposition projector,
/// and diagnostic health-warning builder for <see cref="AcousticRoomProcessor"/>.
/// </summary>
internal sealed class AcousticRoomDiagnosticsTracker
{
    private const int SubBlocksPerTimelineBucket = 25; // 250ms = 25 sub-blocks of 10ms
    private const int MaxTimelineBuckets = 180;        // 45 seconds of rolling history (retains full 30s captures intact)

    private readonly Queue<AcousticTimelineBucket> _timelineBuckets = new();
    private readonly List<string> _currentBucketEvents = [];
    private DateTime _diagnosticsResetUtc = DateTime.UtcNow;

    private int _currentBucketSubBlockCount;
    private long _currentBucketStartSample;
    private int _currentBucketClicksRejected;

    public void RecordEvent(string message) => _currentBucketEvents.Add(message);

    public void RecordRejectedClick() => _currentBucketClicksRejected++;

    public void Reset(long globalSampleClock, IEnumerable<AcousticParticipantStream> participants)
    {
        _diagnosticsResetUtc = DateTime.UtcNow;
        _timelineBuckets.Clear();
        _currentBucketSubBlockCount = 0;
        _currentBucketStartSample = globalSampleClock;
        _currentBucketClicksRejected = 0;
        _currentBucketEvents.Clear();

        foreach (var p in participants)
        {
            p.ResetTelemetryCounters();
        }
    }

    public void AdvanceBucketIfMaster(
        AcousticParticipantStream self,
        IReadOnlyDictionary<string, AcousticParticipantStream> participants,
        long subStartSample)
    {
        // Only advance the room bucket counter once per 10ms step (driven by the first participant in dictionary order)
        var firstKey = participants.Keys.FirstOrDefault();
        if (!string.Equals(self.UserId, firstKey, StringComparison.Ordinal))
        {
            return;
        }

        if (_currentBucketSubBlockCount == 0)
        {
            _currentBucketStartSample = Math.Max(0L, subStartSample);
        }

        if (++_currentBucketSubBlockCount < SubBlocksPerTimelineBucket)
        {
            return;
        }

        double startSec = Math.Round(_currentBucketStartSample / (double)SampleRate, 2);
        double endSec = Math.Round((subStartSample + FrameSamples10Ms) / (double)SampleRate, 2);

        var gateMap = participants.Values.ToDictionary(
            p => p.UserId,
            p => p.BucketDoubleTalkSubFrames >= 3
                ? AcousticGateState.DoubleTalkSubtraction
                : (p.BucketGatedSubFrames >= 3 ? AcousticGateState.GatedBleed : p.LastGateState),
            StringComparer.Ordinal);

        string? dominantUser = null;
        float dominantVocalRms = 0f;
        foreach (var p in participants.Values)
        {
            bool isPredominantlyGated = p.BucketGatedSubFrames > (SubBlocksPerTimelineBucket / 2) ||
                                        (gateMap.TryGetValue(p.UserId, out var gs) &&
                                         gs == AcousticGateState.GatedBleed &&
                                         p.BucketOpenSpeechSubFrames <= p.BucketGatedSubFrames);
            if (!isPredominantlyGated && p.BucketPeakOpenVocalRms >= VocalActivityFloorRms && p.BucketPeakOpenVocalRms > dominantVocalRms)
            {
                dominantVocalRms = p.BucketPeakOpenVocalRms;
                dominantUser = p.UserId;
            }
        }

        string? summary = _currentBucketEvents.Count > 0
            ? string.Join("; ", _currentBucketEvents.Distinct())
            : null;

        _timelineBuckets.Enqueue(new AcousticTimelineBucket(
            startSec,
            endSec,
            dominantUser,
            AcousticDspMath.ToDbFs(dominantVocalRms),
            gateMap,
            _currentBucketClicksRejected,
            summary));

        while (_timelineBuckets.Count > MaxTimelineBuckets)
        {
            _timelineBuckets.Dequeue();
        }

        foreach (var p in participants.Values)
        {
            p.ResetBucketCounters();
        }

        _currentBucketSubBlockCount = 0;
        _currentBucketClicksRejected = 0;
        _currentBucketEvents.Clear();
    }

    public static List<PairwiseAcousticCalibration> BuildCalibrationsList(AcousticCalibrationMatrix matrix)
    {
        return matrix.Entries
            .Where(kvp => kvp.Value.Confidence >= 0.20)
            .OrderBy(kvp => kvp.Key.Source, StringComparer.Ordinal)
            .ThenBy(kvp => kvp.Key.Target, StringComparer.Ordinal)
            .Select(kvp => new PairwiseAcousticCalibration(
                kvp.Key.Source,
                kvp.Key.Target,
                kvp.Value.DelaySamples,
                Math.Round(kvp.Value.DelaySamples * 1000.0 / SampleRate, 1),
                (float)Math.Round(kvp.Value.BleedGain, 4),
                AcousticDspMath.ToDbFs(kvp.Value.BleedGain),
                Math.Round(kvp.Value.Confidence, 2),
                kvp.Value.IsReciprocalPrior,
                Math.Round(kvp.Value.PeakEnvelopeCorrelation, 2),
                Math.Round(kvp.Value.PeakWaveformCorrelation, 2),
                kvp.Value.IsLiveVerifiedInCurrentSession))
            .ToList();
    }

    public PhysicalRoomDiagnosticSnapshot BuildSnapshot(
        IReadOnlyDictionary<string, AcousticParticipantStream> participantsMap,
        AcousticCalibrationMatrix matrix,
        Func<AcousticParticipantStream, int> resolveEffectiveLookahead)
    {
        var calibrations = BuildCalibrationsList(matrix);
        var participants = new List<ParticipantRoomDspTelemetry>(participantsMap.Count);

        foreach (var p in participantsMap.Values.OrderBy(x => x.UserId, StringComparer.Ordinal))
        {
            int lookaheadMs = (int)Math.Round(resolveEffectiveLookahead(p) * 1000.0 / SampleRate);
            float displayVocalRms = Math.Max(p.LastVocalRms, p.PeakVocalRms);
            double erleDb = p.BleedInputEnergySum > 1e-9 && p.BleedOutputEnergySum > 1e-12
                ? Math.Round(AcousticDspMath.ToPowerRatioDb(p.BleedInputEnergySum, p.BleedOutputEnergySum), 1)
                : (p.GatedBleedSubFrames > 0 ? 35.0 : 0.0);
            double postResidualDbFs = p.GatedBleedSubFrames > 0 && p.BleedOutputEnergySum > 1e-12
                ? Math.Round(AcousticDspMath.ToPowerRatioDb(p.BleedOutputEnergySum, p.GatedBleedSubFrames), 1)
                : -90.0;
            double driftMs = Math.Round(p.TotalClockDriftCompensatedSamples * 1000.0 / SampleRate, 1);
            double peakRawDbFs = AcousticDspMath.ToDbFs(p.PeakRawSample);
            double makeupGainDb = Math.Round(20.0 * Math.Log10(Math.Max(p.Leveler.EffectiveGain * p.CurrentDoubleTalkMakeupScale, 1e-4f)), 1);

            participants.Add(new ParticipantRoomDspTelemetry(
                p.UserId,
                p.LastGateState,
                (float)Math.Round(p.CurrentGain, 3),
                AcousticDspMath.ToDbFs(displayVocalRms),
                lookaheadMs,
                driftMs,
                p.TotalSubFramesProcessed,
                p.SpeechSubFrames,
                p.GatedBleedSubFrames,
                p.DoubleTalkSubFrames,
                p.RejectedClickTransients,
                Math.Clamp(erleDb, 0.0, 60.0),
                postResidualDbFs,
                Math.Round(p.EstimatedCaptureLatencyMs ?? 0.0, 1),
                Math.Round(p.ServerToClientDelayMs ?? 0.0, 1),
                peakRawDbFs,
                p.ClippedSampleCount,
                makeupGainDb));
        }

        var decompositions = BuildDecompositions(participantsMap.Keys, matrix);
        var warnings = BuildDiagnosticWarnings(participants, calibrations);

        int maxSubFrames = participants.Count > 0 ? participants.Max(x => x.TotalSubFramesProcessed) : 0;
        double durationSec = maxSubFrames > 0
            ? Math.Round(maxSubFrames * 0.010, 2)
            : Math.Round(Math.Max(0.1, (DateTime.UtcNow - _diagnosticsResetUtc).TotalSeconds), 1);

        return new PhysicalRoomDiagnosticSnapshot(
            DateTime.UtcNow,
            durationSec,
            SampleRate,
            participants,
            calibrations,
            decompositions,
            _timelineBuckets.ToList(),
            warnings);
    }

    private static List<BidirectionalAcousticDecomposition> BuildDecompositions(
        IEnumerable<string> participantIds,
        AcousticCalibrationMatrix matrix)
    {
        var results = new List<BidirectionalAcousticDecomposition>();
        var userIds = participantIds.OrderBy(x => x, StringComparer.Ordinal).ToList();

        for (int i = 0; i < userIds.Count; i++)
        {
            for (int j = i + 1; j < userIds.Count; j++)
            {
                string userA = userIds[i];
                string userB = userIds[j];

                if (matrix.TryGetCalibratedPair(userA, userB, 0.20, out var ab) &&
                    matrix.TryGetCalibratedPair(userB, userA, 0.20, out var ba))
                {
                    double delayABMs = ab.DelaySamples * 1000.0 / SampleRate;
                    double delayBAMs = ba.DelaySamples * 1000.0 / SampleRate;
                    double airMs = Math.Max(0.2, (delayABMs + delayBAMs) / 2.0);
                    double distanceMeters = Math.Round((airMs / 1000.0) * 343.0, 2);
                    double dspOffsetMs = Math.Round((delayABMs - delayBAMs) / 2.0, 2);

                    results.Add(new BidirectionalAcousticDecomposition(
                        userA,
                        userB,
                        Math.Round(airMs, 2),
                        distanceMeters,
                        dspOffsetMs,
                        !ab.IsReciprocalPrior && !ba.IsReciprocalPrior));
                }
            }
        }

        return results;
    }

    private static List<string> BuildDiagnosticWarnings(
        IReadOnlyList<ParticipantRoomDspTelemetry> participants,
        IReadOnlyList<PairwiseAcousticCalibration> calibrations)
    {
        var warnings = new List<string>();
        if (participants.Count < 2)
        {
            warnings.Add("Fewer than 2 devices are currently active in this physical room.");
            return warnings;
        }

        int totalSpeechSubFrames = participants.Sum(p => p.SpeechSubFrames);
        if (totalSpeechSubFrames < 40)
        {
            warnings.Add($"Low vocal activity detected ({Math.Round(totalSpeechSubFrames * 0.010, 2):F2}s of speech across room) — speak a full sentence for reliable calibration (short coughs or brief noises are ignored).");
        }

        foreach (var p in participants)
        {
            if (p.ClippedSampleCount > 0 || p.PeakRawInputDbFs > -0.3)
            {
                warnings.Add($"Input clipping detected on {p.UserId}'s microphone (Peak={p.PeakRawInputDbFs:F1} dBFS, ClippedSamples={p.ClippedSampleCount}) — non-linear mic clipping distorts waveforms and degrades echo cancellation; lower hardware microphone boost.");
            }

            if (!calibrations.Any(c => c.SourceUserId == p.UserId && !c.IsReciprocalPrior && c.Confidence >= 0.30))
            {
                warnings.Add($"Direction from {p.UserId} -> roommates has not been directly calibrated yet (have {p.UserId} speak a short phrase).");
            }
        }

        foreach (var c in calibrations)
        {
            if (!c.IsReciprocalPrior)
            {
                if (c.BleedAttenuationDb > 0.5)
                {
                    warnings.Add($"Target mic {c.TargetUserId} applies hardware/AGC gain boost (+{c.BleedAttenuationDb:F1} dB relative to {c.SourceUserId}) — far-field room speech is amplified by {c.TargetUserId}'s device.");
                }
                else if (c.BleedAttenuationDb > -7.0)
                {
                    warnings.Add($"High acoustic bleed ({c.BleedAttenuationDb:F1} dB) from {c.SourceUserId} into {c.TargetUserId}'s mic — consider moving microphones further apart or lowering mic gain.");
                }
            }

            if (c.DelayMs < -45.0)
            {
                var targetDiag = participants.FirstOrDefault(p => string.Equals(p.UserId, c.TargetUserId, StringComparison.Ordinal));
                int targetLookaheadMs = targetDiag?.EffectiveLookaheadMs ?? LookaheadMs;
                warnings.Add($"Large negative latency offset ({c.DelayMs:F1} ms) detected from {c.SourceUserId} -> {c.TargetUserId} (capture/filter pipeline delay on {c.SourceUserId}; adaptive lookahead at {targetLookaheadMs} ms).");
            }
        }

        return warnings;
    }
}
