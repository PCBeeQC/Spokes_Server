namespace Spokes_Server.Core.Services.Communication.Voice.Dsp;

using System;
using System.Buffers;
using System.Runtime.CompilerServices;
using NWaves.Utils;
using static Spokes_Server.Core.Services.Communication.Voice.Dsp.AcousticRoomProcessor;

internal readonly record struct PairAlignmentMeasurement(
    int MeasuredLagSamples,
    float MeasuredGain,
    float PeakOnsetGain,
    float SrcAlignedRms,
    float TgtAlignedRms,
    double BestEnvCorr,
    double FineWaveCorr,
    double BarkCorr,
    double DirectnessDb,
    double DominanceScoreDb);

/// <summary>
/// Stateless hybrid signal-alignment &amp; acoustic directness estimator combining:
/// 1. Vocal-band envelope Normalized Cross-Correlation (immune to AI phase warping),
/// 2. Two-stage SIMD fine waveform lag search + zero-allocation NWaves <see cref="NWaves.Transforms.RealFft"/> GCC-PHAT fallback,
/// 3. 6-band log-Bark formant trajectory correlation, and
/// 4. Multi-scale Modulation Transfer Function (MTF) directness differential for AGC-inverted far-field rejection.
/// </summary>
internal static class AcousticLagCorrelator
{
    public static bool TryMeasurePairAlignment(
        AcousticParticipantStream source,
        AcousticParticipantStream target,
        PairwiseState? reversePairState,
        int? topologicalPriorLagSamples,
        long startSample,
        int windowSamples,
        int maxPositiveLagSteps,
        bool requireDominancePass,
        out PairAlignmentMeasurement measurement)
    {
        measurement = default;

        int numSteps = windowSamples / EnvelopeStepSamples;
        if (numSteps < 20) return false;

        Span<float> srcEnv = stackalloc float[numSteps];
        float srcMean = source.ReadEnvelopeSteps(startSample, srcEnv);
        if (srcMean < 0.0035f || !HasBalancedSpeechDutyCycle(srcEnv, srcMean))
        {
            return false;
        }

        Span<float> srcMeanNorm = stackalloc float[numSteps];
        double srcVar = AcousticDspMath.CenterAndSumSquares(srcEnv, srcMean, srcMeanNorm);
        if (srcVar < 1e-7) return false;

        bool hasStrongReverseDirect = reversePairState?.IsLiveDirectLock(0.55, requireLatched: true) == true;
        int clampedMaxPosSteps = Math.Clamp(maxPositiveLagSteps, 1, MaxLagEnvelopeSteps);
        int numLagSteps = (2 * MaxLagEnvelopeSteps) + 1;
        Span<float> envCorrByStep = stackalloc float[numLagSteps];
        envCorrByStep.Fill(-1.0f);

        int bestLagStep = SweepCoarseEnvelopeCorrelation(
            target,
            reversePairState,
            hasStrongReverseDirect,
            startSample,
            srcEnv,
            srcMeanNorm,
            srcMean,
            srcVar,
            clampedMaxPosSteps,
            envCorrByStep,
            out double bestEnvCorr);

        if (bestEnvCorr < 0.40) return false;

        int maxPositiveLagSamples = clampedMaxPosSteps * EnvelopeStepSamples;
        int refineSampleCount = Math.Min(windowSamples, 9600); // 200ms fine vocal waveform correlation
        int halfRefineCount = (refineSampleCount + 1) >> 1;

        const int minDelta = -EnvelopeStepSamples * 5;
        const int maxDelta = EnvelopeStepSamples * 3;
        int maxTgtBufLen = refineSampleCount + (maxDelta - minDelta) + 2;
        int maxHalfTgtLen = (maxTgtBufLen + 1) >> 1;

        float[] rentedFloats = ArrayPool<float>.Shared.Rent(refineSampleCount + halfRefineCount + maxTgtBufLen + (maxHalfTgtLen * 2));
        double[] rentedDoubles = ArrayPool<double>.Shared.Rent((halfRefineCount + 1) + ((maxHalfTgtLen + 1) * 2));
        try
        {
            Span<float> srcFullTemp = rentedFloats.AsSpan(0, refineSampleCount);
            Span<float> srcSub = rentedFloats.AsSpan(refineSampleCount, halfRefineCount);
            int tgtRawOffset = refineSampleCount + halfRefineCount;
            Span<float> tgtRawBuf = rentedFloats.AsSpan(tgtRawOffset, maxTgtBufLen);
            Span<float> tgtEvenBuf = rentedFloats.AsSpan(tgtRawOffset + maxTgtBufLen, maxHalfTgtLen);
            Span<float> tgtOddBuf = rentedFloats.AsSpan(tgtRawOffset + maxTgtBufLen + maxHalfTgtLen, maxHalfTgtLen);

            Span<double> srcWavePrefixSq = rentedDoubles.AsSpan(0, halfRefineCount + 1);
            Span<double> tgtEvenPrefixSq = rentedDoubles.AsSpan(halfRefineCount + 1, maxHalfTgtLen + 1);
            Span<double> tgtOddPrefixSq = rentedDoubles.AsSpan((halfRefineCount + 1) + (maxHalfTgtLen + 1), maxHalfTgtLen + 1);

            source.CopyVocalRange(startSample, srcFullTemp);
            srcWavePrefixSq[0] = 0.0;
            double srcWaveEnergy = 0.0;
            for (int k = 0; k < halfRefineCount; k++)
            {
                float s = srcFullTemp[k * 2];
                srcSub[k] = s;
                srcWaveEnergy += (double)s * s;
                srcWavePrefixSq[k + 1] = srcWaveEnergy;
            }

            long peakBurstSample = FindPeakBurstStartSample(srcEnv, startSample, windowSamples);
            double? expectedHardwareLagSamples = ResolveExpectedHardwareLagSamples(source, target, topologicalPriorLagSamples);

            int coarseLagSamples = bestLagStep * EnvelopeStepSamples;
            int fineBestLag = coarseLagSamples;
            double fineBestScore = 0.0;
            double selectedEnvCorr = bestEnvCorr;
            double selectedBarkCorr = 0.0;
            double bestJointScore = -1.0;

            float candidateEnvFloor = (float)Math.Max(0.42, bestEnvCorr * 0.78);
            for (int lagStep = -MaxLagEnvelopeSteps; lagStep <= clampedMaxPosSteps; lagStep++)
            {
                int idx = lagStep + MaxLagEnvelopeSteps;
                float envCorr = envCorrByStep[idx];
                if (envCorr < candidateEnvFloor || !IsEnvelopeLocalPeak(envCorrByStep, idx, lagStep == bestLagStep))
                {
                    continue;
                }

                int candidateCoarseSamples = lagStep * EnvelopeStepSamples;
                int localFineLag = candidateCoarseSamples;
                double localFineWaveCorr = 0.0;

                if (srcWaveEnergy > 1e-6)
                {
                    localFineLag = RefineFineWaveformLag(
                        source,
                        target,
                        startSample,
                        peakBurstSample,
                        candidateCoarseSamples,
                        minDelta,
                        maxDelta,
                        maxPositiveLagSamples,
                        refineSampleCount,
                        halfRefineCount,
                        srcSub,
                        srcWavePrefixSq,
                        tgtRawBuf,
                        tgtEvenBuf,
                        tgtOddBuf,
                        tgtEvenPrefixSq,
                        tgtOddPrefixSq,
                        out localFineWaveCorr);
                }

                int barkEvalLag = localFineWaveCorr >= 0.28 ? localFineLag : candidateCoarseSamples;
                double barkCorr = ComputeBarkFormantCorrelation(source, target, startSample, windowSamples, barkEvalLag);

                if (hasStrongReverseDirect &&
                    reversePairState!.PeakWaveformCorrelation >= 0.45 &&
                    startSample - reversePairState.LastVerifiedSample <= SampleRate * 25)
                {
                    long impliedTwoWayAir = (long)candidateCoarseSamples + reversePairState.DelaySamples;
                    if ((impliedTwoWayAir < -(SampleRate * 85) / 1000 || impliedTwoWayAir > (SampleRate * 115) / 1000) &&
                        localFineWaveCorr < 0.38 &&
                        barkCorr < 0.46)
                    {
                        continue;
                    }
                }

                double waveBonus = localFineWaveCorr >= 0.28 ? localFineWaveCorr * 0.65 : 0.0;
                double barkBonus = localFineWaveCorr >= 0.55
                    ? (barkCorr > 0.15 ? barkCorr * 0.10 : 0.0)
                    : (barkCorr > 0.15 ? barkCorr * 0.40 : (barkCorr < -0.10 ? barkCorr * 0.30 : 0.0));
                double hardwarePriorBonus = expectedHardwareLagSamples.HasValue &&
                                            Math.Abs(candidateCoarseSamples - expectedHardwareLagSamples.Value) <= 45 * 48
                    ? 0.06
                    : 0.0;
                double reverseConsistencyBonus = hasStrongReverseDirect
                    ? (AcousticCalibrationMatrix.IsConsistentTwoWayAir(candidateCoarseSamples, reversePairState!.DelaySamples)
                        ? 0.07
                        : (Math.Abs(candidateCoarseSamples + reversePairState.DelaySamples - (2 * DefaultAirDelaySamples)) <= 45 * 48 ? 0.03 : 0.0))
                    : 0.0;

                double absLagMs = Math.Abs(candidateCoarseSamples) * 1000.0 / SampleRate;
                double extendedLagPenalty = absLagMs > 200.0 ? 0.06 * ((absLagMs - 200.0) / 140.0) : 0.0;

                double jointScore = envCorr + waveBonus + barkBonus + hardwarePriorBonus + reverseConsistencyBonus - extendedLagPenalty;
                if (jointScore > bestJointScore)
                {
                    bestJointScore = jointScore;
                    coarseLagSamples = candidateCoarseSamples;
                    fineBestLag = localFineLag;
                    fineBestScore = localFineWaveCorr;
                    selectedEnvCorr = envCorr;
                    selectedBarkCorr = barkCorr;
                }
            }

            bestEnvCorr = selectedEnvCorr;

            if (Math.Abs(coarseLagSamples) > 45 * 48 &&
                !PassesExtendedLagCoherence(
                    coarseLagSamples,
                    bestEnvCorr,
                    fineBestScore,
                    selectedBarkCorr,
                    expectedHardwareLagSamples,
                    hasStrongReverseDirect,
                    reversePairState))
            {
                return false;
            }

            int measuredLagSamples = fineBestScore >= 0.28 ? fineBestLag : coarseLagSamples;
            long overlapStartSample = Math.Max(startSample, target.TrackEpochSample - measuredLagSamples);
            int overlapWindowSamples = (int)((startSample + windowSamples) - overlapStartSample);
            if (overlapWindowSamples < 32 * EnvelopeStepSamples)
            {
                return false;
            }

            float srcAlignedRms = source.ComputeEnvelopeRms(overlapStartSample, overlapWindowSamples);
            float tgtAlignedRms = target.ComputeEnvelopeRms(overlapStartSample + measuredLagSamples, overlapWindowSamples);
            if (srcAlignedRms < 0.0035f || tgtAlignedRms < 0.00020f || tgtAlignedRms > srcAlignedRms * MaxAllowedAgcBleedGain)
            {
                return false;
            }

            double directnessDb = ComputeAcousticDirectnessDifferentialDb(source, target, startSample, srcEnv, srcMean, measuredLagSamples);
            double dominanceScoreDb = ComputeDirectionalDominanceScoreDb(srcAlignedRms, tgtAlignedRms, directnessDb, bestEnvCorr, reversePairState);
            if (requireDominancePass && dominanceScoreDb < 3.1)
            {
                return false;
            }

            MeasureAlignedBleedAndOnsetGains(
                target,
                startSample,
                srcEnv,
                srcMean,
                srcAlignedRms,
                tgtAlignedRms,
                measuredLagSamples,
                out float measuredGain,
                out float peakOnsetGain);

            measurement = new PairAlignmentMeasurement(
                measuredLagSamples,
                measuredGain,
                peakOnsetGain,
                srcAlignedRms,
                tgtAlignedRms,
                bestEnvCorr,
                fineBestScore,
                selectedBarkCorr,
                directnessDb,
                dominanceScoreDb);
            return true;
        }
        finally
        {
            ArrayPool<float>.Shared.Return(rentedFloats);
            ArrayPool<double>.Shared.Return(rentedDoubles);
        }
    }

