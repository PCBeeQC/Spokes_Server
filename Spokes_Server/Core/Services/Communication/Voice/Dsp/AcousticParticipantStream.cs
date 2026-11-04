namespace Spokes_Server.Core.Services.Communication.Voice.Dsp;

using System;
using System.Runtime.CompilerServices;
using NWaves.Filters.Fda;
using NWaves.Transforms;
using static Spokes_Server.Core.Services.Communication.Voice.Dsp.AcousticRoomProcessor;

/// <summary>
/// Per-participant 2.0-second multi-band ring buffer, Opus DTX &amp; crystal clock-drift synchronizer,
/// vocal/click band splitter, and lazy NWaves 6-band Bark spectral cache.
/// </summary>
internal sealed class AcousticParticipantStream
{
    private const int BarkRingLength = RingBufferCapacity / BarkStepSamples;

    public string UserId { get; }
    public string FilterType { get; set; } = "webrtc";
    public double? EstimatedCaptureLatencyMs { get; set; }
    public double? ServerToClientDelayMs { get; set; }
    public long WriteSampleIndex { get; private set; }
    public long TrackEpochSample { get; private set; }
    public long? LastWallClockMs { get; private set; }
    public long FramesWritten { get; private set; }
    public const int StftBins = (GccPhatFftSize / 2) + 1;

    public float CurrentGain { get; set; } = 1.0f;
    public float TargetGain { get; set; } = 1.0f;
    public float LevelerNoiseFloorRms { get; set; } = 0.0012f;
    public float LevelerSpeechRmsEnv { get; set; } = PostLevelerTargetSpeechRms;
    public float TargetMakeupGain { get; set; } = 1.0f;
    public float CurrentMakeupGain { get; set; } = 1.0f;
    public float CurrentDoubleTalkMakeupScale { get; set; } = 1.0f;
    public bool HasSeededLevelerSpeechEnv { get; set; }
    public int ConsecutiveNearFieldSubFrames { get; set; }
    public int ActiveSpeechHoldoverSubFrames { get; set; }
    public float RecentSpeechRms { get; set; }
    public int AppliedLookaheadSamples { get; set; } = LookaheadSamples;

    // Temporal STFT Wiener smoothing & causal room-reverb power states (zero-allocation per-stream)
    public float[] SmoothedWienerGain { get; } = new float[StftBins];
    public float[] ReverbRefPowerSpectrum { get; } = new float[StftBins];
    public long LastReverbHopSample { get; set; } = -1L;

    // Per-bucket (250ms = 25 sub-frames) aggregation state
    public int BucketGatedSubFrames { get; set; }
    public int BucketDoubleTalkSubFrames { get; set; }
    public int BucketOpenSpeechSubFrames { get; set; }
    public float BucketPeakOpenVocalRms { get; set; }

    // Diagnostic Telemetry State
    public AcousticGateState LastGateState { get; set; } = AcousticGateState.Open;
    public float LastVocalRms { get; set; }
    public float PeakVocalRms { get; set; }
    public int TotalSubFramesProcessed { get; set; }
    public int SpeechSubFrames { get; set; }
    public int GatedBleedSubFrames { get; set; }
    public int DoubleTalkSubFrames { get; set; }
    public int RejectedClickTransients { get; set; }
    public double BleedInputEnergySum { get; set; }
    public double BleedOutputEnergySum { get; set; }
    public long TotalClockDriftCompensatedSamples { get; private set; }

    private double _smoothedClockDriftSamples;
    private int _sustainedDriftFrames;

    private readonly float[] _rawRing = new float[RingBufferCapacity];
    private readonly float[] _vocalRing = new float[RingBufferCapacity];
    private readonly float[] _highFreqRing = new float[RingBufferCapacity];
    private readonly float[] _envelopeRing = new float[RingBufferCapacity / EnvelopeStepSamples];
    private readonly bool[] _clickMaskRing = new bool[RingBufferCapacity / EnvelopeStepSamples];

