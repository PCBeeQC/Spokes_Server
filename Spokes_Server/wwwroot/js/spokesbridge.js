// spokesbridge.js - Unified client bridge for Spokes (Desktop, Mobile, Web)
// Provides a single extensible entry point for native capabilities across platforms.
(function () {
    const existing = window.SpokesBridge || {};

    const isMobile = Boolean(
        (typeof window.isCapacitorNative === 'function' && window.isCapacitorNative()) ||
        Boolean(window.Capacitor?.isNativePlatform?.()) ||
        (typeof navigator !== 'undefined' && navigator.userAgent && navigator.userAgent.includes('Capacitor'))
    );

    const isDesktop = !isMobile && Boolean(
        (typeof window.isSpokesDesktop !== 'undefined' && window.isSpokesDesktop) ||
        existing.platform === 'desktop' ||
        (typeof navigator !== 'undefined' && navigator.userAgent && navigator.userAgent.includes('SpokesDesktop'))
    );

    const platform = isDesktop ? 'desktop' : (isMobile ? 'mobile' : 'web');

    window.SpokesBridge = Object.assign({}, existing, {
        platform: platform,
        isDesktop: isDesktop,
        isMobile: isMobile,
        isWeb: !isDesktop && !isMobile,
        version: existing.version || '1.0.0',

        // Platform query
        getPlatform: function () {
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

        // Batched Blazor MainLayout initialization (single WebSocket round-trip on firstRender)
        initLayout: async function (dotNetRef, options) {
            options = options || {};

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

            if (options.subscriptionId) {
                window._currentSubscriptionId = options.subscriptionId;
            }

            if (dotNetRef) {
                if (options.chatDiagnostics) { try { window.spokesChatDiagnostics?.attachServer(dotNetRef); } catch (e) {} } // DIAG-TEMP
                try { if (typeof window.registerCtrlK === 'function') window.registerCtrlK(dotNetRef); } catch (e) {}
                try { if (typeof window.registerWindowFocus === 'function') window.registerWindowFocus(dotNetRef); } catch (e) {}
                try { if (typeof window.registerActivityTracker === 'function') window.registerActivityTracker(dotNetRef); } catch (e) {}
                try { if (typeof window.registerShareTarget === 'function') window.registerShareTarget(dotNetRef); } catch (e) {}
                try { if (typeof window.registerImageRecoveryCoordinator === 'function') window.registerImageRecoveryCoordinator(dotNetRef); } catch (e) {}
                if (window.pushNotifications) {
                    try {
                        if (typeof window.pushNotifications.registerPresenceLayout === 'function') {
                            window.pushNotifications.registerPresenceLayout(dotNetRef);
                        }
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
            if (pn && typeof pn.getPermissionStatus === 'function') {
                try {
                    permissionStatus = String(await pn.getPermissionStatus());
                } catch (e) {}
            }

            var existingSubscription = null;
            if (pn && typeof pn.getExistingSubscription === 'function') {
                try {
                    existingSubscription = await pn.getExistingSubscription();
                } catch (e) {}
            }

            var iosSoundDisabled = false;
            if (isCapacitorApp && pn && typeof pn.getNativeSettings === 'function') {
                try {
                    var settings = await pn.getNativeSettings();
                    if (settings && settings.authorizationStatus === 'authorized' && !settings.soundEnabled) {
                        iosSoundDisabled = true;
                    }
                } catch (e) {}
            }

            return {
                platform: window.SpokesBridge.platform,
                isCapacitorApp: isCapacitorApp,
                isDesktopApp: isDesktopApp,
                deviceType: deviceType,
                isFocused: typeof window.isSpokesWindowFocused === 'function' ? Boolean(window.isSpokesWindowFocused()) : (document.visibilityState === 'visible'),
                existingSubscription: existingSubscription,
                permissionStatus: permissionStatus,
                hasBeenPromptedForPush: hasBeenPromptedForPush,
                isPushSupported: isPushSupported,
                requiresPushRelay: requiresPushRelay,
                iosSoundDisabled: iosSoundDisabled,
                timeZone: (typeof Intl !== 'undefined' && Intl.DateTimeFormat) ? Intl.DateTimeFormat().resolvedOptions().timeZone : null
            };
        },

        // Batched Blazor MainLayout disposal
        disposeLayout: function () {
            try { if (typeof window.unregisterCtrlK === 'function') window.unregisterCtrlK(); } catch (e) {}
            try { if (typeof window.unregisterWindowFocus === 'function') window.unregisterWindowFocus(); } catch (e) {}
            try { if (typeof window.unregisterActivityTracker === 'function') window.unregisterActivityTracker(); } catch (e) {}
            try { if (typeof window.unregisterShareTarget === 'function') window.unregisterShareTarget(); } catch (e) {}
            try {
                if (window.pushNotifications && typeof window.pushNotifications.stopIdleDetector === 'function') {
                    window.pushNotifications.stopIdleDetector();
                }
            } catch (e) {}
        },

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

    // ─── CHAT TRANSITION DIAGNOSTICS (CLIENT CONSOLE ONLY) ─────────────────────
    // DIAG-TEMP: dormant until attachServer() is called from SpokesBridge.initLayout
    // (gated server-side by ChatDiagnostics.Enabled). Strip together with the server DIAG-TEMP code.
    (function initChatDiagnostics() {
        const now = () => performance.timeOrigin + performance.now();
        const QUIET_MS = 1500, SERVER_QUIET_MS = 500, HARD_CAP_MS = 15000, PROBE_MS = 250, MAX_REPORTS = 10;

        let server = null;            // DotNetObjectReference<MainLayout>
        let seq = 0;
        let reportCounter = 0;
        let current = null;           // report receiving client/wire marks
        const open = [];              // reports not yet printed
        const finished = [];          // last MAX_REPORTS printed reports
        let lastPointerDown = null, lastPointerUp = null;
        let mutationObserver = null, longTaskObserver = null, probeTimer = null, tickTimer = null;
        const pendingJsCalls = new Map(); // asyncHandle -> { identifier, t }

        // ── DOM state helpers ──
        const isAppBarHidden = () => document.querySelector('.mud-appbar')?.classList.contains('hide-appbar-mobile') || false;
        const isMainContentChannelOpen = () => document.querySelector('.mud-main-content')?.classList.contains('chat-channel-open') || false;
        const isSidebarHidden = () => document.querySelector('.chat-page-container > .mud-grid > .mud-grid-item:first-child')?.classList.contains('d-none') || false;
        const getMessagesChannelId = () => {
            const el = document.getElementById('chat-messages-container');
            return el ? (el.getAttribute('data-channel-id') || 'mounted') : null;
        };

        function add(report, lane, name, t) {
            if (!report) return;
            report.entries.push({ lane, name, t: t ?? now(), seq: ++seq });
            if (lane !== 'S') report.lastClientMs = Math.max(report.lastClientMs, t ?? now());
        }

        function markPainted(report, label) {
            requestAnimationFrame(() => requestAnimationFrame(() => add(report, 'P', `Painted: ${label}`)));
        }

        function checkDomState() {
            const r = current;
            if (!r) return;
            const appBar = isAppBarHidden();
            if (appBar !== r.dom.appBar) {
                r.dom.appBar = appBar;
                add(r, 'C', appBar ? 'DOM MudAppBar .hide-appbar-mobile Added' : 'DOM MudAppBar .hide-appbar-mobile Removed');
                markPainted(r, appBar ? 'AppBar hidden' : 'AppBar visible');
            }
            const main = isMainContentChannelOpen();
            if (main !== r.dom.main) {
                r.dom.main = main;
                add(r, 'C', main ? 'DOM MudMainContent .chat-channel-open Added' : 'DOM MudMainContent .chat-channel-open Removed');
            }
            const sidebar = isSidebarHidden();
            if (sidebar !== r.dom.sidebar) {
                r.dom.sidebar = sidebar;
                add(r, 'C', sidebar ? 'DOM Sidebar .d-none Added (Sidebar Hidden)' : 'DOM Sidebar .d-none Removed (Sidebar Visible)');
                if (!sidebar) markPainted(r, 'list visible');
            }
            const channel = getMessagesChannelId();
            if (channel !== r.dom.channel) {
                r.dom.channel = channel;
                add(r, 'C', channel ? `DOM #chat-messages-container Mounted (AppBarHidden=${appBar})` : `DOM #chat-messages-container Removed (AppBarHidden=${appBar})`);
                if (channel) markPainted(r, 'channel visible');
            }
        }

        // ── WebSocket frame classification (BlazorPack binary or JSON text) ──
        const IN_TARGETS = ['JS.RenderBatch', 'JS.BeginInvokeJS', 'JS.EndInvokeDotNet', 'JS.EndLocationChanging', 'JS.Error'];
        const OUT_TARGETS = ['OnRenderCompleted', 'DispatchBrowserEvent', 'OnLocationChanged', 'OnLocationChanging', 'BeginInvokeDotNetFromJS', 'EndInvokeJSFromDotNet'];

        function toBytes(data) {
            if (data instanceof ArrayBuffer) return new Uint8Array(data);
            if (ArrayBuffer.isView(data)) return new Uint8Array(data.buffer, data.byteOffset, data.byteLength);
            return null;
        }
        function readInt(b, i) {
            const x = b[i];
            if (x < 0x80) return [x, i + 1];
            if (x >= 0xe0) return [x - 256, i + 1];
            if (x === 0xcc) return [b[i + 1], i + 2];
            if (x === 0xcd) return [(b[i + 1] << 8) | b[i + 2], i + 3];
            if (x === 0xce || x === 0xd2) return [((b[i + 1] << 24) >>> 0) + (b[i + 2] << 16) + (b[i + 3] << 8) + b[i + 4], i + 5];
            if (x === 0xcf || x === 0xd3) { let v = 0; for (let k = 1; k <= 8; k++) v = v * 256 + b[i + k]; return [v, i + 9]; }
            if (x === 0xd0) return [(b[i + 1] << 24) >> 24, i + 2];
            if (x === 0xd1) return [(((b[i + 1] << 8) | b[i + 2]) << 16) >> 16, i + 3];
            return [null, i + 1];
        }
        // Returns [string|null, nextIndex]; handles nil and str8/str16/fixstr.
        function readStr(b, i) {
            const x = b[i];
            let len, start;
            if (x === 0xc0) return [null, i + 1];
            if (x >= 0xa0 && x <= 0xbf) { len = x & 0x1f; start = i + 1; }
            else if (x === 0xd9) { len = b[i + 1]; start = i + 2; }
            else if (x === 0xda) { len = (b[i + 1] << 8) | b[i + 2]; start = i + 3; }
            else return [null, i + 1];
            return [String.fromCharCode.apply(null, b.subarray(start, start + Math.min(len, 80))), start + len];
        }
        // Returns [firstIntArg, secondStrArg] following a hub target (args array header, then values).
        function readArgsAfter(bytes, head, text, target) {
            const idx = head.indexOf(target);
            if (idx < 0) return [null, null];
            if (bytes) {
                let i = idx + target.length;
                if (bytes[i] >= 0x90 && bytes[i] <= 0x9f) i++;
                const [n, next] = readInt(bytes, i);
                return [n, readStr(bytes, next)[0]];
            }
            const m = text.slice(idx).match(/"arguments":\[\s*"?(\d+)"?\s*,\s*"?([^",\]]*)/);
            return m ? [Number(m[1]), m[2]] : [null, null];
        }
        // BeginInvokeDotNetFromJS args: [callId, assemblyName, methodIdentifier, dotNetObjectId, argsJson]
        function readDotNetMethod(bytes, head, text) {
            const target = 'BeginInvokeDotNetFromJS';
            const idx = head.indexOf(target);
            if (idx < 0) return null;
            if (bytes) {
                let i = idx + target.length;
                if (bytes[i] >= 0x90 && bytes[i] <= 0x9f) i++;
                i = readStr(bytes, i)[1];          // callId
                i = readStr(bytes, i)[1];          // assemblyName
                return readStr(bytes, i)[0];       // methodIdentifier
            }
            const m = text.slice(idx).match(/"arguments":\[[^,]*,[^,]*,\s*"([^"]+)"/);
            return m ? m[1] : null;
        }

        function describe(data, targets, direction) {
            const bytes = toBytes(data);
            const text = typeof data === 'string' ? data : null;
            const size = bytes ? bytes.byteLength : (text ? text.length : 0);
            const head = text ? text.slice(0, 1024) : String.fromCharCode.apply(null, bytes.subarray(0, Math.min(size, 1024)));
            const hits = targets.filter(t => head.includes(t));
            if (hits.length === 0) return size < 32 ? null : { size, label: 'Other', kind: 'other' };
            if (head.includes('DiagPing') || head.includes('DiagDrain') || head.includes('DiagBeginTransition')) return { size, kind: 'diag' };
            let detail = '', ident = null, held = null;
            if (hits.includes('JS.BeginInvokeJS')) {
                const [handle, identifier] = readArgsAfter(bytes, head, text, 'JS.BeginInvokeJS');
                ident = identifier || '?';
                detail = `:${ident}`;
                if (handle != null) pendingJsCalls.set(handle, { identifier: ident, t: now() });
            } else if (hits.includes('EndInvokeJSFromDotNet')) {
                const [handle] = readArgsAfter(bytes, head, text, 'EndInvokeJSFromDotNet');
                const call = handle != null ? pendingJsCalls.get(handle) : null;
                if (call) {
                    ident = call.identifier;
                    held = now() - call.t;
                    detail = `:${ident} (client held ${Math.round(held)}ms)`;
                    pendingJsCalls.delete(handle);
                }
            } else if (hits.includes('DispatchBrowserEvent')) {
                const m = head.match(/eventName\\?"\s*:\s*\\?"(\w+)/) || head.match(/"type"\s*:\s*"(\w+)"/);
                if (m) detail = `:${m[1]}`;
            } else if (hits.includes('BeginInvokeDotNetFromJS')) {
                const method = readDotNetMethod(bytes, head, text);
                if (method) detail = `:${method}`;
            }
            const kind = hits.includes('JS.RenderBatch') ? 'batch' : hits.includes('JS.BeginInvokeJS') ? 'jscall'
                : hits.includes('EndInvokeJSFromDotNet') ? 'jsdone' : hits.includes('OnRenderCompleted') ? 'ack'
                : hits.includes('JS.EndInvokeDotNet') ? 'dotnetdone' : 'other';
            const count = Math.max(1, head.split(hits[0]).length - 1);
            return { size, label: hits.join('+') + detail, kind, ident, held, count };
        }

        function onFrame(data, direction) {
            const r = current;
            if (!r) return;
            const info = describe(data, direction === 'in' ? IN_TARGETS : OUT_TARGETS, direction);
            if (!info) return;
            r.wire[info.kind] = (r.wire[info.kind] || 0) + info.count;
            if (info.kind === 'batch') r.wire.batchBytes = (r.wire.batchBytes || 0) + info.size;
            if (info.kind === 'jscall') r.jsCalls[info.ident] = (r.jsCalls[info.ident] || 0) + info.count;
            if (info.kind === 'diag' || info.kind === 'dotnetdone') return; // probe/drain traffic: counted, not listed
            const kb = info.size >= 1024 ? `${(info.size / 1024).toFixed(1)}KB` : `${info.size}B`;
            const countLabel = info.count > 1 ? ` (${info.count} msgs)` : '';
            add(r, 'W', `WS ${direction === 'in' ? '←' : '→'} ${info.label}${countLabel} ${kb}`);
            Object.assign(r.entries[r.entries.length - 1], { dir: direction, kind: info.kind, ident: info.ident, held: info.held, size: info.size, count: info.count });
        }

        function hookSocket() {
            // Inbound frames arrive via the early listener registered in App.razor (runs before SignalR's onmessage).
            window.__spokesWsIn = (data) => { if (current) { try { onFrame(data, 'in'); } catch (e) { } } };
            const ws = window.__spokesBlazorSocket;
            if (!ws || ws.__spokesDiagHooked) return;
            ws.__spokesDiagHooked = true;
            const originalSend = ws.send;
            ws.send = function (data) {
                if (current) { try { onFrame(data, 'out'); } catch (e) { } }
                return originalSend.call(this, data);
            };
        }

        // ── Observers active only while a report is open ──
        function startObservers() {
            hookSocket();
            if (!mutationObserver && document.body) {
                mutationObserver = new MutationObserver(checkDomState);
                mutationObserver.observe(document.body, { childList: true, subtree: true, attributes: true, attributeFilter: ['class', 'data-channel-id'] });
            }
            if (!longTaskObserver && PerformanceObserver.supportedEntryTypes?.includes('longtask')) {
                longTaskObserver = new PerformanceObserver((list) => {
                    for (const e of list.getEntries()) {
                        const r = current;
                        if (!r) continue;
                        r.longTasks.push(Math.round(e.duration));
                        add(r, 'C', `LongTask ${Math.round(e.duration)}ms`, performance.timeOrigin + e.startTime);
                    }
                });
                longTaskObserver.observe({ type: 'longtask' });
            }
            if (!probeTimer && server) {
                probeTimer = setInterval(() => {
                    const r = current;
                    if (!r || !server) return;
                    const t0 = now();
                    server.invokeMethodAsync('DiagPing').then(() => r.probes.push(now() - t0)).catch(() => { });
                }, PROBE_MS);
            }
            if (!tickTimer) tickTimer = setInterval(tick, 500);
        }

        function stopObserversIfIdle() {
            if (open.length > 0) return;
            mutationObserver?.disconnect(); mutationObserver = null;
            longTaskObserver?.disconnect(); longTaskObserver = null;
            clearInterval(probeTimer); probeTimer = null;
            clearInterval(tickTimer); tickTimer = null;
            pendingJsCalls.clear();
        }

        // ── Transition lifecycle ──
        function startTransition(name, initialStep) {
            if (!server) return;
            const t = now();
            // Native back is followed by a popstate: join it instead of starting a new report.
            if (name.startsWith('PopState') && current && current.name === 'NativeBack' && t - current.startedMs < 1500) {
                add(current, 'C', `${initialStep} (${window.location.pathname})`, t);
                return;
            }
            const report = {
                id: ++reportCounter, name, serverTid: 0, serverName: null, startedMs: t, lastClientMs: t,
                entries: [], cursor: 0, draining: false, probes: [], longTasks: [], wire: {}, jsCalls: {},
                dom: { appBar: isAppBarHidden(), main: isMainContentChannelOpen(), sidebar: isSidebarHidden(), channel: getMessagesChannelId() }
            };
            if (lastPointerDown && t - lastPointerDown < 3000) add(report, 'C', 'pointerdown', lastPointerDown);
            if (lastPointerUp && t - lastPointerUp < 3000) add(report, 'C', 'pointerup', lastPointerUp);
            add(report, 'C', `${initialStep} (AppBarHidden=${report.dom.appBar})`, t);
            current = report;
            open.push(report);
            // Sent before Blazor's own click dispatch (capture phase), so the server tags everything with this id.
            server.invokeMethodAsync('DiagBeginTransition', name).then(tid => { report.serverTid = tid; }).catch(() => { });
            startObservers();
        }

        function tick() {
            const t = now();
            for (const r of [...open]) {
                if (r.draining) continue;
                const age = t - r.startedMs;
                if (!r.serverTid) {
                    if (age > 5000) finalize(r, false, 'no server transition id');
                    continue;
                }
                if (t - r.lastClientMs >= QUIET_MS || age > HARD_CAP_MS) drain(r);
            }
        }

        function drain(r) {
            r.draining = true;
            server.invokeMethodAsync('DiagDrain', r.serverTid, r.cursor).then(res => {
                for (const e of (res.entries || [])) {
                    add(r, 'S', e.name, e.timestampMs || e.timestamp); // server clock; aligned in finalize()
                }
                r.cursor = res.nextCursor;
                r.serverName = res.transitionName || r.serverName;
                r.openSpans = res.openSpans;
                const age = now() - r.startedMs;
                if ((res.openSpans === 0 && res.lastEntryAgeMs >= SERVER_QUIET_MS) || age > HARD_CAP_MS) {
                    finalize(r, age > HARD_CAP_MS && res.openSpans > 0, null);
                } else {
                    r.draining = false;
                }
            }).catch(() => finalize(r, true, 'drain failed'));
        }

        async function measureClock() {
            let best = null;
            for (let i = 0; i < 5; i++) {
                try {
                    const t0 = now();
                    const st = await server.invokeMethodAsync('DiagPing');
                    const t3 = now();
                    const rtt = t3 - t0;
                    if (!best || rtt < best.rtt) best = { rtt, offset: st - (t0 + t3) / 2 };
                } catch (e) { break; }
            }
            return best;
        }

        async function finalize(r, truncated, reason) {
            r.draining = true;
            const i = open.indexOf(r);
            if (i >= 0) open.splice(i, 1);
            if (current === r) current = null;
            stopObserversIfIdle();
            const clock = server ? await measureClock() : null;
            const offset = clock ? clock.offset : 0;
            for (const e of r.entries) {
                if (e.lane === 'S') e.t -= offset;
            }
            r.entries.sort((a, b) => (a.t - b.t) || (a.seq - b.seq));
            print(r, clock, truncated, reason);
        }

        // ── Report output ──
        function find(r, lane, predicate) { return r.entries.find(e => (!lane || e.lane === lane) && predicate(e.name)); }
        function since(t0, e) { return e ? `${Math.round(e.t - t0)}ms` : 'n/a'; }
        function median(a) { if (!a.length) return null; const s = [...a].sort((x, y) => x - y); return Math.round(s[Math.floor(s.length / 2)]); }

        function print(r, clock, truncated, reason) {
            if (r.entries.length === 0) return;
            const t0 = r.entries[0].t;
            const tap = find(r, 'C', n => n.startsWith('Client: Tap') || n.startsWith('Client: Native') || n.startsWith('Client: Browser PopState')) || r.entries[0];
            const tags = (component) => [...new Set(r.entries.filter(e => e.lane === 'S').map(e => (e.name.match(new RegExp(`${component}\\[([0-9a-f]{4})\\]`)) || [])[1]).filter(Boolean))];
            const chatTags = tags('Chat'), layoutTags = tags('MainLayout');
            const chatRemount = r.entries.some(e => e.lane === 'S' && /Chat\[[0-9a-f]{4}\] OnInitializedAsync/.test(e.name));
            const layoutRemount = r.entries.some(e => e.lane === 'S' && /MainLayout\[[0-9a-f]{4}\] OnInitialized/.test(e.name));
            const lt = r.longTasks;
            const w = r.wire;
            const lines = [];
            lines.push('==================================================');
            lines.push(`[CHAT TRANSITION REPORT #${r.id}] ${r.serverName || r.name}${truncated ? '  ** TRUNCATED **' : ''}${reason ? `  (${reason})` : ''}`);
            lines.push(`Platform: ${window.SpokesBridge?.platform || 'unknown'} | Viewport: ${window.innerWidth}x${window.innerHeight} | Server tid: #${r.serverTid || '-'}` +
                (clock ? ` | Clock offset: ${Math.round(clock.offset)}ms ±${Math.round(clock.rtt / 2)}ms | Idle RTT: ${Math.round(clock.rtt)}ms` : ' | Clock: not synced'));
            lines.push('--------------------------------------------------');
            lines.push('SUMMARY (from tap)');
            const row = (label, value) => lines.push(`  ${label.padEnd(40)}: ${value}`);
            row('Tap → server received', since(tap.t, find(r, 'S', n => n.startsWith('Server: Transition #'))));
            row('Tap → channel painted', since(tap.t, find(r, 'P', n => n.includes('channel visible'))));
            row('Tap → AppBar hidden painted', since(tap.t, find(r, 'P', n => n.includes('AppBar hidden'))));
            row('Tap → list visible painted', since(tap.t, find(r, 'P', n => n.includes('list visible'))));
            row('Tap → AppBar visible painted', since(tap.t, find(r, 'P', n => n.includes('AppBar visible'))));
            row('Tap → composer init done', since(tap.t, find(r, 'S', n => /ChatComposer\[.*Init chain.*End/.test(n))));
            row('Tap → read confirmed', since(tap.t, find(r, 'S', n => /ConfirmChannelViewedAndReadAsync End/.test(n))));
            row('Chat instances', `${chatTags.join(' → ') || 'n/a'}${chatRemount ? '  (REMOUNT: new Chat initialized)' : ''}`);
            row('MainLayout instances', `${layoutTags.join(' → ') || 'n/a'}${layoutRemount ? '  (NEW MainLayout initialized)' : ''}`);
            row('Wire', `${w.batch || 0} render batches (${((w.batchBytes || 0) / 1024).toFixed(1)}KB), ${w.jscall || 0} JS calls, ${w.jsdone || 0} JS completions, ${w.ack || 0} acks`);
            row('Main thread long tasks', PerformanceObserver.supportedEntryTypes?.includes('longtask')
                ? `${lt.length} tasks, ${lt.reduce((a, b) => a + b, 0)}ms total, max ${lt.length ? Math.max(...lt) : 0}ms` : 'unsupported on this platform');
            row('RTT under load', r.probes.length ? `median ${median(r.probes)}ms, max ${Math.round(Math.max(...r.probes))}ms (${r.probes.length} probes)` : 'n/a');
            const topCalls = Object.entries(r.jsCalls).sort((a, b) => b[1] - a[1]).slice(0, 8).map(([k, v]) => `${k}×${v}`).join(', ');
            row('Top JS calls (server → client)', topCalls || 'n/a');
            lines.push('--------------------------------------------------');
            lines.push('TIMELINE   [C]=client [S]=server [W]=wire [P]=paint   (bursts of 3+ similar lines are collapsed)');
            const entries = r.entries;
            const isBoringWire = e => e.lane === 'W' && (e.kind === 'jscall' || e.kind === 'jsdone') && (!e.ident || /^(mud|spokesUpload\.)/.test(e.ident));
            const serverKey = e => e.lane === 'S' ? e.name.replace(/\s*\(.*\)\s*$/, '') : null;
            let prevT = t0;
            const emit = (lane, name, t) => {
                lines.push(`[${lane}] ${name.padEnd(78)}: ${String(Math.round(t - t0)).padStart(6)}ms (+${Math.round(t - prevT)})`);
                prevT = t;
            };
            for (let i = 0; i < entries.length;) {
                const e = entries[i];
                let j = i + 1;
                if (isBoringWire(e)) {
                    while (j < entries.length && isBoringWire(entries[j])) j++;
                    if (j - i >= 3) {
                        const groups = new Map();
                        let bytes = 0;
                        for (let k = i; k < j; k++) {
                            const x = entries[k];
                            const key = `${x.dir === 'in' ? '←' : '→ done'} ${x.ident || (x.kind === 'jsdone' ? 'EndInvokeJSFromDotNet' : '?')}`;
                            const g = groups.get(key) || { n: 0, held: 0, heldN: 0 };
                            g.n += x.count || 1;
                            if (x.held != null) { g.held += x.held; g.heldN++; }
                            groups.set(key, g);
                            bytes += x.size || 0;
                        }
                        const parts = [...groups.entries()].map(([k, g]) => `${g.n}× ${k}${g.heldN ? ` (avg held ${Math.round(g.held / g.heldN)}ms)` : ''}`);
                        emit('W', `WS burst over ${Math.round(entries[j - 1].t - e.t)}ms, ${(bytes / 1024).toFixed(1)}KB: ${parts.join(', ')}`, e.t);
                        i = j;
                        continue;
                    }
                } else if (e.lane === 'S') {
                    const key = serverKey(e);
                    while (j < entries.length && serverKey(entries[j]) === key) j++;
                    if (j - i >= 3) {
                        emit('S', `${j - i}× ${key} (over ${Math.round(entries[j - 1].t - e.t)}ms)`, e.t);
                        i = j;
                        continue;
                    }
                }
                const name = e.lane === 'S' ? e.name : (e.name.startsWith('Client: ') || e.lane !== 'C' ? e.name : `Client: ${e.name}`);
                emit(e.lane, name, e.t);
                i++;
            }
            lines.push('==================================================');
            lines.forEach(l => console.log(l));
            finished.push({ id: r.id, name: r.serverName || r.name, clock, truncated, entries: r.entries.map(e => ({ lane: e.lane, name: e.name, ms: Math.round(e.t - t0) })) });
            if (finished.length > MAX_REPORTS) finished.shift();
        }

        // ── Triggers ──
        document.addEventListener('pointerdown', () => { if (server) lastPointerDown = now(); }, true);
        document.addEventListener('pointerup', () => { if (server) lastPointerUp = now(); }, true);
        document.addEventListener('click', function (e) {
            if (!server || !e.target || typeof e.target.closest !== 'function') return;
            if (e.target.closest('#chat-mobile-back-btn, #chat-drop-zone button.d-md-none')) {
                startTransition('GoBack', 'Client: Tap Back Button');
                return;
            }
            if (e.target.closest('.chat-page-container .mud-list-item-clickable') && !e.target.closest('button')) {
                startTransition('SelectChannel', 'Client: Tap Channel Item');
            }
        }, true);
        window.addEventListener('popstate', function () {
            if (server && window.location.pathname.startsWith('/chat')) {
                startTransition('PopState (' + window.location.pathname + ')', 'Client: Browser PopState');
            }
        });

        window.spokesChatDiagnostics = {
            attachServer: function (dotNetRef) { server = dotNetRef; },
            startTransition: startTransition,
            mark: function (name) {
                if (!current) return;
                checkDomState();
                add(current, 'C', name);
            },
            reports: finished,
            dump: function () { return JSON.stringify(finished[finished.length - 1] || null, null, 2); }
        };
    })();
})();
