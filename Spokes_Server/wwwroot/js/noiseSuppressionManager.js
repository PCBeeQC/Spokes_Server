export class NoiseSuppressionManager {
    constructor() {
        this.processors = new Map();
        this.registerProcessors();
    }

    registerProcessors() {
        this.processors.set('rnnoise', async (options = {}) => {
            const { CustomRNNoiseProcessor } = await import('./customRNNoiseProcessor.js');
            return new CustomRNNoiseProcessor(options);
        });
        this.processors.set('deepfilternet', async (options = {}) => {
            const { DeepFilterNoiseFilterProcessor } = await import('/lib/noise-suppression/deepfilternet-processor.js');
            return new DeepFilterNoiseFilterProcessor({
                sampleRate: 48000,
                noiseReductionLevel: 40,
                enabled: true,
                stationaryRoomMode: Boolean(options.stationaryRoomMode),
                assetConfig: { cdnUrl: '/lib/noise-suppression/deepfilternet3' }
            });
        });
    }

    async getProcessor(type, options = {}) {
        const factory = this.processors.get(type);
        return factory ? await factory(options) : null;
    }
}
