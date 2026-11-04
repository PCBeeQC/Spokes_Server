import { NoiseSuppressionManager } from './noiseSuppressionManager.js?v=20261104c';
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
} from './voicePip.js?v=20261104c';

let currentRoom = null;
let dotnetHelper = null;
let globalScreenTracks = [];
let _isStoppingScreenShare = false;
let noiseManager = new NoiseSuppressionManager();
let currentNoiseSuppression = 'webrtc';

let _iosActiveMicrophoneMode = 'unsupported';
let _mutedRemoteScreenAudio = new Set();
const PROCESSED_MIC_PREFIX = 'processed-mic:';
let _coLocatedProcessedUsers = new Set();
let _mutedRoommateUsers = new Set();
let _activeLocalAudioProcessor = null;
let _localHardwareMuteCleanup = null;
let _suppressNextUnpublishMuteNotify = false;
let _micOperationSeq = 0;
let _nativeCallResourcesActive = false;
let _iosAudioInterruptionListener = null;
let _lastCameraResolution = 'Auto';
let _lastCameraFps = 30;
let _lastCameraSimulcast = null;
let _lastScreenResolution = 'Auto';
let _lastScreenFps = 30;
let _lastScreenMode = 'motion';
let _localCameraDesired = false;
let _localCameraFacing = 'user';
let _iosCompanionLiveKitUrl = '';
let _iosCompanionToken = '';
let _iosActiveCameraId = '';
let _iosCachedCameras = [];
const CAMERA_COMPANION_SUFFIX = ':cam';

function isCameraCompanionIdentity(identity) {
    return typeof identity === 'string' && identity.endsWith(CAMERA_COMPANION_SUFFIX);
}

function isSyntheticIdentity(identity) {
    return isBotIdentity(identity) || isCameraCompanionIdentity(identity);
}

function getEffectiveParticipantIdentity(participantOrIdentity) {
    const raw = typeof participantOrIdentity === 'string'
        ? participantOrIdentity
        : (participantOrIdentity?.identity || '');
    if (!raw) return '';
    if (raw.endsWith(CAMERA_COMPANION_SUFFIX) && raw.length > CAMERA_COMPANION_SUFFIX.length) {
        return raw.slice(0, -CAMERA_COMPANION_SUFFIX.length);
    }
    return raw;
}

function syncLocalStageVideoMirroring() {
    if (typeof document === 'undefined' || !currentRoom?.localParticipant?.identity) return;
    const localId = currentRoom.localParticipant.identity;
    const stageEl = document.getElementById(`participant-video-${localId}`);
    if (stageEl) {
        stageEl.style.transform = (_localCameraFacing === 'environment')
            ? 'translateZ(0)'
            : 'translateZ(0) scaleX(-1)';
    }
}

async function ensureIosCompanionCredentials() {
    if (!(isNativePlatform() && getNativePlatform() === 'ios')) return null;
    const localUserId = currentRoom?.localParticipant?.identity || '';
    if (!_iosCompanionToken && dotnetHelper) {
        try {
            const tok = await dotnetHelper.invokeMethodAsync('RequestCameraCompanionToken');
            if (tok && typeof tok === 'string') {
                _iosCompanionToken = tok;
            }
        } catch (e) {
            console.warn('[Voice] RequestCameraCompanionToken failed:', e);
        }
    }
    if (!_iosCompanionLiveKitUrl || !_iosCompanionToken) {
        return null;
    }
    const creds = {
        url: _iosCompanionLiveKitUrl,
        token: _iosCompanionToken,
        localUserId
    };
    const audioPlugin = getAudioSessionPlugin();
    if (audioPlugin && typeof audioPlugin.configureCompanionSession === 'function') {
        try {
            await audioPlugin.configureCompanionSession(creds);
        } catch (e) {}
    } else if (typeof window !== 'undefined' && typeof window.SpokesNative?.controlNativeCamera === 'function') {
        try {
            await window.SpokesNative.controlNativeCamera('configure', creds);
        } catch (e) {}
    }
    return creds;
}

async function fetchIosNativeCameras() {
    if (!(isNativePlatform() && getNativePlatform() === 'ios')) return [];
    const audioPlugin = getAudioSessionPlugin();
    try {
        if (audioPlugin && typeof audioPlugin.getNativeCameras === 'function') {
            const res = await audioPlugin.getNativeCameras();
            if (Array.isArray(res?.cameras) && res.cameras.length > 0) {
                _iosCachedCameras = res.cameras;
                return _iosCachedCameras;
            }
        }
        if (typeof window !== 'undefined' && typeof window.SpokesNative?.controlNativeCamera === 'function') {
            const res = await window.SpokesNative.controlNativeCamera('enumerate', {});
            if (Array.isArray(res?.cameras) && res.cameras.length > 0) {
                _iosCachedCameras = res.cameras;
                return _iosCachedCameras;
            }
        }
    } catch (e) {
        console.warn('[Voice] fetchIosNativeCameras failed:', e);
    }
    if (_iosCachedCameras.length > 0) return _iosCachedCameras;
    return [
        { deviceId: 'ios_front', label: 'Front Camera', facingMode: 'user', maxWidth: 3840, maxHeight: 2160, maxFps: 60 },
        { deviceId: 'ios_back', label: 'Back Camera (Wide)', facingMode: 'environment', maxWidth: 3840, maxHeight: 2160, maxFps: 60 }
    ];
}

