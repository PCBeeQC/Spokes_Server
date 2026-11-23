// voicePip.js — Cross-platform Picture-in-Picture & Native Call Metadata Coordinator
// Supports:
//  1. In-App Floating PiP (<VoicePipOverlay />) across Web, Desktop, and Mobile (pre-mounted + corner pill)
//  2. Desktop (Electron 45) Frameless Spokes Child Window ('spokes_call_pip') with zero-copy 60fps WebRTC tracks
//  3. Web Browser OS PiP via Document PiP API (Spokes design language) + Canvas HTMLVideoElement fallback (Safari/Firefox)
//  4. Android Native Activity PiP 0ms full-bleed stage mode + ForegroundService metadata bridge
//  5. iOS Native AVPictureInPictureVideoCallViewController metadata & single-room native video coordinator

import { isAppleMobileDevice, isNativePlatform } from './voice/voiceState.js';

const ACTIVE_SPEAKER_HYSTERESIS_MS = 1500;
const PIP_CORNER_STORAGE_KEY = 'spokes_pip_corner_v1';
const DESKTOP_PIP_FRAME_NAME = 'spokes_call_pip';
const FALLBACK_CANVAS_INTERVAL_MS = 67;

let _dotnetHelper = null;
let _actions = {
    onToggleMute: null,
    onToggleCamera: null,
    onToggleScreenShare: null,
    onHangUp: null,
    onReturnToCall: null,
    onRestoreStageTracks: null
};

// Map<`${participantId}:${source}`, { participantId, source: 'screenshare'|'camera', mediaStreamTrack, isLocal, width, height, facingMode, livekitTrack, publication, cleanupTrackListeners }>
const _videoTracks = new Map();

const _callMeta = {
    active: false,
    channelId: '',
    channelName: '',
    serverName: '',
    localUserId: '',
    isNativePlatform: false,
    isMuted: false,
    isDeafened: false,
    isVideoOn: false,
    isScreenSharing: false,
    focusedParticipantId: '',
    inAppPipActive: false,
    speakingIds: new Set(),
    // Map<id, { id, name, avatarUrl, initials, profileColor, isMuted }>
    participants: new Map()
};

let _stickySpeakerId = '';
let _stickySpeakerUntil = 0;
let _lastNativePayloadJson = '';

// Rich HTML PiP Window state (Electron frameless child window & Chromium Document PiP)
let _docPipWindow = null;
let _docPipRefs = null;
let _docPipCloseWatchTimer = null;

// Fallback HTMLVideoElement + Canvas PiP state (Safari / Firefox Web only)
let _fallbackPipVideo = null;
let _fallbackCanvas = null;
let _fallbackCtx = null;
let _fallbackSourceVideo = null;
let _fallbackTimer = null;
let _isFallbackPipOpen = false;

// iOS Native OS PiP state (signaled by SpokesIosPipController)
let _isIosOsPipActive = false;

// In-App PiP drag state
let _dragCleanup = null;
let _boundOverlayEl = null;

// Native Android PiP mode
let _isNativeMobilePip = false;

function isDesktopApp() {
    return Boolean(
        typeof window !== 'undefined' &&
        (window.isSpokesDesktop || window.SpokesBridge?.isDesktop || window.SpokesBridge?.platform === 'desktop')
    );
}

function computeInitials(name) {
    if (!name || typeof name !== 'string') return '?';
    const parts = name.trim().split(/\s+/).filter(Boolean);
    if (parts.length === 0) return '?';
    if (parts.length === 1) return parts[0].substring(0, 2).toUpperCase();
    return (parts[0][0] + parts[parts.length - 1][0]).toUpperCase();
}

function normalizeAvatarUrl(url) {
    if (!url || typeof url !== 'string') return '';
    if (url.startsWith('http://') || url.startsWith('https://') || url.startsWith('data:')) {
        return url;
    }
    try {
        return new URL(url, window.location.origin).toString();
    } catch (e) {
        return url;
    }
}

export function configureVoicePip(dotnetHelper, actions = {}) {
    _dotnetHelper = dotnetHelper;
    _actions = { ..._actions, ...actions };
}

export function isNativeMobilePipActive() {
    return Boolean(_isNativeMobilePip);
}

export function isStageSuppressedByExternalPip() {
    return Boolean(
        _isNativeMobilePip ||
        (_docPipWindow && !_docPipWindow.closed) ||
        _isFallbackPipOpen ||
        (typeof document !== 'undefined' && document.pictureInPictureElement)
    );
}

const _appliedTrackPolicies = new WeakMap();

function resolveLivekitVideoQuality(qualityKey) {
    const vq = window.LivekitClient?.VideoQuality;
    if (!vq || !qualityKey) return null;
    if (qualityKey === 'LOW') return vq.LOW !== undefined ? vq.LOW : (vq.MEDIUM !== undefined ? vq.MEDIUM : vq.HIGH);
    if (qualityKey === 'MEDIUM') return vq.MEDIUM !== undefined ? vq.MEDIUM : vq.HIGH;
    return vq.HIGH;
}

function applyRemoteTrackPolicy(entry, enabled, qualityKey = null, width = 0, height = 0) {
    if (!entry || entry.isLocal) return;
    const pub = entry.publication;
    if (!pub) return;

    const resolvedQuality = enabled ? resolveLivekitVideoQuality(qualityKey) : null;
    const targetWidth = enabled ? width : 0;
    const targetHeight = enabled ? height : 0;
    const prev = _appliedTrackPolicies.get(pub);
    if (prev
        && prev.enabled === enabled
        && prev.quality === resolvedQuality
        && prev.width === targetWidth
        && prev.height === targetHeight) {
        return;
    }

    _appliedTrackPolicies.set(pub, {
        enabled,
        quality: resolvedQuality,
        width: targetWidth,
        height: targetHeight
    });

    try {
        if (enabled) {
            if (typeof pub.setSubscribed === 'function' && pub.isSubscribed === false) {
                pub.setSubscribed(true);
            }
            if (typeof pub.setEnabled === 'function') {
                pub.setEnabled(true);
            }
            if (resolvedQuality !== null && typeof pub.setVideoQuality === 'function') {
                pub.setVideoQuality(resolvedQuality);
            }
            if (targetWidth > 0 && targetHeight > 0 && typeof pub.setVideoDimensions === 'function') {
                pub.setVideoDimensions({ width: targetWidth, height: targetHeight });
            }
        } else {
            if (typeof pub.setEnabled === 'function') {
                pub.setEnabled(false);
            }
        }
    } catch (e) {}
}

function ensureTrackSubscribed(entry) {
    if (!entry || entry.isLocal) return;
    const pub = entry.publication;
    if (!pub) return;
    try {
        if (typeof pub.setSubscribed === 'function' && pub.isSubscribed === false) {
            pub.setSubscribed(true);
        }
    } catch (e) {}
}

function cleanupTrackEntry(entry) {
    if (!entry) return;
    if (typeof entry.cleanupTrackListeners === 'function') {
        try { entry.cleanupTrackListeners(); } catch (e) {}
        entry.cleanupTrackListeners = null;
    }
}

function syncPipTrackSubscriptions(target) {
    const overlay = typeof document !== 'undefined' ? document.getElementById('spokes-voice-pip-overlay') : null;
    const isMinimizedPill = Boolean(overlay?.classList.contains('is-minimized-pill')) && !_isNativeMobilePip;
    const isHostViewVisible = typeof window === 'undefined' || window._spokesHostViewVisible !== false;
    const isExternalPipActive = Boolean(
        _isNativeMobilePip
        || (_docPipWindow && !_docPipWindow.closed)
        || _isFallbackPipOpen
        || _isIosOsPipActive
    );
    const isVisibleInAppPip = Boolean(isHostViewVisible && _callMeta.inAppPipActive && !isMinimizedPill);
    const isStageMounted = isHostViewVisible
        && typeof document !== 'undefined'
        && Boolean(document.getElementById('voice-video-container'))
        && !isExternalPipActive;

    // 1. Full call stage is visible (not in PiP): restore full quality for stage rendering
    if (isStageMounted && !_callMeta.inAppPipActive) {
        for (const entry of _videoTracks.values()) {
            if (!entry || entry.isLocal) continue;
            applyRemoteTrackPolicy(entry, true, 'HIGH', 3840, 2160);
        }
        return;
    }

    // 2. Minimized pill or hidden surface while stage is unmounted: pause remote video RTP (0 kbps)
    if (!isVisibleInAppPip && !isExternalPipActive) {
        for (const entry of _videoTracks.values()) {
            if (!entry || entry.isLocal) continue;
            applyRemoteTrackPolicy(entry, false);
        }
        return;
    }

    // 3. Active PiP surface: request SFU Simulcast LOW/MEDIUM for small webcam tiles, HIGH for screen shares,
    // and disable remote video tracks that are not rendered on the active PiP surface.
    const pinnedPrimary = target?.primaryTrack || null;
    const pinnedPresenterCam = target?.presenterCamTrack || null;
    const isGrid = target?.layoutMode === 'grid';
    const gridIds = new Set((target?.gridParticipants || []).map(p => p?.id).filter(Boolean));
    const filmstripIds = new Set((target?.filmstripParticipants || []).map(p => p?.id).filter(Boolean));

    for (const entry of _videoTracks.values()) {
        if (!entry || entry.isLocal) continue;

        const isScreen = entry.source === 'screenshare';
        const isPrimary = !isGrid && entry === pinnedPrimary;
        const isPresenterCam = !isGrid && entry === pinnedPresenterCam;
        const isInGridTile = isGrid && gridIds.has(entry.participantId);
        const isInFilmstrip = !isGrid && filmstripIds.has(entry.participantId);

        if (!isPrimary && !isPresenterCam && !isInGridTile && !isInFilmstrip) {
            applyRemoteTrackPolicy(entry, false);
            continue;
        }

        if (isScreen) {
            applyRemoteTrackPolicy(entry, true, 'HIGH', 1920, 1080);
        } else if (isPrimary) {
            // Single focused webcam in PiP (~320x198px): MEDIUM (640x360) stays sharp at 2x DPI while saving >70% bandwidth
            applyRemoteTrackPolicy(entry, true, 'MEDIUM', 640, 360);
        } else {
            // Grid tile / filmstrip / presenter-cam thumbnail (~140-180px): LOW (320x180) simulcast layer
            applyRemoteTrackPolicy(entry, true, 'LOW', 320, 180);
        }
    }
}

function isPipTrackAlive(entry) {
    return Boolean(entry && (entry.isNative || (entry.mediaStreamTrack && entry.mediaStreamTrack.readyState !== 'ended')));
}

export function registerPipVideoTrack(
    participantId,
    source,
    mediaStreamTrack,
    isLocal = false,
    explicitWidth = 0,
    explicitHeight = 0,
    livekitTrack = null,
    publication = null
) {
    const isNativeTrack = Boolean(publication?.isNativeTrack);
    if (!participantId || (!mediaStreamTrack && !isNativeTrack)) return;
    const normalizedSource = source === 'screenshare' ? 'screenshare' : 'camera';
    const key = `${participantId}:${normalizedSource}`;
    const existing = _videoTracks.get(key);

    if (existing && existing.mediaStreamTrack !== mediaStreamTrack && typeof existing.cleanupTrackListeners === 'function') {
        try { existing.cleanupTrackListeners(); } catch (e) {}
        existing.cleanupTrackListeners = null;
    }

    let width = explicitWidth > 0 ? explicitWidth : 0;
    let height = explicitHeight > 0 ? explicitHeight : 0;
    let facingMode = typeof publication?.facingMode === 'string' ? publication.facingMode : '';
    try {
        if (mediaStreamTrack && typeof mediaStreamTrack.getSettings === 'function') {
            const s = mediaStreamTrack.getSettings() || {};
            if (width <= 0 && s.width > 0) width = s.width;
            if (height <= 0 && s.height > 0) height = s.height;
            if (!facingMode && typeof s.facingMode === 'string') facingMode = s.facingMode;
        }
    } catch (e) {}

    if ((width <= 0 || height <= 0) && existing && (existing.mediaStreamTrack === mediaStreamTrack || (isNativeTrack && existing.isNative))) {
        if (width <= 0 && existing.width > 0) width = existing.width;
        if (height <= 0 && existing.height > 0) height = existing.height;
    }
    if (!facingMode && existing?.facingMode) {
        facingMode = existing.facingMode;
    }

    const entry = {
        participantId,
        source: normalizedSource,
        mediaStreamTrack: mediaStreamTrack || null,
        isNative: isNativeTrack,
        isLocal: Boolean(isLocal),
        width,
        height,
        facingMode: facingMode || (isLocal ? 'user' : ''),
        livekitTrack: livekitTrack || existing?.livekitTrack || null,
        publication: publication || existing?.publication || null,
        cleanupTrackListeners: existing?.mediaStreamTrack === mediaStreamTrack ? existing.cleanupTrackListeners : null
    };

    if (!entry.cleanupTrackListeners && mediaStreamTrack && typeof mediaStreamTrack.addEventListener === 'function') {
        const onTrackUnmute = () => {
            try {
                if (typeof mediaStreamTrack.getSettings === 'function') {
                    const s = mediaStreamTrack.getSettings() || {};
                    if (s.width > 0) entry.width = s.width;
                    if (s.height > 0) entry.height = s.height;
                }
            } catch (e) {}
            refreshAllPipSurfaces();
        };
        mediaStreamTrack.addEventListener('unmute', onTrackUnmute);
        entry.cleanupTrackListeners = () => {
            try { mediaStreamTrack.removeEventListener('unmute', onTrackUnmute); } catch (e) {}
        };
    }

    _videoTracks.set(key, entry);

    if (isLocal) {
        if (normalizedSource === 'camera') _callMeta.isVideoOn = true;
        if (normalizedSource === 'screenshare') _callMeta.isScreenSharing = true;
    }

    refreshAllPipSurfaces();
}

