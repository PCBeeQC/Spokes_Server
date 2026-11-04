namespace Spokes_Server.Core.Services.Communication.Voice.Dsp;

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using static Spokes_Server.Core.Services.Communication.Voice.Dsp.AcousticRoomProcessor;

internal sealed class PairwiseState
{
    public int DelaySamples { get; set; }
    public float BleedGain { get; set; } = 0.15f;
    public float PeakBleedGain { get; set; } = 0.15f;
    public double Confidence { get; set; }
    public bool IsReciprocalPrior { get; set; }
    public bool IsLatched { get; set; }
    public bool IsLiveVerifiedInCurrentSession { get; set; }
    public int CandidateDelaySamples { get; set; }
    public int CandidateStreakCount { get; set; }
    public double PeakEnvelopeCorrelation { get; set; }
    public double PeakWaveformCorrelation { get; set; }
    public long LastCalibratedSample { get; set; }
    public long LastVerifiedSample { get; set; }

    public bool IsDirectLock(double minConfidence = 0.30) =>
        !IsReciprocalPrior && Confidence >= minConfidence;

    public bool IsLiveDirectLock(double minConfidence = 0.45, bool requireLatched = false) =>
        !IsReciprocalPrior &&
        IsLiveVerifiedInCurrentSession &&
        Confidence >= minConfidence &&
        (!requireLatched || IsLatched);

    public void ApplyReciprocalPrior(int delaySamples, float bleedGain, bool updateGain = true)
    {
        DelaySamples = delaySamples;
        if (updateGain)
        {
            BleedGain = bleedGain;
            PeakBleedGain = bleedGain;
        }
        else
        {
            PeakBleedGain = BleedGain;
        }
        Confidence = 0.24;
        IsReciprocalPrior = true;
        IsLatched = false;
        IsLiveVerifiedInCurrentSession = false;
        CandidateStreakCount = 0;
        PeakEnvelopeCorrelation = 0.0;
        PeakWaveformCorrelation = 0.0;
    }

    public void ShiftLiveLockToOffset(int newDelaySamples, long shiftedAtSample)
    {
        DelaySamples = Math.Clamp(newDelaySamples, -MaxLagSamples, MaxLagSamples);
        IsLatched = false;
        CandidateStreakCount = 0;
        Confidence = Math.Clamp(Confidence, 0.35, 0.52);
        LastVerifiedSample = Math.Max(LastVerifiedSample, shiftedAtSample);
    }

    public void CommitAcceptedPeakMetrics(
        float gain,
        float onsetGain,
        double envCorr,
        double waveCorr,
        bool resetPriorPeaks = false)
    {
        float decayedPriorPeak = resetPriorPeaks ? gain : PeakBleedGain * 0.92f;
        PeakBleedGain = Math.Clamp(Math.Max(gain, Math.Max(decayedPriorPeak, onsetGain)), gain, MaxAllowedAgcBleedGain);
        PeakEnvelopeCorrelation = resetPriorPeaks ? envCorr : Math.Max(PeakEnvelopeCorrelation, envCorr);
        PeakWaveformCorrelation = resetPriorPeaks ? waveCorr : Math.Max(PeakWaveformCorrelation, waveCorr);
    }

    public bool CommitDirectMeasurement(
        int delaySamples,
        float bleedGain,
        double confidence,
        bool isLatched,
        in PairAlignmentMeasurement m,
        bool resetPriorPeaks = false)
    {
        DelaySamples = delaySamples;
        BleedGain = bleedGain;
        Confidence = confidence;
        IsReciprocalPrior = false;
        IsLiveVerifiedInCurrentSession = true;
        CandidateStreakCount = 0;
        IsLatched = isLatched;
        CommitAcceptedPeakMetrics(bleedGain, m.PeakOnsetGain, m.BestEnvCorr, m.FineWaveCorr, resetPriorPeaks);
        return true;
    }

    public bool TryAdvanceCandidateStreak(
        int measuredLagSamples,
        int toleranceSamples,
        float measuredGain,
        float newGainWeight,
        double acceptedConfidence,
        in PairAlignmentMeasurement m)
    {
        if (CandidateStreakCount > 0 && Math.Abs(measuredLagSamples - CandidateDelaySamples) <= toleranceSamples)
        {
            CandidateStreakCount++;
            CandidateDelaySamples = (int)Math.Round(CandidateDelaySamples * 0.60 + measuredLagSamples * 0.40);
            if (CandidateStreakCount >= 3)
            {
                DelaySamples = CandidateDelaySamples;
                BleedGain = BleedGain * (1.0f - newGainWeight) + measuredGain * newGainWeight;
                Confidence = acceptedConfidence;
                IsLiveVerifiedInCurrentSession = true;
                CandidateStreakCount = 0;
                CommitAcceptedPeakMetrics(BleedGain, m.PeakOnsetGain, m.BestEnvCorr, m.FineWaveCorr, resetPriorPeaks: true);
                return true;
            }
        }
        else
        {
            CandidateDelaySamples = measuredLagSamples;
            CandidateStreakCount = 1;
        }
        return false;
    }
}

/// <summary>
/// Manages the $N \times N$ pairwise acoustic delay/gain matrix, latched calibration state machine,
/// near-field dominant speaker resolution, physical reciprocity conservation laws, and 3-node triangle priors.
/// </summary>
internal sealed class AcousticCalibrationMatrix
{
    private readonly Dictionary<(string Source, string Target), PairwiseState> _pairs = new();
    private long _lastGlobalCalibrationSample = -FrameSamples20Ms * 4;

    public IReadOnlyDictionary<(string Source, string Target), PairwiseState> Entries => _pairs;

    public static bool IsConsistentTwoWayAir(int delayAB, int delayBA)
    {
        int sum = delayAB + delayBA;
        return sum >= 48 && sum <= FrameSamples20Ms * 3;
    }

