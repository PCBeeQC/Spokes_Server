namespace Spokes_Server.Tests.Services.Voice;

using System;
using System.Collections.Generic;
using Spokes_Server.Core.Services.Communication.Voice.Dsp;

/// <summary>
/// Deterministic multi-microphone acoustic room and network transport simulator for quantitative evaluation
/// of <see cref="AcousticRoomProcessor"/>.
/// Models direct acoustic propagation, early desk/wall reflections, RT60 diffuse reverb tails,
/// head-shadow low-pass filtering, dispersive AI filter phase warping (NVIDIA Broadcast / DeepFilterNet),
/// staggered participant join times, packet arrival jitter, and Opus DTX silence gaps.
/// </summary>
public static class AcousticRoomSimulator
{
    public const int SampleRate = AcousticRoomProcessor.SampleRate;
    public const int FrameSamples10Ms = AcousticRoomProcessor.FrameSamples10Ms;

    /// <summary>
    /// Generates a realistic multi-syllable synthetic speech signal with vocal formants, harmonic structure,
    /// and natural inter-phrase pauses.
    /// </summary>
    public static short[] GenerateNaturalSpeech(
        int totalSamples,
        double fundamentalHz = 220.0,
        double amplitude = 14000.0,
        int startSilenceSamples = 0,
        bool includeSyllablePauses = false)
    {
        var buffer = new short[totalSamples];
        for (int i = 0; i < totalSamples; i++)
        {
            int tIdx = i - startSilenceSamples;
            if (tIdx < 0)
            {
                buffer[i] = 0;
                continue;
            }

            double t = (double)tIdx / SampleRate;

            // Syllable modulation (3.8 Hz + 6.7 Hz)
            double env = 0.55 + 0.35 * Math.Sin(2.0 * Math.PI * 3.8 * t) + 0.10 * Math.Cos(2.0 * Math.PI * 6.7 * t);

            if (includeSyllablePauses)
            {
                // Natural cadence: 320ms active speech bursts separated by 80ms inter-word dips
                double cyclePos = (t % 0.40) / 0.40;
                if (cyclePos > 0.82)
                {
                    env *= 0.08;
                }
            }

            // Rich vocal formant harmonics (F0, 2*F0, 3*F0, 4.5*F0) within 200-3400Hz band
            double f0 = fundamentalHz * (1.0 + 0.03 * Math.Sin(2.0 * Math.PI * 2.1 * t));
            double wave = 0.52 * Math.Sin(2.0 * Math.PI * f0 * t)
                        + 0.26 * Math.Sin(2.0 * Math.PI * (f0 * 2.0) * t + 0.3)
                        + 0.14 * Math.Sin(2.0 * Math.PI * (f0 * 3.0) * t + 0.7)
                        + 0.08 * Math.Sin(2.0 * Math.PI * (f0 * 4.5) * t + 1.2);

            double sample = amplitude * env * wave;
            buffer[i] = (short)Math.Clamp(Math.Round(sample), short.MinValue, short.MaxValue);
        }
        return buffer;
    }

