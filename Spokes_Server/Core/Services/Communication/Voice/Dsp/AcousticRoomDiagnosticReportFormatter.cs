namespace Spokes_Server.Core.Services.Communication.Voice.Dsp;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Spokes_Server.Core.Models.Communication;

/// <summary>
/// Formats live <see cref="PhysicalRoomDiagnosticSnapshot"/> captures and automated <see cref="AcousticBenchmarkScenarioResult"/>
/// runs into compact, self-contained text reports designed to be pasted directly into an AI coding agent for analysis and debugging.
/// </summary>
public static class AcousticRoomDiagnosticReportFormatter
{
    public static string FormatLiveRoomReport(
        PhysicalRoomDiagnosticSnapshot snapshot,
        string roomName,
        IReadOnlyDictionary<string, string>? userDisplayNames = null,
        IReadOnlyDictionary<string, (string DeviceName, string FilterType)>? deviceMetadata = null,
        PhysicalRoomAudioMode? configuredAudioMode = null,
        DirectRoomOperatingStatus? directOperatingStatus = null,
        DirectRoomClientTelemetry? directClientTelemetry = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        var sb = new StringBuilder();
        sb.AppendLine("=== SPOKES PHYSICAL ROOM DIAGNOSTIC REPORT ===");
        string modeSuffix = configuredAudioMode.HasValue
            ? string.Format(
                CultureInfo.InvariantCulture,
                " | AudioMode: {0} (DirectStatus={1})",
                configuredAudioMode.Value,
                directOperatingStatus ?? DirectRoomOperatingStatus.NotApplicable)
            : string.Empty;
        sb.AppendLine(string.Format(
            CultureInfo.InvariantCulture,
            "Timestamp: {0:yyyy-MM-ddTHH:mm:ssZ} | Duration: {1:F1}s | Room: \"{2}\" ({3} Active Devices) | SampleRate: {4}Hz{5}",
            snapshot.CapturedAtUtc,
            snapshot.CaptureDurationSeconds,
            string.IsNullOrWhiteSpace(roomName) ? "Physical Room" : roomName,
            snapshot.Participants.Count,
            snapshot.SampleRate,
            modeSuffix));
        sb.AppendLine();

        AppendParticipantsSection(sb, snapshot, userDisplayNames, deviceMetadata);
        sb.AppendLine();
        if (directClientTelemetry != null ||
            (configuredAudioMode.HasValue && configuredAudioMode.Value != PhysicalRoomAudioMode.ServerCleanup))
        {
            AppendDirectLanTelemetrySection(sb, configuredAudioMode, directOperatingStatus, directClientTelemetry, userDisplayNames);
            sb.AppendLine();
        }
        AppendPairwiseMatrixSection(sb, snapshot, userDisplayNames);
        sb.AppendLine();
        AppendTimelineSection(sb, snapshot, userDisplayNames);
        sb.AppendLine();
        AppendWarningsSection(sb, snapshot, userDisplayNames);
        sb.AppendLine("==============================================");

        return sb.ToString();
    }