    private static bool HasBalancedSpeechDutyCycle(ReadOnlySpan<float> srcEnv, float srcMean)
    {
        int numSteps = srcEnv.Length;
        float activeStepFloor = Math.Max(0.0020f, srcMean * 0.40f);
        int activeSrcSteps = 0;
        int firstHalfActiveSteps = 0;
        int recentHalfActiveSteps = 0;
        int halfPoint = numSteps / 2;

        for (int k = 0; k < numSteps; k++)
        {
            if (srcEnv[k] > activeStepFloor)
            {
                activeSrcSteps++;
                if (k < halfPoint) firstHalfActiveSteps++;
                else recentHalfActiveSteps++;
            }
        }

        return activeSrcSteps >= (int)(numSteps * 0.45) &&
               firstHalfActiveSteps >= (int)(halfPoint * 0.40) &&
               recentHalfActiveSteps >= (int)((numSteps - halfPoint) * 0.40);
    }

    private static int SweepCoarseEnvelopeCorrelation(
        AcousticParticipantStream target,
        PairwiseState? reversePairState,
        bool hasStrongReverseDirect,
        long startSample,
        ReadOnlySpan<float> srcEnv,
        ReadOnlySpan<float> srcMeanNorm,
        float srcMean,
        double srcVar,
        int clampedMaxPosSteps,
        Span<float> envCorrByStep,
        out double bestEnvCorr)
    {
        int numSteps = srcEnv.Length;
        int bestLagStep = 0;
        bestEnvCorr = -1.0;

        Span<double> srcPrefixSum = stackalloc double[numSteps + 1];
        Span<double> srcPrefixSq = stackalloc double[numSteps + 1];
        for (int k = 0; k < numSteps; k++)
        {
            double se = srcEnv[k];
            srcPrefixSum[k + 1] = srcPrefixSum[k] + se;
            srcPrefixSq[k + 1] = srcPrefixSq[k] + (se * se);
        }

        int minOverlapSteps = numSteps >= CorrelationWindowEnvelopeSteps ? (int)(numSteps * 0.60) : numSteps;
        Span<float> tgtEnv = stackalloc float[numSteps];

        for (int lagStep = -MaxLagEnvelopeSteps; lagStep <= clampedMaxPosSteps; lagStep++)
        {
            long lagSamples = lagStep * EnvelopeStepSamples;
            int kStart = 0;
            long targetWindowStart = startSample + lagSamples;
            if (targetWindowStart < target.TrackEpochSample)
            {
                long missingSamples = target.TrackEpochSample - targetWindowStart;
                kStart = (int)((missingSamples + EnvelopeStepSamples - 1) / EnvelopeStepSamples);
            }

            int overlapSteps = numSteps - kStart;
            if (overlapSteps < minOverlapSteps) continue;

            float effSrcMean = srcMean;
            double effSrcVar = srcVar;
            if (kStart > 0)
            {
                double subSum = srcPrefixSum[numSteps] - srcPrefixSum[kStart];
                double subSq = srcPrefixSq[numSteps] - srcPrefixSq[kStart];
                effSrcMean = (float)(subSum / overlapSteps);
                effSrcVar = subSq - ((subSum * subSum) / overlapSteps);
                if (effSrcMean < 0.0035f || effSrcVar < 1e-7) continue;
            }

            Span<float> tgtOverlap = tgtEnv.Slice(kStart, overlapSteps);
            float tgtMean = target.ReadEnvelopeSteps(startSample + (kStart * EnvelopeStepSamples) + lagSamples, tgtOverlap);
            if (tgtMean < 0.00020f || tgtMean < effSrcMean * 0.0020f || tgtMean > effSrcMean * MaxAllowedAgcBleedGain)
            {
                continue;
            }

            if (hasStrongReverseDirect)
            {
                float apparentGain = tgtMean / Math.Max(effSrcMean, 1e-5f);
                long impliedTwoWayAirSamples = lagSamples + reversePairState!.DelaySamples;
                if (reversePairState.BleedGain * apparentGain > 0.42f &&
                    (impliedTwoWayAirSamples < -(FrameSamples10Ms * 5 / 2) || impliedTwoWayAirSamples > FrameSamples20Ms * 6))
                {
                    continue;
                }
            }

            double corr = kStart == 0
                ? AcousticDspMath.ComputeCenteredPearson(srcMeanNorm, effSrcVar, tgtEnv, tgtMean, 1e-7)
                : AcousticDspMath.ComputePearson(srcEnv.Slice(kStart, overlapSteps), effSrcMean, effSrcVar, tgtOverlap, tgtMean, 1e-7);

            if (corr > -1.0)
            {
                if (kStart > 0)
                {
                    corr *= 0.94 + 0.06 * ((double)overlapSteps / numSteps);
                }
                envCorrByStep[lagStep + MaxLagEnvelopeSteps] = (float)corr;
                if (corr > bestEnvCorr)
                {
                    bestEnvCorr = corr;
                    bestLagStep = lagStep;
                }
            }
        }

        return bestLagStep;
    }

