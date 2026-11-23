import { NoiseSuppressionManager } from './noiseSuppressionManager.js';
import {
    ensureVoiceLevelerWorkletLoaded,
    createReceiverVoiceLevelerNode,
    createMasterSoftLimiterNode
} from './voiceCaptureConditioner.js';
import {
    configureVoicePip,
    registerPipVideoTrack,
    unregisterPipVideoTrack,
    updatePipSpeakingParticipants,
    updatePipCallState,
    resetPipOnCallEnd,
    setInAppPipActive as setPipInAppActive,
    syncInAppPipElements,
    toggleOsPip as togglePipOsWindow,
    closeOsPip as closePipOsWindow,
    isOsPipSupported as checkOsPipSupported,
    isStageSuppressedByExternalPip
} from './voicePip.js';
import { MediaZoomGestureController } from './mediaZoomGesture.js';
import {
    voiceState,
    PROCESSED_MIC_PREFIX,
    VAD_HANGOVER_MS,
    VAD_ATTACK_FRAMES,
    toArray,
    isBotIdentity,
    isSyntheticIdentity,
    getEffectiveParticipantIdentity,
    getParticipantTrackPublications,
    isScreenShareTrackOrPub,
    isHumanMicrophonePublication,
    getRemoteParticipantsIterable,
    isEffectiveLocalMicMuted,
    reportLocalSpeakingState,
    resetLocalSpeakingState,
    isNativePlatform,
    getNativePlatform,
    isDesktopClient,
    isAppleMobileDevice,
    isMobileVoiceClient,
    resetMobileCameraSelectionForNewCall,
    getNativeVoicePlugin,
    getNativeVoiceTelemetryPlatformTag,
    getAudioSessionPlugin,
    invokeDotNetSafe,
    flushPendingVoiceRoomSnapshot,
    resetVoiceRoomSnapshotSync,
    publishVoiceRoomSnapshot,
    scheduleRoomSnapshotSync,
    applyTrackSenderParameters
} from './voice/voiceState.js';
import {
    configureScreenShareCallbacks,
    getScreenTrackMaxSourceEdge,
    stopAllScreenTracks,
    setScreenShareEnabled,
    setScreenShareAudioEnabled,
    updateScreenShareSettings
} from './voice/voiceScreenShare.js';
import {
    configureDirectLanCallbacks,
    getDirectRoomAudioBridge,
    pushDirectRoomControlsUpdate,
    stopDirectRoomAudioInternal,
    isDesktopDirectRoomSupported,
    prepareDirectRoomEndpoints,
    syncDirectRoomSession,
    getDirectRoomAudioTelemetry
} from './voice/voiceDirectLanCoordinator.js';
import {
    configureNativeMobileCallbacks,
    estimateNativeMobileCaptureMs,
    setNativeMobileVideoTrackActive,
    scheduleNativeMobileVideoSlotsSync,
    cleanupNativeMobileVoiceState,
    syncNativeMobilePhysicalRoomState,
    setNativeMobileVoiceSensitivity,
    reportNativeMobileCameraCapabilitiesToDotnet,
    resolveNativeMobileCaptureConfig,
    fetchNativeMobileCameras,
    syncNativeMobileConnectionState,
    connectNativeMobileVoice
} from './voice/voiceNativeMobileAdapter.js';

export {
    isAppleMobileDevice,
    isDesktopDirectRoomSupported,
    prepareDirectRoomEndpoints,
    syncDirectRoomSession,
    getDirectRoomAudioTelemetry,
    setScreenShareEnabled,
    setScreenShareAudioEnabled,
    updateScreenShareSettings
};

let noiseManager = new NoiseSuppressionManager();
let _iosActiveMicrophoneMode = 'unsupported';
let _wasLocalInActivePhysicalRoom = false;
let _lastCaptureFifoFlushMs = 0;
let _preJoinHydrated = false;
let _preJoinMicDeviceId = '';
let _nativeCallResourcesActive = false;
let _iosAudioInterruptionListener = null;
let _iosLastConfiguredVideoMode = null;
let _webAvSkewPollTimer = null;
let _webAvSkewPrev = { aDelay: 0, aEmit: 0, vDelay: 0, vEmit: 0, vDec: 0, vProc: 0, vFrames: 0 };
function isCrossOriginVoiceUrl(url) {
    if (!url || typeof window === 'undefined' || !window.location?.host) return false;
    try {
        const parsed = new URL(url, window.location.href);
        return Boolean(parsed.host) && parsed.host.toLowerCase() !== window.location.host.toLowerCase();
    } catch (e) {
        return false;
    }
}

export async function prepareVoiceConnection(url, token = '') {
    if (!url || voiceState.usingNativeMobileVoice || getNativeVoicePlugin()) return;
    try {
        if (!window.LivekitClient && typeof window.ensureVoiceScriptsLoaded === 'function') {
            await window.ensureVoiceScriptsLoaded();
        }
        if (!window.LivekitClient?.Room) return;
        // When LiveKit is reverse-proxied on the same origin and no token is supplied, DNS/TCP/TLS
        // to location.host is already open and livekit-client's prepareConnection(url) only issues
        // an unauthenticated HEAD request to the root page (which hits Blazor GET / and logs 405).
        if (!token && !isCrossOriginVoiceUrl(url)) return;
        const warmRoom = new window.LivekitClient.Room();
        if (typeof warmRoom.prepareConnection === 'function') {
            await warmRoom.prepareConnection(url, token || undefined);
        }
    } catch (e) {}
}

export function isRoomConnected() {
    let connected = false;
    if (voiceState.usingNativeMobileVoice) {
        connected = voiceState.nativeConnectionState === 'connected'
            || voiceState.nativeConnectionState === 'reconnecting';
    } else if (voiceState.currentRoom) {
        const ConnectionState = window.LivekitClient?.ConnectionState;
        const disconnectedState = ConnectionState?.Disconnected || 'disconnected';
        if (typeof voiceState.currentRoom.state === 'string') {
            connected = voiceState.currentRoom.state !== disconnectedState && voiceState.currentRoom.state !== 'disconnected';
        } else {
            connected = Boolean(voiceState.currentRoom.localParticipant);
        }
    }
    if (connected) {
        flushPendingVoiceRoomSnapshot();
    }
    return connected;
}

function reportConnectingStage(stage) {
    if (!voiceState.dotnetHelper || !stage) return;
    invokeDotNetSafe('OnVoiceConnectingStageChanged', stage);
}

function flushLocalCaptureFifoIfAvailable() {
    if (voiceState.activeLocalAudioProcessor && typeof voiceState.activeLocalAudioProcessor.flushCaptureFifo === 'function') {
        try {
            if (voiceState.activeLocalAudioProcessor.flushCaptureFifo()) {
                _lastCaptureFifoFlushMs = Date.now();
            }
        } catch (e) {}
    }
}

function applyLowLatencyAudioReceiverHints(track, publication = null) {
    try {
        const receiver = track?.receiver || publication?.track?.receiver;
        if (!receiver) return;
        // 20 ms playout target keeps LAN/WAN playout ultra-crisp without forcing 0ms NetEq underrun/PLC time-stretching
        if ('playoutDelayHint' in receiver) {
            receiver.playoutDelayHint = 0.02;
        }
        if ('jitterBufferTarget' in receiver) {
            receiver.jitterBufferTarget = 20;
        }
    } catch (e) {}
}
function syncLocalStageVideoMirroring() {
    if (voiceState.usingNativeMobileVoice) {
        scheduleNativeMobileVideoSlotsSync();
        return;
    }
    if (typeof document === 'undefined' || !voiceState.currentRoom?.localParticipant?.identity) return;
    const localId = voiceState.currentRoom.localParticipant.identity;
    const stageEl = document.getElementById(`participant-video-${localId}`);
    if (stageEl) {
        stageEl.style.transform = (voiceState.localCameraFacing === 'environment')
            ? 'translateZ(0)'
            : 'translateZ(0) scaleX(-1)';
    }
}

function hasAnyActiveRoomVideo() {
    if (voiceState.localCameraDesired) return true;
    if (voiceState.usingNativeMobileVoice) {
        return voiceState.nativeVideoTracks.size > 0;
    }
    if (!voiceState.currentRoom) return false;
    const Kind = window.LivekitClient?.Track?.Kind;
    const participants = [voiceState.currentRoom.localParticipant, ...getRemoteParticipantsIterable()].filter(Boolean);
    for (const p of participants) {
        for (const pub of getParticipantTrackPublications(p)) {
            const isVideo = pub?.kind === Kind?.Video || pub?.track?.kind === Kind?.Video;
            if (isVideo && pub.track && !pub.isMuted) {
                return true;
            }
        }
    }
    return false;
}

async function syncIosAudioSessionVideoMode() {
    if (!isNativePlatform() || getNativePlatform() !== 'ios' || (!voiceState.currentRoom && !voiceState.usingNativeMobileVoice)) return;
    const audioPlugin = getAudioSessionPlugin();
    if (!audioPlugin || typeof audioPlugin.configureVoiceChat !== 'function') return;
    const desiredVideo = hasAnyActiveRoomVideo();
    if (_iosLastConfiguredVideoMode === desiredVideo) return;
    try {
        await audioPlugin.configureVoiceChat({ enabled: true, video: desiredVideo });
        _iosLastConfiguredVideoMode = desiredVideo;
        console.info(`[Voice] iOS AVAudioSession mode synced (video=${desiredVideo})`);
    } catch (e) {
        console.warn('[Voice] iOS syncIosAudioSessionVideoMode failed:', e);
    }
}

function stopWebAvSkewTelemetryPolling() {
    if (_webAvSkewPollTimer) {
        clearInterval(_webAvSkewPollTimer);
        _webAvSkewPollTimer = null;
    }
    _webAvSkewPrev = { aDelay: 0, aEmit: 0, vDelay: 0, vEmit: 0, vDec: 0, vProc: 0, vFrames: 0 };
}

function startWebAvSkewTelemetryPolling() {
    stopWebAvSkewTelemetryPolling();
    if (voiceState.usingNativeMobileVoice) return;
    _webAvSkewPollTimer = setInterval(async () => {
        if (!voiceState.currentRoom || !voiceState.dotnetHelper || voiceState.usingNativeMobileVoice) return;
        const subPc = voiceState.currentRoom?.engine?.pcManager?.subscriber?.pc;
        if (!subPc || typeof subPc.getStats !== 'function') return;
        try {
            const report = await subPc.getStats();
            if (!report || typeof report.forEach !== 'function') return;
            let aDelay = 0, aEmit = 0, vDelay = 0, vEmit = 0, vDec = 0, vProc = 0, vFrames = 0;
            report.forEach(stat => {
                if (stat.type !== 'inbound-rtp') return;
                if (stat.kind === 'audio') {
                    aDelay += stat.jitterBufferDelay || 0;
                    aEmit += stat.jitterBufferEmittedCount || 0;
                } else if (stat.kind === 'video') {
                    vDelay += stat.jitterBufferDelay || 0;
                    vEmit += stat.jitterBufferEmittedCount || 0;
                    vDec += stat.totalDecodeTime || 0;
                    vProc += stat.totalProcessingDelay || 0;
                    vFrames += stat.framesDecoded || 0;
                }
            });
            if (vEmit <= 0 || aEmit <= 0) return;
            const dADelay = Math.max(0, aDelay - _webAvSkewPrev.aDelay);
            const dAEmit = Math.max(0, aEmit - _webAvSkewPrev.aEmit);
            const dVDelay = Math.max(0, vDelay - _webAvSkewPrev.vDelay);
            const dVEmit = Math.max(0, vEmit - _webAvSkewPrev.vEmit);
            const dVDec = Math.max(0, vDec - _webAvSkewPrev.vDec);
            const dVProc = Math.max(0, vProc - _webAvSkewPrev.vProc);
            const dVFrames = Math.max(0, vFrames - _webAvSkewPrev.vFrames);
            _webAvSkewPrev = { aDelay, aEmit, vDelay, vEmit, vDec, vProc, vFrames };

            const audioJbMs = dAEmit > 0 ? (dADelay * 1000.0) / dAEmit : (aDelay * 1000.0) / aEmit;
            const videoJbMs = dVEmit > 0 ? (dVDelay * 1000.0) / dVEmit : (vDelay * 1000.0) / vEmit;
            const videoDecMs = dVFrames > 0 ? (dVDec * 1000.0) / dVFrames : 0;
            const videoProcMs = dVFrames > 0 ? (dVProc * 1000.0) / dVFrames : (videoJbMs + videoDecMs);
            const skewMs = audioJbMs - videoProcMs;
            const platformTag = isNativePlatform() ? (getNativePlatform() || 'mobile') : (isAppleMobileDevice() ? 'ios-web' : 'web');
            voiceState.dotnetHelper.invokeMethodAsync('OnAvSkewTelemetry', skewMs, audioJbMs, videoJbMs, videoDecMs, platformTag)?.catch?.(() => {});
        } catch (e) {}
    }, 4000);
}

function clampCameraResolutionForCapabilities(resolutionStr, targetFps, maxDim, maxFps) {
    let clampedRes = resolutionStr || 'Auto';
    let clampedFps = targetFps || 30;
    if ((clampedRes === '2160p' || clampedRes === '4k') && maxDim < 3000) {
        clampedRes = maxDim >= 1800 ? '1080p' : 'Auto';
    } else if (clampedRes === '1080p' && maxDim < 1800) {
        clampedRes = 'Auto';
    }
    if (clampedFps > 35 && maxFps < 50) {
        clampedFps = 30;
    }
    return { resolution: clampedRes, fps: clampedFps };
}

function getHardwareCameraCapabilities(mediaStreamTrack) {
    let w = 0, h = 0, fps = 30;
    if (mediaStreamTrack && typeof mediaStreamTrack.getCapabilities === 'function') {
        try {
            const caps = mediaStreamTrack.getCapabilities() || {};
            if (caps.width && caps.width.max) w = caps.width.max;
            if (caps.height && caps.height.max) h = caps.height.max;
            if (caps.frameRate && caps.frameRate.max) fps = Math.round(caps.frameRate.max);
        } catch (e) {}
    }
    if ((w === 0 || h === 0) && mediaStreamTrack && typeof mediaStreamTrack.getSettings === 'function') {
        try {
            const settings = mediaStreamTrack.getSettings() || {};
            w = settings.width || 0;
            h = settings.height || 0;
            if (settings.frameRate) fps = Math.max(fps, Math.round(settings.frameRate));
        } catch (e) {}
    }
    return { maxDim: Math.max(w, h), maxFps: fps };
}

function getVideoElementIdForTrack(participantIdentity, track, publication) {
    return isScreenShareTrackOrPub(track, publication)
        ? `participant-screen-${participantIdentity}`
        : `participant-video-${participantIdentity}`;
}

function clearParticipantVideoDom(participantIdentity, source = null) {
    if (!participantIdentity) return;
    const ids = source === 'screenshare'
        ? [`participant-screen-${participantIdentity}`]
        : (source === 'camera'
            ? [`participant-video-${participantIdentity}`]
            : [`participant-video-${participantIdentity}`, `participant-screen-${participantIdentity}`]);
    for (const id of ids) {
        const el = document.getElementById(id);
        if (el && el.tagName === 'VIDEO') {
            safeRemove(el);
        }
    }
    if (voiceState.fullscreenParticipantId === participantIdentity && voiceState.fullscreenGestureController) {
        refreshParticipantFullscreenPrimaryTarget();
    }
}

function isLocalInActivePhysicalRoom() {
    const localId = voiceState.currentRoom?.localParticipant?.identity;
    return Boolean(localId && voiceState.coLocatedProcessedUsers.has(localId));
}

function isEffectiveSenderAutoGainEnabled() {
    return Boolean(voiceState.autoGainControl) && !isLocalInActivePhysicalRoom();
}

function syncLocalCaptureAutoGainState({ allowWebRtcTrackUpdate = true } = {}) {
    if (voiceState.usingNativeMobileVoice) {
        const nativeVoice = getNativeVoicePlugin();
        if (nativeVoice && typeof nativeVoice.setNoiseFilter === 'function') {
            const inPhysicalRoom = Boolean(voiceState.nativeLocalIdentity && voiceState.coLocatedProcessedUsers.has(voiceState.nativeLocalIdentity));
            nativeVoice.setNoiseFilter({
                noiseFilter: String(voiceState.currentNoiseSuppression || 'rnnoise').toLowerCase(),
                stationaryRoomMode: inPhysicalRoom,
                autoGainControl: Boolean(voiceState.autoGainControl)
            }).catch(() => {});
        }
        return;
    }

    const inRoom = isLocalInActivePhysicalRoom();
    const enteredRoom = inRoom && !_wasLocalInActivePhysicalRoom;
    _wasLocalInActivePhysicalRoom = inRoom;
    const effectiveAutoGain = isEffectiveSenderAutoGainEnabled();

    if (voiceState.activeLocalAudioProcessor && typeof voiceState.activeLocalAudioProcessor.setAutoGainEnabled === 'function') {
        voiceState.activeLocalAudioProcessor.setAutoGainEnabled(effectiveAutoGain);
        if (enteredRoom) {
            flushLocalCaptureFifoIfAvailable();
        }
        return;
    }

    if (allowWebRtcTrackUpdate &&
        voiceState.currentRoom?.localParticipant &&
        !voiceState.isLocalSoftwareMuted &&
        !isAppleMobileDevice()) {
        const filter = voiceState.currentNoiseSuppression || 'webrtc';
        if (filter === 'webrtc') {
            const micPub = toArray(voiceState.currentRoom.localParticipant.audioTrackPublications)
                .find(p => p && p.track && p.source !== window.LivekitClient?.Track?.Source?.ScreenShareAudio);
            const rawTrack = getRawCaptureMediaStreamTrack(micPub?.audioTrack || micPub?.track);
            if (rawTrack && rawTrack.readyState === 'live') {
                const currentAgc = rawTrack.getSettings?.()?.autoGainControl;
                if (currentAgc !== effectiveAutoGain) {
                    const applyPromise = typeof rawTrack.applyConstraints === 'function'
                        ? rawTrack.applyConstraints({ autoGainControl: effectiveAutoGain })
                        : Promise.reject(new Error('applyConstraints unavailable'));
                    Promise.resolve(applyPromise).then(() => {
                        const applied = rawTrack.getSettings?.()?.autoGainControl;
                        if (typeof applied === 'boolean' && applied !== effectiveAutoGain) {
                            reinitializeMicrophone().catch(() => {});
                        }
                    }).catch(() => {
                        reinitializeMicrophone().catch(() => {});
                    });
                }
            }
        }
    }
}

function syncIncomingVoiceNormalizationState() {
    const enabled = Boolean(voiceState.incomingVoiceNormalization);
    _participantGainNodes.forEach((item) => {
        if (item?.levelerNode?.port) {
            try {
                item.levelerNode.port.postMessage({ type: 'SET_ENABLED', value: enabled });
            } catch (_) {}
        }
    });
    pushDirectRoomControlsUpdate({ incomingVoiceNormalization: enabled });
}

function getRawCaptureMediaStreamTrack(livekitTrack) {
    if (!livekitTrack) return null;
    return livekitTrack.processor?.originalTrack
        || livekitTrack._mediaStreamTrack
        || livekitTrack.mediaStreamTrack
        || null;
}

function isLocalScreenShareActive() {
    if (Array.isArray(voiceState.globalScreenTracks) && voiceState.globalScreenTracks.some(t =>
        t && t.kind === 'video' && t.mediaStreamTrack?.readyState !== 'ended'
    )) {
        return true;
    }
    const localParticipant = voiceState.currentRoom?.localParticipant;
    if (!localParticipant) return false;
    const screenSource = window.LivekitClient?.Track?.Source?.ScreenShare || 'screen_share';
    return toArray(localParticipant.videoTrackPublications).some(p => {
        if (!p || p.source !== screenSource) return false;
        const track = p.videoTrack || p.track;
        if (!track) return false;
        return track.mediaStreamTrack ? track.mediaStreamTrack.readyState !== 'ended' : true;
    });
}

function isHostViewHiddenForStage() {
    return typeof window !== 'undefined' && window._spokesHostViewVisible === false;
}

function notifyHostVoiceState(active, isMuted = false) {
    const isActive = Boolean(active);
    const muted = Boolean(isMuted);
    const isScreenSharing = isActive && isLocalScreenShareActive();
    window._spokesInVoiceCall = isActive;
    if (isActive) {
        updatePipCallState({ active: true, isMuted: muted });
    } else {
        resetPipOnCallEnd();
    }
    const channelName = voiceState.currentRoom?.name || '';

    try {
        if (window.SpokesBridge && typeof window.SpokesBridge.setVoiceCallState === 'function') {
            window.SpokesBridge.setVoiceCallState({
                active: isActive,
                channelName,
                isMuted: muted,
                isScreenSharing
            });
        } else if (window.SpokesNative && typeof window.SpokesNative.setVoiceCallState === 'function') {
            window.SpokesNative.setVoiceCallState(isActive, muted);
        }
    } catch (e) {}

    if (isActive) {
        pushDirectRoomControlsUpdate({ isMuted: muted });
    } else {
        stopDirectRoomAudioInternal();
    }

    try {
        window.dispatchEvent(new CustomEvent('spokes:voice-state-changed', {
            detail: {
                active: isActive,
                channelName,
                isMuted: muted,
                isScreenSharing,
                origin: window.location.origin
            }
        }));
    } catch (e) {}
}

async function requestHostCrossServerVoiceHandoff() {
    try {
        if (window.SpokesBridge && typeof window.SpokesBridge.prepareVoiceConnect === 'function') {
            await window.SpokesBridge.prepareVoiceConnect();
        } else if (window.SpokesNative && typeof window.SpokesNative.prepareVoiceConnect === 'function') {
            await window.SpokesNative.prepareVoiceConnect();
        }
    } catch (e) {
        console.warn('[Voice] Cross-server voice handoff warning:', e);
    }
}

if (typeof window !== 'undefined') {
    window.spokesLeaveVoiceCall = async function () {
        try {
            if (voiceState.dotnetHelper) {
                await voiceState.dotnetHelper.invokeMethodAsync('RequestExternalDisconnect');
                return true;
            }
        } catch (e) {
            console.warn('[Voice] RequestExternalDisconnect failed, falling back to direct disconnect:', e);
        }
        await disconnectFromVoice();
        return true;
    };
}

function resumeAllVoiceAudioPlayback() {
    try {
        if (_globalAudioContext && _globalAudioContext.state === 'suspended') {
            _globalAudioContext.resume().catch(() => {});
        }
        if (_captureAudioContext && _captureAudioContext.state === 'suspended') {
            _captureAudioContext.resume().catch(() => {});
        }
        const container = document.getElementById('voice-audio-container');
        if (container) {
            container.querySelectorAll('audio').forEach(a => {
                if (a.paused) {
                    a.play().catch(() => {});
                }
            });
        }
    } catch (e) {}
}

if (typeof window !== 'undefined') {
    window.spokesNativeCallAction = async function (action) {
        const act = String(action || '');
        try {
            if (voiceState.dotnetHelper) {
                await voiceState.dotnetHelper.invokeMethodAsync('OnNativeCallAction', act);
                return true;
            }
        } catch (e) {
            console.warn('[Voice] OnNativeCallAction interop failed, applying fallback:', e);
        }
        if (act === 'hangup') {
            await disconnectFromVoice();
        } else if (act === 'toggleMute') {
            await setMicrophoneEnabled(voiceState.isLocalSoftwareMuted);
        } else if (act === 'toggleCamera') {
            await setCameraEnabled(!voiceState.localCameraDesired, voiceState.lastCameraResolution, voiceState.lastCameraFps);
        } else if (act === 'toggleScreenShare') {
            await setScreenShareEnabled(voiceState.globalScreenTracks.length === 0, voiceState.lastScreenResolution, voiceState.lastScreenFps, voiceState.lastScreenMode, false);
        }
        return true;
    };

    document.addEventListener('visibilitychange', () => {
        if (document.visibilityState !== 'visible' || (!voiceState.currentRoom && !voiceState.usingNativeMobileVoice)) return;
        if (voiceState.usingNativeMobileVoice) {
            syncNativeMobileConnectionState().catch(() => {});
        }
        resumeAllVoiceAudioPlayback();
        refreshVideoLayouts();
    });

    if (typeof window.addEventListener === 'function') {
        window.addEventListener('spokes:host-view-visible-changed', (ev) => {
            if (!voiceState.currentRoom && !voiceState.usingNativeMobileVoice) return;
            const isVisible = ev?.detail?.visible !== false && window._spokesHostViewVisible !== false;
            if (isVisible) {
                resumeAllVoiceAudioPlayback();
            }
            refreshVideoLayouts();
        });
    }
}

function getProcessedMicOwner(track, publication, participant) {
    if (!participant?.identity?.startsWith('bot:')) return null;
    const name = publication?.trackName || track?.name || '';
    if (typeof name === 'string' && name.startsWith(PROCESSED_MIC_PREFIX)) {
        return name.substring(PROCESSED_MIC_PREFIX.length);
    }
    return null;
}