    public static int ComputeReciprocalLagSamples(int forwardDelaySamples) =>
        Math.Clamp(-forwardDelaySamples + (2 * DefaultAirDelaySamples), -MaxLagSamples, MaxLagSamples);

    public static float ComputeReciprocalBleedGain(float forwardBleedGain, float minFloor = 0.008f) =>
        forwardBleedGain > 0.70f ? Math.Clamp(0.025f / forwardBleedGain, minFloor, 0.14f) : 0.14f;

    public bool TryGetPair(string sourceId, string targetId, [NotNullWhen(true)] out PairwiseState? state) =>
        _pairs.TryGetValue((sourceId, targetId), out state);

    public bool TryGetCalibratedPair(string sourceId, string targetId, double minConfidence, [NotNullWhen(true)] out PairwiseState? state) =>
        _pairs.TryGetValue((sourceId, targetId), out state) && state.Confidence >= minConfidence;

    public PairwiseState GetOrCreatePair(string sourceId, string targetId)
    {
        var key = (sourceId, targetId);
        if (!_pairs.TryGetValue(key, out var state))
        {
            state = new PairwiseState();
            _pairs[key] = state;
        }
        return state;
    }

    public void RemoveParticipantPairs(string userId)
    {
        var keysToRemove = _pairs.Keys
            .Where(k => string.Equals(k.Source, userId, StringComparison.Ordinal) ||
                        string.Equals(k.Target, userId, StringComparison.Ordinal))
            .ToList();
        foreach (var key in keysToRemove)
        {
            _pairs.Remove(key);
        }
    }

    public void SoftenPairsForTrackReplacement(string userId)
    {
        foreach (var kvp in _pairs)
        {
            if (string.Equals(kvp.Key.Source, userId, StringComparison.Ordinal) ||
                string.Equals(kvp.Key.Target, userId, StringComparison.Ordinal))
            {
                kvp.Value.IsLatched = false;
                kvp.Value.IsLiveVerifiedInCurrentSession = false;
                kvp.Value.CandidateStreakCount = 0;
                kvp.Value.Confidence = Math.Min(kvp.Value.Confidence, 0.18);
                kvp.Value.LastCalibratedSample = 0;
            }
        }
    }

    public void AdjustForClockDrift(string userId, int driftCorrectionSamples)
    {
        if (driftCorrectionSamples == 0) return;

        foreach (var kvp in _pairs)
        {
            if (kvp.Value.Confidence < 0.20) continue;

            int delta = string.Equals(kvp.Key.Source, userId, StringComparison.Ordinal)
                ? -driftCorrectionSamples
                : (string.Equals(kvp.Key.Target, userId, StringComparison.Ordinal) ? driftCorrectionSamples : 0);

            if (delta != 0)
            {
                kvp.Value.DelaySamples = Math.Clamp(kvp.Value.DelaySamples + delta, -MaxLagSamples, MaxLagSamples);
                if (kvp.Value.CandidateStreakCount > 0)
                {
                    kvp.Value.CandidateDelaySamples = Math.Clamp(kvp.Value.CandidateDelaySamples + delta, -MaxLagSamples, MaxLagSamples);
                }
            }
        }
    }

    public int GetTargetLookaheadSamples(string targetUserId)
    {
        int requiredSamples = LookaheadSamples;
        foreach (var kvp in _pairs)
        {
            if (string.Equals(kvp.Key.Target, targetUserId, StringComparison.Ordinal) &&
                kvp.Value.DelaySamples < 0 &&
                (kvp.Value.IsDirectLock(0.25) || (kvp.Value.IsReciprocalPrior && kvp.Value.Confidence >= 0.24)))
            {
                int needed = (-kvp.Value.DelaySamples) + (FrameSamples10Ms + FrameSamples10Ms / 2);
                if (needed > requiredSamples) requiredSamples = needed;
            }
        }
        return Math.Clamp(requiredSamples, LookaheadSamples, MaxLookaheadSamples);
    }

    public bool HasDirectNegativeLockTo(string targetUserId) =>
        _pairs.Any(kvp =>
            string.Equals(kvp.Key.Target, targetUserId, StringComparison.Ordinal) &&
            kvp.Value.IsDirectLock(0.25) &&
            kvp.Value.DelaySamples < 0);

    public bool SeedSavedCalibration(
        string sourceUserId,
        string targetUserId,
        int delaySamples,
        float bleedGain,
        double confidence,
        IReadOnlyCollection<string> participantIds)
    {
        var pairState = GetOrCreatePair(sourceUserId, targetUserId);
        bool conflictsWithLiveReverse = TryGetPair(targetUserId, sourceUserId, out var revLive) &&
                                        revLive.IsLiveDirectLock(0.20) &&
                                        !IsConsistentTwoWayAir(delaySamples, revLive.DelaySamples);

        if (conflictsWithLiveReverse ||
            pairState.IsLiveVerifiedInCurrentSession ||
            (!pairState.IsReciprocalPrior && pairState.Confidence >= confidence))
        {
            return false;
        }

        pairState.DelaySamples = Math.Clamp(delaySamples, -MaxLagSamples, MaxLagSamples);
        pairState.BleedGain = Math.Clamp(bleedGain, 0.001f, MaxAllowedAgcBleedGain);
        pairState.PeakBleedGain = pairState.BleedGain;
        pairState.Confidence = Math.Clamp(confidence, 0.50, 0.85);
        pairState.IsReciprocalPrior = false;
        pairState.IsLatched = true;
        pairState.IsLiveVerifiedInCurrentSession = false;
        pairState.CandidateStreakCount = 0;

        SeedReciprocalDelayPrior(sourceUserId, targetUserId, pairState);
        RefreshTransitivePriors(participantIds);
        return true;
    }

