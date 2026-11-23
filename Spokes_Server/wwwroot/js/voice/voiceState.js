// voiceState.js — Single canonical client-side voice & video session state container,
// Blazor circuit liveness guard, 3-state mute evaluator, and complete room snapshot builder.

export const PROCESSED_MIC_PREFIX = 'processed-mic:';
export const VAD_HANGOVER_MS = 280;
export const VAD_ATTACK_FRAMES = 2;

export const voiceState = {
    // Active Web/Desktop LiveKit room & Blazor callback reference
    currentRoom: null,
    dotnetHelper: null,

    // Local media & mute state
    isLocalSoftwareMuted: false,
    isLocalHardwareMuted: false,
    lastReportedLocalSpeaking: false,
    localCameraDesired: false,
    localCameraFacing: 'user',
    lastCameraResolution: 'Auto',
    lastCameraFps: 30,
    lastScreenResolution: 'Auto',
    lastScreenFps: 30,
    lastScreenMode: 'motion',
    globalScreenTracks: [],
    isStoppingScreenShare: false,
    screenShareOperationChain: Promise.resolve(),
    micOperationSeq: 0,
    suppressNextUnpublishMuteNotify: false,
    activeLocalAudioProcessor: null,
    localHardwareMuteCleanup: null,
    currentNoiseSuppression: 'webrtc',

    // Audio settings & routing
    masterVolume: 1.0,
    voiceSensitivity: 50.0,
    autoGainControl: true,
    incomingVoiceNormalization: true,
    savedVoiceSettings: {
        masterVolume: 100,
        participantVolumes: {},
        voiceSensitivity: 50,
        autoGainControl: true,
        incomingVoiceNormalization: true
    },
    mutedRemoteScreenAudio: new Set(),
    coLocatedProcessedUsers: new Set(),
    mutedRoommateUsers: new Set(),
    suppressedPublicationSids: new Set(),
    cachedMicDevices: null,
    cachedSpeakerDevices: null,
    lastAudioDevicesSignature: '',
    lastPublishedSnapshotSignature: '',
    snapshotSyncTimer: null,
    activeVoiceChannelId: '',
    activeNativeSpeakerId: 'default',

    // Desktop Direct LAN UDP engine state
    activeDirectLanPeerUsers: new Set(),
    directPeerTokenToUserId: new Map(),
    directUserIdToPeerToken: new Map(),
    directBridgeStatusUnsub: null,
    directSessionActive: false,
    directSessionRoomId: '',
    lastDirectSessionFingerprint: '',
    lastDirectActivePeerKey: '',
    lastDirectBridgeStatus: null,

    // Unified Native Mobile (Android & iOS SpokesNativeVoice) state
    usingNativeMobileVoice: false,
    nativeConnectionState: 'disconnected', // 'disconnected' | 'connecting' | 'connected' | 'reconnecting'
    nativeLocalIdentity: '',
    nativeEffectiveFilter: 'rnnoise',
    nativeActiveCameraId: '',
    nativeCachedCameras: [],
    nativeListeners: [],
    nativeRemoteParticipants: new Map(),   // identity -> metadata
    nativeRemoteMicMuted: new Map(),       // identity -> true | false | null (3-state)
    nativeRemoteScreenAudio: new Set(),    // identity
    nativeVideoTracks: new Map(),          // identity -> Set<'camera' | 'screenshare'>
    nativeVideoTrackSids: new Map(),       // `${identity}:${source}` -> active track sid
    nativeVideoDims: new Map(),            // `${identity}:${source}` -> { width, height }
    nativeLastSlotSignature: '',

    // Fullscreen gesture transform (shared between stage & native video slots)
    fullscreenGestureController: null,
    fullscreenParticipantId: '',
    fullscreenDotNetHelper: null,
    fullscreenTransform: {
        identity: '',
        source: '',
        scale: 1,
        panX: 0,
        panY: 0,
        rotationDeg: 0
    },

    // Blazor circuit reconnect snapshot sync state
    snapshotSyncScheduled: false,
    pendingCircuitSnapshotMethod: null,
    blazorConnectedListenerAttached: false,
    isRoomReconnecting: false,

    // Cached Capacitor AudioSession plugin handle
    spokesAudioSessionPlugin: null
};

