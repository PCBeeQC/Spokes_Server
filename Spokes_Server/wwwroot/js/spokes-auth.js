/**
 * Shared authentication helper for Spokes API calls.
 * Centralizes mobile Bearer token injection so it's defined once.
 */
window.spokesAuth = window.spokesAuth || {};

window.spokesAuth._getOriginPrefAsync = async function(Prefs, baseKey, currentOrigin, allowLegacyHost) {
    const scopedKey = baseKey + '::' + currentOrigin;
    const res = await Prefs.get({ key: scopedKey });
    if (res && res.value) {
        return res.value;
    }

    // Copy legacy host/global fallbacks into the origin-scoped key without deleting the legacy keys.
    // On newer mobile shells, OriginPreferencesPlugin already isolates this WebView to its own
    // per-origin store so retaining the legacy key carries zero cross-origin risk; on older mobile
    // binaries without OriginPreferencesPlugin, Preferences is shared with switcher.js / index.html
    // and other configured servers, so deleting legacy keys would break unread badges and device IDs.
    if (allowLegacyHost && window.location && window.location.hostname) {
        const legacyHostKey = baseKey + '_' + window.location.hostname;
        const legacyHostRes = await Prefs.get({ key: legacyHostKey });
        if (legacyHostRes && legacyHostRes.value) {
            await Prefs.set({ key: scopedKey, value: legacyHostRes.value }).catch(() => {});
            return legacyHostRes.value;
        }
    }

    const legacyGlobalRes = await Prefs.get({ key: baseKey });
    if (legacyGlobalRes && legacyGlobalRes.value) {
        await Prefs.set({ key: scopedKey, value: legacyGlobalRes.value }).catch(() => {});
        return legacyGlobalRes.value;
    }

    return null;
};

/**
 * Build request headers with mobile auth if running in Capacitor.
 * @param {Object} [extraHeaders] - Additional headers to merge
 *        e.g. { 'Content-Type': 'application/json' }
 * @param {string} [targetHostname] - Optional target hostname, used when fetching cross-origin APIs.
 * @returns {Promise<Object>} Headers object ready for fetch()
 */
window.spokesAuth.getHeaders = async function(extraHeaders, targetHostname) {
    var hdrs = Object.assign({}, extraHeaders || {});
    if (typeof window.isCapacitorNative === 'function' ? window.isCapacitorNative() : Boolean(window.Capacitor?.isNativePlatform?.())) {
        const currentOrigin = window.spokesAuth.getCanonicalOrigin();
        if (!currentOrigin) {
            return hdrs;
        }
        if (targetHostname !== undefined && targetHostname !== null && String(targetHostname).trim() !== '') {
            const targetOrigin = window.spokesAuth.getCanonicalOrigin(targetHostname);
            if (!targetOrigin || targetOrigin !== currentOrigin) {
                return hdrs;
            }
        }
        hdrs['X-Spokes-Client'] = 'mobile';
        try {
            const Prefs = window.Capacitor?.Plugins?.Preferences;
            if (!Prefs) {
                return hdrs;
            }
            const [refreshToken, deviceId] = await Promise.all([
                window.spokesAuth._getOriginPrefAsync(Prefs, 'spokes_refresh', currentOrigin, true),
                window.spokesAuth._getOriginPrefAsync(Prefs, 'spokes_device_id', currentOrigin, false)
            ]);
            if (refreshToken) {
                hdrs['Authorization'] = 'Bearer ' + refreshToken;
            }
            if (deviceId) {
                hdrs['X-Device-Id'] = deviceId;
            }
        } catch (e) {
            console.warn('[SpokesAuth] Failed to read mobile auth token:', e);
        }
    }
    return hdrs;
};

/**
 * Polls /spokesapi/auth/current-session?strict=1 to verify WKWebView has attached the session cookie.
 * @param {number} [maxRetries=20]
 * @param {number} [intervalMs=100]
 * @returns {Promise<boolean>}
 */
