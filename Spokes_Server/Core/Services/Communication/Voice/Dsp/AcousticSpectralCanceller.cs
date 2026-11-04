namespace Spokes_Server.Core.Services.Communication.Voice.Dsp;

using System;
using System.Runtime.CompilerServices;
using static Spokes_Server.Core.Services.Communication.Voice.Dsp.AcousticRoomProcessor;

internal readonly record struct RoommateBleedReference(
    AcousticParticipantStream Stream,
    long AlignedStartSample,
    float BleedGain,
    double WaveformCorr);

[InlineArray(2)]
internal struct RoommateBleedReferencePair
{
    private RoommateBleedReference _element0;
}

/// <summary>
/// Real-time 50%-overlapped Hann (COLA) SIR-adaptive temporally-smoothed multi-reference Power Wiener bleed subtractor
/// powered by NWaves <see cref="NWaves.Transforms.RealFft"/>, causal room-reverb tail estimator, smoothed noise-gate envelope,
/// echo-restrained speech loudness normalizer (-20 dBFS), and -1.0 dBFS soft-knee peak limiter.
/// </summary>
internal static class AcousticSpectralCanceller
{
    private const float PostLevelerMaxMakeupGain = 5.6234133f;  // +15.0 dB max upward makeup gain
    private const float PostLevelerSpeechFloorRms = 0.0056234f; // -45.0 dBFS post-cancellation speech gate floor
    private const float SoftLimiterThreshold = 0.78f;           // ~ -2.15 dBFS soft-knee onset
    private const float SoftLimiterCeiling = 0.8912509f;        // -1.0 dBFS true peak ceiling
    private const float SoftLimiterKneeSpan = SoftLimiterCeiling - SoftLimiterThreshold;

    public static void ApplySmoothedGain(
        AcousticParticipantStream self,
        long subStartSample,
        int subLen,
        float selfVocalRms,
        bool isPercussiveClick,
        Span<short> outPcm)
    {
        self.RelaxSpectralCancellerState();

        Span<float> cleaned = subLen <= 1024 ? stackalloc float[subLen] : new float[subLen];
        Span<float> gateEnv = subLen <= 1024 ? stackalloc float[subLen] : new float[subLen];

        // Crisp ~1.8ms attack when gating bleed down; smooth ~12ms release when opening
        float alpha = self.TargetGain < self.CurrentGain ? 0.036f : 0.006f;
        for (int i = 0; i < subLen; i++)
        {
            self.CurrentGain += alpha * (self.TargetGain - self.CurrentGain);
            gateEnv[i] = self.CurrentGain;
            cleaned[i] = self.ReadRawSample(subStartSample + i) * self.CurrentGain;
        }

        ApplyPostCancellationLevelerAndQuantize(self, cleaned, gateEnv, selfVocalRms, rawPreCancelRms: 0f, isPercussiveClick, outPcm);
    }

    /// <summary>
    /// 50%-Overlapped 512-point Hann STFT (Constant Overlap-Add / COLA) multi-reference spectral bleed subtraction.
    /// Evaluates three 256-sample-hop Hann-windowed FFT frames covering [subStart - 256 .. subStart + 767]
    /// so that w[n] + w[n + 256] == 1.0 across every sample in [0 .. subLen - 1].
    /// </summary>
    public static void ApplySpectralSubtractionCola(
        AcousticParticipantStream self,
        ReadOnlySpan<RoommateBleedReference> references,
        long selfStartSample,
        int subLen,
        float selfVocalRms,
        bool isPercussiveClick,
        Span<short> outPcm)
    {
        if (references.IsEmpty)
        {
            ApplySmoothedGain(self, selfStartSample, subLen, selfVocalRms, isPercussiveClick, outPcm);
            return;
        }

        const int fftSize = 512;
        const int hopSize = 256;

        float rawPreCancelRms = (float)Math.Sqrt(self.ComputeRawMeanSquare(selfStartSample, subLen));

        Span<float> hop0 = stackalloc float[fftSize];
        Span<float> hop1 = stackalloc float[fftSize];
        Span<float> hop2 = stackalloc float[fftSize];
        Span<float> cleaned = subLen <= 1024 ? stackalloc float[subLen] : new float[subLen];
        Span<float> gateEnv = subLen <= 1024 ? stackalloc float[subLen] : new float[subLen];

        ProcessSingleStftHop(self, references, selfStartSample, hopOffset: -hopSize, hop0);
        ProcessSingleStftHop(self, references, selfStartSample, hopOffset: 0, hop1);
        ProcessSingleStftHop(self, references, selfStartSample, hopOffset: hopSize, hop2);

        for (int i = 0; i < subLen; i++)
        {
            self.CurrentGain += 0.015f * (1.0f - self.CurrentGain);
            float colaSample = i < hopSize
                ? (hop0[i + hopSize] + hop1[i])
                : (hop1[i] + hop2[i - hopSize]);

            gateEnv[i] = self.CurrentGain;
            cleaned[i] = colaSample * self.CurrentGain;
        }

        ApplyPostCancellationLevelerAndQuantize(self, cleaned, gateEnv, selfVocalRms, rawPreCancelRms, isPercussiveClick, outPcm);
    }