    // NWaves 6-band Bark spectral cache (lazy-evaluated on calibration windows)
    private readonly RealFft _barkFft = new(BarkFftSize);
    private readonly float[] _barkFftInput = new float[BarkFftSize];
    private readonly float[] _barkPowerSpec = new float[(BarkFftSize / 2) + 1];
    private readonly float[] _barkTempBands = new float[BarkBandCount];
    private readonly float[] _barkRing = new float[BarkRingLength * BarkBandCount];
    private readonly long[] _barkCachedStepIdx = new long[BarkRingLength];

    // 1st-order high-pass (@200Hz) + low-pass (@3400Hz) filter states for vocal formant extraction
    private float _hpPrevIn;
    private float _hpPrevOut;
    private float _lpPrevOut;
    private float _fastVocalEnv;
    private float _fastHighFreqEnv;
    private float _envAccumSq;
    private int _envAccumCount;
    private bool _envStepHadClick;

    public AcousticParticipantStream(string userId, long initialSampleIndex)
    {
        UserId = userId;
        WriteSampleIndex = initialSampleIndex;
        Array.Fill(_barkCachedStepIdx, -1L);
        Array.Fill(SmoothedWienerGain, 1.0f);
    }

    public void OnTrackReplaced(long currentGlobalSampleClock)
    {
        FramesWritten = 0;
        LastWallClockMs = null;
        _smoothedClockDriftSamples = 0;
        _sustainedDriftFrames = 0;
        Array.Fill(_barkCachedStepIdx, -1L);
        Array.Fill(SmoothedWienerGain, 1.0f);
        Array.Clear(ReverbRefPowerSpectrum);
        LastReverbHopSample = -1L;
        CurrentDoubleTalkMakeupScale = 1.0f;
        AdvanceWithSilenceTo(currentGlobalSampleClock);
        TrackEpochSample = WriteSampleIndex;
    }

    public void RelaxSpectralCancellerState()
    {
        for (int k = 0; k < StftBins; k++)
        {
            SmoothedWienerGain[k] += 0.35f * (1.0f - SmoothedWienerGain[k]);
        }
    }

    public void ResetBucketCounters()
    {
        BucketGatedSubFrames = 0;
        BucketDoubleTalkSubFrames = 0;
        BucketOpenSpeechSubFrames = 0;
        BucketPeakOpenVocalRms = 0f;
    }

    public void ResetTelemetryCounters()
    {
        PeakVocalRms = 0f;
        TotalSubFramesProcessed = 0;
        SpeechSubFrames = 0;
        GatedBleedSubFrames = 0;
        DoubleTalkSubFrames = 0;
        RejectedClickTransients = 0;
        BleedInputEnergySum = 0.0;
        BleedOutputEnergySum = 0.0;
        TotalClockDriftCompensatedSamples = 0;
        ResetBucketCounters();
    }

