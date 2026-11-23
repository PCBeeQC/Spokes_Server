// voiceNativeMobileAdapter.js — Unified Android & iOS SpokesNativeVoice Capacitor adapter
// and scoped stage / In-App PiP / OS PiP hardware video slot synchronizer.

import {
    voiceState,
    PROCESSED_MIC_PREFIX,
    isSyntheticIdentity,
    isBotIdentity,
    getEffectiveParticipantIdentity,
    getNativePlatform,
    getNativeVoicePlugin,
    getNativeVoiceTelemetryPlatformTag,
    getAudioSessionPlugin,
    resetMobileCameraSelectionForNewCall,
    isEffectiveLocalMicMuted,
    reportLocalSpeakingState,
    resetLocalSpeakingState,
    invokeDotNetSafe,
    publishVoiceRoomSnapshot,
    scheduleRoomSnapshotSync
} from './voiceState.js';
import {
    configureVoicePip,
    registerPipVideoTrack,
    unregisterPipVideoTrack,
    updatePipCallState,
    syncInAppPipElements,
    isNativeMobilePipActive,
    isStageSuppressedByExternalPip
} from '../voicePip.js';

let _stageSlotObserver = null;
let _pipSlotObserver = null;
let _structuralMountObserver = null;
let _observedStageEl = null;
let _observedPipEl = null;
let _resizeObserver = null;
let _observedResizeTargets = new Set();
let _resizeHandler = null;
let _scrollHandler = null;
let _layoutRaf = 0;

let _callbacks = {
    reportConnectingStage: () => {},
    notifyHostVoiceState: () => {},
    releaseNativeCallResources: async () => {},
    syncIosAudioSessionVideoMode: async () => {},
    updateAudioDevices: async () => {},
    refreshParticipantFullscreenPrimaryTarget: () => {},
    clampCameraResolutionForCapabilities: (res, fps) => ({ resolution: res, fps }),
    getCustomPreset: () => ({ width: 1920, height: 1080, maxBitrate: 8000000 })
};

export function configureNativeMobileCallbacks(callbacks = {}) {
    _callbacks = { ..._callbacks, ...callbacks };
}

export function estimateNativeMobileCaptureMs(filterType) {
    let captureMs = 25.0;
    if (voiceState.activeNativeSpeakerId === 'bluetooth') {
        captureMs += 150.0;
    }
    const f = String(filterType || 'rnnoise').toLowerCase();
    const lookaheadMs = (f === 'rnnoise' || f === 'deepfilternet' || f === 'webrtc') ? 10.0 : 0.0;
    return Math.round((captureMs + lookaheadMs) * 10) / 10;
}

export function setNativeMobileVideoTrackActive(identity, source, active, width = 0, height = 0) {
    if (!identity) return;
    const pipSource = source === 'screen' || source === 'screenshare' ? 'screenshare' : 'camera';
    const isLocal = identity === voiceState.nativeLocalIdentity;
    if (active) {
        let set = voiceState.nativeVideoTracks.get(identity);
        if (!set) {
            set = new Set();
            voiceState.nativeVideoTracks.set(identity, set);
        }
        set.add(pipSource);
        if (width > 0 && height > 0) {
            voiceState.nativeVideoDims.set(`${identity}:${pipSource}`, { width, height });
        }
        const dims = voiceState.nativeVideoDims.get(`${identity}:${pipSource}`) || { width: 0, height: 0 };
        try {
            registerPipVideoTrack(identity, pipSource, null, isLocal, width || dims.width || 0, height || dims.height || 0, null, {
                isNativeTrack: true,
                facingMode: isLocal ? (voiceState.localCameraFacing || 'user') : ''
            });
        } catch {}
    } else {
        const set = voiceState.nativeVideoTracks.get(identity);
        if (set) {
            set.delete(pipSource);
            if (set.size === 0) voiceState.nativeVideoTracks.delete(identity);
        }
        voiceState.nativeVideoDims.delete(`${identity}:${pipSource}`);
        voiceState.nativeVideoTrackSids.delete(`${identity}:${pipSource}`);
        const rectEl = typeof document !== 'undefined' ? document.getElementById(`participant-rect-${identity}`) : null;
        if (rectEl && !voiceState.nativeVideoTracks.has(identity)) {
            rectEl.classList.remove('native-video-active');
            rectEl.style.removeProperty('--tile-video-aspect');
        }
        try {
            unregisterPipVideoTrack(identity, pipSource);
        } catch {}
    }
    if (voiceState.fullscreenParticipantId === identity && voiceState.fullscreenGestureController) {
        _callbacks.refreshParticipantFullscreenPrimaryTarget();
    }
    scheduleNativeMobileVideoSlotsSync();
}

function getComputedBorder(style, side) {
    const val = parseFloat(style[`border${side}Width`] || '0');
    return Number.isFinite(val) ? val : 0;
}

function measureBorderBox(el) {
    const r = el.getBoundingClientRect();
    const cs = typeof getComputedStyle === 'function' ? getComputedStyle(el) : {};
    const bL = getComputedBorder(cs, 'Left');
    const bT = getComputedBorder(cs, 'Top');
    const bR = getComputedBorder(cs, 'Right');
    const bB = getComputedBorder(cs, 'Bottom');
    const rawRadius = parseFloat(cs.borderTopLeftRadius || '0');
    const cornerRadius = Number.isFinite(rawRadius) && rawRadius > 0 ? Math.round(rawRadius) : 0;
    return {
        x: r.left + bL,
        y: r.top + bT,
        width: Math.max(0, r.width - bL - bR),
        height: Math.max(0, r.height - bT - bB),
        cornerRadius
    };
}