export function unregisterPipVideoTrack(participantId, source = null) {
    if (!participantId) return;
    if (source) {
        const normalizedSource = source === 'screenshare' ? 'screenshare' : 'camera';
        const key = `${participantId}:${normalizedSource}`;
        cleanupTrackEntry(_videoTracks.get(key));
        _videoTracks.delete(key);
        if (participantId === _callMeta.localUserId) {
            if (normalizedSource === 'camera') _callMeta.isVideoOn = false;
            if (normalizedSource === 'screenshare') _callMeta.isScreenSharing = false;
        }
    } else {
        cleanupTrackEntry(_videoTracks.get(`${participantId}:camera`));
        cleanupTrackEntry(_videoTracks.get(`${participantId}:screenshare`));
        _videoTracks.delete(`${participantId}:camera`);
        _videoTracks.delete(`${participantId}:screenshare`);
        if (participantId === _callMeta.localUserId) {
            _callMeta.isVideoOn = false;
            _callMeta.isScreenSharing = false;
        }
    }
    refreshAllPipSurfaces();
}

function computePipLayoutSignature(target) {
    if (!target) return '';
    const primaryKey = target.primaryTrack ? `${target.primaryTrack.participantId}:${target.primaryTrack.source}` : '';
    const presenterKey = target.presenterCamTrack ? `${target.presenterCamTrack.participantId}:${target.presenterCamTrack.source}` : '';
    const selfKey = target.selfTrack ? `${target.selfTrack.participantId}:${target.selfTrack.source}` : '';
    const gridKey = (target.gridParticipants || []).map(p => p?.id || '').join(',');
    const stripKey = (target.filmstripParticipants || []).map(p => p?.id || '').join(',');
    return `${target.layoutMode}|${target.mode}|${primaryKey}|${presenterKey}|${selfKey}|${target.speaker?.id || ''}|${gridKey}|${stripKey}`;
}

function applyPipSpeakingClassesOnly(target) {
    if (typeof document !== 'undefined') {
        const overlay = document.getElementById('spokes-voice-pip-overlay');
        if (overlay) {
            const tiles = overlay.querySelectorAll('.pip-participant-tile');
            for (let i = 0; i < tiles.length; i++) {
                const pid = tiles[i].dataset.participantId || '';
                tiles[i].classList.toggle('is-speaking', Boolean(pid && _callMeta.speakingIds.has(pid)));
            }
            const dot = overlay.querySelector('.voice-pip-live-dot');
            if (dot) {
                const isAnySpeaking = target?.speaker?.id
                    ? _callMeta.speakingIds.has(target.speaker.id)
                    : _callMeta.speakingIds.size > 0;
                dot.classList.toggle('is-speaking', Boolean(isAnySpeaking));
            }
            const ring = overlay.querySelector('.voice-pip-avatar-ring');
            if (ring) {
                ring.classList.toggle('is-speaking', Boolean(target?.isSpeaking));
            }
        }
    }

    if (_docPipWindow && !_docPipWindow.closed && _docPipWindow.document) {
        const docTiles = _docPipWindow.document.querySelectorAll('.pip-participant-tile');
        for (let i = 0; i < docTiles.length; i++) {
            const pid = docTiles[i].dataset.participantId || '';
            docTiles[i].classList.toggle('is-speaking', Boolean(pid && _callMeta.speakingIds.has(pid)));
        }
        const docDot = _docPipWindow.document.querySelector('.voice-pip-live-dot');
        if (docDot) {
            docDot.classList.toggle('is-speaking', Boolean(target?.isSpeaking));
        }
    }
}

export function updatePipSpeakingParticipants(speakingIdsArray) {
    const prevSignature = computePipLayoutSignature(selectPipTarget());

    _callMeta.speakingIds.clear();
    const now = Date.now();
    if (Array.isArray(speakingIdsArray)) {
        for (const id of speakingIdsArray) {
            if (id && !String(id).startsWith('bot:')) {
                _callMeta.speakingIds.add(id);
            }
        }
    }

    // Update sticky active speaker with 1.5s hysteresis
    const remoteSpeaking = Array.from(_callMeta.speakingIds).filter(id => id !== _callMeta.localUserId);
    if (remoteSpeaking.length > 0) {
        if (_stickySpeakerId && _callMeta.speakingIds.has(_stickySpeakerId)) {
            _stickySpeakerUntil = now + ACTIVE_SPEAKER_HYSTERESIS_MS;
        } else if (!_stickySpeakerId || now >= _stickySpeakerUntil || !_callMeta.participants.has(_stickySpeakerId)) {
            _stickySpeakerId = remoteSpeaking[0];
            _stickySpeakerUntil = now + ACTIVE_SPEAKER_HYSTERESIS_MS;
        }
    } else if (_callMeta.speakingIds.has(_callMeta.localUserId)) {
        if (!_stickySpeakerId || now >= _stickySpeakerUntil) {
            _stickySpeakerId = _callMeta.localUserId;
            _stickySpeakerUntil = now + ACTIVE_SPEAKER_HYSTERESIS_MS;
        }
    }

    const nextTarget = selectPipTarget();
    const nextSignature = computePipLayoutSignature(nextTarget);
    if (prevSignature === nextSignature) {
        applyPipSpeakingClassesOnly(nextTarget);
        return;
    }

    refreshAllPipSurfaces();
}

export function updatePipCallState(partialState = {}) {
    if (typeof partialState.active === 'boolean') _callMeta.active = partialState.active;
    if (typeof partialState.channelId === 'string') _callMeta.channelId = partialState.channelId;
    if (typeof partialState.channelName === 'string') _callMeta.channelName = partialState.channelName;
    if (typeof partialState.serverName === 'string' && partialState.serverName.trim()) {
        _callMeta.serverName = partialState.serverName.trim();
    }
    if (typeof partialState.localUserId === 'string') _callMeta.localUserId = partialState.localUserId;
    if (typeof partialState.isNativePlatform === 'boolean') _callMeta.isNativePlatform = partialState.isNativePlatform;
    if (typeof partialState.isMuted === 'boolean') _callMeta.isMuted = partialState.isMuted;
    if (typeof partialState.isDeafened === 'boolean') _callMeta.isDeafened = partialState.isDeafened;
    if (typeof partialState.isVideoOn === 'boolean') _callMeta.isVideoOn = partialState.isVideoOn;
    if (typeof partialState.isScreenSharing === 'boolean') _callMeta.isScreenSharing = partialState.isScreenSharing;
    if (typeof partialState.focusedParticipantId === 'string') _callMeta.focusedParticipantId = partialState.focusedParticipantId;

    if (Array.isArray(partialState.participants)) {
        _callMeta.participants.clear();
        for (const p of partialState.participants) {
            if (!p || !p.id) continue;
            const name = p.name || p.id;
            _callMeta.participants.set(p.id, {
                id: p.id,
                name,
                avatarUrl: normalizeAvatarUrl(p.avatarUrl || ''),
                initials: p.initials || computeInitials(name),
                profileColor: p.profileColor || '#673ab7',
                isMuted: Boolean(p.isMuted)
            });
        }
    }

    if (!_callMeta.active) {
        resetPipOnCallEnd();
        return;
    }

    syncMediaSession();
    refreshAllPipSurfaces();
}

export function resetPipOnCallEnd() {
    _callMeta.active = false;
    _callMeta.inAppPipActive = false;
    _callMeta.isVideoOn = false;
    _callMeta.isScreenSharing = false;
    _callMeta.focusedParticipantId = '';
    _callMeta.speakingIds.clear();
    for (const entry of _videoTracks.values()) {
        cleanupTrackEntry(entry);
    }
    _videoTracks.clear();
    _stickySpeakerId = '';
    _stickySpeakerUntil = 0;

    closeOsPip();
    _isIosOsPipActive = false;
    _callMeta.isNativePlatform = false;
    clearMediaSession();
    detachInAppPipVideos();

    if (_isNativeMobilePip) {
        setNativePipModeInternal(false);
    }

    pushNativeMobileCallMetadata(true);
}

function resolveDisplayParticipant() {
    if (_callMeta.focusedParticipantId && _callMeta.participants.has(_callMeta.focusedParticipantId)) {
        return _callMeta.participants.get(_callMeta.focusedParticipantId);
    }
    if (_stickySpeakerId && _callMeta.participants.has(_stickySpeakerId)) {
        return _callMeta.participants.get(_stickySpeakerId);
    }
    for (const [id, info] of _callMeta.participants.entries()) {
        if (id !== _callMeta.localUserId) return info;
    }
    if (_callMeta.localUserId && _callMeta.participants.has(_callMeta.localUserId)) {
        return _callMeta.participants.get(_callMeta.localUserId);
    }
    const fallbackName = _callMeta.channelName || 'Voice Call';
    return {
        id: _callMeta.localUserId || '',
        name: fallbackName,
        avatarUrl: '',
        initials: computeInitials(fallbackName),
        isMuted: _callMeta.isMuted
    };
}

function resolveTrackIntrinsicDimensions(entry) {
    if (!entry) return { width: 0, height: 0 };
    if (typeof document !== 'undefined') {
        const domId = entry.source === 'screenshare'
            ? `participant-screen-${entry.participantId}`
            : `participant-video-${entry.participantId}`;
        const stageVideo = document.getElementById(domId);
        if (stageVideo && stageVideo.videoWidth > 0 && stageVideo.videoHeight > 0) {
            entry.width = stageVideo.videoWidth;
            entry.height = stageVideo.videoHeight;
            return { width: entry.width, height: entry.height };
        }
        const pipPrimary = document.getElementById('spokes-pip-primary-video');
        if (pipPrimary && pipPrimary.srcObject?.getVideoTracks?.()?.[0] === entry.mediaStreamTrack && pipPrimary.videoWidth > 0 && pipPrimary.videoHeight > 0) {
            entry.width = pipPrimary.videoWidth;
            entry.height = pipPrimary.videoHeight;
            return { width: entry.width, height: entry.height };
        }
        const pipSelf = document.getElementById('spokes-pip-self-video');
        if (pipSelf && pipSelf.srcObject?.getVideoTracks?.()?.[0] === entry.mediaStreamTrack && pipSelf.videoWidth > 0 && pipSelf.videoHeight > 0) {
            entry.width = pipSelf.videoWidth;
            entry.height = pipSelf.videoHeight;
            return { width: entry.width, height: entry.height };
        }
        if (_docPipRefs?.primaryVideo && _docPipRefs.primaryVideo.srcObject?.getVideoTracks?.()?.[0] === entry.mediaStreamTrack && _docPipRefs.primaryVideo.videoWidth > 0 && _docPipRefs.primaryVideo.videoHeight > 0) {
            entry.width = _docPipRefs.primaryVideo.videoWidth;
            entry.height = _docPipRefs.primaryVideo.videoHeight;
            return { width: entry.width, height: entry.height };
        }
        if (_docPipRefs?.selfVideo && _docPipRefs.selfVideo.srcObject?.getVideoTracks?.()?.[0] === entry.mediaStreamTrack && _docPipRefs.selfVideo.videoWidth > 0 && _docPipRefs.selfVideo.videoHeight > 0) {
            entry.width = _docPipRefs.selfVideo.videoWidth;
            entry.height = _docPipRefs.selfVideo.videoHeight;
            return { width: entry.width, height: entry.height };
        }
    }
    const lkDims = entry.publication?.videoDimensions || entry.publication?.dimensions || entry.livekitTrack?.dimensions;
    if (lkDims && lkDims.width > 0 && lkDims.height > 0) {
        entry.width = lkDims.width;
        entry.height = lkDims.height;
        return { width: entry.width, height: entry.height };
    }
    try {
        const settings = entry.mediaStreamTrack?.getSettings?.() || {};
        if (settings.width > 0 && settings.height > 0) {
            entry.width = settings.width;
            entry.height = settings.height;
            return { width: entry.width, height: entry.height };
        }
    } catch (e) {}
    return { width: entry.width || 0, height: entry.height || 0 };
}

