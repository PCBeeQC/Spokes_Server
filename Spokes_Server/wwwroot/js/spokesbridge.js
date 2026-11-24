// spokesbridge.js - Unified client bridge for Spokes (Desktop, Mobile, Web)
// Provides a single extensible entry point for native capabilities across platforms.
(function () {
    const existing = window.SpokesBridge || {};

    function detectIsMobile() {
        return Boolean(
            (typeof window.isCapacitorNative === 'function' && window.isCapacitorNative()) ||
            Boolean(window.Capacitor?.isNativePlatform?.()) ||
            Boolean(window.SpokesNative && (typeof window.SpokesNative.openServerSwitcher === 'function' || typeof window.SpokesNative.openServer === 'function')) ||
            Boolean(window.webkit?.messageHandlers?.SpokesNative_shell || window.webkit?.messageHandlers?.SpokesNative) ||
            (typeof navigator !== 'undefined' && navigator.userAgent && navigator.userAgent.includes('Capacitor')) ||
            (typeof window.location !== 'undefined' && typeof window.location.search === 'string' && window.location.search.includes('client=mobile'))
        );
    }

    const isMobile = detectIsMobile();

    const isDesktop = !isMobile && Boolean(
        (typeof window.isSpokesDesktop !== 'undefined' && window.isSpokesDesktop) ||
        Boolean(window.SpokesDesktopNative) ||
        existing.isDesktop === true ||
        existing.platform === 'desktop' ||
        (typeof navigator !== 'undefined' && navigator.userAgent && /SpokesDesktop|Electron/i.test(navigator.userAgent))
    );

    const platform = isDesktop ? 'desktop' : (isMobile ? 'mobile' : 'web');

    function refreshPlatformFlags() {
        const currentMobile = detectIsMobile();
        if (currentMobile && !window.SpokesBridge.isMobile) {
            window.SpokesBridge.isMobile = true;
            window.SpokesBridge.isDesktop = false;
            window.SpokesBridge.isWeb = false;
            window.SpokesBridge.platform = 'mobile';
        }
        return window.SpokesBridge.isMobile;
    }

    window.SpokesBridge = Object.assign({}, existing, {
        platform: platform,
        isDesktop: isDesktop,
        isMobile: isMobile,
        isWeb: !isDesktop && !isMobile,
        version: existing.version || '1.0.0',

        // Platform query
        getPlatform: function () {
            refreshPlatformFlags();
            return this.platform;
        },

        // Device identification
        getDeviceId: async function () {
            if (existing.getDeviceId) {
                try {
                    const devId = await existing.getDeviceId();
                    if (devId) return devId;
                } catch (e) {
                    console.warn('[SpokesBridge] Error calling existing.getDeviceId():', e);
                }
            }
            if (window.pushNotifications && typeof window.pushNotifications.getDeviceId === 'function') {
                return await window.pushNotifications.getDeviceId();
            }
            let devId = localStorage.getItem('spokes_device_id');
            if (!devId || devId.startsWith('dev_')) {
                devId = (typeof crypto !== 'undefined' && crypto.randomUUID ? crypto.randomUUID() : 'xxxxxxxx-xxxx-4xxx-yxxx-xxxxxxxxxxxx'.replace(/[xy]/g, function(c) {
                    var r = Math.random() * 16 | 0, v = c === 'x' ? r : (r & 0x3 | 0x8);
                    return v.toString(16);
                }));
                localStorage.setItem('spokes_device_id', devId);
            }
            return devId;
        },

        // Safe clipboard copy
        copyText: async function (text) {
            if (existing.copyText) {
                return await existing.copyText(text);
            }
            if (window.Capacitor?.Plugins?.Clipboard) {
                try {
                    await window.Capacitor.Plugins.Clipboard.write({ string: text });
                    return true;
                } catch (e) {
                    console.warn('[SpokesBridge] Capacitor clipboard write failed, trying fallback:', e);
                }
            }
            if (navigator.clipboard && window.isSecureContext && !navigator.userAgent.match(/ipad|iphone|macintosh/i)) {
                await navigator.clipboard.writeText(text);
                return true;
            }
            // Resilient DOM fallback
            const textArea = document.createElement('textarea');
            textArea.value = text;
            textArea.style.position = 'fixed';
            textArea.style.left = '-999999px';
            textArea.style.top = '-999999px';
            document.body.appendChild(textArea);
            textArea.focus();
            textArea.select();
            try {
                const res = document.execCommand('copy');
                if (!res) throw new Error('execCommand copy returned false');
                return true;
            } finally {
                textArea.remove();
            }
        },

        // External browser navigation
        openExternal: async function (url) {
            if (existing.openExternal) {
                return await existing.openExternal(url);
            }
            if (window.Capacitor?.Plugins?.Browser) {
                try {
                    return await window.Capacitor.Plugins.Browser.open({ url: url });
                } catch (e) {
                    console.warn('[SpokesBridge] Capacitor Browser.open failed:', e);
                }
            }
            window.open(url, '_blank', 'noopener,noreferrer');
        },

        // Badge count updates
        setBadgeCount: async function (count) {
            if (existing.setBadgeCount) {
                try {
                    await existing.setBadgeCount(count);
                } catch (e) {
                    console.warn('[SpokesBridge] Native setBadgeCount error:', e);
                }
            }
            if (window.pushNotifications && typeof window.pushNotifications.updateBadge === 'function') {
                try {
                    await window.pushNotifications.updateBadge(count);
                } catch (e) {}
            } else if ('setAppBadge' in navigator) {
                try {
                    if (count > 0) await navigator.setAppBadge(count);
                    else await navigator.clearAppBadge();
                } catch (e) {}
            }
        },

        // Haptics & tactile feedback
        triggerHaptic: async function (style = 'LIGHT') {
            if (window.Capacitor?.Plugins?.Haptics) {
                try {
                    await window.Capacitor.Plugins.Haptics.impact({ style: style });
                    return;
                } catch (e) {}
            }
            if (typeof navigator.vibrate === 'function') {
                navigator.vibrate(style === 'HEAVY' ? 40 : (style === 'MEDIUM' ? 25 : 15));
            }
        },

        // Mobile status bar & UI style
        setStatusBarStyle: async function (isDarkMode) {
            if (typeof window.setCapacitorStatusBarStyle === 'function') {
                await window.setCapacitorStatusBarStyle(isDarkMode);
            }
        },

        // Hardware presence & idle time
        getSystemIdleTime: async function () {
            if (existing.getSystemIdleTime) {
                return await existing.getSystemIdleTime();
            }
            return 0;
        },

        onPresenceChanged: function (callback) {
            if (existing.onPresenceChanged) {
                return existing.onPresenceChanged(callback);
            }
            return function () {};
        },

        // Native notification display (unfocused alerts)
        showNotification: async function (payload) {
            if (existing.showNotification) {
                return await existing.showNotification(payload);
            }
            if (typeof Notification !== 'undefined' && Notification.permission === 'granted') {
                new Notification(payload.title, {
                    body: payload.body,
                    icon: payload.icon,
                    silent: payload.silent
                });
            }
        },

        // FCM / NativeRelay push subscription token
        getFcmSubscription: async function () {
            if (existing.getFcmSubscription) {
                return await existing.getFcmSubscription();
            }
            return null;
        },

        // Active voice call state reporting & cross-server single-call handoff
        setVoiceCallState: function (state) {
            state = state || {};
            if (existing.setVoiceCallState) {
                try {
                    existing.setVoiceCallState(state);
                    return;
                } catch (e) {
                    console.warn('[SpokesBridge] Error calling existing.setVoiceCallState():', e);
                }
            }
            if (window.SpokesNative && typeof window.SpokesNative.setVoiceCallState === 'function') {
                try {
                    window.SpokesNative.setVoiceCallState(Boolean(state.active), Boolean(state.isMuted));
                } catch (e) {
                    console.warn('[SpokesBridge] Error calling SpokesNative.setVoiceCallState():', e);
                }
            }
        },

        prepareVoiceConnect: async function () {
            if (existing.prepareVoiceConnect) {
                try {
                    await existing.prepareVoiceConnect();
                    return;
                } catch (e) {
                    console.warn('[SpokesBridge] Error calling existing.prepareVoiceConnect():', e);
                }
            }
            if (window.SpokesNative && typeof window.SpokesNative.prepareVoiceConnect === 'function') {
                try {
                    await window.SpokesNative.prepareVoiceConnect();
                } catch (e) {
                    console.warn('[SpokesBridge] Error calling SpokesNative.prepareVoiceConnect():', e);
                }
            }
        },

        focusVoiceWindow: async function () {
            if (existing.focusVoiceWindow) {
                try {
                    return await existing.focusVoiceWindow();
                } catch (e) {
                    console.warn('[SpokesBridge] Error calling existing.focusVoiceWindow():', e);
                }
            }
            try {
                window.focus();
            } catch (e) {}
        },

        // Batched Blazor MainLayout initialization (single WebSocket round-trip on firstRender)
        initLayout: async function (dotNetRef, options) {
            options = options || {};
            refreshPlatformFlags();

            if (window.startupDiagnostics && typeof window.startupDiagnostics.reportInteractive === 'function') {
                try {
                    window.startupDiagnostics.reportInteractive(
                        options.userId || null,
                        options.avatarLength || 0,
                        options.serverTimings || []
                    );
                } catch (e) {
                    console.warn('[SpokesBridge] reportInteractive failed:', e);
                }
            }

            window._spokesPageUnloading = false;
            if (options.subscriptionId) {
                window._currentSubscriptionId = options.subscriptionId;
            }
            window._currentPresenceToken = (typeof options.presenceToken === 'string' && options.presenceToken.length > 0)
                ? options.presenceToken
                : null;

            if (dotNetRef) {
                window.spokesDotNetRef = dotNetRef;
                try { if (window.spokesChatDiagnostics?.isEnabled?.()) window.spokesChatDiagnostics.attachServer(dotNetRef); } catch (e) {}
                try { if (typeof window.registerCtrlK === 'function') window.registerCtrlK(dotNetRef); } catch (e) {}
                try { if (typeof window.registerWindowFocus === 'function') window.registerWindowFocus(dotNetRef); } catch (e) {}
                try { if (typeof window.registerActivityTracker === 'function') window.registerActivityTracker(dotNetRef); } catch (e) {}
                if (Boolean(window.SpokesBridge.isMobile) && typeof window.ensureShareScriptsLoaded === 'function') {
                    window.ensureShareScriptsLoaded().then(function () {
                        try { if (typeof window.registerShareTarget === 'function') window.registerShareTarget(dotNetRef); } catch (e) {}
                    }).catch(function () {});
                } else {
                    try { if (typeof window.registerShareTarget === 'function') window.registerShareTarget(dotNetRef); } catch (e) {}
                }
                try { if (typeof window.registerImageRecoveryCoordinator === 'function') window.registerImageRecoveryCoordinator(dotNetRef); } catch (e) {}
                if (window.pushNotifications) {
                    try {
                        if (typeof window.pushNotifications.initializeNativePushListeners === 'function') {
                            window.pushNotifications.initializeNativePushListeners(dotNetRef).catch(function () {});
                        }
                    } catch (e) {}
                }
            }

            if (typeof window.setCapacitorStatusBarStyle === 'function') {
                try {
                    window.setCapacitorStatusBarStyle(Boolean(options.isDarkMode)).catch(function () {});
                } catch (e) {}
            }

            var pn = window.pushNotifications;
            var isCapacitorApp = Boolean(window.SpokesBridge.isMobile);
            var isDesktopApp = Boolean(window.SpokesBridge.isDesktop);
            var deviceType = isDesktopApp
                ? 'Desktop'
                : (isCapacitorApp
                    ? 'Mobile'
                    : (pn && typeof pn.detectDeviceType === 'function' ? pn.detectDeviceType() : 'Desktop'));
            var isPushSupported = pn && typeof pn.isSupported === 'function' ? Boolean(pn.isSupported()) : false;
            var requiresPushRelay = pn && typeof pn.requiresPushRelay === 'function'
                ? Boolean(pn.requiresPushRelay())
                : (isCapacitorApp || isDesktopApp);
            var hasBeenPromptedForPush = pn && typeof pn.hasBeenPromptedForPush === 'function'
                ? Boolean(pn.hasBeenPromptedForPush(options.vapidPublicKey || ''))
                : false;
            var permissionStatus = 'default';
            if (!isCapacitorApp && pn && typeof pn.getPermissionStatus === 'function') {
                try {
                    var rawPerm = pn.getPermissionStatus();
                    permissionStatus = (rawPerm && typeof rawPerm.then === 'function')
                        ? String(await rawPerm)
                        : String(rawPerm);
                } catch (e) {
                    permissionStatus = 'default';
                }
            }
            var storedVaultObj = null;
            if (options.needsVaultKey !== false && window.spokesVault && typeof window.spokesVault.getStoredVaultKey === 'function') {
                try {
                    storedVaultObj = await window.spokesVault.getStoredVaultKey();
                } catch (e) {
                    storedVaultObj = null;
                }
            }

            var viewportWidth = Math.round(window.innerWidth || (document.documentElement && document.documentElement.clientWidth) || 0);
            var storedVaultKey = (storedVaultObj && typeof storedVaultObj.key === 'string' && storedVaultObj.key.length > 0)
                ? storedVaultObj.key
                : null;

            return {
                platform: window.SpokesBridge.platform,
                isCapacitorApp: isCapacitorApp,
                isDesktopApp: isDesktopApp,
                deviceType: deviceType,
                isFocused: typeof window.isSpokesWindowFocused === 'function' ? Boolean(window.isSpokesWindowFocused()) : (document.visibilityState === 'visible'),
                existingSubscription: null,
                permissionStatus: permissionStatus,
                hasBeenPromptedForPush: hasBeenPromptedForPush,
                isPushSupported: isPushSupported,
                requiresPushRelay: requiresPushRelay,
                iosSoundDisabled: false,
                timeZone: (typeof Intl !== 'undefined' && Intl.DateTimeFormat) ? Intl.DateTimeFormat().resolvedOptions().timeZone : null,
                viewportWidth: viewportWidth,
                storedVaultKey: storedVaultKey
            };
        },

        // Deferred background probe for push subscription & native notification settings (post-TTI)
        probePushState: async function (options) {
            options = options || {};
            refreshPlatformFlags();
            var pn = window.pushNotifications;
            if (pn && typeof pn.setServerPushDisabled === 'function') {
                if (options.pushExplicitlyDisabled === true) {
                    pn.setServerPushDisabled(true);
                } else if (options.hasServerPush === true) {
                    pn.setServerPushDisabled(false);
                }
            }
            var isCapacitorApp = (typeof options.isCapacitorApp === 'boolean')
                ? options.isCapacitorApp
                : Boolean(window.SpokesBridge.isMobile);
            var [permissionStatus, existingSubscription, nativeSettings] = await Promise.all([(pn && typeof pn.getPermissionStatus === 'function') ? Promise.resolve(pn.getPermissionStatus()).catch(function () { return 'default'; }) : Promise.resolve('default'),
                (pn && typeof pn.getExistingSubscription === 'function')
                    ? Promise.resolve(pn.getExistingSubscription({ allowNativeTokenFetch: Boolean(options.hasServerPush) })).catch(function () { return null; })
                    : Promise.resolve(null),
                (isCapacitorApp && pn && typeof pn.getNativeSettings === 'function')
                    ? Promise.resolve(pn.getNativeSettings()).catch(function () { return null; })
                    : Promise.resolve(null)
            ]);
            var iosSoundDisabled = Boolean(
                nativeSettings && nativeSettings.authorizationStatus === 'authorized' && !nativeSettings.soundEnabled
            );
            return {
                existingSubscription: existingSubscription,
                iosSoundDisabled: iosSoundDisabled, permissionStatus: permissionStatus || 'default'
            };
        },

        // Batched Blazor MainLayout disposal
        disposeLayout: function () {
            window.spokesDotNetRef = null;
            window._currentPresenceToken = null;
            window._currentSubscriptionId = null;
            try { if (typeof window.unregisterCtrlK === 'function') window.unregisterCtrlK(); } catch (e) {}
            try { if (typeof window.unregisterWindowFocus === 'function') window.unregisterWindowFocus(); } catch (e) {}
            try { if (typeof window.unregisterActivityTracker === 'function') window.unregisterActivityTracker(); } catch (e) {}
            try { if (typeof window.unregisterShareTarget === 'function') window.unregisterShareTarget(); } catch (e) {}
            try {
                if (window.SpokesBridge && window.SpokesBridge.backStack) {
                    window.SpokesBridge.backStack.clearByPrefix('dialog:');
                    window.SpokesBridge.backStack.remove('mobileActionSheet', { popHistory: false });
                    window.SpokesBridge.backStack.remove('mobileEmojiPicker', { popHistory: false });
                }
            } catch (e) {}
        },

        // Unified LIFO Back-Dismissal Stack (Android Capacitor backButton + Web/iOS popstate)
        backStack: (function () {
            const _entries = [];
            let _nativeBackInterceptorActive = false;
            let _popStateBound = false;
            const _pendingSyntheticPopTimers = [];

            function currentHref() {
                try {
                    return (typeof window !== 'undefined' && window.location && window.location.href) ? window.location.href : '';
                } catch (e) {
                    return '';
                }
            }

            function clearDismissTimer(entry) {
                if (entry && entry.dismissTimer) {
                    clearTimeout(entry.dismissTimer);
                    entry.dismissTimer = null;
                }
            }

            function armHistoryGuard(entry) {
                if (!entry || entry.pushedHistory || _nativeBackInterceptorActive || !entry.allowDismiss) return;
                if (typeof window === 'undefined' || !window.history || typeof window.history.pushState !== 'function') return;
                ensurePopStateListener();
                try {
                    const stateObj = Object.assign({}, window.history.state || {}, { __spokesBackKey: entry.key });
                    window.history.pushState(stateObj, '');
                    entry.pushedHistory = true;
                    entry.openedHref = currentHref();
                } catch (e) {}
            }

            function invokeEntryBack(entry) {
                if (!entry || typeof entry.onBack !== 'function') return;
                try {
                    const result = entry.onBack();
                    if (entry.keepUntilRemoved) {
                        if (result === false) {
                            clearDismissTimer(entry);
                            entry.dismissing = false;
                        } else if (result && typeof result.then === 'function') {
                            result.then((accepted) => {
                                if (accepted === false) {
                                    clearDismissTimer(entry);
                                    entry.dismissing = false;
                                }
                            }).catch(() => {
                                clearDismissTimer(entry);
                                entry.dismissing = false;
                            });
                        }
                    }
                } catch (e) {
                    clearDismissTimer(entry);
                    entry.dismissing = false;
                    console.warn('[SpokesBridge.backStack] Back handler error:', e);
                }
            }

            function handleBack() {
                if (_entries.length === 0) {
                    return { handled: false };
                }

                const top = _entries[_entries.length - 1];
                if (!top.allowDismiss) {
                    return {
                        handled: true,
                        action: 'blocked-by-modal',
                        detail: top.source || top.key
                    };
                }

                if (top.dismissing) {
                    return {
                        handled: true,
                        action: 'dismiss-pending',
                        detail: top.source || top.key
                    };
                }

                const action = 'dismiss-' + (top.source || 'overlay');
                const detail = top.key;

                if (top.keepUntilRemoved) {
                    top.dismissing = true;
                    clearDismissTimer(top);
                    top.dismissTimer = setTimeout(() => {
                        top.dismissing = false;
                        top.dismissTimer = null;
                    }, 2000);
                    invokeEntryBack(top);
                } else {
                    _entries.pop();
                    clearDismissTimer(top);
                    invokeEntryBack(top);
                }

                return {
                    handled: true,
                    action: action,
                    detail: detail
                };
            }

            function onPopState() {
                if (_pendingSyntheticPopTimers.length > 0) {
                    const timer = _pendingSyntheticPopTimers.shift();
                    if (timer) clearTimeout(timer);
                    return;
                }

                if (_nativeBackInterceptorActive || _entries.length === 0) return;

                let guardedEntry = null;
                for (let i = _entries.length - 1; i >= 0; i--) {
                    if (_entries[i].pushedHistory) {
                        guardedEntry = _entries[i];
                        break;
                    }
                }
                if (!guardedEntry) return;

                const currentStateKey = (window.history && window.history.state && window.history.state.__spokesBackKey) || null;
                if (currentStateKey === guardedEntry.key) return;

                guardedEntry.pushedHistory = false;
                const topBefore = _entries[_entries.length - 1];
                handleBack();

                // If a child overlay above the guarded entry was dismissed (or a blocking modal consumed back),
                // re-arm the underlying history guard so the next back swipe dismisses it cleanly.
                const stillOpenAndNotDismissing = _entries.includes(guardedEntry) && !guardedEntry.dismissing;
                if (stillOpenAndNotDismissing && (topBefore !== guardedEntry || !guardedEntry.allowDismiss)) {
                    armHistoryGuard(guardedEntry);
                }
            }

            function ensurePopStateListener() {
                if (_popStateBound || typeof window === 'undefined' || typeof window.addEventListener !== 'function') return;
                window.addEventListener('popstate', onPopState);
                _popStateBound = true;
            }

            function push(key, onBack, options) {
                if (!key || typeof key !== 'string') return;
                const opts = options || {};
                const allowDismiss = opts.allowDismiss !== false;
                const useHistoryGuard = Boolean(opts.useHistoryGuard);
                const source = (typeof opts.source === 'string' && opts.source) ? opts.source : 'overlay';
                const keepUntilRemoved = Boolean(opts.keepUntilRemoved);

                let existing = null;
                const idx = _entries.findIndex((e) => e.key === key);
                if (idx !== -1) {
                    existing = _entries.splice(idx, 1)[0];
                    clearDismissTimer(existing);
                }

                const entry = {
                    key: key,
                    onBack: typeof onBack === 'function' ? onBack : null,
                    allowDismiss: allowDismiss,
                    useHistoryGuard: useHistoryGuard,
                    pushedHistory: existing ? existing.pushedHistory : false,
                    source: source,
                    keepUntilRemoved: keepUntilRemoved,
                    dismissing: false,
                    dismissTimer: null,
                    openedHref: existing ? existing.openedHref : currentHref()
                };

                _entries.push(entry);

                if (entry.useHistoryGuard) {
                    armHistoryGuard(entry);
                }
            }

            function registerServerEntry(key, dotNetRef, methodName, options) {
                const opts = options || {};
                const args = Array.isArray(opts.args)
                    ? opts.args
                    : (opts.arg !== undefined ? [opts.arg] : []);
                push(key, () => {
                    if (!dotNetRef || typeof dotNetRef.invokeMethodAsync !== 'function') {
                        return false;
                    }
                    return dotNetRef.invokeMethodAsync(methodName, ...args);
                }, Object.assign({ keepUntilRemoved: true }, opts));
            }

            function remove(key, options) {
                if (!key || typeof key !== 'string') return false;
                const idx = _entries.findIndex((e) => e.key === key);
                if (idx === -1) return false;

                const entry = _entries.splice(idx, 1)[0];
                clearDismissTimer(entry);

                const opts = options || {};
                const popHistory = Boolean(opts.popHistory);
                if (popHistory &&
                    entry.pushedHistory &&
                    !_nativeBackInterceptorActive &&
                    typeof window !== 'undefined' &&
                    window.history &&
                    typeof window.history.back === 'function' &&
                    window.history.state &&
                    window.history.state.__spokesBackKey === entry.key &&
                    currentHref() === entry.openedHref) {
                    const timer = setTimeout(() => {
                        const tIdx = _pendingSyntheticPopTimers.indexOf(timer);
                        if (tIdx !== -1) _pendingSyntheticPopTimers.splice(tIdx, 1);
                    }, 500);
                    _pendingSyntheticPopTimers.push(timer);
                    try {
                        window.history.back();
                    } catch (e) {
                        const tIdx = _pendingSyntheticPopTimers.indexOf(timer);
                        if (tIdx !== -1) _pendingSyntheticPopTimers.splice(tIdx, 1);
                        clearTimeout(timer);
                    }
                }

                return true;
            }

            function clearByPrefix(prefix) {
                if (!prefix || typeof prefix !== 'string') return 0;
                let removed = 0;
                for (let i = _entries.length - 1; i >= 0; i--) {
                    if (_entries[i].key.startsWith(prefix)) {
                        clearDismissTimer(_entries[i]);
                        _entries.splice(i, 1);
                        removed++;
                    }
                }
                return removed;
            }

            return {
                setNativeBackInterceptorActive: function (active) {
                    _nativeBackInterceptorActive = Boolean(active);
                },
                isNativeBackInterceptorActive: function () {
                    return _nativeBackInterceptorActive;
                },
                push: push,
                registerServerEntry: registerServerEntry,
                remove: remove,
                clearByPrefix: clearByPrefix,
                handleBack: handleBack,
                size: function () { return _entries.length; },
                peekKey: function () { return _entries.length > 0 ? _entries[_entries.length - 1].key : null; }
            };
        })(),

        // External Display (Screen Mirroring / AirPlay to TV / Android Smart View)
        externalDisplay: {
            isAvailable: async function () {
                const plugin = window.Capacitor?.Plugins?.BridgeManager || window.Capacitor?.Plugins?.ExternalDisplay;
                if (plugin) {
                    try {
                        const fn = plugin.checkExternalDisplayAvailability || plugin.checkAvailability;
                        if (typeof fn === 'function') {
                            const res = await fn.call(plugin);
                            return Boolean(res && res.isAvailable);
                        }
                    } catch (e) {
                        return false;
                    }
                }
                return false;
            },
            startPresentation: async function (url) {
                const plugin = window.Capacitor?.Plugins?.BridgeManager || window.Capacitor?.Plugins?.ExternalDisplay;
                if (plugin) {
                    try {
                        const fn = plugin.startExternalDisplayPresentation || plugin.startPresentation;
                        if (typeof fn === 'function') {
                            const res = await fn.call(plugin, { url: url });
                            return Boolean(res && res.success);
                        }
                    } catch (e) {
                        console.error('[SpokesBridge] startPresentation error:', e);
                        return false;
                    }
                }
                return false;
            },
            stopPresentation: async function () {
                const plugin = window.Capacitor?.Plugins?.BridgeManager || window.Capacitor?.Plugins?.ExternalDisplay;
                if (plugin) {
                    try {
                        const fn = plugin.stopExternalDisplayPresentation || plugin.stopPresentation;
                        if (typeof fn === 'function') {
                            await fn.call(plugin);
                            return true;
                        }
                    } catch (e) {
                        console.error('[SpokesBridge] stopPresentation error:', e);
                        return false;
                    }
                }
                return false;
            }
        }
    });

    window.spokesExternalDisplay = {
        checkAvailability: async function () {
            return await window.SpokesBridge.externalDisplay.isAvailable();
        },
        startPresentation: async function (url) {
            return await window.SpokesBridge.externalDisplay.startPresentation(url);
        },
        stopPresentation: async function () {
            return await window.SpokesBridge.externalDisplay.stopPresentation();
        },
        initListener: function (dotNetRef) {
            const plugin = window.Capacitor?.Plugins?.BridgeManager || window.Capacitor?.Plugins?.ExternalDisplay;
            if (!plugin || typeof plugin.addListener !== 'function') return null;
            try {
                return plugin.addListener('onExternalDisplayChange', function (data) {
                    if (dotNetRef) {
                        dotNetRef.invokeMethodAsync('OnExternalDisplayChanged', Boolean(data && data.isConnected));
                    }
                });
            } catch (e) {
                console.warn('[SpokesBridge] ExternalDisplay listener registration error:', e);
                return null;
            }
        }
    };

    // Backwards-compatible aliases
    window.isSpokesDesktop = isDesktop;
    window.spokesCopyText = window.SpokesBridge.copyText;
    window.triggerHaptic = window.SpokesBridge.triggerHaptic;

    // ─── LIGHTWEIGHT SESSION PERF TELEMETRY & ON-DEMAND CHAT PROFILER LOADER ───
    (function initPerformanceTelemetry() {
        const now = () => performance.timeOrigin + performance.now();
        const MEDIA_SETTLE_MS = 450, MAX_SESSION_SAMPLES = 300;

        let chatProfilerActive = false;
        let chatProfilerInstance = null;
        let chatProfilerLoadPromise = null;
        let mediaHook = null;
        let pendingMediaTapAnchorMs = null;

        function createEmptySession() {
            return {
                startedAtIso: new Date().toISOString(),
                startup: null,
                channelOpens: [],
                backNavigations: [],
                viewportFills: [],
                mediaBatches: [],
                mediaImages: [],
                mediaCounts: { registered: 0, loaded: 0, cacheHits: 0, memoryHits: 0, diskIdbHits: 0, networkLoads: 0, selfHealed: 0, retried: 0, errors: 0, aborted: 0, totalTransferBytes: 0 },
                longTasks: [],
                backActions: []
            };
        }
        let session = createEmptySession();

        let activeMediaBatch = null;
        let mediaSettleTimer = null;

        function getNetworkSummary() {
            try {
                const c = navigator.connection || navigator.mozConnection || navigator.webkitConnection;
                if (!c) return null;
                return {
                    effectiveType: c.effectiveType || 'unknown',
                    downlinkMbps: typeof c.downlink === 'number' ? c.downlink : null,
                    rttMs: typeof c.rtt === 'number' ? c.rtt : null,
                    saveData: Boolean(c.saveData)
                };
            } catch (_) { return null; }
        }

        function formatNetworkSummary(net) {
            if (!net) return 'unknown';
            const parts = [net.effectiveType];
            if (net.downlinkMbps != null) parts.push(`${net.downlinkMbps}Mbps`);
            if (net.rttMs != null) parts.push(`${net.rttMs}ms rtt`);
            if (net.saveData) parts.push('saveData');
            return parts.join(', ');
        }

        function percentile(arr, p) {
            if (!arr || arr.length === 0) return null;
            const s = [...arr].sort((a, b) => a - b);
            const idx = Math.min(s.length - 1, Math.max(0, Math.ceil((p / 100) * s.length) - 1));
            return Math.round(s[idx]);
        }

        function statsOf(arr) {
            if (!arr || arr.length === 0) return null;
            const s = [...arr].sort((a, b) => a - b);
            const sum = s.reduce((a, b) => a + b, 0);
            return {
                count: s.length,
                p50: percentile(s, 50),
                p95: percentile(s, 95),
                max: Math.round(s[s.length - 1]),
                avg: Math.round(sum / s.length)
            };
        }

        function fmtStats(st, unit = 'ms') {
            if (!st) return 'n/a';
            return `p50 ${st.p50}${unit} | p95 ${st.p95}${unit} | max ${st.max}${unit} (n=${st.count})`;
        }

        function pushCapped(arr, item, max = MAX_SESSION_SAMPLES) {
            arr.push(item);
            if (arr.length > max) arr.shift();
        }

        // ── Media batch telemetry (Lane 2 — silent by default; prints automatically only when profiler is active) ──
        function ensureMediaBatch(viewKey, concurrencyCap) {
            const normalizedView = viewKey || window.location.pathname || '/';
            if (activeMediaBatch && activeMediaBatch.view !== normalizedView) {
                flushMediaBatch('view-changed');
            }
            if (!activeMediaBatch) {
                const nowMs = now();
                const isInitialTapBatch = Boolean(pendingMediaTapAnchorMs && (nowMs - pendingMediaTapAnchorMs) < 15000);
                const anchorTapMs = isInitialTapBatch ? pendingMediaTapAnchorMs : nowMs;
                if (isInitialTapBatch) pendingMediaTapAnchorMs = null;
                activeMediaBatch = {
                    view: normalizedView,
                    startedMs: nowMs,
                    anchorTapMs: anchorTapMs,
                    isInitialTapBatch: isInitialTapBatch,
                    concurrencyCap: concurrencyCap || 0,
                    items: new Map(),
                    firstLoadedMs: null,
                    newestChatLoadedMs: null,
                    lastVisibleLoadedMs: null,
                    byPriority: {},
                    aborted: 0,
                    errors: 0,
                    retried: 0,
                    cacheHits: 0,
                    memoryHits: 0,
                    diskIdbHits: 0,
                    networkLoads: 0,
                    selfHealed: 0,
                    totalTransferBytes: 0,
                    printed: false
                };
            }
            if (concurrencyCap) activeMediaBatch.concurrencyCap = concurrencyCap;
            return activeMediaBatch;
        }

        function scheduleMediaSettleCheck() {
            if (mediaSettleTimer) clearTimeout(mediaSettleTimer);
            mediaSettleTimer = setTimeout(() => {
                mediaSettleTimer = null;
                if (!activeMediaBatch || activeMediaBatch.printed) return;
                let pendingVisible = 0;
                for (const item of activeMediaBatch.items.values()) {
                    if (item.visible && !item.finished) pendingVisible++;
                }
                if (pendingVisible === 0 && activeMediaBatch.items.size > 0) {
                    flushMediaBatch('settled');
                }
            }, MEDIA_SETTLE_MS);
        }

        function flushMediaBatch(reason) {
            if (mediaSettleTimer) {
                clearTimeout(mediaSettleTimer);
                mediaSettleTimer = null;
            }
            const batch = activeMediaBatch;
            activeMediaBatch = null;
            if (!batch || batch.printed || batch.items.size === 0) return;
            batch.printed = true;

            const loadedItems = [];
            let visibleTotal = 0;
            let visibleLoaded = 0;
            for (const item of batch.items.values()) {
                if (item.visible) {
                    visibleTotal++;
                    if (item.outcome === 'loaded') visibleLoaded++;
                }
                if (item.outcome === 'loaded') loadedItems.push(item);
            }
            if (loadedItems.length === 0 && batch.aborted === 0 && batch.errors === 0) return;

            const loadDurs = loadedItems.map(x => x.loadMs).filter(v => typeof v === 'number');
            const waitDurs = loadedItems.map(x => x.queueWaitMs).filter(v => typeof v === 'number');
            const ttfbDurs = loadedItems.map(x => x.ttfbMs).filter(v => typeof v === 'number');
            const dlDurs = loadedItems.map(x => x.downloadMs).filter(v => typeof v === 'number');

            const tapToFirst = batch.firstLoadedMs != null ? Math.max(0, Math.round(batch.firstLoadedMs - batch.anchorTapMs)) : null;
            const tapToNewest = batch.newestChatLoadedMs != null ? Math.max(0, Math.round(batch.newestChatLoadedMs - batch.anchorTapMs)) : null;
            const tapToAllVisible = batch.lastVisibleLoadedMs != null ? Math.max(0, Math.round(batch.lastVisibleLoadedMs - batch.anchorTapMs)) : null;

            const failedItems = [];
            for (const it of batch.items.values()) {
                if (it.outcome === 'error') {
                    failedItems.push({
                        priority: it.priority,
                        order: it.order,
                        isSmallThumb: it.isSmallThumb,
                        status: it.errorStatus ?? 0,
                        reason: it.errorReason || 'unknown',
                        path: it.errorPath || ''
                    });
                }
            }

            const batchSummary = {
                view: batch.view,
                reason: reason,
                isInitialTapBatch: Boolean(batch.isInitialTapBatch),
                concurrencyCap: batch.concurrencyCap,
                totalRegistered: batch.items.size,
                visibleTotal,
                visibleLoaded,
                loaded: loadedItems.length,
                cacheHits: batch.cacheHits,
                memoryHits: batch.memoryHits,
                diskIdbHits: batch.diskIdbHits,
                networkLoads: batch.networkLoads,
                selfHealed: batch.selfHealed,
                retried: batch.retried,
                errors: batch.errors,
                aborted: batch.aborted,
                totalTransferKB: Math.round((batch.totalTransferBytes / 1024) * 10) / 10,
                tapToFirstImgMs: tapToFirst,
                tapToNewestChatImgMs: tapToNewest,
                tapToAllVisibleMs: tapToAllVisible,
                loadStats: statsOf(loadDurs),
                queueWaitStats: statsOf(waitDurs),
                ttfbStats: statsOf(ttfbDurs),
                downloadStats: statsOf(dlDurs),
                failedItems
            };
            pushCapped(session.mediaBatches, batchSummary, 100);

            if (!chatProfilerActive) return;

            const prioParts = Object.entries(batch.byPriority).map(([k, v]) => `${k}: ${v}`).join(', ');
            const netStr = formatNetworkSummary(getNetworkSummary());
            const lines = [];
            lines.push('==================================================');
            lines.push(`[MEDIA LOAD REPORT] ${batch.view} (${prioParts || 'images'}) | concurrency=${batch.concurrencyCap || '?'} | net=${netStr}${reason !== 'settled' ? ` | ${reason}` : ''}`);
            lines.push('--------------------------------------------------');
            const mrow = (label, val) => lines.push(`  ${label.padEnd(40)}: ${val}`);
            const anchorLabel = batch.isInitialTapBatch ? 'Tap' : 'Batch';
            const cacheBreakdown = batch.cacheHits > 0
                ? ` (${batch.memoryHits} mem, ${batch.diskIdbHits} idb)`
                : '';
            mrow('Images (visible / total)', `${visibleLoaded}/${visibleTotal} visible loaded (${loadedItems.length}/${batch.items.size} total) | ${batch.cacheHits} cache${cacheBreakdown}, ${batch.networkLoads} net, ${batch.selfHealed} self-heal | ${batch.retried} retried, ${batch.errors} err, ${batch.aborted} aborted`);
            mrow(`${anchorLabel} → 1st image loaded`, tapToFirst != null ? `${tapToFirst}ms` : 'n/a');
            mrow(`${anchorLabel} → newest chat image loaded`, tapToNewest != null ? `${tapToNewest}ms` : 'n/a');
            mrow(`${anchorLabel} → all visible images loaded`, tapToAllVisible != null ? `${tapToAllVisible}ms` : 'n/a');
            const avgKb = batch.networkLoads > 0 ? ` (avg ${(batch.totalTransferBytes / 1024 / batch.networkLoads).toFixed(1)}KB/net img)` : '';
            mrow('Total transferred', `${(batch.totalTransferBytes / 1024).toFixed(1)}KB${avgKb}`);
            mrow('Per-image load (start → load)', fmtStats(batchSummary.loadStats));
            mrow('Queue wait (visible → start)', fmtStats(batchSummary.queueWaitStats));
            mrow('Network TTFB / download', `TTFB ${fmtStats(batchSummary.ttfbStats)} | DL ${fmtStats(batchSummary.downloadStats)}`);

            const slowest = [...loadedItems]
                .filter(x => typeof x.loadMs === 'number')
                .sort((a, b) => (b.loadMs || 0) - (a.loadMs || 0))
                .slice(0, 5);
            if (slowest.length > 0) {
                lines.push('  Slowest images:');
                slowest.forEach((it, idx) => {
                    const kbStr = it.transferSize != null ? `${(it.transferSize / 1024).toFixed(1)}KB` : (it.cacheHit ? (it.cacheTier || 'cache') : '?KB');
                    const srvStr = it.serverTiming
                        ? `, srv: ${it.serverTiming.map(s => `${s.name}=${s.dur}ms${s.desc ? `(${s.desc})` : ''}`).join(' ')}`
                        : '';
                    const thumbTag = it.isSmallThumb ? ' 400px' : '';
                    const cacheTag = it.cacheHit ? `, ${it.cacheTier || 'cache'}` : '';
                    lines.push(`    ${idx + 1}. [${it.priority}${thumbTag} #${it.order}] ${it.loadMs}ms (wait ${it.queueWaitMs ?? 0}ms, ttfb ${it.ttfbMs ?? '-'}ms, dl ${it.downloadMs ?? '-'}ms, ${kbStr}${cacheTag}${srvStr})`);
                });
            }
            if (failedItems.length > 0) {
                lines.push('  Failed images:');
                failedItems.slice(0, 10).forEach((fi, idx) => {
                    const thumbTag = fi.isSmallThumb ? ' 400px' : '';
                    const statusPart = fi.status ? `status=${fi.status}` : 'status=0';
                    lines.push(`    ${idx + 1}. [${fi.priority}${thumbTag} #${fi.order}] ${fi.reason} (${statusPart})${fi.path ? ` ${fi.path}` : ''}`);
                });
            }
            lines.push('==================================================');
            lines.forEach(l => console.log(l));
        }

        function recordMediaEvent(evt) {
            if (!evt || !evt.type) return;
            const batch = ensureMediaBatch(evt.view, evt.concurrencyCap);
            let item = batch.items.get(evt.seq);
            if (!item) {
                item = {
                    seq: evt.seq,
                    priority: evt.priority || 'chat',
                    order: evt.order || 0,
                    isSmallThumb: Boolean(evt.isSmallThumb),
                    visible: Boolean(evt.visible),
                    finished: false,
                    outcome: null,
                    queueWaitMs: null,
                    loadMs: null,
                    ttfbMs: null,
                    downloadMs: null,
                    transferSize: null,
                    cacheHit: false,
                    cacheTier: null,
                    selfHealed: false,
                    serverTiming: null
                };
                batch.items.set(evt.seq, item);
                batch.byPriority[item.priority] = (batch.byPriority[item.priority] || 0) + 1;
                session.mediaCounts.registered++;
            }
            if (evt.visible) item.visible = true;

            if (evt.type === 'loaded') {
                item.finished = true;
                item.outcome = 'loaded';
                item.queueWaitMs = evt.queueWaitMs;
                item.loadMs = evt.loadMs;
                item.ttfbMs = evt.ttfbMs;
                item.downloadMs = evt.downloadMs;
                item.transferSize = evt.transferSize;
                item.cacheHit = Boolean(evt.cacheHit);
                item.cacheTier = evt.cacheTier || (item.cacheHit ? 'http-cache' : 'network');
                item.selfHealed = Boolean(evt.selfHealed);
                item.serverTiming = evt.serverTiming || null;

                const ts = evt.timestampMs || now();
                const tierTag = item.cacheHit ? `, ${item.cacheTier}` : '';
                if (batch.firstLoadedMs == null) {
                    batch.firstLoadedMs = ts;
                    if (mediaHook) mediaHook.onFirstMediaLoaded(`Client: 1st Media Loaded ([${item.priority} #${item.order}] ${item.loadMs ?? 0}ms${tierTag})`, ts);
                }
                if (item.priority === 'chat' && item.order === 0 && batch.newestChatLoadedMs == null) {
                    batch.newestChatLoadedMs = ts;
                    if (mediaHook) mediaHook.onNewestChatImageLoaded(`Client: Newest Chat Image Loaded (${item.loadMs ?? 0}ms${tierTag})`, ts);
                }
                if (item.visible) {
                    batch.lastVisibleLoadedMs = Math.max(batch.lastVisibleLoadedMs || 0, ts);
                }

                session.mediaCounts.loaded++;
                if (item.cacheHit) {
                    batch.cacheHits++;
                    session.mediaCounts.cacheHits++;
                    if (item.cacheTier === 'memory') { batch.memoryHits++; session.mediaCounts.memoryHits++; }
                    else if (item.cacheTier === 'disk-idb') { batch.diskIdbHits++; session.mediaCounts.diskIdbHits++; }
                } else {
                    batch.networkLoads++;
                    session.mediaCounts.networkLoads++;
                }
                if (item.selfHealed) { batch.selfHealed++; session.mediaCounts.selfHealed++; }
                if (evt.retried) { batch.retried++; session.mediaCounts.retried++; }
                if (typeof evt.transferSize === 'number' && evt.transferSize > 0) {
                    batch.totalTransferBytes += evt.transferSize;
                    session.mediaCounts.totalTransferBytes += evt.transferSize;
                }

                pushCapped(session.mediaImages, {
                    priority: item.priority,
                    order: item.order,
                    isSmallThumb: item.isSmallThumb,
                    loadMs: item.loadMs,
                    queueWaitMs: item.queueWaitMs,
                    ttfbMs: item.ttfbMs,
                    downloadMs: item.downloadMs,
                    transferSize: item.transferSize,
                    cacheHit: item.cacheHit,
                    cacheTier: item.cacheTier,
                    selfHealed: item.selfHealed
                });

                if (mediaHook) mediaHook.onMediaItemLoaded(evt.transferSize || 0);
                scheduleMediaSettleCheck();
            } else if (evt.type === 'error') {
                item.finished = true;
                item.outcome = 'error';
                item.errorStatus = typeof evt.status === 'number' ? evt.status : 0;
                item.errorReason = evt.errorReason || 'unknown';
                item.errorPath = evt.path || '';
                batch.errors++;
                session.mediaCounts.errors++;
                scheduleMediaSettleCheck();
            } else if (evt.type === 'aborted') {
                item.finished = true;
                item.outcome = 'aborted';
                batch.aborted++;
                session.mediaCounts.aborted++;
                scheduleMediaSettleCheck();
            }
        }

        function buildBaselineObject() {
            const perfTelemetry = window.__spokesPerfTelemetry || null;
            const boot = session.startup || perfTelemetry?.boot || null;
            const nav = perfTelemetry?.navigation || null;
            const res = perfTelemetry?.resources || null;
            const startupMt = perfTelemetry?.mainThread || null;
            const openPainted = session.channelOpens.map(x => x.paintedMs).filter(v => typeof v === 'number');
            const openWarm = session.channelOpens.filter(x => x.diskReads === 0 && typeof x.paintedMs === 'number').map(x => x.paintedMs);
            const openCold = session.channelOpens.filter(x => (x.diskReads || 0) > 0 && typeof x.paintedMs === 'number').map(x => x.paintedMs);
            const openWireKB = session.channelOpens.map(x => x.wireKB).filter(v => typeof v === 'number');
            const openGaps = session.channelOpens.map(x => x.interOpenGapMs).filter(v => typeof v === 'number');
            const backPainted = session.backNavigations.map(x => x.listPaintedMs).filter(v => typeof v === 'number');
            const fillDurs = session.viewportFills.map(x => x.durationMs).filter(v => typeof v === 'number');

            const imgLoadDurs = session.mediaImages.map(x => x.loadMs).filter(v => typeof v === 'number');
            const imgWaitDurs = session.mediaImages.map(x => x.queueWaitMs).filter(v => typeof v === 'number');
            const imgTtfbDurs = session.mediaImages.map(x => x.ttfbMs).filter(v => typeof v === 'number');
            const initialTapBatches = session.mediaBatches.filter(x => x.isInitialTapBatch !== false);
            const batchFirstImg = initialTapBatches.map(x => x.tapToFirstImgMs).filter(v => typeof v === 'number');
            const batchNewestImg = initialTapBatches.map(x => x.tapToNewestChatImgMs).filter(v => typeof v === 'number');
            const batchAllVis = initialTapBatches.map(x => x.tapToAllVisibleMs).filter(v => typeof v === 'number');

            const backActionCounts = {};
            for (const a of session.backActions) {
                backActionCounts[a.action] = (backActionCounts[a.action] || 0) + 1;
            }

            return {
                capturedAt: new Date().toISOString(),
                sessionStartedAt: session.startedAtIso,
                platform: window.SpokesBridge?.platform || 'unknown',
                viewport: `${window.innerWidth}x${window.innerHeight}`,
                network: getNetworkSummary(),
                startup: boot ? {
                    clickToUsableMs: boot.clickToUsableMs ?? null,
                    shellTtiMs: boot.shellTtiMs ?? null,
                    navToUsableMs: boot.navToUsableMs ?? null,
                    settledRoute: boot.settledRoute || null,
                    wsExtensions: boot.wsExtensions || (window.__spokesBlazorSocket?.extensions || window.__spokesWsExtensions || ''),
                    htmlTransferKB: nav ? Math.round((nav.transferBytes / 1024) * 10) / 10 : null,
                    htmlDecodedKB: nav ? Math.round((nav.decodedBytes / 1024) * 10) / 10 : null,
                    protocol: nav?.protocol || null,
                    startupBatchSummary: boot.startupBatch?.summary || null,
                    blockingResourcesCount: res?.blockingHeadResources?.length ?? null,
                    startupLongTaskCount: startupMt?.longTaskCount ?? null,
                    startupMaxLongTaskMs: startupMt?.maxLongTaskMs ?? null
                } : null,
                channelOpen: {
                    total: session.channelOpens.length,
                    paintedMs: statsOf(openPainted),
                    warmCachePaintedMs: statsOf(openWarm),
                    coldDiskPaintedMs: statsOf(openCold),
                    wireKB: statsOf(openWireKB),
                    betweenOpensMs: statsOf(openGaps)
                },
                backNavigation: {
                    total: session.backNavigations.length,
                    listPaintedMs: statsOf(backPainted),
                    androidBackActions: backActionCounts
                },
                stage2ViewportFill: {
                    total: session.viewportFills.length,
                    durationMs: statsOf(fillDurs)
                },
                media: {
                    counts: Object.assign({}, session.mediaCounts, {
                        totalTransferKB: Math.round((session.mediaCounts.totalTransferBytes / 1024) * 10) / 10
                    }),
                    loadMs: statsOf(imgLoadDurs),
                    queueWaitMs: statsOf(imgWaitDurs),
                    ttfbMs: statsOf(imgTtfbDurs),
                    tapToFirstImgMs: statsOf(batchFirstImg),
                    tapToNewestChatImgMs: statsOf(batchNewestImg),
                    tapToAllVisibleMs: statsOf(batchAllVis),
                    batches: session.mediaBatches.slice(-20)
                },
                longTasks: {
                    count: session.longTasks.length,
                    totalMs: session.longTasks.reduce((a, b) => a + b, 0),
                    maxMs: session.longTasks.length ? Math.max(...session.longTasks) : 0
                },
                recentChannelOpens: session.channelOpens.slice(-20)
            };
        }

        function printBaselineSummary() {
            flushMediaBatch('summary');
            const b = buildBaselineObject();
            const lines = [];
            lines.push('==================================================');
            lines.push(`[PERF BASELINE SUMMARY] Platform: ${b.platform} | Viewport: ${b.viewport} | Net: ${formatNetworkSummary(b.network)}`);
            lines.push('--------------------------------------------------');
            const srow = (label, val) => lines.push(`  ${label.padEnd(40)}: ${val}`);
            if (b.startup) {
                srow('Startup TTI (clickToUsable / shellTti)', `${b.startup.clickToUsableMs ?? 'n/a'}ms / ${b.startup.shellTtiMs ?? 'n/a'}ms (${b.startup.settledRoute || 'app'})`);
                if (b.startup.htmlTransferKB != null || b.startup.wsExtensions !== undefined) {
                    const htmlPart = b.startup.htmlTransferKB != null
                        ? `${b.startup.htmlTransferKB}KB xfer / ${b.startup.htmlDecodedKB ?? '?'}KB raw (${b.startup.protocol || '?'})`
                        : 'n/a';
                    srow('Startup HTML / WS ext', `${htmlPart} | WS ext: ${b.startup.wsExtensions || 'none'}`);
                }
            }
            srow('Channel Open (all)', fmtStats(b.channelOpen.paintedMs));
            if (b.channelOpen.warmCachePaintedMs) srow('Channel Open (warm cache)', fmtStats(b.channelOpen.warmCachePaintedMs));
            if (b.channelOpen.coldDiskPaintedMs) srow('Channel Open (cold disk)', fmtStats(b.channelOpen.coldDiskPaintedMs));
            srow('Channel Open Wire KB', fmtStats(b.channelOpen.wireKB, 'KB'));
            srow('Time between channel opens', fmtStats(b.channelOpen.betweenOpensMs));
            srow('Back Navigation (list painted)', fmtStats(b.backNavigation.listPaintedMs));
            if (Object.keys(b.backNavigation.androidBackActions).length > 0) {
                srow('Android Back Actions', Object.entries(b.backNavigation.androidBackActions).map(([k, v]) => `${k}×${v}`).join(', '));
            }
            srow('Stage 2 Viewport Fill', fmtStats(b.stage2ViewportFill.durationMs));
            const mc = b.media.counts;
            srow('Images (loaded / cache / net / aborted)', `${mc.loaded} loaded (${mc.cacheHits} cache, ${mc.networkLoads} net, ${mc.selfHealed} self-heal), ${mc.retried} retried, ${mc.errors} err, ${mc.aborted} aborted, ${mc.totalTransferKB}KB`);
            srow('Image Load (start → load)', fmtStats(b.media.loadMs));
            srow('Image Queue Wait (visible → start)', fmtStats(b.media.queueWaitMs));
            srow('Image TTFB', fmtStats(b.media.ttfbMs));
            srow('Tap → 1st / newest / all visible img', `${b.media.tapToFirstImgMs?.p50 ?? 'n/a'}ms / ${b.media.tapToNewestChatImgMs?.p50 ?? 'n/a'}ms / ${b.media.tapToAllVisibleMs?.p50 ?? 'n/a'}ms (p50)`);
            srow('Main Thread Long Tasks', `${b.longTasks.count} tasks, ${b.longTasks.totalMs}ms total, max ${b.longTasks.maxMs}ms`);
            const tapDiag = typeof window.spokesTouch?.getDiagnostics === 'function' ? window.spokesTouch.getDiagnostics() : null;
            if (tapDiag && (tapDiag.rescuedTaps > 0 || tapDiag.suppressedDuplicates > 0 || tapDiag.abortedRipples > 0)) {
                srow('Touch Tap Reconciliation', `${tapDiag.rescuedTaps} rescued, ${tapDiag.nativeClicks} native, ${tapDiag.suppressedDuplicates} dup-shield, ${tapDiag.abortedRipples} aborted-ripples`);
            }
            lines.push('==================================================');
            lines.forEach(l => console.log(l));
            return b;
        }

        function loadChatProfilerScript() {
            if (typeof window.__spokesChatProfilerFactory === 'function') return Promise.resolve();
            if (chatProfilerLoadPromise) return chatProfilerLoadPromise;
            const url = window.__spokesChatProfilerUrl || '/js/spokes-chat-profiler.js';
            chatProfilerLoadPromise = new Promise((resolve, reject) => {
                const s = document.createElement('script');
                s.src = url;
                s.async = true;
                s.onload = () => resolve();
                s.onerror = () => { chatProfilerLoadPromise = null; reject(new Error('Failed to load ' + url)); };
                (document.head || document.documentElement).appendChild(s);
            });
            return chatProfilerLoadPromise;
        }

        async function enableChatDiagnostics() {
            await loadChatProfilerScript();
            if (!chatProfilerInstance) {
                chatProfilerInstance = window.__spokesChatProfilerFactory({
                    getSession: () => session,
                    now,
                    pushCapped,
                    getNetworkSummary,
                    formatNetworkSummary,
                    flushMediaBatch,
                    setPendingMediaTapAnchorMs: (t) => { pendingMediaTapAnchorMs = t; },
                    setMediaHook: (h) => { mediaHook = h; },
                    setProfilerActive: (flag) => { chatProfilerActive = Boolean(flag); }
                });
            }
            await chatProfilerInstance.enable(window.spokesDotNetRef);
            window.spokesChatDiagnostics.reports = chatProfilerInstance.reports;
            console.log('[Perf] UI, Chat & wire profiler ENABLED for this page session (resets to OFF on refresh). Use spokesChatDiagnostics.dump() for the last report, or spokesPerf.disableChatDiagnostics() to stop.');
            return true;
        }

        async function disableChatDiagnostics() {
            if (chatProfilerInstance) {
                await chatProfilerInstance.disable();
            } else {
                chatProfilerActive = false;
                if (window.spokesDotNetRef?.invokeMethodAsync) {
                    try { await window.spokesDotNetRef.invokeMethodAsync('SetChatProfilerEnabled', false); } catch (_) { }
                }
            }
            console.log('[Perf] UI, Chat & wire profiler DISABLED.');
            return false;
        }

        function printConsoleCommandsHelp() {
            if (typeof window.__spokesPrintConsoleCommands === 'function') {
                window.__spokesPrintConsoleCommands();
                return;
            }
            console.log([
                '[Spokes] Console Commands (wire profiler OFF by default; resets on page refresh):',
                '  spokesPerf.summary()                — Print session TTI & media baseline summary',
                '  spokesPerf.exportBaseline()         — Export structured JSON baseline metrics',
                '  spokesPerf.enableChatDiagnostics()  — Enable live UI, chat & wire profiler',
                '  spokesPerf.disableChatDiagnostics() — Disable UI, chat & wire profiler',
                '  spokesPerf.reset()                  — Clear session performance counters',
                '  spokesPerf.help()                   — Show this command list again'
            ].join('\n'));
        }

        window.spokesChatDiagnostics = {
            enable: enableChatDiagnostics,
            disable: disableChatDiagnostics,
            isEnabled: () => chatProfilerActive,
            attachServer: function (dotNetRef) { chatProfilerInstance?.attachServer(dotNetRef); },
            startTransition: function (name, initialStep, meta) { chatProfilerInstance?.startTransition(name, initialStep, meta); },
            mark: function (name) { chatProfilerInstance?.mark(name); },
            reports: [],
            dump: function () { return chatProfilerInstance ? chatProfilerInstance.dump() : 'null'; }
        };

        window.spokesPerf = {
            summary: printBaselineSummary,
            exportBaseline: function () { return JSON.stringify(buildBaselineObject(), null, 2); },
            reset: function () {
                flushMediaBatch('reset');
                session = createEmptySession();
                console.log('[Perf] Session baseline reset.');
            },
            enableChatDiagnostics: enableChatDiagnostics,
            disableChatDiagnostics: disableChatDiagnostics,
            isChatDiagnosticsEnabled: () => chatProfilerActive,
            help: printConsoleCommandsHelp,
            recordMediaEvent: recordMediaEvent,
            flushMediaBatch: flushMediaBatch,
            recordViewportFill: function (info) {
                if (!info) return;
                pushCapped(session.viewportFills, info);
            },
            recordBackAction: function (info) {
                if (!info) return;
                pushCapped(session.backActions, Object.assign({ t: Date.now() }, info));
            },
            onStartupSettled: function (boot) {
                session.startup = boot ? Object.assign({}, boot) : null;
            },
            tapDiagnostics: function () {
                return typeof window.spokesTouch?.getDiagnostics === 'function'
                    ? window.spokesTouch.getDiagnostics()
                    : null;
            },
            getSession: function () { return session; }
        };
    })();

    // Canonical delegated copy-code-btn handler (replaces ~1.3KB per-block inline onclick JS in ChatMarkdownRenderer)
    (function () {
        if (window.__spokesCodeCopyDelegated) return;
        window.__spokesCodeCopyDelegated = true;

        function copyTextWithFeedback(btn, text) {
            if (typeof window.spokesCopyText === 'function') {
                window.spokesCopyText(text);
            } else if (navigator.clipboard && typeof navigator.clipboard.writeText === 'function') {
                navigator.clipboard.writeText(text).catch(() => {});
            }
            const copiedLabel = btn.getAttribute('data-copied-label') || 'Copied';
            if (!btn.dataset.origHtml) {
                btn.dataset.origHtml = btn.innerHTML;
            }
            const originalHtml = btn.dataset.origHtml;
            btn.innerHTML = `<svg class="copy-code-icon" viewBox="0 0 24 24" aria-hidden="true"><use href="#spokes-icon-check" /></svg>${copiedLabel}`;
            if (btn._spokesCopyTimer) clearTimeout(btn._spokesCopyTimer);
            btn._spokesCopyTimer = setTimeout(() => {
                btn.innerHTML = originalHtml;
                btn._spokesCopyTimer = null;
            }, 2000);
        }

        function readFallbackCode(btn) {
            const wrapper = btn.parentElement;
            const target = wrapper ? (wrapper.querySelector('code') || wrapper.querySelector('pre')) : null;
            return target ? (target.textContent || '') : '';
        }

        window.spokesCopyCodeButton = function (btn) {
            if (!btn) return;
            const msgId = btn.getAttribute('data-copy-msg-id');
            if (msgId && typeof window.spokesCopyMessageCode === 'function') {
                const blockIdx = parseInt(btn.getAttribute('data-copy-code-idx') || '0', 10) || 0;
                Promise.resolve(window.spokesCopyMessageCode(msgId, blockIdx))
                    .then((fullCode) => copyTextWithFeedback(btn, fullCode || readFallbackCode(btn)))
                    .catch(() => copyTextWithFeedback(btn, readFallbackCode(btn)));
                return;
            }
            const rawCode = btn.getAttribute('data-raw-code');
            copyTextWithFeedback(btn, rawCode || readFallbackCode(btn));
        };

        document.addEventListener('click', (e) => {
            const target = e.target;
            if (!target || typeof target.closest !== 'function') return;

            const copyBtn = target.closest('.copy-code-btn');
            if (copyBtn) {
                window.spokesCopyCodeButton(copyBtn);
                return;
            }

            // Clicking an already-selected channel row scrolls the message list back to bottom with 0 server traffic.
            const selectedRow = target.closest('[data-sidebar-selected="true"]');
            if (selectedRow && !target.closest('button')) {
                const container = document.getElementById('chat-messages-container');
                if (container && Math.abs(container.scrollTop) > 4) {
                    container.scrollTo({ top: 0, behavior: 'smooth' });
                }
            }
        });
    })();

    // Reusable container-scoped infinite scroll sentinel observer (keyed per component instance, zero eval).
    window.spokesInfiniteScroll = (function () {
        const observers = new Map();

        function disconnect(key) {
            if (!key) return;
            const existing = observers.get(key);
            if (existing) {
                existing.observer.disconnect();
                observers.delete(key);
            }
        }

        function observe(key, scrollContainer, sentinel, dotNetHelper, methodName, rootMargin) {
            if (!key || !sentinel || !dotNetHelper) return;

            const rootEl = scrollContainer || null;
            const existing = observers.get(key);
            if (existing && existing.sentinel === sentinel && existing.root === rootEl) {
                return;
            }

            disconnect(key);

            let inFlight = false;
            const observer = new IntersectionObserver((entries) => {
                if (inFlight || !entries.some((e) => e.isIntersecting)) return;
                inFlight = true;
                Promise.resolve(dotNetHelper.invokeMethodAsync(methodName || 'LoadMore'))
                    .catch(() => { })
                    .finally(() => { inFlight = false; });
            }, {
                root: rootEl,
                rootMargin: rootMargin || '200px'
            });

            observer.observe(sentinel);
            observers.set(key, { observer, sentinel, root: rootEl });
        }

        return { observe, disconnect };
    })();
})();