    /// <summary>
    /// Simulates acoustic propagation from a speaker to a roommate's microphone inside a physical room,
    /// including direct air delay, multi-tap early reflections, RT60 diffuse reverb decay, and head-shadow low-pass filtering.
    /// </summary>
    public static short[] ApplyRoomImpulseResponse(
        ReadOnlySpan<short> sourceSpeech,
        int directDelaySamples,
        double directBleedGain = 0.22,
        double rt60Ms = 0.0,
        bool applyHeadShadowLowPass = true)
    {
        int n = sourceSpeech.Length;
        var floatOut = new double[n];

        // Build discrete Room Impulse Response (RIR) taps: (delayOffsetSamples, linearGain)
        var taps = new List<(int Offset, double Gain)>
        {
            (Math.Max(0, directDelaySamples), directBleedGain)
        };

        if (rt60Ms > 10.0)
        {
            // Early reflections (desk, monitor, nearby walls) + diffuse exponential tail
            // RT60 is the time for energy to decay by 60 dB (amplitude factor of 0.001 = exp(-6.9078 * t / rt60))
            double[] reflectionOffsetsMs = [2.8, 5.4, 8.9, 13.2, 18.5, 24.0, 31.5, 40.0];
            double[] reflectionSigns = [1.0, -0.85, 0.75, -0.65, 0.55, -0.45, 0.35, -0.25];

            for (int r = 0; r < reflectionOffsetsMs.Length; r++)
            {
                double extraMs = reflectionOffsetsMs[r];
                if (extraMs > rt60Ms * 0.6) break;

                int tapDelay = Math.Max(0, directDelaySamples + (int)Math.Round(extraMs * SampleRate / 1000.0));
                double decay = Math.Exp(-6.9078 * extraMs / rt60Ms);
                double tapGain = directBleedGain * 0.38 * decay * reflectionSigns[r];
                taps.Add((tapDelay, tapGain));
            }
        }

        foreach (var (offset, gain) in taps)
        {
            for (int i = offset; i < n; i++)
            {
                floatOut[i] += sourceSpeech[i - offset] * gain;
            }
        }

        // Apply 1st-order low-pass filter (~2400 Hz) to model head/torso acoustic shadowing on distant bleed
        var result = new short[n];
        double lpState = 0.0;
        const double alpha = 0.27; // ~2400 Hz @ 48kHz

        for (int i = 0; i < n; i++)
        {
            double s = floatOut[i];
            if (applyHeadShadowLowPass)
            {
                lpState += alpha * (s - lpState);
                s = lpState;
            }
            result[i] = (short)Math.Clamp(Math.Round(s), short.MinValue, short.MaxValue);
        }

        return result;
    }

    /// <summary>
    /// Simulates a client-side AI noise filter (such as NVIDIA Broadcast, DeepFilterNet, or RNNoise)
    /// that introduces a fixed processing buffer delay and optionally scrambles fine waveform phase via
    /// a 4-stage cascaded all-pass filter network while preserving the vocal syllable envelope.
    /// </summary>
    public static short[] ApplyClientAiFilterSimulation(
        ReadOnlySpan<short> inputPcm,
        int processingDelaySamples,
        bool warpWaveformPhase = true)
    {
        int n = inputPcm.Length;
        var delayed = new double[n];
        for (int i = Math.Max(0, processingDelaySamples); i < n; i++)
        {
            delayed[i] = inputPcm[i - processingDelaySamples];
        }

        if (warpWaveformPhase)
        {
            // Cascade 4 all-pass biquad/1st-order sections with pole coefficients that rotate phase across 200-3400Hz
            // so time-domain waveform correlation drops while vocal envelope remains intact.
            double[] allPassCoeffs = [0.62, -0.54, 0.71, -0.43];
            foreach (double a in allPassCoeffs)
            {
                double xPrev = 0.0;
                double yPrev = 0.0;
                for (int i = 0; i < n; i++)
                {
                    double x = delayed[i];
                    double y = -a * x + xPrev + a * yPrev;
                    xPrev = x;
                    yPrev = y;
                    delayed[i] = y;
                }
            }
        }

        var output = new short[n];
        for (int i = 0; i < n; i++)
        {
            output[i] = (short)Math.Clamp(Math.Round(delayed[i]), short.MinValue, short.MaxValue);
        }
        return output;
    }

