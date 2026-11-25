// Spokes On-Demand UI, Chat & Wire Profiler (lazy-loaded only via spokesPerf.enableChatDiagnostics())
// Strictly in-memory: never written to localStorage/cookies, so refreshing the page always resets it to OFF.
(function () {
    'use strict';

    if (typeof window === 'undefined' || window.__spokesChatProfilerFactory) return;

    window.__spokesChatProfilerFactory = function createChatProfiler(bridge) {
        const now = bridge.now;
        const pushCapped = bridge.pushCapped;
        const getNetworkSummary = bridge.getNetworkSummary;
        const formatNetworkSummary = bridge.formatNetworkSummary;
        const flushMediaBatch = bridge.flushMediaBatch;
        const setPendingMediaTapAnchorMs = bridge.setPendingMediaTapAnchorMs;

        const CHAT_QUIET_MS = 1500, UI_QUIET_MS = 900, SERVER_QUIET_MS = 500, HARD_CAP_MS = 15000, PROBE_MS = 250, MAX_REPORTS = 20;
        const LARGE_BATCH_BYTES = 16384;
        const XRAY_BATCH_BYTES = 4096;
        const CLOCK_CACHE_TTL_MS = 60000;
        const CLOCK_PING_COUNT = 3;
        const RAPID_TAP_COALESCE_MS = 150;

        let active = false;
        let server = null;            // DotNetObjectReference<MainLayout>
        let seq = 0;
        let reportCounter = 0;
        let current = null;           // report receiving client/wire marks
        const open = [];              // reports not yet printed
        const finished = [];          // last MAX_REPORTS printed reports
        let lastPointerDown = null, lastPointerUp = null;
        let lastTransitionTapMs = null;
        let lastChannelOpenTapMs = null;
        let cachedClock = null;       // { rtt, offset, measuredAt }
        let missingServerWarned = false;
        let mutationObserver = null, longTaskObserver = null, probeTimer = null, tickTimer = null;
        let listenersAttached = false;
        const pendingJsCalls = new Map(); // asyncHandle -> { identifier, t }

        // ── DOM state helpers ──
        const isAppBarHidden = () => document.querySelector?.('.mud-appbar')?.classList?.contains('hide-appbar-mobile') || false;
        const isMainContentChannelOpen = () => document.querySelector?.('.mud-main-content')?.classList?.contains('chat-channel-open') || false;
        const isSidebarHidden = () => document.querySelector?.('.chat-page-container > .mud-grid > .mud-grid-item:first-child')?.classList?.contains('d-none') || false;
        const getMessagesChannelId = () => {
            const el = document.getElementById?.('chat-messages-container');
            return el ? (el.getAttribute('data-channel-id') || 'mounted') : null;
        };
        const isDrawerOpen = () => {
            const el = document.getElementById?.('nav-drawer');
            return Boolean(el && el.classList && (el.classList.contains('is-nav-drawer-open') || el.classList.contains('mud-drawer--open')));
        };
        const getDialogCount = () => {
            if (typeof document.querySelectorAll !== 'function') return 0;
            return document.querySelectorAll('.mud-dialog-container .mud-dialog, .mud-dialog-provider .mud-dialog').length;
        };
        const getRoutePath = () => (window.location && typeof window.location.pathname === 'string' && window.location.pathname) ? window.location.pathname : '/';

        function add(report, lane, name, t) {
            if (!report) return;
            report.entries.push({ lane, name, t: t ?? now(), seq: ++seq });
            if (lane !== 'S') report.lastClientMs = Math.max(report.lastClientMs, t ?? now());
        }

        function markPainted(report, label, isUiMilestone) {
            requestAnimationFrame(() => requestAnimationFrame(() => {
                add(report, 'P', `Painted: ${label}`);
                if (isUiMilestone) report.hasUiPaint = true;
            }));
        }

        function checkDomState() {
            const r = current;
            if (!r) return;
            const appBar = isAppBarHidden();
            if (appBar !== r.dom.appBar) {
                r.dom.appBar = appBar;
                add(r, 'C', appBar ? 'DOM MudAppBar .hide-appbar-mobile Added' : 'DOM MudAppBar .hide-appbar-mobile Removed');
                markPainted(r, appBar ? 'AppBar hidden' : 'AppBar visible', false);
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
                if (!sidebar) markPainted(r, 'list visible', false);
            }
            const channel = getMessagesChannelId();
            if (channel !== r.dom.channel) {
                r.dom.channel = channel;
                add(r, 'C', channel ? `DOM #chat-messages-container Mounted (AppBarHidden=${appBar})` : `DOM #chat-messages-container Removed (AppBarHidden=${appBar})`);
                if (channel) markPainted(r, 'channel visible', false);
            }
            const drawerOpen = isDrawerOpen();
            if (drawerOpen !== r.dom.drawerOpen) {
                r.dom.drawerOpen = drawerOpen;
                add(r, 'C', drawerOpen ? 'DOM NavDrawer Opened' : 'DOM NavDrawer Closed');
                markPainted(r, drawerOpen ? 'drawer open' : 'drawer closed', true);
            }
            const dialogCount = getDialogCount();
            if (dialogCount !== r.dom.dialogCount) {
                const opened = dialogCount > r.dom.dialogCount;
                r.dom.dialogCount = dialogCount;
                add(r, 'C', opened ? `DOM Dialog Mounted (count=${dialogCount})` : `DOM Dialog Removed (count=${dialogCount})`);
                markPainted(r, opened ? 'dialog visible' : 'dialog closed', true);
            }
            const routePath = getRoutePath();
            if (routePath !== r.dom.routePath) {
                const prevPath = r.dom.routePath;
                r.dom.routePath = routePath;
                add(r, 'C', `DOM Route Changed (${prevPath} → ${routePath})`);
                markPainted(r, `route ${routePath}`, true);
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
        function readDotNetMethod(bytes, head, text) {
            const target = 'BeginInvokeDotNetFromJS';
            const idx = head.indexOf(target);
            if (idx < 0) return null;
            if (bytes) {
                let i = idx + target.length;
                if (bytes[i] >= 0x90 && bytes[i] <= 0x9f) i++;
                i = readStr(bytes, i)[1];
                i = readStr(bytes, i)[1];
                return readStr(bytes, i)[0];
            }
            const m = text.slice(idx).match(/"arguments":\[[^,]*,[^,]*,\s*"([^"]+)"/);
            return m ? m[1] : null;
        }

        function describe(data, targets) {
            const bytes = toBytes(data);
            const text = typeof data === 'string' ? data : null;
            const size = bytes ? bytes.byteLength : (text ? text.length : 0);
            const headLen = Math.min(size, 2048);
            const head = text ? text.slice(0, headLen) : String.fromCharCode.apply(null, bytes.subarray(0, headLen));
            const hits = targets.filter(t => head.includes(t));
            if (hits.length === 0) return size < 32 ? null : { size, label: 'Other', kind: 'other' };
            if (head.includes('DiagClockPing') || head.includes('DiagPing') || head.includes('DiagDrain') || head.includes('DiagBeginTransition') || head.includes('SetChatProfilerEnabled')) {
                return { size, kind: 'diag' };
            }
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
                if (method) {
                    detail = `:${method}`;
                    if (method === 'DispatchEventAsync') {
                        const typeMatch = head.match(/\\?"type\\?"\s*:\s*\\?"([\w-]+)\\?"/);
                        const handlerMatch = head.match(/\\?"eventHandlerId\\?"\s*:\s*(\d+)/);
                        const evType = typeMatch ? typeMatch[1] : null;
                        const evHandler = handlerMatch ? `#${handlerMatch[1]}` : '';
                        if (evType || evHandler) {
                            detail += `:${evType || '?'}${evHandler ? `(${evHandler})` : ''}`;
                        }
                    }
                }
            }
            const kind = hits.includes('JS.RenderBatch') ? 'batch' : hits.includes('JS.BeginInvokeJS') ? 'jscall'
                : hits.includes('EndInvokeJSFromDotNet') ? 'jsdone' : hits.includes('OnRenderCompleted') ? 'ack'
                : hits.includes('JS.EndInvokeDotNet') ? 'dotnetdone' : 'other';
            const count = Math.max(1, head.split(hits[0]).length - 1);
            return { size, label: hits.join('+') + detail, kind, ident, held, count };
        }

        function inspectLargeBatchPayload(report, batchBytes) {
            requestAnimationFrame(() => {
                try {
                    const container = document.getElementById?.('chat-messages-container');
                    const rows = container ? container.querySelectorAll('.message-row[data-message-id]') : [];
                    if (!rows || rows.length === 0) return;
                    const sizes = [];
                    for (let i = 0; i < rows.length; i++) {
                        const row = rows[i];
                        const htmlLen = row.innerHTML ? row.innerHTML.length : 0;
                        const hasRawCode = Boolean(row.querySelector('[data-raw-code]'));
                        const imgCount = row.querySelectorAll('img').length;
                        sizes.push({
                            id: (row.getAttribute('data-message-id') || '?').slice(0, 8),
                            bytes: htmlLen,
                            hasRawCode,
                            imgCount
                        });
                    }
                    sizes.sort((a, b) => b.bytes - a.bytes);
                    const top = sizes.slice(0, 3).map(x =>
                        `msg[${x.id}]=${(x.bytes / 1024).toFixed(1)}KB` +
                        (x.hasRawCode ? '(rawCode)' : '') +
                        (x.imgCount > 0 ? `(${x.imgCount}img)` : '')
                    );
                    report.largeBatchInfo = `${(batchBytes / 1024).toFixed(1)}KB across ${rows.length} msgs; largest DOM: ${top.join(', ')}`;
                } catch (_) { }
            });
        }

        function onFrame(data, direction) {
            const r = current;
            if (!r || !active) return;
            const info = describe(data, direction === 'in' ? IN_TARGETS : OUT_TARGETS);
            if (!info) return;
            r.wire[info.kind] = (r.wire[info.kind] || 0) + info.count;
            if (info.kind === 'batch') {
                r.wire.batchBytes = (r.wire.batchBytes || 0) + info.size;
                if (info.size > (r.wire.maxBatchBytes || 0)) r.wire.maxBatchBytes = info.size;
                if (direction === 'in' && info.size >= XRAY_BATCH_BYTES) {
                    if (typeof window.__spokesInspectRenderBatch === 'function' && (!r.batchXRay || info.size > r.batchXRay.wireBytes)) {
                        const inspected = window.__spokesInspectRenderBatch(data);
                        if (inspected) {
                            inspected.wireBytes = info.size;
                            r.batchXRay = inspected;
                        }
                    }
                }
                if (direction === 'in' && info.size >= LARGE_BATCH_BYTES && r.isChat) {
                    if (!r.largeBatchInspected || info.size > (r.largeBatchInspectedBytes || 0)) {
                        r.largeBatchInspected = true;
                        r.largeBatchInspectedBytes = info.size;
                        inspectLargeBatchPayload(r, info.size);
                    }
                }
            }
            if (info.kind === 'jscall') r.jsCalls[info.ident] = (r.jsCalls[info.ident] || 0) + info.count;
            if (info.kind === 'diag' || info.kind === 'dotnetdone') return;
            const kb = info.size >= 1024 ? `${(info.size / 1024).toFixed(1)}KB` : `${info.size}B`;
            const countLabel = info.count > 1 ? ` (${info.count} msgs)` : '';
            add(r, 'W', `WS ${direction === 'in' ? '←' : '→'} ${info.label}${countLabel} ${kb}`);
            Object.assign(r.entries[r.entries.length - 1], { dir: direction, kind: info.kind, ident: info.ident, held: info.held, size: info.size, count: info.count });
        }

        function hookSocket() {
            if (!active) return;
            window.__spokesWsIn = (data) => { if (active && current) { try { onFrame(data, 'in'); } catch (e) { } } };
            const ws = window.__spokesBlazorSocket;
            if (!ws || ws.__spokesDiagHooked) return;
            ws.__spokesDiagHooked = true;
            const originalSend = ws.send;
            if (typeof originalSend !== 'function') return;
            ws.send = function (data) {
                if (active && current) { try { onFrame(data, 'out'); } catch (e) { } }
                return originalSend.call(this, data);
            };
        }

        async function syncServerProfilerFlag(enabled) {
            const ref = server || window.spokesDotNetRef;
            if (!ref || typeof ref.invokeMethodAsync !== 'function') {
                if (enabled) {
                    console.warn('[Perf] Wire profiler enabled on client, waiting for Blazor circuit (spokesDotNetRef) to attach.');
                }
                return false;
            }
            try {
                await ref.invokeMethodAsync('SetChatProfilerEnabled', Boolean(enabled));
                return true;
            } catch (err) {
                if (enabled) {
                    console.warn('[Perf] Wire profiler could not sync server flag (SetChatProfilerEnabled):', err && err.message ? err.message : err);
                }
                return false;
            }
        }

        function onBlazorConnected() {
            cachedClock = null;
            if (!active) return;
            hookSocket();
            server = window.spokesDotNetRef || server;
            syncServerProfilerFlag(true);
        }

        function startObservers() {
            if (!active) return;
            hookSocket();
            if (!mutationObserver && document.body && typeof MutationObserver !== 'undefined') {
                mutationObserver = new MutationObserver(checkDomState);
                mutationObserver.observe(document.body, { childList: true, subtree: true, attributes: true, attributeFilter: ['class', 'data-channel-id'] });
            }
            if (!longTaskObserver && typeof PerformanceObserver !== 'undefined') {
                const supported = PerformanceObserver.supportedEntryTypes || [];
                const useLoaf = supported.includes('long-animation-frame');
                if (useLoaf || supported.includes('longtask')) {
                    longTaskObserver = new PerformanceObserver((list) => {
                        for (const e of list.getEntries()) {
                            const r = current;
                            if (!r) continue;
                            const dur = Math.round(e.duration);
                            r.longTasks.push(dur);
                            if (useLoaf) {
                                const blk = Math.round(e.blockingDuration || Math.max(0, e.duration - 50));
                                const hasScripts = Array.isArray(e.scripts) && e.scripts.length > 0;
                                const phases = [];
                                if (e.renderStart > 0) {
                                    const workMs = Math.max(0, Math.round(e.renderStart - e.startTime));
                                    const renderMs = Math.max(0, Math.round((e.startTime + e.duration) - e.renderStart));
                                    phases.push(`work=${workMs}ms`);
                                    phases.push(`render=${renderMs}ms`);
                                }
                                if (e.styleAndLayoutStart > 0) {
                                    const layoutMs = Math.max(0, Math.round((e.startTime + e.duration) - e.styleAndLayoutStart));
                                    phases.push(`layout=${layoutMs}ms`);
                                }
                                const phaseInfo = phases.length > 0
                                    ? `, ${phases.join(' ')}`
                                    : '';
                                let attr = '';
                                if (hasScripts) {
                                    const topScripts = e.scripts
                                        .slice()
                                        .sort((a, b) => (b.duration || 0) - (a.duration || 0))
                                        .slice(0, 2)
                                        .map(s => {
                                            const file = s.sourceURL ? s.sourceURL.split('/').pop().split('?')[0] : '';
                                            const inv = s.invoker || s.sourceFunctionName || s.invokerType || 'script';
                                            return `${inv}${file ? '@' + file : ''}(${Math.round(s.duration || 0)}ms)`;
                                        });
                                    attr = ` [${topScripts.join(', ')}]`;
                                }
                                add(r, 'C', `LongTask(LoAF) ${dur}ms (blk ${blk}ms${phaseInfo})${attr}`, performance.timeOrigin + e.startTime);
                            } else {
                                add(r, 'C', `LongTask ${dur}ms`, performance.timeOrigin + e.startTime);
                            }
                        }
                    });
                    try {
                        longTaskObserver.observe({ type: useLoaf ? 'long-animation-frame' : 'longtask' });
                    } catch (_) {
                        longTaskObserver = null;
                    }
                }
            }
            if (!probeTimer && server) {
                probeTimer = setInterval(() => {
                    const r = current;
                    if (!r || !server || !active) return;
                    const t0 = now();
                    server.invokeMethodAsync('DiagClockPing').then(() => r.probes.push(now() - t0)).catch(() => { });
                }, PROBE_MS);
            }
            if (!tickTimer) tickTimer = setInterval(tick, 500);
        }

        function stopObserversIfIdle(force) {
            if (!force && open.length > 0) return;
            mutationObserver?.disconnect(); mutationObserver = null;
            longTaskObserver?.disconnect(); longTaskObserver = null;
            if (probeTimer) { clearInterval(probeTimer); probeTimer = null; }
            if (tickTimer) { clearInterval(tickTimer); tickTimer = null; }
            pendingJsCalls.clear();
        }

        function isChatTransitionName(name, routePath) {
            if (!name) return false;
            if (name === 'SelectChannel' || name === 'SwitchChannel' || name === 'GoBack') return true;
            if (name === 'NativeBack' && (routePath || '').startsWith('/chat')) return true;
            if (name.startsWith('PopState') && name.includes('/chat')) return true;
            return false;
        }

        // ── Transition lifecycle ──
        function startTransition(name, initialStep, meta) {
            if (!active) return;
            server = server || window.spokesDotNetRef || null;
            if (!server) {
                if (!missingServerWarned) {
                    missingServerWarned = true;
                    console.warn('[Perf] Wire profiler active, but window.spokesDotNetRef is unavailable; skipping transition:', name);
                }
                return;
            }
            missingServerWarned = false;
            const t = now();
            const routePath = getRoutePath();
            if (current && (current.name === 'NativeBack' || current.name === 'DialogClose (Back)' || current.name === 'DrawerClose (Back)') && (t - current.startedMs) < 1500) {
                if (name.startsWith('PopState') || name === 'GoBack' || name.startsWith('UIAction') || name.startsWith('DrawerClose')) {
                    add(current, 'C', `${initialStep} (${routePath})`, t);
                    return;
                }
            }
            if (meta && meta.targetEl && current && current.targetEl === meta.targetEl && (t - current.startedMs) < RAPID_TAP_COALESCE_MS) {
                add(current, 'C', `${initialStep} (coalesced)`, t);
                return;
            }

            const isChat = meta && typeof meta.kind === 'string'
                ? meta.kind === 'chat'
                : isChatTransitionName(name, routePath);

            if (isChat) {
                flushMediaBatch('nav-away');
            }

            const isSelectChannel = name === 'SelectChannel' || name === 'SwitchChannel';
            const prevGapMs = lastTransitionTapMs != null ? Math.round(t - lastTransitionTapMs) : null;
            const prevChannelOpenGapMs = (isSelectChannel && lastChannelOpenTapMs != null) ? Math.round(t - lastChannelOpenTapMs) : null;
            lastTransitionTapMs = t;
            if (isSelectChannel) {
                lastChannelOpenTapMs = t;
                setPendingMediaTapAnchorMs(t);
            }

            const report = {
                id: ++reportCounter, name, isChat, route: routePath, targetEl: meta?.targetEl || null,
                serverTid: 0, serverName: null, startedMs: t, lastClientMs: t, hasUiPaint: false,
                prevGapMs, prevChannelOpenGapMs,
                entries: [], cursor: 0, draining: false, probes: [], longTasks: [], wire: {}, jsCalls: {},
                largeBatchInspected: false, largeBatchInfo: null, mediaLoaded: 0, mediaBytes: 0,
                dom: {
                    appBar: isAppBarHidden(),
                    main: isMainContentChannelOpen(),
                    sidebar: isSidebarHidden(),
                    channel: getMessagesChannelId(),
                    drawerOpen: isDrawerOpen(),
                    dialogCount: getDialogCount(),
                    routePath
                }
            };
            if (lastPointerDown && t - lastPointerDown < 3000) add(report, 'C', 'pointerdown', lastPointerDown);
            if (lastPointerUp && t - lastPointerUp < 3000) add(report, 'C', 'pointerup', lastPointerUp);
            const initialSuffix = isChat ? ` (AppBarHidden=${report.dom.appBar})` : ` (route=${routePath})`;
            add(report, 'C', `${initialStep}${initialSuffix}`, t);
            current = report;
            open.push(report);
            console.log(`[Perf] Capturing #${report.id}: ${name} (${routePath})`);

            const beginOnServer = () => server.invokeMethodAsync('DiagBeginTransition', name).then(async tid => {
                if (tid > 0) {
                    report.serverTid = tid;
                    return;
                }
                // Server profiler flag may have reset (e.g., reconnect/re-attach); resync once and retry.
                const synced = await syncServerProfilerFlag(true);
                if (synced && active && !report.draining) {
                    try {
                        const retryTid = await server.invokeMethodAsync('DiagBeginTransition', name);
                        if (retryTid > 0) report.serverTid = retryTid;
                    } catch (_) { }
                }
            }).catch(() => { });
            beginOnServer();
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
                const quietMs = (!r.isChat && ((r.wire.batch || 0) > 0 || r.hasUiPaint)) ? UI_QUIET_MS : CHAT_QUIET_MS;
                if (t - r.lastClientMs >= quietMs || age > HARD_CAP_MS) drain(r);
            }
        }

        function drain(r) {
            r.draining = true;
            if (!server) { finalize(r, true, 'no server ref'); return; }
            server.invokeMethodAsync('DiagDrain', r.serverTid, r.cursor).then(res => {
                for (const e of (res.entries || [])) {
                    add(r, 'S', e.name, e.timestampMs || e.timestamp);
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

        async function measureClock(forceFresh) {
            if (!forceFresh && cachedClock && (now() - cachedClock.measuredAt) < CLOCK_CACHE_TTL_MS) {
                return cachedClock;
            }
            if (!server || typeof server.invokeMethodAsync !== 'function') return cachedClock;
            let best = null;
            for (let i = 0; i < CLOCK_PING_COUNT; i++) {
                try {
                    const t0 = now();
                    const st = await server.invokeMethodAsync('DiagClockPing');
                    const t3 = now();
                    const rtt = t3 - t0;
                    if (!best || rtt < best.rtt) best = { rtt, offset: st - (t0 + t3) / 2, measuredAt: t3 };
                } catch (e) { break; }
            }
            if (best) cachedClock = best;
            return best || cachedClock;
        }

        async function finalize(r, truncated, reason) {
            r.draining = true;
            const i = open.indexOf(r);
            if (i >= 0) open.splice(i, 1);
            if (current === r) current = null;
            stopObserversIfIdle(false);
            const clock = (server && active) ? await measureClock(false) : null;
            const offset = clock ? clock.offset : 0;
            for (const e of r.entries) {
                if (e.lane === 'S') e.t -= offset;
            }
            r.entries.sort((a, b) => (a.t - b.t) || (a.seq - b.seq));
            print(r, clock, truncated, reason);
        }

        function find(r, lane, predicate) { return r.entries.find(e => (!lane || e.lane === lane) && predicate(e.name)); }
        function since(t0, e) { return e ? `${Math.round(e.t - t0)}ms` : 'n/a'; }
        function median(a) { if (!a.length) return null; const s = [...a].sort((x, y) => x - y); return Math.round(s[Math.floor(s.length / 2)]); }

        function print(r, clock, truncated, reason) {
            if (r.entries.length === 0) return;
            const session = bridge.getSession();
            const t0 = r.entries[0].t;
            const tap = find(r, 'C', n => n.startsWith('Client: Tap') || n.startsWith('Client: Native') || n.startsWith('Client: Browser PopState')) || r.entries[0];
            const tags = (component) => [...new Set(r.entries.filter(e => e.lane === 'S').map(e => (e.name.match(new RegExp(`${component}\\[([0-9a-f]{4})\\]`)) || [])[1]).filter(Boolean))];
            const chatTags = tags('Chat'), layoutTags = tags('MainLayout');
            const chatRemount = r.entries.some(e => e.lane === 'S' && /Chat\[[0-9a-f]{4}\] OnInitializedAsync/.test(e.name));
            const layoutRemount = r.entries.some(e => e.lane === 'S' && /MainLayout\[[0-9a-f]{4}\] OnInitialized/.test(e.name));
            const lt = r.longTasks;
            const w = r.wire;
            const tapToChannelMs = (() => { const e = find(r, 'P', n => n.includes('channel visible')); return e ? Math.round(e.t - tap.t) : null; })();
            const tapToListMs = (() => { const e = find(r, 'P', n => n.includes('list visible')); return e ? Math.round(e.t - tap.t) : null; })();
            const tapToComposerMs = (() => { const e = find(r, 'S', n => /ChatComposer\[.*Init chain.*End/.test(n)); return e ? Math.round(e.t - tap.t) : null; })();
            const dbDoneEntry = find(r, 'S', n => n.includes('Db.ChatMessages.GetByChannelAsync Done'));
            const dbDiskMatch = dbDoneEntry ? dbDoneEntry.name.match(/cache=(\d+),\s*disk=(\d+),\s*index=([\d.]+)ms,\s*disk=([\d.]+)ms/) : null;
            const payloadEntry = find(r, 'S', n => n.includes('Channel Payload Stats'));

            for (const d of lt) pushCapped(session.longTasks, d);
            const effectiveName = r.serverName || r.name;
            if (effectiveName.startsWith('SelectChannel') || effectiveName.startsWith('SwitchChannel')) {
                pushCapped(session.channelOpens, {
                    id: r.id,
                    name: effectiveName,
                    isSwitch: r.name === 'SwitchChannel',
                    paintedMs: tapToChannelMs,
                    composerInitMs: tapToComposerMs,
                    wireKB: Math.round(((w.batchBytes || 0) / 1024) * 10) / 10,
                    maxBatchKB: Math.round(((w.maxBatchBytes || 0) / 1024) * 10) / 10,
                    interOpenGapMs: r.prevChannelOpenGapMs,
                    idleRttMs: clock ? Math.round(clock.rtt) : null,
                    probeMaxRttMs: r.probes.length ? Math.round(Math.max(...r.probes)) : null,
                    cacheHits: dbDiskMatch ? Number(dbDiskMatch[1]) : null,
                    diskReads: dbDiskMatch ? Number(dbDiskMatch[2]) : null,
                    indexMs: dbDiskMatch ? Number(dbDiskMatch[3]) : null,
                    diskMs: dbDiskMatch ? Number(dbDiskMatch[4]) : null,
                    longTaskMs: lt.reduce((a, b) => a + b, 0)
                });
            } else if (effectiveName.startsWith('GoBack') || (effectiveName.startsWith('NativeBack') && r.isChat)) {
                pushCapped(session.backNavigations, {
                    id: r.id,
                    name: effectiveName,
                    listPaintedMs: tapToListMs,
                    wireKB: Math.round(((w.batchBytes || 0) / 1024) * 10) / 10,
                    longTaskMs: lt.reduce((a, b) => a + b, 0)
                });
            }

            const wsExt = (window.__spokesBlazorSocket && window.__spokesBlazorSocket.extensions) || window.__spokesWsExtensions || '';
            const reportHeader = r.isChat ? 'CHAT TRANSITION REPORT' : 'UI TRANSITION REPORT';
            const lines = [];
            lines.push('==================================================');
            lines.push(`[${reportHeader} #${r.id}] ${effectiveName}${truncated ? '  ** TRUNCATED **' : ''}${reason ? `  (${reason})` : ''}`);
            lines.push(`Platform: ${window.SpokesBridge?.platform || 'unknown'} | Viewport: ${window.innerWidth}x${window.innerHeight} | Net: ${formatNetworkSummary(getNetworkSummary())} | WS ext: ${wsExt || 'none'} | Server tid: #${r.serverTid || '-'}` +
                (clock ? ` | Clock offset: ${Math.round(clock.offset)}ms ±${Math.round(clock.rtt / 2)}ms | Idle RTT: ${Math.round(clock.rtt)}ms` : ' | Clock: not synced'));
            lines.push('--------------------------------------------------');
            lines.push('SUMMARY (from tap)');
            const row = (label, value) => lines.push(`  ${label.padEnd(40)}: ${value}`);
            if (r.prevGapMs != null) row('Since previous transition', `${r.prevGapMs}ms` + (r.prevChannelOpenGapMs != null ? ` (between channel opens: ${r.prevChannelOpenGapMs}ms)` : ''));
            row('Tap → server received', since(tap.t, find(r, 'S', n => n.startsWith('Server: Transition #'))));

            if (r.isChat) {
                row('Tap → channel painted', since(tap.t, find(r, 'P', n => n.includes('channel visible'))));
                row('Tap → AppBar hidden painted', since(tap.t, find(r, 'P', n => n.includes('AppBar hidden'))));
                row('Tap → list visible painted', since(tap.t, find(r, 'P', n => n.includes('list visible'))));
                row('Tap → AppBar visible painted', since(tap.t, find(r, 'P', n => n.includes('AppBar visible'))));
                row('Tap → composer init done', since(tap.t, find(r, 'S', n => /ChatComposer\[.*Init chain.*End/.test(n))));
                row('Tap → 1st / newest image loaded', `${since(tap.t, find(r, 'C', n => n.includes('1st Media Loaded')))} / ${since(tap.t, find(r, 'C', n => n.includes('Newest Chat Image Loaded')))}`);
                if (dbDiskMatch) row('Server DB (cache / disk)', `cache=${dbDiskMatch[1]}, disk=${dbDiskMatch[2]} (index ${dbDiskMatch[3]}ms, disk ${dbDiskMatch[4]}ms)`);
                if (payloadEntry) row('Payload composition', payloadEntry.name.replace(/^Server:\s*Channel Payload Stats\s*/, ''));
                row('Chat instances', `${chatTags.join(' → ') || 'n/a'}${chatRemount ? '  (REMOUNT: new Chat initialized)' : ''}`);
                row('MainLayout instances', `${layoutTags.join(' → ') || 'n/a'}${layoutRemount ? '  (NEW MainLayout initialized)' : ''}`);
            } else {
                const routeLabel = r.dom.routePath && r.dom.routePath !== r.route ? `${r.route} → ${r.dom.routePath}` : r.route;
                row('Route', routeLabel);
                row('Tap → first RenderBatch', since(tap.t, find(r, 'W', n => n.includes('WS ← JS.RenderBatch'))));
                const drawerPainted = find(r, 'P', n => n.includes('drawer open') || n.includes('drawer closed'));
                if (drawerPainted || effectiveName.startsWith('Drawer')) {
                    row('Tap → drawer painted', since(tap.t, drawerPainted));
                }
                const dialogPainted = find(r, 'P', n => n.includes('dialog visible') || n.includes('dialog closed'));
                if (dialogPainted || effectiveName.startsWith('Dialog')) {
                    row('Tap → dialog painted', since(tap.t, dialogPainted));
                }
                const routePainted = find(r, 'P', n => n.includes('Painted: route '));
                if (routePainted || effectiveName.startsWith('NavLink') || effectiveName.startsWith('PopState')) {
                    row('Tap → route painted', since(tap.t, routePainted));
                }
                if (layoutTags.length > 0 || layoutRemount) {
                    row('MainLayout instances', `${layoutTags.join(' → ') || 'n/a'}${layoutRemount ? '  (NEW MainLayout initialized)' : ''}`);
                }
            }

            row('Wire', `${w.batch || 0} render batches (${((w.batchBytes || 0) / 1024).toFixed(1)}KB, max ${((w.maxBatchBytes || 0) / 1024).toFixed(1)}KB), ${w.jscall || 0} JS calls, ${w.jsdone || 0} JS completions, ${w.ack || 0} acks`);
            if (r.batchXRay) row('RenderBatch X-Ray', r.batchXRay.summary);
            if (r.largeBatchInfo) row('Large RenderBatch DOM', r.largeBatchInfo);
            row('Main thread long tasks', typeof PerformanceObserver !== 'undefined' && (PerformanceObserver.supportedEntryTypes?.includes('long-animation-frame') || PerformanceObserver.supportedEntryTypes?.includes('longtask'))
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
            finished.push({ id: r.id, name: r.serverName || r.name, isChat: r.isChat, route: r.route, clock, truncated, entries: r.entries.map(e => ({ lane: e.lane, name: e.name, ms: Math.round(e.t - t0) })) });
            if (finished.length > MAX_REPORTS) finished.shift();
        }

        // ── PII-safe interactive control labeler ──
        const GENERIC_EXCLUDED_SELECTOR = 'input:not([type="button"]):not([type="submit"]):not([type="checkbox"]):not([type="radio"]), textarea, select, [contenteditable="true"], .mud-slider, .mud-range-slider';
        const GENERIC_INTERACTIVE_SELECTOR = [
            '[data-diag-label]',
            'button',
            '[role="button"]',
            '.mud-button-root',
            '.mud-icon-button',
            '.mud-fab',
            '.mud-chip',
            '.mud-menu-item',
            'a[href]',
            '.album-card',
            '.m-cell',
            '.calendar-cell',
            '.calendar-event',
            '.agenda-row',
            '.agenda-block'
        ].join(', ');

        function describeControl(el) {
            if (!el) return 'control';
            const diagLabel = typeof el.getAttribute === 'function' ? el.getAttribute('data-diag-label') : null;
            if (diagLabel) return diagLabel.slice(0, 40);
            if (el.id) return `#${el.id}`;
            if (typeof el.getAttribute === 'function') {
                const href = el.getAttribute('href');
                if (href && href !== '#' && !href.startsWith('javascript:')) {
                    return `link:${href.split('?')[0].slice(0, 40)}`;
                }
            }
            const tag = (el.tagName || 'el').toLowerCase();
            const cls = typeof el.className === 'string'
                ? el.className.trim().split(/\s+/).filter(c => c && !c.startsWith('mud-ripple') && !c.startsWith('d-') && !c.startsWith('pa-') && !c.startsWith('ma-')).slice(0, 2).join('.')
                : '';
            return cls ? `${tag}.${cls}` : tag;
        }

        function classifyClick(target) {
            if (!target || typeof target.closest !== 'function') return null;

            // Priority 1: Chat transitions (preserves exact existing Chat diagnostics)
            const chatBack = target.closest('#chat-mobile-back-btn, #chat-drop-zone button.d-md-none');
            if (chatBack) {
                return { name: 'GoBack', step: 'Client: Tap Back Button', kind: 'chat', targetEl: chatBack };
            }
            const channelRow = target.closest('.chat-page-container .mud-list-item-clickable');
            if (channelRow && !target.closest('button')) {
                const alreadyInChannel = Boolean(getMessagesChannelId());
                return {
                    name: alreadyInChannel ? 'SwitchChannel' : 'SelectChannel',
                    step: 'Client: Tap Channel Item',
                    kind: 'chat',
                    targetEl: channelRow
                };
            }

            // Priority 2: App shell & navigation drawer
            const drawerBtn = target.closest('#nav-drawer-toggle-btn');
            if (drawerBtn) {
                const nextAction = isDrawerOpen() ? 'Close' : 'Open';
                return {
                    name: `DrawerToggle (${nextAction})`,
                    step: `Client: Tap Nav Drawer Button (${nextAction})`,
                    kind: 'ui',
                    targetEl: drawerBtn
                };
            }
            const drawerBackdrop = target.closest('.mud-overlay-drawer');
            if (drawerBackdrop) {
                return {
                    name: 'DrawerClose (Backdrop)',
                    step: 'Client: Tap Drawer Backdrop',
                    kind: 'ui',
                    targetEl: drawerBackdrop
                };
            }
            const navLink = target.closest('#nav-drawer .mud-nav-link, #nav-drawer a[href]');
            if (navLink) {
                const rawHref = typeof navLink.getAttribute === 'function' ? (navLink.getAttribute('href') || '/') : '/';
                const cleanHref = rawHref.split('?')[0] || '/';
                return {
                    name: `NavLink (${cleanHref})`,
                    step: `Client: Tap Nav Link (${cleanHref})`,
                    kind: 'ui',
                    targetEl: navLink
                };
            }

            // Priority 3: General interactive UI controls (FABs, buttons, dialogs, calendar cells/events, chips)
            if (target.closest(GENERIC_EXCLUDED_SELECTOR)) return null;
            const control = target.closest(GENERIC_INTERACTIVE_SELECTOR);
            if (!control) return null;
            const label = describeControl(control);
            return {
                name: `UIAction (${label})`,
                step: `Client: Tap ${label}`,
                kind: 'ui',
                targetEl: control
            };
        }

        // ── Event triggers (attached only while profiler is enabled) ──
        function onPointerDown() { if (active && server) lastPointerDown = now(); }
        function onPointerUp() { if (active && server) lastPointerUp = now(); }
        function onClick(e) {
            if (!active || !e || !e.target) return;
            const hit = classifyClick(e.target);
            if (!hit) return;
            startTransition(hit.name, hit.step, { kind: hit.kind, targetEl: hit.targetEl });
        }
        function onPopState() {
            if (!active) return;
            const path = getRoutePath();
            if (server || window.spokesDotNetRef) {
                startTransition(`PopState (${path})`, 'Client: Browser PopState', { kind: path.startsWith('/chat') ? 'chat' : 'ui' });
            } else {
                flushMediaBatch('popstate');
            }
        }

        function attachListeners() {
            if (listenersAttached) return;
            listenersAttached = true;
            document.addEventListener('pointerdown', onPointerDown, true);
            document.addEventListener('pointerup', onPointerUp, true);
            document.addEventListener('click', onClick, true);
            window.addEventListener('popstate', onPopState);
            window.addEventListener('spokes:blazor-connected', onBlazorConnected);
        }

        function detachListeners() {
            if (!listenersAttached) return;
            listenersAttached = false;
            document.removeEventListener('pointerdown', onPointerDown, true);
            document.removeEventListener('pointerup', onPointerUp, true);
            document.removeEventListener('click', onClick, true);
            window.removeEventListener('popstate', onPopState);
            window.removeEventListener('spokes:blazor-connected', onBlazorConnected);
        }

        return {
            async enable(dotNetRef) {
                server = dotNetRef || window.spokesDotNetRef || server;
                active = true;
                missingServerWarned = false;
                cachedClock = null;
                bridge.setProfilerActive(true);
                bridge.setMediaHook({
                    onFirstMediaLoaded: (label, ts) => { if (active && current) add(current, 'C', label, ts); },
                    onNewestChatImageLoaded: (label, ts) => { if (active && current) add(current, 'C', label, ts); },
                    onMediaItemLoaded: (transferSize) => {
                        if (active && current) {
                            current.mediaLoaded = (current.mediaLoaded || 0) + 1;
                            current.mediaBytes = (current.mediaBytes || 0) + (transferSize || 0);
                        }
                    }
                });
                attachListeners();
                hookSocket();
                await syncServerProfilerFlag(true);
            },
            async disable() {
                active = false;
                current = null;
                cachedClock = null;
                missingServerWarned = false;
                open.length = 0;
                stopObserversIfIdle(true);
                detachListeners();
                window.__spokesWsIn = null;
                bridge.setMediaHook(null);
                bridge.setProfilerActive(false);
                await syncServerProfilerFlag(false);
            },
            isEnabled: () => active,
            attachServer: (dotNetRef) => {
                server = dotNetRef;
                missingServerWarned = false;
                if (active) {
                    hookSocket();
                    syncServerProfilerFlag(true);
                }
            },
            startTransition,
            mark: (name) => {
                if (!active || !current) return;
                checkDomState();
                add(current, 'C', name);
            },
            reports: finished,
            dump: () => JSON.stringify(finished[finished.length - 1] || null, null, 2)
        };
    };
})();
