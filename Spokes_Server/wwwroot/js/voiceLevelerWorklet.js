// voiceLevelerWorklet.js
// Reusable 48 kHz mono AudioWorklet stages for Spokes voice capture:
// 1. SpokesPreDenoiserWorklet: Ultra-slow stationary pre-calibration lifting quiet (< -26 dBFS) mics
//    into RNNoise / DeepFilterNet3's training sweet spot without pumping background noise.
// 2. SpokesVoiceLevelerWorklet: Post-denoiser Speech-Gated Loudness Normalizer (-20 dBFS RMS target,
//    +15 dB max upward boost, frozen gain in silence) + C1-continuous -1.0 dBFS Soft-Knee Peak Limiter.
// Both stages automatically lock to 1.0x unity gain when `stationaryRoomMode` is enabled (active
// multi-device Physical Room), preserving a linear acoustic transfer function for server NLMS cancellation.

const SOFT_KNEE_THRESHOLD = 0.78;     // ~ -2.15 dBFS soft knee start
const SOFT_KNEE_CEILING = 0.8912509;  // -1.0 dBFS true peak ceiling
const SOFT_KNEE_SPAN = SOFT_KNEE_CEILING - SOFT_KNEE_THRESHOLD;

function applySoftKneeLimiter(sample) {
    if (!Number.isFinite(sample)) return 0.0;
    const abs = Math.abs(sample);
    if (abs <= SOFT_KNEE_THRESHOLD) {
        return sample;
    }
    const compressed = SOFT_KNEE_THRESHOLD + SOFT_KNEE_SPAN * Math.tanh((abs - SOFT_KNEE_THRESHOLD) / SOFT_KNEE_SPAN);
    return sample >= 0.0 ? compressed : -compressed;
}

function computeFrameRms(channel) {
    const len = channel.length;
    if (len === 0) return 0.0;
    let sumSq = 0.0;
    for (let i = 0; i < len; i++) {
        const s = channel[i];
        sumSq += s * s;
    }
    return Math.sqrt(sumSq / len);
}

class SpokesPreDenoiserWorklet extends AudioWorkletProcessor {
    constructor(options) {
        super();
        const opts = (options && options.processorOptions) || {};
        this._stationaryRoomMode = Boolean(opts.stationaryRoomMode);

        // Target -26 dBFS (0.0501) neural input sweet spot, max +9 dB (2.8184x) pre-lift
        this._targetRms = 0.0501187;
        this._maxPreGain = 2.8183829;
        this._speechFloorRms = 0.0045; // ~ -47 dBFS

        this._noiseFloorRms = 0.0018;  // ~ -55 dBFS initial floor estimate
        this._speechRmsEnv = this._targetRms;
        this._hasSeededSpeechEnv = false;
        this._targetGain = 1.0;
        this._currentGain = 1.0;

        this.port.onmessage = (event) => {
            const msg = event && event.data;
            if (msg && msg.type === 'SET_STATIONARY_ROOM_MODE') {
                this._stationaryRoomMode = Boolean(msg.value);
                if (this._stationaryRoomMode) {
                    this._targetGain = 1.0;
                }
            }
        };
    }

    process(inputs, outputs) {
        const input = inputs[0];
        const output = outputs[0];
        if (!output || output.length === 0) return true;

        const outCh = output[0];
        if (!input || input.length === 0 || !input[0]) {
            outCh.fill(0);
            return true;
        }

        const inCh = input[0];
        const len = inCh.length;

        if (this._stationaryRoomMode) {
            this._targetGain = 1.0;
        } else {
            const frameRms = computeFrameRms(inCh);

            // Minimum-statistics noise floor tracker: follows quiet valleys quickly, rises very slowly
            if (frameRms > 1e-6) {
                if (frameRms < this._noiseFloorRms) {
                    this._noiseFloorRms = 0.90 * this._noiseFloorRms + 0.10 * frameRms;
                } else if (frameRms < this._speechRmsEnv * 0.45) {
                    this._noiseFloorRms = Math.min(0.025, 0.9992 * this._noiseFloorRms + 0.0008 * frameRms);
                }
            }

            const dynamicGate = Math.max(this._speechFloorRms, this._noiseFloorRms * 2.4);
            if (frameRms >= dynamicGate) {
                if (!this._hasSeededSpeechEnv) {
                    this._speechRmsEnv = Math.max(0.005, Math.min(0.35, frameRms));
                    this._hasSeededSpeechEnv = true;
                } else {
                    // Ultra-slow speech calibration (~1.0s downward, ~250ms upward protection)
                    const alpha = frameRms > this._speechRmsEnv ? 0.012 : 0.003;
                    this._speechRmsEnv += alpha * (frameRms - this._speechRmsEnv);
                }

                if (this._speechRmsEnv < this._targetRms) {
                    const desired = this._targetRms / Math.max(this._speechRmsEnv, 1e-4);
                    this._targetGain = Math.min(this._maxPreGain, Math.max(1.0, desired));
                } else {
                    this._targetGain = 1.0;
                }
            }
            // During silence (frameRms < dynamicGate), _speechRmsEnv and _targetGain remain frozen.
        }

        const smoothCoeff = this._targetGain < this._currentGain ? 0.0025 : 0.0004;
        for (let i = 0; i < len; i++) {
            this._currentGain += smoothCoeff * (this._targetGain - this._currentGain);
            outCh[i] = applySoftKneeLimiter(inCh[i] * this._currentGain);
        }

        for (let ch = 1; ch < output.length; ch++) {
            output[ch].set(outCh);
        }

        return true;
    }
}