export function selectPipTarget() {
    const aliveTracks = [];
    for (const [key, entry] of _videoTracks.entries()) {
        if (!isPipTrackAlive(entry)) {
            _videoTracks.delete(key);
            continue;
        }
        aliveTracks.push(entry);
    }

    const resolveKnownParticipant = (participantId) => {
        if (!participantId) return null;
        if (_callMeta.participants.has(participantId)) {
            return _callMeta.participants.get(participantId);
        }
        const fallbackName = _callMeta.channelName || 'Voice Call';
        return {
            id: participantId,
            name: fallbackName,
            avatarUrl: '',
            initials: computeInitials(fallbackName),
            profileColor: '#673ab7',
            isMuted: participantId === _callMeta.localUserId ? Boolean(_callMeta.isMuted) : false
        };
    };

    const findTrack = (predicate) => aliveTracks.find(predicate) || null;
    const isSingleStagePip = Boolean(_isNativeMobilePip || _isIosOsPipActive || _callMeta.isNativePlatform);
    const hasParticipant = (id) => Boolean(id && (_callMeta.participants.has(id) || aliveTracks.some(t => t.participantId === id)));
    const focusedId = _callMeta.focusedParticipantId && hasParticipant(_callMeta.focusedParticipantId)
        ? _callMeta.focusedParticipantId
        : '';

    const canUseGrid = !isSingleStagePip && _callMeta.participants.size > 1;
    const layoutMode = (!focusedId && canUseGrid) ? 'grid' : 'focus';

    let primary = null;
    if (focusedId) {
        // In multi-tile PiP (Desktop / Web / In-App), the maximized stage belongs strictly to the focused
        // participant (their screen share, camera, or avatar card). Never hijack another user's video track.
        primary = findTrack(t => t.participantId === focusedId && t.source === 'screenshare')
            || findTrack(t => t.participantId === focusedId && t.source === 'camera');
    }
    if (!primary && (!focusedId || isSingleStagePip)) {
        // 1. Any remote screen share
        primary = findTrack(t => !t.isLocal && t.source === 'screenshare')
            // 2. Sticky active speaker camera (remote)
            || (_stickySpeakerId && _stickySpeakerId !== _callMeta.localUserId
                ? findTrack(t => !t.isLocal && t.participantId === _stickySpeakerId && t.source === 'camera')
                : null)
            // 3. Any remote camera
            || findTrack(t => !t.isLocal && t.source === 'camera')
            // 4. Local screen share
            || findTrack(t => t.isLocal && t.source === 'screenshare')
            // 5. In native mobile PiP or solo call, promote local camera so self-preview fills the PiP window
            || ((isSingleStagePip || _callMeta.participants.size <= 1) ? findTrack(t => t.isLocal && t.source === 'camera') : null);
    }

    // When the primary feed is a screen share, overlay the streamer's webcam on the stream
    const presenterCamTrack = primary?.source === 'screenshare'
        ? findTrack(t => t.participantId === primary.participantId && t.source === 'camera')
        : null;

    const speaker = (focusedId && !isSingleStagePip)
        ? (resolveKnownParticipant(focusedId) || resolveDisplayParticipant())
        : (primary
            ? (resolveKnownParticipant(primary.participantId) || resolveDisplayParticipant())
            : (focusedId ? (resolveKnownParticipant(focusedId) || resolveDisplayParticipant()) : resolveDisplayParticipant()));

    const primaryParticipantId = (focusedId && !isSingleStagePip)
        ? focusedId
        : (primary?.participantId || focusedId || speaker?.id || '');
    const localCam = findTrack(t => t.isLocal && t.source === 'camera');
    const isDesktopDocPipOpen = Boolean(_docPipWindow && !_docPipWindow.closed);
    const useFaceTimeSelfCorner = Boolean(
        localCam
        && primary
        && primary !== localCam
        && presenterCamTrack !== localCam
        && (isSingleStagePip || (!isDesktopDocPipOpen && _callMeta.participants.size === 2 && primaryParticipantId !== _callMeta.localUserId))
    );

    const filmstripParticipants = [];
    if (layoutMode === 'focus' && !isSingleStagePip && _callMeta.participants.size > 1) {
        for (const p of _callMeta.participants.values()) {
            if (!p || !p.id) continue;
            if (p.id === primaryParticipantId) continue;
            if (useFaceTimeSelfCorner && p.id === _callMeta.localUserId) continue;
            filmstripParticipants.push(p);
        }
    }

    const selfCamera = useFaceTimeSelfCorner ? localCam : null;

    const gridParticipants = layoutMode === 'grid'
        ? Array.from(_callMeta.participants.values())
        : [];

    return {
        layoutMode,
        mode: primary ? 'video' : 'avatar',
        primaryTrack: primary,
        presenterCamTrack,
        selfTrack: selfCamera,
        filmstripParticipants,
        gridParticipants,
        speaker,
        isSpeaking: Boolean(speaker?.id && _callMeta.speakingIds.has(speaker.id)),
        isSpeakerMuted: speaker?.id === _callMeta.localUserId ? _callMeta.isMuted : Boolean(speaker?.isMuted)
    };
}

export function setPipFocusedParticipant(participantId) {
    const normalized = participantId && _callMeta.participants.has(participantId) ? participantId : '';
    _callMeta.focusedParticipantId = normalized;
    refreshAllPipSurfaces();
    if (_dotnetHelper) {
        _dotnetHelper.invokeMethodAsync('OnPipFocusedParticipantChanged', normalized || null).catch(() => {});
    }
}

export function togglePipLayoutMode() {
    if (_callMeta.focusedParticipantId) {
        setPipFocusedParticipant('');
    } else {
        let nextFocusId = '';
        for (const entry of _videoTracks.values()) {
            if (!entry.isLocal && entry.source === 'screenshare' && entry.mediaStreamTrack?.readyState !== 'ended') {
                nextFocusId = entry.participantId;
                break;
            }
        }
        if (!nextFocusId && _stickySpeakerId && _callMeta.participants.has(_stickySpeakerId) && _stickySpeakerId !== _callMeta.localUserId) {
            nextFocusId = _stickySpeakerId;
        }
        if (!nextFocusId) {
            for (const id of _callMeta.participants.keys()) {
                if (id !== _callMeta.localUserId) {
                    nextFocusId = id;
                    break;
                }
            }
        }
        if (!nextFocusId && _callMeta.localUserId) {
            nextFocusId = _callMeta.localUserId;
        }
        setPipFocusedParticipant(nextFocusId);
    }
}

function syncVideoIntrinsicAspectRatio(videoEl, trackEntry = null) {
    if (!videoEl) return;
    let vw = videoEl.videoWidth || 0;
    let vh = videoEl.videoHeight || 0;
    if ((vw <= 0 || vh <= 0) && trackEntry) {
        const dims = resolveTrackIntrinsicDimensions(trackEntry);
        vw = dims.width || 0;
        vh = dims.height || 0;
    }
    if (vw > 0 && vh > 0) {
        const aspect = vw / vh;
        if (Number.isFinite(aspect) && aspect > 0.1 && aspect < 10) {
            videoEl.style.setProperty('--self-aspect', aspect.toFixed(4));
        }
    }
}

function ensurePipVideoElementListeners(videoEl) {
    if (!videoEl || videoEl._spokesPipListenersBound) return;
    videoEl._spokesPipListenersBound = true;

    const onVideoMetaOrResize = () => {
        if (videoEl.paused && videoEl.srcObject) {
            videoEl.play().catch(() => {});
        }
        const activeTrack = videoEl.srcObject?.getVideoTracks?.()?.[0];
        if (!activeTrack) return;
        const vw = videoEl.videoWidth || 0;
        const vh = videoEl.videoHeight || 0;
        if (vw > 0 && vh > 0) {
            syncVideoIntrinsicAspectRatio(videoEl);
            let changed = false;
            for (const entry of _videoTracks.values()) {
                if (entry.mediaStreamTrack === activeTrack) {
                    if (entry.width !== vw || entry.height !== vh) {
                        entry.width = vw;
                        entry.height = vh;
                        changed = true;
                    }
                }
            }
            if (changed && _callMeta.active) {
                pushNativeMobileCallMetadata(false);
            }
        }
    };

    videoEl.addEventListener('loadedmetadata', onVideoMetaOrResize);
    videoEl.addEventListener('loadeddata', onVideoMetaOrResize);
    videoEl.addEventListener('playing', onVideoMetaOrResize);
    videoEl.addEventListener('resize', onVideoMetaOrResize);
}

function isElementInsideInAppPip(el) {
    if (!el) return false;
    if (
        el.id === 'spokes-pip-primary-video'
        || el.id === 'spokes-pip-self-video'
        || el.id === 'spokes-pip-presenter-cam-video'
    ) {
        return true;
    }
    return Boolean(typeof el.closest === 'function' && el.closest('#spokes-voice-pip-overlay'));
}

function detachNonPipElementsFromTrack(entry, keepEl1 = null, keepEl2 = null) {
    if (!entry?.livekitTrack) return;
    const lkTrack = entry.livekitTrack;
    if (Array.isArray(lkTrack.attachedElements)) {
        const toDetach = lkTrack.attachedElements.filter(
            el => el && el !== keepEl1 && el !== keepEl2 && !isElementInsideInAppPip(el)
        );
        for (const el of toDetach) {
            try { lkTrack.detach(el); } catch (e) {}
            try {
                if (el.srcObject) {
                    el.pause?.();
                    el.srcObject = null;
                }
                el.classList?.remove?.('has-stream');
            } catch (e) {}
        }
    }
}

function attachMediaTrackToVideo(videoEl, trackEntry, shouldMirror = false) {
    if (!videoEl) return;
    ensurePipVideoElementListeners(videoEl);
    if (!isPipTrackAlive(trackEntry)) {
        if (videoEl._spokesAttachedLkTrack && typeof videoEl._spokesAttachedLkTrack.detach === 'function') {
            try { videoEl._spokesAttachedLkTrack.detach(videoEl); } catch (e) {}
            videoEl._spokesAttachedLkTrack = null;
        }
        if (videoEl.srcObject) {
            videoEl.pause();
            videoEl.srcObject = null;
        }
        delete videoEl.dataset.nativeIdentity;
        delete videoEl.dataset.nativeSource;
        videoEl.style.removeProperty('--self-aspect');
        videoEl.classList.remove('has-stream', 'spokes-pip-mirror', 'native-video-active');
        if (typeof _actions.onSyncNativeVideoSlots === 'function') {
            try { _actions.onSyncNativeVideoSlots(); } catch (e) {}
        }
        return;
    }

    if (trackEntry.isNative) {
        if (videoEl._spokesAttachedLkTrack && typeof videoEl._spokesAttachedLkTrack.detach === 'function') {
            try { videoEl._spokesAttachedLkTrack.detach(videoEl); } catch (e) {}
            videoEl._spokesAttachedLkTrack = null;
        }
        if (videoEl.srcObject) {
            videoEl.pause();
            videoEl.srcObject = null;
        }
        syncVideoIntrinsicAspectRatio(videoEl, trackEntry);
        videoEl.dataset.nativeIdentity = trackEntry.participantId;
        videoEl.dataset.nativeSource = trackEntry.source;
        videoEl.classList.add('has-stream', 'native-video-active');
        videoEl.classList.toggle('spokes-pip-mirror', Boolean(shouldMirror));
        if (typeof _actions.onSyncNativeVideoSlots === 'function') {
            try { _actions.onSyncNativeVideoSlots(); } catch (e) {}
        }
        return;
    }

    delete videoEl.dataset.nativeIdentity;
    delete videoEl.dataset.nativeSource;
    videoEl.classList.remove('native-video-active');

    syncVideoIntrinsicAspectRatio(videoEl, trackEntry);
    videoEl.style.opacity = '1';
    videoEl.muted = true;
    videoEl.defaultMuted = true;
    videoEl.playsInline = true;
    videoEl.autoplay = true;
    videoEl.setAttribute('muted', '');
    videoEl.setAttribute('playsinline', '');
    videoEl.setAttribute('webkit-playsinline', '');
    if (videoEl !== _fallbackPipVideo) {
        videoEl.disablePictureInPicture = true;
        videoEl.setAttribute('disablepictureinpicture', '');
    }
    videoEl.classList.add('has-stream');
    videoEl.classList.toggle('spokes-pip-mirror', Boolean(shouldMirror));

    let trackChanged = false;
    const isInAppPipVideo = isElementInsideInAppPip(videoEl);
    if (isInAppPipVideo && trackEntry.livekitTrack && typeof trackEntry.livekitTrack.attach === 'function') {
        if (videoEl._spokesAttachedLkTrack && videoEl._spokesAttachedLkTrack !== trackEntry.livekitTrack) {
            try { videoEl._spokesAttachedLkTrack.detach(videoEl); } catch (e) {}
            videoEl._spokesAttachedLkTrack = null;
            trackChanged = true;
        }

        const existingTrack = videoEl.srcObject?.getVideoTracks?.()?.[0];
        const isAttachedToLk = Array.isArray(trackEntry.livekitTrack.attachedElements)
            && trackEntry.livekitTrack.attachedElements.includes(videoEl);
        if (!isAttachedToLk || existingTrack !== trackEntry.mediaStreamTrack) {
            trackChanged = true;
            try {
                trackEntry.livekitTrack.attach(videoEl);
                videoEl._spokesAttachedLkTrack = trackEntry.livekitTrack;
            } catch (e) {}
        }
        if (videoEl.srcObject?.getVideoTracks?.()?.[0] !== trackEntry.mediaStreamTrack) {
            trackChanged = true;
            const StreamCtor = videoEl.ownerDocument?.defaultView?.MediaStream || MediaStream;
            videoEl.srcObject = new StreamCtor([trackEntry.mediaStreamTrack]);
        }

        const isStageMounted = typeof document !== 'undefined' && Boolean(document.getElementById('voice-video-container'));
        if (_isNativeMobilePip || !isStageMounted) {
            const primaryEl = typeof document !== 'undefined' ? document.getElementById('spokes-pip-primary-video') : null;
            const selfEl = typeof document !== 'undefined' ? document.getElementById('spokes-pip-self-video') : null;
            detachNonPipElementsFromTrack(trackEntry, primaryEl, selfEl);
        }
        ensureTrackSubscribed(trackEntry);

        videoEl.muted = true;
        videoEl.playsInline = true;
    } else {
        const existingTrack = videoEl.srcObject?.getVideoTracks?.()?.[0];
        if (existingTrack !== trackEntry.mediaStreamTrack) {
            trackChanged = true;
            const StreamCtor = videoEl.ownerDocument?.defaultView?.MediaStream || MediaStream;
            videoEl.srcObject = new StreamCtor([trackEntry.mediaStreamTrack]);
        }
        ensureTrackSubscribed(trackEntry);
    }

    syncVideoIntrinsicAspectRatio(videoEl, trackEntry);
    if (videoEl.paused) {
        videoEl.play().catch(() => {});
    }
    if (trackChanged || videoEl.paused) {
        setTimeout(() => {
            if (videoEl.srcObject) {
                syncVideoIntrinsicAspectRatio(videoEl, trackEntry);
                if (videoEl.paused) {
                    videoEl.muted = true;
                    videoEl.play().catch(() => {});
                }
            }
        }, 60);
    }
}