function collectNativeMobilePipSlots() {
    if (typeof document === 'undefined') return [];
    const overlay = document.getElementById('spokes-voice-pip-overlay');
    if (!overlay) {
        return [];
    }
    const inNativePip = isNativeMobilePipActive();
    // Never mutate Blazor-owned classes on #spokes-voice-pip-overlay; rely on html.spokes-native-pip CSS for 0ms OS PiP.
    if (!inNativePip && (overlay.classList.contains('is-stage-hidden') || overlay.classList.contains('is-minimized-pill'))) {
        return [];
    }
    const slotDefs = [
        { id: 'spokes-pip-primary-video', cover: false, zOrder: 0 },
        { id: 'spokes-pip-presenter-cam-video', cover: false, zOrder: 5 },
        { id: 'spokes-pip-self-video', cover: false, zOrder: 10 }
    ];
    if (voiceState.nativeVideoTracks.size > 0 && slotDefs.every(d => !document.getElementById(d.id)?.classList?.contains('has-stream'))) {
        try { syncInAppPipElements(); } catch {}
    }
    const slots = [];
    for (const def of slotDefs) {
        const el = document.getElementById(def.id);
        if (!el || !el.classList.contains('has-stream')) continue;
        const identity = el.dataset?.nativeIdentity || '';
        const pipSource = el.dataset?.nativeSource === 'screenshare' ? 'screenshare' : 'camera';
        if (!identity || !voiceState.nativeVideoTracks.get(identity)?.has(pipSource)) continue;
        const box = measureBorderBox(el);
        if (box.width <= 2 || box.height <= 2) continue;
        const nativeSource = pipSource === 'screenshare' ? 'screen' : 'camera';
        slots.push({
            key: `pip:${identity}:${nativeSource}`,
            source: nativeSource,
            identity,
            x: Math.round(box.x),
            y: Math.round(box.y),
            width: Math.round(box.width),
            height: Math.round(box.height),
            cornerRadius: box.cornerRadius,
            mirror: identity === voiceState.nativeLocalIdentity && nativeSource === 'camera' && voiceState.localCameraFacing !== 'environment',
            cover: def.cover,
            visible: true,
            zOrder: def.zOrder
        });
    }
    return slots;
}

function pushNativeMobileSuppressedSlots(nativeVoice, forceSend = false) {
    document.documentElement?.classList?.remove('spokes-native-video-underlay');
    const sig = 'suppressed';
    if (forceSend || voiceState.nativeLastSlotSignature !== sig) {
        voiceState.nativeLastSlotSignature = sig;
        nativeVoice.updateVideoSlots({
            slots: [],
            viewportWidth: window.innerWidth || 0,
            viewportHeight: window.innerHeight || 0,
            underlaid: false,
            suppressed: true
        }).catch(() => {});
    }
}

