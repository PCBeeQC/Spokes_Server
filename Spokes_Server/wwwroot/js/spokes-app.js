// Spokes Application Helpers & Utilities
// Extracted from App.razor for cacheability and reduced initial HTML payload size

window.openBase64InNewTab = (base64, filename) => {
    // Convert Base64 Data URL to Blob
    var arr = base64.split(','), mime = arr[0].match(/:(.*?);/)[1],
        bstr = atob(arr[1]), n = bstr.length, u8arr = new Uint8Array(n);
    while (n--) {
        u8arr[n] = bstr.charCodeAt(n);
    }
    var blob = new Blob([u8arr], { type: mime });

    // Create Blob URL
    var url = URL.createObjectURL(blob);

    // Open in new tab
    var newWin = window.open(url, '_blank');
    if (newWin && filename) {
        newWin.onload = () => {
            try { newWin.document.title = filename; } catch (e) { }
        };
    }
};

function applyTextAreaUpdate(el, value, selStart, selEnd) {
    el.value = value;
    el.selectionStart = selStart;
    el.selectionEnd = selEnd;
    el.focus();
    el.dispatchEvent(new Event('input', { bubbles: true }));
}

window.insertTextAtCursor = (elementId, textToInsert) => {
    var el = document.getElementById(elementId);
    if (!el) return;
    var start = el.selectionStart;
    var end = el.selectionEnd;
    var text = el.value;
    var nextPos = start + textToInsert.length;
    applyTextAreaUpdate(el, text.substring(0, start) + textToInsert + text.substring(end), nextPos, nextPos);
    return el.value;
};

window.wrapSelection = (elementId, startTag, endTag) => {
    var el = document.getElementById(elementId);
    if (!el) return;
    var start = el.selectionStart;
    var end = el.selectionEnd;
    var text = el.value;
    var selected = text.substring(start, end);
    var newText = startTag + selected + endTag;
    var selStart = selected.length === 0 ? start + startTag.length : start;
    var selEnd = selected.length === 0 ? selStart : start + newText.length;
    applyTextAreaUpdate(el, text.substring(0, start) + newText + text.substring(end), selStart, selEnd);
    return el.value;
};

window.getCursorPosition = (elementId) => {
    var el = document.getElementById(elementId);
    return el ? el.selectionStart : 0;
};

window.getTextAreaValue = (elementId) => {
    var el = document.getElementById(elementId);
    return el ? el.value : '';
};

window.setTextAreaValue = (elementId, value, cursorPosition) => {
    var el = document.getElementById(elementId);
    if (!el) return;
    applyTextAreaUpdate(el, value, cursorPosition, cursorPosition);
};

window.scrollElementIntoView = (parentId, childId) => {
    const parent = document.getElementById(parentId);
    const child = document.getElementById(childId);
    if (!parent || !child) return;

    const parentRect = parent.getBoundingClientRect();
    const childRect = child.getBoundingClientRect();

    if (childRect.bottom > parentRect.bottom) {
        parent.scrollTop += (childRect.bottom - parentRect.bottom);
    } else if (childRect.top < parentRect.top) {
        parent.scrollTop -= (parentRect.top - childRect.top);
    }
};

// Global Search Ctrl+K shortcut
let _ctrlKDotNetRef = null;
let _ctrlKHandler = null;
window.registerCtrlK = (dotNetRef) => {
    _ctrlKDotNetRef = dotNetRef;
    _ctrlKHandler = (e) => {
        if ((e.ctrlKey || e.metaKey) && e.key === 'k') {
            e.preventDefault();
            if (_ctrlKDotNetRef && !window._blazorDisconnected) {
                try { _ctrlKDotNetRef.invokeMethodAsync('OnCtrlK').catch(()=>{}); } catch(e) {}
            }
        }
    };
    document.addEventListener('keydown', _ctrlKHandler);
};
window.unregisterCtrlK = () => {
    if (_ctrlKHandler) {
        document.removeEventListener('keydown', _ctrlKHandler);
        _ctrlKHandler = null;
    }
    _ctrlKDotNetRef = null;
};

// Window Focus Tracking & Presence Ordering
let _presenceSeq = 0;
const nextPresenceSeq = () => ++_presenceSeq;
let _pendingOutOfBandAbort = null;
let _onFocusStateChangedForActivity = null;
let _getConfirmedFocusState = null;

function abortPendingOutOfBandPresence() {
    if (_pendingOutOfBandAbort) {
        try { _pendingOutOfBandAbort.abort(); } catch (_) {}
        _pendingOutOfBandAbort = null;
    }
}

function isMobileOrNativePlatform() {
    const ua = typeof navigator !== 'undefined' ? (navigator.userAgent || '') : '';
    return /Android|webOS|iPhone|iPad|iPod|BlackBerry|IEMobile|Opera Mini/i.test(ua) ||
        (typeof window.isCapacitorNative === 'function' && window.isCapacitorNative());
}

function sendOutOfBandPresencePost(endpointPath, options) {
    if (!window._currentSubscriptionId) return;
    const opts = options || {};
    const preferBeacon = Boolean(opts.preferBeacon === true);
    const abortable = Boolean(opts.abortable === true);
    const bodyObj = {
        subscriptionId: window._currentSubscriptionId,
        presenceToken: window._currentPresenceToken || null
    };
    if (typeof opts.sequence === 'number' && opts.sequence > 0) {
        bodyObj.sequence = opts.sequence;
    }
    if (typeof opts.state === 'string' && opts.state.length > 0) {
        bodyObj.state = opts.state;
    }
    const payload = JSON.stringify(bodyObj);

    if (preferBeacon && typeof navigator !== 'undefined' && typeof navigator.sendBeacon === 'function' && typeof Blob === 'function') {
        try {
            if (navigator.sendBeacon(endpointPath, new Blob([payload], { type: 'application/json' }))) {
                return;
            }
        } catch (_) {}
    }

    let controller = null;
    if (abortable && typeof AbortController === 'function') {
        abortPendingOutOfBandPresence();
        controller = new AbortController();
        _pendingOutOfBandAbort = controller;
    }

    try {
        const fetchOpts = {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: payload,
            keepalive: true,
            cache: 'no-store'
        };
        if (controller) {
            fetchOpts.signal = controller.signal;
        }
        const req = fetch(endpointPath, fetchOpts);
        if (req && typeof req.catch === 'function') {
            req.catch(() => {}).finally(() => {
                if (controller && _pendingOutOfBandAbort === controller) {
                    _pendingOutOfBandAbort = null;
                }
            });
        }
    } catch (e) {
        if (controller && _pendingOutOfBandAbort === controller) {
            _pendingOutOfBandAbort = null;
        }
        if (typeof navigator !== 'undefined' && typeof navigator.sendBeacon === 'function' && typeof Blob === 'function') {
            try { navigator.sendBeacon(endpointPath, new Blob([payload], { type: 'application/json' })); } catch (_) {}
        }
    }
}

let _focusDotNetRef = null;
let _visHandler = null;
let _focusHandler = null;
let _blurHandler = null;
let _hostViewActiveFocusHandler = null;
let _blazorConnectedFocusHandler = null;
let _blazorDisconnectedFocusHandler = null;
let _electronFocusUnsub = null;
let _isNativeActive = true;
let _refocusTimer = null;
let _focusRetryTimer = null;

window.isSpokesWindowFocused = () => {
    if (window._spokesHostViewActive === false) {
        return false;
    }
    const ua = typeof navigator !== 'undefined' ? (navigator.userAgent || '') : '';
    const isMobile = /Android|webOS|iPhone|iPad|iPod|BlackBerry|IEMobile|Opera Mini/i.test(ua);
    const isNative = typeof window.isCapacitorNative === 'function' && window.isCapacitorNative();
    const isDesktop = !!(window.SpokesBridge && window.SpokesBridge.isDesktop);

    if (isDesktop) {
        if (typeof window._spokesElectronWindowFocused === 'boolean') {
            return document.visibilityState === 'visible' && window._spokesElectronWindowFocused;
        }
        return document.visibilityState === 'visible' && document.hasFocus();
    }
    if (isNative) {
        if (window._spokesNativeShellLifecycle === true) {
            return window._spokesHostViewActive === true &&
                (document.visibilityState === 'visible' || window._spokesHostViewExplicitlyActivated === true);
        }
        if (!_isNativeActive && !window._spokesHostViewExplicitlyActivated) {
            return false;
        }
        if (window._spokesHostViewExplicitlyActivated === true && window._spokesHostViewActive === true) {
            return true;
        }
        return document.visibilityState === 'visible';
    }
    return document.visibilityState === 'visible' && (isMobile || document.hasFocus());
};

