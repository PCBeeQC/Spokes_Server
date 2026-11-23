async function applyCapacitorStatusBarThemeFromStorage() {
    if (window._spokesHostViewActive === false) return true;
    const statusBar = window.Capacitor?.Plugins?.StatusBar;
    if (!statusBar) return false;

    let isDarkMode = true;
    let cached = null;
    try { cached = localStorage.getItem('spokes_is_dark_mode'); } catch (e) {}
    if (cached !== null && cached !== undefined) {
        isDarkMode = cached !== 'false';
    } else if (window.Capacitor.Plugins.Preferences) {
        try {
            const res = await window.Capacitor.Plugins.Preferences.get({ key: 'spokes_is_dark_mode' });
            if (res && res.value !== null && res.value !== undefined) {
                isDarkMode = res.value !== 'false';
                try { localStorage.setItem('spokes_is_dark_mode', res.value); } catch (e) {}
            }
        } catch (e) {}
    }

    await statusBar.setOverlaysWebView({ overlay: true });
    await statusBar.setStyle({ style: isDarkMode ? 'DARK' : 'LIGHT' });
    return true;
}

// --- FAST-BOOT STATUS BAR ---
// Native MainActivity.java / AppDelegate.swift style the status bar before WebView load.
// Sync user theme preference once StatusBar plugin is available without 10ms timer spam during parse.
(function fastBootStatusBar() {
    if (typeof window._spokesHostViewActive === 'undefined') {
        window._spokesHostViewActive = true;
    }
    if (window.Capacitor?.isNativePlatform?.()) {
        const tryApply = () => applyCapacitorStatusBarThemeFromStorage().catch(console.error);
        tryApply().then(applied => {
            if (!applied && document.readyState === 'loading') {
                document.addEventListener('DOMContentLoaded', () => { tryApply(); }, { once: true });
            }
        });
    }
})();

// Unified platform check
window.checkIsCapacitorNative = function () { return window.isCapacitorNative(); };
window.isCapacitorNative = function () {
    return navigator.userAgent.includes("Capacitor")
        || Boolean(window.Capacitor?.isNativePlatform?.())
        || Boolean(window.SpokesNative)
        || Boolean(window.webkit?.messageHandlers?.SpokesNative_shell || window.webkit?.messageHandlers?.SpokesNative)
        || (typeof window.location !== 'undefined' && typeof window.location.search === 'string' && window.location.search.includes('client=mobile'));
};

// Support dynamic theme toggling from Blazor
window.setCapacitorStatusBarStyle = async function (isDarkMode) {
    if (window.isCapacitorNative()) {
        try {
            const valStr = isDarkMode ? 'true' : 'false';
            try { localStorage.setItem('spokes_is_dark_mode', valStr); } catch (e) {}
            if (window._spokesHostViewActive === false) return;
            if (window.Capacitor?.Plugins?.StatusBar) {
                // Style.DARK means light text for dark backgrounds. Style.LIGHT means dark text for light backgrounds.
                const style = isDarkMode ? 'DARK' : 'LIGHT';
                await window.Capacitor.Plugins.StatusBar.setOverlaysWebView({ overlay: true });
                await window.Capacitor.Plugins.StatusBar.setStyle({ style: style });
            }
            if (window.Capacitor?.Plugins?.Preferences) {
                await window.Capacitor.Plugins.Preferences.set({
                    key: 'spokes_is_dark_mode',
                    value: valStr
                });
            }
        } catch (err) {
            console.error('[Capacitor-Init] Error applying StatusBar style/color:', err);
        }
    }
};

window.reapplyCapacitorStatusBarStyle = async function () {
    if (window.isCapacitorNative()) {
        try {
            await applyCapacitorStatusBarThemeFromStorage();
        } catch (err) {
            console.error('[Capacitor-Init] Error reapplying StatusBar style:', err);
        }
    }
};