    /// <summary>
    /// Simulates a mobile speakerphone hardware HAL / WebRTC AGC2 stage that aggressively boosts
    /// quiet far-field room speech toward a target digital level (e.g. -15.7 dBFS) with fast attack,
    /// slow release gain pumping, and soft-knee limiter saturation.
    /// </summary>
    public static short[] ApplyMobileSpeakerphoneAgcSimulation(
        ReadOnlySpan<short> inputPcm,
        double targetRmsDbFs = -15.7,
        double maxGainDb = 34.0,
        double noiseGateFloorDbFs = -65.0)
    {
        int n = inputPcm.Length;
        var output = new short[n];
        double targetRms = Math.Pow(10.0, targetRmsDbFs / 20.0);
        double maxLinearGain = Math.Pow(10.0, maxGainDb / 20.0);
        double gateFloor = Math.Pow(10.0, noiseGateFloorDbFs / 20.0);

        // Envelope follower time constants @ 48kHz: ~8ms attack, ~180ms release
        double attackAlpha = 1.0 - Math.Exp(-1.0 / (0.008 * SampleRate));
        double releaseAlpha = 1.0 - Math.Exp(-1.0 / (0.180 * SampleRate));
        double gainSmoothAlpha = 1.0 - Math.Exp(-1.0 / (0.025 * SampleRate));

        double envSq = 0.0;
        double currentGain = 1.0;

        for (int i = 0; i < n; i++)
        {
            double x = inputPcm[i] / 32768.0;
            double xSq = x * x;
            double envAlpha = xSq > envSq ? attackAlpha : releaseAlpha;
            envSq += envAlpha * (xSq - envSq);

            double envRms = Math.Sqrt(Math.Max(envSq, 1e-12));
            double desiredGain = envRms > gateFloor
                ? Math.Clamp(targetRms / envRms, 0.5, maxLinearGain)
                : 1.0;

            currentGain += gainSmoothAlpha * (desiredGain - currentGain);

            // Soft-knee tanh limiter at -3 dBFS to prevent hard digital clipping
            double boosted = x * currentGain;
            double limited = 0.72 * Math.Tanh(boosted / 0.72);
            output[i] = (short)Math.Clamp(Math.Round(limited * 32768.0), short.MinValue, short.MaxValue);
        }

        return output;
    }

    /// <summary>
    /// Simulates mobile hardware/WebRTC Noise Suppression (NS) onset-burst dynamics on far-field roommate speech:
    /// After a silence gap (&gt;= <paramref name="silenceResetMs"/>), the mobile NS passes the initial
    /// <paramref name="onsetHoldMs"/> (~80-100ms) of far-field speech nearly un-attenuated (1.0x) before classifying
    /// it as background noise and clamping steady-state gain down to <paramref name="steadyStateSuppressionGain"/> (~0.14x).
    /// </summary>
    public static short[] ApplyMobileHardwareNsOnsetBurstSimulation(
        ReadOnlySpan<short> rawBleedPcm,
        double onsetHoldMs = 85.0,
        double steadyStateSuppressionGain = 0.14,
        double silenceResetMs = 150.0)
    {
        int n = rawBleedPcm.Length;
        var output = new short[n];
        int onsetHoldSamples = (int)Math.Round(onsetHoldMs * SampleRate / 1000.0);
        int silenceResetSamples = (int)Math.Round(silenceResetMs * SampleRate / 1000.0);

        double envAlpha = 1.0 - Math.Exp(-1.0 / (0.008 * SampleRate));
        double clampDecayAlpha = 1.0 - Math.Exp(-1.0 / (0.040 * SampleRate));
        double openRecoverAlpha = 1.0 - Math.Exp(-1.0 / (0.025 * SampleRate));

        double envSq = 0.0;
        double currentNsGain = 1.0;
        int activeSamples = 0;
        int silentSamples = silenceResetSamples;

        for (int i = 0; i < n; i++)
        {
            double x = rawBleedPcm[i] / 32768.0;
            envSq += envAlpha * (x * x - envSq);
            double envRms = Math.Sqrt(Math.Max(envSq, 1e-12));

            if (envRms >= 0.0018)
            {
                silentSamples = 0;
                activeSamples++;
                double targetGain = activeSamples <= onsetHoldSamples ? 1.0 : steadyStateSuppressionGain;
                currentNsGain += clampDecayAlpha * (targetGain - currentNsGain);
            }
            else
            {
                silentSamples++;
                if (silentSamples >= silenceResetSamples)
                {
                    activeSamples = 0;
                    currentNsGain += openRecoverAlpha * (1.0 - currentNsGain);
                }
            }

            output[i] = (short)Math.Clamp(Math.Round(rawBleedPcm[i] * currentNsGain), short.MinValue, short.MaxValue);
        }

        return output;
    }

