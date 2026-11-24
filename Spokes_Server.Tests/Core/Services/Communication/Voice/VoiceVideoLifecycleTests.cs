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
    private readonly VoiceStateService _voiceState;
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

        _voiceState = new VoiceStateService();

        _voiceChannelServiceMock = new Mock<VoiceChannelService>();
        _voiceChannelServiceMock.Setup(x => x.JoinVoiceAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, CancellationToken>((channelId, userId, _) => _voiceState.NotifyVoiceMemberJoined(channelId, userId))
            .Returns(Task.CompletedTask);
        _voiceChannelServiceMock.Setup(x => x.LeaveVoiceAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, CancellationToken>((channelId, userId, _) => _voiceState.NotifyVoiceMemberLeft(channelId, userId))
            .Returns(Task.CompletedTask);

        var jsInterop = new VoiceJsInteropService(_jsMock.Object);
        _deviceService = new VoiceDeviceService(jsInterop, NullLogger<VoiceDeviceService>.Instance);
        _service = new GlobalVoiceService(
            jsInterop,
            _voiceChannelServiceMock.Object,
            _voiceState,
            new ChatStateService(),
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
        _voiceState.NotifyVoiceMemberJoined("ch-voice", "user-remote");
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
        _voiceState.NotifyVoiceMemberJoined("ch-voice", "user-remote");
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
        var tempDir = Path.Combine(Path.GetTempPath(), $"spokes_voice_js_test_{Guid.NewGuid():N}");
        try
        {
            const string mockVoicePipJs = """
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
                export function isNativeMobilePipActive() { return false; }
                """;
            await VoiceJsTestModuleLoader.StageStandaloneAsync(tempDir, mockVoicePipJs);

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
                    TrackUnmuted: 'trackUnmuted',
                    Reconnected: 'reconnected'
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
                    VideoQuality: { LOW: 'low', MEDIUM: 'medium', HIGH: 'high' },
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

                // 5. Simulate remote camera ('user-remote') subscribing & unsubscribing WHILE local camera is on
                const remoteCamTrack = createMockLivekitTrack('video', Track.Source.Camera);
                const remoteCamPub = { trackSid: remoteCamTrack.sid, kind: 'video', source: Track.Source.Camera, track: remoteCamTrack, isSubscribed: true };
                const remoteParticipant = {
                    identity: 'user-remote',
                    videoTrackPublications: new Map([[remoteCamTrack.sid, remoteCamPub]])
                };
                activeRoomInstance.remoteParticipants.set('user-remote', remoteParticipant);
                activeRoomInstance.emit(RoomEvent.TrackSubscribed, remoteCamTrack, remoteCamPub, remoteParticipant);
                await new Promise(r => setTimeout(r, 30));

                const remoteCamEl = domById.get('participant-video-user-remote');
                assert.equal(remoteCamEl.classList.contains('has-stream'), true, 'Remote camera should attach to participant-video-user-remote');

                // Unsubscribe remote camera while local camera is still enabled (_localCameraDesired === true)
                remoteParticipant.videoTrackPublications.clear();
                activeRoomInstance.emit(RoomEvent.TrackUnsubscribed, remoteCamTrack, remoteCamPub, remoteParticipant);
                assert.equal(remoteCamEl.classList.contains('has-stream'), false, 'Remote camera unsubscribe must clear remote video DOM even when local camera is on');

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

                // 7a. Verify automatic video-only fallback when getDisplayMedia/createLocalScreenTracks fails audio source init (e.g. WASAPI 0x88890008 NotReadableError)
                let screenTrackCallArgs = [];
                const origCreateScreenTracks = globalThis.LivekitClient.createLocalScreenTracks;
                globalThis.LivekitClient.createLocalScreenTracks = async (opts) => {
                    screenTrackCallArgs.push(opts);
                    if (opts && opts.audio) {
                        const err = new Error('Could not start audio source');
                        err.name = 'NotReadableError';
                        throw err;
                    }
                    return [createMockLivekitTrack('video', Track.Source.ScreenShare)];
                };
                await voice.setScreenShareEnabled(true, '1080p', 60, 'motion', true);
                assert.equal(screenTrackCallArgs.length, 2, 'Should retry createLocalScreenTracks with audio: false after NotReadableError');
                assert.equal(screenTrackCallArgs[0].audio, true);
                assert.equal(screenTrackCallArgs[1].audio, false);
                assert.equal(localScreenEl.classList.contains('has-stream'), true, 'Screen share video must still succeed when audio loopback fails');
                await voice.setScreenShareEnabled(false);

                // 7a-2. Verify Desktop Native WASAPI Stereo Gameplay Loopback delegates cleanly to
                // Spokes_Desktop's single canonical preload bridge (_spokesCreateDesktopNativeLoopbackTrack / _spokesStopDesktopNativeLoopback)
                let nativeLoopbackStarted = 0;
                let nativeLoopbackStopped = 0;
                globalThis.LivekitClient.createLocalScreenTracks = async () => [
                    createMockLivekitTrack('video', Track.Source.ScreenShare)
                ];
                globalThis.window._spokesLastPickerAudioSelection = { shareAudio: true, useNativeLoopback: true };
                globalThis.window._spokesCreateDesktopNativeLoopbackTrack = async () => {
                    nativeLoopbackStarted++;
                    return createMockMediaStreamTrack('audio', 'WASAPI Native Loopback');
                };
                globalThis.window._spokesStopDesktopNativeLoopback = () => {
                    nativeLoopbackStopped++;
                };
                globalThis.window.SpokesBridge = {
                    platform: 'desktop',
                    isDesktop: true,
                    screenShareAudio: {
                        async setPreferredShareAudio() {}
                    }
                };
                await voice.setScreenShareEnabled(true, '1440p', 60, 'motion', true);
                assert.equal(nativeLoopbackStarted, 1, 'Desktop screen share with audio selected must delegate to _spokesCreateDesktopNativeLoopbackTrack');
                const screenAudioPubDesktop = Array.from(activeRoomInstance.localParticipant.audioTrackPublications.values())
                    .find(p => p.source === Track.Source.ScreenShareAudio);
                assert.ok(screenAudioPubDesktop, 'Native desktop loopback audio track must be published as ScreenShareAudio');
                // Toggling screen share audio off mid-stream must also stop native WASAPI loopback capture (preventing idle CPU/IPC leak)
                await voice.setScreenShareAudioEnabled(false);
                assert.ok(nativeLoopbackStopped >= 1, 'setScreenShareAudioEnabled(false) must stop native WASAPI loopback');
                const stoppedBeforeEnd = nativeLoopbackStopped;
                await voice.setScreenShareEnabled(false);
                assert.ok(nativeLoopbackStopped > stoppedBeforeEnd, 'Stopping screen share must stop native WASAPI loopback');
                delete globalThis.window._spokesLastPickerAudioSelection;
                delete globalThis.window._spokesCreateDesktopNativeLoopbackTrack;
                delete globalThis.window._spokesStopDesktopNativeLoopback;
                delete globalThis.window.SpokesBridge;
                globalThis.LivekitClient.createLocalScreenTracks = origCreateScreenTracks;

                // 7a-3. Verify stop -> re-share resilience when publishTrack transiently throws 'Cancelled publication by calling unpublish' during LiveKit ICE restart
                let publishAttemptCount = 0;
                const origPublishTrack = activeRoomInstance.localParticipant.publishTrack.bind(activeRoomInstance.localParticipant);
                activeRoomInstance.localParticipant.publishTrack = async (track, opts) => {
                    publishAttemptCount++;
                    if (publishAttemptCount === 1) {
                        activeRoomInstance.state = 'reconnecting';
                        setTimeout(() => {
                            activeRoomInstance.state = 'connected';
                        }, 60);
                        throw new Error('Cancelled publication by calling unpublish');
                    }
                    return await origPublishTrack(track, opts);
                };
                await voice.setScreenShareEnabled(true, '1080p', 30, 'motion', false);
                assert.equal(publishAttemptCount, 3, 'publishLocalTrackResilient must wait for room reconnect, retry videoTrack (2 attempts), and publish audioTrack (1 attempt)');
                assert.equal(localScreenEl.classList.contains('has-stream'), true, 'Screen share must attach and succeed after transient reconnect retry');
                activeRoomInstance.localParticipant.publishTrack = origPublishTrack;
                await voice.setScreenShareEnabled(false);

                // 7b. RoomEvent.Reconnected and connectToVoice must sync full VoiceRoomSnapshot payloads
                assert.ok(
                    dotnetCalls.some(c => c.method === 'SyncRoomSnapshotAsync' && c.args[0] && c.args[0].localUserId === 'user-local'),
                    'connectToVoice must publish an initial VoiceRoomSnapshotAsync payload'
                );
                const callsBeforeReconnect = dotnetCalls.length;
                activeRoomInstance.emit(RoomEvent.Reconnected);
                const reconnectCall = dotnetCalls.slice(callsBeforeReconnect).find(c => c.method === 'OnRoomReconnectedAsync');
                assert.ok(reconnectCall && reconnectCall.args[0]?.localUserId === 'user-local', 'RoomEvent.Reconnected must invoke OnRoomReconnectedAsync with a snapshot');

                // 7c. When LiveKit reconnects while the Blazor circuit is disconnected (_blazorDisconnected = true),
                // OnRoomReconnectedAsync must be queued and flushed once the circuit is restored.
                const callsBeforeOfflineReconnect = dotnetCalls.length;
                globalThis.window._blazorDisconnected = true;
                activeRoomInstance.emit(RoomEvent.Reconnected);
                assert.equal(
                    dotnetCalls.slice(callsBeforeOfflineReconnect).filter(c => c.method === 'OnRoomReconnectedAsync').length,
                    0,
                    'OnRoomReconnectedAsync must not be invoked while _blazorDisconnected is true'
                );
                globalThis.window._blazorDisconnected = false;
                assert.equal(voice.isRoomConnected(), true, 'isRoomConnected() should return true while connected');
                const flushedReconnectCall = dotnetCalls.slice(callsBeforeOfflineReconnect).find(c => c.method === 'OnRoomReconnectedAsync');
                assert.ok(
                    flushedReconnectCall && flushedReconnectCall.args[0]?.localUserId === 'user-local',
                    'Queued OnRoomReconnectedAsync snapshot must flush automatically when circuit is restored'
                );

                // 7d. prepareVoiceConnection must skip redundant same-origin tokenless HEAD prewarm (avoiding 405 on GET /)
                // while still warming cross-origin LiveKit hosts.
                let preparedUrls = [];
                MockRoom.prototype.prepareConnection = async function(url, token) {
                    preparedUrls.push({ url, token });
                };
                globalThis.window.location = { host: 'spokes.elnuage.ca', href: 'https://spokes.elnuage.ca/' };
                await voice.prepareVoiceConnection('wss://spokes.elnuage.ca', '');
                assert.equal(preparedUrls.length, 0, 'Same-origin tokenless prepareVoiceConnection must not call Room.prepareConnection');
                await voice.prepareVoiceConnection('wss://livekit.external.test', '');
                assert.equal(preparedUrls.length, 1, 'Cross-origin prepareVoiceConnection must call Room.prepareConnection');

                // 7e. During LiveKit full reconnect (handleRestarting emitting TrackUnsubscribed + ParticipantDisconnected
                // while room.state is 'signalReconnecting' / engine.attemptingReconnect is true), voiceInterop must NOT
                // send synthetic OnParticipantDisconnected or OnVideoTrackStateChanged(..., false) to C#.
                const callsBeforeRestarting = dotnetCalls.length;
                activeRoomInstance.state = 'signalReconnecting';
                activeRoomInstance.engine = { attemptingReconnect: true };
                const restartingCamTrack = createMockLivekitTrack('video', Track.Source.Camera);
                const restartingCamPub = { trackSid: restartingCamTrack.sid, kind: 'video', source: Track.Source.Camera, track: restartingCamTrack, isSubscribed: true };
                const restartingPeer = {
                    identity: 'user-remote',
                    videoTrackPublications: new Map([[restartingCamTrack.sid, restartingCamPub]]),
                    audioTrackPublications: new Map()
                };
                activeRoomInstance.emit(RoomEvent.TrackUnsubscribed, restartingCamTrack, restartingCamPub, restartingPeer);
                activeRoomInstance.emit(RoomEvent.ParticipantDisconnected, restartingPeer);
                const restartingCalls = dotnetCalls.slice(callsBeforeRestarting);
                assert.equal(
                    restartingCalls.some(c => c.method === 'OnParticipantDisconnected' || (c.method === 'OnVideoTrackStateChanged' && c.args[2] === false)),
                    false,
                    'Synthetic TrackUnsubscribed/ParticipantDisconnected during LiveKit reconnect must not wipe C# participant or video state'
                );
                activeRoomInstance.state = 'connected';
                activeRoomInstance.engine.attemptingReconnect = false;
                activeRoomInstance.emit(RoomEvent.Reconnected);
                restartingCamTrack.stop();

                // 8. Disconnect from voice -> ALL local MediaStreamTracks (mic, camera, screen) must be stopped (readyState === 'ended')
                await voice.disconnectFromVoice();
                const liveLocalTracks = createdMediaTracks.filter(t => t !== remoteCamTrack.mediaStreamTrack && t.readyState !== 'ended');
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
    public async Task VoiceInteropJs_PhysicalRoomDeterministicRouting_AndAndroidPreConnectTelecom_SucceedsOnFirstJoin()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"spokes_voice_routing_test_{Guid.NewGuid():N}");
        try
        {
            const string mockVoicePipJs = """
                export function configureVoicePip() {}
                export function registerPipVideoTrack() {}
                export function unregisterPipVideoTrack() {}
                export function updatePipSpeakingParticipants() {}
                export function updatePipCallState() {}
                export function resetPipOnCallEnd() {}
                export function setInAppPipActive() {}
                export function syncInAppPipElements() {}
                export async function toggleOsPip() { return false; }
                export function closeOsPip() {}
                export function isOsPipSupported() { return false; }
                export function isStageSuppressedByExternalPip() { return false; }
                export function isNativeMobilePipActive() { return false; }
                """;
            await VoiceJsTestModuleLoader.StageStandaloneAsync(tempDir, mockVoicePipJs);

            var runnerScript = """
                import assert from 'node:assert/strict';

                const timeline = [];
                const createdAudioEls = [];

                function createAudioTrack(sid) {
                    const mst = {
                        id: `mst-${sid}`,
                        kind: 'audio',
                        readyState: 'live',
                        enabled: true,
                        stop() { this.readyState = 'ended'; },
                        getSettings() { return { deviceId: 'mic-1' }; },
                        addEventListener() {},
                        removeEventListener() {}
                    };
                    return {
                        sid,
                        kind: 'audio',
                        mediaStreamTrack: mst,
                        attach() {
                            let currentSrcObj = { id: mst.id };
                            const el = {
                                tagName: 'AUDIO',
                                playCalls: 0,
                                pauseCalls: 0,
                                srcObjectSets: 0,
                                get srcObject() { return currentSrcObj; },
                                set srcObject(v) { this.srcObjectSets++; currentSrcObj = v; },
                                style: {},
                                remove() {},
                                pause() { this.pauseCalls++; },
                                async play() { this.playCalls++; }
                            };
                            createdAudioEls.push({ sid, el });
                            return el;
                        },
                        detach() { return []; },
                        setAudioContext() {},
                        async setProcessor() {},
                        stop() { mst.stop(); }
                    };
                }

                function createRemoteAudioPub(sid, trackName, track) {
                    return {
                        trackSid: sid,
                        trackName,
                        kind: 'audio',
                        source: 'microphone',
                        isSubscribed: true,
                        track,
                        audioTrack: track,
                        subHistory: [],
                        setSubscribed(val) {
                            this.subHistory.push(val);
                            this.isSubscribed = val;
                        }
                    };
                }

                globalThis.localStorage = {
                    _map: new Map(),
                    getItem(k) { return this._map.get(k) ?? null; },
                    setItem(k, v) { this._map.set(k, String(v)); }
                };
                Object.defineProperty(globalThis, 'navigator', {
                    configurable: true,
                    writable: true,
                    value: {
                        userAgent: 'Mozilla/5.0 (Linux; Android 14; Pixel 8) AppleWebKit/537.36 Chrome/131.0.0.0 Mobile Safari/537.36',
                        mediaDevices: {
                            async enumerateDevices() {
                                return [{ kind: 'audioinput', deviceId: 'mic-1', label: 'Phone Mic' }];
                            }
                        }
                    }
                });
                const appendedEls = new Set();
                globalThis.document = {
                    visibilityState: 'visible',
                    body: {
                        appendChild(el) { appendedEls.add(el); return el; }
                    },
                    getElementById() { return null; },
                    querySelectorAll() { return []; },
                    addEventListener() {},
                    removeEventListener() {}
                };

                let telecomListener = null;
                globalThis.Capacitor = {
                    isNativePlatform: () => true,
                    getPlatform: () => 'android',
                    Plugins: {
                        KeepAwake: {
                            async keepAwake() { timeline.push('keepAwake'); },
                            async allowSleep() {}
                        },
                        TelecomConnectionPlugin: {
                            async startCall() {
                                timeline.push('telecom.startCall');
                            },
                            async endCall() {
                                timeline.push('telecom.endCall');
                            },
                            async addListener(evt, cb) {
                                if (evt === 'onAudioStateChanged') telecomListener = cb;
                                return { remove: async () => {} };
                            }
                        }
                    }
                };

                let directStatusCb = null;
                globalThis.SpokesBridge = {
                    directRoomAudio: {
                        async isSupported() { return true; },
                        async configureSession() { return null; },
                        async updateControls() {},
                        async stop() {},
                        onStatusChanged(cb) {
                            directStatusCb = cb;
                            return () => { directStatusCb = null; };
                        }
                    }
                };

                const rawPcTrack = createAudioTrack('TR_RAW_PCA');
                const procPcTrack = createAudioTrack('TR_PROC_PCA');
                const rawPcPub = createRemoteAudioPub('TR_RAW_PCA', 'microphone', rawPcTrack);
                const procPcPub = createRemoteAudioPub('TR_PROC_PCA', 'processed-mic:pc-a', procPcTrack);

                const RoomEvent = {
                    TrackSubscribed: 'TrackSubscribed',
                    TrackUnsubscribed: 'TrackUnsubscribed',
                    TrackPublished: 'TrackPublished',
                    TrackUnpublished: 'TrackUnpublished',
                    ParticipantConnected: 'ParticipantConnected',
                    ParticipantDisconnected: 'ParticipantDisconnected',
                    ActiveSpeakersChanged: 'ActiveSpeakersChanged',
                    Disconnected: 'Disconnected',
                    TrackMuted: 'TrackMuted',
                    TrackUnmuted: 'TrackUnmuted',
                    LocalTrackPublished: 'LocalTrackPublished',
                    LocalTrackUnpublished: 'LocalTrackUnpublished'
                };
                const Track = {
                    Kind: { Audio: 'audio', Video: 'video' },
                    Source: { Microphone: 'microphone', Camera: 'camera', ScreenShare: 'screen_share', ScreenShareAudio: 'screen_share_audio' }
                };

                class MockRoom {
                    constructor() {
                        this.handlers = new Map();
                        this.remoteParticipants = new Map();
                        this.localParticipant = {
                            identity: 'android-user',
                            videoTrackPublications: new Map(),
                            audioTrackPublications: new Map(),
                            async publishTrack(t) { return { trackSid: t.sid, kind: 'audio', track: t }; },
                            async unpublishTrack() {}
                        };
                    }
                    on(evt, fn) { this.handlers.set(evt, fn); return this; }
                    emit(evt, ...args) { const fn = this.handlers.get(evt); if (fn) fn(...args); }
                    async startAudio() {}
                    async connect() {
                        timeline.push('room.connect');
                        const pcParticipant = {
                            identity: 'pc-a',
                            audioTrackPublications: new Map([['TR_RAW_PCA', rawPcPub]]),
                            videoTrackPublications: new Map(),
                            trackPublications: new Map([['TR_RAW_PCA', rawPcPub]])
                        };
                        const botParticipant = {
                            identity: 'bot:chan1',
                            audioTrackPublications: new Map([['TR_PROC_PCA', procPcPub]]),
                            videoTrackPublications: new Map(),
                            trackPublications: new Map([['TR_PROC_PCA', procPcPub]])
                        };
                        this.remoteParticipants.set('pc-a', pcParticipant);
                        this.remoteParticipants.set('bot:chan1', botParticipant);
                    }
                    async disconnect() { this.emit(RoomEvent.Disconnected); }
                    static async getLocalDevices() { return [{ deviceId: 'mic-1', label: 'Phone Mic' }]; }
                }

                globalThis.window = globalThis;
                globalThis.CustomEvent = class CustomEvent { constructor(name, opts) { this.type = name; this.detail = opts?.detail; } };
                globalThis.dispatchEvent = () => {};
                globalThis.addEventListener = () => {};
                let createLocalAudioTrackCalls = 0;
                globalThis.LivekitClient = {
                    Room: MockRoom,
                    RoomEvent,
                    Track,
                    createLocalAudioTrack: async () => {
                        createLocalAudioTrackCalls++;
                        return createAudioTrack('TR_LOCAL_MIC');
                    }
                };

                const voice = await import('./voiceInterop.js');
                const stageEvents = [];
                const dotnetHelper = {
                    invokeMethodAsync: async (method, ...args) => {
                        if (method === 'OnVoiceConnectingStageChanged') stageEvents.push(args[0]);
                        return '';
                    }
                };

                // 0. Verify combined getClientVoiceBootstrap() single-roundtrip helper
                localStorage.setItem('spokes_noise_filter', 'rnnoise');
                const bootstrap = await voice.getClientVoiceBootstrap();
                assert.equal(bootstrap.isAndroid, true, 'Bootstrap should detect Android');
                assert.equal(bootstrap.isNativePlatform, true, 'Bootstrap should detect Capacitor native');
                assert.equal(bootstrap.savedNoiseFilter, 'rnnoise', 'Bootstrap should return saved noise filter');
                assert.ok(bootstrap.deviceId && bootstrap.deviceId.length > 0, 'Bootstrap should create/return deviceId');

                // 1. Pass VoicePreJoinContext directly into connectToVoice (matching GlobalVoiceService.ConnectAsync)
                const connected = await voice.connectToVoice('tok', 'ws://lk', dotnetHelper, 64, 'rnnoise', {
                    settingsJson: JSON.stringify({ MasterVolume: 100, ParticipantVolumes: {} }),
                    coLocatedUserIds: ['pc-a', 'pc-b'],
                    mutedRoommateUserIds: [],
                    preferredMicDeviceId: '',
                    initialMuted: false
                });
                assert.equal(connected, true, 'connectToVoice should succeed on first attempt');
                assert.deepEqual(
                    stageEvents,
                    ['connecting', 'microphone'],
                    'connectToVoice must emit live connecting stages in order'
                );

                // 2. Verify Android Telecom ConnectionService started BEFORE room.connect()!
                const telecomIdx = timeline.indexOf('telecom.startCall');
                const connectIdx = timeline.indexOf('room.connect');
                assert.ok(telecomIdx >= 0, 'TelecomConnectionPlugin.startCall must be invoked');
                assert.ok(telecomIdx < connectIdx, `Expected telecom.startCall (${telecomIdx}) BEFORE room.connect (${connectIdx})`);

                // 3. Verify deterministic first-join routing: raw-mic unsubscribed, processed-mic:pc-a subscribed & playing!
                assert.equal(rawPcPub.isSubscribed, false, 'Co-located PC raw mic must be unsubscribed immediately on join');
                assert.equal(procPcPub.isSubscribed, true, 'Processed room mic must be subscribed immediately on join');
                const procAttached = createdAudioEls.find(x => x.sid === 'TR_PROC_PCA');
                assert.ok(procAttached, 'Processed room mic <audio> element must be attached on first join without reconnect');
                assert.ok(procAttached.el.playCalls >= 1, 'Explicit element.play() must be called on mobile <audio> element');

                // 4. Verify Android Telecom's real two-step initial route settlement (EARPIECE -> SPEAKER 30ms later)
                // updates active speaker route cleanly WITHOUT clobbering srcObject or tearing down getUserMedia!
                assert.ok(telecomListener, 'onAudioStateChanged listener should be registered');
                telecomListener({ state: { route: 'earpiece', hasBluetooth: false, hasWiredHeadset: false, hasEarpiece: true } });
                assert.equal(await voice.getActiveSpeakerId(), 'earpiece', 'Active speaker should track Telecom route');
                await new Promise(r => setTimeout(r, 30));
                telecomListener({ state: { route: 'speaker', hasBluetooth: false, hasWiredHeadset: false, hasEarpiece: true } });
                assert.equal(await voice.getActiveSpeakerId(), 'speaker', 'Active speaker should update cleanly to speaker');
                await new Promise(r => setTimeout(r, 50));
                assert.equal(procAttached.el.srcObjectSets, 0, 'Initial Telecom EARPIECE->SPEAKER settlement must NOT reset audioEl.srcObject');
                assert.equal(createLocalAudioTrackCalls, 1, 'Initial Telecom EARPIECE->SPEAKER settlement must NOT tear down & recreate local mic');

                // 5. Verify Direct LAN override (on Spokes Desktop) cleanly suppresses WebRTC tracks and seamless fallback restores processed-mic!
                globalThis.Capacitor = undefined;
                await voice.syncDirectRoomSession({
                    channelId: 'chan1',
                    roomId: 'room1',
                    audioMode: 1,
                    keyEpoch: 1,
                    sessionKeyBase64: 'AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=',
                    localPeerToken: 100,
                    peers: [{ userId: 'pc-a', peerToken: 101, endpoints: ['192.168.1.10:53000'] }]
                }, false);
                directStatusCb({ activePeerTokens: [101], estimatedEndToEndMs: 18.4, maxPeerRttMs: 1.2, jitterBufferMs: 4.0, achievedSuppressionDb: 22.0 });
                assert.equal(rawPcPub.isSubscribed, false);
                assert.equal(procPcPub.isSubscribed, false, 'Direct LAN active must suppress processed-mic WebRTC track');

                directStatusCb({ activePeerTokens: [], estimatedEndToEndMs: 0, maxPeerRttMs: 0, jitterBufferMs: 0, achievedSuppressionDb: 0 });
                assert.equal(procPcPub.isSubscribed, true, 'Clearing Direct LAN peers (fallback) must immediately restore processed-mic subscription');
                assert.equal(rawPcPub.isSubscribed, false, 'Raw mic remains suppressed while pc-a is in co-located room');

                await voice.disconnectFromVoice();
                console.log('VOICE_ROUTING_AND_TELECOM_OK');
                """;

            await File.WriteAllTextAsync(Path.Combine(tempDir, "runner_routing.mjs"), runnerScript, Encoding.UTF8);

            var psi = new ProcessStartInfo
            {
                FileName = "node",
                Arguments = "runner_routing.mjs",
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

            Assert.True(proc.ExitCode == 0, $"Node JS routing test failed (exit {proc.ExitCode}):\nSTDOUT:\n{stdout}\nSTDERR:\n{stderr}");
            Assert.Contains("VOICE_ROUTING_AND_TELECOM_OK", stdout);
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

        // 1. iOS enables camera (Auto preset -> 1080p30 @ 8.0 Mbps, balanced degradation, single-layer hardware H.264 directly on primary participant 'user-ios', zero ':cam' companion)
        await ios.VoiceService.ToggleVideoAsync("Auto", 30);
        await cluster.SettleAsync();

        Assert.True(ios.VoiceService.IsVideoOn);
        var iosSnap = await ios.GetStageSnapshotAsync();
        Assert.False(iosSnap.Hardware.NativeIosCompanionConnected);
        Assert.NotNull(iosSnap.Hardware.CameraEncoding);
        Assert.Equal(8_000_000, iosSnap.Hardware.CameraEncoding!.MaxBitrate);
        Assert.Equal("balanced", iosSnap.Hardware.CameraEncoding.DegradationPreference);
        Assert.False(iosSnap.Hardware.CameraEncoding.Simulcast);
        Assert.Equal(1920, iosSnap.Hardware.CameraEncoding.Width);
        Assert.Equal(1080, iosSnap.Hardware.CameraEncoding.Height);

        // Verify 'user-ios:cam' NEVER appears as a ghost participant in CallParticipants or ChatState
        Assert.DoesNotContain("user-ios:cam", cluster.VoiceState.GetVoiceParticipants("ch-family"));
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

        // 3. iOS upgrades camera preset to 1080p60 then 4K (2160p @ 60fps) -> cleanly republishes fresh track & streams to all peers
        await ios.VoiceService.UpdateVideoSettingsAsync("1080p", 60, "ios_back");
        await cluster.SettleAsync();

        var ios1080p60Snap = await ios.GetStageSnapshotAsync();
        Assert.NotNull(ios1080p60Snap.Hardware.CameraEncoding);
        Assert.Equal(12_000_000, ios1080p60Snap.Hardware.CameraEncoding!.MaxBitrate);
        Assert.Equal(60, ios1080p60Snap.Hardware.CameraEncoding.MaxFramerate);
        Assert.Equal("maintain-resolution", ios1080p60Snap.Hardware.CameraEncoding.DegradationPreference);
        Assert.False(ios1080p60Snap.Hardware.CameraEncoding.Simulcast);
        Assert.True((await android.GetStageSnapshotAsync()).Tiles["user-ios"].HasCameraStream);
        Assert.True((await desktop.GetStageSnapshotAsync()).Tiles["user-ios"].HasCameraStream);

        await ios.VoiceService.UpdateVideoSettingsAsync("2160p", 60, "ios_back");
        await cluster.SettleAsync();

        var ios4kSnap = await ios.GetStageSnapshotAsync();
        Assert.NotNull(ios4kSnap.Hardware.CameraEncoding);
        Assert.Equal(30_000_000, ios4kSnap.Hardware.CameraEncoding!.MaxBitrate);
        Assert.Equal(60, ios4kSnap.Hardware.CameraEncoding.MaxFramerate);
        Assert.Equal("maintain-resolution", ios4kSnap.Hardware.CameraEncoding.DegradationPreference);
        Assert.False(ios4kSnap.Hardware.CameraEncoding.Simulcast);
        Assert.Equal(3840, ios4kSnap.Hardware.CameraEncoding.Width);
        Assert.Equal(2160, ios4kSnap.Hardware.CameraEncoding.Height);
        Assert.Equal(3840, android.DeviceService.RemoteVideoHeights.GetValueOrDefault("user-ios", 0));
        Assert.Equal(3840, desktop.DeviceService.RemoteVideoHeights.GetValueOrDefault("user-ios", 0));
        Assert.True((await android.GetStageSnapshotAsync()).Tiles["user-ios"].HasCameraStream);
        Assert.True((await desktop.GetStageSnapshotAsync()).Tiles["user-ios"].HasCameraStream);

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

        // 1. Android Camera Auto preset -> 1080p30 @ 8.0 Mbps, balanced degradation, single-layer hardware H.264 (simulcast: false on mobile!)
        await android.VoiceService.ToggleVideoAsync("Auto", 30);
        await cluster.SettleAsync();

        var androidAuto = (await android.GetStageSnapshotAsync()).Hardware.CameraEncoding;
        Assert.NotNull(androidAuto);
        Assert.Equal(8_000_000, androidAuto!.MaxBitrate);
        Assert.Equal(30, androidAuto.MaxFramerate);
        Assert.Equal("balanced", androidAuto.DegradationPreference);
        Assert.False(androidAuto.Simulcast);
        Assert.Equal("", androidAuto.ContentHint);
        Assert.Equal(1920, androidAuto.Width);
        Assert.Equal(1080, androidAuto.Height);

        // 2. Android Camera fixed 1080p @ 60fps -> 12.0 Mbps, maintain-resolution, simulcast disabled, contentHint 'detail'
        await android.VoiceService.UpdateVideoSettingsAsync("1080p", 60, "");
        await cluster.SettleAsync();

        var android1080p60 = (await android.GetStageSnapshotAsync()).Hardware.CameraEncoding;
        Assert.NotNull(android1080p60);
        Assert.Equal(12_000_000, android1080p60!.MaxBitrate);
        Assert.Equal(60, android1080p60.MaxFramerate);
        Assert.Equal("maintain-resolution", android1080p60.DegradationPreference);
        Assert.False(android1080p60.Simulcast);
        Assert.Equal("detail", android1080p60.ContentHint);

        // 3. Desktop Camera Auto -> 1080p30 @ 8.0 Mbps, balanced degradation, simulcast enabled on desktop
        await desktop.VoiceService.ToggleVideoAsync("Auto", 30);
        await cluster.SettleAsync();

        var desktopAutoCam = (await desktop.GetStageSnapshotAsync()).Hardware.CameraEncoding;
        Assert.NotNull(desktopAutoCam);
        Assert.Equal(8_000_000, desktopAutoCam!.MaxBitrate);
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
        // and ALL non-maximized connected users ('user-peer' AND 'user-desktop') are visible in the bottom filmstrip!
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
        Assert.False(focusedStreamerPip.InAppPip.SelfVideoHasStream);
        Assert.Equal(2, focusedStreamerPip.InAppPip.FilmstripTileParticipantIds.Count);
        Assert.Contains("user-peer", focusedStreamerPip.InAppPip.FilmstripTileParticipantIds);
        Assert.Contains("user-desktop", focusedStreamerPip.InAppPip.FilmstripTileParticipantIds);

        // 4. While 'user-streamer' is STILL streaming, 'user-peer' turns off their camera (becoming voice-only)
        // and desktop clicks 'user-peer' in the PiP bottom filmstrip -> PiP must switch focus to 'user-peer' avatar card
        // (NEVER hijacking 'user-streamer's video feed!) and display 'user-streamer' + 'user-desktop' in the bottom filmstrip!
        await peer.VoiceService.ToggleVideoAsync();
        await cluster.SettleAsync();
        await desktop.ClickPipParticipantTileAsync("user-peer", inFilmstrip: true);
        await cluster.SettleAsync();

        var focusedVoiceOnlyPeerPip = await desktop.GetStageSnapshotAsync();
        Assert.Equal("user-peer", desktop.UiState.FocusedParticipantId);
        Assert.Equal("focus", focusedVoiceOnlyPeerPip.InAppPip.LayoutMode);
        Assert.Equal("avatar", focusedVoiceOnlyPeerPip.InAppPip.Mode);
        Assert.False(focusedVoiceOnlyPeerPip.InAppPip.PrimaryVideoHasStream);
        Assert.False(focusedVoiceOnlyPeerPip.InAppPip.PresenterCamVideoHasStream);
        Assert.Equal(2, focusedVoiceOnlyPeerPip.InAppPip.FilmstripTileParticipantIds.Count);
        Assert.Contains("user-streamer", focusedVoiceOnlyPeerPip.InAppPip.FilmstripTileParticipantIds);
        Assert.Contains("user-desktop", focusedVoiceOnlyPeerPip.InAppPip.FilmstripTileParticipantIds);

        // 5. 'user-peer' turns their camera back on while focused -> primary video immediately displays 'user-peer's camera!
        await peer.VoiceService.ToggleVideoAsync("1080p", 30);
        await cluster.SettleAsync();

        var focusedPeerPip = await desktop.GetStageSnapshotAsync();
        Assert.Equal("user-peer", desktop.UiState.FocusedParticipantId);
        Assert.Equal("focus", focusedPeerPip.InAppPip.LayoutMode);
        Assert.Equal("video", focusedPeerPip.InAppPip.Mode);
        Assert.True(focusedPeerPip.InAppPip.PrimaryVideoHasStream);
        Assert.Equal("user-peer", focusedPeerPip.InAppPip.PrimaryTrackParticipantId);
        Assert.Equal("camera", focusedPeerPip.InAppPip.PrimaryTrackSource);
        Assert.False(focusedPeerPip.InAppPip.PresenterCamVideoHasStream);
        Assert.False(focusedPeerPip.InAppPip.SelfVideoHasStream);
        Assert.Equal(2, focusedPeerPip.InAppPip.FilmstripTileParticipantIds.Count);
        Assert.Contains("user-streamer", focusedPeerPip.InAppPip.FilmstripTileParticipantIds);
        Assert.Contains("user-desktop", focusedPeerPip.InAppPip.FilmstripTileParticipantIds);

        // 6. Desktop returns to the in-app call stage -> stage preserves focus on 'user-peer' seamlessly!
        await desktop.ReturnToCallStageAsync();
        await cluster.SettleAsync();

        var returnedStage = await desktop.GetStageSnapshotAsync();
        Assert.Equal(StageLayoutMode.FocusedParticipant, returnedStage.LayoutMode);
        Assert.Equal("user-peer", returnedStage.FocusedParticipantId);
    }

    [Fact]
    public async Task Cluster_MultiUserConnectDisconnectAndRapidReconnect_UpdatesChannelListUiAndFencesStaleSessionEvents()
    {
        await using var cluster = await VoiceCallClusterSimulator.CreateAsync();

        var alice = await cluster.CreateClientAsync("alice-client", "alice", VoiceClientPlatform.DesktopElectron);
        var bob = await cluster.CreateClientAsync("bob-client", "bob", VoiceClientPlatform.DesktopElectron);
        var charlie = await cluster.CreateClientAsync("charlie-client", "charlie", VoiceClientPlatform.WebBrowser);

        await alice.ConnectToChannelAsync("ch-lifecycle", voiceSessionId: "sess-alice-1");
        await bob.ConnectToChannelAsync("ch-lifecycle", voiceSessionId: "sess-bob-1");
        await charlie.ConnectToChannelAsync("ch-lifecycle", voiceSessionId: "sess-charlie-1");
        await cluster.SettleAsync();

        // 1. All 3 participants are present in ChatState (Channel List UI) and on every client's CallParticipants
        var serverMembers = cluster.VoiceState.GetVoiceParticipants("ch-lifecycle");
        Assert.Equal(3, serverMembers.Count);
        Assert.Contains("alice", serverMembers);
        Assert.Contains("bob", serverMembers);
        Assert.Contains("charlie", serverMembers);

        foreach (var client in new[] { alice, bob, charlie })
        {
            Assert.Contains("alice", client.VoiceService.CallParticipants);
            Assert.Contains("bob", client.VoiceService.CallParticipants);
            Assert.Contains("charlie", client.VoiceService.CallParticipants);
        }

        // Bob starts camera & screen share before disconnecting
        await bob.VoiceService.ToggleVideoAsync("1080p", 30);
        await bob.VoiceService.ToggleScreenShareAsync("1080p", 30, "detail", captureAudio: true);
        await cluster.SettleAsync();
        Assert.Contains("bob", alice.VoiceService.ActiveCameraParticipants);
        Assert.Contains("bob", alice.VoiceService.ActiveScreenShareParticipants);

        // 2. Bob disconnects cleanly -> Channel List UI (ChatState) and all peer clients immediately remove Bob
        await bob.DisconnectFromChannelAsync();
        await cluster.SettleAsync();

        Assert.False(bob.VoiceService.IsInVoiceCall);
        Assert.DoesNotContain("bob", cluster.VoiceState.GetVoiceParticipants("ch-lifecycle"));
        Assert.True(cluster.VoiceState.IsVoiceSessionClosed("sess-bob-1"));

        foreach (var remaining in new[] { alice, charlie })
        {
            Assert.DoesNotContain("bob", remaining.VoiceService.CallParticipants);
            Assert.DoesNotContain("bob", remaining.VoiceService.ActiveCameraParticipants);
            Assert.DoesNotContain("bob", remaining.VoiceService.ActiveScreenShareParticipants);
            Assert.DoesNotContain("bob", remaining.VoiceService.RemoteScreenAudioParticipants);
        }

        // 3. Stale/delayed reconciliation carrying Bob's closed session ID is rejected by the session tombstone
        await cluster.VoiceChannelService.ReconcileMemberStateAsync(
            "ch-lifecycle",
            "bob",
            new VoiceParticipantMetadata
            {
                VoiceSessionId = "sess-bob-1",
                DeviceId = "dev-bob",
                DeviceName = "Bob PC",
                Platform = "desktop"
            },
            isMicMuted: false);
        Assert.DoesNotContain("bob", cluster.VoiceState.GetVoiceParticipants("ch-lifecycle"));

        // 4. Bob rapidly reconnects with a new session ("sess-bob-2"), then a delayed leave from "sess-bob-1" arrives
        await bob.ConnectToChannelAsync("ch-lifecycle", voiceSessionId: "sess-bob-2");
        await cluster.SettleAsync();

        bool staleLeaveRemoved = await cluster.VoiceChannelService.TryLeaveVoiceAsync(
            "ch-lifecycle",
            "bob",
            expectedSessionId: "sess-bob-1");
        Assert.False(staleLeaveRemoved);
        Assert.Contains("bob", cluster.VoiceState.GetVoiceParticipants("ch-lifecycle"));
        Assert.Equal("sess-bob-2", cluster.VoiceState.GetVoiceParticipantSessionId("ch-lifecycle", "bob"));

        // Verify Alice and Charlie both hear Bob on the very first reconnect without needing multiple reconnects
        var aliceRouting = await alice.GetAudioRoutingSnapshotAsync();
        var charlieRouting = await charlie.GetAudioRoutingSnapshotAsync();
        Assert.True(aliceRouting.RemoteUsers["bob"].RawMicSubscribed);
        Assert.True(aliceRouting.RemoteUsers["bob"].RawMicAttached);
        Assert.True(charlieRouting.RemoteUsers["bob"].RawMicSubscribed);
        Assert.True(charlieRouting.RemoteUsers["bob"].RawMicAttached);
    }

    [Fact]
    public async Task Cluster_JoinLeaveChimeOrdering_FirstJoinPlaysSingleChime_AndReconnectAfterLeavePlaysSingleChime()
    {
        await using var cluster = await VoiceCallClusterSimulator.CreateAsync();

        var alice = await cluster.CreateClientAsync("alice-chime", "alice", VoiceClientPlatform.DesktopElectron);
        var bob = await cluster.CreateClientAsync("bob-chime", "bob", VoiceClientPlatform.DesktopElectron);

        // 1. Alice joins empty channel -> no join chime
        await alice.ConnectToChannelAsync("ch-chimes", voiceSessionId: "sess-alice-1");
        await cluster.SettleAsync();
        Assert.Empty(cluster.VoiceChannelService.SimulatedBot.PlayedSounds);

        // 2. Bob begins joining: simulate early LiveKit participant_joined webhook arriving while Bob is still loading ICE/audio
        var bobEarlyMeta = new VoiceParticipantMetadata
        {
            VoiceSessionId = "sess-bob-1",
            DeviceId = "dev-bob",
            DeviceName = "Bob PC",
            Platform = "desktop"
        };
        await cluster.VoiceChannelService.ReconcileMemberStateAsync("ch-chimes", "bob", bobEarlyMeta, isMicMuted: false);
        Assert.Contains("bob", cluster.VoiceState.GetVoiceParticipants("ch-chimes"));
        // Webhook reconciliation must NOT play a premature chime while Bob is still loading!
        Assert.Empty(cluster.VoiceChannelService.SimulatedBot.PlayedSounds);

        // 3. Bob completes ConnectToChannelAsync -> plays exactly ONE join chime
        await bob.ConnectToChannelAsync("ch-chimes", deviceId: "dev-bob", deviceName: "Bob PC", voiceSessionId: "sess-bob-1");
        await cluster.SettleAsync();
        Assert.Single(cluster.VoiceChannelService.SimulatedBot.PlayedSounds);
        Assert.Equal(("ch-chimes", "channel_join"), cluster.VoiceChannelService.SimulatedBot.PlayedSounds[0]);

        // 4. Bob disconnects -> plays exactly ONE leave chime
        await bob.DisconnectFromChannelAsync();
        await cluster.SettleAsync();
        Assert.Equal(2, cluster.VoiceChannelService.SimulatedBot.PlayedSounds.Count);
        Assert.Equal(("ch-chimes", "channel_leave"), cluster.VoiceChannelService.SimulatedBot.PlayedSounds[1]);

        // 5. Bob reconnects to the same channel (even if webhook reconcile arrives first again) -> plays a NEW join chime!
        var bobReconnectMeta = new VoiceParticipantMetadata
        {
            VoiceSessionId = "sess-bob-2",
            DeviceId = "dev-bob",
            DeviceName = "Bob PC",
            Platform = "desktop"
        };
        await cluster.VoiceChannelService.ReconcileMemberStateAsync("ch-chimes", "bob", bobReconnectMeta, isMicMuted: false);
        Assert.Equal(2, cluster.VoiceChannelService.SimulatedBot.PlayedSounds.Count);

        await bob.ConnectToChannelAsync("ch-chimes", deviceId: "dev-bob", deviceName: "Bob PC", voiceSessionId: "sess-bob-2");
        await cluster.SettleAsync();
        Assert.Equal(3, cluster.VoiceChannelService.SimulatedBot.PlayedSounds.Count);
        Assert.Equal(("ch-chimes", "channel_join"), cluster.VoiceChannelService.SimulatedBot.PlayedSounds[2]);
    }

    [Fact]
    public async Task Cluster_MuteUnmuteHardwareBoomMuteAndUnpublishedInitialMic_SynchronizesAccuratelyAcrossAllClients()
    {
        await using var cluster = await VoiceCallClusterSimulator.CreateAsync();

        var alice = await cluster.CreateClientAsync("alice-client", "alice", VoiceClientPlatform.DesktopElectron);
        var bob = await cluster.CreateClientAsync("bob-client", "bob", VoiceClientPlatform.DesktopElectron);
        var charlie = await cluster.CreateClientAsync("charlie-client", "charlie", VoiceClientPlatform.WebBrowser);

        await alice.ConnectToChannelAsync("ch-mute", voiceSessionId: "sess-alice");
        await bob.ConnectToChannelAsync("ch-mute", voiceSessionId: "sess-bob");
        await charlie.ConnectToChannelAsync("ch-mute", voiceSessionId: "sess-charlie");
        await cluster.SettleAsync();

        // 1. A remote peer ("dave") joins the SFU room before their microphone track finishes publishing.
        // Verify they are NEVER falsely marked as muted while their mic publication list is still empty.
        var daveMeta = new VoiceParticipantMetadata
        {
            VoiceSessionId = "sess-dave",
            DeviceId = "dev-dave",
            DeviceName = "Dave PC",
            Platform = "web"
        }.ToJson();
        await cluster.SimulateRemotePeerJoinedWithoutMicAsync("ch-mute", "dave", daveMeta);
        await cluster.SettleAsync();

        Assert.False(cluster.VoiceState.IsUserMuted("ch-mute", "dave"));
        foreach (var client in new[] { alice, bob, charlie })
        {
            Assert.Contains("dave", client.VoiceService.CallParticipants);
            Assert.DoesNotContain("dave", client.VoiceService.MutedParticipants);
        }

        // 2. Alice is speaking, then toggles software mute -> speaking state clears and all clients + ChatState see Alice muted
        await cluster.SimulateActiveSpeakersChangedAsync("ch-mute", "alice");
        await cluster.SettleAsync();
        Assert.Contains("alice", bob.VoiceService.ActiveSpeakers);

        await alice.VoiceService.ToggleMuteAsync();
        await cluster.SettleAsync();

        Assert.True(alice.VoiceService.IsSoftwareMuted);
        Assert.True(alice.VoiceService.IsMuted);
        Assert.True(cluster.VoiceState.IsUserMuted("ch-mute", "alice"));
        foreach (var client in new[] { alice, bob, charlie })
        {
            Assert.Contains("alice", client.VoiceService.MutedParticipants);
            Assert.DoesNotContain("alice", client.VoiceService.ActiveSpeakers);
        }

        // Unmuting Alice clears mute across all clients and ChatState
        await alice.VoiceService.ToggleMuteAsync();
        await cluster.SettleAsync();

        Assert.False(alice.VoiceService.IsMuted);
        Assert.False(cluster.VoiceState.IsUserMuted("ch-mute", "alice"));
        foreach (var client in new[] { alice, bob, charlie })
        {
            Assert.DoesNotContain("alice", client.VoiceService.MutedParticipants);
        }

        // 3. Bob flips his physical headset boom-arm mute switch -> hardware mute propagates to ChatState and all peers
        await bob.SimulateHardwareMicMuteAsync(isMuted: true);
        await cluster.SettleAsync();

        Assert.True(bob.VoiceService.IsHardwareMuted);
        Assert.True(bob.VoiceService.IsMuted);
        Assert.True(cluster.VoiceState.IsUserMuted("ch-mute", "bob"));
        foreach (var client in new[] { alice, bob, charlie })
        {
            Assert.Contains("bob", client.VoiceService.MutedParticipants);
        }

        // Lowering the headset boom arm unmutes Bob across all clients
        await bob.SimulateHardwareMicMuteAsync(isMuted: false);
        await cluster.SettleAsync();

        Assert.False(bob.VoiceService.IsHardwareMuted);
        Assert.False(bob.VoiceService.IsMuted);
        Assert.False(cluster.VoiceState.IsUserMuted("ch-mute", "bob"));
        foreach (var client in new[] { alice, bob, charlie })
        {
            Assert.DoesNotContain("bob", client.VoiceService.MutedParticipants);
        }
    }

    [Fact]
    public async Task Cluster_PhysicalRoomCoLocatedPeersAndRemoteCaller_ReadinessGatedHandoffDirectLanAndStationaryMode()
    {
        await using var cluster = await VoiceCallClusterSimulator.CreateAsync();

        var peerA = await cluster.CreateClientAsync("peera-client", "peer-a", VoiceClientPlatform.DesktopElectron);
        var peerB = await cluster.CreateClientAsync("peerb-client", "peer-b", VoiceClientPlatform.DesktopElectron);
        var remoteCaller = await cluster.CreateClientAsync("remote-client", "remote-caller", VoiceClientPlatform.WebBrowser);

        // Configure "Room Alpha" physical room in LowLatencyCleaned (Direct LAN + Server Cleanup for remote callers)
        var createRes = await cluster.PhysicalRoomService.CreateRoomAsync(
            "Room Alpha",
            "peer-b",
            initialDeviceId: "dev-peerb-pc",
            initialDeviceName: "Windows PC (Spokes Desktop)",
            muteUsersInMyRoom: false,
            audioMode: PhysicalRoomAudioMode.LowLatencyCleaned);
        Assert.True(createRes.IsSuccess);
        var room = createRes.Value!;

        var enrollRes = await cluster.PhysicalRoomService.EnrollUserDeviceAsync(
            room.Id,
            "peer-a",
            "dev-peera-pc",
            "Windows PC (Spokes Desktop)",
            muteUsersInMyRoom: false,
            actorUserId: "peer-a");
        Assert.True(enrollRes.IsSuccess);

        await peerA.ConnectToChannelAsync("ch-room-a", noiseSuppression: "rnnoise", deviceId: "dev-peera-pc", deviceName: "Windows PC (Spokes Desktop)", voiceSessionId: "sess-peera");
        await peerB.ConnectToChannelAsync("ch-room-a", noiseSuppression: "rnnoise", deviceId: "dev-peerb-pc", deviceName: "Windows PC (Spokes Desktop)", voiceSessionId: "sess-peerb");
        await remoteCaller.ConnectToChannelAsync("ch-room-a", noiseSuppression: "webrtc", deviceId: "dev-remote-web", deviceName: "Desktop Browser", voiceSessionId: "sess-remote");
        await cluster.SettleAsync();

        // 1. Both co-located roommates preserve their 'desktop' platform tag in ChatState even after JS audio telemetry reports
        Assert.Equal("desktop", cluster.VoiceState.GetVoiceParticipantAudioTelemetry("ch-room-a", "peer-a")?.Platform);
        Assert.Equal("desktop", cluster.VoiceState.GetVoiceParticipantAudioTelemetry("ch-room-a", "peer-b")?.Platform);
        Assert.Equal(DirectRoomOperatingStatus.ActiveDirectLan, peerA.VoiceService.GetPhysicalRoomDirectStatus(room.Id));
        Assert.Equal(DirectRoomOperatingStatus.ActiveDirectLan, peerB.VoiceService.GetPhysicalRoomDirectStatus(room.Id));

        // 2. Verify stationaryRoomMode is enabled on both co-located roommates' RNNoise processors,
        // and Direct LAN mode suppresses WebRTC tracks between peer-a and peer-b so neither hears a faint self-echo
        var peerARouting = await peerA.GetAudioRoutingSnapshotAsync();
        var peerBRouting = await peerB.GetAudioRoutingSnapshotAsync();
        Assert.True(peerARouting.StationaryRoomMode);
        Assert.True(peerBRouting.StationaryRoomMode);
        Assert.Contains("peer-b", peerARouting.DirectLanPeerUserIds);
        Assert.Contains("peer-a", peerBRouting.DirectLanPeerUserIds);
        Assert.False(peerARouting.RemoteUsers["peer-b"].RawMicSubscribed);
        Assert.False(peerARouting.RemoteUsers["peer-b"].RawMicAttached);
        Assert.False(peerBRouting.RemoteUsers["peer-a"].RawMicSubscribed);
        Assert.False(peerBRouting.RemoteUsers["peer-a"].RawMicAttached);

        // 2b. Event-driven transition push + pull-on-demand telemetry:
        // Repeated bridge status ticks with unchanged activePeerTokens must emit ZERO SignalR callbacks,
        // while RefreshDirectRoomTelemetryAsync() pulls fresh live metrics on demand (e.g. when VoiceSettingsDialog opens).
        int callbacksBeforeTicks = peerA.DirectRoomStatusCallbackCount;
        await peerA.EmitSimulatedDirectRoomStatusAsync(estimatedEndToEndMs: 12.8, txPacketCount: 420);
        await peerA.EmitSimulatedDirectRoomStatusAsync(estimatedEndToEndMs: 13.4, txPacketCount: 840);
        await cluster.SettleAsync();
        Assert.Equal(callbacksBeforeTicks, peerA.DirectRoomStatusCallbackCount);

        var pulledTelemetry = await peerA.VoiceService.RefreshDirectRoomTelemetryAsync();
        Assert.NotNull(pulledTelemetry);
        Assert.Equal(room.Id, pulledTelemetry!.RoomId);
        Assert.Equal(13.4, pulledTelemetry.EstimatedEndToEndMs, precision: 1);
        Assert.Equal(840, pulledTelemetry.TxPacketCount);
        Assert.Equal(13.4, peerA.VoiceService.DirectRoomTelemetry!.EstimatedEndToEndMs, precision: 1);

        // 3. Before the server bot publishes processed-mic tracks, remote-caller hears BOTH peer-a and peer-b via raw-mic
        var remoteInitialRouting = await remoteCaller.GetAudioRoutingSnapshotAsync();
        Assert.True(remoteInitialRouting.RemoteUsers["peer-a"].RawMicSubscribed);
        Assert.True(remoteInitialRouting.RemoteUsers["peer-a"].RawMicAttached);
        Assert.True(remoteInitialRouting.RemoteUsers["peer-b"].RawMicSubscribed);
        Assert.True(remoteInitialRouting.RemoteUsers["peer-b"].RawMicAttached);

        // 4. Server bot publishes processed-mic:peer-a in pending-subscription state (subscribeReady: false, pub.track == null).
        // Readiness-gated handoff MUST keep peer-a's raw-mic subscribed & attached until processed-mic has a live MediaStreamTrack!
        await cluster.PublishBotProcessedAudioTrackAsync("ch-room-a", "peer-a", subscribeReady: false);
        await cluster.SettleAsync();

        var remotePendingRouting = await remoteCaller.GetAudioRoutingSnapshotAsync();
        Assert.True(remotePendingRouting.RemoteUsers["peer-a"].ProcessedMicPublished);
        Assert.False(remotePendingRouting.RemoteUsers["peer-a"].ProcessedMicHasTrack);
        Assert.True(remotePendingRouting.RemoteUsers["peer-a"].RawMicSubscribed);
        Assert.True(remotePendingRouting.RemoteUsers["peer-a"].RawMicAttached);
        Assert.True(remotePendingRouting.RemoteUsers["peer-b"].RawMicSubscribed);
        Assert.True(remotePendingRouting.RemoteUsers["peer-b"].RawMicAttached);

        // 5. Once processed-mic:peer-a and processed-mic:peer-b complete track subscription, remote-caller seamlessly
        // switches to processed-mic for both roommates and detaches their raw-mic tracks (while roommates keep both suppressed!)
        await cluster.CompleteBotProcessedAudioSubscriptionAsync("ch-room-a", "peer-a");
        await cluster.PublishBotProcessedAudioTrackAsync("ch-room-a", "peer-b", subscribeReady: true);
        await cluster.SettleAsync();

        var remoteReadyRouting = await remoteCaller.GetAudioRoutingSnapshotAsync();
        Assert.True(remoteReadyRouting.RemoteUsers["peer-a"].ProcessedMicSubscribed);
        Assert.True(remoteReadyRouting.RemoteUsers["peer-a"].ProcessedMicAttached);
        Assert.False(remoteReadyRouting.RemoteUsers["peer-a"].RawMicSubscribed);
        Assert.False(remoteReadyRouting.RemoteUsers["peer-a"].RawMicAttached);
        Assert.True(remoteReadyRouting.RemoteUsers["peer-b"].ProcessedMicSubscribed);
        Assert.True(remoteReadyRouting.RemoteUsers["peer-b"].ProcessedMicAttached);
        Assert.False(remoteReadyRouting.RemoteUsers["peer-b"].RawMicSubscribed);
        Assert.False(remoteReadyRouting.RemoteUsers["peer-b"].RawMicAttached);

        // Roommates in Direct LAN mode must ALSO suppress the bot's processed-mic tracks for each other and themselves
        var peerAAfterBotRouting = await peerA.GetAudioRoutingSnapshotAsync();
        Assert.False(peerAAfterBotRouting.RemoteUsers["peer-a"].ProcessedMicSubscribed);
        Assert.False(peerAAfterBotRouting.RemoteUsers["peer-a"].ProcessedMicAttached);
        Assert.False(peerAAfterBotRouting.RemoteUsers["peer-b"].ProcessedMicSubscribed);
        Assert.False(peerAAfterBotRouting.RemoteUsers["peer-b"].ProcessedMicAttached);

        // 6. When peer-a disconnects and the bot unpublishes processed-mic:peer-a, peer-b is now solo in "Room Alpha"
        // so remote-caller cleanly detaches peer-a and seamlessly hands peer-b back to low-latency raw-mic
        await peerA.DisconnectFromChannelAsync();
        await cluster.UnpublishBotProcessedAudioTrackAsync("ch-room-a", "peer-a");
        await cluster.SettleAsync();

        Assert.DoesNotContain("peer-a", cluster.VoiceState.GetVoiceParticipants("ch-room-a"));
        Assert.DoesNotContain("peer-a", remoteCaller.VoiceService.CallParticipants);
        var remoteAfterLeaveRouting = await remoteCaller.GetAudioRoutingSnapshotAsync();
        Assert.True(remoteAfterLeaveRouting.RemoteUsers["peer-b"].RawMicSubscribed);
        Assert.True(remoteAfterLeaveRouting.RemoteUsers["peer-b"].RawMicAttached);
        Assert.False(remoteAfterLeaveRouting.RemoteUsers["peer-b"].ProcessedMicSubscribed);
        Assert.False(remoteAfterLeaveRouting.RemoteUsers["peer-b"].ProcessedMicAttached);
    }

    [Fact]
    public async Task Cluster_FourPlatformMixedCall_WebDesktopAndroidNativeIosNative_ExercisesRealSpokesNativeVoiceAndInAppPipCoherence()
    {
        await using var cluster = await VoiceCallClusterSimulator.CreateAsync();

        var web = await cluster.CreateClientAsync("web-client", "user-web", VoiceClientPlatform.WebBrowser);
        var desktop = await cluster.CreateClientAsync("desktop-client", "user-desktop", VoiceClientPlatform.DesktopElectron);
        var android = await cluster.CreateClientAsync("android-client", "user-android", VoiceClientPlatform.AndroidNative);
        var ios = await cluster.CreateClientAsync("ios-client", "user-ios", VoiceClientPlatform.IosNative);

        await web.ConnectToChannelAsync("ch-4p", voiceSessionId: "sess-web");
        await desktop.ConnectToChannelAsync("ch-4p", voiceSessionId: "sess-desktop");
        await android.ConnectToChannelAsync("ch-4p", voiceSessionId: "sess-android");
        await ios.ConnectToChannelAsync("ch-4p", voiceSessionId: "sess-ios");
        await cluster.SettleAsync();

        // Android & iOS must BOTH be running on real SpokesNativeVoice (zero WebView LiveKit fallback)
        Assert.True((await android.GetStageSnapshotAsync()).Hardware.UsingNativeMobileVoice);
        Assert.True((await ios.GetStageSnapshotAsync()).Hardware.UsingNativeMobileVoice);
        Assert.False((await desktop.GetStageSnapshotAsync()).Hardware.UsingNativeMobileVoice);
        Assert.False((await web.GetStageSnapshotAsync()).Hardware.UsingNativeMobileVoice);

        // Desktop starts screen share + webcam, Android & iOS enable cameras
        await desktop.VoiceService.ToggleScreenShareAsync("1440p", 60, "motion", captureAudio: true);
        await desktop.VoiceService.ToggleVideoAsync("1080p", 30);
        await android.VoiceService.ToggleVideoAsync("Auto", 30);
        await ios.VoiceService.ToggleVideoAsync("Auto", 30);
        await cluster.SettleAsync();

        await cluster.AssertClusterInvariantsAsync("ch-4p");

        // Both Android and iOS navigate away to channel list -> In-App Floating PiP MUST route pip:* slots
        // to the non-interactive overlay layer (underlaid=false) and stay focus-only (no multi-tile grid on mobile)!
        await android.NavigateAwayFromCallStageAsync();
        await ios.NavigateAwayFromCallStageAsync();
        await cluster.SettleAsync();

        var androidPip = await android.GetStageSnapshotAsync();
        Assert.True(androidPip.InAppPip.IsOverlayVisible);
        Assert.Equal("focus", androidPip.InAppPip.LayoutMode);
        Assert.Equal("video", androidPip.InAppPip.Mode);
        Assert.True(androidPip.InAppPip.PrimaryVideoHasStream);
        Assert.True(androidPip.InAppPip.SelfVideoHasStream);
        Assert.NotNull(androidPip.NativeVideoSlots);
        Assert.False(androidPip.NativeVideoSlots!.Underlaid);
        Assert.False(androidPip.NativeVideoSlots.Suppressed);
        Assert.Contains(androidPip.NativeVideoSlots.Slots, s => s.Key.StartsWith("pip:", StringComparison.Ordinal));

        var iosPip = await ios.GetStageSnapshotAsync();
        Assert.True(iosPip.InAppPip.IsOverlayVisible);
        Assert.Equal("focus", iosPip.InAppPip.LayoutMode);
        Assert.True(iosPip.InAppPip.PrimaryVideoHasStream);
        Assert.True(iosPip.InAppPip.SelfVideoHasStream);
        Assert.NotNull(iosPip.NativeVideoSlots);
        Assert.False(iosPip.NativeVideoSlots!.Underlaid);
        Assert.Contains(iosPip.NativeVideoSlots.Slots, s => s.Key.StartsWith("pip:", StringComparison.Ordinal));

        await cluster.AssertClusterInvariantsAsync("ch-4p");

        // Android returns to stage -> underlay restores cleanly
        await android.ReturnToCallStageAsync();
        await cluster.SettleAsync();

        var androidStage = await android.GetStageSnapshotAsync();
        Assert.False(androidStage.InAppPip.IsOverlayVisible);
        Assert.NotNull(androidStage.NativeVideoSlots);
        Assert.True(androidStage.NativeVideoSlots!.Underlaid);
        Assert.True(androidStage.Tiles["user-desktop"].HasScreenShareStream);
        Assert.True(androidStage.Tiles["user-android"].HasCameraStream);
    }

    [Fact]
    public async Task Cluster_EarlyTrackPublishBeforeServerJoinCompletes_PreservesCameraScreenAndDimensions()
    {
        await using var cluster = await VoiceCallClusterSimulator.CreateAsync();

        var desktop = await cluster.CreateClientAsync("desk-early", "user-desktop", VoiceClientPlatform.DesktopElectron);
        await desktop.ConnectToChannelAsync("ch-early", voiceSessionId: "sess-desk-early");
        await cluster.SettleAsync();

        // Simulate a remote peer ("user-early") entering PendingConnect and publishing camera + screen webhooks
        // BEFORE JoinVoiceAsync finishes — server must preserve media state and hydrate joining clients!
        var earlyMeta = new VoiceParticipantMetadata
        {
            VoiceSessionId = "sess-early-1",
            DeviceId = "dev-early",
            DeviceName = "Early Phone",
            Platform = "android-native"
        };
        cluster.VoiceState.SetVoiceParticipantSessionContext(
            "ch-early",
            "user-early",
            earlyMeta.DeviceId,
            earlyMeta.DeviceName,
            "rnnoise",
            25.0,
            earlyMeta.Platform,
            isMuted: false,
            voiceSessionId: earlyMeta.VoiceSessionId);

        await cluster.DispatchLiveKitWebhookAsync(
            "track_published",
            "ch-early",
            "user-early",
            participantSid: "PA_EARLY_1",
            metadata: earlyMeta.ToJson(),
            trackSource: Livekit.Server.Sdk.Dotnet.TrackSource.Camera,
            trackType: Livekit.Server.Sdk.Dotnet.TrackType.Video,
            trackMuted: false);
        await cluster.DispatchLiveKitWebhookAsync(
            "track_published",
            "ch-early",
            "user-early",
            participantSid: "PA_EARLY_1",
            metadata: earlyMeta.ToJson(),
            trackSource: Livekit.Server.Sdk.Dotnet.TrackSource.ScreenShare,
            trackType: Livekit.Server.Sdk.Dotnet.TrackType.Video,
            trackMuted: false);

        var mediaState = cluster.VoiceState.GetVoiceParticipantMediaState("ch-early", "user-early");
        Assert.True(mediaState.IsCameraOn);
        Assert.True(mediaState.IsScreenSharing);

        // Now complete server membership and verify desktop's GlobalVoiceService immediately receives hydrated camera/screen & accepts dimensions
        await cluster.VoiceChannelService.ReconcileMemberStateAsync("ch-early", "user-early", earlyMeta, isMicMuted: false);
        desktop.VoiceService.OnParticipantVideoDimensions("user-early", 1440, 60);

        Assert.Contains("user-early", desktop.VoiceService.ActiveCameraParticipants);
        Assert.Contains("user-early", desktop.VoiceService.ActiveScreenShareParticipants);
        Assert.Equal(1440, desktop.DeviceService.RemoteVideoHeights.GetValueOrDefault("user-early", 0));
    }

    [Fact]
    public async Task Cluster_SfuSignalReconnectAndSidRotationWebhookRace_PreservesActiveSessionAndConvergesViaWatchdog()
    {
        await using var cluster = await VoiceCallClusterSimulator.CreateAsync();

        var desktop = await cluster.CreateClientAsync("desk-rec", "user-desktop", VoiceClientPlatform.DesktopElectron);
        var android = await cluster.CreateClientAsync("and-rec", "user-android", VoiceClientPlatform.AndroidNative);

        await desktop.ConnectToChannelAsync("ch-reconn", voiceSessionId: "sess-desk-rec");
        await android.ConnectToChannelAsync("ch-reconn", voiceSessionId: "sess-and-rec");
        await android.VoiceService.ToggleVideoAsync("Auto", 30);
        await cluster.SettleAsync();

        Assert.Contains("user-android", desktop.VoiceService.ActiveCameraParticipants);

        // Trigger full signal reconnect with synthetic TrackUnsubscribed/ParticipantDisconnected + PA_old -> PA_new SID rotation webhooks + watchdog poll
        cluster.VoiceChannelService.WebhookLeaveChimeDelay = TimeSpan.FromMilliseconds(120);
        await cluster.SimulateSfuSignalReconnectAsync("desk-rec", dispatchSidRotationWebhooks: true);
        await cluster.SettleAsync();
        await Task.Delay(200);

        // Desktop MUST stay connected, MUST NOT lose user-android from CallParticipants or ActiveCameraParticipants,
        // stale PA_old participant_left webhook MUST NOT evict user-desktop from ChatState, AND ZERO phantom
        // channel_leave chimes may play!
        Assert.True(desktop.VoiceService.IsInVoiceCall);
        Assert.Contains("user-desktop", cluster.VoiceState.GetVoiceParticipants("ch-reconn"));
        Assert.Contains("user-android", desktop.VoiceService.CallParticipants);
        Assert.Contains("user-android", desktop.VoiceService.ActiveCameraParticipants);
        Assert.DoesNotContain(("ch-reconn", "channel_leave"), cluster.VoiceChannelService.SimulatedBot.PlayedSounds);
        await cluster.AssertClusterInvariantsAsync("ch-reconn");
    }

    [Fact]
    public async Task Cluster_TransientParticipantLeftWebhookReconnect_SuppressesPhantomLeaveChime_WhileRealDropPlaysAfterGrace()
    {
        await using var cluster = await VoiceCallClusterSimulator.CreateAsync();
        cluster.VoiceChannelService.WebhookLeaveChimeDelay = TimeSpan.FromMilliseconds(500);

        var desktop = await cluster.CreateClientAsync("desk-pip", "user-desktop", VoiceClientPlatform.DesktopElectron);
        var ios = await cluster.CreateClientAsync("ios-pip", "user-ios", VoiceClientPlatform.IosNative);

        await desktop.ConnectToChannelAsync("ch-pip-chime", voiceSessionId: "sess-desk-pip");
        await ios.ConnectToChannelAsync("ch-pip-chime", voiceSessionId: "sess-ios-pip");
        await cluster.SettleAsync();

        var iosMeta = new VoiceParticipantMetadata
        {
            VoiceSessionId = "sess-ios-pip",
            Platform = "ios-native"
        }.ToJson();
        await cluster.DispatchLiveKitWebhookAsync("participant_joined", "ch-pip-chime", "user-ios", "PA_ios_1", iosMeta);
        cluster.VoiceChannelService.SimulatedBot.PlayedSounds.Clear();

        // 1. Ordering A (iOS minimizes to PiP -> transient full reconnect):
        // participant_left(PA_ios_1) arrives slightly BEFORE participant_joined(PA_ios_2) within grace window.
        await cluster.DispatchLiveKitWebhookAsync("participant_left", "ch-pip-chime", "user-ios", "PA_ios_1", iosMeta);
        await Task.Delay(30);
        await cluster.DispatchLiveKitWebhookAsync("participant_joined", "ch-pip-chime", "user-ios", "PA_ios_2", iosMeta);
        await Task.Delay(650);

        Assert.Contains("user-ios", cluster.VoiceState.GetVoiceParticipants("ch-pip-chime"));
        Assert.DoesNotContain(("ch-pip-chime", "channel_leave"), cluster.VoiceChannelService.SimulatedBot.PlayedSounds);
        Assert.DoesNotContain(("ch-pip-chime", "channel_join"), cluster.VoiceChannelService.SimulatedBot.PlayedSounds);

        // 2. Ordering B (PA_ios_3 joins BEFORE stale PA_ios_2 participant_left arrives):
        await cluster.DispatchLiveKitWebhookAsync("participant_joined", "ch-pip-chime", "user-ios", "PA_ios_3", iosMeta);
        await cluster.DispatchLiveKitWebhookAsync("participant_left", "ch-pip-chime", "user-ios", "PA_ios_2", iosMeta);
        await Task.Delay(650);

        Assert.Contains("user-ios", cluster.VoiceState.GetVoiceParticipants("ch-pip-chime"));
        Assert.DoesNotContain(("ch-pip-chime", "channel_leave"), cluster.VoiceChannelService.SimulatedBot.PlayedSounds);

        // 3. Real involuntary drop (e.g., force-closing iOS app without reconnecting):
        // presence clears immediately and channel_leave plays once the grace window elapses.
        await cluster.DispatchLiveKitWebhookAsync("participant_left", "ch-pip-chime", "user-ios", "PA_ios_3", iosMeta);
        Assert.DoesNotContain("user-ios", cluster.VoiceState.GetVoiceParticipants("ch-pip-chime"));
        Assert.DoesNotContain(("ch-pip-chime", "channel_leave"), cluster.VoiceChannelService.SimulatedBot.PlayedSounds);

        await Task.Delay(650);
        Assert.Contains(("ch-pip-chime", "channel_leave"), cluster.VoiceChannelService.SimulatedBot.PlayedSounds);
    }

    [Fact]
    public async Task Cluster_MidCallBlazorCircuitDropAndRestore_ClearsStuckSpeakingAndConvergesMediaState()
    {
        await using var cluster = await VoiceCallClusterSimulator.CreateAsync();

        var alice = await cluster.CreateClientAsync("alice-circ", "alice", VoiceClientPlatform.DesktopElectron);
        var bob = await cluster.CreateClientAsync("bob-circ", "bob", VoiceClientPlatform.DesktopElectron);
        var charlie = await cluster.CreateClientAsync("charlie-circ", "charlie", VoiceClientPlatform.AndroidNative);

        await alice.ConnectToChannelAsync("ch-circuit", voiceSessionId: "sess-alice-c");
        await bob.ConnectToChannelAsync("ch-circuit", voiceSessionId: "sess-bob-c");
        await charlie.ConnectToChannelAsync("ch-circuit", voiceSessionId: "sess-charlie-c");
        await cluster.SettleAsync();

        // Alice is actively speaking right before her Blazor circuit drops
        await cluster.SimulateActiveSpeakersChangedAsync("ch-circuit", "alice");
        await cluster.SettleAsync();
        Assert.Contains("alice", cluster.VoiceState.GetActiveSpeakers("ch-circuit"));

        await cluster.SimulateBlazorCircuitDropAndRestoreAsync("alice-circ", async () =>
        {
            // Dropping Alice's circuit must immediately clear her speaking indicator in ChatState so she never stays stuck green
            Assert.DoesNotContain("alice", cluster.VoiceState.GetActiveSpeakers("ch-circuit"));

            // While Alice's Blazor circuit is offline, Bob enables camera + screen share and Charlie mutes
            await bob.VoiceService.ToggleVideoAsync("1080p", 30);
            await bob.VoiceService.ToggleScreenShareAsync("1080p", 30, "motion", captureAudio: false);
            await charlie.VoiceService.ToggleMuteAsync();
            await cluster.SettleAsync();
        });

        // Once Alice's Blazor circuit restores, her GlobalVoiceService must converge 100% with Bob's video/screen and Charlie's mute
        Assert.True(alice.VoiceService.IsInVoiceCall);
        Assert.Contains("bob", alice.VoiceService.ActiveCameraParticipants);
        Assert.Contains("bob", alice.VoiceService.ActiveScreenShareParticipants);
        Assert.Contains("charlie", alice.VoiceService.MutedParticipants);
        await cluster.AssertClusterInvariantsAsync("ch-circuit");
    }

    [Fact]
    public async Task Cluster_ThreeStateMuteContract_MobileUnmuteOverridesStaleServerMute_WhileWebZeroPubsDefersToServer()
    {
        await using var cluster = await VoiceCallClusterSimulator.CreateAsync();

        var desktop = await cluster.CreateClientAsync("desk-3s", "user-desktop", VoiceClientPlatform.DesktopElectron);
        var android = await cluster.CreateClientAsync("and-3s", "user-android", VoiceClientPlatform.AndroidNative);

        await desktop.ConnectToChannelAsync("ch-3state", voiceSessionId: "sess-d3");
        await android.ConnectToChannelAsync("ch-3state", voiceSessionId: "sess-a3");
        await cluster.SettleAsync();

        // Case 1: Stale server mute flag for Android while Android actually has an active unmuted mic publication (isMicMuted === false).
        // SyncRoomSnapshotAsync MUST let definite live unmuted audio (IsMicMuted == false) win and clear mute!
        cluster.VoiceState.SetUserMuted("ch-3state", "user-android", true);
        await desktop.VoiceService.SyncRoomSnapshotAsync(new VoiceRoomSnapshotDto
        {
            ChannelId = "ch-3state",
            LocalUserId = "user-desktop",
            LocalIsMuted = false,
            Participants =
            [
                new VoiceParticipantSnapshotDto
                {
                    UserId = "user-android",
                    IsMuted = false,
                    IsMicMuted = false,
                    Metadata = ""
                }
            ]
        });
        Assert.DoesNotContain("user-android", desktop.VoiceService.MutedParticipants);

        // Case 2: Web peer muted-by-unpublish has 0 mic publications (IsMicMuted == null) while server ChatState knows they are muted.
        // SyncRoomSnapshotAsync MUST defer to authoritative server ChatState.IsUserMuted == true!
        cluster.VoiceState.SetUserMuted("ch-3state", "user-web-muted", true);
        await cluster.SimulateRemotePeerJoinedWithoutMicAsync("ch-3state", "user-web-muted");
        cluster.VoiceState.SetUserMuted("ch-3state", "user-web-muted", true);
        await desktop.VoiceService.SyncRoomSnapshotAsync(new VoiceRoomSnapshotDto
        {
            ChannelId = "ch-3state",
            LocalUserId = "user-desktop",
            LocalIsMuted = false,
            Participants =
            [
                new VoiceParticipantSnapshotDto
                {
                    UserId = "user-web-muted",
                    IsMuted = false,
                    IsMicMuted = null,
                    Metadata = ""
                }
            ]
        });
        Assert.Contains("user-web-muted", desktop.VoiceService.MutedParticipants);
    }

    [Fact]
    public async Task NativeMobileVoiceAdapter_TracksRealConnectionStateAndReconnectingLifecycle()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"spokes_native_conn_state_{Guid.NewGuid():N}");
        try
        {
            const string mockVoicePipJs = """
                export function configureVoicePip() {}
                export function registerPipVideoTrack() {}
                export function unregisterPipVideoTrack() {}
                export function updatePipSpeakingParticipants() {}
                export function updatePipCallState() {}
                export function resetPipOnCallEnd() {}
                export function setInAppPipActive() {}
                export function syncInAppPipElements() {}
                export async function toggleOsPip() { return false; }
                export function closeOsPip() {}
                export function isOsPipSupported() { return false; }
                export function isStageSuppressedByExternalPip() { return false; }
                export function isNativeMobilePipActive() { return false; }
                """;
            await VoiceJsTestModuleLoader.StageStandaloneAsync(tempDir, mockVoicePipJs);

            var runnerScript = """
                import assert from 'node:assert/strict';

                const nativeListeners = new Map();
                let nativeReportedState = 'disconnected';
                const dotNetCalls = [];

                const mockNativeVoice = {
                    async isAvailable() { return { available: true }; },
                    async addListener(eventName, cb) {
                        nativeListeners.set(eventName, cb);
                        return { remove() { nativeListeners.delete(eventName); } };
                    },
                    async connect() {
                        nativeReportedState = 'connected';
                        return {
                            localIdentity: 'user-android',
                            effectiveFilter: 'rnnoise',
                            remoteParticipants: ['user-remote'],
                            remoteParticipantStates: [{ userId: 'user-remote', isMuted: false, isMicMuted: false, metadata: '' }],
                            remoteVideoTracks: []
                        };
                    },
                    async disconnect() { nativeReportedState = 'disconnected'; },
                    async setMasterVolume() {},
                    async setPhysicalRoomRouting() {},
                    async setNoiseFilter() { return { effectiveFilter: 'rnnoise' }; },
                    async updateVideoSlots() {},
                    async getConnectionState() {
                        return {
                            state: nativeReportedState,
                            connected: nativeReportedState === 'connected' || nativeReportedState === 'reconnecting'
                        };
                    }
                };

                globalThis.localStorage = {
                    _m: new Map(),
                    getItem(k) { return this._m.get(k) ?? null; },
                    setItem(k, v) { this._m.set(k, String(v)); }
                };
                Object.defineProperty(globalThis, 'navigator', {
                    configurable: true,
                    writable: true,
                    value: { userAgent: 'Mozilla/5.0 (Linux; Android 15) Capacitor', mediaDevices: { async enumerateDevices() { return []; } } }
                });
                const docListeners = new Map();
                globalThis.document = {
                    visibilityState: 'visible',
                    documentElement: { classList: { add() {}, remove() {}, toggle() {} } },
                    body: { appendChild() {} },
                    addEventListener(evt, fn) {
                        if (!docListeners.has(evt)) docListeners.set(evt, []);
                        docListeners.get(evt).push(fn);
                    },
                    removeEventListener() {},
                    getElementById() { return null; },
                    querySelector() { return null; },
                    querySelectorAll() { return []; }
                };
                globalThis.window = globalThis;
                globalThis.innerWidth = 412;
                globalThis.innerHeight = 915;
                globalThis.requestAnimationFrame = (cb) => setTimeout(cb, 0);
                globalThis.cancelAnimationFrame = (id) => clearTimeout(id);
                globalThis.addEventListener = () => {};
                globalThis.removeEventListener = () => {};
                globalThis.dispatchEvent = () => {};
                globalThis.CustomEvent = class CustomEvent { constructor(t, o) { this.type = t; this.detail = o?.detail; } };
                globalThis.Capacitor = {
                    isNativePlatform: () => true,
                    getPlatform: () => 'android',
                    Plugins: {
                        SpokesNativeVoice: mockNativeVoice,
                        TelecomConnectionPlugin: {
                            async addListener() { return { remove() {} }; },
                            async startCall() {},
                            async endCall() {},
                            async getNativeCameras() { return { cameras: [] }; }
                        }
                    }
                };

                const voice = await import('./voiceInterop.js');
                const { voiceState } = await import('./voice/voiceState.js');
                const { syncNativeMobileConnectionState } = await import('./voice/voiceNativeMobileAdapter.js');

                assert.equal(voice.isRoomConnected(), false, 'must be disconnected before connect');

                const dotNetHelper = {
                    async invokeMethodAsync(method, ...args) {
                        dotNetCalls.push({ method, args });
                    }
                };

                const connected = await voice.connectToVoice('jwt', 'wss://lk.test', dotNetHelper, 64, 'rnnoise');
                assert.equal(connected, true);
                assert.equal(voiceState.nativeConnectionState, 'connected');
                assert.equal(voice.isRoomConnected(), true);
                assert.ok(nativeListeners.has('onReconnecting'), 'adapter must subscribe to onReconnecting');

                // Transient reconnecting state keeps room logically active while marking reconnecting
                nativeReportedState = 'reconnecting';
                nativeListeners.get('onReconnecting')();
                assert.equal(voiceState.nativeConnectionState, 'reconnecting');
                assert.equal(voice.isRoomConnected(), true);

                // Reconnected restores connected state and invokes OnRoomReconnectedAsync
                nativeReportedState = 'connected';
                nativeListeners.get('onReconnected')({
                    localIdentity: 'user-android',
                    remoteParticipants: ['user-remote'],
                    remoteParticipantStates: [{ userId: 'user-remote', isMuted: false, isMicMuted: false, metadata: '' }]
                });
                assert.equal(voiceState.nativeConnectionState, 'connected');
                assert.equal(voice.isRoomConnected(), true);
                assert.ok(dotNetCalls.some(c => c.method === 'OnRoomReconnectedAsync'), 'reconnected must reconcile server state');

                // Native getConnectionState sync catches a silent disconnect when WebView resumes
                nativeReportedState = 'disconnected';
                const synced = await syncNativeMobileConnectionState();
                assert.equal(synced, 'disconnected');
                assert.equal(voice.isRoomConnected(), false, 'isRoomConnected must report false once native room disconnects');

                // Explicit onDisconnected resets state cleanly
                nativeReportedState = 'connected';
                voiceState.nativeConnectionState = 'connected';
                await nativeListeners.get('onDisconnected')({ reason: 'CLIENT_INITIATED' });
                assert.equal(voiceState.nativeConnectionState, 'disconnected');
                assert.equal(voice.isRoomConnected(), false);

                console.log('NATIVE_CONN_STATE_PASS');
                """;

            var runnerPath = Path.Combine(tempDir, "run_native_conn_state.mjs");
            await File.WriteAllTextAsync(runnerPath, runnerScript);

            var psi = new ProcessStartInfo("node", $"\"{runnerPath}\"")
            {
                WorkingDirectory = tempDir,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var proc = Process.Start(psi)!;
            var stdout = await proc.StandardOutput.ReadToEndAsync();
            var stderr = await proc.StandardError.ReadToEndAsync();
            await proc.WaitForExitAsync();

            Assert.True(proc.ExitCode == 0, $"Node native connection state test failed (exit {proc.ExitCode}):\nSTDOUT:\n{stdout}\nSTDERR:\n{stderr}");
            Assert.Contains("NATIVE_CONN_STATE_PASS", stdout);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                try { Directory.Delete(tempDir, recursive: true); } catch { }
            }
        }
    }

    [Fact]
    public async Task VoicePipJs_DownscalesSimulcastForInAppPip_DisablesWhenMinimized_AndRestoresOnStageReturn()
    {
        var voicePipJsPath = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..",
            "Spokes_Server", "wwwroot", "js", "voicePip.js"));
        Assert.True(File.Exists(voicePipJsPath), $"Expected voicePip.js at {voicePipJsPath}");

        var tempDir = Path.Combine(Path.GetTempPath(), $"spokes_pip_sfu_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            var voicePipUrl = new Uri(voicePipJsPath).AbsoluteUri;
            var runnerScript = $$"""
                import assert from 'node:assert/strict';

                class FakeClassList {
                    constructor(initial = []) { this._set = new Set(initial); }
                    add(...cls) { cls.forEach(c => this._set.add(c)); }
                    remove(...cls) { cls.forEach(c => this._set.delete(c)); }
                    contains(c) { return this._set.has(c); }
                    toggle(c, force) {
                        if (force === undefined) {
                            if (this._set.has(c)) { this._set.delete(c); return false; }
                            this._set.add(c); return true;
                        }
                        if (force) this._set.add(c); else this._set.delete(c);
                        return Boolean(force);
                    }
                }

                class FakeElement {
                    constructor(tag, id = '') {
                        this.tagName = tag.toUpperCase();
                        this.id = id;
                        this.dataset = {};
                        this.style = { setProperty() {}, removeProperty() {} };
                        this.classList = new FakeClassList();
                        this.children = [];
                        this.paused = false;
                        this.srcObject = null;
                        this._innerHTML = '';
                    }
                    set innerHTML(v) { this._innerHTML = v; }
                    get innerHTML() { return this._innerHTML; }
                    setAttribute() {}
                    removeAttribute() {}
                    addEventListener() {}
                    removeEventListener() {}
                    appendChild(child) { this.children.push(child); return child; }
                    remove() {}
                    play() { return Promise.resolve(); }
                    pause() {}
                    querySelectorAll() { return []; }
                    querySelector() { return new FakeElement('div'); }
                    closest() { return null; }
                    contains() { return false; }
                }

                const byId = new Map();
                const overlay = new FakeElement('div', 'spokes-voice-pip-overlay');
                const stageEl = new FakeElement('div', 'voice-video-container');
                const primaryVideo = new FakeElement('video', 'spokes-pip-primary-video');
                const presenterVideo = new FakeElement('video', 'spokes-pip-presenter-cam-video');
                const selfVideo = new FakeElement('video', 'spokes-pip-self-video');
                const gridEl = new FakeElement('div', 'spokes-pip-grid-container');
                const stripEl = new FakeElement('div', 'spokes-pip-filmstrip');
                byId.set('spokes-voice-pip-overlay', overlay);
                byId.set('spokes-pip-primary-video', primaryVideo);
                byId.set('spokes-pip-presenter-cam-video', presenterVideo);
                byId.set('spokes-pip-self-video', selfVideo);
                byId.set('spokes-pip-grid-container', gridEl);
                byId.set('spokes-pip-filmstrip', stripEl);

                globalThis.window = globalThis;
                globalThis.document = {
                    getElementById: (id) => byId.get(id) || null,
                    createElement: (tag) => new FakeElement(tag),
                    querySelectorAll: () => [],
                    documentElement: { classList: new FakeClassList() }
                };
                Object.defineProperty(globalThis, 'navigator', {
                    configurable: true,
                    value: { userAgent: 'Mozilla/5.0' }
                });
                globalThis.MediaStream = class MediaStream { constructor(tracks = []) { this._tracks = tracks; } getVideoTracks() { return this._tracks; } };
                globalThis.requestAnimationFrame = () => 1;
                globalThis.LivekitClient = { VideoQuality: { LOW: 0, MEDIUM: 1, HIGH: 2 } };

                const pip = await import('{{voicePipUrl}}');

                function makeRemotePub() {
                    return {
                        isSubscribed: true,
                        enabled: true,
                        quality: null,
                        dims: null,
                        calls: { setEnabled: 0, setVideoQuality: 0 },
                        setSubscribed(v) { this.isSubscribed = v; },
                        setEnabled(v) { this.enabled = v; this.calls.setEnabled++; },
                        setVideoQuality(q) { this.quality = q; this.calls.setVideoQuality++; },
                        setVideoDimensions(d) { this.dims = d; }
                    };
                }

                const pubCamA = makeRemotePub();
                const pubCamB = makeRemotePub();
                const pubScreenA = makeRemotePub();
                const fakeTrack = () => ({ readyState: 'live', getSettings: () => ({ width: 1280, height: 720 }), addEventListener() {}, removeEventListener() {} });

                pip.updatePipCallState({
                    active: true,
                    channelId: 'v-1',
                    channelName: 'Voice',
                    localUserId: 'me',
                    participants: [
                        { id: 'me', name: 'Me' },
                        { id: 'alice', name: 'Alice' },
                        { id: 'bob', name: 'Bob' }
                    ]
                });
                pip.registerPipVideoTrack('alice', 'camera', fakeTrack(), false, 1280, 720, null, pubCamA);
                pip.registerPipVideoTrack('bob', 'camera', fakeTrack(), false, 1280, 720, null, pubCamB);

                // 1. In-app PiP grid mode (no stage mounted, 3 participants, unfocused) -> LOW (320x180)
                pip.setInAppPipActive(true);
                assert.equal(pubCamA.enabled, true);
                assert.equal(pubCamA.quality, 0, 'grid PiP camera must request VideoQuality.LOW');
                assert.deepEqual(pubCamA.dims, { width: 320, height: 180 });
                assert.equal(pubCamB.quality, 0, 'grid PiP camera B must request VideoQuality.LOW');

                // 2. Focused single primary camera in PiP -> MEDIUM (640x360) for primary, LOW for filmstrip
                pip.setPipFocusedParticipant('alice');
                assert.equal(pubCamA.quality, 1, 'focused PiP camera must request VideoQuality.MEDIUM');
                assert.deepEqual(pubCamA.dims, { width: 640, height: 360 });
                assert.equal(pubCamB.quality, 0, 'filmstrip PiP camera must stay VideoQuality.LOW');

                // 3. Screen share in PiP stays HIGH while presenter cam overlay stays LOW
                pip.registerPipVideoTrack('alice', 'screenshare', fakeTrack(), false, 1920, 1080, null, pubScreenA);
                assert.equal(pubScreenA.enabled, true);
                assert.equal(pubScreenA.quality, 2, 'screen share in PiP must stay VideoQuality.HIGH');
                assert.equal(pubCamA.quality, 0, 'presenter cam PiP overlay must use VideoQuality.LOW');

                // 4. Minimized PiP pill -> setEnabled(false) on all remote tracks (0 kbps)
                overlay.classList.add('is-minimized-pill');
                pip.setInAppPipActive(true);
                assert.equal(pubCamA.enabled, false, 'minimized PiP must disable remote camera A');
                assert.equal(pubCamB.enabled, false, 'minimized PiP must disable remote camera B');
                assert.equal(pubScreenA.enabled, false, 'minimized PiP must disable remote screen share');

                // 5. Return to full call stage -> restores enabled + VideoQuality.HIGH
                overlay.classList.remove('is-minimized-pill');
                byId.set('voice-video-container', stageEl);
                pip.setInAppPipActive(false);
                assert.equal(pubCamA.enabled, true);
                assert.equal(pubCamA.quality, 2, 'full call stage must restore VideoQuality.HIGH');
                assert.equal(pubCamB.enabled, true);
                assert.equal(pubCamB.quality, 2);
                assert.equal(pubScreenA.enabled, true);
                assert.equal(pubScreenA.quality, 2);

                // 6. Redundant speaking tick without layout change must NOT re-invoke LiveKit setters
                const beforeQualityCalls = pubCamA.calls.setVideoQuality;
                pip.updatePipSpeakingParticipants(['alice']);
                pip.updatePipSpeakingParticipants(['alice']);
                assert.equal(pubCamA.calls.setVideoQuality, beforeQualityCalls);

                console.log('VOICE_PIP_SFU_PASS');
                """;

            var runnerPath = Path.Combine(tempDir, "run_pip_sfu.mjs");
            await File.WriteAllTextAsync(runnerPath, runnerScript);

            var psi = new ProcessStartInfo("node", $"\"{runnerPath}\"")
            {
                WorkingDirectory = tempDir,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var proc = Process.Start(psi)!;
            var stdout = await proc.StandardOutput.ReadToEndAsync();
            var stderr = await proc.StandardError.ReadToEndAsync();
            await proc.WaitForExitAsync();

            Assert.True(proc.ExitCode == 0, $"Node voicePip SFU test failed (exit {proc.ExitCode}):\nSTDOUT:\n{stdout}\nSTDERR:\n{stderr}");
            Assert.Contains("VOICE_PIP_SFU_PASS", stdout);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                try { Directory.Delete(tempDir, recursive: true); } catch { }
            }
        }
    }

    [Fact]
    public async Task VoiceInteropAndPipJs_HostViewVisibilityAndScreenShareState_PausesHiddenStageVideoWithoutStoppingCapture()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"spokes_host_vis_{Guid.NewGuid():N}");
        try
        {
            // Stage the real voiceInterop.js + voice/*.js + real voicePip.js together
            await VoiceJsTestModuleLoader.StageStandaloneAsync(tempDir);

            var runnerScript = """
                import assert from 'node:assert/strict';

                const windowListeners = new Map();
                const voiceStatePayloads = [];

                function createMockMediaStreamTrack(kind, label = '') {
                    const listeners = new Map();
                    return {
                        id: `mst-${Math.random().toString(36).slice(2)}`,
                        kind,
                        label,
                        enabled: true,
                        muted: false,
                        readyState: 'live',
                        contentHint: '',
                        stop() { this.readyState = 'ended'; },
                        getSettings() {
                            return kind === 'video'
                                ? { width: 1920, height: 1080, frameRate: 60 }
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
                }

                class FakeClassList {
                    constructor(initial = []) { this._set = new Set(initial); }
                    add(...cls) { cls.forEach(c => this._set.add(c)); }
                    remove(...cls) { cls.forEach(c => this._set.delete(c)); }
                    contains(c) { return this._set.has(c); }
                    toggle(c, force) {
                        if (force === undefined) {
                            if (this._set.has(c)) { this._set.delete(c); return false; }
                            this._set.add(c); return true;
                        }
                        if (force) this._set.add(c); else this._set.delete(c);
                        return Boolean(force);
                    }
                }

                function createMockElement(tag, id = '', initialClasses = []) {
                    const styles = new Map();
                    return {
                        id,
                        tagName: tag.toUpperCase(),
                        isConnected: true,
                        paused: true,
                        muted: false,
                        srcObject: null,
                        videoWidth: 1920,
                        videoHeight: 1080,
                        dataset: {},
                        style: {
                            opacity: '0',
                            pointerEvents: 'none',
                            transform: '',
                            setProperty(k, v) { styles.set(k, v); },
                            removeProperty(k) { styles.delete(k); }
                        },
                        classList: new FakeClassList(initialClasses),
                        pause() { this.paused = true; },
                        async play() { this.paused = false; },
                        load() {},
                        setAttribute() {},
                        removeAttribute() {},
                        addEventListener() {},
                        removeEventListener() {},
                        appendChild() {},
                        remove() {},
                        querySelectorAll() { return []; },
                        querySelector() { return null; },
                        closest() { return null; },
                        contains() { return false; }
                    };
                }

                const domById = new Map();
                const stageContainer = createMockElement('div', 'voice-video-container');
                const localScreenEl = createMockElement('video', 'participant-screen-user-local', ['livekit-screenshare']);
                const remoteCamEl = createMockElement('video', 'participant-video-user-remote', ['livekit-camera']);
                const remoteCam2El = createMockElement('video', 'participant-video-user-remote-2', ['livekit-camera']);
                domById.set('voice-video-container', stageContainer);
                domById.set(localScreenEl.id, localScreenEl);
                domById.set(remoteCamEl.id, remoteCamEl);
                domById.set(remoteCam2El.id, remoteCam2El);

                globalThis.window = globalThis;
                globalThis.location = { origin: 'https://spokes.test' };
                globalThis.localStorage = {
                    _map: new Map(),
                    getItem(k) { return this._map.get(k) ?? null; },
                    setItem(k, v) { this._map.set(k, String(v)); }
                };
                globalThis.MediaStream = class MediaStream {
                    constructor(tracks = []) { this._tracks = tracks; }
                    getVideoTracks() { return this._tracks; }
                };
                globalThis.requestAnimationFrame = () => 1;
                globalThis.CustomEvent = class CustomEvent {
                    constructor(type, opts) { this.type = type; this.detail = opts?.detail; }
                };
                globalThis.addEventListener = (type, fn) => {
                    if (!windowListeners.has(type)) windowListeners.set(type, new Set());
                    windowListeners.get(type).add(fn);
                };
                globalThis.removeEventListener = (type, fn) => {
                    windowListeners.get(type)?.delete(fn);
                };
                globalThis.dispatchEvent = (ev) => {
                    for (const fn of Array.from(windowListeners.get(ev.type) || [])) fn(ev);
                    return true;
                };
                globalThis.SpokesBridge = {
                    setVoiceCallState(payload) {
                        voiceStatePayloads.push({ ...payload });
                    }
                };
                Object.defineProperty(globalThis, 'navigator', {
                    configurable: true,
                    writable: true,
                    value: {
                        userAgent: 'Mozilla/5.0 (Windows NT 10.0; Win64; x64) Electron/45.0.0 Chrome/156.0.0.0',
                        mediaDevices: {
                            async enumerateDevices() {
                                return [{ kind: 'audioinput', deviceId: 'mic-1', label: 'Studio Mic' }];
                            }
                        }
                    }
                });
                globalThis.document = {
                    visibilityState: 'visible',
                    body: { appendChild() {} },
                    documentElement: { classList: new FakeClassList() },
                    addEventListener() {},
                    createElement: (tag) => createMockElement(tag),
                    getElementById: (id) => domById.get(id) || null,
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
                    TrackUnmuted: 'trackUnmuted',
                    Reconnected: 'reconnected'
                };

                function createMockTrack(kind, source) {
                    const mst = createMockMediaStreamTrack(kind, source);
                    const attached = [];
                    return {
                        sid: `TR_${Math.random().toString(36).slice(2)}`,
                        kind,
                        source,
                        mediaStreamTrack: mst,
                        attachedElements: attached,
                        sender: kind === 'video'
                            ? {
                                getParameters: () => ({ encodings: [{ maxBitrate: 1000000, maxFramerate: 30, scaleResolutionDownBy: 1.0 }] }),
                                async setParameters() {}
                              }
                            : null,
                        attach(el) {
                            const target = el || createMockElement('video');
                            target.srcObject = { getVideoTracks: () => [mst] };
                            target.paused = false; // Mirrors LiveKit SDK calling video.play() inside attach()
                            if (!attached.includes(target)) attached.push(target);
                            return target;
                        },
                        detach() {
                            const copy = [...attached];
                            attached.length = 0;
                            return copy;
                        },
                        stop() { mst.stop(); },
                        async setDegradationPreference() {}
                    };
                }

                let activeRoom = null;
                class MockRoom {
                    constructor() {
                        this.name = 'Screen Share Room';
                        this.handlers = new Map();
                        this.remoteParticipants = new Map();
                        const self = this;
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
                                if (track.kind === 'video') this.videoTrackPublications.set(track.sid, pub);
                                else this.audioTrackPublications.set(track.sid, pub);
                                self.emit(RoomEvent.LocalTrackPublished, pub, this);
                                return pub;
                            },
                            async unpublishTrack(track, stopOnUnpublish = true) {
                                const map = track.kind === 'video' ? this.videoTrackPublications : this.audioTrackPublications;
                                const pub = map.get(track.sid) || { trackSid: track.sid, kind: track.kind, source: track.source, track: undefined };
                                map.delete(track.sid);
                                pub.track = undefined;
                                if (stopOnUnpublish) track.stop();
                                self.emit(RoomEvent.LocalTrackUnpublished, pub, this);
                            }
                        };
                        activeRoom = this;
                    }
                    on(evt, fn) { this.handlers.set(evt, fn); return this; }
                    emit(evt, ...args) { const fn = this.handlers.get(evt); if (fn) fn(...args); }
                    async connect() {}
                    async disconnect() { this.emit(RoomEvent.Disconnected); }
                    static async getLocalDevices() { return [{ deviceId: 'mic-1', label: 'Studio Mic' }]; }
                }

                globalThis.LivekitClient = {
                    Room: MockRoom,
                    RoomEvent,
                    Track,
                    VideoQuality: { LOW: 0, MEDIUM: 1, HIGH: 2 },
                    VideoPresets: { h360: { width: 640, height: 360 }, h720: { width: 1280, height: 720 }, h1080: { width: 1920, height: 1080 }, h2160: { width: 3840, height: 2160 } },
                    VideoPreset: class VideoPreset { constructor(w, h, b, f) { this.width = w; this.height = h; this.maxBitrate = b; this.maxFramerate = f; } },
                    async createLocalAudioTrack() { return createMockTrack('audio', Track.Source.Microphone); },
                    async createLocalScreenTracks() { return [createMockTrack('video', Track.Source.ScreenShare)]; }
                };

                const voice = await import('./voiceInterop.js');
                const dotnetHelper = { async invokeMethodAsync() { return null; } };

                // 1. Connect to voice -> host receives active: true, isScreenSharing: false
                window._spokesHostViewVisible = true;
                await voice.connectToVoice('tok', 'wss://lk.test', dotnetHelper, 64, 'webrtc');
                assert.equal(voiceStatePayloads.at(-1).active, true);
                assert.equal(voiceStatePayloads.at(-1).isScreenSharing, false);

                // 2. Start screen share -> host receives isScreenSharing: true
                await voice.setScreenShareEnabled(true, '1080p', 60, 'motion', false);
                await new Promise(r => setTimeout(r, 30));
                assert.equal(voiceStatePayloads.at(-1).active, true);
                assert.equal(voiceStatePayloads.at(-1).isScreenSharing, true);

                const localScreenPub = Array.from(activeRoom.localParticipant.videoTrackPublications.values())
                    .find(p => p.source === Track.Source.ScreenShare);
                assert.ok(localScreenPub, 'Local screen share publication must exist');
                assert.equal(localScreenEl.paused, false, 'Local screen preview should play while host view is visible');

                // Attach a remote participant webcam
                const remoteTrack = createMockTrack('video', Track.Source.Camera);
                const remotePub = {
                    trackSid: remoteTrack.sid,
                    kind: 'video',
                    source: Track.Source.Camera,
                    track: remoteTrack,
                    isSubscribed: true,
                    enabled: true,
                    quality: 2,
                    setSubscribed(v) { this.isSubscribed = v; },
                    setEnabled(v) { this.enabled = v; },
                    setVideoQuality(q) { this.quality = q; },
                    setVideoDimensions() {}
                };
                const remoteParticipant = { identity: 'user-remote', videoTrackPublications: new Map([[remoteTrack.sid, remotePub]]) };
                activeRoom.remoteParticipants.set('user-remote', remoteParticipant);
                activeRoom.emit(RoomEvent.TrackSubscribed, remoteTrack, remotePub, remoteParticipant);
                await new Promise(r => setTimeout(r, 30));
                assert.equal(remoteCamEl.paused, false, 'Remote camera should play while host view is visible');
                assert.equal(remotePub.enabled, true, 'Remote video RTP must be enabled while host view is visible');

                // 3. Unfocused on second monitor (_spokesHostViewVisible remains true) -> playback & RTP stay active
                window.dispatchEvent(new CustomEvent('spokes:host-view-visible-changed', { detail: { visible: true } }));
                assert.equal(localScreenEl.paused, false, 'Visible unfocused window must keep local preview playing');
                assert.equal(remoteCamEl.paused, false, 'Visible unfocused window must keep remote camera playing');
                assert.equal(remotePub.enabled, true, 'Visible unfocused window must keep remote video RTP enabled');

                // 4. Minimize / hide to tray / switch server (_spokesHostViewVisible = false)
                window._spokesHostViewVisible = false;
                window.dispatchEvent(new CustomEvent('spokes:host-view-visible-changed', { detail: { visible: false } }));

                assert.equal(localScreenEl.paused, true, 'Hidden host view must pause local screen-share preview <video>');
                assert.equal(remoteCamEl.paused, true, 'Hidden host view must pause remote stage <video>');
                assert.equal(localScreenPub.track.mediaStreamTrack.readyState, 'live', 'Pausing preview must NOT stop screen capture track');
                assert.equal(localScreenPub.track.mediaStreamTrack.enabled, true, 'Pausing preview must keep screen capture track enabled');
                assert.equal(remotePub.enabled, false, 'Hidden host view without External PiP must pause remote video RTP');

                // 4b. New remote video attached while host view is hidden must stay paused
                const remoteTrack2 = createMockTrack('video', Track.Source.Camera);
                const remotePub2 = {
                    trackSid: remoteTrack2.sid,
                    kind: 'video',
                    source: Track.Source.Camera,
                    track: remoteTrack2,
                    isSubscribed: true,
                    enabled: true,
                    quality: 2,
                    setSubscribed(v) { this.isSubscribed = v; },
                    setEnabled(v) { this.enabled = v; },
                    setVideoQuality(q) { this.quality = q; },
                    setVideoDimensions() {}
                };
                const remoteParticipant2 = { identity: 'user-remote-2', videoTrackPublications: new Map([[remoteTrack2.sid, remotePub2]]) };
                activeRoom.remoteParticipants.set('user-remote-2', remoteParticipant2);
                activeRoom.emit(RoomEvent.TrackSubscribed, remoteTrack2, remotePub2, remoteParticipant2);
                await new Promise(r => setTimeout(r, 30));
                assert.equal(remoteCam2El.paused, true, 'Track attached while host view is hidden must stay paused');
                assert.equal(remotePub2.enabled, false, 'Newly registered remote video must stay RTP-paused while hidden');

                // 5. Restore host view visibility -> stage <video> elements and remote video RTP resume cleanly
                window._spokesHostViewVisible = true;
                window.dispatchEvent(new CustomEvent('spokes:host-view-visible-changed', { detail: { visible: true } }));

                assert.equal(localScreenEl.paused, false, 'Restoring host view must resume local screen preview');
                assert.equal(remoteCamEl.paused, false, 'Restoring host view must resume remote stage video');
                assert.equal(remoteCam2El.paused, false, 'Restoring host view must resume video attached while hidden');
                assert.equal(remotePub.enabled, true, 'Restoring host view must re-enable remote video RTP');
                assert.equal(remotePub.quality, 2, 'Restoring host view must restore VideoQuality.HIGH');
                assert.equal(remotePub2.enabled, true, 'Restoring host view must re-enable remote video 2 RTP');

                // 6. Stop screen share -> host receives active: true, isScreenSharing: false
                await voice.setScreenShareEnabled(false);
                assert.equal(voiceStatePayloads.at(-1).active, true);
                assert.equal(voiceStatePayloads.at(-1).isScreenSharing, false);

                // 7. Re-start screen share then end externally via OS "Stop sharing" -> host receives isScreenSharing: false
                await voice.setScreenShareEnabled(true, '1080p', 60, 'motion', false);
                assert.equal(voiceStatePayloads.at(-1).isScreenSharing, true);
                const screenPub2 = Array.from(activeRoom.localParticipant.videoTrackPublications.values())
                    .find(p => p.source === Track.Source.ScreenShare);
                screenPub2.track.mediaStreamTrack.dispatchEvent('ended');
                await new Promise(r => setTimeout(r, 40));
                assert.equal(voiceStatePayloads.at(-1).active, true);
                assert.equal(voiceStatePayloads.at(-1).isScreenSharing, false);

                // 8. Disconnect -> active: false, isScreenSharing: false
                await voice.disconnectFromVoice();
                assert.equal(voiceStatePayloads.at(-1).active, false);
                assert.equal(voiceStatePayloads.at(-1).isScreenSharing, false);

                console.log('VOICE_HOST_VISIBILITY_AND_SCREENSHARE_PASS');
                """;

            var runnerPath = Path.Combine(tempDir, "run_host_vis.mjs");
            await File.WriteAllTextAsync(runnerPath, runnerScript);

            var psi = new ProcessStartInfo("node", $"\"{runnerPath}\"")
            {
                WorkingDirectory = tempDir,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var proc = Process.Start(psi)!;
            var stdout = await proc.StandardOutput.ReadToEndAsync();
            var stderr = await proc.StandardError.ReadToEndAsync();
            await proc.WaitForExitAsync();

            Assert.True(proc.ExitCode == 0, $"Node host visibility / screen-share test failed (exit {proc.ExitCode}):\nSTDOUT:\n{stdout}\nSTDERR:\n{stderr}");
            Assert.Contains("VOICE_HOST_VISIBILITY_AND_SCREENSHARE_PASS", stdout);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                try { Directory.Delete(tempDir, recursive: true); } catch { }
            }
        }
    }

    [Fact]
    public async Task Cluster_SingleSourceVoiceActivity_PropagatesIdenticallyAcrossDesktopAndroidAndIos()
    {
        await using var cluster = await VoiceCallClusterSimulator.CreateAsync();

        var desktop = await cluster.CreateClientAsync("desk-ssot", "mats2", VoiceClientPlatform.DesktopElectron);
        var android = await cluster.CreateClientAsync("and-ssot", "admin-first", VoiceClientPlatform.AndroidNative);
        var ios = await cluster.CreateClientAsync("ios-ssot", "mats3", VoiceClientPlatform.IosNative);

        await desktop.ConnectToChannelAsync("ch-ssot", voiceSessionId: "sess-d");
        await android.ConnectToChannelAsync("ch-ssot", voiceSessionId: "sess-a");
        await ios.ConnectToChannelAsync("ch-ssot", voiceSessionId: "sess-i");
        await cluster.SettleAsync();

        // 1. Desktop (mats2) speaks via local Web Audio VAD -> all 3 clients (Desktop, Android, iOS) see mats2 speaking
        await cluster.SimulateActiveSpeakersChangedAsync("ch-ssot", "mats2");
        await cluster.SettleAsync();
        Assert.Contains("mats2", desktop.VoiceService.ActiveSpeakers);
        Assert.Contains("mats2", android.VoiceService.ActiveSpeakers);
        Assert.Contains("mats2", ios.VoiceService.ActiveSpeakers);

        // 2. iOS (mats3) speaks via native local capture VAD (onLocalSpeakingChanged) -> all 3 clients see mats3 speaking
        await cluster.SimulateActiveSpeakersChangedAsync("ch-ssot", "mats3");
        await cluster.SettleAsync();
        Assert.DoesNotContain("mats2", ios.VoiceService.ActiveSpeakers);
        Assert.Contains("mats3", desktop.VoiceService.ActiveSpeakers);
        Assert.Contains("mats3", android.VoiceService.ActiveSpeakers);
        Assert.Contains("mats3", ios.VoiceService.ActiveSpeakers);

        // 3. Active Physical Room DSP takes over mats2 + admin-first:
        //    raw bleed from admin-first is suppressed everywhere, while post-DSP mats2 lights up everywhere (including iOS!)
        cluster.VoiceState.SetPhysicalRoomDspManaged("ch-ssot", "mats2", true);
        cluster.VoiceState.SetPhysicalRoomDspManaged("ch-ssot", "admin-first", true);
        await cluster.SimulateActiveSpeakersChangedAsync("ch-ssot", "admin-first");
        await cluster.SettleAsync();
        Assert.DoesNotContain("admin-first", desktop.VoiceService.ActiveSpeakers);
        Assert.DoesNotContain("admin-first", android.VoiceService.ActiveSpeakers);
        Assert.DoesNotContain("admin-first", ios.VoiceService.ActiveSpeakers);

        cluster.VoiceState.SetPhysicalRoomDspSpeaking("ch-ssot", "mats2", true);
        Assert.Contains("mats2", desktop.VoiceService.ActiveSpeakers);
        Assert.Contains("mats2", android.VoiceService.ActiveSpeakers);
        Assert.Contains("mats2", ios.VoiceService.ActiveSpeakers);

        cluster.VoiceState.SetPhysicalRoomDspSpeaking("ch-ssot", "mats2", false);
        Assert.DoesNotContain("mats2", ios.VoiceService.ActiveSpeakers);
    }

    [Fact]
    public async Task Wave2_UpdateAudioDevices_EnumeratesOncePerCallAndDeduplicatesUnchangedCallbacks()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"spokes_wave2_audio_devices_{Guid.NewGuid():N}");
        try
        {
            const string mockVoicePipJs = """
                export function configureVoicePip() {}
                export function registerPipVideoTrack() {}
                export function unregisterPipVideoTrack() {}
                export function updatePipSpeakingParticipants() {}
                export function updatePipCallState() {}
                export function resetPipOnCallEnd() {}
                export function setInAppPipActive() {}
                export function syncInAppPipElements() {}
                export async function toggleOsPip() { return false; }
                export function closeOsPip() {}
                export function isOsPipSupported() { return false; }
                export function isStageSuppressedByExternalPip() { return false; }
                export function isNativeMobilePipActive() { return false; }
                """;
            await VoiceJsTestModuleLoader.StageStandaloneAsync(tempDir, mockVoicePipJs);

            var runnerScript = """
                import assert from 'node:assert/strict';

                let enumerateCount = 0;
                const dotNetCalls = [];
                const mockDevices = [
                    { kind: 'audioinput', deviceId: 'mic-1', groupId: 'g1', label: 'Studio Mic' },
                    { kind: 'audiooutput', deviceId: 'spk-1', groupId: 'g2', label: 'Desk Speakers' }
                ];

                globalThis.window = {
                    addEventListener: () => {},
                    removeEventListener: () => {},
                    LivekitClient: { Room: { getLocalDevices: async () => { throw new Error('Should not hit LiveKit getLocalDevices when enumerateDevices succeeds'); } } }
                };
                globalThis.document = { getElementById: () => null, addEventListener: () => {}, removeEventListener: () => {} };
                globalThis.localStorage = { getItem: () => null, setItem: () => {}, removeItem: () => {} };
                Object.defineProperty(globalThis, 'navigator', {
                    configurable: true,
                    value: {
                        userAgent: 'Mozilla/5.0 (Windows NT 10.0; Win64; x64)',
                        mediaDevices: {
                            addEventListener: () => {},
                            async enumerateDevices() {
                                enumerateCount++;
                                return mockDevices;
                            }
                        }
                    }
                });

                const { voiceState } = await import('./voice/voiceState.js');
                const interop = await import('./voiceInterop.js');

                voiceState.dotnetHelper = {
                    invokeMethodAsync: async (method, ...args) => {
                        dotNetCalls.push({ method, args });
                    }
                };

                // First call: must enumerate hardware ONCE (not 4x) and notify Blazor once.
                await interop.updateAudioDevices();
                assert.equal(enumerateCount, 1, 'updateAudioDevices should call enumerateDevices exactly once');
                assert.equal(dotNetCalls.length, 1);
                assert.equal(dotNetCalls[0].method, 'OnAudioDevicesUpdated');
                assert.equal(dotNetCalls[0].args[0].length, 2); // default + Studio Mic
                assert.equal(dotNetCalls[0].args[2].length, 2); // default + Desk Speakers

                // Second call with identical hardware list: enumerates once, suppresses redundant Blazor callback.
                await interop.updateAudioDevices();
                assert.equal(enumerateCount, 2);
                assert.equal(dotNetCalls.length, 1, 'Identical device payload should be deduplicated');

                // Hardware plug-in event: changed device list emits a new callback.
                mockDevices.push({ kind: 'audiooutput', deviceId: 'spk-2', groupId: 'g3', label: 'USB Headset' });
                await interop.updateAudioDevices();
                assert.equal(enumerateCount, 3);
                assert.equal(dotNetCalls.length, 2);
                assert.equal(dotNetCalls[1].args[2].length, 3);
                console.log('WAVE2_AUDIO_DEVICES_OK');
                """;

            await File.WriteAllTextAsync(Path.Combine(tempDir, "runner.mjs"), runnerScript);
            var output = await RunNodeRunnerAsync(tempDir, "runner.mjs");
            Assert.Contains("WAVE2_AUDIO_DEVICES_OK", output, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                try { Directory.Delete(tempDir, recursive: true); } catch { }
            }
        }
    }

    [Fact]
    public async Task Wave2_VoiceSpeakingDom_VoiceMessagePlayer_And_MediaLoader404_WorkClientSide()
    {
        var serverRoot = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..",
            "Spokes_Server"));
        var jsDir = Path.Combine(serverRoot, "wwwroot", "js");
        var tempDir = Path.Combine(Path.GetTempPath(), $"spokes_wave2_dom_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            var runnerScript = $$"""
                import assert from 'node:assert/strict';
                import fs from 'node:fs';
                import vm from 'node:vm';

                function makeEl(tag, attrs = {}) {
                    const listeners = new Map();
                    const attrMap = new Map(Object.entries(attrs));
                    const classes = new Set();
                    const el = {
                        nodeType: 1,
                        tagName: tag.toUpperCase(),
                        isConnected: true,
                        dataset: {},
                        children: [],
                        parentElement: null,
                        textContent: '',
                        value: '0',
                        paused: true,
                        currentTime: 0,
                        duration: 0,
                        readyState: 0,
                        classList: {
                            add: (...c) => c.forEach(x => classes.add(x)),
                            remove: (...c) => c.forEach(x => classes.delete(x)),
                            toggle: (c, force) => { if (force === undefined ? !classes.has(c) : force) classes.add(c); else classes.delete(c); },
                            contains: (c) => classes.has(c)
                        },
                        getAttribute: (k) => attrMap.has(k) ? attrMap.get(k) : null,
                        setAttribute: (k, v) => attrMap.set(k, String(v)),
                        removeAttribute: (k) => attrMap.delete(k),
                        hasAttribute: (k) => attrMap.has(k),
                        addEventListener: (ev, fn) => {
                            if (!listeners.has(ev)) listeners.set(ev, new Set());
                            listeners.get(ev).add(fn);
                        },
                        removeEventListener: (ev, fn) => listeners.get(ev)?.delete(fn),
                        dispatch: (ev) => {
                            for (const fn of Array.from(listeners.get(ev) || [])) fn({ target: el });
                        },
                        play() { this.paused = false; this.dispatch('play'); return Promise.resolve(); },
                        pause() { this.paused = true; this.dispatch('pause'); },
                        appendChild(child) { child.parentElement = this; this.children.push(child); return child; },
                        closest(sel) {
                            const wanted = sel.split(',').map(s => s.trim().replace(/^\./, ''));
                            let cur = this;
                            while (cur) {
                                if (wanted.some(c => cur.classList.contains(c))) return cur;
                                cur = cur.parentElement;
                            }
                            return null;
                        },
                        querySelector(sel) { return this.querySelectorAll(sel)[0] || null; },
                        querySelectorAll(sel) {
                            const parts = sel.split(',').map(s => s.trim());
                            const matches = [];
                            const matchOne = (node, p) => {
                                if (p === 'audio') return node.tagName === 'AUDIO';
                                const attrMatches = [...p.matchAll(/\[([^\]=]+)(?:="([^"]*)")?\]/g)];
                                if (attrMatches.length === 0) return false;
                                return attrMatches.every(([, k, v]) => v === undefined ? node.hasAttribute(k) : node.getAttribute(k) === v);
                            };
                            const walk = (n) => {
                                for (const ch of n.children) {
                                    if (parts.some(p => matchOne(ch, p))) matches.push(ch);
                                    walk(ch);
                                }
                            };
                            walk(this);
                            return matches;
                        }
                    };
                    return el;
                }

                // 1. Test voiceSpeakingDom.js
                const body = makeEl('body');
                const stageTile = body.appendChild(makeEl('div', { 'data-voice-speaker-channel': 'ch-1', 'data-voice-speaker-user': 'u-1' }));
                const mutedTile = body.appendChild(makeEl('div', { 'data-voice-speaker-channel': 'ch-1', 'data-voice-speaker-user': 'u-2', 'data-voice-muted': 'true' }));
                const countSpan = body.appendChild(makeEl('span', { 'data-voice-speaker-count': 'ch-1' }));
                const anyDot = body.appendChild(makeEl('span', { 'data-voice-channel-any-speaking': 'ch-1' }));

                const doc = {
                    readyState: 'complete',
                    body,
                    addEventListener: () => {},
                    querySelectorAll: (sel) => body.querySelectorAll(sel)
                };
                const win = { document: doc };
                const ctx1 = vm.createContext({ window: win, document: doc, queueMicrotask, console });
                vm.runInContext(fs.readFileSync({{System.Text.Json.JsonSerializer.Serialize(Path.Combine(jsDir, "voiceSpeakingDom.js"))}}, 'utf8'), ctx1);

                win.spokesVoiceSpeaking.syncChannel('ch-1', ['u-1', 'u-2']);
                assert.equal(stageTile.getAttribute('data-voice-speaking'), 'true');
                assert.equal(mutedTile.getAttribute('data-voice-speaking'), null, 'Muted user must never show speaking ring');
                assert.equal(countSpan.textContent, '2');
                assert.equal(anyDot.getAttribute('data-voice-speaking'), 'true');

                win.spokesVoiceSpeaking.syncChannel('ch-1', []);
                assert.equal(stageTile.getAttribute('data-voice-speaking'), null);
                assert.equal(countSpan.textContent, '0');
                assert.equal(anyDot.getAttribute('data-voice-speaking'), null);

                // 2. Test declarative spokesAudioPlayer in voice-recorder.js
                const playerBody = makeEl('body');
                const pRoot = playerBody.appendChild(makeEl('div', {
                    'data-spokes-audio-player': '',
                    'data-compact': 'false',
                    'data-label-play': 'Play',
                    'data-label-pause': 'Pause'
                }));
                const toggleBtn = pRoot.appendChild(makeEl('button', { 'data-audio-toggle': '' }));
                const playIcon = toggleBtn.appendChild(makeEl('span', { 'data-audio-icon-play': '' }));
                const pauseIcon = toggleBtn.appendChild(makeEl('span', { 'data-audio-icon-pause': '' }));
                pauseIcon.classList.add('d-none');
                const slider = pRoot.appendChild(makeEl('input', { 'data-audio-slider': '' }));
                const curLabel = pRoot.appendChild(makeEl('span', { 'data-audio-current': '' }));
                const durLabel = pRoot.appendChild(makeEl('span', { 'data-audio-duration': '' }));
                const audioEl = pRoot.appendChild(makeEl('audio', { src: '/test.webm' }));

                const doc2 = {
                    readyState: 'complete',
                    body: playerBody,
                    addEventListener: () => {},
                    querySelectorAll: (sel) => playerBody.querySelectorAll(sel)
                };
                const win2 = { document: doc2, MediaRecorder: undefined };
                const ctx2 = vm.createContext({ window: win2, document: doc2, setTimeout, clearTimeout, console });
                vm.runInContext(fs.readFileSync({{System.Text.Json.JsonSerializer.Serialize(Path.Combine(jsDir, "voice-recorder.js"))}}, 'utf8'), ctx2);

                // Simulate WebM Infinity duration probe strictly client-side
                audioEl.duration = Infinity;
                audioEl.dispatch('loadedmetadata');
                assert.equal(audioEl.currentTime, 1e101, 'WebM Infinity duration should trigger client-side seek probe');
                audioEl.duration = 15;
                audioEl.dispatch('durationchange');
                assert.equal(audioEl.currentTime, 0);
                assert.equal(durLabel.textContent, '0:15');

                // Play & timeupdate strictly client-side
                toggleBtn.dispatch('click');
                assert.equal(audioEl.paused, false);
                assert.equal(playIcon.classList.contains('d-none'), true);
                assert.equal(pauseIcon.classList.contains('d-none'), false);
                audioEl.currentTime = 7.5;
                audioEl.dispatch('timeupdate');
                assert.equal(curLabel.textContent, '0:07');
                assert.equal(slider.value, '50');

                // 3. Test mediaLoader.js terminal 404 handling on .attachment-video
                let fetchCalls = 0;
                class FakeHTMLImageElement {}
                const videoBody = makeEl('body');
                const videoWrap = videoBody.appendChild(makeEl('div'));
                videoWrap.classList.add('attachment-video');
                const videoImg = videoWrap.appendChild(makeEl('img', {
                    'data-media-src': '/spokesapi/files/thumb/chat/c1/clip.mp4?t=abc',
                    'data-media-priority': 'chat',
                    'data-media-order': '0'
                }));
                Object.setPrototypeOf(videoImg, FakeHTMLImageElement.prototype);
                Object.defineProperty(videoImg, 'src', {
                    get() { return this.getAttribute('src') || ''; },
                    set(v) {
                        this.setAttribute('src', v);
                        queueMicrotask(() => this.dispatch('error'));
                    }
                });
                const doc3 = {
                    readyState: 'complete',
                    body: videoBody,
                    addEventListener: () => {},
                    querySelectorAll: (sel) => videoBody.querySelectorAll(sel)
                };
                const win3 = {
                    document: doc3,
                    HTMLImageElement: FakeHTMLImageElement,
                    location: { href: 'https://spokes.test/chat/c1', pathname: '/chat/c1' },
                    IntersectionObserver: class {
                        constructor(cb) { this.cb = cb; }
                        observe(target) { queueMicrotask(() => this.cb([{ target, isIntersecting: true }])); }
                        unobserve() {}
                    }
                };
                class MockURL extends URL {
                    static createObjectURL() { return 'blob:mock'; }
                    static revokeObjectURL() {}
                }
                const ctx3 = vm.createContext({
                    window: win3,
                    document: doc3,
                    HTMLImageElement: FakeHTMLImageElement,
                    location: win3.location,
                    navigator: { userAgent: 'Desktop' },
                    IntersectionObserver: win3.IntersectionObserver,
                    URL: MockURL,
                    performance: { now: () => Date.now(), timeOrigin: 0, getEntriesByName: () => [] },
                    fetch: async () => { fetchCalls++; return { ok: false, status: 404 }; },
                    setTimeout: (fn) => { queueMicrotask(fn); return 1; },
                    clearTimeout: () => {},
                    queueMicrotask,
                    console
                });
                vm.runInContext(fs.readFileSync({{System.Text.Json.JsonSerializer.Serialize(Path.Combine(jsDir, "mediaLoader.js"))}}, 'utf8'), ctx3);
                await new Promise(r => setTimeout(r, 30));
                assert.equal(fetchCalls, 1, '404 fetch should run once and terminate without retrying');
                assert.equal(videoWrap.classList.contains('is-error'), true, '404 video poster must mark .attachment-video.is-error');
                assert.equal(videoWrap.classList.contains('is-loaded'), false);

                console.log('WAVE2_CLIENT_CONTROLLERS_OK');
                """;

            await File.WriteAllTextAsync(Path.Combine(tempDir, "runner.mjs"), runnerScript);
            var output = await RunNodeRunnerAsync(tempDir, "runner.mjs");
            Assert.Contains("WAVE2_CLIENT_CONTROLLERS_OK", output, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                try { Directory.Delete(tempDir, recursive: true); } catch { }
            }
        }
    }

    [Fact]
    public async Task VoiceLevelerWorkletAndSettingsHydration_DiscordTargetAndDualToggles_WorkEndToEnd()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"spokes_voice_leveler_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            await VoiceJsTestModuleLoader.StageStandaloneAsync(tempDir);
            var serverRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
            var workletPath = Path.Combine(serverRoot, "Spokes_Server", "wwwroot", "js", "voiceLevelerWorklet.js");
            var workletSrc = await File.ReadAllTextAsync(workletPath, Encoding.UTF8);
            await File.WriteAllTextAsync(Path.Combine(tempDir, "voiceLevelerWorklet.js"), workletSrc, Encoding.UTF8);

            const string runnerScript = """
                import assert from 'node:assert/strict';
                import fs from 'node:fs';
                import vm from 'node:vm';

                // 1. Exercise real voiceLevelerWorklet.js AudioWorkletProcessors
                const registered = new Map();
                class FakeAudioWorkletProcessor {
                    constructor() {
                        this.port = { onmessage: null, postMessage() {} };
                    }
                }
                const workletSrc = fs.readFileSync('./voiceLevelerWorklet.js', 'utf8');
                vm.runInNewContext(workletSrc, {
                    AudioWorkletProcessor: FakeAudioWorkletProcessor,
                    registerProcessor: (name, cls) => registered.set(name, cls),
                    sampleRate: 48000,
                    Math,
                    Number,
                    Float32Array
                });

                const LevelerCls = registered.get('SpokesVoiceLevelerWorklet');
                assert.ok(LevelerCls, 'SpokesVoiceLevelerWorklet must be registered');

                // 1a. Quiet speech (~-28 dBFS) converges to Discord-matched -16.5 dBFS
                const leveler = new LevelerCls({ processorOptions: { enabled: true } });
                let quietSumSq = 0;
                let quietCount = 0;
                for (let b = 0; b < 320; b++) {
                    const inp = new Float32Array(128);
                    const out = new Float32Array(128);
                    for (let i = 0; i < 128; i++) {
                        const t = (b * 128 + i) / 48000;
                        inp[i] = 0.056 * Math.sin(2 * Math.PI * 220 * t);
                    }
                    leveler.process([[inp]], [[out]]);
                    if (b >= 200) {
                        for (let i = 0; i < 128; i++) {
                            quietSumSq += out[i] * out[i];
                            quietCount++;
                        }
                    }
                }
                const quietOutDbfs = 10 * Math.log10((quietSumSq / quietCount) + 1e-12);
                assert.ok(quietOutDbfs >= -18.0 && quietOutDbfs <= -15.0, `Expected -16.5 dBFS convergence, got ${quietOutDbfs.toFixed(2)} dBFS`);

                // 1b. Toggling SET_ENABLED=false smoothly returns gain to 1.0 unity while keeping -1.0 dBFS limiter active
                leveler.port.onmessage({ data: { type: 'SET_ENABLED', value: false } });
                for (let b = 0; b < 80; b++) {
                    const inp = new Float32Array(128).fill(0.04);
                    const out = new Float32Array(128);
                    leveler.process([[inp]], [[out]]);
                }
                assert.ok(Math.abs(leveler._currentGain - 1.0) < 0.02, `Expected smooth unity ramp, got gain=${leveler._currentGain}`);
                const hotIn = new Float32Array(128).fill(1.4);
                const hotOut = new Float32Array(128);
                leveler.process([[hotIn]], [[hotOut]]);
                const peak = Math.max(...hotOut.map(Math.abs));
                assert.ok(peak > 0.78 && peak <= 0.892, `Expected -1.0 dBFS limiter ceiling when disabled, got peak=${peak}`);

                // 1c. Post-silence cough/bump, first-word plosive, cold-start unmute click, sender+receiver cascade,
                //     hot mic stability, scream protection, and ambient noise floor without creep lockout
                const PreCls = registered.get('SpokesPreDenoiserWorklet');
                assert.ok(PreCls, 'SpokesPreDenoiserWorklet must be registered');

                function makeSyllableSample(idx, rmsTarget, plosiveFirstMs = 0, noiseRms = 0.0005) {
                    const t = idx / 48000;
                    const noise = noiseRms * Math.sin(idx * 1.6180339);
                    if (plosiveFirstMs > 0 && t < plosiveFirstMs / 1000) {
                        return 0.80 * Math.sin(2 * Math.PI * 260 * t) + noise;
                    }
                    const syllPhase = (t % 0.28) / 0.28;
                    if (syllPhase >= 0.50) return noise;
                    const env = Math.sin(Math.PI * syllPhase / 0.50);
                    const wave = 0.50 * Math.sin(2 * Math.PI * 185 * t) +
                                 0.30 * Math.sin(2 * Math.PI * 370 * t + 0.3) +
                                 0.20 * Math.sin(2 * Math.PI * 740 * t + 0.9);
                    return (rmsTarget / 0.31) * env * wave + noise;
                }

                function runChain(nodes, seconds, sampleFn, onlyActiveSyllables = false) {
                    const blocks = Math.floor(seconds * 48000 / 128);
                    let sumSq = 0, count = 0, maxPeak = 0;
                    for (let b = 0; b < blocks; b++) {
                        let cur = new Float32Array(128);
                        for (let i = 0; i < 128; i++) cur[i] = sampleFn(b * 128 + i);
                        for (const node of nodes) {
                            const nxt = new Float32Array(128);
                            node.process([[cur]], [[nxt]]);
                            cur = nxt;
                        }
                        const syllPhase = ((b * 128 / 48000) % 0.28) / 0.28;
                        const include = !onlyActiveSyllables || syllPhase < 0.45;
                        for (let i = 0; i < 128; i++) {
                            const a = Math.abs(cur[i]);
                            if (a > maxPeak) maxPeak = a;
                            if (include) { sumSq += cur[i] * cur[i]; count++; }
                        }
                    }
                    return { dbfs: 10 * Math.log10((sumSq / Math.max(1, count)) + 1e-12), maxPeak };
                }

                const receiverOpts = {
                    enabled: true,
                    confirmWindows: 6,
                    envRiseTauSec: 1.0,
                    envFallTauSec: 0.9,
                    maxRiseStepDb: 0.40,
                    speechGateFloorDb: -46.0,
                    maxNoiseFloorDb: -38.0,
                    noiseMarginDb: 6.0
                };

                // Sender (Pre + Post) + Receiver cascade: 50ms cough/bump -> 10s silence -> immediate Sec 1 recovery
                const preNode = new PreCls({ processorOptions: { enabled: true } });
                const senderLv = new LevelerCls({ processorOptions: { enabled: true } });
                const receiverLv = new LevelerCls({ processorOptions: receiverOpts });
                const cascade = [preNode, senderLv, receiverLv];
                const warmCascade = runChain(cascade, 4.0, (idx) => makeSyllableSample(idx, 0.04), true);
                assert.ok(warmCascade.dbfs >= -19.5 && warmCascade.dbfs <= -13.5, `Expected cascade warm speech near -16.5 dBFS, got ${warmCascade.dbfs.toFixed(2)} dBFS`);
                const bumpRes = runChain(cascade, 0.05, (idx) => 0.85 * Math.sin(2 * Math.PI * 280 * idx / 48000));
                assert.ok(bumpRes.maxPeak <= 0.892, `Expected 50ms bump capped <= -1.0 dBFS, got ${bumpRes.maxPeak.toFixed(4)}`);
                runChain(cascade, 10.0, (idx) => 0.0004 * Math.sin(idx * 1.618));
                const sec1AfterSilence = runChain(cascade, 1.0, (idx) => makeSyllableSample(idx, 0.04, 30), true);
                assert.ok(sec1AfterSilence.maxPeak <= 0.892, `Expected first-word plosive capped <= -1.0 dBFS, got ${sec1AfterSilence.maxPeak.toFixed(4)}`);
                assert.ok(sec1AfterSilence.dbfs >= -20.5 && sec1AfterSilence.dbfs <= -12.5, `Expected immediate Sec 1 recovery after cough+silence+plosive, got ${sec1AfterSilence.dbfs.toFixed(2)} dBFS`);

                // Cold start / unmute with 15ms unmute pop must not seed crushed gain
                const unmuteSender = new LevelerCls({ processorOptions: { enabled: true } });
                const unmuteReceiver = new LevelerCls({ processorOptions: receiverOpts });
                const unmuteSec1 = runChain([unmuteSender, unmuteReceiver], 1.2, (idx) => makeSyllableSample(idx, 0.04, 15), true);
                assert.ok(unmuteSec1.dbfs >= -21.0 && unmuteSec1.dbfs <= -12.5, `Expected cold-start unmute pop not to crush Sec 1, got ${unmuteSec1.dbfs.toFixed(2)} dBFS`);

                // Hot mic (-14 dBFS) with pauses must not over-boost
                const hotLv = new LevelerCls({ processorOptions: { enabled: true } });
                runChain([hotLv], 3.0, (idx) => makeSyllableSample(idx, 0.20));
                const hotGainDb = 20 * Math.log10(hotLv._currentGain);
                assert.ok(hotGainDb >= -5.5 && hotGainDb <= 2.5, `Expected hot mic gain natural [-5.5..+2.5 dB], got ${hotGainDb.toFixed(2)} dB`);

                // Ambient -38 dBFS fan noise during 15s silence must NOT pump gain upward OR lock out quiet speech
                const fanLv = new LevelerCls({ processorOptions: receiverOpts });
                runChain([fanLv], 3.0, (idx) => makeSyllableSample(idx, 0.04, 0, 0.0125));
                const gainBeforeFanSilence = fanLv._currentGain;
                runChain([fanLv], 15.0, (idx) => 0.0125 * Math.sqrt(2) * Math.sin(idx * 0.7));
                assert.ok(fanLv._currentGain <= gainBeforeFanSilence * 1.05, `Expected ambient fan silence not to pump gain up (${gainBeforeFanSilence.toFixed(2)} -> ${fanLv._currentGain.toFixed(2)})`);

                // 2. Exercise real voiceInterop.js PascalCase hydration & live toggles
                const directControlUpdates = [];
                globalThis.window = {
                    SpokesBridge: {
                        directRoomAudio: {
                            updateControls: async (payload) => { directControlUpdates.push(payload); }
                        }
                    },
                    addEventListener() {},
                    removeEventListener() {}
                };
                globalThis.document = {
                    getElementById() { return null; },
                    querySelector() { return null; },
                    querySelectorAll() { return []; },
                    addEventListener() {},
                    removeEventListener() {},
                    visibilityState: 'visible'
                };
                Object.defineProperty(globalThis, 'navigator', {
                    value: { userAgent: 'Mozilla/5.0 (Windows NT 10.0; Win64; x64) Electron/45.0.0' },
                    configurable: true
                });
                globalThis.localStorage = { getItem() { return null; }, setItem() {}, removeItem() {} };

                const { voiceState } = await import('./voice/voiceState.js');
                const voice = await import('./voiceInterop.js');

                // PascalCase payload from C# System.Text.Json.JsonSerializer.Serialize(VoiceUserSettings)
                voice.initVoiceSettings(JSON.stringify({
                    MasterVolume: 125,
                    ParticipantVolumes: { u2: 75 },
                    VoiceSensitivity: 68,
                    AutoGainControl: false,
                    IncomingVoiceNormalization: false
                }));
                assert.equal(voiceState.savedVoiceSettings.masterVolume, 125);
                assert.equal(voiceState.savedVoiceSettings.voiceSensitivity, 68);
                assert.equal(voiceState.savedVoiceSettings.participantVolumes.u2, 75);
                assert.equal(voiceState.autoGainControl, false);
                assert.equal(voiceState.incomingVoiceNormalization, false);

                // Live toggle updates active processor and Direct LAN controls
                let lastProcessorAgc = null;
                voiceState.activeLocalAudioProcessor = {
                    setAutoGainEnabled(val) { lastProcessorAgc = val; }
                };
                voiceState.directSessionActive = true;
                directControlUpdates.length = 0;

                voice.setAutoGainControl(true);
                assert.equal(voiceState.autoGainControl, true);
                assert.equal(lastProcessorAgc, true);

                voice.setIncomingVoiceNormalization(true);
                assert.equal(voiceState.incomingVoiceNormalization, true);
                assert.equal(directControlUpdates.length, 1);
                assert.equal(directControlUpdates[0].incomingVoiceNormalization, true);

                voice.setIncomingVoiceNormalization(false);
                assert.equal(voiceState.incomingVoiceNormalization, false);
                assert.equal(directControlUpdates[1].incomingVoiceNormalization, false);

                console.log('VOICE_LEVELER_AND_TOGGLES_OK');
                """;

            await File.WriteAllTextAsync(Path.Combine(tempDir, "runner_leveler.mjs"), runnerScript, Encoding.UTF8);
            var stdout = await RunNodeRunnerAsync(tempDir, "runner_leveler.mjs");
            Assert.Contains("VOICE_LEVELER_AND_TOGGLES_OK", stdout);
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
    public async Task PhysicalDeviceTagging_AccuratelyDetectsDesktopElectronMobileAndBrowsers()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"spokes_voice_device_tag_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            await VoiceJsTestModuleLoader.StageStandaloneAsync(tempDir);

            const string runnerScript = """
                import assert from 'node:assert/strict';

                const storage = new Map();
                globalThis.localStorage = {
                    getItem(k) { return storage.has(k) ? storage.get(k) : null; },
                    setItem(k, v) { storage.set(k, String(v)); }
                };
                globalThis.window = { addEventListener() {} };
                globalThis.document = { addEventListener() {} };

                const voice = await import('./voiceInterop.js');
                const voiceStateMod = await import('./voice/voiceState.js');

                function setEnv({ ua = '', platform = '', userAgentDataPlatform = '', win = {}, doc = {}, brave = false } = {}) {
                    globalThis.window = { addEventListener() {}, ...win };
                    globalThis.document = { addEventListener() {}, ...doc };
                    const navObj = {
                        userAgent: ua,
                        platform,
                        userAgentData: userAgentDataPlatform ? { platform: userAgentDataPlatform } : undefined,
                        brave: brave ? { isBrave: async () => true } : undefined
                    };
                    Object.defineProperty(globalThis, 'navigator', {
                        value: navObj,
                        configurable: true,
                        writable: true
                    });
                }

                // 1. Electron Windows client with window.isSpokesDesktop=true (even with Chrome-only UA & no directRoomAudio bridge)
                setEnv({
                    ua: 'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/136.0.0.0 Safari/537.36',
                    win: { isSpokesDesktop: true }
                });
                assert.equal(voiceStateMod.isDesktopClient(), true);
                assert.equal(voice.detectPhysicalDeviceName(), 'Windows PC (Spokes Desktop)');
                const bootstrap = await voice.getClientVoiceBootstrap();
                assert.equal(bootstrap.isDesktopClient, true);
                assert.equal(bootstrap.deviceName, 'Windows PC (Spokes Desktop)');

                // 2. Electron Windows client via window.SpokesDesktopNative before main-world bridge runs
                setEnv({
                    ua: 'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/136.0.0.0 Safari/537.36',
                    win: { SpokesDesktopNative: { platform: 'desktop' } }
                });
                assert.equal(voice.detectPhysicalDeviceName(), 'Windows PC (Spokes Desktop)');

                // 3. Electron Windows client via window.SpokesBridge.isDesktop
                setEnv({
                    ua: 'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/136.0.0.0 Safari/537.36',
                    win: { SpokesBridge: { isDesktop: true, platform: 'desktop' } }
                });
                assert.equal(voice.detectPhysicalDeviceName(), 'Windows PC (Spokes Desktop)');

                // 4. Electron Windows client via SpokesDesktop UA token without Electron token
                setEnv({
                    ua: 'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/136.0.0.0 Safari/537.36 SpokesDesktop/1.8.0'
                });
                assert.equal(voice.detectPhysicalDeviceName(), 'Windows PC (Spokes Desktop)');

                // 5. Electron macOS and Linux clients
                setEnv({
                    ua: 'Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 Chrome/136.0.0.0 Electron/45.0.0 Safari/537.36 SpokesDesktop/1.8.0'
                });
                assert.equal(voice.detectPhysicalDeviceName(), 'Mac (Spokes Desktop)');

                setEnv({
                    ua: 'Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 Chrome/136.0.0.0 Electron/45.0.0 Safari/537.36 SpokesDesktop/1.8.0'
                });
                assert.equal(voice.detectPhysicalDeviceName(), 'Linux PC (Spokes Desktop)');

                // 6. Regular web browsers remain accurately tagged
                setEnv({ ua: 'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/136.0.0.0 Safari/537.36' });
                assert.equal(voiceStateMod.isDesktopClient(), false);
                assert.equal(voice.detectPhysicalDeviceName(), 'Windows PC (Chrome)');

                setEnv({ ua: 'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/136.0.0.0 Safari/537.36 Edg/136.0.0.0' });
                assert.equal(voice.detectPhysicalDeviceName(), 'Windows PC (Edge)');

                setEnv({ ua: 'Mozilla/5.0 (Windows NT 10.0; Win64; x64; rv:138.0) Gecko/20100101 Firefox/138.0' });
                assert.equal(voice.detectPhysicalDeviceName(), 'Windows PC (Firefox)');

                // 7. iOS Chrome (CriOS) is tagged Chrome, not Safari
                setEnv({ ua: 'Mozilla/5.0 (iPhone; CPU iPhone OS 18_0 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) CriOS/136.0.0.0 Mobile/15E148 Safari/604.1' });
                assert.equal(voice.detectPhysicalDeviceName(), 'iOS Device (Chrome)');

                // 8. Capacitor mobile apps are tagged Spokes App
                setEnv({
                    ua: 'Mozilla/5.0 (Linux; Android 15; Pixel 9) AppleWebKit/537.36 Chrome/136.0.0.0 Mobile Safari/537.36',
                    win: { Capacitor: { isNativePlatform: () => true, getPlatform: () => 'android' } }
                });
                assert.equal(voice.detectPhysicalDeviceName(), 'Android Device (Spokes App)');

                setEnv({
                    ua: 'Mozilla/5.0 (iPhone; CPU iPhone OS 18_0 like Mac OS X) AppleWebKit/605.1.15 Mobile/15E148 Safari/604.1',
                    win: { Capacitor: { isNativePlatform: () => true, getPlatform: () => 'ios' } }
                });
                assert.equal(voice.detectPhysicalDeviceName(), 'iOS Device (Spokes App)');

                console.log('PHYSICAL_DEVICE_TAGGING_OK');
                """;

            await File.WriteAllTextAsync(Path.Combine(tempDir, "runner_device_tag.mjs"), runnerScript, Encoding.UTF8);
            var stdout = await RunNodeRunnerAsync(tempDir, "runner_device_tag.mjs");
            Assert.Contains("PHYSICAL_DEVICE_TAGGING_OK", stdout);
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
    public async Task NativeMobileAdapter_StaleTrackUnsubscribeAfterReplacementSubscribe_PreservesLiveVideoFeed()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"spokes_native_track_race_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            await VoiceJsTestModuleLoader.StageStandaloneAsync(tempDir);

            const string runnerScript = """
                import assert from 'node:assert/strict';

                const storage = new Map();
                globalThis.localStorage = {
                    getItem(k) { return storage.has(k) ? storage.get(k) : null; },
                    setItem(k, v) { storage.set(k, String(v)); }
                };
                globalThis.window = {
                    innerWidth: 412,
                    innerHeight: 915,
                    addEventListener() {},
                    removeEventListener() {},
                    requestAnimationFrame(cb) { cb(); return 1; },
                    cancelAnimationFrame() {}
                };
                globalThis.document = {
                    documentElement: { classList: { add() {}, remove() {}, toggle() {} } },
                    body: null,
                    addEventListener() {},
                    removeEventListener() {},
                    getElementById() { return null; },
                    querySelector() { return null; },
                    querySelectorAll() { return []; }
                };
                Object.defineProperty(globalThis, 'navigator', {
                    value: { userAgent: 'Android' },
                    configurable: true,
                    writable: true
                });

                const { voiceState } = await import('./voice/voiceState.js');
                const adapter = await import('./voice/voiceNativeMobileAdapter.js');

                const listeners = new Map();
                const dotnetCalls = [];
                voiceState.dotnetHelper = {
                    async invokeMethodAsync(method, ...args) {
                        dotnetCalls.push({ method, args });
                    }
                };

                const fakeNativeVoice = {
                    async addListener(name, cb) {
                        listeners.set(name, cb);
                        return { remove() {} };
                    },
                    async connect() {
                        return {
                            connected: true,
                            localIdentity: 'android-user',
                            effectiveFilter: 'rnnoise',
                            remoteParticipants: ['ios-user'],
                            remoteParticipantStates: [{ userId: 'ios-user', isMuted: false, isMicMuted: false, metadata: '' }],
                            remoteMutedParticipants: [],
                            remoteScreenAudioParticipants: [],
                            remoteVideoTracks: []
                        };
                    },
                    async setMasterVolume() {},
                    async setPhysicalRoomRouting() {},
                    async setNoiseFilter() {},
                    async updateVideoSlots() {},
                    async disconnect() {}
                };

                await adapter.connectNativeMobileVoice(fakeNativeVoice, 'tok', 'wss://lk.local', 'rnnoise', false);

                // 1. iOS publishes initial 1080p camera track (TR_OLD)
                listeners.get('onTrackSubscribed')({
                    identity: 'ios-user',
                    kind: 'video',
                    source: 'camera',
                    sid: 'TR_OLD_1080P',
                    muted: false,
                    width: 1920,
                    height: 1080
                });
                assert.equal(voiceState.nativeVideoTracks.get('ios-user')?.has('camera'), true);
                assert.equal(voiceState.nativeVideoTrackSids.get('ios-user:camera'), 'TR_OLD_1080P');

                // 2. iOS switches 1080p -> Auto: TR_NEW subscribes BEFORE stale TR_OLD unsubscribes/unpublishes
                listeners.get('onTrackSubscribed')({
                    identity: 'ios-user',
                    kind: 'video',
                    source: 'camera',
                    sid: 'TR_NEW_AUTO',
                    muted: false,
                    width: 1920,
                    height: 1080
                });
                assert.equal(voiceState.nativeVideoTrackSids.get('ios-user:camera'), 'TR_NEW_AUTO');

                const callsBeforeStale = dotnetCalls.length;
                // Out-of-order stale unsubscribe & unpublish for TR_OLD must NOT kill TR_NEW
                listeners.get('onTrackUnsubscribed')({
                    identity: 'ios-user',
                    kind: 'video',
                    source: 'camera',
                    sid: 'TR_OLD_1080P',
                    muted: true
                });
                listeners.get('onTrackMuted')({
                    identity: 'ios-user',
                    kind: 'video',
                    source: 'camera',
                    sid: 'TR_OLD_1080P',
                    muted: true
                });

                assert.equal(voiceState.nativeVideoTracks.get('ios-user')?.has('camera'), true, 'Live replacement Auto track must remain active');
                assert.equal(voiceState.nativeVideoTrackSids.get('ios-user:camera'), 'TR_NEW_AUTO');
                const falseCalls = dotnetCalls.slice(callsBeforeStale).filter(
                    c => c.method === 'OnVideoTrackStateChanged' && c.args[0] === 'ios-user' && c.args[2] === false
                );
                assert.equal(falseCalls.length, 0, 'Stale TR_OLD events must never emit OnVideoTrackStateChanged(..., false)');

                // 3. Genuine unsubscribe for the active TR_NEW cleanly tears down video state
                listeners.get('onTrackUnsubscribed')({
                    identity: 'ios-user',
                    kind: 'video',
                    source: 'camera',
                    sid: 'TR_NEW_AUTO',
                    muted: true
                });
                assert.equal(voiceState.nativeVideoTracks.has('ios-user'), false, 'Genuine unsubscribe must clear track');
                assert.equal(voiceState.nativeVideoTrackSids.has('ios-user:camera'), false);

                await adapter.cleanupNativeMobileVoiceState();
                console.log('TRACK_REPLACEMENT_RACE_GUARD_OK');
                """;

            await File.WriteAllTextAsync(Path.Combine(tempDir, "runner_track_race.mjs"), runnerScript, Encoding.UTF8);
            var stdout = await RunNodeRunnerAsync(tempDir, "runner_track_race.mjs");
            Assert.Contains("TRACK_REPLACEMENT_RACE_GUARD_OK", stdout);
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
    public async Task NativeMobileCamera_AdaptsSelfPreviewAspectRatio_AndResetsToFrontCameraOnNewCall()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"spokes_native_cam_aspect_reset_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            await VoiceJsTestModuleLoader.StageStandaloneAsync(tempDir);

            const string runnerScript = """
                import assert from 'node:assert/strict';

                const listeners = new Map();
                const connectCalls = [];
                const setCameraCalls = [];
                const switchCameraCalls = [];
                const updateVideoSlotCalls = [];
                const storage = new Map([['spokes_active_camera_id', 'cam-back']]);

                globalThis.localStorage = {
                    getItem(k) { return storage.has(k) ? storage.get(k) : null; },
                    setItem(k, v) { storage.set(k, String(v)); },
                    removeItem(k) { storage.delete(k); }
                };

                function createMockEl(id, rect) {
                    const classes = new Set();
                    const props = new Map();
                    return {
                        id,
                        dataset: {},
                        classList: {
                            add: (...cs) => cs.forEach(c => classes.add(c)),
                            remove: (...cs) => cs.forEach(c => classes.delete(c)),
                            contains: (c) => classes.has(c),
                            toggle: (c, force) => {
                                const on = force === undefined ? !classes.has(c) : Boolean(force);
                                if (on) classes.add(c); else classes.delete(c);
                                return on;
                            }
                        },
                        style: {
                            setProperty(k, v) { props.set(k, String(v)); },
                            getPropertyValue(k) { return props.get(k) || ''; },
                            removeProperty(k) { props.delete(k); }
                        },
                        getBoundingClientRect() { return { ...rect }; }
                    };
                }

                const videoArea = createMockEl('voice-video-area', { left: 0, top: 0, width: 412, height: 700, right: 412, bottom: 700 });
                const videoContainer = createMockEl('voice-video-container', { left: 0, top: 0, width: 412, height: 700, right: 412, bottom: 700 });
                const selfRect = createMockEl('participant-rect-user-local', { left: 12, top: 12, width: 112, height: 148, right: 124, bottom: 160 });
                selfRect.classList.add('participant-rect', 'is-facetime-corner');

                const domById = new Map([
                    ['voice-video-container', videoContainer],
                    ['participant-rect-user-local', selfRect]
                ]);

                const rootClasses = new Set();
                globalThis.document = {
                    documentElement: {
                        classList: {
                            add: (...cs) => cs.forEach(c => rootClasses.add(c)),
                            remove: (...cs) => cs.forEach(c => rootClasses.delete(c)),
                            contains: (c) => rootClasses.has(c),
                            toggle: (c, force) => {
                                const on = force === undefined ? !rootClasses.has(c) : Boolean(force);
                                if (on) rootClasses.add(c); else rootClasses.delete(c);
                                return on;
                            }
                        }
                    },
                    body: {},
                    getElementById: (id) => domById.get(id) || null,
                    querySelector: (sel) => sel === '.voice-video-area' ? videoArea : null,
                    querySelectorAll: () => [],
                    addEventListener: () => {},
                    removeEventListener: () => {}
                };
                globalThis.getComputedStyle = () => ({ display: 'flex', borderTopLeftRadius: '12px' });
                globalThis.innerWidth = 412;
                globalThis.innerHeight = 915;
                globalThis.requestAnimationFrame = (fn) => { fn(); return 1; };
                globalThis.cancelAnimationFrame = () => {};
                globalThis.addEventListener = () => {};
                globalThis.removeEventListener = () => {};

                const nativeCameras = [
                    { cameraId: 'cam-front', label: 'Front Camera', facingMode: 'user', maxWidth: 1920, maxHeight: 1080, maxFps: 30 },
                    { cameraId: 'cam-back', label: 'Back Camera', facingMode: 'environment', maxWidth: 3840, maxHeight: 2160, maxFps: 60 }
                ];

                const nativeVoice = {
                    async addListener(name, cb) {
                        listeners.set(name, cb);
                        return { remove: async () => listeners.delete(name) };
                    },
                    async connect(opts) {
                        connectCalls.push(opts);
                        return {
                            connected: true,
                            localIdentity: 'user-local',
                            effectiveFilter: 'rnnoise',
                            participants: [],
                            remoteVideoTracks: []
                        };
                    },
                    async disconnect() { return { disconnected: true }; },
                    async getCameras() { return { cameras: nativeCameras }; },
                    async setCameraEnabled(opts) {
                        setCameraCalls.push(opts);
                        return { enabled: opts.enabled, facingMode: opts.facingMode || 'user' };
                    },
                    async switchCamera(opts) {
                        switchCameraCalls.push(opts);
                        return { facingMode: opts.facingMode || 'environment' };
                    },
                    async setMasterVolume() {},
                    async setPhysicalRoomRouting() {},
                    async setNoiseFilter() {},
                    async updateVideoSlots(payload) { updateVideoSlotCalls.push(payload); }
                };

                globalThis.window = globalThis;
                Object.defineProperty(globalThis, 'navigator', {
                    value: { userAgent: 'Mozilla/5.0 (Linux; Android 15) AppleWebKit/537.36' },
                    configurable: true
                });
                globalThis.Capacitor = {
                    isNativePlatform: () => true,
                    getPlatform: () => 'android',
                    Plugins: {
                        SpokesNativeVoice: nativeVoice,
                        TelecomConnectionPlugin: {
                            async getNativeCameras() { return { cameras: nativeCameras }; },
                            async addListener() { return { remove: async () => {} }; },
                            async removeAllListeners() {}
                        }
                    }
                };

                const voice = await import('./voiceInterop.js');
                const { voiceState } = await import('./voice/voiceState.js');
                const adapter = await import('./voice/voiceNativeMobileAdapter.js');

                const helper = { invokeMethodAsync: async () => null };

                // 1. Call #1: even with stale localStorage('spokes_active_camera_id' = 'cam-back'), must connect & open on FRONT camera
                const connected1 = await voice.connectToVoice('tok-1', 'wss://lk.test', helper, 64, 'rnnoise');
                assert.equal(connected1, true);
                assert.equal(connectCalls[0].facingMode, 'user', 'New call must connect with front camera facingMode');
                assert.equal(storage.has('spokes_active_camera_id'), false, 'Mobile new call must clear legacy saved camera ID');

                await voice.setCameraEnabled(true, 'Auto', 30);
                assert.equal(setCameraCalls[0].facingMode, 'user', 'First camera enable on new call must use front camera');
                assert.equal(setCameraCalls[0].deviceId, 'cam-front', 'First camera enable must pick front camera deviceId');
                assert.equal(storage.has('spokes_active_camera_id'), false, 'Mobile must never persist spokes_active_camera_id');

                // 2. Before native first-frame dimensions arrive, JS must NOT seed fake landscape 1920x1080 dimensions
                assert.equal(voiceState.nativeVideoDims.has('user-local:camera'), false, 'Must not fabricate landscape capture dims before first native frame');
                assert.equal(selfRect.style.getPropertyValue('--tile-video-aspect'), '', 'Corner tile must not have fake 1.7778 landscape aspect before first frame');

                // Native renderer reports actual rotated portrait dimensions (1080x1920)
                listeners.get('onVideoDimensionsChanged')({
                    identity: 'user-local',
                    source: 'camera',
                    width: 1080,
                    height: 1920,
                    hasAnyVideo: true
                });
                adapter.syncNativeMobileVideoSlots(true);
                assert.equal(selfRect.style.getPropertyValue('--tile-video-aspect'), '0.5625', 'Corner tile must adapt to portrait 9:16 aspect ratio');

                // 3. Flip camera with empty/null deviceId (mobile voice bar Switch Camera button) -> picks primary back camera, clears stale dims, returns resolved ID
                const flippedToBack = await voice.switchActiveCamera(null);
                assert.equal(flippedToBack, 'cam-back', 'Flipping camera from front with null deviceId must resolve to cam-back');
                assert.equal(switchCameraCalls[0].facingMode, 'environment');
                assert.equal(switchCameraCalls[0].deviceId, 'cam-back', 'Flipping with null deviceId must target back camera deviceId');
                assert.equal(voiceState.nativeVideoDims.has('user-local:camera'), false, 'Switching camera lens must clear stale dimensions until new lens reports frame size');
                assert.equal(storage.has('spokes_active_camera_id'), false, 'Switching camera on mobile must not write localStorage');

                // Flipping again with empty string returns to front camera, then switch back to cam-back for same-call toggle check
                const flippedToFront = await voice.switchActiveCamera('');
                assert.equal(flippedToFront, 'cam-front', 'Flipping camera again must return to front camera');
                assert.equal(switchCameraCalls[1].facingMode, 'user');
                assert.equal(switchCameraCalls[1].deviceId, 'cam-front');

                await voice.switchActiveCamera('cam-back');
                await voice.setCameraEnabled(false);
                await voice.setCameraEnabled(true, 'Auto', 30);
                assert.equal(setCameraCalls[2].facingMode, 'environment', 'Toggling camera off/on in the SAME call must preserve back camera');
                assert.equal(setCameraCalls[2].deviceId, 'cam-back');

                // 4. Disconnect and join Call #2 -> MUST reset to front camera
                await voice.disconnectFromVoice();
                assert.equal(voiceState.localCameraFacing, 'user', 'Disconnect must reset localCameraFacing to front camera');
                assert.equal(voiceState.nativeActiveCameraId, '', 'Disconnect must clear nativeActiveCameraId');

                const connected2 = await voice.connectToVoice('tok-2', 'wss://lk.test', helper, 64, 'rnnoise');
                assert.equal(connected2, true);
                assert.equal(connectCalls[1].facingMode, 'user', 'Call #2 must connect with front camera');

                await voice.setCameraEnabled(true, 'Auto', 30);
                assert.equal(setCameraCalls[3].facingMode, 'user', 'Call #2 camera enable must default to front camera');
                assert.equal(setCameraCalls[3].deviceId, 'cam-front', 'Call #2 must select front camera deviceId');

                await voice.disconnectFromVoice();
                console.log('NATIVE_MOBILE_ASPECT_AND_FRONT_CAM_OK');
                """;

            await File.WriteAllTextAsync(Path.Combine(tempDir, "runner_native_cam_aspect_reset.mjs"), runnerScript, Encoding.UTF8);
            var stdout = await RunNodeRunnerAsync(tempDir, "runner_native_cam_aspect_reset.mjs");
            Assert.Contains("NATIVE_MOBILE_ASPECT_AND_FRONT_CAM_OK", stdout);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }
    }

    private static async Task<string> RunNodeRunnerAsync(string workingDirectory, string scriptFileName)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "node",
            Arguments = scriptFileName,
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        using var proc = Process.Start(psi)!;
        var stdout = await proc.StandardOutput.ReadToEndAsync();
        var stderr = await proc.StandardError.ReadToEndAsync();
        await proc.WaitForExitAsync();
        Assert.True(proc.ExitCode == 0, $"Node runner failed (exit {proc.ExitCode}):\nSTDOUT:\n{stdout}\nSTDERR:\n{stderr}");
        return stdout;
    }
}
