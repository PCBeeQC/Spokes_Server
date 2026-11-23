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
/// Decoupled two-stage log-domain (dB) speech loudness normalizer + independent fast peak/shout guard
/// for server-side Physical Room streams (matching Direct LAN and client AudioWorklet levelers).
/// </summary>
internal sealed class AcousticSpeechLeveler
{
    private const float TargetSpeechDb = -16.5f;
    private const float MaxBoostDb = 15.0f;
    private const float MinAttenDb = -12.0f;
    private const float SpeechGateFloorDb = -45.0f;
    private const float MaxNoiseFloorDb = -44.0f;
    private const float NoiseMarginDb = 8.0f;
    private const float MaxCrestFactor = 6.5f;
    private const float EnvRiseTauSec = 0.55f;
    private const float EnvFallTauSec = 0.45f;
    private const float MaxRiseStepDb = 0.55f;
    private const float RelativeGateDb = -10.0f;
    private const int SoftUnlockWindows = 12;
    private const int ConfirmWindows = 3;
    private const float IdleRelaxDelaySec = 1.5f;
    private const float IdleRelaxTauSec = 2.0f;
    private const float BaselineUpTauSec = 8.0f;
    private const float BaselineDownTauSec = 16.0f;
    private const float LimiterThreshold = 0.78f;
    private const int WindowSamples = 960; // 20 ms @ 48 kHz
    private const int NfRingSize = 75;     // 1.5 s rolling minimum-statistics window

    private static readonly float MinMakeupGain = DbToLinear(MinAttenDb);
    private static readonly float MaxMakeupGain = DbToLinear(MaxBoostDb);
    private static readonly float ShoutRmsCeiling = DbToLinear(TargetSpeechDb + 2.0f);
    private static readonly float MakeupUpAlpha = 1.0f - MathF.Exp(-1.0f / (SampleRate * 0.120f));
    private static readonly float MakeupDownAlpha = 1.0f - MathF.Exp(-1.0f / (SampleRate * 0.035f));
    private static readonly float DuckAttackAlpha = 1.0f - MathF.Exp(-1.0f / (SampleRate * 0.002f));
    private static readonly float DuckReleaseAlpha = 1.0f - MathF.Exp(-1.0f / (SampleRate * 0.120f));
    private static readonly float FastEnvAlpha = 1.0f - MathF.Exp(-1.0f / (SampleRate * 0.010f));
    private static readonly int FastDuckHoldSamples = (SampleRate * 20) / 1000;

    private readonly float[] _nfRing = new float[NfRingSize];
    private readonly float[] _seedBuf = new float[ConfirmWindows];
    private int _nfIdx;
    private float _noiseFloorDb = -60.0f;

    private double _winSumSq;
    private float _winPeak;
    private int _winSamples;
    private int _winConfirmedSamples;
    private bool _winDoubleTalk;

    private float _speechLevelDb = TargetSpeechDb;
    private float _baselineGainDb;
    private bool _hasSeeded;
    private int _confirmCount;
    private int _seedCount;
    private int _softBelowCount;
    private float _silenceSec;

    private float _fastPowerEnv;
    private int _fastDuckHoldRemaining;
    private float _heldDuckTarget = 1.0f;

    public float TargetMakeupGain { get; private set; } = 1.0f;
    public float CurrentMakeupGain { get; private set; } = 1.0f;
    public float CurrentFastDuckGain { get; private set; } = 1.0f;
    public float EffectiveGain => CurrentMakeupGain * CurrentFastDuckGain;
    public float NoiseFloorRms => DbToLinear(_noiseFloorDb);

    public bool IsIdleSettled =>
        MathF.Abs(CurrentMakeupGain - TargetMakeupGain) < 1e-4f &&
        MathF.Abs(CurrentFastDuckGain - 1.0f) < 1e-4f &&
        (!_hasSeeded || (20.0f * MathF.Log10(MathF.Max(TargetMakeupGain, 1e-4f))) >= Math.Clamp(_baselineGainDb, MinAttenDb, MaxBoostDb) - 0.05f);

    public AcousticSpeechLeveler()
    {
        Array.Fill(_nfRing, -60.0f);
    }

