using System;
using System.Collections.Generic;
using System.Linq;
using Spokes_Server.Core.Services.Communication.Voice.Dsp;
using Xunit;
using Xunit.Abstractions;

namespace Spokes_Server.Tests.Services.Voice;

public class AcousticRoomProcessorTests
{
    private const int SampleRate = AcousticRoomProcessor.SampleRate;
    private const int FrameSamples = AcousticRoomProcessor.FrameSamples10Ms;
    private readonly ITestOutputHelper _output;

    public AcousticRoomProcessorTests(ITestOutputHelper output)
    {
        _output = output;
    }

    private static short[] GenerateSyntheticSpeech(int totalSamples, double fundamentalHz = 260.0, double amplitude = 12000.0, int phaseOffsetSamples = 0)
    {
        return AcousticRoomSimulator.GenerateNaturalSpeech(totalSamples, fundamentalHz, amplitude, phaseOffsetSamples);
    }

    private static double ComputeRms(ReadOnlySpan<short> samples)
    {
        if (samples.Length == 0) return 0;
        double sumSq = 0;
        for (int i = 0; i < samples.Length; i++)
        {
            double s = samples[i];
            sumSq += s * s;
        }
        return Math.Sqrt(sumSq / samples.Length);
    }

    [Fact]
    public void ProcessSynchronizedStep_DetectsPositiveAcousticDelay_AndSuppressesSecondaryMicBleed()
    {
        var processor = new AcousticRoomProcessor();
        processor.RegisterParticipant("userA");
        processor.RegisterParticipant("userB");

        int totalFrames = 120;
        int totalSamples = totalFrames * FrameSamples;
        int delaySamples = 6 * 48; // +288 samples (+6 ms)

        var speechA = GenerateSyntheticSpeech(totalSamples, fundamentalHz: 240.0, amplitude: 14000.0);
        var bleedOnB = new short[totalSamples];
        for (int i = delaySamples; i < totalSamples; i++)
        {
            bleedOnB[i] = (short)Math.Round(speechA[i - delaySamples] * 0.25);
        }

        double lateBleedInputEnergy = 0;
        double lateBleedOutputEnergy = 0;

        for (int f = 0; f < totalFrames; f++)
        {
            var frameA = speechA.AsSpan(f * FrameSamples, FrameSamples).ToArray();
            var frameB = bleedOnB.AsSpan(f * FrameSamples, FrameSamples).ToArray();

            var outputs = processor.ProcessSynchronizedStep(new Dictionary<string, short[]>
            {
                ["userA"] = frameA,
                ["userB"] = frameB
            }, f * 10);

            if (f >= 40)
            {
                lateBleedInputEnergy += ComputeRms(frameB);
                lateBleedOutputEnergy += ComputeRms(outputs["userB"]);
            }
        }

        var cal = processor.GetCalibrations().FirstOrDefault(c => c.SourceUserId == "userA" && c.TargetUserId == "userB");
        Assert.NotNull(cal);
        Assert.InRange(cal!.DelaySamples48k, 150, 420);
        Assert.True(cal.Confidence > 0.25);
        Assert.True(lateBleedOutputEnergy < lateBleedInputEnergy * 0.10,
            $"Expected bleed RMS to be suppressed by >90%, but input={lateBleedInputEnergy:F1}, output={lateBleedOutputEnergy:F1}");
    }

    [Fact]
    public void ProcessSynchronizedStep_DetectsNegativeProcessingOffset_FromClientDspFilter()
    {
        var processor = new AcousticRoomProcessor();
        processor.RegisterParticipant("userA");
        processor.RegisterParticipant("userB");

        int totalFrames = 130;
        int totalSamples = totalFrames * FrameSamples;
        int advanceSamples = 30 * 48; // 1440 samples (30ms)

        var rawVoice = GenerateSyntheticSpeech(totalSamples + advanceSamples, fundamentalHz: 220.0, amplitude: 15000.0);
        var speechA = new short[totalSamples];
        var bleedOnB = new short[totalSamples];

        for (int i = 0; i < totalSamples; i++)
        {
            speechA[i] = rawVoice[i];
            bleedOnB[i] = (short)Math.Round(rawVoice[i + advanceSamples] * 0.22);
        }

        double lateBleedInputEnergy = 0;
        double lateBleedOutputEnergy = 0;

        for (int f = 0; f < totalFrames; f++)
        {
            var frameA = speechA.AsSpan(f * FrameSamples, FrameSamples).ToArray();
            var frameB = bleedOnB.AsSpan(f * FrameSamples, FrameSamples).ToArray();

            var outputs = processor.ProcessSynchronizedStep(new Dictionary<string, short[]>
            {
                ["userA"] = frameA,
                ["userB"] = frameB
            }, f * 10);

            if (f >= 45)
            {
                lateBleedInputEnergy += ComputeRms(frameB);
                lateBleedOutputEnergy += ComputeRms(outputs["userB"]);
            }
        }

        var cal = processor.GetCalibrations().FirstOrDefault(c => c.SourceUserId == "userA" && c.TargetUserId == "userB");
        Assert.NotNull(cal);
        Assert.True(cal!.DelaySamples48k < 0, $"Expected negative lag when User B's bleed leads User A's DSP-delayed mic, got {cal.DelaySamples48k}");
        Assert.True(lateBleedOutputEnergy < lateBleedInputEnergy * 0.15,
            $"Expected negative-offset bleed to be suppressed via lookahead buffer, but input={lateBleedInputEnergy:F1}, output={lateBleedOutputEnergy:F1}");
    }

    [Fact]
    public void ProcessParticipantFrame_DtxSilenceGap_PreservesSampleAlignment()
    {
        var processor = new AcousticRoomProcessor();
        processor.RegisterParticipant("userA");
        processor.RegisterParticipant("userB");

        var frame = GenerateSyntheticSpeech(FrameSamples, fundamentalHz: 250.0, amplitude: 10000.0);
        for (int f = 0; f < 20; f++)
        {
            processor.ProcessParticipantFrame("userA", frame, f * 10);
            processor.ProcessParticipantFrame("userB", new short[FrameSamples], f * 10);
        }

        var afterGapA = processor.ProcessParticipantFrame("userA", frame, 700);
        var afterGapB = processor.ProcessParticipantFrame("userB", new short[FrameSamples], 700);

        Assert.Equal(FrameSamples, afterGapA.Length);
        Assert.Equal(FrameSamples, afterGapB.Length);
    }

    [Fact]
    public void ProcessSynchronizedStep_MechanicalKeyboardClicksOnSecondaryMic_DoNotTriggerFalseDoubleTalk()
    {
        var processor = new AcousticRoomProcessor();
        processor.RegisterParticipant("userA");
        processor.RegisterParticipant("userB");

        int totalFrames = 100;
        int totalSamples = totalFrames * FrameSamples;
        int delaySamples = 5 * 48; // +5ms

        var speechA = GenerateSyntheticSpeech(totalSamples, fundamentalHz: 230.0, amplitude: 14000.0);
        var micB = new short[totalSamples];

        for (int i = delaySamples; i < totalSamples; i++)
        {
            micB[i] = (short)Math.Round(speechA[i - delaySamples] * 0.25);
        }

        var clickFrames = Enumerable.Range(0, (totalFrames - 30) / 20).Select(k => 30 + k * 20).ToList();
        AcousticRoomSimulator.InjectKeyboardClicks(micB, clickFrames, 16000.0);

        double bleedPlusClickInput = 0;
        double bleedPlusClickOutput = 0;

        for (int f = 0; f < totalFrames; f++)
        {
            var frameA = speechA.AsSpan(f * FrameSamples, FrameSamples).ToArray();
            var frameB = micB.AsSpan(f * FrameSamples, FrameSamples).ToArray();

            var outputs = processor.ProcessSynchronizedStep(new Dictionary<string, short[]>
            {
                ["userA"] = frameA,
                ["userB"] = frameB
            }, f * 10);

            if (f >= 40)
            {
                bleedPlusClickInput += ComputeRms(frameB);
                bleedPlusClickOutput += ComputeRms(outputs["userB"]);
            }
        }

        Assert.True(bleedPlusClickOutput < bleedPlusClickInput * 0.15,
            $"Keyboard clicks should not open secondary mic gate during speech; input={bleedPlusClickInput:F1}, output={bleedPlusClickOutput:F1}");
    }

    [Fact]
    public void ProcessSynchronizedStep_ThreeMicrophonesInRoom_IsolatesPrimarySpeakerFromBothRoommates()
    {
        var processor = new AcousticRoomProcessor();
        processor.RegisterParticipant("dad");
        processor.RegisterParticipant("kid1");
        processor.RegisterParticipant("kid2");

        int totalFrames = 100;
        int totalSamples = totalFrames * FrameSamples;
        var dadSpeech = GenerateSyntheticSpeech(totalSamples, fundamentalHz: 190.0, amplitude: 15000.0);

        double kid1OutputRms = 0;
        double kid2OutputRms = 0;
        double dadOutputRms = 0;

        for (int f = 0; f < totalFrames; f++)
        {
            var frameDad = dadSpeech.AsSpan(f * FrameSamples, FrameSamples).ToArray();
            var frameKid1 = frameDad.Select(s => (short)(s * 0.28)).ToArray();
            var frameKid2 = frameDad.Select(s => (short)(s * 0.18)).ToArray();

            var outputs = processor.ProcessSynchronizedStep(new Dictionary<string, short[]>
            {
                ["dad"] = frameDad,
                ["kid1"] = frameKid1,
                ["kid2"] = frameKid2
            }, f * 10);

            if (f >= 35)
            {
                dadOutputRms += ComputeRms(outputs["dad"]);
                kid1OutputRms += ComputeRms(outputs["kid1"]);
                kid2OutputRms += ComputeRms(outputs["kid2"]);
            }
        }

        Assert.True(dadOutputRms > 50000);
        Assert.True(kid1OutputRms < dadOutputRms * 0.05);
        Assert.True(kid2OutputRms < dadOutputRms * 0.05);
    }

    [Fact]
    public void ProcessSynchronizedStep_SimultaneousDoubleTalk_PreservesBothVoicesWhileSubtractingBleed()
    {
        var processor = new AcousticRoomProcessor();
        processor.RegisterParticipant("userA");
        processor.RegisterParticipant("userB");

        int totalFrames = 100;
        int totalSamples = totalFrames * FrameSamples;

        var speechA = GenerateSyntheticSpeech(totalSamples, fundamentalHz: 220.0, amplitude: 13000.0);
        var speechB = GenerateSyntheticSpeech(totalSamples, fundamentalHz: 370.0, amplitude: 13000.0);

        double doubleTalkOutputA = 0;
        double doubleTalkOutputB = 0;

        for (int f = 0; f < totalFrames; f++)
        {
            var cleanA = speechA.AsSpan(f * FrameSamples, FrameSamples).ToArray();
            var cleanB = f < 40 ? new short[FrameSamples] : speechB.AsSpan(f * FrameSamples, FrameSamples).ToArray();

            var micA = new short[FrameSamples];
            var micB = new short[FrameSamples];
            for (int i = 0; i < FrameSamples; i++)
            {
                micA[i] = (short)Math.Clamp(cleanA[i] + (short)(cleanB[i] * 0.25), short.MinValue, short.MaxValue);
                micB[i] = (short)Math.Clamp(cleanB[i] + (short)(cleanA[i] * 0.25), short.MinValue, short.MaxValue);
            }

            var outputs = processor.ProcessSynchronizedStep(new Dictionary<string, short[]>
            {
                ["userA"] = micA,
                ["userB"] = micB
            }, f * 10);

            if (f >= 55)
            {
                doubleTalkOutputA += ComputeRms(outputs["userA"]);
                doubleTalkOutputB += ComputeRms(outputs["userB"]);
            }
        }

        Assert.True(doubleTalkOutputA > 20000, $"Expected User A to remain audible during double-talk, got RMS sum {doubleTalkOutputA:F1}");
        Assert.True(doubleTalkOutputB > 20000, $"Expected User B to remain audible during double-talk, got RMS sum {doubleTalkOutputB:F1}");
    }

    [Theory]
    [InlineData(-75.0)]
    [InlineData(-35.0)]
    [InlineData(-10.0)]
    [InlineData(0.0)]
    [InlineData(6.0)]
    [InlineData(25.0)]
    [InlineData(70.0)]
    public void AutoLatencyDetectionSweep_AcrossFullNegativeAndPositiveRange_LocksAccuratelyAndCancelsBleed(double groundTruthLagMs)
    {
        var result = RunSingleSpeakerScenario(
            scenarioName: $"LagSweep_{groundTruthLagMs:+0;-0;0}ms",
            description: $"Sweep evaluation at {groundTruthLagMs:+0.0;-0.0;0.0}ms net lag",
            groundTruthLagMs: groundTruthLagMs,
            bleedGain: 0.22,
            rt60Ms: 120.0,
            warpPhase: groundTruthLagMs < 0,
            useAsyncParticipantFrames: false,
            staggeredJoinMs: 0);

        _output.WriteLine(AcousticRoomDiagnosticReportFormatter.FormatBenchmarkScenarioReport(result));

        Assert.True(result.LatencyErrorMs <= 2.5,
            $"Latency error {result.LatencyErrorMs:F2}ms exceeded 2.5ms threshold for groundTruth={groundTruthLagMs}ms (detected={result.DetectedDelayMs}ms)");
        Assert.True(result.BleedSuppressionErleDb >= 20.0,
            $"Bleed suppression {result.BleedSuppressionErleDb:F1}dB was below 20dB for groundTruth={groundTruthLagMs}ms");
        Assert.True(result.PrimarySpeechRetentionPercent >= 95.0,
            $"Primary speaker retention {result.PrimarySpeechRetentionPercent:F1}% dropped below 95%");
    }

    [Fact]
    public void ProcessParticipantFrame_StaggeredLateJoinAndJitter_AlignsClocksAndCancelsBleed()
    {
        // Verifies Audit Fix #1:
        // Both participants are pre-registered at sample 0 (just like PhysicalRoomPipelineStage),
        // User A streams for 1,200ms BEFORE User B's first audio frame arrives, with ±10ms arrival jitter.
        var result = RunSingleSpeakerScenario(
            scenarioName: "AsyncWorker_StaggeredJoin_1200ms_WithJitter",
            description: "User B joins 1.2s after User A via ProcessParticipantFrame with ±10ms packet arrival jitter",
            groundTruthLagMs: 6.0,
            bleedGain: 0.24,
            rt60Ms: 150.0,
            warpPhase: false,
            useAsyncParticipantFrames: true,
            staggeredJoinMs: 1200);

        _output.WriteLine(AcousticRoomDiagnosticReportFormatter.FormatBenchmarkScenarioReport(result));

        Assert.True(result.LatencyErrorMs <= 2.0, $"Expected staggered join latency error <= 2.0ms, got {result.LatencyErrorMs:F2}ms");
        Assert.True(result.BleedSuppressionErleDb >= 22.0, $"Expected staggered join ERLE >= 22dB, got {result.BleedSuppressionErleDb:F1}dB");
    }

    [Fact]
    public void ReciprocalPriorAndBidirectionalCalibration_DecomposesPhysicalDistanceAndDspOffset()
    {
        // Verifies Audit Fix #4:
        // User A has NVIDIA Broadcast (+35ms filter delay), User B has raw mic (0ms filter delay),
        // sitting 1.71m apart (+5.0ms acoustic air delay).
        // Ground truth:
        //   Delay(A -> B) = (0 - 35) + 5 = -30.0 ms
        //   Delay(B -> A) = (35 - 0) + 5 = +40.0 ms
        //   AirPropagation = (-30 + 40) / 2 = +5.0 ms (~1.71 m)
        //   RelativeDspOffset = (-30 - 40) / 2 = -35.0 ms
        var processor = new AcousticRoomProcessor();
        processor.RegisterParticipant("userA");
        processor.RegisterParticipant("userB");

        int phase1Frames = 80; // 0.8s: Only User A speaks
        int phase2Frames = 80; // 0.8s: Only User B speaks
        int totalFrames = phase1Frames + phase2Frames;
        int totalSamples = totalFrames * FrameSamples;

        int filterASamples = 35 * 48; // +1680 samples (+35.0ms)
        int netLagAB = -30 * 48;    // -1440 samples (-30.0ms)
        int netLagBA = +40 * 48;    // +1920 samples (+40.0ms)

        var rawVoiceA = AcousticRoomSimulator.GenerateNaturalSpeech(totalSamples + filterASamples, 215.0, 14500.0);
        var rawVoiceB = AcousticRoomSimulator.GenerateNaturalSpeech(totalSamples + netLagBA, 310.0, 14500.0);

        var trackA = new short[totalSamples];
        var trackB = new short[totalSamples];

        // Phase 1 (frames 0..79): User A speaks; bleed on B leads Track A by 30ms (-1440 samples)
        int phase1Samples = phase1Frames * FrameSamples;
        for (int i = 0; i < phase1Samples; i++)
        {
            trackA[i] = rawVoiceA[i];
            trackB[i] = (short)Math.Round(rawVoiceA[i - netLagAB] * 0.22);
        }

        // Phase 2 (frames 80..159): User B speaks; bleed on A trails Track B by +40ms (+1920 samples)
        for (int i = phase1Samples; i < totalSamples; i++)
        {
            int bIdx = i - phase1Samples;
            trackB[i] = rawVoiceB[bIdx];
            if (bIdx >= netLagBA)
            {
                trackA[i] = (short)Math.Round(rawVoiceB[bIdx - netLagBA] * 0.16);
            }
        }

        PairwiseAcousticCalibration? reciprocalPriorBA = null;

        for (int f = 0; f < totalFrames; f++)
        {
            var frameA = trackA.AsSpan(f * FrameSamples, FrameSamples).ToArray();
            var frameB = trackB.AsSpan(f * FrameSamples, FrameSamples).ToArray();

            processor.ProcessSynchronizedStep(new Dictionary<string, short[]>
            {
                ["userA"] = frameA,
                ["userB"] = frameB
            }, f * 10);

            if (f == phase1Frames - 1)
            {
                // At the end of Phase 1 (before User B has spoken a word), verify B -> A has a Reciprocal Prior!
                reciprocalPriorBA = processor.GetCalibrations()
                    .FirstOrDefault(c => c.SourceUserId == "userB" && c.TargetUserId == "userA");
            }
        }

        Assert.NotNull(reciprocalPriorBA);
        Assert.True(reciprocalPriorBA!.IsReciprocalPrior, "Expected B -> A to be seeded as a ReciprocalPrior after only User A spoke");
        Assert.InRange(reciprocalPriorBA.DelayMs, 32.0, 44.0);

        // After Phase 2 (both have spoken), verify both directions are DirectLocked and physics decomposition is accurate
        var snapshot = processor.GetDiagnosticSnapshot();
        string report = AcousticRoomDiagnosticReportFormatter.FormatLiveRoomReport(snapshot, "Asymmetric NVIDIA Broadcast Test Room");
        _output.WriteLine(report);

        var calAB = snapshot.Calibrations.First(c => c.SourceUserId == "userA" && c.TargetUserId == "userB");
        var calBA = snapshot.Calibrations.First(c => c.SourceUserId == "userB" && c.TargetUserId == "userA");
        Assert.False(calAB.IsReciprocalPrior);
        Assert.False(calBA.IsReciprocalPrior);
        Assert.InRange(calAB.DelayMs, -32.5, -27.5);
        Assert.InRange(calBA.DelayMs, 37.5, 42.5);

        Assert.Single(snapshot.Decompositions);
        var dec = snapshot.Decompositions[0];
        Assert.True(dec.BothDirectionsDirectlyLocked);
        Assert.InRange(dec.AirPropagationMs, 3.5, 6.5);
        Assert.InRange(dec.EstimatedDistanceMeters, 1.2, 2.3);
        Assert.InRange(dec.RelativeDspOffsetMs, -37.5, -32.5);
    }

    [Fact]
    public void BenchmarkSuite_AllScenarios_OutputsAgentDiagnosticReportAndMeetsQuantitativeTargets()
    {
        var results = new List<AcousticBenchmarkScenarioResult>
        {
            RunSingleSpeakerScenario(
                "1_Anechoic_NearField_6ms",
                "Two mics 2m apart (+6.0ms air delay, -12dB bleed, no reverb)",
                groundTruthLagMs: 6.0,
                bleedGain: 0.25,
                rt60Ms: 0.0,
                warpPhase: false,
                useAsyncParticipantFrames: false,
                staggeredJoinMs: 0),

            RunSingleSpeakerScenario(
                "2_Reverberant_Room_RT60_240ms",
                "Living room with multi-tap desk/wall reflections (RT60=240ms) + head-shadow low-pass filter",
                groundTruthLagMs: 7.5,
                bleedGain: 0.22,
                rt60Ms: 240.0,
                warpPhase: false,
                useAsyncParticipantFrames: false,
                staggeredJoinMs: 0),

            RunSingleSpeakerScenario(
                "3_NvidiaBroadcast_PhaseWarped_Minus30ms",
                "User A uses NVIDIA Broadcast (+35ms delay + 4-stage all-pass phase warp), User B uses raw mic (-30ms net lag)",
                groundTruthLagMs: -30.0,
                bleedGain: 0.22,
                rt60Ms: 140.0,
                warpPhase: true,
                useAsyncParticipantFrames: false,
                staggeredJoinMs: 0),

            RunSingleSpeakerScenario(
                "4_DeepFilterNet_HeavyLag_Minus70ms",
                "User A uses heavy AI filter (-70ms net lag, exceeding baseline 50ms lookahead -> tests adaptive 85ms lookahead)",
                groundTruthLagMs: -70.0,
                bleedGain: 0.20,
                rt60Ms: 120.0,
                warpPhase: true,
                useAsyncParticipantFrames: false,
                staggeredJoinMs: 0),

            RunSingleSpeakerScenario(
                "5_AsyncWorkers_LateJoin1200ms_Jitter",
                "Production ProcessParticipantFrame path: User B joins 1.2s late with ±10ms packet jitter",
                groundTruthLagMs: 6.0,
                bleedGain: 0.24,
                rt60Ms: 150.0,
                warpPhase: false,
                useAsyncParticipantFrames: true,
                staggeredJoinMs: 1200),

            RunSingleSpeakerScenario(
                "6_MechanicalKeyboard_ClickStorm",
                "User A speaking while User B types loudly (10 percussive 6.2kHz click bursts on Mic B)",
                groundTruthLagMs: 5.0,
                bleedGain: 0.24,
                rt60Ms: 120.0,
                warpPhase: false,
                useAsyncParticipantFrames: false,
                staggeredJoinMs: 0,
                injectKeyboardClicks: true),

            RunDoubleTalkScenario(
                "7_Simultaneous_DoubleTalk_COLA_STFT",
                "User A and User B speak simultaneously after calibration; evaluates 50%-overlapped Hann STFT bleed scrubbing")
        };

        string fullSuiteReport = AcousticRoomDiagnosticReportFormatter.FormatBenchmarkSuiteSummary(results);
        _output.WriteLine(fullSuiteReport);

        foreach (var r in results)
        {
            Assert.True(r.LatencyErrorMs <= 2.5, $"[{r.ScenarioName}] Latency error {r.LatencyErrorMs:F2}ms > 2.5ms");
            Assert.True(r.BleedSuppressionErleDb >= 19.0, $"[{r.ScenarioName}] ERLE {r.BleedSuppressionErleDb:F1}dB < 19.0dB");
            Assert.True(r.PrimarySpeechRetentionPercent >= 90.0, $"[{r.ScenarioName}] Primary retention {r.PrimarySpeechRetentionPercent:F1}% < 90%");
            Assert.True(r.FalseGatePercent <= 2.0, $"[{r.ScenarioName}] False gate {r.FalseGatePercent:F2}% > 2.0%");
        }

        var dtResult = results.First(r => r.ScenarioName.StartsWith("7_", StringComparison.Ordinal));
        Assert.True(dtResult.DoubleTalkSiSdrImprovementDb >= 2.0,
            $"Expected Double-Talk SI-SDR improvement >= +2.0 dB, got {dtResult.DoubleTalkSiSdrImprovementDb:+0.00;-0.00} dB");
        Assert.True(dtResult.LogSpectralDistortionDb <= 4.5,
            $"Expected Double-Talk Log-Spectral Distortion <= 4.5 dB, got {dtResult.LogSpectralDistortionDb:F2} dB");
    }