export function toArray(mapOrObj) {
    if (!mapOrObj) return [];
    return typeof mapOrObj.values === 'function'
        ? Array.from(mapOrObj.values())
        : Object.values(mapOrObj);
}

export function isBotIdentity(identity) {
    return typeof identity === 'string' && identity.trim().toLowerCase().startsWith('bot:');
}

export function isSyntheticIdentity(identity) {
    return isBotIdentity(identity);
}

export function getEffectiveParticipantIdentity(participantOrIdentity) {
    const raw = typeof participantOrIdentity === 'string'
        ? participantOrIdentity
        : (participantOrIdentity?.identity || '');
    return raw ? String(raw).trim() : '';
}

export function getParticipantTrackPublications(participant) {
    if (!participant) return [];
    const seen = new Set();
    const out = [];
    const addFrom = (col) => {
        for (const pub of toArray(col)) {
            if (pub && !seen.has(pub)) {
                seen.add(pub);
                out.push(pub);
            }
        }
    };
    addFrom(participant.trackPublications);
    addFrom(participant.videoTrackPublications);
    addFrom(participant.audioTrackPublications);
    addFrom(participant.tracks);
    return out;
}

export function isScreenShareTrackOrPub(track, publication) {
    const source = publication?.source || track?.source;
    const screenSrc = window.LivekitClient?.Track?.Source?.ScreenShare;
    return source === screenSrc || source === 'screen_share';
}

export function isHumanMicrophonePublication(publication, participant) {
    if (!participant || !participant.identity || isBotIdentity(participant.identity)) {
        return false;
    }
    const Kind = window.LivekitClient?.Track?.Kind;
    const Source = window.LivekitClient?.Track?.Source;
    const isAudio = publication?.kind === Kind?.Audio || publication?.track?.kind === Kind?.Audio;
    if (!isAudio) return false;

    const trackName = publication?.trackName || publication?.name || publication?.track?.name || '';
    if (typeof trackName === 'string' && trackName.startsWith(PROCESSED_MIC_PREFIX)) {
        return false;
    }

    const src = publication?.source || publication?.track?.source;
    if (Source && src === Source.ScreenShareAudio) {
        return false;
    }
    return !Source || !src || src === Source.Microphone || src === Source.Unknown || src === 'unknown';
}

export function getRemoteParticipantsIterable(room = voiceState.currentRoom) {
    if (!room) return [];
    return toArray(room.remoteParticipants || room.participants);
}

export function isEffectiveLocalMicMuted() {
    return voiceState.isLocalSoftwareMuted || voiceState.isLocalHardwareMuted;
}

export function reportLocalSpeakingState(isSpeaking) {
    const next = Boolean(isSpeaking) && !isEffectiveLocalMicMuted();
    if (next === voiceState.lastReportedLocalSpeaking) return;
    voiceState.lastReportedLocalSpeaking = next;
    // Instant 0ms local DOM feedback outside active 2+ Physical Room DSP isolation
    // (authoritative server updates via syncSpeakingParticipants overwrite seamlessly).
    const activeChannelId = voiceState.activeVoiceChannelId
        || (typeof voiceState.currentRoom?.name === 'string' && voiceState.currentRoom.name.startsWith('channel-')
            ? voiceState.currentRoom.name.slice('channel-'.length)
            : '');
    const localId = voiceState.currentRoom?.localParticipant?.identity || voiceState.nativeLocalIdentity;
    if (activeChannelId &&
        localId &&
        voiceState.coLocatedProcessedUsers.size === 0 &&
        typeof window !== 'undefined' &&
        window.spokesVoiceSpeaking &&
        typeof window.spokesVoiceSpeaking.setUserSpeaking === 'function') {
        window.spokesVoiceSpeaking.setUserSpeaking(activeChannelId, localId, next);
    }
    invokeDotNetSafe('OnLocalSpeakingChanged', next);
}

