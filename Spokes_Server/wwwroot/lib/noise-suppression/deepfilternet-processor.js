class AssetLoader {
    constructor(config = {}) {
        this.cdnUrl = config.cdnUrl ?? 'https://cdn.mezon.ai/AI/models/datas/noise_suppression/deepfilternet3';
    }
    getCdnUrl(relativePath) {
        return `${this.cdnUrl}/${relativePath}`;
    }
    getAssetUrls() {
        return {
            wasm: this.getCdnUrl('v3/pkg/df_bg.wasm'),
            model: this.getCdnUrl('v3/models/DeepFilterNet3_onnx.tar.gz')
        };
    }
    async fetchAsset(url) {
        const response = await fetch(url);
        if (!response.ok) {
            throw new Error(`Failed to fetch asset: ${response.statusText}`);
        }
        return response.arrayBuffer();
    }
}
let defaultLoader = null;
function getAssetLoader(config) {
    if (!defaultLoader || config) {
        defaultLoader = new AssetLoader(config);
    }
    return defaultLoader;
}

/**
 * Creates a worklet module URL from inline code string
 * This approach works with all bundlers without special configuration
 */
async function createWorkletModule(audioContext, workletCode) {
    const blob = new Blob([workletCode], { type: 'application/javascript' });
    const blobUrl = URL.createObjectURL(blob);
    await audioContext.audioWorklet.addModule(blobUrl);
}

const WorkletMessageTypes = {
    SET_SUPPRESSION_LEVEL: 'SET_SUPPRESSION_LEVEL',
    SET_BYPASS: 'SET_BYPASS'
};

