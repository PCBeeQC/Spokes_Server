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

window.insertTextAtCursor = (elementId, textToInsert) => {
    var el = document.getElementById(elementId);
    if (!el) return;
    var start = el.selectionStart;
    var end = el.selectionEnd;
    var text = el.value;
    var before = text.substring(0, start);
    var after = text.substring(end, text.length);
    el.value = before + textToInsert + after;
    el.selectionStart = el.selectionEnd = start + textToInsert.length;
    el.focus();
    // Trigger input event for Blazor binding update
    el.dispatchEvent(new Event('input', { bubbles: true }));
    return el.value;
};

window.wrapSelection = (elementId, startTag, endTag) => {
    var el = document.getElementById(elementId);
    if (!el) return;
    var start = el.selectionStart;
    var end = el.selectionEnd;
    var text = el.value;
    var before = text.substring(0, start);
    var selected = text.substring(start, end);
    var after = text.substring(end, text.length);

    // If no text selected, just insert the tags with cursor in between
    var newText = startTag + selected + endTag;

    el.value = before + newText + after;

    if (selected.length === 0) {
        el.selectionStart = el.selectionEnd = start + startTag.length;
    } else {
        el.selectionStart = start;
        el.selectionEnd = start + newText.length;
    }

    el.focus();
    el.dispatchEvent(new Event('input', { bubbles: true }));
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
    el.value = value;
    el.selectionStart = el.selectionEnd = cursorPosition;
    el.focus();
    el.dispatchEvent(new Event('input', { bubbles: true }));
};