function syncPipTileCollection(containerEl, participants) {
    if (!containerEl) return;
    const list = Array.isArray(participants) ? participants : [];
    const desiredIds = new Set(list.map(p => p?.id).filter(Boolean));
    const existingByParticipantId = new Map();

    const existingTiles = Array.from(containerEl.querySelectorAll(':scope > .pip-participant-tile'));
    for (const tile of existingTiles) {
        const pid = tile.dataset.participantId || '';
        if (!desiredIds.has(pid)) {
            tile.querySelectorAll('video').forEach(v => attachMediaTrackToVideo(v, null, false));
            tile.remove();
        } else {
            existingByParticipantId.set(pid, tile);
        }
    }

    containerEl.dataset.count = String(list.length);
    const doc = containerEl.ownerDocument || document;

    for (const p of list) {
        if (!p || !p.id) continue;
        let tile = existingByParticipantId.get(p.id) || null;
        if (!tile) {
            tile = doc.createElement('div');
            tile.className = 'pip-participant-tile voice-pip-no-drag';
            tile.dataset.participantId = p.id;
            tile.innerHTML = `
                <video class="pip-tile-video" autoplay playsinline muted disablepictureinpicture></video>
                <video class="pip-tile-presenter-cam" autoplay playsinline muted disablepictureinpicture></video>
                <div class="pip-tile-avatar-wrap">
                    <span class="pip-tile-initials"></span>
                    <img class="pip-tile-avatar-img" alt="" />
                    <span class="pip-tile-mute">${SVG_ICONS.micOff}</span>
                </div>
                <div class="pip-tile-footer">
                    <span class="pip-tile-name"></span>
                </div>
            `;
            const avatarImg = tile.querySelector('.pip-tile-avatar-img');
            const initialsEl = tile.querySelector('.pip-tile-initials');
            avatarImg.addEventListener('load', () => {
                avatarImg.style.display = 'block';
                initialsEl.style.display = 'none';
            });
            avatarImg.addEventListener('error', () => {
                avatarImg.style.display = 'none';
                initialsEl.style.display = 'flex';
            });
            tile.addEventListener('click', (e) => {
                e.stopPropagation();
                const pid = tile.dataset.participantId;
                if (pid) {
                    setPipFocusedParticipant(pid);
                }
            });
            containerEl.appendChild(tile);
        }

        const screenEntry = _videoTracks.get(`${p.id}:screenshare`);
        const camEntry = _videoTracks.get(`${p.id}:camera`);
        const isTileVideoRenderable = (entry) => Boolean(
            entry && !entry.isNative && entry.mediaStreamTrack && entry.mediaStreamTrack.readyState !== 'ended'
        );
        const aliveScreen = isTileVideoRenderable(screenEntry) ? screenEntry : null;
        const aliveCam = isTileVideoRenderable(camEntry) ? camEntry : null;
        const primaryEntry = aliveScreen || aliveCam || null;
        // Unified stream + streamer webcam overlay: when streaming, overlay the streamer's webcam
        const presenterCamEntry = (aliveScreen && aliveCam) ? aliveCam : null;

        const isSpeaking = _callMeta.speakingIds.has(p.id);
        const isMuted = p.id === _callMeta.localUserId ? _callMeta.isMuted : Boolean(p.isMuted);

        tile.classList.toggle('is-speaking', isSpeaking);
        tile.classList.toggle('has-video', Boolean(primaryEntry));
        tile.classList.toggle('is-screenshare', Boolean(aliveScreen));
        tile.title = p.name || '';

        const tileVideo = tile.querySelector('.pip-tile-video');
        const tilePresenterCam = tile.querySelector('.pip-tile-presenter-cam');
        attachMediaTrackToVideo(tileVideo, primaryEntry, shouldMirrorEntry(primaryEntry));
        attachMediaTrackToVideo(tilePresenterCam, presenterCamEntry, shouldMirrorEntry(presenterCamEntry));

        const avatarWrap = tile.querySelector('.pip-tile-avatar-wrap');
        const nameEl = tile.querySelector('.pip-tile-name');
        const initialsEl = tile.querySelector('.pip-tile-initials');
        const avatarImg = tile.querySelector('.pip-tile-avatar-img');
        const muteEl = tile.querySelector('.pip-tile-mute');

        if (avatarWrap) avatarWrap.style.setProperty('--avatar-bg', p.profileColor || '#673ab7');
        if (nameEl) nameEl.textContent = p.name || '';
        if (initialsEl) initialsEl.textContent = p.initials || computeInitials(p.name || '');
        if (muteEl) muteEl.classList.toggle('visible', isMuted);

        const nextAvatar = p.avatarUrl || '';
        if (avatarImg && tile.dataset.loadedAvatar !== nextAvatar) {
            tile.dataset.loadedAvatar = nextAvatar;
            if (nextAvatar) {
                avatarImg.src = nextAvatar;
            } else {
                avatarImg.removeAttribute('src');
                avatarImg.style.display = 'none';
                if (initialsEl) initialsEl.style.display = 'flex';
            }
        }
    }
}

function getInAppPipSlots() {
    return {
        primaryVideo: document.getElementById('spokes-pip-primary-video'),
        presenterCamVideo: document.getElementById('spokes-pip-presenter-cam-video'),
        selfVideo: document.getElementById('spokes-pip-self-video'),
        gridContainer: document.getElementById('spokes-pip-grid-container'),
        filmstripContainer: document.getElementById('spokes-pip-filmstrip')
    };
}

function applyPipStageMedia(slots, targetOrNull, isGridMode = false, hasFilmstrip = false, requirePrimaryForOverlayTracks = false) {
    if (!targetOrNull || isGridMode) {
        attachMediaTrackToVideo(slots.primaryVideo, null, false);
        attachMediaTrackToVideo(slots.presenterCamVideo, null, false);
        attachMediaTrackToVideo(slots.selfVideo, null, false);
        syncPipTileCollection(slots.gridContainer, targetOrNull ? targetOrNull.gridParticipants : []);
        syncPipTileCollection(slots.filmstripContainer, []);
        return;
    }

    const primaryTrack = targetOrNull.primaryTrack || null;
    const presenterCamTrack = (!requirePrimaryForOverlayTracks || primaryTrack) ? (targetOrNull.presenterCamTrack || null) : null;
    const selfTrack = (!requirePrimaryForOverlayTracks || primaryTrack) ? (targetOrNull.selfTrack || null) : null;

    attachMediaTrackToVideo(slots.primaryVideo, primaryTrack, shouldMirrorEntry(primaryTrack));
    attachMediaTrackToVideo(slots.presenterCamVideo, presenterCamTrack, shouldMirrorEntry(presenterCamTrack));
    attachMediaTrackToVideo(slots.selfVideo, selfTrack, shouldMirrorEntry(selfTrack));
    syncPipTileCollection(slots.gridContainer, []);
    syncPipTileCollection(slots.filmstripContainer, hasFilmstrip ? targetOrNull.filmstripParticipants : []);
}

function detachInAppPipVideos() {
    if (typeof document === 'undefined') return;
    applyPipStageMedia(getInAppPipSlots(), null);
}

function shouldMirrorEntry(entry) {
    if (!entry || !entry.isLocal || entry.source !== 'camera') return false;
    if (entry.facingMode === 'environment') return false;
    try {
        const facing = entry.mediaStreamTrack?.getSettings?.()?.facingMode;
        if (facing === 'environment') return false;
    } catch (e) {}
    if (typeof window !== 'undefined' && window._spokesLocalCameraFacing === 'environment') return false;
    return true;
}

export function setInAppPipActive(active) {
    const next = Boolean(active);
    const changed = _callMeta.inAppPipActive !== next;
    _callMeta.inAppPipActive = next;
    if (_callMeta.inAppPipActive || _isNativeMobilePip) {
        if (_isNativeMobilePip) {
            detachStageVideosForExternalPip();
        }
        syncInAppPipElements();
        syncPipTrackSubscriptions(selectPipTarget());
        requestAnimationFrame(() => {
            syncInAppPipElements();
            syncPipTrackSubscriptions(selectPipTarget());
            bindInAppPipDrag('spokes-voice-pip-overlay');
        });
        setTimeout(() => {
            if (_callMeta.inAppPipActive || _isNativeMobilePip) {
                syncInAppPipElements();
                syncPipTrackSubscriptions(selectPipTarget());
            }
        }, 90);
    } else {
        syncPipTrackSubscriptions(selectPipTarget());
        detachInAppPipVideos();
        if (_dragCleanup) {
            _dragCleanup();
            _dragCleanup = null;
            _boundOverlayEl = null;
        }
        if (changed && typeof _actions.onRestoreStageTracks === 'function') {
            requestAnimationFrame(() => {
                if (!_callMeta.inAppPipActive && !_isNativeMobilePip) {
                    try { _actions.onRestoreStageTracks(); } catch (e) {}
                }
            });
        }
    }
}

export function syncInAppPipElements() {
    if (typeof document === 'undefined') return;
    const overlay = document.getElementById('spokes-voice-pip-overlay');
    const slots = getInAppPipSlots();
    if (!slots.primaryVideo && !slots.selfVideo && !overlay) return;

    if (!_callMeta.inAppPipActive && !_isNativeMobilePip) {
        applyPipStageMedia(slots, null);
        return;
    }

    const isHostViewVisible = typeof window === 'undefined' || window._spokesHostViewVisible !== false;
    const isMinimizedPill = Boolean(overlay?.classList.contains('is-minimized-pill')) && !_isNativeMobilePip;
    const shouldRenderMedia = (isHostViewVisible && _callMeta.inAppPipActive && !isMinimizedPill) || _isNativeMobilePip;

    if (!shouldRenderMedia) {
        applyPipStageMedia(slots, null);
        if (_callMeta.inAppPipActive && overlay && overlay !== _boundOverlayEl) {
            bindInAppPipDrag('spokes-voice-pip-overlay');
        }
        return;
    }

    const target = selectPipTarget();
    const isGridMode = target.layoutMode === 'grid' && !_isNativeMobilePip && !_callMeta.isNativePlatform;
    const hasFilmstrip = !isGridMode && !_isNativeMobilePip && !_callMeta.isNativePlatform && target.filmstripParticipants.length > 0;

    // Blazor (<VoicePipOverlay />) exclusively owns #spokes-voice-pip-overlay's class list;
    // voicePip.js exclusively binds streams inside the ShouldRender()=>false <VoicePipVideoSlot />.
    applyPipStageMedia(slots, target, isGridMode, hasFilmstrip, true);

    if (overlay && overlay !== _boundOverlayEl) {
        bindInAppPipDrag('spokes-voice-pip-overlay');
    }
}

function applySavedCorner(overlayEl, corner) {
    if (!overlayEl) return;
    overlayEl.dataset.corner = corner;
    overlayEl.style.transform = '';
    overlayEl.style.left = '';
    overlayEl.style.right = '';
    overlayEl.style.top = '';
    overlayEl.style.bottom = '';
}

export function bindInAppPipDrag(overlayId = 'spokes-voice-pip-overlay') {
    const overlayEl = document.getElementById(overlayId);
    if (!overlayEl) return;
    if (_boundOverlayEl === overlayEl) return;

    if (_dragCleanup) {
        _dragCleanup();
        _dragCleanup = null;
    }

    _boundOverlayEl = overlayEl;
    let savedCorner = 'bottom-right';
    try {
        savedCorner = localStorage.getItem(PIP_CORNER_STORAGE_KEY) || 'bottom-right';
    } catch (e) {}
    if (!['bottom-right', 'bottom-left', 'top-right', 'top-left'].includes(savedCorner)) {
        savedCorner = 'bottom-right';
    }
    applySavedCorner(overlayEl, savedCorner);

    let pointerId = null;
    let startX = 0;
    let startY = 0;
    let baseLeft = 0;
    let baseTop = 0;
    let dragging = false;

    const onPointerDown = (e) => {
        if (_isNativeMobilePip) return;
        if (e.button !== undefined && e.button !== 0) return;
        if (e.target?.closest?.('button, a, .voice-pip-no-drag')) return;

        const rect = overlayEl.getBoundingClientRect();
        pointerId = e.pointerId;
        startX = e.clientX;
        startY = e.clientY;
        baseLeft = rect.left;
        baseTop = rect.top;
        dragging = false;

        try { overlayEl.setPointerCapture(pointerId); } catch (err) {}
    };

    const onPointerMove = (e) => {
        if (pointerId === null || e.pointerId !== pointerId) return;
        const dx = e.clientX - startX;
        const dy = e.clientY - startY;

        if (!dragging && Math.hypot(dx, dy) < 6) return;
        if (!dragging) {
            dragging = true;
            overlayEl.classList.add('is-dragging');
            overlayEl.style.right = 'auto';
            overlayEl.style.bottom = 'auto';
        }

        const vw = window.innerWidth;
        const vh = window.innerHeight;
        const rect = overlayEl.getBoundingClientRect();
        const clampedLeft = Math.max(8, Math.min(vw - rect.width - 8, baseLeft + dx));
        const clampedTop = Math.max(8, Math.min(vh - rect.height - 8, baseTop + dy));
        overlayEl.style.left = `${clampedLeft}px`;
        overlayEl.style.top = `${clampedTop}px`;
    };

    const finishDrag = (e) => {
        if (pointerId === null || (e && e.pointerId !== pointerId)) return;
        try { overlayEl.releasePointerCapture(pointerId); } catch (err) {}
        pointerId = null;

        if (!dragging) return;
        dragging = false;
        overlayEl.classList.remove('is-dragging');

        const rect = overlayEl.getBoundingClientRect();
        const centerX = rect.left + rect.width / 2;
        const centerY = rect.top + rect.height / 2;
        const isRight = centerX >= window.innerWidth / 2;
        const isBottom = centerY >= window.innerHeight / 2;
        const nextCorner = `${isBottom ? 'bottom' : 'top'}-${isRight ? 'right' : 'left'}`;

        try { localStorage.setItem(PIP_CORNER_STORAGE_KEY, nextCorner); } catch (err) {}
        applySavedCorner(overlayEl, nextCorner);
    };

    overlayEl.addEventListener('pointerdown', onPointerDown);
    overlayEl.addEventListener('pointermove', onPointerMove);
    overlayEl.addEventListener('pointerup', finishDrag);
    overlayEl.addEventListener('pointercancel', finishDrag);

    _dragCleanup = () => {
        overlayEl.removeEventListener('pointerdown', onPointerDown);
        overlayEl.removeEventListener('pointermove', onPointerMove);
        overlayEl.removeEventListener('pointerup', finishDrag);
        overlayEl.removeEventListener('pointercancel', finishDrag);
    };
}

// ============================================================================
// Desktop (Electron 45) & Web Browser OS Picture-in-Picture (Spokes Theme)
// ============================================================================

export function isOsPipSupported() {
    if (isDesktopApp()) {
        return true;
    }
    if (window.SpokesNative && typeof window.SpokesNative.enterPictureInPicture === 'function') {
        return true;
    }
    if (typeof window !== 'undefined' && 'documentPictureInPicture' in window) {
        return true;
    }
    if (typeof document !== 'undefined' && document.pictureInPictureEnabled) {
        return true;
    }
    return false;
}