export function syncNativeMobileVideoSlots(forceSend = false) {
    if (!voiceState.usingNativeMobileVoice || typeof document === 'undefined') return;
    const nativeVoice = getNativeVoicePlugin();
    if (!nativeVoice || typeof nativeVoice.updateVideoSlots !== 'function') return;

    const inNativePip = isNativeMobilePipActive();
    if (inNativePip) {
        const pipSlots = collectNativeMobilePipSlots();
        if (pipSlots.length === 0) {
            pushNativeMobileSuppressedSlots(nativeVoice, forceSend);
            return;
        }
        document.documentElement?.classList?.add('spokes-native-video-underlay');
        const vpW = window.innerWidth || 0;
        const vpH = window.innerHeight || 0;
        const signature = `pip|${vpW}x${vpH}|` + pipSlots.map(s =>
            `${s.key}:${s.x},${s.y},${s.width},${s.height},${s.cornerRadius || 0},${s.mirror},${s.cover},${s.visible},${s.zOrder}`
        ).join(';');
        if (forceSend || signature !== voiceState.nativeLastSlotSignature) {
            voiceState.nativeLastSlotSignature = signature;
            nativeVoice.updateVideoSlots({
                slots: pipSlots,
                viewportWidth: vpW,
                viewportHeight: vpH,
                underlaid: true,
                suppressed: false
            }).catch(() => {});
        }
        return;
    }

    const videoArea = document.querySelector('.voice-video-area');
    const container = document.getElementById('voice-video-container');
    const videoAreaDisplay = videoArea && typeof getComputedStyle === 'function' ? getComputedStyle(videoArea).display : '';
    const isSuppressed = isStageSuppressedByExternalPip()
        || !videoArea
        || !container
        || videoAreaDisplay === 'none'
        || container.getBoundingClientRect().width <= 1
        || container.getBoundingClientRect().height <= 1;

    document.querySelectorAll('.participant-rect.native-video-active').forEach(el => {
        const sid = el.id?.startsWith('participant-rect-') ? el.id.substring(17) : '';
        if (!sid || !voiceState.nativeVideoTracks.has(sid)) {
            el.classList.remove('native-video-active');
            el.style.removeProperty('--tile-video-aspect');
        }
    });

    if (isSuppressed) {
        if (!isStageSuppressedByExternalPip()) {
            const inAppPipSlots = collectNativeMobilePipSlots();
            if (inAppPipSlots.length > 0) {
                document.documentElement?.classList?.remove('spokes-native-video-underlay');
                const vpW = window.innerWidth || 0;
                const vpH = window.innerHeight || 0;
                const signature = `inapp-pip|${vpW}x${vpH}|` + inAppPipSlots.map(s =>
                    `${s.key}:${s.x},${s.y},${s.width},${s.height},${s.cornerRadius || 0},${s.mirror},${s.cover},${s.visible},${s.zOrder}`
                ).join(';');
                if (forceSend || signature !== voiceState.nativeLastSlotSignature) {
                    voiceState.nativeLastSlotSignature = signature;
                    nativeVoice.updateVideoSlots({
                        slots: inAppPipSlots,
                        viewportWidth: vpW,
                        viewportHeight: vpH,
                        underlaid: false,
                        suppressed: false
                    }).catch(() => {});
                }
                return;
            }
        }
        pushNativeMobileSuppressedSlots(nativeVoice, forceSend);
        return;
    }

    const containerRect = container.getBoundingClientRect();
    const slots = [];
    let anyVisible = false;
    const fsRectEl = voiceState.fullscreenParticipantId
        ? document.getElementById(`participant-rect-${voiceState.fullscreenParticipantId}`)
        : null;
    const activeFullscreenId = (fsRectEl && fsRectEl.classList.contains('is-fullscreen-stage'))
        ? voiceState.fullscreenParticipantId
        : '';

    voiceState.nativeVideoTracks.forEach((sources, identity) => {
        const rectEl = document.getElementById(`participant-rect-${identity}`);
        if (!rectEl) return;

        if (!rectEl.classList.contains('native-video-active')) {
            rectEl.classList.add('native-video-active');
        }
        const hasScreen = sources.has('screenshare');
        const hasCamera = sources.has('camera');
        const preferredSrc = hasScreen ? 'screenshare' : 'camera';
        const dims = voiceState.nativeVideoDims.get(`${identity}:${preferredSrc}`)
            || voiceState.nativeVideoDims.get(`${identity}:camera`);
        if (dims && dims.width > 0 && dims.height > 0) {
            const aspect = dims.width / dims.height;
            if (Number.isFinite(aspect) && aspect > 0.1 && aspect < 10) {
                const nextAspect = aspect.toFixed(4);
                if (rectEl.style.getPropertyValue('--tile-video-aspect') !== nextAspect) {
                    rectEl.style.setProperty('--tile-video-aspect', nextAspect);
                }
            }
        } else if (rectEl.style.getPropertyValue('--tile-video-aspect')) {
            rectEl.style.removeProperty('--tile-video-aspect');
        }

        if (activeFullscreenId && identity !== activeFullscreenId) {
            return;
        }

        const isFullscreenTile = Boolean(activeFullscreenId && identity === activeFullscreenId);
        const box = measureBorderBox(rectEl);
        const tileX = box.x;
        const tileY = box.y;
        const tileW = box.width;
        const tileH = box.height;
        const isInViewport = isFullscreenTile
            ? (tileW > 2 && tileH > 2)
            : (tileW > 2 && tileH > 2
                && (tileY + tileH) > containerRect.top
                && tileY < containerRect.bottom
                && (tileX + tileW) > containerRect.left
                && tileX < containerRect.right);

        const isFaceTimeCorner = !isFullscreenTile && rectEl.classList.contains('is-facetime-corner');
        const baseZ = isFullscreenTile ? 20 : (isFaceTimeCorner ? 10 : 0);
        const fsMatches = isFullscreenTile && voiceState.fullscreenTransform.identity === identity;
        const fsScale = fsMatches ? (voiceState.fullscreenTransform.scale || 1) : 1;
        const fsPanX = fsMatches ? (voiceState.fullscreenTransform.panX || 0) : 0;
        const fsPanY = fsMatches ? (voiceState.fullscreenTransform.panY || 0) : 0;
        const fsRot = fsMatches ? (voiceState.fullscreenTransform.rotationDeg || 0) : 0;

        if (hasScreen) {
            if (isInViewport) anyVisible = true;
            slots.push({
                key: `${identity}:screen`,
                source: 'screen',
                identity,
                x: Math.round(tileX),
                y: Math.round(tileY),
                width: Math.round(tileW),
                height: Math.round(tileH),
                cornerRadius: isFullscreenTile ? 0 : box.cornerRadius,
                mirror: false,
                cover: false,
                visible: isInViewport,
                zOrder: baseZ,
                scale: fsScale,
                panX: Math.round(fsPanX * 10) / 10,
                panY: Math.round(fsPanY * 10) / 10,
                rotationDeg: fsRot
            });
        }
        if (hasCamera) {
            if (isInViewport) anyVisible = true;
            const isPipOverlay = hasScreen;
            const camW = isPipOverlay ? Math.max(48, Math.round(tileW * 0.22)) : Math.round(tileW);
            const camH = isPipOverlay ? Math.max(36, Math.round(tileH * 0.28)) : Math.round(tileH);
            const camX = isPipOverlay
                ? (isFullscreenTile && fsRot === 90
                    ? Math.round(tileX + 16)
                    : Math.round(tileX + tileW - camW - 12))
                : Math.round(tileX);
            const camY = isPipOverlay
                ? (isFullscreenTile && fsRot === 90
                    ? Math.round(tileY + tileH - camH - 24)
                    : Math.round(tileY + tileH - camH - 12))
                : Math.round(tileY);
            slots.push({
                key: `${identity}:camera`,
                source: 'camera',
                identity,
                x: camX,
                y: camY,
                width: camW,
                height: camH,
                cornerRadius: isPipOverlay ? 8 : (isFullscreenTile ? 0 : box.cornerRadius),
                mirror: identity === voiceState.nativeLocalIdentity && voiceState.localCameraFacing !== 'environment',
                cover: false,
                visible: isInViewport,
                zOrder: isPipOverlay ? baseZ + 5 : baseZ,
                scale: isPipOverlay ? 1 : fsScale,
                panX: isPipOverlay ? 0 : Math.round(fsPanX * 10) / 10,
                panY: isPipOverlay ? 0 : Math.round(fsPanY * 10) / 10,
                rotationDeg: fsRot
            });
        }
    });

    document.documentElement?.classList?.toggle('spokes-native-video-underlay', anyVisible);
    const vpW = window.innerWidth || 0;
    const vpH = window.innerHeight || 0;
    const signature = `stage|${anyVisible}|${vpW}x${vpH}|` + slots.map(s =>
        `${s.key}:${s.x},${s.y},${s.width},${s.height},${s.cornerRadius || 0},${s.mirror},${s.cover},${s.visible},${s.zOrder},${s.scale || 1},${s.panX || 0},${s.panY || 0},${s.rotationDeg || 0}`
    ).join(';');

    if (forceSend || signature !== voiceState.nativeLastSlotSignature) {
        voiceState.nativeLastSlotSignature = signature;
        nativeVoice.updateVideoSlots({
            slots,
            viewportWidth: vpW,
            viewportHeight: vpH,
            underlaid: anyVisible,
            suppressed: false
        }).catch(() => {});
    }
}

export function scheduleNativeMobileVideoSlotsSync() {
    if (!voiceState.usingNativeMobileVoice || typeof window === 'undefined') return;
    if (_layoutRaf) return;
    _layoutRaf = window.requestAnimationFrame(() => {
        _layoutRaf = 0;
        syncNativeMobileVideoSlots();
    });
}