window.registerWindowFocus = (dotNetRef) => {
    window.unregisterWindowFocus();
    _focusDotNetRef = dotNetRef;
    _isNativeActive = true;
    const FOCUS_RETRY_DELAYS_MS = [250, 500, 1000];
    let _confirmedFocusState = null;
    let _inFlightFocusState = null;
    let _inFlightFocusSeq = 0;
    let _focusRetryCount = 0;
    _getConfirmedFocusState = () => _confirmedFocusState;

    if (typeof window._spokesElectronWindowFocused === 'undefined') {
        window._spokesElectronWindowFocused = null;
    }
    if (typeof window._spokesHostViewActive === 'undefined') {
        window._spokesHostViewActive = true;
    }

    const clearFocusRetryTimer = () => {
        if (_focusRetryTimer) {
            clearTimeout(_focusRetryTimer);
            _focusRetryTimer = null;
        }
    };

    const resetServerFocusSync = () => {
        clearFocusRetryTimer();
        _confirmedFocusState = null;
        _inFlightFocusState = null;
        _inFlightFocusSeq = 0;
        _focusRetryCount = 0;
    };

    const handleFocusInteropError = (err) => {
        const msg = String(err || '');
        if (msg.includes('No interop methods are registered for renderer') || msg.includes('There is no tracked object')) {
            window._blazorDisconnected = true;
            if (window.spokesResume && typeof window.spokesResume.onConnectionDown === 'function') {
                window.spokesResume.onConnectionDown('renderer-disposed');
            }
        }
    };

    const isBlazorSocketReady = () => {
        if (window._blazorDisconnected || window._isServerRestarting || window._reloadInProgress) return false;
        const ws = window.__spokesBlazorSocket;
        if (ws && typeof ws.readyState === 'number' && ws.readyState !== 1) return false;
        return true;
    };

    const sendFocusToServer = (isFocused, seq, verifyAlive) => {
        if (!_focusDotNetRef || !isBlazorSocketReady() || window._spokesPageUnloading) return false;
        _inFlightFocusState = isFocused;
        _inFlightFocusSeq = seq;
        try {
            const p = _focusDotNetRef.invokeMethodAsync('OnWindowFocusChanged', isFocused, seq);
            if (isFocused && verifyAlive && window.spokesResume && typeof window.spokesResume.verifyCircuitAlive === 'function') {
                window.spokesResume.verifyCircuitAlive(p, true, 'focus-resume');
            }
            if (p && typeof p.then === 'function') {
                p.then(() => {
                    if (_inFlightFocusSeq === seq) {
                        _inFlightFocusSeq = 0;
                        _inFlightFocusState = null;
                        _confirmedFocusState = isFocused;
                        _focusRetryCount = 0;
                        if (isFocused) {
                            window._lastHiddenDurationMs = 0;
                        }
                    }
                    // If focus changed again while this RPC was in flight, converge immediately.
                    if (!window._spokesPageUnloading && window.isSpokesWindowFocused() !== isFocused) {
                        updateState(false);
                    }
                }, (err) => {
                    if (_inFlightFocusSeq === seq) {
                        _inFlightFocusSeq = 0;
                        _inFlightFocusState = null;
                    }
                    handleFocusInteropError(err);
                    if (!window._spokesPageUnloading &&
                        window.isSpokesWindowFocused() === isFocused &&
                        isBlazorSocketReady() &&
                        _focusRetryCount < FOCUS_RETRY_DELAYS_MS.length) {
                        const delay = FOCUS_RETRY_DELAYS_MS[_focusRetryCount++];
                        clearFocusRetryTimer();
                        _focusRetryTimer = setTimeout(() => {
                            _focusRetryTimer = null;
                            updateState(true);
                        }, delay);
                    }
                });
            }
            return true;
        } catch (e) {
            if (_inFlightFocusSeq === seq) {
                _inFlightFocusSeq = 0;
                _inFlightFocusState = null;
            }
            handleFocusInteropError(e);
            return false;
        }
    };

    const updateState = (force = false) => {
        if (window._spokesPageUnloading) return;
        const isFocused = window.isSpokesWindowFocused();

        if (!isFocused) {
            if (_refocusTimer) {
                clearTimeout(_refocusTimer);
                _refocusTimer = null;
            }
            clearFocusRetryTimer();
            _focusRetryCount = 0;
            if (typeof _onFocusStateChangedForActivity === 'function') {
                try { _onFocusStateChangedForActivity(false); } catch (_) {}
            }

            if (_inFlightFocusSeq !== 0 && _inFlightFocusState === false) return;
            if (!force && _confirmedFocusState === false && _inFlightFocusSeq === 0) return;

            const seq = nextPresenceSeq();
            sendOutOfBandPresencePost('/spokesapi/presence/unfocus', { sequence: seq, abortable: true });
            const sent = sendFocusToServer(false, seq, false);
            if (!sent) {
                // Out-of-band /unfocus already notified the server while socket is down;
                // spokes:blazor-connected will reset _confirmedFocusState and re-sync on reconnect.
                _confirmedFocusState = false;
            }
            return;
        }

        abortPendingOutOfBandPresence();
        if (typeof _onFocusStateChangedForActivity === 'function') {
            try { _onFocusStateChangedForActivity(true); } catch (_) {}
        }

        if (_inFlightFocusSeq !== 0 && _inFlightFocusState === true) return;
        if (!force && _confirmedFocusState === true && _inFlightFocusSeq === 0) return;

        const applyFocused = () => {
            _refocusTimer = null;
            if (window._spokesPageUnloading || !window.isSpokesWindowFocused()) return;
            if (_inFlightFocusSeq !== 0 && _inFlightFocusState === true) return;
            if (!force && _confirmedFocusState === true && _inFlightFocusSeq === 0) return;

            if (!isBlazorSocketReady()) {
                // Socket not ready yet: keep _confirmedFocusState unconfirmed so spokes:blazor-connected
                // or the next foreground event synchronizes focus as soon as the socket is open.
                const ws = window.__spokesBlazorSocket;
                if (!window._blazorDisconnected && ws && typeof ws.readyState === 'number' && ws.readyState >= 2) {
                    if (window.spokesResume && typeof window.spokesResume.handleDetectedDeadSocket === 'function') {
                        window.spokesResume.handleDetectedDeadSocket('focus-socket-closed');
                    } else if (window.spokesResume && typeof window.spokesResume.onConnectionDown === 'function') {
                        window.spokesResume.onConnectionDown('focus-socket-closed');
                    }
                }
                return;
            }

            const seq = nextPresenceSeq();
            const shouldVerifyAlive = _confirmedFocusState !== true || (window._lastHiddenDurationMs || 0) > 0;
            const sent = sendFocusToServer(true, seq, shouldVerifyAlive);
            if (sent && typeof window._spokesReobserveReadReceipts === 'function') {
                try { window._spokesReobserveReadReceipts(); } catch (e) {}
            }
        };

        if (_confirmedFocusState === false && isMobileOrNativePlatform() && !force) {
            if (_refocusTimer) clearTimeout(_refocusTimer);
            _refocusTimer = setTimeout(applyFocused, 150);
        } else {
            if (_refocusTimer) {
                clearTimeout(_refocusTimer);
                _refocusTimer = null;
            }
            applyFocused();
        }
    };

    window._forceUpdateFocusState = () => {
        if (window._spokesHostViewActive === true) {
            _isNativeActive = true;
            window._spokesPageUnloading = false;
        }
        updateState(true);
    };

    _visHandler = () => {
        if (document.visibilityState === 'visible') {
            window._spokesPageUnloading = false;
        } else {
            window._spokesHostViewExplicitlyActivated = false;
        }
        updateState();
    };
    _focusHandler = () => updateState();
    _blurHandler = () => updateState();
    _hostViewActiveFocusHandler = () => {
        if (window._spokesHostViewActive === true) {
            _isNativeActive = true;
            window._spokesPageUnloading = false;
        }
        updateState();
    };
    _blazorDisconnectedFocusHandler = () => {
        resetServerFocusSync();
    };
    _blazorConnectedFocusHandler = () => {
        resetServerFocusSync();
        if (window._spokesHostViewActive === true && document.visibilityState === 'visible') {
            window._spokesPageUnloading = false;
        }
        updateState(true);
    };

    document.addEventListener('visibilitychange', _visHandler);
    window.addEventListener('focus', _focusHandler);
    window.addEventListener('blur', _blurHandler);
    window.addEventListener('spokes:host-view-active-changed', _hostViewActiveFocusHandler);
    window.addEventListener('spokes:blazor-disconnected', _blazorDisconnectedFocusHandler);
    window.addEventListener('spokes:blazor-connected', _blazorConnectedFocusHandler);

    if (window.SpokesBridge && typeof window.SpokesBridge.onWindowFocusChanged === 'function') {
        if (_electronFocusUnsub) {
            try { _electronFocusUnsub(); } catch(e) {}
        }
        _electronFocusUnsub = window.SpokesBridge.onWindowFocusChanged((isFocused) => {
            window._spokesElectronWindowFocused = !!isFocused;
            updateState();
        });
    }

    if (typeof window.isCapacitorNative === 'function' && window.isCapacitorNative()) {
        if (window.Capacitor && window.Capacitor.Plugins && window.Capacitor.Plugins.App) {
            try {
                const result = window.Capacitor.Plugins.App.addListener('appStateChange', ({ isActive }) => {
                    console.log(`[Capacitor AppState] State changed. isActive: ${isActive}, subscriptionId: ${window._currentSubscriptionId}`);
                    // When the native Android/iOS shell drives monotonic epoch-tagged lifecycle updates via
                    // spokesSetServerViewActive(..., { nativeShellLifecycle: true, epoch }), ignore un-sequenced
                    // Capacitor WebMessagePort appStateChange callbacks so delayed background events cannot race resume.
                    if (window._spokesNativeShellLifecycle === true) {
                        return;
                    }
                    _isNativeActive = isActive;
                    if (!isActive) {
                        window._spokesHostViewExplicitlyActivated = false;
                    } else {
                        window._spokesPageUnloading = false;
                    }
                    updateState();
                });

                if (result && typeof result.then === 'function') {
                    result.then(listener => {
                        window._capacitorAppStateListener = listener;
                    }).catch(e => console.error("Failed to add appStateChange listener", e));
                } else {
                    window._capacitorAppStateListener = result;
                }
            } catch(e) {
                console.error("Error setting up appStateChange listener:", e);
            }
        }
    }

    // Initial state
    updateState();
};

