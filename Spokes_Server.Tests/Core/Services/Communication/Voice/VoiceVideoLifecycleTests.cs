using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.JSInterop;
using Microsoft.JSInterop.Infrastructure;
using Moq;
using MudBlazor;
using Spokes_Server.Components.UI;
using Spokes_Server.Core.Data;
using Spokes_Server.Core.Models.Communication;
using Spokes_Server.Core.Services.Communication.Chat;
using Spokes_Server.Core.Services.Communication.Voice;
using Spokes_Server.Core.Services.UI;
using Xunit;

namespace Spokes_Server.Tests.Core.Services.Communication.Voice;

public class VoiceVideoLifecycleTests : IAsyncDisposable
{
    private readonly Mock<IJSRuntime> _jsMock;
    private readonly Mock<IJSObjectReference> _voiceModuleMock;
    private readonly Mock<VoiceChannelService> _voiceChannelServiceMock;
    private readonly ChatStateService _chatState;
    private readonly VoiceDeviceService _deviceService;
    private readonly GlobalVoiceService _service;
    private readonly VoiceUiStateService _uiState;

    public VoiceVideoLifecycleTests()
    {
        _jsMock = new Mock<IJSRuntime>();
        _voiceModuleMock = new Mock<IJSObjectReference>();

        _voiceModuleMock.Setup(x => x.InvokeAsync<bool>("isAppleMobileDevice", It.IsAny<object[]>())).Returns(new ValueTask<bool>(false));
        _voiceModuleMock.Setup(x => x.InvokeAsync<bool>("isAndroidDevice", It.IsAny<object[]>())).Returns(new ValueTask<bool>(false));
        _voiceModuleMock.Setup(x => x.InvokeAsync<bool>("connectToVoice", It.IsAny<object[]>())).Returns(new ValueTask<bool>(true));
        _voiceModuleMock.Setup(x => x.InvokeAsync<CameraInitResult>("setCameraEnabled", It.IsAny<object[]>()))
            .Returns((string _, object[] args) =>
            {
                bool enabled = args.Length > 0 && args[0] is bool b && b;
                return new ValueTask<CameraInitResult>(new CameraInitResult
                {
                    Cameras = enabled ? [new CameraDevice { DeviceId = "cam-hd", Label = "HD Webcam" }] : [],
                    ActiveCameraDeviceId = enabled ? "cam-hd" : ""
                });
            });

        _jsMock.Setup(x => x.InvokeAsync<IJSObjectReference>("import", It.IsAny<object[]>()))
            .Returns(new ValueTask<IJSObjectReference>(_voiceModuleMock.Object));

        _chatState = new ChatStateService();

        _voiceChannelServiceMock = new Mock<VoiceChannelService>();
        _voiceChannelServiceMock.Setup(x => x.JoinVoiceAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, CancellationToken>((channelId, userId, _) => _chatState.NotifyVoiceMemberJoined(channelId, userId))
            .Returns(Task.CompletedTask);
        _voiceChannelServiceMock.Setup(x => x.LeaveVoiceAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, CancellationToken>((channelId, userId, _) => _chatState.NotifyVoiceMemberLeft(channelId, userId))
            .Returns(Task.CompletedTask);

        var jsInterop = new VoiceJsInteropService(_jsMock.Object);
        _deviceService = new VoiceDeviceService(jsInterop, NullLogger<VoiceDeviceService>.Instance);
        _service = new GlobalVoiceService(
            jsInterop,
            _voiceChannelServiceMock.Object,
            _chatState,
            NullLogger<GlobalVoiceService>.Instance,
            _deviceService);
        _uiState = new VoiceUiStateService(
            Mock.Of<ISnackbar>(),
            Mock.Of<ISoundService>(),
            _jsMock.Object,
            _service,
            jsInterop,
            _deviceService,
            NullLogger<VoiceUiStateService>.Instance);
    }

    public async ValueTask DisposeAsync()
    {
        _uiState.Dispose();
        await _service.DisposeAsync();
    }

    [Fact]
    public async Task FullLifecycle_ScreenShareThenStopThenWebcamToggle_NeverSticksLayoutOrBlocksCamera()
    {
        // 1. Connect to voice channel with 2 participants
        _chatState.NotifyVoiceMemberJoined("ch-voice", "user-remote");
        await _service.ConnectAsync(new VoiceConnectionOptions("jwt-token", "wss://lk.test", "ch-voice", "user-local", 64, "webrtc"));

        Assert.True(_service.IsInVoiceCall);
        Assert.False(_service.IsVideoOn);
        Assert.False(_service.IsScreenSharing);
        Assert.False(_service.HasAnyActiveVideoOrScreen);

        // 2. Start screen share on desktop client
        await _service.ToggleScreenShareAsync("1440p", 60, "motion", true);
        _service.OnVideoTrackStateChanged("user-local", "screenshare", true);
        _service.OnLocalScreenDimensions(2560, 60);

        Assert.True(_service.IsScreenSharing);
        Assert.Contains("user-local", _service.ActiveScreenShareParticipants);
        Assert.True(_service.HasAnyActiveVideoOrScreen);

        // 3. Stop screen share via button
        await _service.ToggleScreenShareAsync();
        _service.OnVideoTrackStateChanged("user-local", "screenshare", false);

        Assert.False(_service.IsScreenSharing);
        Assert.DoesNotContain("user-local", _service.ActiveScreenShareParticipants);
        Assert.False(_service.HasAnyActiveVideoOrScreen);

        // 4. Enable webcam after screen share ends -> must succeed and update state
        await _service.ToggleVideoAsync("Auto", 30, "");
        _service.OnVideoTrackStateChanged("user-local", "camera", true);

        Assert.True(_service.IsVideoOn);
        Assert.Contains("user-local", _service.ActiveCameraParticipants);
        Assert.True(_service.HasAnyActiveVideoOrScreen);
        Assert.Equal("cam-hd", _uiState.ActiveCameraDeviceId);

        // 5. Disable webcam -> must cleanly turn off
        await _service.ToggleVideoAsync("Auto", 30, "cam-hd");
        _service.OnVideoTrackStateChanged("user-local", "camera", false);

        Assert.False(_service.IsVideoOn);
        Assert.DoesNotContain("user-local", _service.ActiveCameraParticipants);
        Assert.False(_service.HasAnyActiveVideoOrScreen);

        // 6. Disconnect and reconnect
        await _service.DisconnectAsync();
        Assert.False(_service.IsInVoiceCall);
        Assert.Empty(_service.ActiveCameraParticipants);
        Assert.Empty(_service.ActiveScreenShareParticipants);
    }

    [Fact]
    public async Task NativeOsStopSharingBanner_ResetsIsScreenSharingAndClearsActiveScreenShareParticipants()
    {
        await _service.ConnectAsync(new VoiceConnectionOptions("jwt-token", "wss://lk.test", "ch-voice", "user-local", 64, "webrtc"));

        await _service.ToggleScreenShareAsync("1080p", 60, "detail", false);
        _service.OnVideoTrackStateChanged("user-local", "screenshare", true);
        Assert.True(_service.IsScreenSharing);
        Assert.Contains("user-local", _service.ActiveScreenShareParticipants);

        // Simulate user clicking the OS/browser native "Stop sharing" banner (JS fires OnVideoTrackStateChanged(..., false))
        _service.OnVideoTrackStateChanged("user-local", "screenshare", false);

        Assert.False(_service.IsScreenSharing);
        Assert.DoesNotContain("user-local", _service.ActiveScreenShareParticipants);
        Assert.False(_service.HasAnyActiveVideoOrScreen);

        // Clicking Share Screen button again should start sharing (newScreenState == true), not try to stop again
        await _service.ToggleScreenShareAsync("1080p", 60, "detail", false);
        Assert.True(_service.IsScreenSharing);
    }

    [Fact]
    public async Task RemoteScreenShareAndCameraStop_ClearsRemoteVideoHeightsImmediately()
    {
        _chatState.NotifyVoiceMemberJoined("ch-voice", "user-remote");
        await _service.ConnectAsync(new VoiceConnectionOptions("jwt-token", "wss://lk.test", "ch-voice", "user-local", 64, "webrtc"));

        // Remote user starts screen share and reports 1440p60 dimensions
        _service.OnVideoTrackStateChanged("user-remote", "screenshare", true);
        _service.OnParticipantVideoDimensions("user-remote", 1440, 60);

        Assert.Contains("user-remote", _service.ActiveScreenShareParticipants);
        Assert.Equal(1440, _deviceService.RemoteVideoHeights.GetValueOrDefault("user-remote", 0));

        // Remote user also enables camera
        _service.OnVideoTrackStateChanged("user-remote", "camera", true);
        Assert.Contains("user-remote", _service.ActiveCameraParticipants);

        // Remote user stops screen share while camera is still active -> height remains until camera also stops
        _service.OnVideoTrackStateChanged("user-remote", "screenshare", false);
        Assert.DoesNotContain("user-remote", _service.ActiveScreenShareParticipants);
        Assert.Contains("user-remote", _service.ActiveCameraParticipants);
        Assert.Equal(1440, _deviceService.RemoteVideoHeights.GetValueOrDefault("user-remote", 0));

        // Remote user turns off camera -> no active video/screen remains, RemoteVideoHeights must be cleared to 0
        _service.OnVideoTrackStateChanged("user-remote", "camera", false);
        Assert.DoesNotContain("user-remote", _service.ActiveCameraParticipants);
        Assert.Equal(0, _deviceService.RemoteVideoHeights.GetValueOrDefault("user-remote", 0));
        Assert.False(_service.HasAnyActiveVideoOrScreen);
    }

    [Fact]
    public async Task VoiceInteropJs_RuntimeLifecycle_ScreenShareWebcamAndDeviceRelease_AllSucceedWithoutErrors()
    {
        // Locate voiceInterop.js on disk
        var baseDir = AppContext.BaseDirectory;
        var repoRoot = Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", ".."));
        var voiceInteropPath = Path.Combine(repoRoot, "Spokes_Server", "wwwroot", "js", "voiceInterop.js");
        Assert.True(File.Exists(voiceInteropPath), $"Expected voiceInterop.js at {voiceInteropPath}");

        var tempDir = Path.Combine(Path.GetTempPath(), $"spokes_voice_js_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(tempDir, "package.json"), """{"type":"module"}""");

            // Copy voiceInterop.js and strip query strings on relative imports so Node ESM resolves them directly
            var voiceInteropSrc = await File.ReadAllTextAsync(voiceInteropPath);
            voiceInteropSrc = voiceInteropSrc
                .Replace("./noiseSuppressionManager.js?v=20261103i", "./noiseSuppressionManager.js")
                .Replace("./voicePip.js?v=20261103i", "./voicePip.js");
            await File.WriteAllTextAsync(Path.Combine(tempDir, "voiceInterop.js"), voiceInteropSrc);

            await File.WriteAllTextAsync(Path.Combine(tempDir, "noiseSuppressionManager.js"), """
                export class NoiseSuppressionManager {
                    async getProcessor() { return null; }
                }
                """);

            await File.WriteAllTextAsync(Path.Combine(tempDir, "voicePip.js"), """
                export const pipTracks = new Map();
                export const pipState = {};
                export function configureVoicePip() {}
                export function registerPipVideoTrack(id, source, mst) { pipTracks.set(`${id}:${source}`, mst); }
                export function unregisterPipVideoTrack(id, source) {
                    if (source) pipTracks.delete(`${id}:${source}`);
                    else { pipTracks.delete(`${id}:camera`); pipTracks.delete(`${id}:screenshare`); }
                }
                export function updatePipSpeakingParticipants() {}
                export function updatePipCallState(patch) { Object.assign(pipState, patch); }
                export function resetPipOnCallEnd() { pipTracks.clear(); }
                export function setInAppPipActive() {}
                export function syncInAppPipElements() {}
                export async function toggleOsPip() { return false; }
                export function closeOsPip() {}
                export function isOsPipSupported() { return false; }
                export function isStageSuppressedByExternalPip() { return false; }
                """);

            var runnerScript = """
                import assert from 'node:assert/strict';

                const createdMediaTracks = [];
                function createMockMediaStreamTrack(kind, label = '') {
                    const listeners = new Map();
                    const mst = {
                        id: `mst-${createdMediaTracks.length + 1}`,
                        kind,
                        label,
                        enabled: true,
                        muted: false,
                        readyState: 'live',
                        contentHint: '',
                        stopCalls: 0,
                        stop() {
                            this.stopCalls++;
                            this.readyState = 'ended';
                        },
                        getSettings() {
                            return kind === 'video'
                                ? { width: 1920, height: 1080, frameRate: 60, deviceId: 'cam-1', facingMode: 'user' }
                                : { deviceId: 'mic-1' };
                        },
                        async applyConstraints() {},
                        addEventListener(evt, fn) {
                            if (!listeners.has(evt)) listeners.set(evt, new Set());
                            listeners.get(evt).add(fn);
                        },
                        removeEventListener(evt, fn) {
                            listeners.get(evt)?.delete(fn);
                        },
                        dispatchEvent(evt) {
                            for (const fn of Array.from(listeners.get(evt) || [])) fn();
                        }
                    };
                    createdMediaTracks.push(mst);
                    return mst;
                }

                function createMockVideoElement(id, cls) {
                    const classes = new Set([cls]);
                    const styles = new Map();
                    return {
                        id,
                        tagName: 'VIDEO',
                        isConnected: true,
                        paused: true,
                        muted: false,
                        srcObject: null,
                        videoWidth: 1920,
                        videoHeight: 1080,
                        style: {
                            opacity: '0',
                            pointerEvents: 'none',
                            transform: '',
                            setProperty(k, v) { styles.set(k, v); },
                            removeProperty(k) { styles.delete(k); }
                        },
                        classList: {
                            add(c) { classes.add(c); },
                            remove(c) { classes.delete(c); },
                            contains(c) { return classes.has(c); }
                        },
                        pause() { this.paused = true; },
                        load() {},
                        removeAttribute() {},
                        async play() { this.paused = false; },
                        addEventListener() {},
                        removeEventListener() {},
                        closest() { return null; }
                    };
                }

                const domById = new Map();
                function ensureSlotElements(participantId) {
                    const screenId = `participant-screen-${participantId}`;
                    const camId = `participant-video-${participantId}`;
                    if (!domById.has(screenId)) domById.set(screenId, createMockVideoElement(screenId, 'livekit-screenshare'));
                    if (!domById.has(camId)) domById.set(camId, createMockVideoElement(camId, 'livekit-camera'));
                }
                ensureSlotElements('user-local');
                ensureSlotElements('user-remote');

                globalThis.localStorage = {
                    _map: new Map(),
                    getItem(k) { return this._map.get(k) ?? null; },
                    setItem(k, v) { this._map.set(k, String(v)); }
                };
                Object.defineProperty(globalThis, 'navigator', {
                    configurable: true,
                    writable: true,
                    value: {
                        userAgent: 'Mozilla/5.0 (Windows NT 10.0; Win64; x64) Electron/41.0.0 Chrome/134.0.0.0',
                        mediaDevices: {
                            async enumerateDevices() {
                                return [
                                    { kind: 'audioinput', deviceId: 'mic-1', label: 'Studio Mic' },
                                    { kind: 'videoinput', deviceId: 'cam-1', label: 'Studio Cam' }
                                ];
                            }
                        }
                    }
                });
                globalThis.document = {
                    visibilityState: 'visible',
                    body: { appendChild() {} },
                    addEventListener() {},
                    getElementById(id) { return domById.get(id) || null; },
                    querySelectorAll(sel) {
                        if (sel === 'video.has-stream') {
                            return Array.from(domById.values()).filter(el => el.tagName === 'VIDEO' && el.classList.contains('has-stream'));
                        }
                        return [];
                    }
                };

                const Track = {
                    Kind: { Audio: 'audio', Video: 'video' },
                    Source: { Camera: 'camera', Microphone: 'microphone', ScreenShare: 'screen_share', ScreenShareAudio: 'screen_share_audio' }
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

                function createStrictRtpSender() {
                    const currentParams = {
                        encodings: [{ maxBitrate: 1000000, maxFramerate: 30, scaleResolutionDownBy: 1.0 }]
                    };
                    return {
                        getParameters() {
                            return JSON.parse(JSON.stringify(currentParams));
                        },
                        async setParameters(params) {
                            // Strictly enforce Chromium WebRTC RTCRtpSender.setParameters behavior:
                            // reject with OperationError if unimplemented fields (priority, networkPriority, degradationPreference) are added!
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
                        },
                        getAppliedEncodings() { return currentParams.encodings; }
                    };
                }

                function createMockLivekitTrack(kind, source) {
                    const mst = createMockMediaStreamTrack(kind, source);
                    const attached = [];
                    const sender = kind === 'video' ? createMockRtpSender() : null;
                    function createMockRtpSender() { return createStrictRtpSender(); }
                    return {
                        sid: `TR_${createdMediaTracks.length}`,
                        kind,
                        source,
                        mediaStreamTrack: mst,
                        sender,
                        attachedElements: attached,
                        attach(el) {
                            const target = el || createMockVideoElement(`auto-${this.sid}`, '');
                            target.srcObject = { getVideoTracks: () => [mst] };
                            if (!attached.includes(target)) attached.push(target);
                            return target;
                        },
                        detach() {
                            const copy = [...attached];
                            attached.length = 0;
                            return copy;
                        },
                        stop() {
                            mst.stop();
                        },
                        async setDegradationPreference() {}
                    };
                }

                let activeRoomInstance = null;
                class MockRoom {
                    constructor() {
                        this.name = 'General Voice';
                        this.handlers = new Map();
                        this.remoteParticipants = new Map();
                        const roomSelf = this;
                        this.localParticipant = {
                            identity: 'user-local',
                            videoTrackPublications: new Map(),
                            audioTrackPublications: new Map(),
                            async publishTrack(track, opts = {}) {
                                track.source = opts.source || track.source;
                                const pub = {
                                    trackSid: track.sid,
                                    kind: track.kind,
                                    source: track.source,
                                    track,
                                    videoTrack: track.kind === 'video' ? track : undefined,
                                    audioTrack: track.kind === 'audio' ? track : undefined
                                };
                                if (track.kind === 'video') {
                                    this.videoTrackPublications.set(track.sid, pub);
                                } else {
                                    this.audioTrackPublications.set(track.sid, pub);
                                }
                                roomSelf.emit(RoomEvent.LocalTrackPublished, pub, this);
                                return pub;
                            },
                            async unpublishTrack(track, stopOnUnpublish = true) {
                                const map = track.kind === 'video' ? this.videoTrackPublications : this.audioTrackPublications;
                                const pub = map.get(track.sid) || { trackSid: track.sid, kind: track.kind, source: track.source, track: undefined };
                                map.delete(track.sid);
                                // Match livekit-client behavior: detach & clear pub.track before firing LocalTrackUnpublished
                                pub.track = undefined;
                                if (stopOnUnpublish) track.stop();
                                roomSelf.emit(RoomEvent.LocalTrackUnpublished, pub, this);
                            }
                        };
                        activeRoomInstance = this;
                    }
                    on(evt, fn) {
                        this.handlers.set(evt, fn);
                        return this;
                    }
                    emit(evt, ...args) {
                        const fn = this.handlers.get(evt);
                        if (fn) fn(...args);
                    }
                    async connect() {}
                    async disconnect() {
                        // Match livekit-client behavior: emit TrackUnsubscribed for remote tracks during room.disconnect()
                        for (const p of this.remoteParticipants.values()) {
                            for (const pub of p.videoTrackPublications.values()) {
                                this.emit(RoomEvent.TrackUnsubscribed, pub.track, pub, p);
                            }
                        }
                        this.emit(RoomEvent.Disconnected);
                    }
                    static async getLocalDevices(kind) {
                        if (kind === 'videoinput') return [{ deviceId: 'cam-1', label: 'Studio Cam' }];
                        return [{ deviceId: 'mic-1', label: 'Studio Mic' }];
                    }
                }

                globalThis.window = globalThis;
                globalThis.CustomEvent = class CustomEvent { constructor(name, opts) { this.type = name; this.detail = opts?.detail; } };
                globalThis.dispatchEvent = () => {};
                globalThis.addEventListener = () => {};
                globalThis.LivekitClient = {
                    Room: MockRoom,
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
                    async createLocalAudioTrack() {
                        await new Promise(r => setTimeout(r, 40));
                        return createMockLivekitTrack('audio', Track.Source.Microphone);
                    },
                    async createLocalVideoTrack() {
                        return createMockLivekitTrack('video', Track.Source.Camera);
                    },
                    async createLocalScreenTracks() {
                        return [
                            createMockLivekitTrack('video', Track.Source.ScreenShare),
                            createMockLivekitTrack('audio', Track.Source.ScreenShareAudio)
                        ];
                    }
                };

                const dotnetCalls = [];
                const dotnetHelper = {
                    async invokeMethodAsync(method, ...args) {
                        dotnetCalls.push({ method, args });
                        return null;
                    }
                };

                const voice = await import('./voiceInterop.js');

                // 1. Connect to voice, then IMMEDIATELY click Mute before initial setMicrophoneEnabled(true) finishes awaiting createLocalAudioTrack
                const connected = await voice.connectToVoice('token-1', 'wss://lk.test', dotnetHelper, 64, 'webrtc');
                assert.equal(connected, true, 'connectToVoice should succeed');
                await voice.setMicrophoneEnabled(false, 64, 'webrtc');
                await new Promise(r => setTimeout(r, 65));
                assert.equal(
                    activeRoomInstance.localParticipant.audioTrackPublications.size,
                    0,
                    'Clicking Mute immediately after connecting must abort in-flight setMicrophoneEnabled(true) and never publish audio'
                );

                // Unmute -> now audio track publishes cleanly
                await voice.setMicrophoneEnabled(true, 64, 'webrtc');
                assert.equal(activeRoomInstance.localParticipant.audioTrackPublications.size, 1, 'Unmuting after initial mute should publish 1 audio track');

                // 2. Start screen share (1440p 60fps Smooth)
                await voice.setScreenShareEnabled(true, '1440p', 60, 'motion', true);
                await new Promise(r => setTimeout(r, 30));

                const localScreenEl = domById.get('participant-screen-user-local');
                const localCamEl = domById.get('participant-video-user-local');
                assert.equal(localScreenEl.classList.contains('has-stream'), true, 'Screen share <video> must have .has-stream when sharing');
                assert.equal(localScreenEl.style.opacity, '1', 'Screen share <video> must be visible when sharing');

                // 3. Stop screen share
                await voice.setScreenShareEnabled(false);
                await new Promise(r => setTimeout(r, 30));

                assert.equal(localScreenEl.classList.contains('has-stream'), false, 'Screen share <video> must NOT have .has-stream after stopping');
                assert.equal(localScreenEl.style.opacity, '0', 'Screen share <video> opacity must reset to 0 after stopping');
                assert.ok(
                    dotnetCalls.some(c => c.method === 'OnVideoTrackStateChanged' && c.args[0] === 'user-local' && c.args[1] === 'screenshare' && c.args[2] === false),
                    'Stopping screen share must notify C# OnVideoTrackStateChanged(user-local, screenshare, false)'
                );

                // 4. Enable webcam after ending screen share -> must attach and display cleanly
                const camRes = await voice.setCameraEnabled(true, '1080p', 60);
                await new Promise(r => setTimeout(r, 30));
                assert.ok(camRes && Array.isArray(camRes.cameras), 'setCameraEnabled(true) after screen share must return camera list');
                assert.equal(localCamEl.classList.contains('has-stream'), true, 'Camera <video> must have .has-stream after enabling webcam');
                assert.equal(localCamEl.style.opacity, '1', 'Camera <video> must have opacity 1 after enabling webcam');

                // 5. Simulate remote iOS companion camera ('user-remote:cam') subscribing & unsubscribing WHILE local camera is on
                const remoteCompanionTrack = createMockLivekitTrack('video', Track.Source.Camera);
                const remoteCompanionPub = { trackSid: remoteCompanionTrack.sid, kind: 'video', source: Track.Source.Camera, track: remoteCompanionTrack, isSubscribed: true };
                const remoteCompanionParticipant = {
                    identity: 'user-remote:cam',
                    videoTrackPublications: new Map([[remoteCompanionTrack.sid, remoteCompanionPub]])
                };
                activeRoomInstance.remoteParticipants.set('user-remote:cam', remoteCompanionParticipant);
                activeRoomInstance.emit(RoomEvent.TrackSubscribed, remoteCompanionTrack, remoteCompanionPub, remoteCompanionParticipant);
                await new Promise(r => setTimeout(r, 30));

                const remoteCamEl = domById.get('participant-video-user-remote');
                assert.equal(remoteCamEl.classList.contains('has-stream'), true, 'Remote companion camera should attach to participant-video-user-remote');

                // Unsubscribe remote companion camera while local camera is still enabled (_localCameraDesired === true)
                remoteCompanionParticipant.videoTrackPublications.clear();
                activeRoomInstance.emit(RoomEvent.TrackUnsubscribed, remoteCompanionTrack, remoteCompanionPub, remoteCompanionParticipant);
                assert.equal(remoteCamEl.classList.contains('has-stream'), false, 'Remote companion camera unsubscribe must clear remote video DOM even when local camera is on');

                // 6. Disable local webcam -> must stop underlying MediaStreamTrack and clear DOM
                await voice.setCameraEnabled(false);
                assert.equal(localCamEl.classList.contains('has-stream'), false, 'Camera <video> must remove .has-stream when disabled');
                assert.equal(localCamEl.style.opacity, '0', 'Camera <video> opacity must reset to 0 when disabled');

                // 7. Re-enable webcam & screen share, then test OS native 'ended' event on screen share track
                await voice.setCameraEnabled(true, 'Auto', 30);
                await voice.setScreenShareEnabled(true, 'Auto', 30, 'motion', false);
                await new Promise(r => setTimeout(r, 30));
                assert.equal(localScreenEl.classList.contains('has-stream'), true);

                // Trigger native OS 'Stop sharing' banner ('ended' event on screen share MediaStreamTrack)
                const activeScreenPub = Array.from(activeRoomInstance.localParticipant.videoTrackPublications.values())
                    .find(p => p.source === Track.Source.ScreenShare);
                assert.ok(activeScreenPub, 'ScreenShare publication should exist');
                activeScreenPub.track.mediaStreamTrack.dispatchEvent('ended');
                await new Promise(r => setTimeout(r, 50));

                assert.equal(localScreenEl.classList.contains('has-stream'), false, 'Native OS screen share stop must clear screen share DOM');
                assert.equal(localCamEl.classList.contains('has-stream'), true, 'Local camera must remain active when screen share stops');

                // 8. Disconnect from voice -> ALL local MediaStreamTracks (mic, camera, screen) must be stopped (readyState === 'ended')
                await voice.disconnectFromVoice();
                const liveLocalTracks = createdMediaTracks.filter(t => t !== remoteCompanionTrack.mediaStreamTrack && t.readyState !== 'ended');
                assert.equal(
                    liveLocalTracks.length,
                    0,
                    `All local MediaStreamTracks must be stopped on disconnect to prevent NotReadableError: Device in use, but found ${liveLocalTracks.length} still live`
                );

                console.log('VOICE_JS_LIFECYCLE_OK');
                """;

            await File.WriteAllTextAsync(Path.Combine(tempDir, "runner.mjs"), runnerScript, Encoding.UTF8);

            var psi = new ProcessStartInfo
            {
                FileName = "node",
                Arguments = "runner.mjs",
                WorkingDirectory = tempDir,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var proc = Process.Start(psi);
            Assert.NotNull(proc);
            string stdout = await proc.StandardOutput.ReadToEndAsync();
            string stderr = await proc.StandardError.ReadToEndAsync();
            await proc.WaitForExitAsync();

            Assert.True(proc.ExitCode == 0, $"Node JS lifecycle test failed (exit {proc.ExitCode}):\nSTDOUT:\n{stdout}\nSTDERR:\n{stderr}");
            Assert.Contains("VOICE_JS_LIFECYCLE_OK", stdout);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }
    }

    [Fact]
    public async Task Cluster_DesktopScreenShare_StartStopViaButtonAndOsBanner_ThenToggleWebcam_UnsticksLayoutAcrossIosAndroidDesktop()
    {
        await using var cluster = await VoiceCallClusterSimulator.CreateAsync();

        var desktop = await cluster.CreateClientAsync("desktop", "user-desktop", VoiceClientPlatform.DesktopElectron);
        var ios = await cluster.CreateClientAsync("ios", "user-ios", VoiceClientPlatform.IosNative);
        var android = await cluster.CreateClientAsync("android", "user-android", VoiceClientPlatform.AndroidNative);

        await desktop.ConnectToChannelAsync("ch-eng");
        await ios.ConnectToChannelAsync("ch-eng");
        await android.ConnectToChannelAsync("ch-eng");
        await cluster.SettleAsync();

        // Verify all 3 clients see all 3 participants in avatar mode initially
        foreach (var client in new[] { desktop, ios, android })
        {
            Assert.Equal(3, client.VoiceService.CallParticipants.Count);
            Assert.False(client.VoiceService.HasAnyActiveVideoOrScreen);
            var snap = await client.GetStageSnapshotAsync();
            Assert.Equal(StageLayoutMode.Grid, snap.LayoutMode);
            Assert.True(snap.Tiles["user-desktop"].IsLargeAvatarVisible);
            Assert.False(snap.Tiles["user-desktop"].HasScreenShareStream);
            Assert.False(snap.Tiles["user-desktop"].HasCameraStream);
        }

        // 1. Desktop starts 1440p60 Smooth screen share with system audio
        await desktop.VoiceService.ToggleScreenShareAsync("1440p", 60, "motion", captureAudio: true);
        await cluster.SettleAsync();

        Assert.True(desktop.VoiceService.IsScreenSharing);
        Assert.True(desktop.UiState.IsScreenShareAudioEnabled);
        Assert.Contains("user-desktop", ios.VoiceService.RemoteScreenAudioParticipants);
        Assert.Contains("user-desktop", android.VoiceService.RemoteScreenAudioParticipants);
        Assert.Equal(2560, ios.DeviceService.RemoteVideoHeights.GetValueOrDefault("user-desktop", 0));
        Assert.Equal(2560, android.DeviceService.RemoteVideoHeights.GetValueOrDefault("user-desktop", 0));

        foreach (var client in new[] { desktop, ios, android })
        {
            Assert.Contains("user-desktop", client.VoiceService.ActiveScreenShareParticipants);
            Assert.True(client.VoiceService.HasAnyActiveVideoOrScreen);
            var snap = await client.GetStageSnapshotAsync();
            var deskTile = snap.Tiles["user-desktop"];
            Assert.True(deskTile.HasScreenShareStream);
            Assert.False(deskTile.ScreenVideoPaused);
            Assert.Equal("live", deskTile.ScreenTrackReadyState);
            Assert.False(deskTile.IsLargeAvatarVisible);
            Assert.True(deskTile.IsSmallOverlayVisible);
        }

        // 2. Desktop also enables webcam while screen sharing -> camera appears in corner PiP mode on all 3 clients
        await desktop.VoiceService.ToggleVideoAsync("1080p", 60);
        await cluster.SettleAsync();

        foreach (var client in new[] { desktop, ios, android })
        {
            Assert.Contains("user-desktop", client.VoiceService.ActiveCameraParticipants);
            Assert.Contains("user-desktop", client.VoiceService.ActiveScreenShareParticipants);
            var snap = await client.GetStageSnapshotAsync();
            var deskTile = snap.Tiles["user-desktop"];
            Assert.True(deskTile.HasScreenShareStream);
            Assert.True(deskTile.HasCameraStream);
            Assert.True(deskTile.IsCameraInCornerPipMode);
            Assert.False(deskTile.IsCameraFullTileMode);
        }

        // 3. Desktop stops screen share via UI button -> screen share clears and camera transitions cleanly to full-tile mode
        await desktop.VoiceService.ToggleScreenShareAsync();
        await cluster.SettleAsync();

        Assert.False(desktop.VoiceService.IsScreenSharing);
        Assert.False(desktop.UiState.IsScreenShareAudioEnabled);
        Assert.DoesNotContain("user-desktop", ios.VoiceService.RemoteScreenAudioParticipants);
        Assert.DoesNotContain("user-desktop", android.VoiceService.RemoteScreenAudioParticipants);

        foreach (var client in new[] { desktop, ios, android })
        {
            Assert.DoesNotContain("user-desktop", client.VoiceService.ActiveScreenShareParticipants);
            Assert.Contains("user-desktop", client.VoiceService.ActiveCameraParticipants);
            var snap = await client.GetStageSnapshotAsync();
            var deskTile = snap.Tiles["user-desktop"];
            Assert.False(deskTile.HasScreenShareStream);
            Assert.True(deskTile.HasCameraStream);
            Assert.False(deskTile.IsCameraInCornerPipMode);
            Assert.True(deskTile.IsCameraFullTileMode);
            Assert.False(deskTile.IsLargeAvatarVisible);
        }

        // 4. Desktop turns off webcam -> all 3 clients return to avatar view and clear RemoteVideoHeights
        await desktop.VoiceService.ToggleVideoAsync();
        await cluster.SettleAsync();

        Assert.Equal(0, ios.DeviceService.RemoteVideoHeights.GetValueOrDefault("user-desktop", 0));
        Assert.Equal(0, android.DeviceService.RemoteVideoHeights.GetValueOrDefault("user-desktop", 0));

        foreach (var client in new[] { desktop, ios, android })
        {
            Assert.False(client.VoiceService.HasAnyActiveVideoOrScreen);
            var snap = await client.GetStageSnapshotAsync();
            var deskTile = snap.Tiles["user-desktop"];
            Assert.False(deskTile.HasScreenShareStream);
            Assert.False(deskTile.HasCameraStream);
            Assert.True(deskTile.IsLargeAvatarVisible);
        }

        // 5. Desktop starts screen share again, then stops via native OS "Stop sharing" banner ('ended' event)
        await desktop.VoiceService.ToggleScreenShareAsync("1080p", 60, "detail", captureAudio: false);
        await cluster.SettleAsync();
        Assert.True(desktop.VoiceService.IsScreenSharing);

        await desktop.TriggerNativeOsStopScreenShareBannerAsync();
        await cluster.SettleAsync();

        Assert.False(desktop.VoiceService.IsScreenSharing);
        foreach (var client in new[] { desktop, ios, android })
        {
            Assert.DoesNotContain("user-desktop", client.VoiceService.ActiveScreenShareParticipants);
            Assert.False(client.VoiceService.HasAnyActiveVideoOrScreen);
            var snap = await client.GetStageSnapshotAsync();
            Assert.False(snap.Tiles["user-desktop"].HasScreenShareStream);
            Assert.True(snap.Tiles["user-desktop"].IsLargeAvatarVisible);
        }

        // 6. Desktop enables and disables webcam after native OS screen share stop -> works cleanly
        await desktop.VoiceService.ToggleVideoAsync("1080p", 60);
        await cluster.SettleAsync();

        foreach (var client in new[] { desktop, ios, android })
        {
            var snap = await client.GetStageSnapshotAsync();
            Assert.True(snap.Tiles["user-desktop"].HasCameraStream);
            Assert.True(snap.Tiles["user-desktop"].IsCameraFullTileMode);
        }

        await desktop.VoiceService.ToggleVideoAsync();
        await cluster.SettleAsync();

        foreach (var client in new[] { desktop, ios, android })
        {
            var snap = await client.GetStageSnapshotAsync();
            Assert.False(snap.Tiles["user-desktop"].HasCameraStream);
            Assert.True(snap.Tiles["user-desktop"].IsLargeAvatarVisible);
        }
    }

    [Fact]
    public async Task Cluster_IosNativeCompanionCamera_StartSwitchQualityFlipAndStop_StreamsToAndroidAndDesktopWithoutGhostParticipants()
    {
        await using var cluster = await VoiceCallClusterSimulator.CreateAsync();

        var ios = await cluster.CreateClientAsync("ios", "user-ios", VoiceClientPlatform.IosNative);
        var android = await cluster.CreateClientAsync("android", "user-android", VoiceClientPlatform.AndroidNative);
        var desktop = await cluster.CreateClientAsync("desktop", "user-desktop", VoiceClientPlatform.DesktopElectron);

        await ios.ConnectToChannelAsync("ch-family");
        await android.ConnectToChannelAsync("ch-family");
        await desktop.ConnectToChannelAsync("ch-family");
        await cluster.SettleAsync();

        // 1. iOS enables camera (Auto preset -> 1080p30 @ 3 Mbps via native companion 'user-ios:cam')
        await ios.VoiceService.ToggleVideoAsync("Auto", 30);
        await cluster.SettleAsync();

        Assert.True(ios.VoiceService.IsVideoOn);
        var iosSnap = await ios.GetStageSnapshotAsync();
        Assert.True(iosSnap.Hardware.NativeIosCompanionConnected);
        Assert.NotNull(iosSnap.Hardware.CameraEncoding);
        Assert.Equal(3_000_000, iosSnap.Hardware.CameraEncoding!.MaxBitrate);
        Assert.Equal(1920, iosSnap.Hardware.CameraEncoding.Width);
        Assert.Equal(1080, iosSnap.Hardware.CameraEncoding.Height);

        // Verify 'user-ios:cam' NEVER appears as a ghost participant in CallParticipants or ChatState
        Assert.DoesNotContain("user-ios:cam", cluster.ChatState.GetVoiceParticipants("ch-family"));
        foreach (var client in new[] { ios, android, desktop })
        {
            Assert.DoesNotContain("user-ios:cam", client.VoiceService.CallParticipants);
            Assert.Contains("user-ios", client.VoiceService.ActiveCameraParticipants);

            var snap = await client.GetStageSnapshotAsync();
            var iosTile = snap.Tiles["user-ios"];
            Assert.True(iosTile.HasCameraStream);
            Assert.False(iosTile.CameraVideoPaused);
            Assert.Equal("live", iosTile.CameraTrackReadyState);
            Assert.True(iosTile.IsCameraFullTileMode);
            Assert.False(iosTile.IsLargeAvatarVisible);
        }

        // Front camera is mirrored locally on iOS, but unmirrored on remote peers
        Assert.True((await ios.GetStageSnapshotAsync()).Tiles["user-ios"].IsCameraMirrored);
        Assert.False((await android.GetStageSnapshotAsync()).Tiles["user-ios"].IsCameraMirrored);
        Assert.False((await desktop.GetStageSnapshotAsync()).Tiles["user-ios"].IsCameraMirrored);

        // 2. iOS flips camera to rear lens ('ios_back') -> local mirror turns off, stream stays live on all peers
        await ios.DeviceService.SwitchCameraAsync("ios_back", isVideoOn: true);
        await cluster.SettleAsync();

        Assert.False((await ios.GetStageSnapshotAsync()).Tiles["user-ios"].IsCameraMirrored);
        Assert.True((await android.GetStageSnapshotAsync()).Tiles["user-ios"].HasCameraStream);
        Assert.True((await desktop.GetStageSnapshotAsync()).Tiles["user-ios"].HasCameraStream);

        // 3. iOS upgrades camera preset to 4K (2160p @ 60fps) -> updates native encoder & remote video dimensions
        await ios.VoiceService.UpdateVideoSettingsAsync("2160p", 60, "ios_back");
        await cluster.SettleAsync();

        var ios4kSnap = await ios.GetStageSnapshotAsync();
        Assert.NotNull(ios4kSnap.Hardware.CameraEncoding);
        Assert.Equal(12_000_000, ios4kSnap.Hardware.CameraEncoding!.MaxBitrate);
        Assert.Equal(60, ios4kSnap.Hardware.CameraEncoding.MaxFramerate);
        Assert.Equal(3840, ios4kSnap.Hardware.CameraEncoding.Width);
        Assert.Equal(2160, ios4kSnap.Hardware.CameraEncoding.Height);
        Assert.Equal(3840, android.DeviceService.RemoteVideoHeights.GetValueOrDefault("user-ios", 0));
        Assert.Equal(3840, desktop.DeviceService.RemoteVideoHeights.GetValueOrDefault("user-ios", 0));

        // 4. iOS stops camera -> companion disconnects, remote video feeds stop & clear on Android and Desktop
        await ios.VoiceService.ToggleVideoAsync();
        await cluster.SettleAsync();

        Assert.False(ios.VoiceService.IsVideoOn);
        var iosStoppedSnap = await ios.GetStageSnapshotAsync();
        Assert.False(iosStoppedSnap.Hardware.NativeIosCompanionConnected);
        Assert.Equal(0, android.DeviceService.RemoteVideoHeights.GetValueOrDefault("user-ios", 0));
        Assert.Equal(0, desktop.DeviceService.RemoteVideoHeights.GetValueOrDefault("user-ios", 0));

        foreach (var client in new[] { ios, android, desktop })
        {
            Assert.DoesNotContain("user-ios", client.VoiceService.ActiveCameraParticipants);
            var snap = await client.GetStageSnapshotAsync();
            Assert.False(snap.Tiles["user-ios"].HasCameraStream);
            Assert.True(snap.Tiles["user-ios"].IsLargeAvatarVisible);
        }
    }

    [Fact]
    public async Task Cluster_NavigateAwayToChannelList_OpensInAppPipWithLiveLocalAndRemoteVideo_AndRestoresStageOnReturn()
    {
        await using var cluster = await VoiceCallClusterSimulator.CreateAsync();

        var ios = await cluster.CreateClientAsync("ios", "user-ios", VoiceClientPlatform.IosNative);
        var desktop = await cluster.CreateClientAsync("desktop", "user-desktop", VoiceClientPlatform.DesktopElectron);

        await ios.ConnectToChannelAsync("ch-2p");
        await desktop.ConnectToChannelAsync("ch-2p");
        await cluster.SettleAsync();

        // Both enable cameras -> 2-person call automatically enters FaceTime layout focusing the remote peer
        await ios.VoiceService.ToggleVideoAsync("Auto", 30);
        await desktop.VoiceService.ToggleVideoAsync("1080p", 60);
        await cluster.SettleAsync();

        var iosStageSnap = await ios.GetStageSnapshotAsync();
        Assert.Equal(StageLayoutMode.FaceTimeTwoPerson, iosStageSnap.LayoutMode);
        Assert.Equal("user-desktop", iosStageSnap.FocusedParticipantId);
        Assert.False(iosStageSnap.InAppPip.IsOverlayVisible);
        Assert.True(iosStageSnap.Tiles["user-ios"].HasCameraStream);
        Assert.True(iosStageSnap.Tiles["user-desktop"].HasCameraStream);

        // 1. iOS user presses Back to go to the channel list -> call stage unmounts and In-App PiP overlay activates
        await ios.NavigateAwayFromCallStageAsync();
        await cluster.SettleAsync();

        var iosPipSnap = await ios.GetStageSnapshotAsync();
        Assert.Equal(StageLayoutMode.StageUnmountedPipActive, iosPipSnap.LayoutMode);
        Assert.True(iosPipSnap.InAppPip.IsOverlayVisible);
        Assert.Equal("video", iosPipSnap.InAppPip.Mode);

        // Both primary (remote desktop) and self (local iOS) video feeds in PiP must be attached, live, and playing (not black/paused)
        Assert.True(iosPipSnap.InAppPip.PrimaryVideoHasStream);
        Assert.False(iosPipSnap.InAppPip.PrimaryVideoPaused);
        Assert.Equal("live", iosPipSnap.InAppPip.PrimaryVideoTrackReadyState);

        Assert.True(iosPipSnap.InAppPip.SelfVideoHasStream);
        Assert.False(iosPipSnap.InAppPip.SelfVideoPaused);
        Assert.Equal("live", iosPipSnap.InAppPip.SelfVideoTrackReadyState);
        Assert.True(iosPipSnap.InAppPip.SelfVideoMirrored);
        Assert.False(string.IsNullOrWhiteSpace(iosPipSnap.InAppPip.SelfAspectCssVar));

        // 2. iOS user taps the channel / PiP to return to the call stage -> PiP closes and stage video tiles re-attach
        await ios.ReturnToCallStageAsync();
        await cluster.SettleAsync();

        var iosReturnedSnap = await ios.GetStageSnapshotAsync();
        Assert.Equal(StageLayoutMode.FaceTimeTwoPerson, iosReturnedSnap.LayoutMode);
        Assert.False(iosReturnedSnap.InAppPip.IsOverlayVisible);
        Assert.True(iosReturnedSnap.Tiles["user-ios"].HasCameraStream);
        Assert.False(iosReturnedSnap.Tiles["user-ios"].CameraVideoPaused);
        Assert.True(iosReturnedSnap.Tiles["user-desktop"].HasCameraStream);
        Assert.False(iosReturnedSnap.Tiles["user-desktop"].CameraVideoPaused);
    }

    [Fact]
    public async Task Cluster_HardwareDeviceExclusivity_DisableCameraOrDisconnect_ReleasesAllTracks_PreventingNotReadableErrorInSecondTab()
    {
        await using var cluster = await VoiceCallClusterSimulator.CreateAsync();

        // Two browser tabs sharing the exact same physical machine hardware table
        var tab1 = await cluster.CreateClientAsync("tab1", "user-1", VoiceClientPlatform.WebBrowser);
        var tab2 = await cluster.CreateClientAsync("tab2", "user-2", VoiceClientPlatform.WebBrowser, sharedHardwareGroupWithClientId: "tab1");

        await tab1.ConnectToChannelAsync("ch-hw");
        await tab2.ConnectToChannelAsync("ch-hw");
        await cluster.SettleAsync();

        // 1. Tab 1 enables camera -> acquires exclusive hardware lock on 'cam-hd-front'
        await tab1.VoiceService.ToggleVideoAsync("1080p", 60);
        await cluster.SettleAsync();
        Assert.True(tab1.VoiceService.IsVideoOn);
        var tab1Snap = await tab1.GetStageSnapshotAsync();
        Assert.Contains("cam-hd-front", tab1Snap.Hardware.LockedDeviceIds);

        // 2. While Tab 1 holds the camera, Tab 2 tries to open the same camera -> OS throws NotReadableError: Device in use
        await tab2.VoiceService.ToggleVideoAsync("1080p", 60);
        await cluster.SettleAsync();
        Assert.False(tab2.VoiceService.IsVideoOn);

        // 3. Tab 1 disables camera -> must stop MediaStreamTrack and release hardware lock immediately
        await tab1.VoiceService.ToggleVideoAsync();
        await cluster.SettleAsync();
        Assert.False(tab1.VoiceService.IsVideoOn);
        Assert.DoesNotContain("cam-hd-front", (await tab1.GetStageSnapshotAsync()).Hardware.LockedDeviceIds);

        // 4. Now Tab 2 enables camera -> succeeds because Tab 1 released the physical device!
        await tab2.VoiceService.ToggleVideoAsync("1080p", 60);
        await cluster.SettleAsync();
        Assert.True(tab2.VoiceService.IsVideoOn);
        Assert.True((await tab2.GetStageSnapshotAsync()).Tiles["user-2"].HasCameraStream);

        // 5. Tab 2 also starts screen share, then disconnects from voice -> ALL local tracks (mic, cam, screen) must be stopped
        await tab2.VoiceService.ToggleScreenShareAsync("1080p", 60, "motion", true);
        await cluster.SettleAsync();
        Assert.True(tab2.VoiceService.IsScreenSharing);

        await tab2.VoiceService.DisconnectAsync();
        await cluster.SettleAsync();

        var tab2AfterDisconnect = await tab2.GetStageSnapshotAsync();
        Assert.Equal(0, tab2AfterDisconnect.Hardware.LiveLocalTrackCount);
        Assert.DoesNotContain("cam-hd-front", tab2AfterDisconnect.Hardware.LockedDeviceIds);

        // 6. Tab 1 enables camera, then suffers an unexpected SFU disconnect -> tracks are released and Tab 1 can reconnect
        await tab1.VoiceService.ToggleVideoAsync("Auto", 30);
        await cluster.SettleAsync();
        Assert.True(tab1.VoiceService.IsVideoOn);

        await tab1.SimulateUnexpectedSfuDisconnectAsync();
        await cluster.SettleAsync();

        Assert.False(tab1.VoiceService.IsInVoiceCall);
        var tab1AfterDrop = await tab1.GetStageSnapshotAsync();
        Assert.Equal(0, tab1AfterDrop.Hardware.LiveLocalTrackCount);
        Assert.Empty(tab1AfterDrop.Hardware.LockedDeviceIds);

        // Reconnect Tab 1 and turn camera back on -> succeeds cleanly
        await tab1.ConnectToChannelAsync("ch-hw");
        await tab1.VoiceService.ToggleVideoAsync("Auto", 30);
        await cluster.SettleAsync();
        Assert.True(tab1.VoiceService.IsInVoiceCall);
        Assert.True(tab1.VoiceService.IsVideoOn);
        Assert.True((await tab1.GetStageSnapshotAsync()).Tiles["user-1"].HasCameraStream);
    }

    [Fact]
    public async Task Cluster_PresetsAndHardwareEncoding_CameraAndScreenShare_MatchExactBitrateFpsAndDegradationContracts()
    {
        await using var cluster = await VoiceCallClusterSimulator.CreateAsync();

        var android = await cluster.CreateClientAsync("android", "user-android", VoiceClientPlatform.AndroidNative);
        var desktop = await cluster.CreateClientAsync("desktop", "user-desktop", VoiceClientPlatform.DesktopElectron);

        await android.ConnectToChannelAsync("ch-quality");
        await desktop.ConnectToChannelAsync("ch-quality");
        await cluster.SettleAsync();

        // 1. Android Camera Auto preset -> 1080p30 @ 3 Mbps, balanced degradation, single-layer hardware H.264 (simulcast: false on mobile!)
        await android.VoiceService.ToggleVideoAsync("Auto", 30);
        await cluster.SettleAsync();

        var androidAuto = (await android.GetStageSnapshotAsync()).Hardware.CameraEncoding;
        Assert.NotNull(androidAuto);
        Assert.Equal(3_000_000, androidAuto!.MaxBitrate);
        Assert.Equal(30, androidAuto.MaxFramerate);
        Assert.Equal("balanced", androidAuto.DegradationPreference);
        Assert.False(androidAuto.Simulcast);
        Assert.Equal("", androidAuto.ContentHint);
        Assert.Equal(1920, androidAuto.Width);
        Assert.Equal(1080, androidAuto.Height);

        // 2. Android Camera fixed 1080p @ 60fps -> 4.5 Mbps, maintain-resolution, simulcast disabled, contentHint 'detail'
        await android.VoiceService.UpdateVideoSettingsAsync("1080p", 60, "");
        await cluster.SettleAsync();

        var android1080p60 = (await android.GetStageSnapshotAsync()).Hardware.CameraEncoding;
        Assert.NotNull(android1080p60);
        Assert.Equal(4_500_000, android1080p60!.MaxBitrate);
        Assert.Equal(60, android1080p60.MaxFramerate);
        Assert.Equal("maintain-resolution", android1080p60.DegradationPreference);
        Assert.False(android1080p60.Simulcast);
        Assert.Equal("detail", android1080p60.ContentHint);

        // 3. Desktop Camera Auto -> 1080p30 @ 3 Mbps, balanced degradation, simulcast enabled on desktop
        await desktop.VoiceService.ToggleVideoAsync("Auto", 30);
        await cluster.SettleAsync();

        var desktopAutoCam = (await desktop.GetStageSnapshotAsync()).Hardware.CameraEncoding;
        Assert.NotNull(desktopAutoCam);
        Assert.Equal(3_000_000, desktopAutoCam!.MaxBitrate);
        Assert.Equal(30, desktopAutoCam.MaxFramerate);
        Assert.Equal("balanced", desktopAutoCam.DegradationPreference);
        Assert.True(desktopAutoCam.Simulcast);

        // 4. Desktop Camera 4K (2160p @ 60fps) -> 30 Mbps, maintain-resolution, simulcast disabled
        await desktop.VoiceService.UpdateVideoSettingsAsync("2160p", 60, "");
        await cluster.SettleAsync();

        var desktop4kCam = (await desktop.GetStageSnapshotAsync()).Hardware.CameraEncoding;
        Assert.NotNull(desktop4kCam);
        Assert.Equal(30_000_000, desktop4kCam!.MaxBitrate);
        Assert.Equal(60, desktop4kCam.MaxFramerate);
        Assert.Equal("maintain-resolution", desktop4kCam.DegradationPreference);
        Assert.False(desktop4kCam.Simulcast);
        Assert.Equal(3840, desktop4kCam.Width);
        Assert.Equal(2160, desktop4kCam.Height);

        // 5. Desktop Screen Share 1440p @ 60fps Smooth ('motion') -> 24 Mbps, maintain-resolution (explicit resolution locked), contentHint 'motion'
        await desktop.VoiceService.ToggleScreenShareAsync("1440p", 60, "motion", captureAudio: true);
        await cluster.SettleAsync();

        var screen1440pMotion = (await desktop.GetStageSnapshotAsync()).Hardware.ScreenEncoding;
        Assert.NotNull(screen1440pMotion);
        Assert.Equal(24_000_000, screen1440pMotion!.MaxBitrate);
        Assert.Equal(60, screen1440pMotion.MaxFramerate);
        Assert.Equal("maintain-resolution", screen1440pMotion.DegradationPreference);
        Assert.Equal("motion", screen1440pMotion.ContentHint);
        Assert.Equal(2560, screen1440pMotion.Width);
        Assert.Equal(1440, screen1440pMotion.Height);

        // 6. Desktop Screen Share live update to 2160p (4K) @ 30fps Clarity ('detail') -> 25 Mbps, maintain-resolution, contentHint 'detail'
        await desktop.VoiceService.UpdateScreenShareSettingsAsync("2160p", 30, "detail");
        await cluster.SettleAsync();

        var screen4kDetail = (await desktop.GetStageSnapshotAsync()).Hardware.ScreenEncoding;
        Assert.NotNull(screen4kDetail);
        Assert.Equal(25_000_000, screen4kDetail!.MaxBitrate);
        Assert.Equal(30, screen4kDetail.MaxFramerate);
        Assert.Equal("maintain-resolution", screen4kDetail.DegradationPreference);
        Assert.Equal("detail", screen4kDetail.ContentHint);
        Assert.Equal(3840, screen4kDetail.Width);
        Assert.Equal(2160, screen4kDetail.Height);
    }

    [Fact]
    public async Task Cluster_IosCameraCapabilities_Initial1080pEnabled_AndRear4kToFront1080pAutoStepdown_NeverStaysBlack()
    {
        await using var cluster = await VoiceCallClusterSimulator.CreateAsync();

        var ios = await cluster.CreateClientAsync("ios", "user-ios", VoiceClientPlatform.IosNative);
        var desktop = await cluster.CreateClientAsync("desktop", "user-desktop", VoiceClientPlatform.DesktopElectron);

        await ios.ConnectToChannelAsync("ch-ios-caps");
        await desktop.ConnectToChannelAsync("ch-ios-caps");
        await cluster.SettleAsync();

        // 1. iOS starts camera on front lens ('ios_front', max 16:9 video is 1920x1080 @ 60fps)
        await ios.VoiceService.ToggleVideoAsync("Auto", 30);
        await cluster.SettleAsync();

        // Verify 1080p is enabled immediately on first start (MaxCameraHeight >= 1800) and 4K is disabled (< 3000)
        Assert.Equal(1920, ios.UiState.MaxCameraHeight);
        Assert.Equal(60, ios.UiState.MaxCameraFps);

        // 2. Simulate WebRTC <video> element firing loadedmetadata/resize at low BWE ramp-up dimensions (640x360)
        // Verify playback <video> dimensions NEVER overwrite hardware MaxCameraHeight!
        await ios.SimulateVideoElementResizeAsync("user-ios", "camera", 640, 360);
        await cluster.SettleAsync();
        Assert.Equal(1920, ios.UiState.MaxCameraHeight);

        // 3. Switch to rear camera ('ios_back', supports 3840x2160 4K @ 60fps)
        await ios.DeviceService.SwitchCameraAsync("ios_back", isVideoOn: true);
        await cluster.SettleAsync();
        Assert.Equal(3840, ios.UiState.MaxCameraHeight);
        Assert.Equal(60, ios.UiState.MaxCameraFps);

        // 4. Enable 4K (2160p @ 60fps) on rear camera
        ios.UiState.MaxVideoSendQuality = "2160p";
        ios.UiState.MaxVideoSendFps = 60;
        await ios.VoiceService.UpdateVideoSettingsAsync("2160p", 60, "ios_back");
        await cluster.SettleAsync();

        var rear4kSnap = await ios.GetStageSnapshotAsync();
        Assert.NotNull(rear4kSnap.Hardware.CameraEncoding);
        Assert.Equal(3840, rear4kSnap.Hardware.CameraEncoding!.Width);
        Assert.Equal(2160, rear4kSnap.Hardware.CameraEncoding.Height);

        // 5. Switch back to front camera ('ios_front', max 1920x1080) while 4K ('2160p') was selected
        // Must automatically step down to 1080p so the front camera feed stays live and never goes black!
        await ios.DeviceService.SwitchCameraAsync("ios_front", isVideoOn: true);
        await cluster.SettleAsync();

        Assert.Equal(1920, ios.UiState.MaxCameraHeight);
        Assert.Equal("1080p", ios.UiState.MaxVideoSendQuality);

        var frontStepdownSnap = await ios.GetStageSnapshotAsync();
        Assert.NotNull(frontStepdownSnap.Hardware.CameraEncoding);
        Assert.Equal(1920, frontStepdownSnap.Hardware.CameraEncoding!.Width);
        Assert.Equal(1080, frontStepdownSnap.Hardware.CameraEncoding.Height);
        Assert.True(frontStepdownSnap.Tiles["user-ios"].HasCameraStream);
        Assert.False(frontStepdownSnap.Tiles["user-ios"].CameraVideoPaused);
        Assert.Equal("live", frontStepdownSnap.Tiles["user-ios"].CameraTrackReadyState);
        Assert.True((await desktop.GetStageSnapshotAsync()).Tiles["user-ios"].HasCameraStream);
    }

    [Fact]
    public async Task Cluster_PipAndStageCoherence_AllUsersGrid_BottomFilmstrip_FocusSwitchDuringStream_AndUnifiedStreamerWebcamOverlay()
    {
        await using var cluster = await VoiceCallClusterSimulator.CreateAsync();

        var desktop = await cluster.CreateClientAsync("desktop", "user-desktop", VoiceClientPlatform.DesktopElectron);
        var streamer = await cluster.CreateClientAsync("streamer", "user-streamer", VoiceClientPlatform.DesktopElectron);
        var peer = await cluster.CreateClientAsync("peer", "user-peer", VoiceClientPlatform.WebBrowser);

        await desktop.ConnectToChannelAsync("ch-coherence");
        await streamer.ConnectToChannelAsync("ch-coherence");
        await peer.ConnectToChannelAsync("ch-coherence");
        await cluster.SettleAsync();

        // Streamer starts screen share AND enables webcam; peer enables webcam; desktop enables webcam
        await streamer.VoiceService.ToggleScreenShareAsync("1440p", 60, "motion", captureAudio: true);
        await streamer.VoiceService.ToggleVideoAsync("1080p", 60);
        await peer.VoiceService.ToggleVideoAsync("1080p", 30);
        await desktop.VoiceService.ToggleVideoAsync("Auto", 30);
        await cluster.SettleAsync();

        // 1. In a 3-person call with no focused participant, stage starts in All-Users Grid mode
        var initialStage = await desktop.GetStageSnapshotAsync();
        Assert.Equal(StageLayoutMode.Grid, initialStage.LayoutMode);
        Assert.Null(initialStage.FocusedParticipantId);

        // 2. Desktop navigates away to In-App PiP while in All-Users Grid mode -> PiP preserves All-Users Grid mode!
        await desktop.NavigateAwayFromCallStageAsync();
        await cluster.SettleAsync();

        var gridPipSnap = await desktop.GetStageSnapshotAsync();
        Assert.True(gridPipSnap.InAppPip.IsOverlayVisible);
        Assert.Equal("grid", gridPipSnap.InAppPip.LayoutMode);
        Assert.Equal("grid", gridPipSnap.InAppPip.Mode);
        Assert.Equal(3, gridPipSnap.InAppPip.GridTileParticipantIds.Count);
        Assert.Contains("user-streamer", gridPipSnap.InAppPip.GridTileParticipantIds);
        Assert.Contains("user-peer", gridPipSnap.InAppPip.GridTileParticipantIds);
        Assert.Contains("user-desktop", gridPipSnap.InAppPip.GridTileParticipantIds);

        // 3. Desktop clicks 'user-streamer' tile in PiP Grid -> switches to Focused mode on streamer
        // Streamer's screen share is primary AND streamer's webcam is overlaid (PresenterCamVideoHasStream == true),
        // local user's own camera is in top-right self preview, and 'user-peer' is visible in the bottom filmstrip!
        await desktop.ClickPipParticipantTileAsync("user-streamer", inFilmstrip: false);
        await cluster.SettleAsync();

        var focusedStreamerPip = await desktop.GetStageSnapshotAsync();
        Assert.Equal("user-streamer", desktop.UiState.FocusedParticipantId);
        Assert.Equal("focus", focusedStreamerPip.InAppPip.LayoutMode);
        Assert.True(focusedStreamerPip.InAppPip.PrimaryVideoHasStream);
        Assert.Equal("user-streamer", focusedStreamerPip.InAppPip.PrimaryTrackParticipantId);
        Assert.Equal("screenshare", focusedStreamerPip.InAppPip.PrimaryTrackSource);
        Assert.True(focusedStreamerPip.InAppPip.PresenterCamVideoHasStream);
        Assert.Equal("user-streamer", focusedStreamerPip.InAppPip.PresenterCamParticipantId);
        Assert.True(focusedStreamerPip.InAppPip.SelfVideoHasStream);
        Assert.Contains("user-peer", focusedStreamerPip.InAppPip.FilmstripTileParticipantIds);

        // 4. While 'user-streamer' is STILL streaming, desktop clicks 'user-peer' in the PiP bottom filmstrip
        // PiP must switch focus to 'user-peer' camera (never locked onto the stream) and move 'user-streamer' to the bottom filmstrip!
        await desktop.ClickPipParticipantTileAsync("user-peer", inFilmstrip: true);
        await cluster.SettleAsync();

        var focusedPeerPip = await desktop.GetStageSnapshotAsync();
        Assert.Equal("user-peer", desktop.UiState.FocusedParticipantId);
        Assert.Equal("focus", focusedPeerPip.InAppPip.LayoutMode);
        Assert.True(focusedPeerPip.InAppPip.PrimaryVideoHasStream);
        Assert.Equal("user-peer", focusedPeerPip.InAppPip.PrimaryTrackParticipantId);
        Assert.Equal("camera", focusedPeerPip.InAppPip.PrimaryTrackSource);
        Assert.False(focusedPeerPip.InAppPip.PresenterCamVideoHasStream);
        Assert.True(focusedPeerPip.InAppPip.SelfVideoHasStream);
        Assert.Contains("user-streamer", focusedPeerPip.InAppPip.FilmstripTileParticipantIds);

        // 5. Desktop returns to the in-app call stage -> stage preserves focus on 'user-peer' seamlessly!
        await desktop.ReturnToCallStageAsync();
        await cluster.SettleAsync();

        var returnedStage = await desktop.GetStageSnapshotAsync();
        Assert.Equal(StageLayoutMode.FocusedParticipant, returnedStage.LayoutMode);
        Assert.Equal("user-peer", returnedStage.FocusedParticipantId);
    }
}
