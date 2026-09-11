import { NoiseSuppressionManager } from './noiseSuppressionManager.js';

let currentRoom = null;
let dotnetHelper = null;
let globalScreenTracks = [];
let noiseManager = new NoiseSuppressionManager();
let currentNoiseSuppression = 'webrtc';

// iOS adaptive resolution state — dynamically scales capture resolution based on connection quality
let _iosAdaptiveEnabled = false;
let _iosCurrentTier = null;         // 'low' (360p) | 'mid' (720p) | 'high' (1080p)
let _iosAdaptiveDebounce = null;    // debounce timer to prevent rapid resolution flapping
let _iosTargetCeiling = null;       // user's selected quality as the max tier ('low' | 'mid' | 'high')
// --- Initialize local iOS native plugin lazily ---
let _spokesAudioSessionPlugin = null;
function getAudioSessionPlugin() {
    if (!_spokesAudioSessionPlugin && window.Capacitor && window.Capacitor.registerPlugin) {
        _spokesAudioSessionPlugin = window.Capacitor.registerPlugin('SpokesAudioSessionPlugin');
    }
    return _spokesAudioSessionPlugin || (window.Capacitor && window.Capacitor.Plugins && window.Capacitor.Plugins.SpokesAudioSessionPlugin);
}

export function setNoiseSuppressionType(type) {
    currentNoiseSuppression = type;
}

function safeRemove(el) {
    if (!el) return;
    if (el.tagName === 'AUDIO') {
        el.pause();
        el.removeAttribute('src');
        if (el.srcObject) {
            el.srcObject = null;
        }
        el.load();
        el.style.display = 'none';
        try { if (el.parentNode) el.parentNode.removeChild(el); } catch(e) {}
    } else if (el.tagName === 'VIDEO') {
        // DO NOT REMOVE FROM DOM! The element is strictly managed by Blazor.
        el.pause();
        el.removeAttribute('src');
        if (el.srcObject) {
            el.srcObject = null;
        }
        el.load();
        el.style.opacity = '0';
        el.style.pointerEvents = 'none';
        el.classList.remove('has-stream');
    }
}

// Toggle to true to capture client-side browser logs and output them to the C# server console
const ENABLE_TELEMETRY = false;

export function isAppleMobileDevice() {
    if (window.Capacitor && window.Capacitor.isNativePlatform() && window.Capacitor.getPlatform() === 'ios') return true;
    return Boolean(/iPhone|iPad|iPod/i.test(navigator.userAgent) || (navigator.userAgent.includes("Mac") && "ontouchend" in document));
}

let _androidHasBluetooth = false;
let _androidInitialRouteSet = false;
let _globalAudioContext = null;
let _activeGainNodes = new Map();

function applyMobileVolumeBoost(element, trackSid, mediaStreamTrack) {
    if (!window.Capacitor || !window.Capacitor.isNativePlatform()) return;
    let platform = window.Capacitor.getPlatform();
    if (platform !== 'android' && platform !== 'ios') return;
    if (!window.AudioContext && !window.webkitAudioContext) return;
    
    if (!_globalAudioContext) {
        _globalAudioContext = new (window.AudioContext || window.webkitAudioContext)();
    }
    
    if (_globalAudioContext.state === 'suspended') {
        _globalAudioContext.resume();
    }
    
    try {
        const stream = new MediaStream([mediaStreamTrack]);
        const source = _globalAudioContext.createMediaStreamSource(stream);
        const gainNode = _globalAudioContext.createGain();
        gainNode.gain.value = 3.0; // Boost by approx 9.5 dB
        
        source.connect(gainNode);
        gainNode.connect(_globalAudioContext.destination);
        
        element.volume = 0; // Prevent duplicate audio from the original element
        
        _activeGainNodes.set(trackSid, { source, gainNode });
    } catch(e) {
        console.warn("[Voice] Failed to apply volume boost", e);
    }
}

function removeMobileVolumeBoost(trackSid) {
    if (_activeGainNodes.has(trackSid)) {
        const nodes = _activeGainNodes.get(trackSid);
        try {
            nodes.source.disconnect();
            nodes.gainNode.disconnect();
        } catch(e) {}
        _activeGainNodes.delete(trackSid);
    }
}

export async function connectToVoice(token, url, helper, audioBitrateKbps = 64, noiseSuppression = 'webrtc') {
    dotnetHelper = helper;
    currentNoiseSuppression = noiseSuppression;
    
    // Wire up detailed console intercept to funnel logs to the C# backend
    if (ENABLE_TELEMETRY && !window._voiceLoggerPatched) {
        window._voiceLoggerPatched = true;
        ['debug', 'log', 'info', 'warn', 'error'].forEach(level => {
            const original = console[level];
            if (!original) return;
            console[level] = function() {
                original.apply(console, arguments);
                if (dotnetHelper) {
                    try {
                        let msg = Array.from(arguments).map(a => {
                            if (a instanceof Error) return `${a.name}: ${a.message}\n${a.stack}`;
                            if (typeof a === 'object') {
                                try { return JSON.stringify(a); } catch(e) { return String(a); }
                            }
                            return String(a);
                        }).join(' ');
                        
                        // Prevent giant JSON objects from blowing out the signalR connection
                        if (msg.length > 3000) msg = msg.substring(0, 3000) + '...[truncated]';
                        
                        dotnetHelper.invokeMethodAsync('LogToServer', level.toUpperCase(), msg).catch(()=>{});
                    } catch(e) {}
                }
            };
        });
    }
    
    // Check if LivekitClient is available
    if (!window.LivekitClient) {
        console.error('[Voice] LivekitClient not found! Ensure the CDN script is loaded.');
        return false;
    }

    if (ENABLE_TELEMETRY) {
        // Enable verbose debug logging to trace the exact network failure
        window.LivekitClient.setLogLevel(window.LivekitClient.LogLevel.debug);
    }



    try {
        currentRoom = new window.LivekitClient.Room({
            adaptiveStream: true,
            dynacast: true,
            publishDefaults: {
                audioBitrate: audioBitrateKbps * 1000,
                audioPreset: { maxBitrate: audioBitrateKbps * 1000 }
            }
        });

        // Setup event listeners for the specific events you'll need
        currentRoom
            .on(window.LivekitClient.RoomEvent.TrackSubscribed, handleTrackSubscribed)
            .on(window.LivekitClient.RoomEvent.TrackUnsubscribed, handleTrackUnsubscribed)
            .on(window.LivekitClient.RoomEvent.ActiveSpeakersChanged, handleActiveSpeakers)
            .on(window.LivekitClient.RoomEvent.Disconnected, handleDisconnect)
            .on(window.LivekitClient.RoomEvent.LocalTrackPublished, handleLocalTrackPublished)
            .on(window.LivekitClient.RoomEvent.LocalTrackUnpublished, handleLocalUnpublished)
            .on(window.LivekitClient.RoomEvent.ParticipantConnected, handleParticipantConnected)
            .on(window.LivekitClient.RoomEvent.ParticipantDisconnected, handleParticipantDisconnected)
            .on(window.LivekitClient.RoomEvent.TrackMuted, handleTrackMuted)
            .on(window.LivekitClient.RoomEvent.TrackUnmuted, handleTrackUnmuted);

        await currentRoom.connect(url, token);
        console.log('[Voice] Connected to room:', currentRoom.name);

        // --- Capacitor Native Mobile Fixes ---
        if (window.Capacitor && window.Capacitor.isNativePlatform()) {
            try {
                // Prevent screen lock (fixes background audio dropping)
                if (window.Capacitor.Plugins.KeepAwake) {
                    await window.Capacitor.Plugins.KeepAwake.keepAwake();
                }
                
                let platform = window.Capacitor.getPlatform();
                let audioPlugin = getAudioSessionPlugin();
                if (platform === 'ios' && audioPlugin) {
                    let res = await audioPlugin.configureVoiceChat({ enabled: true });
                    console.info(`[Voice] iOS Native Session Initialized: Category=${res.category}, Mode=${res.mode}, Output=${res.activeOutput}`);
                } else if (platform === 'ios') {
                    console.warn(`[Voice] iOS Native Session plugin NOT FOUND`);
                }
                
                if (platform === 'android' && window.Capacitor.Plugins.TelecomConnectionPlugin) {
                    _androidInitialRouteSet = false;
                    _activeNativeSpeakerId = 'speaker';
                    window.Capacitor.Plugins.TelecomConnectionPlugin.addListener('onAudioStateChanged', (data) => {
                        if (data && data.state) {
                            if (typeof data.state.hasBluetooth === 'boolean') _androidHasBluetooth = data.state.hasBluetooth;

                            if (!_androidInitialRouteSet) {
                                // First callback signals the telecom session is ready.
                                // Apply the desired default route (speaker) now.
                                _androidInitialRouteSet = true;
                                window.Capacitor.Plugins.TelecomConnectionPlugin.setAudioRoute({ route: 'speaker' })
                                    .then(() => {
                                        _activeNativeSpeakerId = 'speaker';
                                        updateAudioDevices().catch(()=>{});
                                    })
                                    .catch(() => {
                                        // Route set failed, accept whatever Android reports
                                        if (data.state.route) _activeNativeSpeakerId = data.state.route;
                                        updateAudioDevices().catch(()=>{});
                                    });
                                return;
                            }

                            // Subsequent callbacks: accept the reported route (user or system initiated)
                            if (data.state.route) _activeNativeSpeakerId = data.state.route;
                            updateAudioDevices().catch(()=>{});
                        }
                    });
                    await window.Capacitor.Plugins.TelecomConnectionPlugin.startCall();
                }
            } catch (e) {
                console.warn('[Voice] Capacitor native plugins error on connect:', e);
            }
        }

        if (dotnetHelper) {
            let existingParticipants = [];
            if (currentRoom.remoteParticipants) {
                if (typeof currentRoom.remoteParticipants.values === 'function') {
                    existingParticipants = Array.from(currentRoom.remoteParticipants.values()).map(p => p.identity);
                } else {
                    existingParticipants = Object.values(currentRoom.remoteParticipants).map(p => p.identity);
                }
            }
            try {
                dotnetHelper.invokeMethodAsync('OnRoomConnected', existingParticipants);
            } catch(e) {}
        }

        // Turn on microphone by default when joining a voice channel
        // Executed asynchronously to prevent the permission prompt from blocking the UI render
        setMicrophoneEnabled(true).then(() => {
            updateAudioDevices().catch(e => console.warn('[Voice] Failed to update audio devices:', e));
        }).catch(e => console.warn('[Voice] Failed to enable microphone:', e));

        return true;
    } catch (error) {
        console.error('[Voice] Connection failed:', error);
        return false;
    }
}