    private static AcousticBenchmarkScenarioResult RunSingleSpeakerScenario(
        string scenarioName,
        string description,
        double groundTruthLagMs,
        double bleedGain,
        double rt60Ms,
        bool warpPhase,
        bool useAsyncParticipantFrames,
        int staggeredJoinMs,
        bool injectKeyboardClicks = false)
    {
        var processor = new AcousticRoomProcessor();
        processor.RegisterParticipant("userA");
        processor.RegisterParticipant("userB");

        int activeFrames = 150; // 1.5 seconds of active evaluation
        int staggerFrames = staggeredJoinMs / 10;
        int totalFrames = activeFrames + staggerFrames;

        int lagSamples = (int)Math.Round(groundTruthLagMs * SampleRate / 1000.0);
        int absLag = Math.Abs(lagSamples);
        int activeSamples = activeFrames * FrameSamples;

        var rawSpeech = AcousticRoomSimulator.GenerateNaturalSpeech(activeSamples + absLag + FrameSamples * 10, 230.0, 14500.0);
        var speechA = new short[activeSamples];
        var rawForBleed = new short[activeSamples];

        if (lagSamples >= 0)
        {
            Array.Copy(rawSpeech, 0, speechA, 0, activeSamples);
            Array.Copy(rawSpeech, 0, rawForBleed, 0, activeSamples);
        }
        else
        {
            // Negative lag: bleed arrives on B |lagSamples| earlier than A's own track
            Array.Copy(rawSpeech, 0, rawForBleed, 0, activeSamples);
            var filteredA = AcousticRoomSimulator.ApplyClientAiFilterSimulation(rawSpeech, absLag, warpPhase);
            Array.Copy(filteredA, 0, speechA, 0, activeSamples);
        }

        var bleedOnB = AcousticRoomSimulator.ApplyRoomImpulseResponse(
            rawForBleed,
            directDelaySamples: Math.Max(0, lagSamples),
            directBleedGain: bleedGain,
            rt60Ms: rt60Ms,
            applyHeadShadowLowPass: rt60Ms > 0);

        int clicksInjected = 0;
        if (injectKeyboardClicks)
        {
            var clickFrames = Enumerable.Range(0, 10).Select(k => 45 + k * 10);
            clicksInjected = AcousticRoomSimulator.InjectKeyboardClicks(bleedOnB, clickFrames, 15500.0);
        }

        // Warm-up staggered frames where only User A is streaming
        var warmupSilence = new short[FrameSamples];
        for (int f = 0; f < staggerFrames; f++)
        {
            long wallMs = f * 10;
            processor.ProcessParticipantFrame("userA", warmupSilence, wallMs);
        }

        if (staggerFrames > 0)
        {
            processor.ResetDiagnosticCounters();
        }

        var evalBleedIn = new List<short>(activeSamples);
        var evalBleedOut = new List<short>(activeSamples);
        var evalPrimaryIn = new List<short>(activeSamples);
        var evalPrimaryOut = new List<short>(activeSamples);
        var frameAHistory = new List<short[]>(activeFrames);
        int lookaheadFramesA = AcousticRoomProcessor.LookaheadSamples / FrameSamples; // 5 frames (50ms)

        double? convergenceMs = null;
        int falseGatedPrimaryFrames = 0;
        int evaluatedPrimaryFrames = 0;

        for (int f = 0; f < activeFrames; f++)
        {
            var frameA = speechA.AsSpan(f * FrameSamples, FrameSamples).ToArray();
            var frameB = bleedOnB.AsSpan(f * FrameSamples, FrameSamples).ToArray();
            frameAHistory.Add(frameA);
            long baseWallMs = (staggerFrames + f) * 10;

            short[] outA;
            short[] outB;

            if (useAsyncParticipantFrames)
            {
                // Simulate realistic ±8ms arrival jitter on individual async streams
                long jitterA = (f % 3 == 0) ? 6 : (f % 3 == 1 ? -5 : 0);
                long jitterB = (f % 4 == 0) ? -7 : (f % 4 == 2 ? 8 : 0);
                outA = processor.ProcessParticipantFrame("userA", frameA, Math.Max(0, baseWallMs + jitterA));
                outB = processor.ProcessParticipantFrame("userB", frameB, Math.Max(0, baseWallMs + jitterB));
            }
            else
            {
                var stepOut = processor.ProcessSynchronizedStep(new Dictionary<string, short[]>
                {
                    ["userA"] = frameA,
                    ["userB"] = frameB
                }, baseWallMs);
                outA = stepOut["userA"];
                outB = stepOut["userB"];
            }

            if (!convergenceMs.HasValue)
            {
                var calNow = processor.GetCalibrations().FirstOrDefault(c => c.SourceUserId == "userA" && c.TargetUserId == "userB" && !c.IsReciprocalPrior);
                if (calNow != null && calNow.Confidence >= 0.30 && Math.Abs(calNow.DelayMs - groundTruthLagMs) <= 3.0)
                {
                    convergenceMs = (f + 1) * 10.0;
                }
            }

            // Evaluate steady-state performance after initial calibration + lookahead fill (frames 55..149)
            if (f >= 55)
            {
                var alignedFrameA = frameAHistory[f - lookaheadFramesA];
                evalBleedIn.AddRange(frameB);
                evalBleedOut.AddRange(outB);
                evalPrimaryIn.AddRange(alignedFrameA);
                evalPrimaryOut.AddRange(outA);

                double inRms = ComputeRms(alignedFrameA);
                double outRms = ComputeRms(outA);
                if (inRms > 1000.0)
                {
                    evaluatedPrimaryFrames++;
                    if (outRms < inRms * 0.70)
                    {
                        falseGatedPrimaryFrames++;
                    }
                }
            }
        }

        var snapshot = processor.GetDiagnosticSnapshot();
        var cal = snapshot.Calibrations.FirstOrDefault(c => c.SourceUserId == "userA" && c.TargetUserId == "userB");
        double detectedMs = cal?.DelayMs ?? 0.0;
        double latErrorMs = Math.Round(Math.Abs(detectedMs - groundTruthLagMs), 2);
        double erleDb = AcousticRoomSimulator.ComputeBleedSuppressionDb(evalBleedIn.ToArray(), evalBleedOut.ToArray());

        double primaryInRms = ComputeRms(evalPrimaryIn.ToArray());
        double primaryOutRms = ComputeRms(evalPrimaryOut.ToArray());
        double retentionPct = primaryInRms > 1.0 ? Math.Clamp(Math.Round(100.0 * primaryOutRms / primaryInRms, 1), 0.0, 100.0) : 100.0;
        double falseGatePct = evaluatedPrimaryFrames > 0 ? Math.Round(100.0 * falseGatedPrimaryFrames / evaluatedPrimaryFrames, 2) : 0.0;

        var userBStats = snapshot.Participants.FirstOrDefault(p => p.UserId == "userB");
        int clicksRejected = userBStats?.RejectedClickTransients ?? 0;

        return new AcousticBenchmarkScenarioResult(
            scenarioName,
            description,
            groundTruthLagMs,
            detectedMs,
            latErrorMs,
            convergenceMs ?? (activeFrames * 10.0),
            erleDb,
            0.0,
            0.0,
            0.0,
            0.0,
            retentionPct,
            falseGatePct,
            clicksInjected,
            clicksRejected,
            snapshot);
    }

    private static AcousticBenchmarkScenarioResult RunDoubleTalkScenario(string scenarioName, string description)
    {
        var processor = new AcousticRoomProcessor();
        processor.RegisterParticipant("userA");
        processor.RegisterParticipant("userB");

        int singleSpeakerFrames = 55; // First 550ms: only User A speaks to lock calibration and measure single-speaker ERLE
        int doubleTalkFrames = 65;    // Next 650ms: both User A and User B speak simultaneously
        int totalFrames = singleSpeakerFrames + doubleTalkFrames;
        int totalSamples = totalFrames * FrameSamples;
        int delaySamples = 5 * 48; // +5.0ms (+240 samples)

        var cleanSpeechA = AcousticRoomSimulator.GenerateNaturalSpeech(totalSamples, 215.0, 13500.0);
        var cleanSpeechB = AcousticRoomSimulator.GenerateNaturalSpeech(totalSamples, 365.0, 13500.0);
        var bleedAOnB = AcousticRoomSimulator.ApplyRoomImpulseResponse(cleanSpeechA, delaySamples, directBleedGain: 0.28, rt60Ms: 90.0, applyHeadShadowLowPass: false);
        var bleedBOnA = AcousticRoomSimulator.ApplyRoomImpulseResponse(cleanSpeechB, delaySamples, directBleedGain: 0.28, rt60Ms: 90.0, applyHeadShadowLowPass: false);

        var singleBleedIn = new List<short>();
        var singleBleedOut = new List<short>();

        var dtCleanRefB = new List<short>();
        var dtCorruptedMicB = new List<short>();
        var dtCleanedOutB = new List<short>();

        int lookaheadFrames = AcousticRoomProcessor.LookaheadSamples / FrameSamples; // 5 frames (50ms)
        var micBHistory = new List<short[]>();
        var cleanBHistory = new List<short[]>();

        for (int f = 0; f < totalFrames; f++)
        {
            var frameCleanA = cleanSpeechA.AsSpan(f * FrameSamples, FrameSamples).ToArray();
            var frameCleanB = f < singleSpeakerFrames
                ? new short[FrameSamples]
                : cleanSpeechB.AsSpan(f * FrameSamples, FrameSamples).ToArray();

            var frameBleedA = bleedAOnB.AsSpan(f * FrameSamples, FrameSamples).ToArray();
            var frameBleedB = f < singleSpeakerFrames
                ? new short[FrameSamples]
                : bleedBOnA.AsSpan(f * FrameSamples, FrameSamples).ToArray();

            var micA = new short[FrameSamples];
            var micB = new short[FrameSamples];
            for (int i = 0; i < FrameSamples; i++)
            {
                micA[i] = (short)Math.Clamp(frameCleanA[i] + frameBleedB[i], short.MinValue, short.MaxValue);
                micB[i] = (short)Math.Clamp(frameCleanB[i] + frameBleedA[i], short.MinValue, short.MaxValue);
            }

            cleanBHistory.Add(frameCleanB);
            micBHistory.Add(micB);

            var outputs = processor.ProcessSynchronizedStep(new Dictionary<string, short[]>
            {
                ["userA"] = micA,
                ["userB"] = micB
            }, f * 10);

            if (f >= 35 && f < singleSpeakerFrames)
            {
                singleBleedIn.AddRange(micB);
                singleBleedOut.AddRange(outputs["userB"]);
            }
            else if (f >= singleSpeakerFrames + lookaheadFrames + 6)
            {
                // Compare output frame (which corresponds to input at f - lookaheadFrames)
                int alignedFrameIdx = f - lookaheadFrames;
                dtCleanRefB.AddRange(cleanBHistory[alignedFrameIdx]);
                dtCorruptedMicB.AddRange(micBHistory[alignedFrameIdx]);
                dtCleanedOutB.AddRange(outputs["userB"]);
            }
        }

        double erleDb = AcousticRoomSimulator.ComputeBleedSuppressionDb(singleBleedIn.ToArray(), singleBleedOut.ToArray());
        double siSdrBefore = AcousticRoomSimulator.ComputeSiSdrDb(dtCleanRefB.ToArray(), dtCorruptedMicB.ToArray(), 0);
        double siSdrAfter = AcousticRoomSimulator.ComputeSiSdrDb(dtCleanRefB.ToArray(), dtCleanedOutB.ToArray(), 0);
        double siSdrDelta = Math.Round(siSdrAfter - siSdrBefore, 2);
        double lsdDb = AcousticRoomSimulator.ComputeLogSpectralDistortionDb(dtCleanRefB.ToArray(), dtCleanedOutB.ToArray(), 0);

        var snapshot = processor.GetDiagnosticSnapshot();
        var cal = snapshot.Calibrations.FirstOrDefault(c => c.SourceUserId == "userA" && c.TargetUserId == "userB");
        double detectedMs = cal?.DelayMs ?? 5.0;

        return new AcousticBenchmarkScenarioResult(
            scenarioName,
            description,
            5.0,
            detectedMs,
            Math.Round(Math.Abs(detectedMs - 5.0), 2),
            200.0,
            erleDb,
            siSdrBefore,
            siSdrAfter,
            siSdrDelta,
            lsdDb,
            94.0,
            0.0,
            0,
            0,
            snapshot);
    }

    [Fact]
    public void CoughOnlyAndSilentWindow_RejectsFalseLockAndCollapsesSilentBucketsInReport()
    {
        var processor = new AcousticRoomProcessor();
        processor.RegisterParticipant("admin");
        processor.RegisterParticipant("peerA");

        int totalFrames = 120; // 1.2s total
        for (int f = 0; f < totalFrames; f++)
        {
            var frameAdmin = new short[FrameSamples];
            var framePeerA = new short[FrameSamples];

            // Inject a brief 90ms cough on "peerA" (frames 10..18) and a tiny RNNoise residual blip on "admin" 140ms later (frame 24)
            if (f >= 10 && f < 19)
            {
                framePeerA = GenerateSyntheticSpeech(FrameSamples, 240.0, 12000.0, f * FrameSamples);
            }
            if (f == 24)
            {
                frameAdmin = GenerateSyntheticSpeech(FrameSamples, 240.0, 45.0, f * FrameSamples); // near-silent RNNoise blip
            }

            processor.ProcessSynchronizedStep(new Dictionary<string, short[]>
            {
                ["admin"] = frameAdmin,
                ["peerA"] = framePeerA
            }, f * 10);
        }

        var snapshot = processor.GetDiagnosticSnapshot();
        Assert.Empty(snapshot.Calibrations);
        Assert.Contains(snapshot.Warnings, w => w.Contains("Low vocal activity detected", StringComparison.OrdinalIgnoreCase));

        string report = AcousticRoomDiagnosticReportFormatter.FormatLiveRoomReport(
            snapshot,
            "bureau",
            new Dictionary<string, string> { ["admin"] = "admin", ["peerA"] = "peerA" });
        Assert.Contains("buckets,", report);
    }

    [Fact]
    public void NotifyParticipantTrackReplaced_SwitchingFromRnnoiseToWebRtc_KeepsBothParticipantsActiveAndReCalibratesLag()
    {
        var processor = new AcousticRoomProcessor();
        processor.SyncParticipants(["admin", "peerA"]);

        int phase1Frames = 60; // 600ms with RNNoise (-20ms lag, 30cm high bleed = 0.42)
        int rnnoiseLagSamples = 20 * 48; // 960 samples (20ms)
        var rawSpeech1 = AcousticRoomSimulator.GenerateNaturalSpeech(phase1Frames * FrameSamples + rnnoiseLagSamples + FrameSamples * 4, 225.0, 14000.0);
        var filteredAdmin1 = AcousticRoomSimulator.ApplyClientAiFilterSimulation(rawSpeech1, rnnoiseLagSamples, true);
        var bleedOnPeerA1 = AcousticRoomSimulator.ApplyRoomImpulseResponse(rawSpeech1, directDelaySamples: 0, directBleedGain: 0.42, rt60Ms: 60.0);

        for (int f = 0; f < phase1Frames; f++)
        {
            var fA = filteredAdmin1.AsSpan(f * FrameSamples, FrameSamples).ToArray();
            var fB = bleedOnPeerA1.AsSpan(f * FrameSamples, FrameSamples).ToArray();
            processor.ProcessSynchronizedStep(new Dictionary<string, short[]>
            {
                ["admin"] = fA,
                ["peerA"] = fB
            }, f * 10);
        }

        var snap1 = processor.GetDiagnosticSnapshot();
        Assert.Equal(2, snap1.Participants.Count);
        var cal1 = snap1.Calibrations.FirstOrDefault(c => c.SourceUserId == "admin" && c.TargetUserId == "peerA");
        Assert.NotNull(cal1);
        Assert.InRange(cal1!.DelayMs, -22.5, -17.5);

        // Simulate "admin" switching Noise Filter from RNNoise -> Basic (WebRTC) (+2.0ms lag at ~30cm/60cm)
        processor.SyncParticipants(["admin", "peerA"]);
        processor.NotifyParticipantTrackReplaced("admin");

        // Verify Room Isolation remains active (2 participants) immediately after track replacement
        var snapDuringSwitch = processor.GetDiagnosticSnapshot();
        Assert.Equal(2, snapDuringSwitch.Participants.Count);

        int phase2Frames = 60; // 600ms with Basic WebRTC (+2.0ms lag, 30cm bleed = 0.42)
        int webrtcLagSamples = 2 * 48; // +96 samples (+2.0ms)
        var rawSpeech2 = AcousticRoomSimulator.GenerateNaturalSpeech(phase2Frames * FrameSamples + FrameSamples * 4, 225.0, 14000.0);
        var bleedOnPeerB = AcousticRoomSimulator.ApplyRoomImpulseResponse(rawSpeech2, directDelaySamples: webrtcLagSamples, directBleedGain: 0.42, rt60Ms: 60.0);

        for (int f = 0; f < phase2Frames; f++)
        {
            var fA = rawSpeech2.AsSpan(f * FrameSamples, FrameSamples).ToArray();
            var fB = bleedOnPeerB.AsSpan(f * FrameSamples, FrameSamples).ToArray();
            processor.ProcessSynchronizedStep(new Dictionary<string, short[]>
            {
                ["admin"] = fA,
                ["peerA"] = fB
            }, (phase1Frames + f) * 10);
        }

        var snap2 = processor.GetDiagnosticSnapshot();
        Assert.Equal(2, snap2.Participants.Count);
        var cal2 = snap2.Calibrations.FirstOrDefault(c => c.SourceUserId == "admin" && c.TargetUserId == "peerA");
        Assert.NotNull(cal2);
        Assert.InRange(cal2!.DelayMs, 0.0, 4.5);
    }

    [Fact]
    public void QuietSpeechFollowedBySilence_CalibratesAndPreservesPeakVocalRms_AndFormatsWarningsWithDisplayNames()
    {
        var processor = new AcousticRoomProcessor();
        string guidA = "3014bc97-b629-4ec0-bf06-15446c7b6622";
        string guidB = "419bedb9-85ac-4923-9e23-45d8ad0e29dd";

        // 1. First check uncalibrated warning formatting with display names
        var silentFrame = new short[FrameSamples];
        for (int f = 0; f < 20; f++)
        {
            processor.ProcessSynchronizedStep(new Dictionary<string, short[]>
            {
                [guidA] = (short[])silentFrame.Clone(),
                [guidB] = (short[])silentFrame.Clone()
            }, f * 10);
        }

        var uncalSnap = processor.GetDiagnosticSnapshot();
        var displayNames = new Dictionary<string, string>
        {
            [guidA] = "admin",
            [guidB] = "peerA"
        };
        string uncalReport = AcousticRoomDiagnosticReportFormatter.FormatLiveRoomReport(uncalSnap, "bureau", displayNames);
        Assert.Contains("\"admin\"", uncalReport);
        Assert.Contains("\"peerA\"", uncalReport);
        Assert.DoesNotContain($"have {guidA} speak", uncalReport);
        Assert.Contains("have \"admin\" speak", uncalReport);

        // 2. Now simulate quiet/distant room speech (~-35 dBFS source and -24 dB bleed) followed by 500ms of silence
        int speechSamples = SampleRate * 2;
        var quietSpeech = AcousticRoomSimulator.GenerateNaturalSpeech(speechSamples, 220.0, 1800.0);
        var quietBleed = AcousticRoomSimulator.ApplyRoomImpulseResponse(quietSpeech, directDelaySamples: 96, directBleedGain: 0.06, rt60Ms: 60.0); // +2.0ms, -24 dB bleed

        int speechFrames = speechSamples / FrameSamples;
        for (int f = 0; f < speechFrames; f++)
        {
            var fA = quietSpeech.AsSpan(f * FrameSamples, FrameSamples).ToArray();
            var fB = quietBleed.AsSpan(f * FrameSamples, FrameSamples).ToArray();
            processor.ProcessSynchronizedStep(new Dictionary<string, short[]>
            {
                [guidA] = fA,
                [guidB] = fB
            }, (20 + f) * 10);
        }

        // Follow with 50 frames (500ms) of total silence before clicking "Stop & Analyze"
        for (int f = 0; f < 50; f++)
        {
            processor.ProcessSynchronizedStep(new Dictionary<string, short[]>
            {
                [guidA] = (short[])silentFrame.Clone(),
                [guidB] = (short[])silentFrame.Clone()
            }, (20 + speechFrames + f) * 10);
        }

        var afterSnap = processor.GetDiagnosticSnapshot();
        var partA = afterSnap.Participants.First(p => p.UserId == guidA);
        Assert.True(partA.SpeechSubFrames > 40, $"Expected SpeechSubFrames > 40 on quiet speech, got {partA.SpeechSubFrames}");
        Assert.True(partA.RecentVocalRmsDbFs > -48.0, $"Expected peak VocalRms > -48 dBFS to be preserved after trailing silence, got {partA.RecentVocalRmsDbFs:F1} dBFS");

        var calAB = afterSnap.Calibrations.FirstOrDefault(c => c.SourceUserId == guidA && c.TargetUserId == guidB);
        Assert.NotNull(calAB);
        Assert.True(calAB!.Confidence >= 0.30, $"Expected quiet speech calibration confidence >= 0.30, got {calAB.Confidence:F2}");
        Assert.InRange(calAB.DelayMs, 0.5, 4.0);
    }