window.spokesAuth.waitForCookieSyncAsync = async function(maxRetries, intervalMs) {
    var retries = maxRetries || 20;
    var interval = intervalMs || 100;
    for (var i = 0; i < retries; i++) {
        try {
            var r = await fetch('/spokesapi/auth/current-session?strict=1', { cache: 'no-store' });
            if (r.ok) {
                var text = await r.text();
                if (text.includes('active') || text.includes('sessionId')) {
                    return true;
                }
            }
        } catch (e) {}
        await new Promise(function(res) { setTimeout(res, interval); });
    }
    return false;
};

/**
 * Get the raw refresh token for native HTTP requests (uploads, downloads).
 * Native plugins (OkHttp/URLSession) can't access Capacitor Preferences directly,
 * so JS reads the token and passes it to the native call.
 * @returns {Promise<string|null>} The raw refresh token, or null on desktop/if not stored.
 */
window.spokesAuth.getCanonicalOrigin = function(urlOrHost) {
    try {
        if (urlOrHost === undefined || urlOrHost === null || String(urlOrHost).trim() === '') {
            const current = new URL(window.location.href);
            if (current.protocol === 'http:' || current.protocol === 'https:') {
                return current.origin.toLowerCase();
            }
            return '';
        }
        const raw = String(urlOrHost).trim();
        let parsed;
        if (/^[a-zA-Z][a-zA-Z0-9+.-]*:\/\//.test(raw)) {
            parsed = new URL(raw);
        } else {
            const scheme = (window.location && (window.location.protocol === 'http:' || window.location.protocol === 'https:'))
                ? window.location.protocol
                : 'https:';
            parsed = new URL(scheme + '//' + raw);
        }
        if (parsed.protocol === 'http:' || parsed.protocol === 'https:') {
            return parsed.origin.toLowerCase();
        }
    } catch (e) {}
    return '';
};

window.spokesAuth.setRefreshToken = async function(token, targetOrigin) {
    if (!window.Capacitor?.Plugins?.Preferences) return false;
    const currentOrigin = window.spokesAuth.getCanonicalOrigin();
    const resolvedOrigin = targetOrigin ? window.spokesAuth.getCanonicalOrigin(targetOrigin) : currentOrigin;
    if (!resolvedOrigin) return false;
    if (currentOrigin && resolvedOrigin !== currentOrigin) {
        return false;
    }
    const key = 'spokes_refresh::' + resolvedOrigin;
    if (!token) {
        await window.Capacitor.Plugins.Preferences.remove({ key });
    } else {
        await window.Capacitor.Plugins.Preferences.set({ key, value: token });
    }
    return true;
};

window.spokesAuth.getRefreshToken = async function() {
    if (!window.Capacitor?.isNativePlatform?.()) return null;
    try {
        const Prefs = window.Capacitor.Plugins.Preferences;
        const currentOrigin = window.spokesAuth.getCanonicalOrigin();
        if (!Prefs || !currentOrigin) return null;

        return await window.spokesAuth._getOriginPrefAsync(Prefs, 'spokes_refresh', currentOrigin, true);
    } catch (e) {
        console.warn('[SpokesAuth] Failed to read refresh token:', e);
        return null;
    }
};

/**
 * Generate a random PKCE Code Verifier.
 * @returns {string} 43-128 character base64url encoded random string
 */
window.spokesAuth.generateCodeVerifier = function() {
    const array = new Uint8Array(32);
    window.crypto.getRandomValues(array);
    let base64 = btoa(String.fromCharCode.apply(null, array));
    return base64.replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/, '');
};


/**
 * Generate a PKCE Code Challenge from a Verifier.
 * @param {string} verifier 
 * @returns {Promise<string>} base64url encoded SHA-256 hash
 */
window.spokesAuth.generateCodeChallenge = async function(verifier) {
    const encoder = new TextEncoder();
    const data = encoder.encode(verifier);
    const digest = await window.crypto.subtle.digest('SHA-256', data);
    let base64 = btoa(String.fromCharCode.apply(null, new Uint8Array(digest)));
    return base64.replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/, '');
};