    public void Reset()
    {
        Array.Fill(_nfRing, -60.0f);
        _nfIdx = 0;
        _noiseFloorDb = -60.0f;
        _winSumSq = 0.0;
        _winPeak = 0.0f;
        _winSamples = 0;
        _winConfirmedSamples = 0;
        _winDoubleTalk = false;
        _speechLevelDb = TargetSpeechDb;
        _baselineGainDb = 0.0f;
        _hasSeeded = false;
        _confirmCount = 0;
        _seedCount = 0;
        _softBelowCount = 0;
        _silenceSec = 0.0f;
        _fastPowerEnv = 0.0f;
        _fastDuckHoldRemaining = 0;
        _heldDuckTarget = 1.0f;
        TargetMakeupGain = 1.0f;
        CurrentMakeupGain = 1.0f;
        CurrentFastDuckGain = 1.0f;
    }

    public void AdvancePureSilenceSubBlock(int sampleCount)
    {
        if (sampleCount <= 0) return;
        float dt = sampleCount / (float)SampleRate;
        _winSumSq = 0.0;
        _winPeak = 0.0f;
        _winSamples = 0;
        _winConfirmedSamples = 0;
        _winDoubleTalk = false;
        _confirmCount = 0;
        _softBelowCount = 0;
        if (!_hasSeeded) _seedCount = 0;

        AdvanceIdleSilence(dt);

        float makeupAlpha = TargetMakeupGain < CurrentMakeupGain ? MakeupDownAlpha : MakeupUpAlpha;
        float stepMakeup = 1.0f - MathF.Pow(1.0f - makeupAlpha, sampleCount);
        CurrentMakeupGain += stepMakeup * (TargetMakeupGain - CurrentMakeupGain);

        _fastPowerEnv *= MathF.Pow(1.0f - FastEnvAlpha, sampleCount);
        _fastDuckHoldRemaining = 0;
        _heldDuckTarget = 1.0f;
        float stepDuck = 1.0f - MathF.Pow(1.0f - DuckReleaseAlpha, sampleCount);
        CurrentFastDuckGain += stepDuck * (1.0f - CurrentFastDuckGain);
        if (MathF.Abs(CurrentFastDuckGain - 1.0f) < 1e-4f)
        {
            CurrentFastDuckGain = 1.0f;
        }
    }

    public float ComputeSubBlockDesiredDuck(
        float cleanedMeanSq,
        float cleanedPeak,
        int subLen,
        bool isGatedBleed,
        bool isPercussiveClick,
        bool allowSpeechUpdate,
        bool isDoubleTalk,
        out bool seededThisBlock)
    {
        seededThisBlock = false;

        if (isGatedBleed)
        {
            _winSumSq = 0.0;
            _winPeak = 0.0f;
            _winSamples = 0;
            _winConfirmedSamples = 0;
            _winDoubleTalk = false;
            _confirmCount = 0;
            _softBelowCount = 0;
            if (!_hasSeeded) _seedCount = 0;
            AdvanceIdleSilence(subLen / (float)SampleRate);
            _fastPowerEnv *= MathF.Pow(1.0f - FastEnvAlpha, subLen);
            _fastDuckHoldRemaining = 0;
            _heldDuckTarget = 1.0f;
            return 1.0f;
        }

        _winSumSq += cleanedMeanSq * subLen;
        if (cleanedPeak > _winPeak) _winPeak = cleanedPeak;
        if (allowSpeechUpdate) _winConfirmedSamples += subLen;
        if (isDoubleTalk) _winDoubleTalk = true;
        _winSamples += subLen;

        if (_winSamples >= WindowSamples)
        {
            float meanSq = (float)(_winSumSq / _winSamples);
            bool winConfirmed = _winConfirmedSamples * 2 >= _winSamples;
            seededThisBlock = EvaluateWindow(meanSq, _winPeak, winConfirmed, _winDoubleTalk);
            _winSumSq = 0.0;
            _winPeak = 0.0f;
            _winSamples = 0;
            _winConfirmedSamples = 0;
            _winDoubleTalk = false;
        }

        float blockFastAlpha = 1.0f - MathF.Pow(1.0f - FastEnvAlpha, subLen);
        _fastPowerEnv += blockFastAlpha * (cleanedMeanSq - _fastPowerEnv);
        float fastRms = MathF.Sqrt(MathF.Max(_fastPowerEnv, 0.0f));

        float instDuck = 1.0f;
        if (!isPercussiveClick)
        {
            float maxSafeTotal = MaxMakeupGain;
            if (cleanedPeak * CurrentMakeupGain > LimiterThreshold)
            {
                maxSafeTotal = MathF.Min(
                    maxSafeTotal,
                    Math.Clamp(LimiterThreshold / (cleanedPeak + 1e-6f), MinMakeupGain, MaxMakeupGain));
            }
            if (fastRms * CurrentMakeupGain > ShoutRmsCeiling)
            {
                maxSafeTotal = MathF.Min(
                    maxSafeTotal,
                    Math.Clamp(ShoutRmsCeiling / (fastRms + 1e-6f), MinMakeupGain, MaxMakeupGain));
            }
            if (maxSafeTotal < CurrentMakeupGain)
            {
                instDuck = Math.Clamp(maxSafeTotal / MathF.Max(CurrentMakeupGain, 1e-4f), 0.05f, 1.0f);
            }
        }

        float desiredDuck = 1.0f;
        if (instDuck < 0.999f)
        {
            if (instDuck <= _heldDuckTarget || _fastDuckHoldRemaining <= 0)
            {
                _heldDuckTarget = instDuck;
            }
            _fastDuckHoldRemaining = FastDuckHoldSamples;
            desiredDuck = MathF.Min(instDuck, _heldDuckTarget);
        }
        else if (_fastDuckHoldRemaining > 0)
        {
            _fastDuckHoldRemaining = Math.Max(0, _fastDuckHoldRemaining - subLen);
            desiredDuck = _heldDuckTarget;
        }
        else
        {
            _heldDuckTarget = 1.0f;
        }

        return desiredDuck;
    }