window.unregisterWindowFocus = () => {
    if (_refocusTimer) {
        clearTimeout(_refocusTimer);
        _refocusTimer = null;
    }
    if (_focusRetryTimer) {
        clearTimeout(_focusRetryTimer);
        _focusRetryTimer = null;
    }
    abortPendingOutOfBandPresence();
    if (_visHandler) document.removeEventListener('visibilitychange', _visHandler);
    if (_focusHandler) window.removeEventListener('focus', _focusHandler);
    if (_blurHandler) window.removeEventListener('blur', _blurHandler);
    if (_hostViewActiveFocusHandler) window.removeEventListener('spokes:host-view-active-changed', _hostViewActiveFocusHandler);
    if (_blazorDisconnectedFocusHandler) window.removeEventListener('spokes:blazor-disconnected', _blazorDisconnectedFocusHandler);
    if (_blazorConnectedFocusHandler) window.removeEventListener('spokes:blazor-connected', _blazorConnectedFocusHandler);
    if (_electronFocusUnsub) {
        try { _electronFocusUnsub(); } catch(e) {}
        _electronFocusUnsub = null;
    }
    _focusDotNetRef = null;
    _getConfirmedFocusState = null;
    _visHandler = null;
    _focusHandler = null;
    _blurHandler = null;
    _hostViewActiveFocusHandler = null;
    _blazorDisconnectedFocusHandler = null;
    _blazorConnectedFocusHandler = null;

    if (window._capacitorAppStateListener && typeof window._capacitorAppStateListener.remove === 'function') {
        window._capacitorAppStateListener.remove();
        window._capacitorAppStateListener = null;
    }
};

// Explicit helper for Mobile/Desktop Server Switcher before unloading or hiding a server view.
// In warm-pooled native shells (Android Fragment pool / iOS WKWebView pool), switching servers
// hides the current WebView without destroying its Blazor circuit, so we transition to unfocused
// unless the session is explicitly being evicted (`options.evict === true`).
window.spokesNotifyServerSwitchAway = (options) => {
    const isEvicting = Boolean(options && options.evict === true);
    const hasWarmNativePool = Boolean(window.SpokesNative && (typeof window.SpokesNative.openServerSwitcher === 'function' || typeof window.SpokesNative.openServer === 'function')) ||
        Boolean(window.webkit && window.webkit.messageHandlers && (window.webkit.messageHandlers.SpokesNative_shell || window.webkit.messageHandlers.SpokesNative));

    if (hasWarmNativePool && !isEvicting) {
        if (typeof window.spokesSetServerViewActive === 'function') {
            try { window.spokesSetServerViewActive(false); } catch (e) {}
        }
        sendOutOfBandPresencePost('/spokesapi/presence/unfocus', { sequence: nextPresenceSeq(), abortable: true });
        return;
    }

    window._spokesPageUnloading = true;
    sendOutOfBandPresencePost('/spokesapi/presence/offline', { preferBeacon: true, sequence: nextPresenceSeq() });
};

// App Termination Teardown & BFCache Restore
window.addEventListener('pagehide', function() {
    window._spokesPageUnloading = true;
    sendOutOfBandPresencePost('/spokesapi/presence/offline', { preferBeacon: true, sequence: nextPresenceSeq() });
});

window.addEventListener('pageshow', function(event) {
    const wasUnloading = window._spokesPageUnloading === true;
    window._spokesPageUnloading = false;
    if (event && event.persisted) {
        if (_focusDotNetRef && !window._blazorDisconnected) {
            try {
                _focusDotNetRef.invokeMethodAsync('OnPageRestoredFromCache', Boolean(window.isSpokesWindowFocused()), nextPresenceSeq()).catch(() => {});
            } catch (_) {}
        }
        if (typeof window._forceUpdateFocusState === 'function') {
            try { window._forceUpdateFocusState(); } catch (_) {}
        }
    } else if (wasUnloading && typeof window._forceUpdateFocusState === 'function') {
        try { window._forceUpdateFocusState(); } catch (_) {}
    }
});

// Unified Activity & Idle Tracker (5-minute timeout in Browser; OS-level in Electron Desktop)
const SPOKES_IDLE_TIMEOUT_MS = 5 * 60 * 1000;
let _activityTrackerDotNetRef = null;
let _activityTrackerLastPing = 0;
let _activityTrackerHandler = null;
let _browserIdleTimer = null;
let _desktopPresenceUnsub = null;
let _desktopActivePingInterval = null;
let _desktopHostViewActiveHandler = null;

window.registerImageRecoveryCoordinator = (dotNetRef) => {
    window._imageRecoveryCoordinator = dotNetRef;
};

window.registerActivityTracker = (dotNetRef) => {
    window.unregisterActivityTracker();

    _activityTrackerDotNetRef = dotNetRef;
    _activityTrackerLastPing = Date.now();
    window._spokesClientIdle = false;

    const sendActivePing = () => {
        if (window._spokesPageUnloading || window._spokesHostViewActive === false) return;
        const isFocused = typeof window.isSpokesWindowFocused === 'function' ? Boolean(window.isSpokesWindowFocused()) : false;
        const ws = window.__spokesBlazorSocket;
        if (_activityTrackerDotNetRef && !window._blazorDisconnected && (!ws || ws.readyState === 1)) {
            try { _activityTrackerDotNetRef.invokeMethodAsync('OnDeviceActivePing', isFocused).catch(()=>{}); } catch(e) {}
        }
        if (isFocused && typeof _getConfirmedFocusState === 'function' && _getConfirmedFocusState() !== true && typeof window._forceUpdateFocusState === 'function') {
            try { window._forceUpdateFocusState(); } catch(e) {}
        }
    };

    const reportClientPresenceState = (state) => {
        if (window._spokesPageUnloading) return;
        const seq = nextPresenceSeq();
        const ws = window.__spokesBlazorSocket;
        if (_activityTrackerDotNetRef && !window._blazorDisconnected && (!ws || ws.readyState === 1)) {
            try { _activityTrackerDotNetRef.invokeMethodAsync('OnClientPresenceStateChanged', state, seq).catch(()=>{}); } catch(e) {}
        }
        sendOutOfBandPresencePost('/spokesapi/presence/state', {
            sequence: seq,
            state: state,
            abortable: state === 'away' || state === 'idle'
        });
    };

    const isDesktopApp = !!(window.SpokesBridge && window.SpokesBridge.isDesktop);

    if (isDesktopApp) {
        let _lastDesktopPresenceStatus = 'online';
        const applyDesktopPresence = (status) => {
            if (!status) return;
            _lastDesktopPresenceStatus = status;
            if (window._spokesHostViewActive === false) {
                if (_desktopActivePingInterval) {
                    clearInterval(_desktopActivePingInterval);
                    _desktopActivePingInterval = null;
                }
                if (status !== 'online') {
                    window._spokesClientIdle = (status === 'away' || status === 'offline');
                    reportClientPresenceState(status);
                }
                return;
            }
            if (status === 'online') {
                const wasIdle = window._spokesClientIdle === true;
                window._spokesClientIdle = false;
                reportClientPresenceState('online');
                sendActivePing();
                if (wasIdle && typeof window._spokesReobserveReadReceipts === 'function') {
                    try { window._spokesReobserveReadReceipts(); } catch(e) {}
                }
                if (_desktopActivePingInterval) clearInterval(_desktopActivePingInterval);
                _desktopActivePingInterval = setInterval(sendActivePing, 30000);
            } else {
                window._spokesClientIdle = (status === 'away' || status === 'offline');
                if (_desktopActivePingInterval) {
                    clearInterval(_desktopActivePingInterval);
                    _desktopActivePingInterval = null;
                }
                reportClientPresenceState(status);
            }
        };

        _desktopHostViewActiveHandler = () => {
            if (window._spokesHostViewActive === false) {
                if (_desktopActivePingInterval) {
                    clearInterval(_desktopActivePingInterval);
                    _desktopActivePingInterval = null;
                }
            } else {
                applyDesktopPresence(_lastDesktopPresenceStatus);
            }
        };
        window.addEventListener('spokes:host-view-active-changed', _desktopHostViewActiveHandler);

        if (typeof window.SpokesBridge.onPresenceChanged === 'function') {
            _desktopPresenceUnsub = window.SpokesBridge.onPresenceChanged((status) => {
                applyDesktopPresence(status);
            });
        }

        if (typeof window.SpokesBridge.getPresence === 'function') {
            try {
                Promise.resolve(window.SpokesBridge.getPresence()).then((status) => {
                    applyDesktopPresence(status || 'online');
                }).catch(() => {
                    applyDesktopPresence('online');
                });
            } catch(e) {
                applyDesktopPresence('online');
            }
        } else {
            applyDesktopPresence('online');
        }
    } else {
        const clearBrowserIdleTimer = () => {
            if (_browserIdleTimer) {
                clearTimeout(_browserIdleTimer);
                _browserIdleTimer = null;
            }
        };

        const resetBrowserIdleTimer = () => {
            clearBrowserIdleTimer();
            // Do not arm the idle timer while a mobile/native view is backgrounded;
            // backgrounded mobile presence is already represented by IsFocused = false.
            if (isMobileOrNativePlatform() && !window.isSpokesWindowFocused()) {
                return;
            }
            _browserIdleTimer = setTimeout(() => {
                _browserIdleTimer = null;
                if (isMobileOrNativePlatform() && !window.isSpokesWindowFocused()) {
                    return;
                }
                if (window._spokesInVoiceCall) {
                    sendActivePing();
                    resetBrowserIdleTimer();
                    return;
                }
                window._spokesClientIdle = true;
                reportClientPresenceState('away');
            }, SPOKES_IDLE_TIMEOUT_MS);
        };

        _onFocusStateChangedForActivity = (isFocused) => {
            if (isFocused) {
                window._spokesClientIdle = false;
                resetBrowserIdleTimer();
            } else if (isMobileOrNativePlatform()) {
                clearBrowserIdleTimer();
            }
        };

        resetBrowserIdleTimer();

        _activityTrackerHandler = () => {
            const now = Date.now();
            const wasIdle = window._spokesClientIdle === true;
            window._spokesClientIdle = false;
            resetBrowserIdleTimer();

            if (wasIdle || (now - _activityTrackerLastPing > 30000)) {
                _activityTrackerLastPing = now;
                sendActivePing();
                if (wasIdle && typeof window._spokesReobserveReadReceipts === 'function') {
                    try { window._spokesReobserveReadReceipts(); } catch(e) {}
                }
            }
        };

        // Passive listeners ensure they don't block scrolling/rendering
        const opts = { passive: true };
        window.addEventListener('mousemove', _activityTrackerHandler, opts);
        window.addEventListener('mousedown', _activityTrackerHandler, opts);
        window.addEventListener('keydown', _activityTrackerHandler, opts);
        window.addEventListener('touchstart', _activityTrackerHandler, opts);
        window.addEventListener('scroll', _activityTrackerHandler, opts);
        window.addEventListener('wheel', _activityTrackerHandler, opts);
    }
};