// Host-view activation primitive for pooled multi-server native sessions (Android, iOS & Electron Desktop).
// Isolates presence focus, reconnect splash timers, and status-bar ownership per server view.
window.spokesSetServerViewActive = function (isActive, meta) {
    const active = Boolean(isActive);
    const hasEpoch = Boolean(meta && typeof meta.epoch === 'number' && Number.isFinite(meta.epoch));
    if (hasEpoch) {
        if (typeof window._spokesHostViewEpoch === 'number' && meta.epoch < window._spokesHostViewEpoch) {
            return;
        }
        window._spokesHostViewEpoch = meta.epoch;
    }
    if (meta && meta.nativeShellLifecycle === true) {
        window._spokesNativeShellLifecycle = true;
    }

    const changed = window._spokesHostViewActive !== active;
    window._spokesHostViewActive = active;
    window._spokesHostViewExplicitlyActivated = active;
    if (active) {
        window._spokesPageUnloading = false;
    }

    try {
        window.dispatchEvent(new CustomEvent('spokes:host-view-active-changed', {
            detail: {
                active: active,
                nativeShellLifecycle: Boolean(window._spokesNativeShellLifecycle),
                epoch: hasEpoch ? meta.epoch : null
            }
        }));
    } catch (e) {}

    if (active) {
        window._spokesShareNavigating = false;
        try { window.dispatchEvent(new CustomEvent('spokes:host-view-active')); } catch (e) {}
        if (changed && window.startupDiagnostics && typeof window.startupDiagnostics.recordHostViewSwitch === 'function') {
            try { window.startupDiagnostics.recordHostViewSwitch(window.__spokesPendingSwitchMeta || null); } catch (e) {}
            window.__spokesPendingSwitchMeta = null;
        }
        if (window.spokesResume && typeof window.spokesResume.onHostViewActivated === 'function') {
            try { window.spokesResume.onHostViewActivated(); } catch (e) {}
        }
        if (typeof window.reapplyCapacitorStatusBarStyle === 'function') {
            window.reapplyCapacitorStatusBarStyle();
        }
        if (changed) {
            if (typeof window.spokesCheckPendingShare === 'function') {
                try { window.spokesCheckPendingShare(); } catch (e) {}
            } else if (typeof window.checkPendingShares === 'function') {
                try { window.checkPendingShares(0); } catch (e) {}
            }
            if (typeof window.spokesCheckAppShortcuts === 'function') {
                try { window.spokesCheckAppShortcuts(); } catch (e) {}
            }
        }
    } else {
        if (window.spokesResume && typeof window.spokesResume.onHostViewDeactivated === 'function') {
            try { window.spokesResume.onHostViewDeactivated(); } catch (e) {}
        }
    }

    if (typeof window._forceUpdateFocusState === 'function') {
        try { window._forceUpdateFocusState(); } catch (e) {}
    }
};

// Unified in-circuit navigation primitive for native widgets, app shortcuts, push notifications, and server switches.
// Preserves live Blazor circuit state (channel, scroll, drafts) when switching back to a server at "/" or same route.
window.spokesNavigateFromNative = async function (targetRoute) {
    if (targetRoute === null || targetRoute === undefined) return false;
    const raw = String(targetRoute).trim();
    if (!raw) return false;

    let parsed;
    try {
        parsed = new URL(raw, window.location.origin);
    } catch (e) {
        return false;
    }

    // Cross-origin navigation delegates to native BridgeManager.openServer, which natively enforces saved-server membership
    if (parsed.origin !== window.location.origin &&
        parsed.origin !== 'capacitor://localhost' &&
        parsed.origin !== 'http://localhost') {
        if (parsed.protocol !== 'http:' && parsed.protocol !== 'https:') {
            console.warn('[Capacitor-Init] Rejected non-HTTP(S) cross-origin navigation target.');
            return false;
        }
        if (typeof window.spokesNotifyServerSwitchAway === 'function') {
            try { window.spokesNotifyServerSwitchAway(); } catch (e) {}
        }
        const fullTargetUrl = parsed.toString();
        if (window.Capacitor?.Plugins?.BridgeManager?.openServer) {
            try {
                await window.Capacitor.Plugins.BridgeManager.openServer({ url: fullTargetUrl });
                return true;
            } catch (e) {
                console.warn('[Capacitor-Init] BridgeManager.openServer rejected target:', e);
            }
        }
        return false;
    }

    const stripClientParam = (searchStr) => {
        const params = new URLSearchParams(searchStr || '');
        params.delete('client');
        return params.toString();
    };

    const normalizePath = (p) => {
        if (!p || p === '/') return '/';
        return p.endsWith('/') ? p.slice(0, -1) : p;
    };

    const targetPath = normalizePath(parsed.pathname);
    const targetQueryNoClient = stripClientParam(parsed.search);

    // Tapping a server shortcut widget ("/") when already mounted on an active page of this server
    // should simply bring the warm view forward without losing current channel, scroll, or drafts.
    if (targetPath === '/' && !targetQueryNoClient && !parsed.hash) {
        return false;
    }

    const currentPath = normalizePath(window.location.pathname);
    const currentQueryNoClient = stripClientParam(window.location.search);
    if (targetPath === currentPath && targetQueryNoClient === currentQueryNoClient && (parsed.hash || '') === (window.location.hash || '')) {
        return false;
    }

    if (window.isCapacitorNative() && !parsed.searchParams.has('client')) {
        parsed.searchParams.set('client', 'mobile');
    }
    const finalRoute = parsed.pathname + parsed.search + parsed.hash;

    if (window.spokesDotNetRef && !window._blazorDisconnected) {
        window.spokesPendingNav = finalRoute;
        window.pendingNotificationRoute = finalRoute;
        try {
            await window.spokesDotNetRef.invokeMethodAsync('NavigateTo', finalRoute);
            if (window.spokesPendingNav === finalRoute) {
                window.spokesPendingNav = null;
            }
            if (window.pendingNotificationRoute === finalRoute) {
                window.pendingNotificationRoute = null;
            }
            return true;
        } catch (e) {
            console.warn('[Capacitor-Init] In-circuit NavigateTo failed, queueing for circuit resume:', e);
            // If completeCircuitConnected or a newer navigation already consumed/superseded this route, do not re-queue.
            if (window.pendingNotificationRoute !== finalRoute) {
                return false;
            }
            // If the circuit already reconnected before this rejection settled, retry once immediately.
            if (window.spokesDotNetRef && !window._blazorDisconnected) {
                try {
                    await window.spokesDotNetRef.invokeMethodAsync('NavigateTo', finalRoute);
                    if (window.spokesPendingNav === finalRoute) {
                        window.spokesPendingNav = null;
                    }
                    if (window.pendingNotificationRoute === finalRoute) {
                        window.pendingNotificationRoute = null;
                    }
                    return true;
                } catch (retryErr) {
                    console.warn('[Capacitor-Init] In-circuit NavigateTo retry failed, keeping queued for circuit resume:', retryErr);
                    if (window.pendingNotificationRoute !== finalRoute) {
                        return false;
                    }
                }
            }
        }
    }

    // Circuit is still booting or reconnecting: queue route for initializeNativePushListeners / completeCircuitConnected / reloadNow
    window.spokesPendingNav = finalRoute;
    window.pendingNotificationRoute = finalRoute;
    if (window._blazorDisconnected && window.spokesResume && typeof window.spokesResume.onHostViewActivated === 'function') {
        try { window.spokesResume.onHostViewActivated(); } catch (e) {}
    }
    return true;
};

