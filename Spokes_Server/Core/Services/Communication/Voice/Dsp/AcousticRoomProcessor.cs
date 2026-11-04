namespace Spokes_Server.Core.Services.Communication.Voice.Dsp;

using System;
using System.Collections.Generic;
using System.Linq;

/// <summary>
/// Multi-microphone server-side DSP processor for a <see cref="Spokes_Server.Core.Models.Communication.PhysicalVoiceRoom"/>.
/// Orchestrates per-participant ring-buffer streams (<see cref="AcousticParticipantStream"/>),
/// the $N \times N$ pairwise calibration &amp; topological prior matrix (<see cref="AcousticCalibrationMatrix"/>),
/// hybrid envelope/waveform/GCC-PHAT/Bark lag correlation (<see cref="AcousticLagCorrelator"/>),
/// 50%-overlapped Hann (COLA) NWaves Power Wiener bleed subtraction (<see cref="AcousticSpectralCanceller"/>),
/// and rolling 250ms diagnostic telemetry (<see cref="AcousticRoomDiagnosticsTracker"/>).
/// </summary>
public sealed class AcousticRoomProcessor
{
    public const int SampleRate = 48000;
    public const int FrameSamples10Ms = 480; // 10ms @ 48kHz
    public const int FrameSamples20Ms = 960; // 20ms @ 48kHz
    public const int MaxLagMs = 340;
    public const int MaxLagSamples = (SampleRate * MaxLagMs) / 1000; // +/-16320 samples (+/-340ms)
    public const int LookaheadMs = 50;
    public const int LookaheadSamples = (SampleRate * LookaheadMs) / 1000; // 2400 samples (50ms baseline)
    public const int MaxLookaheadMs = 355;
    public const int MaxLookaheadSamples = (SampleRate * MaxLookaheadMs) / 1000; // 17040 samples (355ms max adaptive)

    internal const int InitialLagHeadroomSamples = (SampleRate * 150) / 1000;
    internal const int DefaultPositiveHeadroomSamples = (SampleRate * 220) / 1000;
    internal const float MaxAllowedAgcBleedGain = 8.5f;
    internal const int DefaultAirDelaySamples = 192; // ~4ms = ~1.37 meters 1-way air assumption
    internal const int RingBufferCapacity = 96000;   // 2.0 seconds @ 48kHz
    internal const int EnvelopeStepSamples = 96;     // 2ms @ 48kHz
    internal const int MaxLagEnvelopeSteps = MaxLagSamples / EnvelopeStepSamples;
    internal const int CorrelationWindowEnvelopeSteps = 200; // 400ms steady-state window
    internal const int FastOnsetWindowEnvelopeSteps = 80;    // 160ms fast initial lock window
    internal const int DoubleTalkAttackSubFrames = 4;        // 40ms sustained attack rejects <20ms clicks
    internal const float VocalActivityFloorRms = 0.0045f;    // ~ -47.0 dBFS vocal passband activity floor
    internal const int BarkBandCount = 6;
    internal const int BarkFftSize = 256;
    internal const int BarkStepSamples = EnvelopeStepSamples * 2; // 4ms (192 samples)
    internal const int GccPhatFftSize = 512;
    internal const float PostLevelerTargetSpeechRms = 0.10f; // -20.0 dBFS standard VoIP target

    private readonly object _syncLock = new();
    private readonly Dictionary<string, AcousticParticipantStream> _participants = new(StringComparer.Ordinal);
    private readonly AcousticCalibrationMatrix _matrix = new();
    private readonly AcousticRoomDiagnosticsTracker _diagnostics = new();

    private long _globalSampleClock;
    private long? _roomEpochWallClockMs;

    public int CurrentLookaheadMs => LookaheadMs;

    /// <summary>
    /// Ensures a participant is registered in the processor's $N \times N$ matrix.
    /// </summary>
    public void RegisterParticipant(string userId)
    {
        if (string.IsNullOrWhiteSpace(userId)) return;
        lock (_syncLock)
        {
            GetOrCreateParticipantLocked(userId, _globalSampleClock);
        }
    }

