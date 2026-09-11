export class NoiseSuppressionManager {
    constructor() {
        this.processors = new Map();
        this.registerProcessors();
    }

    registerProcessors() {
        this.processors.set('rnnoise', async () => {
            const { CustomRNNoiseProcessor } = await import('./customRNNoiseProcessor.js');
            return new CustomRNNoiseProcessor();
        });
        this.processors.set('deepfilternet', async () => {
            const { DeepFilterNoiseFilterProcessor } = await import('/lib/noise-suppression/deepfilternet-processor.js');
            return new DeepFilterNoiseFilterProcessor({
                sampleRate: 48000,
                noiseReductionLevel: 100,
                enabled: true,
                assetConfig: { cdnUrl: '/lib/noise-suppression/deepfilternet3' }
            });
        });
    }

    async getProcessor(type) {
        if (!this.processors.has(type)) {
            return null;
        }
        const factory = this.processors.get(type);
        return await factory();
    }
}
