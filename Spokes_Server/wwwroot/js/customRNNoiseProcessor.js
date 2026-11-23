import { createMonoMediaStreamDestination, createVoiceConditioningStage, ensureVoiceLevelerWorkletLoaded } from './voiceCaptureConditioner.js';

const _rnnoiseLoadedContexts = new WeakSet();
const _rnnoiseLoadingPromises = new WeakMap();
const RNNOISE_WORKLET_PATH = '/lib/noise-suppression/rnnoise-processor.js';

function markVoiceDiag(label) {
    try {
        if (typeof window !== 'undefined' && window.spokesChatDiagnostics?.isEnabled?.()) {
            window.spokesChatDiagnostics.mark(label);
        }
    } catch (_) {}
}

export async function ensureRnnoiseWorkletLoaded(audioContext) {
    if (!audioContext || !audioContext.audioWorklet || audioContext.state === 'closed') {
        return;
    }
    if (_rnnoiseLoadedContexts.has(audioContext)) {
        return;
    }
    let pending = _rnnoiseLoadingPromises.get(audioContext);
    if (!pending) {
        const workletUrl = (typeof window !== 'undefined' && typeof window.spokesResolveAssetUrl === 'function')
            ? window.spokesResolveAssetUrl(RNNOISE_WORKLET_PATH)
            : RNNOISE_WORKLET_PATH;
        markVoiceDiag('Voice: rnnoiseWorklet.addModule Start');
        pending = audioContext.audioWorklet.addModule(workletUrl)
            .then(() => {
                _rnnoiseLoadedContexts.add(audioContext);
                _rnnoiseLoadingPromises.delete(audioContext);
                markVoiceDiag('Voice: rnnoiseWorklet.addModule Done');
            })
            .catch((err) => {
                _rnnoiseLoadingPromises.delete(audioContext);
                throw err;
            });
        _rnnoiseLoadingPromises.set(audioContext, pending);
    }
    await pending;
}

export class CustomRNNoiseProcessor {
    constructor(options = {}) {
        this.name = 'rnnoise-processor';
        this.audioContext = null;
        this.sourceNode = null;
        this.workletNode = null;
        this.conditioningStage = null;
        this.destination = null;
        this.processedTrack = null;
        this.originalTrack = null;
        this.autoGainEnabled = options.autoGainEnabled !== false;
    }

    async preload(audioContext) {
        if (!audioContext || !audioContext.audioWorklet || audioContext.state === 'closed') {
            return;
        }
        await Promise.all([
            ensureRnnoiseWorkletLoaded(audioContext),
            ensureVoiceLevelerWorkletLoaded(audioContext).catch(() => false)
        ]);
    }

    setAutoGainEnabled(enabled) {
        this.autoGainEnabled = Boolean(enabled);
        if (this.conditioningStage) {
            this.conditioningStage.setAutoGainEnabled(this.autoGainEnabled);
        }
    }