    /// <summary>
    /// Synchronizes the set of registered participants with the active co-located physical room group.
    /// </summary>
    public void SyncParticipants(IReadOnlyCollection<string> activeUserIds)
    {
        if (activeUserIds == null) return;
        lock (_syncLock)
        {
            var activeSet = new HashSet<string>(activeUserIds.Where(u => !string.IsNullOrWhiteSpace(u)), StringComparer.Ordinal);
            foreach (var uid in activeSet)
            {
                GetOrCreateParticipantLocked(uid, _globalSampleClock);
            }

            foreach (var uid in _participants.Keys.Where(k => !activeSet.Contains(k)).ToList())
            {
                RemoveParticipantLocked(uid);
            }
        }
    }

    /// <summary>
    /// Notifies the processor that a participant's audio track was replaced (e.g. switching RNNoise, DeepFilterNet, or WebRTC).
    /// </summary>
    public void NotifyParticipantTrackReplaced(string userId)
    {
        if (string.IsNullOrWhiteSpace(userId)) return;
        lock (_syncLock)
        {
            if (_participants.TryGetValue(userId, out var state))
            {
                state.OnTrackReplaced(_globalSampleClock);
            }
            else
            {
                _participants[userId] = new AcousticParticipantStream(userId, _globalSampleClock);
            }

            _matrix.SoftenPairsForTrackReplacement(userId);
            _diagnostics.RecordEvent($"Filter/Track switched ({userId})");
        }
    }

    /// <summary>
    /// Updates client-reported audio capture/filter pipeline telemetry for a participant.
    /// </summary>
    public void SetParticipantClientPipelineInfo(
        string userId,
        string filterType,
        double estimatedCaptureLatencyMs,
        double serverToClientDelayMs = 0.0)
    {
        if (string.IsNullOrWhiteSpace(userId)) return;
        lock (_syncLock)
        {
            var state = GetOrCreateParticipantLocked(userId, _globalSampleClock);
            state.FilterType = string.IsNullOrWhiteSpace(filterType) ? "webrtc" : filterType.Trim();
            if (estimatedCaptureLatencyMs > 0 && estimatedCaptureLatencyMs <= 500.0)
            {
                state.EstimatedCaptureLatencyMs = estimatedCaptureLatencyMs;
            }
            if (serverToClientDelayMs >= 0 && serverToClientDelayMs <= 2000.0)
            {
                state.ServerToClientDelayMs = serverToClientDelayMs;
            }
        }
    }

    /// <summary>
    /// Pre-loads a previously persisted directional calibration for a device pair to warm-start lookahead and bleed gating.
    /// </summary>
    public void SeedSavedPairCalibration(
        string sourceUserId,
        string targetUserId,
        int delaySamples,
        float bleedGain,
        double confidence = 0.65)
    {
        if (string.IsNullOrWhiteSpace(sourceUserId) ||
            string.IsNullOrWhiteSpace(targetUserId) ||
            string.Equals(sourceUserId, targetUserId, StringComparison.Ordinal))
        {
            return;
        }

        lock (_syncLock)
        {
            GetOrCreateParticipantLocked(sourceUserId, _globalSampleClock);
            GetOrCreateParticipantLocked(targetUserId, _globalSampleClock);

            if (_matrix.SeedSavedCalibration(sourceUserId, targetUserId, delaySamples, bleedGain, confidence, _participants.Keys))
            {
                foreach (var p in _participants.Values)
                {
                    p.AppliedLookaheadSamples = _matrix.GetTargetLookaheadSamples(p.UserId);
                }
            }
        }
    }

    /// <summary>
    /// Removes a participant and their pairwise matrix entries from the processor.
    /// </summary>
    public void UnregisterParticipant(string userId)
    {
        if (string.IsNullOrWhiteSpace(userId)) return;
        lock (_syncLock)
        {
            RemoveParticipantLocked(userId);
        }
    }

    /// <summary>
    /// Resets rolling diagnostic telemetry counters and timeline history while preserving learned calibrations.
    /// </summary>
    public void ResetDiagnosticCounters()
    {
        lock (_syncLock)
        {
            _diagnostics.Reset(_globalSampleClock, _participants.Values);
        }
    }