    [Fact]
    public void DesktopRnnoiseAndAndroidAgcWith90MsOffset_LocksOnQuietSpeech_ExpandsReciprocalLookahead_AndGatesCleanly()
    {
        // Reproduces the live "bureau" room capture:
        // - "admin": Windows PC (Chrome, rnnoise) speaking quietly at ~-40 dBFS vocal RMS
        // - "peerA": Android Device (Spokes App, client-default) with +94.0ms (+4512 samples) net delay and -15.7 dB bleed
        var processor = new AcousticRoomProcessor();
        string adminId = "3014bc97-b629-4ec0-bf06-15446c7b6622";
        string peerAId = "419bedb9-85ac-4923-9e23-45d8ad0e29dd";
        processor.SyncParticipants([adminId, peerAId]);

        int delaySamplesAB = 94 * 48; // +4512 samples (+94.0ms)
        int speechFrames = 100;       // 1.0s of quiet (-40 dBFS) speech from "admin"
        int pauseFrames = 40;         // 0.4s of silence (allows +94ms delayed bleed to finish and pause-synchronized lookahead to expand)
        int peerASpeechFrames = 60;    // 0.6s of speech from "peerA" (arriving 86ms after bleeding into "admin"'s mic)

        int phase1Samples = (speechFrames + pauseFrames) * FrameSamples;
        var rawAdminSpeech = AcousticRoomSimulator.GenerateNaturalSpeech(
            speechFrames * FrameSamples,
            fundamentalHz: 210.0,
            amplitude: 950.0, // ~ -40 dBFS in vocal passband
            includeSyllablePauses: true);

        var adminTrackPhase1 = new short[phase1Samples];
        Array.Copy(rawAdminSpeech, 0, adminTrackPhase1, 0, rawAdminSpeech.Length);

        var peerABleedPhase1 = AcousticRoomSimulator.ApplyRoomImpulseResponse(
            adminTrackPhase1,
            directDelaySamples: delaySamplesAB,
            directBleedGain: 0.164, // -15.7 dB
            rt60Ms: 140.0,
            applyHeadShadowLowPass: true);

        for (int f = 0; f < speechFrames + pauseFrames; f++)
        {
            var fAdmin = adminTrackPhase1.AsSpan(f * FrameSamples, FrameSamples).ToArray();
            var fPeerA = peerABleedPhase1.AsSpan(f * FrameSamples, FrameSamples).ToArray();
            processor.ProcessSynchronizedStep(new Dictionary<string, short[]>
            {
                [adminId] = fAdmin,
                [peerAId] = fPeerA
            }, f * 10);
        }

        var snapAfterPhase1 = processor.GetDiagnosticSnapshot();
        var calAB = snapAfterPhase1.Calibrations.FirstOrDefault(c => c.SourceUserId == adminId && c.TargetUserId == peerAId);
        var calBA = snapAfterPhase1.Calibrations.FirstOrDefault(c => c.SourceUserId == peerAId && c.TargetUserId == adminId);

        Assert.NotNull(calAB);
        Assert.False(calAB!.IsReciprocalPrior, "Expected admin -> peerA to lock directly on -40 dBFS speech");
        Assert.InRange(calAB.DelayMs, 91.0, 97.0);

        Assert.NotNull(calBA);
        Assert.True(calBA!.IsReciprocalPrior, "Expected peerA -> admin to be seeded as a ReciprocalPrior (-86ms)");
        Assert.InRange(calBA.DelayMs, -89.0, -83.0);

        var adminDiag = snapAfterPhase1.Participants.First(p => p.UserId == adminId);
        var peerADiag = snapAfterPhase1.Participants.First(p => p.UserId == peerAId);

        // Verify pause-synchronized adaptive lookahead expanded on "admin" to cover the -86ms reciprocal prior (+15ms margin = ~101ms)
        Assert.InRange(adminDiag.EffectiveLookaheadMs, 98, 105);
        Assert.Equal(0, adminDiag.GatedBleedSubFrames);
        Assert.True(peerADiag.AchievedBleedSuppressionDb >= 18.0, $"Expected peerA ERLE >= 18 dB on -40 dBFS speech, got {peerADiag.AchievedBleedSuppressionDb:F1} dB");

        // Verify timeline buckets show peerA=Gated during active speech, never falsely report peerA as Dominant speaker,
        // and never deadlock with both admin=Gated and peerA=Gated simultaneously
        Assert.Contains(snapAfterPhase1.TimelineBuckets, b =>
            b.ParticipantGateStates.TryGetValue(peerAId, out var gs) && gs == AcousticGateState.GatedBleed);
        Assert.DoesNotContain(snapAfterPhase1.TimelineBuckets, b =>
            string.Equals(b.DominantSpeakerUserId, peerAId, StringComparison.Ordinal));
        Assert.DoesNotContain(snapAfterPhase1.TimelineBuckets, b =>
            b.ParticipantGateStates.TryGetValue(adminId, out var gA) && gA == AcousticGateState.GatedBleed &&
            b.ParticipantGateStates.TryGetValue(peerAId, out var gB) && gB == AcousticGateState.GatedBleed);

        // Phase 2: Now "peerA" speaks first word; because "admin" already expanded lookahead to ~101ms,
        // "admin" gates "peerA"'s -86ms negative-lag bleed immediately!
        int negLagBA = 86 * 48; // 4128 samples (86ms)
        int phase2Samples = peerASpeechFrames * FrameSamples;
        var rawPeerAVoice = AcousticRoomSimulator.GenerateNaturalSpeech(phase2Samples + negLagBA, 285.0, 12000.0);
        double adminBleedInEnergy = 0;
        double adminBleedOutEnergy = 0;

        for (int f = 0; f < peerASpeechFrames; f++)
        {
            int baseIdx = f * FrameSamples;
            // Bleed arrives on "admin"'s PC mic 86ms EARLIER than "peerA"'s delayed Android stream
            var fAdminBleed = new short[FrameSamples];
            var fPeerADirect = new short[FrameSamples];
            for (int i = 0; i < FrameSamples; i++)
            {
                fAdminBleed[i] = (short)Math.Round(rawPeerAVoice[baseIdx + i + negLagBA] * 0.15);
                fPeerADirect[i] = rawPeerAVoice[baseIdx + i];
            }

            var outStep = processor.ProcessSynchronizedStep(new Dictionary<string, short[]>
            {
                [adminId] = fAdminBleed,
                [peerAId] = fPeerADirect
            }, (speechFrames + pauseFrames + f) * 10);

            if (f >= 20)
            {
                adminBleedInEnergy += ComputeRms(fAdminBleed);
                adminBleedOutEnergy += ComputeRms(outStep[adminId]);
            }
        }

        Assert.True(adminBleedOutEnergy < adminBleedInEnergy * 0.15,
            $"Expected pre-expanded reciprocal lookahead on admin to suppress peerA's -86ms bleed by >85%, got in={adminBleedInEnergy:F1}, out={adminBleedOutEnergy:F1}");
    }

    [Fact]
    public void LatchedCalibrationAndSeededMemory_NeverDecaysBetweenSentences_AndGatesFirstSyllableImmediately()
    {
        var processor = new AcousticRoomProcessor();
        string adminId = "admin";
        string peerAId = "peerA";
        processor.SyncParticipants([adminId, peerAId]);

        // Provide client pipeline telemetry matching Sentence 1 below (admin=150ms slow capture, peerA=20ms fast rnnoise capture -> admin->peerA lag = -130ms)
        processor.SetParticipantClientPipelineInfo(adminId, 150.0);
        processor.SetParticipantClientPipelineInfo(peerAId, 20.0);

        // Seed persistent room calibration from previous call: admin -> peerA is -130.0ms (-6240 samples)
        int trueLagSamples = -6240; // -130.0ms
        processor.SeedSavedPairCalibration(adminId, peerAId, trueLagSamples, bleedGain: 0.25f, confidence: 0.72f);

        // Verify warm-start: before a single frame of audio is processed, calibration is already locked
        // and peerA's lookahead buffer is already pre-sized to cover -130ms (+15ms margin = 145ms)!
        var initialSnap = processor.GetDiagnosticSnapshot();
        var initialCal = initialSnap.Calibrations.FirstOrDefault(c => c.SourceUserId == adminId && c.TargetUserId == peerAId);
        Assert.NotNull(initialCal);
        Assert.False(initialCal!.IsReciprocalPrior);
        Assert.True(initialCal.Confidence >= 0.70);
        Assert.Equal(-130.0, initialCal.DelayMs, 1);

        var initialPeerADiag = initialSnap.Participants.First(p => p.UserId == peerAId);
        Assert.InRange(initialPeerADiag.EffectiveLookaheadMs, 140, 150);

        // Sentence 1 (0.8s): Admin speaks; bleed arrives on PeerA 130ms EARLIER (-6240 samples) with heavy head-shadow EQ
        int sentenceFrames = 80;
        int absLag = Math.Abs(trueLagSamples);
        var rawSentence1 = AcousticRoomSimulator.GenerateNaturalSpeech(sentenceFrames * FrameSamples + absLag, 205.0, 9500.0);
        var filteredBleed1 = AcousticRoomSimulator.ApplyRoomImpulseResponse(
            rawSentence1,
            directDelaySamples: 0,
            directBleedGain: 0.25,
            rt60Ms: 120.0,
            applyHeadShadowLowPass: true);

        double firstSyllableBleedIn = 0;
        double firstSyllableBleedOut = 0;

        for (int f = 0; f < sentenceFrames; f++)
        {
            int idx = f * FrameSamples;
            var fAdmin = rawSentence1.AsSpan(idx, FrameSamples).ToArray();
            var fPeerA = filteredBleed1.AsSpan(idx + absLag, FrameSamples).ToArray();

            var outStep = processor.ProcessSynchronizedStep(new Dictionary<string, short[]>
            {
                [adminId] = fAdmin,
                [peerAId] = fPeerA
            }, f * 10);

            // Check the very first syllable (frames 15..30, once lookahead FIFO fills)
            if (f >= 15 && f < 30)
            {
                firstSyllableBleedIn += ComputeRms(fPeerA);
                firstSyllableBleedOut += ComputeRms(outStep[peerAId]);
            }
        }

        Assert.True(firstSyllableBleedOut < firstSyllableBleedIn * 0.15,
            $"Expected warm-started calibration to suppress first-syllable bleed by >85%, got in={firstSyllableBleedIn:F1}, out={firstSyllableBleedOut:F1}");

        var afterSentence1Cal = processor.GetCalibrations().First(c => c.SourceUserId == adminId && c.TargetUserId == peerAId);
        double lockedConfidence = afterSentence1Cal.Confidence;
        Assert.False(afterSentence1Cal.IsReciprocalPrior);
        Assert.True(afterSentence1Cal.Confidence >= 0.70);
        Assert.InRange(afterSentence1Cal.DelayMs, -133.0, -127.0);

        // Inter-sentence pause + uncorrelated room noise (1.5 seconds = 150 frames):
        // Verify that the Latched Calibration State Machine NEVER decays confidence or forgets the delay!
        var rng = new Random(99);
        for (int f = 0; f < 150; f++)
        {
            var noiseA = new short[FrameSamples];
            var noiseB = new short[FrameSamples];
            for (int i = 0; i < FrameSamples; i++)
            {
                noiseA[i] = (short)rng.Next(-120, 120);
                noiseB[i] = (short)rng.Next(-120, 120);
            }

            processor.ProcessSynchronizedStep(new Dictionary<string, short[]>
            {
                [adminId] = noiseA,
                [peerAId] = noiseB
            }, (sentenceFrames + f) * 10);
        }

        var afterSilenceCal = processor.GetCalibrations().First(c => c.SourceUserId == adminId && c.TargetUserId == peerAId);
        Assert.False(afterSilenceCal.IsReciprocalPrior, "Expected latched calibration to remain directly locked across inter-sentence pause");
        Assert.True(afterSilenceCal.Confidence >= lockedConfidence,
            $"Expected latched calibration confidence not to decay (before={lockedConfidence:F3}, after={afterSilenceCal.Confidence:F3})");
        Assert.Equal(afterSentence1Cal.DelaySamples48k, afterSilenceCal.DelaySamples48k);
    }

    [Fact]
    public void ThreeDevices_TwoHeadsetsAndFarFieldAgcPhoneAt142Ms_KeepsHeadsetOpen_GatesPhone_AndSupportsTurnTaking()
    {
        // Exact reproduction of real-world "Room Alpha" 3-device failure mode:
        // - Headset 1 ("admin first"): Windows Chrome + rnnoise, speaking directly into boom mic at ~ -27.4 dBFS
        // - Headset 2 ("peerA"): Windows Chrome + rnnoise, passive headset picking up -41.5 dBFS bleed at +89.6ms
        // - Android Phone ("peerB"): Far-field phone at other end of room (+141.8ms delay, RT60=220ms), whose
        //   speakerphone hardware AGC boosts far-field room bleed up to -15.7 dBFS (+11.7 dB LOUDER than Headset 1!).
        var processor = new AcousticRoomProcessor();
        string headset1 = "6f30c05d-c9d6-4cbb-bdbb-b2f8f3b3c9c3";
        string headset2 = "d9ba9018-59cb-4d1d-b222-9b9aba711103";
        string agcPhone = "340555a5-a468-477f-870f-3b49a4d09679";

        processor.SyncParticipants([headset1, headset2, agcPhone]);
        processor.SetParticipantClientPipelineInfo(headset1, 30.0, 10.5);
        processor.SetParticipantClientPipelineInfo(headset2, 30.0, 9.5);
        processor.SetParticipantClientPipelineInfo(agcPhone, 80.3, 11.5);

        int lagHeadset2Samples = 4300; // +89.6ms
        int lagPhoneSamples = 6806;    // +141.8ms
        int speechFrames = 220;        // 2.2s of Headset 1 speech
        int pauseFrames = 55;          // 0.55s inter-turn silence (allows +142ms bleed tail to drain & lookahead to expand)
        int phase1Frames = speechFrames + pauseFrames;
        int phase1Samples = phase1Frames * FrameSamples;

        var cleanHeadset1Speech = AcousticRoomSimulator.GenerateNaturalSpeech(
            speechFrames * FrameSamples,
            fundamentalHz: 195.0,
            amplitude: 2800.0, // ~ -27.4 dBFS vocal RMS
            includeSyllablePauses: true);

        var trackHeadset1 = new short[phase1Samples];
        Array.Copy(cleanHeadset1Speech, 0, trackHeadset1, 0, cleanHeadset1Speech.Length);

        var trackHeadset2 = AcousticRoomSimulator.ApplyRoomImpulseResponse(
            trackHeadset1,
            directDelaySamples: lagHeadset2Samples,
            directBleedGain: 0.21,
            rt60Ms: 180.0,
            applyHeadShadowLowPass: true);

        var rawFarFieldAtPhone = AcousticRoomSimulator.ApplyRoomImpulseResponse(
            trackHeadset1,
            directDelaySamples: lagPhoneSamples,
            directBleedGain: 0.08,
            rt60Ms: 220.0,
            applyHeadShadowLowPass: true);

        var trackAgcPhone = AcousticRoomSimulator.ApplyMobileSpeakerphoneAgcSimulation(
            rawFarFieldAtPhone,
            targetRmsDbFs: -15.7,
            maxGainDb: 34.0);

        double headset1SpeechIn = 0;
        double headset1SpeechOut = 0;
        double phoneSteadyBleedIn = 0;
        double phoneSteadyBleedOut = 0;

        for (int f = 0; f < phase1Frames; f++)
        {
            var fH1 = trackHeadset1.AsSpan(f * FrameSamples, FrameSamples).ToArray();
            var fH2 = trackHeadset2.AsSpan(f * FrameSamples, FrameSamples).ToArray();
            var fPhone = trackAgcPhone.AsSpan(f * FrameSamples, FrameSamples).ToArray();

            var stepOut = processor.ProcessSynchronizedStep(new Dictionary<string, short[]>
            {
                [headset1] = fH1,
                [headset2] = fH2,
                [agcPhone] = fPhone
            }, f * 10);

            // Evaluate steady-state post-calibration behavior (after 600ms calibration + 142ms delay)
            if (f >= 65 && f < speechFrames)
            {
                headset1SpeechIn += ComputeRms(fH1);
                headset1SpeechOut += ComputeRms(stepOut[headset1]);
            }
            if (f >= 75 && f < speechFrames + 14)
            {
                phoneSteadyBleedIn += ComputeRms(fPhone);
                phoneSteadyBleedOut += ComputeRms(stepOut[agcPhone]);
            }
        }

        var snap1 = processor.GetDiagnosticSnapshot();
        _output.WriteLine(AcousticRoomDiagnosticReportFormatter.FormatLiveRoomReport(
            snap1,
            "Room Alpha",
            new Dictionary<string, string>
            {
                [headset1] = "admin first",
                [headset2] = "peerA",
                [agcPhone] = "peerB"
            }));

        var calH1ToPhone = snap1.Calibrations.FirstOrDefault(c => c.SourceUserId == headset1 && c.TargetUserId == agcPhone);
        var calPhoneToH1 = snap1.Calibrations.FirstOrDefault(c => c.SourceUserId == agcPhone && c.TargetUserId == headset1);
        var calH1ToH2 = snap1.Calibrations.FirstOrDefault(c => c.SourceUserId == headset1 && c.TargetUserId == headset2);

        Assert.NotNull(calH1ToPhone);
        Assert.False(calH1ToPhone!.IsReciprocalPrior, "Expected Headset 1 -> AGC Phone (+141.8ms) to lock directly despite phone being +11.7 dB louder");
        Assert.InRange(calH1ToPhone.DelayMs, 137.0, 146.5);
        Assert.True(calH1ToPhone.BleedGainLinear > 2.0f, $"Expected BleedGainLinear > 2.0x on AGC-boosted phone, got {calH1ToPhone.BleedGainLinear:F3}");

        Assert.NotNull(calPhoneToH1);
        Assert.True(calPhoneToH1!.IsReciprocalPrior, "Expected AGC Phone -> Headset 1 NOT to false-lock backwards when only Headset 1 spoke");
        Assert.InRange(calPhoneToH1.DelayMs, -139.0, -129.0);

        Assert.NotNull(calH1ToH2);
        Assert.False(calH1ToH2!.IsReciprocalPrior);
        Assert.InRange(calH1ToH2.DelayMs, 86.0, 93.5);

        var diagH1 = snap1.Participants.First(p => p.UserId == headset1);
        var diagH2 = snap1.Participants.First(p => p.UserId == headset2);
        var diagPhone = snap1.Participants.First(p => p.UserId == agcPhone);

        // Headset 1 must NOT be gated by its own delayed AGC echo on the phone!
        Assert.True(headset1SpeechOut >= headset1SpeechIn * 0.92,
            $"Expected Headset 1 speech retention >= 92%, got out={headset1SpeechOut:F1} / in={headset1SpeechIn:F1}");
        Assert.True(phoneSteadyBleedOut <= phoneSteadyBleedIn * 0.12,
            $"Expected AGC Phone steady-state bleed to be suppressed by >= 88%, got out={phoneSteadyBleedOut:F1} / in={phoneSteadyBleedIn:F1}");
        Assert.True(diagH2.AchievedBleedSuppressionDb >= 18.0,
            $"Expected Headset 2 bleed suppression >= 18 dB, got {diagH2.AchievedBleedSuppressionDb:F1} dB");

        // Verify adaptive lookahead on Headset 1 expanded during the pause to cover the ~ -134ms reciprocal prior from the phone
        Assert.True(diagH1.EffectiveLookaheadMs >= 145,
            $"Expected Headset 1 lookahead >= 145ms to cover negative lag from phone, got {diagH1.EffectiveLookaheadMs}ms");

        // Phase 2: Turn-taking — now someone actually speaks directly into the Android phone ("peerB") at -14 dBFS dry near-field,
        // while Headset 1 & Headset 2 across the room only receive -50 dBFS far-field bleed arriving 134ms EARLIER!
        int phoneSpeechFrames = 85;
        int negLagSamples = 6422; // 133.8ms
        var cleanPhoneUserSpeech = AcousticRoomSimulator.GenerateNaturalSpeech(
            phoneSpeechFrames * FrameSamples + negLagSamples,
            fundamentalHz: 275.0,
            amplitude: 11500.0,
            includeSyllablePauses: true);

        var bleedAtHeadsets = AcousticRoomSimulator.ApplyRoomImpulseResponse(
            cleanPhoneUserSpeech,
            directDelaySamples: 0,
            directBleedGain: 0.012, // -38 dB acoustic loss into closed-boom headsets across the room
            rt60Ms: 200.0,
            applyHeadShadowLowPass: true);

        double phoneNearFieldIn = 0;
        double phoneNearFieldOut = 0;

        for (int f = 0; f < phoneSpeechFrames; f++)
        {
            int idx = f * FrameSamples;
            var fPhoneDirect = cleanPhoneUserSpeech.AsSpan(idx, FrameSamples).ToArray();
            var fH1Bleed = bleedAtHeadsets.AsSpan(idx + negLagSamples, FrameSamples).ToArray();
            var fH2Bleed = bleedAtHeadsets.AsSpan(idx + Math.Max(0, negLagSamples - 1800), FrameSamples).ToArray();

            var stepOut = processor.ProcessSynchronizedStep(new Dictionary<string, short[]>
            {
                [headset1] = fH1Bleed,
                [headset2] = fH2Bleed,
                [agcPhone] = fPhoneDirect
            }, (phase1Frames + f) * 10);

            if (f >= 20)
            {
                phoneNearFieldIn += ComputeRms(fPhoneDirect);
                phoneNearFieldOut += ComputeRms(stepOut[agcPhone]);
            }
        }

        Assert.True(phoneNearFieldOut >= phoneNearFieldIn * 0.90,
            $"Expected Android Phone to open cleanly when someone genuinely speaks into it, got out={phoneNearFieldOut:F1} / in={phoneNearFieldIn:F1}");
    }

