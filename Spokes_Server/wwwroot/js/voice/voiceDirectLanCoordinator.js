// voiceDirectLanCoordinator.js — Desktop C++ Direct LAN UDP peer-to-peer room audio coordinator.

import {
    voiceState,
    invokeDotNetSafe,
    isEffectiveLocalMicMuted
} from './voiceState.js';

let _callbacks = {
    reconcileAudioRouting: () => {},
    flushLocalCaptureFifoIfAvailable: () => {},
    resolveSelectedMicLabel: async () => '',
    resolveSelectedSpeakerLabel: async () => ''
};

export function configureDirectLanCallbacks(callbacks = {}) {
    _callbacks = { ..._callbacks, ...callbacks };
}

export function getDirectRoomAudioBridge() {
    if (typeof window === 'undefined') return null;
    const bridge = window.SpokesBridge?.directRoomAudio;
    return bridge && typeof bridge === 'object' ? bridge : null;
}

export function buildDirectPeerVolumesPayload() {
    const peerVolumes = {};
    const saved = voiceState.savedVoiceSettings?.participantVolumes || {};
    voiceState.directUserIdToPeerToken.forEach((token, userId) => {
        const pct = typeof saved[userId] === 'number' ? saved[userId] : 100;
        peerVolumes[String(token)] = Math.max(0, Math.min(200, pct)) / 100.0;
    });
    return peerVolumes;
}

export function pushDirectRoomControlsUpdate(partial = {}) {
    if (!voiceState.directSessionActive) return;
    const bridge = getDirectRoomAudioBridge();
    if (!bridge || typeof bridge.updateControls !== 'function') return;
    try {
        const payload = Object.assign({
            isMuted: isEffectiveLocalMicMuted(),
            masterVolume: voiceState.masterVolume,
            peerVolumes: buildDirectPeerVolumesPayload(),
            incomingVoiceNormalization: Boolean(voiceState.incomingVoiceNormalization)
        }, partial);
        bridge.updateControls(payload)?.catch?.(() => {});
    } catch {}
}

export function stopDirectRoomAudioInternal() {
    const hadPeers = voiceState.activeDirectLanPeerUsers.size > 0 || voiceState.directUserIdToPeerToken.size > 0;
    const wasActive = voiceState.directSessionActive || hadPeers;
    voiceState.directSessionActive = false;
    voiceState.directSessionRoomId = '';
    voiceState.lastDirectSessionFingerprint = '';
    voiceState.activeDirectLanPeerUsers.clear();
    voiceState.directPeerTokenToUserId.clear();
    voiceState.directUserIdToPeerToken.clear();
    voiceState.lastDirectActivePeerKey = '';
    voiceState.lastDirectBridgeStatus = null;
    if (voiceState.directBridgeStatusUnsub) {
        try { voiceState.directBridgeStatusUnsub(); } catch {}
        voiceState.directBridgeStatusUnsub = null;
    }
    const bridge = getDirectRoomAudioBridge();
    if (bridge && typeof bridge.stop === 'function') {
        try { bridge.stop()?.catch?.(() => {}); } catch {}
    }
    if (wasActive && voiceState.dotnetHelper) {
        invokeDotNetSafe('OnDirectRoomAudioStatusChanged', null);
    }
    if (hadPeers && voiceState.currentRoom) {
        _callbacks.reconcileAudioRouting();
    }
}