export function detectPhysicalDeviceName() {
    try {
        const nav = typeof navigator !== 'undefined' ? navigator : null;
        const ua = nav?.userAgent || '';
        const platformHint = String(nav?.userAgentData?.platform || nav?.platform || '');
        const desktopClient = isDesktopClient(ua);

        let os = 'Device';
        if (!desktopClient && isAppleMobileDevice()) {
            os = 'iOS Device';
        } else if (/Android/i.test(ua) || /Android/i.test(platformHint)) {
            os = 'Android Device';
        } else if (/Windows/i.test(ua) || /^Win/i.test(platformHint)) {
            os = 'Windows PC';
        } else if (/Macintosh|Mac OS X/i.test(ua) || /Mac/i.test(platformHint)) {
            os = 'Mac';
        } else if (/CrOS/i.test(ua) || /Chrome\s*OS/i.test(platformHint)) {
            os = 'Chromebook';
        } else if (/Linux/i.test(ua) || /Linux/i.test(platformHint)) {
            os = 'Linux PC';
        }

        let client = '';
        if (isNativePlatform()) {
            client = 'Spokes App';
        } else if (desktopClient) {
            client = 'Spokes Desktop';
        } else if (/Edg(?:A|iOS)?\//i.test(ua)) {
            client = 'Edge';
        } else if (/OPR\/|Opera/i.test(ua)) {
            client = 'Opera';
        } else if (/Vivaldi\//i.test(ua)) {
            client = 'Vivaldi';
        } else if (/Brave\//i.test(ua) || Boolean(nav?.brave && typeof nav.brave.isBrave === 'function')) {
            client = 'Brave';
        } else if (/Firefox\/|FxiOS\//i.test(ua)) {
            client = 'Firefox';
        } else if (/Chrome\/|CriOS\//i.test(ua)) {
            client = 'Chrome';
        } else if (/Safari\//i.test(ua)) {
            client = 'Safari';
        }

        return client ? `${os} (${client})` : os;
    } catch (e) {
        return 'Desktop Browser';
    }
}

export function getOrCreatePhysicalDeviceIdentity() {
    let deviceId = '';
    try {
        deviceId = localStorage.getItem('spokes_physical_device_id') || '';
        if (!deviceId) {
            if (typeof crypto !== 'undefined' && typeof crypto.randomUUID === 'function') {
                deviceId = 'dev-' + crypto.randomUUID();
            } else {
                deviceId = 'dev-' + Math.random().toString(36).substring(2, 11) + '-' + Date.now().toString(36);
            }
            localStorage.setItem('spokes_physical_device_id', deviceId);
        }
    } catch (e) {
        deviceId = 'dev-session-fallback';
    }

    const deviceName = detectPhysicalDeviceName();
    return { deviceId, deviceName };
}

function stripChromiumDevicePrefix(label) {
    if (!label || typeof label !== 'string') return '';
    return label.replace(/^(?:Default|Communications)\s*-\s*/i, '').trim();
}

async function resolveSelectedAudioDeviceLabel(storageKey, cacheKey, fetchDevices) {
    try {
        let saved = '';
        try {
            saved = localStorage.getItem(storageKey) || '';
        } catch (e) {}
        if (!saved || saved === 'default') return '';
        const devices = voiceState[cacheKey] && voiceState[cacheKey].length > 0
            ? voiceState[cacheKey]
            : await fetchDevices();
        const match = devices.find(d => d.deviceId === saved);
        const clean = match?.label && match.label !== 'System Default' ? stripChromiumDevicePrefix(match.label) : '';
        return clean && clean !== 'System Default' ? clean : '';
    } catch (e) {
        return '';
    }
}

async function resolveSelectedMicLabelForDirectAudio() {
    const savedLabel = await resolveSelectedAudioDeviceLabel('spokes_active_mic_id', 'cachedMicDevices', getAvailableMicrophones);
    if (savedLabel) return savedLabel;

    // Parity with getActiveMicrophoneId(): when saved mic is empty or 'default', inspect the live
    // published raw microphone MediaStreamTrack so Direct LAN opens the exact same physical mic
    // that Chromium/LiveKit opened (e.g., Corsair HS55 headset vs motherboard microphone).
    try {
        if (voiceState.currentRoom?.localParticipant && window.LivekitClient?.Track) {
            const micPub = toArray(voiceState.currentRoom.localParticipant.audioTrackPublications)
                .find(p => p.source === window.LivekitClient.Track.Source.Microphone && p.track);
            const rawTrack = getRawCaptureMediaStreamTrack(micPub?.track);
            const liveLabel = stripChromiumDevicePrefix(rawTrack?.label || '');
            if (liveLabel && liveLabel !== 'System Default') {
                return liveLabel;
            }
        }
    } catch (e) {}
    return '';
}

const resolveSelectedSpeakerLabelForDirectAudio = () =>
    resolveSelectedAudioDeviceLabel('spokes_active_speaker_id', 'cachedSpeakerDevices', getAvailableSpeakers);

export async function getClientVoiceBootstrap() {
    const native = isNativePlatform();
    const nativePlatform = native ? getNativePlatform() : '';
    const isAndroid = native && nativePlatform === 'android';
    const isIOS = isAppleMobileDevice();
    const directSupported = await isDesktopDirectRoomSupported();
    const identity = getOrCreatePhysicalDeviceIdentity();
    let savedNoiseFilter = 'rnnoise';
    try {
        const storedFilter = localStorage.getItem('spokes_noise_filter');
        if (storedFilter && storedFilter.trim()) {
            savedNoiseFilter = storedFilter.trim();
        }
    } catch (e) {}

    return {
        isIOS,
        isAndroid,
        isNativePlatform: native,
        isDesktopClient: isDesktopClient(),
        isDesktopDirectRoomSupported: directSupported,
        deviceId: identity?.deviceId || '',
        deviceName: identity?.deviceName || '',
        savedNoiseFilter
    };
}

function hydratePreJoinVoiceState(settingsJson, coLocatedUserIds, mutedRoommateUserIds) {
    hydrateSavedVoiceSettingsFromStorage();
    if (settingsJson) {
        initVoiceSettings(settingsJson);
    }
    voiceState.coLocatedProcessedUsers = new Set(Array.isArray(coLocatedUserIds) ? coLocatedUserIds : []);
    voiceState.mutedRoommateUsers = new Set(Array.isArray(mutedRoommateUserIds) ? mutedRoommateUserIds : []);
    _preJoinHydrated = true;
}

export function syncPhysicalRoomState(coLocatedUserIds, mutedRoommateUserIds) {
    voiceState.coLocatedProcessedUsers = new Set(Array.isArray(coLocatedUserIds) ? coLocatedUserIds : []);
    voiceState.mutedRoommateUsers = new Set(Array.isArray(mutedRoommateUserIds) ? mutedRoommateUserIds : []);
    if (voiceState.usingNativeMobileVoice) {
        syncNativeMobilePhysicalRoomState().catch(() => {});
        return;
    }
    syncLocalCaptureAutoGainState({ allowWebRtcTrackUpdate: true });
    reconcileAudioRouting();
}

function getRemoteAudioPublicationsForParticipant(participant) {
    if (!participant) return [];
    const Kind = window.LivekitClient?.Track?.Kind;
    const Source = window.LivekitClient?.Track?.Source;
    const allPubs = getParticipantTrackPublications(participant);
    return allPubs.filter(pub => {
        if (!pub) return false;
        if (pub.kind === Kind?.Audio || pub.track?.kind === Kind?.Audio) return true;
        const src = pub.source || pub.track?.source;
        if (Source && (src === Source.Microphone || src === Source.ScreenShareAudio)) return true;
        const name = pub.trackName || pub.track?.name || '';
        return typeof name === 'string' && name.startsWith(PROCESSED_MIC_PREFIX);
    });
}

function suppressAndDetachAudioPublication(pub, effectiveIdentity) {
    if (!pub) return;
    const trackSid = pub.trackSid || pub.track?.sid || null;
    if (effectiveIdentity) {
        detachParticipantAudio(effectiveIdentity, trackSid);
    }
    if (pub.track && typeof pub.track.detach === 'function') {
        try { pub.track.detach().forEach(el => safeRemove(el)); } catch (e) {}
    }
    const shouldUnsub = pub.isSubscribed !== false || pub.isDesired !== false || (trackSid && !voiceState.suppressedPublicationSids.has(trackSid));
    if (typeof pub.setSubscribed === 'function' && shouldUnsub) {
        if (trackSid) {
            voiceState.suppressedPublicationSids.add(trackSid);
        }
        try { pub.setSubscribed(false); } catch (e) {}
    }
}

function ensureDesiredAudioPublication(pub, participant, effectiveIdentity) {
    if (!pub || !participant || !effectiveIdentity) return;
    const pubSid = pub.trackSid || pub.track?.sid || '';
    if (pubSid) {
        voiceState.suppressedPublicationSids.delete(pubSid);
    }
    if (typeof pub.setSubscribed === 'function' && (!pub.isSubscribed || pub.isDesired === false)) {
        try { pub.setSubscribed(true); } catch (e) {}
        if (!pub.track) {
            return;
        }
    }
    if (!pub.track) {
        return;
    }

    const Source = window.LivekitClient?.Track?.Source;
    const isScreenAudio = Boolean(Source && (pub.source === Source.ScreenShareAudio || pub.track.source === Source.ScreenShareAudio));
    const trackSid = pub.track.sid || pub.trackSid || pub.track.mediaStreamTrack?.id || (isScreenAudio ? 'screenshare_audio' : 'mic');
    const trackKey = `${effectiveIdentity}_${trackSid}`;
    const existing = _participantGainNodes.get(trackKey);

    if (existing &&
        existing.element &&
        existing.element.isConnected &&
        existing.trackMediaStreamTrack &&
        existing.trackMediaStreamTrack === pub.track.mediaStreamTrack) {
        const isScreenMuted = isScreenAudio && voiceState.mutedRemoteScreenAudio.has(effectiveIdentity);
        const isRoommateMuted = !isScreenAudio && voiceState.mutedRoommateUsers.has(effectiveIdentity);
        const savedPct = (!isScreenAudio && voiceState.savedVoiceSettings?.participantVolumes && typeof voiceState.savedVoiceSettings.participantVolumes[effectiveIdentity] === 'number')
            ? voiceState.savedVoiceSettings.participantVolumes[effectiveIdentity]
            : 100;
        const effectiveMuted = isScreenMuted || isRoommateMuted || (!isScreenAudio && savedPct === 0);

        if (existing.gainNode) {
            existing.gainNode.gain.value = (isScreenMuted || isRoommateMuted)
                ? 0
                : (isScreenAudio ? 1.0 : Math.max(0, Math.min(200, savedPct)) / 100.0);
        } else if (existing.element) {
            existing.element.muted = Boolean(effectiveMuted);
            existing.element.volume = effectiveMuted ? 0 : Math.min(1.0, voiceState.masterVolume);
            if (!effectiveMuted && existing.element.paused) {
                existing.element.play?.().catch(() => {});
            }
        }
        return;
    }

    attachTrack(pub.track, participant, pub);
}

/**
 * Single-authority deterministic client audio routing state machine.
 * Reconciles every remote participant's audio tracks against:
 *   1. Muted roommates / Direct LAN peers -> neither processed-mic nor raw-mic
 *   2. Co-located room members (with published processed-mic:<U>) -> processed-mic:<U> only
 *   3. Standard remote callers (or fallback before processed-mic:<U> publishes) -> raw-mic:<U> only
 *   4. Screen share audio & non-processed bot chimes -> always subscribed & attached
 */
function reconcileAudioRouting() {
    if (!voiceState.currentRoom || !voiceState.currentRoom.remoteParticipants) return;

    const localIdentity = voiceState.currentRoom.localParticipant?.identity || '';
    const Source = window.LivekitClient?.Track?.Source;
    const remotes = getRemoteParticipantsIterable();

    const userVoiceMap = new Map(); // userId -> { raw: Array<{pub, participant}>, processed: Array<{pub, participant}> }
    const ensureUserBucket = (uid) => {
        let bucket = userVoiceMap.get(uid);
        if (!bucket) {
            bucket = { raw: [], processed: [] };
            userVoiceMap.set(uid, bucket);
        }
        return bucket;
    };

    remotes.forEach(p => {
        if (!p || !p.identity) return;
        const pubs = getRemoteAudioPublicationsForParticipant(p);
        pubs.forEach(pub => {
            if (!pub) return;
            const src = pub.source || pub.track?.source;
            if (Source && src === Source.ScreenShareAudio) {
                if (p.identity !== localIdentity) {
                    ensureDesiredAudioPublication(pub, p, p.identity);
                }
                return;
            }

            const processedOwner = getProcessedMicOwner(pub.track, pub, p);
            if (processedOwner) {
                ensureUserBucket(processedOwner).processed.push({ pub, participant: p });
                return;
            }

            if (isBotIdentity(p.identity)) {
                // Non-processed bot audio (e.g., join/leave chimes) is always subscribed & attached
                ensureDesiredAudioPublication(pub, p, p.identity);
                return;
            }

            ensureUserBucket(p.identity).raw.push({ pub, participant: p });
        });
    });

    userVoiceMap.forEach((bucket, userId) => {
        const suppressAll =
            !userId ||
            userId === localIdentity ||
            voiceState.mutedRoommateUsers.has(userId) ||
            voiceState.activeDirectLanPeerUsers.has(userId);

        if (suppressAll) {
            bucket.processed.forEach(({ pub }) => suppressAndDetachAudioPublication(pub, userId));
            bucket.raw.forEach(({ pub }) => suppressAndDetachAudioPublication(pub, userId));
            return;
        }

        const preferProcessed = voiceState.coLocatedProcessedUsers.has(userId) && bucket.processed.length > 0;
        if (preferProcessed) {
            let preferredProcessed = bucket.processed[bucket.processed.length - 1];
            for (let i = bucket.processed.length - 1; i >= 0; i--) {
                const candidate = bucket.processed[i];
                if (candidate?.pub?.track && !candidate.pub.isMuted) {
                    preferredProcessed = candidate;
                    break;
                }
            }
            bucket.processed.forEach(({ pub, participant }) => {
                if (pub === preferredProcessed.pub) {
                    ensureDesiredAudioPublication(pub, participant, userId);
                } else {
                    suppressAndDetachAudioPublication(pub, userId);
                }
            });
            // Readiness-gated handoff: only detach raw-mic once processed-mic has finished subscribing
            // its MediaStreamTrack so remote callers never experience a silent gap during DSP startup.
            const isProcessedTrackReady = Boolean(preferredProcessed?.pub?.track);
            if (isProcessedTrackReady) {
                bucket.raw.forEach(({ pub }) => suppressAndDetachAudioPublication(pub, userId));
            } else {
                bucket.raw.forEach(({ pub, participant }) => ensureDesiredAudioPublication(pub, participant, userId));
            }
        } else {
            bucket.processed.forEach(({ pub }) => suppressAndDetachAudioPublication(pub, userId));
            bucket.raw.forEach(({ pub, participant }) => ensureDesiredAudioPublication(pub, participant, userId));
        }
    });
}
export function setNoiseSuppressionType(type) {
    voiceState.currentNoiseSuppression = type;
    if (voiceState.usingNativeMobileVoice) {
        const nativeVoice = getNativeVoicePlugin();
        const inPhysicalRoom = Boolean(voiceState.nativeLocalIdentity && voiceState.coLocatedProcessedUsers.has(voiceState.nativeLocalIdentity));
        if (nativeVoice && typeof nativeVoice.setNoiseFilter === 'function') {
            nativeVoice.setNoiseFilter({
                noiseFilter: String(type || 'rnnoise').toLowerCase(),
                stationaryRoomMode: inPhysicalRoom,
                autoGainControl: Boolean(voiceState.autoGainControl)
            }).then((res) => {
                const effFilter = res?.effectiveFilter || type || 'rnnoise';
                voiceState.nativeEffectiveFilter = effFilter;
                if (voiceState.dotnetHelper) {
                    voiceState.dotnetHelper.invokeMethodAsync('OnLocalAudioPipelineTelemetry', effFilter, estimateNativeMobileCaptureMs(effFilter), getNativeVoiceTelemetryPlatformTag(), 0)?.catch?.(() => {});
                }
            }).catch(() => {});
        }
    }
}

export function setVoiceSensitivity(val) {
    if (typeof val === 'number') {
        voiceState.voiceSensitivity = Math.max(0, Math.min(100, val));
        voiceState.savedVoiceSettings.voiceSensitivity = voiceState.voiceSensitivity;
        setNativeMobileVoiceSensitivity(voiceState.voiceSensitivity);
    }
}

export function setAutoGainControl(enabled) {
    const next = Boolean(enabled);
    const changed = voiceState.autoGainControl !== next;
    voiceState.autoGainControl = next;
    voiceState.savedVoiceSettings.autoGainControl = next;
    if (changed) {
        syncLocalCaptureAutoGainState({ allowWebRtcTrackUpdate: true });
    }
}

export function setIncomingVoiceNormalization(enabled) {
    const next = Boolean(enabled);
    voiceState.incomingVoiceNormalization = next;
    voiceState.savedVoiceSettings.incomingVoiceNormalization = next;
    syncIncomingVoiceNormalizationState();
}

function resolveInitialTrackDimensions(track, publication = null) {
    const lkDims = publication?.videoDimensions || publication?.dimensions || track?.dimensions;
    if (lkDims && lkDims.width > 0 && lkDims.height > 0) {
        return { width: lkDims.width, height: lkDims.height };
    }
    if (track?.mediaStreamTrack && typeof track.mediaStreamTrack.getSettings === 'function') {
        try {
            const s = track.mediaStreamTrack.getSettings() || {};
            if (s.width > 0 && s.height > 0) {
                return { width: s.width, height: s.height };
            }
        } catch (e) {}
    }
    return { width: 0, height: 0 };
}

function syncStageVideoAspectRatio(videoEl, track = null, publication = null) {
    if (!videoEl) return;
    let vw = videoEl.videoWidth || 0;
    let vh = videoEl.videoHeight || 0;
    if (vw <= 0 || vh <= 0) {
        const init = resolveInitialTrackDimensions(track, publication);
        vw = init.width;
        vh = init.height;
    }
    if (vw > 0 && vh > 0) {
        const aspect = vw / vh;
        if (Number.isFinite(aspect) && aspect > 0.1 && aspect < 10) {
            const aspectStr = aspect.toFixed(4);
            videoEl.style.setProperty('--cam-aspect', aspectStr);
            const rectEl = videoEl.closest?.('.participant-rect');
            if (rectEl) {
                const screenEl = rectEl.querySelector?.('video.livekit-screenshare.has-stream');
                if (!screenEl || videoEl === screenEl) {
                    rectEl.style.setProperty('--tile-video-aspect', aspectStr);
                }
            }
        }
    }
}

function safeRemove(el) {
    if (!el) return;
    if (el.tagName === 'AUDIO') {
        el.pause();
        el.removeAttribute('src');
        if (el.srcObject) {
            el.srcObject = null;
        }
        el.load();
        el.style.display = 'none';
        try { if (el.parentNode) el.parentNode.removeChild(el); } catch(e) {}
    } else if (el.tagName === 'VIDEO') {
        // DO NOT REMOVE FROM DOM! The element is strictly managed by Blazor.
        el.pause();
        el.removeAttribute('src');
        if (el.srcObject) {
            el.srcObject = null;
        }
        el.load();
        el.style.opacity = '0';
        el.style.pointerEvents = 'none';
        el.style.removeProperty('--cam-aspect');
        el.classList.remove('has-stream');
        const rectEl = el.closest?.('.participant-rect');
        if (rectEl) {
            const remainingVideo = rectEl.querySelector?.('video.has-stream');
            if (remainingVideo) {
                syncStageVideoAspectRatio(remainingVideo);
            } else {
                rectEl.style.removeProperty('--tile-video-aspect');
            }
        }
    }
}

let _androidHasBluetooth = false;
let _androidHasWiredHeadset = false;
let _androidHasEarpiece = true;
let _globalAudioContext = null;

function getVoiceAudioContext() {
    if (!_globalAudioContext) {
        const AudioCtx = window.AudioContext || window.webkitAudioContext;
        if (AudioCtx) {
            try {
                _globalAudioContext = new AudioCtx({ latencyHint: 'interactive' });
            } catch (e) {
                _globalAudioContext = new AudioCtx();
            }
            if (!isNativePlatform()) {
                try {
                    const savedSink = localStorage.getItem('spokes_active_speaker_id');
                    if (isSyntheticMobileSpeakerRoute(savedSink)) {
                        localStorage.removeItem('spokes_active_speaker_id');
                    } else if (savedSink && typeof _globalAudioContext.setSinkId === 'function') {
                        _globalAudioContext.setSinkId(savedSink === 'default' ? '' : savedSink).catch(() => {});
                    }
                } catch(e) {}
                ensureDesktopPlaybackConditioning(_globalAudioContext);
            }
        }
    }
    if (_globalAudioContext && _globalAudioContext.state === 'suspended') {
        _globalAudioContext.resume().catch(() => {});
    }
    return _globalAudioContext;
}

// Dedicated 48 kHz singleton AudioContext for outgoing microphone AI noise processors
// (RNNoise & DeepFilterNet3). Kept separate from _globalAudioContext so speaker setSinkId()
// routing or non-48 kHz output devices never disturb capture DSP, while avoiding per-unmute
// AudioContext leaks and satisfying LiveKit LocalAudioTrack.setProcessor()'s audioContext check.
let _captureAudioContext = null;

async function getCaptureAudioContext() {
    if (!_captureAudioContext || _captureAudioContext.state === 'closed') {
        const AudioCtx = window.AudioContext || window.webkitAudioContext;
        if (!AudioCtx) return null;
        try {
            _captureAudioContext = new AudioCtx({ sampleRate: 48000, latencyHint: 'interactive' });
        } catch (e) {
            _captureAudioContext = new AudioCtx();
        }
    }
    if (_captureAudioContext && _captureAudioContext.state === 'suspended') {
        try {
            await _captureAudioContext.resume();
        } catch (e) {}
    }
    return _captureAudioContext;
}

// --- Web Audio Volume Pipeline (0% to 200% Master & Per-Participant + -16.5 dBFS Trim & -0.5 dBFS Limiter) ---
let _masterGainNode = null;
let _masterLimiterNode = null;
const _participantGainNodes = new Map(); // trackKey -> { identity, trackSid, source, levelerNode, gainNode, element }

function attachMasterLimiterIfReady(ctx) {
    if (!ctx || !_masterGainNode || _masterGainNode.context !== ctx || _masterLimiterNode) return;
    const limiter = createMasterSoftLimiterNode(ctx);
    if (!limiter) return;
    try {
        _masterGainNode.disconnect();
        _masterGainNode.connect(limiter);
        limiter.connect(ctx.destination);
        _masterLimiterNode = limiter;
    } catch (e) {
        try { _masterGainNode.connect(ctx.destination); } catch (_) {}
    }
}

function ensureDesktopPlaybackConditioning(ctx) {
    if (!ctx || isNativePlatform()) return;
    ensureVoiceLevelerWorkletLoaded(ctx)
        .then((loaded) => {
            if (!loaded || ctx.state === 'closed') return;
            attachMasterLimiterIfReady(ctx);
            _participantGainNodes.forEach((item) => {
                if (!item || item.isScreenAudio || item.levelerNode || !item.source || !item.gainNode) return;
                const levelerNode = createReceiverVoiceLevelerNode(ctx, voiceState.incomingVoiceNormalization);
                if (!levelerNode) return;
                try {
                    item.source.disconnect();
                    item.source.connect(levelerNode);
                    levelerNode.connect(item.gainNode);
                    item.levelerNode = levelerNode;
                } catch (_) {
                    try { item.source.connect(item.gainNode); } catch (__) {}
                }
            });
        })
        .catch(() => {});
}

function getMasterGainNode() {
    const ctx = getVoiceAudioContext();
    if (!ctx) return null;
    if (!_masterGainNode || _masterGainNode.context !== ctx) {
        _masterLimiterNode = null;
        _masterGainNode = ctx.createGain();
        _masterGainNode.gain.value = voiceState.masterVolume;
        const limiter = createMasterSoftLimiterNode(ctx);
        if (limiter) {
            _masterGainNode.connect(limiter);
            limiter.connect(ctx.destination);
            _masterLimiterNode = limiter;
        } else {
            _masterGainNode.connect(ctx.destination);
            ensureDesktopPlaybackConditioning(ctx);
        }
    }
    return _masterGainNode;
}