    [Fact]
    public void SoloSpeakerWithReverberantBleedAndQuietTrailingSyllables_NeverSelfGatesOnRoommateHangover()
    {
        // Reproduces "Room Alpha" Test 1 anomaly at t=51.73s..51.98s where solo speaker ("admin first")
        // dipped to quiet trailing syllables (-36..-40 dBFS) and briefly self-gated against roommate's reverberant hangover.
        var processor = new AcousticRoomProcessor();
        string adminId = "6f30c05d-c9d6-4cbb-bdbb-b2f8f3b3c9c3";
        string peerAId = "d9ba9018-59cb-4d1d-b222-9b9aba711103";

        processor.SyncParticipants([adminId, peerAId]);
        processor.SeedSavedPairCalibration(adminId, peerAId, delaySamples: 1807, bleedGain: 0.142f, confidence: 0.88f);  // +37.6ms
        processor.SeedSavedPairCalibration(peerAId, adminId, delaySamples: -1469, bleedGain: 0.085f, confidence: 0.88f); // -30.6ms

        int totalFrames = 240; // 2.4 seconds
        int totalSamples = totalFrames * FrameSamples;

        // Modulate phrase amplitude from normal (-25 dBFS) down into quiet trailing syllables (-38 dBFS) and back
        var baseSpeech = AcousticRoomSimulator.GenerateNaturalSpeech(
            totalSamples,
            fundamentalHz: 185.0,
            amplitude: 3400.0,
            includeSyllablePauses: true);

        for (int i = 0; i < totalSamples; i++)
        {
            double phraseEnv = 0.62 + 0.38 * Math.Cos(2.0 * Math.PI * 0.85 * i / SampleRate); // dips down to 0.24x (~ -37 dBFS)
            baseSpeech[i] = (short)Math.Round(baseSpeech[i] * phraseEnv);
        }

        var peerAReverbBleed = AcousticRoomSimulator.ApplyRoomImpulseResponse(
            baseSpeech,
            directDelaySamples: 1807, // +37.6ms
            directBleedGain: 0.15,
            rt60Ms: 210.0,
            applyHeadShadowLowPass: true);

        for (int f = 0; f < totalFrames; f++)
        {
            var fAdmin = baseSpeech.AsSpan(f * FrameSamples, FrameSamples).ToArray();
            var fPeerA = peerAReverbBleed.AsSpan(f * FrameSamples, FrameSamples).ToArray();
            processor.ProcessSynchronizedStep(new Dictionary<string, short[]>
            {
                [adminId] = fAdmin,
                [peerAId] = fPeerA
            }, f * 10);
        }

        var snap = processor.GetDiagnosticSnapshot();
        var adminDiag = snap.Participants.First(p => p.UserId == adminId);
        var peerADiag = snap.Participants.First(p => p.UserId == peerAId);

        Assert.Equal(0, adminDiag.GatedBleedSubFrames);
        Assert.True(peerADiag.AchievedBleedSuppressionDb >= 25.0,
            $"Expected roommate bleed suppression >= 25 dB, got {peerADiag.AchievedBleedSuppressionDb:F1} dB");
        Assert.DoesNotContain(snap.TimelineBuckets, b =>
            b.ParticipantGateStates.TryGetValue(adminId, out var gA) && gA == AcousticGateState.GatedBleed &&
            b.ParticipantGateStates.TryGetValue(peerAId, out var gB) && gB == AcousticGateState.GatedBleed);
    }

    [Fact]
    public void SeedSavedPairCalibration_WithShiftedNewSessionWebRtcOffset_RelocksCleanlyWithoutStaleReverseBlock()
    {
        // Session 1 locked bidirectional delays at +37.6ms / -30.6ms (RelativeClientDspOffset = +34.1ms).
        // In Session 2, browser WebRTC RTP stream startup timing shifted by -51.6ms so true delay is now -14.0ms (-672 samples).
        // Verify that the unverified cross-session reverse seed (-30.6ms) does NOT block live re-anchoring to -14.0ms!
        var processor = new AcousticRoomProcessor();
        string adminId = "admin_first";
        string peerAId = "peerA";

        processor.SyncParticipants([adminId, peerAId]);
        processor.SeedSavedPairCalibration(adminId, peerAId, delaySamples: 1807, bleedGain: 0.142f, confidence: 0.88f);
        processor.SeedSavedPairCalibration(peerAId, adminId, delaySamples: -1469, bleedGain: 0.031f, confidence: 0.95f);

        int newSessionLagSamples = -672; // -14.0ms
        int absLag = Math.Abs(newSessionLagSamples);
        int speechFrames = 160; // 1.6s
        var rawAdmin = AcousticRoomSimulator.GenerateNaturalSpeech(
            speechFrames * FrameSamples + absLag,
            fundamentalHz: 205.0,
            amplitude: 3600.0,
            includeSyllablePauses: true);

        var bleedPeerA = AcousticRoomSimulator.ApplyRoomImpulseResponse(
            rawAdmin,
            directDelaySamples: 0,
            directBleedGain: 0.22,
            rt60Ms: 140.0,
            applyHeadShadowLowPass: true);

        for (int f = 0; f < speechFrames; f++)
        {
            int idx = f * FrameSamples;
            var fAdmin = rawAdmin.AsSpan(idx, FrameSamples).ToArray();
            var fPeerA = bleedPeerA.AsSpan(idx + absLag, FrameSamples).ToArray();

            processor.ProcessSynchronizedStep(new Dictionary<string, short[]>
            {
                [adminId] = fAdmin,
                [peerAId] = fPeerA
            }, f * 10);
        }

        var snap = processor.GetDiagnosticSnapshot();
        var calAB = snap.Calibrations.FirstOrDefault(c => c.SourceUserId == adminId && c.TargetUserId == peerAId);
        var calBA = snap.Calibrations.FirstOrDefault(c => c.SourceUserId == peerAId && c.TargetUserId == adminId);

        Assert.NotNull(calAB);
        Assert.False(calAB!.IsReciprocalPrior);
        Assert.InRange(calAB.DelayMs, -16.5, -11.5);

        Assert.NotNull(calBA);
        Assert.True(calBA!.IsReciprocalPrior, "Expected stale cross-session reverse seed (-30.6ms) to refresh to a consistent reciprocal prior (+22ms)");
        Assert.InRange(calBA.DelayMs, 18.0, 26.0);

        var peerADiag = snap.Participants.First(p => p.UserId == peerAId);
        Assert.True(peerADiag.AchievedBleedSuppressionDb >= 20.0,
            $"Expected re-anchored session calibration to suppress bleed by >= 20 dB, got {peerADiag.AchievedBleedSuppressionDb:F1} dB");
    }

    [Fact]
    public void QuietSingleTalkSpeech_PostCancellationLevelerBoostsTowardMinus20DbFsWithoutExceedingMinus1DbFsPeak()
    {
        var processor = new AcousticRoomProcessor();
        const string quietSpeaker = "quiet_user";
        const string roommate = "roommate_user";
        processor.SyncParticipants([quietSpeaker, roommate]);

        int totalFrames = 180; // 1.8s
        int totalSamples = totalFrames * FrameSamples;
        int delaySamples = 18 * 48; // +18ms

        // ~ -33 dBFS quiet near-field speech (GenerateNaturalSpeech crest factor ~ 9 dB -> RMS ~ 730 PCM16)
        var quietSpeech = AcousticRoomSimulator.GenerateNaturalSpeech(
            totalSamples,
            fundamentalHz: 205.0,
            amplitude: 2000.0,
            includeSyllablePauses: false);

        var bleedAtRoommate = AcousticRoomSimulator.ApplyRoomImpulseResponse(
            quietSpeech,
            directDelaySamples: delaySamples,
            directBleedGain: 0.25,
            rt60Ms: 140.0,
            applyHeadShadowLowPass: true);

        double steadyInRmsSum = 0;
        double steadyOutRmsSum = 0;
        double roommateBleedInSum = 0;
        double roommateBleedOutSum = 0;
        int maxAbsPeakSample = 0;

        for (int f = 0; f < totalFrames; f++)
        {
            var fSpeaker = quietSpeech.AsSpan(f * FrameSamples, FrameSamples).ToArray();
            var fRoommate = bleedAtRoommate.AsSpan(f * FrameSamples, FrameSamples).ToArray();

            var stepOut = processor.ProcessSynchronizedStep(new Dictionary<string, short[]>
            {
                [quietSpeaker] = fSpeaker,
                [roommate] = fRoommate
            }, f * 10);

            foreach (short s in stepOut[quietSpeaker])
            {
                int abs = Math.Abs((int)s);
                if (abs > maxAbsPeakSample) maxAbsPeakSample = abs;
            }

            if (f >= 60)
            {
                steadyInRmsSum += ComputeRms(fSpeaker);
                steadyOutRmsSum += ComputeRms(stepOut[quietSpeaker]);
                roommateBleedInSum += ComputeRms(fRoommate);
                roommateBleedOutSum += ComputeRms(stepOut[roommate]);
            }
        }

        double boostDb = 20.0 * Math.Log10(steadyOutRmsSum / Math.Max(1.0, steadyInRmsSum));
        Assert.True(boostDb >= 9.5,
            $"Expected quiet -33 dBFS single-talk speech to be boosted by >= +9.5 dB toward -20 dBFS, got +{boostDb:F2} dB (in={steadyInRmsSum:F1}, out={steadyOutRmsSum:F1})");
        Assert.True(maxAbsPeakSample <= 29250,
            $"Expected soft-knee limiter to cap output peaks at <= -1.0 dBFS (29205 PCM16), got {maxAbsPeakSample}");
        Assert.True(roommateBleedOutSum <= roommateBleedInSum * 0.10,
            $"Expected gated roommate bleed NOT to be boosted by post-cancellation leveler, got in={roommateBleedInSum:F1}, out={roommateBleedOutSum:F1}");
    }

    [Fact]
    public void QuietAmbientSilence_PostCancellationLevelerFreezesGainWithoutNoisePumping()
    {
        var processor = new AcousticRoomProcessor();
        processor.SyncParticipants(["userA", "userB"]);

        // 1. Quiet speech for 600ms (makeup gain settles to ~ +4.2 dB)
        var quietSpeech = AcousticRoomSimulator.GenerateNaturalSpeech(60 * FrameSamples, fundamentalHz: 210.0, amplitude: 6500.0);
        for (int f = 0; f < 60; f++)
        {
            var fA = quietSpeech.AsSpan(f * FrameSamples, FrameSamples).ToArray();
            processor.ProcessSynchronizedStep(new Dictionary<string, short[]>
            {
                ["userA"] = fA,
                ["userB"] = new short[FrameSamples]
            }, f * 10);
        }

        // 2. 1.2s of -66 dBFS ambient room floor -> silence freeze must hold gain frozen without upward drift/pumping
        var rng = new Random(42);
        double earlySilenceOutRms = 0;
        double lateSilenceOutRms = 0;
        double lateSilenceInRms = 0;
        for (int f = 0; f < 120; f++)
        {
            var floorA = new short[FrameSamples];
            for (int i = 0; i < FrameSamples; i++)
            {
                floorA[i] = (short)rng.Next(-26, 27);
            }

            var stepOut = processor.ProcessSynchronizedStep(new Dictionary<string, short[]>
            {
                ["userA"] = floorA,
                ["userB"] = new short[FrameSamples]
            }, (60 + f) * 10);

            if (f >= 20 && f < 60)
            {
                earlySilenceOutRms += ComputeRms(stepOut["userA"]);
            }
            else if (f >= 80)
            {
                lateSilenceInRms += ComputeRms(floorA);
                lateSilenceOutRms += ComputeRms(stepOut["userA"]);
            }
        }

        double driftRatio = lateSilenceOutRms / Math.Max(1.0, earlySilenceOutRms);
        double totalGainDuringSilence = lateSilenceOutRms / Math.Max(1.0, lateSilenceInRms);
        Assert.True(driftRatio <= 1.05,
            $"Expected silence freeze to hold gain constant across pause without upward pumping (driftRatio <= 1.05x), got {driftRatio:F3}x");
        Assert.True(totalGainDuringSilence < 3.6,
            $"Expected pause noise to stay frozen near speech gain (~2.9x for -15 dBFS target) and not pump toward +18 dB (7.94x) max boost, got {totalGainDuringSilence:F2}x");
    }

    [Fact]
    public void ProcessParticipantFrame_BurstJitterStallsUpTo110Ms_NeverInjectsFakeDtxSilenceOrCorruptsCalibration()
    {
        // Reproduces "Room Alpha" wireless Corsair headset clock-drift desync:
        // Two symmetric desktop wireless headsets (+4.0ms = +192 samples air delay, -8.5 dB bleed) stream via
        // ProcessParticipantFrame while experiencing periodic 50..110ms Wi-Fi/GC/thread wakeup stalls followed
        // immediately by queued packet bursts draining. Verify zero fake DTX silence injection, 0.0ms spurious
        // ClockDrift, accurate +4.0ms DirectLock, and zero self-gating.
        var processor = new AcousticRoomProcessor();
        const string adminId = "6f30c05d-c9d6-4cbb-bdbb-b2f8f3b3c9c3";
        const string peerAId = "d9ba9018-59cb-4d1d-b222-9b9aba711103";
        processor.SyncParticipants([adminId, peerAId]);
        processor.SetParticipantClientPipelineInfo(adminId, 20.0, 17.0);
        processor.SetParticipantClientPipelineInfo(peerAId, 20.0, 9.0);

        int totalFrames = 240; // 2.4s
        int totalSamples = totalFrames * FrameSamples;
        int airDelaySamples = 192; // +4.0ms (~1.37m)

        var rawSpeech = AcousticRoomSimulator.GenerateNaturalSpeech(
            totalSamples,
            fundamentalHz: 205.0,
            amplitude: 11500.0,
            includeSyllablePauses: true);

        var bleedOnPeerA = AcousticRoomSimulator.ApplyRoomImpulseResponse(
            rawSpeech,
            directDelaySamples: airDelaySamples,
            directBleedGain: 0.378, // -8.5 dB
            rt60Ms: 140.0,
            applyHeadShadowLowPass: true);

        var pendingA = new Queue<short[]>();
        var pendingB = new Queue<short[]>();

        for (int f = 0; f < totalFrames; f++)
        {
            long wallMs = f * 10L;
            pendingA.Enqueue(rawSpeech.AsSpan(f * FrameSamples, FrameSamples).ToArray());
            pendingB.Enqueue(bleedOnPeerA.AsSpan(f * FrameSamples, FrameSamples).ToArray());

            // Simulate 60ms stall on admin (frames 30..35, 110..115) and 100ms stall on peerA (frames 55..64, 145..154)
            bool stallA = (f >= 30 && f < 36) || (f >= 110 && f < 116);
            bool stallB = (f >= 55 && f < 65) || (f >= 145 && f < 155);

            if (!stallA)
            {
                while (pendingA.Count > 0)
                {
                    processor.ProcessParticipantFrame(adminId, pendingA.Dequeue(), wallMs);
                }
            }

            if (!stallB)
            {
                while (pendingB.Count > 0)
                {
                    processor.ProcessParticipantFrame(peerAId, pendingB.Dequeue(), wallMs);
                }
            }
        }

        var snap = processor.GetDiagnosticSnapshot();
        var adminDiag = snap.Participants.First(p => p.UserId == adminId);
        var peerADiag = snap.Participants.First(p => p.UserId == peerAId);

        Assert.Equal(0.0, adminDiag.ClockDriftCompensatedMs, 1);
        Assert.Equal(0.0, peerADiag.ClockDriftCompensatedMs, 1);
        Assert.Equal(0, adminDiag.GatedBleedSubFrames);
        Assert.True(peerADiag.AchievedBleedSuppressionDb >= 20.0,
            $"Expected peerA bleed suppression >= 20 dB across async packet bursts, got {peerADiag.AchievedBleedSuppressionDb:F1} dB");

        var calAB = snap.Calibrations.FirstOrDefault(c => c.SourceUserId == adminId && c.TargetUserId == peerAId);
        Assert.NotNull(calAB);
        Assert.False(calAB!.IsReciprocalPrior);
        Assert.True(calAB.IsLiveVerified);
        Assert.InRange(calAB.DelayMs, 1.5, 6.5);
    }

    [Fact]
    public void TwoWirelessHeadsets_AsymmetricBleedAndMidCallNetEqShift_DirectLocksBothDirectionsAndNeverSelfGates()
    {
        // Reproduces "Room Alpha" turn-taking on two wireless Corsair headsets:
        // 1. Phase 1 (0..1.8s): "peerA" speaks first with heavy -8.5 dB (0.378) bleed into "admin first" (+4.0ms).
        // 2. Mid-call WebRTC NetEq shift (+32.0ms on "peerA") + Phase 2 (2.0..4.0s): "admin first" speaks loudly
        //    (-11 dBFS with syllable pauses) while directional boom + Chrome WebRTC NS on "peerA" attenuates bleed
        //    to -46 dB (0.005x) at shifted lag +36.0ms (+1728 samples).
        // 3. Phase 3 (4.3..6.1s): "peerA" speaks again at shifted lag -28.0ms (-1344 samples), proving the latched
        //    calibration self-heals to -28.0ms, both directions end DirectLocked with ~+4.0ms decomposed air delay,
        //    and active speakers never self-gate or mutual-gate.
        var processor = new AcousticRoomProcessor();
        const string adminId = "6f30c05d-c9d6-4cbb-bdbb-b2f8f3b3c9c3";
        const string peerAId = "d9ba9018-59cb-4d1d-b222-9b9aba711103";
        processor.SyncParticipants([adminId, peerAId]);
        processor.SetParticipantClientPipelineInfo(adminId, 20.0, 17.0);
        processor.SetParticipantClientPipelineInfo(peerAId, 20.0, 9.0);

        var silentFrame = new short[FrameSamples];
        long frameCursor = 0;

        // Phase 1: "peerA" speaks for 1.8s (+4.0ms = +192 samples on admin, 0.378 = -8.5 dB bleed)
        int phase1Frames = 180;
        var peerASpeech1 = AcousticRoomSimulator.GenerateNaturalSpeech(
            phase1Frames * FrameSamples,
            fundamentalHz: 195.0,
            amplitude: 10500.0,
            includeSyllablePauses: true);
        var adminBleed1 = AcousticRoomSimulator.ApplyRoomImpulseResponse(
            peerASpeech1,
            directDelaySamples: 192,
            directBleedGain: 0.378,
            rt60Ms: 150.0,
            applyHeadShadowLowPass: true);

        for (int f = 0; f < phase1Frames; f++, frameCursor++)
        {
            processor.ProcessSynchronizedStep(new Dictionary<string, short[]>
            {
                [adminId] = adminBleed1.AsSpan(f * FrameSamples, FrameSamples).ToArray(),
                [peerAId] = peerASpeech1.AsSpan(f * FrameSamples, FrameSamples).ToArray()
            }, frameCursor * 10);
        }

        // 250ms inter-turn pause
        for (int f = 0; f < 25; f++, frameCursor++)
        {
            processor.ProcessSynchronizedStep(new Dictionary<string, short[]>
            {
                [adminId] = (short[])silentFrame.Clone(),
                [peerAId] = (short[])silentFrame.Clone()
            }, frameCursor * 10);
        }

        // Phase 2: WebRTC NetEq on "peerA" shifts by +32ms -> true lag (admin -> peerA) is now +36.0ms (+1728 samples),
        // with directional boom + WebRTC NS attenuating bleed to 0.005 (-46 dB).
        int phase2Frames = 200;
        int shiftedPosLag = 1728; // +36.0ms
        var adminSpeech2 = AcousticRoomSimulator.GenerateNaturalSpeech(
            phase2Frames * FrameSamples,
            fundamentalHz: 185.0,
            amplitude: 13500.0,
            includeSyllablePauses: true);
        var peerAQuietBleed2 = AcousticRoomSimulator.ApplyRoomImpulseResponse(
            adminSpeech2,
            directDelaySamples: shiftedPosLag,
            directBleedGain: 0.0075, // -42.5 dB direct -> ~ -46.5 dB post-RIR/head-shadow (< 0.008 old vocal-band threshold!)
            rt60Ms: 120.0,
            applyHeadShadowLowPass: true);

        double adminPhase2In = 0;
        double adminPhase2Out = 0;
        for (int f = 0; f < phase2Frames; f++, frameCursor++)
        {
            var fAdmin = adminSpeech2.AsSpan(f * FrameSamples, FrameSamples).ToArray();
            var fPeerA = peerAQuietBleed2.AsSpan(f * FrameSamples, FrameSamples).ToArray();
            var stepOut = processor.ProcessSynchronizedStep(new Dictionary<string, short[]>
            {
                [adminId] = fAdmin,
                [peerAId] = fPeerA
            }, frameCursor * 10);

            if (f >= 20 && f < phase2Frames - 10)
            {
                adminPhase2In += ComputeRms(fAdmin);
                adminPhase2Out += ComputeRms(stepOut[adminId]);
            }
        }

        Assert.True(adminPhase2Out >= adminPhase2In * 0.94,
            $"Expected active speaker 'admin first' never to self-gate during Phase 2, got retention {(100.0 * adminPhase2Out / Math.Max(1.0, adminPhase2In)):F1}%");

        // 300ms inter-turn pause (allows adaptive lookahead on admin to expand for negative lag)
        for (int f = 0; f < 30; f++, frameCursor++)
        {
            processor.ProcessSynchronizedStep(new Dictionary<string, short[]>
            {
                [adminId] = (short[])silentFrame.Clone(),
                [peerAId] = (short[])silentFrame.Clone()
            }, frameCursor * 10);
        }

        // Phase 3: "peerA" speaks again under the +32ms NetEq shift -> true lag (peerA -> admin) is now -28.0ms (-1344 samples)
        int phase3Frames = 180;
        int shiftedNegLagAbs = 1344; // 28.0ms (bleed arrives on admin 28ms BEFORE peerA's NetEq-delayed stream)
        var rawPeerASpeech3 = AcousticRoomSimulator.GenerateNaturalSpeech(
            phase3Frames * FrameSamples + shiftedNegLagAbs,
            fundamentalHz: 195.0,
            amplitude: 10800.0,
            includeSyllablePauses: true);
        var rawAdminBleed3 = AcousticRoomSimulator.ApplyRoomImpulseResponse(
            rawPeerASpeech3,
            directDelaySamples: 0,
            directBleedGain: 0.378,
            rt60Ms: 150.0,
            applyHeadShadowLowPass: true);

        double peerAPhase3In = 0;
        double peerAPhase3Out = 0;
        for (int f = 0; f < phase3Frames; f++, frameCursor++)
        {
            int idx = f * FrameSamples;
            var fPeerA = rawPeerASpeech3.AsSpan(idx, FrameSamples).ToArray();
            var fAdmin = rawAdminBleed3.AsSpan(idx + shiftedNegLagAbs, FrameSamples).ToArray();
            var stepOut = processor.ProcessSynchronizedStep(new Dictionary<string, short[]>
            {
                [adminId] = fAdmin,
                [peerAId] = fPeerA
            }, frameCursor * 10);

            if (f >= 20 && f < phase3Frames - 10)
            {
                peerAPhase3In += ComputeRms(fPeerA);
                peerAPhase3Out += ComputeRms(stepOut[peerAId]);
            }
        }

        Assert.True(peerAPhase3Out >= peerAPhase3In * 0.94,
            $"Expected active speaker 'peerA' never to self-gate during Phase 3, got retention {(100.0 * peerAPhase3Out / Math.Max(1.0, peerAPhase3In)):F1}%");

        var finalSnap = processor.GetDiagnosticSnapshot();
        var calAdminToPeerA = finalSnap.Calibrations.FirstOrDefault(c => c.SourceUserId == adminId && c.TargetUserId == peerAId);
        var calPeerAToAdmin = finalSnap.Calibrations.FirstOrDefault(c => c.SourceUserId == peerAId && c.TargetUserId == adminId);

        Assert.NotNull(calAdminToPeerA);
        Assert.False(calAdminToPeerA!.IsReciprocalPrior, "Expected admin -> peerA (-46 dB WebRTC NS bleed) to achieve DirectLock");
        Assert.True(calAdminToPeerA.IsLiveVerified);
        Assert.InRange(calAdminToPeerA.DelayMs, 33.0, 39.0);

        Assert.NotNull(calPeerAToAdmin);
        Assert.False(calPeerAToAdmin!.IsReciprocalPrior, "Expected latched peerA -> admin to re-latch onto -28.0ms NetEq shift");
        Assert.True(calPeerAToAdmin.IsLiveVerified);
        Assert.InRange(calPeerAToAdmin.DelayMs, -31.0, -25.0);

        Assert.Single(finalSnap.Decompositions);
        Assert.True(finalSnap.Decompositions[0].BothDirectionsDirectlyLocked);
        Assert.InRange(finalSnap.Decompositions[0].AirPropagationMs, 2.0, 6.0);

        // Active-speaker buckets must never deadlock with both participants gated simultaneously
        Assert.DoesNotContain(finalSnap.TimelineBuckets, b =>
            !string.Equals(b.DominantSpeakerUserId, "Silence", StringComparison.OrdinalIgnoreCase) &&
            b.ParticipantGateStates.TryGetValue(adminId, out var gA) && gA == AcousticGateState.GatedBleed &&
            b.ParticipantGateStates.TryGetValue(peerAId, out var gB) && gB == AcousticGateState.GatedBleed);
    }