function syncActiveDirectPeersFromStatus(status) {
    const nextActiveUsers = new Set();
    const rawTokens = Array.isArray(status?.activePeerTokens) ? status.activePeerTokens : [];
    for (const rawTok of rawTokens) {
        const uid = voiceState.directPeerTokenToUserId.get(Number(rawTok));
        if (uid) {
            nextActiveUsers.add(uid);
        }
    }

    let peersChanged = nextActiveUsers.size !== voiceState.activeDirectLanPeerUsers.size;
    if (!peersChanged) {
        for (const uid of nextActiveUsers) {
            if (!voiceState.activeDirectLanPeerUsers.has(uid)) {
                peersChanged = true;
                break;
            }
        }
    }

    if (peersChanged) {
        const prevUsers = new Set(voiceState.activeDirectLanPeerUsers);
        voiceState.activeDirectLanPeerUsers = nextActiveUsers;
        if (prevUsers.size > 0 && nextActiveUsers.size === 0) {
            _callbacks.flushLocalCaptureFifoIfAvailable();
        }
        _callbacks.reconcileAudioRouting();
    }

    return Array.from(voiceState.activeDirectLanPeerUsers).sort();
}

function mapDirectBridgeStatusToTelemetry(status, activeIdsSorted) {
    const peerDiagnostics = {};
    if (status.peerDiagnostics && typeof status.peerDiagnostics === 'object') {
        for (const [tokKey, pd] of Object.entries(status.peerDiagnostics)) {
            if (!pd || typeof pd !== 'object') continue;
            const mappedUid = voiceState.directPeerTokenToUserId.get(Number(tokKey)) || String(tokKey);
            peerDiagnostics[mappedUid] = {
                userId: mappedUid,
                rttMs: typeof pd.rttMs === 'number' ? pd.rttMs : 0,
                lagMs: typeof pd.lagMs === 'number' ? pd.lagMs : 0,
                lookaheadMs: typeof pd.lookaheadMs === 'number' ? pd.lookaheadMs : 0,
                bleedDb: typeof pd.bleedDb === 'number' ? pd.bleedDb : 0,
                confidence: typeof pd.confidence === 'number' ? pd.confidence : 0,
                levelerGainDb: typeof pd.levelerGainDb === 'number' ? pd.levelerGainDb : 0,
                peakInputDbfs: typeof pd.peakInputDbfs === 'number' ? pd.peakInputDbfs : -100,
                clippedSamples: typeof pd.clippedSamples === 'number' ? Math.trunc(pd.clippedSamples) : 0,
                bleedGatedPct: typeof pd.bleedGatedPct === 'number' ? pd.bleedGatedPct : 0,
                jitterUnderruns: typeof pd.jitterUnderruns === 'number' ? Math.trunc(pd.jitterUnderruns) : 0,
                jitterOverflows: typeof pd.jitterOverflows === 'number' ? Math.trunc(pd.jitterOverflows) : 0,
                jitterTrims: typeof pd.jitterTrims === 'number' ? Math.trunc(pd.jitterTrims) : 0,
                maxBurstHops: typeof pd.maxBurstHops === 'number' ? Math.trunc(pd.maxBurstHops) : 0
            };
        }
    }

    return {
        roomId: voiceState.directSessionRoomId || '',
        estimatedEndToEndMs: typeof status.estimatedEndToEndMs === 'number' ? status.estimatedEndToEndMs : 0,
        maxPeerRttMs: typeof status.maxPeerRttMs === 'number' ? status.maxPeerRttMs : 0,
        jitterBufferMs: typeof status.jitterBufferMs === 'number' ? status.jitterBufferMs : 0,
        activeDirectPeerUserIds: activeIdsSorted,
        achievedBleedSuppressionDb: typeof status.achievedSuppressionDb === 'number' ? status.achievedSuppressionDb : 0,
        txPacketCount: typeof status.txPacketCount === 'number' ? Math.trunc(status.txPacketCount) : 0,
        rxAudioPacketCount: typeof status.rxAudioPacketCount === 'number' ? Math.trunc(status.rxAudioPacketCount) : 0,
        authDropCount: typeof status.authDropCount === 'number' ? Math.trunc(status.authDropCount) : 0,
        localPeakDbfs: typeof status.localPeakDbfs === 'number' ? status.localPeakDbfs : -100,
        localClippedSamples: typeof status.localClippedSamples === 'number' ? Math.trunc(status.localClippedSamples) : 0,
        capturePeriodMs: typeof status.capturePeriodMs === 'number' ? status.capturePeriodMs : 0,
        playbackPeriodMs: typeof status.playbackPeriodMs === 'number' ? status.playbackPeriodMs : 0,
        captureNativeRateHz: typeof status.captureNativeRateHz === 'number' ? Math.trunc(status.captureNativeRateHz) : 0,
        captureNativeChannels: typeof status.captureNativeChannels === 'number' ? Math.trunc(status.captureNativeChannels) : 0,
        captureUsingAudioClient3: Boolean(status.captureUsingAudioClient3),
        captureRawMode: Boolean(status.captureRawMode),
        captureDiscontinuities: typeof status.captureDiscontinuities === 'number' ? Math.trunc(status.captureDiscontinuities) : 0,
        playbackNativeRateHz: typeof status.playbackNativeRateHz === 'number' ? Math.trunc(status.playbackNativeRateHz) : 0,
        playbackNativeChannels: typeof status.playbackNativeChannels === 'number' ? Math.trunc(status.playbackNativeChannels) : 0,
        playbackUsingAudioClient3: Boolean(status.playbackUsingAudioClient3),
        refUnderrunCount: typeof status.refUnderrunCount === 'number' ? Math.trunc(status.refUnderrunCount) : 0,
        refOverrunCount: typeof status.refOverrunCount === 'number' ? Math.trunc(status.refOverrunCount) : 0,
        peerDiagnostics
    };
}