export function resetLocalSpeakingState() {
    voiceState.lastReportedLocalSpeaking = false;
}

export function isNativePlatform() {
    if (typeof window === 'undefined') return false;
    if (typeof window.isCapacitorNative === 'function') {
        return Boolean(window.isCapacitorNative());
    }
    return Boolean(window.Capacitor && window.Capacitor.isNativePlatform && window.Capacitor.isNativePlatform());
}

export function getNativePlatform() {
    if (typeof window !== 'undefined' && window.Capacitor && window.Capacitor.getPlatform) {
        return window.Capacitor.getPlatform();
    }
    return 'web';
}

export function isDesktopClient(userAgentOverride) {
    if (isNativePlatform()) return false;
    if (typeof window !== 'undefined') {
        if (window.SpokesBridge?.isDesktop === true || window.SpokesBridge?.platform === 'desktop') return true;
        if (window.isSpokesDesktop === true || Boolean(window.SpokesDesktopNative)) return true;
        if (window.SpokesBridge?.directRoomAudio && typeof window.SpokesBridge.directRoomAudio === 'object') return true;
    }
    const ua = typeof userAgentOverride === 'string'
        ? userAgentOverride
        : (typeof navigator !== 'undefined' ? (navigator.userAgent || '') : '');
    return /SpokesDesktop|Electron/i.test(ua);
}

export function isAppleMobileDevice() {
    if (isNativePlatform() && getNativePlatform() === 'ios') return true;
    if (isDesktopClient()) return false;
    if (typeof navigator === 'undefined') return false;
    return Boolean(/iPhone|iPad|iPod/i.test(navigator.userAgent) || (navigator.userAgent.includes('Mac') && typeof document !== 'undefined' && 'ontouchend' in document));
}

export function isMobileVoiceClient(userAgentOverride) {
    if (isNativePlatform()) return true;
    if (isDesktopClient(userAgentOverride)) return false;
    if (isAppleMobileDevice()) return true;
    const ua = typeof userAgentOverride === 'string'
        ? userAgentOverride
        : (typeof navigator !== 'undefined' ? (navigator.userAgent || '') : '');
    return /Android|iPhone|iPad|iPod/i.test(ua);
}

export function resetMobileCameraSelectionForNewCall() {
    voiceState.localCameraFacing = 'user';
    voiceState.nativeActiveCameraId = '';
    if (typeof window !== 'undefined') {
        window._spokesLocalCameraFacing = 'user';
    }
    try {
        if (typeof localStorage !== 'undefined') {
            localStorage.removeItem('spokes_active_camera_id');
        }
    } catch {}
}

export function getNativeVoicePlugin() {
    if (!isNativePlatform()) return null;
    const platform = getNativePlatform();
    if (platform !== 'android' && platform !== 'ios') return null;
    return window.Capacitor?.Plugins?.SpokesNativeVoice || null;
}

export function getNativeVoiceTelemetryPlatformTag() {
    return getNativePlatform() === 'ios' ? 'ios-native' : 'android-native';
}

export function getAudioSessionPlugin() {
    if (!voiceState.spokesAudioSessionPlugin && typeof window !== 'undefined' && window.Capacitor?.registerPlugin) {
        voiceState.spokesAudioSessionPlugin = window.Capacitor.registerPlugin('SpokesAudioSessionPlugin');
    }
    return voiceState.spokesAudioSessionPlugin || (typeof window !== 'undefined' ? window.Capacitor?.Plugins?.SpokesAudioSessionPlugin : null);
}

export function formatVoiceError(error) {
    if (!error) return 'Unknown error';
    if (typeof error === 'string') return error;
    const name = error.name ? String(error.name) : 'Error';
    const message = error.message ? String(error.message) : String(error);
    return `${name}: ${message}`;
}