function notifyOsPipChanged(isOpen) {
    if (_dotnetHelper) {
        _dotnetHelper.invokeMethodAsync('OnOsPipStateChanged', Boolean(isOpen)).catch(() => {});
    }
}

const SVG_ICONS = {
    micOn: '<svg viewBox="0 0 24 24" width="18" height="18" fill="currentColor"><path d="M12 14c1.66 0 3-1.34 3-3V5c0-1.66-1.34-3-3-3S9 3.34 9 5v6c0 1.66 1.34 3 3 3zm5.91-3c-.49 0-.9.36-.98.85C16.52 14.2 14.47 16 12 16s-4.52-1.8-4.93-4.15a.998.998 0 0 0-.98-.85c-.61 0-1.09.54-1 1.14.49 3 2.89 5.35 5.91 5.78V20c0 .55.45 1 1 1s1-.45 1-1v-2.08c3.02-.43 5.42-2.78 5.91-5.78.1-.6-.39-1.14-1-1.14z"/></svg>',
    micOff: '<svg viewBox="0 0 24 24" width="18" height="18" fill="currentColor"><path d="M19 11h-1.7c0 .74-.16 1.43-.43 2.05l1.23 1.23c.56-.98.9-2.09.9-3.28zm-4.02.17c0-.06.02-.11.02-.17V5c0-1.66-1.34-3-3-3S9 3.34 9 5v.18l5.98 5.99zM4.27 3L3 4.27l6.01 6.01V11c0 1.66 1.33 3 2.99 3 .22 0 .44-.03.65-.08l1.66 1.66c-.71.33-1.5.52-2.31.52-2.76 0-5.3-2.1-5.3-5.1H5c0 3.41 2.72 6.23 6 6.72V21h2v-3.28c.91-.13 1.77-.45 2.54-.9L19.73 21 21 19.73 4.27 3z"/></svg>',
    camOn: '<svg viewBox="0 0 24 24" width="18" height="18" fill="currentColor"><path d="M17 10.5V7c0-.55-.45-1-1-1H4c-.55 0-1 .45-1 1v10c0 .55.45 1 1 1h12c.55 0 1-.45 1-1v-3.5l4 4v-11l-4 4z"/></svg>',
    camOff: '<svg viewBox="0 0 24 24" width="18" height="18" fill="currentColor"><path d="M21 6.5l-4 4V7c0-.55-.45-1-1-1H9.82L21 17.18V6.5zM3.27 2L2 3.27 4.73 6H4c-.55 0-1 .45-1 1v10c0 .55.45 1 1 1h12c.21 0 .39-.08.54-.18L19.73 21 21 19.73 3.27 2z"/></svg>',
    screenOn: '<svg viewBox="0 0 24 24" width="18" height="18" fill="currentColor"><path d="M20 18c1.1 0 1.99-.9 1.99-2L22 6c0-1.11-.9-2-2-2H4c-1.11 0-2 .89-2 2v10c0 1.1.89 2 2 2H0v2h24v-2h-4zm-7-3.53v-2.19c-2.78 0-4.61.85-6 2.72.56-2.67 2.11-5.33 6-5.87V7l4 3.73-4 3.74z"/></svg>',
    screenOff: '<svg viewBox="0 0 24 24" width="18" height="18" fill="currentColor"><path d="M21.22 18.02l2 2H24v-2h-2.78zm.77-2l.01-10a2 2 0 0 0-2-2H7.22l5.23 5.23c.18-.04.36-.07.55-.1V7l4 3.73-1.58 1.47 5.54 5.54c.61-.33 1.03-.99 1.03-1.72zM2.39 1.73L1.11 3l1.54 1.54c-.4.36-.65.89-.65 1.46v10a2 2 0 0 0 2 2H0v2h18.13l2.71 2.71 1.27-1.27L2.39 1.73zM7 15.02c.31-1.48.92-2.95 2.07-4.06l1.59 1.59c-1.54.38-2.7 1.18-3.66 2.47z"/></svg>',
    gridView: '<svg viewBox="0 0 24 24" width="16" height="16" fill="currentColor"><path d="M3 3v8h8V3H3zm6 6H5V5h4v4zm-6 4v8h8v-8H3zm6 6H5v-4h4v4zm4-16v8h8V3h-8zm6 6h-4V5h4v4zm-6 4v8h8v-8h-8zm6 6h-4v-4h4v4z"/></svg>',
    focusView: '<svg viewBox="0 0 24 24" width="16" height="16" fill="currentColor"><path d="M5 15H3v4c0 1.1.9 2 2 2h4v-2H5v-4zM5 5h4V3H5c-1.1 0-2 .9-2 2v4h2V5zm14-2h-4v2h4v4h2V5c0-1.1-.9-2-2-2zm0 16h-4v2h4c1.1 0 2-.9 2-2v-4h-2v4zM12 8c-2.21 0-4 1.79-4 4s1.79 4 4 4 4-1.79 4-4-1.79-4-4-4z"/></svg>',
    openStage: '<svg viewBox="0 0 24 24" width="17" height="17" fill="currentColor"><path d="M19 19H5V5h7V3H5c-1.11 0-2 .9-2 2v14c0 1.1.89 2 2 2h14c1.1 0 2-.9 2-2v-7h-2v7zM14 3v2h3.59l-9.83 9.83 1.41 1.41L19 6.41V10h2V3h-7z"/></svg>',
    close: '<svg viewBox="0 0 24 24" width="16" height="16" fill="currentColor"><path d="M19 6.41L17.59 5 12 10.59 6.41 5 5 6.41 10.59 12 5 17.59 6.41 19 12 13.41 17.59 19 19 17.59 13.41 12z"/></svg>',
    hangUp: '<svg viewBox="0 0 24 24" width="18" height="18" fill="currentColor"><path d="M12 9c-1.6 0-3.15.25-4.6.72v3.1c0 .39-.23.74-.56.9-.98.49-1.87 1.12-2.66 1.85-.18.18-.43.28-.7.28-.28 0-.53-.11-.71-.29L.29 13.08a.956.956 0 0 1-.29-.7c0-.28.11-.53.29-.71C3.34 8.78 7.46 7 12 7s8.66 1.78 11.71 4.67c.18.18.29.43.29.71 0 .28-.11.53-.29.71l-2.48 2.48c-.18.18-.43.29-.71.29-.27 0-.52-.11-.7-.28a11.27 11.27 0 0 0-2.67-1.85.996.996 0 0 1-.56-.9v-3.1C15.15 9.25 13.6 9 12 9z"/></svg>'
};