class SpokesVoiceLevelerWorklet extends AudioWorkletProcessor {
    constructor(options) {
        super();
        const opts = (options && options.processorOptions) || {};
        this._stationaryRoomMode = Boolean(opts.stationaryRoomMode);

        // Standard VoIP conversational loudness target: -20.0 dBFS (0.10 linear RMS)
        this._targetSpeechRms = 0.10;
        this._maxBoostGain = 5.6234133;   // +15.0 dB max upward makeup gain
        this._minAttenGain = 0.5011872;   // -6.0 dB gentle downward protection on shouting
        this._speechGateFloorRms = 0.0056234; // -45.0 dBFS post-denoiser speech gate

        this._noiseFloorRms = 0.0010;     // -60 dBFS post-denoiser residual floor
        this._speechRmsEnv = this._targetSpeechRms;
        this._hasSeededSpeechEnv = false;
        this._targetGain = 1.0;
        this._currentGain = 1.0;

        this.port.onmessage = (event) => {
            const msg = event && event.data;
            if (msg && msg.type === 'SET_STATIONARY_ROOM_MODE') {
                this._stationaryRoomMode = Boolean(msg.value);
                if (this._stationaryRoomMode) {
                    this._targetGain = 1.0;
                }
            }
        };
    }

    process(inputs, outputs) {
        const input = inputs[0];
        const output = outputs[0];
        if (!output || output.length === 0) return true;

        const outCh = output[0];
        if (!input || input.length === 0 || !input[0]) {
            outCh.fill(0);
            return true;
        }

        const inCh = input[0];
        const len = inCh.length;

        if (this._stationaryRoomMode) {
            this._targetGain = 1.0;
        } else {
            const frameRms = computeFrameRms(inCh);

            if (frameRms > 1e-6) {
                if (frameRms < this._noiseFloorRms) {
                    this._noiseFloorRms = 0.92 * this._noiseFloorRms + 0.08 * frameRms;
                } else if (frameRms < this._speechRmsEnv * 0.35) {
                    this._noiseFloorRms = Math.min(0.015, 0.9994 * this._noiseFloorRms + 0.0006 * frameRms);
                }
            }

            const gateThreshold = Math.max(this._speechGateFloorRms, this._noiseFloorRms * 2.5);
            if (frameRms >= gateThreshold) {
                if (!this._hasSeededSpeechEnv) {
                    this._speechRmsEnv = Math.max(0.006, Math.min(0.35, frameRms));
                    this._hasSeededSpeechEnv = true;
                } else {
                    // Active speech detected (~2.67ms per block at 48 kHz):
                    // ~75ms upward RMS tracking (fast protection against sudden loud peaks),
                    // ~280ms downward RMS tracking (smooth natural syllable dynamics).
                    const rmsAlpha = frameRms > this._speechRmsEnv ? 0.035 : 0.0095;
                    this._speechRmsEnv += rmsAlpha * (frameRms - this._speechRmsEnv);
                }

                const rawMakeup = this._targetSpeechRms / Math.max(this._speechRmsEnv, 1e-4);
                this._targetGain = Math.min(this._maxBoostGain, Math.max(this._minAttenGain, rawMakeup));
            }
            // Silence freeze: when frameRms < gateThreshold, _speechRmsEnv and _targetGain hold steady
            // so background noise is never pumped during pauses.
        }

        // Per-sample gain ballistics: ~15ms attack when reducing gain, ~220ms smooth ramp when boosting
        const gainCoeff = this._targetGain < this._currentGain ? 0.0014 : 0.0001;
        for (let i = 0; i < len; i++) {
            this._currentGain += gainCoeff * (this._targetGain - this._currentGain);
            outCh[i] = applySoftKneeLimiter(inCh[i] * this._currentGain);
        }

        for (let ch = 1; ch < output.length; ch++) {
            output[ch].set(outCh);
        }

        return true;
    }
}

registerProcessor('SpokesPreDenoiserWorklet', SpokesPreDenoiserWorklet);
registerProcessor('SpokesVoiceLevelerWorklet', SpokesVoiceLevelerWorklet);
