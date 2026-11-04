using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.JSInterop;
using Microsoft.JSInterop.Infrastructure;
using Moq;
using MudBlazor;
using Spokes_Server.Components.UI;
using Spokes_Server.Core.Models.Communication;
using Spokes_Server.Core.Services.Communication.Chat;
using Spokes_Server.Core.Services.Communication.Voice;
using Spokes_Server.Core.Services.UI;

namespace Spokes_Server.Tests.Core.Services.Communication.Voice;

public enum VoiceClientPlatform
{
    DesktopElectron,
    IosNative,
    AndroidNative,
    WebBrowser
}

public enum StageLayoutMode
{
    StageUnmountedPipActive,
    Grid,
    FaceTimeTwoPerson,
    FocusedParticipant
}

public sealed record ParticipantTileVisualState(
    string ParticipantId,
    bool IsLargeAvatarVisible,
    bool IsSmallOverlayVisible,
    bool HasCameraStream,
    bool CameraVideoPaused,
    string CameraTrackReadyState,
    bool HasScreenShareStream,
    bool ScreenVideoPaused,
    string ScreenTrackReadyState,
    bool IsCameraInCornerPipMode,
    bool IsCameraFullTileMode,
    bool IsCameraMirrored,
    string CamAspectCssVar,
    string TileVideoAspectCssVar);

public sealed record InAppPipVisualState(
    bool IsOverlayVisible,
    string Mode,
    string LayoutMode,
    bool PrimaryVideoHasStream,
    bool PrimaryVideoPaused,
    string PrimaryVideoTrackReadyState,
    int PrimaryVideoWidth,
    int PrimaryVideoHeight,
    string? PrimaryTrackParticipantId,
    string? PrimaryTrackSource,
    bool PresenterCamVideoHasStream,
    string? PresenterCamParticipantId,
    bool SelfVideoHasStream,
    bool SelfVideoPaused,
    string SelfVideoTrackReadyState,
    bool SelfVideoMirrored,
    string SelfAspectCssVar,
    IReadOnlyList<string> GridTileParticipantIds,
    IReadOnlyList<string> FilmstripTileParticipantIds);

public sealed record RtpEncodingSnapshot(
    int MaxBitrate,
    int MaxFramerate,
    double ScaleResolutionDownBy,
    string DegradationPreference,
    string VideoCodec,
    bool Simulcast,
    string ContentHint,
    int Width,
    int Height);

public sealed record ClientHardwareSnapshot(
    int LiveLocalTrackCount,
    IReadOnlyList<string> LockedDeviceIds,
    bool NativeIosCompanionConnected,
    RtpEncodingSnapshot? CameraEncoding,
    RtpEncodingSnapshot? ScreenEncoding);

public sealed record ClientVisualSnapshot(
    string ClientId,
    string UserId,
    StageLayoutMode LayoutMode,
    string? FocusedParticipantId,
    IReadOnlyDictionary<string, ParticipantTileVisualState> Tiles,
    InAppPipVisualState InAppPip,
    ClientHardwareSnapshot Hardware);

/// <summary>
/// Multi-client real-world Voice &amp; Video Call Cluster Simulator.
/// Bridges real C# services (<see cref="GlobalVoiceService"/>, <see cref="VoiceDeviceService"/>,
/// <see cref="VoiceUiStateService"/>, <see cref="VoiceChannelService"/>, <see cref="ChatStateService"/>)
/// bidirectionally with real <c>voiceInterop.js</c> and <c>voicePip.js</c> instances running inside a
/// multi-sandbox Node.js WebRTC / LiveKit SFU / OS Hardware / DOM engine.
/// </summary>
public sealed class VoiceCallClusterSimulator : IAsyncDisposable
{
    private readonly string _tempDir;
    private readonly string _voiceInteropOriginalSrc;
    private readonly string _voicePipOriginalSrc;
    private readonly Process _nodeProcess;
    private readonly SemaphoreSlim _stdinLock = new(1, 1);
    private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonElement>> _pendingRequests = new();
    private readonly ConcurrentDictionary<string, SimulatedVoiceClient> _clients = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _stdoutReaderTask;
    private readonly Task _stderrReaderTask;
    private readonly StringBuilder _stderrLog = new();
    private int _nextReqId;

    public ChatStateService ChatState { get; }
    public SimulatedClusterVoiceChannelService VoiceChannelService { get; }

    private VoiceCallClusterSimulator(
        string tempDir,
        string voiceInteropSrc,
        string voicePipSrc,
        Process nodeProcess)
    {
        _tempDir = tempDir;
        _voiceInteropOriginalSrc = voiceInteropSrc;
        _voicePipOriginalSrc = voicePipSrc;
        _nodeProcess = nodeProcess;

        ChatState = new ChatStateService();
        VoiceChannelService = new SimulatedClusterVoiceChannelService(this, ChatState);

        _stdoutReaderTask = Task.Run(ReadStdoutLoopAsync);
        _stderrReaderTask = Task.Run(ReadStderrLoopAsync);
    }

