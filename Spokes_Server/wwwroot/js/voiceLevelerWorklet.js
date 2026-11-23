// voiceLevelerWorklet.js
// Reusable AudioWorklet stages for Spokes voice capture and playback conditioning:
// 1. SpokesPreDenoiserWorklet: Log-domain (dB) stationary pre-calibration lifting quiet (< -26 dBFS) mics
//    into RNNoise / DeepFilterNet3's training sweet spot without pumping background noise or suffering
//    multi-second post-silence gain blackouts after brief transients.
// 2. SpokesVoiceLevelerWorklet: Decoupled two-stage Log-Domain (dB) Sustained-Speech Loudness Normalizer
//    (-16.5 dBFS Discord-matched target, +15 dB max upward boost, -12 dB shouting attenuation, 20 ms
//    pitch-spanning window, minimum-statistics noise floor, crest-factor impulse rejection, sustained
//    onset confirmation, ITU BS.1770-style relative gating, idle attenuation relaxation) + independent
//    fast peak-duck guard (2 ms attack / 120 ms release) + always-on -1.0 dBFS C1 Soft-Knee Limiter.
//    Serves both post-denoiser sender capture and Desktop/Web receiver trim normalization.
// 3. SpokesSoftLimiterWorklet: Zero-lookahead multi-channel C1-continuous -0.5 dBFS true-peak ceiling
//    protecting Desktop/Web output after 0%–200% per-participant & master gain boosts.

const DEFAULT_LIMITER_THRESHOLD = 0.78;        // ~ -2.15 dBFS soft-knee start
const DEFAULT_LIMITER_CEILING = 0.8912509;     // -1.0 dBFS capture/leveler peak ceiling
const MASTER_LIMITER_THRESHOLD = 0.8413951;    // -1.5 dBFS master post-boost knee start
const MASTER_LIMITER_CEILING = 0.9440609;      // -0.5 dBFS master post-boost DAC ceiling
const NF_RING_SIZE = 75;                       // 75 x 20 ms = 1.5 s rolling minimum-statistics window

function resolveSampleRate() {
    return (typeof sampleRate === 'number' && Number.isFinite(sampleRate) && sampleRate > 8000)
        ? sampleRate
        : 48000;
}

function dbToLinear(db) {
    return Math.pow(10, db * 0.05);
}

function clamp(val, min, max) {
    return val < min ? min : (val > max ? max : val);
}

function applySoftKneeLimiter(sample, threshold = DEFAULT_LIMITER_THRESHOLD, ceiling = DEFAULT_LIMITER_CEILING) {
    if (!Number.isFinite(sample)) return 0.0;
    const abs = Math.abs(sample);
    if (abs <= threshold) {
        return sample;
    }
    const span = Math.max(1e-4, ceiling - threshold);
    const compressed = threshold + span * Math.tanh((abs - threshold) / span);
    return sample >= 0.0 ? compressed : -compressed;
}

/**
 * Shared decoupled two-stage speech loudness normalizer + independent fast peak-duck guard.
 * Stage 1 estimates sustained speech loudness in dB over 20 ms pitch-spanning windows so brief
 * coughs, mic bumps, plosives, or unmute pops never poison the speaker's long-term baseline.
 * Stage 2 applies an independent fast peak-duck gain (2 ms attack / 120 ms release) that tames
 * sudden shouts/transients without mutating Stage 1 state.
 */