function refreshScopedVideoSlotObservers() {
    if (typeof document === 'undefined') return;
    const videoContainer = document.getElementById('voice-video-container');
    const pipOverlay = document.getElementById('spokes-voice-pip-overlay');

    // Keep ResizeObserver strictly scoped to stage & PiP containers
    if (_resizeObserver) {
        try {
            const desired = new Set();
            if (videoContainer) desired.add(videoContainer);
            if (pipOverlay) desired.add(pipOverlay);
            for (const prevEl of _observedResizeTargets) {
                if (!desired.has(prevEl)) {
                    try { _resizeObserver.unobserve(prevEl); } catch {}
                    _observedResizeTargets.delete(prevEl);
                }
            }
            for (const nextEl of desired) {
                if (!_observedResizeTargets.has(nextEl)) {
                    _resizeObserver.observe(nextEl);
                    _observedResizeTargets.add(nextEl);
                }
            }
        } catch {}
    }

    // Attach scoped MutationObservers ONLY to #voice-video-container and #spokes-voice-pip-overlay
    // (never watch attributes across document.body, which thrashes layout on chat/drawer/ripple mutations).
    if (typeof MutationObserver === 'function') {
        if (videoContainer !== _observedStageEl) {
            try { _stageSlotObserver?.disconnect(); } catch {}
            _stageSlotObserver = null;
            _observedStageEl = videoContainer;
            if (videoContainer) {
                _stageSlotObserver = new MutationObserver(() => scheduleNativeMobileVideoSlotsSync());
                _stageSlotObserver.observe(videoContainer, {
                    childList: true,
                    subtree: true,
                    attributes: true,
                    attributeFilter: ['class', 'style']
                });
            }
        }
        if (pipOverlay !== _observedPipEl) {
            try { _pipSlotObserver?.disconnect(); } catch {}
            _pipSlotObserver = null;
            _observedPipEl = pipOverlay;
            if (pipOverlay) {
                _pipSlotObserver = new MutationObserver(() => scheduleNativeMobileVideoSlotsSync());
                _pipSlotObserver.observe(pipOverlay, {
                    childList: true,
                    subtree: true,
                    attributes: true,
                    attributeFilter: ['class', 'style']
                });
            }
        }
    }
}

export function startNativeMobileVideoSlotObserver() {
    stopNativeMobileVideoSlotObserver();
    if (typeof window === 'undefined' || typeof document === 'undefined') return;

    document.documentElement?.classList?.add('spokes-native-voice-active');

    if (typeof ResizeObserver === 'function') {
        _resizeObserver = new ResizeObserver(() => {
            scheduleNativeMobileVideoSlotsSync();
        });
    }

    refreshScopedVideoSlotObservers();

    // Lightweight structural-only (childList: true, attributes: false) observer solely to detect
    // when Blazor mounts or unmounts #voice-video-container / #spokes-voice-pip-overlay during navigation.
    if (typeof MutationObserver === 'function' && document.body) {
        _structuralMountObserver = new MutationObserver(() => {
            const prevStage = _observedStageEl;
            const prevPip = _observedPipEl;
            refreshScopedVideoSlotObservers();
            if (_observedStageEl !== prevStage || _observedPipEl !== prevPip) {
                scheduleNativeMobileVideoSlotsSync();
            }
        });
        _structuralMountObserver.observe(document.body, {
            childList: true,
            subtree: true
        });
    }

    _resizeHandler = () => scheduleNativeMobileVideoSlotsSync();
    _scrollHandler = (ev) => {
        const target = ev?.target;
        if (!target || target === document || target === window ||
            (typeof target.contains === 'function' && (_observedStageEl && target.contains(_observedStageEl))) ||
            target === _observedStageEl) {
            scheduleNativeMobileVideoSlotsSync();
        }
    };
    window.addEventListener('resize', _resizeHandler);
    document.addEventListener('scroll', _scrollHandler, true);
}

export function stopNativeMobileVideoSlotObserver() {
    if (_layoutRaf) {
        try { window.cancelAnimationFrame(_layoutRaf); } catch {}
        _layoutRaf = 0;
    }
    _observedResizeTargets.clear();
    if (_resizeObserver) {
        try { _resizeObserver.disconnect(); } catch {}
        _resizeObserver = null;
    }
    if (_stageSlotObserver) {
        try { _stageSlotObserver.disconnect(); } catch {}
        _stageSlotObserver = null;
    }
    if (_pipSlotObserver) {
        try { _pipSlotObserver.disconnect(); } catch {}
        _pipSlotObserver = null;
    }
    if (_structuralMountObserver) {
        try { _structuralMountObserver.disconnect(); } catch {}
        _structuralMountObserver = null;
    }
    _observedStageEl = null;
    _observedPipEl = null;
    if (_resizeHandler) {
        try { window.removeEventListener('resize', _resizeHandler); } catch {}
        _resizeHandler = null;
    }
    if (_scrollHandler) {
        try { document.removeEventListener('scroll', _scrollHandler, true); } catch {}
        _scrollHandler = null;
    }
    if (typeof document !== 'undefined') {
        document.documentElement?.classList?.remove('spokes-native-video-underlay', 'spokes-native-voice-active');
        document.querySelectorAll('.participant-rect.native-video-active').forEach(el => {
            el.classList.remove('native-video-active');
            el.style.removeProperty('--tile-video-aspect');
        });
    }
    voiceState.nativeLastSlotSignature = '';
}

export async function cleanupNativeMobileVoiceState() {
    const wasUsing = voiceState.usingNativeMobileVoice;
    voiceState.usingNativeMobileVoice = false;
    voiceState.nativeConnectionState = 'disconnected';
    voiceState.nativeLocalIdentity = '';
    resetMobileCameraSelectionForNewCall();
    voiceState.nativeRemoteParticipants.clear();
    voiceState.nativeRemoteMicMuted.clear();
    voiceState.nativeRemoteScreenAudio.clear();
    voiceState.nativeVideoTracks.clear();
    voiceState.nativeVideoTrackSids.clear();
    voiceState.nativeVideoDims.clear();
    resetLocalSpeakingState();
    stopNativeMobileVideoSlotObserver();
    for (const h of voiceState.nativeListeners) {
        try { await h?.remove?.(); } catch {}
    }
    voiceState.nativeListeners = [];
    if (wasUsing) {
        const nativeVoice = getNativeVoicePlugin();
        if (nativeVoice) {
            try { await nativeVoice.disconnect(); } catch {}
        }
    }
}

export function setNativeMobileVoiceSensitivity(sensitivity) {
    if (!voiceState.usingNativeMobileVoice) return;
    const nativeVoice = getNativeVoicePlugin();
    if (nativeVoice && typeof nativeVoice.setVoiceSensitivity === 'function') {
        const clamped = Math.max(0, Math.min(100, Number(sensitivity)));
        if (Number.isFinite(clamped)) {
            nativeVoice.setVoiceSensitivity({ sensitivity: clamped }).catch(() => {});
        }
    }
}