function handleDirectBridgeStatus(status) {
    if (!status || !voiceState.currentRoom) return;

    voiceState.lastDirectBridgeStatus = status;
    const activeIdsSorted = syncActiveDirectPeersFromStatus(status);
    const activeKey = activeIdsSorted.join(',');

    // Push over SignalR strictly on active direct peer membership transitions (0 steady-state traffic).
    // Live diagnostics & report exports pull fresh snapshots on demand via getDirectRoomAudioTelemetry().
    if (voiceState.dotnetHelper && activeKey !== voiceState.lastDirectActivePeerKey) {
        voiceState.lastDirectActivePeerKey = activeKey;
        invokeDotNetSafe('OnDirectRoomAudioStatusChanged', mapDirectBridgeStatusToTelemetry(status, activeIdsSorted));
    }
}

export async function getDirectRoomAudioTelemetry() {
    if (!voiceState.directSessionActive || !voiceState.currentRoom) {
        return null;
    }

    const bridge = getDirectRoomAudioBridge();
    let status = null;
    if (bridge && typeof bridge.getStatus === 'function') {
        try {
            status = await bridge.getStatus();
        } catch (e) {
            console.warn('[Voice:DirectRoom] getStatus failed:', e);
        }
    }
    if (!status) {
        status = voiceState.lastDirectBridgeStatus;
    }
    if (!status) {
        return null;
    }

    voiceState.lastDirectBridgeStatus = status;
    const activeIdsSorted = syncActiveDirectPeersFromStatus(status);
    voiceState.lastDirectActivePeerKey = activeIdsSorted.join(',');
    return mapDirectBridgeStatusToTelemetry(status, activeIdsSorted);
}

export function ensureDirectBridgeStatusSubscription() {
    if (voiceState.directBridgeStatusUnsub) return;
    const bridge = getDirectRoomAudioBridge();
    if (bridge && typeof bridge.onStatusChanged === 'function') {
        try {
            voiceState.directBridgeStatusUnsub = bridge.onStatusChanged(handleDirectBridgeStatus);
        } catch {}
    }
}

export async function isDesktopDirectRoomSupported() {
    const bridge = getDirectRoomAudioBridge();
    if (!bridge) return false;
    if (typeof bridge.isSupported === 'function') {
        try {
            return Boolean(await bridge.isSupported());
        } catch {
            return false;
        }
    }
    return true;
}