var workletCode = "(function () {\n    'use strict';\n\n    /* @ts-self-types=\"./df.d.ts\" */\n\n    // --- AudioWorklet polyfill ---------------------------------------------------\n    // AudioWorkletGlobalScope has no TextDecoder/TextEncoder. wasm-bindgen 0.2.126\n    // constructs `new TextDecoder()` at module top-level (unguarded), which throws\n    // `ReferenceError: TextDecoder is not defined` inside a worklet and prevents\n    // registerProcessor() from running. Provide minimal UTF-8 shims when absent.\n    // (Only used for error/diagnostic strings; the audio path passes bytes/floats.)\n    if (typeof TextDecoder === 'undefined') {\n        globalThis.TextDecoder = class {\n            decode(bytes) {\n                if (!bytes) return '';\n                const u8 = bytes instanceof Uint8Array ? bytes : new Uint8Array(bytes.buffer || bytes);\n                let out = '';\n                for (let i = 0; i < u8.length;) {\n                    const c = u8[i++];\n                    if (c < 0x80) out += String.fromCharCode(c);\n                    else if (c < 0xE0) out += String.fromCharCode(((c & 0x1F) << 6) | (u8[i++] & 0x3F));\n                    else if (c < 0xF0) out += String.fromCharCode(((c & 0x0F) << 12) | ((u8[i++] & 0x3F) << 6) | (u8[i++] & 0x3F));\n                    else {\n                        const cp = (((c & 0x07) << 18) | ((u8[i++] & 0x3F) << 12) | ((u8[i++] & 0x3F) << 6) | (u8[i++] & 0x3F)) - 0x10000;\n                        out += String.fromCharCode(0xD800 + (cp >> 10), 0xDC00 + (cp & 0x3FF));\n                    }\n                }\n                return out;\n            }\n        };\n    }\n    if (typeof TextEncoder === 'undefined') {\n        globalThis.TextEncoder = class {\n            encode(str) {\n                const out = [];\n                for (let i = 0; i < str.length; i++) {\n                    let c = str.charCodeAt(i);\n                    if (c < 0x80) out.push(c);\n                    else if (c < 0x800) out.push(0xC0 | (c >> 6), 0x80 | (c & 0x3F));\n                    else if (c >= 0xD800 && c < 0xDC00) {\n                        const c2 = str.charCodeAt(++i);\n                        c = 0x10000 + ((c & 0x3FF) << 10) + (c2 & 0x3FF);\n                        out.push(0xF0 | (c >> 18), 0x80 | ((c >> 12) & 0x3F), 0x80 | ((c >> 6) & 0x3F), 0x80 | (c & 0x3F));\n                    } else out.push(0xE0 | (c >> 12), 0x80 | ((c >> 6) & 0x3F), 0x80 | (c & 0x3F));\n                }\n                return new Uint8Array(out);\n            }\n            encodeInto(str, dst) {\n                const enc = this.encode(str);\n                dst.set(enc);\n                return { read: str.length, written: enc.length };\n            }\n        };\n    }\n\n    /**\n     * Create a DeepFilterNet Model\n     *\n     * Args:\n     *     - path: File path to a DeepFilterNet tar.gz onnx model\n     *     - atten_lim: Attenuation limit in dB.\n     *\n     * Returns:\n     *     - DF state doing the full processing: stft, DNN noise reduction, istft.\n     * @param {Uint8Array} model_bytes\n     * @param {number} atten_lim\n     * @returns {number}\n     */\n    function df_create(model_bytes, atten_lim) {\n        const ptr0 = passArray8ToWasm0(model_bytes, wasm.__wbindgen_malloc_command_export);\n        const len0 = WASM_VECTOR_LEN;\n        const ret = wasm.df_create(ptr0, len0, atten_lim);\n        return ret >>> 0;\n    }\n\n    /**\n     * Get DeepFilterNet frame size in samples.\n     * @param {number} st\n     * @returns {number}\n     */\n    function df_get_frame_length(st) {\n        const ret = wasm.df_get_frame_length(st);\n        return ret >>> 0;\n    }\n\n    /**\n     * Processes a chunk of samples.\n     *\n     * Args:\n     *     - df_state: Created via df_create()\n     *     - input: Input buffer of length df_get_frame_length()\n     *     - output: Output buffer of length df_get_frame_length()\n     *\n     * Returns:\n     *     - Local SNR of the current frame.\n     * @param {number} st\n     * @param {Float32Array} input\n     * @returns {Float32Array}\n     */\n    function df_process_frame(st, input) {\n        const ptr0 = passArrayF32ToWasm0(input, wasm.__wbindgen_malloc_command_export);\n        const len0 = WASM_VECTOR_LEN;\n        const ret = wasm.df_process_frame(st, ptr0, len0);\n        return ret;\n    }\n\n    /**\n     * Set DeepFilterNet attenuation limit.\n     *\n     * Args:\n     *     - lim_db: New attenuation limit in dB.\n     * @param {number} st\n     * @param {number} lim_db\n     */\n    function df_set_atten_lim(st, lim_db) {\n        wasm.df_set_atten_lim(st, lim_db);\n    }\n    function __wbg_get_imports() {\n        const import0 = {\n            __proto__: null,\n            __wbg___wbindgen_throw_344f42d3211c4765: function(arg0, arg1) {\n                throw new Error(getStringFromWasm0(arg0, arg1));\n            },\n            __wbg_getRandomValues_cc7f052a444bb2ce: function() { return handleError(function (arg0, arg1) {\n                globalThis.crypto.getRandomValues(getArrayU8FromWasm0(arg0, arg1));\n            }, arguments); },\n            __wbg_new_from_slice_ddf8b82c4d6af38e: function(arg0, arg1) {\n                const ret = new Float32Array(getArrayF32FromWasm0(arg0, arg1));\n                return ret;\n            },\n            __wbindgen_init_externref_table: function() {\n                const table = wasm.__wbindgen_externrefs;\n                const offset = table.grow(4);\n                table.set(0, undefined);\n                table.set(offset + 0, undefined);\n                table.set(offset + 1, null);\n                table.set(offset + 2, true);\n                table.set(offset + 3, false);\n            },\n        };\n        return {\n            __proto__: null,\n            \"./df_bg.js\": import0,\n        };\n    }\n\n    (typeof FinalizationRegistry === 'undefined')\n        ? { }\n        : new FinalizationRegistry(ptr => wasm.__wbg_dfstate_free(ptr, 1));\n\n    function addToExternrefTable0(obj) {\n        const idx = wasm.__externref_table_alloc_command_export();\n        wasm.__wbindgen_externrefs.set(idx, obj);\n        return idx;\n    }\n\n    function getArrayF32FromWasm0(ptr, len) {\n        ptr = ptr >>> 0;\n        return getFloat32ArrayMemory0().subarray(ptr / 4, ptr / 4 + len);\n    }\n\n    function getArrayU8FromWasm0(ptr, len) {\n        ptr = ptr >>> 0;\n        return getUint8ArrayMemory0().subarray(ptr / 1, ptr / 1 + len);\n    }\n\n    let cachedFloat32ArrayMemory0 = null;\n    function getFloat32ArrayMemory0() {\n        if (cachedFloat32ArrayMemory0 === null || cachedFloat32ArrayMemory0.byteLength === 0) {\n            cachedFloat32ArrayMemory0 = new Float32Array(wasm.memory.buffer);\n        }\n        return cachedFloat32ArrayMemory0;\n    }\n\n    function getStringFromWasm0(ptr, len) {\n        return decodeText(ptr >>> 0, len);\n    }\n\n    let cachedUint8ArrayMemory0 = null;\n    function getUint8ArrayMemory0() {\n        if (cachedUint8ArrayMemory0 === null || cachedUint8ArrayMemory0.byteLength === 0) {\n            cachedUint8ArrayMemory0 = new Uint8Array(wasm.memory.buffer);\n        }\n        return cachedUint8ArrayMemory0;\n    }\n\n    function handleError(f, args) {\n        try {\n            return f.apply(this, args);\n        } catch (e) {\n            const idx = addToExternrefTable0(e);\n            wasm.__wbindgen_exn_store_command_export(idx);\n        }\n    }\n\n    function passArray8ToWasm0(arg, malloc) {\n        const ptr = malloc(arg.length * 1, 1) >>> 0;\n        getUint8ArrayMemory0().set(arg, ptr / 1);\n        WASM_VECTOR_LEN = arg.length;\n        return ptr;\n    }\n\n    function passArrayF32ToWasm0(arg, malloc) {\n        const ptr = malloc(arg.length * 4, 4) >>> 0;\n        getFloat32ArrayMemory0().set(arg, ptr / 4);\n        WASM_VECTOR_LEN = arg.length;\n        return ptr;\n    }\n\n    let cachedTextDecoder = new TextDecoder('utf-8', { ignoreBOM: true, fatal: true });\n    cachedTextDecoder.decode();\n    const MAX_SAFARI_DECODE_BYTES = 2146435072;\n    let numBytesDecoded = 0;\n    function decodeText(ptr, len) {\n        numBytesDecoded += len;\n        if (numBytesDecoded >= MAX_SAFARI_DECODE_BYTES) {\n            cachedTextDecoder = new TextDecoder('utf-8', { ignoreBOM: true, fatal: true });\n            cachedTextDecoder.decode();\n            numBytesDecoded = len;\n        }\n        return cachedTextDecoder.decode(getUint8ArrayMemory0().subarray(ptr, ptr + len));\n    }\n\n    let WASM_VECTOR_LEN = 0;\n\n    let wasm;\n    function __wbg_finalize_init(instance, module) {\n        wasm = instance.exports;\n        cachedFloat32ArrayMemory0 = null;\n        cachedUint8ArrayMemory0 = null;\n        wasm.__wbindgen_start();\n        return wasm;\n    }\n\n    function initSync(module) {\n        if (wasm !== undefined) return wasm;\n\n\n        if (module !== undefined) {\n            if (Object.getPrototypeOf(module) === Object.prototype) {\n                ({module} = module);\n            } else {\n                console.warn('using deprecated parameters for `initSync()`; pass a single object instead');\n            }\n        }\n\n        const imports = __wbg_get_imports();\n        if (!(module instanceof WebAssembly.Module)) {\n            module = new WebAssembly.Module(module);\n        }\n        const instance = new WebAssembly.Instance(module, imports);\n        return __wbg_finalize_init(instance);\n    }\n\n    const WorkletMessageTypes = {\n        SET_SUPPRESSION_LEVEL: 'SET_SUPPRESSION_LEVEL',\n        SET_BYPASS: 'SET_BYPASS'\n    };\n\n    class DeepFilterAudioProcessor extends AudioWorkletProcessor {\n        constructor(options) {\n            super();\n            this.dfModel = null;\n            this.inputWritePos = 0;\n            this.inputReadPos = 0;\n            this.outputWritePos = 0;\n            this.outputReadPos = 0;\n            this.bypass = false;\n            this.isInitialized = false;\n            this.tempFrame = null;\n            this.bufferSize = 8192;\n            this.inputBuffer = new Float32Array(this.bufferSize);\n            this.outputBuffer = new Float32Array(this.bufferSize);\n            try {\n                // Initialize WASM from pre-compiled module\n                initSync(options.processorOptions.wasmModule);\n                const modelBytes = new Uint8Array(options.processorOptions.modelBytes);\n                const handle = df_create(modelBytes, options.processorOptions.suppressionLevel ?? 50);\n                const frameLength = df_get_frame_length(handle);\n                this.dfModel = { handle, frameLength };\n                this.bufferSize = frameLength * 4;\n                this.inputBuffer = new Float32Array(this.bufferSize);\n                this.outputBuffer = new Float32Array(this.bufferSize);\n                // Pre-allocate temp frame buffer for processing\n                this.tempFrame = new Float32Array(frameLength);\n                this.isInitialized = true;\n                this.port.onmessage = (event) => {\n                    this.handleMessage(event.data);\n                };\n            }\n            catch (error) {\n                console.error('Failed to initialize DeepFilter in AudioWorklet:', error);\n                this.isInitialized = false;\n            }\n        }\n        handleMessage(data) {\n            switch (data.type) {\n                case WorkletMessageTypes.SET_SUPPRESSION_LEVEL:\n                    if (this.dfModel && typeof data.value === 'number') {\n                        const level = Math.max(0, Math.min(100, Math.floor(data.value)));\n                        df_set_atten_lim(this.dfModel.handle, level);\n                    }\n                    break;\n                case WorkletMessageTypes.SET_BYPASS:\n                    this.bypass = Boolean(data.value);\n                    break;\n            }\n        }\n        getInputAvailable() {\n            return (this.inputWritePos - this.inputReadPos + this.bufferSize) % this.bufferSize;\n        }\n        getOutputAvailable() {\n            return (this.outputWritePos - this.outputReadPos + this.bufferSize) % this.bufferSize;\n        }\n        process(inputList, outputList) {\n            const sourceLimit = Math.min(inputList.length, outputList.length);\n            const input = inputList[0]?.[0];\n            if (!input) {\n                return true;\n            }\n            // Passthrough mode - copy input to all output channels\n            if (!this.isInitialized || !this.dfModel || this.bypass || !this.tempFrame) {\n                for (let inputNum = 0; inputNum < sourceLimit; inputNum++) {\n                    const output = outputList[inputNum];\n                    const channelCount = output.length;\n                    for (let channelNum = 0; channelNum < channelCount; channelNum++) {\n                        output[channelNum].set(input);\n                    }\n                }\n                return true;\n            }\n            // Write input to ring buffer\n            for (let i = 0; i < input.length; i++) {\n                this.inputBuffer[this.inputWritePos] = input[i];\n                this.inputWritePos = (this.inputWritePos + 1) % this.bufferSize;\n            }\n            const frameLength = this.dfModel.frameLength;\n            while (this.getInputAvailable() >= frameLength) {\n                // Extract frame from ring buffer\n                for (let i = 0; i < frameLength; i++) {\n                    this.tempFrame[i] = this.inputBuffer[this.inputReadPos];\n                    this.inputReadPos = (this.inputReadPos + 1) % this.bufferSize;\n                }\n                const processed = df_process_frame(this.dfModel.handle, this.tempFrame);\n                // Write to output ring buffer\n                for (let i = 0; i < processed.length; i++) {\n                    this.outputBuffer[this.outputWritePos] = processed[i];\n                    this.outputWritePos = (this.outputWritePos + 1) % this.bufferSize;\n                }\n            }\n            const outputAvailable = this.getOutputAvailable();\n            if (outputAvailable >= 128) {\n                for (let inputNum = 0; inputNum < sourceLimit; inputNum++) {\n                    const output = outputList[inputNum];\n                    const channelCount = output.length;\n                    for (let channelNum = 0; channelNum < channelCount; channelNum++) {\n                        const outputChannel = output[channelNum];\n                        let readPos = this.outputReadPos;\n                        for (let i = 0; i < 128; i++) {\n                            outputChannel[i] = this.outputBuffer[readPos];\n                            readPos = (readPos + 1) % this.bufferSize;\n                        }\n                    }\n                }\n                this.outputReadPos = (this.outputReadPos + 128) % this.bufferSize;\n            }\n            return true;\n        }\n    }\n    registerProcessor('deepfilter-audio-processor', DeepFilterAudioProcessor);\n\n})();\n";