class SpeechLoudnessEstimator {
    constructor(cfg) {
        this.targetSpeechDb = cfg.targetSpeechDb;
        this.maxBoostDb = cfg.maxBoostDb;
        this.minAttenDb = cfg.minAttenDb;
        this.speechGateFloorDb = cfg.speechGateFloorDb;
        this.maxNoiseFloorDb = cfg.maxNoiseFloorDb;
        this.noiseMarginDb = cfg.noiseMarginDb;
        this.confirmWindows = Math.max(1, Math.min(8, cfg.confirmWindows || 3));
        this.envRiseTauSec = cfg.envRiseTauSec || 0.55;
        this.envFallTauSec = cfg.envFallTauSec || 0.45;
        this.maxRiseStepDb = cfg.maxRiseStepDb || 0.55;
        this.relativeGateDb = cfg.relativeGateDb ?? -10.0;
        this.softUnlockWindows = cfg.softUnlockWindows || 12;
        this.maxCrestFactor = cfg.maxCrestFactor || 6.5;
        this.idleRelaxDelaySec = cfg.idleRelaxDelaySec ?? 1.5;
        this.idleRelaxTauSec = cfg.idleRelaxTauSec || 2.0;
        this.baselineUpTauSec = cfg.baselineUpTauSec || 8.0;
        this.baselineDownTauSec = cfg.baselineDownTauSec || 16.0;
        this.limiterThreshold = cfg.limiterThreshold || DEFAULT_LIMITER_THRESHOLD;
        this.limiterCeiling = cfg.limiterCeiling || DEFAULT_LIMITER_CEILING;
        this.enableFastDuck = cfg.enableFastDuck !== false;

        this.sampleRate = resolveSampleRate();
        this.windowSamples = Math.max(128, Math.round(this.sampleRate * 0.020));
        this.minMakeupGain = dbToLinear(this.minAttenDb);
        this.maxMakeupGain = dbToLinear(this.maxBoostDb);

        const sr = this.sampleRate;
        this.makeupUpAlpha = 1.0 - Math.exp(-1.0 / (sr * (cfg.makeupUpTauSec || 0.120)));
        this.makeupDownAlpha = 1.0 - Math.exp(-1.0 / (sr * (cfg.makeupDownTauSec || 0.035)));
        this.duckAttackAlpha = 1.0 - Math.exp(-1.0 / (sr * (cfg.fastDuckAttackTauSec || 0.002)));
        this.duckReleaseAlpha = 1.0 - Math.exp(-1.0 / (sr * (cfg.fastDuckReleaseTauSec || 0.120)));
        this.fastEnvAlpha = 1.0 - Math.exp(-1.0 / (sr * 0.010));
        this.fastDuckHoldSamples = Math.max(1, Math.round(sr * (cfg.fastDuckHoldSec || 0.020)));
        this.shoutRmsCeiling = dbToLinear(this.targetSpeechDb + (cfg.fastShoutHeadroomDb ?? 2.0));

        this._nfRing = new Float32Array(NF_RING_SIZE);
        this._seedBuf = [];
        this.resetState(true);
    }

    resetState(resetCurrentGain = false) {
        this._winSumSq = 0.0;
        this._winPeak = 0.0;
        this._winCount = 0;

        this._nfRing.fill(-60.0);
        this._nfIdx = 0;
        this._noiseFloorDb = -60.0;

        this._speechLevelDb = this.targetSpeechDb;
        this._baselineGainDb = 0.0;
        this._hasSeeded = false;
        this._confirmCount = 0;
        this._seedBuf.length = 0;
        this._softBelowCount = 0;
        this._silenceSec = 0.0;

        this.targetMakeupGain = 1.0;
        this._fastPowerEnv = 0.0;
        this._fastDuckHoldRemaining = 0;
        this._heldDuckTarget = 1.0;
        if (resetCurrentGain) {
            this.currentMakeupGain = 1.0;
            this.fastDuckGain = 1.0;
        }
    }

    get effectiveGain() {
        return this.currentMakeupGain * this.fastDuckGain;
    }