export async function prepareDirectRoomEndpoints() {
    const bridge = getDirectRoomAudioBridge();
    if (!bridge || typeof bridge.prepareEndpoints !== 'function') return [];
    ensureDirectBridgeStatusSubscription();
    try {
        const res = await bridge.prepareEndpoints();
        return Array.isArray(res?.endpoints) ? res.endpoints : [];
    } catch (e) {
        console.warn('[Voice:DirectRoom] prepareEndpoints failed:', e);
        return [];
    }
}

function buildDirectSessionFingerprint(descriptor, inputDeviceLabel, outputDeviceLabel) {
    if (!descriptor) return '';
    const peers = Array.isArray(descriptor.peers) ? descriptor.peers : [];
    const peerKey = peers
        .map(p => `${p.userId}:${p.peerToken}:${Array.isArray(p.endpoints) ? p.endpoints.join('|') : ''}`)
        .sort()
        .join(',');
    return `${descriptor.channelId}|${descriptor.roomId}|${descriptor.audioMode}|${descriptor.keyEpoch}|${descriptor.localPeerToken}|${descriptor.sessionKeyBase64}|${inputDeviceLabel}|${outputDeviceLabel}|${peerKey}`;
}

export async function syncDirectRoomSession(descriptor, isLocalMuted = false) {
    const bridge = getDirectRoomAudioBridge();
    if (!descriptor || !bridge || typeof bridge.configureSession !== 'function') {
        voiceState.lastDirectSessionFingerprint = '';
        stopDirectRoomAudioInternal();
        return;
    }

    ensureDirectBridgeStatusSubscription();
    const [inputDeviceLabel, outputDeviceLabel] = await Promise.all([
        _callbacks.resolveSelectedMicLabel(),
        _callbacks.resolveSelectedSpeakerLabel()
    ]);

    const nextFingerprint = buildDirectSessionFingerprint(descriptor, inputDeviceLabel, outputDeviceLabel);
    const effectiveMuted = Boolean(isLocalMuted || isEffectiveLocalMicMuted());
    if (voiceState.directSessionActive && nextFingerprint === voiceState.lastDirectSessionFingerprint) {
        pushDirectRoomControlsUpdate({ isMuted: effectiveMuted });
        return;
    }

    voiceState.directPeerTokenToUserId.clear();
    voiceState.directUserIdToPeerToken.clear();
    const peers = Array.isArray(descriptor.peers) ? descriptor.peers : [];
    for (const p of peers) {
        if (p && typeof p.userId === 'string' && typeof p.peerToken === 'number') {
            voiceState.directPeerTokenToUserId.set(Number(p.peerToken), p.userId);
            voiceState.directUserIdToPeerToken.set(p.userId, Number(p.peerToken));
        }
    }

    voiceState.directSessionActive = true;
    voiceState.directSessionRoomId = descriptor.roomId || '';
    voiceState.lastDirectSessionFingerprint = nextFingerprint;
    if (voiceState.currentRoom) {
        _callbacks.reconcileAudioRouting();
    }
    try {
        const status = await bridge.configureSession({
            channelId: descriptor.channelId,
            roomId: descriptor.roomId,
            audioMode: descriptor.audioMode,
            keyEpoch: descriptor.keyEpoch,
            sessionKeyBase64: descriptor.sessionKeyBase64,
            localPeerToken: descriptor.localPeerToken,
            peers,
            inputDeviceLabel,
            outputDeviceLabel,
            isMuted: effectiveMuted,
            masterVolume: voiceState.masterVolume,
            peerVolumes: buildDirectPeerVolumesPayload(),
            incomingVoiceNormalization: Boolean(voiceState.incomingVoiceNormalization)
        });
        if (status) {
            handleDirectBridgeStatus(status);
        }
    } catch (e) {
        console.warn('[Voice:DirectRoom] configureSession failed, using server fallback:', e);
        voiceState.lastDirectSessionFingerprint = '';
        stopDirectRoomAudioInternal();
    }
}