class DeepFilterNet3Core {
    constructor(config = {}) {
        this.assets = null;
        this.workletNode = null;
        this.isInitialized = false;
        this.bypassEnabled = false;
        this.config = {
            sampleRate: config.sampleRate ?? 48000,
            noiseReductionLevel: config.noiseReductionLevel ?? 50,
            assetConfig: config.assetConfig
        };
        this.assetLoader = getAssetLoader(config.assetConfig);
    }
    async initialize() {
        if (this.isInitialized)
            return;
        // Fetch and compile WASM on main thread
        const assetUrls = this.assetLoader.getAssetUrls();
        const [wasmBytes, modelBytes] = await Promise.all([
            this.assetLoader.fetchAsset(assetUrls.wasm),
            this.assetLoader.fetchAsset(assetUrls.model)
        ]);
        // Compile WASM module
        const wasmModule = await WebAssembly.compile(wasmBytes);
        this.assets = { wasmModule, modelBytes };
        this.isInitialized = true;
    }
    async createAudioWorkletNode(audioContext) {
        this.ensureInitialized();
        if (!this.assets) {
            throw new Error('Assets not loaded');
        }
        await createWorkletModule(audioContext, workletCode);
        this.workletNode = new AudioWorkletNode(audioContext, 'deepfilter-audio-processor', {
            processorOptions: {
                wasmModule: this.assets.wasmModule,
                modelBytes: this.assets.modelBytes,
                suppressionLevel: this.config.noiseReductionLevel
            }
        });
        return this.workletNode;
    }
    setSuppressionLevel(level) {
        if (!this.workletNode || typeof level !== 'number' || isNaN(level))
            return;
        const clampedLevel = Math.max(0, Math.min(100, Math.floor(level)));
        this.workletNode.port.postMessage({
            type: WorkletMessageTypes.SET_SUPPRESSION_LEVEL,
            value: clampedLevel
        });
    }
    destroy() {
        if (!this.isInitialized)
            return;
        if (this.workletNode) {
            this.workletNode.disconnect();
            this.workletNode = null;
        }
        this.assets = null;
        this.isInitialized = false;
    }
    isReady() {
        return this.isInitialized && this.workletNode !== null;
    }
    setNoiseSuppressionEnabled(enabled) {
        if (!this.workletNode)
            return;
        this.bypassEnabled = !enabled;
        this.workletNode.port.postMessage({
            type: WorkletMessageTypes.SET_BYPASS,
            value: !enabled
        });
    }
    isNoiseSuppressionEnabled() {
        return !this.bypassEnabled;
    }
    ensureInitialized() {
        if (!this.isInitialized) {
            throw new Error('Processor not initialized. Call initialize() first.');
        }
    }
}

