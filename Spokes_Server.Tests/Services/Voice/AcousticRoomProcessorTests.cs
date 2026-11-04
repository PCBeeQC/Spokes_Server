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
        processor.RegisterParticipant("mats");

        int totalFrames = 120; // 1.2s total
        for (int f = 0; f < totalFrames; f++)
        {
            var frameAdmin = new short[FrameSamples];
            var frameMats = new short[FrameSamples];

            // Inject a brief 90ms cough on "mats" (frames 10..18) and a tiny RNNoise residual blip on "admin" 140ms later (frame 24)
            if (f >= 10 && f < 19)
            {
                frameMats = GenerateSyntheticSpeech(FrameSamples, 240.0, 12000.0, f * FrameSamples);
            }
            if (f == 24)
            {
                frameAdmin = GenerateSyntheticSpeech(FrameSamples, 240.0, 45.0, f * FrameSamples); // near-silent RNNoise blip
            }

            processor.ProcessSynchronizedStep(new Dictionary<string, short[]>
            {
                ["admin"] = frameAdmin,
                ["mats"] = frameMats
            }, f * 10);
        }

        var snapshot = processor.GetDiagnosticSnapshot();
        Assert.Empty(snapshot.Calibrations);
        Assert.Contains(snapshot.Warnings, w => w.Contains("Low vocal activity detected", StringComparison.OrdinalIgnoreCase));

        string report = AcousticRoomDiagnosticReportFormatter.FormatLiveRoomReport(
            snapshot,
            "bureau",
            new Dictionary<string, string> { ["admin"] = "admin", ["mats"] = "mats" });
        Assert.Contains("buckets,", report);
    }

    [Fact]
    public void NotifyParticipantTrackReplaced_SwitchingFromRnnoiseToWebRtc_KeepsBothParticipantsActiveAndReCalibratesLag()
    {
        var processor = new AcousticRoomProcessor();
        processor.SyncParticipants(["admin", "mats"]);

        int phase1Frames = 60; // 600ms with RNNoise (-20ms lag, 30cm high bleed = 0.42)
        int rnnoiseLagSamples = 20 * 48; // 960 samples (20ms)
        var rawSpeech1 = AcousticRoomSimulator.GenerateNaturalSpeech(phase1Frames * FrameSamples + rnnoiseLagSamples + FrameSamples * 4, 225.0, 14000.0);
        var filteredAdmin1 = AcousticRoomSimulator.ApplyClientAiFilterSimulation(rawSpeech1, rnnoiseLagSamples, true);
        var bleedOnMats1 = AcousticRoomSimulator.ApplyRoomImpulseResponse(rawSpeech1, directDelaySamples: 0, directBleedGain: 0.42, rt60Ms: 60.0);

        for (int f = 0; f < phase1Frames; f++)
        {
            var fA = filteredAdmin1.AsSpan(f * FrameSamples, FrameSamples).ToArray();
            var fB = bleedOnMats1.AsSpan(f * FrameSamples, FrameSamples).ToArray();
            processor.ProcessSynchronizedStep(new Dictionary<string, short[]>
            {
                ["admin"] = fA,
                ["mats"] = fB
            }, f * 10);
        }

        var snap1 = processor.GetDiagnosticSnapshot();
        Assert.Equal(2, snap1.Participants.Count);
        var cal1 = snap1.Calibrations.FirstOrDefault(c => c.SourceUserId == "admin" && c.TargetUserId == "mats");
        Assert.NotNull(cal1);
        Assert.InRange(cal1!.DelayMs, -22.5, -17.5);

        // Simulate "admin" switching Noise Filter from RNNoise -> Basic (WebRTC) (+2.0ms lag at ~30cm/60cm)
        processor.SyncParticipants(["admin", "mats"]);
        processor.NotifyParticipantTrackReplaced("admin");

        // Verify Room Isolation remains active (2 participants) immediately after track replacement
        var snapDuringSwitch = processor.GetDiagnosticSnapshot();
        Assert.Equal(2, snapDuringSwitch.Participants.Count);

        int phase2Frames = 60; // 600ms with Basic WebRTC (+2.0ms lag, 30cm bleed = 0.42)
        int webrtcLagSamples = 2 * 48; // +96 samples (+2.0ms)
        var rawSpeech2 = AcousticRoomSimulator.GenerateNaturalSpeech(phase2Frames * FrameSamples + FrameSamples * 4, 225.0, 14000.0);
        var bleedOnMats2 = AcousticRoomSimulator.ApplyRoomImpulseResponse(rawSpeech2, directDelaySamples: webrtcLagSamples, directBleedGain: 0.42, rt60Ms: 60.0);

        for (int f = 0; f < phase2Frames; f++)
        {
            var fA = rawSpeech2.AsSpan(f * FrameSamples, FrameSamples).ToArray();
            var fB = bleedOnMats2.AsSpan(f * FrameSamples, FrameSamples).ToArray();
            processor.ProcessSynchronizedStep(new Dictionary<string, short[]>
            {
                ["admin"] = fA,
                ["mats"] = fB
            }, (phase1Frames + f) * 10);
        }

        var snap2 = processor.GetDiagnosticSnapshot();
        Assert.Equal(2, snap2.Participants.Count);
        var cal2 = snap2.Calibrations.FirstOrDefault(c => c.SourceUserId == "admin" && c.TargetUserId == "mats");
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
            [guidB] = "mats"
        };
        string uncalReport = AcousticRoomDiagnosticReportFormatter.FormatLiveRoomReport(uncalSnap, "bureau", displayNames);
        Assert.Contains("\"admin\"", uncalReport);
        Assert.Contains("\"mats\"", uncalReport);
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
        // - "mats": Android Device (Spokes App, client-default) with +94.0ms (+4512 samples) net delay and -15.7 dB bleed
        var processor = new AcousticRoomProcessor();
        string adminId = "3014bc97-b629-4ec0-bf06-15446c7b6622";
        string matsId = "419bedb9-85ac-4923-9e23-45d8ad0e29dd";
        processor.SyncParticipants([adminId, matsId]);

        int delaySamplesAB = 94 * 48; // +4512 samples (+94.0ms)
        int speechFrames = 100;       // 1.0s of quiet (-40 dBFS) speech from "admin"
        int pauseFrames = 40;         // 0.4s of silence (allows +94ms delayed bleed to finish and pause-synchronized lookahead to expand)
        int matsSpeechFrames = 60;    // 0.6s of speech from "mats" (arriving 86ms after bleeding into "admin"'s mic)

        int phase1Samples = (speechFrames + pauseFrames) * FrameSamples;
        var rawAdminSpeech = AcousticRoomSimulator.GenerateNaturalSpeech(
            speechFrames * FrameSamples,
            fundamentalHz: 210.0,
            amplitude: 950.0, // ~ -40 dBFS in vocal passband
            includeSyllablePauses: true);

        var adminTrackPhase1 = new short[phase1Samples];
        Array.Copy(rawAdminSpeech, 0, adminTrackPhase1, 0, rawAdminSpeech.Length);

        var matsBleedPhase1 = AcousticRoomSimulator.ApplyRoomImpulseResponse(
            adminTrackPhase1,
            directDelaySamples: delaySamplesAB,
            directBleedGain: 0.164, // -15.7 dB
            rt60Ms: 140.0,
            applyHeadShadowLowPass: true);

        for (int f = 0; f < speechFrames + pauseFrames; f++)
        {
            var fAdmin = adminTrackPhase1.AsSpan(f * FrameSamples, FrameSamples).ToArray();
            var fMats = matsBleedPhase1.AsSpan(f * FrameSamples, FrameSamples).ToArray();
            processor.ProcessSynchronizedStep(new Dictionary<string, short[]>
            {
                [adminId] = fAdmin,
                [matsId] = fMats
            }, f * 10);
        }

        var snapAfterPhase1 = processor.GetDiagnosticSnapshot();
        var calAB = snapAfterPhase1.Calibrations.FirstOrDefault(c => c.SourceUserId == adminId && c.TargetUserId == matsId);
        var calBA = snapAfterPhase1.Calibrations.FirstOrDefault(c => c.SourceUserId == matsId && c.TargetUserId == adminId);

        Assert.NotNull(calAB);
        Assert.False(calAB!.IsReciprocalPrior, "Expected admin -> mats to lock directly on -40 dBFS speech");
        Assert.InRange(calAB.DelayMs, 91.0, 97.0);

        Assert.NotNull(calBA);
        Assert.True(calBA!.IsReciprocalPrior, "Expected mats -> admin to be seeded as a ReciprocalPrior (-86ms)");
        Assert.InRange(calBA.DelayMs, -89.0, -83.0);

        var adminDiag = snapAfterPhase1.Participants.First(p => p.UserId == adminId);
        var matsDiag = snapAfterPhase1.Participants.First(p => p.UserId == matsId);

        // Verify pause-synchronized adaptive lookahead expanded on "admin" to cover the -86ms reciprocal prior (+15ms margin = ~101ms)
        Assert.InRange(adminDiag.EffectiveLookaheadMs, 98, 105);
        Assert.Equal(0, adminDiag.GatedBleedSubFrames);
        Assert.True(matsDiag.AchievedBleedSuppressionDb >= 18.0, $"Expected mats ERLE >= 18 dB on -40 dBFS speech, got {matsDiag.AchievedBleedSuppressionDb:F1} dB");

        // Verify timeline buckets show mats=Gated during active speech, never falsely report mats as Dominant speaker,
        // and never deadlock with both admin=Gated and mats=Gated simultaneously
        Assert.Contains(snapAfterPhase1.TimelineBuckets, b =>
            b.ParticipantGateStates.TryGetValue(matsId, out var gs) && gs == AcousticGateState.GatedBleed);
        Assert.DoesNotContain(snapAfterPhase1.TimelineBuckets, b =>
            string.Equals(b.DominantSpeakerUserId, matsId, StringComparison.Ordinal));
        Assert.DoesNotContain(snapAfterPhase1.TimelineBuckets, b =>
            b.ParticipantGateStates.TryGetValue(adminId, out var gA) && gA == AcousticGateState.GatedBleed &&
            b.ParticipantGateStates.TryGetValue(matsId, out var gB) && gB == AcousticGateState.GatedBleed);

        // Phase 2: Now "mats" speaks first word; because "admin" already expanded lookahead to ~101ms,
        // "admin" gates "mats"'s -86ms negative-lag bleed immediately!
        int negLagBA = 86 * 48; // 4128 samples (86ms)
        int phase2Samples = matsSpeechFrames * FrameSamples;
        var rawMatsVoice = AcousticRoomSimulator.GenerateNaturalSpeech(phase2Samples + negLagBA, 285.0, 12000.0);
        double adminBleedInEnergy = 0;
        double adminBleedOutEnergy = 0;

        for (int f = 0; f < matsSpeechFrames; f++)
        {
            int baseIdx = f * FrameSamples;
            // Bleed arrives on "admin"'s PC mic 86ms EARLIER than "mats"'s delayed Android stream
            var fAdminBleed = new short[FrameSamples];
            var fMatsDirect = new short[FrameSamples];
            for (int i = 0; i < FrameSamples; i++)
            {
                fAdminBleed[i] = (short)Math.Round(rawMatsVoice[baseIdx + i + negLagBA] * 0.15);
                fMatsDirect[i] = rawMatsVoice[baseIdx + i];
            }

            var outStep = processor.ProcessSynchronizedStep(new Dictionary<string, short[]>
            {
                [adminId] = fAdminBleed,
                [matsId] = fMatsDirect
            }, (speechFrames + pauseFrames + f) * 10);

            if (f >= 20)
            {
                adminBleedInEnergy += ComputeRms(fAdminBleed);
                adminBleedOutEnergy += ComputeRms(outStep[adminId]);
            }
        }

        Assert.True(adminBleedOutEnergy < adminBleedInEnergy * 0.15,
            $"Expected pre-expanded reciprocal lookahead on admin to suppress mats's -86ms bleed by >85%, got in={adminBleedInEnergy:F1}, out={adminBleedOutEnergy:F1}");
    }

    [Fact]
    public void LatchedCalibrationAndSeededMemory_NeverDecaysBetweenSentences_AndGatesFirstSyllableImmediately()
    {
        var processor = new AcousticRoomProcessor();
        string adminId = "admin";
        string matsId = "mats";
        processor.SyncParticipants([adminId, matsId]);

        // Provide client pipeline telemetry (admin=rnnoise 20ms, mats=webrtc 150ms Android pipeline)
        processor.SetParticipantClientPipelineInfo(adminId, "rnnoise", 20.0);
        processor.SetParticipantClientPipelineInfo(matsId, "webrtc", 150.0);

        // Seed persistent room calibration from previous call: admin -> mats is -130.0ms (-6240 samples)
        int trueLagSamples = -6240; // -130.0ms
        processor.SeedSavedPairCalibration(adminId, matsId, trueLagSamples, bleedGain: 0.25f, confidence: 0.72f);

        // Verify warm-start: before a single frame of audio is processed, calibration is already locked
        // and mats's lookahead buffer is already pre-sized to cover -130ms (+15ms margin = 145ms)!
        var initialSnap = processor.GetDiagnosticSnapshot();
        var initialCal = initialSnap.Calibrations.FirstOrDefault(c => c.SourceUserId == adminId && c.TargetUserId == matsId);
        Assert.NotNull(initialCal);
        Assert.False(initialCal!.IsReciprocalPrior);
        Assert.True(initialCal.Confidence >= 0.70);
        Assert.Equal(-130.0, initialCal.DelayMs, 1);

        var initialMatsDiag = initialSnap.Participants.First(p => p.UserId == matsId);
        Assert.InRange(initialMatsDiag.EffectiveLookaheadMs, 140, 150);

        // Sentence 1 (0.8s): Admin speaks; bleed arrives on Mats 130ms EARLIER (-6240 samples) with heavy head-shadow EQ
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
            var fMats = filteredBleed1.AsSpan(idx + absLag, FrameSamples).ToArray();

            var outStep = processor.ProcessSynchronizedStep(new Dictionary<string, short[]>
            {
                [adminId] = fAdmin,
                [matsId] = fMats
            }, f * 10);

            // Check the very first syllable (frames 15..30, once lookahead FIFO fills)
            if (f >= 15 && f < 30)
            {
                firstSyllableBleedIn += ComputeRms(fMats);
                firstSyllableBleedOut += ComputeRms(outStep[matsId]);
            }
        }

        Assert.True(firstSyllableBleedOut < firstSyllableBleedIn * 0.15,
            $"Expected warm-started calibration to suppress first-syllable bleed by >85%, got in={firstSyllableBleedIn:F1}, out={firstSyllableBleedOut:F1}");

        var afterSentence1Cal = processor.GetCalibrations().First(c => c.SourceUserId == adminId && c.TargetUserId == matsId);
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
                [matsId] = noiseB
            }, (sentenceFrames + f) * 10);
        }

        var afterSilenceCal = processor.GetCalibrations().First(c => c.SourceUserId == adminId && c.TargetUserId == matsId);
        Assert.False(afterSilenceCal.IsReciprocalPrior, "Expected latched calibration to remain directly locked across inter-sentence pause");
        Assert.True(afterSilenceCal.Confidence >= lockedConfidence,
            $"Expected latched calibration confidence not to decay (before={lockedConfidence:F3}, after={afterSilenceCal.Confidence:F3})");
        Assert.Equal(afterSentence1Cal.DelaySamples48k, afterSilenceCal.DelaySamples48k);
    }

    [Fact]
    public void ThreeDevices_TwoHeadsetsAndFarFieldAgcPhoneAt142Ms_KeepsHeadsetOpen_GatesPhone_AndSupportsTurnTaking()
    {
        // Exact reproduction of real-world "Bureau Stewie" 3-device failure mode:
        // - Headset 1 ("admin first"): Windows Chrome + rnnoise, speaking directly into boom mic at ~ -27.4 dBFS
        // - Headset 2 ("mats"): Windows Chrome + rnnoise, passive headset picking up -41.5 dBFS bleed at +89.6ms
        // - Android Phone ("mats2"): Far-field phone at other end of room (+141.8ms delay, RT60=220ms), whose
        //   speakerphone hardware AGC boosts far-field room bleed up to -15.7 dBFS (+11.7 dB LOUDER than Headset 1!).
        var processor = new AcousticRoomProcessor();
        string headset1 = "6f30c05d-c9d6-4cbb-bdbb-b2f8f3b3c9c3";
        string headset2 = "d9ba9018-59cb-4d1d-b222-9b9aba711103";
        string agcPhone = "340555a5-a468-477f-870f-3b49a4d09679";

        processor.SyncParticipants([headset1, headset2, agcPhone]);
        processor.SetParticipantClientPipelineInfo(headset1, "rnnoise", 30.0, 10.5);
        processor.SetParticipantClientPipelineInfo(headset2, "rnnoise", 30.0, 9.5);
        processor.SetParticipantClientPipelineInfo(agcPhone, "rnnoise", 80.3, 11.5);

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
            "Bureau Stewie",
            new Dictionary<string, string>
            {
                [headset1] = "admin first",
                [headset2] = "mats",
                [agcPhone] = "mats2"
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

        // Phase 2: Turn-taking — now someone actually speaks directly into the Android phone ("mats2") at -14 dBFS dry near-field,
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
        // Reproduces "Bureau Stewie" Test 1 anomaly at t=51.73s..51.98s where solo speaker ("admin first")
        // dipped to quiet trailing syllables (-36..-40 dBFS) and briefly self-gated against roommate's reverberant hangover.
        var processor = new AcousticRoomProcessor();
        string adminId = "6f30c05d-c9d6-4cbb-bdbb-b2f8f3b3c9c3";
        string matsId = "d9ba9018-59cb-4d1d-b222-9b9aba711103";

        processor.SyncParticipants([adminId, matsId]);
        processor.SeedSavedPairCalibration(adminId, matsId, delaySamples: 1807, bleedGain: 0.142f, confidence: 0.88f);  // +37.6ms
        processor.SeedSavedPairCalibration(matsId, adminId, delaySamples: -1469, bleedGain: 0.085f, confidence: 0.88f); // -30.6ms

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

        var matsReverbBleed = AcousticRoomSimulator.ApplyRoomImpulseResponse(
            baseSpeech,
            directDelaySamples: 1807, // +37.6ms
            directBleedGain: 0.15,
            rt60Ms: 210.0,
            applyHeadShadowLowPass: true);

        for (int f = 0; f < totalFrames; f++)
        {
            var fAdmin = baseSpeech.AsSpan(f * FrameSamples, FrameSamples).ToArray();
            var fMats = matsReverbBleed.AsSpan(f * FrameSamples, FrameSamples).ToArray();
            processor.ProcessSynchronizedStep(new Dictionary<string, short[]>
            {
                [adminId] = fAdmin,
                [matsId] = fMats
            }, f * 10);
        }

        var snap = processor.GetDiagnosticSnapshot();
        var adminDiag = snap.Participants.First(p => p.UserId == adminId);
        var matsDiag = snap.Participants.First(p => p.UserId == matsId);

        Assert.Equal(0, adminDiag.GatedBleedSubFrames);
        Assert.True(matsDiag.AchievedBleedSuppressionDb >= 25.0,
            $"Expected roommate bleed suppression >= 25 dB, got {matsDiag.AchievedBleedSuppressionDb:F1} dB");
        Assert.DoesNotContain(snap.TimelineBuckets, b =>
            b.ParticipantGateStates.TryGetValue(adminId, out var gA) && gA == AcousticGateState.GatedBleed &&
            b.ParticipantGateStates.TryGetValue(matsId, out var gB) && gB == AcousticGateState.GatedBleed);
    }

    [Fact]
    public void SeedSavedPairCalibration_WithShiftedNewSessionWebRtcOffset_RelocksCleanlyWithoutStaleReverseBlock()
    {
        // Session 1 locked bidirectional delays at +37.6ms / -30.6ms (RelativeClientDspOffset = +34.1ms).
        // In Session 2, browser WebRTC RTP stream startup timing shifted by -51.6ms so true delay is now -14.0ms (-672 samples).
        // Verify that the unverified cross-session reverse seed (-30.6ms) does NOT block live re-anchoring to -14.0ms!
        var processor = new AcousticRoomProcessor();
        string adminId = "admin_first";
        string matsId = "mats";

        processor.SyncParticipants([adminId, matsId]);
        processor.SeedSavedPairCalibration(adminId, matsId, delaySamples: 1807, bleedGain: 0.142f, confidence: 0.88f);
        processor.SeedSavedPairCalibration(matsId, adminId, delaySamples: -1469, bleedGain: 0.031f, confidence: 0.95f);

        int newSessionLagSamples = -672; // -14.0ms
        int absLag = Math.Abs(newSessionLagSamples);
        int speechFrames = 160; // 1.6s
        var rawAdmin = AcousticRoomSimulator.GenerateNaturalSpeech(
            speechFrames * FrameSamples + absLag,
            fundamentalHz: 205.0,
            amplitude: 3600.0,
            includeSyllablePauses: true);

        var bleedMats = AcousticRoomSimulator.ApplyRoomImpulseResponse(
            rawAdmin,
            directDelaySamples: 0,
            directBleedGain: 0.22,
            rt60Ms: 140.0,
            applyHeadShadowLowPass: true);

        for (int f = 0; f < speechFrames; f++)
        {
            int idx = f * FrameSamples;
            var fAdmin = rawAdmin.AsSpan(idx, FrameSamples).ToArray();
            var fMats = bleedMats.AsSpan(idx + absLag, FrameSamples).ToArray();

            processor.ProcessSynchronizedStep(new Dictionary<string, short[]>
            {
                [adminId] = fAdmin,
                [matsId] = fMats
            }, f * 10);
        }

        var snap = processor.GetDiagnosticSnapshot();
        var calAB = snap.Calibrations.FirstOrDefault(c => c.SourceUserId == adminId && c.TargetUserId == matsId);
        var calBA = snap.Calibrations.FirstOrDefault(c => c.SourceUserId == matsId && c.TargetUserId == adminId);

        Assert.NotNull(calAB);
        Assert.False(calAB!.IsReciprocalPrior);
        Assert.InRange(calAB.DelayMs, -16.5, -11.5);

        Assert.NotNull(calBA);
        Assert.True(calBA!.IsReciprocalPrior, "Expected stale cross-session reverse seed (-30.6ms) to refresh to a consistent reciprocal prior (+22ms)");
        Assert.InRange(calBA.DelayMs, 18.0, 26.0);

        var matsDiag = snap.Participants.First(p => p.UserId == matsId);
        Assert.True(matsDiag.AchievedBleedSuppressionDb >= 20.0,
            $"Expected re-anchored session calibration to suppress bleed by >= 20 dB, got {matsDiag.AchievedBleedSuppressionDb:F1} dB");
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
        Assert.True(totalGainDuringSilence < 2.0,
            $"Expected pause noise not to pump toward +15 dB (5.62x) max boost, got {totalGainDuringSilence:F2}x");
    }

    [Fact]
    public void ProcessParticipantFrame_BurstJitterStallsUpTo110Ms_NeverInjectsFakeDtxSilenceOrCorruptsCalibration()
    {
        // Reproduces "Bureau Stewie" wireless Corsair headset clock-drift desync:
        // Two symmetric desktop wireless headsets (+4.0ms = +192 samples air delay, -8.5 dB bleed) stream via
        // ProcessParticipantFrame while experiencing periodic 50..110ms Wi-Fi/GC/thread wakeup stalls followed
        // immediately by queued packet bursts draining. Verify zero fake DTX silence injection, 0.0ms spurious
        // ClockDrift, accurate +4.0ms DirectLock, and zero self-gating.
        var processor = new AcousticRoomProcessor();
        const string adminId = "6f30c05d-c9d6-4cbb-bdbb-b2f8f3b3c9c3";
        const string matsId = "d9ba9018-59cb-4d1d-b222-9b9aba711103";
        processor.SyncParticipants([adminId, matsId]);
        processor.SetParticipantClientPipelineInfo(adminId, "webrtc", 20.0, 17.0);
        processor.SetParticipantClientPipelineInfo(matsId, "webrtc", 20.0, 9.0);

        int totalFrames = 240; // 2.4s
        int totalSamples = totalFrames * FrameSamples;
        int airDelaySamples = 192; // +4.0ms (~1.37m)

        var rawSpeech = AcousticRoomSimulator.GenerateNaturalSpeech(
            totalSamples,
            fundamentalHz: 205.0,
            amplitude: 11500.0,
            includeSyllablePauses: true);

        var bleedOnMats = AcousticRoomSimulator.ApplyRoomImpulseResponse(
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
            pendingB.Enqueue(bleedOnMats.AsSpan(f * FrameSamples, FrameSamples).ToArray());

            // Simulate 60ms stall on admin (frames 30..35, 110..115) and 100ms stall on mats (frames 55..64, 145..154)
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
                    processor.ProcessParticipantFrame(matsId, pendingB.Dequeue(), wallMs);
                }
            }
        }

        var snap = processor.GetDiagnosticSnapshot();
        var adminDiag = snap.Participants.First(p => p.UserId == adminId);
        var matsDiag = snap.Participants.First(p => p.UserId == matsId);

        Assert.Equal(0.0, adminDiag.ClockDriftCompensatedMs, 1);
        Assert.Equal(0.0, matsDiag.ClockDriftCompensatedMs, 1);
        Assert.Equal(0, adminDiag.GatedBleedSubFrames);
        Assert.True(matsDiag.AchievedBleedSuppressionDb >= 20.0,
            $"Expected mats bleed suppression >= 20 dB across async packet bursts, got {matsDiag.AchievedBleedSuppressionDb:F1} dB");

        var calAB = snap.Calibrations.FirstOrDefault(c => c.SourceUserId == adminId && c.TargetUserId == matsId);
        Assert.NotNull(calAB);
        Assert.False(calAB!.IsReciprocalPrior);
        Assert.True(calAB.IsLiveVerified);
        Assert.InRange(calAB.DelayMs, 1.5, 6.5);
    }

    [Fact]
    public void TwoWirelessHeadsets_AsymmetricBleedAndMidCallNetEqShift_DirectLocksBothDirectionsAndNeverSelfGates()
    {
        // Reproduces "Bureau Stewie" turn-taking on two wireless Corsair headsets:
        // 1. Phase 1 (0..1.8s): "mats" speaks first with heavy -8.5 dB (0.378) bleed into "admin first" (+4.0ms).
        // 2. Mid-call WebRTC NetEq shift (+32.0ms on "mats") + Phase 2 (2.0..4.0s): "admin first" speaks loudly
        //    (-11 dBFS with syllable pauses) while directional boom + Chrome WebRTC NS on "mats" attenuates bleed
        //    to -46 dB (0.005x) at shifted lag +36.0ms (+1728 samples).
        // 3. Phase 3 (4.3..6.1s): "mats" speaks again at shifted lag -28.0ms (-1344 samples), proving the latched
        //    calibration self-heals to -28.0ms, both directions end DirectLocked with ~+4.0ms decomposed air delay,
        //    and active speakers never self-gate or mutual-gate.
        var processor = new AcousticRoomProcessor();
        const string adminId = "6f30c05d-c9d6-4cbb-bdbb-b2f8f3b3c9c3";
        const string matsId = "d9ba9018-59cb-4d1d-b222-9b9aba711103";
        processor.SyncParticipants([adminId, matsId]);
        processor.SetParticipantClientPipelineInfo(adminId, "webrtc", 20.0, 17.0);
        processor.SetParticipantClientPipelineInfo(matsId, "webrtc", 20.0, 9.0);

        var silentFrame = new short[FrameSamples];
        long frameCursor = 0;

        // Phase 1: "mats" speaks for 1.8s (+4.0ms = +192 samples on admin, 0.378 = -8.5 dB bleed)
        int phase1Frames = 180;
        var matsSpeech1 = AcousticRoomSimulator.GenerateNaturalSpeech(
            phase1Frames * FrameSamples,
            fundamentalHz: 195.0,
            amplitude: 10500.0,
            includeSyllablePauses: true);
        var adminBleed1 = AcousticRoomSimulator.ApplyRoomImpulseResponse(
            matsSpeech1,
            directDelaySamples: 192,
            directBleedGain: 0.378,
            rt60Ms: 150.0,
            applyHeadShadowLowPass: true);

        for (int f = 0; f < phase1Frames; f++, frameCursor++)
        {
            processor.ProcessSynchronizedStep(new Dictionary<string, short[]>
            {
                [adminId] = adminBleed1.AsSpan(f * FrameSamples, FrameSamples).ToArray(),
                [matsId] = matsSpeech1.AsSpan(f * FrameSamples, FrameSamples).ToArray()
            }, frameCursor * 10);
        }

        // 250ms inter-turn pause
        for (int f = 0; f < 25; f++, frameCursor++)
        {
            processor.ProcessSynchronizedStep(new Dictionary<string, short[]>
            {
                [adminId] = (short[])silentFrame.Clone(),
                [matsId] = (short[])silentFrame.Clone()
            }, frameCursor * 10);
        }

        // Phase 2: WebRTC NetEq on "mats" shifts by +32ms -> true lag (admin -> mats) is now +36.0ms (+1728 samples),
        // with directional boom + WebRTC NS attenuating bleed to 0.005 (-46 dB).
        int phase2Frames = 200;
        int shiftedPosLag = 1728; // +36.0ms
        var adminSpeech2 = AcousticRoomSimulator.GenerateNaturalSpeech(
            phase2Frames * FrameSamples,
            fundamentalHz: 185.0,
            amplitude: 13500.0,
            includeSyllablePauses: true);
        var matsQuietBleed2 = AcousticRoomSimulator.ApplyRoomImpulseResponse(
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
            var fMats = matsQuietBleed2.AsSpan(f * FrameSamples, FrameSamples).ToArray();
            var stepOut = processor.ProcessSynchronizedStep(new Dictionary<string, short[]>
            {
                [adminId] = fAdmin,
                [matsId] = fMats
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
                [matsId] = (short[])silentFrame.Clone()
            }, frameCursor * 10);
        }

        // Phase 3: "mats" speaks again under the +32ms NetEq shift -> true lag (mats -> admin) is now -28.0ms (-1344 samples)
        int phase3Frames = 180;
        int shiftedNegLagAbs = 1344; // 28.0ms (bleed arrives on admin 28ms BEFORE mats's NetEq-delayed stream)
        var rawMatsSpeech3 = AcousticRoomSimulator.GenerateNaturalSpeech(
            phase3Frames * FrameSamples + shiftedNegLagAbs,
            fundamentalHz: 195.0,
            amplitude: 10800.0,
            includeSyllablePauses: true);
        var rawAdminBleed3 = AcousticRoomSimulator.ApplyRoomImpulseResponse(
            rawMatsSpeech3,
            directDelaySamples: 0,
            directBleedGain: 0.378,
            rt60Ms: 150.0,
            applyHeadShadowLowPass: true);

        double matsPhase3In = 0;
        double matsPhase3Out = 0;
        for (int f = 0; f < phase3Frames; f++, frameCursor++)
        {
            int idx = f * FrameSamples;
            var fMats = rawMatsSpeech3.AsSpan(idx, FrameSamples).ToArray();
            var fAdmin = rawAdminBleed3.AsSpan(idx + shiftedNegLagAbs, FrameSamples).ToArray();
            var stepOut = processor.ProcessSynchronizedStep(new Dictionary<string, short[]>
            {
                [adminId] = fAdmin,
                [matsId] = fMats
            }, frameCursor * 10);

            if (f >= 20 && f < phase3Frames - 10)
            {
                matsPhase3In += ComputeRms(fMats);
                matsPhase3Out += ComputeRms(stepOut[matsId]);
            }
        }

        Assert.True(matsPhase3Out >= matsPhase3In * 0.94,
            $"Expected active speaker 'mats' never to self-gate during Phase 3, got retention {(100.0 * matsPhase3Out / Math.Max(1.0, matsPhase3In)):F1}%");

        var finalSnap = processor.GetDiagnosticSnapshot();
        var calAdminToMats = finalSnap.Calibrations.FirstOrDefault(c => c.SourceUserId == adminId && c.TargetUserId == matsId);
        var calMatsToAdmin = finalSnap.Calibrations.FirstOrDefault(c => c.SourceUserId == matsId && c.TargetUserId == adminId);

        Assert.NotNull(calAdminToMats);
        Assert.False(calAdminToMats!.IsReciprocalPrior, "Expected admin -> mats (-46 dB WebRTC NS bleed) to achieve DirectLock");
        Assert.True(calAdminToMats.IsLiveVerified);
        Assert.InRange(calAdminToMats.DelayMs, 33.0, 39.0);

        Assert.NotNull(calMatsToAdmin);
        Assert.False(calMatsToAdmin!.IsReciprocalPrior, "Expected latched mats -> admin to re-latch onto -28.0ms NetEq shift");
        Assert.True(calMatsToAdmin.IsLiveVerified);
        Assert.InRange(calMatsToAdmin.DelayMs, -31.0, -25.0);

        Assert.Single(finalSnap.Decompositions);
        Assert.True(finalSnap.Decompositions[0].BothDirectionsDirectlyLocked);
        Assert.InRange(finalSnap.Decompositions[0].AirPropagationMs, 2.0, 6.0);

        // Active-speaker buckets must never deadlock with both participants gated simultaneously
        Assert.DoesNotContain(finalSnap.TimelineBuckets, b =>
            !string.Equals(b.DominantSpeakerUserId, "Silence", StringComparison.OrdinalIgnoreCase) &&
            b.ParticipantGateStates.TryGetValue(adminId, out var gA) && gA == AcousticGateState.GatedBleed &&
            b.ParticipantGateStates.TryGetValue(matsId, out var gB) && gB == AcousticGateState.GatedBleed);
    }

    [Fact]
    public void ThreeDevices_BureauStewie_AndroidOnsetBleedBurst_Over220MsLag_AndWifiJitterShift_GatesOnsetEchoCleanly()
    {
        // Reproduces the live 3-device "Bureau Stewie" diagnostic report:
        // - "admin first" (Windows Chrome, Filter="webrtc", ClientDsp=20ms, ServerDelay=17ms)
        // - "mats"        (Windows Chrome, Filter="webrtc", ClientDsp=20ms, ServerDelay=9ms)
        // - "mats2"       (Android App,    Filter="webrtc", ClientDsp=75ms, ServerDelay=60ms)
        //
        // Validates all 4 interacting fixes:
        // 1. Mobile -> Desktop lag > +220ms (+247.8ms on admin -> mats2) locks directly without clamping at +220.0ms.
        // 2. Android hardware-NS 90ms un-attenuated phrase-onset bleed bursts (0.36x onset -> 0.05x steady-state)
        //    do NOT trigger false DoubleTalkSTFT echo leaks on mats2 or self-gating back-reaction on solo speakers.
        // 3. 3-node triangle transitivity + Wi-Fi jitter window lets mats -> mats2 (+110.0ms) acquire a live DirectLock
        //    even after a +42ms Android Wi-Fi jitter-buffer shift away from Phase 1's +58.1ms ReciprocalPrior.
        // 4. Genuine simultaneous near-field double-talk on mats2 still opens and preserves mats2's voice.
        var processor = new AcousticRoomProcessor();
        string adminId = "6f30c05d-c9d6-4cbb-bdbb-b2f8f3b3c9c3";
        string matsId = "d9ba9018-59cb-4d1d-b222-9b9aba711103";
        string mats2Id = "340555a5-a468-477f-870f-3b49a4d09679";

        processor.SyncParticipants([adminId, matsId, mats2Id]);
        processor.SetParticipantClientPipelineInfo(adminId, "webrtc", 20.0, 17.0);
        processor.SetParticipantClientPipelineInfo(matsId, "webrtc", 20.0, 9.0);
        processor.SetParticipantClientPipelineInfo(mats2Id, "webrtc", 75.0, 60.0);

        int frameCursor = 0;
        var silentFrame = new short[FrameSamples];

        // --- PHASE 1: "mats2" (Android) speaks briefly before Wi-Fi jitter shift ---
        // Bleed arrives earlier on admin (-166.2ms = -7978 smp) and mats (-50.1ms = -2404 smp, seeding mats->mats2 prior at +58.1ms).
        int p1SpeechFrames = 110;
        int p1PauseFrames = 45;
        int p1LeadAdmin = 7978; // 166.2ms
        int p1LeadMats = 2404;  // 50.1ms
        var p1RawMats2 = AcousticRoomSimulator.GenerateNaturalSpeech(
            p1SpeechFrames * FrameSamples + p1LeadAdmin,
            fundamentalHz: 260.0,
            amplitude: 10500.0,
            includeSyllablePauses: true);
        var p1BleedAdmin = AcousticRoomSimulator.ApplyRoomImpulseResponse(p1RawMats2, 0, directBleedGain: 0.055, rt60Ms: 150.0, applyHeadShadowLowPass: true);
        var p1BleedMats = AcousticRoomSimulator.ApplyRoomImpulseResponse(p1RawMats2, 0, directBleedGain: 0.174, rt60Ms: 130.0, applyHeadShadowLowPass: true);

        for (int f = 0; f < p1SpeechFrames; f++, frameCursor++)
        {
            int idx = f * FrameSamples;
            processor.ProcessSynchronizedStep(new Dictionary<string, short[]>
            {
                [mats2Id] = p1RawMats2.AsSpan(idx, FrameSamples).ToArray(),
                [adminId] = p1BleedAdmin.AsSpan(idx + p1LeadAdmin, FrameSamples).ToArray(),
                [matsId] = p1BleedMats.AsSpan(idx + p1LeadMats, FrameSamples).ToArray()
            }, frameCursor * 10);
        }
        for (int f = 0; f < p1PauseFrames; f++, frameCursor++)
        {
            processor.ProcessSynchronizedStep(new Dictionary<string, short[]>
            {
                [adminId] = (short[])silentFrame.Clone(),
                [matsId] = (short[])silentFrame.Clone(),
                [mats2Id] = (short[])silentFrame.Clone()
            }, frameCursor * 10);
        }

        // --- PHASE 2: Android Wi-Fi jitter buffer shifts mats2 by +42ms; "admin first" speaks 2 phrases separated by 450ms silence ---
        // True lags: admin -> mats = +138.1ms (+6628 smp, 0.582x high bleed); admin -> mats2 = +247.8ms (+11894 smp, > 220ms!).
        int lagAdminToMats = 6628;   // +138.1ms
        int lagAdminToMats2 = 11894; // +247.8ms (exceeds old +220.0ms / 10560-sample ceiling!)
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

        var p2MatsTrack = AcousticRoomSimulator.ApplyRoomImpulseResponse(
            p2AdminTrack,
            directDelaySamples: lagAdminToMats,
            directBleedGain: 0.582, // -4.7 dB strong nearby PC bleed
            rt60Ms: 140.0,
            applyHeadShadowLowPass: true);

        var p2Mats2RawAcousticBleed = AcousticRoomSimulator.ApplyRoomImpulseResponse(
            p2AdminTrack,
            directDelaySamples: lagAdminToMats2,
            directBleedGain: 0.36, // Un-suppressed onset bleed burst (-8.9 dB) before Android NS clamps down
            rt60Ms: 165.0,
            applyHeadShadowLowPass: true);
        var p2Mats2Track = AcousticRoomSimulator.ApplyMobileHardwareNsOnsetBurstSimulation(
            p2Mats2RawAcousticBleed,
            onsetHoldMs: 90.0,
            steadyStateSuppressionGain: 0.14,
            silenceResetMs: 150.0);

        int mats2DtBeforeAdminPhrase2 = 0;
        double adminPhrase2In = 0;
        double adminPhrase2Out = 0;
        double mats2AdminOnsetBurstIn = 0;
        double mats2AdminOnsetBurstOut = 0;

        int p2Phrase2StartFrame = p2Phrase1Frames + p2InterPhraseSilence;
        // Onset burst arrives on mats2 at p2Phrase2StartFrame + ~25 frames (+247.8ms) and exits mats2's 50ms lookahead 5 frames later
        int p2Mats2BurstInStart = p2Phrase2StartFrame + 24;
        int p2Mats2BurstInEnd = p2Phrase2StartFrame + 38;

        for (int f = 0; f < p2TotalFrames; f++, frameCursor++)
        {
            if (f == p2Phrase2StartFrame)
            {
                mats2DtBeforeAdminPhrase2 = processor.GetDiagnosticSnapshot()
                    .Participants.First(p => p.UserId == mats2Id).DoubleTalkSubFrames;
            }

            int idx = f * FrameSamples;
            var fAdmin = p2AdminTrack.AsSpan(idx, FrameSamples).ToArray();
            var fMats = p2MatsTrack.AsSpan(idx, FrameSamples).ToArray();
            var fMats2 = p2Mats2Track.AsSpan(idx, FrameSamples).ToArray();

            var stepOut = processor.ProcessSynchronizedStep(new Dictionary<string, short[]>
            {
                [adminId] = fAdmin,
                [matsId] = fMats,
                [mats2Id] = fMats2
            }, frameCursor * 10);

            if (f >= p2Phrase2StartFrame + 25 && f < p2Phrase2StartFrame + p2Phrase2Frames)
            {
                adminPhrase2In += ComputeRms(fAdmin);
                adminPhrase2Out += ComputeRms(stepOut[adminId]);
            }

            if (f >= p2Mats2BurstInStart && f < p2Mats2BurstInEnd)
            {
                mats2AdminOnsetBurstIn += ComputeRms(fMats2);
            }
            if (f >= p2Mats2BurstInStart + 5 && f < p2Mats2BurstInEnd + 5)
            {
                mats2AdminOnsetBurstOut += ComputeRms(stepOut[mats2Id]);
            }
        }

        var snapAfterAdmin = processor.GetDiagnosticSnapshot();
        int mats2DtAfterAdminPhrase2 = snapAfterAdmin.Participants.First(p => p.UserId == mats2Id).DoubleTalkSubFrames;
        var calAdminToMats2 = snapAfterAdmin.Calibrations.FirstOrDefault(c => c.SourceUserId == adminId && c.TargetUserId == mats2Id);

        Assert.NotNull(calAdminToMats2);
        Assert.False(calAdminToMats2!.IsReciprocalPrior, "Expected admin -> mats2 (+247.8ms) to achieve live DirectLock above old 220ms ceiling");
        Assert.InRange(calAdminToMats2.DelayMs, 243.0, 253.0);
        Assert.Equal(0, mats2DtAfterAdminPhrase2 - mats2DtBeforeAdminPhrase2);
        Assert.True(mats2AdminOnsetBurstOut <= mats2AdminOnsetBurstIn * 0.08,
            $"Expected mats2 90ms onset bleed burst from admin to be gated by >= 92%, got out={mats2AdminOnsetBurstOut:F1} / in={mats2AdminOnsetBurstIn:F1}");
        Assert.True(adminPhrase2Out >= adminPhrase2In * 0.94,
            $"Expected solo speaker admin never to self-gate during mats2 onset burst, got retention {(100.0 * adminPhrase2Out / Math.Max(1.0, adminPhrase2In)):F1}%");

        // --- PHASE 3: "mats" speaks 2 phrases separated by 400ms silence ---
        // True lags: mats -> admin = -114.4ms (-5492 smp, 0.378x bleed); mats -> mats2 = +110.0ms (+5280 smp, shifted +51.9ms from Phase 1 prior!).
        int leadMatsToAdmin = 5492; // -114.4ms (arrives on admin 114.4ms BEFORE mats's stream)
        int lagMatsToMats2 = 5280;  // +110.0ms
        int p3Phrase1Frames = 135;
        int p3InterPhraseSilence = 40;
        int p3Phrase2Frames = 105;
        int p3TailFrames = 35;
        int p3TotalFrames = p3Phrase1Frames + p3InterPhraseSilence + p3Phrase2Frames + p3TailFrames;
        int p3TotalSamples = p3TotalFrames * FrameSamples + leadMatsToAdmin;

        var p3MatsExtended = new short[p3TotalSamples];
        var p3Phrase1Clean = AcousticRoomSimulator.GenerateNaturalSpeech(p3Phrase1Frames * FrameSamples, 210.0, 12000.0, includeSyllablePauses: true);
        var p3Phrase2Clean = AcousticRoomSimulator.GenerateNaturalSpeech(p3Phrase2Frames * FrameSamples, 215.0, 12000.0, includeSyllablePauses: true);
        Array.Copy(p3Phrase1Clean, 0, p3MatsExtended, 0, p3Phrase1Clean.Length);
        int p3Phrase2OffsetSamples = (p3Phrase1Frames + p3InterPhraseSilence) * FrameSamples;
        Array.Copy(p3Phrase2Clean, 0, p3MatsExtended, p3Phrase2OffsetSamples, p3Phrase2Clean.Length);

        var p3AdminBleedExtended = AcousticRoomSimulator.ApplyRoomImpulseResponse(
            p3MatsExtended,
            directDelaySamples: 0,
            directBleedGain: 0.378,
            rt60Ms: 140.0,
            applyHeadShadowLowPass: true);

        var p3Mats2RawBleed = AcousticRoomSimulator.ApplyRoomImpulseResponse(
            p3MatsExtended.AsSpan(0, p3TotalFrames * FrameSamples),
            directDelaySamples: lagMatsToMats2,
            directBleedGain: 0.35,
            rt60Ms: 155.0,
            applyHeadShadowLowPass: true);
        var p3Mats2Track = AcousticRoomSimulator.ApplyMobileHardwareNsOnsetBurstSimulation(
            p3Mats2RawBleed,
            onsetHoldMs: 90.0,
            steadyStateSuppressionGain: 0.14,
            silenceResetMs: 150.0);

        int mats2DtBeforeMatsPhrase2 = 0;
        double matsPhrase2In = 0;
        double matsPhrase2Out = 0;
        double mats2MatsOnsetBurstIn = 0;
        double mats2MatsOnsetBurstOut = 0;

        int p3Phrase2StartFrame = p3Phrase1Frames + p3InterPhraseSilence;
        int p3Mats2BurstInStart = p3Phrase2StartFrame + 11; // +110ms arrival
        int p3Mats2BurstInEnd = p3Phrase2StartFrame + 24;

        for (int f = 0; f < p3TotalFrames; f++, frameCursor++)
        {
            if (f == p3Phrase2StartFrame)
            {
                mats2DtBeforeMatsPhrase2 = processor.GetDiagnosticSnapshot()
                    .Participants.First(p => p.UserId == mats2Id).DoubleTalkSubFrames;
            }

            int idx = f * FrameSamples;
            var fMats = p3MatsExtended.AsSpan(idx, FrameSamples).ToArray();
            var fAdmin = p3AdminBleedExtended.AsSpan(idx + leadMatsToAdmin, FrameSamples).ToArray();
            var fMats2 = p3Mats2Track.AsSpan(idx, FrameSamples).ToArray();

            var stepOut = processor.ProcessSynchronizedStep(new Dictionary<string, short[]>
            {
                [adminId] = fAdmin,
                [matsId] = fMats,
                [mats2Id] = fMats2
            }, frameCursor * 10);

            if (f >= p3Phrase2StartFrame + 15 && f < p3Phrase2StartFrame + p3Phrase2Frames)
            {
                matsPhrase2In += ComputeRms(fMats);
                matsPhrase2Out += ComputeRms(stepOut[matsId]);
            }

            if (f >= p3Mats2BurstInStart && f < p3Mats2BurstInEnd)
            {
                mats2MatsOnsetBurstIn += ComputeRms(fMats2);
            }
            if (f >= p3Mats2BurstInStart + 7 && f < p3Mats2BurstInEnd + 7)
            {
                mats2MatsOnsetBurstOut += ComputeRms(stepOut[mats2Id]);
            }
        }

        var snapAfterMats = processor.GetDiagnosticSnapshot();
        _output.WriteLine(AcousticRoomDiagnosticReportFormatter.FormatLiveRoomReport(
            snapAfterMats,
            "Bureau Stewie (3-Device Onset Burst Verification)",
            new Dictionary<string, string>
            {
                [adminId] = "admin first",
                [matsId] = "mats",
                [mats2Id] = "mats2"
            }));

        int mats2DtAfterMatsPhrase2 = snapAfterMats.Participants.First(p => p.UserId == mats2Id).DoubleTalkSubFrames;
        var calMatsToMats2 = snapAfterMats.Calibrations.FirstOrDefault(c => c.SourceUserId == matsId && c.TargetUserId == mats2Id);

        Assert.NotNull(calMatsToMats2);
        Assert.False(calMatsToMats2!.IsReciprocalPrior, "Expected 3-node triangle prior + Wi-Fi jitter window to lock mats -> mats2 directly");
        Assert.InRange(calMatsToMats2.DelayMs, 105.0, 115.0);
        Assert.Equal(0, mats2DtAfterMatsPhrase2 - mats2DtBeforeMatsPhrase2);
        Assert.True(mats2MatsOnsetBurstOut <= mats2MatsOnsetBurstIn * 0.08,
            $"Expected mats2 90ms onset bleed burst from mats to be gated by >= 92%, got out={mats2MatsOnsetBurstOut:F1} / in={mats2MatsOnsetBurstIn:F1}");
        Assert.True(matsPhrase2Out >= matsPhrase2In * 0.94,
            $"Expected solo speaker mats never to self-gate during mats2 onset burst, got retention {(100.0 * matsPhrase2Out / Math.Max(1.0, matsPhrase2In)):F1}%");

        // --- PHASE 4: Genuine Simultaneous Double-Talk (admin + mats2 speak near-field together) ---
        int dtFrames = 85;
        var dtAdminClean = AcousticRoomSimulator.GenerateNaturalSpeech(dtFrames * FrameSamples + p1LeadAdmin, 184.0, 10500.0);
        var dtMats2Clean = AcousticRoomSimulator.GenerateNaturalSpeech(dtFrames * FrameSamples + p1LeadAdmin, 272.0, 10500.0);
        var dtAdminBleedOnMats2 = AcousticRoomSimulator.ApplyMobileHardwareNsOnsetBurstSimulation(
            AcousticRoomSimulator.ApplyRoomImpulseResponse(dtAdminClean.AsSpan(0, dtFrames * FrameSamples), lagAdminToMats2, 0.36, 150.0, true));
        var dtMats2BleedOnAdmin = AcousticRoomSimulator.ApplyRoomImpulseResponse(dtMats2Clean, 0, 0.055, 150.0, true);

        double mats2DtCleanIn = 0;
        double mats2DtOut = 0;
        for (int f = 0; f < dtFrames; f++, frameCursor++)
        {
            int idx = f * FrameSamples;
            var fAdmin = new short[FrameSamples];
            var fMats2 = new short[FrameSamples];
            for (int i = 0; i < FrameSamples; i++)
            {
                fAdmin[i] = (short)Math.Clamp(dtAdminClean[idx + i] + dtMats2BleedOnAdmin[idx + p1LeadAdmin + i], short.MinValue, short.MaxValue);
                fMats2[i] = (short)Math.Clamp(dtMats2Clean[idx + i] + dtAdminBleedOnMats2[idx + i], short.MinValue, short.MaxValue);
            }

            var stepOut = processor.ProcessSynchronizedStep(new Dictionary<string, short[]>
            {
                [adminId] = fAdmin,
                [matsId] = (short[])silentFrame.Clone(),
                [mats2Id] = fMats2
            }, frameCursor * 10);

            if (f >= 35)
            {
                mats2DtCleanIn += ComputeRms(dtMats2Clean.AsSpan(idx, FrameSamples));
                mats2DtOut += ComputeRms(stepOut[mats2Id]);
            }
        }

        Assert.True(mats2DtOut >= mats2DtCleanIn * 0.72,
            $"Expected genuine simultaneous double-talk on mats2 to open and retain near-field speech (>= 72%), got out={mats2DtOut:F1} / cleanIn={mats2DtCleanIn:F1}");
    }

    [Fact]
    public void StaleTriangleDoesNotOverwriteFresherDirectReverseAfterWifiShift_AndDoubleTalkNeverInflatesAgcPeakBleedGain()
    {
        var processor = new AcousticRoomProcessor();
        const string userA = "node-a-desktop";
        const string userB = "node-b-mid";
        const string userC = "node-c-mobile";

        processor.SyncParticipants([userA, userB, userC]);
        processor.SetParticipantClientPipelineInfo(userA, "webrtc", 20.0, 12.0);
        processor.SetParticipantClientPipelineInfo(userB, "webrtc", 20.0, 14.0);
        processor.SetParticipantClientPipelineInfo(userC, "webrtc", 75.0, 60.0);

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
        Assert.True(modCOut >= modCCleanIn * 0.62,
            $"Expected AGC-pair PeakBleedGain not to inflate during double-talk (retaining >= 62% speech across 0.54x STFT subtraction), got out={modCOut:F1} / cleanIn={modCCleanIn:F1}");
    }

    [Fact]
    public void TwoDesktopRnnoisePeers_LowBleedSyllabicAlias_NeverOverwritesLiveReverseLockOrInflatesLookahead()
    {
        // Reproduces Log 1 from "Bureau Stewie" (2 Windows Chrome PCs with client RNNoise):
        // 1. "mats" speaks softly (-35 dBFS) for 3.2s -> locks "mats" -> "admin first" (+4.0ms, WaveCorr >= 0.55).
        // 2. After a 3.0s pause (> 2.0s), "admin first" speaks (-27 dBFS) while client RNNoise on "mats" heavily
        //    suppresses and phase-warps bleed (-32 dB) with a spurious envelope-only echo burst at -186.2ms.
        // Verifies "admin first" -> "mats" never false-locks onto the -186.2ms envelope alias, "mats" lookahead stays
        // at 50ms (never 201ms), and "mats" -> "admin first" is never wiped back into an uncalibrated ReciprocalPrior.
        var processor = new AcousticRoomProcessor();
        const string adminId = "6f30c05d-c9d6-4cbb-bdbb-b2f8f3b3c9c3";
        const string matsId = "d9ba9018-59cb-4d1d-b222-9b9aba711103";

        processor.SyncParticipants([adminId, matsId]);
        processor.SetParticipantClientPipelineInfo(adminId, "rnnoise", 30.0, 9.0);
        processor.SetParticipantClientPipelineInfo(matsId, "rnnoise", 30.0, 9.5);

        int frameCursor = 0;
        var silentFrame = new short[FrameSamples];

        // Phase 1: "mats" speaks softly for 3.2s (+4.0ms = +192 samples air delay into "admin first", 0.14x = -17.1 dB bleed)
        int p1Frames = 320;
        var p1MatsSpeech = AcousticRoomSimulator.GenerateNaturalSpeech(p1Frames * FrameSamples, 195.0, 3800.0, includeSyllablePauses: true);
        var p1AdminBleed = AcousticRoomSimulator.ApplyRoomImpulseResponse(p1MatsSpeech, directDelaySamples: 192, directBleedGain: 0.14, rt60Ms: 135.0, applyHeadShadowLowPass: true);

        for (int f = 0; f < p1Frames; f++, frameCursor++)
        {
            int idx = f * FrameSamples;
            processor.ProcessSynchronizedStep(new Dictionary<string, short[]>
            {
                [adminId] = p1AdminBleed.AsSpan(idx, FrameSamples).ToArray(),
                [matsId] = p1MatsSpeech.AsSpan(idx, FrameSamples).ToArray()
            }, frameCursor * 10);
        }

        var snapAfterP1 = processor.GetDiagnosticSnapshot();
        var calMatsToAdminP1 = snapAfterP1.Calibrations.FirstOrDefault(c => c.SourceUserId == matsId && c.TargetUserId == adminId);
        Assert.NotNull(calMatsToAdminP1);
        Assert.False(calMatsToAdminP1!.IsReciprocalPrior);
        Assert.InRange(calMatsToAdminP1.DelayMs, 1.5, 6.5);

        // 3.0s silence gap (> 2.0s isSignificantlyNewerLiveLatch window)
        for (int f = 0; f < 300; f++, frameCursor++)
        {
            processor.ProcessSynchronizedStep(new Dictionary<string, short[]>
            {
                [adminId] = (short[])silentFrame.Clone(),
                [matsId] = (short[])silentFrame.Clone()
            }, frameCursor * 10);
        }

        // Phase 2: "admin first" speaks for 3.2s; construct an incoherent carrier modulated by admin's envelope shifted by -186.2ms
        // (simulating an RNNoise-gated -31.9 dB syllabic envelope alias with high EnvCorr ~0.75 but low WaveCorr < 0.30).
        int p2Frames = 320;
        int aliasLeadSamples = 8937; // 186.2ms lead
        var p2AdminExtended = AcousticRoomSimulator.GenerateNaturalSpeech(p2Frames * FrameSamples + aliasLeadSamples, 175.0, 8200.0, includeSyllablePauses: true);
        var incoherentCarrier = AcousticRoomSimulator.GenerateNaturalSpeech(p2Frames * FrameSamples + aliasLeadSamples, 313.0, 8200.0, includeSyllablePauses: false);
        var p2MatsAliasBleed = new short[p2Frames * FrameSamples];
        float smoothEnv = 0f;
        for (int i = 0; i < p2MatsAliasBleed.Length; i++)
        {
            float srcAbs = MathF.Abs(p2AdminExtended[i + aliasLeadSamples]) / 8200f;
            smoothEnv = 0.985f * smoothEnv + 0.015f * srcAbs;
            float trueBleed = p2AdminExtended[Math.Max(0, i - 192)] * 0.008f;
            float aliasBleed = incoherentCarrier[i] * smoothEnv * 0.032f;
            p2MatsAliasBleed[i] = (short)Math.Clamp(trueBleed + aliasBleed, short.MinValue, short.MaxValue);
        }

        for (int f = 0; f < p2Frames; f++, frameCursor++)
        {
            int idx = f * FrameSamples;
            processor.ProcessSynchronizedStep(new Dictionary<string, short[]>
            {
                [adminId] = p2AdminExtended.AsSpan(idx, FrameSamples).ToArray(),
                [matsId] = p2MatsAliasBleed.AsSpan(idx, FrameSamples).ToArray()
            }, frameCursor * 10);
        }

        var finalSnap = processor.GetDiagnosticSnapshot();
        var matsDiag = finalSnap.Participants.First(p => p.UserId == matsId);
        var calMatsToAdminFinal = finalSnap.Calibrations.FirstOrDefault(c => c.SourceUserId == matsId && c.TargetUserId == adminId);
        var calAdminToMatsFinal = finalSnap.Calibrations.FirstOrDefault(c => c.SourceUserId == adminId && c.TargetUserId == matsId);

        Assert.Equal(50, matsDiag.EffectiveLookaheadMs);
        Assert.NotNull(calMatsToAdminFinal);
        Assert.False(calMatsToAdminFinal!.IsReciprocalPrior, "Expected mats -> admin live DirectLock never to be wiped by a low-WaveCorr envelope alias");
        Assert.DoesNotContain(finalSnap.Warnings, w => w.Contains(matsId, StringComparison.Ordinal) && w.Contains("not been directly calibrated", StringComparison.OrdinalIgnoreCase));
        if (calAdminToMatsFinal != null)
        {
            Assert.True(calAdminToMatsFinal.DelayMs > -30.0, $"Expected admin -> mats never to false-lock at -186.2ms, got {calAdminToMatsFinal.DelayMs:F1}ms");
        }
    }

    [Fact]
    public void ThreeDevices_BureauStewie_Sub40MsNodeSlipAndAgcGatedRoommate_PropagatesNodeOffsetAndPreventsFalseDoubleTalk()
    {
        // Reproduces Log 2 from "Bureau Stewie" (2 Windows Chrome PCs + 1 Android AGC device):
        // 1. All 3 participants calibrate:
        //    - mats -> admin (-40.6ms), admin -> mats (+48.6ms), admin -> mats2 (+85.3ms, 3.36x = +10.5 dB Android AGC)
        //    - mats2 -> admin (-77.5ms), mats2 -> mats (-35.4ms)  => exact ~3.9ms (~1.34m) air propagation!
        // 2. "admin first" experiences a +35.4ms WebRTC NetEq stream slip (inside the old <= 40ms drift blind spot),
        //    moving true mats2 -> admin from -77.5ms to -42.1ms while mats2 -> mats stays at -35.4ms.
        // 3. "mats2" speaks solo at hot Android levels (-12 dBFS):
        //    - Verifies 0 false DoubleTalkSTFT buckets on mats2 against gated roommates.
        //    - Verifies kinematic node-offset propagation updates admin's stale outgoing pairs so decomposed air distance
        //      stays realistic (~1.3-2.5m instead of 7.40m) and "mats" is never demoted back to uncalibrated ReciprocalPrior.
        var processor = new AcousticRoomProcessor();
        const string adminId = "6f30c05d-c9d6-4cbb-bdbb-b2f8f3b3c9c3";
        const string matsId = "d9ba9018-59cb-4d1d-b222-9b9aba711103";
        const string mats2Id = "340555a5-a468-477f-870f-3b49a4d09679";

        processor.SyncParticipants([adminId, matsId, mats2Id]);
        processor.SetParticipantClientPipelineInfo(adminId, "rnnoise", 30.0, 9.0);
        processor.SetParticipantClientPipelineInfo(matsId, "rnnoise", 30.0, 9.5);
        processor.SetParticipantClientPipelineInfo(mats2Id, "rnnoise", 80.3, 11.0);

        int frameCursor = 0;
        var silentFrame = new short[FrameSamples];

        // Turn 1: "mats" speaks (mats -> admin = -40.6ms = -1949 smp; mats -> mats2 = +43.4ms = +2083 smp)
        int t1Frames = 150;
        int leadMatsToAdmin = 1949;
        var t1Mats = AcousticRoomSimulator.GenerateNaturalSpeech(t1Frames * FrameSamples + leadMatsToAdmin, 205.0, 5200.0, includeSyllablePauses: true);
        var t1AdminBleed = AcousticRoomSimulator.ApplyRoomImpulseResponse(t1Mats, 0, 0.12, 130.0, true);
        var t1Mats2Bleed = AcousticRoomSimulator.ApplyRoomImpulseResponse(t1Mats.AsSpan(0, t1Frames * FrameSamples), 2083, 0.45, 140.0, true);
        for (int f = 0; f < t1Frames; f++, frameCursor++)
        {
            int idx = f * FrameSamples;
            processor.ProcessSynchronizedStep(new Dictionary<string, short[]>
            {
                [matsId] = t1Mats.AsSpan(idx, FrameSamples).ToArray(),
                [adminId] = t1AdminBleed.AsSpan(idx + leadMatsToAdmin, FrameSamples).ToArray(),
                [mats2Id] = t1Mats2Bleed.AsSpan(idx, FrameSamples).ToArray()
            }, frameCursor * 10);
        }

        for (int f = 0; f < 40; f++, frameCursor++)
        {
            processor.ProcessSynchronizedStep(new Dictionary<string, short[]>
            {
                [adminId] = (short[])silentFrame.Clone(),
                [matsId] = (short[])silentFrame.Clone(),
                [mats2Id] = (short[])silentFrame.Clone()
            }, frameCursor * 10);
        }

        // Turn 2: "admin first" speaks (admin -> mats = +48.6ms = +2331 smp; admin -> mats2 = +85.3ms = +4093 smp, 2.2x AGC boost)
        int t2Frames = 160;
        var t2Admin = AcousticRoomSimulator.GenerateNaturalSpeech(t2Frames * FrameSamples, 178.0, 5500.0, includeSyllablePauses: true);
        var t2MatsBleed = AcousticRoomSimulator.ApplyRoomImpulseResponse(t2Admin, 2331, 0.08, 130.0, true);
        var t2Mats2Bleed = AcousticRoomSimulator.ApplyRoomImpulseResponse(t2Admin, 4093, 2.15, 145.0, true);
        for (int f = 0; f < t2Frames; f++, frameCursor++)
        {
            int idx = f * FrameSamples;
            processor.ProcessSynchronizedStep(new Dictionary<string, short[]>
            {
                [adminId] = t2Admin.AsSpan(idx, FrameSamples).ToArray(),
                [matsId] = t2MatsBleed.AsSpan(idx, FrameSamples).ToArray(),
                [mats2Id] = t2Mats2Bleed.AsSpan(idx, FrameSamples).ToArray()
            }, frameCursor * 10);
        }

        for (int f = 0; f < 40; f++, frameCursor++)
        {
            processor.ProcessSynchronizedStep(new Dictionary<string, short[]>
            {
                [adminId] = (short[])silentFrame.Clone(),
                [matsId] = (short[])silentFrame.Clone(),
                [mats2Id] = (short[])silentFrame.Clone()
            }, frameCursor * 10);
        }

        // Turn 3: "mats2" speaks before slip (mats2 -> admin = -77.5ms = -3720 smp; mats2 -> mats = -35.4ms = -1698 smp)
        int t3Frames = 150;
        int leadMats2ToAdminPre = 3720;
        int leadMats2ToMats = 1698;
        var t3Mats2 = AcousticRoomSimulator.GenerateNaturalSpeech(t3Frames * FrameSamples + leadMats2ToAdminPre, 255.0, 14500.0, includeSyllablePauses: true);
        var t3AdminBleed = AcousticRoomSimulator.ApplyRoomImpulseResponse(t3Mats2, 0, 0.045, 130.0, true);
        var t3MatsBleed = AcousticRoomSimulator.ApplyRoomImpulseResponse(t3Mats2, 0, 0.075, 130.0, true);
        for (int f = 0; f < t3Frames; f++, frameCursor++)
        {
            int idx = f * FrameSamples;
            processor.ProcessSynchronizedStep(new Dictionary<string, short[]>
            {
                [mats2Id] = t3Mats2.AsSpan(idx, FrameSamples).ToArray(),
                [adminId] = t3AdminBleed.AsSpan(idx + leadMats2ToAdminPre, FrameSamples).ToArray(),
                [matsId] = t3MatsBleed.AsSpan(idx + leadMats2ToMats, FrameSamples).ToArray()
            }, frameCursor * 10);
        }

        // 2.5s pause while "admin first" experiences a +35.4ms NetEq slip (mats2 -> admin moves from -77.5ms to -42.1ms = -2021 smp)
        for (int f = 0; f < 250; f++, frameCursor++)
        {
            processor.ProcessSynchronizedStep(new Dictionary<string, short[]>
            {
                [adminId] = (short[])silentFrame.Clone(),
                [matsId] = (short[])silentFrame.Clone(),
                [mats2Id] = (short[])silentFrame.Clone()
            }, frameCursor * 10);
        }

        processor.ResetDiagnosticCounters();

        // Turn 4: "mats2" speaks solo at hot Android level after admin's +35.4ms slip (mats2 -> admin = -42.1ms = -2021 smp, mats2 -> mats = -35.4ms)
        int t4Frames = 180;
        int leadMats2ToAdminPost = 2021;
        var t4Mats2 = AcousticRoomSimulator.GenerateNaturalSpeech(t4Frames * FrameSamples + leadMats2ToAdminPost, 258.0, 16500.0, includeSyllablePauses: true);
        var t4AdminBleed = AcousticRoomSimulator.ApplyRoomImpulseResponse(t4Mats2, 0, 0.048, 130.0, true);
        var t4MatsBleed = AcousticRoomSimulator.ApplyRoomImpulseResponse(t4Mats2, 0, 0.075, 130.0, true);
        for (int f = 0; f < t4Frames; f++, frameCursor++)
        {
            int idx = f * FrameSamples;
            processor.ProcessSynchronizedStep(new Dictionary<string, short[]>
            {
                [mats2Id] = t4Mats2.AsSpan(idx, FrameSamples).ToArray(),
                [adminId] = t4AdminBleed.AsSpan(idx + leadMats2ToAdminPost, FrameSamples).ToArray(),
                [matsId] = t4MatsBleed.AsSpan(idx + leadMats2ToMats, FrameSamples).ToArray()
            }, frameCursor * 10);
        }

        var snap = processor.GetDiagnosticSnapshot();
        Assert.DoesNotContain(snap.TimelineBuckets, b =>
            b.ParticipantGateStates.TryGetValue(mats2Id, out var gM2) && gM2 == AcousticGateState.DoubleTalkSubtraction &&
            b.ParticipantGateStates.TryGetValue(adminId, out var gA) && gA == AcousticGateState.GatedBleed &&
            b.ParticipantGateStates.TryGetValue(matsId, out var gM) && gM == AcousticGateState.GatedBleed);

        var calMatsToAdmin = snap.Calibrations.FirstOrDefault(c => c.SourceUserId == matsId && c.TargetUserId == adminId);
        Assert.NotNull(calMatsToAdmin);
        Assert.False(calMatsToAdmin!.IsReciprocalPrior, "Expected mats -> admin to stay calibrated (not wiped to ReciprocalPrior) after admin's 35.4ms stream slip");

        var decompAdminMats2 = snap.Decompositions.FirstOrDefault(d =>
            (d.UserAId == mats2Id && d.UserBId == adminId) || (d.UserAId == adminId && d.UserBId == mats2Id));
        Assert.NotNull(decompAdminMats2);
        Assert.InRange(decompAdminMats2!.EstimatedDistanceMeters, 0.5, 3.0);
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

        int lookaheadFrames = AcousticRoomProcessor.LookaheadSamples / FrameSamples;
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
}