window.getCursorXY = (elementId) => {
    const el = document.getElementById(elementId);
    if (!el) return { top: 0, left: 0 };

    const start = el.selectionStart;
    const text = el.value.substring(0, start);

    const shadow = document.createElement('div');
    const styles = window.getComputedStyle(el);
    
    // Mirror essential layout and font styles
    const properties = [
        'direction', 'boxSizing', 'width', 'height', 'overflowX', 'overflowY',
        'borderTopWidth', 'borderRightWidth', 'borderBottomWidth', 'borderLeftWidth',
        'paddingTop', 'paddingRight', 'paddingBottom', 'paddingLeft',
        'fontStyle', 'fontVariant', 'fontWeight', 'fontStretch', 'fontSize', 'lineHeight', 'fontFamily',
        'textAlign', 'textTransform', 'textIndent', 'textDecoration', 'letterSpacing', 'wordSpacing', 'tabSize'
    ];

    properties.forEach(prop => {
        shadow.style[prop] = styles[prop];
    });

    shadow.style.position = 'absolute';
    shadow.style.visibility = 'hidden'; // Hidden but in DOM
    shadow.style.whiteSpace = 'pre-wrap';
    shadow.style.wordWrap = 'break-word';
    shadow.style.top = '0';
    shadow.style.left = '-9999px';

    shadow.textContent = text;
    const span = document.createElement('span');
    span.textContent = '|'; // Use a standard character
    shadow.appendChild(span);

    document.body.appendChild(shadow);
    const rect = span.getBoundingClientRect();
    const elRect = el.getBoundingClientRect();
    
    // Coordinates relative to the textarea's TOP LEFT corner (including its own padding)
    // span.offsetTop is relative to the shadow div top, which starts at the same Y as textarea top
    const top = span.offsetTop - el.scrollTop;
    const left = span.offsetLeft - el.scrollLeft;

    document.body.removeChild(shadow);

    return { top, left };
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

// Window Focus Tracking
let _focusDotNetRef = null;
let _visHandler = null;
let _focusHandler = null;
let _blurHandler = null;
let _electronFocusUnsub = null;
let _isNativeActive = true;
let _refocusTimer = null;

function sendOutOfBandPresencePost(endpointPath) {
    if (!window._currentSubscriptionId) return;
    const url = endpointPath + '?subscriptionId=' + encodeURIComponent(window._currentSubscriptionId);
    try {
        fetch(url, {
            method: 'POST',
            keepalive: true,
            cache: 'no-store'
        }).catch(() => {});
    } catch (e) {
        if (navigator.sendBeacon) {
            try { navigator.sendBeacon(url); } catch (_) {}
        }
    }
}

window.isSpokesWindowFocused = () => {
    const isMobile = /Android|webOS|iPhone|iPad|iPod|BlackBerry|IEMobile|Opera Mini/i.test(navigator.userAgent);
    const isNative = typeof window.isCapacitorNative === 'function' && window.isCapacitorNative();
    const isDesktop = !!(window.SpokesBridge && window.SpokesBridge.isDesktop);

    if (isNative && !_isNativeActive) {
        return false;
    }
    if (isDesktop && window._spokesElectronWindowFocused === false) {
        return false;
    }
    return document.visibilityState === 'visible' && (isMobile || isNative || document.hasFocus());
};

window.registerWindowFocus = (dotNetRef) => {
    _focusDotNetRef = dotNetRef;
    _isNativeActive = true;
    let _lastFocusState = null;
    if (typeof window._spokesElectronWindowFocused === 'undefined') {
        window._spokesElectronWindowFocused = true;
    }

    const handleFocusInteropError = (err) => {
        const msg = String(err || '');
        if (msg.includes('No interop methods are registered for renderer') || msg.includes('There is no tracked object')) {
            window._blazorDisconnected = true;
            if (window.spokesResume && typeof window.spokesResume.onConnectionDown === 'function') {
                window.spokesResume.onConnectionDown('renderer-disposed');
            }
        }
    };

    const invokeFocusInterop = (isFocused) => {
        if (!_focusDotNetRef || window._blazorDisconnected) return;
        const hiddenDuration = window._hiddenAt ? Math.max(0, Date.now() - window._hiddenAt) : (window._lastHiddenDurationMs || 0);
        if (isFocused && hiddenDuration >= 180000) {
            window._blazorDisconnected = true;
            if (window.spokesResume && typeof window.spokesResume.onConnectionDown === 'function') {
                window.spokesResume.onConnectionDown('background-retention-expired');
            }
            return;
        }
        try {
            const p = _focusDotNetRef.invokeMethodAsync('OnWindowFocusChanged', isFocused);
            if (isFocused && window.spokesResume && typeof window.spokesResume.verifyCircuitAlive === 'function') {
                window.spokesResume.verifyCircuitAlive(p);
            }
            p.catch(handleFocusInteropError);
        } catch (e) {
            handleFocusInteropError(e);
        }
    };

    const updateState = (force = false) => {
        const isFocused = window.isSpokesWindowFocused();

        if (!isFocused) {
            if (_refocusTimer) {
                clearTimeout(_refocusTimer);
                _refocusTimer = null;
            }
            if (!force && _lastFocusState === false) return;
            _lastFocusState = false;

            sendOutOfBandPresencePost('/spokesapi/presence/unfocus');
            invokeFocusInterop(false);
            return;
        }

        if (!force && _lastFocusState === true) return;

        const applyFocused = () => {
            _refocusTimer = null;
            if (!window.isSpokesWindowFocused()) return;
            if (!force && _lastFocusState === true) return;
            _lastFocusState = true;

            invokeFocusInterop(true);
            if (typeof window._spokesReobserveReadReceipts === 'function') {
                try { window._spokesReobserveReadReceipts(); } catch(e) {}
            }
        };

        const isMobileOrNative = /Android|webOS|iPhone|iPad|iPod|BlackBerry|IEMobile|Opera Mini/i.test(navigator.userAgent) ||
            (typeof window.isCapacitorNative === 'function' && window.isCapacitorNative());

        if (_lastFocusState === false && isMobileOrNative && !force) {
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

    window._forceUpdateFocusState = () => updateState(true);

    _visHandler = () => updateState();
    _focusHandler = () => updateState();
    _blurHandler = () => updateState();

    document.addEventListener('visibilitychange', _visHandler);
    window.addEventListener('focus', _focusHandler);
    window.addEventListener('blur', _blurHandler);

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
                    _isNativeActive = isActive;
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
    if (_visHandler) document.removeEventListener('visibilitychange', _visHandler);
    if (_focusHandler) window.removeEventListener('focus', _focusHandler);
    if (_blurHandler) window.removeEventListener('blur', _blurHandler);
    if (_electronFocusUnsub) {
        try { _electronFocusUnsub(); } catch(e) {}
        _electronFocusUnsub = null;
    }
    _focusDotNetRef = null;
    _visHandler = null;
    _focusHandler = null;
    _blurHandler = null;
    
    if (window._capacitorAppStateListener && typeof window._capacitorAppStateListener.remove === 'function') {
        window._capacitorAppStateListener.remove();
        window._capacitorAppStateListener = null;
    }
};

// Explicit helper for Mobile/Desktop Server Switcher before unloading or hiding a server view
window.spokesNotifyServerSwitchAway = () => {
    sendOutOfBandPresencePost('/spokesapi/presence/offline');
};

// App Termination Teardown
window.addEventListener('pagehide', function(event) {
    sendOutOfBandPresencePost('/spokesapi/presence/offline');
});

// Unified Activity & Idle Tracker (5-minute timeout in Browser; OS-level in Electron Desktop)
const SPOKES_IDLE_TIMEOUT_MS = 5 * 60 * 1000;
let _activityTrackerDotNetRef = null;
let _activityTrackerLastPing = 0;
let _activityTrackerHandler = null;
let _browserIdleTimer = null;
let _desktopPresenceUnsub = null;
let _desktopActivePingInterval = null;

window.registerImageRecoveryCoordinator = (dotNetRef) => {
    window._imageRecoveryCoordinator = dotNetRef;
};

window.registerActivityTracker = (dotNetRef) => {
    window.unregisterActivityTracker();

    _activityTrackerDotNetRef = dotNetRef;
    _activityTrackerLastPing = Date.now();
    window._spokesClientIdle = false;

    const sendActivePing = () => {
        if (_activityTrackerDotNetRef && !window._blazorDisconnected) {
            try { _activityTrackerDotNetRef.invokeMethodAsync('OnDeviceActivePing').catch(()=>{}); } catch(e) {}
        }
    };

    const reportClientPresenceState = (state) => {
        if (_activityTrackerDotNetRef && !window._blazorDisconnected) {
            try { _activityTrackerDotNetRef.invokeMethodAsync('OnClientPresenceStateChanged', state).catch(()=>{}); } catch(e) {}
        }
        if (window._currentSubscriptionId) {
            try {
                fetch('/spokesapi/presence/state?subscriptionId=' + encodeURIComponent(window._currentSubscriptionId) + '&state=' + encodeURIComponent(state), {
                    method: 'POST',
                    keepalive: true
                }).catch(() => {});
            } catch(e) {}
        }
    };

    const isDesktopApp = !!(window.SpokesBridge && window.SpokesBridge.isDesktop);

    if (isDesktopApp) {
        const applyDesktopPresence = (status) => {
            if (!status) return;
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
        const resetBrowserIdleTimer = () => {
            if (_browserIdleTimer) clearTimeout(_browserIdleTimer);
            _browserIdleTimer = setTimeout(() => {
                window._spokesClientIdle = true;
                reportClientPresenceState('away');
            }, SPOKES_IDLE_TIMEOUT_MS);
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
    _activityTrackerHandler = null;
    _activityTrackerDotNetRef = null;
};


// --- Page-based viewer pagination ---
// IMPORTANT: Never mutate Blazor-managed DOM (no innerHTML, no removeChild on
// tracked nodes). Instead, hide the original paper and insert cloned pages as
// a sibling. Call cleanupPagination on component dispose to remove the sibling.
window.paginateSections = (paperId, availableHeight) => {
    const paper = document.getElementById(paperId);
    if (!paper) return;

    const printBody = paper.querySelector('.print-body');
    if (!printBody) return;

    // --- 1. Identify measurable blocks ---
    const blocks = [];
    let currentId = 0;

    function collectBlocks(node) {
        // If it's keep-together, it's atomic
        if (node.classList.contains('keep-together')) {
            node.setAttribute('data-pg-id', currentId++);
            blocks.push(node);
            return;
        }

        // List containers (OL, UL) inside an allow-break context should drill down
        // so individual list items (LI) can paginate across pages
        const isListContainer = /^(OL|UL)$/i.test(node.tagName);
        if (!node.classList.contains('allow-break') && !isListContainer) {
            node.setAttribute('data-pg-id', currentId++);
            blocks.push(node);
        } else {
            // It is allow-break or a list container: recursively drill down
            const children = Array.from(node.children);
            if (children.length === 0) {
                node.setAttribute('data-pg-id', currentId++);
                blocks.push(node);
            } else {
                children.forEach(child => collectBlocks(child));
            }
        }
    }

    Array.from(printBody.children).forEach(child => collectBlocks(child));

    if (blocks.length === 0) {
        return;
    }

    // --- 2. Measure every block ---
    const measurements = [];
    for (let i = 0; i < blocks.length; i++) {
        const block = blocks[i];
        const rect = block.getBoundingClientRect();
        const style = window.getComputedStyle(block);
        
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
        }

        // Calculate height of the "keep together" group starting here
        let groupHeight = m.height;
        let j = i;
        while (j + 1 < measurements.length) {
            if (measurements[j].keepWithNext) {
                j++;
                groupHeight += measurements[j].height;
                if (measurements[j].forceBreak) break;
            } else if (measurements[j].isEmpty || measurements[j].height < 15) {
                // Look ahead through empty spacer paragraphs following a keep-with-next element
                j++;
                groupHeight += measurements[j].height;
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
    paper.style.display = 'none';
    paper.insertAdjacentElement('afterend', container);
};

// Cleanup: removes the paginated sibling and restores the original paper.
// Must be called from component DisposeAsync to prevent stale DOM on navigation.
window.cleanupPagination = (paperId) => {
    const container = document.querySelector(`[data-paginated-for="${paperId}"]`);
    if (container) container.remove();
    const paper = document.getElementById(paperId);
    if (paper) paper.style.display = '';
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
    initRemoteVideo: function (elementId, dotNetRef, isLooping) {
        const v = document.getElementById(elementId);
        if (!v) return;
        v.muted = true;
        v.loop = Boolean(isLooping);

        const onLoaded = () => {
            if (dotNetRef && Number.isFinite(v.duration)) {
                dotNetRef.invokeMethodAsync('OnRemoteDurationKnown', v.duration);
            }
        };
        const onTime = () => {
            if (dotNetRef && Number.isFinite(v.currentTime)) {
                dotNetRef.invokeMethodAsync('OnRemoteTimeUpdate', v.currentTime);
            }
        };
        const onEnd = () => {
            if (!v.loop && dotNetRef) {
                dotNetRef.invokeMethodAsync('OnRemoteVideoEnded');
            }
        };

        v.addEventListener('loadedmetadata', onLoaded);
        v.addEventListener('timeupdate', onTime);
        v.addEventListener('ended', onEnd);

        if (Number.isFinite(v.duration) && v.duration > 0) {
            onLoaded();
        }

        v._cleanupVideo = () => {
            v.removeEventListener('loadedmetadata', onLoaded);
            v.removeEventListener('timeupdate', onTime);
            v.removeEventListener('ended', onEnd);
        };
    },
    cleanupRemoteVideo: function (elementId) {
        const v = document.getElementById(elementId);
        if (v && typeof v._cleanupVideo === 'function') {
            v._cleanupVideo();
        }
    },
    syncPlayPause: function (elementId, isPlaying) {
        const v = document.getElementById(elementId);
        if (!v) return;
        if (isPlaying) {
            v.play().catch(() => {});
        } else {
            v.pause();
        }
    },
    seekTo: function (elementId, timeSeconds) {
        const v = document.getElementById(elementId);
        if (!v || !Number.isFinite(timeSeconds)) return;
        if (Math.abs(v.currentTime - timeSeconds) > 0.25) {
            v.currentTime = timeSeconds;
        }
    },
    setLoop: function (elementId, isLooping) {
        const v = document.getElementById(elementId);
        if (v) v.loop = Boolean(isLooping);
    },
    syncTvVideo: function (isPlaying, currentTime, isMuted, isLooping) {
        const v = document.getElementById('stage-video');
        if (!v) return;
        v.muted = Boolean(isMuted);
        v.loop = Boolean(isLooping);
        if (Number.isFinite(currentTime) && currentTime >= 0 && Math.abs(v.currentTime - currentTime) > 0.35) {
            v.currentTime = currentTime;
        }
        if (isPlaying) {
            const p = v.play();
            if (p !== undefined) {
                p.catch(e => {
                    console.warn('[spokesSlideshowVideo] Play rejected in syncTvVideo', e);
                });
            }
        } else {
            v.pause();
        }
    },
    initTvVideo: function (elementId, dotNetRef, isMuted, isLooping) {
        const v = document.getElementById(elementId || 'stage-video');
        if (!v) return;
        if (v._cleanupTvVideo) {
            v._cleanupTvVideo();
        }

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
                dotNetRef.invokeMethodAsync('OnTvPlayStateChanged', !v.paused, v.currentTime).catch(() => {});
            }
        };

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
            if (!v.paused) return;
            const p = v.play();
            if (p !== undefined) {
                p.catch(e => {
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
        };
    },
    cleanupTvVideo: function (elementId) {
        const v = document.getElementById(elementId || 'stage-video');
        if (v && typeof v._cleanupTvVideo === 'function') {
            v._cleanupTvVideo();
        }
    }
};