    async init(opts) {
        this.originalTrack = opts.track || opts.mediaStreamTrack;
        if (!this.originalTrack) throw new Error("No track provided");

        const AudioCtx = window.AudioContext || window.webkitAudioContext;
        const candidateCtx = opts.audioContext || opts.localTrack?.audioContext;
        if (candidateCtx && candidateCtx.sampleRate === 48000 && candidateCtx.state !== 'closed') {
            this.audioContext = candidateCtx;
            this._ownsAudioContext = false;
        } else {
            this.audioContext = new AudioCtx({ sampleRate: 48000, latencyHint: 'interactive' });
            this._ownsAudioContext = true;
        }

        if (this.audioContext.state === 'suspended') {
            try {
                await this.audioContext.resume();
            } catch (_) {}
        }

        // Load RNNoise processor once per AudioContext
        try {
            await ensureRnnoiseWorkletLoaded(this.audioContext);
        } catch (err) {
            console.error('[CustomRNNoiseProcessor] Failed to load worklet:', err);
            throw err;
        }

        this.workletNode = new AudioWorkletNode(this.audioContext, 'NoiseSuppressorWorklet', {
            numberOfInputs: 1,
            numberOfOutputs: 1,
            channelCount: 1,
            channelCountMode: 'explicit',
            outputChannelCount: [1]
        });
        markVoiceDiag('Voice: rnnoiseWorklet Node Created');

        this.destination = createMonoMediaStreamDestination(this.audioContext);

        // Pre-load conditioning stage BEFORE creating MediaStreamAudioSourceNode so Chromium's
        // PushPullFIFO never accumulates 100-140ms of buffered mic audio during async worklet compilation.
        try {
            this.conditioningStage = await createVoiceConditioningStage(this.audioContext, this.autoGainEnabled);
            markVoiceDiag('Voice: Conditioning Stage Ready');
        } catch (condErr) {
            console.warn('[CustomRNNoiseProcessor] Conditioning stage unavailable, falling back to direct RNNoise chain:', condErr);
            this.conditioningStage = null;
        }

        const stream = new MediaStream([this.originalTrack]);
        this.sourceNode = this.audioContext.createMediaStreamSource(stream);
        if (this.conditioningStage) {
            this.sourceNode.connect(this.conditioningStage.preNode);
            this.conditioningStage.preNode.connect(this.workletNode);
            this.workletNode.connect(this.conditioningStage.postNode);
            this.conditioningStage.postNode.connect(this.destination);
        } else {
            this.sourceNode.connect(this.workletNode);
            this.workletNode.connect(this.destination);
        }

        this.processedTrack = this.destination.stream.getAudioTracks()[0];
    }

    async restart(opts = {}) {
        const nextTrack = opts.track || opts.mediaStreamTrack;
        if (nextTrack) {
            this.originalTrack = nextTrack;
        }
        if (!this.originalTrack) {
            throw new Error("No track provided");
        }

        if (this.audioContext &&
            this.audioContext.state !== 'closed' &&
            this.workletNode &&
            this.destination &&
            this.processedTrack &&
            this.processedTrack.readyState !== 'ended') {
            if (this.audioContext.state === 'suspended') {
                try {
                    await this.audioContext.resume();
                } catch (_) {}
            }
            if (this.flushCaptureFifo()) {
                return;
            }
        }

        await this.init({
            ...opts,
            track: this.originalTrack,
            audioContext: opts.audioContext || opts.localTrack?.audioContext || this.audioContext
        });
    }

    flushCaptureFifo() {
        if (!this.audioContext || !this.originalTrack || !this.workletNode || !this.destination) {
            return false;
        }
        if (this.originalTrack.readyState !== 'live') {
            return false;
        }
        try {
            if (this.sourceNode) {
                try { this.sourceNode.disconnect(); } catch (_) {}
                this.sourceNode = null;
            }
            if (this.workletNode.port) {
                this.workletNode.port.postMessage({ type: 'FLUSH_BUFFER' });
            }
            const stream = new MediaStream([this.originalTrack]);
            this.sourceNode = this.audioContext.createMediaStreamSource(stream);
            if (this.conditioningStage && this.conditioningStage.preNode) {
                this.sourceNode.connect(this.conditioningStage.preNode);
            } else {
                this.sourceNode.connect(this.workletNode);
            }
            return true;
        } catch (_) {
            return false;
        }
    }

    async destroy() {
        if (this.sourceNode) {
            try { this.sourceNode.disconnect(); } catch (_) {}
            this.sourceNode = null;
        }
        if (this.conditioningStage) {
            this.conditioningStage.disconnect();
            this.conditioningStage = null;
        }
        if (this.workletNode) {
            try { this.workletNode.disconnect(); } catch (_) {}
            this.workletNode = null;
        }
        if (this.destination) {
            try { this.destination.disconnect(); } catch (_) {}
            this.destination = null;
        }
        if (this.processedTrack) {
            try { this.processedTrack.stop?.(); } catch (_) {}
            this.processedTrack = null;
        }
        if (this.audioContext) {
            if (this._ownsAudioContext !== false) {
                try { await this.audioContext.close(); } catch (_) {}
            }
            this.audioContext = null;
        }
        this.originalTrack = null;
    }
}
