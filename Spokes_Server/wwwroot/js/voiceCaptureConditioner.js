// voiceCaptureConditioner.js
// Shared voice conditioning & protection primitives across sender capture and Desktop/Web playback:
// - Sender Capture: pre-denoiser calibration (-26 dBFS floor lift) + post-denoiser -16.5 dBFS Discord-matched
//   speech loudness normalization + always-on -1.0 dBFS soft-knee peak limiting (toggleable via autoGainEnabled).
// - Desktop/Web Playback: idempotent -16.5 dBFS receiver loudness normalization for remote callers
//   (toggleable via Incoming Voice Normalization) + always-on -0.5 dBFS master soft-knee DAC ceiling.

const WORKLET_MODULE_URL = '/js/voiceLevelerWorklet.js';
const _loadedAudioContexts = new WeakSet();
const _loadingPromises = new WeakMap();

export function isVoiceLevelerWorkletReady(audioContext) {
    return Boolean(audioContext && _loadedAudioContexts.has(audioContext));
}

export async function ensureVoiceLevelerWorkletLoaded(audioContext) {
    if (!audioContext || !audioContext.audioWorklet || audioContext.state === 'closed') return false;
    if (_loadedAudioContexts.has(audioContext)) return true;

    let pending = _loadingPromises.get(audioContext);
    if (!pending) {
        const resolvedWorkletUrl = (typeof window !== 'undefined' && typeof window.spokesResolveAssetUrl === 'function')
            ? window.spokesResolveAssetUrl(WORKLET_MODULE_URL)
            : WORKLET_MODULE_URL;
        pending = audioContext.audioWorklet.addModule(resolvedWorkletUrl)
            .then(() => {
                _loadedAudioContexts.add(audioContext);
                _loadingPromises.delete(audioContext);
                return true;
            })
            .catch((err) => {
                _loadingPromises.delete(audioContext);
                throw err;
            });
        _loadingPromises.set(audioContext, pending);
    }

    return await pending;
}

export function createMonoMediaStreamDestination(audioContext) {
    const destination = audioContext.createMediaStreamDestination();
    try {
        destination.channelCount = 1;
        destination.channelCountMode = 'explicit';
    } catch (_) {
        // Older WebKit implementations may treat channelCount on MediaStreamDestinationNode as read-only
    }
    return destination;
}

export async function createVoiceConditioningStage(audioContext, initialAutoGainEnabled = true) {
    await ensureVoiceLevelerWorkletLoaded(audioContext);

    const monoNodeOptions = {
        numberOfInputs: 1,
        numberOfOutputs: 1,
        channelCount: 1,
        channelCountMode: 'explicit',
        outputChannelCount: [1],
        processorOptions: {
            enabled: initialAutoGainEnabled !== false
        }
    };

    const preNode = new AudioWorkletNode(audioContext, 'SpokesPreDenoiserWorklet', monoNodeOptions);
    const postNode = new AudioWorkletNode(audioContext, 'SpokesVoiceLevelerWorklet', monoNodeOptions);

    return {
        preNode,
        postNode,
        setAutoGainEnabled(enabled) {
            const flag = Boolean(enabled);
            try {
                preNode.port.postMessage({ type: 'SET_ENABLED', value: flag });
            } catch (_) {}
            try {
                postNode.port.postMessage({ type: 'SET_ENABLED', value: flag });
            } catch (_) {}
        },
        disconnect() {
            try { preNode.disconnect(); } catch (_) {}
            try { postNode.disconnect(); } catch (_) {}
        }
    };
}

export function createReceiverVoiceLevelerNode(audioContext, initialEnabled = true) {
    if (!isVoiceLevelerWorkletReady(audioContext)) return null;
    try {
        return new AudioWorkletNode(audioContext, 'SpokesVoiceLevelerWorklet', {
            numberOfInputs: 1,
            numberOfOutputs: 1,
            processorOptions: {
                enabled: initialEnabled !== false,
                // Gentle inter-speaker trim profile so sender AGC leads cleanly without double-AGC hunting:
                // 120 ms sustained speech confirmation, slower dB tracking, and -46 dBFS gate floor.
                confirmWindows: 6,
                envRiseTauSec: 1.0,
                envFallTauSec: 0.9,
                maxRiseStepDb: 0.40,
                speechGateFloorDb: -46.0,
                maxNoiseFloorDb: -38.0,
                noiseMarginDb: 6.0
            }
        });
    } catch (_) {
        return null;
    }
}

export function createMasterSoftLimiterNode(audioContext) {
    if (!isVoiceLevelerWorkletReady(audioContext)) return null;
    try {
        return new AudioWorkletNode(audioContext, 'SpokesSoftLimiterWorklet', {
            numberOfInputs: 1,
            numberOfOutputs: 1,
            processorOptions: {
                threshold: 0.8413951, // -1.5 dBFS soft-knee start
                ceiling: 0.9440609    // -0.5 dBFS true-peak DAC ceiling
            }
        });
    } catch (_) {
        return null;
    }
}
