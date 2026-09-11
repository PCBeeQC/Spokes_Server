// Web Push Notifications JavaScript interop for Blazor
// Provides functions to manage push notification permissions and subscriptions

window.pushNotifications = {
    // Get the physical DeviceId from the platform-appropriate store.
    // Native (Capacitor): Uses platform stable ID (ANDROID_ID / iOS Keychain) that survives reinstalls.
    // Web: localStorage with web_ prefix.
    getDeviceId: async function () {
        if (window._spokesCachedDeviceId) return window._spokesCachedDeviceId;

        const native = window.Capacitor && window.Capacitor.isNativePlatform && window.Capacitor.isNativePlatform() && window.Capacitor.Plugins;

        if (native) {
            // 0. Fast-path: Check localStorage to avoid bridge overhead on subsequent calls/boots
            const localCache = localStorage.getItem('spokes_native_device_id');
            if (localCache && !localCache.startsWith('dev_')) {
                window._spokesCachedDeviceId = localCache;
                return localCache;
            }

            // 1. Try platform-native stable ID (survives reinstalls)
            try {
                const crypto = window.Capacitor.Plugins.NotificationCrypto;
                if (crypto && crypto.getStableDeviceId) {
                    const res = await crypto.getStableDeviceId();
                    if (res && res.deviceId) {
                        // Cache in Preferences for quick access by other code paths
                        try { await window.Capacitor.Plugins.Preferences.set({ key: 'spokes_device_id', value: res.deviceId }); } catch (e) {}
                        localStorage.setItem('spokes_native_device_id', res.deviceId);
                        window._spokesCachedDeviceId = res.deviceId;
                        return res.deviceId;
                    }
                }
            } catch (e) {
                console.warn('[DeviceId] Native stable ID failed, falling back to Preferences', e);
            }

            // 2. Fall back to Preferences (modern namespaced ID)
            try {
                const res = await window.Capacitor.Plugins.Preferences.get({ key: 'spokes_device_id' });
                if (res && res.value && !res.value.startsWith('dev_')) {
                    localStorage.setItem('spokes_native_device_id', res.value);
                    window._spokesCachedDeviceId = res.value;
                    return res.value;
                }
            } catch (e) {
                console.error('Failed to read deviceId from Preferences', e);
            }

            // 3. Last resort: generate random UUID (should rarely happen)
            var deviceId = (crypto.randomUUID ? crypto.randomUUID() : 'xxxxxxxx-xxxx-4xxx-yxxx-xxxxxxxxxxxx'.replace(/[xy]/g, function(c) {
                var r = Math.random() * 16 | 0, v = c == 'x' ? r : (r & 0x3 | 0x8);
                return v.toString(16);
            }));
            try { await window.Capacitor.Plugins.Preferences.set({ key: 'spokes_device_id', value: deviceId }); } catch (e) {}
            localStorage.setItem('spokes_native_device_id', deviceId);
            window._spokesCachedDeviceId = deviceId;
            return deviceId;
        }

        // Web: localStorage
        var deviceId = localStorage.getItem('spokes_device_id');
        if (!deviceId || deviceId.startsWith('dev_')) {
            // Generate standard UUID
            deviceId = (crypto.randomUUID ? crypto.randomUUID() : 'xxxxxxxx-xxxx-4xxx-yxxx-xxxxxxxxxxxx'.replace(/[xy]/g, function(c) {
                var r = Math.random() * 16 | 0, v = c == 'x' ? r : (r & 0x3 | 0x8);
                return v.toString(16);
            }));
            localStorage.setItem('spokes_device_id', deviceId);
        }
        return deviceId;
    },

    // Legacy stub — bidirectional sync has been removed.
    // Mobile uses Preferences exclusively; web uses cookies exclusively.
    // Retained as a no-op so callers (App.razor runStartup) don't throw.
    initDeviceId: async function () {
        await this.getDeviceId();
    },


    // Safely copy text to clipboard with fallback for iOS WebViews
    copyText: function(text) {
        if (navigator.clipboard && window.isSecureContext && !navigator.userAgent.match(/ipad|iphone|macintosh/i)) {
            return navigator.clipboard.writeText(text);
        } else {
            return new Promise((resolve, reject) => {
                try {
                    var textArea = document.createElement('textarea');
                    textArea.value = text;
                    textArea.style.position = 'fixed';
                    textArea.style.left = '-999999px';
                    textArea.style.top = '-999999px';
                    document.body.appendChild(textArea);
                    textArea.focus();
                    textArea.select();
                    var res = document.execCommand('copy');
                    textArea.remove();
                    if (res) resolve();
                    else reject(new Error('copy failed'));
                } catch (e) {
                    reject(e);
                }
            });
        }
    },

    // Update app icon badge natively via Capacitor Badge plugin
    updateBadge: async function (count) {
        if (window.Capacitor && window.Capacitor.isNativePlatform && window.Capacitor.isNativePlatform()) {
            const currentOrigin = window.location.origin.replace(/\/$/, '');
            
            if (window.Capacitor.Plugins.NotificationCrypto) {
                try {
                    await window.Capacitor.Plugins.NotificationCrypto.setServerBadgeCount({ originUrl: currentOrigin, count: count });
                } catch(e) {}
            }
            
            if (window.Capacitor.Plugins.Badge) {
                try {
                    let totalCount = count;
                    
                    if (window.Capacitor.Plugins.Preferences) {
                        const res = await window.Capacitor.Plugins.Preferences.get({ key: 'savedServers' });
                        let servers = res.value ? JSON.parse(res.value) : [];
                        
                        for (let s of servers) {
                            let serverUrl = s.url.replace(/\/$/, '');
                            if (serverUrl !== currentOrigin) {
                                try {
                                    let fetchUrl = serverUrl + '/spokesapi/push/unread-count';
                                    let data = null;
                                    let targetHost = new URL(serverUrl).hostname;
                                    let hdrs = window.spokesAuth ? await window.spokesAuth.getHeaders({ 'Content-Type': 'application/json' }, targetHost) : { 'Content-Type': 'application/json' };
                                    
                                    // Cross-origin: use explicit CapacitorHttp to bypass CORS on native
                                    if (window.Capacitor && window.Capacitor.Plugins && window.Capacitor.Plugins.CapacitorHttp) {
                                        let resp = await window.Capacitor.Plugins.CapacitorHttp.get({ url: fetchUrl, headers: hdrs });
                                        if (resp.status >= 200 && resp.status < 300 && resp.data) {
                                            data = typeof resp.data === 'string' ? JSON.parse(resp.data) : resp.data;
                                        }
                                    } else {
                                        let req = await fetch(fetchUrl, { headers: hdrs, credentials: 'include' });
                                        if (req.ok) data = await req.json();
                                    }
                                    if (data) {
                                        totalCount += (data.count || 0);
                                    }
                                } catch (e) {}
                            }
                        }
                    }
                    
                    if (totalCount > 0) {
                        await window.Capacitor.Plugins.Badge.set({ count: totalCount });
                    } else {
                        await window.Capacitor.Plugins.Badge.clear();
                    }
                } catch (e) {
                    console.error('Error updating native app badge:', e);
                }
            } else {
                console.warn('Badge plugin not found. Did you run cap sync?');
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

    // Check if push notifications are supported
    isSupported: function () {
        if (window.Capacitor && window.Capacitor.isNativePlatform && window.Capacitor.isNativePlatform()) return true;
        return 'serviceWorker' in navigator &&
            'PushManager' in window &&
            'Notification' in window;
    },

    // Get the reason why push notifications are not supported
    getSupportReason: function () {
        if (window.Capacitor && window.Capacitor.isNativePlatform && window.Capacitor.isNativePlatform()) return 'Supported via Capacitor';
        if (!('serviceWorker' in navigator)) return 'No Service Worker (HTTPS required)';
        if (!('PushManager' in window)) return 'No Push API';
        if (!('Notification' in window)) return 'No Notification API';
        return 'Unknown error';
    },

    // Get current permission status: 'granted', 'denied', or 'default'
    getPermissionStatus: async function () {
        if (!this.isSupported()) return 'unsupported';
        if (window.Capacitor && window.Capacitor.isNativePlatform && window.Capacitor.isNativePlatform()) {
            try {
                const status = await Capacitor.Plugins.FirebaseMessaging.checkPermissions();
                return status.receive;
            } catch (e) {
                return 'default';
            }
        }
        return Notification.permission;
    },

    // Auto-detect device type from User-Agent
    detectDeviceType: function () {
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
        var isCapacitor = window.Capacitor && window.Capacitor.isNativePlatform && window.Capacitor.isNativePlatform();
        
        var browser = "Browser";
        if (isCapacitor) browser = "App";
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

        if (window.Capacitor && window.Capacitor.isNativePlatform && window.Capacitor.isNativePlatform()) {
            try {
                log('Native: checking permissions...');
                let permStatus = await Capacitor.Plugins.FirebaseMessaging.checkPermissions();
                log('Native: current permission is ' + permStatus.receive);
                if (permStatus.receive === 'prompt') {
                    log('Native: prompting for permissions...');
                    permStatus = await Capacitor.Plugins.FirebaseMessaging.requestPermissions();
                    log('Native: after prompt, permission is ' + permStatus.receive);
                }
                return { success: permStatus.receive === 'granted', permission: permStatus.receive };
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
        if (window.Capacitor && window.Capacitor.isNativePlatform && window.Capacitor.isNativePlatform()) {
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

        if (window.Capacitor && window.Capacitor.isNativePlatform && window.Capacitor.isNativePlatform()) {
             log('Native: inside subscribe, initializing FirebaseMessaging...');
             return new Promise(async (resolve) => {
                 const pushPlugin = Capacitor.Plugins.FirebaseMessaging;
                 
                 // Generate and Register Native Android Notification Channels
                     log('Native: removed previous push listeners.');
                     
                     // Generate and Register Native Android Notification Channels 
                     if (window.Capacitor.getPlatform() === 'android') {
                         log('Native: configuring android notification channels...');
                         await pushPlugin.createChannel({ id: 'channel_default', name: 'General Notifications', description: 'General app notifications', importance: 3, visibility: 1 });
                         await pushPlugin.createChannel({ id: 'channel_email', name: 'Emails', description: 'New email notifications', importance: 3, visibility: 1 });
                         await pushPlugin.createChannel({ id: 'channel_chat', name: 'Chat Messages', description: 'Instant messages', importance: 4, visibility: 1, vibration: true });
                         await pushPlugin.createChannel({ id: 'channel_calls', name: 'Incoming Calls', description: 'Voice and video call alerts', importance: 5, visibility: 1, vibration: true });
                     }

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
                         localStorage.setItem('spokes_native_push_token', event.token);

                         let publicKey = '';
                         try {
                             if (window.Capacitor) {
                                 log('Native [tokenReceived]: Capacitor is available. Plugins: ' + Object.keys(window.Capacitor.Plugins || {}).join(', '));
                                 let cryptoPlugin = window.Capacitor.Plugins?.NotificationCrypto;
                                 if (!cryptoPlugin) {
                                     log('Native [tokenReceived]: NotificationCrypto not in Plugins. Trying registerPlugin...');
                                     if (window.Capacitor.registerPlugin) {
                                         cryptoPlugin = window.Capacitor.registerPlugin('NotificationCrypto');
                                         log('Native [tokenReceived]: registerPlugin called. Result: ' + !!cryptoPlugin);
                                     } else {
                                         log('Native [tokenReceived]: registerPlugin not available.');
                                     }
                                 } else {
                                     log('Native [tokenReceived]: NotificationCrypto found in Plugins.');
                                 }
                                 
                                 if (cryptoPlugin) {
                                     log('Native [tokenReceived]: calling NotificationCrypto.getPublicKey()...');
                                     const result = await cryptoPlugin.getPublicKey();
                                     log('Native [tokenReceived]: crypto result: ' + JSON.stringify(result));
                                     if (result && result.publicKey) publicKey = result.publicKey;
                                 } else {
                                     log('Native [tokenReceived]: cryptoPlugin is still null or undefined.');
                                 }
                             }
                         } catch (e) {
                             log('Native [tokenReceived]: Exception getting publicKey: ' + e.message + (e.stack ? ' | ' + e.stack : ''));
                         }

                         resolve({
                             success: true,
                             endpoint: event.token,
                             p256dh: '',
                             auth: '',
                             publicKey: publicKey,
                             subscriptionType: 'NativeRelay'
                         });
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
                             localStorage.setItem('spokes_native_push_token', result.token);

                             let publicKey = '';
                             try {
                                 if (window.Capacitor) {
                                     log('Native [getToken]: Capacitor is available. Plugins: ' + Object.keys(window.Capacitor.Plugins || {}).join(', '));
                                     let cryptoPlugin = window.Capacitor.Plugins?.NotificationCrypto;
                                     if (!cryptoPlugin) {
                                         log('Native [getToken]: NotificationCrypto not in Plugins. Trying registerPlugin...');
                                         if (window.Capacitor.registerPlugin) {
                                             cryptoPlugin = window.Capacitor.registerPlugin('NotificationCrypto');
                                             log('Native [getToken]: registerPlugin called. Result: ' + !!cryptoPlugin);
                                         } else {
                                             log('Native [getToken]: registerPlugin not available.');
                                         }
                                     } else {
                                         log('Native [getToken]: NotificationCrypto found in Plugins.');
                                     }
                                     
                                     if (cryptoPlugin) {
                                         log('Native [getToken]: calling NotificationCrypto.getPublicKey()...');
                                         const keyResult = await cryptoPlugin.getPublicKey();
                                         log('Native [getToken]: crypto result: ' + JSON.stringify(keyResult));
                                         if (keyResult && keyResult.publicKey) publicKey = keyResult.publicKey;
                                     } else {
                                         log('Native [getToken]: cryptoPlugin is still null or undefined.');
                                     }
                                 }
                             } catch (e) {
                                 log('Native [getToken]: Exception getting publicKey: ' + e.message + (e.stack ? ' | ' + e.stack : ''));
                             }

                             resolve({
                                 success: true,
                                 endpoint: result.token,
                                 p256dh: '',
                                 auth: '',
                                 publicKey: publicKey,
                                 subscriptionType: 'NativeRelay'
                             });
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
                // Already subscribed, return existing subscription
                return this._formatSubscription(subscription);
            }

            // Convert VAPID key from base64 URL to Uint8Array
            const applicationServerKey = this._urlBase64ToUint8Array(vapidPublicKey);

            // Subscribe to push notifications
            subscription = await registration.pushManager.subscribe({
                userVisibleOnly: true,
                applicationServerKey: applicationServerKey
            });

            return this._formatSubscription(subscription);
        } catch (error) {
            console.error('Failed to subscribe:', error);
            return { success: false, error: error.message };
        }
    },

    // Unsubscribe from push notifications
    unsubscribe: async function () {
        try {
            if (window.Capacitor && window.Capacitor.isNativePlatform && window.Capacitor.isNativePlatform()) {
                localStorage.removeItem('spokes_native_push_token');
                try {
                    const pushPlugin = Capacitor.Plugins.FirebaseMessaging;
                    if (pushPlugin && pushPlugin.deleteToken) {
                        await pushPlugin.deleteToken();
                    }
                } catch (e) {
                    console.log('Native [unsubscribe]: failed to delete FCM token natively', e);
                }
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

    // Get existing subscription if any
    getSubscription: async function () {
        try {
            const registration = await navigator.serviceWorker.ready;
            const subscription = await registration.pushManager.getSubscription();

            if (subscription) {
                return this._formatSubscription(subscription);
            }

            return { success: false, message: 'No active subscription' };
        } catch (error) {
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

    // Test notification (for debugging)
    showTestNotification: async function () {
        if (Notification.permission !== 'granted') {
            return { success: false, error: 'Permission not granted' };
        }

        try {
            const registration = await navigator.serviceWorker.ready;
            await registration.showNotification('Test Notification', {
                body: 'Push notifications are working!',
                icon: '/favicon.ico'
            });
            return { success: true };
        } catch (error) {
            return { success: false, error: error.message };
        }
    },

    // Get existing subscription (returns null if none)
    getExistingSubscription: async function () {
        try {
            if (window.Capacitor && window.Capacitor.isNativePlatform && window.Capacitor.isNativePlatform()) {
                let nativeToken = null;
                const cachedToken = localStorage.getItem('spokes_native_push_token');

                if (cachedToken) {
                    // Always try to get a fresh token first (FCM/APNs may have rotated it)
                    try {
                        const pushPlugin = Capacitor.Plugins.FirebaseMessaging;
                        if (pushPlugin) {
                            const result = await pushPlugin.getToken();
                            if (result && result.token) {
                                nativeToken = result.token;
                                // Update cache if token changed
                                if (cachedToken !== nativeToken) {
                                    localStorage.setItem('spokes_native_push_token', nativeToken);
                                    console.log('Native [getExistingSubscription]: FCM token refreshed (was different from cache)');
                                }
                            }
                        }
                    } catch (e) {
                        console.log('Native [getExistingSubscription]: fresh getToken() failed, using cached: ' + e.message);
                    }
                    // Fallback to cached token
                    if (!nativeToken) {
                        nativeToken = cachedToken;
                    }
                }

                if (nativeToken) {
                    let publicKey = '';
                    try {
                        if (window.Capacitor) {
                            console.log('Native [getExistingSubscription]: Capacitor is available. Plugins: ' + Object.keys(window.Capacitor.Plugins || {}).join(', '));
                            let cryptoPlugin = window.Capacitor.Plugins?.NotificationCrypto;
                            if (!cryptoPlugin) {
                                console.log('Native [getExistingSubscription]: NotificationCrypto not in Plugins. Trying registerPlugin...');
                                if (window.Capacitor.registerPlugin) {
                                    cryptoPlugin = window.Capacitor.registerPlugin('NotificationCrypto');
                                    console.log('Native [getExistingSubscription]: registerPlugin called. Result: ' + !!cryptoPlugin);
                                } else {
                                    console.log('Native [getExistingSubscription]: registerPlugin not available.');
                                }
                            } else {
                                console.log('Native [getExistingSubscription]: NotificationCrypto found in Plugins.');
                            }
                            
                            if (cryptoPlugin) {
                                console.log('Native [getExistingSubscription]: calling NotificationCrypto.getPublicKey()...');
                                const result = await cryptoPlugin.getPublicKey();
                                console.log('Native [getExistingSubscription]: crypto result: ' + JSON.stringify(result));
                                if (result && result.publicKey) publicKey = result.publicKey;
                            } else {
                                console.log('Native [getExistingSubscription]: cryptoPlugin is still null or undefined.');
                            }
                        }
                    } catch (e) {
                        console.log('Native [getExistingSubscription]: Exception getting publicKey: ' + e.message + (e.stack ? ' | ' + e.stack : ''));
                    }
                    return {
                        success: true,
                        endpoint: nativeToken,
                        p256dh: '',
                        auth: '',
                        publicKey: publicKey,
                        subscriptionType: 'NativeRelay'
                    };
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
    sendSubscriptionToServer: async function (subscription, deviceType, deviceName, isIdleDetectionEnabled) {
        try {
            const deviceId = await this.getDeviceId();
            
            var hdrs = await window.spokesAuth.getHeaders({ 'Content-Type': 'application/json' });

            const response = await fetch('/spokesapi/push/subscribe', {
                method: 'POST',
                headers: hdrs,
                credentials: 'include',
                body: JSON.stringify({
                    endpoint: subscription.endpoint,
                    p256dh: subscription.p256dh,
                    auth: subscription.auth,
                    publicKey: subscription.publicKey || subscription.PublicKey || '',
                    subscriptionType: subscription.subscriptionType || subscription.SubscriptionType || 'WebPush',
                    deviceType: deviceType || this.detectDeviceType(),
                    deviceName: deviceName || '',
                    isIdleDetectionEnabled: isIdleDetectionEnabled,
                    deviceId: deviceId
                })
            });

            if (response.redirected && response.url.includes('/sso/login')) {
                console.error('Session expired, redirected to login.');
                window.location.reload();
                return false;
            }

            if (!response.ok) {
                console.error('Failed to send subscription to server:', response.status);
                return false;
            }

            const contentType = response.headers.get("content-type");
            if (contentType && contentType.includes("application/json")) {
                const result = await response.json();
                console.log('Subscription saved:', result);
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
    unsubscribeAndNotifyServer: async function () {
        try {
            const subscription = await this.getExistingSubscription();

            if (subscription) {
                var hdrs = await window.spokesAuth.getHeaders({ 'Content-Type': 'application/json' });

                // Notify server first
                await fetch('/spokesapi/push/unsubscribe', {
                    method: 'POST',
                    headers: hdrs,
                    credentials: 'include',
                    body: JSON.stringify({
                        endpoint: subscription.endpoint
                    })
                });
            }

            // Then unsubscribe locally
            return await this.unsubscribe();
        } catch (error) {
            console.error('Error unsubscribing:', error);
            return { success: false, error: error.message };
        }
    },

    // Initialize native push listeners (especially notificationActionPerformed)
    initializeNativePushListeners: async function (dotNetRef) {
        if (!window.Capacitor || !window.Capacitor.isNativePlatform || !window.Capacitor.isNativePlatform()) return;
        window.spokesDotNetRef = dotNetRef;
        if (window.pendingNotificationRoute) {
             const route = window.pendingNotificationRoute;
             window.pendingNotificationRoute = null;
             console.log('Navigating to pending notification route:', route);
             try {
                 await dotNetRef.invokeMethodAsync('NavigateTo', route);
             } catch(e) {
                 window.location.href = route;
             }
        }
    },

    // --- Idle Detection API Implementation ---

    registerPresenceLayout: function (dotNetRef) {
        window.spokesPresenceDotNetRef = dotNetRef;
    },

    isIdleSupported: function () {
        return 'IdleDetector' in window;
    },

    requestIdlePermission: async function () {
        if (!this.isIdleSupported()) {
            return false;
        }
        try {
            const state = await IdleDetector.requestPermission();
            return state === 'granted';
        } catch (error) {
            console.error('Idle detection permission error:', error);
            return false;
        }
    },

    stopIdleDetector: function () {
        if (window.spokesIdlePingInterval) {
            clearInterval(window.spokesIdlePingInterval);
            window.spokesIdlePingInterval = null;
        }
        if (window.spokesIdleAbortController) {
            try { window.spokesIdleAbortController.abort(); } catch (e) {}
            window.spokesIdleAbortController = null;
        }
        window.spokesIdleDetector = null;
        console.log('Idle detection stopped.');
    },

    startIdleDetector: async function (dotNetRef) {
        if (dotNetRef) {
            window.spokesPresenceDotNetRef = dotNetRef;
        }
        const activeRef = window.spokesPresenceDotNetRef;
        if (!activeRef) {
            console.warn('Cannot start idle detector: No presence DotNet reference registered.');
            return false;
        }

        if (!this.isIdleSupported()) return false;

        // Clean up any prior running detector / interval
        this.stopIdleDetector();

        try {
            // Check permission safely without throwing unhandled exceptions
            if (navigator.permissions && navigator.permissions.query) {
                try {
                    const perm = await navigator.permissions.query({ name: 'idle-detection' });
                    if (perm.state !== 'granted') {
                        console.log('Idle detection permission not granted:', perm.state);
                        return false;
                    }
                } catch (e) {
                    const state = await IdleDetector.requestPermission();
                    if (state !== 'granted') return false;
                }
            } else {
                const state = await IdleDetector.requestPermission();
                if (state !== 'granted') return false;
            }

            const controller = new AbortController();
            window.spokesIdleAbortController = controller;
            const signal = controller.signal;

            window.spokesIdleDetector = new IdleDetector();
            window.spokesIdlePingInterval = null;

            const sendPing = () => {
                const ref = window.spokesPresenceDotNetRef;
                if (ref && !window._blazorDisconnected) {
                    try { ref.invokeMethodAsync('OnDeviceActivePing').catch(() => {}); } catch (e) {}
                }
            };

            window.spokesIdleDetector.addEventListener('change', () => {
                if (signal.aborted) return;
                const userState = window.spokesIdleDetector.userState;
                const screenState = window.spokesIdleDetector.screenState;

                if (userState === 'active' && screenState === 'unlocked') {
                    // Ping immediately, and then every 30 seconds while active so the server's 90-second grace window is never breached
                    sendPing();
                    if (window.spokesIdlePingInterval) clearInterval(window.spokesIdlePingInterval);
                    window.spokesIdlePingInterval = setInterval(() => {
                        sendPing();
                    }, 30 * 1000); // 30 seconds
                } else {
                    // User is idle or screen is locked
                    if (window.spokesIdlePingInterval) {
                        clearInterval(window.spokesIdlePingInterval);
                        window.spokesIdlePingInterval = null;
                    }
                }
            });

            await window.spokesIdleDetector.start({
                threshold: 60000, // 1 minute
                signal,
            });
            console.log('Idle detection started successfully.');

            // Initial ping since user is likely active when setting it up
            sendPing();
            // Start the active interval immediately if active and unlocked
            if (window.spokesIdleDetector.userState === 'active' && window.spokesIdleDetector.screenState === 'unlocked') {
                window.spokesIdlePingInterval = setInterval(() => {
                    sendPing();
                }, 30 * 1000); // 30 seconds
            }

            return true;
        } catch (err) {
            if (err && err.name === 'AbortError') return false;
            console.error('Failed to start idle detector:', err);
            this.stopIdleDetector();
            return false;
        }
    },

    // Setup listener for service worker messages to report push results
    setupMessageListener: function (dotNetRef) {
        if (!('serviceWorker' in navigator)) return;
        navigator.serviceWorker.addEventListener('message', event => {
            if (event.data && event.data.type === 'TEST_PUSH_RESULT') {
                dotNetRef.invokeMethodAsync('OnTestPushResult', {
                    success: !!event.data.success,
                    fallbackUsed: !!event.data.fallbackUsed,
                    error: event.data.error || null
                });
            }
        });
    },

    // Check if the user has already been prompted for push notifications on this device
    hasBeenPromptedForPush: function (serverId) {
        var key = serverId ? 'spokes_push_prompted_' + serverId : 'spokes_push_prompted';
        return localStorage.getItem(key) === 'true';
    },

    // Record that the user has been prompted for push notifications on this device
    setPushPrompted: function (serverId) {
        var key = serverId ? 'spokes_push_prompted_' + serverId : 'spokes_push_prompted';
        localStorage.setItem(key, 'true');
    }
};

// Device ID initialization is handled by App.razor's runStartup() sequence.