export async function disconnectFromVoice() {
    if (currentRoom) {
        await currentRoom.disconnect();
        currentRoom = null;
        
        // --- Capacitor Native Release ---
        if (window.Capacitor && window.Capacitor.isNativePlatform()) {
            _activeNativeSpeakerId = 'default';
            try {
                if (window.Capacitor.Plugins.KeepAwake) {
                    await window.Capacitor.Plugins.KeepAwake.allowSleep();
                }
                
                let platform = window.Capacitor.getPlatform();
                let audioPlugin = getAudioSessionPlugin();
                if (platform === 'ios' && audioPlugin) {
                    let res = await audioPlugin.configureVoiceChat({ enabled: false });
                    console.info(`[Voice] iOS Native Session Released: Category=${res.category}, Mode=${res.mode}, Output=${res.activeOutput}`);
                }
                
                if (platform === 'android' && window.Capacitor.Plugins.TelecomConnectionPlugin) {
                    _androidInitialRouteSet = false;
                    window.Capacitor.Plugins.TelecomConnectionPlugin.removeAllListeners();
                    await window.Capacitor.Plugins.TelecomConnectionPlugin.endCall();
                }
            } catch (e) {
                console.warn('[Voice] Capacitor native release error:', e);
            }
        }
    }
}

export async function setMicrophoneEnabled(enabled) {
    if (!currentRoom || !currentRoom.localParticipant) return;

    try {
        if (!enabled) {
            // Unpublish existing tracks (and destroy processors implicitly via LiveKit)
            let tracks = currentRoom.localParticipant.audioTrackPublications;
            if (tracks) {
                let pubArr = typeof tracks.values === 'function' ? Array.from(tracks.values()) : Object.values(tracks);
                for (let p of pubArr) {
                    if (p.track) {
                        await currentRoom.localParticipant.unpublishTrack(p.track, true);
                    }
                }
            }
            return;
        }

        let isIOS = window.Capacitor && window.Capacitor.isNativePlatform() && window.Capacitor.getPlatform() === 'ios';

        if (currentNoiseSuppression === 'webrtc') {
            if (isIOS) {
                // For "Basic" on iOS, enable hardware AEC (echoCancellation) to keep Voice Processing I/O active, but disable software double-processing
                let trackOptions = {
                    echoCancellation: true, 
                    noiseSuppression: false,
                    autoGainControl: false
                };
                console.info(`[Voice] Publishing iOS Basic Track: echo=${trackOptions.echoCancellation}, ns=${trackOptions.noiseSuppression}, agc=${trackOptions.autoGainControl}`);
                let track = await window.LivekitClient.createLocalAudioTrack(trackOptions);
                await currentRoom.localParticipant.publishTrack(track);
            } else {
                console.info(`[Voice] Publishing Standard WebRTC Track`);
                await currentRoom.localParticipant.setMicrophoneEnabled(true);
            }
            return;
        }

        if (currentNoiseSuppression === 'none') {
            if (isIOS) {
                // For "None" on iOS, disable hardware AEC completely to allow raw mic input
                let trackOptions = {
                    echoCancellation: false, 
                    noiseSuppression: false,
                    autoGainControl: false
                };
                console.info(`[Voice] Publishing iOS Raw Track (None): echo=${trackOptions.echoCancellation}, ns=${trackOptions.noiseSuppression}, agc=${trackOptions.autoGainControl}`);
                let track = await window.LivekitClient.createLocalAudioTrack(trackOptions);
                await currentRoom.localParticipant.publishTrack(track);
            } else {
                let trackOptions = { echoCancellation: false, noiseSuppression: false, autoGainControl: false };
                console.info(`[Voice] Publishing Standard Raw Track`);
                let track = await window.LivekitClient.createLocalAudioTrack(trackOptions);
                await currentRoom.localParticipant.publishTrack(track);
            }
            return;
        }

        // --- AI Filtering Logic ---
        // iOS does not support AudioWorklet-based processors (rnnoise, deepfilternet).
        // Apple's hardware Voice Processing I/O provides built-in noise suppression via echoCancellation.
        // This follows the same platform branching pattern used above for 'webrtc' and 'none' modes.
        if (isIOS) {
            let trackOptions = {
                echoCancellation: true,
                noiseSuppression: false,
                autoGainControl: false
            };
            console.info(`[Voice] iOS: AI filter "${currentNoiseSuppression}" not supported, using hardware noise filter. echo=${trackOptions.echoCancellation}`);
            let track = await window.LivekitClient.createLocalAudioTrack(trackOptions);
            await currentRoom.localParticipant.publishTrack(track);
            return;
        }

        let processor = null;
        try {
            processor = await noiseManager.getProcessor(currentNoiseSuppression);
            if (processor && currentNoiseSuppression !== 'none' && dotnetHelper) {
                dotnetHelper.invokeMethodAsync('OnVoiceLog', `AI Filter (${currentNoiseSuppression}) successfully initialized.`, "success").catch(()=>{});
            }
        } catch (err) {
            console.error('[Voice] Failed to load AI processor:', err);
            if (dotnetHelper) {
                dotnetHelper.invokeMethodAsync('OnVoiceLog', `AI Filter Failed: ${err.message}. Falling back to WebRTC.`, "error").catch(()=>{});
            }
            // Fallback to webrtc on failure
            await currentRoom.localParticipant.setMicrophoneEnabled(true);
            return;
        }
        
        let trackOptions = {
            echoCancellation: currentNoiseSuppression !== 'none',
            // CRITICAL: Disable native WebRTC noise suppression and AGC if we are using an AI filter. 
            // Running two filters simultaneously destroys acoustic phase and breaks the AI model's ability to profile noise.
            noiseSuppression: currentNoiseSuppression === 'webrtc',
            autoGainControl: currentNoiseSuppression === 'webrtc'
        };
        console.info(`[Voice] Publishing AI Track (${currentNoiseSuppression}): echo=${trackOptions.echoCancellation}, ns=${trackOptions.noiseSuppression}, agc=${trackOptions.autoGainControl}`);
        
        let track = await window.LivekitClient.createLocalAudioTrack(trackOptions);
        
        if (processor) {
            try {
                if (typeof track.setAudioContext === 'function') {
                    const AudioContext = window.AudioContext || window.webkitAudioContext;
                    if (AudioContext) {
                        let ctx = new AudioContext();
                        if (ctx.state === 'suspended') {
                            await ctx.resume().catch(()=>{});
                        }
                        track.setAudioContext(ctx);
                    }
                }
                await track.setProcessor(processor);
            } catch (err) {
                console.error('[Voice] Failed to attach processor to track:', err);
                if (dotnetHelper) {
                    dotnetHelper.invokeMethodAsync('OnVoiceLog', `AI Filter Processing Failed: ${err.message}.`, "error").catch(()=>{});
                }
                
                // CRITICAL FALLBACK: The AI processor failed to attach, which means the track currently has NO noise suppression
                // because we disabled it in trackOptions. We must destroy this raw track and recreate it with standard WebRTC suppression.
                if (track.mediaStreamTrack && typeof track.mediaStreamTrack.stop === 'function') {
                    track.mediaStreamTrack.stop();
                }
                if (typeof track.stop === 'function') track.stop();
                
                trackOptions.noiseSuppression = true;
                trackOptions.autoGainControl = true;
                trackOptions.echoCancellation = true;
                
                track = await window.LivekitClient.createLocalAudioTrack(trackOptions);
            }
        }
        
        await currentRoom.localParticipant.publishTrack(track);
        
    } catch (e) {
        console.error('[Voice] setMicrophoneEnabled failed:', e);
        if (dotnetHelper) {
            dotnetHelper.invokeMethodAsync('OnVoiceLog', `Microphone Error: ${e.message}`, "error").catch(()=>{});
        }
        // Safest fallback to avoid getting stuck without mic
        await currentRoom.localParticipant.setMicrophoneEnabled(true).catch(()=>{});
    }
}