// window.triggerHaptic is provided by spokesbridge.js.

window.initCapacitorEnv = async function () {
    if (window._capacitorInitCompleted) {
        console.error('[DOUBLE-LOAD-DEBUG] initCapacitorEnv called AGAIN (already completed at:',
            window._capacitorInitCompletedAt, '). This indicates a page reload or duplicate script execution.',
            '| URL:', window.location.href,
            '| cookies: Session=' + document.cookie.includes('Spokes_Session_v3') + ', Refresh=' + document.cookie.includes('Spokes_Refresh'));
        console.trace('[DOUBLE-LOAD-DEBUG] initCapacitorEnv duplicate call stack');
    }
    window._capacitorInitStartedAt = Date.now();
    if (window.isCapacitorNative()) {
        try {
            // StatusBar initialization has been moved to the fastBootStatusBar helper at the top of the file for instant execution.


            // Intercept logout forms to append client=mobile so the backend knows to only clear local cookies
            document.addEventListener('submit', function(e) {
                if (e.target && e.target.action && e.target.action.endsWith('logout')) {
                    var input = document.createElement('input');
                    input.type = 'hidden';
                    input.name = 'client';
                    input.value = 'mobile';
                    e.target.appendChild(input);
                }
            });

            // Intercept clicks on external links and open them in the system browser
            // instead of navigating the WebView away from the app.
            // Same-origin links are NOT intercepted — they navigate inside the WebView as normal.
            // This does NOT affect the Casdoor SSO flow because openCapacitorBrowser() calls
            // Browser.open() directly and never triggers an anchor click event.
            document.addEventListener('click', async function(e) {
                var anchor = e.target.closest('a[href]');
                if (!anchor) return;

                var href = anchor.getAttribute('href');
                if (!href) return;

                // Only intercept external HTTP(S) links
                if (!href.startsWith('http://') && !href.startsWith('https://')) return;

                // Skip same-origin links (internal app navigation)
                try {
                    var linkUrl = new URL(href);
                    if (linkUrl.origin === window.location.origin) return;
                } catch (err) { return; }

                // Prevent the WebView from navigating away
                e.preventDefault();
                e.stopPropagation();

                // Open in system browser (Chrome Custom Tab / SFSafariViewController)
                console.log('[Capacitor-Init] Intercepted external link click:', href);
                if (window.Capacitor.Plugins && window.Capacitor.Plugins.Browser) {
                    await window.Capacitor.Plugins.Browser.open({ url: href });
                }
            }, true); // Capture phase to run before Blazor's event system

            // Register deep link and back button listeners
            if (Capacitor.Plugins.App) {
                if (window._capacitorBackButtonListener && typeof window._capacitorBackButtonListener.remove === 'function') {
                    try { await window._capacitorBackButtonListener.remove(); } catch (e) {}
                    window._capacitorBackButtonListener = null;
                }
                if (window._capacitorStatusBarStateListener && typeof window._capacitorStatusBarStateListener.remove === 'function') {
                    try { await window._capacitorStatusBarStateListener.remove(); } catch (e) {}
                    window._capacitorStatusBarStateListener = null;
                }

                // The auth-callback deep link is now handled entirely natively via AuthTabIntent.
                // We no longer manually intercept appUrlOpen for auth-callbacks.

                const isAndroidNativeBack = (() => {
                    try {
                        const capPlatform = typeof Capacitor.getPlatform === 'function' ? Capacitor.getPlatform() : null;
                        if (capPlatform) return capPlatform === 'android';
                    } catch (e) {}
                    return Boolean(Capacitor.isNativePlatform && Capacitor.isNativePlatform()) &&
                        /Android/i.test((typeof navigator !== 'undefined' && navigator.userAgent) || '');
                })();
                try {
                    window.SpokesBridge?.backStack?.setNativeBackInterceptorActive?.(isAndroidNativeBack);
                } catch (e) {}

                // Register hardware back button listener for Android
                window._capacitorBackButtonListener = await Capacitor.Plugins.App.addListener('backButton', () => {
                    const pathname = window.location.pathname || '/';
                    const pathSegments = pathname.split('/').filter(Boolean);
                    const historyLen = window.history ? window.history.length : 0;

                    const recordBack = (action, extra) => {
                        const info = Object.assign({
                            action: action,
                            pathname: pathname,
                            historyLength: historyLen,
                            hostViewActive: window._spokesHostViewActive !== false
                        }, extra || {});
                        console.log(`[Nav][AndroidBack] action=${info.action} path=${info.pathname} historyLen=${info.historyLength}` +
                            (info.detail ? ` detail=${info.detail}` : ''));
                        try { window.spokesPerf?.recordBackAction?.(info); } catch (e) {}
                    };

                    if (window._spokesHostViewActive === false) {
                        recordBack('ignored-inactive-host');
                        return;
                    }

                    // 1. Canonical unified back-dismissal stack (all MudDialogs including MediaViewerDialog,
                    //    stacked dialogs, blocking modals, voiceFullscreen, MobileActionSheet, mobile emoji picker).
                    const stackResult = window.SpokesBridge?.backStack?.handleBack?.();
                    if (stackResult && stackResult.handled) {
                        recordBack(stackResult.action || 'dismiss-overlay', { detail: stackResult.detail });
                        return;
                    }

                    // 2. Dismiss open lightweight popup/menu click-away backdrop (LazyMudMenu, MessageBubble menu, EmojiPicker)
                    const dismissiblePopups = document.querySelectorAll('[data-spokes-back-dismiss]');
                    if (dismissiblePopups.length > 0) {
                        const topPopup = dismissiblePopups[dismissiblePopups.length - 1];
                        const popupType = (typeof topPopup.getAttribute === 'function' && topPopup.getAttribute('data-spokes-back-dismiss')) || 'popup';
                        recordBack('dismiss-popup', { detail: popupType });
                        topPopup.click();
                        return;
                    }

                    // 3. Defensive safety net: if an unmanaged MudDialog is still in the DOM (e.g. circuit disconnected
                    //    or mid-mount before SpokesDialogProvider registered), close it or swallow Back so it can NEVER
                    //    fall through to exit the chat channel or minimize the app behind a visible modal.
                    const openDialogs = document.querySelectorAll('.mud-dialog-container');
                    if (openDialogs.length > 0) {
                        const topDialog = openDialogs[openDialogs.length - 1];
                        const closeBtn = topDialog.querySelector('button[aria-label="close"], button[aria-label="Close"], button.mud-button-close, .mud-dialog-header button');
                        if (closeBtn) {
                            recordBack('close-dialog');
                            closeBtn.click();
                            return;
                        }
                        const dialogBackdrop = topDialog.querySelector('.mud-overlay-dialog, .mud-overlay');
                        if (dialogBackdrop) {
                            recordBack('close-dialog-backdrop');
                            dialogBackdrop.click();
                            return;
                        }
                        recordBack('dialog-open-blocked');
                        return;
                    }

                    // 4. Close open temporary navigation drawer overlay if present
                    const drawerOverlay = document.querySelector('.mud-overlay-drawer');
                    if (drawerOverlay) {
                        recordBack('close-drawer');
                        drawerOverlay.click();
                        return;
                    }

                    // 5. DOM-first Chat Channel exit: when a chat channel is open on mobile, invoke the canonical
                    // in-app GoBack button directly instead of relying on WebView history.length or delayed URL state.
                    const inAppBack = document.getElementById('chat-mobile-back-btn');
                    const inAppBackVisible = Boolean(inAppBack && inAppBack.offsetParent !== null);
                    const channelOpenDom = Boolean(document.querySelector('.mud-main-content.chat-channel-open'));

                    if (inAppBackVisible) {
                        recordBack('chat-in-app-back', { detail: 'inAppBackBtn' });
                        try { window.spokesChatDiagnostics?.startTransition('NativeBack', 'Client: Native backButton (chat)'); } catch (e) {}
                        inAppBack.click();
                        return;
                    }

                    if (channelOpenDom || (pathSegments[0] === 'chat' && pathSegments.length > 1)) {
                        recordBack('chat-route-fallback', { detail: channelOpenDom ? 'channelOpenDom' : 'chatSubRoute' });
                        try { window.spokesChatDiagnostics?.startTransition('NativeBack', 'Client: Native backButton (chat-fallback)'); } catch (e) {}
                        if (typeof window.spokesNavigateFromNative === 'function') {
                            window.spokesNavigateFromNative('/chat');
                        } else {
                            window.history.back();
                        }
                        return;
                    }

                    // 6. Level 1 root pages (0 or 1 path segment, e.g. /, /chat, /projects, /albums)
                    if (pathSegments.length <= 1) {
                        recordBack('minimize-app');
                        if (typeof Capacitor.Plugins.App.minimizeApp === 'function') {
                            Capacitor.Plugins.App.minimizeApp();
                        } else {
                            Capacitor.Plugins.App.exitApp();
                        }
                        return;
                    }

                    // 7. Other sub-pages (/albums/{id}, /projects/{id}, etc.)
                    if (historyLen > 1) {
                        recordBack('history-back');
                        try { window.spokesChatDiagnostics?.startTransition('NativeBack', 'Client: Native backButton'); } catch (e) {}
                        window.history.back();
                    } else if (typeof window.spokesNavigateFromNative === 'function') {
                        const parentRoute = '/' + pathSegments[0];
                        recordBack('parent-route', { detail: parentRoute });
                        window.spokesNavigateFromNative(parentRoute);
                    } else {
                        recordBack('history-back-fallback');
                        window.history.back();
                    }
                });
                console.log('[Capacitor-Init] Registered native backButton listener successfully.');

                // Push notification tap listener (early intercept)
                if (window.Capacitor.Plugins.FirebaseMessaging) {
                    window.Capacitor.Plugins.FirebaseMessaging.addListener('notificationActionPerformed', async (action) => {
                         console.log('[Capacitor-Init] Push notification tapped', action);
                         
                         if (action && (action.actionId === 'decline' || action.actionId === 'decline_call')) {
                             return;
                         }
                         
                          // Deduplicate stuck intents that Android replays in rapid succession (< 5000ms)
                          const data = action.notification ? action.notification.data : null;
                          const uniqueKey = (data && (data.google_message_id || data.messageId || data.sentAt)) 
                              ? (data.google_message_id || data.messageId || data.sentAt)
                              : ((action.notification && action.notification.id) ? (action.notification.id + '') : null);
                          
                          if (uniqueKey) {
                              try {
                                  const now = Date.now();
                                  let recentRecords = JSON.parse(localStorage.getItem('spokes_recent_notif_taps') || '[]');
                                  // Keep only records within the last 5 seconds
                                  recentRecords = recentRecords.filter(r => (now - r.time) < 5000);
                                  if (recentRecords.some(r => r.key === uniqueKey)) {
                                      console.log('[Capacitor-Init] Ignoring duplicate notification tap event within 5s window:', uniqueKey);
                                      if (window.Capacitor.Plugins.FirebaseMessaging && window.Capacitor.Plugins.FirebaseMessaging.removeAllDeliveredNotifications) {
                                          window.Capacitor.Plugins.FirebaseMessaging.removeAllDeliveredNotifications().catch(() => {});
                                      }
                                      return;
                                  }
                                  recentRecords.push({ key: uniqueKey, time: now });
                                  localStorage.setItem('spokes_recent_notif_taps', JSON.stringify(recentRecords));
                              } catch(e) {
                                  console.error('[Capacitor-Init] Error in notification deduplication', e);
                              }
                          }

                         let targetUrl = (data && data.url) ? data.url : ((data && data.Url) ? data.Url : null);
                         const originUrl = (data && data.originUrl) ? data.originUrl : ((data && data.OriginUrl) ? data.OriginUrl : null);

                         if (targetUrl) {
                             if (!targetUrl.includes('client=mobile')) {
                                 targetUrl += (targetUrl.includes('?') ? '&' : '?') + 'client=mobile';
                             }

                             if (originUrl) {
                                 let targetOriginUrl;
                                 try {
                                     targetOriginUrl = new URL(originUrl);
                                 } catch (e) {
                                     targetOriginUrl = null;
                                 }
                                 if (!targetOriginUrl || (targetOriginUrl.protocol !== 'http:' && targetOriginUrl.protocol !== 'https:')) {
                                     console.warn('[Capacitor-Init] Rejected notification with non-HTTP(S) originUrl');
                                     return;
                                 }
                                 const targetOrigin = targetOriginUrl.origin.toLowerCase();
                                 const currentOrigin = window.location.origin.toLowerCase();
                                 
                                 if (targetOrigin !== currentOrigin && targetOrigin !== 'capacitor://localhost' && targetOrigin !== 'http://localhost') {
                                     let normalizedUrl;
                                     try {
                                         normalizedUrl = new URL(targetUrl, targetOrigin);
                                     } catch (e) {
                                         return;
                                     }
                                     if (normalizedUrl.origin.toLowerCase() !== targetOrigin) {
                                         console.warn('[Capacitor-Init] Rejected mismatched notification targetUrl origin');
                                         return;
                                     }
                                     if (typeof window.spokesNotifyServerSwitchAway === 'function') {
                                         try { window.spokesNotifyServerSwitchAway(); } catch (e) {}
                                     }
                                     const fullTargetUrl = normalizedUrl.toString();
                                     if (window.Capacitor?.Plugins?.BridgeManager?.openServer) {
                                         try {
                                             await window.Capacitor.Plugins.BridgeManager.openServer({ url: fullTargetUrl });
                                         } catch(e) {
                                             console.warn('[Capacitor-Init] Native openServer rejected notification target:', e);
                                         }
                                     }
                                     return;
                                 }
                             }
                             
                              // Same origin: navigate in-circuit without destroying live Blazor state
                              await window.spokesNavigateFromNative(targetUrl);
                          }
                    });
                    console.log('[Capacitor-Init] Registered native notificationActionPerformed listener successfully.');
                }
                
                window._capacitorStatusBarStateListener = await Capacitor.Plugins.App.addListener('appStateChange', (state) => {
                    console.log('[Capacitor-Init] App state changed. isActive:', state.isActive);
                    if (state.isActive) {
                        window.reapplyCapacitorStatusBarStyle();
                    }
                });

                if (Capacitor.Plugins.Browser) {
                    Capacitor.Plugins.Browser.addListener('browserFinished', () => {
                        console.log('[Capacitor-Init] browserFinished event received. User manually closed the system browser.');
                        // If the WebView was used as a logout proxy (which opens the browser), reset it to the login screen.
                        if (window.location.pathname.includes('/logout') || 
                            document.body.innerHTML.includes('Signing out') ||
                            document.body.innerHTML.includes('Logging out')) {
                            console.log('[Capacitor-Init] Resetting WebView to login screen.');
                            window.location.href = window.location.origin + '/sso/login/auto?client=mobile';
                        }
                    });
                }

            } else {
                console.error('[Capacitor-Init] App plugin is MISSING from Capacitor.Plugins!');
            }
        } catch (err) {
            console.error('[Capacitor-Init] Error applying Capacitor configuration:', err);
        }
    } else {
        console.log('[Capacitor-Init] Not running in a native wrapper. Running as PWA or Browser.');
    }
    window._capacitorInitCompleted = true;
    window._capacitorInitCompletedAt = Date.now();
};