function attachParticipantAudio(track, participant, element, overrideIdentity = null) {
    if (!participant || !participant.identity || !track.mediaStreamTrack) return;
    
    // Never route local audio back through speakers
    if (participant === voiceState.currentRoom?.localParticipant) return;

    const identity = overrideIdentity || participant.identity;
    if (identity === voiceState.currentRoom?.localParticipant?.identity) return;
    const trackSid = track.sid || track.mediaStreamTrack.id || (track.source === window.LivekitClient.Track.Source.ScreenShareAudio ? 'screenshare_audio' : 'mic');
    const trackKey = `${identity}_${trackSid}`;
    detachParticipantAudio(identity, trackSid, element);

    // Apply active speaker sinkId to the HTML element on desktop/web where SinkId selection is supported
    try {
        const savedSink = localStorage.getItem('spokes_active_speaker_id');
        if (isSyntheticMobileSpeakerRoute(savedSink)) {
            localStorage.removeItem('spokes_active_speaker_id');
        } else {
            const sinkVal = (!savedSink || savedSink === 'default') ? '' : savedSink;
            if (element && typeof element.setSinkId === 'function') {
                element.setSinkId(sinkVal).catch(() => {});
            }
        }
    } catch(e) {}

    const isScreenAudio = track.source === window.LivekitClient.Track.Source.ScreenShareAudio;
    const isScreenMuted = isScreenAudio && voiceState.mutedRemoteScreenAudio.has(identity);
    const isRoommateMuted = !isScreenAudio && voiceState.mutedRoommateUsers.has(identity);
    const trackMediaStreamTrack = track.mediaStreamTrack;

    if (element) {
        element.playsInline = true;
        element.autoplay = true;
    }

    const attachDirectElementAudio = (targetVolume) => {
        const isMuted = isScreenMuted || isRoommateMuted || (!isScreenAudio && voiceState.savedVoiceSettings?.participantVolumes && voiceState.savedVoiceSettings.participantVolumes[identity] === 0);
        element.muted = Boolean(isMuted);
        element.volume = isMuted ? 0 : targetVolume;
        if (!isMuted) {
            element.play?.().catch(() => {});
        }
        _participantGainNodes.set(trackKey, {
            identity,
            trackSid,
            trackMediaStreamTrack,
            isScreenAudio,
            element
        });
    };

    const ctx = getVoiceAudioContext();
    const masterGain = getMasterGainNode();
    if (!ctx || !masterGain || ctx.state === 'suspended') {
        attachDirectElementAudio(Math.min(1.0, voiceState.masterVolume));
        return;
    }

    try {
        const stream = new MediaStream([track.mediaStreamTrack]);
        const source = ctx.createMediaStreamSource(stream);
        const gainNode = ctx.createGain();
        const levelerNode = !isScreenAudio ? createReceiverVoiceLevelerNode(ctx, voiceState.incomingVoiceNormalization) : null;

        let userVolume = 1.0;
        if (isScreenMuted || isRoommateMuted) {
            userVolume = 0;
        } else if (!isScreenAudio && voiceState.savedVoiceSettings?.participantVolumes && typeof voiceState.savedVoiceSettings.participantVolumes[identity] === 'number') {
            userVolume = Math.max(0, Math.min(200, voiceState.savedVoiceSettings.participantVolumes[identity])) / 100.0;
        }

        gainNode.gain.value = userVolume;

        if (levelerNode) {
            source.connect(levelerNode);
            levelerNode.connect(gainNode);
        } else {
            source.connect(gainNode);
            if (!isScreenAudio) {
                ensureDesktopPlaybackConditioning(ctx);
            }
        }
        gainNode.connect(masterGain);

        // Mute raw HTML audio element to prevent double playback on desktop where Web Audio is active
        element.volume = 0;
        element.muted = true;

        _participantGainNodes.set(trackKey, {
            identity,
            trackSid,
            trackMediaStreamTrack,
            isScreenAudio,
            source,
            levelerNode,
            gainNode,
            element
        });
    } catch (e) {
        console.warn("[Voice] Web Audio routing fallback for", identity, trackSid, e);
        attachDirectElementAudio(Math.min(1.0, voiceState.masterVolume));
    }
}

function teardownAudioItem(item, preserveElement = null) {
    if (!item) return;
    try {
        if (item.source) item.source.disconnect();
        if (item.levelerNode) item.levelerNode.disconnect();
        if (item.gainNode) item.gainNode.disconnect();
    } catch (e) {}
    try {
        if (item.element && item.element !== preserveElement) {
            item.element.muted = true;
            item.element.volume = 0;
            safeRemove(item.element);
        }
    } catch (e) {}
}

function detachParticipantAudio(identity, trackSid = null, preserveElement = null) {
    if (!identity) return;
    if (trackSid) {
        const trackKey = `${identity}_${trackSid}`;
        let item = _participantGainNodes.get(trackKey);
        if (!item) {
            for (const [k, v] of _participantGainNodes.entries()) {
                if (v.identity === identity && (v.trackSid === trackSid || k.endsWith(trackSid))) {
                    item = v;
                    _participantGainNodes.delete(k);
                    break;
                }
            }
        } else {
            _participantGainNodes.delete(trackKey);
        }
        teardownAudioItem(item, preserveElement);
        return;
    }

    // Detach all audio tracks for this participant
    const toDelete = [];
    _participantGainNodes.forEach((item, k) => {
        if (item.identity === identity) {
            teardownAudioItem(item, preserveElement);
            toDelete.push(k);
        }
    });
    toDelete.forEach(k => _participantGainNodes.delete(k));
}

function clearAllParticipantAudio() {
    _participantGainNodes.forEach((item) => {
        teardownAudioItem(item);
    });
    _participantGainNodes.clear();
}

export function setMasterVolume(volumePercent) {
    voiceState.masterVolume = Math.max(0, Math.min(200, volumePercent)) / 100.0;
    if (voiceState.usingNativeMobileVoice) {
        const nativeVoice = getNativeVoicePlugin();
        if (nativeVoice) {
            nativeVoice.setMasterVolume?.({ volume: voiceState.masterVolume })?.catch?.(() => {});
            nativeVoice.setDeafened?.({ deafened: voiceState.masterVolume <= 0.001 })?.catch?.(() => {});
        }
        return;
    }
    if (_masterGainNode) {
        _masterGainNode.gain.value = voiceState.masterVolume;
    }
    pushDirectRoomControlsUpdate({ masterVolume: voiceState.masterVolume });
}

export function setParticipantVolume(participantId, volumePercent) {
    if (!participantId) return;
    if (!voiceState.savedVoiceSettings.participantVolumes) {
        voiceState.savedVoiceSettings.participantVolumes = {};
    }
    voiceState.savedVoiceSettings.participantVolumes[participantId] = volumePercent;

    const isMuted = volumePercent === 0;
    const vol = Math.max(0, Math.min(200, volumePercent)) / 100.0;

    if (voiceState.usingNativeMobileVoice) {
        const nativeVoice = getNativeVoicePlugin();
        nativeVoice?.setParticipantVolume?.({ identity: participantId, volume: vol })?.catch?.(() => {});
        return;
    }

    _participantGainNodes.forEach(item => {
        if (item.identity === participantId && !item.isScreenAudio) {
            if (item.element) {
                item.element.muted = isMuted;
            }
            if (item.gainNode) {
                item.gainNode.gain.value = vol;
            }
        }
    });

    if (voiceState.directUserIdToPeerToken.has(participantId)) {
        pushDirectRoomControlsUpdate();
    }
}

export function setRemoteScreenAudioMuted(participantIdentity, isMuted) {
    if (!participantIdentity) return;
    if (isMuted) {
        voiceState.mutedRemoteScreenAudio.add(participantIdentity);
    } else {
        voiceState.mutedRemoteScreenAudio.delete(participantIdentity);
    }

    if (voiceState.usingNativeMobileVoice) {
        getNativeVoicePlugin()?.setRemoteScreenAudioMuted?.({
            identity: participantIdentity,
            muted: Boolean(isMuted)
        })?.catch?.(() => {});
    }

    _participantGainNodes.forEach(item => {
        if (item.identity === participantIdentity && item.isScreenAudio) {
            if (item.element) {
                item.element.muted = isMuted;
            }
            if (item.gainNode) {
                item.gainNode.gain.value = isMuted ? 0 : 1.0;
            }
        }
    });
}

function pickVoiceSetting(settings, camelKey, pascalKey) {
    if (!settings || typeof settings !== 'object') return undefined;
    if (settings[camelKey] !== undefined) return settings[camelKey];
    return settings[pascalKey];
}

export function initVoiceSettings(settingsJson) {
    try {
        const settings = typeof settingsJson === 'string' ? JSON.parse(settingsJson) : settingsJson;
        if (settings && typeof settings === 'object') {
            const rawMasterVolume = pickVoiceSetting(settings, 'masterVolume', 'MasterVolume');
            const rawParticipantVolumes = pickVoiceSetting(settings, 'participantVolumes', 'ParticipantVolumes');
            const rawVoiceSensitivity = pickVoiceSetting(settings, 'voiceSensitivity', 'VoiceSensitivity');
            const rawAutoGainControl = pickVoiceSetting(settings, 'autoGainControl', 'AutoGainControl');
            const rawIncomingNorm = pickVoiceSetting(settings, 'incomingVoiceNormalization', 'IncomingVoiceNormalization');

            const masterVolume = typeof rawMasterVolume === 'number' ? rawMasterVolume : 100;
            const participantVolumes = (rawParticipantVolumes && typeof rawParticipantVolumes === 'object') ? rawParticipantVolumes : {};
            const voiceSensitivity = typeof rawVoiceSensitivity === 'number' ? rawVoiceSensitivity : 50;
            const autoGainControl = typeof rawAutoGainControl === 'boolean' ? rawAutoGainControl : true;
            const incomingVoiceNormalization = typeof rawIncomingNorm === 'boolean' ? rawIncomingNorm : true;

            voiceState.savedVoiceSettings = {
                masterVolume,
                participantVolumes,
                voiceSensitivity,
                autoGainControl,
                incomingVoiceNormalization
            };
            setMasterVolume(masterVolume);
            setVoiceSensitivity(voiceSensitivity);
            setAutoGainControl(autoGainControl);
            setIncomingVoiceNormalization(incomingVoiceNormalization);
            Object.keys(participantVolumes).forEach(id => {
                const vol = participantVolumes[id];
                if (typeof vol === 'number') {
                    setParticipantVolume(id, vol);
                }
            });
        }
    } catch (e) {
        console.warn('[Voice] initVoiceSettings error:', e);
    }
}

function hydrateSavedVoiceSettingsFromStorage() {
    try {
        const raw = localStorage.getItem('spokes_voice_settings_v1');
        if (raw) {
            initVoiceSettings(raw);
        }
    } catch (e) {}
}

function yieldToBrowserFrame() {
    if (typeof requestAnimationFrame === 'function') {
        return new Promise(resolve => requestAnimationFrame(() => resolve()));
    }
    return new Promise(resolve => setTimeout(resolve, 0));
}

function markVoiceDiag(label) {
    try {
        if (typeof window !== 'undefined' && window.spokesChatDiagnostics?.isEnabled?.()) {
            window.spokesChatDiagnostics.mark(label);
        }
    } catch (_) {}
}

export async function connectToVoice(token, url, helper, audioBitrateKbps = 64, noiseSuppression = 'webrtc', preJoinContext = null) {
    markVoiceDiag(`Voice: connectToVoice Start (filter=${noiseSuppression})`);
    voiceState.dotnetHelper = helper;
    voiceState.currentNoiseSuppression = noiseSuppression;
    voiceState.lastAudioDevicesSignature = '';
    resetVoiceRoomSnapshotSync();
    if (isMobileVoiceClient()) {
        resetMobileCameraSelectionForNewCall();
    }
    let micPrepToken = null;
    let prefetchedMicPromise = null;

    if (preJoinContext && typeof preJoinContext === 'object') {
        hydratePreJoinVoiceState(
            preJoinContext.settingsJson ?? null,
            preJoinContext.coLocatedUserIds ?? [],
            preJoinContext.mutedRoommateUserIds ?? []);
        _preJoinMicDeviceId = typeof preJoinContext.preferredMicDeviceId === 'string'
            ? preJoinContext.preferredMicDeviceId
            : '';
    } else {
        _preJoinMicDeviceId = '';
    }
    const initialMuted = Boolean(preJoinContext?.initialMuted);

    const hasNativeAndroidVoice = Boolean(getNativeVoicePlugin());
    if (!hasNativeAndroidVoice) {
        // Ensure LivekitClient is loaded on-demand if idle preload hasn't finished yet
        if (!window.LivekitClient && typeof window.ensureVoiceScriptsLoaded === 'function') {
            markVoiceDiag('Voice: ensureVoiceScriptsLoaded Start');
            await window.ensureVoiceScriptsLoaded();
            markVoiceDiag('Voice: ensureVoiceScriptsLoaded Done');
            await yieldToBrowserFrame();
        }
        if (!window.LivekitClient) {
            console.error('[Voice] LivekitClient not found! Ensure the CDN script is loaded.');
            return false;
        }
    }

    try {
        if (!_preJoinHydrated) {
            hydrateSavedVoiceSettingsFromStorage();
        }
        _preJoinHydrated = false;
        await requestHostCrossServerVoiceHandoff();

        await cleanupNativeMobileVoiceState();
        getVoiceAudioContext();
        if (voiceState.currentRoom) {
            const staleRoom = voiceState.currentRoom;
            voiceState.currentRoom = null;
            try {
                console.warn('[Voice] Existing room found in connectToVoice, disconnecting first...');
                stopAllLocalMediaTracks(staleRoom);
                detachAllRoomMedia(staleRoom);
                await staleRoom.disconnect(true);
            } catch (e) {
                console.warn('[Voice] Error disconnecting previous room:', e);
            }
        }

        cleanupLocalHardwareMuteMonitor();
        voiceState.isLocalSoftwareMuted = false;
        voiceState.isLocalHardwareMuted = false;
        voiceState.suppressedPublicationSids.clear();

        // Activate native mobile call audio session (Android Telecom MODE_IN_COMMUNICATION / iOS AVAudioSession VoiceChat)
        // BEFORE voiceState.currentRoom.connect() so incoming WebRTC tracks attach directly to active voice-call audio streams on first join.
        if (isNativePlatform()) {
            _nativeCallResourcesActive = true;
            try {
                if (window.Capacitor?.Plugins?.KeepAwake) {
                    await window.Capacitor.Plugins.KeepAwake.keepAwake();
                }

                const platform = getNativePlatform();
                const audioPlugin = getAudioSessionPlugin();
                if (platform === 'ios' && audioPlugin) {
                    const res = await audioPlugin.configureVoiceChat({ enabled: true, video: false });
                    _iosLastConfiguredVideoMode = false;
                    _iosActiveMicrophoneMode = res?.microphoneMode || 'unsupported';
                    if (typeof audioPlugin.addListener === 'function') {
                        try {
                            if (_iosAudioInterruptionListener && typeof _iosAudioInterruptionListener.remove === 'function') {
                                await _iosAudioInterruptionListener.remove();
                            }
                            _iosAudioInterruptionListener = await audioPlugin.addListener('audioInterruptionEnded', () => {
                                console.info('[Voice] iOS audio interruption ended — resuming audio pipelines');
                                resumeAllVoiceAudioPlayback();
                            });
                        } catch (e) {}
                    }
                    console.info(`[Voice] iOS Native Session Initialized (Pre-Connect): Category=${res?.category}, Mode=${res?.mode}, Output=${res?.activeOutput}, MicMode=${_iosActiveMicrophoneMode}`);
                } else if (platform === 'ios') {
                    console.warn('[Voice] iOS Native Session plugin NOT FOUND');
                }

                if (platform === 'android' && window.Capacitor.Plugins.TelecomConnectionPlugin) {
                    voiceState.activeNativeSpeakerId = 'speaker';
                    try {
                        await window.Capacitor.Plugins.TelecomConnectionPlugin.removeAllListeners();
                    } catch (e) {}

                    window.Capacitor.Plugins.TelecomConnectionPlugin.addListener('onAudioStateChanged', (data) => {
                        if (!data || !data.state) return;

                        if (typeof data.state.hasBluetooth === 'boolean') _androidHasBluetooth = data.state.hasBluetooth;
                        if (typeof data.state.hasWiredHeadset === 'boolean') _androidHasWiredHeadset = data.state.hasWiredHeadset;
                        if (typeof data.state.hasEarpiece === 'boolean') _androidHasEarpiece = data.state.hasEarpiece;

                        const reportedRoute = (typeof data.state.route === 'string' && data.state.route !== 'unknown')
                            ? data.state.route
                            : '';

                        if (reportedRoute) {
                            voiceState.activeNativeSpeakerId = reportedRoute;
                        }
                        updateAudioDevices().catch(() => {});
                        refreshLocalAudioPipelineTelemetry();
                    });
                    window.Capacitor.Plugins.TelecomConnectionPlugin.addListener('onCallEnded', () => {
                        console.info('[Voice] Android TelecomConnectionPlugin reported onCallEnded');
                        if ((voiceState.currentRoom || voiceState.usingNativeMobileVoice) && typeof window.spokesLeaveVoiceCall === 'function') {
                            window.spokesLeaveVoiceCall().catch(() => {});
                        }
                    });
                    await window.Capacitor.Plugins.TelecomConnectionPlugin.startCall();
                }
            } catch (e) {
                console.warn('[Voice] Capacitor native plugins error on pre-connect:', e);
            }
        }

        // Unified Native Mobile Voice & Video (Android & iOS): SpokesNativeVoice is the sole
        // voice/video engine on native mobile — never silently fall back to WebView LiveKit.
        const nativeVoice = getNativeVoicePlugin();
        if (nativeVoice) {
            const availRes = typeof nativeVoice.isAvailable === 'function'
                ? await nativeVoice.isAvailable()
                : { available: true };
            if (!availRes?.available) {
                throw new Error('SpokesNativeVoice plugin reported unavailable on native mobile platform.');
            }
            return await connectNativeMobileVoice(nativeVoice, token, url, noiseSuppression, initialMuted);
        }

        // Universal H.264: Leverages on-die hardware acceleration across iOS (VideoToolbox), Android (MediaCodec), and Desktop/Web (NVENC/QuickSync/AMF/VideoToolbox)
        let preferredCodec = 'h264';
        const preferredMicDeviceId = resolvePreferredMicDeviceIdFast();

        voiceState.currentRoom = new window.LivekitClient.Room({
            adaptiveStream: false,
            dynacast: true,
            audioCaptureDefaults: preferredMicDeviceId ? { deviceId: preferredMicDeviceId } : undefined,
            publishDefaults: {
                audioBitrate: audioBitrateKbps * 1000,
                audioPreset: { maxBitrate: audioBitrateKbps * 1000 },
                dtx: false,
                videoCodec: preferredCodec,
                backupCodec: true,
                screenShareEncoding: {
                    maxBitrate: 12000000,
                    maxFramerate: 60
                }
            }
        });

        // Setup event listeners for the specific events you'll need
        voiceState.currentRoom
            .on(window.LivekitClient.RoomEvent.TrackSubscribed, handleTrackSubscribed)
            .on(window.LivekitClient.RoomEvent.TrackUnsubscribed, handleTrackUnsubscribed)
            .on(window.LivekitClient.RoomEvent.TrackPublished, handleRemoteAudioPublicationChanged)
            .on(window.LivekitClient.RoomEvent.TrackUnpublished, handleRemoteAudioPublicationChanged)
            .on(window.LivekitClient.RoomEvent.Disconnected, handleDisconnect)
            .on(window.LivekitClient.RoomEvent.LocalTrackPublished, handleLocalTrackPublished)
            .on(window.LivekitClient.RoomEvent.LocalTrackUnpublished, handleLocalUnpublished)
            .on(window.LivekitClient.RoomEvent.ParticipantConnected, handleParticipantConnected)
            .on(window.LivekitClient.RoomEvent.ParticipantDisconnected, handleParticipantDisconnected)
            .on(window.LivekitClient.RoomEvent.TrackMuted, handleTrackMuted)
            .on(window.LivekitClient.RoomEvent.TrackUnmuted, handleTrackUnmuted);
        if (window.LivekitClient.RoomEvent.Reconnecting) {
            voiceState.currentRoom.on(window.LivekitClient.RoomEvent.Reconnecting, () => { voiceState.isRoomReconnecting = true; });
        }
        if (window.LivekitClient.RoomEvent.SignalReconnecting) {
            voiceState.currentRoom.on(window.LivekitClient.RoomEvent.SignalReconnecting, () => { voiceState.isRoomReconnecting = true; });
        }
        if (window.LivekitClient.RoomEvent.Reconnected) {
            voiceState.currentRoom.on(window.LivekitClient.RoomEvent.Reconnected, handleRoomReconnected);
        }

        voiceState.isLocalSoftwareMuted = initialMuted;
        voiceState.isLocalHardwareMuted = false;
        voiceState.micOperationSeq = 0;

        // Overlap mic capture + WASM denoiser warmup with LiveKit room signaling/ICE negotiation
        // on desktop/web clients, while deferring track.setProcessor(processor) attachment until
        // voiceState.currentRoom.connect() resolves so PushPullFIFO never accumulates pre-publish backlog
        // and autoGainEnabled is evaluated with the authenticated localParticipant.identity.
        if (!initialMuted && !isNativePlatform()) {
            micPrepToken = { cancelled: false };
            _activeMicPrepToken = micPrepToken;
            prefetchedMicPromise = prepareLocalMicTrackAndProcessor(
                voiceState.currentNoiseSuppression,
                preferredMicDeviceId,
                () => Boolean(micPrepToken?.cancelled || voiceState.isLocalSoftwareMuted || voiceState.micOperationSeq > 1),
                true
            ).catch(() => null);
        }

        reportConnectingStage('connecting');
        markVoiceDiag('Voice: room.connect Start');
        await voiceState.currentRoom.connect(url, token);
        markVoiceDiag('Voice: room.connect Done');
        if (typeof voiceState.currentRoom.startAudio === 'function') {
            voiceState.currentRoom.startAudio().catch(() => {});
        }
        notifyHostVoiceState(true, isEffectiveLocalMicMuted());
        console.log('[Voice] Connected to room:', voiceState.currentRoom.name);
        _nativeCallResourcesActive = true;
        voiceState.localCameraDesired = false;

        syncLocalCaptureAutoGainState({ allowWebRtcTrackUpdate: false });
        reconcileAudioRouting();
        rebindRemoteAudioElementsPlayback(false);

        configureVoicePip(voiceState.dotnetHelper, {
            onToggleMute: () => window.spokesNativeCallAction('toggleMute'),
            onToggleCamera: () => window.spokesNativeCallAction('toggleCamera'),
            onToggleScreenShare: () => window.spokesNativeCallAction('toggleScreenShare'),
            onHangUp: () => window.spokesLeaveVoiceCall(),
            onReturnToCall: () => window.spokesNativeCallAction('returnToCall'),
            onRestoreStageTracks: () => reattachAllTracks()
        });
        updatePipCallState({
            active: true,
            channelName: '',
            localUserId: voiceState.currentRoom.localParticipant?.identity || '',
            isNativePlatform: false,
            isMuted: isEffectiveLocalMicMuted(),
            isVideoOn: false,
            isScreenSharing: false
        });

        if (voiceState.dotnetHelper) {
            const existingParticipants = getRemoteParticipantsIterable()
                .map(p => getEffectiveParticipantIdentity(p))
                .filter(id => Boolean(id) && !isSyntheticIdentity(id));
            invokeDotNetSafe('OnRoomConnected', existingParticipants);
            publishVoiceRoomSnapshot('SyncRoomSnapshotAsync');
        }

        // Turn on microphone by default when joining a voice channel (unless muted during connect)
        // Executed asynchronously after yielding a frame so UI paint & Blazor channel mount never stall.
        if (typeof voiceState.currentRoom?.name === 'string' && voiceState.currentRoom.name.startsWith('channel-')) {
            voiceState.activeVoiceChannelId = voiceState.currentRoom.name.slice('channel-'.length);
        }
        if (!voiceState.isLocalSoftwareMuted && voiceState.micOperationSeq === 0) {
            reportConnectingStage('microphone');
            yieldToBrowserFrame()
                .then(() => setMicrophoneEnabled(true, prefetchedMicPromise).catch(e => console.warn('[Voice] Failed to enable microphone:', e)))
                .then(() => updateAudioDevices().then(() => markVoiceDiag('Voice: updateAudioDevices Done')).catch(e => console.warn('[Voice] Failed to update audio devices:', e)));
        } else {
            cancelActiveMicPrep();
            yieldToBrowserFrame()
                .then(() => updateAudioDevices().then(() => markVoiceDiag('Voice: updateAudioDevices Done')).catch(() => {}))
                .catch(() => {});
        }

        startWebAvSkewTelemetryPolling();
        return true;
    } catch (error) {
        if (micPrepToken) micPrepToken.cancelled = true;
        cancelActiveMicPrep();
        console.error('[Voice] Connection failed:', error);
        try {
            await disconnectFromVoice();
        } catch (cleanupErr) {}
        return false;
    }
}