    private static void ProcessSingleStftHop(
        AcousticParticipantStream self,
        ReadOnlySpan<RoommateBleedReference> references,
        long subBlockSelfStart,
        int hopOffset,
        Span<float> timeDomainOut)
    {
        const int fftSize = 512;
        const int bins = (fftSize / 2) + 1;

        long hopSelfStart = subBlockSelfStart + hopOffset;

        Span<float> selfIn = stackalloc float[fftSize];
        Span<float> refIn = stackalloc float[fftSize];
        Span<float> selfRe = stackalloc float[bins];
        Span<float> selfIm = stackalloc float[bins];
        Span<float> refRe = stackalloc float[bins];
        Span<float> refIm = stackalloc float[bins];
        Span<double> directBleedPower = stackalloc double[bins];
        Span<float> rawGains = stackalloc float[bins];

        directBleedPower.Clear();

        Span<float> directSubScales = stackalloc float[references.Length];
        for (int r = 0; r < references.Length; r++)
        {
            float coherentWeight = references[r].WaveformCorr >= 0.35
                ? Math.Clamp((float)references[r].WaveformCorr * 0.85f, 0f, 0.80f)
                : 0f;
            directSubScales[r] = Math.Min(references[r].BleedGain, 0.90f) * coherentWeight;
        }

        var window = AcousticDspMath.HannWindow512;
        for (int n = 0; n < fftSize; n++)
        {
            float cancelled = self.ReadRawSample(hopSelfStart + n);
            for (int r = 0; r < references.Length; r++)
            {
                float subScale = directSubScales[r];
                if (subScale > 0f)
                {
                    cancelled -= references[r].Stream.ReadRawSample(references[r].AlignedStartSample + hopOffset + n) * subScale;
                }
            }
            selfIn[n] = Math.Clamp(cancelled, -1.0f, 1.0f) * window[n];
        }

        var fft = AcousticDspMath.Fft512;
        fft.Direct(selfIn, selfRe, selfIm);

        for (int r = 0; r < references.Length; r++)
        {
            long hopRefStart = references[r].AlignedStartSample + hopOffset;
            for (int n = 0; n < fftSize; n++)
            {
                refIn[n] = references[r].Stream.ReadRawSample(hopRefStart + n) * window[n];
            }
            fft.Direct(refIn, refRe, refIm);

            float bleedGain = references[r].BleedGain;
            float residualBleedScale = Math.Max(0f, bleedGain - directSubScales[r] * 0.65f);
            float maxResidualSq = bleedGain > 0.85f ? (MaxAllowedAgcBleedGain * MaxAllowedAgcBleedGain) : 0.85f;
            double residualBleedSq = Math.Clamp(residualBleedScale * residualBleedScale, 0.001f, maxResidualSq);

            for (int k = 0; k < bins; k++)
            {
                double rRe = refRe[k];
                double rIm = refIm[k];
                directBleedPower[k] += ((rRe * rRe) + (rIm * rIm)) * residualBleedSq;
            }
        }

        bool advanceState = false;
        float reverbDecay = 1.0f;
        if (self.LastReverbHopSample < 0 || hopSelfStart - self.LastReverbHopSample > (SampleRate * 150) / 1000)
        {
            Array.Clear(self.ReverbRefPowerSpectrum);
            reverbDecay = 0f;
            advanceState = true;
        }
        else if (hopSelfStart > self.LastReverbHopSample)
        {
            reverbDecay = MathF.Exp(-(hopSelfStart - self.LastReverbHopSample) / (0.020f * SampleRate));
            advanceState = true;
        }

        var reverbSpec = self.ReverbRefPowerSpectrum;
        var smoothGains = self.SmoothedWienerGain;

        for (int k = 0; k < bins; k++)
        {
            double reverbTailPower = reverbSpec[k] * reverbDecay;
            double baseBleedPower = directBleedPower[k] + (reverbTailPower * 0.22);
            if (advanceState)
            {
                reverbSpec[k] = MathF.Max((float)reverbTailPower, (float)directBleedPower[k]);
            }

            double sRe = selfRe[k];
            double sIm = selfIm[k];
            double powerSelf = (sRe * sRe) + (sIm * sIm);
            float targetBinGain;
            if (powerSelf > 1e-12 && baseBleedPower > 1e-14)
            {
                // Posterior SIR-adaptive curve:
                // - High SIR (>= 5.0, active local speech harmonics): 1.15x over-sub & 0.17 (-15.4 dB) floor preserves natural timbre
                // - Low SIR (<= 1.6, bleed-dominated bins / inter-syllable gaps): 1.65x over-sub & 0.035 (-29.1 dB) floor silences quiet echo
                double sir = powerSelf / baseBleedPower;
                double speechPresence = Math.Clamp((sir - 1.6) / 3.4, 0.0, 1.0);
                double overSubFactor = 1.65 - (0.50 * speechPresence);
                double floorGain = 0.035 + (0.135 * speechPresence);
                double floorSq = floorGain * floorGain;

                double effectiveBleedPower = baseBleedPower * overSubFactor;
                double wienerPowerRatio = Math.Max((powerSelf - effectiveBleedPower) / powerSelf, floorSq);
                targetBinGain = (float)Math.Sqrt(wienerPowerRatio);
            }
            else
            {
                targetBinGain = 1.0f;
            }

            float prevSmooth = smoothGains[k];
            if (advanceState)
            {
                float tempAlpha = targetBinGain < prevSmooth ? 0.65f : 0.28f;
                float nextSmooth = prevSmooth + tempAlpha * (targetBinGain - prevSmooth);
                smoothGains[k] = nextSmooth;
                rawGains[k] = nextSmooth;
            }
            else
            {
                rawGains[k] = 0.50f * prevSmooth + 0.50f * targetBinGain;
            }
        }

        if (advanceState)
        {
            self.LastReverbHopSample = hopSelfStart;
        }

        // 3-bin frequency smoothing (reflecting conjugate-symmetric neighbors at DC k=0 and Nyquist k=256)
        for (int k = 0; k < bins; k++)
        {
            float gPrev = k == 0 ? rawGains[1] : rawGains[k - 1];
            float gCurr = rawGains[k];
            float gNext = k == bins - 1 ? rawGains[bins - 2] : rawGains[k + 1];
            float smoothedGain = 0.25f * gPrev + 0.50f * gCurr + 0.25f * gNext;
            selfRe[k] *= smoothedGain;
            selfIm[k] *= smoothedGain;
        }

        fft.InverseNorm(selfRe, selfIm, timeDomainOut);
    }