// NOTE: initCapacitorEnv() is NO LONGER auto-triggered here.
// It is now called by runStartup() in App.razor AFTER Blazor starts,
// to avoid bridge contention with initDeviceId() on Android.
// See: App.razor runStartup() for the orchestrated boot sequence.

window.openCapacitorBrowser = async function (url) {
    console.log('[Capacitor-Init] openCapacitorBrowser called with URL:', url);
    
    const isProbablyNative = window.isCapacitorNative();
    
    if (isProbablyNative) {
        try {
            console.log('[Capacitor-Init] Waiting for Capacitor and Browser plugin to populate...');
            let attempts = 0;
            while ((!window.Capacitor || !window.Capacitor.Plugins?.Browser) && attempts < 20) {
                await new Promise(r => setTimeout(r, 100));
                attempts++;
            }

            if (window.Capacitor.Plugins && window.Capacitor.Plugins.Browser) {
                console.log('[Capacitor-Init] Browser plugin found after', attempts, 'attempts. Absolute URL construction...');
                const absoluteUrl = new URL(url, window.location.origin).href;
                console.log('[Capacitor-Init] Calling Browser.open with:', absoluteUrl);
                await window.Capacitor.Plugins.Browser.open({ url: absoluteUrl });
                console.log('[Capacitor-Init] Browser.open call completed successfully.');
            } else {
                console.error('[Capacitor-Init] Browser plugin was not found after waiting!');
                alert("Failed to load native browser plugin. Please restart the app.");
            }
        } catch (e) {
            console.error('[Capacitor-Init] Error opening Capacitor Browser:', e);
            alert("Failed to open the system browser: " + e.message);
        }
    } else {
        console.log('[Capacitor-Init] Not a native platform, falling back to window.location.href');
        window.location.href = url;
    }
};