    public void UpdateCalibrations(
        Dictionary<string, AcousticParticipantStream> participants,
        long currentSampleEdge,
        AcousticRoomDiagnosticsTracker diagnostics)
    {
        if (participants.Count < 2 || currentSampleEdge - _lastGlobalCalibrationSample < FrameSamples10Ms * 3)
        {
            return;
        }

        bool anyUncalibrated = false;
        foreach (var src in participants.Keys)
        {
            foreach (var tgt in participants.Keys)
            {
                if (!string.Equals(src, tgt, StringComparison.Ordinal) &&
                    (!TryGetPair(src, tgt, out var ps) || !ps.IsDirectLock(0.20)))
                {
                    anyUncalibrated = true;
                    break;
                }
            }
            if (anyUncalibrated) break;
        }

        const long fullWindowSamples = CorrelationWindowEnvelopeSteps * EnvelopeStepSamples; // 19,200 samples (400ms)
        const long fastWindowSamples = FastOnsetWindowEnvelopeSteps * EnvelopeStepSamples;   // 7,680 samples (160ms)

        if (currentSampleEdge < fastWindowSamples + InitialLagHeadroomSamples + EnvelopeStepSamples)
        {
            return;
        }

        bool canUseFullWindow = currentSampleEdge >= fullWindowSamples + InitialLagHeadroomSamples + EnvelopeStepSamples;
        long activeWindowSamples = (!anyUncalibrated && canUseFullWindow) ? fullWindowSamples : (canUseFullWindow ? fullWindowSamples : fastWindowSamples);
        int extraHeadroom = (int)Math.Max(0, currentSampleEdge - (activeWindowSamples + InitialLagHeadroomSamples + EnvelopeStepSamples));
        int maxPositiveHeadroomTarget = RequiresExtendedPositiveHeadroom(participants, currentSampleEdge, fullWindowSamples)
            ? MaxLagSamples
            : DefaultPositiveHeadroomSamples;
        int positiveHeadroomSamples = Math.Min(maxPositiveHeadroomTarget, InitialLagHeadroomSamples + (extraHeadroom / 2));
        int maxPositiveLagSteps = positiveHeadroomSamples / EnvelopeStepSamples;

        _lastGlobalCalibrationSample = currentSampleEdge;

        var participantList = participants.Values.ToList();
        long evalEnd = currentSampleEdge - positiveHeadroomSamples;
        long evalStart = evalEnd - activeWindowSamples;

        var dominantSpeaker = ResolveNearFieldDominantSpeaker(
            participantList,
            evalStart,
            evalEnd,
            (int)activeWindowSamples,
            (int)fastWindowSamples,
            positiveHeadroomSamples,
            maxPositiveLagSteps);

        if (dominantSpeaker == null) return;

        List<(string TargetId, int ShiftDelta)>? livePairShifts = null;
        List<string>? stableTargetsInSweep = null;

        foreach (var target in participantList)
        {
            if (ReferenceEquals(target, dominantSpeaker)) continue;

            long pairEpoch = Math.Max(dominantSpeaker.TrackEpochSample, target.TrackEpochSample);
            if (evalEnd - pairEpoch < fastWindowSamples) continue;

            long pairEvalStart = Math.Max(evalStart, pairEpoch);
            int pairWindowSamples = (int)(evalEnd - pairEvalStart);

            var pairState = GetOrCreatePair(dominantSpeaker.UserId, target.UserId);
            long minInterval = !pairState.IsLiveDirectLock(0.25) ? FrameSamples10Ms * 3 : FrameSamples20Ms * 2;
            if (currentSampleEdge - pairState.LastCalibratedSample < minInterval) continue;
            pairState.LastCalibratedSample = currentSampleEdge;

            bool wasLocked = pairState.IsLiveDirectLock(0.30);
            bool wasVerifiedLiveDirect = pairState.IsLiveDirectLock(0.45);
            int previousDelaySamples = pairState.DelaySamples;

            TryGetPair(target.UserId, dominantSpeaker.UserId, out var reversePairState);
            int? topologicalPriorSamples = TryComputeTopologicalLagPriorSamples(
                dominantSpeaker.UserId, target.UserId, participants.Keys, out int topoLag, out _, out _)
                ? topoLag
                : null;

            if (CalibratePair(dominantSpeaker, target, pairState, reversePairState, topologicalPriorSamples, pairEvalStart, pairWindowSamples, maxPositiveLagSteps))
            {
                if (pairState.IsLiveDirectLock(0.20))
                {
                    pairState.LastVerifiedSample = currentSampleEdge;
                }
                if (!wasLocked && pairState.Confidence >= 0.30)
                {
                    double delayMs = Math.Round(pairState.DelaySamples * 1000.0 / SampleRate, 1);
                    diagnostics.RecordEvent($"Locked {dominantSpeaker.UserId}->{target.UserId} ({delayMs:+0.0;-0.0;0.0}ms)");
                }

                int shiftDelta = pairState.DelaySamples - previousDelaySamples;
                if (wasVerifiedLiveDirect && pairState.IsLiveDirectLock(0.55))
                {
                    if (Math.Abs(shiftDelta) >= (SampleRate * 10) / 1000 && pairState.PeakWaveformCorrelation >= 0.40)
                    {
                        (livePairShifts ??= []).Add((target.UserId, shiftDelta));
                    }
                    else if (Math.Abs(shiftDelta) <= EnvelopeStepSamples * 2)
                    {
                        (stableTargetsInSweep ??= []).Add(target.UserId);
                    }
                }

                SeedReciprocalDelayPrior(dominantSpeaker.UserId, target.UserId, pairState, wasVerifiedLiveDirect ? shiftDelta : null);
                RefreshTransitivePriors(participants.Keys);
            }
        }

        if (livePairShifts != null)
        {
            PropagateKinematicNodeShifts(dominantSpeaker.UserId, livePairShifts, stableTargetsInSweep, participants.Keys, currentSampleEdge);
            RefreshTransitivePriors(participants.Keys);
        }
    }