    public int WritePcm16(
        ReadOnlySpan<short> pcm16,
        long? wallClockMs,
        long? expectedRoomSampleIndex,
        long currentGlobalSampleClock)
    {
        int clockDriftCorrectionApplied = 0;

        // Staggered Join / First Frame Anchor: lock late joiners onto the shared room timeline
        // without introducing a permanent offset from single-packet (+/-30ms) network arrival jitter.
        if (FramesWritten == 0)
        {
            long activeFrameStart = Math.Max(0L, currentGlobalSampleClock - pcm16.Length);
            long anchorTarget = (currentGlobalSampleClock > 0 &&
                                 expectedRoomSampleIndex.HasValue &&
                                 Math.Abs(expectedRoomSampleIndex.Value - activeFrameStart) <= FrameSamples10Ms * 3)
                ? activeFrameStart
                : (expectedRoomSampleIndex ?? currentGlobalSampleClock);

            TotalClockDriftCompensatedSamples += AdvanceWithSilenceTo(anchorTarget);
        }
        else if (wallClockMs.HasValue && LastWallClockMs.HasValue)
        {
            // Genuine Opus DTX Silence Gap Protection (>= 350ms comfort-noise interval per RFC 6716).
            long elapsedMs = wallClockMs.Value - LastWallClockMs.Value;
            long expectedMs = (pcm16.Length * 1000L) / SampleRate;
            long gapMs = elapsedMs - expectedMs;
            long requiredSampleDeficit = ((gapMs - 35) * SampleRate) / 1000L;
            bool isGenuineDtxGap = !expectedRoomSampleIndex.HasValue ||
                                   (expectedRoomSampleIndex.Value - WriteSampleIndex) >= requiredSampleDeficit;
            if (gapMs >= 350 && gapMs < 15000 && isGenuineDtxGap)
            {
                int silenceSamples = (int)((gapMs * SampleRate) / 1000L);
                WriteSilenceSamples(silenceSamples);
                TotalClockDriftCompensatedSamples += silenceSamples;
                _smoothedClockDriftSamples = 0;
                _sustainedDriftFrames = 0;
            }
            else if (expectedRoomSampleIndex.HasValue)
            {
                // Sustained unidirectional clock-drift tracker (>= 60 consecutive frames outside +/-15ms deadband, smoothed > +/-22ms).
                long instantaneousDrift = expectedRoomSampleIndex.Value - WriteSampleIndex;
                const int driftDeadbandSamples = (SampleRate * 15) / 1000;
                const int driftCorrectionThresholdSamples = (SampleRate * 22) / 1000;
                if (Math.Abs(instantaneousDrift) <= driftDeadbandSamples ||
                    (_smoothedClockDriftSamples != 0 && Math.Sign(instantaneousDrift) != Math.Sign(_smoothedClockDriftSamples)))
                {
                    _smoothedClockDriftSamples = 0;
                    _sustainedDriftFrames = 0;
                }
                else if (Math.Abs(instantaneousDrift) < SampleRate * 10)
                {
                    _sustainedDriftFrames++;
                    double clampedDrift = Math.Clamp((double)instantaneousDrift, -SampleRate * 0.25, SampleRate * 0.25);
                    _smoothedClockDriftSamples = _sustainedDriftFrames == 1
                        ? clampedDrift * 0.25
                        : (0.94 * _smoothedClockDriftSamples + 0.06 * clampedDrift);

                    if (_sustainedDriftFrames >= 60 && Math.Abs(_smoothedClockDriftSamples) > driftCorrectionThresholdSamples)
                    {
                        int correction = (int)Math.Round(_smoothedClockDriftSamples);
                        if (correction > 0)
                        {
                            WriteSilenceSamples(correction);
                            clockDriftCorrectionApplied = correction;
                        }
                        else if (correction < 0)
                        {
                            long prevIdx = WriteSampleIndex;
                            WriteSampleIndex = Math.Max(TrackEpochSample, WriteSampleIndex + correction);
                            clockDriftCorrectionApplied = (int)(WriteSampleIndex - prevIdx);
                        }
                        TotalClockDriftCompensatedSamples += clockDriftCorrectionApplied;
                        _smoothedClockDriftSamples = 0;
                        _sustainedDriftFrames = 0;
                    }
                }
            }
        }

        if (wallClockMs.HasValue)
        {
            LastWallClockMs = wallClockMs.Value;
        }

        for (int i = 0; i < pcm16.Length; i++)
        {
            PushSingleSample(pcm16[i] / 32768.0f);
        }

        FramesWritten++;
        return clockDriftCorrectionApplied;
    }

    private long AdvanceWithSilenceTo(long targetSampleIndex)
    {
        if (targetSampleIndex <= WriteSampleIndex) return 0;
        long delta = targetSampleIndex - WriteSampleIndex;
        if (delta <= RingBufferCapacity)
        {
            WriteSilenceSamples((int)delta);
        }
        else
        {
            WriteSampleIndex = targetSampleIndex;
        }
        return delta;
    }

    private void WriteSilenceSamples(int count)
    {
        int capped = Math.Min(count, RingBufferCapacity);
        for (int i = 0; i < capped; i++)
        {
            PushSingleSample(0f);
        }
        if (count > capped)
        {
            WriteSampleIndex += count - capped;
        }
    }