function getCustomPreset(resolutionStr, is60fps) {
    const mult = is60fps ? 2 : 1;

    if (resolutionStr === 'Auto') {
        if (window.LivekitClient && window.LivekitClient.VideoPresets) {
            return window.LivekitClient.VideoPresets.h1080;
        }
        return { width: 1920, height: 1080, maxBitrate: 2000000 * mult };
    }
    
    // Check if device is iOS to apply strict thermal/memory bandwidth constraints
    let isAppleMobile = /iPhone|iPad|iPod/i.test(navigator.userAgent) || (navigator.userAgent.includes("Mac") && "ontouchend" in document);
    
    // High custom bitrates cause Safari's strict hardware encoder to silently crash on simulcast due to memory limits.
    // If it's an iOS device, entirely remove custom bandwidth caps and return the purest native LiveKit default presets.
    if (isAppleMobile && window.LivekitClient && window.LivekitClient.VideoPresets) {
        if (resolutionStr === '2160p') return window.LivekitClient.VideoPresets.h2160;
        if (resolutionStr === '1440p') return window.LivekitClient.VideoPresets.h1440;
        if (resolutionStr === '1080p') return window.LivekitClient.VideoPresets.h1080;
        if (resolutionStr === '720p') return window.LivekitClient.VideoPresets.h720;
        if (resolutionStr === '360p') return window.LivekitClient.VideoPresets.h360;
        return window.LivekitClient.VideoPresets.h1080;
    }

    // Boosted baseline bandwidth parameters for PC/Android higher visual quality
    if (resolutionStr === '2160p') return { width: 3840, height: 2160, maxBitrate: 20000000 * mult };
    if (resolutionStr === '1440p') return { width: 2560, height: 1440, maxBitrate: 12000000 * mult };
    if (resolutionStr === '1080p') return { width: 1920, height: 1080, maxBitrate: 8000000 * mult };
    if (resolutionStr === '720p') return { width: 1280, height: 720, maxBitrate: 4000000 * mult };
    if (resolutionStr === '360p') return { width: 640, height: 360, maxBitrate: 1500000 * mult };
    
    return { width: 1920, height: 1080, maxBitrate: 8000000 * mult }; // default
}

// --- iOS Adaptive Resolution System ---
// Dynamically adjusts capture resolution based on LiveKit connection quality events.
// This compensates for the lack of simulcast on iOS by manually scaling the single
// encoding layer to match available bandwidth.

function _getIosTierForQuality(quality) {
    const CQ = window.LivekitClient.ConnectionQuality;
    if (quality === CQ.Excellent) return 'high';
    if (quality === CQ.Good) return 'mid';
    return 'low'; // Poor or Unknown
}

function _getIosPresetForTier(tier) {
    const presets = window.LivekitClient.VideoPresets;
    if (tier === 'high') return presets.h1080;
    if (tier === 'mid') return presets.h720;
    return presets.h360;
}

function _iosTierRank(tier) {
    if (tier === 'low') return 0;
    if (tier === 'mid') return 1;
    if (tier === 'high') return 2;
    return 0;
}

function _resolutionCeilingToTier(resolutionStr) {
    if (resolutionStr === '360p') return 'low';
    if (resolutionStr === '720p') return 'mid';
    // 1080p, 1440p, 2160p, Auto — all allow up to 1080p (iOS hardware ceiling)
    return 'high';
}

function _clampTierToCeiling(tier, ceiling) {
    if (_iosTierRank(tier) > _iosTierRank(ceiling)) return ceiling;
    return tier;
}

async function _applyIosTierChange(newTier) {
    if (!currentRoom || !currentRoom.localParticipant) return;

    let pubs = currentRoom.localParticipant.videoTrackPublications;
    if (!pubs) return;
    let pubArr = typeof pubs.values === 'function' ? Array.from(pubs.values()) : Object.values(pubs);
    let cameraPub = pubArr.find(p => p.source === window.LivekitClient.Track.Source.Camera);
    if (!cameraPub || !cameraPub.track) return;

    let actualTrack = cameraPub.videoTrack || cameraPub.track;
    let preset = _getIosPresetForTier(newTier);

    try {
        if (typeof actualTrack.restartTrack === 'function') {
            await actualTrack.restartTrack({ resolution: preset });
        } else {
            console.warn('[Voice] iOS adaptive: restartTrack not available, skipping resolution change');
            return;
        }
        _iosCurrentTier = newTier;
        console.info(`[Voice] iOS adaptive resolution: successfully switched to ${newTier} tier (${preset.width}x${preset.height})`);
    } catch (e) {
        console.error('[Voice] iOS adaptive resolution: restartTrack failed', e);
    }
}