    _evaluateWindow(meanSq, peak) {
        const dt = this.windowSamples / this.sampleRate;
        const rms = Math.sqrt(Math.max(meanSq, 0.0));
        const db = 10.0 * Math.log10(Math.max(meanSq, 1e-12));
        const crest = peak / (rms + 1e-7);

        // Decoupled 1.5 s rolling minimum-statistics noise floor (never coupled to speechLevelDb).
        if (meanSq > 1e-10) {
            this._nfRing[this._nfIdx] = clamp(db, -65.0, this.maxNoiseFloorDb);
            this._nfIdx = (this._nfIdx + 1) % NF_RING_SIZE;
            let minVal = this.maxNoiseFloorDb;
            for (let i = 0; i < NF_RING_SIZE; i++) {
                if (this._nfRing[i] < minVal) minVal = this._nfRing[i];
            }
            this._noiseFloorDb = 0.90 * this._noiseFloorDb + 0.10 * minVal;
        }

        const gateDb = Math.max(this.speechGateFloorDb, this._noiseFloorDb + this.noiseMarginDb);
        const isImpulsive = crest > this.maxCrestFactor;

        if (db >= gateDb && !isImpulsive) {
            this._silenceSec = 0.0;
            this._confirmCount++;

            if (!this._hasSeeded) {
                this._seedBuf.push(clamp(db, -45.0, -8.0));
                if (this._seedBuf.length >= this.confirmWindows) {
                    this._seedBuf.sort((a, b) => a - b);
                    this._speechLevelDb = this._seedBuf[Math.floor(this._seedBuf.length / 2)];
                    this._hasSeeded = true;
                    this._seedBuf.length = 0;

                    const desiredDb = clamp(this.targetSpeechDb - this._speechLevelDb, this.minAttenDb, this.maxBoostDb);
                    this._baselineGainDb = desiredDb;
                    this.targetMakeupGain = dbToLinear(desiredDb);
                    if (this.targetMakeupGain > 1.0) {
                        this.currentMakeupGain = Math.sqrt(this.targetMakeupGain);
                    }
                }
                return;
            }

            if (this._confirmCount >= this.confirmWindows) {
                const diff = db - this._speechLevelDb;
                if (diff >= 0.0) {
                    this._softBelowCount = 0;
                    const riseAlpha = 1.0 - Math.exp(-dt / this.envRiseTauSec);
                    const step = Math.min(this.maxRiseStepDb, riseAlpha * diff);
                    this._speechLevelDb += step;
                } else if (diff >= this.relativeGateDb) {
                    this._softBelowCount = 0;
                    const fallAlpha = 1.0 - Math.exp(-dt / this.envFallTauSec);
                    this._speechLevelDb += fallAlpha * diff;
                } else {
                    // Sustained quiet speech (>240 ms below relative gate) unlocks smooth downward tracking
                    // without letting short inter-syllable consonant dips over-boost hot microphones.
                    this._softBelowCount++;
                    if (this._softBelowCount >= this.softUnlockWindows) {
                        const unlockAlpha = 1.0 - Math.exp(-dt / 0.30);
                        this._speechLevelDb += unlockAlpha * diff;
                    }
                }

                this._speechLevelDb = clamp(this._speechLevelDb, -45.0, -4.0);
                const desiredDb = clamp(this.targetSpeechDb - this._speechLevelDb, this.minAttenDb, this.maxBoostDb);
                this.targetMakeupGain = dbToLinear(desiredDb);

                const baseTau = desiredDb < this._baselineGainDb ? this.baselineDownTauSec : this.baselineUpTauSec;
                const baseAlpha = 1.0 - Math.exp(-dt / baseTau);
                this._baselineGainDb += baseAlpha * (desiredDb - this._baselineGainDb);
            }
        } else {
            this._confirmCount = 0;
            this._softBelowCount = 0;
            if (!this._hasSeeded) {
                this._seedBuf.length = 0;
            }
            if (db < gateDb) {
                this._silenceSec += dt;
                if (this._hasSeeded && this._silenceSec > this.idleRelaxDelaySec) {
                    // During silence: never boost noise above baseline; only relax abnormal
                    // downward attenuation back up toward the speaker's established baseline.
                    const curTargetDb = 20.0 * Math.log10(Math.max(this.targetMakeupGain, 1e-4));
                    const relaxTargetDb = clamp(this._baselineGainDb, this.minAttenDb, this.maxBoostDb);
                    if (curTargetDb < relaxTargetDb - 0.05) {
                        const relaxAlpha = 1.0 - Math.exp(-dt / this.idleRelaxTauSec);
                        const nextDb = curTargetDb + relaxAlpha * (relaxTargetDb - curTargetDb);
                        this.targetMakeupGain = dbToLinear(nextDb);
                        this._speechLevelDb = clamp(this.targetSpeechDb - nextDb, -45.0, -4.0);
                    }
                }
            }
        }
    }