    private void PushSingleSample(float raw)
    {
        // High-pass at ~200 Hz + Low-pass at ~3400 Hz + High-frequency residual (> 3400 Hz)
        float hp = 0.974f * (_hpPrevOut + raw - _hpPrevIn);
        _hpPrevIn = raw;
        _hpPrevOut = hp;

        float vocal = _lpPrevOut + 0.36f * (hp - _lpPrevOut);
        _lpPrevOut = vocal;
        float highFreq = hp - vocal;

        int ringIdx = (int)(WriteSampleIndex % RingBufferCapacity);
        if (ringIdx < 0) ringIdx += RingBufferCapacity;

        // Fast (~0.5ms) vocal & high-freq envelopes zero out percussive keyboard clicks across the burst
        _fastVocalEnv = 0.94f * _fastVocalEnv + 0.06f * MathF.Abs(vocal);
        _fastHighFreqEnv = 0.94f * _fastHighFreqEnv + 0.06f * MathF.Abs(highFreq);
        bool isClickSample = _fastHighFreqEnv > _fastVocalEnv * 1.05f && _fastHighFreqEnv > 0.0005f;
        if (isClickSample || (_fastHighFreqEnv > _fastVocalEnv * 0.85f && _fastHighFreqEnv > 0.0020f))
        {
            _envStepHadClick = true;
        }
        float cleanVocal = isClickSample ? 0f : vocal;

        _rawRing[ringIdx] = raw;
        _vocalRing[ringIdx] = cleanVocal;
        _highFreqRing[ringIdx] = highFreq;

        _envAccumSq += cleanVocal * cleanVocal;
        if (++_envAccumCount >= EnvelopeStepSamples)
        {
            int envRingIdx = (int)((WriteSampleIndex / EnvelopeStepSamples) % _envelopeRing.Length);
            if (envRingIdx < 0) envRingIdx += _envelopeRing.Length;
            _envelopeRing[envRingIdx] = MathF.Sqrt(_envAccumSq / _envAccumCount);
            _clickMaskRing[envRingIdx] = _envStepHadClick;
            _envAccumSq = 0f;
            _envAccumCount = 0;
            _envStepHadClick = false;
        }

        WriteSampleIndex++;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool IsInRingBounds(long sampleIndex) =>
        sampleIndex >= 0 && sampleIndex < WriteSampleIndex && WriteSampleIndex - sampleIndex < RingBufferCapacity;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public float ReadRawSample(long sampleIndex) =>
        IsInRingBounds(sampleIndex) ? _rawRing[(int)(sampleIndex % RingBufferCapacity)] : 0f;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public float ReadVocalSample(long sampleIndex) =>
        IsInRingBounds(sampleIndex) ? _vocalRing[(int)(sampleIndex % RingBufferCapacity)] : 0f;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public float GetEnvelopeAt(long sampleIndex) =>
        IsInRingBounds(sampleIndex)
            ? _envelopeRing[(int)((sampleIndex / EnvelopeStepSamples) % _envelopeRing.Length)]
            : 0f;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool HasClickAt(long sampleIndex) =>
        IsInRingBounds(sampleIndex) &&
        _clickMaskRing[(int)((sampleIndex / EnvelopeStepSamples) % _clickMaskRing.Length)];

    public float ReadEnvelopeSteps(long startSample, Span<float> destination)
    {
        if (destination.IsEmpty) return 0f;
        double sum = 0.0;
        for (int k = 0; k < destination.Length; k++)
        {
            float e = GetEnvelopeAt(startSample + k * EnvelopeStepSamples);
            destination[k] = e;
            sum += e;
        }
        return (float)(sum / destination.Length);
    }

    public void CopyVocalRange(long startSample, Span<float> destination)
    {
        if (destination.IsEmpty) return;

        long validMin = Math.Max(0L, WriteSampleIndex - RingBufferCapacity + 1);
        long copyStart = Math.Max(startSample, validMin);
        long copyEnd = Math.Min(startSample + destination.Length, WriteSampleIndex);
        if (copyStart >= copyEnd)
        {
            destination.Clear();
            return;
        }

        int prefixZeros = (int)(copyStart - startSample);
        if (prefixZeros > 0) destination.Slice(0, prefixZeros).Clear();

        int validCount = (int)(copyEnd - copyStart);
        int suffixStart = prefixZeros + validCount;
        if (suffixStart < destination.Length) destination.Slice(suffixStart).Clear();

        Span<float> dstValid = destination.Slice(prefixZeros, validCount);
        int ringIdx = (int)(copyStart % RingBufferCapacity);
        int firstChunk = Math.Min(validCount, RingBufferCapacity - ringIdx);
        _vocalRing.AsSpan(ringIdx, firstChunk).CopyTo(dstValid.Slice(0, firstChunk));
        if (firstChunk < validCount)
        {
            _vocalRing.AsSpan(0, validCount - firstChunk).CopyTo(dstValid.Slice(firstChunk));
        }
    }

    public void ReadBarkBandsAt(long sampleIndex, Span<float> bandsOut)
    {
        if (!IsInRingBounds(sampleIndex))
        {
            bandsOut.Clear();
            return;
        }

        long barkStep = sampleIndex / BarkStepSamples;
        int slot = (int)(barkStep % BarkRingLength);

        if (_barkCachedStepIdx[slot] != barkStep)
        {
            long frameStart = Math.Max(0L, barkStep * BarkStepSamples - (BarkFftSize / 2));
            for (int i = 0; i < BarkFftSize; i++)
            {
                _barkFftInput[i] = ReadVocalSample(frameStart + i) * AcousticDspMath.HannWindow256[i];
            }

            _barkFft.PowerSpectrum(_barkFftInput, _barkPowerSpec, normalize: true);
            FilterBanks.ApplyAndLog10(AcousticDspMath.VocalBarkFilterBank, _barkPowerSpec, _barkTempBands, floor: 1e-9f);

            float meanLog = 0f;
            for (int b = 0; b < BarkBandCount; b++) meanLog += _barkTempBands[b];
            meanLog /= BarkBandCount;

            int baseIdx = slot * BarkBandCount;
            for (int b = 0; b < BarkBandCount; b++)
            {
                _barkRing[baseIdx + b] = _barkTempBands[b] - meanLog;
            }
            _barkCachedStepIdx[slot] = barkStep;
        }

        _barkRing.AsSpan(slot * BarkBandCount, BarkBandCount).CopyTo(bandsOut);
    }

    private double ComputeRingMeanSquare(float[] ring, long startSample, int count)
    {
        if (count <= 0) return 0.0;
        long validStart = Math.Max(startSample, Math.Max(0L, WriteSampleIndex - RingBufferCapacity + 1));
        long validEnd = Math.Min(startSample + count, WriteSampleIndex);
        if (validStart >= validEnd) return 0.0;

        int validCount = (int)(validEnd - validStart);
        int ringIdx = (int)(validStart % RingBufferCapacity);
        int firstChunk = Math.Min(validCount, RingBufferCapacity - ringIdx);
        var span0 = ring.AsSpan(ringIdx, firstChunk);
        double sumSq = AcousticDspMath.DotProductSimd(span0, span0);
        if (firstChunk < validCount)
        {
            var span1 = ring.AsSpan(0, validCount - firstChunk);
            sumSq += AcousticDspMath.DotProductSimd(span1, span1);
        }
        return sumSq / count;
    }

    public double ComputeRawMeanSquare(long startSample, int count) =>
        ComputeRingMeanSquare(_rawRing, startSample, count);

    public float ComputeVocalRms(long startSample, int count) =>
        (float)Math.Sqrt(ComputeRingMeanSquare(_vocalRing, startSample, count));

    public float ComputeHighFreqRms(long startSample, int count) =>
        (float)Math.Sqrt(ComputeRingMeanSquare(_highFreqRing, startSample, count));

    public float ComputeClickFilteredVocalRms(long startSample, int count)
    {
        float vocal = ComputeVocalRms(startSample, count);
        if (vocal <= 0f) return 0f;
        float hf = ComputeHighFreqRms(startSample, count);
        return (hf > 0.025f && hf > vocal * 1.05f) ? 0f : vocal;
    }

    public float ComputeEnvelopeRms(long startSample, int sampleCount)
    {
        int steps = sampleCount / EnvelopeStepSamples;
        if (steps <= 0) return ComputeVocalRms(startSample, sampleCount);

        double sumSq = 0.0;
        for (int k = 0; k < steps; k++)
        {
            float e = GetEnvelopeAt(startSample + k * EnvelopeStepSamples);
            sumSq += (double)e * e;
        }
        return (float)Math.Sqrt(sumSq / steps);
    }
}