export function isDisplayMediaUserCancelledError(error) {
    if (!error) return false;
    const name = String(error.name || '');
    const msg = String(error.message || '').toLowerCase();
    if (name === 'NotAllowedError' || name === 'AbortError') {
        if (!msg.includes('audio source')) {
            return true;
        }
    }
    return msg.includes('permission denied') || msg.includes('cancelled') || msg.includes('canceled');
}

export function isBlazorCircuitConnected() {
    if (!voiceState.dotnetHelper) return false;
    if (typeof window !== 'undefined') {
        if (window._blazorDisconnected || window._isServerRestarting || window._reloadInProgress) {
            return false;
        }
        const ws = window.__spokesBlazorSocket;
        if (ws && typeof ws.readyState === 'number' && ws.readyState !== 1) {
            return false;
        }
    }
    return true;
}

export function invokeDotNetSafe(methodName, ...args) {
    if (!methodName || !isBlazorCircuitConnected()) return Promise.resolve(null);
    try {
        const p = voiceState.dotnetHelper.invokeMethodAsync(methodName, ...args);
        if (p && typeof p.catch === 'function') {
            return p.catch(() => null);
        }
        return Promise.resolve(p);
    } catch {
        return Promise.resolve(null);
    }
}

/**
 * 3-state remote microphone mute evaluation:
 *  - true  : participant has >= 1 mic publication and ALL are muted
 *  - false : participant has >= 1 active unmuted mic publication
 *  - null  : participant has 0 mic publications (initial connect or web mute-by-unpublish -> defer to server state)
 */
export function getRemoteParticipantMicMutedState(participant) {
    if (!participant) return null;
    const pubs = getParticipantTrackPublications(participant);
    const micPubs = [];
    for (const pub of pubs) {
        if (!pub) continue;
        const Kind = window.LivekitClient?.Track?.Kind;
        const isAudio = !Kind || pub.kind === Kind.Audio || pub.track?.kind === Kind.Audio;
        if (isAudio && isHumanMicrophonePublication(pub, participant)) {
            micPubs.push(pub);
        }
    }
    if (micPubs.length === 0) {
        return null;
    }
    return micPubs.every(pub => Boolean(pub.isMuted));
}

function getParticipantMediaStateFromLiveKit(participant) {
    let isCameraOn = false;
    let isScreenSharing = false;
    let hasScreenAudio = false;
    let videoHeight = 0;
    let videoFps = 0;

    if (!participant) {
        return { isCameraOn, isScreenSharing, hasScreenAudio, videoHeight, videoFps };
    }

    const Kind = window.LivekitClient?.Track?.Kind;
    const Source = window.LivekitClient?.Track?.Source;
    const pubs = getParticipantTrackPublications(participant);

    for (const pub of pubs) {
        if (!pub || pub.isMuted) continue;
        const kind = pub.kind || pub.track?.kind;
        const source = pub.source || pub.track?.source;
        const isVideo = kind === Kind?.Video || kind === 'video';
        const isAudio = kind === Kind?.Audio || kind === 'audio';

        if (isVideo) {
            const isScreen = isScreenShareTrackOrPub(pub.track, pub);
            if (isScreen) {
                isScreenSharing = true;
            } else {
                isCameraOn = true;
            }
            const dims = pub.videoDimensions || pub.dimensions || pub.track?.dimensions;
            const edge = Math.max(Number(dims?.width) || 0, Number(dims?.height) || 0);
            if (edge > videoHeight) {
                videoHeight = edge;
            }
            const fps = Number(pub.track?.mediaStreamTrack?.getSettings?.()?.frameRate) || 0;
            if (fps > videoFps) {
                videoFps = Math.round(fps);
            }
        } else if (isAudio && (source === Source?.ScreenShareAudio || source === 'screen_share_audio')) {
            hasScreenAudio = true;
        }
    }

    return { isCameraOn, isScreenSharing, hasScreenAudio, videoHeight, videoFps };
}