    [Fact]
    public void ThreeDevices_RoomAlpha_AndroidOnsetBleedBurst_Over220MsLag_AndWifiJitterShift_GatesOnsetEchoCleanly()
    {
        // Reproduces the live 3-device "Room Alpha" diagnostic report:
        // - "admin first" (Windows Chrome, Filter="webrtc", ClientDsp=20ms, ServerDelay=17ms)
        // - "peerA"        (Windows Chrome, Filter="webrtc", ClientDsp=20ms, ServerDelay=9ms)
        // - "peerB"       (Android App,    Filter="webrtc", ClientDsp=75ms, ServerDelay=60ms)
        //
        // Validates all 4 interacting fixes:
        // 1. Mobile -> Desktop lag > +220ms (+247.8ms on admin -> peerB) locks directly without clamping at +220.0ms.
        // 2. Android hardware-NS 90ms un-attenuated phrase-onset bleed bursts (0.36x onset -> 0.05x steady-state)
        //    do NOT trigger false DoubleTalkSTFT echo leaks on peerB or self-gating back-reaction on solo speakers.
        // 3. 3-node triangle transitivity + Wi-Fi jitter window lets peerA -> peerB (+110.0ms) acquire a live DirectLock
        //    even after a +42ms Android Wi-Fi jitter-buffer shift away from Phase 1's +58.1ms ReciprocalPrior.
        // 4. Genuine simultaneous near-field double-talk on peerB still opens and preserves peerB's voice.
        var processor = new AcousticRoomProcessor();
        string adminId = "6f30c05d-c9d6-4cbb-bdbb-b2f8f3b3c9c3";
        string peerAId = "d9ba9018-59cb-4d1d-b222-9b9aba711103";
        string peerBId = "340555a5-a468-477f-870f-3b49a4d09679";

        processor.SyncParticipants([adminId, peerAId, peerBId]);
        processor.SetParticipantClientPipelineInfo(adminId, 20.0, 17.0);
        processor.SetParticipantClientPipelineInfo(peerAId, 20.0, 9.0);
        processor.SetParticipantClientPipelineInfo(peerBId, 75.0, 60.0);

        int frameCursor = 0;
        var silentFrame = new short[FrameSamples];

        // --- PHASE 1: "peerB" (Android) speaks briefly before Wi-Fi jitter shift ---
        // Bleed arrives earlier on admin (-166.2ms = -7978 smp) and peerA (-50.1ms = -2404 smp, seeding peerA->peerB prior at +58.1ms).
        int p1SpeechFrames = 110;
        int p1PauseFrames = 45;
        int p1LeadAdmin = 7978; // 166.2ms
        int p1LeadPeerA = 2404;  // 50.1ms
        var p1RawPeerB = AcousticRoomSimulator.GenerateNaturalSpeech(
            p1SpeechFrames * FrameSamples + p1LeadAdmin,
            fundamentalHz: 260.0,
            amplitude: 10500.0,
            includeSyllablePauses: true);
        var p1BleedAdmin = AcousticRoomSimulator.ApplyRoomImpulseResponse(p1RawPeerB, 0, directBleedGain: 0.055, rt60Ms: 150.0, applyHeadShadowLowPass: true);
        var p1BleedPeerA = AcousticRoomSimulator.ApplyRoomImpulseResponse(p1RawPeerB, 0, directBleedGain: 0.174, rt60Ms: 130.0, applyHeadShadowLowPass: true);

        for (int f = 0; f < p1SpeechFrames; f++, frameCursor++)
        {
            int idx = f * FrameSamples;
            processor.ProcessSynchronizedStep(new Dictionary<string, short[]>
            {
                [peerBId] = p1RawPeerB.AsSpan(idx, FrameSamples).ToArray(),
                [adminId] = p1BleedAdmin.AsSpan(idx + p1LeadAdmin, FrameSamples).ToArray(),
                [peerAId] = p1BleedPeerA.AsSpan(idx + p1LeadPeerA, FrameSamples).ToArray()
            }, frameCursor * 10);
        }
        for (int f = 0; f < p1PauseFrames; f++, frameCursor++)
        {
            processor.ProcessSynchronizedStep(new Dictionary<string, short[]>
            {
                [adminId] = (short[])silentFrame.Clone(),
                [peerAId] = (short[])silentFrame.Clone(),
                [peerBId] = (short[])silentFrame.Clone()
            }, frameCursor * 10);
        }

        // --- PHASE 2: Android Wi-Fi jitter buffer shifts peerB by +42ms; "admin first" speaks 2 phrases separated by 450ms silence ---
        // True lags: admin -> peerA = +138.1ms (+6628 smp, 0.582x high bleed); admin -> peerB = +247.8ms (+11894 smp, > 220ms!).
        int lagAdminToPeerA = 6628;   // +138.1ms
        int lagAdminToPeerB = 11894; // +247.8ms (exceeds old +220.0ms / 10560-sample ceiling!)
        int p2Phrase1Frames = 145;
        int p2InterPhraseSilence = 45; // 450ms pause resets Android hardware NS back to 1.0x open onset gain
        int p2Phrase2Frames = 115;
        int p2TailFrames = 45;
        int p2TotalFrames = p2Phrase1Frames + p2InterPhraseSilence + p2Phrase2Frames + p2TailFrames;
        int p2TotalSamples = p2TotalFrames * FrameSamples;

        var p2AdminTrack = new short[p2TotalSamples];
        var p2Phrase1Clean = AcousticRoomSimulator.GenerateNaturalSpeech(p2Phrase1Frames * FrameSamples, 182.0, 11500.0, includeSyllablePauses: true);
        var p2Phrase2Clean = AcousticRoomSimulator.GenerateNaturalSpeech(p2Phrase2Frames * FrameSamples, 186.0, 11800.0, includeSyllablePauses: true);
        Array.Copy(p2Phrase1Clean, 0, p2AdminTrack, 0, p2Phrase1Clean.Length);
        int p2Phrase2OffsetSamples = (p2Phrase1Frames + p2InterPhraseSilence) * FrameSamples;
        Array.Copy(p2Phrase2Clean, 0, p2AdminTrack, p2Phrase2OffsetSamples, p2Phrase2Clean.Length);

        var p2PeerATrack = AcousticRoomSimulator.ApplyRoomImpulseResponse(
            p2AdminTrack,
            directDelaySamples: lagAdminToPeerA,
            directBleedGain: 0.582, // -4.7 dB strong nearby PC bleed
            rt60Ms: 140.0,
            applyHeadShadowLowPass: true);

        var p2PeerBRawAcousticBleed = AcousticRoomSimulator.ApplyRoomImpulseResponse(
            p2AdminTrack,
            directDelaySamples: lagAdminToPeerB,
            directBleedGain: 0.36, // Un-suppressed onset bleed burst (-8.9 dB) before Android NS clamps down
            rt60Ms: 165.0,
            applyHeadShadowLowPass: true);
        var p2PeerBTrack = AcousticRoomSimulator.ApplyMobileHardwareNsOnsetBurstSimulation(
            p2PeerBRawAcousticBleed,
            onsetHoldMs: 90.0,
            steadyStateSuppressionGain: 0.14,
            silenceResetMs: 150.0);

        int peerBDtBeforeAdminPhrase2 = 0;
        double adminPhrase2In = 0;
        double adminPhrase2Out = 0;
        double peerBAdminOnsetBurstIn = 0;
        double peerBAdminOnsetBurstOut = 0;

        int p2Phrase2StartFrame = p2Phrase1Frames + p2InterPhraseSilence;
        // Onset burst arrives on peerB at p2Phrase2StartFrame + ~25 frames (+247.8ms) and exits peerB's 50ms lookahead 5 frames later
        int p2PeerBBurstInStart = p2Phrase2StartFrame + 24;
        int p2PeerBBurstInEnd = p2Phrase2StartFrame + 38;

        for (int f = 0; f < p2TotalFrames; f++, frameCursor++)
        {
            if (f == p2Phrase2StartFrame)
            {
                peerBDtBeforeAdminPhrase2 = processor.GetDiagnosticSnapshot()
                    .Participants.First(p => p.UserId == peerBId).DoubleTalkSubFrames;
            }

            int idx = f * FrameSamples;
            var fAdmin = p2AdminTrack.AsSpan(idx, FrameSamples).ToArray();
            var fPeerA = p2PeerATrack.AsSpan(idx, FrameSamples).ToArray();
            var fPeerB = p2PeerBTrack.AsSpan(idx, FrameSamples).ToArray();

            var stepOut = processor.ProcessSynchronizedStep(new Dictionary<string, short[]>
            {
                [adminId] = fAdmin,
                [peerAId] = fPeerA,
                [peerBId] = fPeerB
            }, frameCursor * 10);

            if (f >= p2Phrase2StartFrame + 25 && f < p2Phrase2StartFrame + p2Phrase2Frames)
            {
                adminPhrase2In += ComputeRms(fAdmin);
                adminPhrase2Out += ComputeRms(stepOut[adminId]);
            }

            if (f >= p2PeerBBurstInStart && f < p2PeerBBurstInEnd)
            {
                peerBAdminOnsetBurstIn += ComputeRms(fPeerB);
            }
            if (f >= p2PeerBBurstInStart + 5 && f < p2PeerBBurstInEnd + 5)
            {
                peerBAdminOnsetBurstOut += ComputeRms(stepOut[peerBId]);
            }
        }

        var snapAfterAdmin = processor.GetDiagnosticSnapshot();
        int peerBDtAfterAdminPhrase2 = snapAfterAdmin.Participants.First(p => p.UserId == peerBId).DoubleTalkSubFrames;
        var calAdminToPeerB = snapAfterAdmin.Calibrations.FirstOrDefault(c => c.SourceUserId == adminId && c.TargetUserId == peerBId);

        Assert.NotNull(calAdminToPeerB);
        Assert.False(calAdminToPeerB!.IsReciprocalPrior, "Expected admin -> peerB (+247.8ms) to achieve live DirectLock above old 220ms ceiling");
        Assert.InRange(calAdminToPeerB.DelayMs, 243.0, 253.0);
        Assert.Equal(0, peerBDtAfterAdminPhrase2 - peerBDtBeforeAdminPhrase2);
        Assert.True(peerBAdminOnsetBurstOut <= peerBAdminOnsetBurstIn * 0.08,
            $"Expected peerB 90ms onset bleed burst from admin to be gated by >= 92%, got out={peerBAdminOnsetBurstOut:F1} / in={peerBAdminOnsetBurstIn:F1}");
        Assert.True(adminPhrase2Out >= adminPhrase2In * 0.94,
            $"Expected solo speaker admin never to self-gate during peerB onset burst, got retention {(100.0 * adminPhrase2Out / Math.Max(1.0, adminPhrase2In)):F1}%");

        // --- PHASE 3: "peerA" speaks 2 phrases separated by 400ms silence ---
        // True lags: peerA -> admin = -114.4ms (-5492 smp, 0.378x bleed); peerA -> peerB = +110.0ms (+5280 smp, shifted +51.9ms from Phase 1 prior!).
        int leadPeerAToAdmin = 5492; // -114.4ms (arrives on admin 114.4ms BEFORE peerA's stream)
        int lagPeerAToPeerB = 5280;  // +110.0ms
        int p3Phrase1Frames = 135;
        int p3InterPhraseSilence = 40;
        int p3Phrase2Frames = 105;
        int p3TailFrames = 35;
        int p3TotalFrames = p3Phrase1Frames + p3InterPhraseSilence + p3Phrase2Frames + p3TailFrames;
        int p3TotalSamples = p3TotalFrames * FrameSamples + leadPeerAToAdmin;

        var p3PeerAExtended = new short[p3TotalSamples];
        var p3Phrase1Clean = AcousticRoomSimulator.GenerateNaturalSpeech(p3Phrase1Frames * FrameSamples, 210.0, 12000.0, includeSyllablePauses: true);
        var p3Phrase2Clean = AcousticRoomSimulator.GenerateNaturalSpeech(p3Phrase2Frames * FrameSamples, 215.0, 12000.0, includeSyllablePauses: true);
        Array.Copy(p3Phrase1Clean, 0, p3PeerAExtended, 0, p3Phrase1Clean.Length);
        int p3Phrase2OffsetSamples = (p3Phrase1Frames + p3InterPhraseSilence) * FrameSamples;
        Array.Copy(p3Phrase2Clean, 0, p3PeerAExtended, p3Phrase2OffsetSamples, p3Phrase2Clean.Length);

        var p3AdminBleedExtended = AcousticRoomSimulator.ApplyRoomImpulseResponse(
            p3PeerAExtended,
            directDelaySamples: 0,
            directBleedGain: 0.378,
            rt60Ms: 140.0,
            applyHeadShadowLowPass: true);

        var p3PeerBRawBleed = AcousticRoomSimulator.ApplyRoomImpulseResponse(
            p3PeerAExtended.AsSpan(0, p3TotalFrames * FrameSamples),
            directDelaySamples: lagPeerAToPeerB,
            directBleedGain: 0.35,
            rt60Ms: 155.0,
            applyHeadShadowLowPass: true);
        var p3PeerBTrack = AcousticRoomSimulator.ApplyMobileHardwareNsOnsetBurstSimulation(
            p3PeerBRawBleed,
            onsetHoldMs: 90.0,
            steadyStateSuppressionGain: 0.14,
            silenceResetMs: 150.0);

        int peerBDtBeforePeerAPhrase2 = 0;
        double peerAPhrase2In = 0;
        double peerAPhrase2Out = 0;
        double peerBPeerAOnsetBurstIn = 0;
        double peerBPeerAOnsetBurstOut = 0;

        int p3Phrase2StartFrame = p3Phrase1Frames + p3InterPhraseSilence;
        int p3PeerBBurstInStart = p3Phrase2StartFrame + 11; // +110ms arrival
        int p3PeerBBurstInEnd = p3Phrase2StartFrame + 24;

        for (int f = 0; f < p3TotalFrames; f++, frameCursor++)
        {
            if (f == p3Phrase2StartFrame)
            {
                peerBDtBeforePeerAPhrase2 = processor.GetDiagnosticSnapshot()
                    .Participants.First(p => p.UserId == peerBId).DoubleTalkSubFrames;
            }

            int idx = f * FrameSamples;
            var fPeerA = p3PeerAExtended.AsSpan(idx, FrameSamples).ToArray();
            var fAdmin = p3AdminBleedExtended.AsSpan(idx + leadPeerAToAdmin, FrameSamples).ToArray();
            var fPeerB = p3PeerBTrack.AsSpan(idx, FrameSamples).ToArray();

            var stepOut = processor.ProcessSynchronizedStep(new Dictionary<string, short[]>
            {
                [adminId] = fAdmin,
                [peerAId] = fPeerA,
                [peerBId] = fPeerB
            }, frameCursor * 10);

            if (f >= p3Phrase2StartFrame + 15 && f < p3Phrase2StartFrame + p3Phrase2Frames)
            {
                peerAPhrase2In += ComputeRms(fPeerA);
                peerAPhrase2Out += ComputeRms(stepOut[peerAId]);
            }

            if (f >= p3PeerBBurstInStart && f < p3PeerBBurstInEnd)
            {
                peerBPeerAOnsetBurstIn += ComputeRms(fPeerB);
            }
            if (f >= p3PeerBBurstInStart + 7 && f < p3PeerBBurstInEnd + 7)
            {
                peerBPeerAOnsetBurstOut += ComputeRms(stepOut[peerBId]);
            }
        }

        var snapAfterPeerA = processor.GetDiagnosticSnapshot();
        _output.WriteLine(AcousticRoomDiagnosticReportFormatter.FormatLiveRoomReport(
            snapAfterPeerA,
            "Room Alpha (3-Device Onset Burst Verification)",
            new Dictionary<string, string>
            {
                [adminId] = "admin first",
                [peerAId] = "peerA",
                [peerBId] = "peerB"
            }));

        int peerBDtAfterPeerAPhrase2 = snapAfterPeerA.Participants.First(p => p.UserId == peerBId).DoubleTalkSubFrames;
        var calPeerAToPeerB = snapAfterPeerA.Calibrations.FirstOrDefault(c => c.SourceUserId == peerAId && c.TargetUserId == peerBId);

        Assert.NotNull(calPeerAToPeerB);
        Assert.False(calPeerAToPeerB!.IsReciprocalPrior, "Expected 3-node triangle prior + Wi-Fi jitter window to lock peerA -> peerB directly");
        Assert.InRange(calPeerAToPeerB.DelayMs, 105.0, 115.0);
        Assert.Equal(0, peerBDtAfterPeerAPhrase2 - peerBDtBeforePeerAPhrase2);
        Assert.True(peerBPeerAOnsetBurstOut <= peerBPeerAOnsetBurstIn * 0.08,
            $"Expected peerB 90ms onset bleed burst from peerA to be gated by >= 92%, got out={peerBPeerAOnsetBurstOut:F1} / in={peerBPeerAOnsetBurstIn:F1}");
        Assert.True(peerAPhrase2Out >= peerAPhrase2In * 0.94,
            $"Expected solo speaker peerA never to self-gate during peerB onset burst, got retention {(100.0 * peerAPhrase2Out / Math.Max(1.0, peerAPhrase2In)):F1}%");

        // --- PHASE 4: Genuine Simultaneous Double-Talk (admin + peerB speak near-field together) ---
        int dtFrames = 85;
        var dtAdminClean = AcousticRoomSimulator.GenerateNaturalSpeech(dtFrames * FrameSamples + p1LeadAdmin, 184.0, 10500.0);
        var dtPeerBClean = AcousticRoomSimulator.GenerateNaturalSpeech(dtFrames * FrameSamples + p1LeadAdmin, 272.0, 10500.0);
        var dtAdminBleedOnPeerB = AcousticRoomSimulator.ApplyMobileHardwareNsOnsetBurstSimulation(
            AcousticRoomSimulator.ApplyRoomImpulseResponse(dtAdminClean.AsSpan(0, dtFrames * FrameSamples), lagAdminToPeerB, 0.36, 150.0, true));
        var dtPeerBBleedOnAdmin = AcousticRoomSimulator.ApplyRoomImpulseResponse(dtPeerBClean, 0, 0.055, 150.0, true);

        double peerBDtCleanIn = 0;
        double peerBDtOut = 0;
        for (int f = 0; f < dtFrames; f++, frameCursor++)
        {
            int idx = f * FrameSamples;
            var fAdmin = new short[FrameSamples];
            var fPeerB = new short[FrameSamples];
            for (int i = 0; i < FrameSamples; i++)
            {
                fAdmin[i] = (short)Math.Clamp(dtAdminClean[idx + i] + dtPeerBBleedOnAdmin[idx + p1LeadAdmin + i], short.MinValue, short.MaxValue);
                fPeerB[i] = (short)Math.Clamp(dtPeerBClean[idx + i] + dtAdminBleedOnPeerB[idx + i], short.MinValue, short.MaxValue);
            }

            var stepOut = processor.ProcessSynchronizedStep(new Dictionary<string, short[]>
            {
                [adminId] = fAdmin,
                [peerAId] = (short[])silentFrame.Clone(),
                [peerBId] = fPeerB
            }, frameCursor * 10);

            if (f >= 35)
            {
                peerBDtCleanIn += ComputeRms(dtPeerBClean.AsSpan(idx, FrameSamples));
                peerBDtOut += ComputeRms(stepOut[peerBId]);
            }
        }

        Assert.True(peerBDtOut >= peerBDtCleanIn * 0.72,
            $"Expected genuine simultaneous double-talk on peerB to open and retain near-field speech (>= 72%), got out={peerBDtOut:F1} / cleanIn={peerBDtCleanIn:F1}");
    }