window.unregisterActivityTracker = () => {
    _onFocusStateChangedForActivity = null;
    if (_activityTrackerHandler) {
        window.removeEventListener('mousemove', _activityTrackerHandler);
        window.removeEventListener('mousedown', _activityTrackerHandler);
        window.removeEventListener('keydown', _activityTrackerHandler);
        window.removeEventListener('touchstart', _activityTrackerHandler);
        window.removeEventListener('scroll', _activityTrackerHandler);
        window.removeEventListener('wheel', _activityTrackerHandler);
    }
    if (_browserIdleTimer) {
        clearTimeout(_browserIdleTimer);
        _browserIdleTimer = null;
    }
    if (_desktopPresenceUnsub) {
        try { _desktopPresenceUnsub(); } catch(e) {}
        _desktopPresenceUnsub = null;
    }
    if (_desktopActivePingInterval) {
        clearInterval(_desktopActivePingInterval);
        _desktopActivePingInterval = null;
    }
    if (_desktopHostViewActiveHandler) {
        window.removeEventListener('spokes:host-view-active-changed', _desktopHostViewActiveHandler);
        _desktopHostViewActiveHandler = null;
    }
    _activityTrackerHandler = null;
    _activityTrackerDotNetRef = null;
};


// --- Page-based viewer pagination ---
// IMPORTANT: Never mutate Blazor-managed DOM (no innerHTML, no removeChild on
// tracked nodes). Instead, hide the original paper and insert cloned pages as
// a sibling. Call cleanupPagination on component dispose to remove the sibling.
window.paginateSections = async (paperId, availableHeight) => {
    const paper = document.getElementById(paperId);
    if (!paper) return;

    const scrollParent = paper.closest('.print-scroll');
    if (scrollParent) {
        scrollParent.classList.add('viewer-scroll-active');
    }

    // Wait for web fonts and any embedded images inside the paper to finish loading before measuring
    try {
        if (document.fonts && document.fonts.ready) {
            await document.fonts.ready;
        }
        const images = Array.from(paper.querySelectorAll('img'));
        await Promise.all(images.map(img => {
            if (img.complete) return Promise.resolve();
            return new Promise(resolve => {
                img.addEventListener('load', resolve, { once: true });
                img.addEventListener('error', resolve, { once: true });
            });
        }));
    } catch (e) { }

    if (!document.body.contains(paper)) return;

    const existingContainer = document.querySelector(`[data-paginated-for="${paperId}"]`);
    if (existingContainer) existingContainer.remove();
    paper.removeAttribute('data-pagination-complete');
    paper.style.display = '';

    const printBody = paper.querySelector('.print-body');
    if (!printBody) {
        paper.setAttribute('data-pagination-complete', 'true');
        return;
    }

    // --- 1. Identify measurable blocks ---
    const blocks = [];
    let currentId = 0;

    function collectBlocks(node, forceBreak = false) {
        const hasForceBreak = forceBreak || node.classList.contains('force-break');

        // If it's keep-together, it's atomic
        if (node.classList.contains('keep-together')) {
            if (hasForceBreak) node.classList.add('force-break');
            node.setAttribute('data-pg-id', currentId++);
            blocks.push(node);
            return;
        }

        // List containers (OL, UL) inside an allow-break context should drill down
        // so individual list items (LI) can paginate across pages
        const isListContainer = /^(OL|UL)$/i.test(node.tagName);
        if (!node.classList.contains('allow-break') && !isListContainer) {
            if (hasForceBreak) node.classList.add('force-break');
            node.setAttribute('data-pg-id', currentId++);
            blocks.push(node);
        } else {
            // It is allow-break or a list container: recursively drill down
            const children = Array.from(node.children);
            if (children.length === 0) {
                if (hasForceBreak) node.classList.add('force-break');
                node.setAttribute('data-pg-id', currentId++);
                blocks.push(node);
            } else {
                children.forEach((child, idx) => {
                    collectBlocks(child, idx === 0 && hasForceBreak);
                });
            }
        }
    }

    Array.from(printBody.children).forEach(child => collectBlocks(child));

    if (blocks.length === 0) {
        paper.setAttribute('data-pagination-complete', 'true');
        return;
    }

    // --- 2. Measure every block ---
    const measurements = [];
    for (let i = 0; i < blocks.length; i++) {
        const block = blocks[i];
        const rect = block.getBoundingClientRect();
        const style = window.getComputedStyle(block);
        
        const boxHeight = rect.height;
        let actualHeight = rect.height;
        if (i < blocks.length - 1) {
            const nextRect = blocks[i + 1].getBoundingClientRect();
            // Distance from top of this block to top of next block natively handles CSS margin collapsing
            actualHeight = nextRect.top - rect.top;
        } else {
            const marginBottom = parseFloat(style.marginBottom) || 0;
            actualHeight = rect.height + marginBottom;
        }

        const isHeading = /^H[1-6]$/i.test(block.tagName);
        const hasKeepWithNext = isHeading || block.classList.contains('keep-with-next');
        const text = (block.textContent || '').trim().replace(/[\s\u00a0]+/g, '');
        const isEmpty = text.length === 0 && block.querySelectorAll('img, svg, table, hr').length === 0;

        measurements.push({
            index: i,
            height: actualHeight,
            boxHeight: boxHeight,
            forceBreak: block.classList.contains('force-break'),
            keepWithNext: hasKeepWithNext,
            isEmpty: isEmpty,
            isHeading: isHeading
        });
    }

    // --- 3. Group into pages ---
    const pages = [];
    let currentPage = [];
    let currentHeight = 0;
    let currentRepeatingBlocks = new Set();

    for (let i = 0; i < measurements.length; i++) {
        let m = measurements[i];
        const blockNode = blocks[m.index];
        const tableNode = blockNode.closest('table');
        
        if (m.forceBreak && currentPage.length > 0) {
            pages.push(currentPage);
            currentPage = [];
            currentHeight = 0;
            currentRepeatingBlocks.clear();
        }

        // Empty force-break markers only break the page and must never occupy a page on their own
        if (m.forceBreak && m.isEmpty) {
            continue;
        }

        let repeatingHeightToAdd = 0;
        let repeatingBlocksToTrack = [];
        if (tableNode) {
            const repeating = tableNode.querySelectorAll('.repeat-on-break[data-pg-id]');
            repeating.forEach(r => {
                const rId = parseInt(r.getAttribute('data-pg-id'));
                if (!currentRepeatingBlocks.has(rId) && m.index !== rId) {
                    const rMeas = measurements.find(x => x.index === rId);
                    if (rMeas) {
                        repeatingHeightToAdd += rMeas.height;
                        repeatingBlocksToTrack.push(rId);
                    }
                }
            });
        }
        
        const isFillRemaining = blockNode.classList.contains('fill-remaining-space');
        
        if (isFillRemaining) {
            let remaining = availableHeight - currentHeight - repeatingHeightToAdd;
            // If less than 200px remains, just push to next page for a better look
            if (remaining < 200 && currentPage.length > 0) {
                pages.push(currentPage);
                currentPage = [];
                currentHeight = 0;
                currentRepeatingBlocks.clear();
                
                repeatingHeightToAdd = 0;
                repeatingBlocksToTrack = [];
                if (tableNode) {
                    const repeating = tableNode.querySelectorAll('.repeat-on-break[data-pg-id]');
                    repeating.forEach(r => {
                        const rId = parseInt(r.getAttribute('data-pg-id'));
                        if (m.index !== rId) {
                            const rMeas = measurements.find(x => x.index === rId);
                            if (rMeas) {
                                repeatingHeightToAdd += rMeas.height;
                                repeatingBlocksToTrack.push(rId);
                            }
                        }
                    });
                }
                remaining = availableHeight - repeatingHeightToAdd;
            }
            
            const img = blockNode.querySelector('img');
            if (img) {
                // Subtract bottom margin (mb-10 is roughly 40px)
                img.style.maxHeight = Math.max(0, remaining - 40) + 'px';
            }
            
            // Re-measure after applying max-height
            m.height = remaining;
            m.boxHeight = remaining;
        }

        // Calculate height of the "keep together" group starting here.
        // Intermediate blocks in the group contribute their full step height (m.height, which includes
        // the collapsed margin to the next block in the group), while the final block in the group only
        // requires its own visual box height (boxHeight) to fit at the bottom of the page.
        let groupHeight = m.boxHeight;
        let j = i;
        while (j + 1 < measurements.length) {
            if (measurements[j].keepWithNext) {
                groupHeight += (measurements[j].height - measurements[j].boxHeight) + measurements[j + 1].boxHeight;
                j++;
                if (measurements[j].forceBreak) break;
            } else if (measurements[j].isEmpty || measurements[j].height < 15) {
                // Look ahead through empty spacer paragraphs following a keep-with-next element
                groupHeight += (measurements[j].height - measurements[j].boxHeight) + measurements[j + 1].boxHeight;
                j++;
                if (measurements[j].forceBreak) break;
            } else {
                break;
            }
        }
        
        // Orphan threshold: A heading with keep-with-next needs at least 100px of following content
        let effectiveRequiredHeight = groupHeight;
        if (m.keepWithNext) {
            effectiveRequiredHeight = Math.max(groupHeight, m.height + 100);
        }
        
        // If the entire group cannot fit on the remaining page space, push to next page
        if (!isFillRemaining && currentHeight + repeatingHeightToAdd + effectiveRequiredHeight > availableHeight && currentPage.length > 0) {
            pages.push(currentPage);
            currentPage = [];
            currentHeight = 0;
            currentRepeatingBlocks.clear();
            
            repeatingHeightToAdd = 0;
            repeatingBlocksToTrack = [];
            if (tableNode) {
                const repeating = tableNode.querySelectorAll('.repeat-on-break[data-pg-id]');
                repeating.forEach(r => {
                    const rId = parseInt(r.getAttribute('data-pg-id'));
                    if (m.index !== rId) {
                        const rMeas = measurements.find(x => x.index === rId);
                        if (rMeas) {
                            repeatingHeightToAdd += rMeas.height;
                            repeatingBlocksToTrack.push(rId);
                        }
                    }
                });
            }
        }
        
        currentPage.push(m.index);
        currentHeight += m.height + repeatingHeightToAdd;
        
        repeatingBlocksToTrack.forEach(id => currentRepeatingBlocks.add(id));
        if (blockNode.classList.contains('repeat-on-break')) {
            currentRepeatingBlocks.add(m.index);
        }
    }
    if (currentPage.length > 0) pages.push(currentPage);

    // Always paginate to ensure consistent .viewer-page-container styling

    // --- 4. Build sibling page container (using clone & prune) ---
    const footer = paper.querySelector('.print-footer-custom');
    const paperStyle = paper.getAttribute('style') || '';

    const container = document.createElement('div');
    container.className = 'viewer-page-container';
    container.setAttribute('data-paginated-for', paperId);

    for (let pi = 0; pi < pages.length; pi++) {
        const pageIndices = new Set(pages[pi]);
        
        // Inject repeating headers into pageIndices
        pages[pi].forEach(index => {
            const blockNode = blocks[index];
            const tableNode = blockNode.closest('table');
            if (tableNode) {
                const repeating = tableNode.querySelectorAll('.repeat-on-break[data-pg-id]');
                repeating.forEach(r => {
                    pageIndices.add(parseInt(r.getAttribute('data-pg-id')));
                });
            }
        });

        const pagePaper = document.createElement('div');
        pagePaper.className = 'viewer-page mud-paper mud-elevation-3 mx-auto overflow-hidden';
        pagePaper.style.cssText = paperStyle;
        pagePaper.style.position = 'relative';
        pagePaper.style.maxHeight = '1050px';

        if (footer) {
            const pageFooter = footer.cloneNode(true);
            pageFooter.querySelectorAll('[data-page-number]').forEach(el => {
                el.textContent = el.textContent
                    .replace('{page}', pi + 1)
                    .replace('{pages}', pages.length);
                el.style.display = '';
            });
            pageFooter.querySelectorAll('[data-pg-id]').forEach(el => el.removeAttribute('data-pg-id'));
            pagePaper.appendChild(pageFooter);
        }

        const clonedBody = printBody.cloneNode(true);
        
        clonedBody.querySelectorAll('[data-pg-id]').forEach(el => {
            const id = parseInt(el.getAttribute('data-pg-id'));
            if (!pageIndices.has(id)) {
                el.remove();
            } else {
                el.removeAttribute('data-pg-id');
            }
        });

        // Remove empty allow-break wrappers and empty list containers (bottom-up)
        const wrappers = Array.from(clonedBody.querySelectorAll('.allow-break, ol, ul')).reverse();
        for (const w of wrappers) {
            if (w.children.length === 0 && w.textContent.trim() === '') {
                w.remove();
            }
        }

        pagePaper.appendChild(clonedBody);
        container.appendChild(pagePaper);
    }

    blocks.forEach(b => b.removeAttribute('data-pg-id'));

    // --- 5. Hide original, insert sibling ---
    paper.setAttribute('data-pagination-complete', 'true');
    paper.style.display = 'none';
    paper.insertAdjacentElement('afterend', container);
};