const RICH_PIP_CSS = `
:root {
    --spokes-pip-bg: #151521;
    --spokes-pip-surface: #1e1e2d;
    --spokes-pip-surface-elevated: #262638;
    --spokes-pip-primary: #7e6fff;
    --spokes-pip-success: #3dcb6c;
    --spokes-pip-error: #ff3f5f;
    --spokes-pip-border: #33323e;
    --spokes-pip-text: #f4f4f8;
    --spokes-pip-text-sub: #92929f;
    --spokes-pip-font: Roboto, -apple-system, BlinkMacSystemFont, "Segoe UI", Helvetica, Arial, sans-serif;
}
* { box-sizing: border-box; margin: 0; padding: 0; }
html, body {
    width: 100%; height: 100%; overflow: hidden;
    background: var(--spokes-pip-bg); color: var(--spokes-pip-text);
    font-family: var(--spokes-pip-font);
    user-select: none;
}
.docpip-root {
    --pip-filmstrip-h: clamp(72px, 22vh, 96px);
    position: relative; width: 100%; height: 100%;
    display: flex; flex-direction: column; justify-content: center; align-items: center;
    background: radial-gradient(circle at 50% 34%, var(--spokes-pip-surface-elevated) 0%, var(--spokes-pip-bg) 100%);
}
.docpip-root.filmstrip-has-video {
    --pip-filmstrip-h: clamp(82px, 23vh, 132px);
}
.docpip-root.is-frameless-desktop {
    border: 1px solid var(--spokes-pip-border);
}
.docpip-header {
    position: absolute; top: 0; left: 0; right: 0; height: 38px; z-index: 6;
    display: flex; align-items: center; justify-content: space-between; gap: 8px;
    padding: 0 8px 0 10px;
    background: linear-gradient(to bottom, rgba(21, 21, 33, 0.9) 0%, rgba(21, 21, 33, 0.42) 78%, transparent 100%);
}
.docpip-root.is-frameless-desktop .docpip-header {
    -webkit-app-region: drag;
}
.docpip-header-pill {
    display: inline-flex; align-items: center; gap: 7px;
    padding: 3px 10px; border-radius: 999px;
    background: rgba(30, 30, 45, 0.78);
    backdrop-filter: blur(10px);
    border: 1px solid rgba(126, 111, 255, 0.22);
    max-width: calc(100% - 38px); min-width: 0;
    cursor: pointer;
    -webkit-app-region: no-drag;
    transition: border-color 0.15s ease, background 0.15s ease;
}
.docpip-header-pill:hover {
    background: rgba(38, 38, 56, 0.92);
    border-color: rgba(126, 111, 255, 0.45);
}
.docpip-dot {
    width: 8px; height: 8px; border-radius: 50%;
    background: rgba(255, 255, 255, 0.32); flex-shrink: 0;
    transition: background-color 0.14s ease, box-shadow 0.14s ease;
}
.docpip-dot.is-speaking {
    background: var(--spokes-pip-success);
    box-shadow: 0 0 7px var(--spokes-pip-success);
}
.docpip-badge-text {
    font-size: 11.5px; font-weight: 600; color: var(--spokes-pip-text);
    white-space: nowrap; overflow: hidden; text-overflow: ellipsis;
}
.docpip-header-actions {
    display: inline-flex; align-items: center; gap: 4px;
    -webkit-app-region: no-drag;
}
.docpip-mini-btn {
    width: 26px; height: 26px; border-radius: 7px; border: none; cursor: pointer;
    display: inline-flex; align-items: center; justify-content: center;
    background: rgba(30, 30, 45, 0.68); color: var(--spokes-pip-text-sub);
    transition: background 0.14s ease, color 0.14s ease;
}
.docpip-mini-btn:hover {
    background: rgba(126, 111, 255, 0.24); color: #ffffff;
}
.docpip-mini-btn.btn-close:hover {
    background: var(--spokes-pip-error); color: #ffffff;
}
.docpip-root:not(.is-frameless-desktop) .btn-close {
    display: none;
}
.docpip-video {
    position: absolute; inset: 0; width: 100%; height: 100%;
    object-fit: contain; background: #0c0c14; display: none;
}
.docpip-root.has-filmstrip .docpip-video {
    top: 38px;
    height: calc(100% - var(--pip-filmstrip-h) - 96px);
}
.docpip-video.has-stream { display: block; }
.docpip-video.spokes-pip-mirror,
.docpip-self.spokes-pip-mirror,
.docpip-presenter-cam.spokes-pip-mirror { transform: scaleX(-1); }
.docpip-presenter-cam {
    --effective-self-aspect: var(--self-aspect, 1.7778);
    position: absolute; right: 12px; bottom: 52px;
    width: min(max(96px, 25vw), calc(min(32vh, 100vh - 110px) * var(--effective-self-aspect)));
    height: min(calc(max(96px, 25vw) / var(--effective-self-aspect)), min(32vh, 100vh - 110px));
    aspect-ratio: var(--effective-self-aspect);
    object-fit: contain;
    border-radius: 9px;
    border: 1.5px solid rgba(255, 255, 255, 0.72); background: #0c0c14;
    box-shadow: 0 6px 16px rgba(0, 0, 0, 0.65); display: none; z-index: 3;
}
.docpip-root.has-filmstrip .docpip-presenter-cam {
    bottom: calc(var(--pip-filmstrip-h) + 60px);
}
.docpip-presenter-cam.has-stream { display: block; }
.docpip-self {
    --effective-self-aspect: var(--self-aspect, 1.7778);
    position: absolute; top: 42px; left: 10px;
    width: min(max(96px, 26vw), calc(min(36vh, 100vh - 110px) * var(--effective-self-aspect)));
    height: min(calc(max(96px, 26vw) / var(--effective-self-aspect)), min(36vh, 100vh - 110px));
    aspect-ratio: var(--effective-self-aspect);
    object-fit: contain;
    border-radius: 9px;
    border: 1.5px solid rgba(126, 111, 255, 0.55); background: #0c0c14;
    box-shadow: 0 6px 16px rgba(0, 0, 0, 0.6); display: none; z-index: 4;
}
.docpip-self.has-stream { display: block; }
.docpip-avatar-card {
    position: absolute; inset: 38px 16px 50px 16px;
    display: flex; flex-direction: column; align-items: center; justify-content: center;
    gap: 6px; padding: 0; text-align: center; z-index: 2;
}
.docpip-root.has-filmstrip .docpip-avatar-card {
    bottom: calc(var(--pip-filmstrip-h) + 58px);
}
.docpip-root.has-filmstrip .docpip-channel {
    display: none;
}
.docpip-root.has-filmstrip .docpip-avatar-wrap {
    width: clamp(48px, 18vh, 60px);
    height: clamp(48px, 18vh, 60px);
    font-size: clamp(16px, 6vh, 20px);
}
.docpip-root.mode-video .docpip-avatar-card { display: none; }
.docpip-grid {
    position: absolute; inset: 40px 10px 52px 10px;
    display: none; gap: 8px;
    grid-template-columns: repeat(2, 1fr);
    grid-auto-rows: 1fr;
    align-items: stretch; justify-items: stretch;
    z-index: 2;
}
.docpip-grid[data-count="1"] {
    grid-template-columns: 1fr;
}
.docpip-grid[data-count="2"],
.docpip-grid[data-count="4"] {
    grid-template-columns: repeat(2, 1fr);
}
.docpip-grid[data-count="3"] {
    grid-template-columns: repeat(4, 1fr);
}
.docpip-grid[data-count="3"] > .pip-participant-tile {
    grid-column: span 2;
}
.docpip-grid[data-count="3"] > .pip-participant-tile:nth-child(3) {
    grid-column: 2 / span 2;
}
.docpip-grid[data-count="5"],
.docpip-grid[data-count="6"] {
    grid-template-columns: repeat(3, 1fr);
}
.docpip-root.mode-grid .docpip-grid { display: grid; }
.docpip-root.mode-grid .docpip-video,
.docpip-root.mode-grid .docpip-presenter-cam,
.docpip-root.mode-grid .docpip-self,
.docpip-root.mode-grid .docpip-avatar-card,
.docpip-root.mode-grid .docpip-filmstrip { display: none !important; }
.docpip-filmstrip {
    position: absolute; left: 10px; right: 10px; bottom: 52px; height: var(--pip-filmstrip-h);
    padding: 0 4px;
    background: transparent;
    border: none;
    display: none; align-items: stretch; justify-content: safe center; gap: 10px;
    overflow-x: auto; overflow-y: hidden;
    z-index: 4;
}
.docpip-root.has-filmstrip:not(.mode-grid) .docpip-filmstrip { display: flex; }
.docpip-filmstrip .pip-participant-tile {
    flex: 1 1 0; max-width: clamp(118px, 26vh, 210px); min-width: 68px; height: 100%;
}
.pip-participant-tile {
    position: relative; overflow: hidden; border-radius: 10px;
    background: transparent;
    border: 1.5px solid transparent;
    display: flex; flex-direction: column; align-items: center; justify-content: center;
    gap: 5px; padding: 4px;
    cursor: pointer; min-width: 0; min-height: 0;
    transition: background-color 0.14s ease, border-color 0.14s ease, box-shadow 0.14s ease;
}
.pip-participant-tile:not(.has-video):hover {
    background: rgba(126, 111, 255, 0.08);
    border-color: rgba(126, 111, 255, 0.22);
}
.pip-participant-tile.has-video {
    border-radius: 9px;
    background: #0c0c14;
    border-color: rgba(126, 111, 255, 0.24);
    padding: 0; gap: 0;
}
.pip-participant-tile.has-video:hover {
    border-color: rgba(126, 111, 255, 0.65);
}
.pip-participant-tile.has-video.is-speaking {
    border-color: var(--spokes-pip-success);
    box-shadow: 0 0 0 2px rgba(61, 203, 108, 0.24);
}
.pip-tile-video {
    position: absolute; inset: 0; width: 100%; height: 100%;
    object-fit: contain; background: #0c0c14; display: none; z-index: 1;
}
.pip-participant-tile.is-screenshare .pip-tile-video {
    object-fit: contain;
}
.pip-tile-video.has-stream { display: block; }
.pip-tile-video.spokes-pip-mirror,
.pip-tile-presenter-cam.spokes-pip-mirror { transform: scaleX(-1); }
.pip-tile-presenter-cam {
    position: absolute; right: 4px; bottom: 4px;
    width: 30%; max-width: 68px; min-width: 36px;
    aspect-ratio: var(--self-aspect, 1.7778);
    object-fit: contain; border-radius: 5px;
    border: 1px solid rgba(255, 255, 255, 0.75);
    background: #0c0c14; display: none; z-index: 3;
}
.pip-tile-presenter-cam.has-stream { display: block; }
.pip-tile-avatar-wrap {
    position: relative; width: 58px; height: 58px; border-radius: 50%;
    padding: 3px;
    display: flex; align-items: center; justify-content: center; flex-shrink: 0;
    background: transparent;
    color: #ffffff; font-weight: 600; font-size: 18px;
    border: 2.5px solid rgba(126, 111, 255, 0.28);
    box-shadow: 0 6px 16px rgba(0, 0, 0, 0.36);
    transition: border-color 0.14s ease, box-shadow 0.14s ease, transform 0.14s ease;
    z-index: 2;
}
.pip-tile-initials {
    width: 100%; height: 100%; border-radius: 50%;
    background: var(--avatar-bg, #673ab7);
    display: flex; align-items: center; justify-content: center;
}
.pip-participant-tile:not(.has-video):hover .pip-tile-avatar-wrap {
    border-color: rgba(126, 111, 255, 0.65);
    transform: scale(1.04);
}
.pip-participant-tile.is-speaking:not(.has-video) .pip-tile-avatar-wrap {
    border-color: var(--spokes-pip-success);
    box-shadow: 0 0 0 3.5px rgba(61, 203, 108, 0.26), 0 6px 16px rgba(0, 0, 0, 0.4);
}
.docpip-grid[data-count="5"] .pip-tile-avatar-wrap,
.docpip-grid[data-count="6"] .pip-tile-avatar-wrap {
    width: 44px; height: 44px; font-size: 14px;
}
.docpip-filmstrip .pip-participant-tile:not(.has-video) {
    gap: 4px; padding: 2px 4px;
}
.docpip-filmstrip .pip-tile-avatar-wrap {
    width: 46px; height: 46px; font-size: 14.5px;
    padding: 2.5px; border-width: 2.5px;
}
.pip-participant-tile.has-video .pip-tile-avatar-wrap { display: none; }
.pip-tile-avatar-img {
    width: 100%; height: 100%; border-radius: 50%; object-fit: cover; display: none;
}
.pip-tile-footer {
    position: static; width: 100%; z-index: 4;
    display: flex; align-items: center; justify-content: center; gap: 4px;
    pointer-events: none;
}
.pip-participant-tile.has-video .pip-tile-footer {
    position: absolute; left: 4px; right: 4px; bottom: 4px; width: auto;
    justify-content: space-between;
}
.pip-tile-name {
    font-size: 12px; font-weight: 600; color: var(--spokes-pip-text);
    background: transparent; padding: 0 2px; border-radius: 4px;
    max-width: 100%; text-align: center;
    white-space: nowrap; overflow: hidden; text-overflow: ellipsis;
}
.docpip-filmstrip .pip-participant-tile:not(.has-video) .pip-tile-name {
    font-size: 11.5px; line-height: 1.2;
}
.pip-participant-tile.has-video .pip-tile-name {
    font-size: 10px; color: #ffffff;
    background: rgba(15, 15, 24, 0.76); padding: 1px 5px;
    text-align: left;
}
.pip-tile-mute {
    position: absolute; right: -2px; bottom: -2px;
    width: 18px; height: 18px; border-radius: 50%;
    background: var(--spokes-pip-error); color: #ffffff;
    border: 2px solid var(--spokes-pip-bg);
    display: none; align-items: center; justify-content: center; flex-shrink: 0;
}
.docpip-filmstrip .pip-tile-mute {
    width: 16px; height: 16px;
}
.pip-tile-mute svg { width: 10px; height: 10px; }
.pip-tile-mute.visible { display: inline-flex; }
.docpip-avatar-wrap {
    position: relative; width: 64px; height: 64px; border-radius: 50%;
    padding: 3px;
    display: flex; align-items: center; justify-content: center; flex-shrink: 0;
    background: transparent;
    color: #ffffff; font-weight: 600; font-size: 21px;
    border: 2.5px solid rgba(126, 111, 255, 0.28);
    box-shadow: 0 6px 18px rgba(0, 0, 0, 0.38);
    transition: border-color 0.14s ease, box-shadow 0.14s ease, transform 0.14s ease;
}
.docpip-initials {
    width: 100%; height: 100%; border-radius: 50%;
    background: var(--avatar-bg, #673ab7);
    display: flex; align-items: center; justify-content: center;
}
.docpip-avatar-wrap.is-speaking {
    border-color: var(--spokes-pip-success);
    box-shadow: 0 0 0 4px rgba(61, 203, 108, 0.24), 0 6px 18px rgba(0, 0, 0, 0.42);
}
.docpip-avatar-img {
    width: 100%; height: 100%; border-radius: 50%; object-fit: cover; display: none;
}
.docpip-mute-pill {
    position: absolute; right: -3px; bottom: -3px; width: 22px; height: 22px;
    border-radius: 50%; background: var(--spokes-pip-error); color: #ffffff;
    display: none; align-items: center; justify-content: center;
    border: 2px solid var(--spokes-pip-bg);
}
.docpip-mute-pill svg { width: 12px; height: 12px; }
.docpip-mute-pill.visible { display: flex; }
.docpip-name {
    font-size: 13.5px; font-weight: 600; color: var(--spokes-pip-text);
    max-width: 260px; white-space: nowrap; overflow: hidden; text-overflow: ellipsis;
}
.docpip-channel {
    font-size: 11.5px; color: var(--spokes-pip-text-sub);
    max-width: 260px; white-space: nowrap; overflow: hidden; text-overflow: ellipsis;
}
.docpip-controls {
    position: absolute; left: 50%; transform: translateX(-50%); bottom: 8px; z-index: 6;
    pointer-events: auto;
    display: inline-flex; align-items: center; justify-content: center; gap: 6px;
    padding: 4px 6px; border-radius: 12px;
    background: rgba(26, 26, 39, 0.86);
    backdrop-filter: blur(14px);
    border: 1px solid rgba(126, 111, 255, 0.18);
    box-shadow: 0 6px 18px rgba(0, 0, 0, 0.44);
    -webkit-app-region: no-drag;
}
.docpip-btn {
    width: 30px; height: 30px; border-radius: 8px; border: 1px solid rgba(255, 255, 255, 0.07);
    cursor: pointer; display: inline-flex; align-items: center; justify-content: center;
    background: rgba(255, 255, 255, 0.08); color: var(--spokes-pip-text);
    transition: background 0.15s ease, border-color 0.15s ease, transform 0.1s ease, color 0.15s ease;
}
.docpip-btn:hover {
    background: rgba(126, 111, 255, 0.24);
    border-color: rgba(126, 111, 255, 0.42);
    color: #ffffff;
}
.docpip-btn:active { transform: scale(0.95); }
.docpip-btn.is-active {
    background: rgba(61, 203, 108, 0.2);
    border-color: rgba(61, 203, 108, 0.45);
    color: var(--spokes-pip-success);
}
.docpip-btn.is-muted-danger {
    background: rgba(255, 63, 95, 0.22);
    border-color: rgba(255, 63, 95, 0.48);
    color: var(--spokes-pip-error);
}
.docpip-btn.is-danger {
    background: var(--spokes-pip-error);
    border-color: transparent;
    color: #ffffff;
}
.docpip-btn.is-danger:hover {
    background: #ff5874;
}
`;

function syncSpokesThemeVariablesToDoc(targetDoc) {
    if (!targetDoc?.documentElement || typeof window === 'undefined' || typeof document === 'undefined') {
        return;
    }
    try {
        const computed = window.getComputedStyle(document.documentElement);
        const primary = computed.getPropertyValue('--mud-palette-primary').trim();
        const success = computed.getPropertyValue('--mud-palette-success').trim();
        const error = computed.getPropertyValue('--mud-palette-error').trim();
        const font = computed.getPropertyValue('--mud-typography-default-family').trim();

        const rootStyle = targetDoc.documentElement.style;
        if (primary) rootStyle.setProperty('--spokes-pip-primary', primary);
        if (success) rootStyle.setProperty('--spokes-pip-success', success);
        if (error) rootStyle.setProperty('--spokes-pip-error', error);
        if (font) rootStyle.setProperty('--spokes-pip-font', font);
    } catch (e) {}
}

function stopPipCloseWatchTimer() {
    if (_docPipCloseWatchTimer) {
        clearInterval(_docPipCloseWatchTimer);
        _docPipCloseWatchTimer = null;
    }
}

function handleRichPipWindowClosed(closedWin) {
    if (_docPipWindow !== closedWin) return;
    stopPipCloseWatchTimer();
    _docPipWindow = null;
    _docPipRefs = null;
    notifyOsPipChanged(false);
    syncPipTrackSubscriptions(selectPipTarget());
    restoreStageVideosIfUnsubpressed();
    requestAnimationFrame(() => syncInAppPipElements());
}