export async function syncNativeMobilePhysicalRoomState() {
    if (!voiceState.usingNativeMobileVoice) return;
    const nativeVoice = getNativeVoicePlugin();
    if (!nativeVoice) return;
    const localId = voiceState.nativeLocalIdentity;
    const inPhysicalRoom = Boolean(localId && voiceState.coLocatedProcessedUsers.has(localId));
    try {
        await nativeVoice.setPhysicalRoomRouting({
            routedUserIds: Array.from(voiceState.coLocatedProcessedUsers),
            mutedUserIds: Array.from(voiceState.mutedRoommateUsers)
        });
        await nativeVoice.setNoiseFilter({
            noiseFilter: voiceState.currentNoiseSuppression || 'rnnoise',
            stationaryRoomMode: inPhysicalRoom,
            autoGainControl: Boolean(voiceState.autoGainControl)
        });
    } catch {}
}

export function getNativeMobileCameraCapabilities(deviceId, facingMode = null) {
    const targetFacing = facingMode || voiceState.localCameraFacing || 'user';
    const match = (deviceId && voiceState.nativeCachedCameras.find(c => c.deviceId === deviceId || c.cameraId === deviceId))
        || voiceState.nativeCachedCameras.find(c => c.facingMode === targetFacing)
        || voiceState.nativeCachedCameras[0];
    const maxDim = Math.max(match?.maxWidth || 1920, match?.maxHeight || 1080);
    const maxFps = match?.maxFps || 30;
    return { maxDim, maxFps, matchedCamera: match || null };
}

export function reportNativeMobileCameraCapabilitiesToDotnet(deviceId) {
    if (!voiceState.dotnetHelper) return;
    const { maxDim, maxFps } = getNativeMobileCameraCapabilities(deviceId, voiceState.localCameraFacing);
    invokeDotNetSafe('OnLocalVideoDimensions', maxDim, maxFps);
}

export async function resolveNativeMobileCaptureConfig(resolutionStr = 'Auto', targetFps = 30, deviceId = null) {
    const nativeCams = await fetchNativeMobileCameras();
    const targetDeviceId = deviceId || voiceState.nativeActiveCameraId || '';
    const matchedCam = nativeCams.find(c => c.cameraId === targetDeviceId || c.deviceId === targetDeviceId)
        || (!targetDeviceId ? nativeCams.find(c => c.facingMode === (voiceState.localCameraFacing || 'user')) : null);
    let facing = voiceState.localCameraFacing || 'user';
    if (matchedCam?.facingMode === 'environment' || matchedCam?.facingMode === 'user') {
        facing = matchedCam.facingMode;
    }
    const resolvedDeviceId = matchedCam ? (matchedCam.cameraId || matchedCam.deviceId) : targetDeviceId;
    const { maxDim, maxFps } = getNativeMobileCameraCapabilities(resolvedDeviceId, facing);
    const clamped = _callbacks.clampCameraResolutionForCapabilities(resolutionStr, targetFps, maxDim, maxFps);
    const isAuto = !clamped.resolution || clamped.resolution === 'Auto';
    const effectiveFps = isAuto ? 30 : (clamped.fps || 30);
    const preset = _callbacks.getCustomPreset(clamped.resolution, effectiveFps > 35);
    return {
        facingMode: facing,
        deviceId: resolvedDeviceId,
        quality: clamped.resolution,
        width: preset.width,
        height: preset.height,
        fps: effectiveFps,
        maxBitrate: preset.maxBitrate
    };
}

export async function fetchNativeMobileCameras() {
    const platform = getNativePlatform();
    const camProvider = platform === 'ios'
        ? getAudioSessionPlugin()
        : window.Capacitor?.Plugins?.TelecomConnectionPlugin;
    if (camProvider && typeof camProvider.getNativeCameras === 'function') {
        try {
            const res = await camProvider.getNativeCameras();
            if (Array.isArray(res?.cameras) && res.cameras.length > 0) {
                voiceState.nativeCachedCameras = res.cameras.map(c => ({
                    cameraId: String(c.cameraId || c.deviceId || ''),
                    deviceId: String(c.deviceId || c.cameraId || ''),
                    label: c.label || `Camera ${c.cameraId || c.deviceId || ''}`,
                    facingMode: c.facingMode === 'environment' ? 'environment' : 'user',
                    maxWidth: Number(c.maxWidth) || 1920,
                    maxHeight: Number(c.maxHeight) || 1080,
                    maxFps: Number(c.maxFps) || 30
                }));
                return voiceState.nativeCachedCameras;
            }
        } catch (e) {
            console.warn('[Voice] getNativeCameras error:', e);
        }
    }
    if (voiceState.nativeCachedCameras.length === 0 && platform === 'ios') {
        voiceState.nativeCachedCameras = [
            { cameraId: 'ios_front', deviceId: 'ios_front', label: 'Front Camera', facingMode: 'user', maxWidth: 3840, maxHeight: 2160, maxFps: 60 },
            { cameraId: 'ios_back', deviceId: 'ios_back', label: 'Back Camera (Wide)', facingMode: 'environment', maxWidth: 3840, maxHeight: 2160, maxFps: 60 }
        ];
    }
    return voiceState.nativeCachedCameras;
}

function syncNativeMobileRemoteParticipantMaps(data) {
    voiceState.nativeRemoteParticipants.clear();
    voiceState.nativeRemoteMicMuted.clear();
    voiceState.nativeRemoteScreenAudio.clear();
    if (Array.isArray(data?.remoteParticipantStates)) {
        for (const st of data.remoteParticipantStates) {
            const rawId = st?.userId;
            if (!rawId || isSyntheticIdentity(rawId)) continue;
            const effId = getEffectiveParticipantIdentity(rawId);
            if (!effId || isSyntheticIdentity(effId)) continue;
            voiceState.nativeRemoteParticipants.set(effId, st?.metadata || '');
            if (typeof st?.isMicMuted === 'boolean') {
                voiceState.nativeRemoteMicMuted.set(effId, st.isMicMuted);
            } else if (st?.isMicMuted === null) {
                voiceState.nativeRemoteMicMuted.set(effId, null);
            } else if (st?.isMuted === true) {
                voiceState.nativeRemoteMicMuted.set(effId, true);
            }
        }
    }
    const remoteIds = Array.isArray(data?.remoteParticipants)
        ? Array.from(new Set(
            data.remoteParticipants
                .filter(id => Boolean(id) && !isSyntheticIdentity(id))
                .map(id => getEffectiveParticipantIdentity(id))
                .filter(id => Boolean(id) && !isSyntheticIdentity(id))
        ))
        : [];
    for (const rid of remoteIds) {
        if (!voiceState.nativeRemoteParticipants.has(rid)) {
            voiceState.nativeRemoteParticipants.set(rid, '');
        }
    }
    if (Array.isArray(data?.remoteMutedParticipants)) {
        for (const mid of data.remoteMutedParticipants) {
            if (!mid || isSyntheticIdentity(mid)) continue;
            const effId = getEffectiveParticipantIdentity(mid);
            if (effId && !isSyntheticIdentity(effId)) {
                voiceState.nativeRemoteMicMuted.set(effId, true);
            }
        }
    }
    if (Array.isArray(data?.remoteScreenAudioParticipants)) {
        for (const sid of data.remoteScreenAudioParticipants) {
            const effId = getEffectiveParticipantIdentity(sid);
            if (effId && !isSyntheticIdentity(effId)) {
                voiceState.nativeRemoteScreenAudio.add(effId);
            }
        }
    }
    return remoteIds;
}

