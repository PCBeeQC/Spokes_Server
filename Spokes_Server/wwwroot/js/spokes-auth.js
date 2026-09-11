/**
 * Shared authentication helper for Spokes API calls.
 * Centralizes mobile Bearer token injection so it's defined once.
 */
window.spokesAuth = window.spokesAuth || {};

/**
 * Build request headers with mobile auth if running in Capacitor.
 * @param {Object} [extraHeaders] - Additional headers to merge
 *        e.g. { 'Content-Type': 'application/json' }
 * @param {string} [targetHostname] - Optional target hostname, used when fetching cross-origin APIs.
 * @returns {Promise<Object>} Headers object ready for fetch()
 */
window.spokesAuth.getHeaders = async function(extraHeaders, targetHostname) {
    var hdrs = Object.assign({}, extraHeaders || {});
    if (window.Capacitor && window.Capacitor.isNativePlatform
            && window.Capacitor.isNativePlatform()) {
        hdrs['X-Spokes-Client'] = 'mobile';
        try {
            const host = targetHostname || window.location.hostname;
            const refreshKey = 'spokes_refresh_' + host;
            var res = await window.Capacitor.Plugins.Preferences.get({ key: refreshKey });
            
            // Migration path: ONLY for current hostname, never leak primary token to secondary targetHostnames
            if ((!res || !res.value) && (!targetHostname || targetHostname === window.location.hostname)) {
                res = await window.Capacitor.Plugins.Preferences.get({ key: 'spokes_refresh' });
                if (res && res.value) {
                    await window.Capacitor.Plugins.Preferences.set({ key: refreshKey, value: res.value });
                    await window.Capacitor.Plugins.Preferences.remove({ key: 'spokes_refresh' });
                }
            }

            if (res && res.value) {
                hdrs['Authorization'] = 'Bearer ' + res.value;
            }

            // Attach device ID if available
            try {
                var devRes = await window.Capacitor.Plugins.Preferences.get({ key: 'spokes_device_id' });
                if (devRes && devRes.value) {
                    hdrs['X-Device-Id'] = devRes.value;
                }
            } catch (e) {}
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
window.spokesAuth.getRefreshToken = async function() {
    if (!window.Capacitor?.isNativePlatform?.()) return null;
    try {
        const Prefs = window.Capacitor.Plugins.Preferences;
        const refreshKey = 'spokes_refresh_' + window.location.hostname;
        let res = await Prefs.get({ key: refreshKey });
        if (!res || !res.value) {
            res = await Prefs.get({ key: 'spokes_refresh' });
        }
        return res?.value || null;
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
 * @returns {Promise<boolean>} True if successfully refreshed.
 */
window.spokesAuth.refreshSessionAsync = async function() {
    if (!window.Capacitor || !window.Capacitor.Plugins || !window.Capacitor.Plugins.Preferences) {
        return false;
    }

    if (window.spokesAuth._refreshPromise) {
        return window.spokesAuth._refreshPromise;
    }

    window.spokesAuth._refreshPromise = (async () => {
        try {
            const Prefs = window.Capacitor.Plugins.Preferences;
            const refreshKey = 'spokes_refresh_' + window.location.hostname;
            
            let [refreshRes, deviceIdRes] = await Promise.all([
                Prefs.get({ key: refreshKey }),
                Prefs.get({ key: 'spokes_device_id' })
            ]);
            
            // Migration path: if namespaced key is missing, check global key
            if (!refreshRes || !refreshRes.value) {
                refreshRes = await Prefs.get({ key: 'spokes_refresh' });
                if (refreshRes && refreshRes.value) {
                    // Background migration, don't await so we don't block boot
                    Prefs.set({ key: refreshKey, value: refreshRes.value }).catch(()=>{});
                    Prefs.remove({ key: 'spokes_refresh' }).catch(()=>{});
                }
            }
            
            if (!refreshRes || !refreshRes.value) {
                return false;
            }

            // Fall back to pushNotifications.getDeviceId() if Preferences device ID is missing
            let deviceId = (deviceIdRes && deviceIdRes.value) || '';
            if (!deviceId || deviceId.startsWith('dev_')) {
                try {
                    if (window.pushNotifications && window.pushNotifications.getDeviceId) {
                        deviceId = await window.pushNotifications.getDeviceId();
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
                    Prefs.set({ key: 'spokes_device_id', value: deviceId }).catch(()=>{});
                    localStorage.setItem('spokes_device_id', deviceId);
                } catch(e) {}
            }
            if (!deviceId) {
                console.warn('[spokesAuth] Device ID not ready; deferring session refresh.');
                return false;
            }

            const isNative = !!(window.Capacitor && window.Capacitor.isNativePlatform && window.Capacitor.isNativePlatform());

            const r = await fetch('/spokesapi/auth/refresh', {
                method: 'POST',
                headers: {
                    'Content-Type': 'application/json',
                    'X-Spokes-Client': isNative ? 'mobile' : 'web'
                },
                body: JSON.stringify({
                    refreshToken: refreshRes.value,
                    deviceId: deviceId
                }),
                cache: 'no-store'
            });

            if (r.ok) {
                try {
                    const data = await r.json();
                    if (data && data.expiresInSeconds) {
                        const validUntil = Date.now() + (data.expiresInSeconds * 1000);
                        await Promise.all([
                            Prefs.set({ key: ('spokes_session_last_refresh_' + window.location.hostname), value: Date.now().toString() }),
                            Prefs.set({ key: ('spokes_session_valid_until_' + window.location.hostname), value: validUntil.toString() })
                        ]);
                    }
                } catch (jsonErr) {
                    console.warn('[spokesAuth] Failed to parse refresh response or set preferences:', jsonErr);
                }
                return true;
            } else {
                // Only wipe the token if the server explicitly rejected credentials as unauthorized
                if (r.status === 401) {
                    try { await Prefs.remove({ key: refreshKey }); } catch(e) {}
                    try { await Prefs.remove({ key: 'spokes_refresh' }); } catch(e) {}
                    try { await Prefs.remove({ key: ('spokes_session_valid_until_' + window.location.hostname) }); } catch(e) {}
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