function mountRichPipWindow(pipWin, { isFramelessDesktop = false } = {}) {
    _docPipWindow = pipWin;
    const doc = pipWin.document;

    if (isFramelessDesktop) {
        try {
            doc.open();
            doc.write('<!DOCTYPE html><html><head></head><body></body></html>');
            doc.close();
        } catch (e) {}
    }

    doc.title = 'Spokes Call';

    if (doc.head) {
        doc.head.innerHTML = '';
    }
    if (doc.body) {
        doc.body.innerHTML = '';
    }

    const styleEl = doc.createElement('style');
    styleEl.textContent = RICH_PIP_CSS;
    doc.head.appendChild(styleEl);
    syncSpokesThemeVariablesToDoc(doc);

    const root = doc.createElement('div');
    root.className = `docpip-root${isFramelessDesktop ? ' is-frameless-desktop' : ''}`;
    root.innerHTML = `
        <video class="docpip-video" autoplay playsinline muted disablepictureinpicture></video>
        <video class="docpip-presenter-cam" autoplay playsinline muted disablepictureinpicture></video>
        <video class="docpip-self" autoplay playsinline muted disablepictureinpicture></video>
        <div class="docpip-grid"></div>
        <div class="docpip-filmstrip"></div>
        <div class="docpip-header">
            <div class="docpip-header-pill" title="Return to Call">
                <span class="docpip-dot"></span>
                <span class="docpip-badge-text"></span>
            </div>
            <div class="docpip-header-actions">
                <button type="button" class="docpip-mini-btn btn-close" title="Close Picture in Picture">${SVG_ICONS.close}</button>
            </div>
        </div>
        <div class="docpip-avatar-card">
            <div class="docpip-avatar-wrap">
                <span class="docpip-initials"></span>
                <img class="docpip-avatar-img" alt="" />
                <span class="docpip-mute-pill">${SVG_ICONS.micOff}</span>
            </div>
            <div class="docpip-name"></div>
            <div class="docpip-channel"></div>
        </div>
        <div class="docpip-controls">
            <button type="button" class="docpip-btn btn-mute" title="Toggle Microphone"></button>
            <button type="button" class="docpip-btn btn-cam" title="Toggle Camera"></button>
            <button type="button" class="docpip-btn btn-screen" title="Share Screen"></button>
            <button type="button" class="docpip-btn btn-grid btn-header-grid" title="All Users View">${SVG_ICONS.gridView}</button>
            <button type="button" class="docpip-btn btn-return btn-header-return" title="Return to Call">${SVG_ICONS.openStage}</button>
            <button type="button" class="docpip-btn is-danger btn-hangup" title="Disconnect">${SVG_ICONS.hangUp}</button>
        </div>
    `;
    doc.body.appendChild(root);

    const refs = {
        root,
        header: root.querySelector('.docpip-header'),
        primaryVideo: root.querySelector('.docpip-video'),
        presenterCamVideo: root.querySelector('.docpip-presenter-cam'),
        selfVideo: root.querySelector('.docpip-self'),
        gridContainer: root.querySelector('.docpip-grid'),
        filmstripContainer: root.querySelector('.docpip-filmstrip'),
        headerPill: root.querySelector('.docpip-header-pill'),
        badgeDot: root.querySelector('.docpip-dot'),
        badgeText: root.querySelector('.docpip-badge-text'),
        avatarCard: root.querySelector('.docpip-avatar-card'),
        avatarWrap: root.querySelector('.docpip-avatar-wrap'),
        initialsEl: root.querySelector('.docpip-initials'),
        avatarImg: root.querySelector('.docpip-avatar-img'),
        mutePill: root.querySelector('.docpip-mute-pill'),
        nameEl: root.querySelector('.docpip-name'),
        channelEl: root.querySelector('.docpip-channel'),
        btnMute: root.querySelector('.btn-mute'),
        btnCam: root.querySelector('.btn-cam'),
        btnScreen: root.querySelector('.btn-screen'),
        btnGrid: root.querySelector('.btn-grid'),
        btnReturn: root.querySelector('.btn-return'),
        btnClose: root.querySelector('.btn-close'),
        btnHangup: root.querySelector('.btn-hangup'),
        loadedAvatarUrl: ''
    };

    refs.avatarImg.addEventListener('load', () => {
        refs.avatarImg.style.display = 'block';
        refs.initialsEl.style.display = 'none';
    });
    refs.avatarImg.addEventListener('error', () => {
        refs.avatarImg.style.display = 'none';
        refs.initialsEl.style.display = 'flex';
    });

    const triggerReturnToCall = () => {
        try { window.focus(); } catch (e) {}
        try { window.SpokesBridge?.focusVoiceWindow?.(); } catch (e) {}
        if (typeof _actions.onReturnToCall === 'function') _actions.onReturnToCall();
        closeOsPip();
    };

    refs.btnMute.addEventListener('click', () => {
        if (typeof _actions.onToggleMute === 'function') _actions.onToggleMute();
    });
    refs.btnCam.addEventListener('click', () => {
        if (typeof _actions.onToggleCamera === 'function') _actions.onToggleCamera();
    });
    refs.btnScreen.addEventListener('click', () => {
        if (typeof _actions.onToggleScreenShare === 'function') _actions.onToggleScreenShare();
    });
    refs.btnGrid.addEventListener('click', (e) => {
        e.stopPropagation();
        togglePipLayoutMode();
    });
    refs.btnReturn.addEventListener('click', triggerReturnToCall);
    refs.headerPill.addEventListener('click', triggerReturnToCall);
    refs.primaryVideo.addEventListener('dblclick', triggerReturnToCall);
    refs.avatarCard.addEventListener('dblclick', triggerReturnToCall);
    refs.btnClose.addEventListener('click', () => {
        closeOsPip();
    });
    refs.btnHangup.addEventListener('click', () => {
        if (typeof _actions.onHangUp === 'function') _actions.onHangUp();
        closeOsPip();
    });

    if (!isFramelessDesktop && refs.header) {
        let savedBounds = null;
        refs.header.addEventListener('dblclick', (e) => {
            if (e.target?.closest?.('.docpip-header-pill, .docpip-controls, .docpip-header-actions, button')) {
                return;
            }
            try {
                const availW = pipWin.screen?.availWidth || window.screen?.availWidth || 1280;
                const availH = pipWin.screen?.availHeight || window.screen?.availHeight || 720;
                const maxW = Math.round(availW * 0.8);
                const maxH = Math.round(availH * 0.8);
                const curW = pipWin.outerWidth || pipWin.innerWidth || 368;
                const curH = pipWin.outerHeight || pipWin.innerHeight || 232;

                if (savedBounds && (curW >= maxW - 40 || curH >= maxH - 40)) {
                    pipWin.resizeTo(savedBounds.width, savedBounds.height);
                    savedBounds = null;
                } else {
                    savedBounds = { width: curW, height: curH };
                    pipWin.resizeTo(maxW, maxH);
                }
            } catch (err) {}
        });
    }

    pipWin.addEventListener('pagehide', () => handleRichPipWindowClosed(pipWin));
    pipWin.addEventListener('unload', () => handleRichPipWindowClosed(pipWin));

    stopPipCloseWatchTimer();
    _docPipCloseWatchTimer = setInterval(() => {
        if (!_docPipWindow || _docPipWindow.closed) {
            handleRichPipWindowClosed(pipWin);
        }
    }, 400);

    _docPipRefs = refs;
    detachStageVideosForExternalPip();
    syncPipTrackSubscriptions(selectPipTarget());
    syncDocumentPipDom();
    notifyOsPipChanged(true);
    return true;
}

function computePipWindowSize(sizes) {
    const target = selectPipTarget();
    const isMultiTile = target.layoutMode === 'grid' || (target.filmstripParticipants?.length > 0);
    const hasAnyVideo = isMultiTile || target.mode === 'video' || Boolean(target.selfTrack);
    if (isMultiTile) return sizes.multiTile;
    return hasAnyVideo ? sizes.video : sizes.audio;
}

function openDesktopPipWindow() {
    const { width, height } = computePipWindowSize({
        multiTile: { width: 420, height: 280 },
        video: { width: 380, height: 240 },
        audio: { width: 320, height: 196 }
    });
    const features = `popup=yes,width=${width},height=${height}`;
    const pipWin = window.open('', DESKTOP_PIP_FRAME_NAME, features);
    if (!pipWin) {
        return false;
    }
    return mountRichPipWindow(pipWin, { isFramelessDesktop: true });
}

async function openDocumentPip() {
    const { width, height } = computePipWindowSize({
        multiTile: { width: 408, height: 272 },
        video: { width: 368, height: 232 },
        audio: { width: 320, height: 188 }
    });

    const pipWin = await window.documentPictureInPicture.requestWindow({ width, height });
    return mountRichPipWindow(pipWin, { isFramelessDesktop: false });
}

function syncDocumentPipDom() {
    if (!_docPipWindow || _docPipWindow.closed || !_docPipRefs) return;
    const refs = _docPipRefs;
    const target = selectPipTarget();
    const isGridMode = target.layoutMode === 'grid';
    const isVideoMode = !isGridMode && target.mode === 'video';
    const hasFilmstrip = !isGridMode && target.filmstripParticipants.length > 0;
    const filmstripHasVideo = hasFilmstrip && target.filmstripParticipants.some(p =>
        Boolean(p?.id && (
            isPipTrackAlive(_videoTracks.get(`${p.id}:screenshare`))
            || isPipTrackAlive(_videoTracks.get(`${p.id}:camera`))
        ))
    );

    refs.root.classList.toggle('mode-grid', isGridMode);
    refs.root.classList.toggle('mode-video', isVideoMode);
    refs.root.classList.toggle('has-filmstrip', hasFilmstrip);
    refs.root.classList.toggle('filmstrip-has-video', filmstripHasVideo);

    if ((isGridMode || hasFilmstrip) && !refs.autoExpandedForMultiTile) {
        try {
            const curW = _docPipWindow.outerWidth || _docPipWindow.innerWidth || 0;
            const curH = _docPipWindow.outerHeight || _docPipWindow.innerHeight || 0;
            if ((curW > 0 && curW < 368) || (curH > 0 && curH < 248)) {
                refs.autoExpandedForMultiTile = true;
                _docPipWindow.resizeTo(Math.max(curW, 420), Math.max(curH, 280));
            }
        } catch (e) {}
    }

    const showGridToggle = _callMeta.participants.size > 1;
    if (refs.btnGrid) {
        refs.btnGrid.style.display = showGridToggle ? 'inline-flex' : 'none';
        refs.btnGrid.innerHTML = isGridMode ? SVG_ICONS.focusView : SVG_ICONS.gridView;
        refs.btnGrid.classList.toggle('is-active', isGridMode);
        refs.btnGrid.title = isGridMode ? 'Focus Speaker' : 'All Users View';
    }

    applyPipStageMedia(refs, target, isGridMode, hasFilmstrip, false);

    const speakerName = target.speaker?.name || _callMeta.channelName || 'Voice Call';
    const channelLabel = _callMeta.channelName || 'Voice Call';
    refs.badgeText.textContent = isVideoMode ? speakerName : channelLabel;
    refs.badgeDot.classList.toggle('is-speaking', isVideoMode ? target.isSpeaking : (_callMeta.speakingIds.size > 0));

    refs.nameEl.textContent = speakerName;
    refs.channelEl.textContent = _callMeta.channelName || '';
    refs.initialsEl.textContent = target.speaker?.initials || computeInitials(speakerName);
    refs.avatarWrap.style.setProperty('--avatar-bg', target.speaker?.profileColor || '#673ab7');
    refs.avatarWrap.classList.toggle('is-speaking', target.isSpeaking);
    refs.mutePill.classList.toggle('visible', target.isSpeakerMuted);

    const nextAvatar = target.speaker?.avatarUrl || '';
    if (refs.loadedAvatarUrl !== nextAvatar) {
        refs.loadedAvatarUrl = nextAvatar;
        if (nextAvatar) {
            refs.avatarImg.src = nextAvatar;
        } else {
            refs.avatarImg.removeAttribute('src');
            refs.avatarImg.style.display = 'none';
            refs.initialsEl.style.display = 'flex';
        }
    }

    refs.btnMute.innerHTML = _callMeta.isMuted ? SVG_ICONS.micOff : SVG_ICONS.micOn;
    refs.btnMute.classList.toggle('is-muted-danger', _callMeta.isMuted);
    refs.btnMute.classList.toggle('is-active', !_callMeta.isMuted);
    refs.btnCam.innerHTML = _callMeta.isVideoOn ? SVG_ICONS.camOn : SVG_ICONS.camOff;
    refs.btnCam.classList.toggle('is-active', _callMeta.isVideoOn);
    if (refs.btnScreen) {
        refs.btnScreen.innerHTML = _callMeta.isScreenSharing ? SVG_ICONS.screenOn : SVG_ICONS.screenOff;
        refs.btnScreen.classList.toggle('is-active', _callMeta.isScreenSharing);
        refs.btnScreen.title = _callMeta.isScreenSharing ? 'Stop Sharing' : 'Share Screen';
    }
}

// --- Tier 2 Fallback: Canvas-backed HTMLVideoElement PiP (Safari / Firefox Web only) ---

function ensureFallbackElements() {
    if (!_fallbackCanvas) {
        _fallbackCanvas = document.createElement('canvas');
        _fallbackCanvas.width = 360;
        _fallbackCanvas.height = 202;
        _fallbackCtx = _fallbackCanvas.getContext('2d');
    }
    if (!_fallbackSourceVideo) {
        _fallbackSourceVideo = document.createElement('video');
        _fallbackSourceVideo.muted = true;
        _fallbackSourceVideo.playsInline = true;
        _fallbackSourceVideo.autoplay = true;
    }
    if (!_fallbackPipVideo) {
        _fallbackPipVideo = document.createElement('video');
        _fallbackPipVideo.id = 'spokes-fallback-pip-video';
        _fallbackPipVideo.muted = true;
        _fallbackPipVideo.playsInline = true;
        _fallbackPipVideo.style.cssText = 'position:fixed;width:1px;height:1px;opacity:0;pointer-events:none;left:-9999px;top:-9999px;';
        document.body.appendChild(_fallbackPipVideo);

        _fallbackPipVideo.addEventListener('leavepictureinpicture', () => {
            _isFallbackPipOpen = false;
            stopFallbackCanvasLoop();
            notifyOsPipChanged(false);
            syncPipTrackSubscriptions(selectPipTarget());
            restoreStageVideosIfUnsubpressed();
            requestAnimationFrame(() => syncInAppPipElements());
        });
    }
}

function renderFallbackCanvasFrame() {
    if (!_fallbackCtx || !_fallbackCanvas) return;
    const ctx = _fallbackCtx;
    const w = _fallbackCanvas.width;
    const h = _fallbackCanvas.height;
    const target = selectPipTarget();

    ctx.fillStyle = '#151521';
    ctx.fillRect(0, 0, w, h);

    if (target.mode === 'video' && target.primaryTrack) {
        attachMediaTrackToVideo(_fallbackSourceVideo, target.primaryTrack, false);
        const vw = _fallbackSourceVideo.videoWidth || 0;
        const vh = _fallbackSourceVideo.videoHeight || 0;
        if (vw > 0 && vh > 0 && _fallbackSourceVideo.readyState >= 2) {
            const scale = Math.min(w / vw, h / vh);
            const dw = vw * scale;
            const dh = vh * scale;
            const dx = (w - dw) / 2;
            const dy = (h - dh) / 2;
            ctx.save();
            if (shouldMirrorEntry(target.primaryTrack)) {
                ctx.translate(w, 0);
                ctx.scale(-1, 1);
            }
            ctx.drawImage(_fallbackSourceVideo, dx, dy, dw, dh);
            ctx.restore();
            return;
        }
    }

    // Voice-only Active Speaker Avatar Card rendered cleanly to Canvas
    const cx = w / 2;
    const cy = h / 2 - 14;
    const radius = 34;

    if (target.isSpeaking) {
        ctx.beginPath();
        ctx.arc(cx, cy, radius + 6, 0, Math.PI * 2);
        ctx.strokeStyle = '#3dcb6c';
        ctx.lineWidth = 4;
        ctx.stroke();
    }

    ctx.beginPath();
    ctx.arc(cx, cy, radius, 0, Math.PI * 2);
    ctx.fillStyle = '#262638';
    ctx.fill();

    ctx.fillStyle = '#ffffff';
    ctx.font = 'bold 22px Roboto, -apple-system, BlinkMacSystemFont, "Segoe UI", sans-serif';
    ctx.textAlign = 'center';
    ctx.textBaseline = 'middle';
    const speakerName = target.speaker?.name || _callMeta.channelName || 'Voice Call';
    ctx.fillText(target.speaker?.initials || computeInitials(speakerName), cx, cy);

    ctx.font = '600 14px Roboto, -apple-system, BlinkMacSystemFont, "Segoe UI", sans-serif';
    ctx.fillStyle = '#f4f4f8';
    ctx.fillText(speakerName.substring(0, 32), cx, cy + radius + 22);

    if (_callMeta.channelName) {
        ctx.font = '12px Roboto, -apple-system, BlinkMacSystemFont, "Segoe UI", sans-serif';
        ctx.fillStyle = '#92929f';
        ctx.fillText(_callMeta.channelName.substring(0, 36), cx, cy + radius + 40);
    }
}

