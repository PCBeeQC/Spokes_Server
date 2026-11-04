namespace Spokes_Server.Core.Services.Communication.Voice.Dsp;

using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using NWaves.Filters.Fda;
using NWaves.Transforms;
using NWaves.Utils;
using static Spokes_Server.Core.Services.Communication.Voice.Dsp.AcousticRoomProcessor;

/// <summary>
/// Shared SIMD math kernels, periodic COLA windows, NWaves FFT/Bark/decibel primitives,
/// and zero-allocation correlation helpers for the physical-room DSP engine.
/// </summary>
internal static class AcousticDspMath
{
    public static readonly float[] HannWindow256 = CreatePeriodicHannWindow(BarkFftSize);
    public static readonly float[] HannWindow512 = CreatePeriodicHannWindow(GccPhatFftSize);

    public static readonly float[][] VocalBarkFilterBank = FilterBanks.BarkBankSlaney(
        BarkBandCount,
        BarkFftSize,
        SampleRate,
        lowFreq: 180.0,
        highFreq: 3500.0);

    // NWaves RealFft maintains internal mutable scratch buffers; keep one instance per thread to guarantee lock-free thread safety.
    [ThreadStatic]
    private static RealFft? _threadFft512;

    public static RealFft Fft512 => _threadFft512 ??= new RealFft(GccPhatFftSize);

    /// <summary>
    /// Creates a periodic Hann window (<c>2πn / size</c>) required for exact 50%-overlap COLA unity (<c>w[n] + w[n + size/2] == 1.0</c>).
    /// </summary>
    public static float[] CreatePeriodicHannWindow(int size)
    {
        var w = new float[size];
        for (int n = 0; n < size; n++)
        {
            w[n] = 0.5f * (1.0f - MathF.Cos(2.0f * MathF.PI * n / size));
        }
        return w;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static double DotProductSimd(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        int len = a.Length;
        int i = 0;
        double sum = 0.0;
        if (Vector.IsHardwareAccelerated && len >= Vector<float>.Count)
        {
            Vector<float> vSum0 = Vector<float>.Zero;
            Vector<float> vSum1 = Vector<float>.Zero;
            int vecCount = Vector<float>.Count;
            int twoVec = vecCount * 2;
            int limit2 = len - (len % twoVec);
            for (; i < limit2; i += twoVec)
            {
                vSum0 += new Vector<float>(a.Slice(i, vecCount)) * new Vector<float>(b.Slice(i, vecCount));
                vSum1 += new Vector<float>(a.Slice(i + vecCount, vecCount)) * new Vector<float>(b.Slice(i + vecCount, vecCount));
            }
            vSum0 += vSum1;
            for (int lane = 0; lane < vecCount; lane++)
            {
                sum += vSum0[lane];
            }
        }

        for (; i < len; i++)
        {
            sum += (double)a[i] * b[i];
        }
        return sum;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static double ComputeMeanSquareSimd(ReadOnlySpan<float> samples) =>
        samples.IsEmpty ? 0.0 : DotProductSimd(samples, samples) / samples.Length;

    public static double ComputePcmMeanSquare(ReadOnlySpan<short> pcm)
    {
        if (pcm.IsEmpty) return 0.0;
        double sumSq = 0.0;
        for (int i = 0; i < pcm.Length; i++)
        {
            double s = pcm[i] / 32768.0;
            sumSq += s * s;
        }
        return sumSq / pcm.Length;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static double CenterAndSumSquares(ReadOnlySpan<float> values, float mean, Span<float> centered)
    {
        double sumSq = 0.0;
        for (int k = 0; k < values.Length; k++)
        {
            float d = values[k] - mean;
            centered[k] = d;
            sumSq += (double)d * d;
        }
        return sumSq;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static double ComputeSumSquaredDeviations(ReadOnlySpan<float> values, float mean)
    {
        double sumSq = 0.0;
        for (int k = 0; k < values.Length; k++)
        {
            double d = values[k] - mean;
            sumSq += d * d;
        }
        return sumSq;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static double ComputeCenteredPearson(
        ReadOnlySpan<float> centeredA,
        double sumSqA,
        ReadOnlySpan<float> b,
        float meanB,
        double minVar = 1e-7)
    {
        double sumSqB = 0.0;
        double cross = 0.0;
        for (int k = 0; k < centeredA.Length; k++)
        {
            float db = b[k] - meanB;
            sumSqB += (double)db * db;
            cross += (double)centeredA[k] * db;
        }
        return sumSqB > minVar ? cross / Math.Sqrt(sumSqA * sumSqB) : -1.0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static double ComputePearson(
        ReadOnlySpan<float> a,
        float meanA,
        double sumSqA,
        ReadOnlySpan<float> b,
        float meanB,
        double minVar = 1e-7)
    {
        double sumSqB = 0.0;
        double cross = 0.0;
        for (int k = 0; k < a.Length; k++)
        {
            double da = a[k] - meanA;
            double db = b[k] - meanB;
            sumSqB += db * db;
            cross += da * db;
        }
        return sumSqB > minVar ? cross / Math.Sqrt(sumSqA * sumSqB) : -1.0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static double ToDbFs(double amplitude, double floor = 1e-5, double emptyDb = -100.0) =>
        amplitude > floor ? Math.Round(Scale.ToDecibel(amplitude), 1) : emptyDb;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static double ToPowerRatioDb(double numeratorPower, double denominatorPower) =>
        Scale.ToDecibelPower(numeratorPower / denominatorPower, 1.0);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static short FloatToPcm16(float sample)
    {
        float clamped = Math.Clamp(sample * 32768.0f, short.MinValue, short.MaxValue);
        return (short)MathF.Round(clamped);
    }
}
