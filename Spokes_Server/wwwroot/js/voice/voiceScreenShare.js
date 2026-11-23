// voiceScreenShare.js — Screen sharing, resilient reconnect publication, and thin delegation
// to Spokes_Desktop's single canonical WASAPI loopback MediaStreamTrack factory.

import {
    voiceState,
    toArray,
    getParticipantTrackPublications,
    isAppleMobileDevice,
    invokeDotNetSafe,
    formatVoiceError,
    isDisplayMediaUserCancelledError,
    scheduleRoomSnapshotSync,
    applyTrackSenderParameters
} from './voiceState.js';
import {
    unregisterPipVideoTrack,
    updatePipCallState
} from '../voicePip.js';

let _callbacks = {
    isRoomConnected: () => false,
    stopLivekitTrackCompletely: () => {},
    clearParticipantVideoDom: () => {},
    notifyHostVoiceState: () => {}
};

export function configureScreenShareCallbacks(callbacks = {}) {
    _callbacks = { ..._callbacks, ...callbacks };
}

export function getScreenPreset(resolutionStr, is60fps) {
    const targetFps = is60fps ? 60 : 30;

    if (isAppleMobileDevice()) {
        if (resolutionStr === '2160p') return { width: 3840, height: 2160, maxBitrate: is60fps ? 18000000 : 12000000, frameRate: targetFps };
        if (resolutionStr === '1440p') return { width: 2560, height: 1440, maxBitrate: is60fps ? 12000000 : 8000000, frameRate: targetFps };
        return { width: 1920, height: 1080, maxBitrate: is60fps ? 8000000 : 6000000, frameRate: targetFps };
    }

    if (resolutionStr === '2160p') return { width: 3840, height: 2160, maxBitrate: is60fps ? 35000000 : 25000000, frameRate: targetFps };
    if (resolutionStr === '1440p') return { width: 2560, height: 1440, maxBitrate: is60fps ? 24000000 : 16000000, frameRate: targetFps };
    if (resolutionStr === '1080p') return { width: 1920, height: 1080, maxBitrate: is60fps ? 15000000 : 10000000, frameRate: targetFps };
    if (resolutionStr === '720p') return { width: 1280, height: 720, maxBitrate: is60fps ? 7500000 : 5000000, frameRate: targetFps };

    return { width: 1920, height: 1080, maxBitrate: is60fps ? 12000000 : 8000000, frameRate: targetFps };
}

