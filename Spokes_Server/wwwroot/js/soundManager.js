window.SoundManager = (function() {
    let audioCtx = null;
    let audioBuffers = {};
    let isInitialized = false;
    let activeCallSource = null;
    let activePreviewSource = null;
    let previewAudio = null;
    let callTimeout = null;
    let currentSinkId = '';
    let hasAppliedNonDefaultSink = false;

    const SYNTHETIC_MOBILE_ROUTES = new Set(['earpiece', 'speaker', 'speakerphone', 'headset', 'bluetooth']);

    function isNativeMobilePlatform() {
        if (typeof window === 'undefined') return false;
        if (typeof window.isCapacitorNative === 'function') {
            return Boolean(window.isCapacitorNative());
        }
        return Boolean(window.Capacitor && typeof window.Capacitor.isNativePlatform === 'function' && window.Capacitor.isNativePlatform());
    }

    function isSyntheticMobileRouteId(sinkId) {
        return Boolean(sinkId) && SYNTHETIC_MOBILE_ROUTES.has(String(sinkId).trim().toLowerCase());
    }

    function normalizeWebAudioSinkId(sinkId) {
        if (!sinkId || isNativeMobilePlatform()) return '';
        const clean = String(sinkId).trim();
        if (!clean || clean === 'default' || isSyntheticMobileRouteId(clean)) {
            return '';
        }
        return clean;
    }

    try {
        const storedSinkId = localStorage.getItem('spokes_active_speaker_id') || '';
        if (isSyntheticMobileRouteId(storedSinkId) || (isNativeMobilePlatform() && storedSinkId)) {
            localStorage.removeItem('spokes_active_speaker_id');
            currentSinkId = '';
        } else {
            currentSinkId = normalizeWebAudioSinkId(storedSinkId);
        }
    } catch(e) {}

    const defaultSoundsToLoad = ['spokesnotif1', 'channel_leave'];

    async function applySinkId() {
        if (isNativeMobilePlatform()) return;
        if (audioCtx && typeof audioCtx.setSinkId === 'function') {
            const targetSink = normalizeWebAudioSinkId(currentSinkId);
            if (!targetSink && !hasAppliedNonDefaultSink) {
                return;
            }
            try {
                await audioCtx.setSinkId(targetSink);
                hasAppliedNonDefaultSink = Boolean(targetSink);
            } catch (e) {
                console.warn('[SoundManager] Failed to apply sinkId to audioCtx:', e);
            }
        }
    }

    async function setSinkId(deviceId) {
        if (isNativeMobilePlatform() || isSyntheticMobileRouteId(deviceId)) {
            currentSinkId = '';
            return;
        }
        currentSinkId = normalizeWebAudioSinkId(deviceId);
        await applySinkId();
    }

    function ensureAudioContext(applySink = false) {
        if (!audioCtx) {
            const AudioContext = window.AudioContext || window.webkitAudioContext;
            if (AudioContext) {
                audioCtx = new AudioContext();
                if (applySink) {
                    applySinkId();
                }
            }
        }
        return audioCtx;
    }

    async function init(preloadDefaults = false) {
        if (isInitialized) return;
        
        try {
            ensureAudioContext();
            await applySinkId();
            
            // Preload essential default sounds
            const fetchPromises = (preloadDefaults ? defaultSoundsToLoad : []).map(async (soundName) => {
                await loadSound(soundName);
            });

            await Promise.all(fetchPromises);
            isInitialized = true;
        } catch (e) {
            console.error("[SoundManager] Failed to initialize AudioContext", e);
        }
    }

    const unlockEvents = ['click', 'keydown'];
    let unlockListenersAttached = false;

    function detachUnlockListeners() {
        if (!unlockListenersAttached) return;
        unlockListenersAttached = false;
        unlockEvents.forEach(evt => {
            document.removeEventListener(evt, unlockAudio, { capture: false });
        });
    }

    function unlockAudio() {
        try {
            if (isNativeMobilePlatform()) {
                detachUnlockListeners();
                return;
            }
            const ctx = ensureAudioContext(true);
            if (!ctx) {
                detachUnlockListeners();
                return;
            }
            if (ctx.state === 'running') {
                detachUnlockListeners();
                return;
            }
            if (ctx.state === 'suspended') {
                ctx.resume().then(() => {
                    if (audioCtx && audioCtx.state === 'running') {
                        detachUnlockListeners();
                    }
                }).catch(() => {});
            }
            try {
                const buffer = ctx.createBuffer(1, 1, 22050);
                const source = ctx.createBufferSource();
                source.buffer = buffer;
                source.connect(ctx.destination);
                source.start(0);
            } catch (e) {}
            if (ctx.state === 'running') {
                detachUnlockListeners();
            }
        } catch (e) {}
    }

    // Attach self-removing bubble-phase unlock listeners on Web/Desktop only (avoids blocking pointerdown first-tap LoAF;
    // native mobile initializes WebAudio lazily on explicit playback)
    if (!isNativeMobilePlatform() && typeof document !== 'undefined') {
        unlockListenersAttached = true;
        unlockEvents.forEach(evt => {
            document.addEventListener(evt, unlockAudio, { passive: true, capture: false });
        });
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
            ensureAudioContext();

            const response = await fetch(`/sounds/${key}.wav`);
            if (!response.ok) throw new Error(`HTTP ${response.status}`);
            const arrayBuffer = await response.arrayBuffer();
            if (audioCtx) {
                const audioBuffer = await audioCtx.decodeAudioData(arrayBuffer);
                audioBuffers[key] = audioBuffer;
                return audioBuffer;
            }
            return null;
        } catch (e) {
            console.warn(`[SoundManager] Failed to load sound ${soundName}:`, e);
            return null;
        }
    }

    // Attempt early initialization
    // Lazy-init AudioContext and decode .wav buffers on first playback instead of downloading 622 KB during cold boot.

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
        if (previewAudio) {
            try {
                previewAudio.pause();
                previewAudio.currentTime = 0;
            } catch (e) {}
            previewAudio = null;
        }
    }

    async function playViaHtmlAudio(key, isPreview) {
        try {
            stopPreview();
            const audio = new Audio(`/sounds/${key}.wav`);
            if (isPreview) {
                previewAudio = audio;
                audio.onended = function() {
                    if (previewAudio === audio) previewAudio = null;
                };
            }
            if (!isNativeMobilePlatform() && currentSinkId && typeof audio.setSinkId === 'function') {
                try {
                    await audio.setSinkId(normalizeWebAudioSinkId(currentSinkId));
                } catch(e) {}
            }
            await audio.play();
            return true;
        } catch (e) {
            console.warn('[SoundManager] HTML5 audio fallback failed:', e);
            return false;
        }
    }

    // Stop active call ringtone when the user focuses the window or navigates
    window.addEventListener('focus', function() {
        if (activeCallSource) {
            stopCallRingtone();
        }
    });

    return {
        preloadSounds: async function(soundNames) {
            if (!Array.isArray(soundNames)) return;
            for (const name of soundNames) {
                try {
                    await loadSound(name);
                } catch (e) {}
            }
        },

        unlock: function() {
            unlockAudio();
        },

        play: async function (soundName, isPreview = false) {
            try {
                if (!soundName || soundName === 'system_default') {
                    // System Default stays silent on desktop (handled natively by the OS)
                    return;
                }

                const key = sanitizeSoundName(soundName);
                const isChannelLeave = key === 'channel_leave';
                const isNativeMobile = isNativeMobilePlatform();
                const isMobileDevice = isNativeMobile || /Android|webOS|iPhone|iPad|iPod|BlackBerry|IEMobile|Opera Mini/i.test(navigator.userAgent || '');
                // CRITICAL FIX: Playing Web Audio on mobile WebViews during active WebRTC VoiceChat 
                // forces the OS to switch AudioSession categories and revokes mic permissions.
                // Previews in user settings and channel leave sounds (after WebRTC disconnect) are safe and permitted on mobile.
                if ((isNativeMobile || (isMobileDevice && window._spokesInVoiceCall)) && !isPreview && !isChannelLeave) {
                    return;
                }

                if (!isInitialized || !audioCtx) {
                    await init();
                }

                if (audioCtx && audioCtx.state === 'suspended') {
                    try {
                        await audioCtx.resume();
                    } catch (e) {}
                }

                if (isPreview) {
                    stopPreview();
                }

                const isCall = key.includes('ringtone') || key.includes('call');
                if (isCall && !isPreview) {
                    stopCallRingtone();
                }

                // If audioCtx is available and running, attempt Web Audio playback
                if (audioCtx) {
                    let buffer = audioBuffers[key];
                    if (!buffer) {
                        buffer = await loadSound(key);
                    }

                    if (buffer) {
                        try {
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
                            return;
                        } catch (e) {
                            console.warn("[SoundManager] WebAudio playback failed, attempting HTML5 fallback:", e);
                        }
                    }
                }

                // Fallback for iOS / suspended AudioContext / missing buffer / preview
                await playViaHtmlAudio(key, isPreview);

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

        setSinkId: async function(deviceId) {
            await setSinkId(deviceId);
        }
    };
})();