    private static long FindPeakBurstStartSample(ReadOnlySpan<float> srcEnv, long startSample, int windowSamples)
    {
        int peakStepIdx = 0;
        float peakStepEnv = 0f;
        for (int k = 0; k < srcEnv.Length; k++)
        {
            if (srcEnv[k] > peakStepEnv)
            {
                peakStepEnv = srcEnv[k];
                peakStepIdx = k;
            }
        }
        return Math.Clamp(
            startSample + peakStepIdx * EnvelopeStepSamples - (GccPhatFftSize / 2),
            startSample,
            Math.Max(startSample, startSample + windowSamples - GccPhatFftSize));
    }

    private static double? ResolveExpectedHardwareLagSamples(
        AcousticParticipantStream source,
        AcousticParticipantStream target,
        int? topologicalPriorLagSamples)
    {
        if (topologicalPriorLagSamples.HasValue) return topologicalPriorLagSamples.Value;
        if (!source.EstimatedCaptureLatencyMs.HasValue || !target.EstimatedCaptureLatencyMs.HasValue) return null;

        double deltaMs = (target.EstimatedCaptureLatencyMs.Value - source.EstimatedCaptureLatencyMs.Value)
                       + (((target.ServerToClientDelayMs ?? 0.0) - (source.ServerToClientDelayMs ?? 0.0)) * 0.5)
                       + 4.0;
        return (deltaMs * SampleRate) / 1000.0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsEnvelopeLocalPeak(ReadOnlySpan<float> envCorrByStep, int idx, bool isGlobalBest)
    {
        if (isGlobalBest) return true;
        float val = envCorrByStep[idx];
        int nStart = Math.Max(0, idx - 4);
        int nEnd = Math.Min(envCorrByStep.Length - 1, idx + 4);
        for (int m = nStart; m <= nEnd; m++)
        {
            if (m != idx && envCorrByStep[m] > val) return false;
        }
        return true;
    }

    private static int RefineFineWaveformLag(
        AcousticParticipantStream source,
        AcousticParticipantStream target,
        long startSample,
        long peakBurstSample,
        int candidateCoarseSamples,
        int minDelta,
        int maxDelta,
        int maxPositiveLagSamples,
        int refineSampleCount,
        int halfRefineCount,
        ReadOnlySpan<float> srcSub,
        ReadOnlySpan<double> srcWavePrefixSq,
        Span<float> tgtRawBuf,
        Span<float> tgtEvenBuf,
        Span<float> tgtOddBuf,
        Span<double> tgtEvenPrefixSq,
        Span<double> tgtOddPrefixSq,
        out double localFineWaveCorr)
    {
        int localFineLag = candidateCoarseSamples;
        localFineWaveCorr = 0.0;

        int lagLo = Math.Max(-MaxLagSamples, candidateCoarseSamples + minDelta);
        int lagHi = Math.Min(maxPositiveLagSamples, candidateCoarseSamples + maxDelta);
        double bestRankedWaveScore = 0.0;

        if (lagLo <= lagHi)
        {
            int spanLags = lagHi - lagLo;
            target.CopyVocalRange(startSample + lagLo, tgtRawBuf.Slice(0, refineSampleCount + spanLags));

            int evenCount = (spanLags >> 1) + halfRefineCount;
            int oddCount = spanLags >= 1 ? (((spanLags - 1) >> 1) + halfRefineCount) : 0;

            PopulateDecimatedHalfBuffer(tgtRawBuf, offset: 0, evenCount, tgtEvenBuf, tgtEvenPrefixSq);
            PopulateDecimatedHalfBuffer(tgtRawBuf, offset: 1, oddCount, tgtOddBuf, tgtOddPrefixSq);

            Span<int> topCoarseLags = stackalloc int[4];
            Span<double> topCoarseScores = stackalloc double[4];
            topCoarseScores.Fill(-1.0);

            for (int candidateLag = lagLo; ; candidateLag += 4)
            {
                if (candidateLag > lagHi) candidateLag = lagHi;

                if (TryScoreFineWaveShift(
                        candidateLag, lagLo, candidateCoarseSamples, startSample, target.TrackEpochSample,
                        refineSampleCount, halfRefineCount, srcSub, srcWavePrefixSq,
                        tgtEvenBuf, tgtOddBuf, tgtEvenPrefixSq, tgtOddPrefixSq,
                        out double waveCorr, out double rankedScore))
                {
                    if (rankedScore > bestRankedWaveScore)
                    {
                        bestRankedWaveScore = rankedScore;
                        localFineWaveCorr = waveCorr;
                        localFineLag = candidateLag;
                    }
                    RecordTopCoarseLobe(topCoarseLags, topCoarseScores, candidateLag, rankedScore);
                }

                if (candidateLag == lagHi) break;
            }

            for (int c = 0; c < 4; c++)
            {
                if (topCoarseScores[c] < 0.0) continue;
                int centerLag = topCoarseLags[c];
                int refLo = Math.Max(lagLo, centerLag - 3);
                int refHi = Math.Min(lagHi, centerLag + 3);
                for (int candidateLag = refLo; candidateLag <= refHi; candidateLag++)
                {
                    if (candidateLag == centerLag) continue;
                    if (TryScoreFineWaveShift(
                            candidateLag, lagLo, candidateCoarseSamples, startSample, target.TrackEpochSample,
                            refineSampleCount, halfRefineCount, srcSub, srcWavePrefixSq,
                            tgtEvenBuf, tgtOddBuf, tgtEvenPrefixSq, tgtOddPrefixSq,
                            out double waveCorr, out double rankedScore) &&
                        rankedScore > bestRankedWaveScore)
                    {
                        bestRankedWaveScore = rankedScore;
                        localFineWaveCorr = waveCorr;
                        localFineLag = candidateLag;
                    }
                }
            }
        }

        if (localFineWaveCorr < 0.35 &&
            ComputeGccPhatFineLag(source, target, peakBurstSample, candidateCoarseSamples, maxPositiveLagSamples, out int gccFineLag, out double gccScore) &&
            gccScore > localFineWaveCorr)
        {
            localFineWaveCorr = gccScore;
            localFineLag = gccFineLag;
        }

        return localFineLag;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void PopulateDecimatedHalfBuffer(
        ReadOnlySpan<float> raw,
        int offset,
        int count,
        Span<float> halfBuf,
        Span<double> prefixSq)
    {
        prefixSq[0] = 0.0;
        double acc = 0.0;
        for (int m = 0; m < count; m++)
        {
            float v = raw[(m * 2) + offset];
            halfBuf[m] = v;
            acc += (double)v * v;
            prefixSq[m + 1] = acc;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void RecordTopCoarseLobe(Span<int> topLags, Span<double> topScores, int candidateLag, double rankedScore)
    {
        for (int c = 0; c < 4; c++)
        {
            if (topScores[c] >= 0.0 && Math.Abs(topLags[c] - candidateLag) < 6)
            {
                if (rankedScore > topScores[c])
                {
                    topScores[c] = rankedScore;
                    topLags[c] = candidateLag;
                }
                return;
            }
        }

        int minSlot = 0;
        for (int c = 1; c < 4; c++)
        {
            if (topScores[c] < topScores[minSlot]) minSlot = c;
        }
        if (rankedScore > topScores[minSlot])
        {
            topScores[minSlot] = rankedScore;
            topLags[minSlot] = candidateLag;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool TryScoreFineWaveShift(
        int candidateLag,
        int lagLo,
        int candidateCoarseSamples,
        long startSample,
        long targetEpochSample,
        int refineSampleCount,
        int halfRefineCount,
        ReadOnlySpan<float> srcSub,
        ReadOnlySpan<double> srcWavePrefixSq,
        ReadOnlySpan<float> tgtEvenBuf,
        ReadOnlySpan<float> tgtOddBuf,
        ReadOnlySpan<double> tgtEvenPrefixSq,
        ReadOnlySpan<double> tgtOddPrefixSq,
        out double waveCorr,
        out double rankedWaveScore)
    {
        waveCorr = 0.0;
        rankedWaveScore = 0.0;

        int kStartWave = 0;
        if (startSample + candidateLag < targetEpochSample)
        {
            long missingWave = targetEpochSample - (startSample + candidateLag);
            if (missingWave >= refineSampleCount - 2400) return false;
            kStartWave = (int)(((missingWave + 1) & ~1L) >> 1);
        }

        int countWave = halfRefineCount - kStartWave;
        if (countWave <= 0) return false;

        int offset = candidateLag - lagLo;
        int shift = offset >> 1;
        bool isOdd = (offset & 1) != 0;

        double effSrcWaveEnergy = srcWavePrefixSq[halfRefineCount] - srcWavePrefixSq[kStartWave];
        double tgtWaveEnergy = isOdd
            ? (tgtOddPrefixSq[shift + halfRefineCount] - tgtOddPrefixSq[shift + kStartWave])
            : (tgtEvenPrefixSq[shift + halfRefineCount] - tgtEvenPrefixSq[shift + kStartWave]);

        if (effSrcWaveEnergy <= 1e-9 || tgtWaveEnergy <= 1e-9) return false;

        ReadOnlySpan<float> srcSlice = srcSub.Slice(kStartWave, countWave);
        ReadOnlySpan<float> tgtSlice = isOdd
            ? tgtOddBuf.Slice(shift + kStartWave, countWave)
            : tgtEvenBuf.Slice(shift + kStartWave, countWave);

        double cross = AcousticDspMath.DotProductSimd(srcSlice, tgtSlice);
        double effectiveCross = cross >= 0 ? cross : Math.Abs(cross) * 0.85;
        waveCorr = effectiveCross / Math.Sqrt(effSrcWaveEnergy * tgtWaveEnergy);

        double proximityWeight = 1.0 - 0.03 * (Math.Abs(candidateLag - candidateCoarseSamples) / (double)(EnvelopeStepSamples * 5));
        rankedWaveScore = waveCorr * proximityWeight;
        return true;
    }

    private static bool PassesExtendedLagCoherence(
        int coarseLagSamples,
        double bestEnvCorr,
        double fineBestScore,
        double selectedBarkCorr,
        double? expectedHardwareLagSamples,
        bool hasStrongReverseDirect,
        PairwiseState? reversePairState)
    {
        bool severelyConflictsWithPrior =
            (hasStrongReverseDirect &&
             reversePairState!.PeakWaveformCorrelation >= 0.45 &&
             ((coarseLagSamples + reversePairState.DelaySamples) < -(SampleRate * 85) / 1000 ||
              (coarseLagSamples + reversePairState.DelaySamples) > (SampleRate * 115) / 1000)) ||
            (expectedHardwareLagSamples.HasValue &&
             Math.Abs(coarseLagSamples - expectedHardwareLagSamples.Value) > (SampleRate * 110) / 1000);

        if (severelyConflictsWithPrior)
        {
            return fineBestScore >= 0.38 || selectedBarkCorr >= 0.46 || (bestEnvCorr >= 0.84 && fineBestScore >= 0.32);
        }

        if (bestEnvCorr >= 0.64 || fineBestScore >= 0.38 || selectedBarkCorr >= 0.48)
        {
            return true;
        }

        bool matchesReciprocalAirWindow = hasStrongReverseDirect &&
                                          (coarseLagSamples + reversePairState!.DelaySamples) >= -(FrameSamples10Ms * 2) &&
                                          (coarseLagSamples + reversePairState.DelaySamples) <= FrameSamples20Ms * 5;
        if (matchesReciprocalAirWindow && bestEnvCorr >= 0.48 && (fineBestScore >= 0.20 || selectedBarkCorr >= 0.22))
        {
            return true;
        }

        bool matchesPriorNeighborhood = expectedHardwareLagSamples.HasValue &&
                                        Math.Abs(coarseLagSamples - expectedHardwareLagSamples.Value) <= 50 * 48;
        return matchesPriorNeighborhood && bestEnvCorr >= 0.50 && (fineBestScore >= 0.22 || selectedBarkCorr >= 0.24);
    }

    private static void MeasureAlignedBleedAndOnsetGains(
        AcousticParticipantStream target,
        long startSample,
        ReadOnlySpan<float> srcEnv,
        float srcMean,
        float srcAlignedRms,
        float tgtAlignedRms,
        int measuredLagSamples,
        out float measuredGain,
        out float peakOnsetGain)
    {
        int numSteps = srcEnv.Length;
        double activeSrcSq = 0;
        double activeTgtSq = 0;
        int activePeakSteps = 0;
        int onsetRatioCount = 0;
        float causalSrcEnv = 0f;
        const float causalStepDecay = 0.92f;
        Span<float> activeStepRatios = stackalloc float[numSteps];

        for (int k = 0; k < numSteps; k++)
        {
            float se = srcEnv[k];
            causalSrcEnv = Math.Max(se, causalSrcEnv * causalStepDecay);
            long tgtStepSample = startSample + k * EnvelopeStepSamples + measuredLagSamples;
            if (tgtStepSample < target.TrackEpochSample) continue;

            if (se >= srcMean)
            {
                float te = target.GetEnvelopeAt(tgtStepSample);
                activeSrcSq += se * se;
                activeTgtSq += te * te;
                activePeakSteps++;
                if (se >= srcMean * 1.05f)
                {
                    activeStepRatios[onsetRatioCount++] = te / Math.Max(causalSrcEnv, 1e-5f);
                }
            }
        }

        float windowGain = tgtAlignedRms / srcAlignedRms;
        measuredGain = (windowGain < 0.08f && activePeakSteps >= 5 && activeSrcSq > 1e-6)
            ? Math.Max(windowGain, (float)Math.Sqrt(activeTgtSq / activeSrcSq) * 0.85f)
            : windowGain;

        float maxGainClamp = tgtAlignedRms > srcAlignedRms * 0.70f ? MaxAllowedAgcBleedGain : 0.85f;
        measuredGain = Math.Clamp(measuredGain, 0.001f, maxGainClamp);
        peakOnsetGain = measuredGain;

        if (onsetRatioCount >= 6)
        {
            Span<float> validRatios = activeStepRatios.Slice(0, onsetRatioCount);
            validRatios.Sort();
            int endExclusive = onsetRatioCount >= 8 ? onsetRatioCount - 1 : onsetRatioCount;
            int startIdx = Math.Max(0, endExclusive - Math.Max(3, onsetRatioCount / 4));
            int ratioCount = endExclusive - startIdx;
            if (ratioCount > 0)
            {
                double ratioSum = 0.0;
                for (int r = startIdx; r < endExclusive; r++) ratioSum += validRatios[r];
                float topQuartileGain = (float)(ratioSum / ratioCount) * 0.90f;
                if (topQuartileGain > measuredGain * 1.45f)
                {
                    peakOnsetGain = Math.Clamp(topQuartileGain, measuredGain, maxGainClamp);
                }
            }
        }
    }

    public static double ComputeAcousticDirectnessDifferentialDb(
        AcousticParticipantStream source,
        AcousticParticipantStream target,
        long startSample,
        ReadOnlySpan<float> srcEnv,
        float srcMean,
        int measuredLagSamples)
    {
        int numSteps = srcEnv.Length;
        if (numSteps < 20 || srcMean < 0.0030f) return 0.0;

        double srcValidSum = 0;
        double tgtSum = 0;
        int inBoundsSteps = 0;
        Span<float> tgtAlignedEnv = stackalloc float[numSteps];
        Span<bool> inBounds = stackalloc bool[numSteps];

        for (int k = 0; k < numSteps; k++)
        {
            long srcSample = startSample + k * EnvelopeStepSamples;
            long tgtSample = srcSample + measuredLagSamples;
            if (srcSample >= source.TrackEpochSample && srcSample < source.WriteSampleIndex &&
                tgtSample >= target.TrackEpochSample && tgtSample < target.WriteSampleIndex)
            {
                float te = target.GetEnvelopeAt(tgtSample);
                tgtAlignedEnv[k] = te;
                inBounds[k] = true;
                srcValidSum += srcEnv[k];
                tgtSum += te;
                inBoundsSteps++;
            }
        }

        if (inBoundsSteps < 20) return 0.0;

        srcMean = (float)(srcValidSum / inBoundsSteps);
        float tgtMean = (float)(tgtSum / inBoundsSteps);
        if (srcMean < 0.0030f || tgtMean < 0.0030f) return 0.0;

        int tgtActiveSteps = 0;
        float tgtActiveFloor = Math.Max(0.0015f, tgtMean * 0.35f);
        for (int k = 0; k < numSteps; k++)
        {
            if (inBounds[k] && tgtAlignedEnv[k] > tgtActiveFloor) tgtActiveSteps++;
        }

        if ((double)tgtActiveSteps / inBoundsSteps < 0.38)
        {
            return tgtMean < srcMean * 0.55f ? 6.0 : 0.0;
        }

        Span<bool> validStep = stackalloc bool[numSteps];
        inBounds.CopyTo(validStep);
        for (int k = 0; k < numSteps; k++)
        {
            if (!inBounds[k]) continue;
            long srcSample = startSample + k * EnvelopeStepSamples;
            if (source.HasClickAt(srcSample) || target.HasClickAt(srcSample + measuredLagSamples))
            {
                int jEnd = Math.Min(numSteps - 1, k + 5);
                for (int j = Math.Max(0, k - 4); j <= jEnd; j++) validStep[j] = false;
            }
        }

        float invSrcMean = 1.0f / srcMean;
        float invTgtMean = 1.0f / tgtMean;

        double sh1Src = 0, sh1Tgt = 0, sh4Src = 0, sh4Tgt = 0, varSrc = 0, varTgt = 0;
        int count1 = 0, count4 = 0, countCv = 0;

        for (int k = 0; k < numSteps; k++)
        {
            if (!validStep[k]) continue;

            float sNorm = srcEnv[k] * invSrcMean;
            float tNorm = tgtAlignedEnv[k] * invTgtMean;
            double ds = sNorm - 1.0;
            double dt = tNorm - 1.0;
            varSrc += ds * ds;
            varTgt += dt * dt;
            countCv++;

            if (k >= 1 && validStep[k - 1])
            {
                sh1Src += Math.Abs(sNorm - (srcEnv[k - 1] * invSrcMean));
                sh1Tgt += Math.Abs(tNorm - (tgtAlignedEnv[k - 1] * invTgtMean));
                count1++;
            }
            if (k >= 4 && validStep[k - 2] && validStep[k - 4])
            {
                sh4Src += Math.Abs(sNorm - (srcEnv[k - 4] * invSrcMean));
                sh4Tgt += Math.Abs(tNorm - (tgtAlignedEnv[k - 4] * invTgtMean));
                count4++;
            }
        }

        if (count1 < 12 || count4 < 10 || countCv < 16) return 0.0;

        double sh1Db = Scale.ToDecibel(Math.Max(sh1Src / count1, 1e-4) / Math.Max(sh1Tgt / count1, 1e-4));
        double sh4Db = Scale.ToDecibel(Math.Max(sh4Src / count4, 1e-4) / Math.Max(sh4Tgt / count4, 1e-4));
        double cvDb = AcousticDspMath.ToPowerRatioDb(Math.Max(varSrc / countCv, 1e-4), Math.Max(varTgt / countCv, 1e-4));

        double rawDirectnessDb = Math.Clamp(sh1Db * 1.4 + sh4Db * 1.6 + cvDb * 1.2, -24.0, 24.0);
        return (srcMean < tgtMean * 0.95f && (sh1Db < 0.35 || sh4Db < 0.35))
            ? Math.Min(rawDirectnessDb, 0.0)
            : rawDirectnessDb;
    }

    public static double ComputeDirectionalDominanceScoreDb(
        float srcAlignedRms,
        float tgtAlignedRms,
        double directnessDb,
        double bestEnvCorr,
        PairwiseState? reversePairState)
    {
        float impliedGain = tgtAlignedRms / Math.Max(srcAlignedRms, 1e-6f);
        if (reversePairState?.IsLiveDirectLock(0.55, requireLatched: true) == true &&
            reversePairState.BleedGain * impliedGain > 0.55f)
        {
            return -10.0;
        }

        double levelDiffDb = Scale.ToDecibel(Math.Max(srcAlignedRms, 1e-6) / Math.Max(tgtAlignedRms, 1e-6));
        if (levelDiffDb < 3.1 && (srcAlignedRms < VocalActivityFloorRms || bestEnvCorr < 0.55 || directnessDb < 4.2))
        {
            return levelDiffDb;
        }

        double effectiveLevelDb = levelDiffDb;
        if (levelDiffDb < 0.0 && directnessDb >= 4.2)
        {
            effectiveLevelDb = Math.Max(levelDiffDb * 0.15, -1.5);
        }
        else if (levelDiffDb > 0.0 && tgtAlignedRms >= VocalActivityFloorRms && directnessDb <= -3.8)
        {
            effectiveLevelDb = Math.Min(levelDiffDb * 0.15, 1.5);
        }

        return effectiveLevelDb + directnessDb;
    }

    public static double ComputeBarkFormantCorrelation(
        AcousticParticipantStream source,
        AcousticParticipantStream target,
        long startSample,
        int windowSamples,
        int lagSamples)
    {
        int numBarkSteps = windowSamples / BarkStepSamples;
        if (numBarkSteps < 8) return 0.0;

        Span<float> srcMatrix = stackalloc float[numBarkSteps * BarkBandCount];
        Span<float> tgtMatrix = stackalloc float[numBarkSteps * BarkBandCount];
        Span<float> srcBandMeans = stackalloc float[BarkBandCount];
        Span<float> tgtBandMeans = stackalloc float[BarkBandCount];

        for (int k = 0; k < numBarkSteps; k++)
        {
            long stepSample = startSample + k * BarkStepSamples;
            Span<float> sRow = srcMatrix.Slice(k * BarkBandCount, BarkBandCount);
            Span<float> tRow = tgtMatrix.Slice(k * BarkBandCount, BarkBandCount);
            source.ReadBarkBandsAt(stepSample, sRow);
            target.ReadBarkBandsAt(stepSample + lagSamples, tRow);

            for (int b = 0; b < BarkBandCount; b++)
            {
                srcBandMeans[b] += sRow[b];
                tgtBandMeans[b] += tRow[b];
            }
        }

        float invSteps = 1.0f / numBarkSteps;
        for (int b = 0; b < BarkBandCount; b++)
        {
            srcBandMeans[b] *= invSteps;
            tgtBandMeans[b] *= invSteps;
        }

        double cross = 0, srcVar = 0, tgtVar = 0;
        for (int k = 0; k < numBarkSteps; k++)
        {
            int rowBase = k * BarkBandCount;
            for (int b = 0; b < BarkBandCount; b++)
            {
                float sd = srcMatrix[rowBase + b] - srcBandMeans[b];
                float td = tgtMatrix[rowBase + b] - tgtBandMeans[b];
                cross += sd * td;
                srcVar += sd * sd;
                tgtVar += td * td;
            }
        }

        return (srcVar < 0.05 || tgtVar < 0.05) ? 0.0 : cross / Math.Sqrt(srcVar * tgtVar);
    }

    public static bool ComputeGccPhatFineLag(
        AcousticParticipantStream source,
        AcousticParticipantStream target,
        long burstStartSample,
        int candidateCoarseLag,
        int maxPositiveLagSamples,
        out int gccFineLag,
        out double gccScore)
    {
        gccFineLag = candidateCoarseLag;
        gccScore = 0.0;

        const int bins = (GccPhatFftSize / 2) + 1;
        Span<float> srcInput = stackalloc float[GccPhatFftSize];
        Span<float> tgtInput = stackalloc float[GccPhatFftSize];
        double srcEnergy = 0, tgtEnergy = 0;
        var window = AcousticDspMath.HannWindow512;

        for (int i = 0; i < GccPhatFftSize; i++)
        {
            float w = window[i];
            float s = source.ReadVocalSample(burstStartSample + i) * w;
            float t = target.ReadVocalSample(burstStartSample + candidateCoarseLag + i) * w;
            srcInput[i] = s;
            tgtInput[i] = t;
            srcEnergy += s * s;
            tgtEnergy += t * t;
        }

        if (srcEnergy < 1e-7 || tgtEnergy < 1e-8) return false;

        Span<float> srcRe = stackalloc float[bins];
        Span<float> srcIm = stackalloc float[bins];
        Span<float> tgtRe = stackalloc float[bins];
        Span<float> tgtIm = stackalloc float[bins];
        Span<float> phatRe = stackalloc float[bins];
        Span<float> phatIm = stackalloc float[bins];
        phatRe.Clear();
        phatIm.Clear();

        var fft = AcousticDspMath.Fft512;
        fft.Direct(srcInput, srcRe, srcIm);
        fft.Direct(tgtInput, tgtRe, tgtIm);

        // Vocal passband bins (~187 Hz to ~3560 Hz at 48kHz / 512 FFT -> bins 2..38)
        int activeBins = 0;
        for (int k = 2; k <= 38; k++)
        {
            float re = tgtRe[k] * srcRe[k] + tgtIm[k] * srcIm[k];
            float im = tgtIm[k] * srcRe[k] - tgtRe[k] * srcIm[k];
            float mag = MathF.Sqrt(re * re + im * im);
            if (mag > 1e-9f)
            {
                phatRe[k] = re / mag;
                phatIm[k] = im / mag;
                activeBins++;
            }
        }

        if (activeBins < 6) return false;

        Span<float> corrOut = stackalloc float[GccPhatFftSize];
        fft.InverseNorm(phatRe, phatIm, corrOut);

        int minDelta = -EnvelopeStepSamples * 2;
        int maxDelta = EnvelopeStepSamples * 2;
        float bestPeak = 0f;
        int bestDelta = 0;
        double energySum = 0;
        int count = 0;

        for (int delta = minDelta; delta <= maxDelta; delta++)
        {
            int candidateLag = candidateCoarseLag + delta;
            if (candidateLag < -MaxLagSamples || candidateLag > maxPositiveLagSamples) continue;

            float val = MathF.Abs(corrOut[(delta + GccPhatFftSize) & (GccPhatFftSize - 1)]);
            energySum += val * val;
            count++;
            if (val > bestPeak)
            {
                bestPeak = val;
                bestDelta = delta;
            }
        }

        if (count == 0 || energySum < 1e-12) return false;
        double rms = Math.Sqrt(energySum / count);
        double crestFactor = rms > 1e-9 ? bestPeak / rms : 0.0;
        if (crestFactor < 2.8) return false;

        gccFineLag = candidateCoarseLag + bestDelta;
        gccScore = Math.Clamp((crestFactor - 2.0) * 0.14, 0.28, 0.72);
        return true;
    }

    public static bool IsCorrelatedRoommateBleedBurst(
        AcousticParticipantStream self,
        AcousticParticipantStream roommate,
        long currentSubEndSample,
        int nominalDelaySamples)
    {
        const int evalSteps = 40;        // 80ms local onset window (40 steps @ 2ms)
        const int evalSamples = evalSteps * EnvelopeStepSamples;
        const int jitterSearchSteps = 18; // +/-36ms Wi-Fi jitter / prior search tolerance

        long selfWinStart = currentSubEndSample - evalSamples;
        if (selfWinStart < self.TrackEpochSample) return false;

        Span<float> selfEnv = stackalloc float[evalSteps];
        float selfMean = self.ReadEnvelopeSteps(selfWinStart, selfEnv);
        if (selfMean < 0.0025f) return false;

        double selfVar = AcousticDspMath.ComputeSumSquaredDeviations(selfEnv, selfMean);
        if (selfVar < 1e-8) return false;

        double bestEnvCorr = -1.0;
        int bestLagSamples = nominalDelaySamples;
        Span<float> refEnv = stackalloc float[evalSteps];

        for (int dStep = -jitterSearchSteps; dStep <= jitterSearchSteps; dStep++)
        {
            int candLag = Math.Clamp(nominalDelaySamples + dStep * EnvelopeStepSamples, -MaxLagSamples, MaxLagSamples);
            long refWinStart = selfWinStart - candLag;
            if (refWinStart < roommate.TrackEpochSample || refWinStart + evalSamples > roommate.WriteSampleIndex)
            {
                continue;
            }

            float refMean = roommate.ReadEnvelopeSteps(refWinStart, refEnv);
            if (refMean < 0.0035f) continue;

            double corr = AcousticDspMath.ComputePearson(selfEnv, selfMean, selfVar, refEnv, refMean, 1e-8);
            if (corr > bestEnvCorr)
            {
                bestEnvCorr = corr;
                bestLagSamples = candLag;
            }
        }

        if (bestEnvCorr < 0.68) return false;

        double barkCorr = ComputeBarkFormantCorrelation(roommate, self, selfWinStart - bestLagSamples, evalSamples, bestLagSamples);
        return barkCorr >= 0.25 || (bestEnvCorr >= 0.86 && barkCorr >= 0.05);
    }
}