export function buildVoiceRoomSnapshot() {
    if (voiceState.usingNativeMobileVoice) {
        const localId = voiceState.nativeLocalIdentity || '';
        const localTracks = localId ? voiceState.nativeVideoTracks.get(localId) : null;
        const participants = [];
        for (const [userId, meta] of voiceState.nativeRemoteParticipants.entries()) {
            if (!userId || userId === localId || isSyntheticIdentity(userId)) continue;
            const micMutedState = voiceState.nativeRemoteMicMuted.has(userId)
                ? voiceState.nativeRemoteMicMuted.get(userId)
                : null;
            const vTracks = voiceState.nativeVideoTracks.get(userId);
            const isCameraOn = Boolean(vTracks?.has('camera'));
            const isScreenSharing = Boolean(vTracks?.has('screenshare'));
            const hasScreenAudio = voiceState.nativeRemoteScreenAudio.has(userId);
            const screenDims = voiceState.nativeVideoDims.get(`${userId}:screenshare`);
            const camDims = voiceState.nativeVideoDims.get(`${userId}:camera`);
            const dims = (isScreenSharing && screenDims) ? screenDims : camDims;
            const videoHeight = dims ? Math.max(Number(dims.width) || 0, Number(dims.height) || 0) : 0;

            participants.push({
                userId,
                isMuted: micMutedState === true,
                isMicMuted: micMutedState,
                metadata: meta || '',
                isCameraOn,
                isScreenSharing,
                hasScreenAudio,
                videoHeight,
                videoFps: videoHeight > 0 ? 30 : 0
            });
        }
        return {
            channelId: '',
            localUserId: localId,
            localIsMuted: isEffectiveLocalMicMuted(),
            localIsVideoOn: Boolean(localTracks?.has('camera') || voiceState.localCameraDesired),
            localIsScreenSharing: Boolean(localTracks?.has('screenshare')),
            participants
        };
    }

    if (!voiceState.currentRoom) return null;
    const localParticipant = voiceState.currentRoom.localParticipant;
    const localId = localParticipant?.identity || '';
    const localMedia = getParticipantMediaStateFromLiveKit(localParticipant);
    const participants = [];
    for (const p of getRemoteParticipantsIterable()) {
        const id = p?.identity;
        if (!id || id === localId || isSyntheticIdentity(id)) continue;
        const micMutedState = getRemoteParticipantMicMutedState(p);
        const media = getParticipantMediaStateFromLiveKit(p);
        participants.push({
            userId: id,
            isMuted: micMutedState === true,
            isMicMuted: micMutedState,
            metadata: typeof p.metadata === 'string' ? p.metadata : '',
            isCameraOn: media.isCameraOn,
            isScreenSharing: media.isScreenSharing,
            hasScreenAudio: media.hasScreenAudio,
            videoHeight: media.videoHeight,
            videoFps: media.videoFps
        });
    }
    return {
        channelId: voiceState.currentRoom.name || '',
        localUserId: localId,
        localIsMuted: isEffectiveLocalMicMuted(),
        localIsVideoOn: localMedia.isCameraOn,
        localIsScreenSharing: localMedia.isScreenSharing,
        participants
    };
}

export function ensureBlazorConnectedListener() {
    if (voiceState.blazorConnectedListenerAttached || typeof window === 'undefined' || typeof window.addEventListener !== 'function') {
        return;
    }
    voiceState.blazorConnectedListenerAttached = true;
    window.addEventListener('spokes:blazor-connected', () => {
        flushPendingVoiceRoomSnapshot();
    });
}

export function flushPendingVoiceRoomSnapshot() {
    if (!voiceState.pendingCircuitSnapshotMethod || !voiceState.dotnetHelper || !isBlazorCircuitConnected()) return;
    const methodName = voiceState.pendingCircuitSnapshotMethod;
    voiceState.pendingCircuitSnapshotMethod = null;
    publishVoiceRoomSnapshot(methodName);
}

export function resetVoiceRoomSnapshotSync() {
    voiceState.lastPublishedSnapshotSignature = '';
    voiceState.snapshotSyncScheduled = false;
    if (voiceState.snapshotSyncTimer) {
        clearTimeout(voiceState.snapshotSyncTimer);
        voiceState.snapshotSyncTimer = null;
    }
}