/**
 * Refresh the session using the stored refresh token.
 * Enforces bounded timeouts on Capacitor Preferences and fetch() so post-sleep
 * network/bridge stalls can never leave _refreshPromise permanently pending.
 * @returns {Promise<boolean>} True if successfully refreshed.
 */
window.spokesAuth.refreshSessionAsync = async function() {
    if (!window.Capacitor || !window.Capacitor.Plugins || !window.Capacitor.Plugins.Preferences) {
        return false;
    }

    if (window.spokesAuth._refreshPromise) {
        return window.spokesAuth._refreshPromise;
    }

    const PREFS_TIMEOUT_MS = 3000;
    const REFRESH_FETCH_TIMEOUT_MS = 10000;
    const withTimeout = (promise, ms, fallbackValue) => {
        let timerId = null;
        return Promise.race([
            Promise.resolve(promise).catch(() => fallbackValue),
            new Promise(resolve => {
                timerId = setTimeout(() => resolve(fallbackValue), ms);
            })
        ]).finally(() => {
            if (timerId) clearTimeout(timerId);
        });
    };

    window.spokesAuth._refreshPromise = (async () => {
        try {
            const Prefs = window.Capacitor.Plugins.Preferences;
            const currentOrigin = window.spokesAuth.getCanonicalOrigin();
            if (!currentOrigin) {
                return false;
            }
            const deviceIdKey = 'spokes_device_id::' + currentOrigin;
            const [refreshToken, initialDeviceId] = await Promise.all([
                withTimeout(window.spokesAuth._getOriginPrefAsync(Prefs, 'spokes_refresh', currentOrigin, true), PREFS_TIMEOUT_MS, null),
                withTimeout(window.spokesAuth._getOriginPrefAsync(Prefs, 'spokes_device_id', currentOrigin, false), PREFS_TIMEOUT_MS, null)
            ]);

            if (!refreshToken) {
                return false;
            }

            // Fall back to pushNotifications.getDeviceId() if Preferences device ID is missing
            let deviceId = initialDeviceId || '';
            if (!deviceId || deviceId.startsWith('dev_')) {
                try {
                    if (window.pushNotifications && window.pushNotifications.getDeviceId) {
                        deviceId = await withTimeout(window.pushNotifications.getDeviceId(), PREFS_TIMEOUT_MS, '');
                    }
                } catch(e) {}
            }
            if (!deviceId || deviceId.startsWith('dev_')) {
                try {
                    deviceId = localStorage.getItem('spokes_device_id') || '';
                } catch(e) {}
            }
            if (!deviceId || deviceId.startsWith('dev_')) {
                try {
                    deviceId = crypto.randomUUID ? crypto.randomUUID() : (Math.random().toString(36).substring(2, 15) + Math.random().toString(36).substring(2, 15));
                    Prefs.set({ key: deviceIdKey, value: deviceId }).catch(()=>{});
                    localStorage.setItem('spokes_device_id', deviceId);
                } catch(e) {}
            }
            if (!deviceId) {
                console.warn('[spokesAuth] Device ID not ready; deferring session refresh.');
                return false;
            }

            const isNative = typeof window.isCapacitorNative === 'function' ? window.isCapacitorNative() : Boolean(window.Capacitor?.isNativePlatform?.());

            // A single fetch() is enough: with CapacitorHttp's fetch patch disabled, fetch, navigation
            // and the Blazor WebSocket all share the WebView cookie store, and the session cookie value
            // is stable (server-side ticket store), so no native-jar priming or iframe sync is needed.
            const controller = typeof AbortController === 'function' ? new AbortController() : null;
            const fetchTimer = controller ? setTimeout(() => { try { controller.abort(); } catch (e) {} }, REFRESH_FETCH_TIMEOUT_MS) : null;
            let r;
            try {
                const fetchOpts = {
                    method: 'POST',
                    headers: {
                        'Content-Type': 'application/json',
                        'X-Spokes-Client': isNative ? 'mobile' : 'web'
                    },
                    body: JSON.stringify({
                        refreshToken: refreshToken,
                        deviceId: deviceId
                    }),
                    cache: 'no-store'
                };
                if (controller) {
                    fetchOpts.signal = controller.signal;
                }
                r = await fetch('/spokesapi/auth/refresh', fetchOpts);
            } finally {
                if (fetchTimer) clearTimeout(fetchTimer);
            }

            if (r.ok) {
                try {
                    const data = await r.json();
                    if (window.__spokesSession) {
                        window.__spokesSession.authenticated = true;
                        if (data && data.expiresInSeconds) {
                            // Read by the resume logic in spokes-boot.js to decide whether a refresh is needed before reconnecting.
                            window.__spokesSession.expiresAt = Date.now() + (data.expiresInSeconds * 1000);
                        }
                        if (data && typeof data.userScope === 'string' && data.userScope.length > 0) {
                            window.__spokesSession.userScope = data.userScope;
                        }
                        if (data && typeof data.hasVaultKey === 'boolean') {
                            window.__spokesSession.hasVaultKey = data.hasVaultKey;
                        }
                    }
                    if (window.spokesMediaLoader && typeof window.spokesMediaLoader.reconcileSession === 'function') {
                        window.spokesMediaLoader.reconcileSession();
                    }
                } catch (jsonErr) {
                    console.warn('[spokesAuth] Failed to parse refresh response:', jsonErr);
                }
                return true;
            } else {
                // Only wipe the token if the server explicitly rejected credentials as unauthorized
                if (r.status === 401) {
                    const scopedRefreshKey = 'spokes_refresh::' + currentOrigin;
                    try { await Prefs.remove({ key: scopedRefreshKey }); } catch(e) {}
                    if (window.location && window.location.hostname) {
                        try { await Prefs.remove({ key: 'spokes_refresh_' + window.location.hostname }); } catch(e) {}
                    }
                    try { await Prefs.remove({ key: 'spokes_refresh' }); } catch(e) {}
                } else {
                    console.warn('[spokesAuth] Server returned transient or request error during refresh:', r.status);
                }
                return false;
            }
        } catch (e) {
            console.warn('[spokesAuth] Preferences-based session refresh failed:', e);
            return false;
        } finally {
            window.spokesAuth._refreshPromise = null;
        }
    })();

    return window.spokesAuth._refreshPromise;
};