    /// <summary>
    /// Injects high-frequency mechanical keyboard click bursts (6.2 kHz percussive transients, 10ms duration)
    /// onto a microphone stream at the specified frame indices.
    /// </summary>
    public static int InjectKeyboardClicks(
        Span<short> targetMicPcm,
        IEnumerable<int> clickFrameIndices,
        double clickAmplitude = 15500.0)
    {
        int injected = 0;
        foreach (int f in clickFrameIndices)
        {
            int start = f * FrameSamples10Ms;
            if (start < 0 || start + FrameSamples10Ms > targetMicPcm.Length) continue;

            for (int j = 0; j < FrameSamples10Ms; j++)
            {
                double t = (double)j / SampleRate;
                double env = Math.Exp(-j / (FrameSamples10Ms * 0.45));
                short click = (short)Math.Round(clickAmplitude * env * Math.Sin(2.0 * Math.PI * 6200.0 * t));
                targetMicPcm[start + j] = (short)Math.Clamp(targetMicPcm[start + j] + click, short.MinValue, short.MaxValue);
            }
            injected++;
        }
        return injected;
    }

    /// <summary>
    /// Computes Echo Return Loss Enhancement / Bleed Suppression Ratio (in dB):
    /// 10 * log10(sum(bleedIn^2) / sum(cleanedOut^2)).
    /// </summary>
    public static double ComputeBleedSuppressionDb(ReadOnlySpan<short> bleedIn, ReadOnlySpan<short> cleanedOut)
    {
        int len = Math.Min(bleedIn.Length, cleanedOut.Length);
        if (len == 0) return 0.0;

        double inSq = 0.0;
        double outSq = 0.0;
        for (int i = 0; i < len; i++)
        {
            double x = bleedIn[i];
            double y = cleanedOut[i];
            inSq += x * x;
            outSq += y * y;
        }

        if (inSq < 1e-6) return 0.0;
        if (outSq < 1e-6) return 60.0;
        return Math.Round(10.0 * Math.Log10(inSq / outSq), 2);
    }

    /// <summary>
    /// Computes Scale-Invariant Signal-to-Distortion Ratio (SI-SDR) in dB between a clean target speech signal
    /// and a degraded or processed signal (accounting for the processor's lookahead delay offset).
    /// </summary>
    public static double ComputeSiSdrDb(
        ReadOnlySpan<short> cleanReference,
        ReadOnlySpan<short> testSignal,
        int lookaheadDelaySamples = 0)
    {
        int count = Math.Min(cleanReference.Length, testSignal.Length - Math.Max(0, lookaheadDelaySamples));
        if (count <= 0) return 0.0;

        double dot = 0.0;
        double refEnergy = 0.0;

        for (int i = 0; i < count; i++)
        {
            double s = cleanReference[i];
            double x = testSignal[i + Math.Max(0, lookaheadDelaySamples)];
            dot += s * x;
            refEnergy += s * s;
        }

        if (refEnergy < 1e-6) return 0.0;
        double alpha = dot / refEnergy;

        double targetEnergy = 0.0;
        double noiseEnergy = 0.0;
        for (int i = 0; i < count; i++)
        {
            double sProj = alpha * cleanReference[i];
            double x = testSignal[i + Math.Max(0, lookaheadDelaySamples)];
            double err = x - sProj;
            targetEnergy += sProj * sProj;
            noiseEnergy += err * err;
        }

        if (noiseEnergy < 1e-6) return 60.0;
        if (targetEnergy < 1e-6) return -60.0;
        return Math.Round(10.0 * Math.Log10(targetEnergy / noiseEnergy), 2);
    }

