window.SoundManager = (function() {
    let audioCtx = null;
    let audioBuffers = {};
    let isInitialized = false;
    let activeCallSource = null;
    let activePreviewSource = null;
    let callTimeout = null;
    let currentSinkId = '';

    try {
        currentSinkId = localStorage.getItem('spokes_active_speaker_id') || '';
    } catch(e) {}

    const defaultSoundsToLoad = ['channel_join', 'channel_leave', 'spokesnotif1'];

    async function applySinkId() {
        if (audioCtx && typeof audioCtx.setSinkId === 'function') {
            try {
                const targetSink = currentSinkId === 'default' ? '' : currentSinkId;
                await audioCtx.setSinkId(targetSink);
            } catch (e) {
                console.warn('[SoundManager] Failed to apply sinkId to audioCtx:', e);
            }
        }
    }

    async function setSinkId(deviceId) {
        currentSinkId = deviceId === 'default' ? '' : (deviceId || '');
        await applySinkId();
    }

    async function init() {
        if (isInitialized) return;
        
        try {
            const AudioContext = window.AudioContext || window.webkitAudioContext;
            audioCtx = new AudioContext();
            await applySinkId();
            
            // Preload essential default sounds
            const fetchPromises = defaultSoundsToLoad.map(async (soundName) => {
                await loadSound(soundName);
            });

            await Promise.all(fetchPromises);
            isInitialized = true;
        } catch (e) {
            console.error("[SoundManager] Failed to initialize AudioContext", e);
        }
    }

    function sanitizeSoundName(soundName) {
        if (!soundName) return '';
        let clean = soundName.trim();
        if (clean.endsWith('.wav')) {
            clean = clean.substring(0, clean.length - 4);
        }
        return clean;
    }

    async function loadSound(soundName) {
        const key = sanitizeSoundName(soundName);
        if (!key) return null;
        if (audioBuffers[key]) return audioBuffers[key];

        try {
            if (!audioCtx) {
                const AudioContext = window.AudioContext || window.webkitAudioContext;
                audioCtx = new AudioContext();
            }

            const response = await fetch(`/sounds/${key}.wav`);
            if (!response.ok) throw new Error(`HTTP ${response.status}`);
            const arrayBuffer = await response.arrayBuffer();
            const audioBuffer = await audioCtx.decodeAudioData(arrayBuffer);
            audioBuffers[key] = audioBuffer;
            return audioBuffer;
        } catch (e) {
            console.warn(`[SoundManager] Failed to load sound ${soundName}:`, e);
            return null;
        }
    }

    // Attempt early initialization
    init();

    function stopCallRingtone() {
        if (callTimeout) {
            clearTimeout(callTimeout);
            callTimeout = null;
        }
        if (activeCallSource) {
            try {
                activeCallSource.stop();
            } catch (e) {}
            activeCallSource = null;
        }
    }

    function stopPreview() {
        if (activePreviewSource) {
            try {
                activePreviewSource.stop();
            } catch (e) {}
            activePreviewSource = null;
        }
    }

    // Stop active call ringtone when the user focuses the window or navigates
    window.addEventListener('focus', function() {
        if (activeCallSource) {
            stopCallRingtone();
        }
    });

    return {
        load: async function(soundName) {
            return await loadSound(soundName);
        },

        play: async function (soundName, isPreview = false) {
            try {
                if (!soundName || soundName === 'system_default') {
                    // System Default stays silent on desktop (handled natively by the OS)
                    return;
                }

                const isNativeMobile = window.Capacitor && window.Capacitor.isNativePlatform && window.Capacitor.isNativePlatform();
                // CRITICAL FIX: Playing Web Audio on mobile WebViews during active WebRTC VoiceChat 
                // forces the OS to switch AudioSession categories and revokes mic permissions.
                // Previews in user settings are safe and permitted on mobile when requested.
                if (isNativeMobile && !isPreview) {
                    return;
                }

                if (!isInitialized || !audioCtx) {
                    await init();
                }

                if (!audioCtx) return;

                if (audioCtx.state === 'suspended') {
                    await audioCtx.resume();
                }

                const key = sanitizeSoundName(soundName);
                let buffer = audioBuffers[key];
                if (!buffer) {
                    buffer = await loadSound(key);
                }

                if (!buffer) {
                    console.warn(`[SoundManager] Sound ${soundName} not loaded or missing.`);
                    return;
                }

                if (isPreview) {
                    stopPreview();
                }

                const isCall = key.includes('ringtone') || key.includes('call');
                if (isCall && !isPreview) {
                    stopCallRingtone();
                }

                const source = audioCtx.createBufferSource();
                source.buffer = buffer;
                source.connect(audioCtx.destination);

                if (isPreview) {
                    activePreviewSource = source;
                    source.onended = function() {
                        if (activePreviewSource === source) activePreviewSource = null;
                    };
                } else if (isCall) {
                    activeCallSource = source;
                    source.onended = function() {
                        if (activeCallSource === source) activeCallSource = null;
                    };
                    // 30 second safety timeout for ringtones
                    callTimeout = setTimeout(() => {
                        stopCallRingtone();
                    }, 30000);
                }

                source.start(0);

            } catch (e) {
                console.warn("[SoundManager] Failed to play sound", e);
            }
        },

        stopCallRingtone: function() {
            stopCallRingtone();
        },

        stopPreview: function() {
            stopPreview();
        },

        stopAll: function() {
            stopCallRingtone();
            stopPreview();
        },

        setSinkId: async function(deviceId) {
            await setSinkId(deviceId);
        },

        getSinkId: function() {
            return currentSinkId;
        }
    };
})();