export function getScreenTrackMaxSourceEdge(track) {
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

export function computeScreenScaleDown(track, targetPreset) {
    if (!track || !targetPreset) return 1.0;
    const sourceMaxEdge = getScreenTrackMaxSourceEdge(track);
    const targetMaxEdge = Math.max(targetPreset.width || 1920, targetPreset.height || 1080);

    if (sourceMaxEdge <= 0 || targetMaxEdge <= 0 || sourceMaxEdge <= targetMaxEdge) {
        return 1.0;
    }
    const rawScale = sourceMaxEdge / targetMaxEdge;
    return rawScale < 1.05 ? 1.0 : Math.round(rawScale * 1000) / 1000;
}

export async function applyScreenShareEncoding(track, resolutionStr = 'Auto', targetFps = 30, mode = 'motion', { skipConstraints = false } = {}) {
    if (!track) return;
    const isAuto = !resolutionStr || resolutionStr === 'Auto';
    const is60 = targetFps > 35;
    const targetPreset = getScreenPreset(resolutionStr, is60);
    const isDetail = mode === 'detail';
    const degradationPref = (isAuto && !isDetail) ? 'balanced' : 'maintain-resolution';

    if (track.mediaStreamTrack) {
        try {
            track.mediaStreamTrack.contentHint = isDetail ? 'detail' : 'motion';
        } catch {}
        if (!skipConstraints && typeof track.mediaStreamTrack.applyConstraints === 'function') {
            try {
                await track.mediaStreamTrack.applyConstraints({
                    frameRate: { ideal: targetFps, max: targetFps }
                });
            } catch (e) {
                console.warn('[VoiceInterop] Failed to apply frameRate constraint on screen track:', e);
            }
        }
    }

    const scaleDown = computeScreenScaleDown(track, targetPreset);
    await applyTrackSenderParameters(track, degradationPref, (encodings) => {
        const topIdx = encodings.length - 1;
        encodings.forEach((enc, idx) => {
            enc.maxFramerate = targetFps;
            if (idx === topIdx) {
                enc.maxBitrate = targetPreset.maxBitrate;
                enc.scaleResolutionDownBy = scaleDown;
            } else {
                enc.maxBitrate = Math.max(500000, Math.round(targetPreset.maxBitrate / 3));
                enc.scaleResolutionDownBy = scaleDown * 2;
            }
        });
    }, 'screen share');
}

async function publishScreenShareAudioTrack(audioTrack) {
    await publishLocalTrackResilient(audioTrack, {
        source: window.LivekitClient.Track.Source.ScreenShareAudio,
        dtx: false,
        red: true,
        forceStereo: true,
        audioBitrate: 128000
    });
}

export function stopDesktopNativeLoopback() {
    if (typeof window === 'undefined') return;
    if (typeof window._spokesStopDesktopNativeLoopback === 'function') {
        try {
            window._spokesStopDesktopNativeLoopback();
        } catch {}
        return;
    }
    try {
        window.SpokesBridge?.screenShareAudio?.stopNativeLoopback?.()?.catch?.(() => {});
    } catch {}
}

async function wrapDesktopNativeLoopbackTrack() {
    if (typeof window === 'undefined' || typeof window._spokesCreateDesktopNativeLoopbackTrack !== 'function') {
        return null;
    }
    const mediaStreamTrack = await window._spokesCreateDesktopNativeLoopbackTrack();
    if (!mediaStreamTrack) {
        return null;
    }

    if (window.LivekitClient && typeof window.LivekitClient.LocalAudioTrack === 'function') {
        const lkAudioTrack = new window.LivekitClient.LocalAudioTrack(mediaStreamTrack, {
            echoCancellation: false,
            noiseSuppression: false,
            autoGainControl: false,
            channelCount: 2
        }, true);
        lkAudioTrack.source = window.LivekitClient.Track.Source.ScreenShareAudio;
        lkAudioTrack._isDesktopNativeLoopback = true;
        const origLkStop = typeof lkAudioTrack.stop === 'function' ? lkAudioTrack.stop.bind(lkAudioTrack) : null;
        lkAudioTrack.stop = () => {
            if (origLkStop) {
                try { origLkStop(); } catch {}
            }
            try { mediaStreamTrack.stop?.(); } catch {}
            stopDesktopNativeLoopback();
        };
        return lkAudioTrack;
    }

    return {
        kind: 'audio',
        sid: 'TR_NATIVE_LOOPBACK',
        source: window.LivekitClient?.Track?.Source?.ScreenShareAudio || 'screen_share_audio',
        mediaStreamTrack,
        _isDesktopNativeLoopback: true,
        stop: () => {
            try { mediaStreamTrack.stop?.(); } catch {}
            stopDesktopNativeLoopback();
        }
    };
}

export function stopAllScreenTracks(room = null) {
    stopDesktopNativeLoopback();
    if (voiceState.globalScreenTracks && voiceState.globalScreenTracks.length > 0) {
        for (const t of voiceState.globalScreenTracks) {
            _callbacks.stopLivekitTrackCompletely(t);
        }
        voiceState.globalScreenTracks = [];
    }
    const targetRoom = room || voiceState.currentRoom;
    if (targetRoom && targetRoom.localParticipant) {
        try {
            const pubs = getParticipantTrackPublications(targetRoom.localParticipant);
            for (const p of pubs) {
                if (p.source === window.LivekitClient?.Track?.Source?.ScreenShare || p.source === window.LivekitClient?.Track?.Source?.ScreenShareAudio) {
                    const t = p.videoTrack || p.audioTrack || p.track;
                    if (t) _callbacks.stopLivekitTrackCompletely(t);
                }
            }
        } catch {}
    }
}

async function acquireLocalScreenTracksWithFallback(probePreset, captureAudio) {
    const screenAudioBridge = typeof window !== 'undefined' ? window.SpokesBridge?.screenShareAudio : null;
    if (screenAudioBridge && typeof screenAudioBridge.setPreferredShareAudio === 'function') {
        try {
            await screenAudioBridge.setPreferredShareAudio(Boolean(captureAudio));
        } catch {}
    }

    try {
        return await window.LivekitClient.createLocalScreenTracks({
            resolution: probePreset,
            audio: true
        });
    } catch (err) {
        if (isDisplayMediaUserCancelledError(err)) {
            throw err;
        }
        console.warn(
            `[VoiceInterop] createLocalScreenTracks with audio: true failed (${formatVoiceError(err)}); retrying video-only screen capture...`
        );
        return await window.LivekitClient.createLocalScreenTracks({
            resolution: probePreset,
            audio: false
        });
    }
}

export function isRoomActivelyReconnecting(room = voiceState.currentRoom) {
    if (!room || typeof room.state !== 'string') return false;
    const ConnectionState = window.LivekitClient?.ConnectionState;
    const reconnectingState = ConnectionState?.Reconnecting || 'reconnecting';
    const signalReconnectingState = ConnectionState?.SignalReconnecting || 'signalReconnecting';
    const connectingState = ConnectionState?.Connecting || 'connecting';
    return room.state === reconnectingState ||
        room.state === 'reconnecting' ||
        room.state === signalReconnectingState ||
        room.state === 'signalReconnecting' ||
        room.state === connectingState ||
        room.state === 'connecting';
}

async function waitForRoomConnectedForPublish(room = voiceState.currentRoom, timeoutMs = 8000) {
    if (!room) return false;
    if (!isRoomActivelyReconnecting(room)) {
        return _callbacks.isRoomConnected();
    }
    const deadline = Date.now() + timeoutMs;
    while (Date.now() < deadline) {
        if (!voiceState.currentRoom || voiceState.currentRoom !== room) return false;
        if (!isRoomActivelyReconnecting(room)) {
            return _callbacks.isRoomConnected();
        }
        await new Promise(resolve => setTimeout(resolve, 40));
    }
    return _callbacks.isRoomConnected() && !isRoomActivelyReconnecting(room);
}

function isTransientPublishReconnectError(error) {
    if (!error) return false;
    const msg = String(error.message || error || '').toLowerCase();
    return msg.includes('cancelled publication by calling unpublish') ||
        msg.includes('canceled publication by calling unpublish') ||
        msg.includes('engine not connected') ||
        msg.includes('cannot publish track when not connected') ||
        msg.includes('signal disconnected');
}

async function publishLocalTrackResilient(track, publishOptions) {
    const targetRoom = voiceState.currentRoom;
    if (!targetRoom || !targetRoom.localParticipant) {
        throw new Error('Room disconnected before track publication.');
    }

    if (isRoomActivelyReconnecting(targetRoom)) {
        await waitForRoomConnectedForPublish(targetRoom, 8000);
    }

    try {
        return await targetRoom.localParticipant.publishTrack(track, publishOptions);
    } catch (err) {
        if (voiceState.currentRoom === targetRoom && _callbacks.isRoomConnected() && (isTransientPublishReconnectError(err) || isRoomActivelyReconnecting(targetRoom))) {
            console.warn(`[VoiceInterop] publishTrack interrupted by room reconnect (${formatVoiceError(err)}); waiting for room reconnect to retry...`);
            const ready = await waitForRoomConnectedForPublish(targetRoom, 8000);
            if (ready && voiceState.currentRoom === targetRoom && targetRoom.localParticipant) {
                return await targetRoom.localParticipant.publishTrack(track, publishOptions);
            }
        }
        throw err;
    }
}

export async function setScreenShareEnabled(enabled, resolutionStr = 'Auto', targetFps = 30, mode = 'motion', captureAudio = false) {
    const executeScreenShareOp = async () => {
        const currentRoom = voiceState.currentRoom;
        if (!currentRoom || !currentRoom.localParticipant) return;
        let acquiredTracksForCleanup = [];
        try {
            if (enabled) {
                stopDesktopNativeLoopback();

                const screenPub = toArray(currentRoom.localParticipant.videoTrackPublications)
                    .find(p => p.source === window.LivekitClient.Track.Source.ScreenShare);
                if (screenPub?.track) {
                    const oldTrack = screenPub.videoTrack || screenPub.track;
                    try { await currentRoom.localParticipant.unpublishTrack(oldTrack, true); } catch {}
                    _callbacks.stopLivekitTrackCompletely(oldTrack);
                }

                const screenAudioPub = toArray(currentRoom.localParticipant.audioTrackPublications)
                    .find(p => p.source === window.LivekitClient.Track.Source.ScreenShareAudio);
                if (screenAudioPub?.track) {
                    const oldAudioTrack = screenAudioPub.audioTrack || screenAudioPub.track;
                    try { await currentRoom.localParticipant.unpublishTrack(oldAudioTrack, true); } catch {}
                    _callbacks.stopLivekitTrackCompletely(oldAudioTrack);
                }

                const is60 = targetFps > 35;
                const targetPreset = getScreenPreset(resolutionStr, is60);
                const probePreset = Object.assign({}, getScreenPreset('2160p', is60), { frameRate: targetFps });

                const trackRes = await acquireLocalScreenTracksWithFallback(probePreset, captureAudio);
                const tracks = Array.isArray(trackRes) ? trackRes : [trackRes];
                acquiredTracksForCleanup = tracks;
                const videoTrack = tracks.find(t => t.kind === 'video') || tracks[0];
                let audioTrack = tracks.find(t => t.kind === 'audio');

                let desktopRequestedAudio = Boolean(captureAudio);
                if (typeof window !== 'undefined' && window._spokesLastPickerAudioSelection) {
                    desktopRequestedAudio = Boolean(window._spokesLastPickerAudioSelection.shareAudio);
                }
                if (!audioTrack && desktopRequestedAudio && typeof window !== 'undefined' && typeof window._spokesCreateDesktopNativeLoopbackTrack === 'function') {
                    const sel = window._spokesLastPickerAudioSelection;
                    if (sel?.useNativeLoopback) {
                        audioTrack = await wrapDesktopNativeLoopbackTrack();
                        if (audioTrack) {
                            tracks.push(audioTrack);
                        }
                    }
                }

                voiceState.globalScreenTracks = tracks;

                if (videoTrack && videoTrack.mediaStreamTrack) {
                    videoTrack.mediaStreamTrack.contentHint = mode === 'detail' ? 'detail' : 'motion';
                    if (typeof videoTrack.mediaStreamTrack.applyConstraints === 'function') {
                        try {
                            await videoTrack.mediaStreamTrack.applyConstraints({
                                frameRate: { ideal: targetFps, max: targetFps }
                            });
                        } catch (e) {
                            console.warn('[VoiceInterop] Failed to apply initial frameRate constraint on screen track:', e);
                        }
                    }
                    if (typeof videoTrack.mediaStreamTrack.addEventListener === 'function') {
                        const onScreenEnded = () => {
                            try { videoTrack.mediaStreamTrack.removeEventListener('ended', onScreenEnded); } catch {}
                            if (voiceState.isStoppingScreenShare) return;
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

                const publishOptions = {
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

                voiceState.lastScreenResolution = resolutionStr || 'Auto';
                voiceState.lastScreenFps = targetFps || 30;
                voiceState.lastScreenMode = mode || 'motion';

                await publishLocalTrackResilient(videoTrack, publishOptions);
                await applyScreenShareEncoding(videoTrack, voiceState.lastScreenResolution, voiceState.lastScreenFps, voiceState.lastScreenMode, { skipConstraints: true });
                try { updatePipCallState({ isScreenSharing: true }); } catch {}
                _callbacks.notifyHostVoiceState();
                scheduleRoomSnapshotSync();

                if (audioTrack) {
                    console.log('[Voice] Publishing desktop audio track:', audioTrack.sid);
                    await publishScreenShareAudioTrack(audioTrack);
                    invokeDotNetSafe('OnScreenShareAudioActive', true);
                } else {
                    invokeDotNetSafe('OnScreenShareAudioActive', false);
                    if (captureAudio || desktopRequestedAudio) {
                        invokeDotNetSafe('OnVoiceLog', 'No desktop audio captured. When sharing in browser, select "Entire Screen" and check "Share system audio" (or share a browser tab).', 'warning');
                    }
                }
            } else {
                voiceState.isStoppingScreenShare = true;
                const localId = currentRoom.localParticipant?.identity;
                try {
                    const tracksToStop = new Set(voiceState.globalScreenTracks);
                    voiceState.globalScreenTracks = [];

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
                            _callbacks.stopLivekitTrackCompletely(t);
                        }
                    }
                } finally {
                    stopDesktopNativeLoopback();
                    voiceState.isStoppingScreenShare = false;
                    if (localId) {
                        try { unregisterPipVideoTrack(localId, 'screenshare'); } catch {}
                        _callbacks.clearParticipantVideoDom(localId, 'screenshare');
                        invokeDotNetSafe('OnVideoTrackStateChanged', localId, 'screenshare', false);
                        invokeDotNetSafe('OnScreenShareAudioActive', false);
                    }
                    try { updatePipCallState({ isScreenSharing: false }); } catch {}
                    _callbacks.notifyHostVoiceState();
                    scheduleRoomSnapshotSync();
                }
            }
        } catch (error) {
            if (enabled) {
                for (const t of acquiredTracksForCleanup) {
                    _callbacks.stopLivekitTrackCompletely(t);
                }
                stopAllScreenTracks();
                _callbacks.notifyHostVoiceState();
            }
            if (isDisplayMediaUserCancelledError(error)) {
                console.info(`[VoiceInterop] Screen share selection cancelled by user (${formatVoiceError(error)})`);
                return false;
            }
            console.error(`[VoiceInterop] Error in setScreenShareEnabled: ${formatVoiceError(error)}`, error);
            throw error;
        }
    };

    const opPromise = voiceState.screenShareOperationChain.then(executeScreenShareOp, executeScreenShareOp);
    voiceState.screenShareOperationChain = opPromise.then(() => {}, () => {});
    return await opPromise;
}

export async function setScreenShareAudioEnabled(enabled) {
    const currentRoom = voiceState.currentRoom;
    if (!currentRoom || !currentRoom.localParticipant) return false;
    let audioTrack = voiceState.globalScreenTracks.find(t => t.kind === 'audio');
    if (!audioTrack && enabled && typeof window !== 'undefined' && typeof window._spokesCreateDesktopNativeLoopbackTrack === 'function') {
        audioTrack = await wrapDesktopNativeLoopbackTrack();
        if (audioTrack) {
            voiceState.globalScreenTracks.push(audioTrack);
        }
    }
    if (audioTrack) {
        const existingPub = toArray(currentRoom.localParticipant.audioTrackPublications)
            .find(p => p.source === window.LivekitClient.Track.Source.ScreenShareAudio);
        if (enabled) {
            if (audioTrack.mediaStreamTrack) audioTrack.mediaStreamTrack.enabled = true;
            if (!existingPub) {
                await publishScreenShareAudioTrack(audioTrack);
            }
            invokeDotNetSafe('OnScreenShareAudioActive', true);
        } else {
            if (audioTrack.mediaStreamTrack) audioTrack.mediaStreamTrack.enabled = false;
            const isNativeLoopback = Boolean(audioTrack._isDesktopNativeLoopback);
            if (existingPub?.track) {
                await currentRoom.localParticipant.unpublishTrack(existingPub.track, isNativeLoopback);
            }
            if (isNativeLoopback) {
                voiceState.globalScreenTracks = voiceState.globalScreenTracks.filter(t => t !== audioTrack);
                _callbacks.stopLivekitTrackCompletely(audioTrack);
                stopDesktopNativeLoopback();
            }
            invokeDotNetSafe('OnScreenShareAudioActive', false);
        }
        scheduleRoomSnapshotSync();
        return true;
    }
    return false;
}

export async function updateScreenShareSettings(resolutionStr = 'Auto', targetFps = 30, mode = 'motion') {
    voiceState.lastScreenResolution = resolutionStr || 'Auto';
    voiceState.lastScreenFps = targetFps || 30;
    voiceState.lastScreenMode = mode || 'motion';
    const currentRoom = voiceState.currentRoom;
    if (!currentRoom || !currentRoom.localParticipant) return;
    try {
        const screenPub = toArray(currentRoom.localParticipant.videoTrackPublications)
            .find(p => p.source === window.LivekitClient.Track.Source.ScreenShare);
        if (screenPub?.track) {
            await applyScreenShareEncoding(screenPub.track, voiceState.lastScreenResolution, voiceState.lastScreenFps, voiceState.lastScreenMode);
        }
    } catch (err) {
        console.warn('[VoiceInterop] Error in updateScreenShareSettings:', err);
    }
}