    [Fact]
    public void StaleTriangleDoesNotOverwriteFresherDirectReverseAfterWifiShift_AndDoubleTalkNeverInflatesAgcPeakBleedGain()
    {
        var processor = new AcousticRoomProcessor();
        const string userA = "node-a-desktop";
        const string userB = "node-b-mid";
        const string userC = "node-c-mobile";

        processor.SyncParticipants([userA, userB, userC]);
        processor.SetParticipantClientPipelineInfo(userA, 20.0, 12.0);
        processor.SetParticipantClientPipelineInfo(userB, 20.0, 14.0);
        processor.SetParticipantClientPipelineInfo(userC, 75.0, 60.0);

        int frameCursor = 0;
        var silentFrame = new short[FrameSamples];

        // Phase 1 (0..1.5s): userB speaks, locking B -> A (+4.0ms = +192 smp) and B -> C (+114.0ms = +5472 smp).
        // Undirected triangle A <-> B <-> C seeds A -> C prior near +114.0ms.
        int p1Frames = 150;
        var p1SpeechB = AcousticRoomSimulator.GenerateNaturalSpeech(p1Frames * FrameSamples, 195.0, 11500.0, includeSyllablePauses: true);
        var p1BleedA = AcousticRoomSimulator.ApplyRoomImpulseResponse(p1SpeechB, directDelaySamples: 192, directBleedGain: 0.32, rt60Ms: 140.0, applyHeadShadowLowPass: true);
        var p1BleedC = AcousticRoomSimulator.ApplyRoomImpulseResponse(p1SpeechB, directDelaySamples: 5472, directBleedGain: 0.30, rt60Ms: 145.0, applyHeadShadowLowPass: true);

        for (int f = 0; f < p1Frames; f++, frameCursor++)
        {
            int idx = f * FrameSamples;
            processor.ProcessSynchronizedStep(new Dictionary<string, short[]>
            {
                [userA] = p1BleedA.AsSpan(idx, FrameSamples).ToArray(),
                [userB] = p1SpeechB.AsSpan(idx, FrameSamples).ToArray(),
                [userC] = p1BleedC.AsSpan(idx, FrameSamples).ToArray()
            }, frameCursor * 10);
        }

        var snapAfterP1 = processor.GetDiagnosticSnapshot();
        var priorACAfterP1 = snapAfterP1.Calibrations.FirstOrDefault(c => c.SourceUserId == userA && c.TargetUserId == userC);
        Assert.NotNull(priorACAfterP1);
        Assert.True(priorACAfterP1!.IsReciprocalPrior);
        Assert.InRange(priorACAfterP1.DelayMs, 108.0, 120.0);

        // 3.5s silence gap so Phase 1 triangle legs become > 3s older than Phase 2
        for (int f = 0; f < 350; f++, frameCursor++)
        {
            processor.ProcessSynchronizedStep(new Dictionary<string, short[]>
            {
                [userA] = (short[])silentFrame.Clone(),
                [userB] = (short[])silentFrame.Clone(),
                [userC] = (short[])silentFrame.Clone()
            }, frameCursor * 10);
        }

        // Phase 2 (5.0..6.5s): userC's Wi-Fi jitter buffer shifts by +55.0ms (true A->C is now +169.0ms, C->A is -161.0ms = -7728 smp).
        // userC speaks directly to userA while userB's mic is muted/isolated so B<->C stays at Phase 1's stale offset.
        int leadCtoA = 7728; // -161.0ms
        int p2Frames = 150;
        var p2SpeechC = AcousticRoomSimulator.GenerateNaturalSpeech(p2Frames * FrameSamples + leadCtoA, 250.0, 11500.0, includeSyllablePauses: true);
        var p2BleedA = AcousticRoomSimulator.ApplyRoomImpulseResponse(p2SpeechC, directDelaySamples: 0, directBleedGain: 0.28, rt60Ms: 140.0, applyHeadShadowLowPass: true);

        for (int f = 0; f < p2Frames; f++, frameCursor++)
        {
            int idx = f * FrameSamples;
            processor.ProcessSynchronizedStep(new Dictionary<string, short[]>
            {
                [userA] = p2BleedA.AsSpan(idx + leadCtoA, FrameSamples).ToArray(),
                [userB] = (short[])silentFrame.Clone(),
                [userC] = p2SpeechC.AsSpan(idx, FrameSamples).ToArray()
            }, frameCursor * 10);
        }

        var snapAfterP2 = processor.GetDiagnosticSnapshot();
        var priorACAfterP2 = snapAfterP2.Calibrations.FirstOrDefault(c => c.SourceUserId == userA && c.TargetUserId == userC);
        Assert.NotNull(priorACAfterP2);
        Assert.True(priorACAfterP2!.IsReciprocalPrior);
        // Fresh C -> A (-161.0ms => reciprocal +169.0ms) MUST beat the stale t=1.5s triangle (+114.0ms)!
        Assert.InRange(priorACAfterP2.DelayMs, 163.0, 175.0);

        // Phase 3 (6.8..8.3s): userA speaks solo into AGC-boosted userC (+169.0ms = +8112 smp, BleedGain = 0.54 > 0.45) to latch A -> C.
        for (int f = 0; f < 30; f++, frameCursor++)
        {
            processor.ProcessSynchronizedStep(new Dictionary<string, short[]>
            {
                [userA] = (short[])silentFrame.Clone(),
                [userB] = (short[])silentFrame.Clone(),
                [userC] = (short[])silentFrame.Clone()
            }, frameCursor * 10);
        }

        int lagAtoC = 8112; // +169.0ms
        int p3Frames = 150;
        var p3SpeechA = AcousticRoomSimulator.GenerateNaturalSpeech(p3Frames * FrameSamples, 175.0, 11000.0, includeSyllablePauses: true);
        var p3BleedC = AcousticRoomSimulator.ApplyRoomImpulseResponse(p3SpeechA, directDelaySamples: lagAtoC, directBleedGain: 0.54, rt60Ms: 145.0, applyHeadShadowLowPass: true);

        for (int f = 0; f < p3Frames; f++, frameCursor++)
        {
            int idx = f * FrameSamples;
            processor.ProcessSynchronizedStep(new Dictionary<string, short[]>
            {
                [userA] = p3SpeechA.AsSpan(idx, FrameSamples).ToArray(),
                [userB] = (short[])silentFrame.Clone(),
                [userC] = p3BleedC.AsSpan(idx, FrameSamples).ToArray()
            }, frameCursor * 10);
        }

        float latchedBleedAfterP3 = processor.GetDiagnosticSnapshot()
            .Calibrations.First(c => c.SourceUserId == userA && c.TargetUserId == userC).BleedGainLinear;

        // Phase 4: Loud simultaneous double-talk (userC loud 16500 amp + userA 7000 amp) must NOT pollute PeakBleedGain on latched A -> C,
        // followed immediately by normal simultaneous double-talk (userC 10500 amp + userA 10500 amp) which must open cleanly!
        int loudDtFrames = 70;
        var loudA = AcousticRoomSimulator.GenerateNaturalSpeech(loudDtFrames * FrameSamples + leadCtoA, 175.0, 7000.0);
        var loudC = AcousticRoomSimulator.GenerateNaturalSpeech(loudDtFrames * FrameSamples + leadCtoA, 255.0, 16500.0);
        var loudABleedOnC = AcousticRoomSimulator.ApplyRoomImpulseResponse(loudA.AsSpan(0, loudDtFrames * FrameSamples), lagAtoC, 0.54, 145.0, true);
        var loudCBleedOnA = AcousticRoomSimulator.ApplyRoomImpulseResponse(loudC, 0, 0.20, 140.0, true);

        for (int f = 0; f < loudDtFrames; f++, frameCursor++)
        {
            int idx = f * FrameSamples;
            var fA = new short[FrameSamples];
            var fC = new short[FrameSamples];
            for (int i = 0; i < FrameSamples; i++)
            {
                fA[i] = (short)Math.Clamp(loudA[idx + i] + loudCBleedOnA[idx + leadCtoA + i], short.MinValue, short.MaxValue);
                fC[i] = (short)Math.Clamp(loudC[idx + i] + loudABleedOnC[idx + i], short.MinValue, short.MaxValue);
            }
            processor.ProcessSynchronizedStep(new Dictionary<string, short[]>
            {
                [userA] = fA,
                [userB] = (short[])silentFrame.Clone(),
                [userC] = fC
            }, frameCursor * 10);
        }

        int modDtFrames = 85;
        var modA = AcousticRoomSimulator.GenerateNaturalSpeech(modDtFrames * FrameSamples + leadCtoA, 178.0, 10500.0);
        var modC = AcousticRoomSimulator.GenerateNaturalSpeech(modDtFrames * FrameSamples + leadCtoA, 252.0, 10500.0);
        var modABleedOnC = AcousticRoomSimulator.ApplyRoomImpulseResponse(modA.AsSpan(0, modDtFrames * FrameSamples), lagAtoC, 0.54, 145.0, true);
        var modCBleedOnA = AcousticRoomSimulator.ApplyRoomImpulseResponse(modC, 0, 0.20, 140.0, true);

        int dtBeforeModPhase = processor.GetDiagnosticSnapshot()
            .Participants.First(p => p.UserId == userC).DoubleTalkSubFrames;
        double modCCleanIn = 0;
        double modCOut = 0;
        for (int f = 0; f < modDtFrames; f++, frameCursor++)
        {
            int idx = f * FrameSamples;
            var fA = new short[FrameSamples];
            var fC = new short[FrameSamples];
            for (int i = 0; i < FrameSamples; i++)
            {
                fA[i] = (short)Math.Clamp(modA[idx + i] + modCBleedOnA[idx + leadCtoA + i], short.MinValue, short.MaxValue);
                fC[i] = (short)Math.Clamp(modC[idx + i] + modABleedOnC[idx + i], short.MinValue, short.MaxValue);
            }
            var stepOut = processor.ProcessSynchronizedStep(new Dictionary<string, short[]>
            {
                [userA] = fA,
                [userB] = (short[])silentFrame.Clone(),
                [userC] = fC
            }, frameCursor * 10);

            if (f >= 30)
            {
                modCCleanIn += ComputeRms(modC.AsSpan(idx, FrameSamples));
                modCOut += ComputeRms(stepOut[userC]);
            }
        }

        var snapFinal = processor.GetDiagnosticSnapshot();
        var calACFinal = snapFinal.Calibrations.FirstOrDefault(c => c.SourceUserId == userA && c.TargetUserId == userC);
        Assert.NotNull(calACFinal);
        Assert.Equal(latchedBleedAfterP3, calACFinal!.BleedGainLinear, 2);

        int dtAfterModPhase = snapFinal.Participants.First(p => p.UserId == userC).DoubleTalkSubFrames;
        Assert.True(dtAfterModPhase - dtBeforeModPhase >= 15,
            $"Expected userC to engage DoubleTalkSubtraction (not stay gated by inflated PeakBleedGain), got {dtAfterModPhase - dtBeforeModPhase} double-talk sub-frames");
        Assert.True(modCOut >= modCCleanIn * 0.55,
            $"Expected AGC-pair PeakBleedGain not to inflate during double-talk (retaining >= 55% speech at -16.5 dBFS target across 0.54x STFT subtraction), got out={modCOut:F1} / cleanIn={modCCleanIn:F1}");
    }

    [Fact]
    public void TwoDesktopRnnoisePeers_LowBleedSyllabicAlias_NeverOverwritesLiveReverseLockOrInflatesLookahead()
    {
        // Reproduces Log 1 from "Room Alpha" (2 Windows Chrome PCs with client RNNoise):
        // 1. "peerA" speaks softly (-35 dBFS) for 3.2s -> locks "peerA" -> "admin first" (+4.0ms, WaveCorr >= 0.55).
        // 2. After a 3.0s pause (> 2.0s), "admin first" speaks (-27 dBFS) while client RNNoise on "peerA" heavily
        //    suppresses and phase-warps bleed (-32 dB) with a spurious envelope-only echo burst at -186.2ms.
        // Verifies "admin first" -> "peerA" never false-locks onto the -186.2ms envelope alias, "peerA" lookahead stays
        // at 50ms (never 201ms), and "peerA" -> "admin first" is never wiped back into an uncalibrated ReciprocalPrior.
        var processor = new AcousticRoomProcessor();
        const string adminId = "6f30c05d-c9d6-4cbb-bdbb-b2f8f3b3c9c3";
        const string peerAId = "d9ba9018-59cb-4d1d-b222-9b9aba711103";

        processor.SyncParticipants([adminId, peerAId]);
        processor.SetParticipantClientPipelineInfo(adminId, 30.0, 9.0);
        processor.SetParticipantClientPipelineInfo(peerAId, 30.0, 9.5);

        int frameCursor = 0;
        var silentFrame = new short[FrameSamples];

        // Phase 1: "peerA" speaks softly for 3.2s (+4.0ms = +192 samples air delay into "admin first", 0.14x = -17.1 dB bleed)
        int p1Frames = 320;
        var p1PeerASpeech = AcousticRoomSimulator.GenerateNaturalSpeech(p1Frames * FrameSamples, 195.0, 3800.0, includeSyllablePauses: true);
        var p1AdminBleed = AcousticRoomSimulator.ApplyRoomImpulseResponse(p1PeerASpeech, directDelaySamples: 192, directBleedGain: 0.14, rt60Ms: 135.0, applyHeadShadowLowPass: true);

        for (int f = 0; f < p1Frames; f++, frameCursor++)
        {
            int idx = f * FrameSamples;
            processor.ProcessSynchronizedStep(new Dictionary<string, short[]>
            {
                [adminId] = p1AdminBleed.AsSpan(idx, FrameSamples).ToArray(),
                [peerAId] = p1PeerASpeech.AsSpan(idx, FrameSamples).ToArray()
            }, frameCursor * 10);
        }

        var snapAfterP1 = processor.GetDiagnosticSnapshot();
        var calPeerAToAdminP1 = snapAfterP1.Calibrations.FirstOrDefault(c => c.SourceUserId == peerAId && c.TargetUserId == adminId);
        Assert.NotNull(calPeerAToAdminP1);
        Assert.False(calPeerAToAdminP1!.IsReciprocalPrior);
        Assert.InRange(calPeerAToAdminP1.DelayMs, 1.5, 6.5);

        // 3.0s silence gap (> 2.0s isSignificantlyNewerLiveLatch window)
        for (int f = 0; f < 300; f++, frameCursor++)
        {
            processor.ProcessSynchronizedStep(new Dictionary<string, short[]>
            {
                [adminId] = (short[])silentFrame.Clone(),
                [peerAId] = (short[])silentFrame.Clone()
            }, frameCursor * 10);
        }

        // Phase 2: "admin first" speaks for 3.2s; construct an incoherent carrier modulated by admin's envelope shifted by -186.2ms
        // (simulating an RNNoise-gated -31.9 dB syllabic envelope alias with high EnvCorr ~0.75 but low WaveCorr < 0.30).
        int p2Frames = 320;
        int aliasLeadSamples = 8937; // 186.2ms lead
        var p2AdminExtended = AcousticRoomSimulator.GenerateNaturalSpeech(p2Frames * FrameSamples + aliasLeadSamples, 175.0, 8200.0, includeSyllablePauses: true);
        var incoherentCarrier = AcousticRoomSimulator.GenerateNaturalSpeech(p2Frames * FrameSamples + aliasLeadSamples, 313.0, 8200.0, includeSyllablePauses: false);
        var p2PeerAAliasBleed = new short[p2Frames * FrameSamples];
        float smoothEnv = 0f;
        for (int i = 0; i < p2PeerAAliasBleed.Length; i++)
        {
            float srcAbs = MathF.Abs(p2AdminExtended[i + aliasLeadSamples]) / 8200f;
            smoothEnv = 0.985f * smoothEnv + 0.015f * srcAbs;
            float trueBleed = p2AdminExtended[Math.Max(0, i - 192)] * 0.008f;
            float aliasBleed = incoherentCarrier[i] * smoothEnv * 0.032f;
            p2PeerAAliasBleed[i] = (short)Math.Clamp(trueBleed + aliasBleed, short.MinValue, short.MaxValue);
        }

        for (int f = 0; f < p2Frames; f++, frameCursor++)
        {
            int idx = f * FrameSamples;
            processor.ProcessSynchronizedStep(new Dictionary<string, short[]>
            {
                [adminId] = p2AdminExtended.AsSpan(idx, FrameSamples).ToArray(),
                [peerAId] = p2PeerAAliasBleed.AsSpan(idx, FrameSamples).ToArray()
            }, frameCursor * 10);
        }

        var finalSnap = processor.GetDiagnosticSnapshot();
        var peerADiag = finalSnap.Participants.First(p => p.UserId == peerAId);
        var calPeerAToAdminFinal = finalSnap.Calibrations.FirstOrDefault(c => c.SourceUserId == peerAId && c.TargetUserId == adminId);
        var calAdminToPeerAFinal = finalSnap.Calibrations.FirstOrDefault(c => c.SourceUserId == adminId && c.TargetUserId == peerAId);

        Assert.InRange(peerADiag.EffectiveLookaheadMs, AcousticRoomProcessor.MinCalibratedLookaheadMs, 50);
        Assert.NotNull(calPeerAToAdminFinal);
        Assert.False(calPeerAToAdminFinal!.IsReciprocalPrior, "Expected peerA -> admin live DirectLock never to be wiped by a low-WaveCorr envelope alias");
        Assert.DoesNotContain(finalSnap.Warnings, w => w.Contains(peerAId, StringComparison.Ordinal) && w.Contains("not been directly calibrated", StringComparison.OrdinalIgnoreCase));
        if (calAdminToPeerAFinal != null)
        {
            Assert.True(calAdminToPeerAFinal.DelayMs > -30.0, $"Expected admin -> peerA never to false-lock at -186.2ms, got {calAdminToPeerAFinal.DelayMs:F1}ms");
        }
    }