// SSO-specific browser function that uses ASWebAuthenticationSession (iOS) / AuthTabIntent (Android)
// via the native SsoAuthPlugin. 
window.openSsoAuth = async function (url) {
    console.log('[Capacitor-Init] openSsoAuth PKCE flow starting...');
    const isProbablyNative = window.isCapacitorNative();

    if (!isProbablyNative) {
        console.log('[Capacitor-Init] Not a native platform, falling back to window.location.href');
        window.location.href = url || '/';
        return;
    }

    try {
        // Resolve SsoAuth plugin: register if needed
        let ssoPlugin = window.Capacitor?.Plugins?.SsoAuth;
        if (!ssoPlugin && window.Capacitor?.registerPlugin) {
            try {
                ssoPlugin = window.Capacitor.registerPlugin('SsoAuth');
            } catch (e) {
                console.warn('[Capacitor-Init] registerPlugin(SsoAuth) threw:', e);
            }
        }

        let attempts = 0;
        while ((!ssoPlugin || !window.spokesAuth?.generateCodeVerifier) && attempts < 20) {
            await new Promise(r => setTimeout(r, 100));
            attempts++;
            ssoPlugin = window.Capacitor?.Plugins?.SsoAuth || ssoPlugin;
            if (!ssoPlugin && window.Capacitor?.registerPlugin) {
                try {
                    ssoPlugin = window.Capacitor.registerPlugin('SsoAuth');
                } catch (e) {}
            }
        }

        if (!ssoPlugin || !window.spokesAuth?.generateCodeVerifier) {
            console.warn('[Capacitor-Init] SsoAuth or spokesAuth missing. Opening external browser tab.');
            if (window.openCapacitorBrowser) {
                await window.openCapacitorBrowser(url);
            } else {
                console.error('[Capacitor-Init] Neither SsoAuth nor Browser plugin is available.');
            }
            return;
        }

        // 0. Resolve stable device ID up front for challenge binding
        let deviceId = '';
        try {
            if (window.pushNotifications?.getDeviceId) {
                deviceId = await window.pushNotifications.getDeviceId();
            }
        } catch(e) {}
        if (!deviceId) {
            const origin = window.spokesAuth.getCanonicalOrigin();
            if (origin && window.Capacitor?.Plugins?.Preferences) {
                const devPrefKey = 'spokes_device_id::' + origin;
                try {
                    const existingDev = await window.Capacitor.Plugins.Preferences.get({ key: devPrefKey });
                    deviceId = existingDev?.value || '';
                } catch(e) {}
                if (!deviceId) {
                    deviceId = crypto.randomUUID ? crypto.randomUUID() : (Date.now().toString(36) + Math.random().toString(36).slice(2));
                    try { await window.Capacitor.Plugins.Preferences.set({ key: devPrefKey, value: deviceId }); } catch(e) {}
                }
            }
        }
        if (!deviceId) throw new Error('Device ID required for mobile authentication');

        // 1. Fetch Config & Server-Bound OAuth State/Nonce Challenge
        const configRes = await fetch('/spokesapi/auth/mobile/config');
        if (!configRes.ok) throw new Error('Failed to fetch mobile auth config');
        const config = await configRes.json();

        const challengeRes = await fetch('/spokesapi/auth/mobile/challenge', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ deviceId: deviceId }),
            cache: 'no-store'
        });
        if (!challengeRes.ok) throw new Error('Failed to initialize mobile auth challenge');
        const challengeData = await challengeRes.json();
        if (!challengeData || !challengeData.state || !challengeData.nonce) {
            throw new Error('Invalid mobile auth challenge response');
        }
        
        // 2. Generate PKCE Challenge
        const verifier = window.spokesAuth.generateCodeVerifier();
        const challenge = await window.spokesAuth.generateCodeChallenge(verifier);

        // Store the verifier so it can be used for the PKCE exchange after login
        sessionStorage.setItem('spokes_pkce_verifier', verifier);
        
        // 3. Build Auth URL using the discovered authorization endpoint and server challenge state/nonce
        const redirectUri = 'spokes://auth-callback';
        const authUrl = `${config.authorizationEndpoint}?client_id=${encodeURIComponent(config.clientId)}&redirect_uri=${encodeURIComponent(redirectUri)}&response_type=code&scope=${encodeURIComponent(config.scope)}&code_challenge=${encodeURIComponent(challenge)}&code_challenge_method=S256&state=${encodeURIComponent(challengeData.state)}&nonce=${encodeURIComponent(challengeData.nonce)}`;
        
        console.log('[Capacitor-Init] Calling SsoAuth.login via PKCE...');
        const result = await ssoPlugin.login({
            url: authUrl,
            callbackScheme: 'spokes',
            expectedState: challengeData.state
        });

        if (result && result.url) {
            console.log('[Capacitor-Init] SsoAuth.login resolved:', result.url);
            if (!String(result.url).toLowerCase().startsWith(redirectUri)) {
                throw new Error('Invalid OAuth callback URI');
            }
            const urlObj = new URL(result.url);
            const code = urlObj.searchParams.get('code');
            const returnedState = urlObj.searchParams.get('state');
            if (!returnedState || returnedState !== challengeData.state) {
                throw new Error('OAuth state mismatch');
            }
            
            if (code) {
                // 4. Exchange Code
                console.log('[Capacitor-Init] Exchanging code...');

                const exchangeRes = await fetch('/spokesapi/auth/mobile/exchange', {
                    method: 'POST',
                    headers: { 'Content-Type': 'application/json' },
                    body: JSON.stringify({
                        code: code,
                        codeVerifier: verifier,
                        redirectUri: redirectUri,
                        deviceId: deviceId,
                        state: challengeData.state
                    })
                });
                
                if (!exchangeRes.ok) {
                    const errText = await exchangeRes.text();
                    throw new Error('Exchange failed: ' + errText);
                }
                
                const exchangeData = await exchangeRes.json();
                
                // 5. Save Token & Refresh Session
                if (exchangeData.refreshToken) {
                    const saved = window.spokesAuth.setRefreshToken
                        ? await window.spokesAuth.setRefreshToken(exchangeData.refreshToken)
                        : false;
                    if (!saved) {
                        const currentOrigin = window.spokesAuth.getCanonicalOrigin();
                        if (!currentOrigin) throw new Error('Invalid server origin for token storage');
                        await window.Capacitor.Plugins.Preferences.set({ key: 'spokes_refresh::' + currentOrigin, value: exchangeData.refreshToken });
                    }
                    console.log('[Capacitor-Init] Token saved, refreshing session...');
                    if (window.spokesAuth.refreshSessionAsync) {
                        await window.spokesAuth.refreshSessionAsync();
                    }
                    window.location.replace('/chat?client=mobile');
                } else {
                    throw new Error('No refresh token in response');
                }
            } else {
                throw new Error('No code in callback URL');
            }
        }
    } catch (e) {
        console.error('[Capacitor-Init] openSsoAuth failed:', e);
        if (e && typeof e.message === 'string' && /cancel/i.test(e.message)) {
            console.log('[Capacitor-Init] User cancelled auth session.');
            return;
        }
        // Fall back to external browser tab rather than navigating the app's internal WebView to IdP
        if (window.openCapacitorBrowser) {
            console.log('[Capacitor-Init] Falling back to openCapacitorBrowser');
            await window.openCapacitorBrowser(url);
        }
    }
};