function _onIosConnectionQualityChanged(quality, participant) {
    // Only react to local participant quality changes
    if (!_iosAdaptiveEnabled || !currentRoom) return;
    if (participant.identity !== currentRoom.localParticipant.identity) return;

    let desiredTier = _getIosTierForQuality(quality);
    desiredTier = _clampTierToCeiling(desiredTier, _iosTargetCeiling);

    if (desiredTier === _iosCurrentTier) return;

    // Debounce: wait 2 seconds at the new quality level before committing to the resolution change.
    // This prevents rapid flapping during transient network jitter.
    if (_iosAdaptiveDebounce) {
        clearTimeout(_iosAdaptiveDebounce);
        _iosAdaptiveDebounce = null;
    }

    let fromTier = _iosCurrentTier;
    console.info(`[Voice] iOS adaptive resolution: network quality event fired (quality=${quality}). Scheduling transition: ${fromTier} -> ${desiredTier} in 2s (debouncing).`);

    _iosAdaptiveDebounce = setTimeout(async () => {
        _iosAdaptiveDebounce = null;
        if (!_iosAdaptiveEnabled) return; // cancelled during debounce
        if (desiredTier === _iosCurrentTier) return; // already changed by another event
        await _applyIosTierChange(desiredTier);
    }, 2000);
}

function _startIosAdaptive(ceilingResolutionStr) {
    if (!currentRoom || !window.LivekitClient) return;
    _iosAdaptiveEnabled = true;
    _iosCurrentTier = 'low';
    _iosTargetCeiling = _resolutionCeilingToTier(ceilingResolutionStr);
    currentRoom.on(window.LivekitClient.RoomEvent.ConnectionQualityChanged, _onIosConnectionQualityChanged);
    console.info(`[Voice] iOS adaptive resolution: system started at 360p. Hardware ceiling: ${_iosTargetCeiling} (${ceilingResolutionStr})`);
}

function _stopIosAdaptive() {
    if (!_iosAdaptiveEnabled) return;
    if (_iosAdaptiveDebounce) {
        clearTimeout(_iosAdaptiveDebounce);
        _iosAdaptiveDebounce = null;
    }
    if (currentRoom) {
        currentRoom.off(window.LivekitClient.RoomEvent.ConnectionQualityChanged, _onIosConnectionQualityChanged);
    }
    _iosAdaptiveEnabled = false;
    _iosCurrentTier = null;
    _iosTargetCeiling = null;
    console.log('[Voice] iOS adaptive resolution: stopped');
}

// --- End iOS Adaptive Resolution System ---


export async function setCameraEnabled(enabled, resolutionStr = '1080p', targetFps = 30, deviceId = null) {
    if (currentRoom && currentRoom.localParticipant) {
        try {
            if (enabled) {
                let exTracks = currentRoom.localParticipant.videoTrackPublications;
                let pubArr = [];
                if (exTracks) {
                    pubArr = typeof exTracks.values === 'function' ? Array.from(exTracks.values()) : Object.values(exTracks);
                }
                let cameraPub = pubArr.find(p => p.source === window.LivekitClient.Track.Source.Camera);

                let is60 = targetFps > 35;
                let targetPreset = getCustomPreset(resolutionStr, is60);
                
                let livekitPreset = targetPreset;
                // Only build a custom VideoPreset for PC/Android where we injected our own maxBitrate POJO.
                // For iOS, targetPreset is a PURE native LiveKit instance, so we must NOT tear it apart into a new wrapper or it loses its internal safety profiles (and throws TypeErrors).
                let isApple = /iPhone|iPad|iPod/i.test(navigator.userAgent) || (navigator.userAgent.includes("Mac") && "ontouchend" in document);
                if (!isApple && resolutionStr !== 'Auto' && window.LivekitClient && window.LivekitClient.VideoPreset) {
                    livekitPreset = new window.LivekitClient.VideoPreset(targetPreset.width, targetPreset.height, targetPreset.maxBitrate, targetFps);
                }

                if (cameraPub && cameraPub.track) {
                    try {
                        // On iOS, a manual resolution change cancels and restarts the adaptive system
                        if (isApple) _stopIosAdaptive();

                        let actualTrack = cameraPub.videoTrack || cameraPub.track;
                        // Natively replaces the hardware stream without breaking DOM attachments or requiring manual timeouts
                        if (typeof actualTrack.restartTrack === 'function') {
                            let restartPreset = isApple ? _getIosPresetForTier('low') : livekitPreset;
                            await actualTrack.restartTrack({ resolution: restartPreset });
                        } else {
                            // Fallback if specific SDK version does not expose restartTrack
                            await currentRoom.localParticipant.unpublishTrack(actualTrack, true);
                            
                            let trackOptions = { resolution: livekitPreset };
                            if (deviceId) trackOptions.deviceId = deviceId;
                            else trackOptions.facingMode = 'user';

                            let track = await window.LivekitClient.createLocalVideoTrack(trackOptions);
                            await currentRoom.localParticipant.publishTrack(track, { 
                                source: window.LivekitClient.Track.Source.Camera, 
                                simulcast: !isApple 
                            });
                        }

                        // Restart adaptive monitoring from the new resolution baseline
                        if (isApple) _startIosAdaptive(resolutionStr);
                    } catch(e) {
                         console.error("[Voice] LiveKit native restartTrack failed, unable to switch camera resolution", e);
                    }
                } else {
                    let iosInitialPreset = isApple ? _getIosPresetForTier('low') : null;
                    let trackOptions = { resolution: isApple ? iosInitialPreset : livekitPreset };
                    if (deviceId) trackOptions.deviceId = deviceId;
                    else trackOptions.facingMode = 'user';

                    let track = await window.LivekitClient.createLocalVideoTrack(trackOptions);

                    // Bypass iOS Safari memory limits by disabling simulcast layers explicitly on iPhones.
                    // iOS starts at 360p and uses the adaptive resolution system to scale up based on connection quality.
                    await currentRoom.localParticipant.publishTrack(track, { 
                        source: window.LivekitClient.Track.Source.Camera, 
                        simulcast: !isApple 
                    });

                    if (isApple) {
                        _startIosAdaptive(resolutionStr);
                    }
                }

                const cameras = await getAvailableCameras();
                const activeId = await getActiveVoiceCameraId();
                return { cameras, activeCameraDeviceId: activeId };

            } else {
                _stopIosAdaptive();
                let tracks = currentRoom.localParticipant.videoTrackPublications;
                if (tracks) {
                    let pubArr = typeof tracks.values === 'function' ? Array.from(tracks.values()) : Object.values(tracks);
                    let cameraPub = pubArr.find(p => p.source === window.LivekitClient.Track.Source.Camera);
                    if (cameraPub && cameraPub.track) {
                        let actualTrack = cameraPub.videoTrack || cameraPub.track;
                        await currentRoom.localParticipant.unpublishTrack(actualTrack, true);
                    }
                }
            }
        } catch (error) {
            console.error('[VoiceInterop] Error in setCameraEnabled:', error);
            throw error;
        }
    }
}

export async function getAvailableCameras() {
    if (!window.LivekitClient || !currentRoom) return [];
    try {
        let devices = await window.LivekitClient.Room.getLocalDevices('videoinput');
        let distinct = [];
        for (let d of devices) {
            if (d.deviceId === 'default' || d.deviceId === 'communications') continue;
            let exists = distinct.find(x => (d.groupId && x.groupId === d.groupId) || (d.label && x.label === d.label));
            if (!exists) distinct.push({ deviceId: d.deviceId, label: d.label });
        }
        return distinct;
    } catch(e) {
        return [];
    }
}