// Cleanup: removes the paginated sibling and restores the original paper.
// Must be called from component DisposeAsync to prevent stale DOM on navigation.
window.cleanupPagination = (paperId) => {
    const container = document.querySelector(`[data-paginated-for="${paperId}"]`);
    if (container) container.remove();
    const paper = document.getElementById(paperId);
    if (paper) {
        paper.removeAttribute('data-pagination-complete');
        paper.style.display = '';
    }
    const scrollParent = document.querySelector('.print-scroll.viewer-scroll-active');
    if (scrollParent && !scrollParent.querySelector('#printable-area')) {
        scrollParent.classList.remove('viewer-scroll-active');
    }
};

window.scrollMainToTop = () => {
    window.scrollTo(0, 0);
    const scrollContainer = document.querySelector('.print-scroll');
    if (scrollContainer) {
        scrollContainer.scrollTop = 0;
    }
};

// --- TV Slideshow Remote Zoom & Pan Dragging ---
window.initSlideshowRemoteDrag = (wrapperId, containerId, dotNetRef) => {
    const wrapper = document.getElementById(wrapperId);
    const container = document.getElementById(containerId);
    if (!wrapper || !container) return;

    let isDragging = false;
    let startX = 0, startY = 0;
    let startPanX = 0, startPanY = 0;
    let currentScale = 1.0;
    let currentPanX = 0, currentPanY = 0;
    let lastBroadcast = 0;

    wrapper._setZoomState = (scale, panX, panY) => {
        currentScale = scale;
        currentPanX = panX;
        currentPanY = panY;
        applyTransform(true);
    };

    function applyTransform(withTransition = false) {
        if (currentScale <= 1.01) {
            container.style.transform = 'none';
        } else {
            container.style.transform = `scale(${currentScale}) translate3d(${currentPanX.toFixed(2)}%, ${currentPanY.toFixed(2)}%, 0)`;
        }
        if (withTransition) {
            container.style.transition = 'transform 0.15s ease-out';
        } else {
            container.style.transition = 'none';
        }
    }

    const onPointerDown = (e) => {
        if (currentScale <= 1.0) return;
        isDragging = true;
        startX = e.clientX;
        startY = e.clientY;
        startPanX = currentPanX;
        startPanY = currentPanY;
        try { wrapper.setPointerCapture(e.pointerId); } catch(err) {}
    };

    const onPointerMove = (e) => {
        if (!isDragging || currentScale <= 1.0) return;

        const rect = wrapper.getBoundingClientRect();
        if (!rect.width || !rect.height) return;

        const dx = e.clientX - startX;
        const dy = e.clientY - startY;

        // 1:1 finger tracking formula:
        // Visual movement on screen is scale * (pan% / 100) * rect.
        // For visual movement to equal dx: pan% = dx / (rect * scale) * 100
        const dxPercent = (dx / (rect.width * currentScale)) * 100;
        const dyPercent = (dy / (rect.height * currentScale)) * 100;

        const maxPan = ((currentScale - 1.0) / (2.0 * currentScale)) * 100;
        currentPanX = Math.max(-maxPan, Math.min(maxPan, startPanX + dxPercent));
        currentPanY = Math.max(-maxPan, Math.min(maxPan, startPanY + dyPercent));

        applyTransform(false);

        const now = Date.now();
        if (now - lastBroadcast >= 50) {
            lastBroadcast = now;
            if (dotNetRef) {
                dotNetRef.invokeMethodAsync('OnJsPanUpdated', currentPanX, currentPanY, true);
            }
        }
    };

    const onPointerUp = (e) => {
        if (isDragging) {
            isDragging = false;
            try { wrapper.releasePointerCapture(e.pointerId); } catch(err) {}
            if (dotNetRef) {
                dotNetRef.invokeMethodAsync('OnJsPanUpdated', currentPanX, currentPanY, false);
            }
        }
    };

    wrapper.addEventListener('pointerdown', onPointerDown);
    wrapper.addEventListener('pointermove', onPointerMove);
    wrapper.addEventListener('pointerup', onPointerUp);
    wrapper.addEventListener('pointercancel', onPointerUp);

    wrapper._cleanupDrag = () => {
        wrapper.removeEventListener('pointerdown', onPointerDown);
        wrapper.removeEventListener('pointermove', onPointerMove);
        wrapper.removeEventListener('pointerup', onPointerUp);
        wrapper.removeEventListener('pointercancel', onPointerUp);
    };
};