    process(input, output, enabled) {
        const len = input[0].length;
        const inChCount = input.length;
        const outChCount = output.length;

        let framePeak = 0.0;
        let maxChMeanSq = 0.0;
        for (let ch = 0; ch < inChCount; ch++) {
            const channel = input[ch];
            if (!channel || channel.length === 0) continue;
            let sumSq = 0.0;
            for (let i = 0; i < len; i++) {
                const s = channel[i];
                const a = Math.abs(s);
                if (a > framePeak) framePeak = a;
                sumSq += s * s;
            }
            const chMeanSq = sumSq / len;
            if (chMeanSq > maxChMeanSq) maxChMeanSq = chMeanSq;
        }

        if (!enabled) {
            this.targetMakeupGain = 1.0;
        } else {
            this._winSumSq += maxChMeanSq * len;
            if (framePeak > this._winPeak) this._winPeak = framePeak;
            this._winCount += len;
            if (this._winCount >= this.windowSamples) {
                this._evaluateWindow(this._winSumSq / this._winCount, this._winPeak);
                this._winSumSq = 0.0;
                this._winPeak = 0.0;
                this._winCount = 0;
            }
        }

        // Fast ~10 ms RMS envelope for Stage 2 shout protection (decoupled from Stage 1 baseline).
        const hopFastAlpha = 1.0 - Math.pow(1.0 - this.fastEnvAlpha, len);
        this._fastPowerEnv += hopFastAlpha * (maxChMeanSq - this._fastPowerEnv);
        const fastRms = Math.sqrt(Math.max(this._fastPowerEnv, 0.0));

        // Independent Stage 2 fast peak + short-term shout ducking guard (never mutates Stage 1 speechLevelDb).
        let instDuck = 1.0;
        if (enabled && this.enableFastDuck) {
            let maxSafeTotal = this.maxMakeupGain;
            if (framePeak * this.currentMakeupGain > this.limiterThreshold) {
                maxSafeTotal = Math.min(
                    maxSafeTotal,
                    clamp(this.limiterThreshold / (framePeak + 1e-6), this.minMakeupGain, this.maxMakeupGain)
                );
            }
            if (fastRms * this.currentMakeupGain > this.shoutRmsCeiling) {
                maxSafeTotal = Math.min(
                    maxSafeTotal,
                    clamp(this.shoutRmsCeiling / (fastRms + 1e-6), this.minMakeupGain, this.maxMakeupGain)
                );
            }
            if (maxSafeTotal < this.currentMakeupGain) {
                instDuck = clamp(maxSafeTotal / Math.max(this.currentMakeupGain, 1e-4), 0.05, 1.0);
            }
        }

        let desiredDuck = 1.0;
        if (instDuck < 0.999) {
            if (instDuck <= this._heldDuckTarget || this._fastDuckHoldRemaining <= 0) {
                this._heldDuckTarget = instDuck;
            }
            this._fastDuckHoldRemaining = this.fastDuckHoldSamples;
            desiredDuck = Math.min(instDuck, this._heldDuckTarget);
        } else if (this._fastDuckHoldRemaining > 0) {
            this._fastDuckHoldRemaining = Math.max(0, this._fastDuckHoldRemaining - len);
            desiredDuck = this._heldDuckTarget;
        } else {
            this._heldDuckTarget = 1.0;
        }

        const makeupAlpha = this.targetMakeupGain < this.currentMakeupGain ? this.makeupDownAlpha : this.makeupUpAlpha;
        const duckAlpha = desiredDuck < this.fastDuckGain ? this.duckAttackAlpha : this.duckReleaseAlpha;

        for (let i = 0; i < len; i++) {
            this.currentMakeupGain += makeupAlpha * (this.targetMakeupGain - this.currentMakeupGain);
            this.fastDuckGain += duckAlpha * (desiredDuck - this.fastDuckGain);
            const g = this.currentMakeupGain * this.fastDuckGain;
            for (let ch = 0; ch < outChCount; ch++) {
                const srcCh = input[ch < inChCount ? ch : 0];
                output[ch][i] = applySoftKneeLimiter(srcCh[i] * g, this.limiterThreshold, this.limiterCeiling);
            }
        }
    }
}