    public void StepSample(bool isGatedBleed, float desiredDuck)
    {
        if (!isGatedBleed)
        {
            float makeupAlpha = TargetMakeupGain < CurrentMakeupGain ? MakeupDownAlpha : MakeupUpAlpha;
            CurrentMakeupGain += makeupAlpha * (TargetMakeupGain - CurrentMakeupGain);
        }
        float duckAlpha = desiredDuck < CurrentFastDuckGain ? DuckAttackAlpha : DuckReleaseAlpha;
        CurrentFastDuckGain += duckAlpha * (desiredDuck - CurrentFastDuckGain);
    }

    private bool EvaluateWindow(float meanSq, float peak, bool externalSpeechConfirmed, bool isDoubleTalk)
    {
        float dt = WindowSamples / (float)SampleRate;
        float rms = MathF.Sqrt(MathF.Max(meanSq, 0.0f));
        float db = 10.0f * MathF.Log10(MathF.Max(meanSq, 1e-12f));
        float crest = peak / (rms + 1e-7f);

        if (meanSq > 1e-10f)
        {
            _nfRing[_nfIdx] = Math.Clamp(db, -65.0f, MaxNoiseFloorDb);
            _nfIdx = (_nfIdx + 1) % NfRingSize;
            float minVal = MaxNoiseFloorDb;
            for (int i = 0; i < NfRingSize; i++)
            {
                if (_nfRing[i] < minVal) minVal = _nfRing[i];
            }
            _noiseFloorDb = 0.90f * _noiseFloorDb + 0.10f * minVal;
        }

        float gateDb = MathF.Max(SpeechGateFloorDb, _noiseFloorDb + NoiseMarginDb);
        bool isImpulsive = crest > MaxCrestFactor;
        bool passesGate = externalSpeechConfirmed && db >= gateDb && !isImpulsive;

        if (passesGate)
        {
            _silenceSec = 0.0f;
            _confirmCount++;

            if (!_hasSeeded)
            {
                _seedBuf[_seedCount++] = Math.Clamp(db, -45.0f, -8.0f);
                if (_seedCount >= ConfirmWindows)
                {
                    Array.Sort(_seedBuf, 0, _seedCount);
                    _speechLevelDb = _seedBuf[_seedCount / 2];
                    _hasSeeded = true;
                    _seedCount = 0;

                    float desiredDb = Math.Clamp(TargetSpeechDb - _speechLevelDb, MinAttenDb, MaxBoostDb);
                    _baselineGainDb = desiredDb;
                    TargetMakeupGain = DbToLinear(desiredDb);
                    if (!isDoubleTalk && TargetMakeupGain > 1.0f)
                    {
                        CurrentMakeupGain = MathF.Sqrt(TargetMakeupGain);
                    }
                    return true;
                }
                return false;
            }

            if (_confirmCount >= ConfirmWindows)
            {
                float dtScale = isDoubleTalk ? 0.25f : 1.0f;
                float diff = db - _speechLevelDb;
                if (diff >= 0.0f)
                {
                    _softBelowCount = 0;
                    float riseAlpha = (1.0f - MathF.Exp(-dt / EnvRiseTauSec)) * dtScale;
                    float step = MathF.Min(MaxRiseStepDb * dtScale, riseAlpha * diff);
                    _speechLevelDb += step;
                }
                else if (diff >= RelativeGateDb)
                {
                    _softBelowCount = 0;
                    float fallAlpha = (1.0f - MathF.Exp(-dt / EnvFallTauSec)) * dtScale;
                    _speechLevelDb += fallAlpha * diff;
                }
                else
                {
                    _softBelowCount++;
                    if (_softBelowCount >= SoftUnlockWindows)
                    {
                        float unlockAlpha = (1.0f - MathF.Exp(-dt / 0.30f)) * dtScale;
                        _speechLevelDb += unlockAlpha * diff;
                    }
                }

                _speechLevelDb = Math.Clamp(_speechLevelDb, -45.0f, -4.0f);
                float desiredDb = Math.Clamp(TargetSpeechDb - _speechLevelDb, MinAttenDb, MaxBoostDb);
                TargetMakeupGain = DbToLinear(desiredDb);

                float baseTau = desiredDb < _baselineGainDb ? BaselineDownTauSec : BaselineUpTauSec;
                float baseAlpha = (1.0f - MathF.Exp(-dt / baseTau)) * dtScale;
                _baselineGainDb += baseAlpha * (desiredDb - _baselineGainDb);
            }
            return false;
        }

        _confirmCount = 0;
        _softBelowCount = 0;
        if (!_hasSeeded) _seedCount = 0;
        if (!externalSpeechConfirmed || db < gateDb)
        {
            AdvanceIdleSilence(dt);
        }
        return false;
    }