function stopLivekitTrackCompletely(track) {
    if (!track) return;
    try {
        const rawTrack = getRawCaptureMediaStreamTrack(track);
        if (rawTrack && typeof rawTrack.stop === 'function') {
            rawTrack.stop();
        }
    } catch (e) {}
    try {
        if (track.mediaStreamTrack && typeof track.mediaStreamTrack.stop === 'function') {
            track.mediaStreamTrack.stop();
        }
    } catch (e) {}
    try {
        if (typeof track.stop === 'function') {
            track.stop();
        }
    } catch (e) {}
}

function stopAllLocalMediaTracks(room = null) {
    stopAllScreenTracks(room);
    const targetRoom = room || voiceState.currentRoom;
    if (targetRoom && targetRoom.localParticipant) {
        try {
            const pubs = getParticipantTrackPublications(targetRoom.localParticipant);
            for (const pub of pubs) {
                const t = pub?.videoTrack || pub?.audioTrack || pub?.track;
                if (t) {
                    stopLivekitTrackCompletely(t);
                }
            }
        } catch (e) {
            console.warn('[Voice] Error stopping local media tracks:', e);
        }
    }
    if (_localMicVadEntry) {
        try {
            if (_localMicVadEntry.rawTrack && typeof _localMicVadEntry.rawTrack.stop === 'function') {
                _localMicVadEntry.rawTrack.stop();
            }
            if (_localMicVadEntry.track && typeof _localMicVadEntry.track.stop === 'function') {
                _localMicVadEntry.track.stop();
            }
        } catch (e) {}
    }
}

function cleanupLocalHardwareMuteMonitor() {
    if (typeof voiceState.localHardwareMuteCleanup === 'function') {
        try { voiceState.localHardwareMuteCleanup(); } catch (e) {}
    }
    voiceState.localHardwareMuteCleanup = null;
}

function bindLocalHardwareMuteMonitor(livekitTrack) {
    cleanupLocalHardwareMuteMonitor();
    if (!livekitTrack) return;

    // Mobile OSes (iOS/Android) do not have physical USB/headset boom-mic mute switches exposed via
    // MediaStreamTrack.muted; WebKit/Blink instead toggle .muted during AVAudioSession/Telecom route
    // transitions. Never treat mobile track.muted as a hardware switch mute.
    if (isNativePlatform() || isAppleMobileDevice()) {
        if (voiceState.isLocalHardwareMuted) {
            voiceState.isLocalHardwareMuted = false;
            if (voiceState.dotnetHelper) {
                voiceState.dotnetHelper.invokeMethodAsync('OnLocalHardwareMuteChanged', false).catch(() => {});
            }
        }
        return;
    }

    const rawTrack = getRawCaptureMediaStreamTrack(livekitTrack);
    if (!rawTrack) return;

    const reportHardwareMute = (hwMuted) => {
        const next = Boolean(hwMuted);
        if (voiceState.isLocalHardwareMuted === next) return;
        voiceState.isLocalHardwareMuted = next;

        try {
            const procTrack = livekitTrack?.processor?.processedTrack;
            if (procTrack && typeof procTrack.enabled === 'boolean') {
                procTrack.enabled = !next && !voiceState.isLocalSoftwareMuted;
            }
        } catch (e) {}

        if (isEffectiveLocalMicMuted()) {
            if (_localMicVadEntry) {
                _localMicVadEntry.isSpeaking = false;
                _localMicVadEntry.consecutiveActiveFrames = 0;
            }
            reportLocalSpeakingState(false);
        }
        notifyHostVoiceState(Boolean(voiceState.currentRoom), isEffectiveLocalMicMuted());
        scheduleRoomSnapshotSync();

        if (voiceState.dotnetHelper) {
            voiceState.dotnetHelper.invokeMethodAsync('OnLocalHardwareMuteChanged', next).catch(() => {});
        }
    };

    const onRawMute = () => reportHardwareMute(true);
    const onRawUnmute = () => reportHardwareMute(false);
    const onUpstreamPaused = () => {
        if (rawTrack.muted) reportHardwareMute(true);
    };
    const onUpstreamResumed = () => {
        if (!rawTrack.muted) reportHardwareMute(false);
    };

    try {
        rawTrack.addEventListener('mute', onRawMute);
        rawTrack.addEventListener('unmute', onRawUnmute);
    } catch (e) {}

    const TrackEvent = window.LivekitClient?.TrackEvent;
    if (TrackEvent && typeof livekitTrack.on === 'function') {
        try {
            if (TrackEvent.UpstreamPaused) livekitTrack.on(TrackEvent.UpstreamPaused, onUpstreamPaused);
            if (TrackEvent.UpstreamResumed) livekitTrack.on(TrackEvent.UpstreamResumed, onUpstreamResumed);
        } catch (e) {}
    }

    voiceState.localHardwareMuteCleanup = () => {
        try {
            rawTrack.removeEventListener('mute', onRawMute);
            rawTrack.removeEventListener('unmute', onRawUnmute);
        } catch (e) {}
        if (TrackEvent && typeof livekitTrack.off === 'function') {
            try {
                if (TrackEvent.UpstreamPaused) livekitTrack.off(TrackEvent.UpstreamPaused, onUpstreamPaused);
                if (TrackEvent.UpstreamResumed) livekitTrack.off(TrackEvent.UpstreamResumed, onUpstreamResumed);
            } catch (e) {}
        }
    };

    if (typeof rawTrack.muted === 'boolean' && rawTrack.muted !== voiceState.isLocalHardwareMuted) {
        reportHardwareMute(rawTrack.muted);
    }
}

let _activeMicPrepToken = null;

function cancelActiveMicPrep() {
    if (_activeMicPrepToken) {
        _activeMicPrepToken.cancelled = true;
        _activeMicPrepToken = null;
    }
}

function resolvePreferredMicDeviceIdFast() {
    if (isNativePlatform()) return undefined;
    if (_preJoinMicDeviceId && _preJoinMicDeviceId !== 'default') {
        return _preJoinMicDeviceId;
    }
    try {
        const saved = localStorage.getItem('spokes_active_mic_id');
        if (saved && saved !== 'default') {
            return saved;
        }
    } catch (e) {}
    return undefined;
}

async function resolvePreferredMicDeviceId() {
    if (isNativePlatform()) return undefined;

    let devices = [];
    if (window.LivekitClient?.Room?.getLocalDevices) {
        try {
            devices = await window.LivekitClient.Room.getLocalDevices('audioinput', false);
        } catch (e) {}
    }
    if ((!devices || devices.length === 0) && navigator?.mediaDevices?.enumerateDevices) {
        try {
            const all = await navigator.mediaDevices.enumerateDevices();
            devices = all.filter(d => d.kind === 'audioinput');
        } catch (e) {}
    }

    let saved = _preJoinMicDeviceId || null;
    if (!saved) {
        try {
            saved = localStorage.getItem('spokes_active_mic_id');
        } catch (e) {}
    }

    if (saved && saved !== 'default') {
        if (!devices || devices.length === 0 || devices.some(d => d.deviceId === saved)) {
            return saved;
        }
    }

    // When no explicit microphone is pinned on desktop Chromium/Electron, prefer the OS
    // Default Communications Device ('communications') over generic 'default', matching Discord & PC games.
    if (!saved && devices && devices.some(d => d.deviceId === 'communications')) {
        return 'communications';
    }

    return undefined;
}

async function createConfiguredLocalAudioTrack(baseOptions, preResolvedDeviceId = undefined) {
    const preferredDeviceId = preResolvedDeviceId !== undefined
        ? preResolvedDeviceId
        : await resolvePreferredMicDeviceId();
    const opts = { ...baseOptions };
    if (preferredDeviceId) {
        opts.deviceId = preferredDeviceId;
    }

    try {
        markVoiceDiag('Voice: micPrep getUserMedia Start');
        const track = await window.LivekitClient.createLocalAudioTrack(opts);
        markVoiceDiag('Voice: micPrep getUserMedia Done');
        return track;
    } catch (err) {
        if (opts.deviceId) {
            console.warn(`[Voice] createLocalAudioTrack with deviceId=${opts.deviceId} failed (${err?.name || err?.message}), retrying with system default`);
            delete opts.deviceId;
            const fallback = await window.LivekitClient.createLocalAudioTrack(opts);
            markVoiceDiag('Voice: micPrep getUserMedia Done (fallback)');
            return fallback;
        }
        throw err;
    }
}

async function attachProcessorToLocalMicTrack(track, processor, preResolvedDeviceId, isAbortedFn) {
    if (!track || !processor) {
        return { track, processor: null, processorAttached: false };
    }
    markVoiceDiag(`Voice: attachProcessor Start (${processor.name || 'ai'})`);
    try {
        if (typeof track.setAudioContext === 'function') {
            const captureCtx = await getCaptureAudioContext();
            if (isAbortedFn()) {
                try { await processor?.destroy?.(); } catch (_) {}
                stopLivekitTrackCompletely(track);
                return null;
            }
            if (captureCtx) {
                track.setAudioContext(captureCtx);
            }
        }
        if (typeof processor.setAutoGainEnabled === 'function') {
            processor.setAutoGainEnabled(isEffectiveSenderAutoGainEnabled());
        }
        await track.setProcessor(processor);
        if (isAbortedFn()) {
            try { await processor?.destroy?.(); } catch (_) {}
            stopLivekitTrackCompletely(track);
            return null;
        }
        if (typeof processor.flushCaptureFifo === 'function') {
            try { processor.flushCaptureFifo(); } catch (_) {}
        }
        markVoiceDiag(`Voice: attachProcessor Done (${processor.name || 'ai'})`);
        return { track, processor, processorAttached: true };
    } catch (err) {
        console.error('[Voice] Failed to attach processor to track:', err);
        try { await processor.destroy?.(); } catch (_) {}
        stopLivekitTrackCompletely(track);
        if (isAbortedFn()) return null;
        if (voiceState.dotnetHelper) {
            voiceState.dotnetHelper.invokeMethodAsync('OnVoiceLog', `AI Filter Processing Failed: ${err.message}.`, 'error').catch(() => {});
        }
        const fallbackTrack = await createConfiguredLocalAudioTrack(
            { echoCancellation: true, noiseSuppression: true, autoGainControl: isEffectiveSenderAutoGainEnabled() },
            preResolvedDeviceId
        );
        if (isAbortedFn()) {
            stopLivekitTrackCompletely(fallbackTrack);
            return null;
        }
        return { track: fallbackTrack, processor: null, processorAttached: false };
    }
}

async function createUnprocessedMicTrackResult(trackOptions, preResolvedDeviceId, isAbortedFn) {
    const track = await createConfiguredLocalAudioTrack(trackOptions, preResolvedDeviceId);
    if (isAbortedFn()) {
        stopLivekitTrackCompletely(track);
        return null;
    }
    return { track, processor: null, processorAttached: false };
}

async function prepareLocalMicTrackAndProcessor(noiseFilter, preResolvedDeviceId, isAbortedFn, deferProcessorAttachment = false) {
    const isIOS = isAppleMobileDevice();
    const filter = noiseFilter || 'webrtc';

    if (filter === 'none') {
        return await createUnprocessedMicTrackResult(
            { echoCancellation: false, noiseSuppression: false, autoGainControl: false },
            preResolvedDeviceId,
            isAbortedFn
        );
    }

    if (filter === 'webrtc' || isIOS) {
        return await createUnprocessedMicTrackResult(
            { echoCancellation: true, noiseSuppression: true, autoGainControl: !isIOS && isEffectiveSenderAutoGainEnabled() },
            preResolvedDeviceId,
            isAbortedFn
        );
    }

    let processor = null;
    let captureCtx = null;
    try {
        markVoiceDiag(`Voice: micPrep Start (filter=${filter})`);
        const [resolvedCtx, loadedProcessor] = await Promise.all([
            getCaptureAudioContext().catch(() => null),
            noiseManager.getProcessor(filter, {
                autoGainEnabled: isEffectiveSenderAutoGainEnabled()
            })
        ]);
        captureCtx = resolvedCtx;
        processor = loadedProcessor;
        markVoiceDiag(`Voice: micPrep AudioContext+Processor Ready (filter=${filter})`);
        if (isAbortedFn()) {
            try { await processor?.destroy?.(); } catch (_) {}
            return null;
        }
    } catch (err) {
        console.error('[Voice] Failed to load AI processor:', err);
        if (isAbortedFn()) return null;
        if (voiceState.dotnetHelper) {
            voiceState.dotnetHelper.invokeMethodAsync('OnVoiceLog', `AI Filter Failed: ${err.message}. Falling back to WebRTC.`, 'error').catch(() => {});
        }
        return await createUnprocessedMicTrackResult(
            { echoCancellation: true, noiseSuppression: true, autoGainControl: isEffectiveSenderAutoGainEnabled() },
            preResolvedDeviceId,
            isAbortedFn
        );
    }

    // Overlap AudioWorklet module loading (e.g. 1.94MB rnnoise-processor.js + voiceLevelerWorklet.js)
    // with getUserMedia() and LiveKit room.connect() instead of stalling after room.connect() resolves.
    const workletPreloadPromise = (processor && captureCtx && typeof processor.preload === 'function')
        ? processor.preload(captureCtx).catch((err) => {
            console.warn('[Voice] AudioWorklet preload failed (will retry on attach):', err);
        })
        : Promise.resolve();

    const [track] = await Promise.all([
        createConfiguredLocalAudioTrack(
            { echoCancellation: true, noiseSuppression: false, autoGainControl: false },
            preResolvedDeviceId
        ),
        workletPreloadPromise
    ]);
    markVoiceDiag(`Voice: micPrep Track+Worklets Ready (filter=${filter})`);
    if (isAbortedFn()) {
        try { await processor?.destroy?.(); } catch (_) {}
        stopLivekitTrackCompletely(track);
        return null;
    }

    if (processor && !deferProcessorAttachment) {
        return await attachProcessorToLocalMicTrack(track, processor, preResolvedDeviceId, isAbortedFn);
    }

    return { track, processor, processorAttached: false, preResolvedDeviceId };
}

async function unpublishAndStopAllLocalMicTracks(suppressMuteNotify = false) {
    if (!voiceState.currentRoom || !voiceState.currentRoom.localParticipant) return;
    for (const p of toArray(voiceState.currentRoom.localParticipant.audioTrackPublications)) {
        if (p && p.track && p.source !== window.LivekitClient?.Track?.Source?.ScreenShareAudio) {
            const trackToStop = p.audioTrack || p.track;
            if (suppressMuteNotify) voiceState.suppressNextUnpublishMuteNotify = true;
            try {
                await voiceState.currentRoom.localParticipant.unpublishTrack(trackToStop, true);
            } catch (e) {
            } finally {
                if (suppressMuteNotify) voiceState.suppressNextUnpublishMuteNotify = false;
                stopLivekitTrackCompletely(trackToStop);
            }
        }
    }
}

async function publishConfiguredLocalMicTrack(track, opSeq = voiceState.micOperationSeq) {
    if (!track) return false;
    if (opSeq !== voiceState.micOperationSeq || voiceState.isLocalSoftwareMuted || !voiceState.currentRoom || !voiceState.currentRoom.localParticipant) {
        stopLivekitTrackCompletely(track);
        return false;
    }
    bindLocalHardwareMuteMonitor(track);
    markVoiceDiag('Voice: publishMicTrack Start');
    await voiceState.currentRoom.localParticipant.publishTrack(track, {
        dtx: false,
        source: window.LivekitClient?.Track?.Source?.Microphone
    });
    markVoiceDiag('Voice: publishMicTrack Done');
    if (opSeq !== voiceState.micOperationSeq || voiceState.isLocalSoftwareMuted || !voiceState.currentRoom || !voiceState.currentRoom.localParticipant) {
        cleanupLocalHardwareMuteMonitor();
        voiceState.activeLocalAudioProcessor = null;
        try {
            if (voiceState.currentRoom?.localParticipant) {
                await voiceState.currentRoom.localParticipant.unpublishTrack(track, true);
            }
        } catch (e) {}
        stopLivekitTrackCompletely(track);
        await unpublishAndStopAllLocalMicTracks();
        return false;
    }
    return true;
}

function detachAllRoomMedia(room) {
    if (!room) return;
    try {
        if (room.localParticipant) {
            const localPubs = getParticipantTrackPublications(room.localParticipant);
            localPubs.forEach(pub => {
                if (pub.track && typeof pub.track.detach === 'function') {
                    try { pub.track.detach().forEach(el => safeRemove(el)); } catch (e) {}
                }
            });
            if (room.localParticipant.identity) {
                clearParticipantVideoDom(room.localParticipant.identity);
            }
        }
        const remoteMap = room.remoteParticipants || room.participants;
        if (remoteMap) {
            toArray(remoteMap).forEach(p => {
                const pubs = getParticipantTrackPublications(p);
                pubs.forEach(pub => {
                    if (pub.track && typeof pub.track.detach === 'function') {
                        try { pub.track.detach().forEach(el => safeRemove(el)); } catch (e) {}
                    }
                });
                const effectiveId = getEffectiveParticipantIdentity(p);
                if (effectiveId) {
                    clearParticipantVideoDom(effectiveId);
                }
            });
        }
    } catch (e) {
        console.warn('[Voice] Error detaching room media:', e);
    }
}

async function releaseNativeCallResources() {
    const wasActive = _nativeCallResourcesActive || voiceState.localCameraDesired;
    _nativeCallResourcesActive = false;
    voiceState.localCameraDesired = false;
    _iosLastConfiguredVideoMode = null;
    if (!wasActive) return;

    if (isNativePlatform()) {
        voiceState.activeNativeSpeakerId = 'default';
        try {
            if (window.Capacitor?.Plugins?.KeepAwake) {
                await window.Capacitor.Plugins.KeepAwake.allowSleep();
            }

            let platform = getNativePlatform();
            let audioPlugin = getAudioSessionPlugin();
            if (platform === 'ios' && audioPlugin) {
                if (_iosAudioInterruptionListener && typeof _iosAudioInterruptionListener.remove === 'function') {
                    try { await _iosAudioInterruptionListener.remove(); } catch (e) {}
                    _iosAudioInterruptionListener = null;
                }
                let res = await audioPlugin.configureVoiceChat({ enabled: false });
                console.info(`[Voice] iOS Native Session Released: Category=${res?.category}, Mode=${res?.mode}, Output=${res?.activeOutput}`);
            }

            if (platform === 'android' && window.Capacitor?.Plugins?.TelecomConnectionPlugin) {
                window.Capacitor.Plugins.TelecomConnectionPlugin.removeAllListeners();
                await window.Capacitor.Plugins.TelecomConnectionPlugin.endCall();
            }
        } catch (e) {
            console.warn('[Voice] Capacitor native release error:', e);
        }
    }
}

function teardownLocalRoomState() {
    voiceState.micOperationSeq++;
    cancelActiveMicPrep();
    _preJoinHydrated = false;
    _preJoinMicDeviceId = '';
    voiceState.pendingCircuitSnapshotMethod = null;
    voiceState.isRoomReconnecting = false;
    voiceState.lastAudioDevicesSignature = '';
    resetVoiceRoomSnapshotSync();
    voiceState.suppressedPublicationSids.clear();
    const prevChannelId = voiceState.activeVoiceChannelId
        || (typeof voiceState.currentRoom?.name === 'string' && voiceState.currentRoom.name.startsWith('channel-')
            ? voiceState.currentRoom.name.slice('channel-'.length)
            : '');
    voiceState.activeVoiceChannelId = '';
    if (prevChannelId && typeof window !== 'undefined' && window.spokesVoiceSpeaking?.clearChannel) {
        window.spokesVoiceSpeaking.clearChannel(prevChannelId);
    }
    stopWebAvSkewTelemetryPolling();
    cleanupLocalHardwareMuteMonitor();
    voiceState.activeLocalAudioProcessor = null;
    voiceState.isLocalSoftwareMuted = false;
    voiceState.isLocalHardwareMuted = false;
    voiceState.localCameraDesired = false;
    voiceState.mutedRemoteScreenAudio.clear();
    voiceState.coLocatedProcessedUsers.clear();
    voiceState.mutedRoommateUsers.clear();
    const roomToClose = voiceState.currentRoom;
    voiceState.currentRoom = null;
    stopAllLocalMediaTracks(roomToClose);
    clearAllVoiceTracks();
    clearAllParticipantAudio();
    notifyHostVoiceState(false, false);
    if (roomToClose) {
        detachAllRoomMedia(roomToClose);
    }
    return roomToClose;
}

export async function disconnectFromVoice() {
    exitParticipantFullscreen(false);
    const roomToClose = teardownLocalRoomState();
    await cleanupNativeMobileVoiceState();
    if (isMobileVoiceClient()) {
        resetMobileCameraSelectionForNewCall();
    }
    if (roomToClose) {
        try {
            await roomToClose.disconnect(true);
        } catch (e) {
            console.warn('[Voice] Error during room.disconnect():', e);
        }
    }
    await releaseNativeCallResources();
    if (!voiceState.currentRoom && !voiceState.usingNativeMobileVoice) {
        notifyHostVoiceState(false, false);
    }
}

export async function reinitializeMicrophone() {
    if (voiceState.usingNativeMobileVoice) {
        if (voiceState.isLocalSoftwareMuted) return;
        const nativeVoice = getNativeVoicePlugin();
        if (nativeVoice && typeof nativeVoice.setMicrophoneEnabled === 'function') {
            await nativeVoice.setMicrophoneEnabled({ enabled: true }).catch(() => {});
        }
        return;
    }
    if (!voiceState.currentRoom || !voiceState.currentRoom.localParticipant) return;
    if (voiceState.isLocalSoftwareMuted) return;

    try {
        cleanupLocalHardwareMuteMonitor();
        voiceState.activeLocalAudioProcessor = null;
        await unpublishAndStopAllLocalMicTracks(true);
        await setMicrophoneEnabled(true);
    } catch (e) {
        console.error('[Voice] reinitializeMicrophone failed:', e);
    }
}

export async function setMicrophoneEnabled(enabled, prePreparedPromise = null) {
    if (voiceState.usingNativeMobileVoice) {
        const nativeVoice = getNativeVoicePlugin();
        voiceState.isLocalSoftwareMuted = !enabled;
        if (isEffectiveLocalMicMuted()) {
            reportLocalSpeakingState(false);
        }
        notifyHostVoiceState(true, isEffectiveLocalMicMuted());
        if (nativeVoice && typeof nativeVoice.setMicrophoneEnabled === 'function') {
            await nativeVoice.setMicrophoneEnabled({ enabled: Boolean(enabled) });
        }
        if (voiceState.dotnetHelper) {
            try { voiceState.dotnetHelper.invokeMethodAsync('OnLocalMuted', !enabled)?.catch?.(() => {}); } catch (e) {}
        }
        scheduleRoomSnapshotSync();
        return;
    }
    if (!voiceState.currentRoom || !voiceState.currentRoom.localParticipant) return;

    const opSeq = ++voiceState.micOperationSeq;
    voiceState.isLocalSoftwareMuted = !enabled;
    notifyHostVoiceState(true, isEffectiveLocalMicMuted());

    const isMicEnableAborted = () =>
        opSeq !== voiceState.micOperationSeq || voiceState.isLocalSoftwareMuted || !voiceState.currentRoom || !voiceState.currentRoom.localParticipant;

    try {
        if (!enabled) {
            cancelActiveMicPrep();
            cleanupLocalHardwareMuteMonitor();
            voiceState.activeLocalAudioProcessor = null;
            unregisterLocalMicVad();

            // Unpublish existing tracks and explicitly stop underlying MediaStreamTracks
            await unpublishAndStopAllLocalMicTracks();
            return;
        }

        voiceState.activeLocalAudioProcessor = null;
        markVoiceDiag('Voice: setMicrophoneEnabled Start');
        let prepared = (prePreparedPromise && typeof prePreparedPromise.then === 'function')
            ? await prePreparedPromise.catch(() => null)
            : null;
        markVoiceDiag(`Voice: setMicrophoneEnabled PrepAwaited (hit=${Boolean(prepared && prepared.track)})`);
        if (prepared && isMicEnableAborted()) {
            try { await prepared.processor?.destroy?.(); } catch (_) {}
            stopLivekitTrackCompletely(prepared.track);
            return;
        }

        if (!prepared || !prepared.track) {
            prepared = await prepareLocalMicTrackAndProcessor(
                voiceState.currentNoiseSuppression,
                null,
                isMicEnableAborted
            );
        }

        if (!prepared || !prepared.track) {
            return;
        }
        if (isMicEnableAborted()) {
            try { await prepared.processor?.destroy?.(); } catch (_) {}
            stopLivekitTrackCompletely(prepared.track);
            return;
        }

        if (prepared.processor && !prepared.processorAttached) {
            prepared = await attachProcessorToLocalMicTrack(
                prepared.track,
                prepared.processor,
                prepared.preResolvedDeviceId,
                isMicEnableAborted
            );
            if (!prepared || !prepared.track) {
                return;
            }
        }

        voiceState.activeLocalAudioProcessor = prepared.processor || null;
        if (voiceState.activeLocalAudioProcessor) {
            syncLocalCaptureAutoGainState({ allowWebRtcTrackUpdate: false });
            flushLocalCaptureFifoIfAvailable();
        }
        const published = await publishConfiguredLocalMicTrack(prepared.track, opSeq);
        if (published) {
            syncLocalCaptureAutoGainState({ allowWebRtcTrackUpdate: true });
            if (voiceState.activeLocalAudioProcessor) {
                flushLocalCaptureFifoIfAvailable();
                setTimeout(() => {
                    if (voiceState.activeLocalAudioProcessor === prepared.processor) {
                        flushLocalCaptureFifoIfAvailable();
                    }
                }, 60);
            }
        }
        
    } catch (e) {
        console.error('[Voice] setMicrophoneEnabled failed:', e);
        if (voiceState.dotnetHelper) {
            voiceState.dotnetHelper.invokeMethodAsync('OnVoiceLog', `Microphone Error: ${e.message}`, "error").catch(()=>{});
        }
        if (!isMicEnableAborted() && voiceState.currentRoom && voiceState.currentRoom.localParticipant) {
            await voiceState.currentRoom.localParticipant.setMicrophoneEnabled(true).catch(()=>{});
        }
    }
}

