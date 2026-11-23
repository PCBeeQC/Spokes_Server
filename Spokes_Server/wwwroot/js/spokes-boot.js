// Spokes Boot, Reconnect, Startup Diagnostics & Lazy-Loader Runtime
// Extracted from App.razor into a fingerprinted, Brotli-precompressed, immutable static asset.
(function () {
const bootConfig = window.__spokesBootConfig || {};
const bootAssets = bootConfig.assets || {};
window.__startupCorrelationId = bootConfig.startupId || '';
window.__clientLocalTimings = bootConfig.clientTimings || '';
try {
    if (!window.__startupCorrelationId || !window.__clientLocalTimings) {
        const storedMeta = JSON.parse(sessionStorage.getItem('spokes_startup_meta') || 'null');
        if (storedMeta) {
            if (!window.__startupCorrelationId && storedMeta.startupId) window.__startupCorrelationId = storedMeta.startupId;
            if (!window.__clientLocalTimings && storedMeta.clientTimings) window.__clientLocalTimings = storedMeta.clientTimings;
        }
    }
    if (!window.__startupCorrelationId) {
        window.__startupCorrelationId = 'st-' + Math.random().toString(36).substring(2, 10);
    }
    sessionStorage.setItem('spokes_startup_meta', JSON.stringify({ startupId: window.__startupCorrelationId, clientTimings: window.__clientLocalTimings }));
} catch (e) {}
window.__signalRLogLevel = bootConfig.signalRLogLevel || '';
window.__spokesChatProfilerUrl = bootAssets.chatProfiler || '/js/spokes-chat-profiler.js';
window.__spokesPrintConsoleCommands = function () {
    console.log([
        '[Spokes] Console Commands (chat profiler OFF by default; resets on page refresh):',
        '  spokesPerf.summary()                — Print session TTI & media baseline summary',
        '  spokesPerf.exportBaseline()         — Export structured JSON baseline metrics',
        '  spokesPerf.enableChatDiagnostics()  — Enable live chat-transition & wire profiler',
        '  spokesPerf.disableChatDiagnostics() — Disable chat-transition profiler',
        '  spokesPerf.reset()                  — Clear session performance counters',
        '  spokesPerf.help()                   — Show this command list again'
    ].join('\n'));
};
window.__spokesPrintConsoleCommands();
window.__isStaticLoginRoute = Boolean(bootConfig.isStaticLoginRoute);
window.__spokesSession = window.__spokesSession || bootConfig.session || { authenticated: false, expiresAt: 0, userScope: null, hasVaultKey: false };
document.addEventListener('submit', function (ev) {
    try {
        const form = ev.target;
        if (!form || !form.getAttribute) return;
        const action = (form.getAttribute('action') || '').split('?')[0].toLowerCase();
        if (action === 'logout' || action === '/logout' || action.endsWith('/logout')) {
            window.spokesMediaLoader?.clearAllCache?.();
        }
    } catch (e) {}
}, true);
window.startupTimings = window.startupTimings || {};
if (!window.startupTimings.remote_html_received) {
    window.startupTimings.remote_html_received = Date.now();
}
window.startupTimings.boot_script_ready = Date.now();

// Shared exact .NET 9 Blazor Server binary RenderBatch X-Ray inspector.
// Reads the trailing 5 int32 section offsets (updatedComponents, referenceFrames,
// disposedComponentIds, disposedEventHandlerIds, stringTableStart) straight from the
// BlazorPack JS.RenderBatch binary argument without retaining raw frame buffers.
window.__spokesInspectRenderBatch = (function () {
    const decoder = typeof TextDecoder !== 'undefined' ? new TextDecoder('utf-8') : null;
    const TARGET = 'JS.RenderBatch';

    function readInt32LE(b, pos) {
        return b[pos] | (b[pos + 1] << 8) | (b[pos + 2] << 16) | (b[pos + 3] << 24);
    }

    function skipMsgPackInt(b, i) {
        const x = b[i];
        if (x < 0x80 || x >= 0xe0) return i + 1;
        if (x === 0xcc || x === 0xd0) return i + 2;
        if (x === 0xcd || x === 0xd1) return i + 3;
        if (x === 0xce || x === 0xd2) return i + 5;
        if (x === 0xcf || x === 0xd3) return i + 9;
        return -1;
    }

    function findAscii(b, str, maxOffset) {
        const limit = Math.min(b.length - str.length, maxOffset);
        const first = str.charCodeAt(0);
        for (let i = 0; i <= limit; i++) {
            if (b[i] !== first) continue;
            let ok = true;
            for (let j = 1; j < str.length; j++) {
                if (b[i + j] !== str.charCodeAt(j)) { ok = false; break; }
            }
            if (ok) return i;
        }
        return -1;
    }

    function extractBatchBytes(rawData) {
        let bytes = null;
        if (rawData instanceof ArrayBuffer) bytes = new Uint8Array(rawData);
        else if (ArrayBuffer.isView(rawData)) bytes = new Uint8Array(rawData.buffer, rawData.byteOffset, rawData.byteLength);
        if (!bytes || bytes.length < 24) return null;

        const targetIdx = findAscii(bytes, TARGET, 512);
        if (targetIdx < 0) return null;

        let i = targetIdx + TARGET.length;
        if (i >= bytes.length) return null;
        // Skip args array header ([batchId, batchBytes])
        const arrHdr = bytes[i];
        if (arrHdr >= 0x90 && arrHdr <= 0x9f) i += 1;
        else if (arrHdr === 0xdc) i += 3;
        else if (arrHdr === 0xdd) i += 5;
        else return null;

        // Skip batchId integer
        i = skipMsgPackInt(bytes, i);
        if (i < 0 || i >= bytes.length) return null;

        // Read MessagePack bin header
        const binHdr = bytes[i];
        let binLen = 0, binStart = 0;
        if (binHdr === 0xc4 && i + 2 <= bytes.length) {
            binLen = bytes[i + 1];
            binStart = i + 2;
        } else if (binHdr === 0xc5 && i + 3 <= bytes.length) {
            binLen = (bytes[i + 1] << 8) | bytes[i + 2];
            binStart = i + 3;
        } else if (binHdr === 0xc6 && i + 5 <= bytes.length) {
            binLen = (((bytes[i + 1] << 24) >>> 0) | (bytes[i + 2] << 16) | (bytes[i + 3] << 8) | bytes[i + 4]) >>> 0;
            binStart = i + 5;
        } else {
            return null;
        }
        if (binLen < 20 || binStart + binLen > bytes.length) return null;
        return bytes.subarray(binStart, binStart + binLen);
    }

    function sanitizeStringPreview(s) {
        return s
            .replace(/([?&]t=)[^&\s"'<>]+/gi, '$1<tok>')
            .replace(/data:[^;,\s"'<>]+,[A-Za-z0-9+/=]{24,}/gi, 'data:<redacted>')
            .replace(/\s+/g, ' ')
            .trim()
            .slice(0, 44);
    }

    function categorizeString(s) {
        if (!s) return 'other';
        if (s.startsWith('/spokesapi/') || s.startsWith('http://') || s.startsWith('https://') || s.startsWith('data:')) return 'urls';
        if (s.indexOf('<') >= 0 && s.indexOf('>') > s.indexOf('<')) return 'markup';
        if (s.indexOf(':') > 0 && s.indexOf(';') > 0 && (s.includes('px') || s.includes('var(') || s.includes('flex') || s.includes('display:') || s.includes('width:') || s.includes('height:') || s.includes('color:') || s.includes('background'))) return 'style';
        if (s.startsWith('mud-') || s.startsWith('d-flex') || s.startsWith('pa-') || s.startsWith('ma-') || s.startsWith('chat-') || s.startsWith('message-') || s.startsWith('voice-')) return 'class';
        return 'other';
    }

    function decodeUtf8(b, start, len) {
        const slice = b.subarray(start, start + len);
        if (decoder) return decoder.decode(slice);
        let out = '';
        for (let k = 0; k < slice.length; k++) out += String.fromCharCode(slice[k]);
        return out;
    }

    function fmtKb(bytes) {
        return bytes >= 1024 ? `${(bytes / 1024).toFixed(1)}KB` : `${bytes}B`;
    }

    return function inspectRenderBatch(rawData) {
        try {
            const b = extractBatchBytes(rawData);
            if (!b) return null;
            const L = b.length;
            const updatedCompPos = readInt32LE(b, L - 20);
            const refFramesPos = readInt32LE(b, L - 16);
            const disposedCompPos = readInt32LE(b, L - 12);
            const disposedHandlerPos = readInt32LE(b, L - 8);
            const stringTableStart = readInt32LE(b, L - 4);

            if (updatedCompPos < 0 || updatedCompPos + 4 > L ||
                refFramesPos < 0 || refFramesPos + 4 > L ||
                disposedCompPos < 0 || disposedCompPos + 4 > L ||
                disposedHandlerPos < 0 || disposedHandlerPos + 4 > L ||
                stringTableStart < 0 || stringTableStart > L - 20) {
                return null;
            }

            const components = readInt32LE(b, updatedCompPos);
            const frames = readInt32LE(b, refFramesPos);
            const disposedComponents = readInt32LE(b, disposedCompPos);
            const disposedHandlers = readInt32LE(b, disposedHandlerPos);
            const stringTableBytes = (L - 20) - stringTableStart;
            const stringCount = stringTableBytes >= 0 ? (stringTableBytes >> 2) : 0;

            const categories = { markup: 0, urls: 0, style: 0, class: 0, other: 0 };
            let stringsBytes = 0;
            const top = [];

            for (let s = 0; s < stringCount; s++) {
                const offset = readInt32LE(b, stringTableStart + (s * 4));
                if (offset < 0 || offset >= L) continue;
                let strLen = 0, shift = 0, lenBytes = 0;
                for (let r = 0; r < 5 && (offset + r) < L; r++) {
                    const byte = b[offset + r];
                    strLen |= (byte & 0x7f) << shift;
                    lenBytes++;
                    if (byte < 0x80) break;
                    shift += 7;
                }
                const strStart = offset + lenBytes;
                if (strLen < 0 || strStart + strLen > L) continue;
                stringsBytes += strLen + lenBytes;

                const sampleLen = Math.min(strLen, 256);
                const sample = decodeUtf8(b, strStart, sampleLen);
                const cat = categorizeString(sample);
                categories[cat] += strLen;

                if (strLen >= 48) {
                    if (top.length < 5 || strLen > top[top.length - 1].bytes) {
                        top.push({ bytes: strLen, cat, preview: sanitizeStringPreview(sample) });
                        top.sort((a, x) => x.bytes - a.bytes);
                        if (top.length > 5) top.length = 5;
                    }
                }
            }

            const framesBytes = Math.max(0, frames * 20);
            const catParts = ['markup', 'urls', 'style', 'class', 'other']
                .filter(k => categories[k] > 0)
                .map(k => `${k} ${fmtKb(categories[k])}`);
            const topParts = top.slice(0, 4).map(t => `${fmtKb(t.bytes)}(${t.cat}:"${t.preview}")`);
            const summary = `${fmtKb(L)} = ${components} comps, ${frames} frames (${fmtKb(framesBytes)}), ` +
                `${stringCount} strings (${fmtKb(stringsBytes)}${catParts.length ? ': ' + catParts.join(', ') : ''}), ` +
                `disposed ${disposedComponents}c/${disposedHandlers}h` +
                (topParts.length ? ` | top: ${topParts.join(', ')}` : '');

            return {
                batchBytes: L,
                components,
                frames,
                framesBytes,
                disposedComponents,
                disposedHandlers,
                stringCount,
                stringsBytes,
                categories,
                topStrings: top,
                summary
            };
        } catch (_) {
            return null;
        }
    };
})();

window.__spokesPerfTelemetry = window.__spokesPerfTelemetry || {
    version: 2,
    boot: {
        settled: false,
        layoutSettled: false,
        routeSettled: false,
        settledRoute: null,
        clickOriginMs: 0,
        shellInteractiveAt: 0,
        shellPaintedAt: 0,
        shellTtiMs: 0,
        routeSettledAt: 0,
        clickToUsableMs: 0,
        navToUsableMs: 0,
        cookieBootstrapDetourMs: 0,
        usedPrewarmedWs: false,
        forcedNegotiateFallback: false,
        wsExtensions: '',
        startupBatch: null,
        serverTimings: [],
        timeline: []
    },
    navigation: null,
    resources: null,
    mainThread: { longTaskCount: 0, totalBlockingTimeMs: 0, maxLongTaskMs: 0, topAttribution: null, _entries: [] },
    switches: [],
    reconnects: []
};
try {
    if (typeof PerformanceObserver !== 'undefined') {
        const supported = PerformanceObserver.supportedEntryTypes || [];
        const useLoaf = supported.includes('long-animation-frame');
        const ltObs = new PerformanceObserver((list) => {
            const entries = list.getEntries();
            const mt = window.__spokesPerfTelemetry && window.__spokesPerfTelemetry.mainThread;
            if (!mt || !mt._entries) return;
            for (let i = 0; i < entries.length; i++) {
                const e = entries[i];
                let attribution = null;
                if (useLoaf && Array.isArray(e.scripts) && e.scripts.length > 0) {
                    let topScript = e.scripts[0];
                    for (let j = 1; j < e.scripts.length; j++) {
                        if ((e.scripts[j].duration || 0) > (topScript.duration || 0)) topScript = e.scripts[j];
                    }
                    const srcFile = topScript.sourceURL ? topScript.sourceURL.split('/').pop().split('?')[0] : '';
                    const invoker = topScript.invoker || topScript.sourceFunctionName || topScript.invokerType || 'script';
                    attribution = `${invoker}${srcFile ? '@' + srcFile : ''}(${Math.round(topScript.duration || 0)}ms)`;
                }
                mt._entries.push({
                    startTime: e.startTime,
                    duration: e.duration,
                    blockingDuration: useLoaf ? (e.blockingDuration || Math.max(0, e.duration - 50)) : Math.max(0, e.duration - 50),
                    attribution
                });
            }
        });
        ltObs.observe({ type: useLoaf ? 'long-animation-frame' : 'longtask', buffered: true });
        window._spokesStartupLongTaskObserver = ltObs;
    }
} catch (e) {}

console.log('[App.razor] Remote HTML received. Startup ID:', window.__startupCorrelationId);

// Boot diagnostic: says why this boot happened and what made it slow, so a slow start can be
// attributed to (a) a cold network/device, (b) the expired-cookie bootstrap detour, or (c) a page
// that booted while hidden (e.g. iOS reloading the WebView after its content process was killed).
try {
    const facts = [];
    const ms = (from, to) => Math.max(0, Math.round(to - from));

    // 'spokes_last_hidden_at' is written when the page is hidden and cleared when it becomes visible
    // again, so if it is still set now, the previous page was destroyed while in the background
    // (app or WebView process killed by the OS, or swiped away).
    const lastHiddenAt = parseInt(localStorage.getItem('spokes_last_hidden_at') || '0', 10);
    localStorage.removeItem('spokes_last_hidden_at');
    facts.push(lastHiddenAt > 0
        ? 'previous page was lost in background (hidden ' + ((Date.now() - lastHiddenAt) / 60000).toFixed(1) + ' min ago)'
        : 'launch or navigation');

    window.startupTimings.boot_hidden = document.visibilityState === 'hidden';
    facts.push(window.startupTimings.boot_hidden ? 'booted while HIDDEN' : 'booted visible');

    // Network cost of this page's HTML request. Large dns/connect = cold radio/DNS/TLS; large ttfb = slow server.
    const nav = performance.getEntriesByType('navigation')[0];
    const origin = Math.round(performance.timeOrigin);
    if (nav) {
        facts.push('nav ' + nav.type
            + ', dns ' + ms(nav.domainLookupStart, nav.domainLookupEnd) + 'ms'
            + ', connect ' + ms(nav.connectStart, nav.connectEnd) + 'ms'
            + (nav.secureConnectionStart > 0 ? ' (tls ' + ms(nav.secureConnectionStart, nav.connectEnd) + 'ms)' : '')
            + ', ttfb ' + ms(nav.requestStart, nav.responseStart) + 'ms'
            + ', html done at +' + Math.round(nav.responseEnd) + 'ms');
        window.startupTimings.nav_start = origin;
        if (nav.requestStart > 0) window.startupTimings.nav_request = origin + Math.round(nav.requestStart);
        if (nav.responseStart > 0) window.startupTimings.nav_first_byte = origin + Math.round(nav.responseStart);
    }

    // Set by the mobile splash interceptor's bootstrap page just before it POSTs the refresh token,
    // i.e. the page request arrived without a valid session cookie.
    const bootstrap = JSON.parse(sessionStorage.getItem('spokes_bootstrap') || 'null');
    sessionStorage.removeItem('spokes_bootstrap');
    if (bootstrap && bootstrap.start > 0 && origin - bootstrap.start < 60000) {
        window.startupTimings.bootstrap_start = bootstrap.start;
        facts.push('COOKIE DETOUR (expired/missing cookie): bootstrap page + POST + redirect cost ' + ms(bootstrap.start, origin) + 'ms');
    } else {
        facts.push('no cookie detour');
    }

    window.startupTimings.boot_reason = facts.join('; ');
    console.log('[Startup] Boot reason: ' + window.startupTimings.boot_reason);
} catch (e) {}
    
try {
    const NEGOTIATE_TIMEOUT_MS = 5000;
    const originalFetch = window.fetch;
    window.fetch = async function() {
        let url = '';
        if (arguments[0] instanceof Request) url = arguments[0].url;
        else if (typeof arguments[0] === 'string') url = arguments[0];
        
        if (url && typeof url === 'string' && url.includes('_blazor/initializers')) {
            return new Response('[]', {
                status: 200,
                headers: { 'Content-Type': 'application/json' }
            });
        }

        if (url && typeof url === 'string' && url.includes('/negotiate')) {
            window.startupTimings.negotiate_start = Date.now();
            const controller = typeof AbortController === 'function' ? new AbortController() : null;
            let timeoutId = null;
            let fetchArgs = Array.prototype.slice.call(arguments);
            if (controller) {
                window.__pendingNegotiateController = controller;
                const existingSignal = (arguments[0] instanceof Request ? arguments[0].signal : null)
                    || (arguments[1] && arguments[1].signal)
                    || null;
                if (existingSignal) {
                    if (existingSignal.aborted) {
                        controller.abort();
                    } else if (typeof existingSignal.addEventListener === 'function') {
                        existingSignal.addEventListener('abort', () => {
                            try { controller.abort(); } catch (e) {}
                        }, { once: true });
                    }
                }
                if (arguments[0] instanceof Request) {
                    fetchArgs[0] = new Request(arguments[0], { signal: controller.signal });
                } else {
                    fetchArgs[1] = Object.assign({}, arguments[1] || {}, { signal: controller.signal });
                }
                timeoutId = setTimeout(() => {
                    try { controller.abort(); } catch (e) {}
                }, NEGOTIATE_TIMEOUT_MS);
            }
            try {
                const res = await originalFetch.apply(this, fetchArgs);
                window.startupTimings.negotiate_end = Date.now();
                return res;
            } catch(err) {
                console.error(`[App.razor] Fetch failed for ${url}`, err);
                throw err;
            } finally {
                if (timeoutId) clearTimeout(timeoutId);
                if (controller && window.__pendingNegotiateController === controller) {
                    window.__pendingNegotiateController = null;
                }
            }
        }
        return originalFetch.apply(this, arguments);
    };
} catch(e) {}

// Patch WebSocket to track startup connection timing (TCP + TLS + upgrade handshake).
// Only the startup socket is instrumented, and the per-message listener detaches once the
// app is interactive so it never adds work to the steady-state render stream.
try {
    const attachBlazorSocketDiagnostics = (ws, isPrewarmed) => {
        if (!ws || ws._spokesDiagAttached) return;
        ws._spokesDiagAttached = true;
        window.__spokesBlazorSocket = ws;
        window.__lastBlazorServerMsgAt = Date.now();
        window.__pendingBlazorWs = ws;
        ws.addEventListener('close', () => {
            if (window.__pendingBlazorWs === ws) window.__pendingBlazorWs = null;
        }, { once: true });
        // Registered before SignalR assigns onmessage: updates liveness timestamp and optionally forwards frames when chat profiler is enabled.
        ws.addEventListener('message', (e) => {
            window.__lastBlazorServerMsgAt = Date.now();
            const f = window.__spokesWsIn;
            if (f) f(e.data);
        });

        if (!window.startupTimings.interactive) {
            if (!isPrewarmed || !window.startupTimings.ws_open_start) {
                window.startupTimings.ws_open_start = Date.now();
            }
            if (ws.readyState === 1) {
                window.__spokesWsExtensions = ws.extensions || '';
                if (!window.startupTimings.ws_open_end) {
                    window.startupTimings.ws_open_end = Date.now();
                }
            } else {
                ws.addEventListener('open', () => {
                    window.__spokesWsExtensions = ws.extensions || '';
                    if (!window.startupTimings.ws_open_end) {
                        window.startupTimings.ws_open_end = Date.now();
                    }
                }, { once: true });
            }
            let msgIndex = 0;
            const onStartupMessage = (event) => {
                if (window.startupTimings.interactive) {
                    ws.removeEventListener('message', onStartupMessage);
                    return;
                }
                msgIndex++;
                let size = 0;
                if (typeof event.data === 'string') {
                    size = event.data.length;
                } else if (event.data instanceof ArrayBuffer) {
                    size = event.data.byteLength;
                } else if (event.data && typeof event.data.size === 'number') {
                    size = event.data.size;
                }
                const now = Date.now();
                if (msgIndex === 1) {
                    window.startupTimings.ws_msg_1 = now;
                }
                if (size > (window.startupTimings.ws_render_batch_size || 0)) {
                    window.startupTimings.ws_render_batch = now;
                    window.startupTimings.ws_render_batch_size = size;
                    if (event.data instanceof ArrayBuffer && typeof window.__spokesInspectRenderBatch === 'function') {
                        const inspected = window.__spokesInspectRenderBatch(event.data);
                        if (inspected) {
                            window.startupTimings.ws_render_batch_inspect = inspected;
                        }
                    }
                }
            };
            ws.addEventListener('message', onStartupMessage);
        }
    };

    if (window.__spokesPrewarmedWs && window.__spokesPrewarmedWs.ws) {
        attachBlazorSocketDiagnostics(window.__spokesPrewarmedWs.ws, true);
        if (window.__spokesPrewarmedWs.messages && window.__spokesPrewarmedWs.messages.length > 0 && !window.startupTimings.ws_msg_1) {
            window.startupTimings.ws_msg_1 = Date.now();
        }
    }

    const OriginalWebSocket = window.WebSocket;
    window.WebSocket = function(url, protocols) {
        const ws = protocols ? new OriginalWebSocket(url, protocols) : new OriginalWebSocket(url);
        if (url && typeof url === 'string' && url.includes('_blazor')) {
            attachBlazorSocketDiagnostics(ws, false);
        }
        return ws;
    };
    window.WebSocket.prototype = OriginalWebSocket.prototype;
    window.WebSocket.CONNECTING = OriginalWebSocket.CONNECTING;
    window.WebSocket.OPEN = OriginalWebSocket.OPEN;
    window.WebSocket.CLOSING = OriginalWebSocket.CLOSING;
    window.WebSocket.CLOSED = OriginalWebSocket.CLOSED;
} catch(e) {}

try {
    let firstMutationRecorded = false;
    window._startupMutationObserver = new MutationObserver(() => {
        if (!firstMutationRecorded) {
            firstMutationRecorded = true;
            window.startupTimings.first_dom_mutation = Date.now();
        }
    });
    window._startupMutationObserver.observe(document.documentElement, { childList: true, subtree: true });
} catch(e) {}

// ---------------------------------------------------------------------------------------
// Canonical splash & recovery watchdog controller.
// Invariant: whenever #startup-splash is visible during initial boot, reconnect, or reload,
// a bounded recovery watchdog guarantees Retry Connection / Switch Server actions appear
// instead of spinning indefinitely behind a blank overlay.
// ---------------------------------------------------------------------------------------
const checkIsNativePlatform = () => typeof window.isCapacitorNative === 'function'
    ? window.isCapacitorNative()
    : (typeof window.checkIsCapacitorNative === 'function'
        ? window.checkIsCapacitorNative()
        : (navigator.userAgent.includes('Capacitor') || Boolean(window.Capacitor && window.Capacitor.isNativePlatform && window.Capacitor.isNativePlatform())));

const splashController = (() => {
    const INITIAL_WATCHDOG_MS = 9500;
    const RECONNECT_WATCHDOG_MS = 8000;
    let watchdogTimer = null;
    let watchdogTriggered = false;

    const cancelDebounced = () => {
        if (window._reconnectSplashTimeout) {
            clearTimeout(window._reconnectSplashTimeout);
            window._reconnectSplashTimeout = null;
        }
    };

    const disarmWatchdog = () => {
        if (watchdogTimer) {
            clearTimeout(watchdogTimer);
            watchdogTimer = null;
        }
        watchdogTriggered = false;
        const actions = document.getElementById('spokes-splash-watchdog-actions');
        if (actions) actions.style.display = 'none';
    };

    const revealWatchdogActions = (mode) => {
        if (window._isServerRestarting) return;
        const splash = document.getElementById('startup-splash');
        if (!splash || splash.style.display === 'none') return;
        watchdogTriggered = true;
        const statusEl = document.getElementById('splash-status');
        if (statusEl && !statusEl.textContent) {
            statusEl.textContent = mode === 'initial'
                ? 'Connection is taking longer than expected. Check your network or proxy configuration.'
                : 'Reconnecting is taking longer than expected. Check your network or tap Retry.';
        }
        const switchBtn = document.getElementById('spokes-splash-switch-server-btn');
        if (switchBtn) {
            const hasSwitcher = checkIsNativePlatform()
                || typeof window.changeCapacitorServer === 'function'
                || Boolean(window.SpokesNative && (window.SpokesNative.openServerSwitcher || window.SpokesNative.openServer));
            switchBtn.style.display = hasSwitcher ? 'inline-block' : 'none';
        }
        const actions = document.getElementById('spokes-splash-watchdog-actions');
        if (actions) actions.style.display = 'flex';
    };

    const armWatchdog = (timeoutMs, mode = 'initial') => {
        disarmWatchdog();
        if (window.__isStaticLoginRoute || window._isServerRestarting) return;
        const delay = typeof timeoutMs === 'number' && timeoutMs > 0
            ? timeoutMs
            : (mode === 'initial' ? INITIAL_WATCHDOG_MS : RECONNECT_WATCHDOG_MS);
        watchdogTimer = setTimeout(() => {
            watchdogTimer = null;
            if (mode === 'initial' && window._blazorCircuitEverOpened) return;
            revealWatchdogActions(mode);
        }, delay);
    };

    const show = (options = {}) => {
        cancelDebounced();
        const splash = document.getElementById('startup-splash');
        const alreadyVisible = Boolean(splash && splash.style.display === 'flex');
        if (splash) splash.style.display = 'flex';
        if (typeof options.statusText === 'string') {
            const statusEl = document.getElementById('splash-status');
            if (statusEl) {
                statusEl.textContent = options.statusText;
                if (options.statusColor) statusEl.style.color = options.statusColor;
            }
        }
        if (!options.skipWatchdog) {
            if (!alreadyVisible || (!watchdogTimer && !watchdogTriggered)) {
                const mode = options.mode || 'reconnect';
                armWatchdog(options.watchdogMs || RECONNECT_WATCHDOG_MS, mode);
            }
        } else {
            disarmWatchdog();
        }
    };

    const scheduleDebounced = (shouldShowFn, onShownFn, delayMs = 1500) => {
        if (!shouldShowFn()) return;
        const splash = document.getElementById('startup-splash');
        if (splash && splash.style.display === 'flex') {
            if (!watchdogTimer && !watchdogTriggered && !window._isServerRestarting) {
                armWatchdog(RECONNECT_WATCHDOG_MS, 'reconnect');
            }
            return;
        }
        if (window._reconnectSplashTimeout) return;
        window._reconnectSplashTimeout = setTimeout(() => {
            window._reconnectSplashTimeout = null;
            if (!shouldShowFn()) return;
            show({ mode: 'reconnect', watchdogMs: RECONNECT_WATCHDOG_MS });
            if (typeof onShownFn === 'function') onShownFn();
        }, delayMs);
    };

    const hide = () => {
        cancelDebounced();
        disarmWatchdog();
        if (window._isServerRestarting || window._reloadInProgress) return;
        const splash = document.getElementById('startup-splash');
        if (splash) splash.style.display = 'none';
        const statusEl = document.getElementById('splash-status');
        if (statusEl) {
            statusEl.textContent = '';
            statusEl.style.color = '#999';
        }
    };

    return {
        INITIAL_WATCHDOG_MS,
        RECONNECT_WATCHDOG_MS,
        show,
        hide,
        scheduleDebounced,
        cancelDebounced,
        armWatchdog,
        disarmWatchdog,
        revealWatchdogActions
    };
})();

window.__disarmSpokesStartupWatchdog = () => splashController.disarmWatchdog();
window.__armSpokesStartupWatchdog = (timeoutMs = splashController.INITIAL_WATCHDOG_MS) => {
    splashController.armWatchdog(timeoutMs, 'initial');
};
window.spokesRetryConnection = () => {
    window._reloadInProgress = false;
    splashController.cancelDebounced();
    splashController.disarmWatchdog();
    if (typeof window.location.reload === 'function') {
        window.location.reload();
    } else {
        window.location.replace(window.location.href);
    }
};

// ---------------------------------------------------------------------------------------
// Resume / reconnect state machine.
// Design invariants:
//  * Act only while visible: reconnecting or reloading inside a suspended WebView hangs.
//  * Truth-based liveness probing: server circuit retention is 2 hours, so foreground resume
//    never forces a synthetic disconnect based on wall-clock background duration; instead,
//    foreground focus sync / PingCircuit verifies socket liveness and closes true zombie
//    sockets cleanly before Blazor.reconnect().
//  * Never reconnect with an expired session cookie: /_blazor is not auth-gated, so
//    ConnectCircuit would silently switch the live circuit to an anonymous user. Refresh
//    first, but only when the cookie is actually near expiry (90d sliding TTL).
//  * Never refresh before a reload: the mobile splash interceptor + mobile-bootstrap set the
//    cookie through a navigation response, which is the most reliable path.
//  * Bounded awaits everywhere: reconnect timeouts, foreground preemption, auth refresh waits,
//    and navigation reloads all enforce finite deadlines so #startup-splash can never deadlock.
//  * Single circuit-restored path: both onConnectionUp() and Blazor.reconnect() -> true
//    converge through completeCircuitConnected() so #startup-splash is always dismissed.
//  * Never reload while offline: a failed navigation shows the native offline overlay.
// ---------------------------------------------------------------------------------------
window.spokesResume = (() => {
    const RECONNECT_HANG_GUARD_MS = 4000;
    const RECONNECT_DRAIN_MS = 500;
    const SPLASH_DEBOUNCE_MS = 1500;
    const RELOAD_REFRESH_WAIT_MS = 2500;
    const RELOAD_STALL_GUARD_MS = 6000;
    const REACHABILITY_TIMEOUT_MS = 2000;
    const ZOMBIE_PROBE_TIMEOUT_MS = 4000;
    const KEEPALIVE_SILENCE_PROBE_MS = 35000;
    const MIN_HIDDEN_FOR_ZOMBIE_PROBE_MS = 3000;
    const EXPIRY_MARGIN_MS = 5 * 60 * 1000;
    const PROACTIVE_REFRESH_WINDOW_MS = 60 * 24 * 60 * 60 * 1000;
    const RETRY_DELAYS_MS = [1000, 2000, 4000, 8000, 15000];

    let running = false;
    let resumeGeneration = 0;
    let hiddenSinceAttemptStart = false;
    let reconnectPromise = null;
    let retryTimer = null;
    let reloadStallTimer = null;
    let zombieProbeInFlight = false;
    let reconnectedViaHandler = false;
    let failures = 0;
    let lastProactiveRefreshAt = 0;

    const sleep = (ms) => new Promise(resolve => setTimeout(resolve, ms));
    const isNative = () => checkIsNativePlatform();
    const isDesktop = () => Boolean((typeof window.isSpokesDesktop !== 'undefined' && window.isSpokesDesktop) || (window.SpokesBridge && window.SpokesBridge.isDesktop) || navigator.userAgent.includes('SpokesDesktop'));
    const isVisible = () => window._spokesHostViewActive !== false && (document.visibilityState === 'visible' || ((isNative() || isDesktop()) && window._spokesHostViewExplicitlyActivated === true));
    // Desktop pooled WebContentsViews (and active voice call sessions) are not OS-suspended when another
    // server tab is focused (_spokesHostViewActive === false). Reconnecting them in the background keeps
    // their Blazor circuits warm for instant tab switching and in-app notifications.
    const canReconnectNow = () => isVisible() || isDesktop() || (Boolean(window._spokesInVoiceCall) && document.visibilityState === 'visible');

    const mark = (name) => {
        if (window._reconnectTimings) window._reconnectTimings.timeline.push({ name: name, absolute: Date.now() });
    };
    const armSplashDebounce = () => {
        splashController.scheduleDebounced(
            () => Boolean(window._blazorDisconnected && !window._isServerRestarting && !window._reloadInProgress && isVisible()),
            () => {
                if (window._reconnectTimings) window._reconnectTimings.splashShown = true;
                mark('Splash Overlay Shown');
            },
            SPLASH_DEBOUNCE_MS
        );
    };
    const showSplash = (mode = 'reconnect') => {
        splashController.show({ mode: mode });
        if (window._reconnectTimings) window._reconnectTimings.splashShown = true;
    };
    const completeCircuitConnected = ({ emitEvent = true } = {}) => {
        failures = 0;
        zombieProbeInFlight = false;
        window.__pendingBlazorWs = null;
        if (retryTimer) {
            clearTimeout(retryTimer);
            retryTimer = null;
        }
        window._blazorDisconnected = false;
        window._lastHiddenDurationMs = 0;
        if (emitEvent) {
            try { window.dispatchEvent(new CustomEvent('spokes:blazor-connected')); } catch (e) {}
        }
        splashController.hide();
        if (!window._reloadInProgress && window.pendingNotificationRoute && typeof window.spokesNavigateFromNative === 'function') {
            const pendingRoute = window.pendingNotificationRoute;
            window.pendingNotificationRoute = null;
            window.spokesNavigateFromNative(pendingRoute);
        }
    };
    const msUntilSessionExpiry = () => {
        const expiresAt = window.__spokesSession?.expiresAt || 0;
        return expiresAt > 0 ? expiresAt - Date.now() : Infinity;
    };

    // Images requested while the cookie was expired fail; retry them once the cookie is fresh.
    const retryBrokenImages = () => {
        setTimeout(() => {
            if (window.spokesMediaLoader && typeof window.spokesMediaLoader.retryBroken === 'function') {
                window.spokesMediaLoader.retryBroken();
            }
            document.querySelectorAll('img:not([data-media-src])').forEach(img => {
                if (!img.complete || img.naturalWidth !== 0 || !img.src || img.dataset.retriedForResume) return;
                if (!img.src.includes('/spokesapi/') && !img.src.includes('/internal/')) return;
                img.dataset.retriedForResume = 'true';
                const cleanSrc = img.src.replace(/(&|\?)_retry=\d+/, '');
                const retryUrl = cleanSrc + (cleanSrc.includes('?') ? '&' : '?') + '_retry=' + Date.now();
                fetch(retryUrl, { method: 'HEAD', cache: 'no-store' })
                    .then(response => {
                        if (response.status === 410 || response.status === 401 || response.status === 404) {
                            console.warn('[Resume] Image retry returned ' + response.status + '. Triggering Blazor token recovery.');
                            if (window._imageRecoveryCoordinator) {
                                window._imageRecoveryCoordinator.invokeMethodAsync('TriggerTokenRecovery').catch(() => {});
                            }
                        } else {
                            img.src = retryUrl;
                        }
                    })
                    .catch(() => { img.src = retryUrl; });
            });
        }, 300);
    };

    const refreshSession = async () => {
        const refreshed = await window.spokesAuth.refreshSessionAsync();
        if (refreshed) retryBrokenImages();
        return refreshed;
    };

    const isServerReachable = async () => {
        const controller = new AbortController();
        const timer = setTimeout(() => controller.abort(), REACHABILITY_TIMEOUT_MS);
        try {
            const response = await fetch('/spokesapi/Manifest', { cache: 'no-store', signal: controller.signal });
            if (!response.ok) return false;
            const contentType = (response.headers.get('content-type') || '').toLowerCase();
            return contentType.includes('json');
        } catch (e) {
            return false;
        } finally {
            clearTimeout(timer);
        }
    };

    const reloadNow = async (reason) => {
        if (window._reloadInProgress) return;
        window._reloadInProgress = true;
        console.log('[Resume] Reloading: ' + reason);
        mark('Reload: ' + reason);
        showSplash('reload');
        // Never navigate away mid-refresh if it finishes promptly, but never block reload indefinitely.
        if (window.spokesAuth?._refreshPromise) {
            try {
                await Promise.race([
                    window.spokesAuth._refreshPromise.catch(() => {}),
                    sleep(RELOAD_REFRESH_WAIT_MS)
                ]);
            } catch (e) {}
        }
        // Keep diagnostics, but never let log persistence delay the reload by more than 150ms.
        if (window.flushAppLogs) {
            await Promise.race([window.flushAppLogs().catch(() => {}), sleep(150)]);
        }
        const url = new URL(window.spokesPendingNav ? (location.origin + window.spokesPendingNav) : location.href);
        if (isNative()) url.searchParams.set('client', 'mobile');
        else url.searchParams.delete('client');

        // Release latch if iOS cancels/drops the navigation (e.g. NSURLErrorCancelled -999)
        // so Retry or subsequent foreground/online events can recover cleanly.
        if (reloadStallTimer) clearTimeout(reloadStallTimer);
        reloadStallTimer = setTimeout(() => {
            reloadStallTimer = null;
            if (window._reloadInProgress) {
                console.warn('[Resume] Navigation reload stalled; releasing reload lock and revealing recovery actions.');
                window._reloadInProgress = false;
                splashController.revealWatchdogActions('reload');
            }
        }, RELOAD_STALL_GUARD_MS);

        const targetHref = url.toString();
        const sameDocWithHash = Boolean(url.hash)
            && url.origin === location.origin
            && url.pathname === location.pathname
            && url.search === location.search;
        if ((targetHref === location.href || sameDocWithHash) && typeof location.reload === 'function') {
            if (url.hash !== location.hash) {
                try { window.history.replaceState(null, '', targetHref); } catch (e) {}
            }
            location.reload();
        } else {
            location.replace(targetHref);
        }
    };

    window.addEventListener('pagehide', () => {
        if (reloadStallTimer) {
            clearTimeout(reloadStallTimer);
            reloadStallTimer = null;
        }
    });

    // A single in-flight Blazor.reconnect() is shared across callers. When RECONNECT_HANG_GUARD_MS
    // expires (or a backgrounded attempt is preempted on foreground resume), pending WebSocket /
    // negotiate transports are aborted and we wait at most RECONNECT_DRAIN_MS before detaching
    // so a hung promise can never lock running === true forever.
    const attemptReconnect = async () => {
        if (!reconnectPromise) {
            mark('Blazor.reconnect()');
            const p = Blazor.reconnect()
                .then(ok => ok ? 'ok' : 'rejected',
                      err => String(err).includes("Status code '401'") ? 'rejected' : 'error')
                .finally(() => {
                    if (reconnectPromise === p) reconnectPromise = null;
                });
            reconnectPromise = p;
        }
        const currentPromise = reconnectPromise;
        let timedOut = false;
        let timerId = null;
        const timeoutPromise = new Promise(resolve => {
            timerId = setTimeout(() => {
                timedOut = true;
                if (typeof window.__abortPendingBlazorSocket === 'function') {
                    window.__abortPendingBlazorSocket('Reconnect hang guard timeout');
                }
                resolve('timeout');
            }, RECONNECT_HANG_GUARD_MS);
        });
        try {
            const outcome = await Promise.race([currentPromise, timeoutPromise]);
            if (timedOut) {
                const drainedOutcome = await Promise.race([
                    currentPromise.catch(() => 'error'),
                    sleep(RECONNECT_DRAIN_MS).then(() => 'timeout')
                ]);
                if (reconnectPromise === currentPromise) {
                    reconnectPromise = null;
                }
                return drainedOutcome === 'ok' ? 'ok' : 'timeout';
            }
            return outcome;
        } finally {
            if (timerId) clearTimeout(timerId);
        }
    };

    const scheduleRetry = () => {
        if (retryTimer) return;
        const delay = RETRY_DELAYS_MS[Math.min(Math.max(1, failures), RETRY_DELAYS_MS.length) - 1];
        console.log('[Resume] Retrying Blazor.reconnect() in ' + delay + 'ms (failure #' + failures + ').');
        retryTimer = setTimeout(() => { retryTimer = null; handleDisconnected('retry'); }, delay);
    };

    // Network-level failure: retry Blazor.reconnect() across transient WebSocket drops (e.g. reverse-proxy 1006 blips)
    // so active WebRTC voice calls and circuit state are preserved. Only reload if the server is confirmed reachable
    // AND Blazor.reconnect() has failed repeatedly (>= 3 times), or when session refresh / state desync requires it.
    const recoverFromFailure = async (reason, myGen, forceReloadIfReachable = false) => {
        if (myGen !== resumeGeneration) return;
        failures++;
        mark('Reachability probe (' + reason + ')');
        if ((forceReloadIfReachable || failures >= 3) && await isServerReachable()) {
            if (myGen !== resumeGeneration) return;
            return reloadNow(reason + ' (server reachable)');
        }
        if (myGen !== resumeGeneration) return;
        scheduleRetry();
    };

    const handleDisconnected = async (triggerSource) => {
        if (window._reloadInProgress || window._isServerRestarting) return;
        if (!window._blazorDisconnected || !canReconnectNow()) return; // visibilitychange / onHostViewActivated will call again
        if (running) {
            if (hiddenSinceAttemptStart && isVisible()) {
                console.log('[Resume] Preempting stale backgrounded reconnect attempt on foreground resume.');
                mark('Preempt stale background attempt');
                resumeGeneration++;
                if (typeof window.__abortPendingBlazorSocket === 'function') {
                    window.__abortPendingBlazorSocket('Preempt stale background attempt');
                }
                if (reconnectPromise) {
                    const stalePromise = reconnectPromise;
                    await Promise.race([stalePromise.catch(() => {}), sleep(RECONNECT_DRAIN_MS)]);
                    if (reconnectPromise === stalePromise) {
                        reconnectPromise = null;
                    }
                }
                running = false;
            } else {
                return;
            }
        }
        if (retryTimer) {
            clearTimeout(retryTimer);
            retryTimer = null;
        }
        if (!navigator.onLine) {
            console.log('[Resume] Offline. Waiting for network.');
            mark('Waiting for network');
            armSplashDebounce();
            window.addEventListener('online', () => handleDisconnected('online'), { once: true });
            return;
        }
        if (window._reconnectTimings && triggerSource && !window._reconnectTimings.trigger) {
            window._reconnectTimings.trigger = triggerSource;
        }
        armSplashDebounce();

        const myGen = ++resumeGeneration;
        hiddenSinceAttemptStart = false;
        running = true;
        try {
            // On subsequent retry ticks (failures > 0), verify reachability via /spokesapi/Manifest first
            // so we never spam Blazor.reconnect() WebSocket upgrades against a server that is still offline/restarting.
            if (failures > 0) {
                mark('Reachability probe (retry #' + failures + ')');
                const reachable = await isServerReachable();
                if (myGen !== resumeGeneration) return;
                if (!reachable) {
                    if (!canReconnectNow()) {
                        mark('Deferred recovery (page hidden)');
                        return;
                    }
                    failures++;
                    scheduleRetry();
                    return;
                }
            }

            if (msUntilSessionExpiry() < EXPIRY_MARGIN_MS) {
                // Web has no refresh-token API, but a page GET renews the cookie from Spokes_Refresh.
                if (!isNative()) return await reloadNow('session cookie expired');
                mark('Session refresh (cookie near expiry)');
                const refreshed = await refreshSession();
                if (myGen !== resumeGeneration) return;
                if (!refreshed) return await recoverFromFailure('session refresh failed', myGen, true);
            }

            reconnectedViaHandler = false;
            let outcome = await attemptReconnect();
            if (myGen !== resumeGeneration) return;

            // If the app went into the background while attemptReconnect() was in flight, the failure/timeout
            // was caused by OS socket/timer suspension. Give it one clean foreground attempt before reloading.
            if (outcome !== 'ok' && outcome !== 'rejected' && hiddenSinceAttemptStart && isVisible()) {
                console.log('[Resume] Background-interrupted reconnect (' + outcome + '). Retrying immediately in foreground.');
                mark('Retry in foreground after background interrupt');
                hiddenSinceAttemptStart = false;
                if (typeof window.__abortPendingBlazorSocket === 'function') {
                    window.__abortPendingBlazorSocket('Background-interrupted reconnect');
                }
                if (reconnectPromise) {
                    const stalePromise = reconnectPromise;
                    await Promise.race([stalePromise.catch(() => {}), sleep(RECONNECT_DRAIN_MS)]);
                    if (reconnectPromise === stalePromise) {
                        reconnectPromise = null;
                    }
                }
                reconnectedViaHandler = false;
                outcome = await attemptReconnect();
                if (myGen !== resumeGeneration) return;
            }

            if (outcome === 'ok') {
                if (!reconnectedViaHandler) {
                    const activeWs = window.__spokesBlazorSocket;
                    if (activeWs && typeof activeWs.readyState === 'number' && activeWs.readyState >= 2) {
                        console.warn('[Resume] Blazor.reconnect() resolved true while WebSocket is closed (readyState=' + activeWs.readyState + '). Treating circuit as desynced.');
                        if (!canReconnectNow()) {
                            mark('Deferred recovery (page hidden)');
                            return;
                        }
                        await recoverFromFailure('circuit socket desynced', myGen, true);
                        return;
                    }
                    completeCircuitConnected({ emitEvent: true });
                } else {
                    failures = 0;
                    window._blazorDisconnected = false;
                    window._lastHiddenDurationMs = 0;
                }
                console.log('[Resume] Blazor.reconnect() succeeded.');
                return;
            }
            if (outcome === 'rejected') return await reloadNow('circuit no longer on server');
            if (!canReconnectNow()) {
                mark('Deferred recovery (page hidden)');
                return;
            }
            await recoverFromFailure('reconnect ' + outcome, myGen);
        } finally {
            if (myGen === resumeGeneration) {
                running = false;
            }
        }
    };

    const onConnectionDown = (reason) => {
        window._blazorDisconnected = true;
        window._blazorDisconnectedAt = Date.now();
        failures = 0;
        try { window.dispatchEvent(new CustomEvent('spokes:blazor-disconnected')); } catch (e) {}
        if (!window._reconnectTimings) {
            window._reconnectTimings = {
                start: Date.now(),
                trigger: reason || (isVisible() ? 'onConnectionDown' : 'onConnectionDown-hidden'),
                hiddenDurationMs: window._lastHiddenDurationMs || 0,
                splashShown: false,
                timeline: [{ name: 'Connection Dropped (' + (reason || 'SignalR') + ')', absolute: Date.now() }]
            };
        }
        armSplashDebounce();
        handleDisconnected(reason || 'onConnectionDown');
    };

    const onConnectionUp = () => {
        reconnectedViaHandler = true;
        completeCircuitConnected({ emitEvent: true });
    };

    // Detects half-open "zombie" sockets when reopening the app shortly after closing it
    // or after a foreground network interface transition (e.g. LTE+ -> Wi-Fi), before SignalR's
    // 120s server timeout notices the mobile OS blackholed the old socket.
    const forceCloseActiveBlazorSocket = (reason) => {
        const activeWs = window.__spokesBlazorSocket;
        if (!activeWs) return;
        window.__spokesBlazorSocket = null;
        const oncloseHandler = activeWs.onclose;
        try {
            activeWs.onclose = null;
            activeWs.close();
        } catch (e) {}
        if (typeof oncloseHandler === 'function') {
            try {
                oncloseHandler({
                    code: 1006,
                    reason: reason || 'Zombie socket closed',
                    wasClean: false
                });
            } catch (e) {}
        }
    };

    const handleDetectedDeadSocket = (reason) => {
        forceCloseActiveBlazorSocket(reason || 'detected-dead-socket');
        if (!window._blazorDisconnected) {
            onConnectionDown(reason || 'detected-dead-socket');
        }
    };

    const verifyCircuitAlive = (interopPromise, forceProbe = false, probeReason = 'background-resume') => {
        if (!interopPromise || typeof interopPromise.then !== 'function') return;
        if (window._blazorDisconnected || zombieProbeInFlight || !isVisible()) return;
        const hiddenDuration = window._lastHiddenDurationMs || 0;
        if (!forceProbe && hiddenDuration < MIN_HIDDEN_FOR_ZOMBIE_PROBE_MS) return;

        zombieProbeInFlight = true;
        const probeGen = resumeGeneration;
        Promise.race([
            interopPromise.then(() => 'ok', () => 'error'),
            sleep(ZOMBIE_PROBE_TIMEOUT_MS).then(() => 'timeout')
        ]).then(result => {
            zombieProbeInFlight = false;
            if (result === 'ok') {
                window.__lastBlazorServerMsgAt = Date.now();
                window._lastHiddenDurationMs = 0;
                return;
            }
            if (window._blazorDisconnected || !isVisible() || probeGen !== resumeGeneration) return;
            console.warn('[Resume] Foreground interop probe ' + result + ' (' + probeReason + ') — zombie socket detected.');
            handleDetectedDeadSocket('zombie-socket-' + probeReason + '-' + result);
        });
    };

    let lastForegroundProbeAt = 0;
    const probeCircuitLiveness = (reason) => {
        if (!window._blazorCircuitEverOpened || window._blazorDisconnected || zombieProbeInFlight || !isVisible()) return;
        const dotNetRef = window.spokesDotNetRef;
        if (!dotNetRef || typeof dotNetRef.invokeMethodAsync !== 'function') return;
        const now = Date.now();
        if (now - lastForegroundProbeAt < 2500) return;
        lastForegroundProbeAt = now;
        try {
            verifyCircuitAlive(dotNetRef.invokeMethodAsync('PingCircuit'), true, reason || 'foreground-probe');
        } catch (e) {}
    };

    window.addEventListener('online', () => {
        if (!window._blazorDisconnected) {
            probeCircuitLiveness('online');
        }
    });
    if (navigator.connection && typeof navigator.connection.addEventListener === 'function') {
        navigator.connection.addEventListener('change', () => {
            if (!window._blazorDisconnected) {
                probeCircuitLiveness('network-change');
            }
        });
    }
    document.addEventListener('pointerdown', () => {
        if (window._blazorCircuitEverOpened && !window._blazorDisconnected && window.__lastBlazorServerMsgAt > 0) {
            // SignalR server keep-alive interval is 15s; require >35s (2 missed keep-alives + mobile jitter margin)
            // before probing on user interaction so normal cellular/UI stalls never kill a healthy circuit.
            const silentMs = Date.now() - window.__lastBlazorServerMsgAt;
            if (silentMs > KEEPALIVE_SILENCE_PROBE_MS) {
                console.debug(`[Resume] Probing circuit after ${silentMs}ms keepalive silence (inVoiceCall=${Boolean(window._spokesInVoiceCall)}).`);
                probeCircuitLiveness('keepalive-silence');
            }
        }
    }, { passive: true });

    const onVisible = () => {
        if (isNative()) {
            if (typeof window.reapplyCapacitorStatusBarStyle === 'function') {
                window.reapplyCapacitorStatusBarStyle();
            }
            // Keep the cookie comfortably fresh so the blocking refresh above is rarely needed.
            if (msUntilSessionExpiry() < PROACTIVE_REFRESH_WINDOW_MS && Date.now() - lastProactiveRefreshAt > 60000) {
                lastProactiveRefreshAt = Date.now();
                console.log('[Resume] Session cookie within proactive refresh window. Background refresh.');
                refreshSession().catch(() => {});
            }
        }
        if (window._blazorDisconnected) {
            if (window._reconnectTimings) {
                window._reconnectTimings.hiddenDurationMs = window._lastHiddenDurationMs || 0;
                mark('App Foregrounded (visible)');
            }
            armSplashDebounce();
            handleDisconnected('visibilitychange');
            return;
        }
        if (window._blazorCircuitEverOpened && (window._lastHiddenDurationMs || 0) >= MIN_HIDDEN_FOR_ZOMBIE_PROBE_MS) {
            setTimeout(() => {
                if (!window._blazorDisconnected && !zombieProbeInFlight && (window._lastHiddenDurationMs || 0) >= MIN_HIDDEN_FOR_ZOMBIE_PROBE_MS && isVisible()) {
                    probeCircuitLiveness('background-resume');
                }
            }, 250);
        }
    };

    document.addEventListener('visibilitychange', () => {
        if (document.visibilityState === 'hidden') {
            window._spokesHostViewExplicitlyActivated = false;
            if (!window._hiddenAt) {
                window._hiddenAt = Date.now();
            }
            hiddenSinceAttemptStart = true;
            // Pause the splash debounce timer while hidden so a fast foreground reconnect on reopen
            // doesn't immediately show a stale spinner.
            splashController.cancelDebounced();
            try { localStorage.setItem('spokes_last_hidden_at', String(window._hiddenAt)); } catch (e) {}
            return;
        }
        try { localStorage.removeItem('spokes_last_hidden_at'); } catch (e) {}
        if (window._hiddenAt) {
            window._lastHiddenDurationMs = Math.max(0, Date.now() - window._hiddenAt);
            console.log('[Resume] Visible after ' + Math.round(window._lastHiddenDurationMs / 1000) + 's in background.');
            window._hiddenAt = 0;
        }
        onVisible();
    });

    const onHostViewDeactivated = () => {
        if (!window._hiddenAt) {
            window._hiddenAt = Date.now();
        }
        hiddenSinceAttemptStart = true;
        splashController.cancelDebounced();
    };

    const onHostViewActivated = () => {
        if (window._hiddenAt) {
            window._lastHiddenDurationMs = Math.max(0, Date.now() - window._hiddenAt);
            window._hiddenAt = 0;
        }
        if (isVisible()) {
            onVisible();
        }
    };

    return Object.assign(() => onHostViewActivated(), { onConnectionDown, onConnectionUp, verifyCircuitAlive, probeCircuitLiveness, handleDetectedDeadSocket, onHostViewActivated, onHostViewDeactivated });
})();

window.startupDiagnostics = {
    reportInteractive: function(userId, avatarLength, serverTimings) {
        const now = Date.now();
        window.startupTimings.interactive = now;
        splashController.disarmWatchdog();
        if (window.SpokesNative && typeof window.SpokesNative.notifyInteractive === 'function') { try { window.SpokesNative.notifyInteractive(); } catch (e) {} }
        
        const correlationId = window.__startupCorrelationId;
        const cTimesStr = window.__clientLocalTimings;
        
        const timeline = [];
        let localStart = null;

        // Include native OS process/Activity cold-boot start timestamp on the first launch in this session
        try {
            if (!sessionStorage.getItem('spokes_native_boot_reported')) {
                sessionStorage.setItem('spokes_native_boot_reported', '1');
                let nativeStartMs = window.__spokesNativeStartMs || 0;
                if (!nativeStartMs && window.SpokesNative && typeof window.SpokesNative.getNativeStartTime === 'function') {
                    nativeStartMs = Number(window.SpokesNative.getNativeStartTime()) || 0;
                }
                if (nativeStartMs > 0 && now - nativeStartMs >= 0 && now - nativeStartMs < 120000) {
                    localStart = nativeStartMs;
                    timeline.push({ name: 'Native: OS App Launch', absolute: nativeStartMs });
                }
            }
        } catch (e) {}
        
        if (cTimesStr) {
            cTimesStr.split(',').forEach(part => {
                const lastIdx = part.lastIndexOf('_');
                if (lastIdx !== -1) {
                    const name = part.substring(0, lastIdx);
                    const timeStr = part.substring(lastIdx + 1);
                    const time = parseInt(timeStr);
                    if (name === 'head' && (!localStart || time < localStart)) localStart = time;
                    timeline.push({ name: 'Client: ' + name, absolute: time });
                }
            });
        }
        
        // Client-side milestones
        if (window.startupTimings.bootstrap_start) timeline.push({ name: 'Client: Cookie Bootstrap Page Start', absolute: window.startupTimings.bootstrap_start });
        if (window.startupTimings.nav_start) timeline.push({ name: 'Client: Page Navigation Start', absolute: window.startupTimings.nav_start });
        if (window.startupTimings.nav_request) timeline.push({ name: 'Client: HTML Request Sent', absolute: window.startupTimings.nav_request });
        if (window.startupTimings.nav_first_byte) timeline.push({ name: 'Client: HTML First Byte', absolute: window.startupTimings.nav_first_byte });
        if (window.startupTimings.remote_html_received) timeline.push({ name: 'Client: HTML Received', absolute: window.startupTimings.remote_html_received });
        if (window.startupTimings.boot_script_ready) timeline.push({ name: 'Client: Boot Script Ready', absolute: window.startupTimings.boot_script_ready });
        if (window.startupTimings.hidden_wait_start) timeline.push({ name: 'Client: Hidden, Waiting To Be Visible', absolute: window.startupTimings.hidden_wait_start });
        if (window.startupTimings.hidden_wait_end) timeline.push({ name: 'Client: Visible, Boot Resumed', absolute: window.startupTimings.hidden_wait_end });
        if (window.startupTimings.cookie_sync_start) timeline.push({ name: 'Client: Cookie Sync Start', absolute: window.startupTimings.cookie_sync_start });
        if (window.startupTimings.cookie_sync_end) timeline.push({ name: 'Client: Cookie Sync Done', absolute: window.startupTimings.cookie_sync_end });
        if (window.startupTimings.vault_sync_end) timeline.push({ name: 'Client: Vault Sync Done', absolute: window.startupTimings.vault_sync_end });
        if (window.startupTimings.blazor_start) timeline.push({ name: 'Client: Blazor.start()', absolute: window.startupTimings.blazor_start });
        if (window.startupTimings.negotiate_start) timeline.push({ name: 'Client: Negotiate Start', absolute: window.startupTimings.negotiate_start });
        if (window.startupTimings.negotiate_end) timeline.push({ name: 'Client: Negotiate Done', absolute: window.startupTimings.negotiate_end });
        if (window.startupTimings.ws_open_start) timeline.push({ name: 'Client: WebSocket Open Start', absolute: window.startupTimings.ws_open_start });
        if (window.startupTimings.ws_open_end) timeline.push({ name: 'Client: WebSocket Connected', absolute: window.startupTimings.ws_open_end });
        if (window.startupTimings.ws_msg_1) timeline.push({ name: 'Client: WS Msg #1 (Handshake)', absolute: window.startupTimings.ws_msg_1 });
        if (window.startupTimings.ws_render_batch && window.startupTimings.ws_render_batch_size > 100) {
            timeline.push({ name: `Client: WS Batch (${window.startupTimings.ws_render_batch_size}B)`, absolute: window.startupTimings.ws_render_batch });
        }
        if (window.startupTimings.first_dom_mutation) timeline.push({ name: 'Client: First DOM Mutation', absolute: window.startupTimings.first_dom_mutation });
        if (window.startupTimings.blazor_conn_up) timeline.push({ name: 'Client: Circuit Established', absolute: window.startupTimings.blazor_conn_up });
        
        let clockOffset = 0;
        if (serverTimings && serverTimings.length > 0) {
            const firstServerTiming = serverTimings[0].timestamp || serverTimings[0].Timestamp;
            const lastServerTiming = serverTimings[serverTimings.length - 1].timestamp || serverTimings[serverTimings.length - 1].Timestamp;
            // MainLayout.OnAfterRenderAsync (lastServerTiming) runs at the symmetric midpoint of the
            // OnRenderCompleted (ws_render_batch) -> OnAfterRenderAsync -> SpokesBridge.initLayout (now) round-trip.
            const batchAnchor = (window.startupTimings.ws_render_batch && window.startupTimings.ws_render_batch <= now)
                ? window.startupTimings.ws_render_batch
                : now;
            clockOffset = Math.round(((batchAnchor + now) / 2) - lastServerTiming);
            if (window.startupTimings.ws_msg_1 && (firstServerTiming + clockOffset) < window.startupTimings.ws_msg_1) {
                clockOffset = window.startupTimings.ws_msg_1 - firstServerTiming;
            }
            if ((lastServerTiming + clockOffset) > now) {
                clockOffset = now - lastServerTiming;
            }
            
            serverTimings.forEach(st => {
                const name = st.name || st.Name;
                const ts = st.timestamp || st.Timestamp;
                timeline.push({ name: name, absolute: ts + clockOffset });
            });
        }

        timeline.push({ name: 'Client: App Interactive', absolute: now });
        
        timeline.sort((a, b) => a.absolute - b.absolute);
        
        if (!localStart && timeline.length > 0) {
            localStart = timeline[0].absolute;
        }
        
        if (window.startupDiagnostics && typeof window.startupDiagnostics._snapshotResourcesAndMainThread === 'function') {
            window.startupDiagnostics._snapshotResourcesAndMainThread(now, false);
        }

        const perfSnap = window.__spokesPerfTelemetry;
        const navSnap = perfSnap && perfSnap.navigation;
        const resSnap = perfSnap && perfSnap.resources;
        const mtSnap = perfSnap && perfSnap.mainThread;
        const wsExt = (window.__spokesBlazorSocket && window.__spokesBlazorSocket.extensions) || window.__spokesWsExtensions || '';
        const fmtKb = (b) => b >= 1024 ? `${(b / 1024).toFixed(1)}KB` : `${b || 0}B`;

        console.log('==================================================');
        console.log('[STARTUP TIMELINE REPORT] ID: ' + correlationId);
        console.log('User ID: ' + userId + ' | Avatar Size: ' + avatarLength + ' bytes');
        if (window.startupTimings.boot_reason) console.log('Boot reason: ' + window.startupTimings.boot_reason);
        if (navSnap) {
            console.log(`HTML: ${fmtKb(navSnap.transferBytes)} xfer / ${fmtKb(navSnap.encodedBytes)} enc / ${fmtKb(navSnap.decodedBytes)} raw (${navSnap.protocol || 'unknown'}) | WS ext: ${wsExt || 'none'}`);
        } else {
            console.log(`WS ext: ${wsExt || 'none'}`);
        }
        if (resSnap && resSnap.byCategory) {
            const bc = resSnap.byCategory;
            const blocking = (resSnap.blockingHeadResources || [])
                .slice()
                .sort((a, b) => (b.durationMs || 0) - (a.durationMs || 0))
                .slice(0, 3)
                .map(x => `${(x.url || '').split('/').pop().split('?')[0]}(${x.durationMs}ms,${fmtKb(x.transferBytes)})`);
            console.log(`Startup Assets: ${resSnap.totalRequests} reqs, ${fmtKb(resSnap.totalTransferBytes)} xfer (js ${fmtKb(bc.js.transferBytes)}, css ${fmtKb(bc.css.transferBytes)}, font ${fmtKb(bc.font.transferBytes)}, img ${fmtKb(bc.image.transferBytes)})${blocking.length ? ' | Blocking: ' + blocking.join(', ') : ''}`);
        }
        if (window.startupTimings.ws_render_batch_inspect) {
            console.log(`Startup WS Batch: ${window.startupTimings.ws_render_batch_inspect.summary}`);
        }
        if (mtSnap) {
            console.log(`Startup MainThread: ${mtSnap.longTaskCount} long tasks, TBT ${mtSnap.totalBlockingTimeMs}ms, max ${mtSnap.maxLongTaskMs}ms${mtSnap.topAttribution ? ' | top: ' + mtSnap.topAttribution : ''}`);
        }
        console.log('--------------------------------------------------');
        
        timeline.forEach((step, idx) => {
            const elapsedSinceStart = step.absolute - localStart;
            const delta = idx === 0 ? 0 : step.absolute - timeline[idx - 1].absolute;
            console.log(`${step.name.padEnd(40)}: ${elapsedSinceStart.toString().padStart(6)}ms (delta: +${delta}ms)`);
        });
        console.log('==================================================');

        splashController.hide();

        // Clear the startup metadata so subsequent reloads don't reuse stale timings
        try { sessionStorage.removeItem('spokes_startup_meta'); } catch (e) {}
        document.cookie = "Spokes_Startup_Id=; Max-Age=0; path=/";
        document.cookie = "Spokes_Client_Times=; Max-Age=0; path=/";

        // Schedule post-interactive preloads (upload/recorder helpers + idle vendor bundles)
        // after initial layout/route settlement so they do not contend with Click-to-Usable hydration.
        if (typeof window.scheduleIdleDeferredScripts === 'function') {
            window.scheduleIdleDeferredScripts();
        }

        if (window._startupMutationObserver) {
            try { window._startupMutationObserver.disconnect(); } catch(e) {}
            window._startupMutationObserver = null;
        }
        if (window.startupDiagnostics && typeof window.startupDiagnostics._onReportInteractive === 'function') {
            window.startupDiagnostics._onReportInteractive(now, localStart, serverTimings, timeline);
        }
    }
};

Object.assign(window.startupDiagnostics, {
    _onReportInteractive: function(now, localStart, serverTimings, timeline) {
        try {
            const perf = window.__spokesPerfTelemetry;
            if (!perf || !perf.boot) return;
            let clickOrigin = localStart || window.startupTimings.nav_start || now;
            try {
                const harnessT0 = parseInt(sessionStorage.getItem('spokes_perf_click_t0') || '0', 10);
                if (harnessT0 > 0 && now - harnessT0 >= 0 && now - harnessT0 < 120000 && harnessT0 < clickOrigin) {
                    clickOrigin = harnessT0;
                }
            } catch (e) {}
            if (window.startupTimings.bootstrap_start && window.startupTimings.bootstrap_start < clickOrigin) {
                clickOrigin = window.startupTimings.bootstrap_start;
            }

            perf.boot.clickOriginMs = clickOrigin;
            perf.boot.shellInteractiveAt = now;
            perf.boot.usedPrewarmedWs = Boolean(window.startupTimings.used_prewarmed_ws);
            perf.boot.forcedNegotiateFallback = sessionStorage.getItem('spokes_force_negotiate') === '1';
            perf.boot.wsExtensions = (window.__spokesBlazorSocket && window.__spokesBlazorSocket.extensions) || window.__spokesWsExtensions || '';
            perf.boot.startupBatch = window.startupTimings.ws_render_batch_inspect || null;
            perf.boot.cookieBootstrapDetourMs = window.startupTimings.bootstrap_start && window.startupTimings.nav_start
                ? Math.max(0, window.startupTimings.nav_start - window.startupTimings.bootstrap_start)
                : 0;
            perf.boot.serverTimings = Array.isArray(serverTimings) ? serverTimings : [];
            perf.boot.timeline = timeline.map(s => ({
                name: s.name,
                absolute: s.absolute,
                elapsedMs: s.absolute - clickOrigin
            }));

            const onDoubleRaf = (cb) => {
                if (typeof requestAnimationFrame === 'function') {
                    requestAnimationFrame(() => requestAnimationFrame(cb));
                } else {
                    setTimeout(cb, 16);
                }
            };
            onDoubleRaf(() => {
                const paintedAt = Date.now();
                perf.boot.shellPaintedAt = paintedAt;
                perf.boot.shellTtiMs = Math.max(0, paintedAt - perf.boot.clickOriginMs);
                window.startupDiagnostics._tryFinalizeUsableTti();
            });
        } catch (e) {}
    },
    _snapshotResourcesAndMainThread: function(settledAtEpochMs, disconnectObserver = true) {
        const perf = window.__spokesPerfTelemetry;
        if (!perf) return;
        try {
            const origin = Math.round(performance.timeOrigin);
            const nav = performance.getEntriesByType('navigation')[0];
            if (nav) {
                const d = (a, b) => Math.max(0, Math.round(b - a));
                perf.navigation = {
                    type: nav.type,
                    protocol: nav.nextHopProtocol || '',
                    redirectCount: nav.redirectCount || 0,
                    dnsMs: d(nav.domainLookupStart, nav.domainLookupEnd),
                    tcpMs: d(nav.connectStart, nav.connectEnd),
                    tlsMs: nav.secureConnectionStart > 0 ? d(nav.secureConnectionStart, nav.connectEnd) : 0,
                    ttfbMs: d(nav.requestStart, nav.responseStart),
                    htmlDownloadMs: d(nav.responseStart, nav.responseEnd),
                    transferBytes: nav.transferSize || 0,
                    encodedBytes: nav.encodedBodySize || 0,
                    decodedBytes: nav.decodedBodySize || 0
                };
            }

            const buckets = {
                js: { count: 0, transferBytes: 0, decodedBytes: 0 },
                css: { count: 0, transferBytes: 0, decodedBytes: 0 },
                font: { count: 0, transferBytes: 0, decodedBytes: 0 },
                audio: { count: 0, transferBytes: 0, decodedBytes: 0 },
                image: { count: 0, transferBytes: 0, decodedBytes: 0 },
                fetch: { count: 0, transferBytes: 0, decodedBytes: 0 },
                other: { count: 0, transferBytes: 0, decodedBytes: 0 }
            };
            const blockingHeadResources = [];
            const uncompressedLargeAssets = [];
            let totalRequests = 0;
            let totalTransferBytes = nav ? (nav.transferSize || 0) : 0;
            let totalDecodedBytes = nav ? (nav.decodedBodySize || 0) : 0;

            const cutoffRelMs = Math.max(0, settledAtEpochMs - origin);
            const entries = performance.getEntriesByType('resource') || [];
            for (let i = 0; i < entries.length; i++) {
                const r = entries[i];
                if (r.startTime > cutoffRelMs + 50) continue;
                totalRequests++;
                const xfer = r.transferSize || r.encodedBodySize || 0;
                const dec = r.decodedBodySize || xfer || 0;
                totalTransferBytes += xfer;
                totalDecodedBytes += dec;

                const cleanUrl = (r.name || '').split('?')[0].toLowerCase();
                let cat = 'other';
                if (cleanUrl.endsWith('.js') || r.initiatorType === 'script') cat = 'js';
                else if (cleanUrl.endsWith('.css') || r.initiatorType === 'link') cat = 'css';
                else if (/\.(woff2?|ttf|otf|eot)$/.test(cleanUrl)) cat = 'font';
                else if (/\.(wav|mp3|ogg|m4a|aac|flac)$/.test(cleanUrl) || r.initiatorType === 'audio') cat = 'audio';
                else if (/\.(png|jpe?g|webp|gif|svg|ico|avif)$/.test(cleanUrl) || r.initiatorType === 'img') cat = 'image';
                else if (r.initiatorType === 'fetch' || r.initiatorType === 'xmlhttprequest') cat = 'fetch';

                buckets[cat].count++;
                buckets[cat].transferBytes += xfer;
                buckets[cat].decodedBytes += dec;

                if (r.renderBlockingStatus === 'blocking') {
                    blockingHeadResources.push({ url: r.name, category: cat, transferBytes: xfer, decodedBytes: dec, durationMs: Math.round(r.duration || 0) });
                }
                const enc = r.encodedBodySize || xfer;
                if (dec >= 30720 && (cat === 'audio' || (enc > 0 && enc >= dec * 0.95))) {
                    uncompressedLargeAssets.push({ url: r.name, category: cat, transferBytes: xfer, decodedBytes: dec });
                }
            }

            perf.resources = {
                totalRequests: totalRequests,
                totalTransferBytes: totalTransferBytes,
                totalDecodedBytes: totalDecodedBytes,
                byCategory: buckets,
                blockingHeadResources: blockingHeadResources,
                uncompressedLargeAssets: uncompressedLargeAssets
            };

            const mt = perf.mainThread;
            if (mt && Array.isArray(mt._entries)) {
                let count = 0;
                let tbt = 0;
                let maxDur = 0;
                let topAttr = null;
                for (let i = 0; i < mt._entries.length; i++) {
                    const lt = mt._entries[i];
                    if (lt.startTime > cutoffRelMs + 50) continue;
                    count++;
                    const dur = Math.round(lt.duration || 0);
                    if (dur > maxDur) {
                        maxDur = dur;
                        if (lt.attribution) topAttr = lt.attribution;
                    }
                    const blk = typeof lt.blockingDuration === 'number' ? Math.round(lt.blockingDuration) : Math.max(0, dur - 50);
                    tbt += Math.max(0, blk);
                }
                mt.longTaskCount = count;
                mt.totalBlockingTimeMs = tbt;
                mt.maxLongTaskMs = maxDur;
                mt.topAttribution = topAttr;
            }
            if (disconnectObserver && window._spokesStartupLongTaskObserver) {
                try { window._spokesStartupLongTaskObserver.disconnect(); } catch (e) {}
                window._spokesStartupLongTaskObserver = null;
            }
        } catch (e) {}
    },
    _tryFinalizeUsableTti: function() {
        const perf = window.__spokesPerfTelemetry;
        if (!perf || !perf.boot || perf.boot.settled) return;
        if (!perf.boot.shellInteractiveAt || !perf.boot.layoutSettled || !perf.boot.routeSettled) return;

        const finalizePaint = () => {
            if (perf.boot.settled) return;
            splashController.hide();
            const now = Date.now();
            const clickOrigin = perf.boot.clickOriginMs || window.startupTimings.nav_start || now;
            const navOrigin = window.startupTimings.nav_start || clickOrigin;
            perf.boot.settled = true;
            perf.boot.routeSettledAt = now;
            perf.boot.clickToUsableMs = Math.max(0, now - clickOrigin);
            perf.boot.navToUsableMs = Math.max(0, now - navOrigin);
            window.startupDiagnostics._snapshotResourcesAndMainThread(now);
            try { sessionStorage.removeItem('spokes_perf_click_t0'); } catch (e) {}
            console.log(`[Startup] Usable TTI settled (${perf.boot.settledRoute || 'app'}): clickToUsable=${perf.boot.clickToUsableMs}ms, shellTti=${perf.boot.shellTtiMs}ms`);
            if (window.spokesPerf && typeof window.spokesPerf.onStartupSettled === 'function') {
                window.spokesPerf.onStartupSettled(perf.boot);
            }
        };

        if (typeof requestAnimationFrame === 'function') {
            requestAnimationFrame(() => requestAnimationFrame(finalizePaint));
        } else {
            setTimeout(finalizePaint, 16);
        }
    },
    reportLayoutSettled: function() {
        const perf = window.__spokesPerfTelemetry;
        if (!perf || !perf.boot) return;
        perf.boot.layoutSettled = true;
        window.startupDiagnostics._tryFinalizeUsableTti();
    },
    reportRouteSettled: function(routeKey) {
        const perf = window.__spokesPerfTelemetry;
        if (!perf || !perf.boot) return;
        perf.boot.routeSettled = true;
        if (routeKey && !perf.boot.settledRoute) {
            perf.boot.settledRoute = String(routeKey);
        }
        window.startupDiagnostics._tryFinalizeUsableTti();
    },
    recordHostViewSwitch: function(meta) {
        const perf = window.__spokesPerfTelemetry;
        if (!perf) return;
        const activatedAt = Date.now();
        const startedAt = (meta && meta.startedAt) ? meta.startedAt : activatedAt;
        const entry = {
            origin: location.origin,
            route: (meta && meta.route) || (location.pathname + location.search),
            isWarm: Boolean(window._blazorCircuitEverOpened),
            disconnectedOnEntry: Boolean(window._blazorDisconnected),
            startedAt: startedAt,
            activatedAt: activatedAt,
            paintedAt: 0,
            totalSwitchMs: 0
        };
        perf.switches.push(entry);
        if (perf.switches.length > 20) perf.switches.shift();
        const done = () => {
            entry.paintedAt = Date.now();
            entry.totalSwitchMs = Math.max(0, entry.paintedAt - entry.startedAt);
        };
        if (typeof requestAnimationFrame === 'function') {
            requestAnimationFrame(() => requestAnimationFrame(done));
        } else {
            setTimeout(done, 16);
        }
    }
});

window.addEventListener('error', function(event) {
    if (event.message && (
        event.message.indexOf('ResizeObserver loop') !== -1 ||
        event.message.indexOf('ResizeObserver loop completed with undelivered notifications') !== -1 ||
        event.message.indexOf('ResizeObserver loop limit exceeded') !== -1
    )) {
        return;
    }
    console.error('[Global Error]', event.message, event.error);
});
window.addEventListener('unhandledrejection', function(event) {
    console.error('[Unhandled Rejection]', event.reason);
});

// Zero-overhead in-memory console ring buffer for Mobile debugging (never wraps console.* on Web/Desktop).
(function() {
    if (!window.Capacitor) {
        window.getAppConsoleLogs = () => [];
        window.flushAppLogs = () => Promise.resolve();
        return;
    }

    // Capacitor drops `console.trace` and Logcat often hides `console.debug`.
    // Map them to console.log so they appear in mobile debug logs.
    console.trace = function() { console.log.apply(console, arguments); };
    console.debug = function() { console.log.apply(console, arguments); };

    const MAX_LOGS = 1000;
    const ring = [];
    let flushedCount = 0;

    function getDebugLogsKey() {
        try {
            const parsed = new URL(window.location.href);
            if (parsed.protocol === 'http:' || parsed.protocol === 'https:') {
                return 'debug_logs::' + parsed.origin.toLowerCase();
            }
        } catch (e) {}
        return 'debug_logs::__local__';
    }

    function flushLogs() {
        if (flushedCount >= ring.length) return Promise.resolve();
        if (window.Capacitor && window.Capacitor.Plugins && window.Capacitor.Plugins.Preferences) {
            const logsToFlush = ring.slice(flushedCount);
            flushedCount = ring.length;
            const debugLogsKey = getDebugLogsKey();
            return window.Capacitor.Plugins.Preferences.get({ key: debugLogsKey }).then(res => {
                let existing = [];
                try { if (res.value) existing = JSON.parse(res.value); } catch(e) {}
                existing = existing.concat(logsToFlush);
                if (existing.length > MAX_LOGS) existing = existing.slice(existing.length - MAX_LOGS);
                return window.Capacitor.Plugins.Preferences.set({ key: debugLogsKey, value: JSON.stringify(existing) });
            }).catch(() => {});
        }
        return Promise.resolve();
    }

    ['log', 'warn', 'error', 'info'].forEach(function(method) {
        const original = console[method];
        if (!original) return;
        console[method] = function() {
            original.apply(console, arguments);
            try {
                const msgArgs = Array.from(arguments).map(a => {
                    if (a instanceof Error) return a.stack || a.message;
                    if (typeof a === 'object' && a !== null) {
                        try { return JSON.stringify(a); } catch(e) { return String(a); }
                    }
                    return String(a);
                }).join(' ');
                if (/^\[.*?\] (Information|Warning|Error|Debug|Trace):/.test(msgArgs)) {
                    if (!window.__signalRLogLevel || window.__signalRLogLevel.toLowerCase() === 'none') {
                        return;
                    }
                }
                if (ring.length >= MAX_LOGS) {
                    ring.shift();
                    if (flushedCount > 0) flushedCount--;
                }
                ring.push(`[${new Date().toISOString()}] [${method.toUpperCase()}] ${msgArgs}`);
            } catch(e) {}
        };
    });

    window.getAppConsoleLogs = () => ring.slice();
    window.flushAppLogs = flushLogs;
    window.addEventListener('pagehide', () => { try { flushLogs(); } catch (e) {} });
})();

// Fallback for severe boot failures to show UI to user instead of white screen
function handleSevereError(msg) {
    console.error('[App.razor] SEVERE ERROR:', msg);
    const statusEl = document.getElementById('splash-status');
    if (statusEl) {
        statusEl.textContent = msg;
        statusEl.style.color = '#ef4444';
    }
    splashController.revealWatchdogActions('initial');
}

const fetchCurrentSessionStrict = async (timeoutMs = 4000) => {
    const controller = typeof AbortController === 'function' ? new AbortController() : null;
    const timer = controller ? setTimeout(() => { try { controller.abort(); } catch (e) {} }, timeoutMs) : null;
    try {
        return await fetch('/spokesapi/auth/current-session?strict=1', controller ? { cache: 'no-store', signal: controller.signal } : { cache: 'no-store' });
    } finally {
        if (timer) clearTimeout(timer);
    }
};

window.checkIsCapacitorNative = () => typeof window.isCapacitorNative === 'function' ? window.isCapacitorNative()

    : (navigator.userAgent.includes("Capacitor") || Boolean(window.Capacitor?.isNativePlatform?.()));

window.spokesTriggerServerRestart = function(targetUrl) {
    window._isServerRestarting = true;
    splashController.show({
        skipWatchdog: true,
        statusText: "Restarting server, please wait...",
        statusColor: "#999"
    });
    const status = document.getElementById('splash-status');

    let destination = targetUrl || window.location.href;
    try {
        const urlObj = new URL(destination, window.location.href);
        if (window.checkIsCapacitorNative()) {
            urlObj.searchParams.set('client', 'mobile');
        } else {
            urlObj.searchParams.delete('client');
        }
        destination = urlObj.toString();
    } catch (e) {}

    const initialBootId = bootConfig.bootId || '';
    console.log('[Server Restart] Initiated restart. Destination after restart:', destination);

    setTimeout(() => {
        const pollInterval = setInterval(async () => {
            try {
                const res = await fetch('/spokesapi/Manifest', { cache: 'no-store' });
                if (res.ok) {
                    const contentType = (res.headers.get('content-type') || '').toLowerCase();
                    if (!contentType.includes('json')) {
                        return;
                    }
                    const data = await res.json().catch(() => null);
                    if (!data || typeof data.boot_id !== 'string') {
                        return;
                    }
                    if (initialBootId && data.boot_id === initialBootId) {
                        return;
                    }
                    clearInterval(pollInterval);
                    if (status) status.innerText = "Server is back online! Loading...";
                    console.log('[Server Restart] Server is back online. Redirecting to:', destination);
                    window.location.href = destination;
                }
            } catch (err) {
                // Server is still offline or starting up, continue polling
            }
        }, 1500);
    }, 2500);
};

window.checkIsAuthRecoverable = async () => {
    try {
        let r = await fetch('/spokesapi/auth/current-session', { cache: 'no-store' });
        if (r.ok) return true;
        if (typeof window.spokesAuth !== 'undefined' && window.spokesAuth.refreshSessionAsync) {
            const refreshed = await window.spokesAuth.refreshSessionAsync();
            if (refreshed) return true;
        }
        return false;
    } catch(e) {
        return false;
    }
};

const ensureCapacitorInitializedOnce = () => {
    if (window._capacitorEnvTriggered) return;
    window._capacitorEnvTriggered = true;
    if (window.initCapacitorEnv) {
        window.initCapacitorEnv();
    }
};

const hasRenderedInteractiveDom = () => {
    if (!document.body) return false;
    for (let i = 0; i < document.body.children.length; i++) {
        const el = document.body.children[i];
        if (!el || el.id === 'startup-splash' || el.id === 'blazor-error-ui' || el.id === 'components-reconnect-modal' || el.hidden || el.style.display === 'none') continue;
        const tag = el.tagName;
        if (tag === 'SCRIPT' || tag === 'STYLE' || tag === 'LINK') continue;
        return true;
    }
    return false;
};

const hideStartupSplashAfterCircuitDomCommit = () => {
    if (window._isServerRestarting || window._reloadInProgress) return;
    const finishHide = () => {
        if (window._isServerRestarting || window._reloadInProgress || window._blazorDisconnected) return;
        splashController.hide();
        if (typeof window.ensureUploadScriptsLoaded === 'function') {
            window.ensureUploadScriptsLoaded();
        }
        if (window.__spokesSession && window.__spokesSession.authenticated && typeof window.scheduleIdleDeferredScripts === 'function') {
            window.scheduleIdleDeferredScripts();
        }
    };
    if (hasRenderedInteractiveDom()) {
        requestAnimationFrame(finishHide);
        return;
    }
    let settled = false;
    const settle = () => {
        if (settled) return;
        settled = true;
        if (obs) { try { obs.disconnect(); } catch (e) {} }
        requestAnimationFrame(finishHide);
    };
    let obs = null;
    try {
        obs = new MutationObserver(() => {
            if (hasRenderedInteractiveDom()) settle();
        });
        if (document.body) {
            obs.observe(document.body, { childList: true, subtree: true });
        }
    } catch (e) {}
    setTimeout(settle, 1500);
};

// Handles pre-open direct WebSocket failures (which .NET 9 blazor.web.js silently swallows instead of
// throwing FailedToNegotiateWithServerError): recovers 401/404 expired cookies first, and falls back
// via a single splash-covered reload to standard negotiated SignalR (SSE / LongPolling) if a proxy
// or VPN blocks direct WebSocket upgrades.
const handleInitialDirectWsFailure = async () => {
    if (window._blazorCircuitEverOpened || window._directWsFallbackHandled || window._isServerRestarting || window._reloadInProgress) {
        return;
    }
    window._directWsFallbackHandled = true;

    try {
        const r = await fetchCurrentSessionStrict();
        if (r.status === 401 || r.status === 404) {
            console.warn('[App.razor] Direct WebSocket failed with ' + r.status + ' session. Refreshing cookie and reloading...');
            const lastAuthReload = parseInt(sessionStorage.getItem('spokes_ws_auth_reload_at') || '0', 10);
            if (Date.now() - lastAuthReload >= 15000 && window.spokesAuth && window.spokesAuth.refreshSessionAsync) {
                const refreshed = await window.spokesAuth.refreshSessionAsync();
                if (refreshed) {
                    sessionStorage.setItem('spokes_ws_auth_reload_at', String(Date.now()));
                    window._reloadInProgress = true;
                    window.location.reload();
                    return;
                }
            }
            // On web (no refresh token API) or unrecoverable token, fall back once to negotiated mode so standard error/redirect paths run
            if (sessionStorage.getItem('spokes_force_negotiate') !== '1') {
                sessionStorage.setItem('spokes_force_negotiate', '1');
                window._reloadInProgress = true;
                window.location.reload();
                return;
            }
            ensureCapacitorInitializedOnce();
            handleSevereError('Session expired. Please sign in again.');
            return;
        }
    } catch (fetchErr) {
        // Server unreachable / device offline — do not poison spokes_force_negotiate
        ensureCapacitorInitializedOnce();
        handleSevereError('Unable to reach server. Please check your connection.');
        return;
    }

    if (sessionStorage.getItem('spokes_force_negotiate') !== '1') {
        console.warn('[App.razor] Direct WebSocket upgrade rejected while session is valid. Falling back to negotiated SignalR...');
        sessionStorage.setItem('spokes_force_negotiate', '1');
        window._reloadInProgress = true;
        window.location.reload();
        return;
    }

    ensureCapacitorInitializedOnce();
    handleSevereError('Circuit not initialized (WebSocket connection blocked).');
};

window.__abortPendingBlazorSocket = function (reason) {
    const negotiateController = window.__pendingNegotiateController;
    if (negotiateController) {
        window.__pendingNegotiateController = null;
        try { negotiateController.abort(); } catch (e) {}
    }
    const ws = window.__pendingBlazorWs;
    if (!ws) return;
    window.__pendingBlazorWs = null;
    const oncloseHandler = ws.onclose;
    try {
        ws.onopen = null;
        ws.onmessage = null;
        ws.onerror = null;
        ws.onclose = null;
        ws.close();
    } catch (e) {}
    if (typeof oncloseHandler === 'function') {
        try {
            oncloseHandler({
                code: 1006,
                reason: reason || 'Reconnect hang guard timeout',
                wasClean: false
            });
        } catch (e) {}
    }
};

window.SpokesBlazorWebSocket = function (url, protocols) {
    const trackPendingSocket = (ws) => {
        window.__pendingBlazorWs = ws;
        ws.addEventListener('close', () => {
            if (window.__pendingBlazorWs === ws) window.__pendingBlazorWs = null;
        }, { once: true });
    };

    const attachHandshakeGuard = (ws, alreadyOpened, preCloseEvent) => {
        if (window._blazorCircuitEverOpened || alreadyOpened) return;
        if (preCloseEvent || ws.readyState >= 2) {
            handleInitialDirectWsFailure();
            return;
        }
        let opened = false;
        ws.addEventListener('open', () => { opened = true; }, { once: true });
        ws.addEventListener('close', () => {
            if (!opened && !window._blazorCircuitEverOpened) {
                handleInitialDirectWsFailure();
            }
        }, { once: true });
    };

    const prewarm = window.__spokesPrewarmedWs;
    if (prewarm && !prewarm.claimed && !protocols && typeof url === 'string' && url.includes('/_blazor')) {
        prewarm.claimed = true;
        window.startupTimings.used_prewarmed_ws = true;
        clearTimeout(prewarm.expireTimer);
        window.__spokesPrewarmedWs = null;
        const ws = prewarm.ws;
        trackPendingSocket(ws);
        attachHandshakeGuard(ws, prewarm.opened, prewarm.closeEvent);

        if (prewarm.opened && ws.readyState === 1) {
            const buffered = prewarm.messages.splice(0, prewarm.messages.length);
            const openEv = prewarm.openEvent || new Event('open');
            Promise.resolve().then(() => {
                if (typeof ws.onopen === 'function') ws.onopen(openEv);
                for (let i = 0; i < buffered.length; i++) {
                    if (typeof ws.onmessage === 'function') ws.onmessage(buffered[i]);
                }
            });
        } else if (prewarm.closeEvent || ws.readyState >= 2) {
            const errEv = prewarm.errorEvent || new Event('error');
            const closeEv = prewarm.closeEvent || new Event('close');
            Promise.resolve().then(() => {
                if (typeof ws.onerror === 'function') ws.onerror(errEv);
                if (typeof ws.onclose === 'function') ws.onclose(closeEv);
            });
        }
        return ws;
    }

    const ws = protocols ? new window.WebSocket(url, protocols) : new window.WebSocket(url);
    trackPendingSocket(ws);
    attachHandshakeGuard(ws, false, null);
    return ws;
};
window.SpokesBlazorWebSocket.CONNECTING = 0;
window.SpokesBlazorWebSocket.OPEN = 1;
window.SpokesBlazorWebSocket.CLOSING = 2;
window.SpokesBlazorWebSocket.CLOSED = 3;

const startBlazor = () => {
    window.startupTimings.blazor_start = Date.now();
    try {
        if (typeof Blazor === 'undefined') {
            throw new Error("Blazor is not defined. The framework script failed to load.");
        }

        const useDirectWebSocket = Boolean(window.WebSocket) && sessionStorage.getItem('spokes_force_negotiate') !== '1';

        Blazor.start({
        circuit: {
            configureSignalR: function (builder) {
                if (useDirectWebSocket) {
                    builder.withUrl('/_blazor', {
                        skipNegotiation: true,
                        transport: 1, // HttpTransportType.WebSockets
                        WebSocket: window.SpokesBlazorWebSocket
                    });
                }

                // Align client timeout with server (120s) so client does not abort during cellular latency spikes
                builder.withServerTimeout(120000);
                builder.withKeepAliveInterval(15000);

                if (window.__signalRLogLevel) {
                    const levelStr = window.__signalRLogLevel.toLowerCase();
                    let level = 0; // Default to Trace (0) if configured but invalid string
                    if (levelStr === 'trace') level = 0;
                    else if (levelStr === 'debug') level = 1;
                    else if (levelStr === 'information') level = 2;
                    else if (levelStr === 'warning') level = 3;
                    else if (levelStr === 'error') level = 4;
                    else if (levelStr === 'critical') level = 5;
                    else if (levelStr === 'none') level = 6;
                    
                    builder.configureLogging(level);
                } else {
                    // Suppress redundant internal SignalR console.info / console.error transport logs by default;
                    // reconnectionHandler and window.spokesResume log structured [Server Connection] and [Resume] events.
                    builder.configureLogging(5); // LogLevel.Critical
                }

                // .NET 9/10 blazor.web.js CircuitManager.startConnection() only rethrows transport/handshake
                // startup errors without calling $o() (#blazor-error-ui) when err.errorType === 'FailedToNegotiateWithServerError'.
                // With skipNegotiation: true (or when a WebSocket drops with 1006 during the SignalR Hub protocol
                // handshake), SignalR throws a plain Error with errorType === undefined. Normalizing it here
                // satisfies blazor.web.js's contract across both initial boot and all Blazor.reconnect() attempts.
                const originalBuild = builder.build.bind(builder);
                builder.build = function () {
                    const connection = originalBuild();
                    const originalStart = connection.start.bind(connection);
                    connection.start = async function () {
                        try {
                            return await originalStart();
                        } catch (err) {
                            if (err && typeof err === 'object' && !err.errorType) {
                                try { err.errorType = 'FailedToNegotiateWithServerError'; } catch (e) {}
                            }
                            if (!window._blazorCircuitEverOpened) {
                                handleInitialDirectWsFailure();
                            }
                            throw err;
                        }
                    };
                    return connection;
                };
            },
            circuitHandlers: [{
                onCircuitOpened() {
                    window._blazorCircuitEverOpened = true;
                    window.__pendingBlazorWs = null;
                    splashController.disarmWatchdog();
                    window.startupTimings.blazor_conn_up = Date.now();
                    try { sessionStorage.removeItem('spokes_ws_auth_reload_at'); } catch (e) {}
                    console.log('[App.razor] Blazor circuit opened.');
                    hideStartupSplashAfterCircuitDomCommit();
                    ensureCapacitorInitializedOnce();
                }
            }],
            reconnectionHandler: {
                onConnectionDown(options, error) {
                    console.log('[Server Connection] Connection Down. Reason: ' + (error ? error.message : 'Unknown'));
                    if (window._isServerRestarting) {
                        console.log('[Server Connection] Connection Down during server restart. Suppressing standard reconnect flow.');
                        return;
                    }

                    window._reconnectTimings = {
                        start: Date.now(),
                        trigger: (document.visibilityState === 'visible' && window._spokesHostViewActive !== false) ? 'onConnectionDown' : 'onConnectionDown-hidden',
                        hiddenDurationMs: window._lastHiddenDurationMs || 0,
                        splashShown: false,
                        timeline: [{ name: 'Connection Dropped', absolute: Date.now() }]
                    };

                    // Aggressively cancel any hanging warm-share promises so they can retry instantly
                    if (window._cancelPendingShare) {
                        window._cancelPendingShare();
                        window._cancelPendingShare = null;
                    }

                    // Reconnects (and arms debounced splash spinner) now if visible; otherwise the next visibilitychange resumes it.
                    window.spokesResume.onConnectionDown();
                },
                onConnectionUp() {
                    console.log('[Server Connection] Connection Up - Blazor circuit restored, hiding reconnect overlay.');
                    if (window._isServerRestarting) {
                        console.log('[Server Connection] Connection Up during server restart. Suppressing overlay hide until redirect.');
                        return;
                    }
                    window.spokesResume.onConnectionUp();
                    if (window._reloadInProgress) return; // a late reconnect must not flash the old page mid-reload
                    window.startupTimings.blazor_conn_up = Date.now();
                    
                    if (window._reconnectTimings) {
                        window._reconnectTimings.timeline.push({ name: 'Connection Restored', absolute: Date.now() });
                        console.log('==================================================');
                        console.log('[RECONNECT TIMELINE REPORT]');
                        const trig = window._reconnectTimings.trigger || 'unknown';
                        const hidSec = ((window._reconnectTimings.hiddenDurationMs || 0) / 1000).toFixed(1);
                        const spShown = window._reconnectTimings.splashShown ? 'yes' : 'no';
                        console.log(`Trigger: ${trig} | Background: ${hidSec}s | Splash Shown: ${spShown}`);
                        console.log('--------------------------------------------------');
                        window._reconnectTimings.timeline.forEach((step, idx) => {
                            const elapsedSinceStart = step.absolute - window._reconnectTimings.start;
                            const delta = idx === 0 ? 0 : step.absolute - window._reconnectTimings.timeline[idx - 1].absolute;
                            console.log(`${step.name.padEnd(40)}: ${elapsedSinceStart.toString().padStart(6)}ms (delta: +${delta}ms)`);
                        });
                        console.log('==================================================');
                        if (window.__spokesPerfTelemetry && Array.isArray(window.__spokesPerfTelemetry.reconnects)) {
                            const endTs = Date.now();
                            window.__spokesPerfTelemetry.reconnects.push({
                                trigger: trig,
                                hiddenDurationMs: window._reconnectTimings.hiddenDurationMs || 0,
                                splashShown: Boolean(window._reconnectTimings.splashShown),
                                totalMs: Math.max(0, endTs - window._reconnectTimings.start),
                                timeline: window._reconnectTimings.timeline.slice()
                            });
                            if (window.__spokesPerfTelemetry.reconnects.length > 10) {
                                window.__spokesPerfTelemetry.reconnects.shift();
                            }
                        }
                        window._reconnectTimings = null;
                    }

                    // If a warm-share was aborted/pending due to socket drop, retry it instantly now
                    if (window.checkPendingShares) {
                        window.checkPendingShares(0);
                    }

                    // Re-apply native status bar style upon circuit restoration
                    if (typeof window.reapplyCapacitorStatusBarStyle === 'function') {
                        window.reapplyCapacitorStatusBarStyle();
                    }
                }
            }
        }
    }).then(() => {
        // Note: In .NET 9 blazor.web.js, Blazor.start() resolves after client runtime setup (~3ms),
        // BEFORE interactive server circuit negotiation/StartCircuit completes. Interactive routes
        // defer splash dismissal, Capacitor bridge init, and heavy script bundles to onCircuitOpened / reportInteractive.
        if (window.__isStaticLoginRoute) {
            splashController.hide();
            ensureCapacitorInitializedOnce();
        }
        try {
            Blazor.addEventListener('enhancedload', () => {
                if (window._blazorDisconnected || window._isServerRestarting || window._reloadInProgress) return;
                if (window._blazorCircuitEverOpened || window.__isStaticLoginRoute) {
                    splashController.hide();
                }
            });
        } catch (e) {}
    }).catch(async e => {
        console.error('[App.razor] Blazor.start() promise rejected:', e);
        const errMsg = e ? (e.message || e.toString()) : "unknown";
        
        // If Blazor failed (e.g. negotiated mode 401), the session may have been lost between the page request
        // and the SignalR handshake. Refresh once and reload.
        try {
            let r = await fetchCurrentSessionStrict();
            if (r.status === 404 || r.status === 401) {
                console.warn('[App.razor] Optimistic boot failed with 401/404. Refreshing cookie and reloading...');
                let refreshed = false;
                if (window.spokesAuth && window.spokesAuth.refreshSessionAsync) {
                    refreshed = await window.spokesAuth.refreshSessionAsync();
                }
                if (refreshed) {
                    window._reloadInProgress = true;
                    window.location.reload();
                    return;
                }
                console.warn('[App.razor] Session refresh failed. Cannot recover.');
            }
        } catch (fetchErr) {}
        
        ensureCapacitorInitializedOnce();
        handleSevereError("Circuit not initialized (Blazor failed to load: " + errMsg + ")");
    });
    } catch (e) {
        console.error('[App.razor] Blazor.start() threw synchronously:', e);
        ensureCapacitorInitializedOnce();
        handleSevereError("Circuit not initialized (Blazor failed to load: " + (e ? e.message || e : "unknown") + ")");
    }
};


const navigateToReturnUrl = () => {
    const rawReturnUrl = new URLSearchParams(window.location.search).get('returnUrl');
    const dest = window.spokesAuth.getSafeRedirectUrl(rawReturnUrl, '/');
    console.log('[App.razor] Session is valid but currently on a login page. Navigating to ' + dest);
    window.location.replace(dest);
};

// Native only: make sure the WebView has a valid session before Blazor starts.
// The server already validated this page request's cookie and rendered the result into
// window.__spokesSession, so the common path needs no network or native bridge calls.
// (The mobile splash interceptor guarantees authenticated Capacitor requests on app pages;
// only pages it skips, e.g. /mobile-login, can arrive here unauthenticated.)
const ensureCookiesFlushed = async () => {
    if (!checkIsNativePlatform()) return true; // Web browsers: cookies just work.

    const session = window.__spokesSession || {};
    const path = window.location.pathname.toLowerCase();
    if (path.startsWith('/access-pending') || path.startsWith('/banned')) {
        return true;
    }
    const onLoginPage = path.includes('/mobile-login') || path.includes('/session-expired') || path.startsWith('/authentication/login');

    if (session.authenticated) {
        if (onLoginPage) { navigateToReturnUrl(); return false; }
        if (session.expiresAt > 0 && session.expiresAt - Date.now() < 60 * 24 * 60 * 60 * 1000) {
            console.log('[App.razor] Session cookie within proactive refresh window. Background refresh.');
            window.spokesAuth.refreshSessionAsync().catch(() => {});
        }
        return true;
    }

    console.log('[App.razor] Page request was unauthenticated. Establishing session from the refresh token...');
    if (window.__spokesPrewarmedWs) {
        try {
            clearTimeout(window.__spokesPrewarmedWs.expireTimer);
            window.__spokesPrewarmedWs.claimed = true;
            window.__spokesPrewarmedWs.ws.close();
        } catch (e) {}
        window.__spokesPrewarmedWs = null;
    }
    window.startupTimings.cookie_sync_start = Date.now();
    const refreshed = await window.spokesAuth.refreshSessionAsync();
    window.startupTimings.cookie_sync_end = Date.now();
    if (!refreshed) {
        console.log('[App.razor] No valid refresh token. User will need to log in.');
        window.spokesMediaLoader?.reconcileSession?.({ purgeIfUnauthenticated: true });
        return true;
    }
    if (onLoginPage) { navigateToReturnUrl(); return false; }
    return true;
};

// --- Partitioned post-interactive & on-demand script loaders ---
// Feature bundles (~950 KB: LiveKit, Quill, Cropper, Upload, Cast) are partitioned into
// idempotent Promise-cached loaders. They preload during post-interactive idle time and can
// also be awaited on-demand by any component or JS interop entrypoint so deep links never race.
(function initDeferredScriptLoaders() {
    const scriptPromises = Object.create(null);
    const loadedCss = Object.create(null);

    const loadScriptOnce = (src) => {
        if (!scriptPromises[src]) {
            scriptPromises[src] = new Promise((resolve) => {
                const s = document.createElement('script');
                s.async = true;
                s.src = src;
                s.onload = () => resolve(true);
                s.onerror = (e) => {
                    console.warn('[Lazy-Load] Failed to load:', src, e);
                    delete scriptPromises[src];
                    resolve(false);
                };
                (document.body || document.head || document.documentElement).appendChild(s);
            });
        }
        return scriptPromises[src];
    };

    const loadCssOnce = (href) => {
        if (loadedCss[href]) return;
        loadedCss[href] = true;
        const l = document.createElement('link');
        l.rel = 'stylesheet';
        l.href = href;
        document.head.appendChild(l);
    };

    let uploadBundlePromise = null;
    window.ensureUploadScriptsLoaded = function () {
        if (!uploadBundlePromise) {
            uploadBundlePromise = Promise.all([
                loadScriptOnce(bootAssets.upload || 'js/upload.js'),
                loadScriptOnce(bootAssets.download || 'js/download.js'),
                loadScriptOnce(bootAssets.voiceRecorder || 'js/voice-recorder.js')
            ]);
        }
        return uploadBundlePromise;
    };

    window.spokesInitializeUploadInput = async function (inputId, dotNetHelper, maxAllowedSize, maxDim, quality) {
        await window.ensureUploadScriptsLoaded();
        if (window.spokesUpload && typeof window.spokesUpload.initializeInput === 'function') {
            window.spokesUpload.initializeInput(inputId, dotNetHelper, maxAllowedSize, maxDim, quality);
        }
    };

    let voiceBundlePromise = null;
    window.ensureVoiceScriptsLoaded = function () {
        if (window.LivekitClient) return Promise.resolve(true);
        if (!voiceBundlePromise) {
            voiceBundlePromise = loadScriptOnce(bootAssets.livekit || 'lib/livekit/livekit-client.umd.min.js');
        }
        return voiceBundlePromise;
    };

    let richTextBundlePromise = null;
    window.ensureRichTextScriptsLoaded = function () {
        if (window.spokesQuill && window.Quill) return Promise.resolve(true);
        if (!richTextBundlePromise) {
            loadCssOnce(bootAssets.quillCss || 'lib/quill/quill.snow.css');
            richTextBundlePromise = loadScriptOnce(bootAssets.quillJs || 'lib/quill/quill.js').then(async () => {
                if (window.Quill && !window.Quill.Module) {
                    window.Quill.Module = window.Quill.import('core/module');
                }
                await Promise.all([
                    loadScriptOnce(bootAssets.quillMagicUrl || 'lib/quill-magic-url/quill-magic-url.min.js'),
                    loadScriptOnce(bootAssets.spokesQuill || 'js/spokes-quill.js')
                ]);
                return true;
            });
        }
        return richTextBundlePromise;
    };

    let cropperBundlePromise = null;
    window.ensureCropperScriptsLoaded = function () {
        if (typeof window.initCropper === 'function' && window.Cropper) return Promise.resolve(true);
        if (!cropperBundlePromise) {
            loadCssOnce(bootAssets.cropperCss || 'lib/cropperjs/cropper.min.css');
            cropperBundlePromise = loadScriptOnce(bootAssets.cropperJs || 'lib/cropperjs/cropper.min.js').then(() =>
                loadScriptOnce(bootAssets.cropperInterop || 'js/cropper-interop.js')
            );
        }
        return cropperBundlePromise;
    };

    let castBundlePromise = null;
    window.ensureCastScriptsLoaded = function () {
        if (window.spokesCast) return Promise.resolve(true);
        if (!castBundlePromise) {
            castBundlePromise = loadScriptOnce(bootAssets.googleCast || 'js/google-cast-sender.js');
        }
        return castBundlePromise;
    };

    let shareBundlePromise = null;
    window.ensureShareScriptsLoaded = function () {
        if (typeof window.registerShareTarget === 'function') {
            return Promise.resolve(true);
        }
        if (!shareBundlePromise) {
            shareBundlePromise = loadScriptOnce(bootAssets.shareTarget || 'js/share-target.js');
        }
        return shareBundlePromise;
    };

    // Safe Capacitor-gated stubs so callers invoked before share-target.js finishes loading
    // no-op cleanly on Web/Desktop and lazy-load on Capacitor Native.
    if (typeof window.spokesPushDirectShareShortcut !== 'function') {
        window.spokesPushDirectShareShortcut = async function (serverUrl, channelId, channelName, iconBase64) {
            if (!window.Capacitor?.Plugins?.ShortcutManager) return;
            await window.ensureShareScriptsLoaded();
            if (window.spokesPushDirectShareShortcut && !window.spokesPushDirectShareShortcut._isStub) {
                return window.spokesPushDirectShareShortcut(serverUrl, channelId, channelName, iconBase64);
            }
        };
        window.spokesPushDirectShareShortcut._isStub = true;
    }
    if (typeof window.spokesPushAppShortcut !== 'function') {
        window.spokesPushAppShortcut = async function (serverUrl, id, shortLabel, longLabel, iconBase64) {
            if (!window.Capacitor?.Plugins?.ShortcutManager) return;
            await window.ensureShareScriptsLoaded();
            if (window.spokesPushAppShortcut && !window.spokesPushAppShortcut._isStub) {
                return window.spokesPushAppShortcut(serverUrl, id, shortLabel, longLabel, iconBase64);
            }
        };
        window.spokesPushAppShortcut._isStub = true;
    }
    if (typeof window.spokesRemoveShortcut !== 'function') {
        window.spokesRemoveShortcut = async function (serverUrl, id) {
            if (!window.Capacitor?.Plugins?.ShortcutManager) return;
            await window.ensureShareScriptsLoaded();
            if (window.spokesRemoveShortcut && !window.spokesRemoveShortcut._isStub) {
                return window.spokesRemoveShortcut(serverUrl, id);
            }
        };
        window.spokesRemoveShortcut._isStub = true;
    }

    let idleScheduled = false;
    window.scheduleIdleDeferredScripts = function () {
        if (idleScheduled) return;
        idleScheduled = true;
        const triggerPreload = () => {
            // Only preload lightweight upload/download helpers post-interactive.
            // Heavy third-party SDKs (LiveKit ~430KB, Quill ~215KB, CropperJS, Google Cast)
            // load strictly on demand via their ensure*ScriptsLoaded() promises so startup
            // never triggers an 11-script download stampede competing with channel media.
            window.ensureUploadScriptsLoaded();
        };
        setTimeout(triggerPreload, 300);
    };
})();

const runStartup = async () => {
    // Step 0: Ensure desktop/web clients do not retain an accidental client=mobile query param
    const nativePlatform = checkIsNativePlatform();
    if (!nativePlatform && window.location.search.includes('client=mobile')) {
        try {
            const cleanUrl = new URL(window.location.href);
            cleanUrl.searchParams.delete('client');
            window.history.replaceState(null, '', cleanUrl.toString());
        } catch(e) {}
    }

    // Step 1: Never boot while hidden. A page loaded in the background (e.g. iOS calling
    // webView.reload() after the WebContent process was killed) would have its session refresh or
    // WebSocket handshake frozen by the OS mid-flight, leaving a half-finished boot on screen when
    // the user returns. Waiting costs nothing: nobody is looking at a hidden page, and native push
    // covers notifications. (Native only: a desktop background tab still needs its circuit for
    // in-app notifications.)
    const isDesktopPlatform = Boolean((typeof window.isSpokesDesktop !== 'undefined' && window.isSpokesDesktop) || (window.SpokesBridge && window.SpokesBridge.isDesktop) || navigator.userAgent.includes('SpokesDesktop'));
    if ((nativePlatform || isDesktopPlatform) && (window._spokesHostViewActive === false || (nativePlatform && document.visibilityState === 'hidden' && window._spokesHostViewExplicitlyActivated !== true))) {
        console.log('[App.razor] Page is hidden. Deferring boot until it becomes visible.');
        window.startupTimings.hidden_wait_start = Date.now();
        await new Promise(resolve => {
            let resolved = false;
            const onWakeCheck = () => {
                if (resolved) return;
                if (window._spokesHostViewActive === false) return;
                if (document.visibilityState !== 'visible' && window._spokesHostViewExplicitlyActivated !== true) return;
                resolved = true;
                document.removeEventListener('visibilitychange', onWakeCheck);
                window.removeEventListener('spokes:host-view-active', onWakeCheck);
                resolve();
            };
            window.addEventListener('spokes:host-view-active', onWakeCheck);
            document.addEventListener('visibilitychange', onWakeCheck);
        });
        window.startupTimings.hidden_wait_end = Date.now();
        console.log('[App.razor] Page visible after ' + (window.startupTimings.hidden_wait_end - window.startupTimings.hidden_wait_start) + 'ms. Resuming boot.');
    }

    // Step 2: Ensure cookies are synced (native only)
    try {
        if ((await ensureCookiesFlushed()) === false) {
            return; // Boot sequence aborted due to redirect
        }
    } catch (e) {
        console.error("[App.razor] ensureCookiesFlushed failed", e);
        // Continue to startBlazor anyway, so the user isn't stuck on the splash screen indefinitely.
    }

    // Step 3: Static SSR routes (/authentication/login, /mobile-login, /access-pending, /banned)
    // require no SignalR circuit or enhanced form/navigation interception.
    if (window.__isStaticLoginRoute) {
        splashController.hide();
        ensureCapacitorInitializedOnce();
        return;
    }

    // Step 4: Start Blazor (initCapacitorEnv runs in Blazor.start().finally())
    splashController.armWatchdog(splashController.INITIAL_WATCHDOG_MS, 'initial');
    startBlazor();
};

if (document.readyState === 'loading') {
    document.addEventListener('DOMContentLoaded', runStartup);
} else {
    runStartup();
}
})();