    /// <summary>
    /// Returns a snapshot of all learned pairwise calibrations in the room.
    /// </summary>
    public IReadOnlyList<PairwiseAcousticCalibration> GetCalibrations()
    {
        lock (_syncLock)
        {
            return AcousticRoomDiagnosticsTracker.BuildCalibrationsList(_matrix);
        }
    }

    /// <summary>
    /// Returns a complete diagnostic snapshot of participant gate states, pairwise calibrations,
    /// decomposed room physics, timeline buckets, and health warnings.
    /// </summary>
    public PhysicalRoomDiagnosticSnapshot GetDiagnosticSnapshot()
    {
        lock (_syncLock)
        {
            return _diagnostics.BuildSnapshot(_participants, _matrix, ResolveEffectiveLookaheadSamplesLocked);
        }
    }

    /// <summary>
    /// Pushes a raw 16-bit PCM frame for a participant and returns the isolated/cleaned 16-bit PCM frame.
    /// </summary>
    public short[] ProcessParticipantFrame(string userId, ReadOnlySpan<short> pcm16Input, long? wallClockTimestampMs = null)
    {
        lock (_syncLock)
        {
            long? expectedStartSample = ResolveExpectedStartSampleLocked(wallClockTimestampMs);
            var state = IngestParticipantPcmLocked(userId, pcm16Input, wallClockTimestampMs, expectedStartSample, _globalSampleClock);
            if (state.WriteSampleIndex > _globalSampleClock)
            {
                _globalSampleClock = state.WriteSampleIndex;
            }

            _matrix.UpdateCalibrations(_participants, GetSharedCalibrationSampleEdgeLocked(state.WriteSampleIndex), _diagnostics);
            return RenderParticipantOutputLocked(state, pcm16Input.Length);
        }
    }

    /// <summary>
    /// Processes a simultaneous step of mono 16-bit PCM frames across multiple participants in lockstep.
    /// </summary>
    public Dictionary<string, short[]> ProcessSynchronizedStep(
        IReadOnlyDictionary<string, short[]> inputsByUser,
        long? wallClockTimestampMs = null)
    {
        lock (_syncLock)
        {
            long? expectedStartSample = ResolveExpectedStartSampleLocked(wallClockTimestampMs);
            int frameLen = 0;
            long stepStartClock = _globalSampleClock;
            long maxWriteClock = _globalSampleClock;

            foreach (var kvp in inputsByUser)
            {
                var pState = IngestParticipantPcmLocked(kvp.Key, kvp.Value, wallClockTimestampMs, expectedStartSample, stepStartClock);
                frameLen = Math.Max(frameLen, kvp.Value.Length);
                if (pState.WriteSampleIndex > maxWriteClock)
                {
                    maxWriteClock = pState.WriteSampleIndex;
                }
            }
            _globalSampleClock = maxWriteClock;

            if (frameLen == 0)
            {
                return new Dictionary<string, short[]>();
            }

            _matrix.UpdateCalibrations(_participants, GetSharedCalibrationSampleEdgeLocked(_globalSampleClock), _diagnostics);

            var outputs = new Dictionary<string, short[]>(inputsByUser.Count, StringComparer.Ordinal);
            foreach (var kvp in inputsByUser)
            {
                outputs[kvp.Key] = RenderParticipantOutputLocked(_participants[kvp.Key], kvp.Value.Length);
            }
            return outputs;
        }
    }

    private AcousticParticipantStream GetOrCreateParticipantLocked(string userId, long initialClock)
    {
        if (!_participants.TryGetValue(userId, out var state))
        {
            state = new AcousticParticipantStream(userId, initialClock);
            _participants[userId] = state;
        }
        return state;
    }

    private void RemoveParticipantLocked(string userId)
    {
        _participants.Remove(userId);
        _matrix.RemoveParticipantPairs(userId);
    }

    private long? ResolveExpectedStartSampleLocked(long? wallClockTimestampMs)
    {
        if (!wallClockTimestampMs.HasValue) return null;
        _roomEpochWallClockMs ??= wallClockTimestampMs.Value;
        long elapsedRoomMs = Math.Max(0L, wallClockTimestampMs.Value - _roomEpochWallClockMs.Value);
        return (elapsedRoomMs * SampleRate) / 1000L;
    }