class SpokesPreDenoiserWorklet extends AudioWorkletProcessor {
    constructor(options) {
        super();
        const opts = (options && options.processorOptions) || {};
        this._enabled = opts.enabled !== false;
        this._estimator = new SpeechLoudnessEstimator({
            targetSpeechDb: -26.0,      // Neural denoiser input sweet spot
            maxBoostDb: 9.0,            // Max +9 dB pre-lift for quiet mics
            minAttenDb: 0.0,            // Never attenuate normal/hot mics pre-denoiser
            speechGateFloorDb: -47.0,
            maxNoiseFloorDb: -38.0,
            noiseMarginDb: 7.0,
            confirmWindows: 3,
            envRiseTauSec: 0.60,
            envFallTauSec: 0.55,
            maxRiseStepDb: 0.50,
            makeupUpTauSec: 0.060,
            makeupDownTauSec: 0.025,
            enableFastDuck: false
        });

        this.port.onmessage = (event) => {
            const msg = event && event.data;
            if (msg && msg.type === 'SET_ENABLED') {
                this._enabled = Boolean(msg.value);
                if (!this._enabled) {
                    // Smoothly ramp currentMakeupGain toward 1.0 in process() without clicking
                    this._estimator.resetState(false);
                }
            }
        };
    }

    get _currentGain() {
        return this._estimator.effectiveGain;
    }

    process(inputs, outputs) {
        const input = inputs[0];
        const output = outputs[0];
        if (!output || output.length === 0) return true;

        if (!input || input.length === 0 || !input[0]) {
            for (let ch = 0; ch < output.length; ch++) {
                output[ch].fill(0);
            }
            return true;
        }

        this._estimator.process(input, output, this._enabled);
        return true;
    }
}