function getCustomPreset(resolutionStr, is60fps) {
    const mult = is60fps ? 1.5 : 1;

    // Unified self-hosted 'Auto' preset across iOS, Android, and Web/Desktop:
    // 1080p @ 30fps with generous 8.0 Mbps ceiling and 'balanced' degradation so
    // handheld motion stays sharp on LAN/broadband while poor networks adapt smoothly.
    if (!resolutionStr || resolutionStr === 'Auto') {
        return { width: 1920, height: 1080, maxBitrate: 8000000 };
    }

    // Unified explicit manual resolutions across Mobile and Desktop (1080p and 4K/2160p):
    // 1080p: 8 Mbps (30fps) / 12 Mbps (60fps); 4K: 20 Mbps (30fps) / 30 Mbps (60fps).
    // WebRTC GCC adapts down automatically on constrained cellular/WAN links.
    if (resolutionStr === '2160p') return { width: 3840, height: 2160, maxBitrate: Math.round(20000000 * mult) };
    return { width: 1920, height: 1080, maxBitrate: Math.round(8000000 * mult) };
}

async function createConfiguredLocalVideoTrack(trackOptions) {
    const opts = { ...trackOptions };
    try {
        return await window.LivekitClient.createLocalVideoTrack(opts);
    } catch (err) {
        if (opts.deviceId) {
            console.warn(`[Voice] createLocalVideoTrack with deviceId=${opts.deviceId} failed (${err?.name || err?.message}), retrying with facingMode/default`);
            delete opts.deviceId;
            if (!opts.facingMode) opts.facingMode = voiceState.localCameraFacing || 'user';
            return await window.LivekitClient.createLocalVideoTrack(opts);
        }
        throw err;
    }
}

async function applyCameraTrackEncoding(track, targetPreset, targetFps = 30, resolutionStr = 'Auto') {
    if (!track || !targetPreset) return;
    const isAuto = !resolutionStr || resolutionStr === 'Auto';
    const effectiveFps = isAuto ? 30 : targetFps;
    const degradationPref = isAuto ? 'balanced' : 'maintain-resolution';

    if (track.mediaStreamTrack) {
        try {
            track.mediaStreamTrack.contentHint = isAuto ? '' : 'detail';
        } catch (e) {}
    }
    await applyTrackSenderParameters(track, degradationPref, (encodings) => {
        const topIdx = encodings.length - 1;
        encodings.forEach((enc, idx) => {
            if (idx === topIdx) {
                enc.active = true;
                enc.maxBitrate = targetPreset.maxBitrate;
                enc.maxFramerate = effectiveFps;
                enc.scaleResolutionDownBy = 1.0;
            } else if (idx === topIdx - 1 && encodings.length >= 3) {
                enc.active = isAuto;
                enc.maxBitrate = Math.max(900000, Math.round(targetPreset.maxBitrate * 0.4));
                enc.maxFramerate = effectiveFps;
            } else {
                enc.active = isAuto;
                enc.maxBitrate = Math.max(350000, Math.round(targetPreset.maxBitrate * 0.15));
                enc.maxFramerate = Math.min(30, effectiveFps);
            }
        });
    }, 'camera');
}


export async function setCameraEnabled(enabled, resolutionStr = 'Auto', targetFps = 30, deviceId = null) {
    voiceState.localCameraDesired = Boolean(enabled);
    voiceState.lastCameraResolution = resolutionStr || 'Auto';
    voiceState.lastCameraFps = targetFps || 30;
    if (voiceState.usingNativeMobileVoice) {
        const nativeVoice = getNativeVoicePlugin();
        const localId = voiceState.nativeLocalIdentity;
        if (!enabled) {
            await nativeVoice?.setCameraEnabled?.({ enabled: false })?.catch?.(() => {});
            setNativeMobileVideoTrackActive(localId, 'camera', false);
            if (localId) {
                try { unregisterPipVideoTrack(localId, 'camera'); } catch (e) {}
                if (voiceState.dotnetHelper) {
                    voiceState.dotnetHelper.invokeMethodAsync('OnVideoTrackStateChanged', localId, 'camera', false).catch(() => {});
                }
            }
            try { updatePipCallState({ isVideoOn: false, localCameraFacing: voiceState.localCameraFacing }); } catch (e) {}
            syncIosAudioSessionVideoMode().catch(() => {});
            scheduleRoomSnapshotSync();
            return;
        }
        const cfg = await resolveNativeMobileCaptureConfig(voiceState.lastCameraResolution, voiceState.lastCameraFps, deviceId);
        voiceState.lastCameraResolution = cfg.quality || voiceState.lastCameraResolution;
        voiceState.lastCameraFps = cfg.fps || voiceState.lastCameraFps;
        voiceState.localCameraFacing = cfg.facingMode;
        if (typeof window !== 'undefined') window._spokesLocalCameraFacing = voiceState.localCameraFacing;
        if (cfg.deviceId) {
            voiceState.nativeActiveCameraId = cfg.deviceId;
        }
        try {
            await nativeVoice.setCameraEnabled({
                enabled: true,
                deviceId: cfg.deviceId || '',
                facingMode: cfg.facingMode,
                quality: cfg.quality,
                videoWidth: cfg.width,
                videoHeight: cfg.height,
                videoFps: cfg.fps,
                videoMaxBitrate: cfg.maxBitrate
            });
        } catch (err) {
            console.error('[Voice] Native setCameraEnabled failed:', err);
            if (voiceState.dotnetHelper) {
                voiceState.dotnetHelper.invokeMethodAsync('OnVoiceLog', `Camera Error: ${err?.message || err}`, 'error').catch(() => {});
            }
            throw err;
        }
        const existingLocalDims = voiceState.nativeVideoDims.get(`${localId}:camera`);
        setNativeMobileVideoTrackActive(
            localId,
            'camera',
            true,
            existingLocalDims?.width || 0,
            existingLocalDims?.height || 0
        );
        if (localId && voiceState.dotnetHelper) {
            voiceState.dotnetHelper.invokeMethodAsync('EnsureParticipantSlot', localId).catch(() => {});
            voiceState.dotnetHelper.invokeMethodAsync('OnVideoTrackStateChanged', localId, 'camera', true).catch(() => {});
        }
        reportNativeMobileCameraCapabilitiesToDotnet(voiceState.nativeActiveCameraId || cfg.deviceId);
        try { updatePipCallState({ isVideoOn: true, localCameraFacing: voiceState.localCameraFacing }); } catch (e) {}
        syncIosAudioSessionVideoMode().catch(() => {});
        scheduleRoomSnapshotSync();
        const cameras = await getAvailableCameras();
        const activeId = await getActiveVoiceCameraId();
        return { cameras, activeCameraDeviceId: activeId };
    }
    if (voiceState.currentRoom && voiceState.currentRoom.localParticipant) {
        try {
            if (enabled) {
                let pubArr = toArray(voiceState.currentRoom.localParticipant.videoTrackPublications);
                let cameraPub = pubArr.find(p => p.source === window.LivekitClient.Track.Source.Camera);

                // Preserve active camera selection and facing mode across operations within a call.
                // On mobile browsers, never restore a previous call's camera from localStorage.
                const isMobileDevice = isMobileVoiceClient();
                let activeMediaTrack = cameraPub?.track?.mediaStreamTrack;
                const savedCameraId = isMobileDevice ? null : localStorage.getItem('spokes_active_camera_id');
                let targetDeviceId = deviceId || activeMediaTrack?.getSettings?.().deviceId || savedCameraId;
                let currentFacing = activeMediaTrack?.getSettings?.().facingMode || voiceState.localCameraFacing || 'user';

                const isAutoRes = !resolutionStr || resolutionStr === 'Auto';
                const effectiveFps = isAutoRes ? 30 : targetFps;
                let is60 = effectiveFps > 35;
                let targetPreset = getCustomPreset(resolutionStr, is60);
                
                let livekitPreset = targetPreset;
                if (window.LivekitClient && window.LivekitClient.VideoPreset) {
                    livekitPreset = new window.LivekitClient.VideoPreset(targetPreset.width, targetPreset.height, targetPreset.maxBitrate, effectiveFps);
                }

                if (currentFacing === 'environment' || currentFacing === 'user') {
                    voiceState.localCameraFacing = currentFacing;
                    if (typeof window !== 'undefined') window._spokesLocalCameraFacing = voiceState.localCameraFacing;
                }

                let trackOptions = { resolution: livekitPreset };
                if (targetDeviceId && targetDeviceId !== 'default') {
                    trackOptions.deviceId = targetDeviceId;
                } else if (currentFacing) {
                    trackOptions.facingMode = currentFacing;
                } else {
                    trackOptions.facingMode = 'user';
                }

                const useSimulcast = isAutoRes && !isMobileDevice;
                let publishOptions = { 
                    source: window.LivekitClient.Track.Source.Camera, 
                    simulcast: useSimulcast,
                    videoCodec: 'h264',
                    degradationPreference: isAutoRes ? 'balanced' : 'maintain-resolution',
                    videoEncoding: {
                        maxBitrate: targetPreset.maxBitrate,
                        maxFramerate: effectiveFps
                    }
                };
                if (useSimulcast && window.LivekitClient?.VideoPresets) {
                    publishOptions.videoSimulcastLayers = [
                        window.LivekitClient.VideoPresets.h360,
                        window.LivekitClient.VideoPresets.h720
                    ];
                }

                const publishNewCameraTrack = async () => {
                    const track = await createConfiguredLocalVideoTrack(trackOptions);
                    await voiceState.currentRoom.localParticipant.publishTrack(track, publishOptions);
                    await applyCameraTrackEncoding(track, targetPreset, effectiveFps, resolutionStr);
                };
                const unpublishAndStopCameraTrack = async (actualTrack) => {
                    try { await voiceState.currentRoom.localParticipant.unpublishTrack(actualTrack, true); } catch (_) {}
                    stopLivekitTrackCompletely(actualTrack);
                };

                if (cameraPub && cameraPub.track) {
                    let actualTrack = cameraPub.videoTrack || cameraPub.track;
                    const isTrackEnded = actualTrack.mediaStreamTrack && actualTrack.mediaStreamTrack.readyState === 'ended';
                    try {
                        if (!isTrackEnded && typeof actualTrack.restartTrack === 'function') {
                            let restartOptions = { resolution: livekitPreset };
                            if (targetDeviceId && targetDeviceId !== 'default') restartOptions.deviceId = targetDeviceId;
                            else if (currentFacing) restartOptions.facingMode = currentFacing;
                            await actualTrack.restartTrack(restartOptions);
                            if (actualTrack.publishOptionsUsed) {
                                actualTrack.publishOptionsUsed = {
                                    ...actualTrack.publishOptionsUsed,
                                    ...publishOptions
                                };
                            }
                            await applyCameraTrackEncoding(actualTrack, targetPreset, effectiveFps, resolutionStr);
                            handleLocalTrackPublished(cameraPub, voiceState.currentRoom.localParticipant);
                        } else {
                            await unpublishAndStopCameraTrack(actualTrack);
                            await publishNewCameraTrack();
                        }
                    } catch(e) {
                        console.warn("[Voice] LiveKit restartTrack failed, recreating camera track:", e);
                        await unpublishAndStopCameraTrack(actualTrack);
                        await publishNewCameraTrack();
                    }
                } else {
                    await publishNewCameraTrack();
                }

                if (!isMobileDevice && targetDeviceId && targetDeviceId !== 'default') {
                    localStorage.setItem('spokes_active_camera_id', targetDeviceId);
                }

                try { updatePipCallState({ isVideoOn: true, localCameraFacing: voiceState.localCameraFacing }); } catch (e) {}
                scheduleRoomSnapshotSync();

                const cameras = await getAvailableCameras();
                const activeId = await getActiveVoiceCameraId();
                return { cameras, activeCameraDeviceId: activeId };

            } else {
                const localId = voiceState.currentRoom.localParticipant.identity;
                let cameraPubs = toArray(voiceState.currentRoom.localParticipant.videoTrackPublications)
                    .filter(p => p.source === window.LivekitClient.Track.Source.Camera || (!isScreenShareTrackOrPub(p.track, p) && p.kind === window.LivekitClient.Track.Kind.Video));
                for (let cameraPub of cameraPubs) {
                    let actualTrack = cameraPub.videoTrack || cameraPub.track;
                    if (actualTrack) {
                        try {
                            await voiceState.currentRoom.localParticipant.unpublishTrack(actualTrack, true);
                        } catch (e) {
                            console.warn('[VoiceInterop] unpublishTrack camera warning:', e);
                        } finally {
                            stopLivekitTrackCompletely(actualTrack);
                        }
                    }
                }
                if (localId) {
                    try { unregisterPipVideoTrack(localId, 'camera'); } catch (e) {}
                    clearParticipantVideoDom(localId, 'camera');
                    if (voiceState.dotnetHelper) {
                        voiceState.dotnetHelper.invokeMethodAsync('OnVideoTrackStateChanged', localId, 'camera', false).catch(() => {});
                    }
                }
                try { updatePipCallState({ isVideoOn: false, localCameraFacing: voiceState.localCameraFacing }); } catch (e) {}
                scheduleRoomSnapshotSync();
            }
        } catch (error) {
            console.error('[VoiceInterop] Error in setCameraEnabled:', error);
            throw error;
        }
    }
}

export async function getAvailableCameras() {
    if (voiceState.usingNativeMobileVoice || isNativePlatform()) {
        const cams = await fetchNativeMobileCameras();
        return cams.map(c => ({ deviceId: c.deviceId, label: c.label }));
    }
    if (!window.LivekitClient || !voiceState.currentRoom) return [];
    try {
        let devices = await window.LivekitClient.Room.getLocalDevices('videoinput');
        let distinct = [];
        for (let d of devices) {
            if (d.deviceId === 'default' || d.deviceId === 'communications') continue;
            let rawLabel = String(d.label || '').trim();
            let exists = distinct.find(x => x.deviceId === d.deviceId || (rawLabel && x.label === rawLabel));
            if (!exists) {
                distinct.push({ deviceId: d.deviceId, label: rawLabel || `Camera ${distinct.length + 1}` });
            }
        }
        return distinct;
    } catch(e) {
        return [];
    }
}

export async function getActiveVoiceCameraId() {
    if (voiceState.usingNativeMobileVoice || isNativePlatform()) {
        if (voiceState.nativeActiveCameraId) return voiceState.nativeActiveCameraId;
        const cams = await fetchNativeMobileCameras();
        const byFacing = cams.find(c => c.facingMode === voiceState.localCameraFacing) || cams[0];
        return byFacing?.deviceId || '';
    }
    if (!voiceState.currentRoom) return "";
    try {
        if (typeof voiceState.currentRoom.getActiveDevice === 'function') {
            const deviceId = await voiceState.currentRoom.getActiveDevice('videoinput');
            if (deviceId) return deviceId;
        }
        if (voiceState.currentRoom.localParticipant && window.LivekitClient && window.LivekitClient.Track) {
            let cameraPub = toArray(voiceState.currentRoom.localParticipant.videoTrackPublications)
                .find(p => p.source === window.LivekitClient.Track.Source.Camera);
            if (cameraPub?.track?.mediaStreamTrack) {
                return cameraPub.track.mediaStreamTrack.getSettings().deviceId || "";
            }
        }
    } catch (e) {
        console.warn('[Voice] Failed to get active camera ID:', e);
    }
    return "";
}

export async function switchActiveCamera(deviceId = null) {
    const cleanDeviceId = typeof deviceId === 'string' ? deviceId.trim() : '';
    if (voiceState.usingNativeMobileVoice) {
        const nativeVoice = getNativeVoicePlugin();
        if (!nativeVoice) return '';
        const cams = await fetchNativeMobileCameras();
        let requestedFacing = (voiceState.localCameraFacing === 'environment') ? 'user' : 'environment';
        const matched = (cleanDeviceId && cams.find(c => c.deviceId === cleanDeviceId || c.cameraId === cleanDeviceId))
            || (!cleanDeviceId ? cams.find(c => c.facingMode === requestedFacing) : null);
        if (matched?.facingMode === 'environment' || matched?.facingMode === 'user') {
            requestedFacing = matched.facingMode;
        }
        const targetCamId = matched ? (matched.cameraId || matched.deviceId) : (cleanDeviceId || undefined);
        const cfg = await resolveNativeMobileCaptureConfig(voiceState.lastCameraResolution, voiceState.lastCameraFps, targetCamId);
        voiceState.lastCameraResolution = cfg.quality || voiceState.lastCameraResolution;
        voiceState.lastCameraFps = cfg.fps || voiceState.lastCameraFps;
        const res = await nativeVoice.switchCamera({
            facingMode: requestedFacing,
            deviceId: targetCamId,
            quality: cfg.quality,
            videoWidth: cfg.width,
            videoHeight: cfg.height,
            videoFps: cfg.fps,
            videoMaxBitrate: cfg.maxBitrate
        });
        voiceState.localCameraFacing = (res?.facingMode === 'environment' || res?.facingMode === 'user') ? res.facingMode : requestedFacing;
        if (typeof window !== 'undefined') window._spokesLocalCameraFacing = voiceState.localCameraFacing;
        const resolvedCam = matched || cams.find(c => c.facingMode === voiceState.localCameraFacing);
        if (resolvedCam?.deviceId) {
            voiceState.nativeActiveCameraId = resolvedCam.deviceId;
        }
        if (voiceState.nativeLocalIdentity) {
            voiceState.nativeVideoDims.delete(`${voiceState.nativeLocalIdentity}:camera`);
        }
        reportNativeMobileCameraCapabilitiesToDotnet(voiceState.nativeActiveCameraId || targetCamId);
        updatePipCallState({ isVideoOn: true, localCameraFacing: voiceState.localCameraFacing });
        setNativeMobileVideoTrackActive(voiceState.nativeLocalIdentity, 'camera', true);
        scheduleNativeMobileVideoSlotsSync();
        return voiceState.nativeActiveCameraId || targetCamId || '';
    }
    if (!voiceState.currentRoom || !voiceState.currentRoom.localParticipant) return '';
    try {
        let pubArr = toArray(voiceState.currentRoom.localParticipant.videoTrackPublications);
        let cameraPub = pubArr.find(p => p.source === window.LivekitClient.Track.Source.Camera);
        const activeTrack = cameraPub?.videoTrack || cameraPub?.track;

        if (!cleanDeviceId) {
            const targetFacing = (voiceState.localCameraFacing === 'environment') ? 'user' : 'environment';
            const isAutoRes = !voiceState.lastCameraResolution || voiceState.lastCameraResolution === 'Auto';
            const effectiveFps = isAutoRes ? 30 : (voiceState.lastCameraFps || 30);
            const targetPreset = getCustomPreset(voiceState.lastCameraResolution, effectiveFps > 35);
            const livekitPreset = window.LivekitClient?.VideoPreset
                ? new window.LivekitClient.VideoPreset(targetPreset.width, targetPreset.height, targetPreset.maxBitrate, effectiveFps)
                : targetPreset;
            if (activeTrack && typeof activeTrack.restartTrack === 'function') {
                try {
                    await activeTrack.restartTrack({ facingMode: { exact: targetFacing }, resolution: livekitPreset });
                } catch {
                    await activeTrack.restartTrack({ facingMode: targetFacing, resolution: livekitPreset });
                }
            }
            voiceState.localCameraFacing = targetFacing;
            if (typeof window !== 'undefined') window._spokesLocalCameraFacing = voiceState.localCameraFacing;
        } else {
            await voiceState.currentRoom.switchActiveDevice('videoinput', cleanDeviceId);
            if (!isMobileVoiceClient() && cleanDeviceId !== 'default') {
                localStorage.setItem('spokes_active_camera_id', cleanDeviceId);
            }
        }

        pubArr = toArray(voiceState.currentRoom.localParticipant.videoTrackPublications);
        cameraPub = pubArr.find(p => p.source === window.LivekitClient.Track.Source.Camera);
        const resolvedTrack = cameraPub?.videoTrack || cameraPub?.track;
        const settings = resolvedTrack?.mediaStreamTrack?.getSettings?.() || {};
        const rawFacing = settings.facingMode;
        if (rawFacing === 'environment' || rawFacing === 'user') {
            voiceState.localCameraFacing = rawFacing;
            if (typeof window !== 'undefined') window._spokesLocalCameraFacing = voiceState.localCameraFacing;
        }
        const resolvedDeviceId = settings.deviceId || cleanDeviceId || '';
        if (resolvedTrack?.mediaStreamTrack) {
            const hwCaps = getHardwareCameraCapabilities(resolvedTrack.mediaStreamTrack);
            if (hwCaps.maxDim > 0) {
                const clamped = clampCameraResolutionForCapabilities(
                    voiceState.lastCameraResolution || 'Auto',
                    voiceState.lastCameraFps || 30,
                    hwCaps.maxDim,
                    hwCaps.maxFps
                );
                const resChanged = clamped.resolution !== (voiceState.lastCameraResolution || 'Auto');
                const fpsChanged = clamped.fps !== (voiceState.lastCameraFps || 30);
                voiceState.lastCameraResolution = clamped.resolution;
                voiceState.lastCameraFps = clamped.fps;
                if (resChanged || fpsChanged) {
                    const isAutoRes = !voiceState.lastCameraResolution || voiceState.lastCameraResolution === 'Auto';
                    const effectiveFps = isAutoRes ? 30 : voiceState.lastCameraFps;
                    const targetPreset = getCustomPreset(voiceState.lastCameraResolution, effectiveFps > 35);
                    if (typeof resolvedTrack.restartTrack === 'function' && window.LivekitClient?.VideoPreset) {
                        const livekitPreset = new window.LivekitClient.VideoPreset(targetPreset.width, targetPreset.height, targetPreset.maxBitrate, effectiveFps);
                        const restartOpts = resolvedDeviceId
                            ? { deviceId: resolvedDeviceId, resolution: livekitPreset }
                            : { facingMode: voiceState.localCameraFacing || 'user', resolution: livekitPreset };
                        await resolvedTrack.restartTrack(restartOpts);
                    }
                    await applyCameraTrackEncoding(resolvedTrack, targetPreset, effectiveFps, voiceState.lastCameraResolution);
                }
            }
        }
        if (cameraPub && cameraPub.track) handleLocalTrackPublished(cameraPub, voiceState.currentRoom.localParticipant);
        updatePipCallState({ isVideoOn: true, localCameraFacing: voiceState.localCameraFacing });
        return resolvedDeviceId;
    } catch(e) {
        console.error('[Voice] Failed to switch camera:', e);
        return '';
    }
}

export async function updateCameraSettings(resolutionStr = 'Auto', targetFps = 30, deviceId = null) {
    if (voiceState.usingNativeMobileVoice) {
        if (!voiceState.localCameraDesired) return;
        await setCameraEnabled(true, resolutionStr || 'Auto', targetFps || 30, deviceId);
        return;
    }
    if (!voiceState.currentRoom || !voiceState.currentRoom.localParticipant || !voiceState.localCameraDesired) return;
    voiceState.lastCameraResolution = resolutionStr || 'Auto';
    voiceState.lastCameraFps = targetFps || 30;
    try {
        await setCameraEnabled(true, voiceState.lastCameraResolution, voiceState.lastCameraFps, deviceId);
    } catch (e) {
        console.warn('[Voice] Failed to update camera settings in-place:', e);
    }
}