/**
 * Validate a return URL to prevent Open Redirect and malicious scheme attacks.
 * Mirrors Spokes_Server.Core.Security.RedirectHelper.
 * @param {string|null} returnUrl
 * @param {string} [fallbackUrl='/']
 * @returns {string} Safe local URL
 */
window.spokesAuth.getSafeRedirectUrl = function(returnUrl, fallbackUrl) {
    var fallback = fallbackUrl || '/';
    if (!returnUrl || typeof returnUrl !== 'string') return fallback;
    
    // Explicitly reject protocol-relative URLs
    if (returnUrl.trim().startsWith('//') || returnUrl.trim().startsWith('/\\')) return fallback;

    try {
        var parsed = new URL(returnUrl, window.location.origin);
        var isSafeScheme = parsed.protocol === 'https:' || parsed.protocol === 'http:';
        var isSameOrigin = parsed.origin === window.location.origin;
        
        if (isSafeScheme && isSameOrigin) {
            // Prevent redirecting back to login routes
            const lowerPath = parsed.pathname.toLowerCase();
            if (lowerPath.startsWith('/mobile-login') || lowerPath.startsWith('/sso/login') || lowerPath.startsWith('/session-expired') || lowerPath.startsWith('/authentication/')) {
                return fallback;
            }
            return parsed.pathname + parsed.search + parsed.hash;
        }
    } catch (e) {}
    return fallback;
};