window.updateSlideshowRemoteZoom = (wrapperId, scale, panX, panY) => {
    const wrapper = document.getElementById(wrapperId);
    if (wrapper && typeof wrapper._setZoomState === 'function') {
        wrapper._setZoomState(scale, panX, panY);
    }
};

window.cleanupSlideshowRemoteDrag = (wrapperId) => {
    const wrapper = document.getElementById(wrapperId);
    if (wrapper && typeof wrapper._cleanupDrag === 'function') {
        wrapper._cleanupDrag();
    }
};

// --- TV Slideshow Synchronized Video Playback & Scrubbing ---
window.spokesSlideshowVideo = {
    syncTvVideo: function (isPlaying, currentTime, isMuted, isLooping) {
        const v = document.getElementById('stage-video');
        if (!v) return;
        v._desiredPlaying = Boolean(isPlaying);
        v.muted = Boolean(isMuted);
        v.loop = Boolean(isLooping);
        if (typeof currentTime === 'number' && Number.isFinite(currentTime) && currentTime >= 0 && Math.abs(v.currentTime - currentTime) > 0.35) {
            v.currentTime = currentTime;
        }
        if (isPlaying) {
            const p = v.play();
            if (p !== undefined) {
                p.then(() => {
                    if (typeof v._notifyPlayStateChanged === 'function') v._notifyPlayStateChanged();
                }).catch(e => {
                    console.warn('[spokesSlideshowVideo] Play rejected in syncTvVideo', e);
                    if (typeof v._notifyPlayStateChanged === 'function') v._notifyPlayStateChanged();
                });
            }
        } else {
            v.pause();
            if (typeof v._notifyPlayStateChanged === 'function') v._notifyPlayStateChanged();
        }
    },
    initTvVideo: function (elementId, dotNetRef, isMuted, isLooping) {
        const v = document.getElementById(elementId || 'stage-video');
        if (!v) return;
        if (v._cleanupTvVideo) {
            v._cleanupTvVideo();
        }

        v._desiredPlaying = true;
        v.muted = isMuted !== undefined ? Boolean(isMuted) : true;
        v.defaultMuted = true;
        v.playsInline = true;
        v.loop = isLooping !== undefined ? Boolean(isLooping) : true;

        let lastBroadcastTime = 0;

        const onLoaded = () => {
            if (dotNetRef && Number.isFinite(v.duration) && v.duration > 0) {
                dotNetRef.invokeMethodAsync('OnTvDurationKnown', v.duration).catch(() => {});
            }
        };

        const onTime = () => {
            const now = performance.now();
            if (now - lastBroadcastTime > 400) {
                lastBroadcastTime = now;
                if (dotNetRef && Number.isFinite(v.currentTime)) {
                    dotNetRef.invokeMethodAsync('OnTvTimeUpdate', v.currentTime, !v.paused, v.duration || 0).catch(() => {});
                }
            }
        };

        const onPlayPause = () => {
            if (dotNetRef) {
                dotNetRef.invokeMethodAsync('OnTvPlayStateChanged', !v.paused, v.currentTime || 0).catch(() => {});
            }
        };
        v._notifyPlayStateChanged = onPlayPause;

        const onEnded = () => {
            if (!v.loop && dotNetRef) {
                dotNetRef.invokeMethodAsync('OnTvVideoEnded').catch(() => {});
            }
        };

        v.addEventListener('loadedmetadata', onLoaded);
        v.addEventListener('timeupdate', onTime);
        v.addEventListener('play', onPlayPause);
        v.addEventListener('pause', onPlayPause);
        v.addEventListener('ended', onEnded);

        if (Number.isFinite(v.duration) && v.duration > 0) {
            onLoaded();
        }

        // Robust autoplay handling with muted fallback
        const attemptPlay = () => {
            if (!v._desiredPlaying || !v.paused) return;
            const p = v.play();
            if (p !== undefined) {
                p.catch(e => {
                    if (!v._desiredPlaying) return;
                    console.warn('[spokesSlideshowVideo] Autoplay blocked, attempting muted fallback', e);
                    v.muted = true;
                    const p2 = v.play();
                    if (p2 !== undefined) {
                        p2.catch(e2 => {
                            console.warn('[spokesSlideshowVideo] Muted autoplay also blocked', e2);
                        });
                    }
                });
            }
        };

        if (v.readyState >= 2) {
            attemptPlay();
        } else {
            v.addEventListener('canplay', attemptPlay, { once: true });
            v.addEventListener('loadeddata', attemptPlay, { once: true });
            attemptPlay();
        }

        v._cleanupTvVideo = () => {
            v.removeEventListener('loadedmetadata', onLoaded);
            v.removeEventListener('timeupdate', onTime);
            v.removeEventListener('play', onPlayPause);
            v.removeEventListener('pause', onPlayPause);
            v.removeEventListener('ended', onEnded);
            v.removeEventListener('canplay', attemptPlay);
            v.removeEventListener('loadeddata', attemptPlay);
            delete v._notifyPlayStateChanged;
            delete v._desiredPlaying;
        };
    },
    cleanupTvVideo: function (elementId) {
        const v = document.getElementById(elementId || 'stage-video');
        if (v && typeof v._cleanupTvVideo === 'function') {
            v._cleanupTvVideo();
        }
    }
};