    private AcousticParticipantStream? ResolveNearFieldDominantSpeaker(
        List<AcousticParticipantStream> participantList,
        long evalStart,
        long evalEnd,
        int activeWindowSamples,
        int fastWindowSamples,
        int positiveHeadroomSamples,
        int maxPositiveLagSteps)
    {
        var activities = new List<(AcousticParticipantStream State, float WindowRms, float NeighborhoodRms)>(participantList.Count);
        foreach (var p in participantList)
        {
            float winRms = p.ComputeEnvelopeRms(evalStart, activeWindowSamples);
            float priorRms = evalStart >= positiveHeadroomSamples
                ? p.ComputeEnvelopeRms(evalStart - positiveHeadroomSamples, positiveHeadroomSamples)
                : 0f;
            float futureRms = p.ComputeEnvelopeRms(evalEnd, positiveHeadroomSamples);
            float recentEdgeRms = positiveHeadroomSamples > FrameSamples20Ms * 3
                ? p.ComputeEnvelopeRms(evalEnd + positiveHeadroomSamples - FrameSamples20Ms * 3, FrameSamples20Ms * 3)
                : futureRms;
            float neighRms = Math.Max(winRms, Math.Max(priorRms, Math.Max(futureRms, recentEdgeRms)));
            activities.Add((p, winRms, neighRms));
        }

        activities.Sort((a, b) => b.WindowRms.CompareTo(a.WindowRms));
        if (activities.Count == 0 || activities[0].WindowRms < VocalActivityFloorRms) return null;

        var bestCandidate = activities[0];
        float secondMaxNeighborhood = 0f;
        for (int i = 1; i < activities.Count; i++)
        {
            if (activities[i].NeighborhoodRms > secondMaxNeighborhood)
            {
                secondMaxNeighborhood = activities[i].NeighborhoodRms;
            }
        }

        if (secondMaxNeighborhood < VocalActivityFloorRms)
        {
            return IsDelayedBleedTailFromVerifiedSpeaker(bestCandidate.State, bestCandidate.WindowRms, evalStart, activeWindowSamples, participantList)
                ? null
                : bestCandidate.State;
        }

        for (int i = 1; i < activities.Count; i++)
        {
            var other = activities[i];
            if (other.NeighborhoodRms < VocalActivityFloorRms) continue;

            if (other.WindowRms >= VocalActivityFloorRms)
            {
                long pairEpoch = Math.Max(bestCandidate.State.TrackEpochSample, other.State.TrackEpochSample);
                if (evalEnd - pairEpoch < fastWindowSamples)
                {
                    if (bestCandidate.WindowRms < other.WindowRms * 1.45f) return null;
                    continue;
                }

                long pairEvalStart = Math.Max(evalStart, pairEpoch);
                int pairWindowSamples = (int)(evalEnd - pairEvalStart);

                TryGetPair(bestCandidate.State.UserId, other.State.UserId, out var reverseForOtherToBest);
                bool measuredOtherToBest = AcousticLagCorrelator.TryMeasurePairAlignment(
                    other.State, bestCandidate.State, reverseForOtherToBest, null,
                    pairEvalStart, pairWindowSamples, maxPositiveLagSteps, requireDominancePass: false, out var otherToBest);

                if (measuredOtherToBest && otherToBest.DominanceScoreDb >= 3.1 && otherToBest.BestEnvCorr >= 0.52 &&
                    !IsDelayedBleedTailFromVerifiedSpeaker(other.State, other.WindowRms, evalStart, activeWindowSamples, participantList))
                {
                    bestCandidate = other;
                    continue;
                }

                float bestGrowth = bestCandidate.NeighborhoodRms / Math.Max(bestCandidate.WindowRms, 1e-5f);
                float otherGrowth = other.NeighborhoodRms / Math.Max(other.WindowRms, 1e-5f);
                bool isKnownAgcTarget = reverseForOtherToBest != null && reverseForOtherToBest.IsDirectLock(0.45) && reverseForOtherToBest.BleedGain > 0.70f;
                if (!isKnownAgcTarget &&
                    bestCandidate.WindowRms < other.NeighborhoodRms * 1.45f &&
                    otherGrowth > Math.Max(1.65f, bestGrowth * 1.45f))
                {
                    return null;
                }

                if (measuredOtherToBest && otherToBest.DominanceScoreDb <= -3.1 && bestCandidate.WindowRms >= other.WindowRms * 1.45f)
                {
                    continue;
                }

                if (bestCandidate.WindowRms < other.WindowRms * 1.45f)
                {
                    TryGetPair(other.State.UserId, bestCandidate.State.UserId, out var reverseForBestToOther);
                    bool measuredBestToOther = AcousticLagCorrelator.TryMeasurePairAlignment(
                        bestCandidate.State, other.State, reverseForBestToOther, null,
                        pairEvalStart, pairWindowSamples, maxPositiveLagSteps, requireDominancePass: true, out var bestToOther);

                    if (!measuredBestToOther || (bestToOther.DirectnessDb < 4.2 && bestToOther.BestEnvCorr < 0.72))
                    {
                        return null;
                    }
                }
            }
            else if (bestCandidate.WindowRms < other.NeighborhoodRms * 1.45f)
            {
                return null;
            }
        }

        return IsDelayedBleedTailFromVerifiedSpeaker(bestCandidate.State, bestCandidate.WindowRms, evalStart, activeWindowSamples, participantList)
            ? null
            : bestCandidate.State;
    }

    private bool IsDelayedBleedTailFromVerifiedSpeaker(
        AcousticParticipantStream candidate,
        float candidateWindowRms,
        long evalStart,
        int activeWindowSamples,
        List<AcousticParticipantStream> participantList)
    {
        long evalEnd = evalStart + activeWindowSamples;
        foreach (var r in participantList)
        {
            if (!ReferenceEquals(r, candidate) &&
                TryGetPair(r.UserId, candidate.UserId, out var cal) &&
                cal.IsLiveDirectLock(0.45) &&
                cal.DelaySamples > FrameSamples10Ms)
            {
                long overlapTgtStart = Math.Max(evalStart, r.TrackEpochSample + cal.DelaySamples);
                int overlapLen = (int)(evalEnd - overlapTgtStart);
                if (overlapLen >= FrameSamples20Ms)
                {
                    float rShiftedRms = r.ComputeEnvelopeRms(overlapTgtStart - cal.DelaySamples, overlapLen);
                    float candOverlapRms = overlapTgtStart == evalStart
                        ? candidateWindowRms
                        : candidate.ComputeEnvelopeRms(overlapTgtStart, overlapLen);
                    if (rShiftedRms >= VocalActivityFloorRms &&
                        candOverlapRms <= rShiftedRms * Math.Max(cal.BleedGain * 1.65f, 0.70f))
                    {
                        return true;
                    }
                }
            }
        }
        return false;
    }