    private void AdvanceIdleSilence(float dt)
    {
        _silenceSec += dt;
        if (!_hasSeeded || _silenceSec <= IdleRelaxDelaySec) return;

        float curTargetDb = 20.0f * MathF.Log10(MathF.Max(TargetMakeupGain, 1e-4f));
        float relaxTargetDb = Math.Clamp(_baselineGainDb, MinAttenDb, MaxBoostDb);
        if (curTargetDb < relaxTargetDb - 0.05f)
        {
            float relaxAlpha = 1.0f - MathF.Exp(-dt / IdleRelaxTauSec);
            float nextDb = curTargetDb + relaxAlpha * (relaxTargetDb - curTargetDb);
            TargetMakeupGain = DbToLinear(nextDb);
            _speechLevelDb = Math.Clamp(TargetSpeechDb - nextDb, -45.0f, -4.0f);
        }
    }

    private static float DbToLinear(float db) => MathF.Pow(10.0f, db * 0.05f);
}

/// <summary>
/// Real-time 50%-overlapped Hann (COLA) SIR-adaptive temporally-smoothed multi-reference Power Wiener bleed subtractor
/// powered by NWaves <see cref="NWaves.Transforms.RealFft"/>, causal room-reverb tail estimator, smoothed noise-gate envelope,
/// echo-restrained decoupled dB-domain speech loudness normalizer (-16.5 dBFS), and -1.0 dBFS soft-knee peak limiter.
/// </summary>
internal static class AcousticSpectralCanceller
{
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

        if (subStartSample > self.LastNonZeroSampleIndex &&
            self.LastGateState == AcousticGateState.Open &&
            selfVocalRms == 0f &&
            !isPercussiveClick &&
            self.LevelerSpeechHoldSubFrames == 0 &&
            MathF.Abs(self.CurrentGain - self.TargetGain) < 1e-5f &&
            MathF.Abs(self.LevelerSpeechPresence) < 1e-4f &&
            MathF.Abs(self.IdleSilenceGateGain - 0.25f) < 1e-4f &&
            MathF.Abs(self.CurrentDoubleTalkMakeupScale - 1.0f) < 1e-4f)
        {
            self.Leveler.AdvancePureSilenceSubBlock(subLen);
            if (self.Leveler.IsIdleSettled)
            {
                self.CurrentGain = self.TargetGain;
                self.LevelerSpeechPresence = 0f;
                self.IdleSilenceGateGain = 0.25f;
                self.CurrentDoubleTalkMakeupScale = 1.0f;
                outPcm.Clear();
                return;
            }
        }

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

        bool isGatedBleed = self.LastGateState == AcousticGateState.GatedBleed;
        bool isDoubleTalk = self.LastGateState == AcousticGateState.DoubleTalkSubtraction;
        float targetDtMakeupScale = isGatedBleed ? 0.0f : 1.0f;
        float targetSpeechPresence = 0.0f;
        float targetIdleGateGain = 1.0f;
        float cleanedPeak = 0.0f;
        for (int i = 0; i < len; i++)
        {
            float absVal = MathF.Abs(cleanedSamples[i]);
            if (absVal > cleanedPeak) cleanedPeak = absVal;
        }