function getIosCameraCapabilities(deviceId, facingMode = null) {
    const targetFacing = facingMode || _localCameraFacing || 'user';
    const match = (deviceId && _iosCachedCameras.find(c => c.deviceId === deviceId))
        || _iosCachedCameras.find(c => c.facingMode === targetFacing)
        || _iosCachedCameras[0];
    const maxDim = Math.max(match?.maxWidth || 1920, match?.maxHeight || 1080);
    const maxFps = match?.maxFps || 30;
    return { maxDim, maxFps, matchedCamera: match || null };
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

function reportIosCameraCapabilitiesToDotnet(deviceId) {
    if (!dotnetHelper) return;
    const { maxDim, maxFps } = getIosCameraCapabilities(deviceId, _localCameraFacing);
    try {
        dotnetHelper.invokeMethodAsync('OnLocalVideoDimensions', maxDim, maxFps);
    } catch (e) {}
}

function getParticipantTrackPublications(participant) {
    if (!participant) return [];
    const seen = new Set();
    const result = [];
    const collect = (mapOrObj) => {
        if (!mapOrObj) return;
        const arr = typeof mapOrObj.values === 'function' ? Array.from(mapOrObj.values()) : Object.values(mapOrObj);
        for (const pub of arr) {
            if (pub && !seen.has(pub)) {
                seen.add(pub);
                result.push(pub);
            }
        }
    };
    collect(participant.trackPublications);
    collect(participant.videoTrackPublications);
    collect(participant.audioTrackPublications);
    collect(participant.tracks);
    return result;
}

function isScreenShareTrackOrPub(track, publication) {
    const Source = window.LivekitClient?.Track?.Source;
    const src = publication?.source || track?.source;
    return Boolean(Source && src === Source.ScreenShare);
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
}

function isBotIdentity(identity) {
    return typeof identity === 'string' && identity.startsWith('bot:');
}

function isLocalInActivePhysicalRoom() {
    const localId = currentRoom?.localParticipant?.identity;
    return Boolean(localId && _coLocatedProcessedUsers.has(localId));
}

function updateLocalProcessorStationaryRoomMode() {
    if (_activeLocalAudioProcessor && typeof _activeLocalAudioProcessor.setStationaryRoomMode === 'function') {
        _activeLocalAudioProcessor.setStationaryRoomMode(isLocalInActivePhysicalRoom());
    }
}

function getRawCaptureMediaStreamTrack(livekitTrack) {
    if (!livekitTrack) return null;
    return livekitTrack.processor?.originalTrack
        || livekitTrack._mediaStreamTrack
        || livekitTrack.mediaStreamTrack
        || null;
}

function notifyHostVoiceState(active, isMuted = false) {
    const isActive = Boolean(active);
    const muted = Boolean(isMuted);
    window._spokesInVoiceCall = isActive;
    if (isActive) {
        updatePipCallState({ active: true, isMuted: muted });
    } else {
        resetPipOnCallEnd();
    }
    const channelName = currentRoom?.name || '';

    try {
        if (window.SpokesBridge && typeof window.SpokesBridge.setVoiceCallState === 'function') {
            window.SpokesBridge.setVoiceCallState({
                active: isActive,
                channelName,
                isMuted: muted
            });
        } else if (window.SpokesNative && typeof window.SpokesNative.setVoiceCallState === 'function') {
            window.SpokesNative.setVoiceCallState(isActive, muted);
        }
    } catch (e) {}

    try {
        window.dispatchEvent(new CustomEvent('spokes:voice-state-changed', {
            detail: {
                active: isActive,
                channelName,
                isMuted: muted,
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
            if (dotnetHelper) {
                await dotnetHelper.invokeMethodAsync('RequestExternalDisconnect');
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
            if (dotnetHelper) {
                await dotnetHelper.invokeMethodAsync('OnNativeCallAction', act);
                return true;
            }
        } catch (e) {
            console.warn('[Voice] OnNativeCallAction interop failed, applying fallback:', e);
        }
        if (act === 'hangup') {
            await disconnectFromVoice();
        } else if (act === 'toggleMute') {
            await setMicrophoneEnabled(_isLocalSoftwareMuted);
        } else if (act === 'toggleCamera') {
            await setCameraEnabled(!_localCameraDesired, _lastCameraResolution, _lastCameraFps);
        }
        return true;
    };

    document.addEventListener('visibilitychange', () => {
        if (document.visibilityState !== 'visible' || !currentRoom) return;
        resumeAllVoiceAudioPlayback();
        refreshVideoLayouts();
    });
}

function getProcessedMicOwner(track, publication, participant) {
    if (!participant?.identity?.startsWith('bot:')) return null;
    const name = publication?.trackName || track?.name || '';
    if (typeof name === 'string' && name.startsWith(PROCESSED_MIC_PREFIX)) {
        return name.substring(PROCESSED_MIC_PREFIX.length);
    }
    return null;
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

    let deviceName = 'Desktop Browser';
    try {
        const ua = navigator.userAgent || '';
        let os = 'Device';
        if (/Windows/i.test(ua)) os = 'Windows PC';
        else if (/Macintosh|Mac OS X/i.test(ua) && !('ontouchend' in document)) os = 'Mac';
        else if (/iPhone|iPad|iPod/i.test(ua) || (/Macintosh/i.test(ua) && 'ontouchend' in document)) os = 'iOS Device';
        else if (/Android/i.test(ua)) os = 'Android Device';
        else if (/Linux/i.test(ua)) os = 'Linux PC';

        let browser = '';
        if (isNativePlatform()) {
            browser = 'Spokes App';
        } else if (/Edg\//i.test(ua)) {
            browser = 'Edge';
        } else if (/Chrome\//i.test(ua)) {
            browser = 'Chrome';
        } else if (/Firefox\//i.test(ua)) {
            browser = 'Firefox';
        } else if (/Safari\//i.test(ua)) {
            browser = 'Safari';
        }
        deviceName = browser ? `${os} (${browser})` : os;
    } catch (e) {}

    return { deviceId, deviceName };
}

export function syncPhysicalRoomState(coLocatedUserIds, mutedRoommateUserIds) {
    _coLocatedProcessedUsers = new Set(Array.isArray(coLocatedUserIds) ? coLocatedUserIds : []);
    _mutedRoommateUsers = new Set(Array.isArray(mutedRoommateUserIds) ? mutedRoommateUserIds : []);
    updateLocalProcessorStationaryRoomMode();

    if (!currentRoom || !currentRoom.remoteParticipants) return;

    const localIdentity = currentRoom.localParticipant?.identity;
    const remotes = typeof currentRoom.remoteParticipants.values === 'function'
        ? Array.from(currentRoom.remoteParticipants.values())
        : Object.values(currentRoom.remoteParticipants);

    remotes.forEach(p => {
        if (!p) return;
        const pubs = p.audioTrackPublications
            ? (typeof p.audioTrackPublications.values === 'function'
                ? Array.from(p.audioTrackPublications.values())
                : Object.values(p.audioTrackPublications))
            : [];

        pubs.forEach(pub => {
            if (!pub) return;
            const processedOwner = getProcessedMicOwner(pub.track, pub, p);
            if (processedOwner) {
                const shouldMuteProcessed = processedOwner === localIdentity || _mutedRoommateUsers.has(processedOwner) || !_coLocatedProcessedUsers.has(processedOwner);
                if (shouldMuteProcessed) {
                    detachParticipantAudio(processedOwner, pub.trackSid || pub.track?.sid);
                    if (pub.track && typeof pub.track.detach === 'function') {
                        try { pub.track.detach().forEach(el => safeRemove(el)); } catch (e) {}
                    }
                    if (processedOwner !== localIdentity) {
                        unregisterRemoteVoiceTrack(processedOwner, pub.track?.mediaStreamTrack || null);
                    }
                    if (typeof pub.setSubscribed === 'function') {
                        try { pub.setSubscribed(false); } catch (e) {}
                    }
                } else {
                    if (typeof pub.setSubscribed === 'function' && !pub.isSubscribed) {
                        try { pub.setSubscribed(true); } catch (e) {}
                    } else if (pub.track) {
                        attachTrack(pub.track, p, pub);
                    }
                }
                return;
            }

            if (!isBotIdentity(p.identity) && pub.source !== window.LivekitClient?.Track?.Source?.ScreenShareAudio) {
                const shouldSuppressRawMic = _coLocatedProcessedUsers.has(p.identity) || _mutedRoommateUsers.has(p.identity);
                if (shouldSuppressRawMic) {
                    detachParticipantAudio(p.identity, pub.trackSid || pub.track?.sid);
                    if (pub.track && typeof pub.track.detach === 'function') {
                        try { pub.track.detach().forEach(el => safeRemove(el)); } catch (e) {}
                    }
                    if (_mutedRoommateUsers.has(p.identity)) {
                        unregisterRemoteVoiceTrack(p.identity);
                    } else if (pub.track?.mediaStreamTrack) {
                        unregisterRemoteVoiceTrack(p.identity, pub.track.mediaStreamTrack);
                    }
                    if (typeof pub.setSubscribed === 'function') {
                        try { pub.setSubscribed(false); } catch (e) {}
                    }
                } else {
                    if (typeof pub.setSubscribed === 'function' && !pub.isSubscribed) {
                        try { pub.setSubscribed(true); } catch (e) {}
                    } else if (pub.track) {
                        attachTrack(pub.track, p, pub);
                    }
                }
            }
        });
    });
}
// --- Native Platform Helpers ---
const isNativePlatform = () => typeof window.isCapacitorNative === 'function' ? window.isCapacitorNative() : Boolean(window.Capacitor?.isNativePlatform?.());
const getNativePlatform = () => window.Capacitor?.getPlatform?.() || '';

// --- Initialize local iOS native plugin lazily ---
let _spokesAudioSessionPlugin = null;
function getAudioSessionPlugin() {
    if (!_spokesAudioSessionPlugin && window.Capacitor?.registerPlugin) {
        _spokesAudioSessionPlugin = window.Capacitor.registerPlugin('SpokesAudioSessionPlugin');
    }
    return _spokesAudioSessionPlugin || window.Capacitor?.Plugins?.SpokesAudioSessionPlugin;
}

export function setNoiseSuppressionType(type) {
    currentNoiseSuppression = type;
}

export function setVoiceSensitivity(val) {
    if (typeof val === 'number') {
        _voiceSensitivity = Math.max(0, Math.min(100, val));
        _savedVoiceSettings.voiceSensitivity = _voiceSensitivity;
    }
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

// Toggle to true to capture client-side browser logs and output them to the C# server console
const ENABLE_TELEMETRY = false;

export function isAppleMobileDevice() {
    if (isNativePlatform() && getNativePlatform() === 'ios') return true;
    return Boolean(/iPhone|iPad|iPod/i.test(navigator.userAgent) || (navigator.userAgent.includes("Mac") && "ontouchend" in document));
}

let _androidHasBluetooth = false;
let _androidInitialRouteSet = false;
let _globalAudioContext = null;
let _activeGainNodes = new Map();

function getVoiceAudioContext() {
    if (!_globalAudioContext) {
        const AudioCtx = window.AudioContext || window.webkitAudioContext;
        if (AudioCtx) {
            _globalAudioContext = new AudioCtx();
            try {
                const savedSink = localStorage.getItem('spokes_active_speaker_id');
                if (savedSink && typeof _globalAudioContext.setSinkId === 'function') {
                    _globalAudioContext.setSinkId(savedSink === 'default' ? '' : savedSink).catch(() => {});
                }
            } catch(e) {}
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
            _captureAudioContext = new AudioCtx({ sampleRate: 48000 });
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

// --- Web Audio Volume Pipeline (0% to 200% Master & Per-Participant) ---
let _masterVolume = 1.0; // 0.0 to 2.0
let _masterGainNode = null;
const _participantGainNodes = new Map(); // trackKey -> { identity, trackSid, source, gainNode, element }
let _savedVoiceSettings = {
    masterVolume: 100,
    participantVolumes: {},
    voiceSensitivity: 50
};

function getMasterGainNode() {
    const ctx = getVoiceAudioContext();
    if (!ctx) return null;
    if (!_masterGainNode) {
        _masterGainNode = ctx.createGain();
        _masterGainNode.gain.value = _masterVolume;
        _masterGainNode.connect(ctx.destination);
    }
    return _masterGainNode;
}

function attachParticipantAudio(track, participant, element, overrideIdentity = null) {
    if (!participant || !participant.identity || !track.mediaStreamTrack) return;
    
    // Never route local audio back through speakers
    if (participant === currentRoom?.localParticipant) return;

    const identity = overrideIdentity || participant.identity;
    if (identity === currentRoom?.localParticipant?.identity) return;
    const trackSid = track.sid || track.mediaStreamTrack.id || (track.source === window.LivekitClient.Track.Source.ScreenShareAudio ? 'screenshare_audio' : 'mic');
    const trackKey = `${identity}_${trackSid}`;
    detachParticipantAudio(identity, trackSid, element);

    // Apply active speaker sinkId to the HTML element if supported
    try {
        const savedSink = localStorage.getItem('spokes_active_speaker_id');
        const sinkVal = (!savedSink || savedSink === 'default') ? '' : savedSink;
        if (element && typeof element.setSinkId === 'function') {
            element.setSinkId(sinkVal).catch(() => {});
        }
    } catch(e) {}

    const isScreenAudio = track.source === window.LivekitClient.Track.Source.ScreenShareAudio;
    const isScreenMuted = isScreenAudio && _mutedRemoteScreenAudio.has(identity);
    const isRoommateMuted = !isScreenAudio && _mutedRoommateUsers.has(identity);

    // NATIVE MOBILE (ANDROID & IOS):
    // Do NOT route incoming WebRTC call audio through Web Audio API!
    if (isNativePlatform()) {
        const isMuted = isScreenMuted || isRoommateMuted || (!isScreenAudio && _savedVoiceSettings?.participantVolumes && _savedVoiceSettings.participantVolumes[identity] === 0);
        element.muted = Boolean(isMuted);
        element.volume = isMuted ? 0 : 1.0;
        _participantGainNodes.set(trackKey, {
            identity,
            trackSid,
            isScreenAudio,
            element
        });
        return;
    }

    const ctx = getVoiceAudioContext();
    const masterGain = getMasterGainNode();
    if (!ctx || !masterGain || ctx.state === 'suspended') {
        // Fallback: If Web Audio is not available or suspended, play the raw HTML element directly
        const isMuted = isScreenMuted || isRoommateMuted || (!isScreenAudio && _savedVoiceSettings?.participantVolumes && _savedVoiceSettings.participantVolumes[identity] === 0);
        element.muted = Boolean(isMuted);
        element.volume = isMuted ? 0 : Math.min(1.0, _masterVolume);
        _participantGainNodes.set(trackKey, {
            identity,
            trackSid,
            isScreenAudio,
            element
        });
        return;
    }

    try {
        const stream = new MediaStream([track.mediaStreamTrack]);
        const source = ctx.createMediaStreamSource(stream);
        const gainNode = ctx.createGain();

        let userVolume = 1.0;
        if (isScreenMuted || isRoommateMuted) {
            userVolume = 0;
        } else if (!isScreenAudio && _savedVoiceSettings?.participantVolumes && typeof _savedVoiceSettings.participantVolumes[identity] === 'number') {
            userVolume = Math.max(0, Math.min(200, _savedVoiceSettings.participantVolumes[identity])) / 100.0;
        }

        gainNode.gain.value = userVolume;

        source.connect(gainNode);
        gainNode.connect(masterGain);

        // Mute raw HTML audio element to prevent double playback on desktop where Web Audio is active
        element.volume = 0;
        element.muted = true;

        _participantGainNodes.set(trackKey, {
            identity,
            trackSid,
            isScreenAudio,
            source,
            gainNode,
            element
        });
    } catch (e) {
        console.warn("[Voice] Web Audio routing fallback for", identity, trackSid, e);
        const isMuted = isScreenMuted || isRoommateMuted || (!isScreenAudio && _savedVoiceSettings?.participantVolumes && _savedVoiceSettings.participantVolumes[identity] === 0);
        element.muted = Boolean(isMuted);
        element.volume = isMuted ? 0 : Math.min(1.0, _masterVolume);
        _participantGainNodes.set(trackKey, {
            identity,
            trackSid,
            isScreenAudio,
            element
        });
    }
}

function teardownAudioItem(item, preserveElement = null) {
    if (!item) return;
    try {
        if (item.source) item.source.disconnect();
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
    _masterVolume = Math.max(0, Math.min(200, volumePercent)) / 100.0;
    if (_masterGainNode) {
        _masterGainNode.gain.value = _masterVolume;
    }
}

export function setParticipantVolume(participantId, volumePercent) {
    if (!participantId) return;
    if (!_savedVoiceSettings.participantVolumes) {
        _savedVoiceSettings.participantVolumes = {};
    }
    _savedVoiceSettings.participantVolumes[participantId] = volumePercent;

    const isMuted = volumePercent === 0;
    const vol = Math.max(0, Math.min(200, volumePercent)) / 100.0;

    _participantGainNodes.forEach(item => {
        if (item.identity === participantId && !item.isScreenAudio) {
            if (item.element) {
                item.element.muted = isMuted;
                if (isNativePlatform()) {
                    item.element.volume = isMuted ? 0 : 1.0;
                }
            }
            if (item.gainNode) {
                item.gainNode.gain.value = vol;
            }
        }
    });
}

export function setRemoteScreenAudioMuted(participantIdentity, isMuted) {
    if (!participantIdentity) return;
    if (isMuted) {
        _mutedRemoteScreenAudio.add(participantIdentity);
    } else {
        _mutedRemoteScreenAudio.delete(participantIdentity);
    }

    _participantGainNodes.forEach(item => {
        if (item.identity === participantIdentity && item.isScreenAudio) {
            if (item.element) {
                item.element.muted = isMuted;
                if (isNativePlatform()) {
                    item.element.volume = isMuted ? 0 : 1.0;
                }
            }
            if (item.gainNode) {
                item.gainNode.gain.value = isMuted ? 0 : 1.0;
            }
        }
    });
}

export function initVoiceSettings(settingsJson) {
    try {
        const settings = typeof settingsJson === 'string' ? JSON.parse(settingsJson) : settingsJson;
        if (settings) {
            _savedVoiceSettings = {
                masterVolume: typeof settings.masterVolume === 'number' ? settings.masterVolume : 100,
                participantVolumes: settings.participantVolumes || {},
                voiceSensitivity: typeof settings.voiceSensitivity === 'number' ? settings.voiceSensitivity : 50
            };
            if (typeof settings.masterVolume === 'number') {
                setMasterVolume(settings.masterVolume);
            }
            if (typeof settings.voiceSensitivity === 'number') {
                setVoiceSensitivity(settings.voiceSensitivity);
            }
            if (settings.participantVolumes) {
                Object.keys(settings.participantVolumes).forEach(id => {
                    const vol = settings.participantVolumes[id];
                    if (typeof vol === 'number') {
                        setParticipantVolume(id, vol);
                    }
                });
            }
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

function setLocalAudioMuted(muted) {
    if (!currentRoom || !currentRoom.localParticipant) return;
    _isLocalSoftwareMuted = Boolean(muted);
    const localId = currentRoom.localParticipant.identity;
    if (isEffectiveLocalMicMuted() && localId) {
        updateParticipantSpeakingDOM(localId, false);
        if (_localMicVadEntry) {
            _localMicVadEntry.isSpeaking = false;
            _localMicVadEntry.consecutiveActiveFrames = 0;
        }
        notifyActiveSpeakersToDotnet(null, true);
    }
    notifyHostVoiceState(Boolean(currentRoom), isEffectiveLocalMicMuted());
    const tracks = currentRoom.localParticipant.audioTrackPublications;
    if (tracks) {
        const pubArr = typeof tracks.values === 'function' ? Array.from(tracks.values()) : Object.values(tracks);
        pubArr.forEach(p => {
            if (p.track && p.track.mediaStreamTrack) {
                p.track.mediaStreamTrack.enabled = !muted;
            }
            if (muted) {
                if (typeof p.mute === 'function') p.mute().catch(() => {});
            } else {
                if (typeof p.unmute === 'function') p.unmute().catch(() => {});
            }
        });
    }
}

export async function connectToVoice(token, url, helper, audioBitrateKbps = 64, noiseSuppression = 'webrtc') {
    dotnetHelper = helper;
    currentNoiseSuppression = noiseSuppression;
    
    // Wire up detailed console intercept to funnel logs to the C# backend
    if (ENABLE_TELEMETRY && !window._voiceLoggerPatched) {
        window._voiceLoggerPatched = true;
        ['debug', 'log', 'info', 'warn', 'error'].forEach(level => {
            const original = console[level];
            if (!original) return;
            console[level] = function() {
                original.apply(console, arguments);
                if (dotnetHelper) {
                    try {
                        let msg = Array.from(arguments).map(a => {
                            if (a instanceof Error) return `${a.name}: ${a.message}\n${a.stack}`;
                            if (typeof a === 'object') {
                                try { return JSON.stringify(a); } catch(e) { return String(a); }
                            }
                            return String(a);
                        }).join(' ');
                        
                        // Prevent giant JSON objects from blowing out the signalR connection
                        if (msg.length > 3000) msg = msg.substring(0, 3000) + '...[truncated]';
                        
                        dotnetHelper.invokeMethodAsync('LogToServer', level.toUpperCase(), msg).catch(()=>{});
                    } catch(e) {}
                }
            };
        });
    }
    
    // Ensure LivekitClient is loaded on-demand if idle preload hasn't finished yet
    if (!window.LivekitClient && typeof window.ensureVoiceScriptsLoaded === 'function') {
        await window.ensureVoiceScriptsLoaded();
    }
    if (!window.LivekitClient) {
        console.error('[Voice] LivekitClient not found! Ensure the CDN script is loaded.');
        return false;
    }

    if (ENABLE_TELEMETRY) {
        // Enable verbose debug logging to trace the exact network failure
        window.LivekitClient.setLogLevel(window.LivekitClient.LogLevel.debug);
    }



    try {
        hydrateSavedVoiceSettingsFromStorage();
        await requestHostCrossServerVoiceHandoff();

        getVoiceAudioContext();
        if (currentRoom) {
            const staleRoom = currentRoom;
            currentRoom = null;
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
        _isLocalSoftwareMuted = false;
        _isLocalHardwareMuted = false;

        // Universal H.264: Leverages on-die hardware acceleration across iOS (VideoToolbox), Android (MediaCodec), and Desktop/Web (NVENC/QuickSync/AMF/VideoToolbox)
        let preferredCodec = 'h264';
        const preferredMicDeviceId = await resolvePreferredMicDeviceId();

        currentRoom = new window.LivekitClient.Room({
            adaptiveStream: false,
            dynacast: true,
            audioCaptureDefaults: preferredMicDeviceId ? { deviceId: preferredMicDeviceId } : undefined,
            publishDefaults: {
                audioBitrate: audioBitrateKbps * 1000,
                audioPreset: { maxBitrate: audioBitrateKbps * 1000 },
                dtx: true,
                videoCodec: preferredCodec,
                backupCodec: true,
                screenShareEncoding: {
                    maxBitrate: 12000000,
                    maxFramerate: 60
                }
            }
        });

        // Setup event listeners for the specific events you'll need
        currentRoom
            .on(window.LivekitClient.RoomEvent.TrackSubscribed, handleTrackSubscribed)
            .on(window.LivekitClient.RoomEvent.TrackUnsubscribed, handleTrackUnsubscribed)
            .on(window.LivekitClient.RoomEvent.ActiveSpeakersChanged, handleActiveSpeakers)
            .on(window.LivekitClient.RoomEvent.Disconnected, handleDisconnect)
            .on(window.LivekitClient.RoomEvent.LocalTrackPublished, handleLocalTrackPublished)
            .on(window.LivekitClient.RoomEvent.LocalTrackUnpublished, handleLocalUnpublished)
            .on(window.LivekitClient.RoomEvent.ParticipantConnected, handleParticipantConnected)
            .on(window.LivekitClient.RoomEvent.ParticipantDisconnected, handleParticipantDisconnected)
            .on(window.LivekitClient.RoomEvent.TrackMuted, handleTrackMuted)
            .on(window.LivekitClient.RoomEvent.TrackUnmuted, handleTrackUnmuted);

        _isLocalSoftwareMuted = false;
        _isLocalHardwareMuted = false;
        _micOperationSeq = 0;

        await currentRoom.connect(url, token);
        notifyHostVoiceState(true, isEffectiveLocalMicMuted());
        console.log('[Voice] Connected to room:', currentRoom.name);
        _nativeCallResourcesActive = true;
        _localCameraDesired = false;
        _iosCompanionLiveKitUrl = url || '';
        _iosCompanionToken = '';

        configureVoicePip(dotnetHelper, {
            onToggleMute: () => window.spokesNativeCallAction('toggleMute'),
            onToggleCamera: () => window.spokesNativeCallAction('toggleCamera'),
            onHangUp: () => window.spokesLeaveVoiceCall(),
            onReturnToCall: () => window.spokesNativeCallAction('returnToCall'),
            onRestoreStageTracks: () => reattachAllTracks()
        });
        updatePipCallState({
            active: true,
            channelName: currentRoom.name || '',
            localUserId: currentRoom.localParticipant?.identity || '',
            isMuted: isEffectiveLocalMicMuted(),
            isVideoOn: false,
            isScreenSharing: false
        });

        // --- Capacitor Native Mobile Fixes ---
        if (isNativePlatform()) {
            try {
                // Prevent screen lock (fixes background audio dropping)
                if (window.Capacitor?.Plugins?.KeepAwake) {
                    await window.Capacitor.Plugins.KeepAwake.keepAwake();
                }
                
                let platform = getNativePlatform();
                let audioPlugin = getAudioSessionPlugin();
                if (platform === 'ios' && audioPlugin) {
                    let res = await audioPlugin.configureVoiceChat({ enabled: true });
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
                    console.info(`[Voice] iOS Native Session Initialized: Category=${res?.category}, Mode=${res?.mode}, Output=${res?.activeOutput}, MicMode=${_iosActiveMicrophoneMode}`);
                    ensureIosCompanionCredentials().catch(() => {});
                    fetchIosNativeCameras().then(() => reportIosCameraCapabilitiesToDotnet(_iosActiveCameraId)).catch(() => {});
                } else if (platform === 'ios') {
                    console.warn(`[Voice] iOS Native Session plugin NOT FOUND`);
                    ensureIosCompanionCredentials().catch(() => {});
                    fetchIosNativeCameras().then(() => reportIosCameraCapabilitiesToDotnet(_iosActiveCameraId)).catch(() => {});
                }
                
                if (platform === 'android' && window.Capacitor.Plugins.TelecomConnectionPlugin) {
                    _androidInitialRouteSet = false;
                    _activeNativeSpeakerId = 'speaker';
                    window.Capacitor.Plugins.TelecomConnectionPlugin.addListener('onAudioStateChanged', (data) => {
                        if (data && data.state) {
                            if (typeof data.state.hasBluetooth === 'boolean') _androidHasBluetooth = data.state.hasBluetooth;

                            if (!_androidInitialRouteSet) {
                                // First callback signals the telecom session is ready.
                                // Apply the desired default route (speaker) now.
                                _androidInitialRouteSet = true;
                                let desiredRoute = _androidHasBluetooth ? 'bluetooth' : 'speaker';
                                window.Capacitor.Plugins.TelecomConnectionPlugin.setAudioRoute({ route: desiredRoute })
                                    .then(() => {
                                        _activeNativeSpeakerId = desiredRoute;
                                        updateAudioDevices().catch(()=>{});
                                    })
                                    .catch(() => {
                                        // Route set failed, accept whatever Android reports
                                        if (data.state.route) _activeNativeSpeakerId = data.state.route;
                                        updateAudioDevices().catch(()=>{});
                                    });
                                return;
                            }

                            // Subsequent callbacks: accept the reported route (user or system initiated)
                            if (data.state.route) _activeNativeSpeakerId = data.state.route;
                            updateAudioDevices().catch(()=>{});
                        }
                    });
                    window.Capacitor.Plugins.TelecomConnectionPlugin.addListener('onCallEnded', () => {
                        console.info('[Voice] Android TelecomConnectionPlugin reported onCallEnded');
                        if (currentRoom && typeof window.spokesLeaveVoiceCall === 'function') {
                            window.spokesLeaveVoiceCall().catch(() => {});
                        }
                    });
                    await window.Capacitor.Plugins.TelecomConnectionPlugin.startCall();
                }
            } catch (e) {
                console.warn('[Voice] Capacitor native plugins error on connect:', e);
            }
        }

        if (dotnetHelper) {
            let existingParticipants = [];
            if (currentRoom.remoteParticipants) {
                if (typeof currentRoom.remoteParticipants.values === 'function') {
                    existingParticipants = Array.from(currentRoom.remoteParticipants.values()).map(p => getEffectiveParticipantIdentity(p)).filter(id => Boolean(id) && !isSyntheticIdentity(id));
                } else {
                    existingParticipants = Object.values(currentRoom.remoteParticipants).map(p => getEffectiveParticipantIdentity(p)).filter(id => Boolean(id) && !isSyntheticIdentity(id));
                }
            }
            try {
                dotnetHelper.invokeMethodAsync('OnRoomConnected', existingParticipants);
            } catch(e) {}
        }

        // Turn on microphone by default when joining a voice channel (unless muted during connect)
        // Executed asynchronously to prevent the permission prompt from blocking the UI render
        if (!_isLocalSoftwareMuted) {
            setMicrophoneEnabled(true).then(() => {
                updateAudioDevices().catch(e => console.warn('[Voice] Failed to update audio devices:', e));
            }).catch(e => console.warn('[Voice] Failed to enable microphone:', e));
        }

        return true;
    } catch (error) {
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

function stopAllScreenTracks(room = null) {
    if (globalScreenTracks && globalScreenTracks.length > 0) {
        for (let t of globalScreenTracks) {
            stopLivekitTrackCompletely(t);
        }
        globalScreenTracks = [];
    }
    const targetRoom = room || currentRoom;
    if (targetRoom && targetRoom.localParticipant) {
        try {
            const pubs = getParticipantTrackPublications(targetRoom.localParticipant);
            for (const p of pubs) {
                if (p.source === window.LivekitClient?.Track?.Source?.ScreenShare || p.source === window.LivekitClient?.Track?.Source?.ScreenShareAudio) {
                    const t = p.videoTrack || p.audioTrack || p.track;
                    if (t) stopLivekitTrackCompletely(t);
                }
            }
        } catch (e) {}
    }
}

function stopAllLocalMediaTracks(room = null) {
    stopAllScreenTracks(room);
    const targetRoom = room || currentRoom;
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
    if (typeof _localHardwareMuteCleanup === 'function') {
        try { _localHardwareMuteCleanup(); } catch (e) {}
    }
    _localHardwareMuteCleanup = null;
}

function bindLocalHardwareMuteMonitor(livekitTrack) {
    cleanupLocalHardwareMuteMonitor();
    if (!livekitTrack) return;

    // Mobile OSes (iOS/Android) do not have physical USB/headset boom-mic mute switches exposed via
    // MediaStreamTrack.muted; WebKit/Blink instead toggle .muted during AVAudioSession/Telecom route
    // transitions. Never treat mobile track.muted as a hardware switch mute.
    if (isNativePlatform() || isAppleMobileDevice()) {
        if (_isLocalHardwareMuted) {
            _isLocalHardwareMuted = false;
            if (dotnetHelper) {
                dotnetHelper.invokeMethodAsync('OnLocalHardwareMuteChanged', false).catch(() => {});
            }
        }
        return;
    }

    const rawTrack = getRawCaptureMediaStreamTrack(livekitTrack);
    if (!rawTrack) return;

    const reportHardwareMute = (hwMuted) => {
        const next = Boolean(hwMuted);
        if (_isLocalHardwareMuted === next) return;
        _isLocalHardwareMuted = next;

        const localId = currentRoom?.localParticipant?.identity;
        if (isEffectiveLocalMicMuted() && localId) {
            if (_localMicVadEntry) {
                _localMicVadEntry.isSpeaking = false;
                _localMicVadEntry.consecutiveActiveFrames = 0;
            }
            updateParticipantSpeakingDOM(localId, false);
            notifyActiveSpeakersToDotnet(null, true);
        }
        notifyHostVoiceState(Boolean(currentRoom), isEffectiveLocalMicMuted());

        if (dotnetHelper) {
            dotnetHelper.invokeMethodAsync('OnLocalHardwareMuteChanged', next).catch(() => {});
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

    _localHardwareMuteCleanup = () => {
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

    if (typeof rawTrack.muted === 'boolean' && rawTrack.muted !== _isLocalHardwareMuted) {
        reportHardwareMute(rawTrack.muted);
    }
}

async function resolvePreferredMicDeviceId() {
    if (isNativePlatform()) return undefined;

    let devices = [];
    if (window.LivekitClient?.Room?.getLocalDevices) {
        try {
            devices = await window.LivekitClient.Room.getLocalDevices('audioinput');
        } catch (e) {}
    }
    if ((!devices || devices.length === 0) && navigator?.mediaDevices?.enumerateDevices) {
        try {
            const all = await navigator.mediaDevices.enumerateDevices();
            devices = all.filter(d => d.kind === 'audioinput');
        } catch (e) {}
    }

    let saved = null;
    try {
        saved = localStorage.getItem('spokes_active_mic_id');
    } catch (e) {}

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

async function createConfiguredLocalAudioTrack(baseOptions) {
    const preferredDeviceId = await resolvePreferredMicDeviceId();
    const opts = { ...baseOptions };
    if (preferredDeviceId) {
        opts.deviceId = preferredDeviceId;
    }

    try {
        return await window.LivekitClient.createLocalAudioTrack(opts);
    } catch (err) {
        if (opts.deviceId) {
            console.warn(`[Voice] createLocalAudioTrack with deviceId=${opts.deviceId} failed (${err?.name || err?.message}), retrying with system default`);
            delete opts.deviceId;
            return await window.LivekitClient.createLocalAudioTrack(opts);
        }
        throw err;
    }
}

async function unpublishAndStopAllLocalMicTracks() {
    if (!currentRoom || !currentRoom.localParticipant) return;
    const tracks = currentRoom.localParticipant.audioTrackPublications;
    if (!tracks) return;
    const pubArr = typeof tracks.values === 'function' ? Array.from(tracks.values()) : Object.values(tracks);
    for (const p of pubArr) {
        if (p && p.track && p.source !== window.LivekitClient?.Track?.Source?.ScreenShareAudio) {
            const trackToStop = p.audioTrack || p.track;
            try {
                await currentRoom.localParticipant.unpublishTrack(trackToStop, true);
            } catch (e) {
            } finally {
                stopLivekitTrackCompletely(trackToStop);
            }
        }
    }
}

async function publishConfiguredLocalMicTrack(track, opSeq = _micOperationSeq) {
    if (!track) return false;
    if (opSeq !== _micOperationSeq || _isLocalSoftwareMuted || !currentRoom || !currentRoom.localParticipant) {
        stopLivekitTrackCompletely(track);
        return false;
    }
    bindLocalHardwareMuteMonitor(track);
    await currentRoom.localParticipant.publishTrack(track, {
        dtx: true,
        source: window.LivekitClient?.Track?.Source?.Microphone
    });
    if (opSeq !== _micOperationSeq || _isLocalSoftwareMuted || !currentRoom || !currentRoom.localParticipant) {
        cleanupLocalHardwareMuteMonitor();
        _activeLocalAudioProcessor = null;
        try {
            if (currentRoom?.localParticipant) {
                await currentRoom.localParticipant.unpublishTrack(track, true);
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
            const remotes = typeof remoteMap.values === 'function'
                ? Array.from(remoteMap.values())
                : Object.values(remoteMap);
            remotes.forEach(p => {
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
    const wasActive = _nativeCallResourcesActive || _localCameraDesired || Boolean(_iosCompanionToken);
    _nativeCallResourcesActive = false;
    _localCameraDesired = false;
    _iosCompanionToken = '';
    _iosActiveCameraId = '';
    if (!wasActive) return;

    if (isNativePlatform()) {
        _activeNativeSpeakerId = 'default';
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
                if (typeof audioPlugin.stopNativeCamera === 'function') {
                    try { await audioPlugin.stopNativeCamera(); } catch (e) {}
                } else if (typeof window !== 'undefined' && typeof window.SpokesNative?.controlNativeCamera === 'function') {
                    try { window.SpokesNative.controlNativeCamera('stop', {}); } catch (e) {}
                }
                let res = await audioPlugin.configureVoiceChat({ enabled: false });
                console.info(`[Voice] iOS Native Session Released: Category=${res?.category}, Mode=${res?.mode}, Output=${res?.activeOutput}`);
            }

            if (platform === 'android' && window.Capacitor?.Plugins?.TelecomConnectionPlugin) {
                _androidInitialRouteSet = false;
                window.Capacitor.Plugins.TelecomConnectionPlugin.removeAllListeners();
                await window.Capacitor.Plugins.TelecomConnectionPlugin.endCall();
            }
        } catch (e) {
            console.warn('[Voice] Capacitor native release error:', e);
        }
    }
}

export async function disconnectFromVoice() {
    _micOperationSeq++;
    cleanupLocalHardwareMuteMonitor();
    _activeLocalAudioProcessor = null;
    _isLocalSoftwareMuted = false;
    _isLocalHardwareMuted = false;
    _localCameraDesired = false;
    _mutedRemoteScreenAudio.clear();
    _coLocatedProcessedUsers.clear();
    _mutedRoommateUsers.clear();
    const roomToClose = currentRoom;
    currentRoom = null;
    stopAllLocalMediaTracks(roomToClose);
    clearAllVoiceTracks();
    clearAllParticipantAudio();
    notifyHostVoiceState(false, false);
    if (roomToClose) {
        detachAllRoomMedia(roomToClose);
        try {
            await roomToClose.disconnect(true);
        } catch (e) {
            console.warn('[Voice] Error during room.disconnect():', e);
        }
    }
    await releaseNativeCallResources();
}

export async function reinitializeMicrophone() {
    if (!currentRoom || !currentRoom.localParticipant) return;
    if (_isLocalSoftwareMuted) return;

    try {
        cleanupLocalHardwareMuteMonitor();
        _activeLocalAudioProcessor = null;
        const tracks = currentRoom.localParticipant.audioTrackPublications;
        if (tracks) {
            const pubArr = typeof tracks.values === 'function' ? Array.from(tracks.values()) : Object.values(tracks);
            for (const p of pubArr) {
                if (p.track && p.source !== window.LivekitClient.Track.Source.ScreenShareAudio) {
                    const trackToStop = p.audioTrack || p.track;
                    _suppressNextUnpublishMuteNotify = true;
                    try {
                        await currentRoom.localParticipant.unpublishTrack(trackToStop, true);
                    } finally {
                        _suppressNextUnpublishMuteNotify = false;
                        stopLivekitTrackCompletely(trackToStop);
                    }
                }
            }
        }
        await setMicrophoneEnabled(true);
    } catch (e) {
        console.error('[Voice] reinitializeMicrophone failed:', e);
    }
}

export async function setMicrophoneEnabled(enabled) {
    if (!currentRoom || !currentRoom.localParticipant) return;

    const opSeq = ++_micOperationSeq;
    _isLocalSoftwareMuted = !enabled;
    const localId = currentRoom.localParticipant.identity;
    notifyHostVoiceState(true, isEffectiveLocalMicMuted());

    const isMicEnableAborted = () =>
        opSeq !== _micOperationSeq || _isLocalSoftwareMuted || !currentRoom || !currentRoom.localParticipant;

    try {
        if (!enabled) {
            cleanupLocalHardwareMuteMonitor();
            _activeLocalAudioProcessor = null;
            if (localId) {
                updateParticipantSpeakingDOM(localId, false);
                unregisterLocalMicVad();
            }
            notifyActiveSpeakersToDotnet(null, true);

            // Unpublish existing tracks and explicitly stop underlying MediaStreamTracks
            await unpublishAndStopAllLocalMicTracks();
            return;
        }

        _activeLocalAudioProcessor = null;
        let isIOS = isAppleMobileDevice();

        if (currentNoiseSuppression === 'webrtc') {
            if (isIOS) {
                let trackOptions = {
                    echoCancellation: true, 
                    noiseSuppression: true,
                    autoGainControl: false
                };
                console.info(`[Voice] Publishing iOS Basic Track: echo=${trackOptions.echoCancellation}, ns=${trackOptions.noiseSuppression}, agc=${trackOptions.autoGainControl}`);
                let track = await createConfiguredLocalAudioTrack(trackOptions);
                if (isMicEnableAborted()) {
                    stopLivekitTrackCompletely(track);
                    return;
                }
                await publishConfiguredLocalMicTrack(track, opSeq);
            } else {
                console.info(`[Voice] Publishing Standard WebRTC Track`);
                let track = await createConfiguredLocalAudioTrack({ echoCancellation: true, noiseSuppression: true, autoGainControl: true });
                if (isMicEnableAborted()) {
                    stopLivekitTrackCompletely(track);
                    return;
                }
                await publishConfiguredLocalMicTrack(track, opSeq);
            }
            return;
        }

        if (currentNoiseSuppression === 'none') {
            let trackOptions = {
                echoCancellation: false, 
                noiseSuppression: false,
                autoGainControl: false
            };
            console.info(`[Voice] Publishing Raw Track (None): isIOS=${isIOS}`);
            let track = await createConfiguredLocalAudioTrack(trackOptions);
            if (isMicEnableAborted()) {
                stopLivekitTrackCompletely(track);
                return;
            }
            await publishConfiguredLocalMicTrack(track, opSeq);
            return;
        }

        // --- AI Filtering Logic ---
        if (isIOS) {
            let trackOptions = {
                echoCancellation: true, 
                noiseSuppression: true,
                autoGainControl: false
            };
            console.info(`[Voice] iOS: using optimized hardware & spectral noise filter. echo=${trackOptions.echoCancellation}, ns=${trackOptions.noiseSuppression}`);
            let track = await createConfiguredLocalAudioTrack(trackOptions);
            if (isMicEnableAborted()) {
                stopLivekitTrackCompletely(track);
                return;
            }
            await publishConfiguredLocalMicTrack(track, opSeq);
            return;
        }

        let processor = null;
        try {
            processor = await noiseManager.getProcessor(currentNoiseSuppression, {
                stationaryRoomMode: isLocalInActivePhysicalRoom()
            });
            if (isMicEnableAborted()) {
                try { await processor?.destroy?.(); } catch (_) {}
                return;
            }
        } catch (err) {
            console.error('[Voice] Failed to load AI processor:', err);
            if (isMicEnableAborted()) {
                return;
            }
            if (dotnetHelper) {
                dotnetHelper.invokeMethodAsync('OnVoiceLog', `AI Filter Failed: ${err.message}. Falling back to WebRTC.`, "error").catch(()=>{});
            }
            let track = await createConfiguredLocalAudioTrack({ echoCancellation: true, noiseSuppression: true, autoGainControl: true });
            if (isMicEnableAborted()) {
                stopLivekitTrackCompletely(track);
                return;
            }
            await publishConfiguredLocalMicTrack(track, opSeq);
            return;
        }
        
        let trackOptions = {
            echoCancellation: currentNoiseSuppression !== 'none',
            // Disable native WebRTC noise suppression and AGC when using a neural processor;
            // pre-calibration and post-denoiser -20 dBFS loudness normalization run inside the processor graph.
            noiseSuppression: currentNoiseSuppression === 'webrtc',
            autoGainControl: currentNoiseSuppression === 'webrtc'
        };
        console.info(`[Voice] Publishing AI Track (${currentNoiseSuppression}): echo=${trackOptions.echoCancellation}, ns=${trackOptions.noiseSuppression}, agc=${trackOptions.autoGainControl}`);
        
        let track = await createConfiguredLocalAudioTrack(trackOptions);
        if (isMicEnableAborted()) {
            try { await processor?.destroy?.(); } catch (_) {}
            stopLivekitTrackCompletely(track);
            return;
        }
        
        if (processor) {
            try {
                if (typeof track.setAudioContext === 'function') {
                    const captureCtx = await getCaptureAudioContext();
                    if (isMicEnableAborted()) {
                        try { await processor?.destroy?.(); } catch (_) {}
                        stopLivekitTrackCompletely(track);
                        return;
                    }
                    if (captureCtx) {
                        track.setAudioContext(captureCtx);
                    }
                }
                await track.setProcessor(processor);
                if (isMicEnableAborted()) {
                    try { await processor?.destroy?.(); } catch (_) {}
                    stopLivekitTrackCompletely(track);
                    return;
                }
                _activeLocalAudioProcessor = processor;
                updateLocalProcessorStationaryRoomMode();
            } catch (err) {
                console.error('[Voice] Failed to attach processor to track:', err);
                try { await processor.destroy?.(); } catch (_) {}
                _activeLocalAudioProcessor = null;
                stopLivekitTrackCompletely(track);
                if (isMicEnableAborted()) {
                    return;
                }
                if (dotnetHelper) {
                    dotnetHelper.invokeMethodAsync('OnVoiceLog', `AI Filter Processing Failed: ${err.message}.`, "error").catch(()=>{});
                }
                
                trackOptions.noiseSuppression = true;
                trackOptions.autoGainControl = true;
                trackOptions.echoCancellation = true;
                
                track = await createConfiguredLocalAudioTrack(trackOptions);
                if (isMicEnableAborted()) {
                    stopLivekitTrackCompletely(track);
                    return;
                }
            }
        }
        
        await publishConfiguredLocalMicTrack(track, opSeq);
        
    } catch (e) {
        console.error('[Voice] setMicrophoneEnabled failed:', e);
        if (dotnetHelper) {
            dotnetHelper.invokeMethodAsync('OnVoiceLog', `Microphone Error: ${e.message}`, "error").catch(()=>{});
        }
        if (!isMicEnableAborted() && currentRoom && currentRoom.localParticipant) {
            await currentRoom.localParticipant.setMicrophoneEnabled(true).catch(()=>{});
        }
    }
}

function getCustomPreset(resolutionStr, is60fps) {
    const mult = is60fps ? 1.5 : 1;

    // Unified 'Auto' preset across iOS, Android, and Desktop:
    // 1080p ceiling at 30fps / 3.0 Mbps with 'balanced' degradation
    // so WebRTC handles all stepdown and network management automatically.
    if (!resolutionStr || resolutionStr === 'Auto') {
        return { width: 1920, height: 1080, maxBitrate: 3000000 };
    }

    let isAppleMobile = /iPhone|iPad|iPod/i.test(navigator.userAgent) || (navigator.userAgent.includes("Mac") && "ontouchend" in document);
    let isAndroidMobile = /Android/i.test(navigator.userAgent) || (isNativePlatform() && getNativePlatform() === 'android');

    if (isAppleMobile && window.LivekitClient && window.LivekitClient.VideoPresets) {
        if (resolutionStr === '2160p') return window.LivekitClient.VideoPresets.h2160;
        if (resolutionStr === '1080p') return window.LivekitClient.VideoPresets.h1080;
        return window.LivekitClient.VideoPresets.h1080;
    }

    if (isAndroidMobile) {
        // Match iOS native hardware H.264 presets 1:1 on Android for explicit 1080p and 4K (2160p)
        if (resolutionStr === '2160p') return { width: 3840, height: 2160, maxBitrate: Math.round(8000000 * mult) };
        return { width: 1920, height: 1080, maxBitrate: Math.round(3000000 * mult) };
    }

    // Explicit manual resolutions on PC/Desktop (1080p and 4K/2160p)
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
            if (!opts.facingMode) opts.facingMode = _localCameraFacing || 'user';
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
            // In Auto mode, leave contentHint unset ('') so WebRTC's congestion controller
            // handles all stepdown & network management. When a specific resolution (1080p / 4K)
            // is chosen, set 'detail' so WebRTC pushes that resolution without downscaling.
            track.mediaStreamTrack.contentHint = isAuto ? '' : 'detail';
        } catch (e) {}
    }
    if (typeof track.setDegradationPreference === 'function') {
        try {
            await track.setDegradationPreference(degradationPref);
        } catch (e) {}
    }
    const sender = track.sender;
    if (sender && typeof sender.getParameters === 'function' && typeof sender.setParameters === 'function') {
        try {
            const params = sender.getParameters();
            if (params && Array.isArray(params.encodings) && params.encodings.length > 0) {
                const topIdx = params.encodings.length - 1;
                params.encodings.forEach((enc, idx) => {
                    if (idx === topIdx) {
                        enc.maxBitrate = targetPreset.maxBitrate;
                        enc.maxFramerate = effectiveFps;
                        if (!isAuto) {
                            enc.scaleResolutionDownBy = 1.0;
                        }
                    } else if (idx === topIdx - 1 && params.encodings.length >= 3) {
                        enc.maxBitrate = Math.max(900000, Math.round(targetPreset.maxBitrate * 0.4));
                        enc.maxFramerate = effectiveFps;
                    } else {
                        enc.maxBitrate = Math.max(350000, Math.round(targetPreset.maxBitrate * 0.15));
                        enc.maxFramerate = Math.min(30, effectiveFps);
                    }
                });
                if (track.encodings) {
                    track.encodings = params.encodings;
                }
                await sender.setParameters(params);
            }
        } catch (e) {
            console.warn('[VoiceInterop] Failed to update camera RTCRtpSender parameters:', e);
        }
    }
}

function getScreenPreset(resolutionStr, is60fps) {
    const targetFps = is60fps ? 60 : 30;
    let isAppleMobile = /iPhone|iPad|iPod/i.test(navigator.userAgent) || (navigator.userAgent.includes("Mac") && "ontouchend" in document);

    if (isAppleMobile && window.LivekitClient && window.LivekitClient.VideoPresets) {
        if (resolutionStr === '2160p') return { width: 3840, height: 2160, maxBitrate: is60fps ? 18000000 : 12000000, frameRate: targetFps };
        if (resolutionStr === '1440p') return { width: 2560, height: 1440, maxBitrate: is60fps ? 12000000 : 8000000, frameRate: targetFps };
        return { width: 1920, height: 1080, maxBitrate: is60fps ? 8000000 : 6000000, frameRate: targetFps };
    }

    // High-bitrate hardware-encoded H.264 presets tailored for gameplay ('Smooth') and meetings ('Clarity'):
    // - 2160p (4K): 25 Mbps @ 30fps / 35 Mbps @ 60fps
    // - 1440p (2K/QHD): 16 Mbps @ 30fps / 24 Mbps @ 60fps
    // - 1080p (FHD): 10 Mbps @ 30fps / 15 Mbps @ 60fps
    // - Auto: 1080p ceiling at 8 Mbps @ 30fps / 12 Mbps @ 60fps with dynamic network stepdown
    if (resolutionStr === '2160p') return { width: 3840, height: 2160, maxBitrate: is60fps ? 35000000 : 25000000, frameRate: targetFps };
    if (resolutionStr === '1440p') return { width: 2560, height: 1440, maxBitrate: is60fps ? 24000000 : 16000000, frameRate: targetFps };
    if (resolutionStr === '1080p') return { width: 1920, height: 1080, maxBitrate: is60fps ? 15000000 : 10000000, frameRate: targetFps };
    if (resolutionStr === '720p') return { width: 1280, height: 720, maxBitrate: is60fps ? 7500000 : 5000000, frameRate: targetFps };

    return { width: 1920, height: 1080, maxBitrate: is60fps ? 12000000 : 8000000, frameRate: targetFps };
}

function getScreenTrackMaxSourceEdge(track) {
    if (!track) return 0;
    const mediaStreamTrack = track.mediaStreamTrack || track;
    const settings = typeof mediaStreamTrack.getSettings === 'function' ? mediaStreamTrack.getSettings() : {};
    let srcW = settings.width || track.dimensions?.width || 0;
    let srcH = settings.height || track.dimensions?.height || 0;
    if ((srcW <= 0 || srcH <= 0) && typeof window !== 'undefined' && window.screen) {
        const dpr = window.devicePixelRatio || 1;
        srcW = Math.round((window.screen.width || 1920) * dpr);
        srcH = Math.round((window.screen.height || 1080) * dpr);
    }
    return Math.max(srcW, srcH);
}

function computeScreenScaleDown(track, targetPreset) {
    if (!track || !targetPreset) return 1.0;
    const sourceMaxEdge = getScreenTrackMaxSourceEdge(track);
    const targetMaxEdge = Math.max(targetPreset.width || 1920, targetPreset.height || 1080);

    if (sourceMaxEdge <= 0 || targetMaxEdge <= 0 || sourceMaxEdge <= targetMaxEdge) {
        return 1.0;
    }
    const rawScale = sourceMaxEdge / targetMaxEdge;
    return rawScale < 1.05 ? 1.0 : Math.round(rawScale * 1000) / 1000;
}

async function applyScreenShareEncoding(track, resolutionStr = 'Auto', targetFps = 30, mode = 'motion') {
    if (!track) return;
    const isAuto = !resolutionStr || resolutionStr === 'Auto';
    const is60 = targetFps > 35;
    const targetPreset = getScreenPreset(resolutionStr, is60);
    const isDetail = mode === 'detail';
    // When an explicit resolution (1080p, 1440p, 4K) is selected, lock 'maintain-resolution' even in Smooth/gameplay
    // mode so WebRTC never downscales a 1440p60/1080p60 stream during high-motion scenes.
    // In 'Auto' mode, allow 'balanced' stepdown in motion mode while preserving sharpness in 'detail' mode.
    const degradationPref = (isAuto && !isDetail) ? 'balanced' : 'maintain-resolution';

    // Content hint & capture frameRate constraint:
    // "motion" prioritizes fluid 30/60 FPS gameplay/video frame pacing
    // "detail" optimizes quantization for razor-sharp static text/lines in meetings & IDE sharing
    if (track.mediaStreamTrack) {
        try {
            track.mediaStreamTrack.contentHint = isDetail ? 'detail' : 'motion';
        } catch (e) {}
        if (typeof track.mediaStreamTrack.applyConstraints === 'function') {
            try {
                await track.mediaStreamTrack.applyConstraints({
                    frameRate: { ideal: targetFps, max: targetFps }
                });
            } catch (e) {
                console.warn('[VoiceInterop] Failed to apply frameRate constraint on screen track:', e);
            }
        }
    }

    if (typeof track.setDegradationPreference === 'function') {
        try {
            await track.setDegradationPreference(degradationPref);
        } catch (e) {}
    }

    const scaleDown = computeScreenScaleDown(track, targetPreset);
    const sender = track.sender;
    if (sender && typeof sender.getParameters === 'function' && typeof sender.setParameters === 'function') {
        try {
            const params = sender.getParameters();
            if (params && Array.isArray(params.encodings) && params.encodings.length > 0) {
                const topIdx = params.encodings.length - 1;
                params.encodings.forEach((enc, idx) => {
                    enc.maxFramerate = targetFps;
                    if (idx === topIdx) {
                        enc.maxBitrate = targetPreset.maxBitrate;
                        enc.scaleResolutionDownBy = scaleDown;
                    } else {
                        enc.maxBitrate = Math.max(500000, Math.round(targetPreset.maxBitrate / 3));
                        enc.scaleResolutionDownBy = scaleDown * 2;
                    }
                });
                if (track.encodings) {
                    track.encodings = params.encodings;
                }
                await sender.setParameters(params);
            }
        } catch (e) {
            console.warn('[VoiceInterop] Failed to update screen share RTCRtpSender parameters:', e);
        }
    }
}




export async function setCameraEnabled(enabled, resolutionStr = 'Auto', targetFps = 30, deviceId = null) {
    _localCameraDesired = Boolean(enabled);
    _lastCameraResolution = resolutionStr || 'Auto';
    _lastCameraFps = targetFps || 30;
    if (!enabled && isNativePlatform() && getNativePlatform() === 'ios') {
        const audioPlugin = getAudioSessionPlugin();
        if (audioPlugin && typeof audioPlugin.stopNativeCamera === 'function') {
            await audioPlugin.stopNativeCamera().catch(() => {});
        } else if (typeof window !== 'undefined' && typeof window.SpokesNative?.controlNativeCamera === 'function') {
            try { window.SpokesNative.controlNativeCamera('stop', {}); } catch (e) {}
        }
        const localId = currentRoom?.localParticipant?.identity;
        if (localId) {
            try { unregisterPipVideoTrack(localId, 'camera'); } catch (e) {}
            clearParticipantVideoDom(localId, 'camera');
            if (dotnetHelper) {
                dotnetHelper.invokeMethodAsync('OnVideoTrackStateChanged', localId, 'camera', false).catch(() => {});
            }
        }
        try { updatePipCallState({ isVideoOn: false, localCameraFacing: _localCameraFacing }); } catch (e) {}
        return;
    }
    if (currentRoom && currentRoom.localParticipant) {
        try {
            if (isNativePlatform() && getNativePlatform() === 'ios') {
                const audioPlugin = getAudioSessionPlugin();
                if (enabled) {
                    if (_iosCachedCameras.length === 0) {
                        await fetchIosNativeCameras();
                    }

                    let targetDeviceId = deviceId || _iosActiveCameraId || localStorage.getItem('spokes_active_camera_id') || '';
                    let targetFacing = _localCameraFacing || 'user';
                    const matchedCam = _iosCachedCameras.find(c => c.deviceId === targetDeviceId);
                    if (matchedCam) {
                        targetFacing = matchedCam.facingMode || targetFacing;
                    } else {
                        const rawDev = String(targetDeviceId || '').toLowerCase();
                        if (rawDev === 'ios_back' || rawDev.includes('back') || rawDev === 'environment' || rawDev.includes('telephoto') || rawDev.includes('ultra_wide')) {
                            targetFacing = 'environment';
                        } else if (rawDev === 'ios_front' || rawDev.includes('front') || rawDev === 'user') {
                            targetFacing = 'user';
                        }
                    }

                    const iosCaps = getIosCameraCapabilities(targetDeviceId, targetFacing);
                    const clamped = clampCameraResolutionForCapabilities(
                        _lastCameraResolution,
                        _lastCameraFps,
                        iosCaps.maxDim,
                        iosCaps.maxFps
                    );
                    _lastCameraResolution = clamped.resolution;
                    _lastCameraFps = clamped.fps;

                    const creds = await ensureIosCompanionCredentials();
                    const startPayload = {
                        url: creds?.url || _iosCompanionLiveKitUrl || '',
                        token: creds?.token || _iosCompanionToken || '',
                        localUserId: creds?.localUserId || currentRoom.localParticipant.identity || '',
                        deviceId: targetDeviceId,
                        facingMode: targetFacing,
                        resolution: _lastCameraResolution,
                        fps: _lastCameraFps
                    };

                    if (audioPlugin && typeof audioPlugin.startNativeCamera === 'function') {
                        try {
                            const res = await audioPlugin.startNativeCamera(startPayload);
                            console.info('[Voice] iOS startNativeCamera result:', JSON.stringify(res || {}));
                            if (res?.facingMode === 'environment' || res?.facingMode === 'user') {
                                targetFacing = res.facingMode;
                            }
                            if (res?.deviceId) {
                                targetDeviceId = res.deviceId;
                            }
                        } catch (e) {
                            console.warn('[Voice] iOS audioPlugin.startNativeCamera error:', e);
                        }
                    } else if (typeof window !== 'undefined' && typeof window.SpokesNative?.controlNativeCamera === 'function') {
                        try { window.SpokesNative.controlNativeCamera('start', startPayload); } catch (e) {}
                    }

                    _localCameraFacing = targetFacing;
                    if (typeof window !== 'undefined') window._spokesLocalCameraFacing = _localCameraFacing;

                    const cameras = await getAvailableCameras();
                    if (!targetDeviceId || !cameras.some(c => c.deviceId === targetDeviceId)) {
                        const byFacing = _iosCachedCameras.find(c => c.facingMode === _localCameraFacing);
                        targetDeviceId = byFacing?.deviceId || cameras[0]?.deviceId || targetDeviceId;
                    }
                    _iosActiveCameraId = targetDeviceId;
                    if (_iosActiveCameraId) {
                        localStorage.setItem('spokes_active_camera_id', _iosActiveCameraId);
                    }

                    try { updatePipCallState({ isVideoOn: true, localCameraFacing: _localCameraFacing }); } catch (e) {}
                    syncLocalStageVideoMirroring();
                    reportIosCameraCapabilitiesToDotnet(_iosActiveCameraId);

                    return { cameras, activeCameraDeviceId: _iosActiveCameraId };
                }
            }

            if (enabled) {
                let exTracks = currentRoom.localParticipant.videoTrackPublications;
                let pubArr = [];
                if (exTracks) {
                    pubArr = typeof exTracks.values === 'function' ? Array.from(exTracks.values()) : Object.values(exTracks);
                }
                let cameraPub = pubArr.find(p => p.source === window.LivekitClient.Track.Source.Camera);

                const isAutoRes = !resolutionStr || resolutionStr === 'Auto';
                const effectiveFps = isAutoRes ? 30 : targetFps;
                let is60 = effectiveFps > 35;
                let targetPreset = getCustomPreset(resolutionStr, is60);
                
                let livekitPreset = targetPreset;
                let isApple = /iPhone|iPad|iPod/i.test(navigator.userAgent) || (navigator.userAgent.includes("Mac") && "ontouchend" in document);
                if (!isApple && window.LivekitClient && window.LivekitClient.VideoPreset) {
                    livekitPreset = new window.LivekitClient.VideoPreset(targetPreset.width, targetPreset.height, targetPreset.maxBitrate, effectiveFps);
                }

                // Preserve active camera selection and facing mode across operations
                let activeMediaTrack = cameraPub?.track?.mediaStreamTrack;
                let targetDeviceId = deviceId || activeMediaTrack?.getSettings?.().deviceId || localStorage.getItem('spokes_active_camera_id');
                let currentFacing = activeMediaTrack?.getSettings?.().facingMode || _localCameraFacing || 'user';
                if (currentFacing === 'environment' || currentFacing === 'user') {
                    _localCameraFacing = currentFacing;
                    if (typeof window !== 'undefined') window._spokesLocalCameraFacing = _localCameraFacing;
                }

                let trackOptions = { resolution: livekitPreset };
                if (targetDeviceId && targetDeviceId !== 'default') {
                    trackOptions.deviceId = targetDeviceId;
                } else if (currentFacing) {
                    trackOptions.facingMode = currentFacing;
                } else {
                    trackOptions.facingMode = 'user';
                }

                // Universal H.264 hardware acceleration across iOS, Android, and Desktop.
                // - 'Auto': 1080p @ 30fps, 3.0 Mbps ceiling with 'balanced' degradation handling all stepdown & network management.
                // - Explicit '1080p' or '2160p' (4K): single-layer (simulcast: false) + 'maintain-resolution' so the app pushes
                //   that exact resolution no matter what.
                const isMobileDevice = isNativePlatform() || /Android|iPhone|iPad|iPod/i.test(navigator.userAgent);
                const useSimulcast = isAutoRes && !isMobileDevice;
                const simulcastModeChanged = _lastCameraSimulcast !== null && _lastCameraSimulcast !== useSimulcast;
                _lastCameraSimulcast = useSimulcast;
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

                if (cameraPub && cameraPub.track) {
                    let actualTrack = cameraPub.videoTrack || cameraPub.track;
                    const isTrackEnded = actualTrack.mediaStreamTrack && actualTrack.mediaStreamTrack.readyState === 'ended';
                    try {
                        if (!isTrackEnded && !simulcastModeChanged && typeof actualTrack.restartTrack === 'function') {
                            let restartOptions = { resolution: livekitPreset };
                            if (targetDeviceId && targetDeviceId !== 'default') restartOptions.deviceId = targetDeviceId;
                            else if (currentFacing) restartOptions.facingMode = currentFacing;
                            await actualTrack.restartTrack(restartOptions);
                            await applyCameraTrackEncoding(actualTrack, targetPreset, effectiveFps, resolutionStr);
                            handleLocalTrackPublished(cameraPub, currentRoom.localParticipant);
                        } else {
                            try { await currentRoom.localParticipant.unpublishTrack(actualTrack, true); } catch (e) {}
                            stopLivekitTrackCompletely(actualTrack);
                            let track = await createConfiguredLocalVideoTrack(trackOptions);
                            await currentRoom.localParticipant.publishTrack(track, publishOptions);
                            await applyCameraTrackEncoding(track, targetPreset, effectiveFps, resolutionStr);
                        }
                    } catch(e) {
                        console.warn("[Voice] LiveKit restartTrack failed, recreating camera track:", e);
                        try { await currentRoom.localParticipant.unpublishTrack(actualTrack, true); } catch (_) {}
                        stopLivekitTrackCompletely(actualTrack);
                        let track = await createConfiguredLocalVideoTrack(trackOptions);
                        await currentRoom.localParticipant.publishTrack(track, publishOptions);
                        await applyCameraTrackEncoding(track, targetPreset, effectiveFps, resolutionStr);
                    }
                } else {
                    let track = await createConfiguredLocalVideoTrack(trackOptions);
                    await currentRoom.localParticipant.publishTrack(track, publishOptions);
                    await applyCameraTrackEncoding(track, targetPreset, effectiveFps, resolutionStr);
                }

                if (targetDeviceId && targetDeviceId !== 'default') {
                    localStorage.setItem('spokes_active_camera_id', targetDeviceId);
                }

                try { updatePipCallState({ isVideoOn: true, localCameraFacing: _localCameraFacing }); } catch (e) {}

                const cameras = await getAvailableCameras();
                const activeId = await getActiveVoiceCameraId();
                return { cameras, activeCameraDeviceId: activeId };

            } else {
                const localId = currentRoom.localParticipant.identity;
                let tracks = currentRoom.localParticipant.videoTrackPublications;
                if (tracks) {
                    let pubArr = typeof tracks.values === 'function' ? Array.from(tracks.values()) : Object.values(tracks);
                    let cameraPubs = pubArr.filter(p => p.source === window.LivekitClient.Track.Source.Camera || (!isScreenShareTrackOrPub(p.track, p) && p.kind === window.LivekitClient.Track.Kind.Video));
                    for (let cameraPub of cameraPubs) {
                        let actualTrack = cameraPub.videoTrack || cameraPub.track;
                        if (actualTrack) {
                            try {
                                await currentRoom.localParticipant.unpublishTrack(actualTrack, true);
                            } catch (e) {
                                console.warn('[VoiceInterop] unpublishTrack camera warning:', e);
                            } finally {
                                stopLivekitTrackCompletely(actualTrack);
                            }
                        }
                    }
                }
                if (localId) {
                    try { unregisterPipVideoTrack(localId, 'camera'); } catch (e) {}
                    clearParticipantVideoDom(localId, 'camera');
                    if (dotnetHelper) {
                        dotnetHelper.invokeMethodAsync('OnVideoTrackStateChanged', localId, 'camera', false).catch(() => {});
                    }
                }
                try { updatePipCallState({ isVideoOn: false, localCameraFacing: _localCameraFacing }); } catch (e) {}
            }
        } catch (error) {
            console.error('[VoiceInterop] Error in setCameraEnabled:', error);
            throw error;
        }
    }
}

export async function getAvailableCameras() {
    if (isNativePlatform() && getNativePlatform() === 'ios') {
        const cams = await fetchIosNativeCameras();
        return cams.map(c => ({ deviceId: c.deviceId, label: c.label }));
    }
    if (!window.LivekitClient || !currentRoom) return [];
    try {
        let devices = await window.LivekitClient.Room.getLocalDevices('videoinput');
        let nativeAndroidCams = [];
        const isAndroid = isNativePlatform() && getNativePlatform() === 'android';
        if (isAndroid && window.Capacitor?.Plugins?.TelecomConnectionPlugin?.getNativeCameras) {
            try {
                const res = await window.Capacitor.Plugins.TelecomConnectionPlugin.getNativeCameras();
                if (Array.isArray(res?.cameras)) {
                    nativeAndroidCams = res.cameras;
                }
            } catch (e) {
                console.warn('[Voice] Android getNativeCameras error:', e);
            }
        }

        let distinct = [];
        let backOrdinal = 0;
        let frontOrdinal = 0;
        for (let d of devices) {
            if (d.deviceId === 'default' || d.deviceId === 'communications') continue;
            let rawLabel = String(d.label || '').trim();
            let cleanLabel = rawLabel;

            if (isAndroid) {
                const camMatch = /camera2?\s+(\d+)/i.exec(rawLabel);
                const camIdNum = camMatch ? camMatch[1] : null;
                const matchedNative = camIdNum !== null
                    ? nativeAndroidCams.find(nc => String(nc.cameraId) === camIdNum)
                    : nativeAndroidCams.find(nc => String(nc.cameraId) === String(d.deviceId));

                if (matchedNative?.label) {
                    cleanLabel = matchedNative.label;
                } else if (/facing\s+front/i.test(rawLabel) || /front/i.test(rawLabel)) {
                    frontOrdinal++;
                    cleanLabel = frontOrdinal === 1 ? 'Front Camera' : `Front Camera ${frontOrdinal}`;
                } else if (/facing\s+back/i.test(rawLabel) || /back/i.test(rawLabel)) {
                    const backNative = nativeAndroidCams.filter(nc => nc.facingMode === 'environment');
                    if (backNative[backOrdinal]?.label) {
                        cleanLabel = backNative[backOrdinal].label;
                    } else {
                        const fallbackRoles = ['Back Camera (Wide)', 'Back Camera (Ultra-Wide)', 'Back Camera (Telephoto)'];
                        cleanLabel = fallbackRoles[backOrdinal] || `Back Camera ${backOrdinal + 1}`;
                    }
                    backOrdinal++;
                }
            }

            let exists = distinct.find(x => x.deviceId === d.deviceId || (cleanLabel && x.label === cleanLabel && !isAndroid));
            if (!exists) {
                distinct.push({ deviceId: d.deviceId, label: cleanLabel || rawLabel || `Camera ${distinct.length + 1}` });
            }
        }
        return distinct;
    } catch(e) {
        return [];
    }
}

export async function getActiveVoiceCameraId() {
    if (isNativePlatform() && getNativePlatform() === 'ios') {
        if (_iosActiveCameraId) return _iosActiveCameraId;
        const saved = localStorage.getItem('spokes_active_camera_id');
        if (saved) return saved;
        const cams = await fetchIosNativeCameras();
        const byFacing = cams.find(c => c.facingMode === _localCameraFacing) || cams[0];
        return byFacing?.deviceId || 'com.apple.avfoundation.avcapturedevice.built-in_video:1';
    }
    if (!currentRoom) return "";
    try {
        if (typeof currentRoom.getActiveDevice === 'function') {
            const deviceId = await currentRoom.getActiveDevice('videoinput');
            if (deviceId) return deviceId;
        }
        if (currentRoom.localParticipant && window.LivekitClient && window.LivekitClient.Track) {
            let tracks = currentRoom.localParticipant.videoTrackPublications;
            if (tracks) {
                let pubArr = typeof tracks.values === 'function' ? Array.from(tracks.values()) : Object.values(tracks);
                let cameraPub = pubArr.find(p => p.source === window.LivekitClient.Track.Source.Camera);
                if (cameraPub && cameraPub.track && cameraPub.track.mediaStreamTrack) {
                    return cameraPub.track.mediaStreamTrack.getSettings().deviceId || "";
                }
            }
        }
    } catch (e) {
        console.warn('[Voice] Failed to get active camera ID:', e);
    }
    return "";
}

export async function switchActiveCamera(deviceId) {
    if (!currentRoom || !currentRoom.localParticipant) return;
    try {
        if (isNativePlatform() && getNativePlatform() === 'ios') {
            if (_iosCachedCameras.length === 0) {
                await fetchIosNativeCameras();
            }
            const audioPlugin = getAudioSessionPlugin();
            const matchedCam = _iosCachedCameras.find(c => c.deviceId === deviceId);
            const rawDev = String(deviceId || '').toLowerCase();
            let requestedFacing = (_localCameraFacing === 'environment') ? 'user' : 'environment';
            if (matchedCam?.facingMode === 'environment' || matchedCam?.facingMode === 'user') {
                requestedFacing = matchedCam.facingMode;
            } else if (rawDev === 'ios_back' || rawDev.includes('back') || rawDev === 'environment' || rawDev.includes('telephoto') || rawDev.includes('ultra_wide')) {
                requestedFacing = 'environment';
            } else if (rawDev === 'ios_front' || rawDev.includes('front') || rawDev === 'user') {
                requestedFacing = 'user';
            }

            let targetDeviceId = matchedCam?.deviceId || deviceId || '';
            const iosCaps = getIosCameraCapabilities(targetDeviceId, requestedFacing);
            const clamped = clampCameraResolutionForCapabilities(
                _lastCameraResolution || 'Auto',
                _lastCameraFps || 30,
                iosCaps.maxDim,
                iosCaps.maxFps
            );
            _lastCameraResolution = clamped.resolution;
            _lastCameraFps = clamped.fps;

            const creds = await ensureIosCompanionCredentials();
            const switchPayload = {
                url: creds?.url || _iosCompanionLiveKitUrl || '',
                token: creds?.token || _iosCompanionToken || '',
                localUserId: creds?.localUserId || currentRoom.localParticipant.identity || '',
                deviceId: targetDeviceId,
                facingMode: requestedFacing,
                resolution: _lastCameraResolution,
                fps: _lastCameraFps
            };

            if (audioPlugin && typeof audioPlugin.switchNativeCamera === 'function') {
                try {
                    const res = await audioPlugin.switchNativeCamera(switchPayload);
                    if (res?.facingMode === 'environment' || res?.facingMode === 'user') {
                        requestedFacing = res.facingMode;
                    }
                    if (res?.deviceId) {
                        targetDeviceId = res.deviceId;
                    }
                } catch (e) {
                    console.warn('[Voice] iOS audioPlugin.switchNativeCamera error:', e);
                }
            } else if (typeof window !== 'undefined' && typeof window.SpokesNative?.controlNativeCamera === 'function') {
                try { window.SpokesNative.controlNativeCamera('switch', switchPayload); } catch (e) {}
            }

            _localCameraFacing = requestedFacing;
            if (typeof window !== 'undefined') window._spokesLocalCameraFacing = _localCameraFacing;
            if (!targetDeviceId) {
                const byFacing = _iosCachedCameras.find(c => c.facingMode === _localCameraFacing);
                targetDeviceId = byFacing?.deviceId || _iosActiveCameraId;
            }
            _iosActiveCameraId = targetDeviceId;
            if (_iosActiveCameraId) {
                localStorage.setItem('spokes_active_camera_id', _iosActiveCameraId);
            }
            updatePipCallState({ isVideoOn: true, localCameraFacing: _localCameraFacing });
            syncLocalStageVideoMirroring();
            reportIosCameraCapabilitiesToDotnet(_iosActiveCameraId);
            return;
        }
        await currentRoom.switchActiveDevice('videoinput', deviceId);
        if (deviceId && deviceId !== 'default') {
            localStorage.setItem('spokes_active_camera_id', deviceId);
        }
        let pubArr = typeof currentRoom.localParticipant.videoTrackPublications.values === 'function' ? Array.from(currentRoom.localParticipant.videoTrackPublications.values()) : Object.values(currentRoom.localParticipant.videoTrackPublications);
        let cameraPub = pubArr.find(p => p.source === window.LivekitClient.Track.Source.Camera);
        const activeTrack = cameraPub?.videoTrack || cameraPub?.track;
        const rawFacing = activeTrack?.mediaStreamTrack?.getSettings?.()?.facingMode;
        if (rawFacing === 'environment' || rawFacing === 'user') {
            _localCameraFacing = rawFacing;
            if (typeof window !== 'undefined') window._spokesLocalCameraFacing = _localCameraFacing;
        }
        if (activeTrack?.mediaStreamTrack) {
            const hwCaps = getHardwareCameraCapabilities(activeTrack.mediaStreamTrack);
            if (hwCaps.maxDim > 0) {
                const clamped = clampCameraResolutionForCapabilities(
                    _lastCameraResolution || 'Auto',
                    _lastCameraFps || 30,
                    hwCaps.maxDim,
                    hwCaps.maxFps
                );
                const resChanged = clamped.resolution !== (_lastCameraResolution || 'Auto');
                const fpsChanged = clamped.fps !== (_lastCameraFps || 30);
                _lastCameraResolution = clamped.resolution;
                _lastCameraFps = clamped.fps;
                if (resChanged || fpsChanged) {
                    const isAutoRes = !_lastCameraResolution || _lastCameraResolution === 'Auto';
                    const effectiveFps = isAutoRes ? 30 : _lastCameraFps;
                    const targetPreset = getCustomPreset(_lastCameraResolution, effectiveFps > 35);
                    await applyCameraTrackEncoding(activeTrack, targetPreset, effectiveFps, _lastCameraResolution);
                }
            }
        }
        if (cameraPub && cameraPub.track) handleLocalTrackPublished(cameraPub, currentRoom.localParticipant);
    } catch(e) {
        console.error('[Voice] Failed to switch camera:', e);
    }
}

export async function updateCameraSettings(resolutionStr = 'Auto', targetFps = 30, deviceId = null) {
    if (!currentRoom || !currentRoom.localParticipant || !_localCameraDesired) return;
    _lastCameraResolution = resolutionStr || 'Auto';
    _lastCameraFps = targetFps || 30;
    try {
        if (isNativePlatform() && getNativePlatform() === 'ios') {
            const audioPlugin = getAudioSessionPlugin();
            const targetDeviceId = deviceId || _iosActiveCameraId || localStorage.getItem('spokes_active_camera_id') || '';
            const creds = await ensureIosCompanionCredentials();
            const updatePayload = {
                url: creds?.url || _iosCompanionLiveKitUrl || '',
                token: creds?.token || _iosCompanionToken || '',
                localUserId: creds?.localUserId || currentRoom.localParticipant.identity || '',
                deviceId: targetDeviceId,
                facingMode: _localCameraFacing || 'user',
                resolution: _lastCameraResolution,
                fps: _lastCameraFps
            };
            if (audioPlugin && typeof audioPlugin.startNativeCamera === 'function') {
                await audioPlugin.startNativeCamera(updatePayload);
            } else if (typeof window !== 'undefined' && typeof window.SpokesNative?.controlNativeCamera === 'function') {
                window.SpokesNative.controlNativeCamera('start', updatePayload);
            }
            return;
        }
        await setCameraEnabled(true, _lastCameraResolution, _lastCameraFps, deviceId);
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
    if (!dotnetHelper) return;
    try {
        const mics = await getAvailableMicrophones();
        const activeMicId = await getActiveMicrophoneId();
        const speakers = await getAvailableSpeakers();
        const activeSpeakerId = await getActiveSpeakerId();
        dotnetHelper.invokeMethodAsync('OnAudioDevicesUpdated', mics, activeMicId, speakers, activeSpeakerId);
    } catch (e) {
        console.warn('[Voice] Failed to update audio devices:', e);
    }
}

export async function getAvailableMicrophones() {
    if (isNativePlatform()) {
        let platform = getNativePlatform();
        if (platform === 'ios' || platform === 'android') {
            return [];
        }
    }
    
    let devices = [];
    if (window.LivekitClient?.Room?.getLocalDevices) {
        try {
            devices = await window.LivekitClient.Room.getLocalDevices('audioinput');
        } catch(e) {}
    }
    if ((!devices || devices.length === 0) && navigator?.mediaDevices?.enumerateDevices) {
        try {
            const all = await navigator.mediaDevices.enumerateDevices();
            devices = all.filter(d => d.kind === 'audioinput');
        } catch(e) {}
    }

    const result = [
        { deviceId: 'default', label: 'System Default' }
    ];

    if (devices && devices.length > 0) {
        for (const d of devices) {
            if (d.deviceId === 'default' || d.deviceId === 'communications') continue;
            if (!d.deviceId && !d.label) continue;
            
            const label = d.label || `Microphone (${d.deviceId.substring(0, 5)})`;
            if (!result.some(x => x.deviceId === d.deviceId || x.label === label)) {
                result.push({
                    deviceId: d.deviceId,
                    groupId: d.groupId,
                    label: label
                });
            }
        }
    }

    return result;
}

export async function getActiveMicrophoneId() {
    if (isNativePlatform()) return "";
    
    const mics = await getAvailableMicrophones();
    if (mics.length === 0) return "default";

    // 1. When actively connected, inspect the live published raw hardware capture track first
    // so Voice Settings always reflects the actual physical microphone transmitting audio.
    if (currentRoom) {
        try {
            let liveDeviceId = "";
            if (currentRoom.localParticipant && window.LivekitClient?.Track) {
                const tracks = currentRoom.localParticipant.audioTrackPublications;
                if (tracks) {
                    const pubArr = typeof tracks.values === 'function' ? Array.from(tracks.values()) : Object.values(tracks);
                    const micPub = pubArr.find(p => p.source === window.LivekitClient.Track.Source.Microphone && p.track);
                    const rawTrack = getRawCaptureMediaStreamTrack(micPub?.track);
                    if (rawTrack && typeof rawTrack.getSettings === 'function') {
                        liveDeviceId = rawTrack.getSettings()?.deviceId || "";
                    }
                }
            }
            if ((!liveDeviceId || liveDeviceId === 'default' || liveDeviceId === 'communications') && typeof currentRoom.getActiveDevice === 'function') {
                liveDeviceId = (await currentRoom.getActiveDevice('audioinput')) || liveDeviceId;
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

    if (currentRoom) {
        try {
            const effectiveSwitchId = targetMic === 'default'
                ? ((await resolvePreferredMicDeviceId()) || 'default')
                : targetMic;
            await currentRoom.switchActiveDevice('audioinput', effectiveSwitchId);

            if (currentRoom.localParticipant && window.LivekitClient?.Track) {
                const tracks = currentRoom.localParticipant.audioTrackPublications;
                const pubArr = tracks
                    ? (typeof tracks.values === 'function' ? Array.from(tracks.values()) : Object.values(tracks))
                    : [];
                const micPub = pubArr.find(p => p.source === window.LivekitClient.Track.Source.Microphone && p.track);
                if (micPub?.track) {
                    bindLocalHardwareMuteMonitor(micPub.track);
                    const vadTrack = micPub.track.processor?.processedTrack || micPub.track.mediaStreamTrack;
                    const rawTrack = getRawCaptureMediaStreamTrack(micPub.track);
                    if (currentRoom.localParticipant.identity && vadTrack) {
                        registerLocalMicVad(currentRoom.localParticipant.identity, vadTrack, rawTrack);
                    }
                }
            }
        } catch (e) {
            console.error('[Voice] Failed to switch microphone:', e);
        }
    }
    await updateAudioDevices();
}

export async function isAndroidDevice() {
    return isNativePlatform() && getNativePlatform() === 'android';
}

export async function isNativePlatformApp() {
    return isNativePlatform();
}

export async function getAvailableSpeakers() {
    if (isNativePlatform()) {
        let platform = getNativePlatform();
        if (platform === 'ios') {
            return [];
        } else if (platform === 'android') {
            let devices = [];
            devices.push({ deviceId: 'speaker', label: 'Speaker' });
            devices.push({ deviceId: 'earpiece', label: 'Earpiece' });
            
            try {
                if (window.Capacitor.Plugins.TelecomConnectionPlugin) {
                    let res = await window.Capacitor.Plugins.TelecomConnectionPlugin.getAvailableRoutes();
                    if (res && typeof res.hasBluetooth === 'boolean' && res.hasBluetooth) {
                        devices.push({ deviceId: 'bluetooth', label: 'Bluetooth' });
                    }
                }
            } catch(e) {}
            return devices;
        }
    }
    
    let devices = [];
    if (window.LivekitClient?.Room?.getLocalDevices) {
        try {
            devices = await window.LivekitClient.Room.getLocalDevices('audiooutput');
        } catch(e) {}
    }
    if ((!devices || devices.length === 0) && navigator?.mediaDevices?.enumerateDevices) {
        try {
            const all = await navigator.mediaDevices.enumerateDevices();
            devices = all.filter(d => d.kind === 'audiooutput');
        } catch(e) {}
    }

    const result = [
        { deviceId: 'default', label: 'System Default' }
    ];

    if (devices && devices.length > 0) {
        for (const d of devices) {
            if (d.deviceId === 'default' || d.deviceId === 'communications') continue;
            if (!d.deviceId && !d.label) continue;
            
            const label = d.label || `Speaker (${d.deviceId.substring(0, 5)})`;
            if (!result.some(x => x.deviceId === d.deviceId || x.label === label)) {
                result.push({
                    deviceId: d.deviceId,
                    groupId: d.groupId,
                    label: label
                });
            }
        }
    }

    return result;
}

let _activeNativeSpeakerId = 'default';

export async function getActiveSpeakerId() {
    if (isNativePlatform()) {
        let platform = getNativePlatform();
        if (platform === 'ios' || platform === 'android') {
            return _activeNativeSpeakerId;
        }
    }

    const speakers = await getAvailableSpeakers();
    if (speakers.length === 0) return "default";

    try {
        const saved = localStorage.getItem('spokes_active_speaker_id');
        if (saved && speakers.some(s => s.deviceId === saved)) {
            return saved;
        }
    } catch(e) {}
    
    try {
        let activeId = "";
        if (currentRoom && typeof currentRoom.getActiveDevice === 'function') {
            activeId = await currentRoom.getActiveDevice('audiooutput');
        }
        if (activeId && activeId !== 'default' && activeId !== 'communications') {
            if (speakers.some(s => s.deviceId === activeId)) return activeId;
        }
    } catch (e) {}

    return "default";
}

export async function switchActiveSpeaker(deviceId) {
    const targetSink = (!deviceId || deviceId === 'default') ? 'default' : deviceId;
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

    if (isNativePlatform()) {
        let platform = getNativePlatform();
        try {
            if (platform === 'ios') {
                let audioPlugin = getAudioSessionPlugin();
                if (audioPlugin) {
                    let targetRoute = targetSink === 'speakerphone' || targetSink === 'speaker' ? 'speaker' : 'earpiece';
                    let res = await audioPlugin.setAudioRoute({ route: targetRoute });
                    console.info(`[Voice] iOS Native Route Set (${targetRoute}): Category=${res.category}, Mode=${res.mode}, Output=${res.activeOutput}`);
                }
            } else if (platform === 'android') {
                if (window.Capacitor.Plugins.TelecomConnectionPlugin) {
                    await window.Capacitor.Plugins.TelecomConnectionPlugin.setAudioRoute({ route: targetSink });
                }
            }
            _activeNativeSpeakerId = targetSink;
            await updateAudioDevices();
        } catch (e) {
            console.error('[Voice] Failed to switch native speaker:', e);
        }
    } else {
        if (currentRoom) {
            try {
                await currentRoom.switchActiveDevice('audiooutput', targetSink === 'default' ? '' : targetSink);
            } catch(e) {
                console.error('[Voice] Failed to switch speaker:', e);
            }
        }
        await updateAudioDevices();
    }
}

export async function playTestSpeakerSound(deviceId) {
    try {
        const audio = new Audio('/sounds/spokesnotif1.wav');
        const sink = (!deviceId || deviceId === 'default') ? '' : deviceId;
        if (typeof audio.setSinkId === 'function') {
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


export async function setScreenShareEnabled(enabled, resolutionStr = 'Auto', targetFps = 30, mode = 'motion', captureAudio = false) {
    if (currentRoom && currentRoom.localParticipant) {
        try {
            if (enabled) {
                let exTracks = currentRoom.localParticipant.videoTrackPublications;
                if (exTracks) {
                    let pubArr = typeof exTracks.values === 'function' ? Array.from(exTracks.values()) : Object.values(exTracks);
                    let screenPub = pubArr.find(p => p.source === window.LivekitClient.Track.Source.ScreenShare);
                    if (screenPub && screenPub.track) {
                        const oldTrack = screenPub.videoTrack || screenPub.track;
                        try { await currentRoom.localParticipant.unpublishTrack(oldTrack, true); } catch (e) {}
                        stopLivekitTrackCompletely(oldTrack);
                    }
                }

                let exAudioTracks = currentRoom.localParticipant.audioTrackPublications;
                if (exAudioTracks) {
                    let pubArr = typeof exAudioTracks.values === 'function' ? Array.from(exAudioTracks.values()) : Object.values(exAudioTracks);
                    let screenAudioPub = pubArr.find(p => p.source === window.LivekitClient.Track.Source.ScreenShareAudio);
                    if (screenAudioPub && screenAudioPub.track) {
                        const oldAudioTrack = screenAudioPub.audioTrack || screenAudioPub.track;
                        try { await currentRoom.localParticipant.unpublishTrack(oldAudioTrack, true); } catch (e) {}
                        stopLivekitTrackCompletely(oldAudioTrack);
                    }
                }

                let is60 = targetFps > 35;
                let targetPreset = getScreenPreset(resolutionStr, is60);
                let probePreset = Object.assign({}, getScreenPreset('2160p', is60), { frameRate: targetFps });

                let trackRes = await window.LivekitClient.createLocalScreenTracks({
                    resolution: probePreset,
                    audio: true
                });
                let tracks = Array.isArray(trackRes) ? trackRes : [trackRes];
                let videoTrack = tracks.find(t => t.kind === 'video') || tracks[0];
                let audioTrack = tracks.find(t => t.kind === 'audio');
                globalScreenTracks = tracks;

                // Content hint controls WebRTC rate control degradation:
                // "motion" maintains frame rate (preventing 1-5 FPS drops during window movement)
                // "detail" prioritizes razor-sharp static text/lines for documents
                if (videoTrack && videoTrack.mediaStreamTrack) {
                    videoTrack.mediaStreamTrack.contentHint = mode === 'detail' ? 'detail' : 'motion';
                    if (typeof videoTrack.mediaStreamTrack.addEventListener === 'function') {
                        const onScreenEnded = () => {
                            try { videoTrack.mediaStreamTrack.removeEventListener('ended', onScreenEnded); } catch (e) {}
                            if (_isStoppingScreenShare) return;
                            console.info('[VoiceInterop] Screen share MediaStreamTrack ended externally');
                            setScreenShareEnabled(false).catch(() => {});
                        };
                        videoTrack.mediaStreamTrack.addEventListener('ended', onScreenEnded);
                    }
                }

                const isAuto = !resolutionStr || resolutionStr === 'Auto';
                const isDetail = mode === 'detail';
                const initialScaleDown = computeScreenScaleDown(videoTrack, targetPreset);
                const degradationPref = (isAuto && !isDetail) ? 'balanced' : 'maintain-resolution';

                let publishOptions = { 
                    source: window.LivekitClient.Track.Source.ScreenShare,
                    simulcast: false,
                    videoCodec: 'h264',
                    degradationPreference: degradationPref,
                    screenShareEncoding: {
                        maxBitrate: targetPreset.maxBitrate,
                        maxFramerate: targetFps,
                        scaleResolutionDownBy: initialScaleDown
                    },
                    videoEncoding: {
                        maxBitrate: targetPreset.maxBitrate,
                        maxFramerate: targetFps
                    }
                };

                _lastScreenResolution = resolutionStr || 'Auto';
                _lastScreenFps = targetFps || 30;
                _lastScreenMode = mode || 'motion';

                // Publish with screenShareEncoding and enforce active capture/sender parameters.
                await currentRoom.localParticipant.publishTrack(videoTrack, publishOptions);
                await applyScreenShareEncoding(videoTrack, _lastScreenResolution, _lastScreenFps, _lastScreenMode);
                try { updatePipCallState({ isScreenSharing: true }); } catch (e) {}
                // Re-apply once first WGC/desktop frame dimensions settle (using latest active settings)
                setTimeout(() => {
                    if (currentRoom && videoTrack && globalScreenTracks.includes(videoTrack)) {
                        applyScreenShareEncoding(videoTrack, _lastScreenResolution, _lastScreenFps, _lastScreenMode).catch(() => {});
                    }
                }, 250);

                // Publish desktop audio if captured
                if (audioTrack) {
                    console.log('[Voice] Publishing desktop audio track:', audioTrack.sid);
                    await currentRoom.localParticipant.publishTrack(audioTrack, {
                        source: window.LivekitClient.Track.Source.ScreenShareAudio
                    });
                    if (dotnetHelper) {
                        dotnetHelper.invokeMethodAsync('OnScreenShareAudioActive', true).catch(() => {});
                    }
                } else {
                    if (dotnetHelper) {
                        dotnetHelper.invokeMethodAsync('OnScreenShareAudioActive', false).catch(() => {});
                        if (captureAudio) {
                            dotnetHelper.invokeMethodAsync('OnVoiceLog', 'No desktop audio captured. When sharing in browser, select "Entire Screen" and check "Share system audio" (or share a browser tab).', 'warning').catch(() => {});
                        }
                    }
                }
            } else {
                _isStoppingScreenShare = true;
                const localId = currentRoom.localParticipant?.identity;
                try {
                    const tracksToStop = new Set(globalScreenTracks);
                    globalScreenTracks = [];

                    const allPubs = getParticipantTrackPublications(currentRoom.localParticipant);
                    for (const p of allPubs) {
                        if (p.source === window.LivekitClient?.Track?.Source?.ScreenShare || p.source === window.LivekitClient?.Track?.Source?.ScreenShareAudio) {
                            const t = p.videoTrack || p.audioTrack || p.track;
                            if (t) tracksToStop.add(t);
                        }
                    }

                    for (const t of tracksToStop) {
                        try {
                            await currentRoom.localParticipant.unpublishTrack(t, true);
                        } catch (e) {
                            console.warn('[VoiceInterop] unpublishTrack screen share warning:', e);
                        } finally {
                            stopLivekitTrackCompletely(t);
                        }
                    }
                } finally {
                    _isStoppingScreenShare = false;
                    if (localId) {
                        try { unregisterPipVideoTrack(localId, 'screenshare'); } catch (e) {}
                        clearParticipantVideoDom(localId, 'screenshare');
                        if (dotnetHelper) {
                            dotnetHelper.invokeMethodAsync('OnVideoTrackStateChanged', localId, 'screenshare', false).catch(() => {});
                            dotnetHelper.invokeMethodAsync('OnScreenShareAudioActive', false).catch(() => {});
                        }
                    }
                    try { updatePipCallState({ isScreenSharing: false }); } catch (e) {}
                }
            }
        } catch (error) {
            console.error('[VoiceInterop] Error in setScreenShareEnabled:', error);
            throw error;
        }
    }
}

export async function setScreenShareAudioEnabled(enabled) {
    if (!currentRoom || !currentRoom.localParticipant) return false;
    let audioTrack = globalScreenTracks.find(t => t.kind === 'audio');
    if (audioTrack) {
        if (enabled) {
            if (audioTrack.mediaStreamTrack) audioTrack.mediaStreamTrack.enabled = true;
            let pubs = currentRoom.localParticipant.audioTrackPublications;
            let pubArr = typeof pubs?.values === 'function' ? Array.from(pubs.values()) : Object.values(pubs || {});
            let existingPub = pubArr.find(p => p.source === window.LivekitClient.Track.Source.ScreenShareAudio);
            if (!existingPub) {
                await currentRoom.localParticipant.publishTrack(audioTrack, {
                    source: window.LivekitClient.Track.Source.ScreenShareAudio
                });
            }
        } else {
            if (audioTrack.mediaStreamTrack) audioTrack.mediaStreamTrack.enabled = false;
            let pubs = currentRoom.localParticipant.audioTrackPublications;
            let pubArr = typeof pubs?.values === 'function' ? Array.from(pubs.values()) : Object.values(pubs || {});
            let existingPub = pubArr.find(p => p.source === window.LivekitClient.Track.Source.ScreenShareAudio);
            if (existingPub && existingPub.track) {
                await currentRoom.localParticipant.unpublishTrack(existingPub.track, false);
            }
        }
        return true;
    }
    return false;
}

export async function updateScreenShareSettings(resolutionStr = 'Auto', targetFps = 30, mode = 'motion') {
    _lastScreenResolution = resolutionStr || 'Auto';
    _lastScreenFps = targetFps || 30;
    _lastScreenMode = mode || 'motion';
    if (!currentRoom || !currentRoom.localParticipant) return;
    try {
        let exTracks = currentRoom.localParticipant.videoTrackPublications;
        if (!exTracks) return;
        let pubArr = typeof exTracks.values === 'function' ? Array.from(exTracks.values()) : Object.values(exTracks);
        let screenPub = pubArr.find(p => p.source === window.LivekitClient.Track.Source.ScreenShare);
        if (!screenPub || !screenPub.track) return;

        let track = screenPub.track;
        await applyScreenShareEncoding(track, _lastScreenResolution, _lastScreenFps, _lastScreenMode);
    } catch (err) {
        console.warn('[VoiceInterop] Error in updateScreenShareSettings:', err);
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
            } else if (!currentRoom) {
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
        // Never attach local participant audio tracks to playback <audio> elements (e.g. during reattachAllTracks)
        if (participant === currentRoom?.localParticipant) {
            return;
        }

        const processedOwner = getProcessedMicOwner(track, publication, participant);
        const localIdentity = currentRoom?.localParticipant?.identity;

        if (processedOwner) {
            if (processedOwner === localIdentity || _mutedRoommateUsers.has(processedOwner) || !_coLocatedProcessedUsers.has(processedOwner)) {
                if (typeof publication?.setSubscribed === 'function') {
                    try { publication.setSubscribed(false); } catch (e) {}
                }
                return;
            }
        } else if (!isBot && track.source !== window.LivekitClient.Track.Source.ScreenShareAudio) {
            if (_coLocatedProcessedUsers.has(participant?.identity) || _mutedRoommateUsers.has(participant?.identity)) {
                if (typeof publication?.setSubscribed === 'function') {
                    try { publication.setSubscribed(false); } catch (e) {}
                }
                return;
            }
        }

        const effectiveIdentity = processedOwner || participant?.identity;
        if (!effectiveIdentity || effectiveIdentity === localIdentity) {
            return;
        }

        // Universal fast-path for remote audio tracks (bot and human participants):
        // Audio has no visual component in the UI grid. Attach directly to the permanent
        // voice-audio-container without waiting for Blazor DOM slots or SignalR roundtrips!
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
        
        attachParticipantAudio(track, participant, element, effectiveIdentity);
        
        if (effectiveIdentity && track.mediaStreamTrack && (!isBot || Boolean(processedOwner)) && track.source !== window.LivekitClient.Track.Source.ScreenShareAudio) {
            registerRemoteVoiceTrack(effectiveIdentity, track.mediaStreamTrack);
        }
        return;
    }
    
    if (isVideo) {
        const isScreenShare = isScreenShareTrackOrPub(track, publication);
        const pipSource = isScreenShare ? 'screenshare' : 'camera';
        const effectiveIdentity = getEffectiveParticipantIdentity(participant);
        const localIdentity = currentRoom?.localParticipant?.identity;
        const isLocalParticipant = participant === currentRoom?.localParticipant
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

        if (dotnetHelper && effectiveIdentity) {
            dotnetHelper.invokeMethodAsync('EnsureParticipantSlot', effectiveIdentity).catch(() => {});
            dotnetHelper.invokeMethodAsync('OnVideoTrackStateChanged', effectiveIdentity, pipSource, true).catch(() => {});
        }

        // While in Android Native PiP or external OS/Desktop PiP, the PiP surface is the sole video sink;
        // stage <video> elements are re-attached automatically when exiting external PiP.
        if (isStageSuppressedByExternalPip()) {
            return;
        }

        waitForElement(targetId).then((targetEl) => {
            if (!currentRoom || isStageSuppressedByExternalPip()) {
                return;
            }
            if (track.mediaStreamTrack && track.mediaStreamTrack.readyState === 'ended') {
                clearParticipantVideoDom(effectiveIdentity, pipSource);
                return;
            }
            if (isLocalParticipant && !isScreenShare && !_localCameraDesired) {
                clearParticipantVideoDom(effectiveIdentity, 'camera');
                return;
            }
            
            // For video, targetEl is the native <video> tag strictly managed by Blazor.
            // We instruct LiveKit to attach the media stream directly to this stable DOM node.
            targetEl.style.opacity = '1';
            targetEl.style.pointerEvents = 'auto';
            targetEl.classList.add('has-stream');
            if (isLocalParticipant && !isScreenShare) {
                targetEl.style.transform = (_localCameraFacing === 'environment')
                    ? 'translateZ(0)'
                    : 'translateZ(0) scaleX(-1)';
            } else {
                targetEl.style.transform = 'translateZ(0)';
            }
            
            track.attach(targetEl);
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
                    } else if (dotnetHelper && isScreenShare) {
                        const localMaxEdge = Math.max(targetEl.videoWidth, targetEl.videoHeight);
                        const settings = typeof track.mediaStreamTrack.getSettings === 'function' ? track.mediaStreamTrack.getSettings() : {};
                        const localFps = Math.max(30, Math.round(settings?.frameRate || 60));
                        const screenMaxEdge = Math.max(localMaxEdge, getScreenTrackMaxSourceEdge(track));
                        dotnetHelper.invokeMethodAsync('OnLocalScreenDimensions', screenMaxEdge, localFps).catch(() => {});
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
            
            // Ensure it plays if the browser blocked autoplay
            if (targetEl.paused) {
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
    if (!dotnetHelper || !participant || !effectiveIdentity) return;
    if (effectiveIdentity === currentRoom?.localParticipant?.identity) return;
    
    let maxOverallDim = 0;
    let maxOverallFps = 30;
    let hasTracks = false;
    let missingDims = false;

    const matchingParticipants = [participant];
    const remoteMap = currentRoom?.remoteParticipants || currentRoom?.participants;
    if (remoteMap) {
        const remotes = typeof remoteMap.values === 'function' ? Array.from(remoteMap.values()) : Object.values(remoteMap);
        for (const p of remotes) {
            if (p && p !== participant && getEffectiveParticipantIdentity(p) === effectiveIdentity) {
                matchingParticipants.push(p);
            }
        }
    }
    
    for (const part of matchingParticipants) {
        let tracks = part.videoTrackPublications;
        if (!tracks) continue;
        let pubArr = typeof tracks.values === 'function' ? Array.from(tracks.values()) : Object.values(tracks);
        for (let p of pubArr) {
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
    
    try { dotnetHelper.invokeMethodAsync('OnParticipantVideoDimensions', effectiveIdentity, maxOverallDim, maxOverallFps); } catch(e) {}
}

function handleTrackSubscribed(track, publication, participant) {
    attachTrack(track, participant, publication);
    if (track.kind === window.LivekitClient.Track.Kind.Video) {
        updateParticipantQualityMax(participant);
        // Ensure remote screen shares are not throttled down to low quality by adaptiveStream in grid view
        if (publication?.source === window.LivekitClient.Track.Source.ScreenShare || track.source === window.LivekitClient.Track.Source.ScreenShare) {
            publication?.setVideoQuality?.(window.LivekitClient.VideoQuality.HIGH);
            publication?.setVideoDimensions?.({ width: 3840, height: 2160 });
        }
    } else if (track.kind === window.LivekitClient.Track.Kind.Audio) {
        if (track.source === window.LivekitClient.Track.Source.ScreenShareAudio || publication?.source === window.LivekitClient.Track.Source.ScreenShareAudio) {
            if (dotnetHelper && participant?.identity) {
                dotnetHelper.invokeMethodAsync('OnRemoteScreenAudioChanged', participant.identity, true).catch(() => {});
            }
        }
    }
}

function handleTrackUnsubscribed(track, publication, participant) {
    if (track) {
        track.detach().forEach(el => safeRemove(el));
    }
    const sid = track?.sid || publication?.trackSid;
    if (sid) {
        const orphaned = document.getElementById(`media-${sid}`);
        if (orphaned) safeRemove(orphaned);
    }
    const isScreenAudio = track?.source === window.LivekitClient.Track.Source.ScreenShareAudio || publication?.source === window.LivekitClient.Track.Source.ScreenShareAudio;
    if (participant && participant.identity && (track?.kind === window.LivekitClient.Track.Kind.Audio || publication?.kind === window.LivekitClient.Track.Kind.Audio)) {
        const localIdentity = currentRoom?.localParticipant?.identity;
        const effectiveIdentity = getProcessedMicOwner(track, publication, participant) || participant.identity;
        if (!isScreenAudio && effectiveIdentity && effectiveIdentity !== localIdentity) {
            unregisterRemoteVoiceTrack(effectiveIdentity, track?.mediaStreamTrack || null);
        }
        if (effectiveIdentity && effectiveIdentity !== localIdentity) {
            detachParticipantAudio(effectiveIdentity, sid);
        }
        if (isScreenAudio && dotnetHelper) {
            dotnetHelper.invokeMethodAsync('OnRemoteScreenAudioChanged', participant.identity, false).catch(() => {});
        }
    }
    
    // Explicit fallback to clear the Blazor-managed video tag, since LiveKit detaches tracks before firing this event
    if (participant && participant.identity) {
        if (!track || track.kind === window.LivekitClient.Track.Kind.Video || (publication && publication.kind === window.LivekitClient.Track.Kind.Video)) {
            const effectiveIdentity = getEffectiveParticipantIdentity(participant);
            const localIdentity = currentRoom?.localParticipant?.identity;
            const pipSource = isScreenShareTrackOrPub(track, publication) ? 'screenshare' : 'camera';
            const isLocalCompanionReconfig = Boolean(localIdentity)
                && effectiveIdentity === localIdentity
                && isCameraCompanionIdentity(participant?.identity)
                && _localCameraDesired
                && pipSource === 'camera';
            if (!isLocalCompanionReconfig) {
                unregisterPipVideoTrack(effectiveIdentity, pipSource);
                clearParticipantVideoDom(effectiveIdentity, pipSource);
                if (dotnetHelper && effectiveIdentity) {
                    dotnetHelper.invokeMethodAsync('OnVideoTrackStateChanged', effectiveIdentity, pipSource, false).catch(() => {});
                }
            }
        }
    }
    
    if (track && track.kind === window.LivekitClient.Track.Kind.Video) {
        updateParticipantQualityMax(participant);
    }
}

async function measureWebRtcServerDelayMs(track) {
    try {
        let rttMs = 0;
        const engineRtt = currentRoom?.engine?.client?.rtt;
        if (typeof engineRtt === 'number' && engineRtt > 0 && engineRtt < 2000) {
            rttMs = engineRtt;
        }

        const statsTarget = track?.sender
            || currentRoom?.engine?.pcManager?.publisher?.pc
            || currentRoom?.engine?.publisher?.pc;
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

function reportLocalAudioPipelineTelemetry(track) {
    if (!dotnetHelper || !track) return;
    try {
        let platformTag = 'web';
        if (isNativePlatform()) {
            platformTag = getNativePlatform() || 'mobile';
        } else if (typeof navigator !== 'undefined' && /Electron/i.test(navigator.userAgent || '')) {
            platformTag = 'electron';
        } else if (isAppleMobileDevice()) {
            platformTag = 'ios-web';
        } else if (typeof navigator !== 'undefined' && /Android/i.test(navigator.userAgent || '')) {
            platformTag = 'android-web';
        }

        const hasAiProcessor = Boolean(track.processor);
        const effectiveFilter = hasAiProcessor
            ? currentNoiseSuppression
            : (currentNoiseSuppression === 'none' ? 'none' : 'webrtc');

        let captureMs = 0;
        const mst = track.mediaStreamTrack;
        if (mst && typeof mst.getSettings === 'function') {
            const settings = mst.getSettings() || {};
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
            if (!dotnetHelper || !currentRoom) return;
            const serverDelayMs = await measureWebRtcServerDelayMs(track);
            dotnetHelper.invokeMethodAsync('OnLocalAudioPipelineTelemetry', effectiveFilter, totalEstimatedMs, platformTag, serverDelayMs).catch(() => {});
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
                if (_isLocalSoftwareMuted) {
                    unpublishAndStopAllLocalMicTracks().catch(() => {});
                    return;
                }
                bindLocalHardwareMuteMonitor(publication.track);
                const mediaStreamTrack = publication.track.processor?.processedTrack || publication.track.mediaStreamTrack;
                const rawTrack = getRawCaptureMediaStreamTrack(publication.track);
                if (participant && participant.identity && mediaStreamTrack) {
                    registerLocalMicVad(participant.identity, mediaStreamTrack, rawTrack);
                }
                reportLocalAudioPipelineTelemetry(publication.track);
            }
            return;
        }
        attachTrack(publication.track, participant, publication);
        
        if (dotnetHelper) {
            let w = 0, h = 0, fps = 30;
            
            // Robust check leveraging native capabilities (e.g. Discord method)
            if (publication.track.mediaStreamTrack && typeof publication.track.mediaStreamTrack.getCapabilities === 'function') {
                try {
                    let caps = publication.track.mediaStreamTrack.getCapabilities();
                    if (caps.width && caps.width.max) w = caps.width.max;
                    if (caps.height && caps.height.max) h = caps.height.max;
                    if (caps.frameRate && caps.frameRate.max) fps = caps.frameRate.max;
                } catch(e) {}
            }
            
            // Fallback for Safari/Firefox
            if ((w === 0 || h === 0) && publication.track.mediaStreamTrack) {
                let settings = publication.track.mediaStreamTrack.getSettings();
                w = settings.width || 0;
                h = settings.height || 0;
            }
            if (publication.track.mediaStreamTrack && typeof publication.track.mediaStreamTrack.getSettings === 'function') {
                let settings = publication.track.mediaStreamTrack.getSettings();
                if (settings.frameRate) fps = Math.max(fps, Math.round(settings.frameRate));
            }
            
            let maxDim = Math.max(w, h);
            if (publication.source === window.LivekitClient.Track.Source.ScreenShare) {
                maxDim = Math.max(maxDim, getScreenTrackMaxSourceEdge(publication.track));
                fps = Math.max(fps, 60);
            }
            if (maxDim > 0) {
                if (publication.source === window.LivekitClient.Track.Source.ScreenShare) {
                    try { dotnetHelper.invokeMethodAsync('OnLocalScreenDimensions', maxDim, fps); } catch(e) {}
                } else {
                    try { dotnetHelper.invokeMethodAsync('OnLocalVideoDimensions', maxDim, fps); } catch(e) {}
                }
            }
        }
    }
}

// --- Real-time Voice Activity Detection (VAD) Engine (Discord-Grade Responsiveness) ---
// Local capture VAD (_localMicVadEntry) is strictly isolated from remote playback VAD (_trackedAudioStreams)
// so Physical Room Isolation bot track suppression (processed-mic:<localIdentity>) can never tear down local VAD.
const _trackedAudioStreams = new Map(); // remoteIdentity -> { source, analyser, dataArray, lastSpokeTime, isSpeaking, track, noiseFloor, consecutiveActiveFrames }
let _localMicVadEntry = null; // { identity, source, analyser, dataArray, lastSpokeTime, isSpeaking, track, rawTrack, noiseFloor, consecutiveActiveFrames }
let _vadIntervalId = null;
let _isLocalSoftwareMuted = false;
let _isLocalHardwareMuted = false;
const VAD_HANGOVER_MS = 280; // Release time to bridge syllables without lingering or clipping word endings
const VAD_ATTACK_FRAMES = 2; // ~60ms attack buffer to reject single-frame transients (clicks, taps, breath pops)
let _voiceSensitivity = 50.0; // 0 (strict gate, higher threshold) to 100 (sensitive gate, lower threshold)
let _lastNotifiedSpeakers = "";
let _speakerSyncDebounce = null;

function isEffectiveLocalMicMuted() {
    return _isLocalSoftwareMuted || _isLocalHardwareMuted;
}

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
        const prevId = _localMicVadEntry.identity;
        try { _localMicVadEntry.source.disconnect(); } catch (e) {}
        _localMicVadEntry = null;
        if (prevId) {
            updateParticipantSpeakingDOM(prevId, false);
        }
    }
    if (!_localMicVadEntry && _trackedAudioStreams.size === 0) {
        stopVADLoop();
    }
    if (shouldNotify) {
        notifyActiveSpeakersToDotnet(null, true);
    }
}

function registerRemoteVoiceTrack(identity, mediaStreamTrack) {
    if (!identity || !mediaStreamTrack) return;
    const localIdentity = currentRoom?.localParticipant?.identity;
    if (identity === localIdentity) return;

    unregisterRemoteVoiceTrack(identity);

    try {
        const entry = createVadAnalyserNodeEntry(identity, mediaStreamTrack, mediaStreamTrack);
        if (!entry) return;
        _trackedAudioStreams.set(identity, entry);
        startVADLoop();
    } catch (err) {
        console.warn('[Voice:VAD] Failed to register remote track for', identity, err);
    }
}

function unregisterRemoteVoiceTrack(identity, expectedMediaStreamTrack = null) {
    if (!identity) return;
    const localIdentity = currentRoom?.localParticipant?.identity;
    if (identity === localIdentity) return;

    const item = _trackedAudioStreams.get(identity);
    if (!item) return;

    if (expectedMediaStreamTrack && item.track && item.track !== expectedMediaStreamTrack) {
        return;
    }

    try {
        item.source.disconnect();
    } catch (e) {}
    _trackedAudioStreams.delete(identity);
    updateParticipantSpeakingDOM(identity, false);

    if (!_localMicVadEntry && _trackedAudioStreams.size === 0) {
        stopVADLoop();
    }
    notifyActiveSpeakersToDotnet(null, true);
}

function clearAllVoiceTracks() {
    if (_localMicVadEntry) {
        try { _localMicVadEntry.source.disconnect(); } catch (e) {}
        if (_localMicVadEntry.identity) {
            updateParticipantSpeakingDOM(_localMicVadEntry.identity, false);
        }
        _localMicVadEntry = null;
    }
    _trackedAudioStreams.forEach((item, id) => {
        try { item.source.disconnect(); } catch (e) {}
        updateParticipantSpeakingDOM(id, false);
    });
    _trackedAudioStreams.clear();
    stopVADLoop();
    _lastNotifiedSpeakers = "";
    if (_speakerSyncDebounce) {
        clearTimeout(_speakerSyncDebounce);
        _speakerSyncDebounce = null;
    }
}

function updateParticipantSpeakingDOM(identity, isSpeaking) {
    const el = document.getElementById(`participant-rect-${identity}`);
    if (el) {
        if (isSpeaking && !el.classList.contains('is-speaking')) {
            el.classList.add('is-speaking');
        } else if (!isSpeaking && el.classList.contains('is-speaking')) {
            el.classList.remove('is-speaking');
        }
    }
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

function evaluateVadEntry(item, identity, isLocal, now, sensMult) {
    if (isLocal) {
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
                updateParticipantSpeakingDOM(identity, false);
                return true;
            }
            return false;
        }
    } else if (item.track && (!item.track.enabled || item.track.readyState === 'ended' || item.track.muted)) {
        if (item.isSpeaking) {
            item.isSpeaking = false;
            item.consecutiveActiveFrames = 0;
            updateParticipantSpeakingDOM(identity, false);
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
                updateParticipantSpeakingDOM(identity, true);
                return true;
            }
        }
    } else {
        item.consecutiveActiveFrames = 0;
        if (item.isSpeaking && (now - item.lastSpokeTime >= VAD_HANGOVER_MS)) {
            item.isSpeaking = false;
            updateParticipantSpeakingDOM(identity, false);
            return true;
        }
    }

    return false;
}

function vadLoopTick() {
    const now = Date.now();
    let stateChanged = false;

    // Sensitivity multiplier: at 50 -> 1.0; at 0 -> 1.5 (stricter); at 100 -> 0.5 (more sensitive)
    const sensMult = 1.0 + (50 - _voiceSensitivity) * 0.01;

    if (_localMicVadEntry && _localMicVadEntry.identity) {
        if (evaluateVadEntry(_localMicVadEntry, _localMicVadEntry.identity, true, now, sensMult)) {
            stateChanged = true;
        }
    }

    _trackedAudioStreams.forEach((item, identity) => {
        if (evaluateVadEntry(item, identity, false, now, sensMult)) {
            stateChanged = true;
        }
    });

    if (stateChanged) {
        notifyActiveSpeakersToDotnet();
    }
}

function getCurrentlySpeakingIdentities(sfuSpeakers = []) {
    const localIdentity = currentRoom?.localParticipant?.identity;
    const isLocalMuted = isEffectiveLocalMicMuted() || !currentRoom?.localParticipant || !_localMicVadEntry;

    // SFU speakers should NEVER drive the local participant's speaking state
    const remoteSfu = sfuSpeakers.filter(id => id !== localIdentity && !isBotIdentity(id));
    const activeSet = new Set(remoteSfu);

    _trackedAudioStreams.forEach((item, id) => {
        if (item.isSpeaking) {
            activeSet.add(id);
        } else {
            activeSet.delete(id);
        }
    });

    if (localIdentity) {
        if (!isLocalMuted && _localMicVadEntry?.isSpeaking) {
            activeSet.add(localIdentity);
        } else {
            activeSet.delete(localIdentity);
        }
    }
    return Array.from(activeSet);
}

function notifyActiveSpeakersToDotnet(explicitList = null, forceImmediate = false) {
    if (!dotnetHelper) return;

    const sendUpdate = (list) => {
        const currentActive = list || getCurrentlySpeakingIdentities();
        updatePipSpeakingParticipants(currentActive);
        const currentSorted = [...currentActive].sort().join(',');
        if (currentSorted !== _lastNotifiedSpeakers) {
            _lastNotifiedSpeakers = currentSorted;
            try {
                dotnetHelper.invokeMethodAsync('OnActiveSpeakersChanged', currentActive);
            } catch(e) {}
        }
    };

    if (forceImmediate) {
        if (_speakerSyncDebounce) {
            clearTimeout(_speakerSyncDebounce);
            _speakerSyncDebounce = null;
        }
        sendUpdate(explicitList);
        return;
    }

    if (_speakerSyncDebounce) return;

    _speakerSyncDebounce = setTimeout(() => {
        _speakerSyncDebounce = null;
        sendUpdate(explicitList);
    }, 80);
}

function handleActiveSpeakers(speakers) {
    if (!dotnetHelper) return;
    
    const localIdentity = currentRoom?.localParticipant?.identity;
    const isLocalMuted = isEffectiveLocalMicMuted();

    // SFU speakers should NEVER drive the local participant's speaking state, and bots should never count as human speakers
    const remoteSpeakers = speakers.filter(s => s.identity !== localIdentity && !isBotIdentity(s.identity));
    const activeSidsList = remoteSpeakers.map(s => s.identity);

    if (localIdentity && isLocalMuted) {
        updateParticipantSpeakingDOM(localIdentity, false);
    }

    // For remote participants not tracked by local playback VAD, update DOM from SFU
    activeSidsList.forEach(identity => {
        if (!_trackedAudioStreams.has(identity)) {
            updateParticipantSpeakingDOM(identity, true);
        }
    });

    document.querySelectorAll('.participant-rect.is-speaking').forEach(el => {
        if (el.id && el.id.startsWith('participant-rect-')) {
            const sid = el.id.substring(17);
            if (sid === localIdentity) {
                if (isLocalMuted || !_localMicVadEntry?.isSpeaking) {
                    updateParticipantSpeakingDOM(sid, false);
                }
            } else if (!_trackedAudioStreams.has(sid) && !activeSidsList.includes(sid)) {
                updateParticipantSpeakingDOM(sid, false);
            }
        }
    });

    notifyActiveSpeakersToDotnet(getCurrentlySpeakingIdentities(activeSidsList));
}

function handleDisconnect() {
    console.log('[Voice] Disconnected');
    cleanupLocalHardwareMuteMonitor();
    _activeLocalAudioProcessor = null;
    _localCameraDesired = false;
    const roomToClean = currentRoom;
    currentRoom = null;
    stopAllLocalMediaTracks(roomToClean);
    notifyHostVoiceState(false, false);
    _isLocalSoftwareMuted = false;
    _isLocalHardwareMuted = false;
    _mutedRemoteScreenAudio.clear();
    _coLocatedProcessedUsers.clear();
    _mutedRoommateUsers.clear();
    clearAllVoiceTracks();
    clearAllParticipantAudio();
    releaseNativeCallResources().catch(() => {});
    
    // Only detach LiveKit-created media elements (tracks we attached via JS).
    // Do NOT clear container.innerHTML — those parent containers are owned by Blazor's
    // render tree, and removing them directly causes "Cannot read properties of null
    // (reading 'removeChild')" when Blazor tries to diff-update the DOM.
    if (roomToClean) {
        detachAllRoomMedia(roomToClean);
    }
    
    if (dotnetHelper) {
        try {
            dotnetHelper.invokeMethodAsync('OnRoomDisconnected');
        } catch(e) {}
    }
}

function handleLocalUnpublished(publication, participant) {
    if (publication.track) {
        publication.track.detach().forEach(el => safeRemove(el));
    }
    const sid = publication.track?.sid || publication?.trackSid;
    if (sid) {
        const orphaned = document.getElementById(`media-${sid}`);
        if (orphaned) safeRemove(orphaned);
    }
    
    // Aggressive fallback to ensure video tags are wiped so CSS :has(video) unhides the avatar
    if (publication.kind === window.LivekitClient.Track.Kind.Video || (publication.track && publication.track.kind === window.LivekitClient.Track.Kind.Video)) {
        if (participant && participant.identity) {
            const pipSource = isScreenShareTrackOrPub(publication.track, publication) ? 'screenshare' : 'camera';
            unregisterPipVideoTrack(participant.identity, pipSource);
            clearParticipantVideoDom(participant.identity, pipSource);
            if (dotnetHelper) {
                dotnetHelper.invokeMethodAsync('OnVideoTrackStateChanged', participant.identity, pipSource, false).catch(() => {});
            }
        }
    }

    const isAudio = publication.kind === window.LivekitClient.Track.Kind.Audio || publication.track?.kind === window.LivekitClient.Track.Kind.Audio;
    const isMicAudio = isAudio && publication.source === window.LivekitClient.Track.Source.Microphone;

    if (participant && participant.identity && isMicAudio) {
        cleanupLocalHardwareMuteMonitor();
        unregisterLocalMicVad();
    }

    // Fired when the user mutes themselves or stops video (suppressed during live noise-filter swap)
    if (dotnetHelper && !_suppressNextUnpublishMuteNotify) {
        try {
            if (isMicAudio) {
                dotnetHelper.invokeMethodAsync('OnLocalMuted', true);
            } else if (publication.source === window.LivekitClient.Track.Source.ScreenShareAudio) {
                dotnetHelper.invokeMethodAsync('OnScreenShareAudioActive', false);
            }
        } catch(e) {}
    }
}

function isHumanMicrophonePublication(publication, participant) {
    if (!participant || !participant.identity || isBotIdentity(participant.identity)) {
        return false;
    }
    const Kind = window.LivekitClient?.Track?.Kind;
    const Source = window.LivekitClient?.Track?.Source;
    const isAudio = publication?.kind === Kind?.Audio || publication?.track?.kind === Kind?.Audio;
    if (!isAudio) return false;

    const src = publication?.source || publication?.track?.source;
    if (Source && src === Source.ScreenShareAudio) {
        return false;
    }
    return !Source || !src || src === Source.Microphone;
}

function handleTrackMuted(publication, participant) {
    if (publication?.track) {
        publication.track.detach().forEach(el => safeRemove(el));
    }
    const sid = publication?.track?.sid || publication?.trackSid;
    if (sid) {
        const orphaned = document.getElementById(`media-${sid}`);
        if (orphaned) safeRemove(orphaned);
    }

    if (participant !== currentRoom?.localParticipant && isHumanMicrophonePublication(publication, participant)) {
        const item = _trackedAudioStreams.get(participant.identity);
        if (item) {
            item.isSpeaking = false;
            item.consecutiveActiveFrames = 0;
            updateParticipantSpeakingDOM(participant.identity, false);
        }
        if (dotnetHelper) {
            try {
                dotnetHelper.invokeMethodAsync('OnParticipantMuteChanged', participant.identity, true);
            } catch (e) {}
        }
    }

    // Fallback: forcefully clear video element if it's a video track muting
    if (publication?.kind === window.LivekitClient?.Track?.Kind?.Video || publication?.track?.kind === window.LivekitClient?.Track?.Kind?.Video) {
        if (participant && participant.identity) {
            const effectiveIdentity = getEffectiveParticipantIdentity(participant);
            const pipSource = isScreenShareTrackOrPub(publication?.track, publication) ? 'screenshare' : 'camera';
            unregisterPipVideoTrack(effectiveIdentity, pipSource);
            clearParticipantVideoDom(effectiveIdentity, pipSource);
            if (dotnetHelper && effectiveIdentity) {
                dotnetHelper.invokeMethodAsync('OnVideoTrackStateChanged', effectiveIdentity, pipSource, false).catch(() => {});
            }
        }
    }
}

function handleTrackUnmuted(publication, participant) {
    // CRITICAL: Notify OnParticipantMuteChanged(..., false) OUTSIDE `if (publication.track)`!
    // During Physical Room Isolation, remote clients are unsubscribed from a co-located peer's raw mic
    // (`publication.track == null`) while receiving `processed-mic:<userId>`. Unblocking this ensures
    // lowering a hardware boom-mic immediately clears the remote mute icon.
    if (participant !== currentRoom?.localParticipant && isHumanMicrophonePublication(publication, participant)) {
        if (dotnetHelper) {
            try {
                dotnetHelper.invokeMethodAsync('OnParticipantMuteChanged', participant.identity, false);
            } catch (e) {}
        }
    }

    if (publication?.track) {
        // Never attach local AUDIO to speakers
        if (participant === currentRoom?.localParticipant && publication.track.kind === window.LivekitClient.Track.Kind.Audio) {
            if (publication.source !== window.LivekitClient?.Track?.Source?.ScreenShareAudio) {
                bindLocalHardwareMuteMonitor(publication.track);
                const mediaStreamTrack = publication.track.processor?.processedTrack || publication.track.mediaStreamTrack;
                const rawTrack = getRawCaptureMediaStreamTrack(publication.track);
                if (participant.identity && mediaStreamTrack) {
                    registerLocalMicVad(participant.identity, mediaStreamTrack, rawTrack);
                }
            }
            return;
        }
        attachTrack(publication.track, participant, publication);
    }
}

function handleParticipantConnected(participant) {
    if (isSyntheticIdentity(participant?.identity)) return;
    if (dotnetHelper) {
        try {
            dotnetHelper.invokeMethodAsync('OnParticipantConnected', participant.identity);
        } catch(e) {}
    }
}

function handleParticipantDisconnected(participant) {
    if (isCameraCompanionIdentity(participant?.identity)) {
        const effectiveIdentity = getEffectiveParticipantIdentity(participant);
        if (effectiveIdentity) {
            unregisterPipVideoTrack(effectiveIdentity, 'camera');
            clearParticipantVideoDom(effectiveIdentity, 'camera');
            if (dotnetHelper) {
                dotnetHelper.invokeMethodAsync('OnVideoTrackStateChanged', effectiveIdentity, 'camera', false).catch(() => {});
            }
        }
        return;
    }
    if (participant && participant.identity) {
        unregisterRemoteVoiceTrack(participant.identity);
        detachParticipantAudio(participant.identity);
        unregisterPipVideoTrack(participant.identity);
        clearParticipantVideoDom(participant.identity);
        _mutedRemoteScreenAudio.delete(participant.identity);
        if (dotnetHelper) {
            try {
                dotnetHelper.invokeMethodAsync('OnRemoteScreenAudioChanged', participant.identity, false);
            } catch(e) {}
        }
    }
    if (isSyntheticIdentity(participant?.identity)) return;
    if (dotnetHelper) {
        try {
             dotnetHelper.invokeMethodAsync('OnParticipantDisconnected', participant.identity);
        } catch(e) {}
    }
}

// Re-attach all active tracks to their DOM containers.
// Called when the Chat component remounts (e.g. after navigating to another channel and back)
// so that existing LiveKit tracks are connected to the freshly created DOM elements.
export async function reattachAllTracks() {
    if (!currentRoom) return;

    if (currentRoom.localParticipant) {
        const pubs = getParticipantTrackPublications(currentRoom.localParticipant);
        pubs.forEach(pub => {
            if (pub.track) attachTrack(pub.track, currentRoom.localParticipant, pub);
        });
    }

    const remoteMap = currentRoom.remoteParticipants || currentRoom.participants;
    if (remoteMap) {
        let participants = typeof remoteMap.values === 'function'
            ? Array.from(remoteMap.values())
            : Object.values(remoteMap);
        participants.forEach(p => {
            const pubs = getParticipantTrackPublications(p);
            pubs.forEach(pub => {
                if (pub.track) attachTrack(pub.track, p, pub);
            });
        });
    }
}

export async function setRemoteVideoQuality(participantIdentity, qualityStr) {
    if (!currentRoom || !participantIdentity) return;

    const matchingParticipants = [];
    if (currentRoom.remoteParticipants) {
        const participantsArr = typeof currentRoom.remoteParticipants.values === 'function'
            ? Array.from(currentRoom.remoteParticipants.values())
            : Object.values(currentRoom.remoteParticipants);
        for (const p of participantsArr) {
            if (p && getEffectiveParticipantIdentity(p) === participantIdentity) {
                matchingParticipants.push(p);
            }
        }
    }

    if (matchingParticipants.length === 0) return;

    let dims = null;
    if (qualityStr === '2160p') dims = { width: 3840, height: 2160 };
    else if (qualityStr === '1440p') dims = { width: 2560, height: 1440 };
    else if (qualityStr === '1080p') dims = { width: 1920, height: 1080 };
    else if (qualityStr === '720p') dims = { width: 1280, height: 720 };
    else if (qualityStr === '360p') dims = { width: 640, height: 360 };

    for (const participant of matchingParticipants) {
        let getPublications = typeof participant.videoTrackPublications?.values === 'function'
            ? Array.from(participant.videoTrackPublications.values())
            : (participant.videoTrackPublications ? Object.values(participant.videoTrackPublications) : []);

        if (getPublications.length === 0 && participant.tracks) {
            let allPubs = typeof participant.tracks.values === 'function'
                ? Array.from(participant.tracks.values())
                : Object.values(participant.tracks);
            getPublications = allPubs.filter(p => p.kind === window.LivekitClient.Track.Kind.Video);
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

export async function isIOSDevice() {
    if (isNativePlatform()) {
        return getNativePlatform() === 'ios';
    }
    return false;
}

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