    private void SeedReciprocalDelayPrior(
        string calibratedSourceId,
        string calibratedTargetId,
        PairwiseState forwardState,
        int? verifiedForwardShiftDelta = null)
    {
        if (!forwardState.IsDirectLock(0.35)) return;

        var reverseState = GetOrCreatePair(calibratedTargetId, calibratedSourceId);
        bool gainProductInversion = forwardState.BleedGain * reverseState.BleedGain > 0.55f;
        bool violatesTwoWayAir = !IsConsistentTwoWayAir(forwardState.DelaySamples, reverseState.DelaySamples);
        bool violatesPhysicalConsistency = violatesTwoWayAir || gainProductInversion;

        if (!reverseState.IsLiveVerifiedInCurrentSession || reverseState.IsReciprocalPrior || reverseState.Confidence < 0.25)
        {
            if (reverseState.Confidence < 0.25 || reverseState.IsReciprocalPrior || (forwardState.IsLiveVerifiedInCurrentSession && violatesPhysicalConsistency))
            {
                reverseState.ApplyReciprocalPrior(
                    ComputeReciprocalLagSamples(forwardState.DelaySamples),
                    ComputeReciprocalBleedGain(forwardState.BleedGain));
            }
            return;
        }

        // Reverse direction was already live-calibrated in this session:
        // Never allow a low-waveform-coherence envelope lock to clobber a high-coherence live reverse lock.
        if (forwardState.PeakWaveformCorrelation < 0.38 && reverseState.PeakWaveformCorrelation >= 0.45)
        {
            return;
        }

        bool isSignificantlyNewerLiveLatch = forwardState.IsLatched &&
                                             forwardState.Confidence >= 0.66 &&
                                             forwardState.LastVerifiedSample - reverseState.LastVerifiedSample > SampleRate * 2;
        bool hasVerifiedForwardShift = verifiedForwardShiftDelta.HasValue &&
                                       Math.Abs(verifiedForwardShiftDelta.Value) >= (SampleRate * 10) / 1000 &&
                                       forwardState.LastVerifiedSample - reverseState.LastVerifiedSample > SampleRate;
        bool shouldReconcileLiveReverse = forwardState.IsLiveVerifiedInCurrentSession &&
                                          forwardState.IsLatched &&
                                          (((!reverseState.IsLatched ||
                                             forwardState.Confidence >= reverseState.Confidence + 0.15 ||
                                             isSignificantlyNewerLiveLatch) &&
                                            violatesPhysicalConsistency) ||
                                           hasVerifiedForwardShift);

        if (!shouldReconcileLiveReverse) return;

        if (gainProductInversion && !violatesTwoWayAir)
        {
            reverseState.ApplyReciprocalPrior(
                ComputeReciprocalLagSamples(forwardState.DelaySamples),
                ComputeReciprocalBleedGain(forwardState.BleedGain));
            return;
        }

        int shiftedReverseDelay = ComputeReciprocalLagSamples(forwardState.DelaySamples);
        if (verifiedForwardShiftDelta.HasValue &&
            Math.Abs(verifiedForwardShiftDelta.Value) >= (SampleRate * 10) / 1000)
        {
            int exactAirPreservingReverse = Math.Clamp(reverseState.DelaySamples - verifiedForwardShiftDelta.Value, -MaxLagSamples, MaxLagSamples);
            if (IsConsistentTwoWayAir(forwardState.DelaySamples, exactAirPreservingReverse))
            {
                shiftedReverseDelay = exactAirPreservingReverse;
            }
        }

        reverseState.ShiftLiveLockToOffset(shiftedReverseDelay, forwardState.LastVerifiedSample);
    }

    private void PropagateKinematicNodeShifts(
        string dominantSpeakerId,
        List<(string TargetId, int ShiftDelta)> livePairShifts,
        List<string>? stableTargetsInSweep,
        IReadOnlyCollection<string> participantIds,
        long currentSampleEdge)
    {
        if (participantIds.Count < 3) return;

        bool hasStableAnchor = (stableTargetsInSweep != null && stableTargetsInSweep.Count > 0) ||
                               participantIds.Any(id =>
                                   !string.Equals(id, dominantSpeakerId, StringComparison.Ordinal) &&
                                   !livePairShifts.Any(s => string.Equals(s.TargetId, id, StringComparison.Ordinal)) &&
                                   TryGetPair(dominantSpeakerId, id, out var anchor) &&
                                   anchor.IsLiveDirectLock(0.45) &&
                                   currentSampleEdge - anchor.LastVerifiedSample <= (SampleRate * 3) / 2);

        if (!hasStableAnchor) return;

        foreach (var (shiftedTargetId, shiftDelta) in livePairShifts)
        {
            foreach (var thirdId in participantIds)
            {
                if (string.Equals(thirdId, dominantSpeakerId, StringComparison.Ordinal) ||
                    string.Equals(thirdId, shiftedTargetId, StringComparison.Ordinal))
                {
                    continue;
                }

                if (TryGetPair(shiftedTargetId, thirdId, out var tk) &&
                    tk.IsLiveVerifiedInCurrentSession &&
                    !tk.IsReciprocalPrior &&
                    currentSampleEdge - tk.LastVerifiedSample > SampleRate)
                {
                    tk.ShiftLiveLockToOffset(tk.DelaySamples - shiftDelta, currentSampleEdge);
                }

                if (TryGetPair(thirdId, shiftedTargetId, out var kt) &&
                    kt.IsLiveVerifiedInCurrentSession &&
                    !kt.IsReciprocalPrior &&
                    currentSampleEdge - kt.LastVerifiedSample > SampleRate)
                {
                    kt.ShiftLiveLockToOffset(kt.DelaySamples + shiftDelta, currentSampleEdge);
                }
            }
        }
    }