let _deviceChangeListenerInitialized = false;
function ensureDeviceChangeListener() {
    if (_deviceChangeListenerInitialized) return;
    if (typeof navigator !== 'undefined' && navigator.mediaDevices?.addEventListener) {
        navigator.mediaDevices.addEventListener('devicechange', async () => {
            console.info('[Voice] Hardware device change detected');
            try {
                const speakers = await getAvailableSpeakers();
                const savedSpeaker = localStorage.getItem('spokes_active_speaker_id');
                if (savedSpeaker && savedSpeaker !== 'default' && !speakers.some(s => s.deviceId === savedSpeaker)) {
                    console.warn('[Voice] Pinned speaker was disconnected, falling back to System Default');
                    await switchActiveSpeaker('default');
                }
                const mics = await getAvailableMicrophones();
                const savedMic = localStorage.getItem('spokes_active_mic_id');
                if (savedMic && savedMic !== 'default' && !mics.some(m => m.deviceId === savedMic)) {
                    console.warn('[Voice] Pinned microphone was disconnected, falling back to System Default');
                    await switchActiveMicrophone('default');
                }
            } catch (e) {
                console.warn('[Voice] Error handling devicechange fallback:', e);
            }
            await updateAudioDevices();
        });
        _deviceChangeListenerInitialized = true;
    }
}

export async function updateAudioDevices() {
    ensureDeviceChangeListener();
    if (!voiceState.dotnetHelper) return;
    try {
        let mics;
        let speakers;
        if (isNativePlatform()) {
            mics = await getAvailableMicrophones();
            speakers = await getAvailableSpeakers();
        } else {
            const enumerated = await enumerateAllStandardAudioDevices();
            mics = enumerated.mics;
            speakers = enumerated.speakers;
        }
        const activeMicId = await getActiveMicrophoneId(mics);
        const activeSpeakerId = await getActiveSpeakerId(speakers);
        const signature = JSON.stringify([activeMicId, activeSpeakerId, mics, speakers]);
        if (signature === voiceState.lastAudioDevicesSignature) {
            return;
        }
        voiceState.lastAudioDevicesSignature = signature;
        invokeDotNetSafe('OnAudioDevicesUpdated', mics, activeMicId, speakers, activeSpeakerId);
    } catch (e) {
        console.warn('[Voice] Failed to update audio devices:', e);
    }
}

function normalizeStandardAudioDeviceList(rawDevices, fallbackPrefix) {
    const result = [{ deviceId: 'default', label: 'System Default' }];
    for (const d of rawDevices || []) {
        if (!d || d.deviceId === 'default' || d.deviceId === 'communications' || (!d.deviceId && !d.label)) continue;
        const label = d.label || `${fallbackPrefix} (${d.deviceId.substring(0, 5)})`;
        if (!result.some(x => x.deviceId === d.deviceId || x.label === label)) {
            result.push({ deviceId: d.deviceId, groupId: d.groupId, label });
        }
    }
    return result;
}

async function enumerateAllStandardAudioDevices() {
    let rawInputs = [];
    let rawOutputs = [];
    if (navigator?.mediaDevices?.enumerateDevices) {
        try {
            const all = await navigator.mediaDevices.enumerateDevices();
            if (Array.isArray(all) && all.length > 0) {
                rawInputs = all.filter(d => d.kind === 'audioinput');
                rawOutputs = all.filter(d => d.kind === 'audiooutput');
            }
        } catch (e) {}
    }
    if (rawInputs.length === 0 && rawOutputs.length === 0 && window.LivekitClient?.Room?.getLocalDevices) {
        try {
            rawInputs = (await window.LivekitClient.Room.getLocalDevices('audioinput', false)) || [];
        } catch (e) {}
        try {
            rawOutputs = (await window.LivekitClient.Room.getLocalDevices('audiooutput', false)) || [];
        } catch (e) {}
    }
    const mics = normalizeStandardAudioDeviceList(rawInputs, 'Microphone');
    const speakers = normalizeStandardAudioDeviceList(rawOutputs, 'Speaker');
    voiceState.cachedMicDevices = mics;
    voiceState.cachedSpeakerDevices = speakers;
    return { mics, speakers };
}

export async function getAvailableMicrophones() {
    if (isNativePlatform()) {
        const platform = getNativePlatform();
        if (platform === 'ios' || platform === 'android') return [];
    }
    const { mics } = await enumerateAllStandardAudioDevices();
    return mics;
}

export async function getActiveMicrophoneId(preEnumeratedMics = null) {
    if (isNativePlatform()) return "";
    
    const mics = Array.isArray(preEnumeratedMics) ? preEnumeratedMics : await getAvailableMicrophones();
    if (mics.length === 0) return "default";

    // 1. When actively connected, inspect the live published raw hardware capture track first
    // so Voice Settings always reflects the actual physical microphone transmitting audio.
    if (voiceState.currentRoom) {
        try {
            let liveDeviceId = "";
            if (voiceState.currentRoom.localParticipant && window.LivekitClient?.Track) {
                const micPub = toArray(voiceState.currentRoom.localParticipant.audioTrackPublications)
                    .find(p => p.source === window.LivekitClient.Track.Source.Microphone && p.track);
                const rawTrack = getRawCaptureMediaStreamTrack(micPub?.track);
                if (rawTrack && typeof rawTrack.getSettings === 'function') {
                    liveDeviceId = rawTrack.getSettings()?.deviceId || "";
                }
            }
            if ((!liveDeviceId || liveDeviceId === 'default' || liveDeviceId === 'communications') && typeof voiceState.currentRoom.getActiveDevice === 'function') {
                liveDeviceId = (await voiceState.currentRoom.getActiveDevice('audioinput')) || liveDeviceId;
            }
            if (liveDeviceId && liveDeviceId !== 'default' && liveDeviceId !== 'communications') {
                if (mics.some(m => m.deviceId === liveDeviceId)) {
                    return liveDeviceId;
                }
            }
        } catch (e) {}
    }

    // 2. Fall back to the user's saved preferred microphone if currently plugged in
    try {
        const saved = localStorage.getItem('spokes_active_mic_id');
        if (saved && mics.some(m => m.deviceId === saved)) {
            return saved;
        }
    } catch (e) {}

    return "default";
}

export async function switchActiveMicrophone(deviceId) {
    const targetMic = (!deviceId || deviceId === 'default') ? 'default' : deviceId;
    try {
        localStorage.setItem('spokes_active_mic_id', targetMic);
    } catch (e) {}

    if (voiceState.currentRoom) {
        try {
            const effectiveSwitchId = targetMic === 'default'
                ? ((await resolvePreferredMicDeviceId()) || 'default')
                : targetMic;
            await voiceState.currentRoom.switchActiveDevice('audioinput', effectiveSwitchId);

            if (voiceState.currentRoom.localParticipant && window.LivekitClient?.Track) {
                const micPub = toArray(voiceState.currentRoom.localParticipant.audioTrackPublications)
                    .find(p => p.source === window.LivekitClient.Track.Source.Microphone && p.track);
                if (micPub?.track) {
                    bindLocalHardwareMuteMonitor(micPub.track);
                    const vadTrack = micPub.track.processor?.processedTrack || micPub.track.mediaStreamTrack;
                    const rawTrack = getRawCaptureMediaStreamTrack(micPub.track);
                    if (voiceState.currentRoom.localParticipant.identity && vadTrack) {
                        registerLocalMicVad(voiceState.currentRoom.localParticipant.identity, vadTrack, rawTrack);
                    }
                    reportLocalAudioPipelineTelemetry(micPub.track);
                }
            }
        } catch (e) {
            console.error('[Voice] Failed to switch microphone:', e);
        }
    }
    await updateAudioDevices();
    if (voiceState.directSessionActive) {
        const inputDeviceLabel = await resolveSelectedMicLabelForDirectAudio();
        pushDirectRoomControlsUpdate({ inputDeviceLabel });
    }
}

export async function isAndroidDevice() {
    return isNativePlatform() && getNativePlatform() === 'android';
}

export async function isNativePlatformApp() {
    return isNativePlatform();
}

function rebindRemoteAudioElementsPlayback(forceSrcReset = false) {
    try {
        if (voiceState.currentRoom && typeof voiceState.currentRoom.startAudio === 'function') {
            voiceState.currentRoom.startAudio().catch(() => {});
        }
        resumeAllVoiceAudioPlayback();
        const seen = new Set();
        const rebindEl = (audioEl) => {
            if (!audioEl || seen.has(audioEl)) return;
            seen.add(audioEl);
            try {
                // Only reassign srcObject on genuine post-connect hardware route transitions;
                // reassigning srcObject during initial connect aborts in-flight play() and resets WebMediaPlayerMS!
                if (forceSrcReset) {
                    const srcObj = audioEl.srcObject;
                    if (srcObj) {
                        audioEl.srcObject = srcObj;
                    }
                }
                if (!audioEl.muted) {
                    audioEl.play?.().catch(() => {});
                }
            } catch (_) {}
        };

        _participantGainNodes.forEach((item) => {
            if (item?.element) {
                rebindEl(item.element);
            }
        });

        const container = document.getElementById('voice-audio-container');
        if (container && typeof container.querySelectorAll === 'function') {
            container.querySelectorAll('audio').forEach(rebindEl);
        }
    } catch (e) {}
}

export async function getAvailableSpeakers() {
    if (isNativePlatform()) {
        let platform = getNativePlatform();
        if (platform === 'ios') {
            return [];
        } else if (platform === 'android') {
            let hasBluetooth = _androidHasBluetooth;
            let hasWiredHeadset = _androidHasWiredHeadset;
            let hasEarpiece = _androidHasEarpiece;

            try {
                if (window.Capacitor.Plugins.TelecomConnectionPlugin) {
                    let res = await window.Capacitor.Plugins.TelecomConnectionPlugin.getAvailableRoutes();
                    if (res) {
                        if (typeof res.hasBluetooth === 'boolean') {
                            hasBluetooth = res.hasBluetooth;
                            _androidHasBluetooth = res.hasBluetooth;
                        }
                        if (typeof res.hasWiredHeadset === 'boolean') {
                            hasWiredHeadset = res.hasWiredHeadset;
                            _androidHasWiredHeadset = res.hasWiredHeadset;
                        }
                        if (typeof res.hasEarpiece === 'boolean') {
                            hasEarpiece = res.hasEarpiece;
                            _androidHasEarpiece = res.hasEarpiece;
                        }
                        if (typeof res.route === 'string' && res.route && res.route !== 'unknown') {
                            voiceState.activeNativeSpeakerId = res.route;
                        }
                    }
                }
            } catch(e) {}

            let devices = [{ deviceId: 'speaker', label: 'Speaker' }];
            if (hasEarpiece !== false) {
                devices.push({ deviceId: 'earpiece', label: 'Earpiece' });
            }
            if (hasWiredHeadset) {
                devices.push({ deviceId: 'headset', label: 'Wired Headset' });
            }
            if (hasBluetooth) {
                devices.push({ deviceId: 'bluetooth', label: 'Bluetooth' });
            }
            return devices;
        }
    }

    const { speakers } = await enumerateAllStandardAudioDevices();
    return speakers;
}

const SYNTHETIC_MOBILE_SPEAKER_ROUTES = new Set(['earpiece', 'speaker', 'speakerphone', 'headset', 'bluetooth']);

function isSyntheticMobileSpeakerRoute(deviceId) {
    return Boolean(deviceId) && SYNTHETIC_MOBILE_SPEAKER_ROUTES.has(String(deviceId).trim().toLowerCase());
}

export async function getActiveSpeakerId(preEnumeratedSpeakers = null) {
    if (isNativePlatform()) {
        let platform = getNativePlatform();
        if (platform === 'ios' || platform === 'android') {
            return voiceState.activeNativeSpeakerId;
        }
    }

    const speakers = Array.isArray(preEnumeratedSpeakers) ? preEnumeratedSpeakers : await getAvailableSpeakers();
    if (speakers.length === 0) return "default";

    try {
        const saved = localStorage.getItem('spokes_active_speaker_id');
        if (saved && speakers.some(s => s.deviceId === saved)) {
            return saved;
        }
    } catch(e) {}
    
    try {
        let activeId = "";
        if (voiceState.currentRoom && typeof voiceState.currentRoom.getActiveDevice === 'function') {
            activeId = await voiceState.currentRoom.getActiveDevice('audiooutput');
        }
        if (activeId && activeId !== 'default' && activeId !== 'communications') {
            if (speakers.some(s => s.deviceId === activeId)) return activeId;
        }
    } catch (e) {}

    return "default";
}

export async function switchActiveSpeaker(deviceId) {
    const targetSink = (!deviceId || deviceId === 'default') ? 'default' : deviceId;

    if (isNativePlatform()) {
        const platform = getNativePlatform();
        try {
            if (platform === 'ios') {
                const audioPlugin = getAudioSessionPlugin();
                if (audioPlugin) {
                    const targetRoute = targetSink === 'speakerphone' || targetSink === 'speaker' ? 'speaker' : 'earpiece';
                    const res = await audioPlugin.setAudioRoute({ route: targetRoute });
                    voiceState.activeNativeSpeakerId = res?.activeOutput || targetRoute;
                    console.info(`[Voice] iOS Native Route Set (${targetRoute}): Category=${res?.category}, Mode=${res?.mode}, Output=${res?.activeOutput}`);
                }
            } else if (platform === 'android') {
                if (window.Capacitor.Plugins.TelecomConnectionPlugin) {
                    const res = await window.Capacitor.Plugins.TelecomConnectionPlugin.setAudioRoute({ route: targetSink });
                    if (res && typeof res.route === 'string' && res.route && res.route !== 'unknown') {
                        voiceState.activeNativeSpeakerId = res.route;
                    }
                }
            }
            await updateAudioDevices();
            refreshLocalAudioPipelineTelemetry();
        } catch (e) {
            console.error('[Voice] Failed to switch native speaker:', e);
            await updateAudioDevices();
        }
        return;
    }

    if (isSyntheticMobileSpeakerRoute(targetSink)) {
        try {
            localStorage.removeItem('spokes_active_speaker_id');
        } catch(e) {}
        return;
    }

    try {
        localStorage.setItem('spokes_active_speaker_id', targetSink);
    } catch(e) {}

    const sinkVal = targetSink === 'default' ? '' : targetSink;

    // Direct Web Audio API destination to selected speaker/headset (Chrome 110+, Edge, Electron)
    if (_globalAudioContext && typeof _globalAudioContext.setSinkId === 'function') {
        try {
            await _globalAudioContext.setSinkId(sinkVal);
            console.log(`[Voice] AudioContext sinkId set to: "${sinkVal}"`);
        } catch(e) {
            console.warn('[Voice] Failed to set sinkId on AudioContext:', e);
        }
    }

    // Direct all attached HTML audio elements to selected speaker/headset
    _participantGainNodes.forEach(item => {
        if (item.element && typeof item.element.setSinkId === 'function') {
            item.element.setSinkId(sinkVal).catch(() => {});
        }
    });

    if (window.SoundManager?.setSinkId) {
        try {
            await window.SoundManager.setSinkId(targetSink);
        } catch(e) {}
    }

    if (voiceState.currentRoom) {
        try {
            await voiceState.currentRoom.switchActiveDevice('audiooutput', sinkVal);
        } catch(e) {
            console.error('[Voice] Failed to switch speaker:', e);
        }
    }
    await updateAudioDevices();
    refreshLocalAudioPipelineTelemetry();
    if (voiceState.directSessionActive) {
        const outputDeviceLabel = await resolveSelectedSpeakerLabelForDirectAudio();
        pushDirectRoomControlsUpdate({ outputDeviceLabel });
    }
}

export async function playTestSpeakerSound(deviceId) {
    try {
        const audio = new Audio('/sounds/spokesnotif1.wav');
        const sink = (!deviceId || deviceId === 'default' || isNativePlatform() || isSyntheticMobileSpeakerRoute(deviceId)) ? '' : deviceId;
        if (!isNativePlatform() && sink && typeof audio.setSinkId === 'function') {
            try {
                await audio.setSinkId(sink);
            } catch(e) {
                console.warn('[Voice] Failed to set sinkId on test audio:', e);
            }
        }
        audio.volume = 1.0;
        await audio.play();
    } catch(e) {
        console.warn('[Voice] playTestSpeakerSound failed, falling back to SoundManager:', e);
        if (window.SoundManager) {
            window.SoundManager.play('spokesnotif1', true);
        }
    }
}


// --- Event Handlers ---

function waitForElement(id, timeoutMs = 5000) {
    return new Promise((resolve, reject) => {
        const el = document.getElementById(id);
        if (el) {
            resolve(el);
            return;
        }

        const observer = new MutationObserver((mutations, obs) => {
            const el = document.getElementById(id);
            if (el) {
                obs.disconnect();
                clearTimeout(timeout);
                resolve(el);
            } else if (!voiceState.currentRoom) {
                obs.disconnect();
                clearTimeout(timeout);
                reject(new Error(`Aborted waiting for ${id} due to room disconnection`));
            }
        });

        observer.observe(document.body, {
            childList: true,
            subtree: true
        });

        const timeout = setTimeout(() => {
            observer.disconnect();
            reject(new Error(`Timeout waiting for element: ${id}`));
        }, timeoutMs);
    });
}

function attachTrack(track, participant, publication = null) {
    const isAudio = track.kind === window.LivekitClient.Track.Kind.Audio;
    const isVideo = track.kind === window.LivekitClient.Track.Kind.Video;
    const isBot = participant?.identity?.startsWith('bot:');
    
    if (isAudio) {
        // Remote audio routing is governed by reconcileAudioRouting() -> ensureDesiredAudioPublication().
        if (participant === voiceState.currentRoom?.localParticipant) {
            return;
        }

        const processedOwner = getProcessedMicOwner(track, publication, participant);
        const localIdentity = voiceState.currentRoom?.localParticipant?.identity;
        const effectiveIdentity = processedOwner || participant?.identity;
        if (!effectiveIdentity || effectiveIdentity === localIdentity) {
            return;
        }

        console.log(`[Voice] Fast-path attaching audio track: ${track.sid} for ${effectiveIdentity} (isBot=${isBot}, processed=${Boolean(processedOwner)})`);
        const container = document.getElementById('voice-audio-container') || document.body;
        let element;
        if (track.attachedElements && track.attachedElements.length > 0) {
            element = track.attachedElements[0];
            if (element.parentElement !== container) {
                container.appendChild(element);
            }
        } else {
            element = track.attach();
            container.appendChild(element);
        }

        applyLowLatencyAudioReceiverHints(track, publication);
        attachParticipantAudio(track, participant, element, effectiveIdentity);

        const slotId = processedOwner || (!isBot ? effectiveIdentity : null);
        if (slotId && voiceState.dotnetHelper) {
            try { voiceState.dotnetHelper.invokeMethodAsync('EnsureParticipantSlot', slotId)?.catch?.(() => {}); } catch (e) {}
        }
        scheduleRoomSnapshotSync();
        return;
    }
    
    if (isVideo) {
        const isScreenShare = isScreenShareTrackOrPub(track, publication);
        const pipSource = isScreenShare ? 'screenshare' : 'camera';
        const effectiveIdentity = getEffectiveParticipantIdentity(participant);
        const localIdentity = voiceState.currentRoom?.localParticipant?.identity;
        const isLocalParticipant = participant === voiceState.currentRoom?.localParticipant
            || (Boolean(localIdentity) && effectiveIdentity === localIdentity);
        const targetId = getVideoElementIdForTrack(effectiveIdentity, track, publication);

        if (track.mediaStreamTrack && track.mediaStreamTrack.readyState === 'ended') {
            clearParticipantVideoDom(effectiveIdentity, pipSource);
            return;
        }

        console.log(`[Voice:Diag] attachTrack called | kind=${track.kind} source=${pipSource} sid=${track.sid} participant=${participant.identity} effectiveIdentity=${effectiveIdentity} targetId=${targetId}`);

        const initialDims = resolveInitialTrackDimensions(track, publication);

        if (effectiveIdentity && track.mediaStreamTrack) {
            try {
                registerPipVideoTrack(
                    effectiveIdentity,
                    pipSource,
                    track.mediaStreamTrack,
                    isLocalParticipant,
                    initialDims.width,
                    initialDims.height,
                    track,
                    publication
                );
            } catch (e) {
                console.warn('[Voice] registerPipVideoTrack error:', e);
            }
        }

        if (voiceState.dotnetHelper && effectiveIdentity) {
            voiceState.dotnetHelper.invokeMethodAsync('EnsureParticipantSlot', effectiveIdentity).catch(() => {});
            voiceState.dotnetHelper.invokeMethodAsync('OnVideoTrackStateChanged', effectiveIdentity, pipSource, true).catch(() => {});
            scheduleRoomSnapshotSync();
        }

        // While in Android Native PiP or external OS/Desktop PiP, the PiP surface is the sole video sink;
        // stage <video> elements are re-attached automatically when exiting external PiP.
        if (isStageSuppressedByExternalPip()) {
            return;
        }

        waitForElement(targetId).then((targetEl) => {
            if (!voiceState.currentRoom || isStageSuppressedByExternalPip()) {
                return;
            }
            if (track.mediaStreamTrack && track.mediaStreamTrack.readyState === 'ended') {
                clearParticipantVideoDom(effectiveIdentity, pipSource);
                return;
            }
            if (isLocalParticipant && !isScreenShare && !voiceState.localCameraDesired) {
                clearParticipantVideoDom(effectiveIdentity, 'camera');
                return;
            }
            
            // For video, targetEl is the native <video> tag strictly managed by Blazor.
            // We instruct LiveKit to attach the media stream directly to this stable DOM node.
            targetEl.style.opacity = '1';
            targetEl.style.pointerEvents = 'auto';
            targetEl.classList.add('has-stream');
            if (isLocalParticipant && !isScreenShare) {
                targetEl.style.transform = (voiceState.localCameraFacing === 'environment')
                    ? 'translateZ(0)'
                    : 'translateZ(0) scaleX(-1)';
            } else if (voiceState.fullscreenParticipantId === effectiveIdentity && voiceState.fullscreenGestureController) {
                refreshParticipantFullscreenPrimaryTarget();
            } else {
                targetEl.style.transform = 'translateZ(0)';
            }
            
            const currentBoundTrack = targetEl.srcObject && typeof targetEl.srcObject.getVideoTracks === 'function'
                ? targetEl.srcObject.getVideoTracks()[0]
                : null;
            const isAlreadyAttached = currentBoundTrack === track.mediaStreamTrack
                && (!Array.isArray(track.attachedElements) || track.attachedElements.includes(targetEl));
            if (!isAlreadyAttached) {
                if (currentBoundTrack && currentBoundTrack !== track.mediaStreamTrack) {
                    try { track.detach(targetEl); } catch (e) {}
                }
                track.attach(targetEl);
            }
            syncStageVideoAspectRatio(targetEl, track, publication);
            
            // CRITICAL: iOS Safari ignores HTML 'muted' attribute on dynamic WebRTC streams.
            // It MUST be set via JS property to bypass the simultaneous playback capability block.
            targetEl.muted = true;
            targetEl.playsInline = true;
            targetEl.disablePictureInPicture = true;

            const notifyVideoDimensions = () => {
                syncStageVideoAspectRatio(targetEl, track, publication);
                if (effectiveIdentity && track.mediaStreamTrack && targetEl.videoWidth > 0 && targetEl.videoHeight > 0) {
                    registerPipVideoTrack(
                        effectiveIdentity,
                        pipSource,
                        track.mediaStreamTrack,
                        isLocalParticipant,
                        targetEl.videoWidth,
                        targetEl.videoHeight,
                        track,
                        publication
                    );
                    if (!isLocalParticipant) {
                        updateParticipantQualityMax(participant);
                    } else if (voiceState.dotnetHelper && isScreenShare) {
                        const localMaxEdge = Math.max(targetEl.videoWidth, targetEl.videoHeight);
                        const settings = typeof track.mediaStreamTrack.getSettings === 'function' ? track.mediaStreamTrack.getSettings() : {};
                        const localFps = Math.max(30, Math.round(settings?.frameRate || 60));
                        const screenMaxEdge = Math.max(localMaxEdge, getScreenTrackMaxSourceEdge(track));
                        voiceState.dotnetHelper.invokeMethodAsync('OnLocalScreenDimensions', screenMaxEdge, localFps).catch(() => {});
                    }
                }
            };
            if (targetEl._spokesOnDimChange) {
                targetEl.removeEventListener('loadedmetadata', targetEl._spokesOnDimChange);
                targetEl.removeEventListener('loadeddata', targetEl._spokesOnDimChange);
                targetEl.removeEventListener('playing', targetEl._spokesOnDimChange);
                targetEl.removeEventListener('resize', targetEl._spokesOnDimChange);
            }
            targetEl._spokesOnDimChange = notifyVideoDimensions;
            targetEl.addEventListener('loadedmetadata', notifyVideoDimensions);
            targetEl.addEventListener('loadeddata', notifyVideoDimensions);
            targetEl.addEventListener('playing', notifyVideoDimensions);
            targetEl.addEventListener('resize', notifyVideoDimensions);
            if (targetEl.videoWidth > 0 && targetEl.videoHeight > 0) {
                notifyVideoDimensions();
            }
            
            // Keep stage <video> paused while the host view is hidden (minimized, tray, or another server)
            // so Chromium does not decode/composite unseen stage video (including the local screen-share preview).
            if (isHostViewHiddenForStage()) {
                if (typeof targetEl.pause === 'function') {
                    try { targetEl.pause(); } catch (e) {}
                }
            } else if (targetEl.paused) {
                targetEl.play().catch(e => console.warn("[Voice] Retry Play:", e));
            }
            
            console.log(`[Voice:Diag] DONE attaching video`);
        }).catch((err) => {
            console.warn(`[Voice] Failed to attach video track: ${err.message}`);
        });
    }
}