function stopFallbackCanvasLoop() {
    if (_fallbackTimer) {
        clearInterval(_fallbackTimer);
        _fallbackTimer = null;
    }
    if (_fallbackSourceVideo?.srcObject) {
        _fallbackSourceVideo.pause();
        _fallbackSourceVideo.srcObject = null;
    }
}

async function openFallbackVideoPip() {
    if (typeof document === 'undefined' || !document.pictureInPictureEnabled) return false;
    ensureFallbackElements();
    renderFallbackCanvasFrame();

    if (!_fallbackTimer) {
        _fallbackTimer = setInterval(renderFallbackCanvasFrame, FALLBACK_CANVAS_INTERVAL_MS);
    }

    if (!_fallbackPipVideo.srcObject && typeof _fallbackCanvas.captureStream === 'function') {
        _fallbackPipVideo.srcObject = _fallbackCanvas.captureStream(15);
    }
    await _fallbackPipVideo.play();
    await _fallbackPipVideo.requestPictureInPicture();
    _isFallbackPipOpen = true;
    detachStageVideosForExternalPip();
    syncPipTrackSubscriptions(selectPipTarget());
    notifyOsPipChanged(true);
    return true;
}

export async function toggleOsPip() {
    if (!_callMeta.active) return false;

    // 1. Mobile Native OS PiP (if invoked programmatically)
    if (window.SpokesNative && typeof window.SpokesNative.enterPictureInPicture === 'function') {
        try {
            window.SpokesNative.enterPictureInPicture();
            return true;
        } catch (e) {
            console.warn('[VoicePiP] Native enterPictureInPicture failed:', e);
        }
    }

    // 2. Close existing OS PiP if already open
    if (_docPipWindow && !_docPipWindow.closed) {
        closeOsPip();
        return false;
    }
    if (typeof document !== 'undefined' && document.pictureInPictureElement) {
        closeOsPip();
        return false;
    }

    // 3. Electron Desktop: Native Frameless Spokes Child Window ('spokes_call_pip')
    //    Never fall back to Chromium's stock video PiP overlay on Desktop.
    if (isDesktopApp()) {
        try {
            return openDesktopPipWindow();
        } catch (e) {
            console.warn('[VoicePiP] Desktop Spokes PiP window open failed:', e);
            return false;
        }
    }

    // 4. Chromium Web Browser (Chrome / Edge): Document Picture-in-Picture
    if (typeof window !== 'undefined' && 'documentPictureInPicture' in window) {
        try {
            return await openDocumentPip();
        } catch (e) {
            console.warn('[VoicePiP] Document PiP failed, attempting video PiP fallback:', e);
        }
    }

    // 5. Safari / Firefox Web Browser: Canvas-backed HTMLVideoElement PiP
    try {
        return await openFallbackVideoPip();
    } catch (e) {
        console.warn('[VoicePiP] Fallback video PiP failed:', e);
        stopFallbackCanvasLoop();
        return false;
    }
}

export function closeOsPip() {
    stopPipCloseWatchTimer();
    let closedAny = false;
    if (_docPipWindow && !_docPipWindow.closed) {
        const win = _docPipWindow;
        _docPipWindow = null;
        _docPipRefs = null;
        closedAny = true;
        try { win.close(); } catch (e) {}
        notifyOsPipChanged(false);
    } else if (_docPipWindow) {
        _docPipWindow = null;
        _docPipRefs = null;
        closedAny = true;
        notifyOsPipChanged(false);
    }
    if (typeof document !== 'undefined' && document.pictureInPictureElement) {
        closedAny = true;
        document.exitPictureInPicture().catch(() => {});
    }
    if (_isFallbackPipOpen) {
        _isFallbackPipOpen = false;
        closedAny = true;
        stopFallbackCanvasLoop();
        notifyOsPipChanged(false);
    }
    syncPipTrackSubscriptions(selectPipTarget());
    if (closedAny) {
        restoreStageVideosIfUnsubpressed();
    }
}

// --- MediaSession API Integration (OS Media Controls) ---

function syncMediaSession() {
    if (typeof navigator === 'undefined' || !('mediaSession' in navigator)) return;
    if (isNativePlatform() || isAppleMobileDevice()) {
        clearMediaSession();
        return;
    }
    try {
        const target = selectPipTarget();
        if (typeof MediaMetadata !== 'undefined') {
            navigator.mediaSession.metadata = new MediaMetadata({
                title: _callMeta.channelName || 'Voice Call',
                artist: target.speaker?.name || 'Spokes'
            });
        }

        const safeSetHandler = (action, handler) => {
            try { navigator.mediaSession.setActionHandler(action, handler); } catch (e) {}
        };

        safeSetHandler('togglemicrophone', () => {
            if (typeof _actions.onToggleMute === 'function') _actions.onToggleMute();
        });
        safeSetHandler('togglecamera', () => {
            if (typeof _actions.onToggleCamera === 'function') _actions.onToggleCamera();
        });
        safeSetHandler('hangup', () => {
            if (typeof _actions.onHangUp === 'function') _actions.onHangUp();
        });
        // Do not register 'enterpictureinpicture' on MediaSession so desktop browsers
        // never auto-prompt or auto-open Picture-in-Picture when the user switches tabs.
        safeSetHandler('enterpictureinpicture', null);
    } catch (e) {}
}

function clearMediaSession() {
    if (typeof navigator === 'undefined' || !('mediaSession' in navigator)) return;
    try { navigator.mediaSession.metadata = null; } catch (e) {}
    try { navigator.mediaSession.playbackState = 'none'; } catch (e) {}
    for (const action of ['togglemicrophone', 'togglecamera', 'hangup', 'enterpictureinpicture', 'play', 'pause', 'seekbackward', 'seekforward', 'previoustrack', 'nexttrack']) {
        try { navigator.mediaSession.setActionHandler(action, null); } catch (e) {}
    }
}

// ============================================================================
// Mobile Native PiP & ForegroundService Metadata Coordinator
// ============================================================================

function pushNativeMobileCallMetadata(force = false) {
    if (!window.SpokesNative || typeof window.SpokesNative.updateCallMetadata !== 'function') {
        return;
    }

    try {
        const target = selectPipTarget();

        // Default to portrait (9:16) when no remote video is active; dynamically match remote video feed when active
        let videoWidth = 9;
        let videoHeight = 16;
        if (target.mode === 'video' && target.primaryTrack) {
            const dims = resolveTrackIntrinsicDimensions(target.primaryTrack);
            if (dims.width > 0 && dims.height > 0) {
                videoWidth = dims.width;
                videoHeight = dims.height;
            }
        }

        let localCameraFacing = 'user';
        try {
            const selfSettings = target.selfTrack?.mediaStreamTrack?.getSettings?.() || {};
            const rawFacing = selfSettings.facingMode || target.selfTrack?.facingMode || (typeof window !== 'undefined' ? window._spokesLocalCameraFacing : '') || 'user';
            localCameraFacing = rawFacing === 'environment' ? 'environment' : 'user';
        } catch (e) {}

        const remoteParticipants = [];
        for (const p of _callMeta.participants.values()) {
            if (p && p.id && p.id !== _callMeta.localUserId) {
                remoteParticipants.push(p);
            }
        }
        const singleRemote = remoteParticipants.length === 1 ? remoteParticipants[0] : null;

        const payload = {
            active: Boolean(_callMeta.active),
            channelId: _callMeta.channelId || '',
            channelName: _callMeta.channelName || 'Voice Call',
            serverName: _callMeta.serverName || '',
            remoteParticipantCount: remoteParticipants.length,
            singleRemoteAvatarUrl: singleRemote?.avatarUrl || '',
            singleRemoteName: singleRemote?.name || '',
            singleRemoteInitials: singleRemote?.initials || '',
            isMuted: Boolean(_callMeta.isMuted),
            isDeafened: Boolean(_callMeta.isDeafened),
            isVideoOn: Boolean(_callMeta.isVideoOn),
            isScreenSharing: Boolean(_callMeta.isScreenSharing),
            localCameraFacing,
            hasAnyVideo: target.mode === 'video',
            primaryVideoParticipantId: target.primaryTrack?.participantId || '',
            primaryVideoSource: target.primaryTrack?.source || 'camera',
            videoWidth,
            videoHeight,
            speakerName: target.speaker?.name || _callMeta.channelName || 'Voice Call',
            speakerInitials: target.speaker?.initials || '?',
            speakerAvatarUrl: target.speaker?.avatarUrl || '',
            isSpeakerSpeaking: Boolean(target.isSpeaking),
            isSpeakerMuted: Boolean(target.isSpeakerMuted)
        };

        const json = JSON.stringify(payload);
        if (!force && json === _lastNativePayloadJson) return;
        _lastNativePayloadJson = json;

        window.SpokesNative.updateCallMetadata(json);
    } catch (e) {
        console.warn('[VoicePiP] SpokesNative.updateCallMetadata error:', e);
    }
}

function detachStageVideosForExternalPip() {
    if (typeof document === 'undefined') return;
    const container = document.getElementById('voice-video-container');
    if (!container) return;
    container.querySelectorAll('video.livekit-camera, video.livekit-screenshare').forEach(videoEl => {
        try {
            if (videoEl.srcObject) {
                videoEl.pause();
                videoEl.srcObject = null;
            }
            videoEl.classList.remove('has-stream');
            videoEl.style.opacity = '0';
            videoEl.style.pointerEvents = 'none';
        } catch (e) {}
    });
    for (const entry of _videoTracks.values()) {
        if (entry?.livekitTrack && typeof entry.livekitTrack.detach === 'function') {
            try { entry.livekitTrack.detach(); } catch (e) {}
        }
    }
}

function restoreStageVideosIfUnsubpressed() {
    if (!isStageSuppressedByExternalPip() && typeof _actions.onRestoreStageTracks === 'function') {
        requestAnimationFrame(() => {
            if (!isStageSuppressedByExternalPip() && typeof _actions.onRestoreStageTracks === 'function') {
                try { _actions.onRestoreStageTracks(); } catch (e) {}
            }
        });
    }
}

function setNativePipModeInternal(isInPip) {
    const next = Boolean(isInPip);
    const changed = _isNativeMobilePip !== next;
    _isNativeMobilePip = next;
    if (typeof document !== 'undefined' && document.documentElement) {
        // html.spokes-native-pip drives 0ms full-bleed OS PiP CSS without mutating Blazor-owned classes on #spokes-voice-pip-overlay
        document.documentElement.classList.toggle('spokes-native-pip', next);
    }

    if (next) {
        // Release covered stage <video> hardware sinks so #spokes-pip-primary-video is the sole decoder sink
        detachStageVideosForExternalPip();
    } else if (!_callMeta.inAppPipActive) {
        detachInAppPipVideos();
    }

    // Synchronously bind/detach pre-mounted stage videos in 0ms without waiting for SignalR round-trip
    refreshAllPipSurfaces();

    if (_dotnetHelper) {
        _dotnetHelper.invokeMethodAsync('OnNativeMobilePipChanged', next).catch(() => {});
    }

    requestAnimationFrame(() => {
        refreshAllPipSurfaces();
        if (typeof _actions.onSyncNativeVideoSlots === 'function') {
            try { _actions.onSyncNativeVideoSlots(); } catch (e) {}
        }
        if (!next && changed) {
            restoreStageVideosIfUnsubpressed();
        }
    });

    if (next) {
        setTimeout(() => {
            if (_isNativeMobilePip) {
                syncInAppPipElements();
                if (typeof _actions.onSyncNativeVideoSlots === 'function') {
                    try { _actions.onSyncNativeVideoSlots(); } catch (e) {}
                }
            }
        }, 120);
    } else if (changed) {
        // Re-sync stage slots after Android OS PiP exit bounds animation and Blazor layout settle.
        setTimeout(() => {
            if (!_isNativeMobilePip) {
                restoreStageVideosIfUnsubpressed();
            }
        }, 90);
        setTimeout(() => {
            if (!_isNativeMobilePip) {
                restoreStageVideosIfUnsubpressed();
            }
        }, 280);
    }
}

export function refreshAllPipSurfaces() {
    try {
        const target = selectPipTarget();
        syncPipTrackSubscriptions(target);
        if (_callMeta.inAppPipActive || _isNativeMobilePip) {
            syncInAppPipElements();
        }
        if (_docPipWindow && !_docPipWindow.closed) {
            syncDocumentPipDom();
        }
        if (_callMeta.active) {
            syncMediaSession();
            pushNativeMobileCallMetadata(false);
        }
    } catch (e) {
        console.warn('[VoicePiP] refreshAllPipSurfaces error:', e);
    }
}

if (typeof window !== 'undefined') {
    window.spokesSetNativePipMode = function (isInPip) {
        setNativePipModeInternal(isInPip);
    };

    window.spokesSetIosNativePipActive = function (isActive) {
        _isIosOsPipActive = Boolean(isActive);
        if (_isIosOsPipActive && _callMeta.active) {
            pushNativeMobileCallMetadata(true);
        }
    };

    if (typeof window.addEventListener === 'function') {
        window.addEventListener('spokes:host-view-visible-changed', () => {
            refreshAllPipSurfaces();
        });
    }
}