function computeVoiceRoomSnapshotSignature(snapshot) {
    if (!snapshot) return '';
    const participants = Array.isArray(snapshot.participants)
        ? snapshot.participants
            .slice()
            .sort((a, b) => (a.userId || '').localeCompare(b.userId || ''))
            .map(p => `${p.userId}|${p.isMuted ? 1 : 0}|${p.isMicMuted === null ? 'n' : (p.isMicMuted ? 1 : 0)}|${p.isCameraOn ? 1 : 0}|${p.isScreenSharing ? 1 : 0}|${p.hasScreenAudio ? 1 : 0}|${p.videoHeight || 0}|${p.videoFps || 0}|${p.metadata || ''}`)
            .join(';')
        : '';
    return `${snapshot.channelId}|${snapshot.localUserId}|${snapshot.localIsMuted ? 1 : 0}|${snapshot.localIsVideoOn ? 1 : 0}|${snapshot.localIsScreenSharing ? 1 : 0}|${participants}`;
}

export function publishVoiceRoomSnapshot(methodName = 'SyncRoomSnapshotAsync', force = false) {
    if (!voiceState.dotnetHelper) return;
    if (!isBlazorCircuitConnected()) {
        ensureBlazorConnectedListener();
        if (methodName === 'OnRoomReconnectedAsync' || !voiceState.pendingCircuitSnapshotMethod) {
            voiceState.pendingCircuitSnapshotMethod = methodName;
        }
        return;
    }
    const snapshot = buildVoiceRoomSnapshot();
    if (!snapshot) return;
    const isReconnectSync = methodName === 'OnRoomReconnectedAsync';
    const sig = computeVoiceRoomSnapshotSignature(snapshot);
    if (!force && !isReconnectSync && sig === voiceState.lastPublishedSnapshotSignature) {
        try {
            if (typeof window !== 'undefined' && window.spokesChatDiagnostics?.isEnabled?.()) {
                window.spokesChatDiagnostics.mark(`Voice: ${methodName} Skipped (identical snapshot)`);
            }
        } catch (_) {}
        return;
    }
    voiceState.lastPublishedSnapshotSignature = sig;
    voiceState.pendingCircuitSnapshotMethod = null;
    invokeDotNetSafe(methodName, snapshot);
}

const SNAPSHOT_COALESCE_MS = 30;

export function scheduleRoomSnapshotSync() {
    if (voiceState.snapshotSyncScheduled || !voiceState.dotnetHelper) return;
    voiceState.snapshotSyncScheduled = true;
    voiceState.snapshotSyncTimer = setTimeout(() => {
        voiceState.snapshotSyncTimer = null;
        voiceState.snapshotSyncScheduled = false;
        publishVoiceRoomSnapshot('SyncRoomSnapshotAsync');
    }, SNAPSHOT_COALESCE_MS);
}

export async function applyTrackSenderParameters(track, degradationPref, configureEncodingsFn, logContext) {
    if (!track) return;
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
                const before = params.encodings.map(enc => ({
                    active: enc.active,
                    maxBitrate: enc.maxBitrate,
                    maxFramerate: enc.maxFramerate,
                    scaleResolutionDownBy: enc.scaleResolutionDownBy
                }));
                configureEncodingsFn(params.encodings);
                if (track.encodings) {
                    track.encodings = params.encodings;
                }
                const changed = params.encodings.some((enc, idx) =>
                    enc.active !== before[idx].active ||
                    enc.maxBitrate !== before[idx].maxBitrate ||
                    enc.maxFramerate !== before[idx].maxFramerate ||
                    enc.scaleResolutionDownBy !== before[idx].scaleResolutionDownBy
                );
                if (changed) {
                    await sender.setParameters(params);
                }
            }
        } catch (e) {
            console.warn(`[VoiceInterop] Failed to update ${logContext} RTCRtpSender parameters:`, e);
        }
    }
}