function updateParticipantQualityMax(participant, retryCount = 0) {
    const effectiveIdentity = getEffectiveParticipantIdentity(participant);
    if (!voiceState.dotnetHelper || !participant || !effectiveIdentity) return;
    if (effectiveIdentity === voiceState.currentRoom?.localParticipant?.identity) return;
    
    let maxOverallDim = 0;
    let maxOverallFps = 30;
    let hasTracks = false;
    let missingDims = false;

    const matchingParticipants = [participant];
    for (const p of getRemoteParticipantsIterable()) {
        if (p && p !== participant && getEffectiveParticipantIdentity(p) === effectiveIdentity) {
            matchingParticipants.push(p);
        }
    }
    
    for (const part of matchingParticipants) {
        for (let p of toArray(part.videoTrackPublications)) {
            if (p.isSubscribed && p.track) {
                hasTracks = true;
                
                // Prioritize the concrete video stream settings exactly as received from Livekit SFU
                let w = p.videoDimensions?.width || p.dimensions?.width || 0;
                let h = p.videoDimensions?.height || p.dimensions?.height || 0;
                
                // If the stream hasn't populated dimensions yet, check the native mediaStreamTrack or attached <video>
                if (w === 0 && h === 0 && p.track.mediaStreamTrack && typeof p.track.mediaStreamTrack.getSettings === 'function') {
                    let settings = p.track.mediaStreamTrack.getSettings();
                    w = settings.width || 0;
                    h = settings.height || 0;
                }
                if (w === 0 && h === 0 && typeof document !== 'undefined') {
                    const el = document.getElementById(getVideoElementIdForTrack(effectiveIdentity, p.track, p));
                    if (el && el.videoWidth > 0 && el.videoHeight > 0) {
                        w = el.videoWidth;
                        h = el.videoHeight;
                    }
                }
                
                if (w === 0 && h === 0) {
                    missingDims = true; // DO NOT fall back to trackInfo.width because that reflects the raw hardware probe, not the simulcast limit!
                } else {
                    let dim = Math.max(w, h);
                    if (dim > maxOverallDim) maxOverallDim = dim;
                }
                
                let trackFps = 30;
                if (typeof p.track.mediaStreamTrack?.getSettings === 'function') {
                    trackFps = Math.round(p.track.mediaStreamTrack.getSettings().frameRate || 30);
                }
                if (trackFps > maxOverallFps) maxOverallFps = trackFps;
            }
        }
    }
    
    if (hasTracks && missingDims && maxOverallDim === 0) {
        if (retryCount < 6) {
            const delayMs = retryCount === 0 ? 180 : 400;
            setTimeout(() => updateParticipantQualityMax(participant, retryCount + 1), delayMs);
        }
        return;
    }
    
    if (hasTracks && maxOverallDim === 0) return;
    
    invokeDotNetSafe('OnParticipantVideoDimensions', effectiveIdentity, maxOverallDim, maxOverallFps);
}

function handleRemoteAudioPublicationChanged(publication, participant) {
    const Kind = window.LivekitClient?.Track?.Kind;
    if (publication?.kind === Kind?.Audio ||
        publication?.track?.kind === Kind?.Audio ||
        getProcessedMicOwner(publication?.track, publication, participant)) {
        reconcileAudioRouting();
        if (voiceState.dotnetHelper && participant && participant !== voiceState.currentRoom?.localParticipant && isHumanMicrophonePublication(publication, participant)) {
            const pubs = getParticipantTrackPublications(participant);
            const stillPublished = pubs.includes(publication);
            if (stillPublished && !publication.isMuted) {
                invokeDotNetSafe('OnParticipantMuteChanged', participant.identity, false);
            }
        }
        scheduleRoomSnapshotSync();
    }
}

function handleTrackSubscribed(track, publication, participant) {
    if (track.kind === window.LivekitClient.Track.Kind.Audio) {
        reconcileAudioRouting();
        scheduleRoomSnapshotSync();
        if (track.source === window.LivekitClient.Track.Source.ScreenShareAudio || publication?.source === window.LivekitClient.Track.Source.ScreenShareAudio) {
            if (voiceState.dotnetHelper && participant?.identity) {
                voiceState.dotnetHelper.invokeMethodAsync('OnRemoteScreenAudioChanged', participant.identity, true).catch(() => {});
            }
        }
        return;
    }

    attachTrack(track, participant, publication);
    if (track.kind === window.LivekitClient.Track.Kind.Video) {
        updateParticipantQualityMax(participant);
        syncIosAudioSessionVideoMode().catch(() => {});
        // Request HIGH quality by default on subscribed video tracks so simulcast camera and
        // screen-share feeds promote cleanly to full resolution whenever downlink bandwidth allows.
        publication?.setVideoQuality?.(window.LivekitClient.VideoQuality.HIGH);
        if (publication?.source === window.LivekitClient.Track.Source.ScreenShare || track.source === window.LivekitClient.Track.Source.ScreenShare) {
            publication?.setVideoDimensions?.({ width: 3840, height: 2160 });
        }
    }
}

function detachTrackAndOrphanedMediaElement(track, publication) {
    if (track) {
        track.detach().forEach(el => safeRemove(el));
    }
    const sid = track?.sid || publication?.trackSid;
    if (sid) {
        const orphaned = document.getElementById(`media-${sid}`);
        if (orphaned) safeRemove(orphaned);
    }
    return sid;
}

function clearParticipantVideoState(participant, track, publication, { resolveEffectiveIdentity = true, notifyDotNet = true } = {}) {
    if (!participant || !participant.identity) return;
    const targetIdentity = resolveEffectiveIdentity ? getEffectiveParticipantIdentity(participant) : participant.identity;
    const pipSource = isScreenShareTrackOrPub(track, publication) ? 'screenshare' : 'camera';
    unregisterPipVideoTrack(targetIdentity, pipSource);
    clearParticipantVideoDom(targetIdentity, pipSource);
    if (notifyDotNet && voiceState.dotnetHelper && targetIdentity) {
        invokeDotNetSafe('OnVideoTrackStateChanged', targetIdentity, pipSource, false);
        scheduleRoomSnapshotSync();
    }
}

function handleTrackUnsubscribed(track, publication, participant) {
    const sid = detachTrackAndOrphanedMediaElement(track, publication);
    const isReconnecting = isRoomCurrentlyReconnecting();
    const isScreenAudio = track?.source === window.LivekitClient.Track.Source.ScreenShareAudio || publication?.source === window.LivekitClient.Track.Source.ScreenShareAudio;
    if (participant && participant.identity && (track?.kind === window.LivekitClient.Track.Kind.Audio || publication?.kind === window.LivekitClient.Track.Kind.Audio)) {
        const localIdentity = voiceState.currentRoom?.localParticipant?.identity;
        const processedOwner = getProcessedMicOwner(track, publication, participant);
        const effectiveIdentity = processedOwner || participant.identity;
        if (effectiveIdentity && effectiveIdentity !== localIdentity) {
            detachParticipantAudio(effectiveIdentity, sid);
        }
        if (!isReconnecting) {
            reconcileAudioRouting();
            if (isScreenAudio && voiceState.dotnetHelper) {
                invokeDotNetSafe('OnRemoteScreenAudioChanged', participant.identity, false);
            }
        }
    }
    
    // Explicit fallback to clear the Blazor-managed video tag, since LiveKit detaches tracks before firing this event.
    // Suppress during LiveKit room reconnects so synthetic handleRestarting unsubscribes do not tear down the stage UI,
    // and suppress when the participant already has a newer active subscribed video track for the same source.
    if (!isReconnecting && participant && participant.identity) {
        if (!track || track.kind === window.LivekitClient.Track.Kind.Video || (publication && publication.kind === window.LivekitClient.Track.Kind.Video)) {
            const isScreen = isScreenShareTrackOrPub(track, publication);
            const hasReplacement = toArray(participant.videoTrackPublications).some(pub => {
                if (!pub || pub.isMuted || !pub.track) return false;
                if (sid && (pub.trackSid === sid || pub.track?.sid === sid)) return false;
                return isScreenShareTrackOrPub(pub.track, pub) === isScreen;
            });
            if (!hasReplacement) {
                clearParticipantVideoState(participant, track, publication);
            }
        }
    }
    
    if (!isReconnecting && track && track.kind === window.LivekitClient.Track.Kind.Video) {
        updateParticipantQualityMax(participant);
        syncIosAudioSessionVideoMode().catch(() => {});
    }
}

async function measureWebRtcServerDelayMs(track) {
    try {
        let rttMs = 0;
        const engineRtt = voiceState.currentRoom?.engine?.client?.rtt;
        if (typeof engineRtt === 'number' && engineRtt > 0 && engineRtt < 2000) {
            rttMs = engineRtt;
        }

        const statsTarget = track?.sender
            || voiceState.currentRoom?.engine?.pcManager?.publisher?.pc
            || voiceState.currentRoom?.engine?.publisher?.pc;
        if (statsTarget && typeof statsTarget.getStats === 'function') {
            const report = await statsTarget.getStats();
            if (report && typeof report.forEach === 'function') {
                report.forEach(stat => {
                    if (stat.type === 'candidate-pair' && (stat.state === 'succeeded' || stat.nominated) && typeof stat.currentRoundTripTime === 'number' && stat.currentRoundTripTime > 0) {
                        rttMs = stat.currentRoundTripTime * 1000.0;
                    } else if (rttMs <= 0 && stat.type === 'remote-inbound-rtp' && typeof stat.roundTripTime === 'number' && stat.roundTripTime > 0) {
                        rttMs = stat.roundTripTime * 1000.0;
                    }
                });
            }
        }

        if (rttMs > 0) {
            return Math.max(0.5, Math.round((rttMs / 2.0) * 10) / 10);
        }
    } catch (e) {}
    return 0;
}

const BLUETOOTH_DEVICE_LABEL_REGEX = /\b(bluetooth|airpods|hands-free|hfp|ag audio|ble audio|galaxy buds|pixel buds|powerbeats|wh-1000|wf-1000|bose qc)\b/i;
const NON_BLUETOOTH_DONGLE_LABEL_REGEX = /\b(usb|2\.4\s*ghz|dongle|lightspeed|hyperspeed|slipstream|wired|line in)\b/i;

function isBluetoothCaptureActive(track) {
    try {
        if (isNativePlatform() && getNativePlatform() === 'android' && voiceState.activeNativeSpeakerId === 'bluetooth') {
            return true;
        }

        const rawMst = getRawCaptureMediaStreamTrack(track);
        const candidates = [rawMst?.label, track?.mediaStreamTrack?.label];
        for (const label of candidates) {
            if (typeof label === 'string' && label.length > 0) {
                if (BLUETOOTH_DEVICE_LABEL_REGEX.test(label) && !NON_BLUETOOTH_DONGLE_LABEL_REGEX.test(label)) {
                    return true;
                }
            }
        }
    } catch (e) {}
    return false;
}

function refreshLocalAudioPipelineTelemetry() {
    try {
        if (voiceState.usingNativeMobileVoice) {
            if (voiceState.dotnetHelper) {
                voiceState.dotnetHelper.invokeMethodAsync(
                    'OnLocalAudioPipelineTelemetry',
                    voiceState.nativeEffectiveFilter || 'rnnoise',
                    estimateNativeMobileCaptureMs(voiceState.nativeEffectiveFilter),
                    getNativeVoiceTelemetryPlatformTag(),
                    0
                )?.catch?.(() => {});
            }
            return;
        }
        if (!voiceState.currentRoom?.localParticipant || !window.LivekitClient?.Track) return;
        const micPub = toArray(voiceState.currentRoom.localParticipant.audioTrackPublications)
            .find(p => p.source === window.LivekitClient.Track.Source.Microphone && p.track);
        if (micPub?.track) {
            reportLocalAudioPipelineTelemetry(micPub.track);
        }
    } catch (e) {}
}

function reportLocalAudioPipelineTelemetry(track) {
    if (!voiceState.dotnetHelper || !track) return;
    try {
        let platformTag = 'web';
        if (isNativePlatform()) {
            platformTag = getNativePlatform() || 'mobile';
        } else if (isDesktopClient()) {
            platformTag = 'desktop';
        } else if (isAppleMobileDevice()) {
            platformTag = 'ios-web';
        } else if (typeof navigator !== 'undefined' && /Android/i.test(navigator.userAgent || '')) {
            platformTag = 'android-web';
        }

        const hasAiProcessor = Boolean(track.processor);
        const effectiveFilter = hasAiProcessor
            ? voiceState.currentNoiseSuppression
            : (voiceState.currentNoiseSuppression === 'none' ? 'none' : 'webrtc');

        let captureMs = 0;
        const rawMst = getRawCaptureMediaStreamTrack(track) || track.mediaStreamTrack;
        if (rawMst && typeof rawMst.getSettings === 'function') {
            const settings = rawMst.getSettings() || {};
            if (typeof settings.latency === 'number' && settings.latency > 0 && settings.latency < 1.0) {
                captureMs = settings.latency * 1000.0;
            }
        }

        if (platformTag === 'android' || platformTag === 'android-web') {
            // Android WebView getSettings().latency often reports only the ~9ms AAudio/OpenSL callback
            // block size rather than total Android HAL + hardware AEC/AGC capture buffering (>= 65ms).
            captureMs = Math.max(captureMs, 65.0);
        } else if (platformTag === 'ios' || platformTag === 'ios-web') {
            captureMs = Math.max(captureMs, 30.0);
        } else if (captureMs <= 0) {
            captureMs = 15.0;
        }

        if (isBluetoothCaptureActive(track)) {
            // Bluetooth HFP/SCO/BLE voice capture adds ~150ms radio packetization + codec buffering
            captureMs += 150.0;
        }

        let filterLookaheadMs = 0;
        const ctx = track.audioContext || _globalAudioContext;
        const baseLatMs = (ctx && typeof ctx.baseLatency === 'number' && ctx.baseLatency > 0) ? (ctx.baseLatency * 1000.0) : 5.3;
        if (effectiveFilter === 'rnnoise') {
            filterLookaheadMs = 10.0 + baseLatMs;
        } else if (effectiveFilter === 'deepfilternet') {
            filterLookaheadMs = 25.0 + baseLatMs;
        } else if (effectiveFilter === 'webrtc') {
            filterLookaheadMs = 10.0;
        }

        const totalEstimatedMs = Math.round((captureMs + filterLookaheadMs) * 10) / 10;
        const pushTelemetry = async () => {
            if (!voiceState.dotnetHelper || !voiceState.currentRoom) return;
            const serverDelayMs = await measureWebRtcServerDelayMs(track);
            invokeDotNetSafe('OnLocalAudioPipelineTelemetry', effectiveFilter, totalEstimatedMs, platformTag, serverDelayMs);
        };

        pushTelemetry();
        setTimeout(pushTelemetry, 2000);
    } catch (e) {}
}

function handleLocalTrackPublished(publication, participant) {
    if (publication.track) {
        // Only attach local VIDEO tracks (camera preview). Never attach local AUDIO —
        // playing your own mic back through speakers causes echo/feedback.
        if (publication.track.kind === window.LivekitClient.Track.Kind.Audio) {
            if (publication.source !== window.LivekitClient?.Track?.Source?.ScreenShareAudio) {
                if (voiceState.isLocalSoftwareMuted) {
                    unpublishAndStopAllLocalMicTracks().catch(() => {});
                    return;
                }
                bindLocalHardwareMuteMonitor(publication.track);
                const mediaStreamTrack = publication.track.processor?.processedTrack || publication.track.mediaStreamTrack;
                const rawTrack = getRawCaptureMediaStreamTrack(publication.track);
                if (participant && participant.identity && mediaStreamTrack) {
                    registerLocalMicVad(participant.identity, mediaStreamTrack, rawTrack);
                }
                notifyHostVoiceState(Boolean(voiceState.currentRoom), isEffectiveLocalMicMuted());
                updatePipCallState({ isMuted: isEffectiveLocalMicMuted() });
                invokeDotNetSafe('OnLocalMuted', false);
                scheduleRoomSnapshotSync();
                reportLocalAudioPipelineTelemetry(publication.track);
            }
            return;
        }
        attachTrack(publication.track, participant, publication);
        
        if (voiceState.dotnetHelper) {
            let { maxDim, maxFps: fps } = getHardwareCameraCapabilities(publication.track.mediaStreamTrack);
            if (publication.source === window.LivekitClient.Track.Source.ScreenShare) {
                maxDim = Math.max(maxDim, getScreenTrackMaxSourceEdge(publication.track));
                fps = Math.max(fps, 60);
            }
            if (maxDim > 0) {
                const method = publication.source === window.LivekitClient.Track.Source.ScreenShare
                    ? 'OnLocalScreenDimensions'
                    : 'OnLocalVideoDimensions';
                invokeDotNetSafe(method, maxDim, fps);
            }
        }
    }
}

// --- Sender-Only Local Microphone Voice Activity Detection (VAD) ---
// Each client evaluates only its own local capture microphone and reports speaking transitions
// (`OnLocalSpeakingChanged(bool)`) to the server. Remote speaking indicators and PiP badges
// are driven solely by canonical server state (VoiceStateService / GlobalVoiceService).
let _localMicVadEntry = null; // { identity, source, analyser, dataArray, lastSpokeTime, isSpeaking, track, rawTrack, noiseFloor, consecutiveActiveFrames }
let _vadIntervalId = null;

function createVadAnalyserNodeEntry(identity, mediaStreamTrack, rawTrack = null) {
    const ctx = getVoiceAudioContext();
    if (!ctx) return null;

    const stream = new MediaStream([mediaStreamTrack]);
    const source = ctx.createMediaStreamSource(stream);
    const analyser = ctx.createAnalyser();
    analyser.fftSize = 256;
    analyser.smoothingTimeConstant = 0.2;
    source.connect(analyser);

    return {
        identity,
        source,
        analyser,
        dataArray: new Uint8Array(analyser.frequencyBinCount),
        lastSpokeTime: 0,
        isSpeaking: false,
        track: mediaStreamTrack,
        rawTrack: rawTrack || mediaStreamTrack,
        noiseFloor: 14.0,
        consecutiveActiveFrames: 0
    };
}

function registerLocalMicVad(identity, mediaStreamTrack, rawTrack = null) {
    if (!identity || !mediaStreamTrack) return;
    unregisterLocalMicVad(false);

    try {
        const entry = createVadAnalyserNodeEntry(identity, mediaStreamTrack, rawTrack);
        if (!entry) return;
        _localMicVadEntry = entry;
        startVADLoop();
    } catch (err) {
        console.warn('[Voice:VAD] Failed to register local mic VAD for', identity, err);
    }
}

function unregisterLocalMicVad(shouldNotify = true) {
    if (_localMicVadEntry) {
        try { _localMicVadEntry.source.disconnect(); } catch (e) {}
        _localMicVadEntry = null;
    }
    stopVADLoop();
    if (shouldNotify) {
        reportLocalSpeakingState(false);
    }
}

function clearAllVoiceTracks() {
    if (_localMicVadEntry) {
        try { _localMicVadEntry.source.disconnect(); } catch (e) {}
        _localMicVadEntry = null;
    }
    stopVADLoop();
    resetLocalSpeakingState();
}

function startVADLoop() {
    if (_vadIntervalId) return;
    _vadIntervalId = setInterval(vadLoopTick, 30); // ~33 Hz evaluation
}

function stopVADLoop() {
    if (_vadIntervalId) {
        clearInterval(_vadIntervalId);
        _vadIntervalId = null;
    }
}

function evaluateLocalMicVad(item, now, sensMult) {
    const raw = item.rawTrack || item.track;
    const ignoreTrackMutedFlag = isNativePlatform() || isAppleMobileDevice();
    if (isEffectiveLocalMicMuted()
        || !item.track?.enabled
        || item.track?.readyState === 'ended'
        || (!ignoreTrackMutedFlag && item.track?.muted)
        || (raw && (!raw.enabled || raw.readyState === 'ended' || (!ignoreTrackMutedFlag && raw.muted)))) {
        if (item.isSpeaking) {
            item.isSpeaking = false;
            item.consecutiveActiveFrames = 0;
            return true;
        }
        return false;
    }

    item.analyser.getByteFrequencyData(item.dataArray);

    // Speech formant band analysis:
    // Exclude bin 0 (0 - 187.5 Hz) which contains DC bias, 50/60Hz AC hum, desk thumps, and fan rumble.
    // Focus on bins 1 to 21 (~187.5 Hz to ~4,125 Hz) where human vocal energy and formants concentrate.
    const minBin = 1;
    const maxBin = Math.min(21, item.dataArray.length - 1);
    let voiceSum = 0;
    let voicePeak = 0;
    for (let i = minBin; i <= maxBin; i++) {
        const val = item.dataArray[i];
        voiceSum += val;
        if (val > voicePeak) voicePeak = val;
    }
    const voiceAvg = voiceSum / (maxBin - minBin + 1);

    if (item.noiseFloor === undefined) item.noiseFloor = 14.0;
    if (!item.isSpeaking) {
        if (voiceAvg < item.noiseFloor) {
            item.noiseFloor = item.noiseFloor * 0.92 + voiceAvg * 0.08;
        } else if (voiceAvg < item.noiseFloor + 15) {
            item.noiseFloor = item.noiseFloor * 0.995 + voiceAvg * 0.005;
        }
        item.noiseFloor = Math.max(5.0, Math.min(55.0, item.noiseFloor));
    }

    const thresholdAvg = Math.max(16.0, item.noiseFloor + 12.0) * sensMult;
    const thresholdPeak = Math.max(60.0, item.noiseFloor * 2.2 + 25.0) * sensMult;

    const isActive = (voiceAvg > thresholdAvg && voicePeak > thresholdPeak) ||
                     (voicePeak > Math.max(105.0, thresholdPeak * 1.4));

    if (isActive) {
        item.consecutiveActiveFrames = (item.consecutiveActiveFrames || 0) + 1;
        if (item.consecutiveActiveFrames >= VAD_ATTACK_FRAMES) {
            item.lastSpokeTime = now;
            if (!item.isSpeaking) {
                item.isSpeaking = true;
                return true;
            }
        }
    } else {
        item.consecutiveActiveFrames = 0;
        if (item.isSpeaking && (now - item.lastSpokeTime >= VAD_HANGOVER_MS)) {
            item.isSpeaking = false;
            return true;
        }
    }

    return false;
}

function vadLoopTick() {
    if (!_localMicVadEntry || !_localMicVadEntry.identity) {
        return;
    }

    const now = Date.now();
    // Sensitivity multiplier: at 50 -> 1.0; at 0 -> 1.5 (stricter); at 100 -> 0.5 (more sensitive)
    const sensMult = 1.0 + (50 - voiceState.voiceSensitivity) * 0.01;

    const raw = _localMicVadEntry.rawTrack || _localMicVadEntry.track;
    if (voiceState.isLocalHardwareMuted && raw && raw.muted === false && raw.readyState === 'live') {
        voiceState.isLocalHardwareMuted = false;
        if (_localMicVadEntry.track && typeof _localMicVadEntry.track.enabled === 'boolean') {
            _localMicVadEntry.track.enabled = !voiceState.isLocalSoftwareMuted;
        }
        notifyHostVoiceState(Boolean(voiceState.currentRoom), isEffectiveLocalMicMuted());
        scheduleRoomSnapshotSync();
        voiceState.dotnetHelper?.invokeMethodAsync('OnLocalHardwareMuteChanged', false)?.catch?.(() => {});
    }
    if (
        !_localMicVadEntry.isSpeaking &&
        now - (_localMicVadEntry.lastSpokeTime || 0) >= 2500 &&
        now - _lastCaptureFifoFlushMs >= 60000 &&
        isLocalInActivePhysicalRoom() &&
        voiceState.activeLocalAudioProcessor &&
        typeof voiceState.activeLocalAudioProcessor.flushCaptureFifo === 'function'
    ) {
        if (voiceState.activeLocalAudioProcessor.flushCaptureFifo()) {
            _lastCaptureFifoFlushMs = now;
        }
    }
    if (evaluateLocalMicVad(_localMicVadEntry, now, sensMult)) {
        reportLocalSpeakingState(_localMicVadEntry.isSpeaking);
    }
}