export async function getActiveVoiceCameraId() {
    if (!currentRoom) return "";
    try {
        if (typeof currentRoom.getActiveDevice === 'function') {
            const deviceId = await currentRoom.getActiveDevice('videoinput');
            if (deviceId) return deviceId;
        }
        if (currentRoom.localParticipant && window.LivekitClient && window.LivekitClient.Track) {
            let tracks = currentRoom.localParticipant.videoTrackPublications;
            if (tracks) {
                let pubArr = typeof tracks.values === 'function' ? Array.from(tracks.values()) : Object.values(tracks);
                let cameraPub = pubArr.find(p => p.source === window.LivekitClient.Track.Source.Camera);
                if (cameraPub && cameraPub.track && cameraPub.track.mediaStreamTrack) {
                    return cameraPub.track.mediaStreamTrack.getSettings().deviceId || "";
                }
            }
        }
    } catch (e) {
        console.warn('[Voice] Failed to get active camera ID:', e);
    }
    return "";
}

export async function switchActiveCamera(deviceId) {
    if (!currentRoom || !currentRoom.localParticipant) return;
    try {
        await currentRoom.switchActiveDevice('videoinput', deviceId);
        let pubArr = typeof currentRoom.localParticipant.videoTrackPublications.values === 'function' ? Array.from(currentRoom.localParticipant.videoTrackPublications.values()) : Object.values(currentRoom.localParticipant.videoTrackPublications);
        let cameraPub = pubArr.find(p => p.source === window.LivekitClient.Track.Source.Camera);
        if (cameraPub && cameraPub.track) handleLocalTrackPublished(cameraPub, currentRoom.localParticipant);
    } catch(e) {
        console.error('[Voice] Failed to switch camera:', e);
    }
}

export async function updateAudioDevices() {
    if (!dotnetHelper) return;
    try {
        const mics = await getAvailableMicrophones();
        const activeMicId = await getActiveMicrophoneId();
        const speakers = await getAvailableSpeakers();
        const activeSpeakerId = await getActiveSpeakerId();
        dotnetHelper.invokeMethodAsync('OnAudioDevicesUpdated', mics, activeMicId, speakers, activeSpeakerId);
    } catch (e) {
        console.warn('[Voice] Failed to update audio devices:', e);
    }
}

export async function getAvailableMicrophones() {
    if (window.Capacitor && window.Capacitor.isNativePlatform()) {
        let platform = window.Capacitor.getPlatform();
        if (platform === 'ios' || platform === 'android') {
            return [];
        }
    }
    
    if (!window.LivekitClient || !currentRoom) return [];
    try {
        let devices = await window.LivekitClient.Room.getLocalDevices('audioinput');
        let distinct = [];
        for (let d of devices) {
            if (d.deviceId === 'default' || d.deviceId === 'communications') continue;
            let exists = distinct.find(x => (d.groupId && x.groupId === d.groupId) || (d.label && x.label === d.label));
            if (!exists) distinct.push({ deviceId: d.deviceId, label: d.label });
        }
        return distinct;
    } catch(e) {
        return [];
    }
}

export async function getActiveMicrophoneId() {
    if (!currentRoom) return "";
    try {
        let activeId = "";
        if (typeof currentRoom.getActiveDevice === 'function') {
            activeId = await currentRoom.getActiveDevice('audioinput');
        }
        if (!activeId && currentRoom.localParticipant && window.LivekitClient && window.LivekitClient.Track) {
            let tracks = currentRoom.localParticipant.audioTrackPublications;
            if (tracks) {
                let pubArr = typeof tracks.values === 'function' ? Array.from(tracks.values()) : Object.values(tracks);
                let micPub = pubArr.find(p => p.source === window.LivekitClient.Track.Source.Microphone);
                if (micPub && micPub.track && micPub.track.mediaStreamTrack) {
                    activeId = micPub.track.mediaStreamTrack.getSettings().deviceId || "";
                }
            }
        }

        if (activeId === 'default' || activeId === 'communications') {
            let devices = await window.LivekitClient.Room.getLocalDevices('audioinput');
            let defaultDevice = devices.find(d => d.deviceId === activeId);
            if (defaultDevice && defaultDevice.groupId) {
                let physicalDevice = devices.find(d => d.groupId === defaultDevice.groupId && d.deviceId !== 'default' && d.deviceId !== 'communications');
                if (physicalDevice) return physicalDevice.deviceId;
            }
        } else if (activeId) {
            return activeId;
        }
    } catch (e) {}
    return "";
}

export async function switchActiveMicrophone(deviceId) {
    if (!currentRoom) return;
    try {
        await currentRoom.switchActiveDevice('audioinput', deviceId);
        await updateAudioDevices();
    } catch(e) {
        console.error('[Voice] Failed to switch microphone:', e);
    }
}

export async function isAndroidDevice() {
    return !!(window.Capacitor && window.Capacitor.isNativePlatform() && window.Capacitor.getPlatform() === 'android');
}

export async function getAvailableSpeakers() {
    if (window.Capacitor && window.Capacitor.isNativePlatform()) {
        let platform = window.Capacitor.getPlatform();
        if (platform === 'ios') {
            return [];
        } else if (platform === 'android') {
            let devices = [];
            devices.push({ deviceId: 'speaker', label: 'Speaker' });
            devices.push({ deviceId: 'earpiece', label: 'Earpiece' });
            
            try {
                if (window.Capacitor.Plugins.TelecomConnectionPlugin) {
                    let res = await window.Capacitor.Plugins.TelecomConnectionPlugin.getAvailableRoutes();
                    if (res && typeof res.hasBluetooth === 'boolean') {
                        _androidHasBluetooth = res.hasBluetooth;
                    }
                }
            } catch(e) {}
            
            if (_androidHasBluetooth) {
                devices.push({ deviceId: 'bluetooth', label: 'Bluetooth' });
            }
            return devices;
        }
    }
    
    if (!window.LivekitClient || !currentRoom) return [];
        try {
            let devices = await window.LivekitClient.Room.getLocalDevices('audiooutput');
            let distinct = [];
            for (let d of devices) {
                if (d.deviceId === 'default' || d.deviceId === 'communications') continue;
                let exists = distinct.find(x => (d.groupId && x.groupId === d.groupId) || (d.label && x.label === d.label));
                if (!exists) distinct.push({ deviceId: d.deviceId, label: d.label });
            }
            return distinct;
        } catch(e) {
            return [];
        }
}

let _activeNativeSpeakerId = 'default';

export async function getActiveSpeakerId() {
    if (window.Capacitor && window.Capacitor.isNativePlatform()) {
        let platform = window.Capacitor.getPlatform();
        if (platform === 'ios' || platform === 'android') {
            return _activeNativeSpeakerId;
        }
    }
    
    if (!currentRoom) return "";
        try {
            let activeId = "";
            if (typeof currentRoom.getActiveDevice === 'function') {
                activeId = await currentRoom.getActiveDevice('audiooutput');
            }
            if (activeId === 'default' || activeId === 'communications') {
                let devices = await window.LivekitClient.Room.getLocalDevices('audiooutput');
                let defaultDevice = devices.find(d => d.deviceId === activeId);
                if (defaultDevice && defaultDevice.groupId) {
                    let physicalDevice = devices.find(d => d.groupId === defaultDevice.groupId && d.deviceId !== 'default' && d.deviceId !== 'communications');
                    if (physicalDevice) return physicalDevice.deviceId;
                }
            } else if (activeId) {
                return activeId;
            }
        } catch (e) {}
        return "";
}