window.openSsoLogout = async function (url) {
    console.log('[Capacitor-Init] openSsoLogout called with URL:', url);
    const isProbablyNative = window.isCapacitorNative();
    
    if (!isProbablyNative) {
        window.location.href = url;
        return;
    }

    try {
        let plugin = window.Capacitor?.Plugins?.SsoAuth;
        if (!plugin && window.Capacitor?.registerPlugin) {
            try {
                plugin = window.Capacitor.registerPlugin('SsoAuth');
            } catch (e) {}
        }

        let attempts = 0;
        while ((!plugin) && attempts < 20) {
            await new Promise(r => setTimeout(r, 100));
            attempts++;
            plugin = window.Capacitor?.Plugins?.SsoAuth || plugin;
            if (!plugin && window.Capacitor?.registerPlugin) {
                try {
                    plugin = window.Capacitor.registerPlugin('SsoAuth');
                } catch (e) {}
            }
        }
        
        if (!plugin) {
            console.warn('[Capacitor-Init] SsoAuth plugin not available for logout. Falling back to openCapacitorBrowser.');
            if (window.openCapacitorBrowser) {
                return window.openCapacitorBrowser(url);
            }
            window.location.href = url;
            return;
        }

        const absoluteUrl = new URL(url, window.location.origin).href;
        console.log('[Capacitor-Init] Calling SsoAuth.logout with:', absoluteUrl);

        // SsoAuth.logout() always resolves — either the IdP redirected to
        // spokes://logout-complete (auto-dismiss) or the user dismissed manually.
        // Both mean the IdP session was destroyed when the end-session page loaded.
        await plugin.logout({ url: absoluteUrl, callbackScheme: 'spokes' });

        console.log('[Capacitor-Init] SsoAuth.logout completed.');
    } catch (e) {
        console.warn('[Capacitor-Init] SsoAuth.logout error (non-fatal):', e);
    }
    // Caller handles navigation after this returns
};

// Notify the native mobile outer shell (Android/iOS) whenever Blazor's navigation drawer opens/closes.
window.spokesNotifyNavDrawerOpen = function (isOpen) {
    try {
        if (window.SpokesNative && typeof window.SpokesNative.setNavDrawerOpen === 'function') {
            window.SpokesNative.setNavDrawerOpen(Boolean(isOpen));
        }
    } catch (e) {}
};

// window.spokesCopyText is provided by spokesbridge.js.