    private AcousticParticipantStream IngestParticipantPcmLocked(
        string userId,
        ReadOnlySpan<short> pcm16,
        long? wallClockMs,
        long? expectedStartSample,
        long anchorClock)
    {
        var state = GetOrCreateParticipantLocked(userId, anchorClock);
        int driftCorrection = state.WritePcm16(pcm16, wallClockMs, expectedStartSample, anchorClock);
        if (driftCorrection != 0)
        {
            _matrix.AdjustForClockDrift(userId, driftCorrection);
        }
        return state;
    }

    private long GetSharedCalibrationSampleEdgeLocked(long fallbackEdge)
    {
        long minEdge = long.MaxValue;
        int activeCount = 0;
        foreach (var p in _participants.Values)
        {
            if (p.FramesWritten > 0 && fallbackEdge - p.WriteSampleIndex < SampleRate / 2)
            {
                if (p.WriteSampleIndex < minEdge) minEdge = p.WriteSampleIndex;
                activeCount++;
            }
        }
        return activeCount >= 2 && minEdge != long.MaxValue ? minEdge : fallbackEdge;
    }

    private int ResolveEffectiveLookaheadSamplesLocked(AcousticParticipantStream state)
    {
        int targetSamples = _matrix.GetTargetLookaheadSamples(state.UserId);
        if (state.AppliedLookaheadSamples != targetSamples)
        {
            bool isActivelySpeakingNearField = state.FramesWritten > 1 &&
                                               state.LastGateState == AcousticGateState.Open &&
                                               state.LastVocalRms >= VocalActivityFloorRms;
            if (_matrix.HasDirectNegativeLockTo(state.UserId) || !isActivelySpeakingNearField)
            {
                state.AppliedLookaheadSamples = targetSamples;
            }
        }
        return state.AppliedLookaheadSamples;
    }

    private short[] RenderParticipantOutputLocked(AcousticParticipantStream self, int numSamples)
    {
        int effectiveLookahead = ResolveEffectiveLookaheadSamplesLocked(self);
        long readStartSample = self.WriteSampleIndex - effectiveLookahead - numSamples;

        var output = new short[numSamples];
        if (readStartSample < 0) return output;

        if (_participants.Count < 2)
        {
            for (int i = 0; i < numSamples; i++)
            {
                output[i] = AcousticDspMath.FloatToPcm16(self.ReadRawSample(readStartSample + i));
            }
            return output;
        }

        int offset = 0;
        while (offset < numSamples)
        {
            int subLen = Math.Min(FrameSamples10Ms, numSamples - offset);
            ProcessSubBlockLocked(self, readStartSample + offset, subLen, output.AsSpan(offset, subLen));
            offset += subLen;
        }
        return output;
    }