    public static async Task<VoiceCallClusterSimulator> CreateAsync()
    {
        var baseDir = AppContext.BaseDirectory;
        var repoRoot = Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", ".."));
        var voiceInteropPath = Path.Combine(repoRoot, "Spokes_Server", "wwwroot", "js", "voiceInterop.js");
        var voicePipPath = Path.Combine(repoRoot, "Spokes_Server", "wwwroot", "js", "voicePip.js");

        if (!File.Exists(voiceInteropPath))
            throw new FileNotFoundException($"voiceInterop.js not found at {voiceInteropPath}");
        if (!File.Exists(voicePipPath))
            throw new FileNotFoundException($"voicePip.js not found at {voicePipPath}");

        var voiceInteropSrc = await File.ReadAllTextAsync(voiceInteropPath);
        var voicePipSrc = await File.ReadAllTextAsync(voicePipPath);

        var tempDir = Path.Combine(Path.GetTempPath(), $"spokes_voice_cluster_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        await File.WriteAllTextAsync(Path.Combine(tempDir, "package.json"), """{"type":"module"}""", Encoding.UTF8);
        await File.WriteAllTextAsync(Path.Combine(tempDir, "noiseSuppressionManager.mjs"), """
            export class NoiseSuppressionManager {
                async getProcessor() { return null; }
            }
            """, Encoding.UTF8);

        await File.WriteAllTextAsync(Path.Combine(tempDir, "cluster_host.mjs"), GetClusterNodeHostScript(), Encoding.UTF8);

        var psi = new ProcessStartInfo
        {
            FileName = "node",
            Arguments = "cluster_host.mjs",
            WorkingDirectory = tempDir,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false)
        };

        var proc = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start node process for VoiceCallClusterSimulator.");
        var sim = new VoiceCallClusterSimulator(tempDir, voiceInteropSrc, voicePipSrc, proc);

        // Wait for Node engine ready ping
        await sim.SendNodeCommandAsync("ping", new { });
        return sim;
    }

    public async Task<SimulatedVoiceClient> CreateClientAsync(
        string clientId,
        string userId,
        VoiceClientPlatform platform,
        string? sharedHardwareGroupWithClientId = null)
    {
        // Write per-client isolated ES module copies of voicePip.js and voiceInterop.js
        // that lexically bind browser globals (window, document, navigator, localStorage, etc.)
        // to globalThis.__spokesClientEnvs[clientId].
        var envPreamble = $$"""
            const {
                window,
                document,
                navigator,
                localStorage,
                MediaStream,
                CustomEvent,
                MutationObserver,
                Audio,
                RTCRtpSender,
                requestAnimationFrame,
                cancelAnimationFrame
            } = globalThis.__spokesClientEnvs['{{clientId}}'];
            """;

        var pipFileName = $"voicePip_{clientId}.mjs";
        var interopFileName = $"voiceInterop_{clientId}.mjs";

        await File.WriteAllTextAsync(
            Path.Combine(_tempDir, pipFileName),
            envPreamble + "\n" + _voicePipOriginalSrc,
            Encoding.UTF8);

        var patchedInterop = System.Text.RegularExpressions.Regex.Replace(
            _voiceInteropOriginalSrc,
            @"\./noiseSuppressionManager\.js(\?[^'""]+)?",
            "./noiseSuppressionManager.mjs");
        patchedInterop = System.Text.RegularExpressions.Regex.Replace(
            patchedInterop,
            @"\./voicePip\.js(\?[^'""]+)?",
            $"./{pipFileName}");

        await File.WriteAllTextAsync(
            Path.Combine(_tempDir, interopFileName),
            envPreamble + "\n" + patchedInterop,
            Encoding.UTF8);

        string platformStr = platform switch
        {
            VoiceClientPlatform.IosNative => "ios",
            VoiceClientPlatform.AndroidNative => "android",
            VoiceClientPlatform.DesktopElectron => "desktop",
            _ => "web"
        };

        await SendNodeCommandAsync("initClient", new
        {
            clientId,
            userId,
            platform = platformStr,
            interopFile = $"./{interopFileName}",
            sharedHardwareClientId = sharedHardwareGroupWithClientId ?? clientId
        });

        var client = new SimulatedVoiceClient(this, clientId, userId, platform);
        _clients[clientId] = client;
        return client;
    }

    internal async Task<JsonElement> SendNodeCommandAsync(string action, object payload)
    {
        if (_nodeProcess.HasExited)
        {
            throw new InvalidOperationException($"Node simulator process exited unexpectedly. STDERR:\n{_stderrLog}");
        }

        int id = Interlocked.Increment(ref _nextReqId);
        var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingRequests[id] = tcs;

        var json = JsonSerializer.Serialize(new
        {
            type = "req",
            id,
            action,
            payload
        });

        await WriteStdinLineAsync(json);

        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var reg = timeoutCts.Token.Register(() =>
        {
            if (_pendingRequests.TryRemove(id, out var pending))
            {
                pending.TrySetException(new TimeoutException($"Timed out waiting for Node action '{action}' (reqId={id}). STDERR:\n{_stderrLog}"));
            }
        });

        return await tcs.Task;
    }

    private async Task WriteStdinLineAsync(string line)
    {
        await _stdinLock.WaitAsync(_cts.Token);
        try
        {
            await _nodeProcess.StandardInput.WriteLineAsync(line);
            await _nodeProcess.StandardInput.FlushAsync();
        }
        finally
        {
            _stdinLock.Release();
        }
    }

    private async Task ReadStdoutLoopAsync()
    {
        try
        {
            while (!_cts.IsCancellationRequested && !_nodeProcess.HasExited)
            {
                var line = await _nodeProcess.StandardOutput.ReadLineAsync();
                if (line == null) break;
                if (!line.StartsWith("{", StringComparison.Ordinal)) continue;

                JsonDocument doc;
                try
                {
                    doc = JsonDocument.Parse(line);
                }
                catch
                {
                    continue;
                }

                using (doc)
                {
                    var root = doc.RootElement;
                    if (!root.TryGetProperty("type", out var typeProp)) continue;
                    var msgType = typeProp.GetString();

                    if (msgType == "res")
                    {
                        int id = root.GetProperty("id").GetInt32();
                        if (_pendingRequests.TryRemove(id, out var tcs))
                        {
                            if (root.TryGetProperty("error", out var errProp) && errProp.ValueKind != JsonValueKind.Null)
                            {
                                tcs.TrySetException(new JSException(errProp.GetString() ?? "JS error"));
                            }
                            else if (root.TryGetProperty("result", out var resProp))
                            {
                                tcs.TrySetResult(resProp.Clone());
                            }
                            else
                            {
                                tcs.TrySetResult(default);
                            }
                        }
                    }
                    else if (msgType == "dotnet_call")
                    {
                        int callId = root.GetProperty("callId").GetInt32();
                        string clientId = root.GetProperty("clientId").GetString() ?? "";
                        string method = root.GetProperty("method").GetString() ?? "";
                        var args = root.GetProperty("args").Clone();

                        _ = Task.Run(async () =>
                        {
                            try
                            {
                                object? result = null;
                                if (_clients.TryGetValue(clientId, out var client))
                                {
                                    result = await client.InvokeDotNetCallbackAsync(method, args);
                                }
                                var reply = JsonSerializer.Serialize(new
                                {
                                    type = "dotnet_res",
                                    callId,
                                    result
                                });
                                await WriteStdinLineAsync(reply);
                            }
                            catch (Exception ex)
                            {
                                var errReply = JsonSerializer.Serialize(new
                                {
                                    type = "dotnet_res",
                                    callId,
                                    error = ex.Message
                                });
                                await WriteStdinLineAsync(errReply);
                            }
                        });
                    }
                }
            }
        }
        catch (Exception)
        {
            // Ignore shutdown exceptions
        }
        finally
        {
            foreach (var kvp in _pendingRequests)
            {
                kvp.Value.TrySetException(new InvalidOperationException($"Node process terminated. STDERR:\n{_stderrLog}"));
            }
        }
    }

    private async Task ReadStderrLoopAsync()
    {
        try
        {
            while (!_cts.IsCancellationRequested && !_nodeProcess.HasExited)
            {
                var line = await _nodeProcess.StandardError.ReadLineAsync();
                if (line == null) break;
                lock (_stderrLog)
                {
                    _stderrLog.AppendLine(line);
                }
            }
        }
        catch
        {
        }
    }

    /// <summary>
    /// Wait for any in-flight asynchronous SFU track subscriptions, DOM MutationObservers,
    /// and JS -> C# callbacks across all clients in the cluster to settle.
    /// </summary>
    public async Task SettleAsync(int delayMs = 80)
    {
        await Task.Delay(delayMs);
        await SendNodeCommandAsync("flush", new { });
        foreach (var client in _clients.Values)
        {
            await client.SyncStageAndPipDomAsync();
        }
        await Task.Delay(40);
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        foreach (var client in _clients.Values)
        {
            await client.DisposeAsync();
        }
        try
        {
            if (!_nodeProcess.HasExited)
            {
                _nodeProcess.Kill(entireProcessTree: true);
            }
        }
        catch { }
        _nodeProcess.Dispose();
        _stdinLock.Dispose();
        _cts.Dispose();

        if (Directory.Exists(_tempDir))
        {
            try { Directory.Delete(_tempDir, recursive: true); } catch { }
        }
    }

    private static string GetClusterNodeHostScript() => """
        import readline from 'node:readline';

        globalThis.__spokesClientEnvs = {};
        const clientModules = new Map();
        let nextDotnetCallId = 1;
        const pendingDotnetCalls = new Map();

        function sendStdout(obj) {
            process.stdout.write(JSON.stringify(obj) + '\n');
        }

        function invokeDotnetOnClient(clientId, method, args) {
            return new Promise((resolve, reject) => {
                const callId = nextDotnetCallId++;
                pendingDotnetCalls.set(callId, { resolve, reject });
                sendStdout({
                    type: 'dotnet_call',
                    callId,
                    clientId,
                    method,
                    args
                });
            });
        }

        // ====================================================================
        // Shared Simulated LiveKit SFU Server (Multi-Client Room Coordinator)
        // ====================================================================
        const Track = {
            Kind: { Audio: 'audio', Video: 'video' },
            Source: {
                Camera: 'camera',
                Microphone: 'microphone',
                ScreenShare: 'screen_share',
                ScreenShareAudio: 'screen_share_audio'
            }
        };

        const RoomEvent = {
            TrackSubscribed: 'trackSubscribed',
            TrackUnsubscribed: 'trackUnsubscribed',
            ActiveSpeakersChanged: 'activeSpeakersChanged',
            Disconnected: 'disconnected',
            LocalTrackPublished: 'localTrackPublished',
            LocalTrackUnpublished: 'localTrackUnpublished',
            ParticipantConnected: 'participantConnected',
            ParticipantDisconnected: 'participantDisconnected',
            TrackMuted: 'trackMuted',
            TrackUnmuted: 'trackUnmuted'
        };

        class SimulatedSfuRoom {
            constructor(name) {
                this.name = name;
                // Map<participantIdentity, SfuParticipantSession>
                this.participants = new Map();
            }

            join(session) {
                // If an older session with same identity exists, disconnect it first
                const prev = this.participants.get(session.identity);
                if (prev && prev !== session) {
                    this.leave(prev.identity, prev);
                }

                // Populate remoteParticipants on the joining session from existing participants
                for (const [existingId, existingSession] of this.participants.entries()) {
                    if (existingId === session.identity) continue;
                    const remoteView = session.getOrCreateRemoteParticipantView(existingSession);
                    for (const pub of existingSession.publishedTracks.values()) {
                        remoteView.attachPublication(pub);
                    }
                }

                this.participants.set(session.identity, session);

                // Notify existing participants of ParticipantConnected
                for (const [existingId, existingSession] of this.participants.entries()) {
                    if (existingId === session.identity) continue;
                    const remoteView = existingSession.getOrCreateRemoteParticipantView(session);
                    existingSession.roomInstance?.emit(RoomEvent.ParticipantConnected, remoteView);
                }

                // Deliver TrackSubscribed for existing tracks to the newly joined session asynchronously
                setTimeout(() => {
                    if (this.participants.get(session.identity) !== session) return;
                    for (const [existingId, existingSession] of this.participants.entries()) {
                        if (existingId === session.identity) continue;
                        const remoteView = session.getOrCreateRemoteParticipantView(existingSession);
                        for (const pub of existingSession.publishedTracks.values()) {
                            const remotePub = remoteView.getPublication(pub.trackSid);
                            if (remotePub && remotePub.track) {
                                session.roomInstance?.emit(RoomEvent.TrackSubscribed, remotePub.track, remotePub, remoteView);
                            }
                        }
                    }
                }, 5);
            }

            publishTrack(session, pub) {
                session.publishedTracks.set(pub.trackSid, pub);
                for (const [otherId, otherSession] of this.participants.entries()) {
                    if (otherId === session.identity) continue;
                    const remoteView = otherSession.getOrCreateRemoteParticipantView(session);
                    const remotePub = remoteView.attachPublication(pub);
                    otherSession.roomInstance?.emit(RoomEvent.TrackSubscribed, remotePub.track, remotePub, remoteView);
                }
            }

            unpublishTrack(session, trackSid) {
                const pub = session.publishedTracks.get(trackSid);
                if (!pub) return;
                session.publishedTracks.delete(trackSid);

                for (const [otherId, otherSession] of this.participants.entries()) {
                    if (otherId === session.identity) continue;
                    const remoteView = otherSession.remoteParticipants.get(session.identity);
                    if (!remoteView) continue;
                    const remotePub = remoteView.removePublication(trackSid);
                    if (remotePub) {
                        otherSession.roomInstance?.emit(RoomEvent.TrackUnsubscribed, remotePub.track, remotePub, remoteView);
                    }
                }
            }

            notifyTrackDimensionChange(session, pub) {
                for (const [otherId, otherSession] of this.participants.entries()) {
                    if (otherId === session.identity) continue;
                    const remoteView = otherSession.remoteParticipants.get(session.identity);
                    if (!remoteView) continue;
                    const remotePub = remoteView.getPublication(pub.trackSid);
                    if (remotePub && remotePub.track) {
                        remotePub.track.mediaStreamTrack = pub.track.mediaStreamTrack;
                        remotePub.track._mediaStreamTrack = pub.track.mediaStreamTrack;
                        const s = pub.track.mediaStreamTrack?.getSettings?.() || {};
                        const params = pub.track.sender?.getParameters?.() || { encodings: [] };
                        const topEnc = params.encodings?.[params.encodings.length - 1] || {};
                        const scale = (topEnc.scaleResolutionDownBy && topEnc.scaleResolutionDownBy > 0) ? topEnc.scaleResolutionDownBy : 1.0;
                        if (s.width > 0 && s.height > 0) {
                            const effW = Math.round(s.width / scale);
                            const effH = Math.round(s.height / scale);
                            remotePub.videoDimensions = { width: effW, height: effH };
                            remotePub.dimensions = { width: effW, height: effH };
                            remotePub.track.dimensions = { width: effW, height: effH };
                        }
                        otherSession.roomInstance?.emit(RoomEvent.TrackSubscribed, remotePub.track, remotePub, remoteView);
                    }
                }
            }

            leave(identity, specificSession = null) {
                const session = this.participants.get(identity);
                if (!session) return;
                if (specificSession && session !== specificSession) return;

                this.participants.delete(identity);

                for (const [otherId, otherSession] of this.participants.entries()) {
                    if (otherId === identity) continue;
                    const remoteView = otherSession.remoteParticipants.get(identity);
                    if (!remoteView) continue;
                    for (const remotePub of Array.from(remoteView.allPublications.values())) {
                        remoteView.removePublication(remotePub.trackSid);
                        otherSession.roomInstance?.emit(RoomEvent.TrackUnsubscribed, remotePub.track, remotePub, remoteView);
                    }
                    otherSession.remoteParticipants.delete(identity);
                    otherSession.roomInstance?.emit(RoomEvent.ParticipantDisconnected, remoteView);
                }
            }
        }

        const sfuRooms = new Map();
        function getOrCreateSfuRoom(roomName) {
            if (!sfuRooms.has(roomName)) {
                sfuRooms.set(roomName, new SimulatedSfuRoom(roomName));
            }
            return sfuRooms.get(roomName);
        }

        // ====================================================================
        // Hardware Exclusivity Table (Shared per Physical Device / Machine)
        // ====================================================================
        const hardwareTables = new Map();
        function getHardwareTable(hardwareGroupId, platform) {
            if (!hardwareTables.has(hardwareGroupId)) {
                const devices = platform === 'ios'
                    ? [
                        { kind: 'videoinput', deviceId: 'ios_front', label: 'Front Camera', facingMode: 'user', maxWidth: 1920, maxHeight: 1080, maxFps: 60 },
                        { kind: 'videoinput', deviceId: 'ios_back', label: 'Back Camera (Wide)', facingMode: 'environment', maxWidth: 3840, maxHeight: 2160, maxFps: 60 },
                        { kind: 'audioinput', deviceId: 'ios_mic', label: 'iPhone Microphone' }
                    ]
                    : platform === 'android'
                    ? [
                        { kind: 'videoinput', deviceId: '0', cameraId: '0', label: 'Back Camera (Wide)', facingMode: 'environment', maxWidth: 3840, maxHeight: 2160, maxFps: 60 },
                        { kind: 'videoinput', deviceId: '1', cameraId: '1', label: 'Front Camera', facingMode: 'user', maxWidth: 3840, maxHeight: 2160, maxFps: 60 },
                        { kind: 'audioinput', deviceId: 'default', label: 'Android Telecom Mic' }
                    ]
                    : [
                        { kind: 'videoinput', deviceId: 'cam-hd-front', label: 'Logitech Brio 4K', facingMode: 'user', maxWidth: 3840, maxHeight: 2160, maxFps: 60 },
                        { kind: 'videoinput', deviceId: 'cam-rear-studio', label: 'Elgato Facecam Pro', facingMode: 'environment', maxWidth: 3840, maxHeight: 2160, maxFps: 60 },
                        { kind: 'audioinput', deviceId: 'default', label: 'System Default' },
                        { kind: 'audioinput', deviceId: 'communications', label: 'Default Communications Mic' },
                        { kind: 'audioinput', deviceId: 'mic-shure-mv7', label: 'Shure MV7 USB' },
                        { kind: 'audiooutput', deviceId: 'default', label: 'System Default' },
                        { kind: 'audiooutput', deviceId: 'spk-studio-headphones', label: 'Studio Headphones' }
                    ];
                hardwareTables.set(hardwareGroupId, {
                    devices,
                    // Map<deviceId, Set<MediaStreamTrack>>
                    activeLocks: new Map()
                });
            }
            return hardwareTables.get(hardwareGroupId);
        }

        let globalTrackCounter = 1;

        function acquireHardwareTrack(hwTable, ownerClientId, kind, options = {}) {
            let resolvedDeviceId = options.deviceId;
            let resolvedFacing = options.facingMode || 'user';
            if (kind === 'video-screen') {
                resolvedDeviceId = `screen-display-${ownerClientId}`;
            } else if (kind === 'audio-screen') {
                resolvedDeviceId = `screen-audio-${ownerClientId}`;
            } else if (kind === 'video') {
                const videoDevs = hwTable.devices.filter(d => d.kind === 'videoinput');
                let match = videoDevs.find(d => d.deviceId === resolvedDeviceId);
                if (!match) {
                    match = videoDevs.find(d => d.facingMode === resolvedFacing) || videoDevs[0];
                }
                resolvedDeviceId = match?.deviceId || 'cam-default';
                resolvedFacing = match?.facingMode || resolvedFacing;
            } else if (kind === 'audio') {
                const audioDevs = hwTable.devices.filter(d => d.kind === 'audioinput');
                let match = audioDevs.find(d => d.deviceId === resolvedDeviceId && d.deviceId !== 'default' && d.deviceId !== 'communications');
                if (!match) {
                    match = audioDevs.find(d => d.deviceId !== 'default' && d.deviceId !== 'communications') || audioDevs[0];
                }
                resolvedDeviceId = match?.deviceId || 'mic-default';
            }

            // Enforce OS Hardware Exclusivity Lock on physical camera devices:
            // If another live MediaStreamTrack already holds this camera device, throw NotReadableError: Device in use!
            if (kind === 'video') {
                const existingSet = hwTable.activeLocks.get(resolvedDeviceId);
                if (existingSet) {
                    for (const existingTrack of Array.from(existingSet)) {
                        if (existingTrack.readyState === 'live') {
                            const err = new Error('Could not start video source: Device in use');
                            err.name = 'NotReadableError';
                            throw err;
                        } else {
                            existingSet.delete(existingTrack);
                        }
                    }
                }
            }

            const width = options.width || (kind === 'video-screen' ? 2560 : 1920);
            const height = options.height || (kind === 'video-screen' ? 1440 : 1080);
            const frameRate = options.frameRate || 60;

            const listeners = new Map();
            const mst = {
                id: `mst_${globalTrackCounter++}`,
                ownerClientId,
                kind: kind.startsWith('video') ? 'video' : 'audio',
                deviceId: resolvedDeviceId,
                facingMode: resolvedFacing,
                width,
                height,
                frameRate,
                enabled: true,
                muted: false,
                readyState: 'live',
                contentHint: '',
                stop() {
                    this.readyState = 'ended';
                    const set = hwTable.activeLocks.get(this.deviceId);
                    if (set) {
                        set.delete(this);
                        if (set.size === 0) hwTable.activeLocks.delete(this.deviceId);
                    }
                },
                getSettings() {
                    if (this.kind === 'video') {
                        return {
                            width: this.width,
                            height: this.height,
                            frameRate: this.frameRate,
                            deviceId: this.deviceId,
                            facingMode: this.facingMode
                        };
                    }
                    return {
                        deviceId: this.deviceId,
                        latency: 0.015
                    };
                },
                getCapabilities() {
                    if (this.kind === 'video') {
                        return {
                            width: { min: 320, max: 3840 },
                            height: { min: 240, max: 2160 },
                            frameRate: { min: 1, max: 60 }
                        };
                    }
                    return {};
                },
                async applyConstraints(constraints = {}) {
                    if (constraints.frameRate) {
                        const f = typeof constraints.frameRate === 'object'
                            ? (constraints.frameRate.max || constraints.frameRate.ideal || this.frameRate)
                            : constraints.frameRate;
                        if (f > 0) this.frameRate = f;
                    }
                },
                addEventListener(evt, fn) {
                    if (!listeners.has(evt)) listeners.set(evt, new Set());
                    listeners.get(evt).add(fn);
                },
                removeEventListener(evt, fn) {
                    listeners.get(evt)?.delete(fn);
                },
                dispatchEvent(evt) {
                    for (const fn of Array.from(listeners.get(evt) || [])) {
                        fn();
                    }
                }
            };

            if (!hwTable.activeLocks.has(resolvedDeviceId)) {
                hwTable.activeLocks.set(resolvedDeviceId, new Set());
            }
            hwTable.activeLocks.get(resolvedDeviceId).add(mst);
            return mst;
        }

        // ====================================================================
        // Strict Chromium RTCRtpSender & LiveKit Track Simulator
        // ====================================================================
        function createStrictRtpSender(initialBitrate = 3000000, initialFps = 30, onParamsUpdated = null) {
            const currentParams = {
                encodings: [{ maxBitrate: initialBitrate, maxFramerate: initialFps, scaleResolutionDownBy: 1.0 }]
            };
            return {
                getParameters() {
                    return JSON.parse(JSON.stringify(currentParams));
                },
                async setParameters(params) {
                    if ('degradationPreference' in params) {
                        const err = new Error("Failed to execute 'setParameters' on 'RTCRtpSender': Attempted to set an unimplemented parameter of RtpParameters.");
                        err.name = 'OperationError';
                        throw err;
                    }
                    for (const enc of params.encodings || []) {
                        if ('priority' in enc || 'networkPriority' in enc) {
                            const err = new Error("Failed to execute 'setParameters' on 'RTCRtpSender': Attempted to set an unimplemented parameter of RtpParameters.");
                            err.name = 'OperationError';
                            throw err;
                        }
                    }
                    currentParams.encodings = JSON.parse(JSON.stringify(params.encodings));
                    if (typeof onParamsUpdated === 'function') {
                        onParamsUpdated();
                    }
                },
                async getStats() {
                    return [{ type: 'candidate-pair', state: 'succeeded', nominated: true, currentRoundTripTime: 0.018 }];
                }
            };
        }

        function createLivekitTrackFromMediaStreamTrack(mst, source, createElementFn) {
            const attached = [];
            let trackObj = null;
            const sender = mst.kind === 'video'
                ? createStrictRtpSender(3000000, mst.frameRate || 30, () => {
                    if (trackObj?._onSenderParamsUpdated) {
                        trackObj._onSenderParamsUpdated();
                    }
                })
                : null;
            trackObj = {
                sid: `TR_${globalTrackCounter++}`,
                kind: mst.kind,
                source,
                mediaStreamTrack: mst,
                _mediaStreamTrack: mst,
                sender,
                degradationPreference: 'balanced',
                publishOptionsUsed: null,
                dimensions: mst.kind === 'video' ? { width: mst.width, height: mst.height } : undefined,
                attachedElements: attached,
                attach(el) {
                    const currentMst = this.mediaStreamTrack;
                    const target = el || createElementFn(currentMst.kind === 'video' ? 'video' : 'audio');
                    if (currentMst.kind === 'video') {
                        target.videoWidth = this.dimensions?.width || currentMst.width || 1920;
                        target.videoHeight = this.dimensions?.height || currentMst.height || 1080;
                        target.srcObject = {
                            getVideoTracks: () => [this.mediaStreamTrack],
                            getAudioTracks: () => [],
                            getTracks: () => [this.mediaStreamTrack]
                        };
                    } else {
                        target.srcObject = {
                            getVideoTracks: () => [],
                            getAudioTracks: () => [this.mediaStreamTrack],
                            getTracks: () => [this.mediaStreamTrack]
                        };
                    }
                    if (!attached.includes(target)) attached.push(target);
                    return target;
                },
                detach(specificEl) {
                    if (specificEl) {
                        const idx = attached.indexOf(specificEl);
                        if (idx !== -1) attached.splice(idx, 1);
                        return specificEl;
                    }
                    const copy = [...attached];
                    attached.length = 0;
                    return copy;
                },
                stop() {
                    this.mediaStreamTrack.stop();
                },
                async setDegradationPreference(pref) {
                    this.degradationPreference = pref;
                },
                async restartTrack(restartOpts = {}) {
                    // Stop old hardware lock before acquiring new one
                    const oldMst = this.mediaStreamTrack;
                    const hwTable = oldMst._hwTable;
                    const ownerClientId = oldMst.ownerClientId;
                    oldMst.stop();
                    const res = restartOpts.resolution || {};
                    const newMst = acquireHardwareTrack(hwTable, ownerClientId, 'video', {
                        deviceId: restartOpts.deviceId,
                        facingMode: restartOpts.facingMode || oldMst.facingMode,
                        width: res.width || oldMst.width,
                        height: res.height || oldMst.height,
                        frameRate: res.maxFramerate || oldMst.frameRate
                    });
                    newMst._hwTable = hwTable;
                    this.mediaStreamTrack = newMst;
                    this._mediaStreamTrack = newMst;
                    this.dimensions = { width: newMst.width, height: newMst.height };
                    for (const el of attached) {
                        el.videoWidth = newMst.width;
                        el.videoHeight = newMst.height;
                        el.srcObject = {
                            getVideoTracks: () => [this.mediaStreamTrack],
                            getAudioTracks: () => [],
                            getTracks: () => [this.mediaStreamTrack]
                        };
                    }
                    if (this._onSenderParamsUpdated) {
                        this._onSenderParamsUpdated();
                    }
                }
            };
            return trackObj;
        }

        // ====================================================================
        // Per-Client Isolated DOM & WebRTC Environment Factory
        // ====================================================================
        function createClientEnvironment(clientId, userId, platform, sharedHardwareClientId) {
            const hwTable = getHardwareTable(sharedHardwareClientId, platform);
            const domById = new Map();
            const mutationObservers = new Set();
            const createdMediaStreamTracks = [];

            function notifyMutationObservers() {
                for (const obs of Array.from(mutationObservers)) {
                    try { obs._callback([], obs); } catch (e) {}
                }
            }

            function createDomElement(tagName, id = '', initialClasses = []) {
                const tag = String(tagName || 'DIV').toUpperCase();
                const classes = new Set(initialClasses.filter(Boolean));
                const styles = new Map();
                const listeners = new Map();
                const children = [];
                const attributes = new Map();

                const el = {
                    id,
                    tagName: tag,
                    isConnected: true,
                    parentNode: null,
                    parentElement: null,
                    children,
                    dataset: { corner: 'bottom-right' },
                    paused: true,
                    muted: false,
                    defaultMuted: false,
                    autoplay: true,
                    playsInline: true,
                    disablePictureInPicture: false,
                    volume: 1.0,
                    readyState: 4,
                    srcObject: null,
                    videoWidth: 1920,
                    videoHeight: 1080,
                    innerHTML: '',
                    textContent: '',
                    get className() { return Array.from(classes).join(' '); },
                    set className(val) {
                        classes.clear();
                        String(val || '').split(/\s+/).filter(Boolean).forEach(c => classes.add(c));
                    },
                    style: {
                        opacity: '0',
                        pointerEvents: 'none',
                        transform: '',
                        display: '',
                        left: '',
                        right: '',
                        top: '',
                        bottom: '',
                        cssText: '',
                        setProperty(k, v) { styles.set(k, String(v)); },
                        getPropertyValue(k) { return styles.get(k) || ''; },
                        removeProperty(k) { styles.delete(k); }
                    },
                    classList: {
                        add(...clsList) { clsList.forEach(c => { if (c) classes.add(c); }); },
                        remove(...clsList) { clsList.forEach(c => classes.delete(c)); },
                        contains(c) { return classes.has(c); },
                        toggle(c, force) {
                            const shouldAdd = force !== undefined ? Boolean(force) : !classes.has(c);
                            if (shouldAdd) classes.add(c);
                            else classes.delete(c);
                            return shouldAdd;
                        },
                        toArray() { return Array.from(classes); }
                    },
                    setAttribute(k, v) { attributes.set(k, String(v)); },
                    getAttribute(k) { return attributes.get(k) ?? null; },
                    removeAttribute(k) { attributes.delete(k); },
                    pause() { this.paused = true; },
                    load() {},
                    async play() { this.paused = false; },
                    async setSinkId() {},
                    addEventListener(evt, fn) {
                        if (!listeners.has(evt)) listeners.set(evt, new Set());
                        listeners.get(evt).add(fn);
                    },
                    removeEventListener(evt, fn) {
                        listeners.get(evt)?.delete(fn);
                    },
                    dispatchEvent(evtObj) {
                        const evtName = typeof evtObj === 'string' ? evtObj : evtObj?.type;
                        for (const fn of Array.from(listeners.get(evtName) || [])) {
                            fn(evtObj);
                        }
                    },
                    appendChild(child) {
                        if (!child) return child;
                        child.parentNode = this;
                        child.parentElement = this;
                        child.isConnected = this.isConnected;
                        if (!children.includes(child)) children.push(child);
                        if (child.id) domById.set(child.id, child);
                        notifyMutationObservers();
                        return child;
                    },
                    removeChild(child) {
                        const idx = children.indexOf(child);
                        if (idx !== -1) children.splice(idx, 1);
                        if (child) {
                            child.parentNode = null;
                            child.parentElement = null;
                            child.isConnected = false;
                            if (child.id && domById.get(child.id) === child) {
                                domById.delete(child.id);
                            }
                        }
                        return child;
                    },
                    getBoundingClientRect() {
                        return { left: 800, top: 500, width: 240, height: 150, right: 1040, bottom: 650 };
                    },
                    closest(selector) {
                        if (selector === '.participant-rect') {
                            return this._participantRect || null;
                        }
                        if (selector === '#spokes-voice-pip-overlay') {
                            let cur = this;
                            while (cur) {
                                if (cur.id === 'spokes-voice-pip-overlay') return cur;
                                cur = cur.parentElement;
                            }
                        }
                        return null;
                    },
                    remove() {
                        if (this.parentElement) {
                            this.parentElement.removeChild(this);
                        }
                    },
                    querySelector(selector) {
                        return this.querySelectorAll(selector)[0] || null;
                    },
                    querySelectorAll(selector) {
                        if (selector === 'video.livekit-screenshare.has-stream') {
                            return children.filter(c => c.tagName === 'VIDEO' && c.classList.contains('livekit-screenshare') && c.classList.contains('has-stream'));
                        }
                        if (selector === 'video.has-stream') {
                            return children.filter(c => c.tagName === 'VIDEO' && c.classList.contains('has-stream'));
                        }
                        if (selector === 'video.livekit-camera, video.livekit-screenshare') {
                            return Array.from(domById.values()).filter(c => c.tagName === 'VIDEO' && (c.classList.contains('livekit-camera') || c.classList.contains('livekit-screenshare')));
                        }
                        if (selector === 'audio') {
                            return children.filter(c => c.tagName === 'AUDIO');
                        }
                        if (selector === 'video') {
                            return children.filter(c => c.tagName === 'VIDEO');
                        }
                        if (selector === ':scope > .pip-participant-tile') {
                            return children.filter(c => c.classList.contains('pip-participant-tile'));
                        }
                        if (selector.startsWith(':scope > .pip-participant-tile[data-participant-id="')) {
                            const pid = selector.slice(':scope > .pip-participant-tile[data-participant-id="'.length, -2);
                            return children.filter(c => c.classList.contains('pip-participant-tile') && c.dataset?.participantId === pid);
                        }
                        if (selector.startsWith('.')) {
                            const cls = selector.slice(1);
                            const matches = [];
                            const walk = (node) => {
                                for (const ch of node.children || []) {
                                    if (ch.classList?.contains(cls)) matches.push(ch);
                                    walk(ch);
                                }
                            };
                            walk(this);
                            return matches;
                        }
                        return [];
                    }
                };
                if (id) domById.set(id, el);
                return el;
            }

            const documentBody = createDomElement('BODY', 'body');
            const documentElement = createDomElement('HTML', 'html');
            const audioContainer = createDomElement('DIV', 'voice-audio-container');
            documentBody.appendChild(audioContainer);

            // Mount permanent In-App PiP overlay elements (<VoicePipOverlay />)
            const pipOverlay = createDomElement('DIV', 'spokes-voice-pip-overlay', ['voice-pip-overlay', 'mode-avatar', 'is-stage-hidden']);
            const pipPrimaryVideo = createDomElement('VIDEO', 'spokes-pip-primary-video', ['voice-pip-primary-video']);
            const pipPresenterCamVideo = createDomElement('VIDEO', 'spokes-pip-presenter-cam-video', ['voice-pip-presenter-cam-video']);
            const pipSelfVideo = createDomElement('VIDEO', 'spokes-pip-self-video', ['voice-pip-self-video']);
            const pipGridContainer = createDomElement('DIV', 'spokes-pip-grid-container', ['voice-pip-grid']);
            const pipFilmstrip = createDomElement('DIV', 'spokes-pip-filmstrip', ['voice-pip-filmstrip']);
            pipOverlay.appendChild(pipPrimaryVideo);
            pipOverlay.appendChild(pipPresenterCamVideo);
            pipOverlay.appendChild(pipSelfVideo);
            pipOverlay.appendChild(pipGridContainer);
            pipOverlay.appendChild(pipFilmstrip);
            documentBody.appendChild(pipOverlay);

            function mountCallStage(participantIds) {
                let stageContainer = domById.get('voice-video-container');
                if (!stageContainer) {
                    stageContainer = createDomElement('DIV', 'voice-video-container');
                    documentBody.appendChild(stageContainer);
                }
                for (const pid of participantIds) {
                    ensureParticipantSlotMounted(pid);
                }
                notifyMutationObservers();
            }

            function ensureParticipantSlotMounted(pid) {
                if (!domById.has('voice-video-container')) return;
                const rectId = `participant-rect-${pid}`;
                let rectEl = domById.get(rectId);
                if (!rectEl) {
                    rectEl = createDomElement('DIV', rectId, ['participant-rect']);
                }
                const screenId = `participant-screen-${pid}`;
                if (!domById.has(screenId)) {
                    const screenEl = createDomElement('VIDEO', screenId, ['livekit-screenshare']);
                    screenEl._participantRect = rectEl;
                    rectEl.appendChild(screenEl);
                }
                const camId = `participant-video-${pid}`;
                if (!domById.has(camId)) {
                    const camEl = createDomElement('VIDEO', camId, ['livekit-camera']);
                    camEl._participantRect = rectEl;
                    rectEl.appendChild(camEl);
                }
                notifyMutationObservers();
            }

            function unmountCallStage() {
                for (const key of Array.from(domById.keys())) {
                    if (key === 'voice-video-container'
                        || key.startsWith('participant-video-')
                        || key.startsWith('participant-screen-')
                        || key.startsWith('participant-rect-')) {
                        const el = domById.get(key);
                        if (el) el.isConnected = false;
                        domById.delete(key);
                    }
                }
            }

            // Start with stage mounted by default
            mountCallStage([userId]);

            class SimulatedMediaStream {
                constructor(tracks = []) {
                    this._tracks = [...tracks];
                }
                getVideoTracks() { return this._tracks.filter(t => t.kind === 'video'); }
                getAudioTracks() { return this._tracks.filter(t => t.kind === 'audio'); }
                getTracks() { return [...this._tracks]; }
            }

            class SimulatedMutationObserver {
                constructor(callback) {
                    this._callback = callback;
                }
                observe() { mutationObservers.add(this); }
                disconnect() { mutationObservers.delete(this); }
            }

            const documentObj = {
                visibilityState: 'visible',
                pictureInPictureEnabled: true,
                pictureInPictureElement: null,
                body: documentBody,
                documentElement,
                 ...(platform === 'ios' ? { ontouchend: null } : {}),
                addEventListener() {},
                removeEventListener() {},
                getElementById(id) { return domById.get(id) || null; },
                createElement(tag) { return createDomElement(tag); },
                querySelectorAll(sel) {
                    if (sel === 'video.has-stream') {
                        return Array.from(domById.values()).filter(
                            el => el.tagName === 'VIDEO'
                                && el.id !== 'spokes-pip-primary-video'
                                && el.id !== 'spokes-pip-self-video'
                                && el.classList.contains('has-stream')
                        );
                    }
                    if (sel === '.participant-rect.is-speaking') {
                        return Array.from(domById.values()).filter(
                            el => el.id?.startsWith('participant-rect-') && el.classList.contains('is-speaking')
                        );
                    }
                    return [];
                }
            };
            const simulatedOwnerDoc = {
                defaultView: { MediaStream: SimulatedMediaStream },
                createElement(tag) {
                    const el = createDomElement(tag);
                    el.ownerDocument = simulatedOwnerDoc;
                    let rawHtml = '';
                    Object.defineProperty(el, 'innerHTML', {
                        get() { return rawHtml; },
                        set(val) {
                            rawHtml = String(val || '');
                            el.children.length = 0;
                            if (rawHtml.includes('pip-tile-video')) {
                                const v1 = createDomElement('VIDEO', '', ['pip-tile-video']);
                                v1.ownerDocument = simulatedOwnerDoc;
                                const v2 = createDomElement('VIDEO', '', ['pip-tile-presenter-cam']);
                                v2.ownerDocument = simulatedOwnerDoc;
                                const avWrap = createDomElement('DIV', '', ['pip-tile-avatar-wrap']);
                                const initEl = createDomElement('SPAN', '', ['pip-tile-initials']);
                                const imgEl = createDomElement('IMG', '', ['pip-tile-avatar-img']);
                                avWrap.appendChild(initEl);
                                avWrap.appendChild(imgEl);
                                const footer = createDomElement('DIV', '', ['pip-tile-footer']);
                                const nameEl = createDomElement('SPAN', '', ['pip-tile-name']);
                                const muteEl = createDomElement('SPAN', '', ['pip-tile-mute']);
                                footer.appendChild(nameEl);
                                footer.appendChild(muteEl);
                                el.appendChild(v1);
                                el.appendChild(v2);
                                el.appendChild(avWrap);
                                el.appendChild(footer);
                            }
                        }
                    });
                    return el;
                }
            };
            pipPrimaryVideo.ownerDocument = simulatedOwnerDoc;
            pipPresenterCamVideo.ownerDocument = simulatedOwnerDoc;
            pipSelfVideo.ownerDocument = simulatedOwnerDoc;
            pipGridContainer.ownerDocument = simulatedOwnerDoc;
            pipFilmstrip.ownerDocument = simulatedOwnerDoc;

            const localStorageObj = {
                _map: new Map(),
                getItem(k) { return this._map.get(k) ?? null; },
                setItem(k, v) { this._map.set(k, String(v)); },
                removeItem(k) { this._map.delete(k); }
            };

            const userAgent = platform === 'ios'
                ? 'Mozilla/5.0 (iPhone; CPU iPhone OS 18_2 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Mobile/15E148'
                : platform === 'android'
                ? 'Mozilla/5.0 (Linux; Android 15; Pixel 9 Pro) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/134.0.0.0 Mobile Safari/537.36'
                : platform === 'desktop'
                ? 'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Spokes/1.0.0 Electron/41.0.0 Chrome/134.0.0.0 Safari/537.36'
                : 'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/134.0.0.0 Safari/537.36';

            const navigatorObj = {
                userAgent,
                mediaDevices: {
                    addEventListener() {},
                    removeEventListener() {},
                    async enumerateDevices() {
                        return hwTable.devices.map(d => ({ ...d }));
                    }
                }
            };

            // ================================================================
            // SfuParticipantSession & Client Room Implementation
            // ================================================================
            class RemoteParticipantView {
                constructor(sourceSession) {
                    this._sourceSession = sourceSession;
                    this.identity = sourceSession.identity;
                    this.videoTrackPublications = new Map();
                    this.audioTrackPublications = new Map();
                    this.trackPublications = new Map();
                    this.allPublications = new Map();
                }

                attachPublication(sourcePub) {
                    const sourceMst = sourcePub.track.mediaStreamTrack;
                    const s = sourceMst?.getSettings?.() || {};
                    const clonedTrack = createLivekitTrackFromMediaStreamTrack(
                        sourceMst,
                        sourcePub.source,
                        (tag) => createDomElement(tag)
                    );
                    clonedTrack.sid = sourcePub.trackSid;
                    const remotePub = {
                        trackSid: sourcePub.trackSid,
                        kind: sourcePub.kind,
                        source: sourcePub.source,
                        trackName: sourcePub.trackName || '',
                        isSubscribed: true,
                        track: clonedTrack,
                        videoTrack: sourcePub.kind === 'video' ? clonedTrack : undefined,
                        audioTrack: sourcePub.kind === 'audio' ? clonedTrack : undefined,
                        videoDimensions: s.width > 0 ? { width: s.width, height: s.height } : undefined,
                        dimensions: s.width > 0 ? { width: s.width, height: s.height } : undefined,
                        setSubscribed(sub) { this.isSubscribed = Boolean(sub); },
                        setEnabled() {},
                        setVideoQuality() {},
                        setVideoDimensions() {}
                    };
                    this.allPublications.set(remotePub.trackSid, remotePub);
                    this.trackPublications.set(remotePub.trackSid, remotePub);
                    if (remotePub.kind === 'video') {
                        this.videoTrackPublications.set(remotePub.trackSid, remotePub);
                    } else {
                        this.audioTrackPublications.set(remotePub.trackSid, remotePub);
                    }
                    return remotePub;
                }

                getPublication(trackSid) {
                    return this.allPublications.get(trackSid) || null;
                }

                removePublication(trackSid) {
                    const pub = this.allPublications.get(trackSid);
                    this.allPublications.delete(trackSid);
                    this.trackPublications.delete(trackSid);
                    this.videoTrackPublications.delete(trackSid);
                    this.audioTrackPublications.delete(trackSid);
                    return pub;
                }
            }

            class SfuParticipantSession {
                constructor(identity, roomInstance) {
                    this.identity = identity;
                    this.roomInstance = roomInstance;
                    this.publishedTracks = new Map();
                    this.remoteParticipants = new Map();
                }

                getOrCreateRemoteParticipantView(otherSession) {
                    if (!this.remoteParticipants.has(otherSession.identity)) {
                        this.remoteParticipants.set(otherSession.identity, new RemoteParticipantView(otherSession));
                    }
                    return this.remoteParticipants.get(otherSession.identity);
                }
            }

            let activeClientRoom = null;

            class ClientRoom {
                constructor(roomOpts = {}) {
                    this.options = roomOpts;
                    this.name = '';
                    this.handlers = new Map();
                    this.remoteParticipants = new Map();
                    this._sfuRoom = null;
                    this._session = null;
                    const roomSelf = this;

                    this.localParticipant = {
                        identity: userId,
                        videoTrackPublications: new Map(),
                        audioTrackPublications: new Map(),
                        trackPublications: new Map(),
                        async publishTrack(track, publishOpts = {}) {
                            track.source = publishOpts.source || track.source;
                            track.publishOptionsUsed = publishOpts;
                            const pub = {
                                trackSid: track.sid,
                                kind: track.kind,
                                source: track.source,
                                track,
                                videoTrack: track.kind === 'video' ? track : undefined,
                                audioTrack: track.kind === 'audio' ? track : undefined,
                                async mute() {
                                    if (track.mediaStreamTrack) track.mediaStreamTrack.enabled = false;
                                },
                                async unmute() {
                                    if (track.mediaStreamTrack) track.mediaStreamTrack.enabled = true;
                                }
                            };
                            this.trackPublications.set(pub.trackSid, pub);
                            if (track.kind === 'video') {
                                this.videoTrackPublications.set(pub.trackSid, pub);
                            } else {
                                this.audioTrackPublications.set(pub.trackSid, pub);
                            }
                            roomSelf.emit(RoomEvent.LocalTrackPublished, pub, this);
                            if (roomSelf._sfuRoom && roomSelf._session) {
                                track._onSenderParamsUpdated = () => {
                                    if (roomSelf._sfuRoom && roomSelf._session) {
                                        roomSelf._sfuRoom.notifyTrackDimensionChange(roomSelf._session, pub);
                                    }
                                };
                                roomSelf._sfuRoom.publishTrack(roomSelf._session, pub);
                            }
                            return pub;
                        },
                        async unpublishTrack(track, stopOnUnpublish = true) {
                            if (!track) return;
                            const map = track.kind === 'video' ? this.videoTrackPublications : this.audioTrackPublications;
                            const pub = map.get(track.sid) || {
                                trackSid: track.sid,
                                kind: track.kind,
                                source: track.source,
                                track: undefined
                            };
                            map.delete(track.sid);
                            this.trackPublications.delete(track.sid);
                            if (roomSelf._sfuRoom && roomSelf._session) {
                                roomSelf._sfuRoom.unpublishTrack(roomSelf._session, track.sid);
                            }
                            pub.track = undefined;
                            if (stopOnUnpublish) {
                                track.stop();
                            }
                            roomSelf.emit(RoomEvent.LocalTrackUnpublished, pub, this);
                        },
                        async setMicrophoneEnabled() {}
                    };

                    activeClientRoom = this;
                }

                on(evt, fn) {
                    this.handlers.set(evt, fn);
                    return this;
                }

                emit(evt, ...args) {
                    const fn = this.handlers.get(evt);
                    if (fn) fn(...args);
                }

                async connect(url, token) {
                    let parsed = { room: 'channel-default', identity: userId };
                    try {
                        if (token && token.startsWith('{')) {
                            parsed = JSON.parse(token);
                        }
                    } catch (e) {}

                    this.name = parsed.room || 'channel-default';
                    this.localParticipant.identity = parsed.identity || userId;
                    this._sfuRoom = getOrCreateSfuRoom(this.name);
                    this._session = new SfuParticipantSession(this.localParticipant.identity, this);
                    this.remoteParticipants = this._session.remoteParticipants;
                    this._sfuRoom.join(this._session);
                }

                async disconnect() {
                    if (this._sfuRoom && this._session) {
                        for (const p of Array.from(this.remoteParticipants.values())) {
                            for (const pub of Array.from(p.allPublications.values())) {
                                this.emit(RoomEvent.TrackUnsubscribed, pub.track, pub, p);
                            }
                        }
                        this._sfuRoom.leave(this._session.identity, this._session);
                        this._sfuRoom = null;
                        this._session = null;
                    }
                    this.emit(RoomEvent.Disconnected);
                }

                async switchActiveDevice(kind, deviceId) {
                    if (kind === 'videoinput') {
                        const camPub = Array.from(this.localParticipant.videoTrackPublications.values())
                            .find(p => p.source === Track.Source.Camera);
                        if (camPub && camPub.track) {
                            await camPub.track.restartTrack({ deviceId });
                            if (this._sfuRoom && this._session) {
                                this._sfuRoom.notifyTrackDimensionChange(this._session, camPub);
                            }
                        }
                    } else if (kind === 'audioinput') {
                        const micPub = Array.from(this.localParticipant.audioTrackPublications.values())
                            .find(p => p.source === Track.Source.Microphone);
                        if (micPub && micPub.track && micPub.track.mediaStreamTrack) {
                            micPub.track.mediaStreamTrack.stop();
                            const newMst = acquireHardwareTrack(hwTable, clientId, 'audio', { deviceId });
                            newMst._hwTable = hwTable;
                            createdMediaStreamTracks.push(newMst);
                            micPub.track.mediaStreamTrack = newMst;
                            micPub.track._mediaStreamTrack = newMst;
                        }
                    }
                }

                async getActiveDevice(kind) {
                    if (kind === 'videoinput') {
                        const camPub = Array.from(this.localParticipant.videoTrackPublications.values())
                            .find(p => p.source === Track.Source.Camera);
                        return camPub?.track?.mediaStreamTrack?.deviceId || '';
                    }
                    if (kind === 'audioinput') {
                        const micPub = Array.from(this.localParticipant.audioTrackPublications.values())
                            .find(p => p.source === Track.Source.Microphone);
                        return micPub?.track?.mediaStreamTrack?.deviceId || 'default';
                    }
                    return 'default';
                }

                static async getLocalDevices(kind) {
                    return hwTable.devices.filter(d => d.kind === kind).map(d => ({ ...d }));
                }
            }

            // ================================================================
            // Native iOS Companion Camera Simulator (SpokesIosPipController)
            // ================================================================
            let iosCompanionSession = null;
            let iosCompanionSfuRoom = null;
            let iosCompanionTrack = null;
            let iosCompanionPub = null;
            let iosCompanionConfig = { url: '', token: '', localUserId: userId };

            const spokesAudioSessionPlugin = {
                async configureVoiceChat({ enabled }) {
                    return {
                        category: 'AVAudioSessionCategoryPlayAndRecord',
                        mode: 'AVAudioSessionModeVoiceChat',
                        activeOutput: 'Receiver',
                        microphoneMode: 'voiceIsolation'
                    };
                },
                async getMicrophoneMode() {
                    return { activeMode: 'voiceIsolation' };
                },
                async promptVoiceIsolation() {
                    return { status: 'opened', activeMode: 'voiceIsolation' };
                },
                async addListener() {
                    return { remove: async () => {} };
                },
                async setAudioRoute({ route }) {
                    return {
                        category: 'AVAudioSessionCategoryPlayAndRecord',
                        mode: 'AVAudioSessionModeVoiceChat',
                        activeOutput: route === 'speaker' ? 'Speaker' : 'Receiver'
                    };
                },
                async getNativeCameras() {
                    return {
                        cameras: hwTable.devices.filter(d => d.kind === 'videoinput').map(d => ({ ...d }))
                    };
                },
                async configureCompanionSession(creds) {
                    iosCompanionConfig = { ...iosCompanionConfig, ...creds };
                },
                async startNativeCamera(payload = {}) {
                    const token = payload.token || iosCompanionConfig.token;
                    let parsed = { room: activeClientRoom?.name || 'channel-default', identity: `${userId}:cam` };
                    try {
                        if (token && token.startsWith('{')) parsed = JSON.parse(token);
                    } catch (e) {}

                    const videoDevs = hwTable.devices.filter(d => d.kind === 'videoinput');
                    const targetDev = videoDevs.find(d => d.deviceId === payload.deviceId)
                        || videoDevs.find(d => d.facingMode === (payload.facingMode || 'user'))
                        || videoDevs[0];
                    const maxCapW = targetDev?.maxWidth || 1920;
                    const maxCapH = targetDev?.maxHeight || 1080;

                    let resStr = String(payload.resolution || 'Auto').toLowerCase();
                    if ((resStr === '2160p' || resStr === '4k') && Math.max(maxCapW, maxCapH) < 3840) {
                        resStr = Math.max(maxCapW, maxCapH) >= 1920 ? '1080p' : 'auto';
                    } else if (resStr === '1080p' && Math.max(maxCapW, maxCapH) < 1920) {
                        resStr = 'auto';
                    }

                    const fps = payload.fps && payload.fps >= 60 ? 60 : 30;
                    const width = (resStr === '2160p' || resStr === '4k') ? 3840 : 1920;
                    const height = (resStr === '2160p' || resStr === '4k') ? 2160 : 1080;
                    const maxBitrate = (resStr === '2160p' || resStr === '4k')
                        ? (fps >= 60 ? 12000000 : 8000000)
                        : (resStr === '1080p' ? (fps >= 60 ? 4500000 : 3000000) : 3000000);
                    const degradationPreference = (resStr === 'auto' || !resStr) ? 'balanced' : 'maintain-resolution';

                    if (!iosCompanionSfuRoom || !iosCompanionSession) {
                        iosCompanionSfuRoom = getOrCreateSfuRoom(parsed.room);
                        iosCompanionSession = new SfuParticipantSession(parsed.identity, null);
                        iosCompanionSfuRoom.join(iosCompanionSession);
                    }

                    // In-place update if companion track already exists (matching SpokesIosPipController.swift!)
                    if (iosCompanionTrack && iosCompanionPub && iosCompanionTrack.mediaStreamTrack?.readyState === 'live') {
                        const mst = iosCompanionTrack.mediaStreamTrack;
                        mst.stop();
                        const updatedMst = acquireHardwareTrack(hwTable, clientId, 'video', {
                            deviceId: payload.deviceId,
                            facingMode: payload.facingMode || 'user',
                            width,
                            height,
                            frameRate: fps
                        });
                        updatedMst._hwTable = hwTable;
                        createdMediaStreamTracks.push(updatedMst);
                        iosCompanionTrack.mediaStreamTrack = updatedMst;
                        iosCompanionTrack._mediaStreamTrack = updatedMst;
                        iosCompanionTrack.dimensions = { width, height };
                        iosCompanionTrack.degradationPreference = degradationPreference;
                        iosCompanionTrack.publishOptionsUsed = {
                            videoCodec: 'h264',
                            simulcast: false,
                            degradationPreference,
                            videoEncoding: { maxBitrate, maxFramerate: fps }
                        };
                        await iosCompanionTrack.sender.setParameters({
                            encodings: [{ maxBitrate, maxFramerate: fps, scaleResolutionDownBy: 1.0 }]
                        });
                        iosCompanionSfuRoom.notifyTrackDimensionChange(iosCompanionSession, iosCompanionPub);
                        return {
                            success: true,
                            facingMode: updatedMst.facingMode,
                            deviceId: updatedMst.deviceId
                        };
                    }

                    const mst = acquireHardwareTrack(hwTable, clientId, 'video', {
                        deviceId: payload.deviceId,
                        facingMode: payload.facingMode || 'user',
                        width,
                        height,
                        frameRate: fps
                    });
                    mst._hwTable = hwTable;
                    createdMediaStreamTracks.push(mst);

                    iosCompanionTrack = createLivekitTrackFromMediaStreamTrack(mst, Track.Source.Camera, (tag) => createDomElement(tag));
                    iosCompanionTrack.degradationPreference = degradationPreference;
                    iosCompanionTrack.publishOptionsUsed = {
                        videoCodec: 'h264',
                        simulcast: false,
                        degradationPreference,
                        videoEncoding: { maxBitrate, maxFramerate: fps }
                    };
                    await iosCompanionTrack.sender.setParameters({
                        encodings: [{ maxBitrate, maxFramerate: fps, scaleResolutionDownBy: 1.0 }]
                    });

                    iosCompanionPub = {
                        trackSid: iosCompanionTrack.sid,
                        kind: 'video',
                        source: Track.Source.Camera,
                        track: iosCompanionTrack,
                        videoTrack: iosCompanionTrack
                    };
                    iosCompanionSfuRoom.publishTrack(iosCompanionSession, iosCompanionPub);

                    return {
                        success: true,
                        facingMode: mst.facingMode,
                        deviceId: mst.deviceId
                    };
                },
                async switchNativeCamera(payload = {}) {
                    return await this.startNativeCamera(payload);
                },
                async stopNativeCamera() {
                    if (iosCompanionSfuRoom && iosCompanionSession && iosCompanionPub) {
                        iosCompanionSfuRoom.unpublishTrack(iosCompanionSession, iosCompanionPub.trackSid);
                    }
                    if (iosCompanionTrack) {
                        iosCompanionTrack.stop();
                    }
                    if (iosCompanionSfuRoom && iosCompanionSession) {
                        iosCompanionSfuRoom.leave(iosCompanionSession.identity, iosCompanionSession);
                    }
                    iosCompanionPub = null;
                    iosCompanionTrack = null;
                    iosCompanionSession = null;
                    iosCompanionSfuRoom = null;
                }
            };

            const telecomConnectionPlugin = {
                _listeners: new Map(),
                addListener(evt, fn) {
                    this._listeners.set(evt, fn);
                    return { remove: async () => this._listeners.delete(evt) };
                },
                removeAllListeners() { this._listeners.clear(); },
                async startCall() {
                    const fn = this._listeners.get('onAudioStateChanged');
                    if (fn) fn({ state: { route: 'speaker', hasBluetooth: false } });
                },
                async endCall() {},
                async setAudioRoute() {},
                async getAvailableRoutes() { return { hasBluetooth: false }; },
                async getNativeCameras() {
                    return {
                        cameras: hwTable.devices.filter(d => d.kind === 'videoinput').map(d => ({ ...d }))
                    };
                }
            };

            const windowObj = {
                location: { origin: 'https://spokes.test' },
                innerWidth: 1280,
                innerHeight: 800,
                devicePixelRatio: 1,
                screen: { width: 2560, height: 1440, availWidth: 2560, availHeight: 1400 },
                isSpokesDesktop: platform === 'desktop',
                SpokesBridge: platform === 'desktop' ? { isDesktop: true, platform: 'desktop', setVoiceCallState() {} } : undefined,
                SpokesNative: (platform === 'ios' || platform === 'android')
                    ? {
                        setVoiceCallState() {},
                        updateCallMetadata() {},
                        enterPictureInPicture() {
                            if (typeof windowObj.spokesSetNativePipMode === 'function') {
                                windowObj.spokesSetNativePipMode(true);
                            }
                        }
                    }
                    : undefined,
                Capacitor: (platform === 'ios' || platform === 'android')
                    ? {
                        isNativePlatform: () => true,
                        getPlatform: () => platform,
                        registerPlugin: (name) => name === 'SpokesAudioSessionPlugin' ? spokesAudioSessionPlugin : null,
                        Plugins: {
                            KeepAwake: { async keepAwake() {}, async allowSleep() {} },
                            SpokesAudioSessionPlugin: platform === 'ios' ? spokesAudioSessionPlugin : undefined,
                            TelecomConnectionPlugin: platform === 'android' ? telecomConnectionPlugin : undefined
                        }
                    }
                    : { isNativePlatform: () => false, getPlatform: () => 'web', Plugins: {} },
                addEventListener() {},
                removeEventListener() {},
                dispatchEvent() {},
                getComputedStyle() {
                    return { getPropertyValue: () => '' };
                },
                AudioContext: class MockAudioContext {
                    constructor() {
                        this.state = 'running';
                        this.baseLatency = 0.005;
                        this.destination = {};
                    }
                    async resume() { this.state = 'running'; }
                    async setSinkId() {}
                    createGain() {
                        return { gain: { value: 1.0 }, connect() {}, disconnect() {} };
                    }
                    createMediaStreamSource() {
                        return { connect() {}, disconnect() {} };
                    }
                    createAnalyser() {
                        return {
                            fftSize: 256,
                            frequencyBinCount: 128,
                            smoothingTimeConstant: 0.2,
                            getByteFrequencyData(arr) { arr.fill(0); }
                        };
                    }
                },
                LivekitClient: {
                    Room: ClientRoom,
                    RoomEvent,
                    Track,
                    VideoQuality: { HIGH: 'high' },
                    VideoPresets: {
                        h360: { width: 640, height: 360, maxBitrate: 400000 },
                        h720: { width: 1280, height: 720, maxBitrate: 1500000 },
                        h1080: { width: 1920, height: 1080, maxBitrate: 3000000 },
                        h2160: { width: 3840, height: 2160, maxBitrate: 8000000 }
                    },
                    VideoPreset: class VideoPreset {
                        constructor(width, height, maxBitrate, maxFramerate) {
                            this.width = width;
                            this.height = height;
                            this.maxBitrate = maxBitrate;
                            this.maxFramerate = maxFramerate;
                        }
                    },
                    async createLocalAudioTrack(opts = {}) {
                        const mst = acquireHardwareTrack(hwTable, clientId, 'audio', opts);
                        mst._hwTable = hwTable;
                        createdMediaStreamTracks.push(mst);
                        return createLivekitTrackFromMediaStreamTrack(mst, Track.Source.Microphone, (tag) => createDomElement(tag));
                    },
                    async createLocalVideoTrack(opts = {}) {
                        const res = opts.resolution || {};
                        const mst = acquireHardwareTrack(hwTable, clientId, 'video', {
                            deviceId: opts.deviceId,
                            facingMode: opts.facingMode || 'user',
                            width: res.width || 1920,
                            height: res.height || 1080,
                            frameRate: res.maxFramerate || 30
                        });
                        mst._hwTable = hwTable;
                        createdMediaStreamTracks.push(mst);
                        return createLivekitTrackFromMediaStreamTrack(mst, Track.Source.Camera, (tag) => createDomElement(tag));
                    },
                    async createLocalScreenTracks(opts = {}) {
                        const res = opts.resolution || {};
                        const videoMst = acquireHardwareTrack(hwTable, clientId, 'video-screen', {
                            width: res.width || 2560,
                            height: res.height || 1440,
                            frameRate: res.frameRate || 60
                        });
                        videoMst._hwTable = hwTable;
                        createdMediaStreamTracks.push(videoMst);
                        const videoTrack = createLivekitTrackFromMediaStreamTrack(videoMst, Track.Source.ScreenShare, (tag) => createDomElement(tag));

                        const audioMst = acquireHardwareTrack(hwTable, clientId, 'audio-screen', {});
                        audioMst._hwTable = hwTable;
                        createdMediaStreamTracks.push(audioMst);
                        const audioTrack = createLivekitTrackFromMediaStreamTrack(audioMst, Track.Source.ScreenShareAudio, (tag) => createDomElement(tag));

                        return [videoTrack, audioTrack];
                    }
                }
            };

            windowObj.window = windowObj;
            windowObj.document = documentObj;
            windowObj.navigator = navigatorObj;
            windowObj.localStorage = localStorageObj;
            windowObj.MediaStream = SimulatedMediaStream;
            windowObj.MutationObserver = SimulatedMutationObserver;
            windowObj.CustomEvent = class CustomEvent {
                constructor(type, opts) { this.type = type; this.detail = opts?.detail; }
            };
            windowObj.Audio = class MockAudio {
                constructor(src) { this.src = src; this.volume = 1.0; }
                async setSinkId() {}
                async play() {}
            };
            windowObj.RTCRtpSender = {};
            windowObj.requestAnimationFrame = (cb) => setTimeout(cb, 0);
            windowObj.cancelAnimationFrame = (id) => clearTimeout(id);

            return {
                clientId,
                userId,
                platform,
                hwTable,
                domById,
                createdMediaStreamTracks,
                mountCallStage,
                ensureParticipantSlotMounted,
                unmountCallStage,
                getActiveClientRoom: () => activeClientRoom,
                getIosCompanionState: () => ({
                    connected: Boolean(iosCompanionSession && iosCompanionSfuRoom),
                    track: iosCompanionTrack
                }),
                window: windowObj,
                document: documentObj,
                navigator: navigatorObj,
                localStorage: localStorageObj,
                MediaStream: SimulatedMediaStream,
                MutationObserver: SimulatedMutationObserver,
                CustomEvent: windowObj.CustomEvent,
                Audio: windowObj.Audio,
                RTCRtpSender: windowObj.RTCRtpSender,
                requestAnimationFrame: windowObj.requestAnimationFrame,
                cancelAnimationFrame: windowObj.cancelAnimationFrame,
                dotnetHelper: {
                    async invokeMethodAsync(method, ...args) {
                        return await invokeDotnetOnClient(clientId, method, args);
                    }
                }
            };
        }

        function buildEncodingSnapshot(track) {
            if (!track) return null;
            const mst = track.mediaStreamTrack;
            const params = track.sender?.getParameters?.() || { encodings: [] };
            const topEnc = params.encodings?.[params.encodings.length - 1] || {};
            const pubOpts = track.publishOptionsUsed || {};
            const scale = (topEnc.scaleResolutionDownBy && topEnc.scaleResolutionDownBy > 0) ? topEnc.scaleResolutionDownBy : 1.0;
            return {
                MaxBitrate: topEnc.maxBitrate || pubOpts.videoEncoding?.maxBitrate || 0,
                MaxFramerate: topEnc.maxFramerate || pubOpts.videoEncoding?.maxFramerate || 30,
                ScaleResolutionDownBy: scale,
                DegradationPreference: track.degradationPreference || pubOpts.degradationPreference || 'balanced',
                VideoCodec: pubOpts.videoCodec || 'h264',
                Simulcast: Boolean(pubOpts.simulcast),
                ContentHint: mst?.contentHint || '',
                Width: mst?.width ? Math.round(mst.width / scale) : 0,
                Height: mst?.height ? Math.round(mst.height / scale) : 0
            };
        }

        // ====================================================================
        // Command Dispatcher
        // ====================================================================
        async function handleCommand(action, payload) {
            switch (action) {
                case 'ping':
                    return { ok: true };

                case 'flush':
                    await new Promise(r => setTimeout(r, 25));
                    return { ok: true };

                case 'initClient': {
                    const { clientId, userId, platform, interopFile, sharedHardwareClientId } = payload;
                    const env = createClientEnvironment(clientId, userId, platform, sharedHardwareClientId);
                    globalThis.__spokesClientEnvs[clientId] = env;
                    const mod = await import(interopFile);
                    clientModules.set(clientId, mod);
                    return { ok: true };
                }

                case 'invokeModule': {
                    const { clientId, identifier, args } = payload;
                    const env = globalThis.__spokesClientEnvs[clientId];
                    const mod = clientModules.get(clientId);
                    if (!env || !mod) throw new Error(`Client '${clientId}' not initialized`);

                    const fn = mod[identifier];
                    if (typeof fn !== 'function') {
                        throw new Error(`Function '${identifier}' not exported on voiceInterop.js`);
                    }

                    const resolvedArgs = (args || []).map(a =>
                        (a && typeof a === 'object' && a.__isDotNetRef) ? env.dotnetHelper : a
                    );
                    const res = await fn(...resolvedArgs);
                    return res === undefined ? null : res;
                }

                case 'localStorageGet': {
                    const { clientId, key } = payload;
                    const env = globalThis.__spokesClientEnvs[clientId];
                    return env ? env.localStorage.getItem(key) : null;
                }

                case 'localStorageSet': {
                    const { clientId, key, value } = payload;
                    const env = globalThis.__spokesClientEnvs[clientId];
                    if (env) env.localStorage.setItem(key, value);
                    return null;
                }

                case 'syncStageDom': {
                    const { clientId, stageVisible, participantIds } = payload;
                    const env = globalThis.__spokesClientEnvs[clientId];
                    if (!env) return null;
                    if (stageVisible) {
                        env.mountCallStage(participantIds || []);
                    } else {
                        env.unmountCallStage();
                    }
                    return { ok: true };
                }

                case 'triggerNativeOsStopScreenShare': {
                    const { clientId } = payload;
                    const env = globalThis.__spokesClientEnvs[clientId];
                    const room = env?.getActiveClientRoom();
                    if (!room) throw new Error(`No active room on client '${clientId}'`);
                    const screenPub = Array.from(room.localParticipant.videoTrackPublications.values())
                        .find(p => p.source === Track.Source.ScreenShare);
                    if (!screenPub || !screenPub.track?.mediaStreamTrack) {
                        throw new Error(`No active ScreenShare track on client '${clientId}'`);
                    }
                    screenPub.track.mediaStreamTrack.dispatchEvent('ended');
                    await new Promise(r => setTimeout(r, 30));
                    return { ok: true };
                }

                case 'simulateUnexpectedSfuDisconnect': {
                    const { clientId } = payload;
                    const env = globalThis.__spokesClientEnvs[clientId];
                    const room = env?.getActiveClientRoom();
                    if (room) {
                        await room.disconnect();
                    }
                    return { ok: true };
                }

                case 'evictParticipantFromRoom': {
                    const { roomName, userId } = payload;
                    const sfuRoom = sfuRooms.get(roomName);
                    if (sfuRoom) {
                        sfuRoom.leave(userId);
                        sfuRoom.leave(`${userId}:cam`);
                    }
                    return { ok: true };
                }

                case 'simulateVideoElementResize': {
                    const { clientId, participantId, source, width, height } = payload;
                    const env = globalThis.__spokesClientEnvs[clientId];
                    if (!env) return { ok: false };
                    const domId = source === 'screenshare'
                        ? `participant-screen-${participantId}`
                        : `participant-video-${participantId}`;
                    const videoEl = env.domById.get(domId);
                    if (videoEl) {
                        videoEl.videoWidth = width;
                        videoEl.videoHeight = height;
                        videoEl.dispatchEvent('loadedmetadata');
                        videoEl.dispatchEvent('resize');
                    }
                    return { ok: true };
                }

                case 'clickPipParticipantTile': {
                    const { clientId, participantId, inFilmstrip } = payload;
                    const env = globalThis.__spokesClientEnvs[clientId];
                    if (!env) return { ok: false };
                    const container = env.domById.get(inFilmstrip ? 'spokes-pip-filmstrip' : 'spokes-pip-grid-container');
                    const tile = (container?.children || []).find(c => c.dataset?.participantId === participantId);
                    if (tile) {
                        tile.dispatchEvent({ type: 'click', stopPropagation() {} });
                        return { ok: true };
                    }
                    return { ok: false };
                }

                case 'inspectClientState': {
                    const { clientId, participantIds } = payload;
                    const env = globalThis.__spokesClientEnvs[clientId];
                    if (!env) throw new Error(`Unknown client '${clientId}'`);

                    const tiles = {};
                    for (const pid of (participantIds || [])) {
                        const camEl = env.domById.get(`participant-video-${pid}`);
                        const screenEl = env.domById.get(`participant-screen-${pid}`);
                        const rectEl = env.domById.get(`participant-rect-${pid}`);

                        const camTrack = camEl?.srcObject?.getVideoTracks?.()?.[0] || null;
                        const screenTrack = screenEl?.srcObject?.getVideoTracks?.()?.[0] || null;

                        const hasCam = Boolean(camEl && camEl.classList.contains('has-stream') && camEl.style.opacity === '1' && camTrack && camTrack.readyState === 'live');
                        const hasScreen = Boolean(screenEl && screenEl.classList.contains('has-stream') && screenEl.style.opacity === '1' && screenTrack && screenTrack.readyState === 'live');

                        tiles[pid] = {
                            ParticipantId: pid,
                            IsLargeAvatarVisible: !hasCam && !hasScreen,
                            IsSmallOverlayVisible: hasCam || hasScreen,
                            HasCameraStream: hasCam,
                            CameraVideoPaused: camEl ? Boolean(camEl.paused) : true,
                            CameraTrackReadyState: camTrack?.readyState || 'none',
                            HasScreenShareStream: hasScreen,
                            ScreenVideoPaused: screenEl ? Boolean(screenEl.paused) : true,
                            ScreenTrackReadyState: screenTrack?.readyState || 'none',
                            IsCameraInCornerPipMode: hasScreen && hasCam,
                            IsCameraFullTileMode: hasCam && !hasScreen,
                            IsCameraMirrored: Boolean(camEl && camEl.style.transform.includes('scaleX(-1)')),
                            CamAspectCssVar: camEl?.style.getPropertyValue('--cam-aspect') || '',
                            TileVideoAspectCssVar: rectEl?.style.getPropertyValue('--tile-video-aspect') || ''
                        };
                    }

                    const pipOverlay = env.domById.get('spokes-voice-pip-overlay');
                    const pipPrimary = env.domById.get('spokes-pip-primary-video');
                    const pipPresenterCam = env.domById.get('spokes-pip-presenter-cam-video');
                    const pipSelf = env.domById.get('spokes-pip-self-video');
                    const pipGrid = env.domById.get('spokes-pip-grid-container');
                    const pipFilmstrip = env.domById.get('spokes-pip-filmstrip');
                    const pipPrimaryTrack = pipPrimary?.srcObject?.getVideoTracks?.()?.[0] || null;
                    const pipPresenterCamTrack = pipPresenterCam?.srcObject?.getVideoTracks?.()?.[0] || null;
                    const pipSelfTrack = pipSelf?.srcObject?.getVideoTracks?.()?.[0] || null;

                    const resolveOwnerUserId = (mst) => {
                        if (!mst) return null;
                        for (const e of Object.values(globalThis.__spokesClientEnvs)) {
                            if (e.clientId === mst.ownerClientId) return e.userId;
                        }
                        return mst.ownerClientId || null;
                    };

                    const resolveTrackSource = (mst) => {
                        if (!mst) return null;
                        return String(mst.deviceId || '').startsWith('screen-display-') ? 'screenshare' : 'camera';
                    };

                    const gridIds = (pipGrid?.children || [])
                        .map(c => c.dataset?.participantId)
                        .filter(Boolean);
                    const filmstripIds = (pipFilmstrip?.children || [])
                        .map(c => c.dataset?.participantId)
                        .filter(Boolean);

                    const inAppPip = {
                        IsOverlayVisible: Boolean(pipOverlay && !pipOverlay.classList.contains('is-stage-hidden')),
                        Mode: pipOverlay?.classList.contains('mode-grid')
                            ? 'grid'
                            : (pipOverlay?.classList.contains('mode-video') ? 'video' : 'avatar'),
                        LayoutMode: pipOverlay?.classList.contains('mode-grid') ? 'grid' : 'focus',
                        PrimaryVideoHasStream: Boolean(pipPrimary && pipPrimary.classList.contains('has-stream') && pipPrimaryTrack && pipPrimaryTrack.readyState === 'live'),
                        PrimaryVideoPaused: pipPrimary ? Boolean(pipPrimary.paused) : true,
                        PrimaryVideoTrackReadyState: pipPrimaryTrack?.readyState || 'none',
                        PrimaryVideoWidth: pipPrimaryTrack?.width || pipPrimary?.videoWidth || 0,
                        PrimaryVideoHeight: pipPrimaryTrack?.height || pipPrimary?.videoHeight || 0,
                        PrimaryTrackParticipantId: resolveOwnerUserId(pipPrimaryTrack),
                        PrimaryTrackSource: resolveTrackSource(pipPrimaryTrack),
                        PresenterCamVideoHasStream: Boolean(pipPresenterCam && pipPresenterCam.classList.contains('has-stream') && pipPresenterCamTrack && pipPresenterCamTrack.readyState === 'live'),
                        PresenterCamParticipantId: resolveOwnerUserId(pipPresenterCamTrack),
                        SelfVideoHasStream: Boolean(pipSelf && pipSelf.classList.contains('has-stream') && pipSelfTrack && pipSelfTrack.readyState === 'live'),
                        SelfVideoPaused: pipSelf ? Boolean(pipSelf.paused) : true,
                        SelfVideoTrackReadyState: pipSelfTrack?.readyState || 'none',
                        SelfVideoMirrored: Boolean(pipSelf && pipSelf.classList.contains('spokes-pip-mirror')),
                        SelfAspectCssVar: pipSelf?.style.getPropertyValue('--self-aspect') || '',
                        GridTileParticipantIds: gridIds,
                        FilmstripTileParticipantIds: filmstripIds
                    };

                    const room = env.getActiveClientRoom();
                    const iosComp = env.getIosCompanionState();
                    const camPub = room ? Array.from(room.localParticipant.videoTrackPublications.values()).find(p => p.source === Track.Source.Camera) : null;
                    const screenPub = room ? Array.from(room.localParticipant.videoTrackPublications.values()).find(p => p.source === Track.Source.ScreenShare) : null;

                    const liveLocalTracks = env.createdMediaStreamTracks.filter(t => t.readyState === 'live');
                    const lockedDevices = [];
                    for (const [devId, lockSet] of env.hwTable.activeLocks.entries()) {
                        if (Array.from(lockSet).some(t => t.readyState === 'live')) {
                            lockedDevices.push(devId);
                        }
                    }

                    const hardware = {
                        LiveLocalTrackCount: liveLocalTracks.length,
                        LockedDeviceIds: lockedDevices,
                        NativeIosCompanionConnected: iosComp.connected,
                        CameraEncoding: buildEncodingSnapshot(camPub?.track || iosComp.track),
                        ScreenEncoding: buildEncodingSnapshot(screenPub?.track)
                    };

                    return { tiles, inAppPip, hardware };
                }

                default:
                    throw new Error(`Unknown action: ${action}`);
            }
        }

        const rl = readline.createInterface({ input: process.stdin, terminal: false });
        rl.on('line', async (line) => {
            if (!line || !line.startsWith('{')) return;
            let msg;
            try {
                msg = JSON.parse(line);
            } catch (e) {
                return;
            }

            if (msg.type === 'req') {
                try {
                    const result = await handleCommand(msg.action, msg.payload || {});
                    sendStdout({ type: 'res', id: msg.id, result });
                } catch (err) {
                    sendStdout({ type: 'res', id: msg.id, error: `${err?.name || 'Error'}: ${err?.message || String(err)}` });
                }
            } else if (msg.type === 'dotnet_res') {
                const pending = pendingDotnetCalls.get(msg.callId);
                if (pending) {
                    pendingDotnetCalls.delete(msg.callId);
                    if (msg.error) pending.reject(new Error(msg.error));
                    else pending.resolve(msg.result);
                }
            }
        });
        """;
}

public sealed class SimulatedClusterVoiceChannelService : VoiceChannelService
{
    private readonly VoiceCallClusterSimulator _cluster;
    private readonly ChatStateService _chatState;

    public SimulatedClusterVoiceChannelService(VoiceCallClusterSimulator cluster, ChatStateService chatState)
    {
        _cluster = cluster;
        _chatState = chatState;
    }

    public override string GenerateJoinToken(string channelId, string userId, string userName, string userAvatarUrl = "")
    {
        return JsonSerializer.Serialize(new
        {
            room = $"channel-{channelId}",
            identity = userId,
            name = userName
        });
    }

    public override string GenerateCameraCompanionToken(string channelId, string userId, string userName = "", string userAvatarUrl = "")
    {
        return JsonSerializer.Serialize(new
        {
            room = $"channel-{channelId}",
            identity = $"{userId}:cam",
            name = string.IsNullOrWhiteSpace(userName) ? $"{userId} (Camera)" : $"{userName} (Camera)"
        });
    }

    public override Task JoinVoiceAsync(string channelId, string userId, CancellationToken cancellationToken = default)
    {
        _chatState.NotifyVoiceMemberJoined(channelId, userId);
        return Task.CompletedTask;
    }

    public override async Task LeaveVoiceAsync(string channelId, string userId, bool evictFromLiveKit, CancellationToken cancellationToken = default)
    {
        _chatState.NotifyVoiceMemberLeft(channelId, userId);
        if (evictFromLiveKit)
        {
            await _cluster.SendNodeCommandAsync("evictParticipantFromRoom", new
            {
                roomName = $"channel-{channelId}",
                userId
            });
        }
    }

    public override Task SetUserSpeakingAsync(string channelId, string userId, bool isSpeaking, CancellationToken cancellationToken = default)
    {
        _chatState.SetUserSpeaking(channelId, userId, isSpeaking);
        return Task.CompletedTask;
    }

    public override Task SetUserMutedAsync(string channelId, string userId, bool isMuted, CancellationToken cancellationToken = default)
    {
        _chatState.SetUserMuted(channelId, userId, isMuted);
        return Task.CompletedTask;
    }
}

public sealed class SimulatedVoiceClient : IAsyncDisposable
{
    private readonly VoiceCallClusterSimulator _cluster;
    private readonly SimulatedClientJsRuntime _jsRuntime;

    // Stage layout evaluator state (mirrors Chat.razor's HandleVoiceStateChanged & VoicePipOverlay.razor)
    private int _prevCameraCount;
    private bool _prevIsFaceTimeGlobal;
    private bool _wasShowingInAppPip;

    public string ClientId { get; }
    public string UserId { get; }
    public VoiceClientPlatform Platform { get; }
    public VoiceJsInteropService JsInterop { get; }
    public VoiceDeviceService DeviceService { get; }
    public GlobalVoiceService VoiceService { get; }
    public VoiceUiStateService UiState { get; }
    public List<(string Message, Severity Severity)> RecordedSnackbars { get; } = new();

    internal SimulatedVoiceClient(
        VoiceCallClusterSimulator cluster,
        string clientId,
        string userId,
        VoiceClientPlatform platform)
    {
        _cluster = cluster;
        ClientId = clientId;
        UserId = userId;
        Platform = platform;

        _jsRuntime = new SimulatedClientJsRuntime(cluster, clientId);
        JsInterop = new VoiceJsInteropService(_jsRuntime, NullLogger<VoiceJsInteropService>.Instance);
        DeviceService = new VoiceDeviceService(JsInterop, NullLogger<VoiceDeviceService>.Instance);
        VoiceService = new GlobalVoiceService(
            JsInterop,
            cluster.VoiceChannelService,
            cluster.ChatState,
            NullLogger<GlobalVoiceService>.Instance,
            DeviceService);

        var snackbarMock = new Mock<ISnackbar>();
        snackbarMock.Setup(x => x.Add(It.IsAny<string>(), It.IsAny<Severity>(), It.IsAny<Action<SnackbarOptions>>(), It.IsAny<string>()))
            .Callback<string, Severity, Action<SnackbarOptions>, string>((msg, sev, _, _) => RecordedSnackbars.Add((msg, sev)));

        UiState = new VoiceUiStateService(
            snackbarMock.Object,
            Mock.Of<ISoundService>(),
            _jsRuntime,
            VoiceService,
            JsInterop,
            DeviceService,
            NullLogger<VoiceUiStateService>.Instance);

        UiState.SetCallStageVisible(true);
        VoiceService.OnChange += EvaluateChatRazorVoiceStateChanged;
    }

    public async Task ConnectToChannelAsync(string channelId, int audioBitrateKbps = 64, string noiseSuppression = "webrtc")
    {
        UiState.SetCallStageVisible(true);
        var token = _cluster.VoiceChannelService.GenerateJoinToken(channelId, UserId, UserId);
        await VoiceService.ConnectAsync(new VoiceConnectionOptions(
            token,
            "wss://lk.spokes.test",
            channelId,
            UserId,
            audioBitrateKbps,
            noiseSuppression));
        await SyncStageAndPipDomAsync();
    }

    public async Task NavigateAwayFromCallStageAsync()
    {
        UiState.SetCallStageVisible(false);
        await SyncStageAndPipDomAsync();
    }

    public async Task ReturnToCallStageAsync()
    {
        UiState.SetCallStageVisible(true);
        await SyncStageAndPipDomAsync();
        await DeviceService.ReattachTracksAsync();
    }

    public async Task TriggerNativeOsStopScreenShareBannerAsync()
    {
        await _cluster.SendNodeCommandAsync("triggerNativeOsStopScreenShare", new { clientId = ClientId });
    }

    public async Task SimulateUnexpectedSfuDisconnectAsync()
    {
        await _cluster.SendNodeCommandAsync("simulateUnexpectedSfuDisconnect", new { clientId = ClientId });
    }

    public async Task SimulateVideoElementResizeAsync(string participantId, string source, int width, int height)
    {
        await _cluster.SendNodeCommandAsync("simulateVideoElementResize", new
        {
            clientId = ClientId,
            participantId,
            source,
            width,
            height
        });
    }

    public async Task ClickPipParticipantTileAsync(string participantId, bool inFilmstrip = true)
    {
        await _cluster.SendNodeCommandAsync("clickPipParticipantTile", new
        {
            clientId = ClientId,
            participantId,
            inFilmstrip
        });
        await SyncStageAndPipDomAsync();
    }

    /// <summary>
    /// Executes the exact stage layout refresh logic from <c>Chat.razor.HandleVoiceStateChanged</c>.
    /// Focus state itself is now owned centrally by <see cref="VoiceUiStateService"/>.
    /// </summary>
    private void EvaluateChatRazorVoiceStateChanged()
    {
        if (!VoiceService.IsInVoiceCall)
        {
            _prevCameraCount = 0;
            _prevIsFaceTimeGlobal = false;
            return;
        }

        int activeCameras = VoiceService.CallParticipants.Count(p =>
            (p == VoiceService.CurrentUserId && (VoiceService.IsVideoOn || VoiceService.IsScreenSharing)) ||
            VoiceService.ActiveCameraParticipants.Contains(p) ||
            VoiceService.ActiveScreenShareParticipants.Contains(p));

        bool isFaceTimeLayoutGlobal = VoiceService.CallParticipants.Count == 2 && activeCameras > 0 && UiState.FocusedParticipantId != null;
        bool needsRefresh = activeCameras != _prevCameraCount || isFaceTimeLayoutGlobal != _prevIsFaceTimeGlobal;

        _prevCameraCount = activeCameras;
        _prevIsFaceTimeGlobal = isFaceTimeLayoutGlobal;

        if (needsRefresh && VoiceService.IsInVoiceCall && UiState.IsCallStageVisible)
        {
            _ = DeviceService.RefreshVideoLayoutsAsync();
        }
    }

    internal async Task SyncStageAndPipDomAsync()
    {
        var participants = VoiceService.CallParticipants.ToArray();
        await _cluster.SendNodeCommandAsync("syncStageDom", new
        {
            clientId = ClientId,
            stageVisible = VoiceService.IsInVoiceCall && UiState.IsCallStageVisible,
            participantIds = participants
        });

        if (VoiceService.IsInVoiceCall)
        {
            var dtos = participants
                .Where(pid => !string.IsNullOrEmpty(pid) && !pid.StartsWith("bot:", StringComparison.OrdinalIgnoreCase))
                .Select(pid => new VoicePipParticipantDto
                {
                    Id = pid,
                    Name = pid,
                    Initials = pid[..Math.Min(2, pid.Length)].ToUpperInvariant(),
                    IsMuted = pid == VoiceService.CurrentUserId
                        ? VoiceService.IsMuted
                        : VoiceService.MutedParticipants.Contains(pid)
                })
                .ToList();

            await UiState.SyncPipMetadataAsync(new VoicePipMetadataPayload
            {
                Active = true,
                ChannelId = VoiceService.CurrentChannelId ?? "",
                ChannelName = "General Voice",
                ServerName = "Spokes Test",
                LocalUserId = VoiceService.CurrentUserId ?? "",
                IsMuted = VoiceService.IsMuted,
                IsVideoOn = VoiceService.IsVideoOn,
                IsScreenSharing = VoiceService.IsScreenSharing,
                FocusedParticipantId = UiState.FocusedParticipantId ?? "",
                Participants = dtos
            });
        }

        bool inAppFloatingActive = VoiceService.IsInVoiceCall && !UiState.IsCallStageVisible && !UiState.IsOsPipActive;
        if (inAppFloatingActive != _wasShowingInAppPip)
        {
            _wasShowingInAppPip = inAppFloatingActive;
            await UiState.SetInAppPipActiveAsync(inAppFloatingActive);
        }
        else if (inAppFloatingActive)
        {
            await JsInterop.RefreshInAppPipAsync();
        }
    }

    public async Task<ClientVisualSnapshot> GetStageSnapshotAsync()
    {
        await SyncStageAndPipDomAsync();
        var participantIds = VoiceService.CallParticipants.ToArray();
        var raw = await _cluster.SendNodeCommandAsync("inspectClientState", new
        {
            clientId = ClientId,
            participantIds
        });

        var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        var tiles = JsonSerializer.Deserialize<Dictionary<string, ParticipantTileVisualState>>(
            raw.GetProperty("tiles").GetRawText(), opts) ?? new();
        var pip = JsonSerializer.Deserialize<InAppPipVisualState>(
            raw.GetProperty("inAppPip").GetRawText(), opts)!;
        var hw = JsonSerializer.Deserialize<ClientHardwareSnapshot>(
            raw.GetProperty("hardware").GetRawText(), opts)!;

        var focusedId = UiState.FocusedParticipantId;
        StageLayoutMode mode;
        if (!UiState.IsCallStageVisible)
        {
            mode = StageLayoutMode.StageUnmountedPipActive;
        }
        else if (VoiceService.CallParticipants.Count == 2 && focusedId != null)
        {
            mode = StageLayoutMode.FaceTimeTwoPerson;
        }
        else if (focusedId != null)
        {
            mode = StageLayoutMode.FocusedParticipant;
        }
        else
        {
            mode = StageLayoutMode.Grid;
        }

        return new ClientVisualSnapshot(
            ClientId,
            UserId,
            mode,
            focusedId,
            tiles,
            pip,
            hw);
    }

    internal async Task<object?> InvokeDotNetCallbackAsync(string method, JsonElement args)
    {
        switch (method)
        {
            case "OnRoomConnected":
            {
                var ids = args.GetArrayLength() > 0
                    ? JsonSerializer.Deserialize<string[]>(args[0].GetRawText()) ?? []
                    : [];
                VoiceService.OnRoomConnected(ids);
                await SyncStageAndPipDomAsync();
                return null;
            }
            case "OnParticipantConnected":
            {
                var id = args[0].GetString() ?? "";
                VoiceService.OnParticipantConnected(id);
                await SyncStageAndPipDomAsync();
                return null;
            }
            case "EnsureParticipantSlot":
            {
                var id = args[0].GetString() ?? "";
                VoiceService.EnsureParticipantSlot(id);
                await SyncStageAndPipDomAsync();
                return null;
            }
            case "OnParticipantDisconnected":
            {
                var id = args[0].GetString() ?? "";
                VoiceService.OnParticipantDisconnected(id);
                return null;
            }
            case "OnVideoTrackStateChanged":
            {
                var pid = args[0].GetString() ?? "";
                var source = args[1].GetString() ?? "camera";
                var active = args[2].GetBoolean();
                VoiceService.OnVideoTrackStateChanged(pid, source, active);
                return null;
            }
            case "OnParticipantVideoDimensions":
            {
                var pid = args[0].GetString() ?? "";
                var height = args[1].GetInt32();
                var fps = args[2].GetInt32();
                VoiceService.OnParticipantVideoDimensions(pid, height, fps);
                return null;
            }
            case "OnLocalVideoDimensions":
            {
                var maxEdge = args[0].GetInt32();
                var fps = args[1].GetInt32();
                VoiceService.OnLocalVideoDimensions(maxEdge, fps);
                return null;
            }
            case "OnLocalScreenDimensions":
            {
                var maxEdge = args[0].GetInt32();
                var fps = args[1].GetInt32();
                VoiceService.OnLocalScreenDimensions(maxEdge, fps);
                return null;
            }
            case "RequestCameraCompanionToken":
            {
                return VoiceService.RequestCameraCompanionToken();
            }
            case "OnAudioDevicesUpdated":
            {
                var jsonOpts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                var mics = JsonSerializer.Deserialize<List<AudioDevice>>(args[0].GetRawText(), jsonOpts) ?? [];
                var activeMic = args[1].GetString() ?? "";
                var speakers = JsonSerializer.Deserialize<List<AudioDevice>>(args[2].GetRawText(), jsonOpts) ?? [];
                var activeSpeaker = args[3].GetString() ?? "";
                VoiceService.OnAudioDevicesUpdated(mics, activeMic, speakers, activeSpeaker);
                return null;
            }
            case "OnLocalAudioPipelineTelemetry":
            {
                var filter = args[0].GetString() ?? "webrtc";
                var latency = args[1].GetDouble();
                var platform = args[2].GetString() ?? "web";
                var delay = args.GetArrayLength() > 3 ? args[3].GetDouble() : 0.0;
                VoiceService.OnLocalAudioPipelineTelemetry(filter, latency, platform, delay);
                return null;
            }
            case "OnScreenShareAudioActive":
            {
                VoiceService.OnScreenShareAudioActive(args[0].GetBoolean());
                return null;
            }
            case "OnRemoteScreenAudioChanged":
            {
                VoiceService.OnRemoteScreenAudioChanged(args[0].GetString() ?? "", args[1].GetBoolean());
                return null;
            }
            case "OnActiveSpeakersChanged":
            {
                var speakers = JsonSerializer.Deserialize<string[]>(args[0].GetRawText()) ?? [];
                VoiceService.OnActiveSpeakersChanged(speakers);
                return null;
            }
            case "OnLocalMuted":
            {
                VoiceService.OnLocalMuted(args[0].GetBoolean());
                return null;
            }
            case "OnLocalHardwareMuteChanged":
            {
                VoiceService.OnLocalHardwareMuteChanged(args[0].GetBoolean());
                return null;
            }
            case "OnParticipantMuteChanged":
            {
                VoiceService.OnParticipantMuteChanged(args[0].GetString() ?? "", args[1].GetBoolean());
                return null;
            }
            case "OnRoomDisconnected":
            {
                await VoiceService.OnRoomDisconnected();
                return null;
            }
            case "OnVoiceLog":
            {
                VoiceService.OnVoiceLog(args[0].GetString() ?? "", args[1].GetString() ?? "info");
                return null;
            }
            case "OnOsPipStateChanged":
            {
                VoiceService.OnOsPipStateChanged(args[0].GetBoolean());
                return null;
            }
            case "OnPipFocusedParticipantChanged":
            {
                var pid = args.GetArrayLength() > 0 && args[0].ValueKind == JsonValueKind.String
                    ? args[0].GetString()
                    : null;
                VoiceService.OnPipFocusedParticipantChanged(pid);
                return null;
            }
            case "OnNativeMobilePipChanged":
            {
                VoiceService.OnNativeMobilePipChanged(args[0].GetBoolean());
                return null;
            }
            case "OnNativeCallAction":
            {
                await VoiceService.OnNativeCallAction(args[0].GetString() ?? "");
                return null;
            }
            case "RequestExternalDisconnect":
            {
                await VoiceService.RequestExternalDisconnect();
                return null;
            }
            default:
                return null;
        }
    }

    public async ValueTask DisposeAsync()
    {
        VoiceService.OnChange -= EvaluateChatRazorVoiceStateChanged;
        UiState.Dispose();
        await VoiceService.DisposeAsync();
    }

    private sealed class SimulatedClientJsRuntime : IJSRuntime
    {
        private readonly VoiceCallClusterSimulator _cluster;
        private readonly string _clientId;
        private readonly SimulatedVoiceModuleReference _moduleRef;

        public SimulatedClientJsRuntime(VoiceCallClusterSimulator cluster, string clientId)
        {
            _cluster = cluster;
            _clientId = clientId;
            _moduleRef = new SimulatedVoiceModuleReference(cluster, clientId);
        }

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args)
            => InvokeAsync<TValue>(identifier, CancellationToken.None, args);

        public async ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args)
        {
            if (identifier == "import")
            {
                return (TValue)(object)_moduleRef;
            }
            if (identifier == "localStorage.getItem")
            {
                var key = args != null && args.Length > 0 ? args[0]?.ToString() ?? "" : "";
                var res = await _cluster.SendNodeCommandAsync("localStorageGet", new { clientId = _clientId, key });
                if (res.ValueKind == JsonValueKind.Null || res.ValueKind == JsonValueKind.Undefined)
                    return default!;
                return JsonSerializer.Deserialize<TValue>(res.GetRawText())!;
            }
            if (identifier == "localStorage.setItem")
            {
                var key = args != null && args.Length > 0 ? args[0]?.ToString() ?? "" : "";
                var val = args != null && args.Length > 1 ? args[1]?.ToString() ?? "" : "";
                await _cluster.SendNodeCommandAsync("localStorageSet", new { clientId = _clientId, key, value = val });
                return default!;
            }
            return default!;
        }
    }

    private sealed class SimulatedVoiceModuleReference : IJSObjectReference
    {
        private readonly VoiceCallClusterSimulator _cluster;
        private readonly string _clientId;
        private static readonly JsonSerializerOptions JsonOpts = new()
        {
            PropertyNameCaseInsensitive = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };

        public SimulatedVoiceModuleReference(VoiceCallClusterSimulator cluster, string clientId)
        {
            _cluster = cluster;
            _clientId = clientId;
        }

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args)
            => InvokeAsync<TValue>(identifier, CancellationToken.None, args);

        public async ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args)
        {
            var sanitizedArgs = (args ?? []).Select(SanitizeArg).ToArray();
            var res = await _cluster.SendNodeCommandAsync("invokeModule", new
            {
                clientId = _clientId,
                identifier,
                args = sanitizedArgs
            });

            if (typeof(TValue) == typeof(IJSVoidResult) || res.ValueKind == JsonValueKind.Null || res.ValueKind == JsonValueKind.Undefined)
            {
                return default!;
            }

            return JsonSerializer.Deserialize<TValue>(res.GetRawText(), JsonOpts)!;
        }

        private static object? SanitizeArg(object? arg)
        {
            if (arg == null) return null;
            var type = arg.GetType();
            if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(DotNetObjectReference<>))
            {
                return new { __isDotNetRef = true };
            }
            return arg;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