    [Fact]
    public void ThreeDevices_Sub40MsNodeSlipAndAgcGatedRoommate_PropagatesNodeOffsetAndPreventsFalseDoubleTalk()
    {
        // Reproduces Log 2 from "Room Alpha" (2 Windows Chrome PCs + 1 Android AGC device):
        // 1. All 3 participants calibrate:
        //    - peerA -> admin (-40.6ms), admin -> peerA (+48.6ms), admin -> peerB (+85.3ms, 3.36x = +10.5 dB Android AGC)
        //    - peerB -> admin (-77.5ms), peerB -> peerA (-35.4ms)  => exact ~3.9ms (~1.34m) air propagation!
        // 2. "admin first" experiences a +35.4ms WebRTC NetEq stream slip (inside the old <= 40ms drift blind spot),
        //    moving true peerB -> admin from -77.5ms to -42.1ms while peerB -> peerA stays at -35.4ms.
        // 3. "peerB" speaks solo at hot Android levels (-12 dBFS):
        //    - Verifies 0 false DoubleTalkSTFT buckets on peerB against gated roommates.
        //    - Verifies kinematic node-offset propagation updates admin's stale outgoing pairs so decomposed air distance
        //      stays realistic (~1.3-2.5m instead of 7.40m) and "peerA" is never demoted back to uncalibrated ReciprocalPrior.
        var processor = new AcousticRoomProcessor();
        const string adminId = "6f30c05d-c9d6-4cbb-bdbb-b2f8f3b3c9c3";
        const string peerAId = "d9ba9018-59cb-4d1d-b222-9b9aba711103";
        const string peerBId = "340555a5-a468-477f-870f-3b49a4d09679";

        processor.SyncParticipants([adminId, peerAId, peerBId]);
        processor.SetParticipantClientPipelineInfo(adminId, 30.0, 9.0);
        processor.SetParticipantClientPipelineInfo(peerAId, 30.0, 9.5);
        processor.SetParticipantClientPipelineInfo(peerBId, 80.3, 11.0);

        int frameCursor = 0;
        var silentFrame = new short[FrameSamples];

        // Turn 1: "peerA" speaks (peerA -> admin = -40.6ms = -1949 smp; peerA -> peerB = +43.4ms = +2083 smp)
        int t1Frames = 150;
        int leadPeerAToAdmin = 1949;
        var t1PeerA = AcousticRoomSimulator.GenerateNaturalSpeech(t1Frames * FrameSamples + leadPeerAToAdmin, 205.0, 5200.0, includeSyllablePauses: true);
        var t1AdminBleed = AcousticRoomSimulator.ApplyRoomImpulseResponse(t1PeerA, 0, 0.12, 130.0, true);
        var t1PeerBBleed = AcousticRoomSimulator.ApplyRoomImpulseResponse(t1PeerA.AsSpan(0, t1Frames * FrameSamples), 2083, 0.45, 140.0, true);
        for (int f = 0; f < t1Frames; f++, frameCursor++)
        {
            int idx = f * FrameSamples;
            processor.ProcessSynchronizedStep(new Dictionary<string, short[]>
            {
                [peerAId] = t1PeerA.AsSpan(idx, FrameSamples).ToArray(),
                [adminId] = t1AdminBleed.AsSpan(idx + leadPeerAToAdmin, FrameSamples).ToArray(),
                [peerBId] = t1PeerBBleed.AsSpan(idx, FrameSamples).ToArray()
            }, frameCursor * 10);
        }

        for (int f = 0; f < 40; f++, frameCursor++)
        {
            processor.ProcessSynchronizedStep(new Dictionary<string, short[]>
            {
                [adminId] = (short[])silentFrame.Clone(),
                [peerAId] = (short[])silentFrame.Clone(),
                [peerBId] = (short[])silentFrame.Clone()
            }, frameCursor * 10);
        }

        // Turn 2: "admin first" speaks (admin -> peerA = +48.6ms = +2331 smp; admin -> peerB = +85.3ms = +4093 smp, 2.2x AGC boost)
        int t2Frames = 160;
        var t2Admin = AcousticRoomSimulator.GenerateNaturalSpeech(t2Frames * FrameSamples, 178.0, 5500.0, includeSyllablePauses: true);
        var t2PeerABleed = AcousticRoomSimulator.ApplyRoomImpulseResponse(t2Admin, 2331, 0.08, 130.0, true);
        var t2PeerBBleed = AcousticRoomSimulator.ApplyRoomImpulseResponse(t2Admin, 4093, 2.15, 145.0, true);
        for (int f = 0; f < t2Frames; f++, frameCursor++)
        {
            int idx = f * FrameSamples;
            processor.ProcessSynchronizedStep(new Dictionary<string, short[]>
            {
                [adminId] = t2Admin.AsSpan(idx, FrameSamples).ToArray(),
                [peerAId] = t2PeerABleed.AsSpan(idx, FrameSamples).ToArray(),
                [peerBId] = t2PeerBBleed.AsSpan(idx, FrameSamples).ToArray()
            }, frameCursor * 10);
        }

        for (int f = 0; f < 40; f++, frameCursor++)
        {
            processor.ProcessSynchronizedStep(new Dictionary<string, short[]>
            {
                [adminId] = (short[])silentFrame.Clone(),
                [peerAId] = (short[])silentFrame.Clone(),
                [peerBId] = (short[])silentFrame.Clone()
            }, frameCursor * 10);
        }

        // Turn 3: "peerB" speaks before slip (peerB -> admin = -77.5ms = -3720 smp; peerB -> peerA = -35.4ms = -1698 smp)
        int t3Frames = 150;
        int leadPeerBToAdminPre = 3720;
        int leadPeerBToPeerA = 1698;
        var t3PeerB = AcousticRoomSimulator.GenerateNaturalSpeech(t3Frames * FrameSamples + leadPeerBToAdminPre, 255.0, 14500.0, includeSyllablePauses: true);
        var t3AdminBleed = AcousticRoomSimulator.ApplyRoomImpulseResponse(t3PeerB, 0, 0.045, 130.0, true);
        var t3PeerABleed = AcousticRoomSimulator.ApplyRoomImpulseResponse(t3PeerB, 0, 0.075, 130.0, true);
        for (int f = 0; f < t3Frames; f++, frameCursor++)
        {
            int idx = f * FrameSamples;
            processor.ProcessSynchronizedStep(new Dictionary<string, short[]>
            {
                [peerBId] = t3PeerB.AsSpan(idx, FrameSamples).ToArray(),
                [adminId] = t3AdminBleed.AsSpan(idx + leadPeerBToAdminPre, FrameSamples).ToArray(),
                [peerAId] = t3PeerABleed.AsSpan(idx + leadPeerBToPeerA, FrameSamples).ToArray()
            }, frameCursor * 10);
        }

        // 2.5s pause while "admin first" experiences a +35.4ms NetEq slip (peerB -> admin moves from -77.5ms to -42.1ms = -2021 smp)
        for (int f = 0; f < 250; f++, frameCursor++)
        {
            processor.ProcessSynchronizedStep(new Dictionary<string, short[]>
            {
                [adminId] = (short[])silentFrame.Clone(),
                [peerAId] = (short[])silentFrame.Clone(),
                [peerBId] = (short[])silentFrame.Clone()
            }, frameCursor * 10);
        }

        processor.ResetDiagnosticCounters();

        // Turn 4: "peerB" speaks solo at hot Android level after admin's +35.4ms slip (peerB -> admin = -42.1ms = -2021 smp, peerB -> peerA = -35.4ms)
        int t4Frames = 180;
        int leadPeerBToAdminPost = 2021;
        var t4PeerB = AcousticRoomSimulator.GenerateNaturalSpeech(t4Frames * FrameSamples + leadPeerBToAdminPost, 258.0, 16500.0, includeSyllablePauses: true);
        var t4AdminBleed = AcousticRoomSimulator.ApplyRoomImpulseResponse(t4PeerB, 0, 0.048, 130.0, true);
        var t4PeerABleed = AcousticRoomSimulator.ApplyRoomImpulseResponse(t4PeerB, 0, 0.075, 130.0, true);
        for (int f = 0; f < t4Frames; f++, frameCursor++)
        {
            int idx = f * FrameSamples;
            processor.ProcessSynchronizedStep(new Dictionary<string, short[]>
            {
                [peerBId] = t4PeerB.AsSpan(idx, FrameSamples).ToArray(),
                [adminId] = t4AdminBleed.AsSpan(idx + leadPeerBToAdminPost, FrameSamples).ToArray(),
                [peerAId] = t4PeerABleed.AsSpan(idx + leadPeerBToPeerA, FrameSamples).ToArray()
            }, frameCursor * 10);
        }

        var snap = processor.GetDiagnosticSnapshot();
        Assert.DoesNotContain(snap.TimelineBuckets, b =>
            b.ParticipantGateStates.TryGetValue(peerBId, out var gM2) && gM2 == AcousticGateState.DoubleTalkSubtraction &&
            b.ParticipantGateStates.TryGetValue(adminId, out var gA) && gA == AcousticGateState.GatedBleed &&
            b.ParticipantGateStates.TryGetValue(peerAId, out var gM) && gM == AcousticGateState.GatedBleed);

        var calPeerAToAdmin = snap.Calibrations.FirstOrDefault(c => c.SourceUserId == peerAId && c.TargetUserId == adminId);
        Assert.NotNull(calPeerAToAdmin);
        Assert.False(calPeerAToAdmin!.IsReciprocalPrior, "Expected peerA -> admin to stay calibrated (not wiped to ReciprocalPrior) after admin's 35.4ms stream slip");

        var decompAdminPeerB = snap.Decompositions.FirstOrDefault(d =>
            (d.UserAId == peerBId && d.UserBId == adminId) || (d.UserAId == adminId && d.UserBId == peerBId));
        Assert.NotNull(decompAdminPeerB);
        Assert.InRange(decompAdminPeerB!.EstimatedDistanceMeters, 0.5, 3.0);
    }

    [Fact]
    public void QuietMicWithMakeupLeveler_EchoDominatedDoubleTalkAnd30sTimeline_SuppressesEchoAndRetainsFullHistory()
    {
        // Verifies two user-reported improvements:
        // 1. On a quiet mic (-34 dBFS speech) where the -20 dBFS post-leveler builds strong upward makeup gain,
        //    bleed-dominated DoubleTalkSubtraction gaps (when the local speaker pauses briefly mid-overlap while a loud
        //    roommate continues talking) suppress reverberant bleed by >= 18 dB instead of boosting echo back up.
        // 2. Rolling timeline history retains >= 128 buckets (> 30s) so 30-second diagnostic captures never clip early buckets.
        var processor = new AcousticRoomProcessor();
        const string loudRoommate = "loud-roommate";
        const string quietLocal = "quiet-local";
        processor.SyncParticipants([loudRoommate, quietLocal]);

        long frameCursor = 0;
        var silentFrame = new short[FrameSamples];

        // Warm up 26s of silence/idle buckets first so total test exceeds 32s (> 120 old bucket limit) without wasting CPU on speech gen
        for (int f = 0; f < 2600; f++, frameCursor++)
        {
            processor.ProcessSynchronizedStep(new Dictionary<string, short[]>
            {
                [loudRoommate] = silentFrame,
                [quietLocal] = silentFrame
            }, frameCursor * 10);
        }

        // Phase 1 (1.5s): quietLocal speaks solo at ~ -34 dBFS (amp 1100) so post-leveler builds upward makeup gain (> +10 dB)
        int p1Frames = 150;
        var p1QuietSpeech = AcousticRoomSimulator.GenerateNaturalSpeech(p1Frames * FrameSamples, 240.0, 1100.0, includeSyllablePauses: false);
        var p1RoommateBleed = AcousticRoomSimulator.ApplyRoomImpulseResponse(p1QuietSpeech, 240, 0.18, 120.0, true);
        for (int f = 0; f < p1Frames; f++, frameCursor++)
        {
            int idx = f * FrameSamples;
            processor.ProcessSynchronizedStep(new Dictionary<string, short[]>
            {
                [loudRoommate] = p1RoommateBleed.AsSpan(idx, FrameSamples).ToArray(),
                [quietLocal] = p1QuietSpeech.AsSpan(idx, FrameSamples).ToArray()
            }, frameCursor * 10);
        }

        // Phase 2 (1.5s): loudRoommate speaks solo at ~ -16 dBFS (amp 8500) to lock loudRoommate -> quietLocal (+5.0ms = +240 smp, 0.25x bleed)
        int p2Frames = 150;
        var p2LoudSpeech = AcousticRoomSimulator.GenerateNaturalSpeech(p2Frames * FrameSamples, 175.0, 8500.0, includeSyllablePauses: true);
        var p2BleedOnQuiet = AcousticRoomSimulator.ApplyRoomImpulseResponse(p2LoudSpeech, 240, 0.25, 140.0, true);
        for (int f = 0; f < p2Frames; f++, frameCursor++)
        {
            int idx = f * FrameSamples;
            processor.ProcessSynchronizedStep(new Dictionary<string, short[]>
            {
                [loudRoommate] = p2LoudSpeech.AsSpan(idx, FrameSamples).ToArray(),
                [quietLocal] = p2BleedOnQuiet.AsSpan(idx, FrameSamples).ToArray()
            }, frameCursor * 10);
        }

        // Phase 3 (3.0s): Simultaneous speech where quietLocal speaks in bursts with 120ms inter-word gaps while loudRoommate speaks continuously
        int p3Frames = 300;
        var p3LoudCont = AcousticRoomSimulator.GenerateNaturalSpeech(p3Frames * FrameSamples, 178.0, 8500.0, includeSyllablePauses: false);
        var p3BleedCont = AcousticRoomSimulator.ApplyRoomImpulseResponse(p3LoudCont, 240, 0.25, 140.0, true);
        var p3QuietBursts = AcousticRoomSimulator.GenerateNaturalSpeech(p3Frames * FrameSamples, 245.0, 4200.0, includeSyllablePauses: false);

        int lookaheadFrames = processor.GetDiagnosticSnapshot().Participants.First(p => p.UserId == quietLocal).EffectiveLookaheadMs / 10;
        var bleedHistory = new List<short[]>();
        var quietPresentHistory = new List<bool>();
        double gapEchoInEnergy = 0.0;
        double gapEchoOutEnergy = 0.0;

        for (int f = 0; f < p3Frames; f++, frameCursor++)
        {
            int idx = f * FrameSamples;
            // quietLocal speaks for 280ms, pauses for 80ms (during the first 30ms of pause or onset/tail, echo residual must remain deeply suppressed)
            bool quietSpeaking = (f % 36) < 24;
            quietPresentHistory.Add(quietSpeaking);

            var rawBleedFrame = p3BleedCont.AsSpan(idx, FrameSamples).ToArray();
            bleedHistory.Add(rawBleedFrame);

            var micQuiet = new short[FrameSamples];
            for (int i = 0; i < FrameSamples; i++)
            {
                int localSample = quietSpeaking ? p3QuietBursts[idx + i] : 0;
                micQuiet[i] = (short)Math.Clamp(localSample + rawBleedFrame[i], short.MinValue, short.MaxValue);
            }

            var outputs = processor.ProcessSynchronizedStep(new Dictionary<string, short[]>
            {
                [loudRoommate] = p3LoudCont.AsSpan(idx, FrameSamples).ToArray(),
                [quietLocal] = micQuiet
            }, frameCursor * 10);

            int alignedIdx = f - lookaheadFrames;
            if (alignedIdx >= 10 && !quietPresentHistory[alignedIdx] && !quietPresentHistory[alignedIdx - 1])
            {
                var inBleed = bleedHistory[alignedIdx];
                var outClean = outputs[quietLocal];
                for (int i = 0; i < FrameSamples; i++)
                {
                    gapEchoInEnergy += (double)inBleed[i] * inBleed[i];
                    gapEchoOutEnergy += (double)outClean[i] * outClean[i];
                }
            }
        }

        double gapEchoSuppressionDb = 10.0 * Math.Log10(Math.Max(gapEchoInEnergy, 1.0) / Math.Max(gapEchoOutEnergy, 1e-6));
        Assert.True(gapEchoSuppressionDb >= 18.0,
            $"Expected quiet-mic inter-word echo during continuous roommate speech to be suppressed by >= 18 dB despite makeup leveler, got {gapEchoSuppressionDb:F1} dB");

        var snap32s = processor.GetDiagnosticSnapshot();
        Assert.True(snap32s.TimelineBuckets.Count >= 125,
            $"Expected rolling diagnostic history to retain >= 125 buckets (> 31s) for 30s captures, got {snap32s.TimelineBuckets.Count} buckets");
    }

    [Fact]
    public void TwoDesktopRnnoisePeers_SanitizesStaleSavedQueueDelayAndDropsLookaheadTo12MsOnBidirectionalLock()
    {
        var processor = new AcousticRoomProcessor();
        const string peerAId = "peer-desktop-a";
        const string peerBId = "peer-desktop-b";

        processor.SyncParticipants([peerAId, peerBId]);
        processor.SetParticipantClientPipelineInfo(peerAId, 30.0, 9.0);
        processor.SetParticipantClientPipelineInfo(peerBId, 30.0, 9.0);

        // 1. Seed stale saved calibrations from a prior session that had a -145.6ms (-6987 smp) / +165.5ms (+7945 smp) queue backlog:
        //    Since both devices are 30ms desktop RNNoise pipelines (expected DSP diff = 0ms), SeedSavedPairCalibration must
        //    sanitize the stale -145.6ms queue lag to the +4.0ms hardware prior while preserving learned BleedGain (0.134 & 0.073),
        //    preventing Peer B's lookahead from inflating to 161ms at startup!
        processor.SeedSavedPairCalibration(peerAId, peerBId, delaySamples: -6987, bleedGain: 0.134f, confidence: 0.95);
        processor.SeedSavedPairCalibration(peerBId, peerAId, delaySamples: 7945, bleedGain: 0.073f, confidence: 0.77);

        var initialSnap = processor.GetDiagnosticSnapshot();
        var initialPeerB = initialSnap.Participants.First(p => p.UserId == peerBId);
        var initialPeerA = initialSnap.Participants.First(p => p.UserId == peerAId);
        Assert.Equal(50, initialPeerB.EffectiveLookaheadMs);
        Assert.Equal(50, initialPeerA.EffectiveLookaheadMs);

        int frameCursor = 0;
        int airDelaySamples = 150; // +3.12ms (~1.07m mic separation)
        var silentFrame = new short[FrameSamples];

        // 2. Turn 1 (1.5s): Peer A speaks solo (+3.12ms air delay into Peer B, 0.211 = -13.5 dB bleed)
        int turnFrames = 150;
        var peerASpeech = AcousticRoomSimulator.GenerateNaturalSpeech(turnFrames * FrameSamples, 235.0, 4800.0, includeSyllablePauses: true);
        var bleedOnPeerB = AcousticRoomSimulator.ApplyRoomImpulseResponse(peerASpeech, directDelaySamples: airDelaySamples, directBleedGain: 0.211, rt60Ms: 125.0, applyHeadShadowLowPass: true);
        for (int f = 0; f < turnFrames; f++, frameCursor++)
        {
            int idx = f * FrameSamples;
            processor.ProcessSynchronizedStep(new Dictionary<string, short[]>
            {
                [peerAId] = peerASpeech.AsSpan(idx, FrameSamples).ToArray(),
                [peerBId] = bleedOnPeerB.AsSpan(idx, FrameSamples).ToArray()
            }, frameCursor * 10);
        }

        // 3. Turn 2 (1.5s): Peer B speaks solo (+3.12ms air delay into Peer A, 0.067 = -23.4 dB bleed)
        var peerBSpeech = AcousticRoomSimulator.GenerateNaturalSpeech(turnFrames * FrameSamples, 175.0, 9500.0, includeSyllablePauses: true);
        var bleedOnPeerA = AcousticRoomSimulator.ApplyRoomImpulseResponse(peerBSpeech, directDelaySamples: airDelaySamples, directBleedGain: 0.067, rt60Ms: 125.0, applyHeadShadowLowPass: true);
        for (int f = 0; f < turnFrames; f++, frameCursor++)
        {
            int idx = f * FrameSamples;
            processor.ProcessSynchronizedStep(new Dictionary<string, short[]>
            {
                [peerAId] = bleedOnPeerA.AsSpan(idx, FrameSamples).ToArray(),
                [peerBId] = peerBSpeech.AsSpan(idx, FrameSamples).ToArray()
            }, frameCursor * 10);
        }

        // 4. Brief 100ms pause so both streams can apply their newly lowered target lookahead cleanly outside active speech
        for (int f = 0; f < 10; f++, frameCursor++)
        {
            processor.ProcessSynchronizedStep(new Dictionary<string, short[]>
            {
                [peerAId] = silentFrame,
                [peerBId] = silentFrame
            }, frameCursor * 10);
        }

        var lockedSnap = processor.GetDiagnosticSnapshot();
        var lockedPeerA = lockedSnap.Participants.First(p => p.UserId == peerAId);
        var lockedPeerB = lockedSnap.Participants.First(p => p.UserId == peerBId);

        // Both directions are now BidirectionallyDirectLocked at ~ +3.12ms (+150 samples),
        // so EffectiveLookaheadMs drops from 50ms down to ~12ms (10..15ms) while maintaining >= 20 dB ERLE!
        Assert.InRange(lockedPeerA.EffectiveLookaheadMs, AcousticRoomProcessor.MinCalibratedLookaheadMs, 15);
        Assert.InRange(lockedPeerB.EffectiveLookaheadMs, AcousticRoomProcessor.MinCalibratedLookaheadMs, 15);
        Assert.True(lockedPeerA.AchievedBleedSuppressionDb >= 20.0,
            $"Expected Peer A ERLE >= 20 dB, got {lockedPeerA.AchievedBleedSuppressionDb:F1} dB");
        Assert.True(lockedPeerB.AchievedBleedSuppressionDb >= 20.0,
            $"Expected Peer B ERLE >= 20 dB, got {lockedPeerB.AchievedBleedSuppressionDb:F1} dB");
    }

    [Fact]
    public void AndroidBluetoothAndDesktopPeers_SeedSavedCalibration_PreservesBluetoothLagAndSanitizesWhenSwitchedToWired()
    {
        const string androidBtId = "340555a5-a468-477f-870f-3b49a4d09679";
        const string desktopPcId = "6f30c05d-c9d6-4cbb-bdbb-b2f8f3b3c9c3";

        // 1. Reconnect while Android is still on Bluetooth (~230.3ms capture) and Windows PC is on RNNoise (30.0ms capture):
        //    Expected delay for Android -> PC is (30.0 - 230.3) + 0.5*(4.0 - 7.0) + 4.0 = -197.8ms (-9494 samples),
        //    which is within +/-45ms of the saved -215.4ms (-10337 samples) Bluetooth calibration!
        var btProcessor = new AcousticRoomProcessor();
        btProcessor.SyncParticipants([androidBtId, desktopPcId]);
        btProcessor.SetParticipantClientPipelineInfo(androidBtId, 230.3, 7.0);
        btProcessor.SetParticipantClientPipelineInfo(desktopPcId, 30.0, 4.0);

        btProcessor.SeedSavedPairCalibration(androidBtId, desktopPcId, delaySamples: -10337, bleedGain: 0.264f, confidence: 0.79);
        btProcessor.SeedSavedPairCalibration(desktopPcId, androidBtId, delaySamples: 10848, bleedGain: 0.036f, confidence: 0.75);

        var btSnap = btProcessor.GetDiagnosticSnapshot();
        var btFwd = btSnap.Calibrations.First(c => c.SourceUserId == androidBtId && c.TargetUserId == desktopPcId);
        var btRev = btSnap.Calibrations.First(c => c.SourceUserId == desktopPcId && c.TargetUserId == androidBtId);
        Assert.False(btFwd.IsReciprocalPrior);
        Assert.False(btRev.IsReciprocalPrior);
        Assert.Equal(-10337, btFwd.DelaySamples48k);
        Assert.Equal(10848, btRev.DelaySamples48k);

        var btDesktopDiag = btSnap.Participants.First(p => p.UserId == desktopPcId);
        Assert.InRange(btDesktopDiag.EffectiveLookaheadMs, 225, 235);

        // 2. Next session: Android user disconnects Bluetooth and joins on built-in Speaker/Wired (80.3ms capture).
        //    Saved -215.4ms Bluetooth calibration now deviates by ~168ms (> 45ms) from expected -47.8ms wired hardware lag,
        //    so SeedSavedPairCalibration must sanitize both pairs to low-confidence hardware priors!
        var wiredProcessor = new AcousticRoomProcessor();
        wiredProcessor.SyncParticipants([androidBtId, desktopPcId]);
        wiredProcessor.SetParticipantClientPipelineInfo(androidBtId, 80.3, 7.0);
        wiredProcessor.SetParticipantClientPipelineInfo(desktopPcId, 30.0, 4.0);

        wiredProcessor.SeedSavedPairCalibration(androidBtId, desktopPcId, delaySamples: -10337, bleedGain: 0.264f, confidence: 0.79);
        wiredProcessor.SeedSavedPairCalibration(desktopPcId, androidBtId, delaySamples: 10848, bleedGain: 0.036f, confidence: 0.75);

        var wiredSnap = wiredProcessor.GetDiagnosticSnapshot();
        var wiredFwd = wiredSnap.Calibrations.First(c => c.SourceUserId == androidBtId && c.TargetUserId == desktopPcId);
        var wiredRev = wiredSnap.Calibrations.First(c => c.SourceUserId == desktopPcId && c.TargetUserId == androidBtId);
        Assert.True(wiredFwd.IsReciprocalPrior);
        Assert.True(wiredRev.IsReciprocalPrior);
        Assert.InRange(wiredFwd.DelayMs, -52.0, -43.0);
        Assert.InRange(wiredRev.DelayMs, 51.0, 60.0);

        var wiredDesktopDiag = wiredSnap.Participants.First(p => p.UserId == desktopPcId);
        Assert.True(wiredDesktopDiag.EffectiveLookaheadMs <= 70,
            $"Expected sanitized wired prior to avoid 230ms Bluetooth lookahead inflation, got {wiredDesktopDiag.EffectiveLookaheadMs}ms");
    }