    /// <summary>
    /// Computes average Log-Spectral Distortion (LSD) in dB across 512-point FFT frames in the vocal band (200-3400Hz).
    /// Lower LSD means cleaner, less distorted speech reconstruction during double-talk.
    /// </summary>
    public static double ComputeLogSpectralDistortionDb(
        ReadOnlySpan<short> cleanReference,
        ReadOnlySpan<short> processedSignal,
        int lookaheadDelaySamples = 0)
    {
        const int fftSize = 512;
        int count = Math.Min(cleanReference.Length, processedSignal.Length - Math.Max(0, lookaheadDelaySamples));
        int numFrames = count / fftSize;
        if (numFrames <= 0) return 0.0;

        // Vocal band bins for 512-pt FFT @ 48kHz (bin width = 93.75 Hz): bins 2..36 (~187 Hz .. 3375 Hz)
        const int minBin = 2;
        const int maxBin = 36;

        // Normalize global scale factor between cleanReference and processedSignal
        double refSumSq = 0;
        double procSumSq = 0;
        for (int i = 0; i < count; i++)
        {
            double r = cleanReference[i] / 32768.0;
            double p = processedSignal[i + Math.Max(0, lookaheadDelaySamples)] / 32768.0;
            refSumSq += r * r;
            procSumSq += p * p;
        }
        double scaleGain = procSumSq > 1e-9 ? Math.Sqrt(refSumSq / procSumSq) : 1.0;

        const int bins = (fftSize / 2) + 1;
        double totalLsd = 0.0;
        Span<float> refIn = stackalloc float[fftSize];
        Span<float> procIn = stackalloc float[fftSize];
        Span<float> refRe = stackalloc float[bins];
        Span<float> refIm = stackalloc float[bins];
        Span<float> procRe = stackalloc float[bins];
        Span<float> procIm = stackalloc float[bins];
        Span<double> binDiffs = stackalloc double[maxBin - minBin + 1];
        var window = AcousticDspMath.HannWindow512;
        var fft = AcousticDspMath.Fft512;

        for (int f = 0; f < numFrames; f++)
        {
            int baseIdx = f * fftSize;
            for (int n = 0; n < fftSize; n++)
            {
                float w = window[n];
                refIn[n] = (cleanReference[baseIdx + n] / 32768.0f) * w;
                procIn[n] = (float)((processedSignal[baseIdx + Math.Max(0, lookaheadDelaySamples) + n] / 32768.0) * scaleGain) * w;
            }

            fft.Direct(refIn, refRe, refIm);
            fft.Direct(procIn, procRe, procIm);

            double binDiffSqSum = 0.0;
            double meanDiffDb = 0.0;
            int activeBins = 0;

            for (int k = minBin; k <= maxBin; k++)
            {
                double refMag = Math.Sqrt((double)refRe[k] * refRe[k] + (double)refIm[k] * refIm[k]);
                if (refMag >= 0.015) // Evaluate spectral fidelity on active target vocal harmonic/formant bins (> -36 dBFS)
                {
                    double procMag = Math.Sqrt((double)procRe[k] * procRe[k] + (double)procIm[k] * procIm[k]);
                    double magRefDb = 20.0 * Math.Log10(refMag);
                    double magProcDb = 20.0 * Math.Log10(Math.Max(procMag, 1e-3));
                    double diff = magRefDb - magProcDb;
                    binDiffs[activeBins] = diff;
                    meanDiffDb += diff;
                    activeBins++;
                }
            }

            if (activeBins > 0)
            {
                meanDiffDb /= activeBins;
                for (int i = 0; i < activeBins; i++)
                {
                    double centered = binDiffs[i] - meanDiffDb;
                    binDiffSqSum += centered * centered;
                }
                totalLsd += Math.Sqrt(binDiffSqSum / activeBins);
            }
        }

        return Math.Round(totalLsd / numFrames, 2);
    }
}