function formatLivekitDisconnectReason(reason) {
    if (reason === undefined || reason === null || reason === '') return 'UNKNOWN_REASON';
    const enumObj = window.LivekitClient?.DisconnectReason;
    if (typeof reason === 'number' && enumObj && typeof enumObj[reason] === 'string') {
        return enumObj[reason];
    }
    if (typeof reason === 'string') {
        const numeric = Number(reason);
        if (!Number.isNaN(numeric) && enumObj && typeof enumObj[numeric] === 'string') {
            return enumObj[numeric];
        }
        return reason;
    }
    return String(reason);
}

function handleDisconnect(reason) {
    const reasonName = formatLivekitDisconnectReason(reason);
    console.log(`[Voice] Disconnected (reason=${reasonName})`);
    teardownLocalRoomState();
    releaseNativeCallResources().catch(() => {});

    if (voiceState.dotnetHelper) {
        try {
            if (reasonName === 'DUPLICATE_IDENTITY') {
                const p = voiceState.dotnetHelper.invokeMethodAsync('OnRoomDisconnectedWithReason', reasonName);
                if (p && typeof p.catch === 'function') {
                    p.catch(() => {
                        try { voiceState.dotnetHelper?.invokeMethodAsync?.('OnRoomDisconnected')?.catch?.(() => {}); } catch (e) {}
                    });
                }
            } else {
                const p = voiceState.dotnetHelper.invokeMethodAsync('OnRoomDisconnected');
                if (p && typeof p.catch === 'function') {
                    p.catch(() => {});
                }
            }
        } catch(e) {}
    }
}

function isRoomCurrentlyReconnecting() {
    if (!voiceState.currentRoom) return false;
    if (voiceState.isRoomReconnecting) return true;
    const ConnectionState = window.LivekitClient?.ConnectionState;
    const reconnectingState = ConnectionState?.Reconnecting || 'reconnecting';
    const signalReconnectingState = ConnectionState?.SignalReconnecting || 'signalReconnecting';
    if (voiceState.currentRoom.state === reconnectingState ||
        voiceState.currentRoom.state === 'reconnecting' ||
        voiceState.currentRoom.state === signalReconnectingState ||
        voiceState.currentRoom.state === 'signalReconnecting') {
        return true;
    }
    if (voiceState.currentRoom.engine?.attemptingReconnect || voiceState.currentRoom.engine?.fullReconnectOnNext) {
        return true;
    }
    return false;
}

function handleLocalUnpublished(publication, participant) {
    detachTrackAndOrphanedMediaElement(publication?.track, publication);
    
    const isReconnecting = isRoomCurrentlyReconnecting();

    // Aggressive fallback to ensure video tags are wiped so CSS :has(video) unhides the avatar
    if (publication.kind === window.LivekitClient.Track.Kind.Video || (publication.track && publication.track.kind === window.LivekitClient.Track.Kind.Video)) {
        clearParticipantVideoState(participant, publication.track, publication, {
            resolveEffectiveIdentity: false,
            notifyDotNet: !isReconnecting
        });
    }

    const isScreenShareVideo = publication?.source === window.LivekitClient?.Track?.Source?.ScreenShare;
    if (isScreenShareVideo && !isReconnecting) {
        const unpublishedTrack = publication.videoTrack || publication.track;
        if (unpublishedTrack && Array.isArray(voiceState.globalScreenTracks) && voiceState.globalScreenTracks.length > 0) {
            voiceState.globalScreenTracks = voiceState.globalScreenTracks.filter(t => t !== unpublishedTrack);
        }
        if (voiceState.currentRoom) {
            notifyHostVoiceState(true, isEffectiveLocalMicMuted());
        }
    }

    const isAudio = publication.kind === window.LivekitClient.Track.Kind.Audio || publication.track?.kind === window.LivekitClient.Track.Kind.Audio;
    const isMicAudio = isAudio && publication.source === window.LivekitClient.Track.Source.Microphone;

    if (participant && participant.identity && isMicAudio) {
        cleanupLocalHardwareMuteMonitor();
        unregisterLocalMicVad();
    }

    // Fired when the user mutes themselves or stops video (suppressed during live noise-filter swap or LiveKit full reconnect)
    if (voiceState.dotnetHelper && !voiceState.suppressNextUnpublishMuteNotify && !isReconnecting) {
        if (isMicAudio && voiceState.isLocalSoftwareMuted) {
            invokeDotNetSafe('OnLocalMuted', true);
            scheduleRoomSnapshotSync();
        } else if (publication.source === window.LivekitClient.Track.Source.ScreenShareAudio) {
            invokeDotNetSafe('OnScreenShareAudioActive', false);
        }
    }
}

function handleTrackMuted(publication, participant) {
    const VideoKind = window.LivekitClient?.Track?.Kind?.Video;
    const isVideo = Boolean(
        VideoKind &&
        (publication?.kind === VideoKind || publication?.track?.kind === VideoKind)
    );

    // Only clear DOM elements for muted video tracks. Detaching muted <audio> elements
    // removes them from #voice-audio-container while leaving _participantGainNodes entries
    // alive, forcing redundant re-attaches and Web Audio graph rebuilds on unmute.
    if (isVideo) {
        detachTrackAndOrphanedMediaElement(publication?.track, publication);
    }

    if (participant !== voiceState.currentRoom?.localParticipant && isHumanMicrophonePublication(publication, participant)) {
        if (voiceState.dotnetHelper) {
            invokeDotNetSafe('OnParticipantMuteChanged', participant.identity, true);
            scheduleRoomSnapshotSync();
        }
    }

    // Fallback: forcefully clear video element if it's a video track muting
    if (isVideo) {
        clearParticipantVideoState(participant, publication?.track, publication);
    }
}

function handleTrackUnmuted(publication, participant) {
    // CRITICAL: Notify OnParticipantMuteChanged(..., false) OUTSIDE `if (publication.track)`!
    // During Physical Room Isolation, remote clients are unsubscribed from a co-located peer's raw mic
    // (`publication.track == null`) while receiving `processed-mic:<userId>`. Unblocking this ensures
    // lowering a hardware boom-mic immediately clears the remote mute icon.
    if (participant !== voiceState.currentRoom?.localParticipant && isHumanMicrophonePublication(publication, participant)) {
        if (voiceState.dotnetHelper) {
            invokeDotNetSafe('OnParticipantMuteChanged', participant.identity, false);
            scheduleRoomSnapshotSync();
        }
    }

    if (publication?.track) {
        if (publication.track.kind === window.LivekitClient.Track.Kind.Audio) {
            if (participant === voiceState.currentRoom?.localParticipant) {
                if (publication.source !== window.LivekitClient?.Track?.Source?.ScreenShareAudio) {
                    bindLocalHardwareMuteMonitor(publication.track);
                    const mediaStreamTrack = publication.track.processor?.processedTrack || publication.track.mediaStreamTrack;
                    const rawTrack = getRawCaptureMediaStreamTrack(publication.track);
                    if (participant.identity && mediaStreamTrack) {
                        registerLocalMicVad(participant.identity, mediaStreamTrack, rawTrack);
                    }
                }
            } else {
                reconcileAudioRouting();
            }
            return;
        }
        attachTrack(publication.track, participant, publication);
    }
}

function handleParticipantConnected(participant) {
    reconcileAudioRouting();
    if (isSyntheticIdentity(participant?.identity)) return;
    if (voiceState.dotnetHelper) {
        invokeDotNetSafe('OnParticipantConnected', participant.identity);
        scheduleRoomSnapshotSync();
    }
}

function handleParticipantDisconnected(participant) {
    const isReconnecting = isRoomCurrentlyReconnecting();
    if (participant && participant.identity) {
        detachParticipantAudio(participant.identity);
        if (!isReconnecting) {
            unregisterPipVideoTrack(participant.identity);
            clearParticipantVideoDom(participant.identity);
            voiceState.mutedRemoteScreenAudio.delete(participant.identity);
            invokeDotNetSafe('OnRemoteScreenAudioChanged', participant.identity, false);
        }
    }
    if (isReconnecting) return;
    reconcileAudioRouting();
    if (isSyntheticIdentity(participant?.identity)) return;
    if (voiceState.dotnetHelper) {
        invokeDotNetSafe('OnParticipantDisconnected', participant.identity);
        scheduleRoomSnapshotSync();
    }
}

function handleRoomReconnected() {
    voiceState.isRoomReconnecting = false;
    if (!voiceState.currentRoom) return;
    console.info('[Voice] LiveKit room reconnected — flushing capture FIFO and reconciling audio routing');
    flushLocalCaptureFifoIfAvailable();
    reconcileAudioRouting();
    rebindRemoteAudioElementsPlayback(false);

    if (!voiceState.isLocalSoftwareMuted && voiceState.currentRoom.localParticipant && window.LivekitClient?.Track) {
        const micPub = toArray(voiceState.currentRoom.localParticipant.audioTrackPublications)
            .find(p => p.source === window.LivekitClient.Track.Source.Microphone && p.track);
        const rawMst = micPub?.track ? (getRawCaptureMediaStreamTrack(micPub.track) || micPub.track.mediaStreamTrack) : null;
        if (!micPub || !micPub.track || !rawMst || rawMst.readyState === 'ended') {
            console.info('[Voice] Local microphone publication missing or ended after full reconnect; re-publishing microphone');
            setMicrophoneEnabled(true).catch(err => console.warn('[Voice] Post-reconnect mic recovery failed:', err));
        } else {
            bindLocalHardwareMuteMonitor(micPub.track);
            const vadTrack = micPub.track.processor?.processedTrack || micPub.track.mediaStreamTrack;
            if (voiceState.currentRoom.localParticipant.identity && vadTrack) {
                registerLocalMicVad(voiceState.currentRoom.localParticipant.identity, vadTrack, rawMst);
            }
        }
    }

    publishVoiceRoomSnapshot('OnRoomReconnectedAsync');
}

// Re-attach all active tracks to their DOM containers.
// Called when the Chat component remounts (e.g. after navigating to another channel and back)
// so that existing LiveKit tracks are connected to the freshly created DOM elements.
export async function reattachAllTracks() {
    if (voiceState.usingNativeMobileVoice) {
        scheduleNativeMobileVideoSlotsSync();
        return;
    }
    if (!voiceState.currentRoom) return;
    const Kind = window.LivekitClient?.Track?.Kind;

    if (voiceState.currentRoom.localParticipant) {
        const pubs = getParticipantTrackPublications(voiceState.currentRoom.localParticipant);
        pubs.forEach(pub => {
            if (pub.track && (!Kind || pub.track.kind === Kind.Video)) {
                attachTrack(pub.track, voiceState.currentRoom.localParticipant, pub);
            }
        });
    }

    reconcileAudioRouting();
    rebindRemoteAudioElementsPlayback(false);

    getRemoteParticipantsIterable().forEach(p => {
        getParticipantTrackPublications(p).forEach(pub => {
            if (pub.track && (!Kind || pub.track.kind === Kind.Video)) {
                attachTrack(pub.track, p, pub);
            }
        });
    });
}

export async function setRemoteVideoQuality(participantIdentity, qualityStr) {
    if (!participantIdentity) return;
    if (voiceState.usingNativeMobileVoice) {
        await getNativeVoicePlugin()?.setRemoteVideoQuality?.({
            identity: participantIdentity,
            quality: qualityStr || 'Auto'
        })?.catch?.(() => {});
        return;
    }
    if (!voiceState.currentRoom) return;

    const matchingParticipants = getRemoteParticipantsIterable()
        .filter(p => p && getEffectiveParticipantIdentity(p) === participantIdentity);
    if (matchingParticipants.length === 0) return;

    let dims = null;
    if (qualityStr === '2160p') dims = { width: 3840, height: 2160 };
    else if (qualityStr === '1440p') dims = { width: 2560, height: 1440 };
    else if (qualityStr === '1080p') dims = { width: 1920, height: 1080 };
    else if (qualityStr === '720p') dims = { width: 1280, height: 720 };
    else if (qualityStr === '360p') dims = { width: 640, height: 360 };

    for (const participant of matchingParticipants) {
        let getPublications = toArray(participant.videoTrackPublications);
        if (getPublications.length === 0 && participant.tracks) {
            getPublications = toArray(participant.tracks).filter(p => p.kind === window.LivekitClient.Track.Kind.Video);
        }

        getPublications.forEach((pub) => {
            if (dims) {
                pub.setVideoDimensions?.(dims);
            } else {
                // Passing a huge dimension effectively turns off manual UI constraints, returning default simulcast control
                pub.setVideoDimensions?.({ width: 4096, height: 2160 });
                pub.setVideoQuality?.(window.LivekitClient.VideoQuality.HIGH);
            }
        });
    }
}

// Refresh video layouts and resume any paused streams without tearing down WebRTC media pipelines
export function refreshVideoLayouts() {
    if (voiceState.usingNativeMobileVoice) {
        scheduleNativeMobileVideoSlotsSync();
        return;
    }
    const hideStageVideos = isHostViewHiddenForStage() || isStageSuppressedByExternalPip();
    document.querySelectorAll('video.has-stream').forEach(v => {
        if (!v.isConnected) return;
        const stream = v.srcObject;
        const videoTracks = stream && typeof stream.getVideoTracks === 'function' ? stream.getVideoTracks() : [];
        const hasLiveTrack = videoTracks.some(t => t && t.readyState === 'live');
        if (!stream || (videoTracks.length > 0 && !hasLiveTrack)) {
            safeRemove(v);
            return;
        }
        v.style.opacity = '1';
        v.muted = true;
        syncStageVideoAspectRatio(v);
        const isStageVideo = !v.closest || !v.closest('#spokes-voice-pip-overlay');
        if (hideStageVideos && isStageVideo) {
            if (!v.paused && typeof v.pause === 'function') {
                try { v.pause(); } catch (e) {}
            }
            return;
        }
        if (v.paused) {
            v.play().catch(e => {
                if (e?.name !== 'AbortError') {
                    console.warn("[Voice] Retry Play:", e);
                }
            });
        }
    });
    syncLocalStageVideoMirroring();
}

// Release Wake Lock on sudden page exit
function releaseWakeLock() {
    if (isNativePlatform() && window.Capacitor?.Plugins?.KeepAwake) {
        window.Capacitor.Plugins.KeepAwake.allowSleep().catch(()=>{});
    }
}
window.addEventListener('beforeunload', releaseWakeLock);
window.addEventListener('pagehide', releaseWakeLock);

export async function promptIosVoiceIsolation() {
    let audioPlugin = getAudioSessionPlugin();
    if (audioPlugin && typeof audioPlugin.promptVoiceIsolation === 'function') {
        let res = await audioPlugin.promptVoiceIsolation();
        if (res?.activeMode) {
            _iosActiveMicrophoneMode = res.activeMode;
        }
        return res;
    }
    return { status: 'unsupported', activeMode: 'unsupported' };
}

export async function getIosMicrophoneMode() {
    let audioPlugin = getAudioSessionPlugin();
    if (audioPlugin && typeof audioPlugin.getMicrophoneMode === 'function') {
        let res = await audioPlugin.getMicrophoneMode();
        if (res?.activeMode) {
            _iosActiveMicrophoneMode = res.activeMode;
            return res.activeMode;
        }
    }
    return _iosActiveMicrophoneMode || 'unsupported';
}

// --- Picture-in-Picture & Native Call Metadata Exports ---

export function syncPipMetadata(payload) {
    updatePipCallState(payload || {});
}

export function syncSpeakingParticipants(channelId, speakerIds) {
    const ids = Array.isArray(speakerIds) ? speakerIds : [];
    if (typeof channelId === 'string' && channelId.length > 0) {
        voiceState.activeVoiceChannelId = channelId;
        if (typeof window !== 'undefined' && window.spokesVoiceSpeaking?.syncChannel) {
            window.spokesVoiceSpeaking.syncChannel(channelId, ids);
        }
    }
    updatePipSpeakingParticipants(ids);
}

export function setInAppPipActive(active) {
    setPipInAppActive(Boolean(active));
}

export function refreshInAppPip() {
    syncInAppPipElements();
}

export async function toggleOsPip() {
    return await togglePipOsWindow();
}

export function closeOsPip() {
    closePipOsWindow();
}

export function isOsPipSupported() {
    return checkOsPipSupported();
}

// --- Full-Screen Remote Participant Pinch-to-Zoom & 90° Rotation ---

function resolveFullscreenPrimarySource(identity) {
    if (!identity) return 'camera';
    if (voiceState.usingNativeMobileVoice) {
        const sources = voiceState.nativeVideoTracks.get(identity);
        return sources?.has('screenshare') ? 'screenshare' : 'camera';
    }
    const screenEl = document.getElementById(`participant-screen-${identity}`);
    if (screenEl && screenEl.classList.contains('has-stream')) {
        return 'screenshare';
    }
    return 'camera';
}

function resolveFullscreenTargetElement(identity) {
    if (!identity) return null;
    const source = resolveFullscreenPrimarySource(identity);
    const screenEl = document.getElementById(`participant-screen-${identity}`);
    const camEl = document.getElementById(`participant-video-${identity}`);
    if (source === 'screenshare' && camEl) {
        // Ensure corner webcam stays unzoomed while screen share is the primary zoom target
        camEl.style.transform = 'translateZ(0)';
        camEl.style.transition = '';
    }
    return source === 'screenshare' ? screenEl : camEl;
}

function resolveFullscreenAspectRatio(identity) {
    if (!identity) return 0;
    const source = resolveFullscreenPrimarySource(identity);
    if (voiceState.usingNativeMobileVoice) {
        const dims = voiceState.nativeVideoDims.get(`${identity}:${source}`)
            || voiceState.nativeVideoDims.get(`${identity}:camera`);
        if (dims && dims.width > 0 && dims.height > 0) {
            return dims.width / dims.height;
        }
    }
    const el = resolveFullscreenTargetElement(identity);
    if (el && el.videoWidth > 0 && el.videoHeight > 0) {
        return el.videoWidth / el.videoHeight;
    }
    const rectEl = document.getElementById(`participant-rect-${identity}`);
    if (rectEl) {
        const prop = source === 'screenshare' ? '--screen-aspect' : '--cam-aspect';
        const val = parseFloat(getComputedStyle(rectEl).getPropertyValue(prop) || getComputedStyle(rectEl).getPropertyValue('--tile-video-aspect') || '0');
        if (Number.isFinite(val) && val > 0.05 && val < 20) {
            return val;
        }
    }
    return 16 / 9;
}

function resetParticipantVideoTransform(identity, { screen = true, camera = true } = {}) {
    if (!identity) return;
    if (screen) {
        const screenEl = document.getElementById(`participant-screen-${identity}`);
        if (screenEl) {
            screenEl.style.transform = 'translateZ(0)';
            screenEl.style.transition = '';
        }
    }
    if (camera) {
        const camEl = document.getElementById(`participant-video-${identity}`);
        if (camEl) {
            camEl.style.transform = 'translateZ(0)';
            camEl.style.transition = '';
        }
    }
}

function refreshParticipantFullscreenPrimaryTarget() {
    if (!voiceState.fullscreenParticipantId || !voiceState.fullscreenGestureController) return;
    const newSource = resolveFullscreenPrimarySource(voiceState.fullscreenParticipantId);
    const prevSource = voiceState.fullscreenTransform.source;

    if (prevSource && prevSource !== newSource) {
        resetParticipantVideoTransform(voiceState.fullscreenParticipantId);
        voiceState.fullscreenTransform.source = newSource;
        voiceState.fullscreenGestureController.resetZoom(false);
    } else {
        voiceState.fullscreenTransform.source = newSource;
        if (newSource === 'screenshare') {
            resetParticipantVideoTransform(voiceState.fullscreenParticipantId, { screen: false, camera: true });
        }
        voiceState.fullscreenGestureController.updateTransform(false);
    }
    scheduleNativeMobileVideoSlotsSync();
}

export function enterParticipantFullscreen(participantId, dotNetObjRef) {
    if (!participantId) return;
    if (voiceState.fullscreenGestureController) {
        exitParticipantFullscreen(false);
    }

    const containerEl = document.getElementById(`participant-rect-${participantId}`);
    if (!containerEl) return;

    voiceState.fullscreenParticipantId = participantId;
    voiceState.fullscreenDotNetHelper = dotNetObjRef || null;
    const primarySource = resolveFullscreenPrimarySource(participantId);
    voiceState.fullscreenTransform = {
        identity: participantId,
        source: primarySource,
        scale: 1,
        panX: 0,
        panY: 0,
        rotationDeg: 0
    };

    try {
        if (window.Capacitor?.Plugins?.StatusBar) {
            window.Capacitor.Plugins.StatusBar.hide().catch(() => {});
        }
    } catch (e) {}

    window.SpokesBridge?.backStack?.remove?.('voiceFullscreen', { popHistory: false });
    window.SpokesBridge?.backStack?.push?.('voiceFullscreen', () => {
        const helper = voiceState.fullscreenDotNetHelper;
        exitParticipantFullscreen(false);
        if (helper) {
            helper.invokeMethodAsync('OnFullscreenBackRequested').catch(() => {});
        }
    }, { useHistoryGuard: true, source: 'voice-fullscreen' });

    voiceState.fullscreenGestureController = new MediaZoomGestureController({
        container: containerEl,
        getTargetElement: () => resolveFullscreenTargetElement(participantId),
        getContentAspectRatio: () => resolveFullscreenAspectRatio(participantId),
        maxScale: 10,
        canSwipeHorizontal: false,
        enableSwipeDownClose: true,
        onTransformChange: ({ scale, x, y, rotationDeg }) => {
            voiceState.fullscreenTransform = {
                identity: participantId,
                source: resolveFullscreenPrimarySource(participantId),
                scale,
                panX: x,
                panY: y,
                rotationDeg
            };
            if (voiceState.usingNativeMobileVoice) {
                scheduleNativeMobileVideoSlotsSync();
            }
        },
        onZoomPercentChange: (percent) => {
            if (voiceState.fullscreenDotNetHelper) {
                voiceState.fullscreenDotNetHelper.invokeMethodAsync('OnFullscreenZoomChanged', percent).catch(() => {});
            }
        },
        onSingleTap: () => {
            if (voiceState.fullscreenDotNetHelper) {
                voiceState.fullscreenDotNetHelper.invokeMethodAsync('OnFullscreenSingleTap').catch(() => {});
            }
        },
        onSwipeDownClose: () => {
            if (voiceState.fullscreenDotNetHelper) {
                voiceState.fullscreenDotNetHelper.invokeMethodAsync('OnFullscreenExitRequested').catch(() => {});
            }
        },
        onEscape: () => {
            if (voiceState.fullscreenDotNetHelper) {
                voiceState.fullscreenDotNetHelper.invokeMethodAsync('OnFullscreenEscapeRequested').catch(() => {});
            }
        }
    });

    scheduleNativeMobileVideoSlotsSync();
}

export function setParticipantFullscreenRotation(rotationDeg) {
    if (!voiceState.fullscreenGestureController) return;
    voiceState.fullscreenGestureController.setRotation(rotationDeg, true);
    scheduleNativeMobileVideoSlotsSync();
}

export function resetParticipantFullscreenZoom() {
    if (!voiceState.fullscreenGestureController) return;
    voiceState.fullscreenGestureController.resetZoom(true);
    scheduleNativeMobileVideoSlotsSync();
}

export function exitParticipantFullscreen(goBack = true) {
    const prevId = voiceState.fullscreenParticipantId;
    voiceState.fullscreenParticipantId = '';
    voiceState.fullscreenDotNetHelper = null;
    voiceState.fullscreenTransform = {
        identity: '',
        source: '',
        scale: 1,
        panX: 0,
        panY: 0,
        rotationDeg: 0
    };

    if (voiceState.fullscreenGestureController) {
        voiceState.fullscreenGestureController.destroy(true);
        voiceState.fullscreenGestureController = null;
    }

    if (prevId) {
        resetParticipantVideoTransform(prevId);
    }

    window.SpokesBridge?.backStack?.remove?.('voiceFullscreen', { popHistory: Boolean(goBack) });

    try {
        if (window.Capacitor?.Plugins?.StatusBar) {
            window.Capacitor.Plugins.StatusBar.show().catch(() => {});
        }
    } catch (e) {}

    scheduleNativeMobileVideoSlotsSync();
}


configureScreenShareCallbacks({
    isRoomConnected,
    stopLivekitTrackCompletely,
    clearParticipantVideoDom,
    notifyHostVoiceState: () => notifyHostVoiceState(Boolean(voiceState.currentRoom), isEffectiveLocalMicMuted())
});

configureDirectLanCallbacks({
    reconcileAudioRouting,
    flushLocalCaptureFifoIfAvailable,
    resolveSelectedMicLabel: resolveSelectedMicLabelForDirectAudio,
    resolveSelectedSpeakerLabel: resolveSelectedSpeakerLabelForDirectAudio
});

configureNativeMobileCallbacks({
    reportConnectingStage,
    notifyHostVoiceState,
    releaseNativeCallResources,
    syncIosAudioSessionVideoMode,
    updateAudioDevices,
    refreshParticipantFullscreenPrimaryTarget,
    clampCameraResolutionForCapabilities,
    getCustomPreset
});