    private static void ApplyPostCancellationLevelerAndQuantize(
        AcousticParticipantStream self,
        ReadOnlySpan<float> cleanedSamples,
        ReadOnlySpan<float> gateEnvelope,
        float selfVocalRms,
        float rawPreCancelRms,
        bool isPercussiveClick,
        Span<short> outPcm)
    {
        int len = cleanedSamples.Length;
        if (len == 0) return;

        float targetDtMakeupScale = self.LastGateState == AcousticGateState.GatedBleed ? 0.0f : 1.0f;

        if (self.LastGateState != AcousticGateState.GatedBleed)
        {
            float cleanedRms = (float)Math.Sqrt(AcousticDspMath.ComputeMeanSquareSimd(cleanedSamples));
            float postWienerRetention = (self.LastGateState == AcousticGateState.DoubleTalkSubtraction && rawPreCancelRms > 1e-5f)
                ? Math.Clamp(cleanedRms / rawPreCancelRms, 0f, 1f)
                : 1.0f;

            if (self.LastGateState == AcousticGateState.DoubleTalkSubtraction)
            {
                targetDtMakeupScale = Math.Clamp((postWienerRetention - 0.28f) / 0.42f, 0.0f, 1.0f);
            }

            if (!isPercussiveClick && cleanedRms > 1e-6f)
            {
                if (cleanedRms < self.LevelerNoiseFloorRms)
                {
                    self.LevelerNoiseFloorRms = 0.88f * self.LevelerNoiseFloorRms + 0.12f * cleanedRms;
                }
                else if (selfVocalRms < VocalActivityFloorRms * 0.85f && cleanedRms < self.LevelerSpeechRmsEnv * 0.35f)
                {
                    self.LevelerNoiseFloorRms = Math.Min(0.015f, 0.998f * self.LevelerNoiseFloorRms + 0.002f * cleanedRms);
                }
            }

            float dynamicSpeechGate = Math.Max(PostLevelerSpeechFloorRms, self.LevelerNoiseFloorRms * 2.8f);
            bool isActiveNearFieldSpeech = !isPercussiveClick &&
                                           selfVocalRms >= VocalActivityFloorRms &&
                                           cleanedRms >= dynamicSpeechGate &&
                                           (self.LastGateState != AcousticGateState.DoubleTalkSubtraction || postWienerRetention >= 0.50f);

            if (isActiveNearFieldSpeech)
            {
                if (!self.HasSeededLevelerSpeechEnv)
                {
                    self.LevelerSpeechRmsEnv = Math.Clamp(cleanedRms, 0.006f, 0.35f);
                    self.HasSeededLevelerSpeechEnv = true;
                }
                else
                {
                    float baseAlpha = cleanedRms > self.LevelerSpeechRmsEnv ? 0.14f : 0.045f;
                    if (self.LastGateState == AcousticGateState.DoubleTalkSubtraction)
                    {
                        baseAlpha *= 0.25f;
                    }
                    self.LevelerSpeechRmsEnv += baseAlpha * (cleanedRms - self.LevelerSpeechRmsEnv);
                }

                self.TargetMakeupGain = self.LevelerSpeechRmsEnv < PostLevelerTargetSpeechRms
                    ? Math.Clamp(PostLevelerTargetSpeechRms / Math.Max(self.LevelerSpeechRmsEnv, 1e-4f), 1.0f, PostLevelerMaxMakeupGain)
                    : 1.0f;
            }
        }

        float makeupAlpha = self.TargetMakeupGain < self.CurrentMakeupGain ? 0.0014f : 0.00014f;
        float dtScaleAlpha = targetDtMakeupScale < self.CurrentDoubleTalkMakeupScale ? 0.018f : 0.006f;
        bool isClosingGate = self.TargetGain <= 0.01f;

        for (int i = 0; i < len; i++)
        {
            if (self.LastGateState != AcousticGateState.GatedBleed)
            {
                self.CurrentMakeupGain += makeupAlpha * (self.TargetMakeupGain - self.CurrentMakeupGain);
            }
            self.CurrentDoubleTalkMakeupScale += dtScaleAlpha * (targetDtMakeupScale - self.CurrentDoubleTalkMakeupScale);

            float g = gateEnvelope[i];
            float gateSq = g * g;
            float gateWeight = isClosingGate ? (gateSq * gateSq) : gateSq;
            float appliedMakeup = 1.0f + (self.CurrentMakeupGain - 1.0f) * gateWeight * self.CurrentDoubleTalkMakeupScale;
            outPcm[i] = AcousticDspMath.FloatToPcm16(ApplySoftKneeLimiterMinus1DbFs(cleanedSamples[i] * appliedMakeup));
        }
    }

    private static float ApplySoftKneeLimiterMinus1DbFs(float x)
    {
        if (!float.IsFinite(x)) return 0f;
        float ax = MathF.Abs(x);
        if (ax <= SoftLimiterThreshold) return x;
        float excess = (ax - SoftLimiterThreshold) / SoftLimiterKneeSpan;
        float compressed = SoftLimiterThreshold + SoftLimiterKneeSpan * (excess / (1.0f + excess));
        return x < 0f ? -compressed : compressed;
    }
}