export async function syncNativeMobileConnectionState() {
    if (!voiceState.usingNativeMobileVoice) {
        voiceState.nativeConnectionState = 'disconnected';
        return 'disconnected';
    }
    const nativeVoice = getNativeVoicePlugin();
    if (!nativeVoice || typeof nativeVoice.getConnectionState !== 'function') {
        return voiceState.nativeConnectionState;
    }
    try {
        const res = await nativeVoice.getConnectionState();
        const rawState = String(res?.state || '').toLowerCase();
        if (rawState === 'connected' || rawState === 'reconnecting' || rawState === 'connecting' || rawState === 'disconnected') {
            voiceState.nativeConnectionState = rawState;
        } else if (typeof res?.connected === 'boolean') {
            voiceState.nativeConnectionState = res.connected ? 'connected' : 'disconnected';
        }
    } catch {}
    return voiceState.nativeConnectionState;
}

export async function connectNativeMobileVoice(nativeVoice, token, url, noiseSuppression, initialMuted = false) {
    await cleanupNativeMobileVoiceState();
    voiceState.usingNativeMobileVoice = true;
    voiceState.nativeConnectionState = 'connecting';
    voiceState.isLocalSoftwareMuted = Boolean(initialMuted);
    voiceState.isLocalHardwareMuted = false;
    voiceState.localCameraDesired = false;
    voiceState.nativeLastSlotSignature = '';

    const pushNativeAudioTelemetry = (filter, rttMs = 0) => {
        if (!voiceState.dotnetHelper) return;
        const captureMs = estimateNativeMobileCaptureMs(filter);
        invokeDotNetSafe('OnLocalAudioPipelineTelemetry', filter, captureMs, getNativeVoiceTelemetryPlatformTag(), rttMs);
    };

    const addNativeListener = async (eventName, cb) => {
        if (typeof nativeVoice.addListener !== 'function') return;
        const handle = await nativeVoice.addListener(eventName, cb);
        voiceState.nativeListeners.push(handle);
    };

    await addNativeListener('onParticipantConnected', (data) => {
        const id = data?.identity;
        if (!id || isSyntheticIdentity(id)) return;
        voiceState.nativeRemoteParticipants.set(id, typeof data?.metadata === 'string' ? data.metadata : '');
        syncNativeMobilePhysicalRoomState().catch(() => {});
        invokeDotNetSafe('OnParticipantConnected', id);
        scheduleRoomSnapshotSync();
    });

    await addNativeListener('onParticipantDisconnected', (data) => {
        const id = data?.identity;
        if (!id || isSyntheticIdentity(id)) return;
        voiceState.nativeRemoteParticipants.delete(id);
        voiceState.nativeRemoteMicMuted.delete(id);
        voiceState.nativeRemoteScreenAudio.delete(id);
        setNativeMobileVideoTrackActive(id, 'camera', false);
        setNativeMobileVideoTrackActive(id, 'screenshare', false);
        scheduleNativeMobileVideoSlotsSync();
        _callbacks.syncIosAudioSessionVideoMode().catch(() => {});
        invokeDotNetSafe('OnParticipantDisconnected', id);
        scheduleRoomSnapshotSync();
    });

    await addNativeListener('onReconnecting', () => {
        voiceState.nativeConnectionState = 'reconnecting';
        console.warn(`[Voice] Native Mobile LiveKit reconnecting | platform=${getNativePlatform()}, identity=${voiceState.nativeLocalIdentity}`);
    });

    await addNativeListener('onReconnected', (data) => {
        voiceState.nativeConnectionState = 'connected';
        console.info(`[Voice] Native Mobile LiveKit reconnected | platform=${getNativePlatform()}, identity=${voiceState.nativeLocalIdentity}`);
        syncNativeMobileRemoteParticipantMaps(data);
        syncNativeMobilePhysicalRoomState().catch(() => {});
        scheduleNativeMobileVideoSlotsSync();
        publishVoiceRoomSnapshot('OnRoomReconnectedAsync');
    });

    await addNativeListener('onLocalSpeakingChanged', (data) => {
        reportLocalSpeakingState(Boolean(data?.isSpeaking));
    });

    await addNativeListener('onTrackSubscribed', (data) => {
        const effectiveId = getEffectiveParticipantIdentity(data?.identity);
        if (!effectiveId) return;
        if (data?.kind === 'audio' && data?.source === 'screen_audio') {
            if (effectiveId !== voiceState.nativeLocalIdentity) {
                voiceState.nativeRemoteScreenAudio.add(effectiveId);
                invokeDotNetSafe('OnRemoteScreenAudioChanged', effectiveId, true);
                scheduleRoomSnapshotSync();
            }
            return;
        }
        if (data?.kind === 'audio') {
            const trackName = String(data?.trackName || '');
            if (!trackName.startsWith(PROCESSED_MIC_PREFIX) && effectiveId !== voiceState.nativeLocalIdentity && !isBotIdentity(effectiveId)) {
                const micMutedState = typeof data?.participantMicMuted === 'boolean'
                    ? data.participantMicMuted
                    : (data?.participantMicMuted === null ? null : Boolean(data?.muted));
                voiceState.nativeRemoteMicMuted.set(effectiveId, micMutedState);
                if (typeof micMutedState === 'boolean') {
                    invokeDotNetSafe('OnParticipantMuteChanged', effectiveId, micMutedState);
                }
                scheduleRoomSnapshotSync();
            }
            return;
        }
        if (data?.kind !== 'video') return;
        syncNativeVideoTrackState(effectiveId, data.source, !data.muted, data.width, data.height, {
            syncSnapshot: true,
            sid: typeof data?.sid === 'string' ? data.sid : ''
        });
    });

    await addNativeListener('onTrackUnsubscribed', (data) => {
        const effectiveId = getEffectiveParticipantIdentity(data?.identity);
        if (!effectiveId) return;
        if (data?.kind === 'audio' && data?.source === 'screen_audio') {
            if (effectiveId !== voiceState.nativeLocalIdentity) {
                voiceState.nativeRemoteScreenAudio.delete(effectiveId);
                invokeDotNetSafe('OnRemoteScreenAudioChanged', effectiveId, false);
                scheduleRoomSnapshotSync();
            }
            return;
        }
        if (data?.kind === 'audio') {
            const trackName = String(data?.trackName || '');
            if (!trackName.startsWith(PROCESSED_MIC_PREFIX) && effectiveId !== voiceState.nativeLocalIdentity && !isBotIdentity(effectiveId)) {
                if (data?.participantMicMuted === true) {
                    voiceState.nativeRemoteMicMuted.set(effectiveId, true);
                    invokeDotNetSafe('OnParticipantMuteChanged', effectiveId, true);
                } else if (data?.participantMicMuted === false) {
                    voiceState.nativeRemoteMicMuted.set(effectiveId, false);
                } else {
                    voiceState.nativeRemoteMicMuted.set(effectiveId, null);
                }
                scheduleRoomSnapshotSync();
            }
            return;
        }
        if (data?.kind !== 'video') return;
        syncNativeVideoTrackState(effectiveId, data.source, false, 0, 0, {
            syncSnapshot: true,
            sid: typeof data?.sid === 'string' ? data.sid : ''
        });
    });

    await addNativeListener('onTrackMuted', (data) => {
        const effectiveId = getEffectiveParticipantIdentity(data?.identity);
        if (!effectiveId) return;
        if (data.kind === 'video') {
            syncNativeVideoTrackState(effectiveId, data.source, false, 0, 0, {
                syncSnapshot: true,
                sid: typeof data?.sid === 'string' ? data.sid : ''
            });
        } else if (data.kind === 'audio' && data?.source !== 'screen_audio') {
            const trackName = String(data?.trackName || '');
            if (!trackName.startsWith(PROCESSED_MIC_PREFIX) && effectiveId !== voiceState.nativeLocalIdentity && !isBotIdentity(effectiveId)) {
                const isMuted = typeof data?.participantMicMuted === 'boolean' ? data.participantMicMuted : true;
                voiceState.nativeRemoteMicMuted.set(effectiveId, isMuted);
                invokeDotNetSafe('OnParticipantMuteChanged', effectiveId, isMuted);
                scheduleRoomSnapshotSync();
            }
        }
    });

    await addNativeListener('onTrackUnmuted', (data) => {
        const effectiveId = getEffectiveParticipantIdentity(data?.identity);
        if (!effectiveId) return;
        if (data.kind === 'video') {
            syncNativeVideoTrackState(effectiveId, data.source, true, data?.width, data?.height, {
                syncSnapshot: true,
                sid: typeof data?.sid === 'string' ? data.sid : ''
            });
        } else if (data.kind === 'audio' && data?.source !== 'screen_audio') {
            const trackName = String(data?.trackName || '');
            if (!trackName.startsWith(PROCESSED_MIC_PREFIX) && effectiveId !== voiceState.nativeLocalIdentity && !isBotIdentity(effectiveId)) {
                voiceState.nativeRemoteMicMuted.set(effectiveId, false);
                invokeDotNetSafe('OnParticipantMuteChanged', effectiveId, false);
                scheduleRoomSnapshotSync();
            }
        }
    });

    await addNativeListener('onVideoDimensionsChanged', (data) => {
        const effectiveId = getEffectiveParticipantIdentity(data?.identity);
        const w = Number(data?.width) || 0;
        const h = Number(data?.height) || 0;
        if (effectiveId && w > 0 && h > 0) {
            const pipSource = (data?.source === 'screen' || data?.source === 'screenshare') ? 'screenshare' : 'camera';
            voiceState.nativeVideoDims.set(`${effectiveId}:${pipSource}`, { width: w, height: h });
            if (voiceState.nativeVideoTracks.get(effectiveId)?.has(pipSource)) {
                setNativeMobileVideoTrackActive(effectiveId, pipSource, true, w, h);
            }
        }
        scheduleNativeMobileVideoSlotsSync();
    });

    await addNativeListener('onAvSkewTelemetry', (data) => {
        if (!data || !voiceState.dotnetHelper) return;
        const effFilter = data.effectiveFilter || voiceState.nativeEffectiveFilter || 'rnnoise';
        voiceState.nativeEffectiveFilter = effFilter;
        const rttMs = typeof data.rttMs === 'number' && data.rttMs > 0 ? Math.round((data.rttMs / 2.0) * 10) / 10 : 0;
        pushNativeAudioTelemetry(effFilter, rttMs);
        if (typeof data.avSkewMs === 'number' && data.videoDecodeMs >= 0 && voiceState.nativeVideoTracks.size > 0) {
            invokeDotNetSafe(
                'OnAvSkewTelemetry',
                data.avSkewMs,
                data.audioJitterMs || 0,
                data.videoJitterMs || 0,
                data.videoDecodeMs || 0,
                getNativeVoiceTelemetryPlatformTag()
            );
        }
    });

    await addNativeListener('onDisconnected', async (data) => {
        const reason = data?.reason || 'UNKNOWN';
        console.warn(`[Voice] Native Mobile LiveKit disconnected | platform=${getNativePlatform()}, reason=${reason}`);
        voiceState.nativeConnectionState = 'disconnected';
        await cleanupNativeMobileVoiceState();
        await _callbacks.releaseNativeCallResources();
        if (!voiceState.currentRoom && !voiceState.usingNativeMobileVoice) {
            _callbacks.notifyHostVoiceState(false, false);
        }
        if (voiceState.dotnetHelper) {
            if (reason === 'DUPLICATE_IDENTITY') {
                voiceState.dotnetHelper.invokeMethodAsync('OnRoomDisconnectedWithReason', reason)?.catch?.(() => {
                    voiceState.dotnetHelper?.invokeMethodAsync?.('OnRoomDisconnected')?.catch?.(() => {});
                });
            } else {
                voiceState.dotnetHelper.invokeMethodAsync('OnRoomDisconnected')?.catch?.(() => {});
            }
        }
    });

    _callbacks.reportConnectingStage('connecting');
    resetMobileCameraSelectionForNewCall();
    const localFilter = (noiseSuppression || voiceState.currentNoiseSuppression || 'rnnoise').trim().toLowerCase();
    const res = await nativeVoice.connect({
        livekitUrl: url,
        token,
        microphoneEnabled: !voiceState.isLocalSoftwareMuted,
        cameraEnabled: false,
        facingMode: 'user',
        quality: 'Auto',
        videoWidth: 1920,
        videoHeight: 1080,
        videoFps: 30,
        videoMaxBitrate: 8000000,
        noiseFilter: localFilter,
        voiceSensitivity: voiceState.voiceSensitivity,
        stationaryRoomMode: false,
        autoGainControl: Boolean(voiceState.autoGainControl),
        deafened: voiceState.masterVolume <= 0.001
    });

    voiceState.nativeConnectionState = 'connected';
    voiceState.nativeLocalIdentity = res?.localIdentity || '';
    voiceState.nativeEffectiveFilter = res?.effectiveFilter || localFilter;

    await nativeVoice.setMasterVolume({ volume: voiceState.masterVolume }).catch(() => {});
    if (voiceState.savedVoiceSettings?.participantVolumes) {
        for (const [id, volPct] of Object.entries(voiceState.savedVoiceSettings.participantVolumes)) {
            if (typeof volPct === 'number') {
                const vol = Math.max(0, Math.min(200, volPct)) / 100.0;
                nativeVoice.setParticipantVolume({ identity: id, volume: vol }).catch(() => {});
            }
        }
    }
    await syncNativeMobilePhysicalRoomState();

    startNativeMobileVideoSlotObserver();
    _callbacks.notifyHostVoiceState(true, voiceState.isLocalSoftwareMuted);

    configureVoicePip(voiceState.dotnetHelper, {
        onToggleMute: () => window.spokesNativeCallAction('toggleMute'),
        onToggleCamera: () => window.spokesNativeCallAction('toggleCamera'),
        onToggleScreenShare: () => window.spokesNativeCallAction('toggleScreenShare'),
        onHangUp: () => window.spokesLeaveVoiceCall(),
        onReturnToCall: () => window.spokesNativeCallAction('returnToCall'),
        onRestoreStageTracks: () => syncNativeMobileVideoSlots(true),
        onSyncNativeVideoSlots: () => scheduleNativeMobileVideoSlotsSync()
    });
    updatePipCallState({
        active: true,
        channelName: '',
        localUserId: voiceState.nativeLocalIdentity,
        isNativePlatform: true,
        isMuted: voiceState.isLocalSoftwareMuted,
        isVideoOn: false,
        isScreenSharing: false
    });

    const remoteIds = syncNativeMobileRemoteParticipantMaps(res);
    if (voiceState.dotnetHelper) {
        invokeDotNetSafe('OnRoomConnected', remoteIds);
        for (const [effId, micState] of voiceState.nativeRemoteMicMuted.entries()) {
            if (micState === true) {
                invokeDotNetSafe('OnParticipantMuteChanged', effId, true);
            }
        }
    }

    for (const effId of voiceState.nativeRemoteScreenAudio) {
        invokeDotNetSafe('OnRemoteScreenAudioChanged', effId, true);
    }

    if (Array.isArray(res?.remoteVideoTracks)) {
        for (const vt of res.remoteVideoTracks) {
            const effId = getEffectiveParticipantIdentity(vt?.identity);
            if (!effId || vt?.muted) continue;
            syncNativeVideoTrackState(effId, vt.source, true, vt.width, vt.height, {
                syncSnapshot: false,
                sid: typeof vt?.sid === 'string' ? vt.sid : ''
            });
        }
    }

    publishVoiceRoomSnapshot('SyncRoomSnapshotAsync');
    pushNativeAudioTelemetry(voiceState.nativeEffectiveFilter, 0);
    fetchNativeMobileCameras().then(() => reportNativeMobileCameraCapabilitiesToDotnet(voiceState.nativeActiveCameraId)).catch(() => {});
    _callbacks.syncIosAudioSessionVideoMode().catch(() => {});
    _callbacks.updateAudioDevices().catch(() => {});
    console.info(`[Voice] Native Mobile LiveKit connected | platform=${getNativePlatform()}, identity=${voiceState.nativeLocalIdentity}, filter=${voiceState.nativeEffectiveFilter}`);
    return true;
}