export async function switchActiveSpeaker(deviceId) {
    if (window.Capacitor && window.Capacitor.isNativePlatform()) {
        let platform = window.Capacitor.getPlatform();
        try {
            if (platform === 'ios') {
                let audioPlugin = getAudioSessionPlugin();
                if (audioPlugin) {
                    let targetRoute = deviceId === 'speakerphone' || deviceId === 'speaker' ? 'speaker' : 'earpiece';
                    let res = await audioPlugin.setAudioRoute({ route: targetRoute });
                    console.info(`[Voice] iOS Native Route Set (${targetRoute}): Category=${res.category}, Mode=${res.mode}, Output=${res.activeOutput}`);
                } else {
                    console.warn(`[Voice] iOS Native Route Set failed: plugin not found`);
                }
            } else if (platform === 'android') {
                if (window.Capacitor.Plugins.TelecomConnectionPlugin) {
                    await window.Capacitor.Plugins.TelecomConnectionPlugin.setAudioRoute({ route: deviceId });
                }
            }
            _activeNativeSpeakerId = deviceId;
            await updateAudioDevices();
        } catch (e) {
            console.error('[Voice] Failed to switch native speaker:', e);
        }
    } else {
        if (!currentRoom) return;
        try {
            await currentRoom.switchActiveDevice('audiooutput', deviceId);
            await updateAudioDevices();
        } catch(e) {
            console.error('[Voice] Failed to switch speaker:', e);
        }
    }
}

export async function setScreenShareEnabled(enabled, resolutionStr = '1080p', targetFps = 30) {
    if (currentRoom && currentRoom.localParticipant) {
        try {
            if (enabled) {
                let exTracks = currentRoom.localParticipant.videoTrackPublications;
                if (exTracks) {
                    let pubArr = typeof exTracks.values === 'function' ? Array.from(exTracks.values()) : Object.values(exTracks);
                    let screenPub = pubArr.find(p => p.source === window.LivekitClient.Track.Source.ScreenShare);
                    if (screenPub && screenPub.track) await currentRoom.localParticipant.unpublishTrack(screenPub.track, true);
                }

                let is60 = targetFps > 35;
                let targetPreset = getCustomPreset(resolutionStr, is60);
                let probePreset = getCustomPreset('2160p', is60);

                let trackRes = await window.LivekitClient.createLocalScreenTracks({
                    resolution: probePreset,
                    audio: false
                });
                let track = Array.isArray(trackRes) ? (trackRes.find(t => t.kind === 'video') || trackRes[0]) : trackRes;
                globalScreenTracks = Array.isArray(trackRes) ? trackRes : [trackRes];

                if (is60 && track && track.mediaStreamTrack) {
                    track.mediaStreamTrack.contentHint = "motion";
                }

                let publishOptions = { 
                    source: window.LivekitClient.Track.Source.ScreenShare,
                    simulcast: true
                };

                if (resolutionStr !== 'Auto') {
                    publishOptions.videoEncoding = {
                        maxBitrate: targetPreset.maxBitrate,
                        maxFramerate: targetFps
                    };
                }

                // With videoEncoding specified, LiveKit enforces the maximum payload natively, scaling internally if the screen is smaller.
                await currentRoom.localParticipant.publishTrack(track, publishOptions);
            } else {
                if (globalScreenTracks.length > 0) {
                    for (let t of globalScreenTracks) {
                        if (t.mediaStreamTrack && typeof t.mediaStreamTrack.stop === 'function') {
                            t.mediaStreamTrack.stop();
                        }
                        if (typeof t.stop === 'function') t.stop();
                        try { await currentRoom.localParticipant.unpublishTrack(t, true); } catch(e) {}
                    }
                    globalScreenTracks = [];
                } else {
                    let tracks = currentRoom.localParticipant.videoTrackPublications;
                    if (tracks) {
                        let pubArr = typeof tracks.values === 'function' ? Array.from(tracks.values()) : Object.values(tracks);
                        let screenPubs = pubArr.filter(p => p.source === window.LivekitClient.Track.Source.ScreenShare || p.source === window.LivekitClient.Track.Source.ScreenShareAudio);
                        for (let p of screenPubs) {
                            if (p.track) {
                                if (p.track.mediaStreamTrack && typeof p.track.mediaStreamTrack.stop === 'function') p.track.mediaStreamTrack.stop(); 
                                if (typeof p.track.stop === 'function') p.track.stop();
                                await currentRoom.localParticipant.unpublishTrack(p.track, true);
                            }
                        }
                    }
                }
            }
        } catch (error) {
            console.error('[VoiceInterop] Error in setScreenShareEnabled:', error);
            throw error;
        }
    }
}

// --- Event Handlers ---

function waitForElement(id, timeoutMs = 5000) {
    return new Promise((resolve, reject) => {
        const el = document.getElementById(id);
        if (el) {
            resolve(el);
            return;
        }

        const observer = new MutationObserver((mutations, obs) => {
            const el = document.getElementById(id);
            if (el) {
                obs.disconnect();
                clearTimeout(timeout);
                resolve(el);
            } else if (!currentRoom) {
                obs.disconnect();
                clearTimeout(timeout);
                reject(new Error(`Aborted waiting for ${id} due to room disconnection`));
            }
        });

        observer.observe(document.body, {
            childList: true,
            subtree: true
        });

        const timeout = setTimeout(() => {
            observer.disconnect();
            reject(new Error(`Timeout waiting for element: ${id}`));
        }, timeoutMs);
    });
}

function attachTrack(track, participant) {
    const isAudio = track.kind === window.LivekitClient.Track.Kind.Audio;
    const isVideo = track.kind === window.LivekitClient.Track.Kind.Video;
    
    if (isAudio || isVideo) {
        const targetId = isAudio ? `participant-audio-${participant.identity}` : `participant-video-${participant.identity}`;
        
        console.log(`[Voice:Diag] attachTrack called | kind=${track.kind} source=${track.source} sid=${track.sid} participant=${participant.identity} targetId=${targetId}`);
        
        waitForElement(targetId).then((targetEl) => {
            if (!currentRoom) {
                console.warn(`[Voice:Diag] Aborted: no currentRoom`);
                return;
            }
            
            if (isAudio) {
                // Audio tracks use empty <div> containers rendered by Blazor. LiveKit creates the <audio> element.
                let element;
                if (track.attachedElements && track.attachedElements.length > 0) {
                    element = track.attachedElements[0];
                    if (element.parentElement !== targetEl) {
                        targetEl.appendChild(element);
                    }
                } else {
                    element = track.attach();
                    targetEl.appendChild(element);
                }
                applyMobileVolumeBoost(element, track.sid, track.mediaStreamTrack);
            } else if (isVideo) {
                // For video, targetEl is the native <video> tag strictly managed by Blazor.
                // We instruct LiveKit to attach the media stream directly to this stable DOM node.
                targetEl.style.opacity = '1';
                targetEl.style.pointerEvents = 'auto';
                targetEl.classList.add('has-stream');
                
                track.attach(targetEl);
                
                // CRITICAL: iOS Safari ignores HTML 'muted' attribute on dynamic WebRTC streams.
                // It MUST be set via JS property to bypass the simultaneous playback capability block.
                targetEl.muted = true;
                
                // Ensure it plays if the browser blocked autoplay
                if (targetEl.paused) {
                    targetEl.play().catch(e => console.warn("[Voice] Retry Play:", e));
                }
            }
            
            console.log(`[Voice:Diag] DONE attaching ${track.kind}`);
        }).catch((err) => {
            console.warn(`[Voice] Failed to attach track: ${err.message}`);
        });
    }
}