// --- Canonical Touch-Tap Reconciliation & Ripple Cleanup (Android WebView / Mobile) ---
// Single source of truth for reconciling dropped clicks when Chromium's 8dp touch-slop or
// post-scroll fling deceleration tail suppresses `click` after `pointerdown`/`touchend`,
// while leaving normal native clicks (~1-5ms) completely untouched.
(function initSpokesTouch() {
    const TAP_MAX_DURATION_MS = 350;
    const TAP_MAX_DRIFT_PX = 22;
    const TAP_BOUNDS_SLOP_PX = 8;
    const SCROLL_CANCEL_DELTA_PX = 4;
    const FAST_FLING_WINDOW_MS = 60;
    const FAST_FLING_STEP_PX = 6;
    const NATIVE_CLICK_GRACE_MS = 55;
    const DUPLICATE_CLICK_SHIELD_MS = 350;
    const DUPLICATE_CLICK_RADIUS_PX = 24;
    const MAX_RECENT_GESTURES = 20;

    const EXCLUDED_SELECTOR = [
        'input',
        'textarea',
        'select',
        'option',
        'video',
        'audio',
        '[contenteditable="true"]',
        '[contenteditable=""]',
        '[data-spokes-no-tap-rescue]',
        '#lightbox-image-area',
        '.voice-bar-pull-tab',
        '.voice-slider',
        '.media-zoom-container',
        '.action-sheet-handle',
        '.mud-slider',
        '.mud-range-slider',
        '[draggable="true"]'
    ].join(',');

    const INTERACTIVE_SELECTOR = [
        'a[href]',
        'button',
        '[role="button"]',
        '[role="menuitem"]',
        '[role="option"]',
        '[role="tab"]',
        '[role="checkbox"]',
        '[role="switch"]',
        '[role="radio"]',
        '.mud-button-root',
        '.mud-icon-button',
        '.mud-list-item-clickable',
        '.mud-nav-link',
        '.mud-chip',
        '.mud-menu-item',
        'summary',
        'label'
    ].join(',');

    const now = () => (typeof performance !== 'undefined' && typeof performance.now === 'function')
        ? performance.now()
        : Date.now();

    let installed = false;
    let gestureSeq = 0;
    let active = null;
    let pendingRescue = null;
    let lastRescued = null;
    let lastScroll = null;
    let isRescuingClick = false;

    const stats = {
        nativeClicks: 0,
        rescuedTaps: 0,
        suppressedDuplicates: 0,
        cancelledScrolls: 0,
        abortedRipples: 0,
        recent: []
    };

    function recordGesture(entry) {
        stats.recent.push(entry);
        if (stats.recent.length > MAX_RECENT_GESTURES) {
            stats.recent.shift();
        }
    }

    function describeElement(el) {
        if (!el || !el.tagName) return 'unknown';
        const tag = el.tagName.toLowerCase();
        const id = el.id ? '#' + el.id : '';
        const cls = typeof el.className === 'string' && el.className.trim()
            ? '.' + el.className.trim().split(/\s+/).slice(0, 2).join('.')
            : '';
        return tag + id + cls;
    }

    function isDisabledElement(el) {
        if (!el) return true;
        if (el.disabled === true) return true;
        if (typeof el.getAttribute === 'function') {
            if (el.getAttribute('aria-disabled') === 'true') return true;
            if (el.hasAttribute && el.hasAttribute('disabled')) return true;
        }
        if (el.classList && (el.classList.contains('mud-disabled') || el.classList.contains('disabled'))) {
            return true;
        }
        return false;
    }

    function hasBlazorClickHandler(el) {
        if (!el || typeof el !== 'object') return false;
        if (typeof el.onclick === 'function') return true;
        if (typeof el.hasAttribute === 'function' && el.hasAttribute('onclick')) return true;
        try {
            const keys = Object.keys(el);
            for (let i = 0; i < keys.length; i++) {
                const k = keys[i];
                if (k.startsWith('_blazorEvents_')) {
                    const info = el[k];
                    if (info) {
                        if (typeof info.getHandler === 'function' && info.getHandler('click')) return true;
                        if (info.handlers && info.handlers.click) return true;
                    }
                }
            }
        } catch (_) {}
        return false;
    }

    function findInteractiveTarget(startNode) {
        let cur = startNode;
        if (cur && cur.nodeType !== 1) {
            cur = cur.parentElement;
        }
        if (!cur) return null;
        if (typeof cur.closest === 'function' && cur.closest(EXCLUDED_SELECTOR)) {
            return null;
        }
        while (cur && cur !== document.body && cur !== document.documentElement) {
            if (typeof cur.matches === 'function' && cur.matches(EXCLUDED_SELECTOR)) {
                return null;
            }
            if (isDisabledElement(cur)) {
                return null;
            }
            if ((typeof cur.matches === 'function' && cur.matches(INTERACTIVE_SELECTOR)) || hasBlazorClickHandler(cur)) {
                return cur;
            }
            cur = cur.parentElement;
        }
        return null;
    }

    function captureScrollSnapshots(el) {
        const snapshots = [];
        let cur = el;
        while (cur && cur !== document) {
            if (typeof cur.scrollTop === 'number' || typeof cur.scrollLeft === 'number') {
                snapshots.push({
                    el: cur,
                    top: cur.scrollTop || 0,
                    left: cur.scrollLeft || 0
                });
            }
            cur = cur.parentElement;
        }
        snapshots.push({
            el: window,
            top: window.scrollY || window.pageYOffset || 0,
            left: window.scrollX || window.pageXOffset || 0
        });
        return snapshots;
    }

    function computeMaxScrollDelta(snapshots) {
        if (!snapshots || snapshots.length === 0) return 0;
        let maxDelta = 0;
        for (let i = 0; i < snapshots.length; i++) {
            const s = snapshots[i];
            let curTop = 0;
            let curLeft = 0;
            if (s.el === window) {
                curTop = window.scrollY || window.pageYOffset || 0;
                curLeft = window.scrollX || window.pageXOffset || 0;
            } else if (s.el) {
                curTop = s.el.scrollTop || 0;
                curLeft = s.el.scrollLeft || 0;
            }
            const d = Math.max(Math.abs(curTop - s.top), Math.abs(curLeft - s.left));
            if (d > maxDelta) maxDelta = d;
        }
        return maxDelta;
    }

    function clearPendingRescue() {
        if (pendingRescue && pendingRescue.timer) {
            clearTimeout(pendingRescue.timer);
        }
        pendingRescue = null;
    }

    function abortMisleadingRipples(node) {
        if (!node) return;
        const rippleHost = (typeof node.closest === 'function')
            ? node.closest('.mud-ripple')
            : (node.parentElement && typeof node.parentElement.closest === 'function' ? node.parentElement.closest('.mud-ripple') : null);
        if (!rippleHost || typeof rippleHost.querySelectorAll !== 'function') return;
        const ripples = rippleHost.querySelectorAll('.mud-ripple-effect');
        if (!ripples || ripples.length === 0) return;
        for (let i = 0; i < ripples.length; i++) {
            try { ripples[i].remove(); } catch (_) {}
        }
        if (rippleHost._mudRipples && typeof rippleHost._mudRipples.clear === 'function') {
            rippleHost._mudRipples.clear();
        }
        stats.abortedRipples++;
    }

    function isPointInsideRectWithSlop(x, y, rect, slop) {
        if (!rect || rect.width <= 0 || rect.height <= 0) return false;
        return x >= rect.left - slop &&
            x <= rect.right + slop &&
            y >= rect.top - slop &&
            y <= rect.bottom + slop;
    }

    function isTargetUnobstructedAt(targetEl, rect, x, y) {
        if (typeof document.elementFromPoint !== 'function' || !rect || rect.width <= 0 || rect.height <= 0) {
            return true;
        }
        const inset = Math.min(1, rect.width / 4, rect.height / 4);
        const clampedX = Math.max(rect.left + inset, Math.min(rect.right - inset, x));
        const clampedY = Math.max(rect.top + inset, Math.min(rect.bottom - inset, y));
        const hit = document.elementFromPoint(clampedX, clampedY);
        if (!hit) return false;
        return hit === targetEl || (typeof targetEl.contains === 'function' && targetEl.contains(hit));
    }

    function hasActiveTextSelection() {
        try {
            const sel = window.getSelection ? window.getSelection() : null;
            return Boolean(sel && typeof sel.toString === 'function' && sel.toString().length > 0);
        } catch (_) {
            return false;
        }
    }

    function dispatchRescuedClick(targetEl, x, y, meta) {
        lastRescued = {
            target: targetEl,
            x: x,
            y: y,
            at: now()
        };
        stats.rescuedTaps++;
        recordGesture(Object.assign({ outcome: 'rescued' }, meta));

        try {
            console.debug(
                `[Touch] Rescued dropped tap on ${meta.target} (dur=${meta.durationMs}ms, drift=${meta.maxDist}px, scroll=${meta.scrollDelta}px, pointerCancel=${meta.pointerCancelled})`
            );
        } catch (_) {}

        isRescuingClick = true;
        try {
            if (typeof targetEl.click === 'function') {
                targetEl.click();
            } else if (typeof MouseEvent === 'function') {
                targetEl.dispatchEvent(new MouseEvent('click', {
                    bubbles: true,
                    cancelable: true,
                    composed: true,
                    view: window,
                    clientX: x,
                    clientY: y,
                    detail: 1
                }));
            }
        } catch (err) {
            try { console.warn('[Touch] Rescued click dispatch failed:', err); } catch (_) {}
        } finally {
            isRescuingClick = false;
        }
    }

    function onScrollCapture(e) {
        const t = now();
        const scroller = (e && e.target && e.target !== document) ? e.target : window;
        const top = scroller === window
            ? (window.scrollY || window.pageYOffset || 0)
            : (scroller.scrollTop || 0);
        const left = scroller === window
            ? (window.scrollX || window.pageXOffset || 0)
            : (scroller.scrollLeft || 0);

        let stepPx = 0;
        if (lastScroll && lastScroll.el === scroller && (t - lastScroll.time) < 120) {
            stepPx = Math.max(Math.abs(top - lastScroll.top), Math.abs(left - lastScroll.left));
        }
        lastScroll = { el: scroller, top: top, left: left, time: t, stepPx: stepPx };
    }

    function isInsideFastFling(targetEl) {
        if (!lastScroll || lastScroll.stepPx < FAST_FLING_STEP_PX) return false;
        if ((now() - lastScroll.time) >= FAST_FLING_WINDOW_MS) return false;
        if (lastScroll.el === window) return true;
        return Boolean(lastScroll.el && typeof lastScroll.el.contains === 'function' && lastScroll.el.contains(targetEl));
    }

    function onTouchStartCapture() {
        clearPendingRescue();
        active = null;
    }

    function onTouchStart(e) {
        if (!e || !e.touches || e.touches.length !== 1 || e.defaultPrevented) {
            active = null;
            return;
        }
        const touch = e.touches[0];
        const targetEl = findInteractiveTarget(e.target);
        if (!targetEl || isInsideFastFling(targetEl)) {
            active = null;
            return;
        }

        active = {
            id: ++gestureSeq,
            targetEl: targetEl,
            downNode: e.target,
            startX: touch.clientX,
            startY: touch.clientY,
            startTime: now(),
            maxDist: 0,
            scrollSnapshots: captureScrollSnapshots(targetEl),
            hadScroll: false,
            pointerCancelled: false,
            nativeClickReceived: false
        };
    }

    function onTouchMove(e) {
        if (!active) return;
        if (!e || !e.touches || e.touches.length !== 1) {
            active = null;
            return;
        }
        const touch = e.touches[0];
        const dist = Math.hypot(touch.clientX - active.startX, touch.clientY - active.startY);
        if (dist > active.maxDist) active.maxDist = dist;
        if (!active.hadScroll && computeMaxScrollDelta(active.scrollSnapshots) >= SCROLL_CANCEL_DELTA_PX) {
            active.hadScroll = true;
        }
    }

    function onPointerCancel() {
        if (!active) return;
        active.pointerCancelled = true;
        const scrollDelta = computeMaxScrollDelta(active.scrollSnapshots);
        if (scrollDelta >= SCROLL_CANCEL_DELTA_PX) {
            active.hadScroll = true;
        }
        if (active.hadScroll || active.maxDist > TAP_MAX_DRIFT_PX) {
            abortMisleadingRipples(active.targetEl || active.downNode);
        }
    }

    function onTouchCancel() {
        if (!active) return;
        const scrollDelta = computeMaxScrollDelta(active.scrollSnapshots);
        if (scrollDelta >= SCROLL_CANCEL_DELTA_PX || active.maxDist > TAP_MAX_DRIFT_PX) {
            abortMisleadingRipples(active.targetEl || active.downNode);
            stats.cancelledScrolls++;
        }
        active = null;
        clearPendingRescue();
    }

    function onTouchEnd(e) {
        const g = active;
        active = null;
        if (!g) return;
        if (!e || e.defaultPrevented || g.nativeClickReceived) return;
        if (e.touches && e.touches.length > 0) return;

        const changed = e.changedTouches && e.changedTouches[0];
        const endX = changed ? changed.clientX : g.startX;
        const endY = changed ? changed.clientY : g.startY;
        const endDist = Math.hypot(endX - g.startX, endY - g.startY);
        const maxDist = Math.round(Math.max(g.maxDist, endDist));
        const durationMs = Math.round(now() - g.startTime);
        const scrollDelta = Math.round(computeMaxScrollDelta(g.scrollSnapshots));
        const meta = {
            target: describeElement(g.targetEl),
            durationMs: durationMs,
            maxDist: maxDist,
            scrollDelta: scrollDelta,
            pointerCancelled: g.pointerCancelled
        };

        if (g.hadScroll || scrollDelta >= SCROLL_CANCEL_DELTA_PX) {
            stats.cancelledScrolls++;
            abortMisleadingRipples(g.targetEl || g.downNode);
            recordGesture(Object.assign({ outcome: 'scroll' }, meta));
            return;
        }
        if (durationMs > TAP_MAX_DURATION_MS || maxDist > TAP_MAX_DRIFT_PX) {
            if (maxDist > TAP_MAX_DRIFT_PX) {
                stats.cancelledScrolls++;
                abortMisleadingRipples(g.targetEl || g.downNode);
            }
            recordGesture(Object.assign({ outcome: durationMs > TAP_MAX_DURATION_MS ? 'long-press' : 'drag' }, meta));
            return;
        }
        if (!g.targetEl.isConnected || isDisabledElement(g.targetEl) || hasActiveTextSelection()) {
            return;
        }
        const rect = typeof g.targetEl.getBoundingClientRect === 'function' ? g.targetEl.getBoundingClientRect() : null;
        if (rect && !isPointInsideRectWithSlop(endX, endY, rect, TAP_BOUNDS_SLOP_PX)) {
            recordGesture(Object.assign({ outcome: 'out-of-bounds' }, meta));
            return;
        }

        const pending = {
            id: g.id,
            targetEl: g.targetEl,
            endX: endX,
            endY: endY,
            scrollSnapshots: g.scrollSnapshots,
            meta: meta,
            timer: null
        };
        pendingRescue = pending;
        pending.timer = setTimeout(() => {
            if (pendingRescue !== pending) return;
            pendingRescue = null;
            if (gestureSeq !== pending.id) return;
            if (!pending.targetEl.isConnected || isDisabledElement(pending.targetEl) || hasActiveTextSelection()) return;

            const postScrollDelta = Math.round(computeMaxScrollDelta(pending.scrollSnapshots));
            if (postScrollDelta >= SCROLL_CANCEL_DELTA_PX) {
                stats.cancelledScrolls++;
                abortMisleadingRipples(pending.targetEl);
                recordGesture(Object.assign({ outcome: 'fling-scroll' }, pending.meta, { scrollDelta: postScrollDelta }));
                return;
            }
            const liveRect = typeof pending.targetEl.getBoundingClientRect === 'function'
                ? pending.targetEl.getBoundingClientRect()
                : null;
            if (liveRect && !isPointInsideRectWithSlop(pending.endX, pending.endY, liveRect, TAP_BOUNDS_SLOP_PX)) {
                recordGesture(Object.assign({ outcome: 'shifted-bounds' }, pending.meta));
                return;
            }
            if (liveRect && !isTargetUnobstructedAt(pending.targetEl, liveRect, pending.endX, pending.endY)) {
                recordGesture(Object.assign({ outcome: 'obstructed' }, pending.meta));
                return;
            }

            dispatchRescuedClick(pending.targetEl, pending.endX, pending.endY, pending.meta);
        }, NATIVE_CLICK_GRACE_MS);
    }

    function onClickCapture(e) {
        if (isRescuingClick) return;

        if (active) {
            active.nativeClickReceived = true;
        }
        if (pendingRescue) {
            const p = pendingRescue;
            clearPendingRescue();
            stats.nativeClicks++;
            recordGesture(Object.assign({ outcome: 'native-click' }, p.meta));
            return;
        }

        if (lastRescued) {
            const elapsed = now() - lastRescued.at;
            if (elapsed <= DUPLICATE_CLICK_SHIELD_MS) {
                const sameTarget = e.target === lastRescued.target ||
                    (lastRescued.target && typeof lastRescued.target.contains === 'function' && lastRescued.target.contains(e.target));
                const nearPoint = typeof e.clientX === 'number' && typeof e.clientY === 'number' &&
                    Math.hypot(e.clientX - lastRescued.x, e.clientY - lastRescued.y) <= DUPLICATE_CLICK_RADIUS_PX;
                if (sameTarget || nearPoint) {
                    stats.suppressedDuplicates++;
                    if (typeof e.preventDefault === 'function') e.preventDefault();
                    if (typeof e.stopImmediatePropagation === 'function') e.stopImmediatePropagation();
                    return;
                }
            } else {
                lastRescued = null;
            }
        }
    }

    function onVisibilityOrBlur() {
        active = null;
        clearPendingRescue();
    }

    function shouldAutoInstall() {
        if (typeof window === 'undefined' || typeof document === 'undefined') return false;
        const hasTouch = ('ontouchstart' in window) ||
            (typeof navigator !== 'undefined' && (navigator.maxTouchPoints || 0) > 0);
        const coarsePointer = typeof window.matchMedia === 'function' &&
            Boolean(window.matchMedia('(pointer: coarse)').matches);
        return hasTouch || coarsePointer;
    }

    function install() {
        if (installed || typeof document === 'undefined') return;
        installed = true;
        // Capture-phase touchstart clears any prior pending rescue even if a custom widget stops propagation.
        // Bubble-phase touchstart/touchend run AFTER target handlers so e.defaultPrevented and stopPropagation()
        // from custom touch controls (mobile send button, autocomplete popovers, zoom gestures) are respected.
        document.addEventListener('touchstart', onTouchStartCapture, { passive: true, capture: true });
        document.addEventListener('touchstart', onTouchStart, { passive: true, capture: false });
        document.addEventListener('touchmove', onTouchMove, { passive: true, capture: true });
        document.addEventListener('touchend', onTouchEnd, { passive: true, capture: false });
        document.addEventListener('touchcancel', onTouchCancel, { passive: true, capture: true });
        document.addEventListener('pointercancel', onPointerCancel, { passive: true, capture: true });
        document.addEventListener('scroll', onScrollCapture, { passive: true, capture: true });
        document.addEventListener('click', onClickCapture, true);
        if (typeof window !== 'undefined' && typeof window.addEventListener === 'function') {
            window.addEventListener('blur', onVisibilityOrBlur);
        }
        document.addEventListener('visibilitychange', onVisibilityOrBlur);
    }

    function destroy() {
        if (!installed || typeof document === 'undefined') return;
        installed = false;
        active = null;
        clearPendingRescue();
        lastRescued = null;
        lastScroll = null;
        document.removeEventListener('touchstart', onTouchStartCapture, true);
        document.removeEventListener('touchstart', onTouchStart, false);
        document.removeEventListener('touchmove', onTouchMove, true);
        document.removeEventListener('touchend', onTouchEnd, false);
        document.removeEventListener('touchcancel', onTouchCancel, true);
        document.removeEventListener('pointercancel', onPointerCancel, true);
        document.removeEventListener('scroll', onScrollCapture, true);
        document.removeEventListener('click', onClickCapture, true);
        if (typeof window !== 'undefined' && typeof window.removeEventListener === 'function') {
            window.removeEventListener('blur', onVisibilityOrBlur);
        }
        document.removeEventListener('visibilitychange', onVisibilityOrBlur);
    }

    window.spokesTouch = {
        install: install,
        destroy: destroy,
        isInstalled: () => installed,
        getDiagnostics: () => ({
            installed: installed,
            nativeClicks: stats.nativeClicks,
            rescuedTaps: stats.rescuedTaps,
            suppressedDuplicates: stats.suppressedDuplicates,
            cancelledScrolls: stats.cancelledScrolls,
            abortedRipples: stats.abortedRipples,
            recent: stats.recent.slice()
        }),
        resetDiagnostics: () => {
            stats.nativeClicks = 0;
            stats.rescuedTaps = 0;
            stats.suppressedDuplicates = 0;
            stats.cancelledScrolls = 0;
            stats.abortedRipples = 0;
            stats.recent.length = 0;
            lastRescued = null;
        }
    };

    if (shouldAutoInstall()) {
        install();
    }
})();