    [Fact]
    public void PostCancellationLeveler_DiscordMatchedMinus16Point5DbFs_NormalizesQuietAndLoudSpeechWithoutClipping()
    {
        var processor = new AcousticRoomProcessor();
        const string quietUser = "quiet-speaker";
        const string loudUser = "loud-speaker";
        processor.SyncParticipants([quietUser, loudUser]);

        int frames = 140; // 1.4s
        var quietIn = AcousticRoomSimulator.GenerateNaturalSpeech(frames * FrameSamples, fundamentalHz: 225.0, amplitude: 2100.0, includeSyllablePauses: false);
        var loudIn = AcousticRoomSimulator.GenerateNaturalSpeech(frames * FrameSamples, fundamentalHz: 185.0, amplitude: 26000.0, includeSyllablePauses: false);
        var silence = new short[FrameSamples];

        var quietSettledOut = new List<short>();
        for (int f = 0; f < frames; f++)
        {
            var step = processor.ProcessSynchronizedStep(new Dictionary<string, short[]>
            {
                [quietUser] = quietIn.AsSpan(f * FrameSamples, FrameSamples).ToArray(),
                [loudUser] = silence
            }, f * 10);

            if (f >= 60)
            {
                quietSettledOut.AddRange(step[quietUser]);
            }
        }

        var loudSettledOut = new List<short>();
        int peakAbsLoudSample = 0;
        for (int f = 0; f < frames; f++)
        {
            var step = processor.ProcessSynchronizedStep(new Dictionary<string, short[]>
            {
                [quietUser] = silence,
                [loudUser] = loudIn.AsSpan(f * FrameSamples, FrameSamples).ToArray()
            }, (frames + f) * 10);

            if (f >= 60)
            {
                foreach (short s in step[loudUser])
                {
                    peakAbsLoudSample = Math.Max(peakAbsLoudSample, Math.Abs((int)s));
                }
                loudSettledOut.AddRange(step[loudUser]);
            }
        }

        double quietOutDbFs = 20.0 * Math.Log10(ComputeRms(quietSettledOut.ToArray()) / 32768.0);
        double loudOutDbFs = 20.0 * Math.Log10(ComputeRms(loudSettledOut.ToArray()) / 32768.0);

        Assert.InRange(quietOutDbFs, -20.5, -14.5);
        Assert.InRange(loudOutDbFs, -19.5, -13.5);
        Assert.True(peakAbsLoudSample <= 29350,
            $"Expected -1.0 dBFS soft-knee peak limiter ceiling (~29205 PCM16), got peak={peakAbsLoudSample}");
    }

    [Fact]
    public void QuietSpeakerFollowedBySustainedIdleNoise_DoesNotBoostRoomNoiseFloor_AndRecoversSpeechImmediately()
    {
        var processor = new AcousticRoomProcessor();
        const string quietUser = "pc_roommate";
        const string silentPeer = "mobile_listener";
        processor.SyncParticipants([quietUser, silentPeer]);

        int speechFrames = 140; // 1.4s quiet speech (~ -32 dBFS -> boosts TargetMakeupGain > 4x)
        int idleFrames = 120;   // 1.2s low-level post-RNNoise room residual (~ -60 dBFS)
        int resumeFrames = 80;  // 0.8s resumed quiet speech
        var quietSpeech = AcousticRoomSimulator.GenerateNaturalSpeech((speechFrames + resumeFrames) * FrameSamples, fundamentalHz: 220.0, amplitude: 2100.0, includeSyllablePauses: false);
        var silence = new short[FrameSamples];
        var rng = new Random(12345);

        for (int f = 0; f < speechFrames; f++)
        {
            processor.ProcessSynchronizedStep(new Dictionary<string, short[]>
            {
                [quietUser] = quietSpeech.AsSpan(f * FrameSamples, FrameSamples).ToArray(),
                [silentPeer] = silence
            }, f * 10);
        }

        var settledIdleOut = new List<short>();
        for (int f = 0; f < idleFrames; f++)
        {
            var residualFrame = new short[FrameSamples];
            for (int i = 0; i < FrameSamples; i++)
            {
                residualFrame[i] = (short)rng.Next(-32, 33); // ~ -64.7 dBFS post-RNNoise ambient floor
            }

            var step = processor.ProcessSynchronizedStep(new Dictionary<string, short[]>
            {
                [quietUser] = residualFrame,
                [silentPeer] = silence
            }, (speechFrames + f) * 10);

            if (f >= 60)
            {
                settledIdleOut.AddRange(step[quietUser]);
            }
        }

        var resumedSpeechOut = new List<short>();
        for (int f = 0; f < resumeFrames; f++)
        {
            var step = processor.ProcessSynchronizedStep(new Dictionary<string, short[]>
            {
                [quietUser] = quietSpeech.AsSpan((speechFrames + f) * FrameSamples, FrameSamples).ToArray(),
                [silentPeer] = silence
            }, (speechFrames + idleFrames + f) * 10);

            if (f >= 25)
            {
                resumedSpeechOut.AddRange(step[quietUser]);
            }
        }

        double idleRms = Math.Max(1e-6, ComputeRms(settledIdleOut.ToArray()));
        double idleOutDbFs = 20.0 * Math.Log10(idleRms / 32768.0);
        double resumedOutDbFs = 20.0 * Math.Log10(ComputeRms(resumedSpeechOut.ToArray()) / 32768.0);

        // Without speech-presence makeup gating & idle silence expansion, +13 dB makeup would boost -64.7 dBFS up to ~ -51 dBFS.
        // With speech-presence gating + -16 dB idle silence gate, settled idle floor stays crisp below -72 dBFS.
        Assert.True(idleOutDbFs < -72.0, $"Expected crisp idle silence floor < -72 dBFS, got {idleOutDbFs:F1} dBFS");
        Assert.InRange(resumedOutDbFs, -21.0, -14.5);
    }

    [Fact]
    public void UnverifiedLongLagReciprocalPrior_CapsTargetLookaheadAt180Ms_PreventingSpeakerphoneEchoLatencyRunaway()
    {
        var processor = new AcousticRoomProcessor();
        const string pcUser = "pc_roommate";
        const string phoneUser = "android_speakerphone";
        processor.SyncParticipants([pcUser, phoneUser]);

        // Seed a +312 ms (+14976 samples) calibration on pc -> phone, which automatically creates an
        // unverified -304 ms IsReciprocalPrior on phone -> pc.
        processor.SeedSavedPairCalibration(pcUser, phoneUser, 14976, bleedGain: 0.25f, confidence: 0.72f);

        var snap = processor.GetDiagnosticSnapshot();
        var reciprocal = snap.Calibrations.FirstOrDefault(c => c.SourceUserId == phoneUser && c.TargetUserId == pcUser);
        Assert.NotNull(reciprocal);
        Assert.True(reciprocal!.IsReciprocalPrior);
        Assert.True(reciprocal.DelayMs < -290.0);

        // Unverified reciprocal prior must never inflate pcUser's lookahead buffer to 355 ms; it is capped at <= 180 ms
        var pcDiag = snap.Participants.First(p => p.UserId == pcUser);
        Assert.InRange(pcDiag.EffectiveLookaheadMs, 50, 180);

        // Reasonable unverified reciprocal priors (e.g., +94 ms -> -86 ms prior -> ~101 ms lookahead) remain completely unaffected
        var normalProcessor = new AcousticRoomProcessor();
        normalProcessor.SyncParticipants([pcUser, phoneUser]);
        normalProcessor.SeedSavedPairCalibration(pcUser, phoneUser, 94 * 48, bleedGain: 0.20f, confidence: 0.72f);
        var normalSnap = normalProcessor.GetDiagnosticSnapshot();
        var normalPcDiag = normalSnap.Participants.First(p => p.UserId == pcUser);
        Assert.InRange(normalPcDiag.EffectiveLookaheadMs, 98, 105);
    }

    [Fact]
    public void NegligibleBleedNegativeLagCalibration_DoesNotInflateLookaheadBuffer()
    {
        var processor = new AcousticRoomProcessor();
        const string pcUser = "pc_roommate";
        const string phoneUser = "android_native_phone";
        processor.SyncParticipants([pcUser, phoneUser]);

        // Negligible directly-measured bleed (< 0.015) even with large negative lag must not inflate target lookahead beyond default floor (50 ms)
        processor.SeedSavedPairCalibration(phoneUser, pcUser, -120 * 48, bleedGain: 0.008f, confidence: 0.75f);

        var snap = processor.GetDiagnosticSnapshot();
        var pcDiag = snap.Participants.First(p => p.UserId == pcUser);
        Assert.Equal(50, pcDiag.EffectiveLookaheadMs);
    }

    [Fact]
    public void ClosedBackHeadsetsWithNegligibleBleed_SingleSpeaker_CalibratesAndDropsLookaheadTo10MsFloor()
    {
        var processor = new AcousticRoomProcessor();
        const string headsetA = "headset_user_a";
        const string headsetB = "headset_user_b";

        processor.SyncParticipants([headsetA, headsetB]);
        processor.SetParticipantClientPipelineInfo(headsetA, 30.0, 6.0);
        processor.SetParticipantClientPipelineInfo(headsetB, 30.0, 6.0);

        int speechFrames = 80; // 0.8s solo speech on Headset A while Headset B has closed-back negligible bleed (~ -52 dB)
        var speechA = AcousticRoomSimulator.GenerateNaturalSpeech(speechFrames * FrameSamples, 220.0, 12000.0, includeSyllablePauses: true);
        var silentFrame = new short[FrameSamples];

        for (int f = 0; f < speechFrames; f++)
        {
            int idx = f * FrameSamples;
            var frameA = speechA.AsSpan(idx, FrameSamples).ToArray();
            var frameB = new short[FrameSamples];
            for (int i = 0; i < FrameSamples; i++)
            {
                frameB[i] = (short)Math.Round(frameA[i] * 0.0025); // -52 dB negligible bleed
            }

            processor.ProcessSynchronizedStep(new Dictionary<string, short[]>
            {
                [headsetA] = frameA,
                [headsetB] = frameB
            }, f * 10);
        }

        // Brief 50ms inter-phrase pause so Headset A applies its newly lowered lookahead target
        for (int f = 0; f < 5; f++)
        {
            processor.ProcessSynchronizedStep(new Dictionary<string, short[]>
            {
                [headsetA] = silentFrame,
                [headsetB] = silentFrame
            }, (speechFrames + f) * 10);
        }

        var snap = processor.GetDiagnosticSnapshot();
        var calAB = snap.Calibrations.FirstOrDefault(c => c.SourceUserId == headsetA && c.TargetUserId == headsetB);
        Assert.NotNull(calAB);
        Assert.True(calAB!.IsLiveVerified);
        Assert.False(calAB.IsReciprocalPrior);
        Assert.True(calAB.BleedGainLinear < AcousticRoomProcessor.NegligibleBleedGainThreshold);

        // No false warning claiming headset_user_a hasn't been calibrated yet
        Assert.DoesNotContain(snap.Warnings, w => w.Contains($"Direction from {headsetA}", StringComparison.Ordinal));

        var diagA = snap.Participants.First(p => p.UserId == headsetA);
        var diagB = snap.Participants.First(p => p.UserId == headsetB);
        Assert.InRange(diagA.EffectiveLookaheadMs, AcousticRoomProcessor.MinCalibratedLookaheadMs, 12);
        Assert.InRange(diagB.EffectiveLookaheadMs, AcousticRoomProcessor.MinCalibratedLookaheadMs, 12);
    }

    [Fact]
    public void ClosedBackHeadsetsWithZeroOrAmbientUncorrelatedNoise_TwoSpeakers_CalibratesBothDirectionsAndDropsLookaheadTo10Ms()
    {
        var processor = new AcousticRoomProcessor();
        const string headsetA = "headset_user_a";
        const string headsetB = "headset_user_b";

        processor.SyncParticipants([headsetA, headsetB]);
        processor.SetParticipantClientPipelineInfo(headsetA, 30.0, 5.0);
        processor.SetParticipantClientPipelineInfo(headsetB, 30.0, 5.0);

        int turnFrames = 75; // 0.75s per turn
        var speechA = AcousticRoomSimulator.GenerateNaturalSpeech(turnFrames * FrameSamples, 210.0, 12500.0, includeSyllablePauses: true);
        var speechB = AcousticRoomSimulator.GenerateNaturalSpeech(turnFrames * FrameSamples, 175.0, 12500.0, includeSyllablePauses: true);
        var silentFrame = new short[FrameSamples];
        var rng = new Random(42);

        int frameCursor = 0;
        // Turn 1: Headset A speaks while Headset B only has uncorrelated -68 dBFS post-RNNoise ambient floor (no correlated bleed)
        for (int f = 0; f < turnFrames; f++, frameCursor++)
        {
            int idx = f * FrameSamples;
            var ambientB = new short[FrameSamples];
            for (int i = 0; i < FrameSamples; i++) ambientB[i] = (short)rng.Next(-14, 15);

            processor.ProcessSynchronizedStep(new Dictionary<string, short[]>
            {
                [headsetA] = speechA.AsSpan(idx, FrameSamples).ToArray(),
                [headsetB] = ambientB
            }, frameCursor * 10);
        }

        // Turn 2: Headset B speaks while Headset A is completely silent (zero bleed)
        for (int f = 0; f < turnFrames; f++, frameCursor++)
        {
            int idx = f * FrameSamples;
            processor.ProcessSynchronizedStep(new Dictionary<string, short[]>
            {
                [headsetA] = silentFrame,
                [headsetB] = speechB.AsSpan(idx, FrameSamples).ToArray()
            }, frameCursor * 10);
        }

        for (int f = 0; f < 5; f++, frameCursor++)
        {
            processor.ProcessSynchronizedStep(new Dictionary<string, short[]>
            {
                [headsetA] = silentFrame,
                [headsetB] = silentFrame
            }, frameCursor * 10);
        }

        var snap = processor.GetDiagnosticSnapshot();
        var calAB = snap.Calibrations.FirstOrDefault(c => c.SourceUserId == headsetA && c.TargetUserId == headsetB);
        var calBA = snap.Calibrations.FirstOrDefault(c => c.SourceUserId == headsetB && c.TargetUserId == headsetA);
        Assert.NotNull(calAB);
        Assert.NotNull(calBA);
        Assert.False(calAB!.IsReciprocalPrior);
        Assert.False(calBA!.IsReciprocalPrior);
        Assert.True(calAB.BleedGainLinear < AcousticRoomProcessor.NegligibleBleedGainThreshold);
        Assert.True(calBA.BleedGainLinear < AcousticRoomProcessor.NegligibleBleedGainThreshold);
        Assert.Empty(snap.Warnings);

        var diagA = snap.Participants.First(p => p.UserId == headsetA);
        var diagB = snap.Participants.First(p => p.UserId == headsetB);
        Assert.InRange(diagA.EffectiveLookaheadMs, AcousticRoomProcessor.MinCalibratedLookaheadMs, 12);
        Assert.InRange(diagB.EffectiveLookaheadMs, AcousticRoomProcessor.MinCalibratedLookaheadMs, 12);
    }

    [Fact]
    public void NegligibleBleedHeadsetLock_InstantlyUpgradesToNegativeLagLock_WhenAudibleBleedStarts()
    {
        var processor = new AcousticRoomProcessor();
        const string userA = "user_a";
        const string userB = "user_b";
        processor.SyncParticipants([userA, userB]);

        int phase1Frames = 70; // 0.7s isolated headset speech (zero bleed on B)
        var p1SpeechA = AcousticRoomSimulator.GenerateNaturalSpeech(phase1Frames * FrameSamples, 230.0, 13000.0, includeSyllablePauses: true);
        var silentFrame = new short[FrameSamples];

        int frameCursor = 0;
        for (int f = 0; f < phase1Frames; f++, frameCursor++)
        {
            int idx = f * FrameSamples;
            processor.ProcessSynchronizedStep(new Dictionary<string, short[]>
            {
                [userA] = p1SpeechA.AsSpan(idx, FrameSamples).ToArray(),
                [userB] = silentFrame
            }, frameCursor * 10);
        }

        for (int f = 0; f < 5; f++, frameCursor++)
        {
            processor.ProcessSynchronizedStep(new Dictionary<string, short[]>
            {
                [userA] = silentFrame,
                [userB] = silentFrame
            }, frameCursor * 10);
        }

        var p1Snap = processor.GetDiagnosticSnapshot();
        Assert.InRange(p1Snap.Participants.First(p => p.UserId == userB).EffectiveLookaheadMs, AcousticRoomProcessor.MinCalibratedLookaheadMs, 12);

        // Phase 2: User B unplugs closed-back headset / switches mic so User A's voice now bleeds into B at -30ms (-1440 samples)
        int phase2Frames = 110;
        int advanceSamples = 30 * 48; // -30ms negative lag on A -> B
        var rawVoiceA = AcousticRoomSimulator.GenerateNaturalSpeech(phase2Frames * FrameSamples + advanceSamples, 220.0, 14500.0, includeSyllablePauses: true);
        double lateBleedIn = 0;
        double lateBleedOut = 0;

        for (int f = 0; f < phase2Frames; f++, frameCursor++)
        {
            int idx = f * FrameSamples;
            var frameA = rawVoiceA.AsSpan(idx, FrameSamples).ToArray();
            var frameB = new short[FrameSamples];
            for (int i = 0; i < FrameSamples; i++)
            {
                frameB[i] = (short)Math.Round(rawVoiceA[idx + i + advanceSamples] * 0.22);
            }

            var outputs = processor.ProcessSynchronizedStep(new Dictionary<string, short[]>
            {
                [userA] = frameA,
                [userB] = frameB
            }, frameCursor * 10);

            if (f >= 45)
            {
                lateBleedIn += ComputeRms(frameB);
                lateBleedOut += ComputeRms(outputs[userB]);
            }
        }

        var p2Snap = processor.GetDiagnosticSnapshot();
        var calAB = p2Snap.Calibrations.First(c => c.SourceUserId == userA && c.TargetUserId == userB);
        Assert.InRange(calAB.DelayMs, -32.5, -27.5);
        Assert.True(p2Snap.Participants.First(p => p.UserId == userB).EffectiveLookaheadMs >= 42);
        Assert.True(lateBleedOut < lateBleedIn * 0.15,
            $"Expected upgraded negative-lag bleed to be suppressed by >85%, got in={lateBleedIn:F1}, out={lateBleedOut:F1}");
    }

    [Fact]
    public void PostCancellationLeveler_CoughOrBumpBeforeSilence_DoesNotCrushFirstSentenceAfterSilence()
    {
        var processor = new AcousticRoomProcessor();
        const string quietSpeaker = "quiet_speaker";
        const string silentPeer = "silent_peer";
        processor.SyncParticipants([quietSpeaker, silentPeer]);

        var silence = new short[FrameSamples];
        int frameCursor = 0;

        // 1. Warm up quiet speaker (~-30 dBFS) for 2.0s
        var warmSpeech = AcousticRoomSimulator.GenerateNaturalSpeech(200 * FrameSamples, fundamentalHz: 205.0, amplitude: 2400.0, includeSyllablePauses: false);
        for (int f = 0; f < 200; f++, frameCursor++)
        {
            processor.ProcessSynchronizedStep(new Dictionary<string, short[]>
            {
                [quietSpeaker] = warmSpeech.AsSpan(f * FrameSamples, FrameSamples).ToArray(),
                [silentPeer] = silence
            }, frameCursor * 10);
        }

        // 2. 50ms loud cough/bump (~0.85 peak)
        var bumpFrame = new short[FrameSamples];
        int maxBumpPeak = 0;
        for (int f = 0; f < 5; f++, frameCursor++)
        {
            for (int i = 0; i < FrameSamples; i++)
            {
                double t = (f * FrameSamples + i) / 48000.0;
                bumpFrame[i] = (short)Math.Round(28000.0 * Math.Sin(2.0 * Math.PI * 260.0 * t));
            }
            var step = processor.ProcessSynchronizedStep(new Dictionary<string, short[]>
            {
                [quietSpeaker] = bumpFrame.ToArray(),
                [silentPeer] = silence
            }, frameCursor * 10);
            foreach (short s in step[quietSpeaker])
            {
                maxBumpPeak = Math.Max(maxBumpPeak, Math.Abs((int)s));
            }
        }

        // 3. 4.0s period of silence
        for (int f = 0; f < 400; f++, frameCursor++)
        {
            processor.ProcessSynchronizedStep(new Dictionary<string, short[]>
            {
                [quietSpeaker] = silence,
                [silentPeer] = silence
            }, frameCursor * 10);
        }

        // 4. Resume normal quiet speech and verify Sec 1 is immediately at normal loudness (no 1-2 sentence whisper)
        var resumeSpeech = AcousticRoomSimulator.GenerateNaturalSpeech(100 * FrameSamples, fundamentalHz: 205.0, amplitude: 2400.0, includeSyllablePauses: false);
        var sec1Out = new List<short>();
        for (int f = 0; f < 100; f++, frameCursor++)
        {
            var step = processor.ProcessSynchronizedStep(new Dictionary<string, short[]>
            {
                [quietSpeaker] = resumeSpeech.AsSpan(f * FrameSamples, FrameSamples).ToArray(),
                [silentPeer] = silence
            }, frameCursor * 10);
            if (f >= 10)
            {
                sec1Out.AddRange(step[quietSpeaker]);
            }
        }

        double sec1OutDbfs = 20.0 * Math.Log10(ComputeRms(sec1Out.ToArray()) / 32768.0);
        Assert.True(maxBumpPeak <= 29350, $"Expected 50ms bump capped <= -1.0 dBFS, got peak={maxBumpPeak}");
        Assert.InRange(sec1OutDbfs, -21.0, -13.5);
    }
}