    private bool RequiresExtendedPositiveHeadroom(
        IReadOnlyDictionary<string, AcousticParticipantStream> participants,
        long currentSampleEdge,
        long fullWindowSamples)
    {
        const int highPositiveThresholdSamples = (SampleRate * 190) / 1000;
        const int highNegativeReverseSamples = -(SampleRate * 165) / 1000;
        const int sub165MsSamples = (SampleRate * 165) / 1000;

        foreach (var cal in _pairs.Values)
        {
            if (cal.IsDirectLock(0.35) &&
                (cal.DelaySamples >= highPositiveThresholdSamples || cal.DelaySamples <= highNegativeReverseSamples))
            {
                return true;
            }
        }

        foreach (var src in participants.Values)
        {
            foreach (var tgt in participants.Values)
            {
                if (ReferenceEquals(src, tgt) || !src.EstimatedCaptureLatencyMs.HasValue || !tgt.EstimatedCaptureLatencyMs.HasValue)
                {
                    continue;
                }

                bool hasKnownSub165Pair =
                    (TryGetPair(src.UserId, tgt.UserId, out var fwdKnown) && fwdKnown.IsDirectLock(0.30) && Math.Abs(fwdKnown.DelaySamples) < sub165MsSamples) ||
                    (TryGetPair(tgt.UserId, src.UserId, out var revKnown) && revKnown.IsDirectLock(0.30) && Math.Abs(revKnown.DelaySamples) < sub165MsSamples);

                if (!hasKnownSub165Pair)
                {
                    double deltaMs = (tgt.EstimatedCaptureLatencyMs.Value - src.EstimatedCaptureLatencyMs.Value)
                                   + (((tgt.ServerToClientDelayMs ?? 0.0) - (src.ServerToClientDelayMs ?? 0.0)) * 0.5);
                    if (deltaMs >= 40.0) return true;
                }
            }
        }

        if (currentSampleEdge >= fullWindowSamples + DefaultPositiveHeadroomSamples + FrameSamples20Ms * 6)
        {
            foreach (var src in participants.Keys)
            {
                foreach (var tgt in participants.Keys)
                {
                    if (string.Equals(src, tgt, StringComparison.Ordinal)) continue;

                    bool forwardLocked = TryGetPair(src, tgt, out var fwd) && fwd.IsDirectLock(0.30);
                    bool reverseSub165Locked = TryGetPair(tgt, src, out var rev) && rev.IsDirectLock(0.30) && Math.Abs(rev.DelaySamples) < sub165MsSamples;
                    if (!forwardLocked && !reverseSub165Locked) return true;
                }
            }
        }

        return false;
    }

    private bool TryGetVerifiedRelativeDspOffset(
        string fromId,
        string toId,
        out int dspToMinusFromSamples,
        out double confidence,
        out long verifiedSample)
    {
        dspToMinusFromSamples = 0;
        confidence = 0.0;
        verifiedSample = 0;

        bool hasFwd = TryGetPair(fromId, toId, out var fwd) && fwd.IsLiveDirectLock(0.45);
        bool hasBwd = TryGetPair(toId, fromId, out var bwd) && bwd.IsLiveDirectLock(0.45);

        if (hasFwd && hasBwd)
        {
            int twoWayAir = fwd!.DelaySamples + bwd!.DelaySamples;
            if (IsConsistentTwoWayAir(fwd.DelaySamples, bwd.DelaySamples) &&
                Math.Abs(fwd.LastVerifiedSample - bwd.LastVerifiedSample) <= SampleRate * 10)
            {
                int estAir = Math.Clamp(twoWayAir / 2, 48, FrameSamples20Ms * 2);
                dspToMinusFromSamples = fwd.DelaySamples - estAir;
                confidence = Math.Max(fwd.Confidence, bwd.Confidence);
                verifiedSample = Math.Max(fwd.LastVerifiedSample, bwd.LastVerifiedSample);
                return true;
            }

            bool preferFwd = fwd.LastVerifiedSample > bwd.LastVerifiedSample ||
                             (fwd.LastVerifiedSample == bwd.LastVerifiedSample && fwd.Confidence >= bwd.Confidence);
            dspToMinusFromSamples = preferFwd ? (fwd.DelaySamples - DefaultAirDelaySamples) : -(bwd.DelaySamples - DefaultAirDelaySamples);
            confidence = preferFwd ? fwd.Confidence : bwd.Confidence;
            verifiedSample = preferFwd ? fwd.LastVerifiedSample : bwd.LastVerifiedSample;
            return true;
        }

        if (hasFwd)
        {
            dspToMinusFromSamples = fwd!.DelaySamples - DefaultAirDelaySamples;
            confidence = fwd.Confidence;
            verifiedSample = fwd.LastVerifiedSample;
            return true;
        }

        if (hasBwd)
        {
            dspToMinusFromSamples = -(bwd!.DelaySamples - DefaultAirDelaySamples);
            confidence = bwd.Confidence;
            verifiedSample = bwd.LastVerifiedSample;
            return true;
        }

        return false;
    }