    private void ProcessSubBlockLocked(
        AcousticParticipantStream self,
        long subStartSample,
        int subLen,
        Span<short> outPcm)
    {
        float selfVocalRms = self.ComputeVocalRms(subStartSample, subLen);
        float selfHighFreqRms = self.ComputeHighFreqRms(subStartSample, subLen);
        bool isPercussiveClick = selfHighFreqRms > 0.025f && selfHighFreqRms > selfVocalRms * 1.15f;
        self.LastVocalRms = selfVocalRms;
        if (!isPercussiveClick && selfVocalRms > self.PeakVocalRms)
        {
            self.PeakVocalRms = selfVocalRms;
        }
        self.TotalSubFramesProcessed++;

        float expectedBleedRms = 0f;
        float maxRoommateSourceRms = 0f;
        float maxNormalizedRoommateSourceRms = 0f;
        AcousticParticipantStream? primaryDominantRoommate = null;
        int primaryDelaySamples = 0;
        float primaryBleedGain = 0.18f;
        float primaryGateBleedGain = 0.18f;
        float primaryAgcGainScale = 1.0f;
        double primaryWaveformCorr = 0.0;

        float secondNormalizedRoommateSourceRms = 0f;
        AcousticParticipantStream? secondaryRoommate = null;
        int secondaryDelaySamples = 0;
        float secondaryBleedGain = 0.18f;
        double secondaryWaveformCorr = 0.0;

        bool allRoommatesCurrentlyGated = true;

        foreach (var other in _participants.Values)
        {
            if (ReferenceEquals(other, self)) continue;
            if (other.LastGateState != AcousticGateState.GatedBleed)
            {
                allRoommatesCurrentlyGated = false;
            }

            int delaySamples = 0;
            float bleedGain = 0.20f;
            float gateBleedGain = 0.20f;
            double waveformCorr = 0.0;

            if (_matrix.TryGetCalibratedPair(other.UserId, self.UserId, 0.20, out var cal))
            {
                delaySamples = cal.DelaySamples;
                bleedGain = cal.BleedGain;
                gateBleedGain = bleedGain > 0.45f
                    ? Math.Max(bleedGain, cal.PeakBleedGain)
                    : Math.Max(bleedGain, Math.Min(cal.PeakBleedGain, 0.30f));
                waveformCorr = cal.PeakWaveformCorrelation;
            }

            int forwardDelay = 0;
            float forwardBleed = 0.30f;
            float forwardPeakBleed = 0.30f;
            bool hasForwardDirect = false;
            bool hasForwardAlignment = _matrix.TryGetCalibratedPair(self.UserId, other.UserId, 0.20, out var fwdCal);
            if (hasForwardAlignment)
            {
                forwardDelay = fwdCal!.DelaySamples;
                forwardBleed = fwdCal.BleedGain;
                forwardPeakBleed = forwardBleed > 0.45f
                    ? Math.Max(forwardBleed, fwdCal.PeakBleedGain)
                    : Math.Max(forwardBleed, Math.Min(fwdCal.PeakBleedGain, 0.34f));
                hasForwardDirect = fwdCal.IsDirectLock(0.25);
            }

            bool hasIncomingDirectLock = cal?.IsDirectLock(0.35) == true;
            float otherAgcScale = (hasForwardDirect && forwardBleed > 0.45f)
                ? (forwardBleed / 0.35f)
                : ((hasIncomingDirectLock && bleedGain > 0.45f) ? (0.35f / bleedGain) : 1.0f);

            long otherAlignedStart = subStartSample - delaySamples;
            float directVocalRms = other.ComputeClickFilteredVocalRms(otherAlignedStart, subLen);
            float preOnsetRms = ComputePreOnsetRms(other, otherAlignedStart, subLen, filterClicks: true, out float rawPreOnset10Ms);
            float reverbRms = ComputeCausalReverbRms(other, otherAlignedStart, subLen, filterClicks: true, 0.72f, 0.52f, 0.42f, out float rawEarlyReverb, out float rawLateReverb);
            float rawSyllableHangover = other.ComputeEnvelopeRms(otherAlignedStart - FrameSamples20Ms * 3, FrameSamples20Ms * 3);
            float syllableHangoverRms = rawSyllableHangover * 0.55f;
            if (hasIncomingDirectLock && bleedGain > 0.45f)
            {
                syllableHangoverRms = Math.Max(
                    syllableHangoverRms,
                    other.ComputeEnvelopeRms(otherAlignedStart - FrameSamples20Ms * 6, FrameSamples20Ms * 6) * 0.45f);
            }

            float otherVocalRms = Math.Max(Math.Max(directVocalRms, preOnsetRms), Math.Max(syllableHangoverRms, reverbRms));
            if (other.LastGateState != AcousticGateState.GatedBleed)
            {
                float maxRawWindowRms = Math.Max(Math.Max(rawPreOnset10Ms, rawEarlyReverb), Math.Max(rawSyllableHangover, rawLateReverb * 0.85f));
                if (maxRawWindowRms >= VocalActivityFloorRms && otherVocalRms < VocalActivityFloorRms * 1.08f)
                {
                    otherVocalRms = Math.Min(maxRawWindowRms, VocalActivityFloorRms * 1.08f);
                }
            }

            if (!isPercussiveClick)
            {
                long selfAtOtherTime = otherAlignedStart - forwardDelay;
                float selfDirectForOther = self.ComputeVocalRms(selfAtOtherTime, subLen);
                float selfReverbForOther = ComputeCausalReverbRms(self, selfAtOtherTime, subLen, filterClicks: false, 0.74f, 0.55f, 0.45f, out _, out _);
                float selfBroadWindowForOther = hasForwardDirect
                    ? Math.Max(
                        Math.Max(
                            self.ComputeEnvelopeRms(selfAtOtherTime - FrameSamples20Ms * 3, FrameSamples20Ms * 3) * 0.75f,
                            self.ComputeEnvelopeRms(selfAtOtherTime - FrameSamples20Ms * 6, FrameSamples20Ms * 3) * 0.55f),
                        ComputePreOnsetRms(self, selfAtOtherTime, subLen, filterClicks: false, out _))
                    : self.ComputeEnvelopeRms(selfAtOtherTime - FrameSamples20Ms * 3, FrameSamples20Ms * 5) * 0.85f;

                float effectiveAgcForSelfCheck = (other.LastGateState == AcousticGateState.GatedBleed && !hasForwardDirect)
                    ? Math.Max(otherAgcScale, 0.32f)
                    : otherAgcScale;
                float normalizedOtherForComparison = otherVocalRms / effectiveAgcForSelfCheck;
                float currentSelfContribution = (!hasForwardAlignment && delaySamples == 0)
                    ? selfVocalRms
                    : (selfVocalRms > normalizedOtherForComparison * 1.15f ? selfVocalRms * 0.85f : 0f);
                if (self.ActiveSpeechHoldoverSubFrames > 0 && other.ActiveSpeechHoldoverSubFrames == 0)
                {
                    currentSelfContribution = Math.Max(currentSelfContribution, self.RecentSpeechRms * 0.70f);
                }

                float selfSourceForOther = Math.Max(currentSelfContribution, Math.Max(selfBroadWindowForOther, Math.Max(selfDirectForOther, selfReverbForOther)));
                float minForwardBleedFloor = hasForwardAlignment ? Math.Min(0.45f, forwardPeakBleed * 2.2f) : 0.45f;
                float expectedSelfBleedOnOther = selfSourceForOther * Math.Max(forwardPeakBleed * 1.80f, minForwardBleedFloor);
                float requiredDominanceRatio = (hasIncomingDirectLock && !hasForwardDirect) ? 1.55f : 1.22f;

                if (otherVocalRms <= expectedSelfBleedOnOther && selfSourceForOther > normalizedOtherForComparison * requiredDominanceRatio)
                {
                    continue;
                }

                float passiveHysteresisBleed = !hasForwardDirect
                    ? Math.Max(forwardPeakBleed, bleedGain <= 0.70f ? gateBleedGain : 0.12f)
                    : forwardPeakBleed;
                float passiveSelfRef = !hasForwardDirect
                    ? Math.Max(selfSourceForOther, selfVocalRms * Math.Clamp(otherAgcScale, 0.30f, 1.0f))
                    : selfSourceForOther;
                if (other.LastGateState == AcousticGateState.GatedBleed &&
                    self.LastGateState != AcousticGateState.GatedBleed &&
                    (self.ActiveSpeechHoldoverSubFrames > 0 || passiveSelfRef >= VocalActivityFloorRms * 0.75f) &&
                    directVocalRms <= Math.Max(passiveSelfRef * passiveHysteresisBleed * 1.80f, VocalActivityFloorRms * 1.25f))
                {
                    continue;
                }
            }

            if (otherVocalRms > VocalActivityFloorRms)
            {
                float minBleedFloor = otherAgcScale > 1.5f ? Math.Min(0.05f, 0.05f / otherAgcScale) : 0.05f;
                expectedBleedRms += otherVocalRms * Math.Max(gateBleedGain, minBleedFloor);

                float normalizedOtherSourceRms = otherVocalRms / otherAgcScale;
                if (normalizedOtherSourceRms > maxNormalizedRoommateSourceRms)
                {
                    secondNormalizedRoommateSourceRms = maxNormalizedRoommateSourceRms;
                    secondaryRoommate = primaryDominantRoommate;
                    secondaryDelaySamples = primaryDelaySamples;
                    secondaryBleedGain = primaryBleedGain;
                    secondaryWaveformCorr = primaryWaveformCorr;

                    maxNormalizedRoommateSourceRms = normalizedOtherSourceRms;
                    maxRoommateSourceRms = otherVocalRms;
                    primaryDominantRoommate = other;
                    primaryDelaySamples = delaySamples;
                    primaryBleedGain = bleedGain;
                    primaryGateBleedGain = gateBleedGain;
                    primaryAgcGainScale = otherAgcScale;
                    primaryWaveformCorr = waveformCorr;
                }
                else if (normalizedOtherSourceRms > secondNormalizedRoommateSourceRms)
                {
                    secondNormalizedRoommateSourceRms = normalizedOtherSourceRms;
                    secondaryRoommate = other;
                    secondaryDelaySamples = delaySamples;
                    secondaryBleedGain = bleedGain;
                    secondaryWaveformCorr = waveformCorr;
                }
            }
        }

        if (maxRoommateSourceRms <= VocalActivityFloorRms)
        {
            self.ConsecutiveNearFieldSubFrames = 0;
            self.TargetGain = 1.0f;
            self.LastGateState = AcousticGateState.Open;
            if (!isPercussiveClick && selfVocalRms > VocalActivityFloorRms)
            {
                self.SpeechSubFrames++;
                self.BucketOpenSpeechSubFrames++;
                if (selfVocalRms > self.BucketPeakOpenVocalRms) self.BucketPeakOpenVocalRms = selfVocalRms;
                if (selfVocalRms > VocalActivityFloorRms * 1.5f)
                {
                    self.ActiveSpeechHoldoverSubFrames = 12;
                    self.RecentSpeechRms = Math.Max(selfVocalRms, self.RecentSpeechRms * 0.85f);
                }
            }
            else if (self.ActiveSpeechHoldoverSubFrames > 0)
            {
                self.ActiveSpeechHoldoverSubFrames--;
                self.RecentSpeechRms *= 0.93f;
            }
            AcousticSpectralCanceller.ApplySmoothedGain(self, subStartSample, subLen, selfVocalRms, isPercussiveClick, outPcm);
            _diagnostics.AdvanceBucketIfMaster(self, _participants, subStartSample, selfVocalRms);
            return;
        }

        if (self.ActiveSpeechHoldoverSubFrames > 0)
        {
            self.ActiveSpeechHoldoverSubFrames--;
            self.RecentSpeechRms *= 0.93f;
        }

        if (isPercussiveClick && selfHighFreqRms > 0.025f)
        {
            self.RejectedClickTransients++;
            _diagnostics.RecordRejectedClick();
        }

        const float nearFieldVocalFloor = VocalActivityFloorRms * 1.5f;
        float sourceFloorRatio = 0.32f / Math.Max(1.0f, primaryAgcGainScale);
        float bleedThreshold = Math.Max(expectedBleedRms * 2.5f, maxRoommateSourceRms * Math.Max(primaryGateBleedGain * 2.2f, sourceFloorRatio));
        bool exceedsBleed = !isPercussiveClick && selfVocalRms > nearFieldVocalFloor && selfVocalRms > bleedThreshold;

        if (exceedsBleed &&
            primaryDominantRoommate != null &&
            selfVocalRms < maxNormalizedRoommateSourceRms * 0.72f &&
            AcousticLagCorrelator.IsCorrelatedRoommateBleedBurst(self, primaryDominantRoommate, subStartSample + subLen, primaryDelaySamples))
        {
            exceedsBleed = false;
        }

        self.ConsecutiveNearFieldSubFrames = exceedsBleed ? (self.ConsecutiveNearFieldSubFrames + 1) : 0;

        bool canBreakMutualGateDeadlock = allRoommatesCurrentlyGated && exceedsBleed && selfVocalRms >= maxNormalizedRoommateSourceRms * 1.25f;
        bool isClearlyLouderSpeaker = !isPercussiveClick &&
                                      (self.LastGateState != AcousticGateState.GatedBleed || canBreakMutualGateDeadlock) &&
                                      selfVocalRms > nearFieldVocalFloor &&
                                      selfVocalRms >= maxNormalizedRoommateSourceRms * 1.15f;

        if (self.ConsecutiveNearFieldSubFrames < DoubleTalkAttackSubFrames && !isClearlyLouderSpeaker)
        {
            self.TargetGain = 0.0f;
            self.LastGateState = AcousticGateState.GatedBleed;
            self.GatedBleedSubFrames++;
            self.BucketGatedSubFrames++;

            double rawInEnergy = self.ComputeRawMeanSquare(subStartSample, subLen);
            AcousticSpectralCanceller.ApplySmoothedGain(self, subStartSample, subLen, selfVocalRms, isPercussiveClick, outPcm);
            self.BleedInputEnergySum += rawInEnergy;
            self.BleedOutputEnergySum += AcousticDspMath.ComputePcmMeanSquare(outPcm);

            _diagnostics.AdvanceBucketIfMaster(self, _participants, subStartSample, selfVocalRms);
            return;
        }

        self.TargetGain = 1.0f;
        self.LastGateState = AcousticGateState.DoubleTalkSubtraction;
        self.DoubleTalkSubFrames++;
        self.SpeechSubFrames++;
        self.BucketDoubleTalkSubFrames++;
        if (selfVocalRms > self.BucketPeakOpenVocalRms) self.BucketPeakOpenVocalRms = selfVocalRms;
        if (!isPercussiveClick && allRoommatesCurrentlyGated && selfVocalRms > nearFieldVocalFloor && selfVocalRms >= maxNormalizedRoommateSourceRms * 1.15f)
        {
            self.ActiveSpeechHoldoverSubFrames = 12;
            self.RecentSpeechRms = Math.Max(selfVocalRms, self.RecentSpeechRms * 0.85f);
        }

        if (primaryDominantRoommate != null)
        {
            RoommateBleedReferencePair bleedRefs = default;
            bleedRefs[0] = new RoommateBleedReference(
                primaryDominantRoommate,
                subStartSample - primaryDelaySamples,
                primaryBleedGain,
                primaryWaveformCorr);
            int refCount = 1;
            if (secondaryRoommate != null)
            {
                bleedRefs[1] = new RoommateBleedReference(
                    secondaryRoommate,
                    subStartSample - secondaryDelaySamples,
                    secondaryBleedGain,
                    secondaryWaveformCorr);
                refCount = 2;
            }

            ReadOnlySpan<RoommateBleedReference> refSpan = bleedRefs;
            AcousticSpectralCanceller.ApplySpectralSubtractionCola(
                self, refSpan.Slice(0, refCount), subStartSample, subLen, selfVocalRms, isPercussiveClick, outPcm);
        }
        else
        {
            AcousticSpectralCanceller.ApplySmoothedGain(self, subStartSample, subLen, selfVocalRms, isPercussiveClick, outPcm);
        }

        _diagnostics.AdvanceBucketIfMaster(self, _participants, subStartSample, selfVocalRms);
    }