class SpokesVoiceLevelerWorklet extends AudioWorkletProcessor {
    constructor(options) {
        super();
        const opts = (options && options.processorOptions) || {};
        this._enabled = opts.enabled !== false;

        const targetSpeechDb = typeof opts.targetSpeechDb === 'number' && Number.isFinite(opts.targetSpeechDb)
            ? opts.targetSpeechDb
            : (typeof opts.targetSpeechRms === 'number' && opts.targetSpeechRms > 0
                ? 20.0 * Math.log10(opts.targetSpeechRms)
                : -16.5);
        const maxBoostDb = typeof opts.maxBoostDb === 'number' && Number.isFinite(opts.maxBoostDb)
            ? opts.maxBoostDb
            : (typeof opts.maxBoostGain === 'number' && opts.maxBoostGain >= 1.0
                ? 20.0 * Math.log10(opts.maxBoostGain)
                : 15.0);
        const minAttenDb = typeof opts.minAttenDb === 'number' && Number.isFinite(opts.minAttenDb)
            ? opts.minAttenDb
            : (typeof opts.minAttenGain === 'number' && opts.minAttenGain > 0
                ? 20.0 * Math.log10(opts.minAttenGain)
                : -12.0);
        const speechGateFloorDb = typeof opts.speechGateFloorDb === 'number' && Number.isFinite(opts.speechGateFloorDb)
            ? opts.speechGateFloorDb
            : (typeof opts.speechGateFloorRms === 'number' && opts.speechGateFloorRms > 0
                ? 20.0 * Math.log10(opts.speechGateFloorRms)
                : -42.0);

        this._estimator = new SpeechLoudnessEstimator({
            targetSpeechDb,
            maxBoostDb,
            minAttenDb,
            speechGateFloorDb,
            maxNoiseFloorDb: typeof opts.maxNoiseFloorDb === 'number' ? opts.maxNoiseFloorDb : -44.0,
            noiseMarginDb: typeof opts.noiseMarginDb === 'number' ? opts.noiseMarginDb : 8.0,
            confirmWindows: typeof opts.confirmWindows === 'number' ? opts.confirmWindows : 3,
            envRiseTauSec: typeof opts.envRiseTauSec === 'number' ? opts.envRiseTauSec : 0.55,
            envFallTauSec: typeof opts.envFallTauSec === 'number' ? opts.envFallTauSec : 0.45,
            maxRiseStepDb: typeof opts.maxRiseStepDb === 'number' ? opts.maxRiseStepDb : 0.55,
            limiterThreshold: typeof opts.limiterThreshold === 'number' && opts.limiterThreshold > 0
                ? opts.limiterThreshold
                : DEFAULT_LIMITER_THRESHOLD,
            limiterCeiling: typeof opts.limiterCeiling === 'number' && opts.limiterCeiling > 0
                ? opts.limiterCeiling
                : DEFAULT_LIMITER_CEILING,
            enableFastDuck: true
        });

        this.port.onmessage = (event) => {
            const msg = event && event.data;
            if (msg && msg.type === 'SET_ENABLED') {
                this._enabled = Boolean(msg.value);
                if (!this._enabled) {
                    // Smoothly ramp currentMakeupGain toward 1.0 in process() without clicking; limiter stays active
                    this._estimator.resetState(false);
                }
            }
        };
    }

    get _currentGain() {
        return this._estimator.effectiveGain;
    }

    process(inputs, outputs) {
        const input = inputs[0];
        const output = outputs[0];
        if (!output || output.length === 0) return true;

        if (!input || input.length === 0 || !input[0]) {
            for (let ch = 0; ch < output.length; ch++) {
                output[ch].fill(0);
            }
            return true;
        }

        this._estimator.process(input, output, this._enabled);
        return true;
    }
}

class SpokesSoftLimiterWorklet extends AudioWorkletProcessor {
    constructor(options) {
        super();
        const opts = (options && options.processorOptions) || {};
        this._threshold = typeof opts.threshold === 'number' && opts.threshold > 0
            ? opts.threshold
            : MASTER_LIMITER_THRESHOLD;
        this._ceiling = typeof opts.ceiling === 'number' && opts.ceiling > this._threshold
            ? opts.ceiling
            : MASTER_LIMITER_CEILING;
    }

    process(inputs, outputs) {
        const input = inputs[0];
        const output = outputs[0];
        if (!output || output.length === 0) return true;

        if (!input || input.length === 0 || !input[0]) {
            for (let ch = 0; ch < output.length; ch++) {
                output[ch].fill(0);
            }
            return true;
        }

        const inChCount = input.length;
        const outChCount = output.length;
        const len = input[0].length;
        const threshold = this._threshold;
        const ceiling = this._ceiling;

        for (let ch = 0; ch < outChCount; ch++) {
            const src = input[ch < inChCount ? ch : 0];
            const dst = output[ch];
            for (let i = 0; i < len; i++) {
                dst[i] = applySoftKneeLimiter(src[i], threshold, ceiling);
            }
        }

        return true;
    }
}

registerProcessor('SpokesPreDenoiserWorklet', SpokesPreDenoiserWorklet);
registerProcessor('SpokesVoiceLevelerWorklet', SpokesVoiceLevelerWorklet);
registerProcessor('SpokesSoftLimiterWorklet', SpokesSoftLimiterWorklet);