        float cleanedMeanSq = 0.0f;
        bool isActiveNearFieldSpeech = false;

        if (isGatedBleed)
        {
            self.LevelerSpeechHoldSubFrames = 0;
        }
        else
        {
            cleanedMeanSq = (float)AcousticDspMath.ComputeMeanSquareSimd(cleanedSamples);
            float cleanedRms = MathF.Sqrt(cleanedMeanSq);
            float postWienerRetention = (isDoubleTalk && rawPreCancelRms > 1e-5f)
                ? Math.Clamp(cleanedRms / rawPreCancelRms, 0f, 1f)
                : 1.0f;

            if (isDoubleTalk)
            {
                targetDtMakeupScale = Math.Clamp((postWienerRetention - 0.30f) / 0.42f, 0.0f, 1.0f);
            }

            float dynamicSpeechGate = MathF.Max(PostLevelerSpeechFloorRms, self.Leveler.NoiseFloorRms * 2.5f);
            isActiveNearFieldSpeech = !isPercussiveClick &&
                                      selfVocalRms >= VocalActivityFloorRms * 0.85f &&
                                      cleanedRms >= dynamicSpeechGate &&
                                      (!isDoubleTalk || postWienerRetention >= 0.50f);

            if (isActiveNearFieldSpeech)
            {
                self.LevelerSpeechHoldSubFrames = 42; // 420ms hangover bridges short breaths & inter-word pauses without pumping
                targetSpeechPresence = 1.0f;
            }
            else if (self.LevelerSpeechHoldSubFrames > 0)
            {
                self.LevelerSpeechHoldSubFrames--;
                targetSpeechPresence = 1.0f;
            }
            else if (self.LastGateState == AcousticGateState.Open &&
                     !isPercussiveClick &&
                     selfVocalRms < VocalActivityFloorRms * 0.75f &&
                     cleanedRms <= MathF.Max(PostLevelerSpeechFloorRms * 0.80f, self.Leveler.NoiseFloorRms * 2.4f))
            {
                // Gentle downward expansion (-12 dB) during sustained room silence keeps post-RNNoise idle hiss crisp & silent
                targetIdleGateGain = 0.25f;
            }
        }

        float desiredDuck = self.Leveler.ComputeSubBlockDesiredDuck(
            cleanedMeanSq,
            cleanedPeak,
            len,
            isGatedBleed,
            isPercussiveClick,
            isActiveNearFieldSpeech,
            isDoubleTalk,
            out bool seededThisBlock);

        if (seededThisBlock && self.LastGateState == AcousticGateState.Open && self.Leveler.TargetMakeupGain > 1.0f)
        {
            self.LevelerSpeechPresence = 1.0f;
        }

        float dtScaleAlpha = targetDtMakeupScale < self.CurrentDoubleTalkMakeupScale ? 0.024f : 0.006f;
        float speechPresenceAlpha = targetSpeechPresence > self.LevelerSpeechPresence ? 0.040f : 0.00035f;
        float idleGateAlpha = targetIdleGateGain > self.IdleSilenceGateGain ? 0.050f : 0.00040f;
        bool isClosingGate = self.TargetGain <= 0.01f;

        for (int i = 0; i < len; i++)
        {
            self.Leveler.StepSample(isGatedBleed, desiredDuck);
            self.CurrentDoubleTalkMakeupScale += dtScaleAlpha * (targetDtMakeupScale - self.CurrentDoubleTalkMakeupScale);
            self.LevelerSpeechPresence += speechPresenceAlpha * (targetSpeechPresence - self.LevelerSpeechPresence);
            self.IdleSilenceGateGain += idleGateAlpha * (targetIdleGateGain - self.IdleSilenceGateGain);

            float g = gateEnvelope[i];
            float gateSq = g * g;
            float gateWeight = isClosingGate ? (gateSq * gateSq) : gateSq;
            float effectiveTotalGain = self.Leveler.EffectiveGain;
            float makeupDelta = effectiveTotalGain - 1.0f;
            float effectiveScale = makeupDelta > 0f
                ? (gateWeight * self.CurrentDoubleTalkMakeupScale * self.LevelerSpeechPresence)
                : gateWeight;
            float appliedMakeup = (1.0f + makeupDelta * effectiveScale) * self.IdleSilenceGateGain;
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