function updateParticipantQualityMax(participant) {
    if (!dotnetHelper || !participant || !participant.identity) return;
    
    let maxOverallDim = 0;
    let maxOverallFps = 30;
    let tracks = participant.videoTrackPublications;
    let hasTracks = false;
    let missingDims = false;
    
    if (tracks) {
        let pubArr = typeof tracks.values === 'function' ? Array.from(tracks.values()) : Object.values(tracks);
        for (let p of pubArr) {
            if (p.isSubscribed && p.track) {
                hasTracks = true;
                
                // Prioritize the concrete video stream settings exactly as received from Livekit SFU
                let w = p.videoDimensions?.width || p.dimensions?.width || 0;
                let h = p.videoDimensions?.height || p.dimensions?.height || 0;
                
                // If the stream hasn't populated dimensions yet, check the native mediaStreamTrack after a delay
                if (w === 0 && h === 0 && p.track.mediaStreamTrack && typeof p.track.mediaStreamTrack.getSettings === 'function') {
                    let settings = p.track.mediaStreamTrack.getSettings();
                    w = settings.width || 0;
                    h = settings.height || 0;
                }
                
                if (w === 0 && h === 0) {
                    missingDims = true; // DO NOT fall back to trackInfo.width because that reflects the raw hardware probe, not the simulcast limit!
                } else {
                    let dim = Math.max(w, h);
                    if (dim > maxOverallDim) maxOverallDim = dim;
                }
                
                let trackFps = 30;
                if (typeof p.track.mediaStreamTrack?.getSettings === 'function') {
                    trackFps = Math.round(p.track.mediaStreamTrack.getSettings().frameRate || 30);
                }
                if (trackFps > maxOverallFps) maxOverallFps = trackFps;
            }
        }
    }
    
    if (hasTracks && missingDims && maxOverallDim === 0) {
        // Retry to fetch actual resolution when keyframe decoding finishes, bypassing the static 4K trackInfo metadata
        setTimeout(() => updateParticipantQualityMax(participant), 1000);
        return;
    }
    
    if (hasTracks && maxOverallDim === 0) return;
    
    try { dotnetHelper.invokeMethodAsync('OnParticipantVideoDimensions', participant.identity, maxOverallDim, maxOverallFps); } catch(e) {}
}

function handleTrackSubscribed(track, publication, participant) {
    attachTrack(track, participant);
    if (track.kind === window.LivekitClient.Track.Kind.Video) {
        updateParticipantQualityMax(participant);
    }
}

function handleTrackUnsubscribed(track, publication, participant) {
    if (track) {
        track.detach().forEach(el => safeRemove(el));
    }
    const sid = track?.sid || publication?.trackSid;
    if (sid) {
        const orphaned = document.getElementById(`media-${sid}`);
        if (orphaned) safeRemove(orphaned);
        removeMobileVolumeBoost(sid);
    }
    
    // Explicit fallback to clear the Blazor-managed video tag, since LiveKit detaches tracks before firing this event
    if (participant && participant.identity) {
        if (!track || track.kind === window.LivekitClient.Track.Kind.Video || (publication && publication.kind === window.LivekitClient.Track.Kind.Video)) {
            const videoEl = document.getElementById(`participant-video-${participant.identity}`);
            if (videoEl && videoEl.tagName === 'VIDEO') {
                safeRemove(videoEl);
            }
        }
    }
    
    if (track && track.kind === window.LivekitClient.Track.Kind.Video) {
        updateParticipantQualityMax(participant);
    }
}

function handleLocalTrackPublished(publication, participant) {
    if (publication.track) {
        // Only attach local VIDEO tracks (camera preview). Never attach local AUDIO —
        // playing your own mic back through speakers causes echo/feedback.
        if (publication.track.kind === window.LivekitClient.Track.Kind.Audio) return;
        attachTrack(publication.track, participant);
        
        if (dotnetHelper) {
            let w = 0, h = 0, fps = 30;
            
            // Robust check leveraging native capabilities (e.g. Discord method)
            if (publication.track.mediaStreamTrack && typeof publication.track.mediaStreamTrack.getCapabilities === 'function') {
                try {
                    let caps = publication.track.mediaStreamTrack.getCapabilities();
                    if (caps.width && caps.width.max) w = caps.width.max;
                    if (caps.height && caps.height.max) h = caps.height.max;
                    if (caps.frameRate && caps.frameRate.max) fps = caps.frameRate.max;
                } catch(e) {}
            }
            
            // Fallback for Safari/Firefox
            if ((w === 0 || h === 0) && publication.track.mediaStreamTrack) {
                let settings = publication.track.mediaStreamTrack.getSettings();
                w = settings.width || 0;
                h = settings.height || 0;
            }
            if (publication.track.mediaStreamTrack && typeof publication.track.mediaStreamTrack.getSettings === 'function') {
                let settings = publication.track.mediaStreamTrack.getSettings();
                if (settings.frameRate) fps = Math.max(fps, Math.round(settings.frameRate));
            }
            
            let maxDim = Math.max(w, h);
            if (maxDim > 0) {
                if (publication.source === window.LivekitClient.Track.Source.ScreenShare) {
                    try { dotnetHelper.invokeMethodAsync('OnLocalScreenDimensions', maxDim, fps); } catch(e) {}
                } else {
                    try { dotnetHelper.invokeMethodAsync('OnLocalVideoDimensions', maxDim, fps); } catch(e) {}
                }
            }
        }
    }
}

let lastSpeakerSids = "";

function handleActiveSpeakers(speakers) {
    if (!dotnetHelper) return;
    
    const activeSidsList = speakers.map(s => s.identity);
    
    // Instantly update the DOM locally for zero latency
    document.querySelectorAll('.participant-rect.is-speaking').forEach(el => {
        if (el.id && el.id.startsWith('participant-rect-')) {
            const sid = el.id.substring(17);
            if (!activeSidsList.includes(sid)) {
                el.classList.remove('is-speaking');
            }
        }
    });

    activeSidsList.forEach(identity => {
        const el = document.getElementById(`participant-rect-${identity}`);
        if (el && !el.classList.contains('is-speaking')) {
            el.classList.add('is-speaking');
        }
    });

    const activeSids = activeSidsList.sort().join(',');
    if (activeSids !== lastSpeakerSids) {
        lastSpeakerSids = activeSids;
        if (dotnetHelper) {
            try { dotnetHelper.invokeMethodAsync('OnActiveSpeakersChanged', activeSidsList); } catch(e) {}
        }
    }
}

function handleDisconnect() {
    console.log('[Voice] Disconnected');
    _stopIosAdaptive();
    
    // Only detach LiveKit-created media elements (tracks we attached via JS).
    // Do NOT clear container.innerHTML — those parent containers are owned by Blazor's
    // render tree, and removing them directly causes "Cannot read properties of null
    // (reading 'removeChild')" when Blazor tries to diff-update the DOM.
    if (currentRoom) {
        // Detach local tracks
        if (currentRoom.localParticipant) {
            const localPubs = typeof currentRoom.localParticipant.tracks?.values === 'function'
                ? Array.from(currentRoom.localParticipant.tracks.values())
                : [];
            localPubs.forEach(pub => {
                if (pub.track) pub.track.detach().forEach(el => safeRemove(el));
            });
            if (currentRoom.localParticipant.identity) {
                const videoEl = document.getElementById(`participant-video-${currentRoom.localParticipant.identity}`);
                if (videoEl && videoEl.tagName === 'VIDEO') safeRemove(videoEl);
            }
        }
        // Detach remote tracks
        if (currentRoom.participants) {
            const remotes = typeof currentRoom.participants.values === 'function'
                ? Array.from(currentRoom.participants.values())
                : Object.values(currentRoom.participants);
            remotes.forEach(p => {
                const pubs = typeof p.tracks?.values === 'function'
                    ? Array.from(p.tracks.values())
                    : [];
                pubs.forEach(pub => {
                    if (pub.track) pub.track.detach().forEach(el => safeRemove(el));
                });
                if (p.identity) {
                    const videoEl = document.getElementById(`participant-video-${p.identity}`);
                    if (videoEl && videoEl.tagName === 'VIDEO') safeRemove(videoEl);
                }
            });
        }
    }
    
    if (dotnetHelper) {
        try {
            dotnetHelper.invokeMethodAsync('OnRoomDisconnected');
        } catch(e) {}
    }
}

