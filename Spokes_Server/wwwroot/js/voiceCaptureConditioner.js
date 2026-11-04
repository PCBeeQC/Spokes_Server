// voiceCaptureConditioner.js
// Shared mono 48 kHz capture conditioning pipeline wrapping neural noise processors
// (CustomRNNoiseProcessor & DeepFilterNoiseFilterProcessor) with pre-denoiser calibration
// and post-denoiser -20 dBFS Speech-Gated Loudness Normalization + -1 dBFS Soft Limiter.

const _loadedAudioContexts = new WeakSet();

export async function ensureVoiceLevelerWorkletLoaded(audioContext) {
    if (!audioContext || !audioContext.audioWorklet) return false;
    if (_loadedAudioContexts.has(audioContext)) return true;

    await audioContext.audioWorklet.addModule('/js/voiceLevelerWorklet.js');
    _loadedAudioContexts.add(audioContext);
    return true;
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

export async function createVoiceConditioningStage(audioContext, initialStationaryRoomMode = false) {
    await ensureVoiceLevelerWorkletLoaded(audioContext);

    const monoNodeOptions = {
        numberOfInputs: 1,
        numberOfOutputs: 1,
        channelCount: 1,
        channelCountMode: 'explicit',
        outputChannelCount: [1],
        processorOptions: {
            stationaryRoomMode: Boolean(initialStationaryRoomMode)
        }
    };

    const preNode = new AudioWorkletNode(audioContext, 'SpokesPreDenoiserWorklet', monoNodeOptions);
    const postNode = new AudioWorkletNode(audioContext, 'SpokesVoiceLevelerWorklet', monoNodeOptions);

    return {
        preNode,
        postNode,
        setStationaryRoomMode(enabled) {
            const flag = Boolean(enabled);
            try {
                preNode.port.postMessage({ type: 'SET_STATIONARY_ROOM_MODE', value: flag });
            } catch (_) {}
            try {
                postNode.port.postMessage({ type: 'SET_STATIONARY_ROOM_MODE', value: flag });
            } catch (_) {}
        },
        disconnect() {
            try { preNode.disconnect(); } catch (_) {}
            try { postNode.disconnect(); } catch (_) {}
        }
    };
}