    private static float ComputePreOnsetRms(
        AcousticParticipantStream stream,
        long alignedStart,
        int subLen,
        bool filterClicks,
        out float raw10Ms)
    {
        raw10Ms = ReadSubBlockVocalRms(stream, alignedStart + FrameSamples10Ms, subLen, filterClicks);
        float raw20Ms = ReadSubBlockVocalRms(stream, alignedStart + FrameSamples20Ms, subLen, filterClicks);
        float raw30Ms = ReadSubBlockVocalRms(stream, alignedStart + FrameSamples10Ms * 3, subLen, filterClicks);
        return Math.Max(raw10Ms * 0.80f, Math.Max(raw20Ms * 0.62f, raw30Ms * 0.48f));
    }

    private static float ComputeCausalReverbRms(
        AcousticParticipantStream stream,
        long alignedStart,
        int subLen,
        bool filterClicks,
        float w10,
        float w20,
        float w30,
        out float raw10Ms,
        out float raw20Ms)
    {
        raw10Ms = ReadSubBlockVocalRms(stream, alignedStart - FrameSamples10Ms, subLen, filterClicks);
        raw20Ms = ReadSubBlockVocalRms(stream, alignedStart - FrameSamples20Ms, subLen, filterClicks);
        float raw30Ms = ReadSubBlockVocalRms(stream, alignedStart - FrameSamples10Ms * 3, subLen, filterClicks);
        return Math.Max(raw10Ms * w10, Math.Max(raw20Ms * w20, raw30Ms * w30));
    }

    private static float ReadSubBlockVocalRms(AcousticParticipantStream stream, long startSample, int subLen, bool filterClicks) =>
        filterClicks ? stream.ComputeClickFilteredVocalRms(startSample, subLen) : stream.ComputeVocalRms(startSample, subLen);
}
