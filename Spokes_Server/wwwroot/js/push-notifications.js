// Web Push Notifications JavaScript interop for Blazor
// Provides functions to manage push notification permissions and subscriptions

window.pushNotifications = {
    _resolvingDesktopDeviceId: false,

    // Get the physical DeviceId from the platform-appropriate store.
    // Native (Capacitor): Uses platform stable ID (ANDROID_ID / iOS Keychain) that survives reinstalls.
    // Desktop (Electron): Uses SpokesBridge.getDeviceId() (desk_<uuid>).
    // Web: localStorage with standard UUID.
    getDeviceId: async function () {
        if (window._spokesCachedDeviceId) return window._spokesCachedDeviceId;

        if (this.isDesktopApp() && window.SpokesBridge && typeof window.SpokesBridge.getDeviceId === 'function' && !this._resolvingDesktopDeviceId) {
            this._resolvingDesktopDeviceId = true;
            try {
                const deskId = await window.SpokesBridge.getDeviceId();
                if (deskId && !deskId.startsWith('dev_')) {
                    localStorage.setItem('spokes_device_id', deskId);
                    window._spokesCachedDeviceId = deskId;
                    return deskId;
                }
            } catch (e) {
                console.warn('[DeviceId] Desktop SpokesBridge.getDeviceId failed, falling back to localStorage', e);
            } finally {
                this._resolvingDesktopDeviceId = false;
            }
        }

        const native = this.isNativePlatform() && window.Capacitor?.Plugins;

        if (native) {
            // 0. Fast-path: Check localStorage to avoid bridge overhead on subsequent calls/boots
            const localCache = localStorage.getItem('spokes_native_device_id');
            if (localCache && !localCache.startsWith('dev_')) {
                window._spokesCachedDeviceId = localCache;
                return localCache;
            }

            const originKeySuffix = (() => {
                if (window.spokesAuth && typeof window.spokesAuth.getCanonicalOrigin === 'function') {
                    return window.spokesAuth.getCanonicalOrigin() || '__local__';
                }
                try {
                    const u = new URL(window.location.href);
                    if (u.protocol === 'http:' || u.protocol === 'https:') {
                        return u.origin.toLowerCase();
                    }
                } catch (e) {}
                return '__local__';
            })();
            const deviceIdPrefKey = 'spokes_device_id::' + originKeySuffix;

            // 1. Try platform-native stable ID (survives reinstalls)
            try {
                const crypto = window.Capacitor.Plugins.NotificationCrypto;
                if (crypto && crypto.getStableDeviceId) {
                    const res = await crypto.getStableDeviceId();
                    if (res && res.deviceId) {
                        // Cache in origin-scoped Preferences for quick access by other code paths
                        try { await window.Capacitor.Plugins.Preferences.set({ key: deviceIdPrefKey, value: res.deviceId }); } catch (e) {}
                        localStorage.setItem('spokes_native_device_id', res.deviceId);
                        window._spokesCachedDeviceId = res.deviceId;
                        return res.deviceId;
                    }
                }
            } catch (e) {
                console.warn('[DeviceId] Native stable ID failed, falling back to Preferences', e);
            }

            // 2. Fall back to Preferences (origin-scoped ID)
            try {
                const res = await window.Capacitor.Plugins.Preferences.get({ key: deviceIdPrefKey });
                if (res && res.value && !res.value.startsWith('dev_')) {
                    localStorage.setItem('spokes_native_device_id', res.value);
                    window._spokesCachedDeviceId = res.value;
                    return res.value;
                }
            } catch (e) {
                console.error('Failed to read deviceId from Preferences', e);
            }

            // 3. Last resort: generate random UUID (should rarely happen)
            var deviceId = this._generateUuid();
            try { await window.Capacitor.Plugins.Preferences.set({ key: deviceIdPrefKey, value: deviceId }); } catch (e) {}
            localStorage.setItem('spokes_native_device_id', deviceId);
            window._spokesCachedDeviceId = deviceId;
            return deviceId;
        }

        // Web: localStorage
        var deviceId = localStorage.getItem('spokes_device_id');
        if (!deviceId || deviceId.startsWith('dev_')) {
            // Generate standard UUID
            deviceId = this._generateUuid();
            localStorage.setItem('spokes_device_id', deviceId);
        }
        return deviceId;
    },

    _generateUuid: function () {
        return crypto.randomUUID ? crypto.randomUUID() : 'xxxxxxxx-xxxx-4xxx-yxxx-xxxxxxxxxxxx'.replace(/[xy]/g, function (c) {
            var r = Math.random() * 16 | 0, v = c == 'x' ? r : (r & 0x3 | 0x8);
            return v.toString(16);
        });
    },

    _buildNativeRelaySubscription: function (endpoint, publicKey) {
        return {
            success: true,
            endpoint: endpoint,
            p256dh: '',
            auth: '',
            publicKey: publicKey,
            subscriptionType: 'NativeRelay'
        };
    },

    // Authoritative hardware keystore public key query for native mobile (Capacitor).
    // Always queries NotificationCrypto.getPublicKey() so localStorage never drifts from AndroidKeyStore / iOS Keychain.
    _getNativePublicKeyInfo: async function (logFn) {
        const log = typeof logFn === 'function' ? logFn : function () {};
        try {
            if (window.Capacitor) {
                let cryptoPlugin = window.Capacitor.Plugins?.NotificationCrypto;
                if (!cryptoPlugin && window.Capacitor.registerPlugin) {
                    cryptoPlugin = window.Capacitor.registerPlugin('NotificationCrypto');
                }
                if (cryptoPlugin && typeof cryptoPlugin.getPublicKey === 'function') {
                    const result = await cryptoPlugin.getPublicKey();
                    if (result && result.publicKey) {
                        localStorage.setItem('spokes_native_push_pubkey', result.publicKey);
                        return {
                            publicKey: result.publicKey,
                            keyDirty: Boolean(result.keyDirty)
                        };
                    }
                } else {
                    log('Native: NotificationCrypto plugin not available.');
                }
            }
        } catch (e) {
            log('Native: Exception getting publicKey: ' + e.message);
        }
        return {
            publicKey: localStorage.getItem('spokes_native_push_pubkey') || '',
            keyDirty: false
        };
    },

    // Report this server's unread count to the pure-native badge coordinator, which sums
    // across all saved servers and updates the OS app icon badge natively.
    updateBadge: async function (count) {
        if (this.isNativePlatform()) {
            const currentOrigin = window.location.origin.replace(/\/$/, '');
            const safeCount = Math.max(0, Number(count) || 0);
            if (window.Capacitor?.Plugins?.NotificationCrypto?.setServerBadgeCount) {
                try {
                    await window.Capacitor.Plugins.NotificationCrypto.setServerBadgeCount({
                        originUrl: currentOrigin,
                        count: safeCount
                    });
                } catch (e) {
                    console.warn('[Push] setServerBadgeCount failed:', e);
                }
            }
        } else if ('setAppBadge' in navigator) {
            // Experimental Web API for PWAs
            try {
                if (count > 0) {
                    await navigator.setAppBadge(count);
                } else {
                    await navigator.clearAppBadge();
                }
            } catch (e) {}
        }
    },

    // Check if running in native mobile Capacitor shell
    isNativePlatform: function () {
        if (window.SpokesBridge && typeof window.SpokesBridge.isMobile !== 'undefined') {
            return Boolean(window.SpokesBridge.isMobile);
        }
        return typeof window.isCapacitorNative === 'function' ? window.isCapacitorNative() : Boolean(window.Capacitor?.isNativePlatform?.());
    },

    // Check if running in native desktop Electron shell
    isDesktopApp: function () {
        // A native mobile platform is never a desktop app
        if (this.isNativePlatform()) {
            return false;
        }
        if (window.SpokesBridge && typeof window.SpokesBridge.isDesktop !== 'undefined') {
            return Boolean(window.SpokesBridge.isDesktop);
        }
        return Boolean(typeof window.isSpokesDesktop !== 'undefined' && window.isSpokesDesktop);
    },

    // Check if the current client platform requires the Spokes Push Relay (Mobile & Desktop apps)
    requiresPushRelay: function () {
        return this.isNativePlatform() || this.isDesktopApp();
    },

    _lastSubscriptionError: null,
    getLastSubscriptionError: function () {
        return this._lastSubscriptionError || null;
    },

    // Check if push notifications are supported
    isSupported: function () {
        if (this.isDesktopApp()) return true;
        if (this.isNativePlatform()) return true;
        return 'serviceWorker' in navigator &&
            'PushManager' in window &&
            'Notification' in window;
    },

    // Get the reason why push notifications are not supported
    getSupportReason: function () {
        if (this.isDesktopApp()) return 'Supported via Spokes Desktop';
        if (this.isNativePlatform()) return 'Supported via Capacitor';
        if (!('serviceWorker' in navigator)) return 'No Service Worker (HTTPS required)';
        if (!('PushManager' in window)) return 'No Push API';
        if (!('Notification' in window)) return 'No Notification API';
        return 'Unknown error';
    },

    // Get current permission status: 'granted', 'denied', or 'default'
    getPermissionStatus: async function () {
        if (!this.isSupported()) return 'unsupported';
        if (this.isDesktopApp()) return 'granted';
        if (this.isNativePlatform()) {
            try {
                const status = await Capacitor.Plugins.FirebaseMessaging.checkPermissions();
                return (status && (status.receive === 'prompt' || status.receive === 'prompt-with-rationale')) ? 'default' : (status?.receive || 'default');
            } catch (e) {
                return 'default';
            }
        }
        return Notification.permission;
    },

    // Retrieve detailed iOS notification settings (authorization status, soundEnabled, alertEnabled, badgeEnabled)
    getNativeSettings: async function () {
        if (this.isNativePlatform() && window.Capacitor?.Plugins?.NotificationCrypto?.getNotificationSettings) {
            try {
                return await window.Capacitor.Plugins.NotificationCrypto.getNotificationSettings();
            } catch (e) {
                console.warn('Failed to get native notification settings', e);
            }
        }
        return null;
    },

    // Open the Spokes app page in iOS Settings
    openAppSettings: async function () {
        if (this.isNativePlatform() && window.Capacitor?.Plugins?.NotificationCrypto?.openAppSettings) {
            try {
                const res = await window.Capacitor.Plugins.NotificationCrypto.openAppSettings();
                return res?.success ?? true;
            } catch (e) {
                console.warn('Failed to open app settings', e);
            }
        }
        return false;
    },

    // Auto-detect device type from User-Agent
    detectDeviceType: function () {
        if (this.isDesktopApp()) {
            return 'Desktop';
        }
        if (this.isNativePlatform()) {
            return 'Mobile';
        }
        var ua = navigator.userAgent || '';
        // Check for mobile/tablet indicators
        if (/Android|webOS|iPhone|iPad|iPod|BlackBerry|IEMobile|Opera Mini/i.test(ua)) {
            return 'Mobile';
        }
        // Check for tablet-specific patterns
        if (/Tablet|iPad/i.test(ua)) {
            return 'Mobile';
        }
        return 'Desktop';
    },

    // Auto-detect a descriptive device name from User-Agent
    detectDeviceName: function () {
        var ua = navigator.userAgent || '';
        var isCapacitor = this.isNativePlatform();
        var isDesktop = this.isDesktopApp();
        
        var browser = "Browser";
        if (isCapacitor) browser = "App";
        else if (isDesktop) browser = "Desktop App";
        else if (/Edg\//i.test(ua)) browser = "Edge";
        else if (/Chrome|CriOS/i.test(ua)) browser = "Chrome";
        else if (/Firefox|FxiOS/i.test(ua)) browser = "Firefox";
        else if (/Safari/i.test(ua)) browser = "Safari";
        
        var os = "Device";
        if (/iPhone/i.test(ua)) os = "iPhone";
        else if (/iPad/i.test(ua)) os = "iPad";
        else if (/Android/i.test(ua)) os = "Android";
        else if (/Mac OS X/i.test(ua)) os = "macOS";
        else if (/Windows/i.test(ua)) os = "Windows";
        else if (/Linux/i.test(ua)) os = "Linux";

        return browser + " on " + os;
    },

    // Request notification permission from the user
    requestPermission: async function (dotNetRef) {
        const log = (msg) => { if(dotNetRef) dotNetRef.invokeMethodAsync('OnJSLog', msg).catch(e=>{}); console.log(msg); };
        if (!this.isSupported()) {
            return { success: false, error: 'Push notifications not supported' };
        }

        if (this.isDesktopApp()) {
            return { 
                success: true, 
                permission: 'granted',
                soundDisabled: false,
                alertDisabled: false
            };
        }

        if (this.isNativePlatform()) {
            try {
                log('Native: checking permissions...');
                let permStatus = await Capacitor.Plugins.FirebaseMessaging.checkPermissions();
                log('Native: current permission is ' + permStatus.receive);
                if (permStatus.receive === 'prompt') {
                    log('Native: prompting for permissions...');
                    permStatus = await Capacitor.Plugins.FirebaseMessaging.requestPermissions();
                    log('Native: after prompt, permission is ' + permStatus.receive);
                }

                let soundDisabled = false;
                let alertDisabled = false;
                if (window.Capacitor?.Plugins?.NotificationCrypto?.getNotificationSettings) {
                    try {
                        const settings = await window.Capacitor.Plugins.NotificationCrypto.getNotificationSettings();
                        if (settings) {
                            if (settings.authorizationStatus === 'authorized' && !settings.soundEnabled) {
                                soundDisabled = true;
                            }
                            if (settings.authorizationStatus === 'authorized' && !settings.alertEnabled) {
                                alertDisabled = true;
                            }
                            log(`Native iOS Settings: auth=${settings.authorizationStatus}, sound=${settings.soundEnabled}, alert=${settings.alertEnabled}, badge=${settings.badgeEnabled}`);
                        }
                    } catch (e) {}
                }

                return { 
                    success: permStatus.receive === 'granted', 
                    permission: permStatus.receive,
                    soundDisabled: soundDisabled,
                    alertDisabled: alertDisabled
                };
            } catch (error) {
                log('Native permission error: ' + error.message);
                return { success: false, error: error.message };
            }
        }

        try {
            const permission = await Notification.requestPermission();
            return { success: permission === 'granted', permission: permission };
        } catch (error) {
            return { success: false, error: error.message };
        }
    },

    // Register the service worker
    registerServiceWorker: async function () {
        if (this.isNativePlatform()) {
            // Service workers are not required for native push notifications
            return { success: true, scope: 'native' };
        }

        if (!('serviceWorker' in navigator)) {
            return { success: false, error: 'Service workers not supported' };
        }

        try {
            const registration = await navigator.serviceWorker.register('/service-worker.js');
            console.log('Service Worker registered:', registration.scope);
            return { success: true, scope: registration.scope };
        } catch (error) {
            console.error('Service Worker registration failed:', error);
            return { success: false, error: error.message };
        }
    },

    // Subscribe to push notifications
    subscribe: async function (vapidPublicKey, dotNetRef) {
        const log = (msg) => { if(dotNetRef) dotNetRef.invokeMethodAsync('OnJSLog', msg).catch(e=>{}); console.log(msg); };
        if (!this.isSupported()) {
            return { success: false, error: 'Push notifications not supported' };
        }

        if (this.isDesktopApp()) {
            log('Desktop: acquiring FCM NativeRelay subscription from SpokesBridge...');
            if (window.SpokesBridge && window.SpokesBridge.getFcmSubscription) {
                try {
                    const fcmSub = await window.SpokesBridge.getFcmSubscription();
                    if (fcmSub && fcmSub.endpoint) {
                        const desktopPublicKey = (fcmSub.publicKey || '').trim();
                        if (!desktopPublicKey) {
                            log('Desktop: SpokesBridge returned subscription without a push encryption public key.');
                            return { success: false, error: 'Push encryption public key missing from desktop bridge.' };
                        }
                        try {
                            localStorage.removeItem('spokes_push_disabled');
                            localStorage.setItem('spokes_desktop_push_subscribed', 'true');
                        } catch (e) {}
                        return this._buildNativeRelaySubscription(fcmSub.endpoint, desktopPublicKey);
                    }
                } catch (err) {
                    log('Desktop subscription error: ' + (err && err.message ? err.message : err));
                    return { success: false, error: err && err.message ? err.message : 'Failed to acquire desktop FCM token' };
                }
            }
            return { success: false, error: 'SpokesBridge getFcmSubscription not available' };
        }

        if (this.isNativePlatform()) {
             const self = this;
             log('Native: inside subscribe, initializing FirebaseMessaging...');
             return new Promise(async (resolve) => {
                 const pushPlugin = Capacitor.Plugins.FirebaseMessaging;
                 
                 // Generate and Register Native Android Notification Channels
                     log('Native: removed previous push listeners.');
                     
                     // Native Android notification channels (channel_call_v4, channel_chat_v3, etc.) are created directly by CustomPushService.java with full AudioAttributes.

                     // Add a 15-second timeout so the UI doesn't hang forever if APNs fails silently
                     const timeoutId = setTimeout(() => {
                         log('Native: TIMEOUT reached waiting for APNs token/FCM token.');
                         resolve({ success: false, error: 'Timed out waiting for APNs. Are you on a Simulator? (APNs requires a physical device & Push Entitlement).' });
                     }, 15000);

                     let tokenListener = null;
                     let errorListener = null;
                     let isResolved = false;
                     
                     const cleanupListeners = () => {
                         if (tokenListener) tokenListener.remove();
                         if (errorListener) errorListener.remove();
                     };

                     tokenListener = await pushPlugin.addListener('tokenReceived', async (event) => {
                         if (isResolved) return;
                         isResolved = true;
                         clearTimeout(timeoutId);
                         cleanupListeners();
                         log('Native: tokenReceived event fired. Token present: ' + (event.token ? 'yes' : 'no'));
                         try {
                             localStorage.removeItem('spokes_push_disabled');
                             localStorage.setItem('spokes_native_push_token', event.token);
                         } catch (e) {}

                         const keyInfo = await self._getNativePublicKeyInfo(log);
                         const nativePublicKey = (keyInfo && keyInfo.publicKey ? keyInfo.publicKey : '').trim();
                         if (!nativePublicKey) {
                             log('Native: refusing NativeRelay registration without a device RSA public key.');
                             resolve({ success: false, error: 'Failed to obtain native push encryption public key.' });
                             return;
                         }
                         resolve(self._buildNativeRelaySubscription(event.token, nativePublicKey));
                     });
                     
                     // Not strictly required by FirebaseMessaging but good practice if available
                     errorListener = await pushPlugin.addListener('registrationError', (error) => {
                         if (isResolved) return;
                         isResolved = true;
                         clearTimeout(timeoutId);
                         cleanupListeners();
                         log('Native: registrationError event fired: ' + (error ? error.error : 'unknown'));
                         resolve({ success: false, error: error.error });
                     });

                     try {
                         if (window.Capacitor && window.Capacitor.Plugins && window.Capacitor.Plugins.Preferences) {
                             try {
                                 const apnsDebug = await window.Capacitor.Plugins.Preferences.get({ key: 'apns_debug_status' });
                                 log('Native: OS APNs Status = ' + (apnsDebug.value || 'None'));
                             } catch(e) {}
                         }

                         log('Native: explicitly calling pushPlugin.getToken()...');
                         const result = await pushPlugin.getToken();
                         log('Native: getToken() completed. Result has token: ' + (result && result.token ? 'yes' : 'no'));
                         if (result && result.token) {
                             if (isResolved) return;
                             isResolved = true;
                             clearTimeout(timeoutId);
                             cleanupListeners();
                             try {
                                 localStorage.removeItem('spokes_push_disabled');
                                 localStorage.setItem('spokes_native_push_token', result.token);
                             } catch (e) {}

                             const keyInfo = await self._getNativePublicKeyInfo(log);
                             const nativePublicKey = (keyInfo && keyInfo.publicKey ? keyInfo.publicKey : '').trim();
                             if (!nativePublicKey) {
                                 log('Native: refusing NativeRelay registration without a device RSA public key.');
                                 resolve({ success: false, error: 'Failed to obtain native push encryption public key.' });
                                 return;
                             }
                             resolve(self._buildNativeRelaySubscription(result.token, nativePublicKey));
                         }
                     } catch (e) {
                         log('Native: getToken() threw exception: ' + e.message + '. Waiting for events...');
                         console.error('Failed to get FCM token natively:', e);
                     }
             });
        }

        if (Notification.permission !== 'granted') {
            return { success: false, error: 'Notification permission not granted' };
        }

        try {
            // Ensure service worker is ready
            const registration = await navigator.serviceWorker.ready;

            // Check for existing subscription
            let subscription = await registration.pushManager.getSubscription();

            if (subscription) {
                let isKeyMatching = true;
                if (vapidPublicKey && subscription.options && subscription.options.applicationServerKey) {
                    try {
                        const currentKey = new Uint8Array(subscription.options.applicationServerKey);
                        const expectedKey = this._urlBase64ToUint8Array(vapidPublicKey);
                        if (currentKey.length !== expectedKey.length) {
                            isKeyMatching = false;
                        } else {
                            for (let i = 0; i < currentKey.length; i++) {
                                if (currentKey[i] !== expectedKey[i]) {
                                    isKeyMatching = false;
                                    break;
                                }
                            }
                        }
                    } catch (e) {
                        isKeyMatching = false;
                    }
                }

                if (isKeyMatching) {
                    try { localStorage.removeItem('spokes_push_disabled'); } catch (e) {}
                    // Already subscribed with matching key, return existing subscription
                    return this._formatSubscription(subscription);
                }

                // VAPID key mismatch (e.g. fresh server with new VAPID keys). Unsubscribe stale subscription.
                console.log('[Push] Existing WebPush subscription key does not match server VAPID key. Unsubscribing stale subscription...');
                await subscription.unsubscribe();
                subscription = null;
            }

            // Convert VAPID key from base64 URL to Uint8Array
            const applicationServerKey = this._urlBase64ToUint8Array(vapidPublicKey);

            // Subscribe to push notifications
            subscription = await registration.pushManager.subscribe({
                userVisibleOnly: true,
                applicationServerKey: applicationServerKey
            });

            try { localStorage.removeItem('spokes_push_disabled'); } catch (e) {}
            return this._formatSubscription(subscription);
        } catch (error) {
            console.error('Failed to subscribe:', error);
            return { success: false, error: error.message };
        }
    },

    // Synchronize server-side explicit push opt-out state with per-origin localStorage
    setServerPushDisabled: function (disabled) {
        try {
            if (disabled) {
                localStorage.setItem('spokes_push_disabled', 'true');
                localStorage.removeItem('spokes_native_push_token');
                localStorage.removeItem('spokes_desktop_push_subscribed');
            } else {
                localStorage.removeItem('spokes_push_disabled');
            }
        } catch (e) {}
    },

    // Unsubscribe from push notifications on the current server origin
    unsubscribe: async function () {
        try {
            try {
                localStorage.setItem('spokes_push_disabled', 'true');
            } catch (e) {}

            if (this.isDesktopApp()) {
                try {
                    localStorage.removeItem('spokes_desktop_push_subscribed');
                } catch (e) {}
                return { success: true };
            }

            if (this.isNativePlatform()) {
                localStorage.removeItem('spokes_native_push_token');
                localStorage.removeItem('spokes_native_push_pubkey');
                // Do NOT call FirebaseMessaging.deleteToken() here:
                // the OS/app-level FCM token is shared by all Spokes servers connected to this mobile app.
                // Per-server unsubscription is scoped to this server's DeviceSession and origin localStorage.
                return { success: true };
            }

            const registration = await navigator.serviceWorker.ready;
            const subscription = await registration.pushManager.getSubscription();

            if (subscription) {
                await subscription.unsubscribe();
                return { success: true };
            }

            return { success: true, message: 'No subscription to remove' };
        } catch (error) {
            console.error('Failed to unsubscribe:', error);
            return { success: false, error: error.message };
        }
    },

    // Format subscription for server
    _formatSubscription: function (subscription) {
        if (subscription && subscription.subscriptionType === 'NativeRelay') {
            return subscription;
        }
        const json = subscription.toJSON();
        return {
            success: true,
            endpoint: json.endpoint,
            p256dh: json.keys?.p256dh || '',
            auth: json.keys?.auth || ''
        };
    },

    // Convert base64 URL to Uint8Array (required for applicationServerKey)
    _urlBase64ToUint8Array: function (base64String) {
        const padding = '='.repeat((4 - base64String.length % 4) % 4);
        const base64 = (base64String + padding)
            .replace(/\-/g, '+')
            .replace(/_/g, '/');

        const rawData = window.atob(base64);
        const outputArray = new Uint8Array(rawData.length);

        for (let i = 0; i < rawData.length; ++i) {
            outputArray[i] = rawData.charCodeAt(i);
        }
        return outputArray;
    },

    // Get existing subscription for this server origin (returns null if none or explicitly disabled)
    getExistingSubscription: async function (options) {
        options = options || {};
        try {
            if (!options.ignoreDisabledFlag) {
                try {
                    if (localStorage.getItem('spokes_push_disabled') === 'true') {
                        return null;
                    }
                } catch (e) {}
            }

            if (this.isDesktopApp()) {
                let allowDesktopToken = Boolean(options.allowNativeTokenFetch);
                if (!allowDesktopToken) {
                    try {
                        allowDesktopToken = localStorage.getItem('spokes_desktop_push_subscribed') === 'true';
                    } catch (e) {}
                }
                if (allowDesktopToken && window.SpokesBridge && window.SpokesBridge.getFcmSubscription) {
                    try {
                        const fcmSub = await window.SpokesBridge.getFcmSubscription();
                        const desktopPublicKey = (fcmSub && fcmSub.publicKey ? fcmSub.publicKey : '').trim();
                        if (fcmSub && fcmSub.endpoint && desktopPublicKey) {
                            try { localStorage.setItem('spokes_desktop_push_subscribed', 'true'); } catch (e) {}
                            return this._buildNativeRelaySubscription(fcmSub.endpoint, desktopPublicKey);
                        }
                    } catch (e) {}
                }
                return null;
            }

            if (this.isNativePlatform()) {
                const self = this;
                let nativeToken = null;
                const cachedToken = localStorage.getItem('spokes_native_push_token');
                const previousCachedPubKey = localStorage.getItem('spokes_native_push_pubkey');

                // Always query the authoritative native keystore public key (cached in-memory in NotificationCryptoPlugin)
                // so a reinstall or AndroidKeyStore regeneration is detected immediately even when localStorage was restored by Auto Backup.
                const keyInfo = await this._getNativePublicKeyInfo();
                const authoritativePubKey = keyInfo.publicKey;
                const pubKeyChanged = Boolean(authoritativePubKey && previousCachedPubKey && previousCachedPubKey !== authoritativePubKey);

                if (cachedToken && authoritativePubKey) {
                    if (keyInfo.keyDirty || pubKeyChanged) {
                        self.sendSubscriptionToServer(self._buildNativeRelaySubscription(cachedToken, authoritativePubKey)).catch(function () {});
                    }

                    // Check for FCM/APNs token rotation asynchronously off the critical path.
                    const pushPlugin = window.Capacitor?.Plugins?.FirebaseMessaging;
                    if (pushPlugin && typeof pushPlugin.getToken === 'function') {
                        Promise.resolve(pushPlugin.getToken())
                            .then(function (result) {
                                if (result && result.token && result.token !== cachedToken) {
                                    localStorage.setItem('spokes_native_push_token', result.token);
                                    self.sendSubscriptionToServer(self._buildNativeRelaySubscription(result.token, authoritativePubKey)).catch(function () {});
                                }
                            })
                            .catch(function () {});
                    }
                    return self._buildNativeRelaySubscription(cachedToken, authoritativePubKey);
                }

                if (cachedToken) {
                    nativeToken = cachedToken;
                } else if (options.allowNativeTokenFetch) {
                    // Server already has push enabled for this session, but per-origin localStorage cache is empty:
                    // recover the FCM token from FirebaseMessaging plugin.
                    try {
                        const pushPlugin = window.Capacitor?.Plugins?.FirebaseMessaging;
                        if (pushPlugin) {
                            const result = await pushPlugin.getToken();
                            if (result && result.token) {
                                nativeToken = result.token;
                                localStorage.setItem('spokes_native_push_token', nativeToken);
                                console.log('Native [getExistingSubscription]: FCM token retrieved directly from FirebaseMessaging');
                            }
                        }
                    } catch (e) {
                        console.log('Native [getExistingSubscription]: direct getToken() failed: ' + e.message);
                    }
                }

                if (nativeToken) {
                    if (!authoritativePubKey) {
                        console.warn('Native [getExistingSubscription]: authoritative RSA public key unavailable; skipping NativeRelay subscription.');
                        return null;
                    }
                    if (keyInfo.keyDirty) {
                        self.sendSubscriptionToServer(self._buildNativeRelaySubscription(nativeToken, authoritativePubKey)).catch(function () {});
                    }
                    return self._buildNativeRelaySubscription(nativeToken, authoritativePubKey);
                }
                return null;
            }

            if (!('serviceWorker' in navigator)) return null;

            const registration = await navigator.serviceWorker.getRegistration();
            if (!registration) return null;

            const subscription = await registration.pushManager.getSubscription();
            return subscription ? this._formatSubscription(subscription) : null;
        } catch (error) {
            console.error('Error getting subscription:', error);
            return null;
        }
    },

    // Send subscription to the server API (with device type and name)
    sendSubscriptionToServer: async function (subscription, deviceType, deviceName) {
        try {
            const isCapacitor = this.isNativePlatform();
            const isDesktop = this.isDesktopApp();
            const resolvedDeviceType = isDesktop ? 'Desktop' : (isCapacitor ? 'Mobile' : (deviceType || this.detectDeviceType()));
            const deviceId = await this.getDeviceId();
            
            var hdrs = (window.spokesAuth && typeof window.spokesAuth.getHeaders === 'function')
                ? await window.spokesAuth.getHeaders({ 'Content-Type': 'application/json' })
                : { 'Content-Type': 'application/json' };

            const subscriptionType = subscription.subscriptionType || subscription.SubscriptionType || (isCapacitor ? 'NativeRelay' : 'WebPush');
            const publicKey = (subscription.publicKey || subscription.PublicKey || '').trim();
            if (subscriptionType === 'NativeRelay' && !publicKey) {
                console.warn('[Push] Refusing to register NativeRelay subscription without a public encryption key.');
                return false;
            }

            const response = await fetch('/spokesapi/push/subscribe', {
                method: 'POST',
                headers: hdrs,
                credentials: 'include',
                body: JSON.stringify({
                    endpoint: subscription.endpoint,
                    p256dh: subscription.p256dh,
                    auth: subscription.auth,
                    publicKey: publicKey,
                    subscriptionType: subscriptionType,
                    deviceType: resolvedDeviceType,
                    deviceName: deviceName || '',
                    isCapacitor: isCapacitor,
                    deviceId: deviceId
                })
            });

            if (response.redirected && response.url.includes('/sso/login')) {
                console.error('Session expired, redirected to login.');
                window.location.reload();
                return false;
            }

            if (!response.ok) {
                let errPayload = null;
                try {
                    errPayload = await response.json();
                } catch (e) { }
                const errCode = errPayload?.code || errPayload?.errorCode || '';
                const errMsg = errPayload?.error || errPayload?.message || `Server returned ${response.status}`;
                console.warn(`[Push] Failed to register subscription on server (HTTP ${response.status}):`, errCode ? `${errCode} - ${errMsg}` : errMsg);
                this._lastSubscriptionError = { status: response.status, code: errCode, message: errMsg };
                return false;
            }

            const contentType = response.headers.get("content-type");
            if (contentType && contentType.includes("application/json")) {
                const result = await response.json();
                console.log('Subscription saved:', result);
                try {
                    localStorage.removeItem('spokes_push_disabled');
                    if (isCapacitor && subscription.endpoint) {
                        localStorage.setItem('spokes_native_push_token', subscription.endpoint);
                    } else if (isDesktop) {
                        localStorage.setItem('spokes_desktop_push_subscribed', 'true');
                    }
                } catch (e) {}
                if (isCapacitor && window.Capacitor?.Plugins?.NotificationCrypto?.clearKeyDirty) {
                    try {
                        await window.Capacitor.Plugins.NotificationCrypto.clearKeyDirty();
                    } catch (e) {}
                }
                return true;
            } else {
                console.error('Received non-JSON response from server. Session likely expired.');
                window.location.reload();
                return false;
            }
        } catch (error) {
            console.error('Error sending subscription to server:', error);
            return false;
        }
    },

    // Unsubscribe and notify server
    unsubscribeAndNotifyServer: async function (fallbackEndpoint) {
        try {
            const subscription = await this.getExistingSubscription({ ignoreDisabledFlag: true, allowNativeTokenFetch: false });
            const endpointToUnsubscribe = (subscription && subscription.endpoint) ? subscription.endpoint : (fallbackEndpoint || '');

            var hdrs = (window.spokesAuth && typeof window.spokesAuth.getHeaders === 'function')
                ? await window.spokesAuth.getHeaders({ 'Content-Type': 'application/json' })
                : { 'Content-Type': 'application/json' };

            // Always notify the server so the active DeviceSession marks PushExplicitlyDisabled = true
            await fetch('/spokesapi/push/unsubscribe', {
                method: 'POST',
                headers: hdrs,
                credentials: 'include',
                body: JSON.stringify({
                    endpoint: endpointToUnsubscribe
                })
            });

            // Then unsubscribe locally for this server origin
            return await this.unsubscribe();
        } catch (error) {
            console.error('Error unsubscribing:', error);
            return { success: false, error: error.message };
        }
    },

    // Initialize native push listeners (especially notificationActionPerformed)
    initializeNativePushListeners: async function (dotNetRef) {
        if (!this.isNativePlatform()) return;
        window.spokesDotNetRef = dotNetRef;
        if (window.pendingNotificationRoute) {
             const route = window.pendingNotificationRoute;
             window.pendingNotificationRoute = null;
             console.log('Navigating to pending notification route:', route);
             try {
                 await dotNetRef.invokeMethodAsync('NavigateTo', route);
                 window.spokesPendingNav = null;
             } catch(e) {
                 window.location.href = route;
             }
        }
    },

    // Setup listener for service worker messages to report push results (single deduplicated listener slot)
    _messageListenerDotNetRef: null,
    _messageListenerAttached: false,

    setupMessageListener: function (dotNetRef) {
        this._messageListenerDotNetRef = dotNetRef || null;
        if (!('serviceWorker' in navigator) || this._messageListenerAttached) return;
        this._messageListenerAttached = true;
        navigator.serviceWorker.addEventListener('message', event => {
            const target = this._messageListenerDotNetRef;
            if (target && event.data && event.data.type === 'TEST_PUSH_RESULT') {
                target.invokeMethodAsync('OnTestPushResult', {
                    success: !!event.data.success,
                    fallbackUsed: !!event.data.fallbackUsed,
                    error: event.data.error || null,
                    category: event.data.category || 'chat',
                    sound: event.data.sound || null
                }).catch(() => {});
            }
        });
    },

    clearMessageListener: function (dotNetRef) {
        if (!dotNetRef || this._messageListenerDotNetRef === dotNetRef) {
            this._messageListenerDotNetRef = null;
        }
    },

    // Single batched snapshot for UserSettingsDialog (replaces 9 sequential JS interop round-trips)
    getDeviceSettingsSnapshot: async function (dotNetRef, options) {
        const opts = options || {};
        this.setupMessageListener(dotNetRef);

        const isSupported = Boolean(this.isSupported());
        const unsupportedReason = isSupported ? '' : (this.getSupportReason() || '');
        let isDesktopApp = false;
        let requiresPushRelay = false;
        let deviceType = '';
        let deviceName = '';

        if (isSupported) {
            try {
                isDesktopApp = Boolean(this.isDesktopApp());
                requiresPushRelay = Boolean(this.requiresPushRelay());
            } catch (e) {}

            if (typeof opts.serverPushDisabled === 'boolean') {
                try {
                    this.setServerPushDisabled(opts.serverPushDisabled);
                } catch (e) {}
            }

            try { deviceType = this.detectDeviceType() || ''; } catch (e) {}
            try { deviceName = this.detectDeviceName() || ''; } catch (e) {}
        }

        const subscriptionPromise = (isSupported && opts.checkSubscription)
            ? this.getExistingSubscription({ allowNativeTokenFetch: Boolean(opts.allowNativeTokenFetch) }).catch(() => null)
            : Promise.resolve(null);

        const mediaCachePromise = (window.spokesMediaLoader && typeof window.spokesMediaLoader.getCacheStatsAsync === 'function')
            ? window.spokesMediaLoader.getCacheStatsAsync().catch(() => null)
            : Promise.resolve(null);

        const [subscription, mediaCache] = await Promise.all([subscriptionPromise, mediaCachePromise]);

        return {
            isSupported: isSupported,
            unsupportedReason: unsupportedReason,
            isDesktopApp: isDesktopApp,
            requiresPushRelay: requiresPushRelay,
            subscription: subscription,
            deviceType: deviceType,
            deviceName: deviceName,
            mediaCache: mediaCache
        };
    },

    // Check if the user has already been prompted for push notifications on this device/server
    hasBeenPromptedForPush: function (serverId) {
        try {
            if (localStorage.getItem('spokes_push_disabled') === 'true') {
                return true;
            }
        } catch (e) {}

        // If notifications are explicitly blocked by the browser or user, never prompt
        if (!this.isNativePlatform() && !this.isDesktopApp() && typeof window !== 'undefined' && 'Notification' in window && Notification.permission === 'denied') {
            return true;
        }
        // On standard Web, if browser permission is already granted for this origin, no need to show the opt-in prompt
        if (!this.isNativePlatform() && !this.isDesktopApp() && typeof window !== 'undefined' && 'Notification' in window && Notification.permission === 'granted') {
            return true;
        }

        var key = serverId ? 'spokes_push_prompted_' + serverId : 'spokes_push_prompted';
        try {
            if (sessionStorage.getItem(key) === 'true') return true;
            return localStorage.getItem(key) === 'true';
        } catch (e) {
            return false;
        }
    },

    // Record that the user has been prompted for push notifications on this device
    setPushPrompted: function (serverId) {
        var key = serverId ? 'spokes_push_prompted_' + serverId : 'spokes_push_prompted';
        try {
            localStorage.setItem(key, 'true');
            sessionStorage.setItem(key, 'true');
        } catch (e) {}
    }
};

// Device ID initialization is handled by App.razor's runStartup() sequence.
