export class CustomRNNoiseProcessor {
    constructor() {
        this.name = 'rnnoise-processor';
        this.audioContext = null;
        this.sourceNode = null;
        this.workletNode = null;
        this.destination = null;
        this.processedTrack = null;
        this.originalTrack = null;
    }

    async init(opts) {
        this.originalTrack = opts.track || opts.mediaStreamTrack;
        if (!this.originalTrack) throw new Error("No track provided");

        this.audioContext = new (window.AudioContext || window.webkitAudioContext)({
            sampleRate: 48000
        });

        // Load RNNoise processor
        try {
            await this.audioContext.audioWorklet.addModule('/lib/noise-suppression/rnnoise-processor.js');
        } catch (err) {
            console.error('[CustomRNNoiseProcessor] Failed to load worklet:', err);
            throw err;
        }

        this.workletNode = new AudioWorkletNode(this.audioContext, 'NoiseSuppressorWorklet');

        const stream = new MediaStream([this.originalTrack]);
        this.sourceNode = this.audioContext.createMediaStreamSource(stream);
        this.destination = this.audioContext.createMediaStreamDestination();

        this.sourceNode.connect(this.workletNode);
        this.workletNode.connect(this.destination);

        this.processedTrack = this.destination.stream.getAudioTracks()[0];
    }

    async restart(opts) {
        await this.destroy();
        await this.init(opts);
    }

    async destroy() {
        if (this.sourceNode) {
            this.sourceNode.disconnect();
            this.sourceNode = null;
        }
        if (this.workletNode) {
            this.workletNode.disconnect();
            this.workletNode = null;
        }
        if (this.destination) {
            this.destination.disconnect();
            this.destination = null;
        }
        if (this.audioContext) {
            await this.audioContext.close();
            this.audioContext = null;
        }
        this.processedTrack = null;
        this.originalTrack = null;
    }
}