    public static string FormatBenchmarkScenarioReport(AcousticBenchmarkScenarioResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        var sb = new StringBuilder();
        sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "=== BENCHMARK SCENARIO: {0} ===", result.ScenarioName));
        sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "Description: {0}", result.Description));
        sb.AppendLine(string.Format(
            CultureInfo.InvariantCulture,
            "Latency: True={0:+0.0;-0.0;0.0}ms | Detected={1:+0.0;-0.0;0.0}ms | Error={2:F2}ms | LockTime={3:F0}ms",
            result.GroundTruthDelayMs,
            result.DetectedDelayMs,
            result.LatencyErrorMs,
            result.ConvergenceTimeMs));
        sb.AppendLine(string.Format(
            CultureInfo.InvariantCulture,
            "Cancellation: BleedSuppression(ERLE)={0:F1}dB | PrimaryRetention={1:F1}% | FalseGate={2:F2}% | ClicksRejected={3}/{4}",
            result.BleedSuppressionErleDb,
            result.PrimarySpeechRetentionPercent,
            result.FalseGatePercent,
            result.ClicksRejected,
            result.ClicksInjected));

        if (Math.Abs(result.DoubleTalkSiSdrImprovementDb) > 0.001 || Math.Abs(result.DoubleTalkSiSdrAfterDb) > 0.001)
        {
            sb.AppendLine(string.Format(
                CultureInfo.InvariantCulture,
                "Double-Talk: SI-SDR Before={0:F1}dB -> After={1:F1}dB (Delta={2:+0.0;-0.0;0.0}dB) | SpectralDistortion(LSD)={3:F2}dB",
                result.DoubleTalkSiSdrBeforeDb,
                result.DoubleTalkSiSdrAfterDb,
                result.DoubleTalkSiSdrImprovementDb,
                result.LogSpectralDistortionDb));
        }

        sb.AppendLine();
        AppendParticipantsSection(sb, result.Snapshot, null, null);
        sb.AppendLine();
        AppendPairwiseMatrixSection(sb, result.Snapshot, null);
        if (result.Snapshot.Warnings.Count > 0)
        {
            sb.AppendLine();
            AppendWarningsSection(sb, result.Snapshot, null);
        }
        sb.AppendLine("==============================================");

        return sb.ToString();
    }

    public static string FormatBenchmarkSuiteSummary(IReadOnlyList<AcousticBenchmarkScenarioResult> results)
    {
        ArgumentNullException.ThrowIfNull(results);

        var sb = new StringBuilder();
        sb.AppendLine("=== SPOKES PHYSICAL ROOM DSP BENCHMARK SUITE REPORT (AGENT-READY) ===");
        sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "ExecutedAt: {0:yyyy-MM-ddTHH:mm:ssZ} | Scenarios: {1}", DateTime.UtcNow, results.Count));
        sb.AppendLine();
        sb.AppendLine("| Scenario | True Lag | Est Lag | Lag Err | Lock ms | ERLE (dB) | DT dSI-SDR | Retention | FalseGate |");
        sb.AppendLine("| :--- | :---: | :---: | :---: | :---: | :---: | :---: | :---: | :---: |");

        foreach (var r in results)
        {
            sb.AppendLine(string.Format(
                CultureInfo.InvariantCulture,
                "| {0} | {1:+0.0;-0.0;0.0}ms | {2:+0.0;-0.0;0.0}ms | {3:F2}ms | {4:F0}ms | {5:F1} dB | {6:+0.0;-0.0;0.0} dB | {7:F1}% | {8:F1}% |",
                r.ScenarioName,
                r.GroundTruthDelayMs,
                r.DetectedDelayMs,
                r.LatencyErrorMs,
                r.ConvergenceTimeMs,
                r.BleedSuppressionErleDb,
                r.DoubleTalkSiSdrImprovementDb,
                r.PrimarySpeechRetentionPercent,
                r.FalseGatePercent));
        }

        sb.AppendLine();
        foreach (var r in results)
        {
            sb.AppendLine(FormatBenchmarkScenarioReport(r));
            sb.AppendLine();
        }

        return sb.ToString();
    }

    private static void AppendParticipantsSection(
        StringBuilder sb,
        PhysicalRoomDiagnosticSnapshot snapshot,
        IReadOnlyDictionary<string, string>? userDisplayNames,
        IReadOnlyDictionary<string, (string DeviceName, string FilterType)>? deviceMetadata)
    {
        sb.AppendLine("[PARTICIPANTS & DEVICES]");
        if (snapshot.Participants.Count == 0)
        {
            sb.AppendLine("- (No active participants registered in DSP processor)");
            return;
        }

        foreach (var p in snapshot.Participants)
        {
            string label = ResolveUserLabel(p.UserId, userDisplayNames);
            string deviceInfo = string.Empty;
            if (deviceMetadata != null && deviceMetadata.TryGetValue(p.UserId, out var meta))
            {
                deviceInfo = string.Format(
                    CultureInfo.InvariantCulture,
                    "Device=\"{0}\", Filter=\"{1}\", ",
                    meta.DeviceName,
                    meta.FilterType);
            }

            int total = Math.Max(1, p.TotalSubFramesProcessed);
            double openPct = Math.Max(0, 100.0 * (total - p.GatedBleedSubFrames - p.DoubleTalkSubFrames) / total);
            double gatedPct = 100.0 * p.GatedBleedSubFrames / total;
            double dtPct = 100.0 * p.DoubleTalkSubFrames / total;
            double speechPct = 100.0 * p.SpeechSubFrames / total;

            string delayBreakdown = string.Empty;
            if (p.ClientProcessingDelayMs > 0 || p.ServerToClientDelayMs > 0)
            {
                delayBreakdown = string.Format(
                    CultureInfo.InvariantCulture,
                    ", ClientDsp={0:F1}ms, ServerDelay={1:F1}ms",
                    p.ClientProcessingDelayMs,
                    p.ServerToClientDelayMs);
            }

            sb.AppendLine(string.Format(
                CultureInfo.InvariantCulture,
                "- {0}: {1}State={2} (Gain={3:F2}), Lookahead={4}ms, ClockDrift={5:+0.0;-0.0;0.0}ms{6}",
                label,
                deviceInfo,
                p.CurrentGateState,
                p.CurrentGain,
                p.EffectiveLookaheadMs,
                p.ClockDriftCompensatedMs,
                delayBreakdown));

            sb.AppendLine(string.Format(
                CultureInfo.InvariantCulture,
                "  SpeechSubFrames={0}/{1} ({2:F0}%) | VocalRms={3:F1}dBFS | GateDist: Open={4:F0}%, GatedBleed={5:F0}%, DoubleTalk={6:F0}%",
                p.SpeechSubFrames,
                p.TotalSubFramesProcessed,
                speechPct,
                p.RecentVocalRmsDbFs,
                openPct,
                gatedPct,
                dtPct));

            sb.AppendLine(string.Format(
                CultureInfo.InvariantCulture,
                "  ClicksRejected={0} | AchievedBleedSuppression(ERLE)={1:F1}dB | PostDspResidual={2:F1}dBFS | PeakRawIn={3:F1}dBFS | ClippedSamples={4} | LevelerGain={5:+0.0;-0.0;0.0}dB",
                p.RejectedClickTransients,
                p.AchievedBleedSuppressionDb,
                p.PostDspResidualDbFs,
                p.PeakRawInputDbFs,
                p.ClippedSampleCount,
                p.AppliedMakeupGainDb));
        }
    }

    private static void AppendDirectLanTelemetrySection(
        StringBuilder sb,
        PhysicalRoomAudioMode? configuredAudioMode,
        DirectRoomOperatingStatus? directOperatingStatus,
        DirectRoomClientTelemetry? telemetry,
        IReadOnlyDictionary<string, string>? userDisplayNames)
    {
        sb.AppendLine("[DIRECT LAN ENGINE & LOCAL C++ DSP TELEMETRY]");
        sb.AppendLine(string.Format(
            CultureInfo.InvariantCulture,
            "- ConfiguredMode={0} | OperatingStatus={1}",
            configuredAudioMode ?? PhysicalRoomAudioMode.LowLatencyCleaned,
            directOperatingStatus ?? DirectRoomOperatingStatus.NotApplicable));

        if (telemetry == null)
        {
            sb.AppendLine("- (No active Spokes Desktop Direct LAN C++ engine telemetry reported by local client)");
            return;
        }

        sb.AppendLine(string.Format(
            CultureInfo.InvariantCulture,
            "- E2ELatency={0:F1}ms | MaxPeerRTT={1:F2}ms | JitterBuffer={2:F2}ms | AchievedSuppression={3:F1}dB",
            telemetry.EstimatedEndToEndMs,
            telemetry.MaxPeerRttMs,
            telemetry.JitterBufferMs,
            telemetry.AchievedBleedSuppressionDb));

        string captureDetails = telemetry.CaptureNativeRateHz > 0
            ? string.Format(
                CultureInfo.InvariantCulture,
                " ({0}Hz {1}ch, AC3={2}, Raw={3}, Disc={4})",
                telemetry.CaptureNativeRateHz,
                telemetry.CaptureNativeChannels,
                telemetry.CaptureUsingAudioClient3 ? "true" : "false",
                telemetry.CaptureRawMode ? "true" : "false",
                telemetry.CaptureDiscontinuities)
            : string.Empty;
        string playbackDetails = telemetry.PlaybackNativeRateHz > 0
            ? string.Format(
                CultureInfo.InvariantCulture,
                " ({0}Hz {1}ch, AC3={2})",
                telemetry.PlaybackNativeRateHz,
                telemetry.PlaybackNativeChannels,
                telemetry.PlaybackUsingAudioClient3 ? "true" : "false")
            : string.Empty;

        sb.AppendLine(string.Format(
            CultureInfo.InvariantCulture,
            "- WASAPIPeriods: Capture={0:F2}ms{9}, Playback={1:F2}ms{10} | LocalMicPeak={2:F1}dBFS | LocalClippedSamples={3} | RefFifo: Underruns={4}, Overruns={5} | UDP: TX={6}, RX={7}, AuthDrops={8}",
            telemetry.CapturePeriodMs,
            telemetry.PlaybackPeriodMs,
            telemetry.LocalPeakDbfs,
            telemetry.LocalClippedSamples,
            telemetry.RefUnderrunCount,
            telemetry.RefOverrunCount,
            telemetry.TxPacketCount,
            telemetry.RxAudioPacketCount,
            telemetry.AuthDropCount,
            captureDetails,
            playbackDetails));

        if (telemetry.PeerDiagnostics != null && telemetry.PeerDiagnostics.Count > 0)
        {
            foreach (var kvp in telemetry.PeerDiagnostics.OrderBy(k => k.Key, StringComparer.Ordinal))
            {
                var pd = kvp.Value;
                string peerLabel = ResolveUserLabel(string.IsNullOrWhiteSpace(pd.UserId) ? kvp.Key : pd.UserId, userDisplayNames);
                string lookaheadPart = pd.LookaheadMs > 0.05
                    ? string.Format(CultureInfo.InvariantCulture, ", Lookahead={0:F1}ms", pd.LookaheadMs)
                    : string.Empty;
                sb.AppendLine(string.Format(
                    CultureInfo.InvariantCulture,
                    "  * Peer {0}: RTT={1:F2}ms, CausalLag={2:+0.0;-0.0;0.0}ms{13}, Bleed={3:F1}dB (Conf={4:F2}), PeerPeakIn={5:F1}dBFS, PeerClipped={6}, LevelerGain={7:+0.0;-0.0;0.0}dB, BleedGated={8:F0}%, Jitter(Und={9}, Ovf={10}, Trim={11}, MaxBurst={12})",
                    peerLabel,
                    pd.RttMs,
                    pd.LagMs,
                    pd.BleedDb,
                    pd.Confidence,
                    pd.PeakInputDbfs,
                    pd.ClippedSamples,
                    pd.LevelerGainDb,
                    pd.BleedGatedPct,
                    pd.JitterUnderruns,
                    pd.JitterOverflows,
                    pd.JitterTrims,
                    pd.MaxBurstHops,
                    lookaheadPart));
            }
        }
    }

    private static void AppendPairwiseMatrixSection(
        StringBuilder sb,
        PhysicalRoomDiagnosticSnapshot snapshot,
        IReadOnlyDictionary<string, string>? userDisplayNames)
    {
        sb.AppendLine("[PAIRWISE ACOUSTIC MATRIX]");
        if (snapshot.Calibrations.Count == 0)
        {
            sb.AppendLine("- (No pairwise calibrations locked yet — waiting for dominant near-field speech)");
            return;
        }

        foreach (var c in snapshot.Calibrations)
        {
            string srcLabel = ResolveUserLabel(c.SourceUserId, userDisplayNames);
            string tgtLabel = ResolveUserLabel(c.TargetUserId, userDisplayNames);
            string lockMode = c.Confidence < 0.20
                ? "Uncalibrated"
                : (c.IsReciprocalPrior ? "ReciprocalPrior" : "DirectLock");

            var targetStats = snapshot.Participants.FirstOrDefault(p => p.UserId == c.TargetUserId);
            double suppressionDb = targetStats?.AchievedBleedSuppressionDb ?? 0.0;
            double residualDbFs = targetStats?.PostDspResidualDbFs ?? -100.0;

            sb.AppendLine(string.Format(
                CultureInfo.InvariantCulture,
                "- {0} -> {1}: Delay={2:+0.0;-0.0;0.0}ms ({3:+#;-#;0} smp) [{4}, Conf={5:F2}, EnvCorr={6:F2}, WaveCorr={7:F2}]",
                srcLabel,
                tgtLabel,
                c.DelayMs,
                c.DelaySamples48k,
                lockMode,
                c.Confidence,
                c.PeakEnvelopeCorrelation,
                c.PeakWaveformCorrelation));

            sb.AppendLine(string.Format(
                CultureInfo.InvariantCulture,
                "  BleedGain={0:F3} ({1:F1}dB) | TargetPostDspResidual={2:F1}dBFS | TargetSuppression(ERLE)={3:F1}dB",
                c.BleedGainLinear,
                c.BleedAttenuationDb,
                residualDbFs,
                suppressionDb));
        }

        if (snapshot.Decompositions.Count > 0)
        {
            sb.AppendLine("[DECOMPOSED ROOM PHYSICS & CLIENT DSP OFFSETS]");
            foreach (var d in snapshot.Decompositions)
            {
                string labelA = ResolveUserLabel(d.UserAId, userDisplayNames);
                string labelB = ResolveUserLabel(d.UserBId, userDisplayNames);
                string mode = d.BothDirectionsDirectlyLocked ? "BidirectionalDirectLock" : "EstimatedFromSingleSpeakerPrior";
                sb.AppendLine(string.Format(
                    CultureInfo.InvariantCulture,
                    "- {0} <-> {1} [{2}]: AirPropagation={3:+0.00;-0.00;0.00}ms (~{4:F2}m mic separation) | RelativeClientDspOffset={5:+0.00;-0.00;0.00}ms",
                    labelA,
                    labelB,
                    mode,
                    d.AirPropagationMs,
                    d.EstimatedDistanceMeters,
                    d.RelativeDspOffsetMs));
            }
        }
    }

    private static void AppendTimelineSection(
        StringBuilder sb,
        PhysicalRoomDiagnosticSnapshot snapshot,
        IReadOnlyDictionary<string, string>? userDisplayNames)
    {
        sb.AppendLine("[TIMELINE & ANOMALY TRACE (250ms buckets)]");
        if (snapshot.TimelineBuckets.Count == 0)
        {
            sb.AppendLine("- (No timeline buckets recorded)");
            return;
        }

        // Collapse contiguous runs of silent, event-free buckets into a single summary line
        // so active speech/cough/lock events earlier in a 10-30s recording are never pushed off the trace!
        var buckets = snapshot.TimelineBuckets;
        int i = 0;
        while (i < buckets.Count)
        {
            var b = buckets[i];
            bool isSilentNoEvent = string.IsNullOrWhiteSpace(b.DominantSpeakerUserId) &&
                                   b.ClickTransientsRejected == 0 &&
                                   string.IsNullOrWhiteSpace(b.EventSummary);

            if (isSilentNoEvent)
            {
                int runEnd = i;
                while (runEnd + 1 < buckets.Count)
                {
                    var next = buckets[runEnd + 1];
                    if (string.IsNullOrWhiteSpace(next.DominantSpeakerUserId) &&
                        next.ClickTransientsRejected == 0 &&
                        string.IsNullOrWhiteSpace(next.EventSummary) &&
                        SameGateStates(b.ParticipantGateStates, next.ParticipantGateStates))
                    {
                        runEnd++;
                    }
                    else
                    {
                        break;
                    }
                }

                int runCount = runEnd - i + 1;
                var states = b.ParticipantGateStates.Select(kvp =>
                    $"{ResolveUserLabel(kvp.Key, userDisplayNames)}={FormatShortGateState(kvp.Value)}");

                if (runCount == 1)
                {
                    sb.AppendLine(string.Format(
                        CultureInfo.InvariantCulture,
                        "t={0:F2}s..{1:F2}s: Dominant=Silence | Gates=[{2}]",
                        b.StartTimeSeconds,
                        b.EndTimeSeconds,
                        string.Join(", ", states)));
                }
                else
                {
                    double startSec = b.StartTimeSeconds;
                    double endSec = buckets[runEnd].EndTimeSeconds;
                    sb.AppendLine(string.Format(
                        CultureInfo.InvariantCulture,
                        "t={0:F2}s..{1:F2}s: Dominant=Silence ({2} buckets, {3:F2}s) | Gates=[{4}]",
                        startSec,
                        endSec,
                        runCount,
                        Math.Max(0.25, endSec - startSec),
                        string.Join(", ", states)));
                }

                i = runEnd + 1;
                continue;
            }

            string speaker = string.IsNullOrWhiteSpace(b.DominantSpeakerUserId)
                ? "Silence"
                : $"{ResolveUserLabel(b.DominantSpeakerUserId, userDisplayNames)} ({b.DominantVocalDbFs:F1}dBFS)";

            var activeStates = b.ParticipantGateStates.Select(kvp =>
                $"{ResolveUserLabel(kvp.Key, userDisplayNames)}={FormatShortGateState(kvp.Value)}");

            string clicks = b.ClickTransientsRejected > 0 ? $" | ClicksRejected={b.ClickTransientsRejected}" : string.Empty;
            string extra = !string.IsNullOrWhiteSpace(b.EventSummary)
                ? $" | {ReplaceUserIdsWithDisplayNames(b.EventSummary, userDisplayNames)}"
                : string.Empty;

            sb.AppendLine(string.Format(
                CultureInfo.InvariantCulture,
                "t={0:F2}s..{1:F2}s: Dominant={2} | Gates=[{3}]{4}{5}",
                b.StartTimeSeconds,
                b.EndTimeSeconds,
                speaker,
                string.Join(", ", activeStates),
                clicks,
                extra));

            i++;
        }
    }

    private static void AppendWarningsSection(
        StringBuilder sb,
        PhysicalRoomDiagnosticSnapshot snapshot,
        IReadOnlyDictionary<string, string>? userDisplayNames)
    {
        if (snapshot.Warnings.Count == 0)
        {
            sb.AppendLine("Warnings: None (All calibrated directions nominal)");
            return;
        }

        sb.AppendLine("Warnings & Diagnostic Findings:");
        foreach (var w in snapshot.Warnings)
        {
            sb.AppendLine($"  ! {ReplaceUserIdsWithDisplayNames(w, userDisplayNames)}");
        }
    }

    public static string ReplaceUserIdsWithDisplayNames(
        string text,
        IReadOnlyDictionary<string, string>? userDisplayNames,
        bool includeRawId = false)
    {
        if (string.IsNullOrEmpty(text) || userDisplayNames == null || userDisplayNames.Count == 0)
        {
            return text;
        }

        string formatted = text;
        foreach (var kvp in userDisplayNames)
        {
            if (string.IsNullOrWhiteSpace(kvp.Key) || string.IsNullOrWhiteSpace(kvp.Value))
            {
                continue;
            }

            string replacement = includeRawId ? $"{kvp.Key} (\"{kvp.Value}\")" : $"\"{kvp.Value}\"";
            formatted = formatted.Replace(kvp.Key, replacement, StringComparison.Ordinal);
        }

        return formatted;
    }

    private static string ResolveUserLabel(string userId, IReadOnlyDictionary<string, string>? userDisplayNames)
    {
        if (userDisplayNames != null && userDisplayNames.TryGetValue(userId, out var name) && !string.IsNullOrWhiteSpace(name))
        {
            return $"{userId} (\"{name}\")";
        }
        return userId;
    }

    private static bool SameGateStates(
        IReadOnlyDictionary<string, AcousticGateState> a,
        IReadOnlyDictionary<string, AcousticGateState> b)
    {
        if (a.Count != b.Count) return false;
        foreach (var kvp in a)
        {
            if (!b.TryGetValue(kvp.Key, out var bVal) || bVal != kvp.Value)
            {
                return false;
            }
        }
        return true;
    }

    private static string FormatShortGateState(AcousticGateState state) => state switch
    {
        AcousticGateState.Open => "Open",
        AcousticGateState.GatedBleed => "Gated",
        AcousticGateState.DoubleTalkSubtraction => "DoubleTalkSTFT",
        _ => state.ToString()
    };
}