    private bool TryComputeTopologicalLagPriorSamples(
        string sourceId,
        string targetId,
        IEnumerable<string> participantIds,
        out int priorLagSamples,
        out float priorBleedGain,
        out bool usedTrianglePath)
    {
        priorLagSamples = 0;
        priorBleedGain = 0.14f;
        usedTrianglePath = false;

        bool hasDirectReverse = TryGetPair(targetId, sourceId, out var rev) && rev.IsLiveDirectLock(0.45);
        long revVerifiedSample = hasDirectReverse ? rev!.LastVerifiedSample : 0;

        double bestTriangleScore = 0.0;
        long bestTriangleMinSample = -1;
        long bestTriangleMaxSample = -1;
        int bestTriangleLag = 0;
        float bestTriangleGain = 0.14f;
        bool foundTriangle = false;

        foreach (var midId in participantIds)
        {
            if (string.Equals(midId, sourceId, StringComparison.Ordinal) ||
                string.Equals(midId, targetId, StringComparison.Ordinal))
            {
                continue;
            }

            if (TryGetVerifiedRelativeDspOffset(sourceId, midId, out int dspMidMinusSrc, out double confSk, out long verSk) &&
                TryGetVerifiedRelativeDspOffset(midId, targetId, out int dspTgtMinusMid, out double confKt, out long verKt))
            {
                double pathScore = Math.Min(confSk, confKt);
                long minVer = Math.Min(verSk, verKt);
                bool isBetterTriangle = !foundTriangle ||
                                        minVer > bestTriangleMinSample + SampleRate ||
                                        (Math.Abs(minVer - bestTriangleMinSample) <= SampleRate && pathScore > bestTriangleScore);
                if (isBetterTriangle)
                {
                    bestTriangleScore = pathScore;
                    bestTriangleMinSample = minVer;
                    bestTriangleMaxSample = Math.Max(verSk, verKt);
                    bestTriangleLag = Math.Clamp(dspMidMinusSrc + dspTgtMinusMid + DefaultAirDelaySamples, -MaxLagSamples, MaxLagSamples);

                    if (TryGetPair(midId, targetId, out var ktDirect) && ktDirect.IsLiveDirectLock(0.45))
                    {
                        bestTriangleGain = Math.Clamp(ktDirect.BleedGain, 0.05f, 0.45f);
                    }
                    else if (TryGetPair(targetId, midId, out var tkDirect) && tkDirect.IsLiveDirectLock(0.45) && tkDirect.BleedGain > 0.70f)
                    {
                        bestTriangleGain = ComputeReciprocalBleedGain(tkDirect.BleedGain, minFloor: 0.05f);
                    }
                    else
                    {
                        bestTriangleGain = 0.14f;
                    }

                    foundTriangle = true;
                }
            }
        }

        bool triangleIsFresherThanReverse = !hasDirectReverse ||
                                            bestTriangleMinSample > revVerifiedSample ||
                                            (bestTriangleMaxSample - revVerifiedSample > SampleRate * 2 &&
                                             revVerifiedSample - bestTriangleMinSample <= SampleRate * 3);
        if (foundTriangle && triangleIsFresherThanReverse)
        {
            priorLagSamples = bestTriangleLag;
            priorBleedGain = bestTriangleGain;
            usedTrianglePath = true;
            return true;
        }

        if (hasDirectReverse)
        {
            priorLagSamples = ComputeReciprocalLagSamples(rev!.DelaySamples);
            priorBleedGain = ComputeReciprocalBleedGain(rev.BleedGain);
            return true;
        }

        return false;
    }

    private void RefreshTransitivePriors(IReadOnlyCollection<string> participantIds)
    {
        if (participantIds.Count < 3) return;

        foreach (var srcId in participantIds)
        {
            foreach (var tgtId in participantIds)
            {
                if (string.Equals(srcId, tgtId, StringComparison.Ordinal)) continue;
                if (TryGetPair(srcId, tgtId, out var existing) && existing.IsDirectLock(0.25)) continue;

                if (TryComputeTopologicalLagPriorSamples(srcId, tgtId, participantIds, out int triLag, out float triGain, out bool usedTrianglePath))
                {
                    existing ??= GetOrCreatePair(srcId, tgtId);
                    if (existing.Confidence < 0.24 || existing.IsReciprocalPrior)
                    {
                        existing.ApplyReciprocalPrior(triLag, triGain, updateGain: existing.Confidence < 0.24 || usedTrianglePath);
                    }
                }
            }
        }
    }

