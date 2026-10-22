/**
 * Spokes Google Cast Sender Interop (CAF v3)
 * Bridges Blazor SlideshowPairDialog and Mobile Remote with Google Cast devices on the local network.
 * Supports both Desktop Web (CAF v3) and Capacitor Native Mobile (via native SpokesCastPlugin).
 */
(function () {
    'use strict';

    const CUSTOM_CHANNEL = 'urn:x-cast:com.spokes.slideshow';
    const DEFAULT_APP_ID = '5AF5A068';

    let castContext = null;
    let currentAppId = DEFAULT_APP_ID;
    let isApiAvailable = false;
    let activeDotNetRef = null;
    let pendingInitPayload = null;
    let activeCastSession = null;
    let attachedSessionId = null;

    function getNativeCastPlugin() {
        if (typeof window !== 'undefined' && window.Capacitor?.isPluginAvailable?.('SpokesCastPlugin')) {
            return window.Capacitor.Plugins.SpokesCastPlugin;
        }
        if (typeof window !== 'undefined' && window.Capacitor?.Plugins?.SpokesCastPlugin) {
            return window.Capacitor.Plugins.SpokesCastPlugin;
        }
        return null;
    }

    function getAutoJoinPolicy() {
        try {
            if (window.chrome && window.chrome.cast && window.chrome.cast.AutoJoinPolicy && window.chrome.cast.AutoJoinPolicy.ORIGIN_SCOPED) {
                return window.chrome.cast.AutoJoinPolicy.ORIGIN_SCOPED;
            }
        } catch (e) {}
        return 'origin_scoped';
    }

    async function waitForCastApi(timeoutMs = 2500) {
        const start = Date.now();
        while (Date.now() - start < timeoutMs) {
            if (window.chrome && window.chrome.cast && window.chrome.cast.SessionRequest && castContext) {
                return true;
            }
            await new Promise(r => setTimeout(r, 100));
        }
        return Boolean(window.chrome && window.chrome.cast && window.chrome.cast.SessionRequest);
    }

    function applyCastOptions(ctx, appId) {
        if (!ctx) return false;
        if (!window.chrome || !window.chrome.cast || !window.chrome.cast.SessionRequest) {
            return false;
        }
        try {
            ctx.setOptions({
                receiverApplicationId: appId || currentAppId,
                autoJoinPolicy: getAutoJoinPolicy()
            });
            return true;
        } catch (e) {
            console.warn('[Spokes Cast] SetOptions note:', e);
            return false;
        }
    }

    window.spokesCast = {
        /**
         * Checks if Google Cast is available in the current environment (Native or Web).
         */
        isAvailable: function () {
            if (getNativeCastPlugin()) return true;
            return isApiAvailable && !!castContext;
        },

        /**
         * Returns whether Google Cast devices were discovered on the local network.
         */
        hasDevices: function () {
            if (getNativeCastPlugin()) return true;
            if (!this.isAvailable()) return false;
            try {
                const state = castContext.getCastState();
                return state !== cast.framework.CastState.NO_DEVICES_AVAILABLE;
            } catch (e) {
                return false;
            }
        },

        /**
         * Initializes the Cast context with the specified App ID and Blazor reference.
         */
        init: async function (appId, dotNetRef) {
            if (appId) currentAppId = appId;
            if (dotNetRef) activeDotNetRef = dotNetRef;

            if (getNativeCastPlugin()) return true;

            await ensureCastSdkLoaded();

            if (isApiAvailable && castContext) {
                applyCastOptions(castContext, currentAppId);
            }

            return this.isAvailable();
        },

        /**
         * Requests the user to choose a Google Cast device and launches the Web Receiver.
         */
        requestCastSession: async function (initPayload, dotNetRef) {
            if (dotNetRef) activeDotNetRef = dotNetRef;
            pendingInitPayload = initPayload;

            if (initPayload && initPayload.appId) {
                currentAppId = initPayload.appId;
            }

            // 1. Capacitor Native Android / iOS Integration
            const nativePlugin = getNativeCastPlugin();
            if (nativePlugin) {
                console.log('[Spokes Cast] Requesting session via native SpokesCastPlugin');

                // Attach native listener as defense-in-depth if not yet attached
                if (!window._spokesNativeCastListenersAttached && typeof nativePlugin.addListener === 'function') {
                    window._spokesNativeCastListenersAttached = true;
                    try {
                        nativePlugin.addListener('castTvReady', async (res) => {
                            console.log('[Spokes Cast Native Event] TV ready:', res);
                            if (activeDotNetRef && res && res.pairingPin) {
                                try {
                                    await activeDotNetRef.invokeMethodAsync('OnCastTvReady', res.sessionId || '', res.pairingPin);
                                } catch (e) {
                                    console.error('[Spokes Cast Native Event] Error invoking OnCastTvReady:', e);
                                }
                            }
                        });
                        nativePlugin.addListener('castAuthFailed', async (err) => {
                            console.warn('[Spokes Cast Native Event] Auth failed:', err);
                            if (activeDotNetRef) {
                                try {
                                    await activeDotNetRef.invokeMethodAsync('OnCastAuthFailed', err?.error || 'LICENSE_REQUIRED', err?.detail || 'Spokes Pass required.');
                                } catch (e) {}
                            }
                        });
                    } catch (e) {
                        console.warn('[Spokes Cast] Failed to attach native listeners:', e);
                    }
                }

                try {
                    const result = await nativePlugin.requestCastSession(initPayload);
                    if (result && result.pairingPin) {
                        console.log('[Spokes Cast Native] Cast session established with PIN:', result.pairingPin);
                        if (activeDotNetRef) {
                            await activeDotNetRef.invokeMethodAsync('OnCastTvReady', result.sessionId || '', result.pairingPin);
                        }
                        return true;
                    }
                    if (result && (result.cancelled || result.canceled)) {
                        console.log('[Spokes Cast Native] User dismissed Cast picker.');
                        return false;
                    }
                    return false;
                } catch (err) {
                    console.warn('[Spokes Cast Native] Failed to request Cast session:', err);
                    const msg = (err && (err.message || err.errorMessage)) || '';
                    if (msg.toLowerCase().includes('cancel') || msg.toLowerCase().includes('dismiss') || msg.toLowerCase().includes('abort')) {
                        return false;
                    }
                    if (activeDotNetRef) {
                        try {
                            await activeDotNetRef.invokeMethodAsync('OnCastAuthFailed', 'NATIVE_ERROR', msg || 'Failed to connect to Google Cast device.');
                        } catch (e) {}
                    }
                    return false;
                }
            }

            // 2. Web Browser Integration (CAF v3)
            await ensureCastSdkLoaded();

            if (!castContext) {
                console.warn('[Spokes Cast] CastContext not available. Browser might not support Cast.');
                if (activeDotNetRef) {
                    try {
                        await activeDotNetRef.invokeMethodAsync('OnCastAuthFailed', 'NOT_SUPPORTED', 'Google Cast is not supported in this browser. Please use the TV PIN pairing code.');
                    } catch (e) {}
                }
                return false;
            }

            // Wait briefly for chrome.cast to be populated by the browser
            await waitForCastApi(2000);

            if (!window.chrome || !window.chrome.cast || !window.chrome.cast.SessionRequest) {
                let reason = 'Google Cast is not available in this browser. Please use Google Chrome or Microsoft Edge, or use the TV PIN pairing code.';
                if (typeof window !== 'undefined' && !window.isSecureContext) {
                    reason = 'Google Cast requires a secure connection (HTTPS or http://localhost). Chrome disables casting over plain HTTP on local network IPs (e.g. 192.168.x.x). Please access the app via https:// or localhost, or use the TV PIN pairing code.';
                }
                console.warn('[Spokes Cast] Cast API unavailable:', reason);
                if (activeDotNetRef) {
                    try {
                        await activeDotNetRef.invokeMethodAsync('OnCastAuthFailed', 'INSECURE_OR_UNSUPPORTED', reason);
                    } catch (e) {}
                }
                return false;
            }

            try {
                const optionsOk = applyCastOptions(castContext, currentAppId);
                if (!optionsOk) {
                    if (activeDotNetRef) {
                        try {
                            await activeDotNetRef.invokeMethodAsync('OnCastAuthFailed', 'OPTIONS_FAILED', 'Failed to configure Google Cast options.');
                        } catch (e) {}
                    }
                    return false;
                }

                // Request user device selection
                const errorCode = await castContext.requestSession();
                if (errorCode) {
                    console.log('[Spokes Cast] Session request cancelled or error:', errorCode);
                    return false;
                }

                // If SESSION_STATE_CHANGED already attached, this is a clean no-op.
                // Otherwise fallback to attachSession if event didn't fire.
                const session = castContext.getCurrentSession();
                if (session && attachedSessionId !== session.getSessionId()) {
                    attachSession(session);
                }
                return true;
            } catch (err) {
                console.warn('[Spokes Cast] Failed to request Cast session:', err);
                if (err && err !== 'cancel') {
                    if (activeDotNetRef) {
                        try {
                            await activeDotNetRef.invokeMethodAsync('OnCastAuthFailed', 'REQUEST_ERROR', err.message || 'Cast connection failed.');
                        } catch (e) {}
                    }
                }
                return false;
            }
        },

        /**
         * Cleanly terminates the active Google Cast presentation session.
         */
        endCastSession: function () {
            const nativePlugin = getNativeCastPlugin();
            if (nativePlugin) {
                try {
                    nativePlugin.endCastSession();
                } catch (e) {}
            }

            try {
                if (activeCastSession) {
                    try {
                        activeCastSession.sendMessage(CUSTOM_CHANNEL, { type: 'END_SESSION' });
                    } catch (e) {}
                }
                if (castContext) {
                    castContext.endCurrentSession(true);
                }
            } catch (err) {
                console.warn('[Spokes Cast] Error ending Cast session:', err);
            } finally {
                activeCastSession = null;
                attachedSessionId = null;
                pendingInitPayload = null;
            }
        }
    };

    function attachSession(session) {
        if (!session) return;
        const sessionId = session.getSessionId();
        if (attachedSessionId === sessionId && activeCastSession) {
            console.log('[Spokes Cast] Already attached to session:', sessionId);
            return;
        }

        attachedSessionId = sessionId;
        activeCastSession = session;
        console.log('[Spokes Cast] Attached to session:', sessionId);

        try {
            session.addMessageListener(CUSTOM_CHANNEL, onReceiverMessage);
        } catch (e) {
            console.warn('[Spokes Cast] Message listener error:', e);
        }

        // Send INIT_SESSION to Chromecast Web Receiver once
        if (pendingInitPayload) {
            const payload = pendingInitPayload;
            pendingInitPayload = null; // Consume payload so it cannot be re-sent
            sendInitSession(session, payload);
        }
    }

    function sendInitSession(session, payload) {
        const msg = {
            type: 'INIT_SESSION',
            serverId: payload.serverId || '',
            serverUrl: payload.serverUrl || window.location.origin,
            licensePayload: payload.licensePayload || null,
            serverFingerprint: payload.serverFingerprint || null,
            trialTicket: payload.trialTicket || null,
            databaseCreationVersion: payload.databaseCreationVersion || null,
            isDemo: Boolean(payload.isDemo)
        };

        console.log('[Spokes Cast] Sending INIT_SESSION to receiver:', msg.serverUrl);
        try {
            session.sendMessage(CUSTOM_CHANNEL, msg);
        } catch (err) {
            console.error('[Spokes Cast] Failed to send INIT_SESSION:', err);
            if (activeDotNetRef) {
                activeDotNetRef.invokeMethodAsync('OnCastAuthFailed', 'SEND_ERROR', 'Failed to communicate with Cast receiver.');
            }
        }
    }

    async function onReceiverMessage(namespace, message) {
        console.log('[Spokes Cast] Received message from TV receiver:', message);
        let data = message;
        if (typeof message === 'string') {
            try {
                data = JSON.parse(message);
            } catch (e) {
                return;
            }
        }

        if (!data || !data.type) return;

        switch (data.type) {
            case 'TV_READY':
                console.log('[Spokes Cast] TV is ready with session:', data.sessionId, 'PIN:', data.pairingPin);
                if (activeDotNetRef && data.pairingPin) {
                    try {
                        await activeDotNetRef.invokeMethodAsync('OnCastTvReady', data.sessionId, data.pairingPin);
                    } catch (e) {
                        console.error('[Spokes Cast] Error invoking OnCastTvReady:', e);
                    }
                }
                break;

            case 'AUTH_FAILED':
                console.warn('[Spokes Cast] Authorization failed on TV:', data.error, data.detail);
                if (activeDotNetRef) {
                    try {
                        await activeDotNetRef.invokeMethodAsync('OnCastAuthFailed', data.error || 'LICENSE_REQUIRED', data.detail || 'Spokes Pass required.');
                    } catch (e) {
                        console.error('[Spokes Cast] Error invoking OnCastAuthFailed:', e);
                    }
                }
                window.spokesCast.endCastSession();
                break;

            default:
                console.log('[Spokes Cast] Unhandled receiver message type:', data.type);
        }
    }

    // Google Cast framework callback (called by cast_sender.js in browser)
    window.__onGCastApiAvailable = function (isAvailable) {
        console.log('[Spokes Cast] Google Cast API available:', isAvailable);
        isApiAvailable = Boolean(isAvailable);
        if (isAvailable && window.cast && window.cast.framework) {
            try {
                castContext = cast.framework.CastContext.getInstance();
                applyCastOptions(castContext, currentAppId);

                castContext.addEventListener(
                    cast.framework.CastContextEventType.SESSION_STATE_CHANGED,
                    (event) => {
                        console.log('[Spokes Cast] Session state changed:', event.sessionState);
                        if (event.sessionState === cast.framework.SessionState.SESSION_STARTED ||
                            event.sessionState === cast.framework.SessionState.SESSION_RESUMED) {
                            attachSession(event.session);
                        } else if (event.sessionState === cast.framework.SessionState.SESSION_ENDED) {
                            activeCastSession = null;
                            attachedSessionId = null;
                            pendingInitPayload = null;
                        }
                    }
                );
            } catch (err) {
                console.warn('[Spokes Cast] Initialization error in __onGCastApiAvailable:', err);
            }
        }

        if (window._resolveCastSdkReady) {
            window._resolveCastSdkReady(isApiAvailable);
            window._resolveCastSdkReady = null;
        }
    };

    let scriptLoadingPromise = null;
    function ensureCastSdkLoaded() {
        if (getNativeCastPlugin()) return Promise.resolve(true);
        if (isApiAvailable && castContext) return Promise.resolve(true);
        if (scriptLoadingPromise) return scriptLoadingPromise;

        scriptLoadingPromise = new Promise((resolve) => {
            if (window.cast && window.cast.framework) {
                isApiAvailable = true;
                castContext = cast.framework.CastContext.getInstance();
                return resolve(true);
            }

            window._resolveCastSdkReady = (avail) => resolve(Boolean(avail));

            const existing = document.querySelector('script[src*="cast_sender.js"]');
            if (existing) {
                setTimeout(() => resolve(window.spokesCast.isAvailable()), 1000);
                return;
            }

            const script = document.createElement('script');
            script.src = 'https://www.gstatic.com/cv/js/sender/v1/cast_sender.js?loadCastFramework=1';
            script.async = true;
            script.defer = true;
            script.onerror = () => {
                console.warn('[Spokes Cast] Google Cast Sender SDK failed to load (offline or blocked).');
                resolve(false);
            };
            document.head.appendChild(script);

            // Fallback timeout if Google callback never fires (e.g. adblocker, unsupported browser)
            setTimeout(() => {
                if (window._resolveCastSdkReady) {
                    window._resolveCastSdkReady(window.spokesCast.isAvailable());
                    window._resolveCastSdkReady = null;
                }
            }, 4000);
        });

        return scriptLoadingPromise;
    }
})();