function handleLocalUnpublished(publication, participant) {
    if (publication.track) {
        publication.track.detach().forEach(el => safeRemove(el));
    }
    const sid = publication.track?.sid || publication?.trackSid;
    if (sid) {
        const orphaned = document.getElementById(`media-${sid}`);
        if (orphaned) safeRemove(orphaned);
    }
    
    // Aggressive fallback to ensure video tags are wiped so CSS :has(video) unhides the avatar
    if (publication.kind === window.LivekitClient.Track.Kind.Video || (publication.track && publication.track.kind === window.LivekitClient.Track.Kind.Video)) {
        if (participant && participant.identity) {
            const videoEl = document.getElementById(`participant-video-${participant.identity}`);
            if (videoEl && videoEl.tagName === 'VIDEO') {
                safeRemove(videoEl);
            }
        }
    }

    // Fired when the user mutes themselves or stops video
    if (dotnetHelper) {
        try {
            if(publication.kind === window.LivekitClient.Track.Kind.Audio) {
                dotnetHelper.invokeMethodAsync('OnLocalMuted', true);
            }
        } catch(e) {}
    }
}

function handleTrackMuted(publication, participant) {
    if (publication.track) {
        publication.track.detach().forEach(el => safeRemove(el));
    }
    const sid = publication.track?.sid || publication?.trackSid;
    if (sid) {
        const orphaned = document.getElementById(`media-${sid}`);
        if (orphaned) safeRemove(orphaned);
    }
    // Fallback: forcefully clear local video if it's the participant muting
    if (publication.kind === window.LivekitClient.Track.Kind.Video || (publication.track && publication.track.kind === window.LivekitClient.Track.Kind.Video)) {
        if (participant && participant.identity) {
            const videoEl = document.getElementById(`participant-video-${participant.identity}`);
            if (videoEl && videoEl.tagName === 'VIDEO') {
                safeRemove(videoEl);
            }
        }
    }
}

function handleTrackUnmuted(publication, participant) {
    if (publication.track) {
        // Never attach local AUDIO
        if (participant === currentRoom?.localParticipant && publication.track.kind === window.LivekitClient.Track.Kind.Audio) return;
        attachTrack(publication.track, participant);
    }
}



function handleParticipantConnected(participant) {
    if (dotnetHelper) {
        try {
            dotnetHelper.invokeMethodAsync('OnParticipantConnected', participant.identity);
        } catch(e) {}
    }
}

function handleParticipantDisconnected(participant) {
    if (dotnetHelper) {
        try {
             dotnetHelper.invokeMethodAsync('OnParticipantDisconnected', participant.identity);
        } catch(e) {}
    }
}

// Re-attach all active tracks to their DOM containers.
// Called when the Chat component remounts (e.g. after navigating to another channel and back)
// so that existing LiveKit tracks are connected to the freshly created DOM elements.
export async function reattachAllTracks() {
    if (!currentRoom) return;

    function getPublications(participant) {
        if (!participant) return [];
        let pubs = [];
        if (participant.tracks) {
            pubs = pubs.concat(typeof participant.tracks.values === 'function' 
                ? Array.from(participant.tracks.values()) 
                : Object.values(participant.tracks));
        }
        // Deduplicate
        return Array.from(new Set(pubs));
    }

    if (currentRoom.localParticipant) {
        const pubs = getPublications(currentRoom.localParticipant);
        pubs.forEach(pub => {
            if (pub.track) attachTrack(pub.track, currentRoom.localParticipant);
        });
    }

    if (currentRoom.remoteParticipants) {
        let participants = typeof currentRoom.remoteParticipants.values === 'function' 
            ? Array.from(currentRoom.remoteParticipants.values()) 
            : Object.values(currentRoom.remoteParticipants);
        participants.forEach(p => {
            const pubs = getPublications(p);
            pubs.forEach(pub => {
                if (pub.track) attachTrack(pub.track, p);
            });
        });
    }
}

export async function setRemoteVideoQuality(participantIdentity, qualityStr) {
    if (!currentRoom) return;
    
    // Remote participants can be a map or object depending on LiveKit version
    let participant = null;
    if (currentRoom.remoteParticipants) {
        if (typeof currentRoom.remoteParticipants.get === 'function') {
            participant = currentRoom.remoteParticipants.get(participantIdentity);
        } else {
            participant = currentRoom.remoteParticipants[participantIdentity];
        }
    }
    
    if (!participant && currentRoom.remoteParticipants) {
        let participantsArr = typeof currentRoom.remoteParticipants.values === 'function' 
            ? Array.from(currentRoom.remoteParticipants.values()) 
            : Object.values(currentRoom.remoteParticipants);
        participant = participantsArr.find(p => p.identity === participantIdentity);
    }
    
    if (!participant) return;

    let dims = null;
    if (qualityStr === '2160p') dims = { width: 3840, height: 2160 };
    else if (qualityStr === '1440p') dims = { width: 2560, height: 1440 };
    else if (qualityStr === '1080p') dims = { width: 1920, height: 1080 };
    else if (qualityStr === '720p') dims = { width: 1280, height: 720 };
    else if (qualityStr === '360p') dims = { width: 640, height: 360 };

    let getPublications = typeof participant.videoTrackPublications?.values === 'function'
        ? Array.from(participant.videoTrackPublications.values())
        : (participant.videoTrackPublications ? Object.values(participant.videoTrackPublications) : []);
        
    // Fallback if videoTrackPublications is not mapped that way in this SDK version
    if (getPublications.length === 0 && participant.tracks) {
        let allPubs = typeof participant.tracks.values === 'function'
            ? Array.from(participant.tracks.values())
            : Object.values(participant.tracks);
        getPublications = allPubs.filter(p => p.kind === window.LivekitClient.Track.Kind.Video);
    }

    getPublications.forEach((pub) => {
        if (dims) {
            pub.setVideoDimensions(dims);
        } else {
            // Passing a huge dimension effectively turns off manual UI constraints, returning default simulcast control
            pub.setVideoDimensions?.({ width: 4096, height: 2160 });
            pub.setVideoQuality?.(window.LivekitClient.VideoQuality.HIGH);
        }
    });
}

// Force Safari to rebuild WebRTC video pipelines by cycling srcObject
export function refreshVideoLayouts() {
    const isIOS = /iP(hone|ad|od)|Mac OS X/.test(navigator.userAgent) && !window.MSStream;
    document.querySelectorAll('video.has-stream').forEach(v => {
        if (isIOS) {
            if (v.srcObject) {
                const stream = v.srcObject;
                v.srcObject = null;
                v.srcObject = stream;
            }
        }
        if (v.paused) {
            v.play().catch(e => console.warn("[Voice] Retry Play:", e));
        }
    });
}

// Release Wake Lock on sudden page exit
function releaseWakeLock() {
    if (window.Capacitor && window.Capacitor.isNativePlatform() && window.Capacitor.Plugins.KeepAwake) {
        window.Capacitor.Plugins.KeepAwake.allowSleep().catch(()=>{});
    }
}
window.addEventListener('beforeunload', releaseWakeLock);
window.addEventListener('pagehide', releaseWakeLock);

export async function isIOSDevice() {
    if (window.Capacitor && window.Capacitor.isNativePlatform()) {
        return window.Capacitor.getPlatform() === 'ios';
    }
    return false;
}
