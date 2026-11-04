import { createMonoMediaStreamDestination, createVoiceConditioningStage } from './voiceCaptureConditioner.js';

const _rnnoiseLoadedContexts = new WeakSet();

async function ensureRnnoiseWorkletLoaded(audioContext) {
    if (_rnnoiseLoadedContexts.has(audioContext)) {
        return;
    }
    await audioContext.audioWorklet.addModule('/lib/noise-suppression/rnnoise-processor.js');
    _rnnoiseLoadedContexts.add(audioContext);
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
        this.stationaryRoomMode = Boolean(options.stationaryRoomMode);
    }

    setStationaryRoomMode(enabled) {
        this.stationaryRoomMode = Boolean(enabled);
        if (this.conditioningStage) {
            this.conditioningStage.setStationaryRoomMode(this.stationaryRoomMode);
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
            this.audioContext = new AudioCtx({ sampleRate: 48000 });
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

        const stream = new MediaStream([this.originalTrack]);
        this.sourceNode = this.audioContext.createMediaStreamSource(stream);
        this.destination = createMonoMediaStreamDestination(this.audioContext);

        try {
            this.conditioningStage = await createVoiceConditioningStage(this.audioContext, this.stationaryRoomMode);
            this.sourceNode.connect(this.conditioningStage.preNode);
            this.conditioningStage.preNode.connect(this.workletNode);
            this.workletNode.connect(this.conditioningStage.postNode);
            this.conditioningStage.postNode.connect(this.destination);
        } catch (condErr) {
            console.warn('[CustomRNNoiseProcessor] Conditioning stage unavailable, falling back to direct RNNoise chain:', condErr);
            this.conditioningStage = null;
            this.sourceNode.connect(this.workletNode);
            this.workletNode.connect(this.destination);
        }

        this.processedTrack = this.destination.stream.getAudioTracks()[0];
    }

    async restart(opts) {
        const sharedCtx = (this._ownsAudioContext === false && this.audioContext && this.audioContext.state !== 'closed')
            ? this.audioContext
            : null;
        await this.destroy();
        await this.init({
            ...opts,
            audioContext: opts?.audioContext || opts?.localTrack?.audioContext || sharedCtx
        });
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