function syncNativeVideoTrackState(identity, rawSource, active, width = 0, height = 0, { syncSnapshot = true, sid = '' } = {}) {
    const pipSource = rawSource === 'screen' || rawSource === 'screenshare' ? 'screenshare' : 'camera';
    const sidKey = `${identity}:${pipSource}`;
    const cleanSid = typeof sid === 'string' ? sid.trim() : '';

    if (!active) {
        const activeSid = voiceState.nativeVideoTrackSids.get(sidKey) || '';
        if (cleanSid && activeSid && cleanSid !== activeSid) {
            // Stale unsubscribe/mute from a superseded track (e.g. old 1080p track unpublishing
            // after the replacement Auto track already subscribed): preserve the live replacement.
            return;
        }
        voiceState.nativeVideoTrackSids.delete(sidKey);
        setNativeMobileVideoTrackActive(identity, pipSource, false);
        invokeDotNetSafe('OnVideoTrackStateChanged', identity, pipSource, false);
    } else {
        if (cleanSid) {
            voiceState.nativeVideoTrackSids.set(sidKey, cleanSid);
        }
        const w = Number(width) || 0;
        const h = Number(height) || 0;
        setNativeMobileVideoTrackActive(identity, pipSource, true, w, h);
        invokeDotNetSafe('EnsureParticipantSlot', identity);
        invokeDotNetSafe('OnVideoTrackStateChanged', identity, pipSource, true);
        if (w > 0 && h > 0) {
            invokeDotNetSafe('OnParticipantVideoDimensions', identity, Math.max(w, h), 30);
        }
    }
    if (syncSnapshot) {
        scheduleRoomSnapshotSync();
    }
}