    private static bool CalibratePair(
        AcousticParticipantStream source,
        AcousticParticipantStream target,
        PairwiseState pairState,
        PairwiseState? reversePairState,
        int? topologicalPriorLagSamples,
        long startSample,
        int windowSamples,
        int maxPositiveLagSteps)
    {
        if (!AcousticLagCorrelator.TryMeasurePairAlignment(
                source, target, reversePairState, topologicalPriorLagSamples,
                startSample, windowSamples, maxPositiveLagSteps, requireDominancePass: true, out var m))
        {
            return false;
        }

        double combinedConfidence = Math.Clamp(m.BestEnvCorr + (m.BarkCorr > 0.20 ? m.BarkCorr * 0.08 : 0.0), 0.25, 0.98);
        bool isFullWindow = windowSamples >= CorrelationWindowEnvelopeSteps * EnvelopeStepSamples;
        bool isFullyInBounds = startSample + m.MeasuredLagSamples >= target.TrackEpochSample - FrameSamples10Ms;
        double maxWindowConf = isFullWindow ? 0.98 : 0.78;
        double effectiveConfidence = Math.Min(combinedConfidence, maxWindowConf);

        // Cross-session re-anchoring on pre-seeded or unverified pairs
        if (!pairState.IsLiveVerifiedInCurrentSession)
        {
            if (Math.Abs(m.MeasuredLagSamples - pairState.DelaySamples) <= EnvelopeStepSamples * 3)
            {
                bool wasPrior = pairState.IsReciprocalPrior;
                int blendedLag = (int)Math.Round(pairState.DelaySamples * 0.65 + m.MeasuredLagSamples * 0.35);
                float blendedGain = pairState.BleedGain * 0.65f + m.MeasuredGain * 0.35f;
                double conf = Math.Clamp(Math.Max(pairState.Confidence, effectiveConfidence), 0.25, 0.99);
                bool latch = (pairState.IsLatched && conf >= 0.64) || (isFullWindow && isFullyInBounds && conf >= 0.64);
                return pairState.CommitDirectMeasurement(blendedLag, blendedGain, conf, latch, m, resetPriorPeaks: wasPrior);
            }

            if (pairState.IsDirectLock(0.65))
            {
                long savedMissingSamples = Math.Max(0L, target.TrackEpochSample - (startSample + pairState.DelaySamples));
                int savedOverlapSamples = windowSamples - (int)Math.Min(windowSamples, savedMissingSamples);
                if (savedOverlapSamples < Math.Max(32 * EnvelopeStepSamples, (int)(windowSamples * 0.55)))
                {
                    return false;
                }
            }

            if (combinedConfidence >= 0.48)
            {
                double conf = Math.Clamp(effectiveConfidence, 0.48, maxWindowConf);
                return pairState.CommitDirectMeasurement(
                    m.MeasuredLagSamples, m.MeasuredGain, conf,
                    isFullWindow && isFullyInBounds && conf >= 0.64, m, resetPriorPeaks: true);
            }
        }

        // Double-Talk / Noise Guard on already-verified live pairs
        if (pairState.IsLiveDirectLock(0.70) &&
            pairState.BleedGain < 0.65f &&
            m.MeasuredGain > Math.Max(pairState.BleedGain * 1.45f, 0.42f) &&
            (combinedConfidence < pairState.Confidence - 0.05 || m.FineWaveCorr < pairState.PeakWaveformCorrelation - 0.10))
        {
            return false;
        }

        // Latched Calibration State Machine
        if (pairState.IsLatched)
        {
            if (Math.Abs(m.MeasuredLagSamples - pairState.DelaySamples) <= EnvelopeStepSamples * 2)
            {
                pairState.CandidateStreakCount = 0;
                if (isFullWindow && isFullyInBounds &&
                    Math.Abs(m.MeasuredLagSamples - pairState.DelaySamples) <= EnvelopeStepSamples &&
                    combinedConfidence > pairState.Confidence)
                {
                    pairState.Confidence = Math.Clamp(combinedConfidence, pairState.Confidence, 0.99);
                    if (m.MeasuredGain <= pairState.BleedGain * 1.25f)
                    {
                        pairState.BleedGain = pairState.BleedGain * 0.85f + m.MeasuredGain * 0.15f;
                    }
                }
                if (combinedConfidence >= pairState.Confidence - 0.10 &&
                    (pairState.BleedGain < 0.65f || m.MeasuredGain <= pairState.BleedGain * 1.45f))
                {
                    pairState.CommitAcceptedPeakMetrics(pairState.BleedGain, m.PeakOnsetGain, m.BestEnvCorr, m.FineWaveCorr);
                }
                return true;
            }

            if (isFullWindow && isFullyInBounds &&
                combinedConfidence >= 0.66 && m.BestEnvCorr >= 0.58 && (m.FineWaveCorr >= 0.30 || m.BarkCorr >= 0.34))
            {
                return pairState.TryAdvanceCandidateStreak(
                    m.MeasuredLagSamples, EnvelopeStepSamples * 2, m.MeasuredGain,
                    newGainWeight: 0.50f, Math.Clamp(effectiveConfidence, 0.66, 0.99), m);
            }
            return false;
        }

        if (pairState.Confidence < 0.45 || pairState.IsReciprocalPrior)
        {
            bool wasPrior = pairState.IsReciprocalPrior;
            double conf = Math.Clamp(effectiveConfidence, 0.25, maxWindowConf);
            return pairState.CommitDirectMeasurement(
                m.MeasuredLagSamples, m.MeasuredGain, conf,
                isFullWindow && isFullyInBounds && conf >= 0.64, m, resetPriorPeaks: wasPrior);
        }

        int emaTolerance = m.FineWaveCorr >= 0.50 ? (FrameSamples10Ms / 10) : (EnvelopeStepSamples * 3);
        if (Math.Abs(m.MeasuredLagSamples - pairState.DelaySamples) <= emaTolerance)
        {
            int smoothedLag = (int)Math.Round(pairState.DelaySamples * 0.75 + m.MeasuredLagSamples * 0.25);
            float smoothedGain = pairState.BleedGain * 0.75f + m.MeasuredGain * 0.25f;
            double updatedConf = pairState.Confidence * 0.85 + effectiveConfidence * 0.15;
            double conf = Math.Clamp(Math.Max(pairState.Confidence, updatedConf), 0.25, isFullWindow ? 0.99 : Math.Max(pairState.Confidence, maxWindowConf));
            return pairState.CommitDirectMeasurement(
                smoothedLag, smoothedGain, conf,
                isFullWindow && isFullyInBounds && conf >= 0.64, m);
        }

        if (combinedConfidence >= pairState.Confidence * 0.92)
        {
            return pairState.CommitDirectMeasurement(
                m.MeasuredLagSamples,
                pairState.BleedGain * 0.60f + m.MeasuredGain * 0.40f,
                Math.Clamp(Math.Max(pairState.Confidence, effectiveConfidence), 0.25, maxWindowConf),
                isLatched: false, m);
        }

        if (pairState.Confidence < 0.68 && combinedConfidence > pairState.Confidence + 0.12 && m.FineWaveCorr >= 0.30)
        {
            return pairState.CommitDirectMeasurement(
                m.MeasuredLagSamples,
                pairState.BleedGain * 0.60f + m.MeasuredGain * 0.40f,
                Math.Clamp(effectiveConfidence, 0.55, maxWindowConf),
                isLatched: false, m);
        }

        return combinedConfidence >= pairState.Confidence * 0.90 &&
               pairState.TryAdvanceCandidateStreak(
                   m.MeasuredLagSamples, EnvelopeStepSamples * 3, m.MeasuredGain,
                   newGainWeight: 0.35f, Math.Clamp(Math.Max(pairState.Confidence, combinedConfidence), 0.55, 0.98), m);
    }
}