class DeepFilterNoiseFilterProcessor {
    constructor(options = {}) {
        this.name = 'deepfilternet3-noise-filter';
        this.audioContext = null;
        this.sourceNode = null;
        this.workletNode = null;
        this.destination = null;
        this.enabled = true;
        this.init = async (opts) => {
            const track = opts.track ?? opts.mediaStreamTrack;
            if (!track) {
                throw new Error('DeepFilterNoiseFilterProcessor.init: missing MediaStreamTrack');
            }
            this.originalTrack = track;
            await this.ensureGraph();
        };
        this.restart = async (opts) => {
            const track = opts.track ?? opts.mediaStreamTrack;
            if (track) {
                this.originalTrack = track;
            }
            await this.ensureGraph();
        };
        this.setEnabled = async (enable) => {
            this.enabled = enable;
            this.processor.setNoiseSuppressionEnabled(enable);
            return this.enabled;
        };
        this.suspend = async () => {
            if (this.audioContext?.state === 'running') {
                await this.audioContext.suspend();
            }
        };
        this.resume = async () => {
            if (this.audioContext?.state === 'suspended') {
                await this.audioContext.resume();
            }
        };
        this.destroy = async () => {
            await this.teardownGraph();
            this.processor.destroy();
        };
        const cfg = {
            sampleRate: options.sampleRate ?? 48000,
            noiseReductionLevel: options.noiseReductionLevel ?? 80,
            assetConfig: options.assetConfig
        };
        this.enabled = options.enabled ?? true;
        this.processor = new DeepFilterNet3Core(cfg);
    }
    static isSupported() {
        return typeof AudioContext !== 'undefined' && typeof WebAssembly !== 'undefined';
    }
    setSuppressionLevel(level) {
        this.processor.setSuppressionLevel(level);
    }
    isEnabled() {
        return this.enabled;
    }
    isNoiseSuppressionEnabled() {
        return this.processor.isNoiseSuppressionEnabled();
    }
    async ensureGraph() {
        if (!this.originalTrack) {
            throw new Error('No source track');
        }
        this.audioContext ?? (this.audioContext = new AudioContext({ sampleRate: 48000 }));
        if (this.audioContext.state !== 'running') {
            try {
                await this.audioContext.resume();
            }
            catch {
                // Ignore resume errors
            }
        }
        await this.processor.initialize();
        if (!this.workletNode) {
            const node = await this.processor.createAudioWorkletNode(this.audioContext);
            this.workletNode = node;
        }
        if (!this.destination) {
            this.destination = this.audioContext.createMediaStreamDestination();
            this.processedTrack = this.destination.stream.getAudioTracks()[0];
        }
        if (this.sourceNode) {
            this.sourceNode.disconnect();
        }
        this.sourceNode = this.audioContext.createMediaStreamSource(new MediaStream([this.originalTrack]));
        this.sourceNode.connect(this.workletNode).connect(this.destination);
        await this.setEnabled(this.enabled);
    }
    async teardownGraph() {
        try {
            if (this.workletNode) {
                this.workletNode.disconnect();
                this.workletNode = null;
            }
            if (this.sourceNode) {
                this.sourceNode.disconnect();
                this.sourceNode = null;
            }
            if (this.destination) {
                this.destination.disconnect();
                this.destination = null;
            }
            if (this.audioContext) {
                void this.audioContext.close();
                this.audioContext = null;
            }
        }
        catch {
            // Ignore disconnect errors
        }
    }
}
function DeepFilterNoiseFilter(options) {
    return new DeepFilterNoiseFilterProcessor(options);
}

export { AssetLoader, DeepFilterNet3Core, DeepFilterNoiseFilter, DeepFilterNoiseFilterProcessor, getAssetLoader };
