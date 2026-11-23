export function initFileHandlers(elementId, dotNetHelper, maxAllowedSize = 268435456) {
    window.spokesChatDiagnostics?.mark('Client: JS Chat.initFileHandlers');
    // We ignore elementId for the container now, and prefer our specific chat-drop-zone
    window.spokesUpload?.initDropZone('chat-drop-zone', dotNetHelper, maxAllowedSize);
}

// ─── SCROLL ANCHORING & POSITION PRESERVATION ──────────────────────────────────

let currentScrollAnchor = null;
let isLoadOlderInFlight = false;
let isViewportFillInFlight = false;
let viewportFillSteps = 0;
let activeChannelId = null;
const MAX_VIEWPORT_FILL_STEPS = 4;

function resolveElement(elOrId, fallbackId = 'chat-messages-container') {
    if (typeof elOrId === 'string') {
        const byId = document.getElementById(elOrId);
        return byId instanceof Element ? byId : null;
    }
    if (elOrId instanceof Element) {
        return elOrId;
    }
    if (fallbackId) {
        const fallback = document.getElementById(fallbackId);
        return fallback instanceof Element ? fallback : null;
    }
    return null;
}

export function captureScrollAnchor(containerOrId) {
    const container = resolveElement(containerOrId);
    if (!container) return;

    // Check if the container is using scaleY(-1) (e.g. mobile reverse scroll).
    // On scaleY(-1), appending elements to the end of the DOM already natively preserves scroll position.
    const style = window.getComputedStyle(container);
    if (style.transform && style.transform !== 'none' && style.transform.includes('-1')) {
        currentScrollAnchor = null;
        return;
    }

    const containerRect = container.getBoundingClientRect();
    const rows = container.querySelectorAll('.message-row[data-message-id]');

    // Find the first message element currently visible within the viewport
    for (const row of rows) {
        const rect = row.getBoundingClientRect();
        if (rect.bottom > containerRect.top && rect.top < containerRect.bottom) {
            currentScrollAnchor = {
                messageId: row.getAttribute('data-message-id'),
                offsetFromTop: rect.top - containerRect.top,
                element: row
            };
            return;
        }
    }
    currentScrollAnchor = null;
}

export function restoreScrollAnchor(containerOrId) {
    const container = resolveElement(containerOrId);
    if (!container || !currentScrollAnchor) {
        currentScrollAnchor = null;
        return;
    }

    const anchor = currentScrollAnchor;
    currentScrollAnchor = null;

    requestAnimationFrame(() => {
        let el = anchor.element;
        if (!el || !container.contains(el)) {
            el = container.querySelector(`.message-row[data-message-id="${anchor.messageId}"]`);
        }
        if (!el) return;

        const containerRect = container.getBoundingClientRect();
        const newRect = el.getBoundingClientRect();
        const currentOffsetFromTop = newRect.top - containerRect.top;
        const delta = currentOffsetFromTop - anchor.offsetFromTop;

        if (Math.abs(delta) > 0.5) {
            container.scrollTop += delta;
        }
    });
}

export function notifyOlderMessagesLoaded() {
    isLoadOlderInFlight = false;
    const sentinel = document.getElementById('chat-top-sentinel');
    if (currentObserver && sentinel instanceof Element) {
        currentObserver.observe(sentinel);
    }
}

// ─── SCROLL LOADING ─────────────────────────────────────────────────────────

let currentObserver = null;

export function initScrollObserver(containerOrId, sentinelOrId, dotNetHelper) {
    window.spokesChatDiagnostics?.mark('Client: JS Chat.initScrollObserver');
    const container = resolveElement(containerOrId, 'chat-messages-container');
    const sentinel = resolveElement(sentinelOrId, 'chat-top-sentinel');

    if (currentObserver) {
        currentObserver.disconnect();
        currentObserver = null;
    }

    if (!container || !sentinel) return;

    currentObserver = new IntersectionObserver((entries) => {
        if (window._blazorDisconnected) return;
        entries.forEach(entry => {
            if (entry.isIntersecting && !isLoadOlderInFlight && !isViewportFillInFlight) {
                // Guard: Only trigger lazy load if content actually overflows the container viewport.
                // Stage 2 viewport auto-fill (checkAndFillViewport) handles filling under-filled viewports.
                if (container.scrollHeight > container.clientHeight + 10) {
                    isLoadOlderInFlight = true;
                    dotNetHelper.invokeMethodAsync('LoadOlderMessagesJS').catch(() => {
                        isLoadOlderInFlight = false;
                    });
                }
            }
        });
    }, {
        root: container,
        threshold: 0.1
    });

    if (currentObserver && sentinel) {
        currentObserver.observe(sentinel);
    }
}

export async function scrollToBottomWhenLoaded(containerOrId) {
    window.spokesChatDiagnostics?.mark('Client: JS Chat.scrollToBottomWhenLoaded');
    const el = resolveElement(containerOrId);
    if (!el) return;

    // Because of transform: scaleY(-1), scrollTop 0 is always the visual bottom
    // universally across all browsers (including iOS Safari).
    el.scrollTop = 0;
    
    // Use requestAnimationFrame instead of arbitrary 100ms timeout so the reset
    // is synchronized with the browser's paint cycle without visual snapping.
    requestAnimationFrame(() => {
        if (el) el.scrollTop = 0;
    });
}

let currentlyHighlightedEl = null;

export function scrollToMessage(messageId) {
    const el = document.querySelector(`[data-message-id="${messageId}"]`);
    if (el) {
        el.scrollIntoView({ behavior: 'smooth', block: 'center' });
        
        if (currentlyHighlightedEl && currentlyHighlightedEl !== el) {
            currentlyHighlightedEl.style.backgroundColor = '';
            currentlyHighlightedEl.style.transition = '';
        }
        
        el.style.transition = 'background-color 0.3s';
        el.style.backgroundColor = 'rgba(var(--mud-palette-primary-rgb), 0.15)';
        currentlyHighlightedEl = el;
    }
}

// Clear the highlight when the user clicks on another message
document.addEventListener('click', (e) => {
    if (currentlyHighlightedEl) {
        const clickedMessage = e.target.closest('[data-message-id]');
        if (clickedMessage && clickedMessage !== currentlyHighlightedEl) {
            currentlyHighlightedEl.style.backgroundColor = '';
            currentlyHighlightedEl.style.transition = 'background-color 0.3s';
            currentlyHighlightedEl = null;
        }
    }
});

// ─── LONG PRESS (MOBILE) ────────────────────────────────────────────────────

const LONG_PRESS_DURATION_MS = 400;
const LONG_PRESS_HIGHLIGHT_DELAY_MS = 90;
const LONG_PRESS_MOVE_SLOP_PX = 10;
const LONG_PRESS_HANDOFF_TIMEOUT_MS = 500;
const LONG_PRESS_IGNORED_SELECTOR = 'input, textarea, select, [contenteditable="true"]';

let longPressHost = null;
let longPressDotNetHelper = null;
let longPressCleanup = null;
let longPressCancelActive = null;

function setRowLongPressing(row, isPressing) {
    if (!row) return;
    if (isPressing) {
        if (typeof row.setAttribute === 'function') {
            row.setAttribute('data-long-pressing', 'true');
        }
    } else if (typeof row.removeAttribute === 'function') {
        row.removeAttribute('data-long-pressing');
    }
}

function resolveClosestTarget(rawTarget) {
    const target = rawTarget && typeof rawTarget.closest === 'function'
        ? rawTarget
        : rawTarget?.parentElement;
    return target && typeof target.closest === 'function' ? target : null;
}

function disposeLongPress() {
    if (longPressCancelActive) {
        longPressCancelActive(true);
        longPressCancelActive = null;
    }
    if (longPressCleanup) {
        longPressCleanup();
        longPressCleanup = null;
    }
    longPressHost = null;
    longPressDotNetHelper = null;
}

export function initLongPress(containerOrId, dotNetHelper) {
    window.spokesChatDiagnostics?.mark('Client: JS Chat.initLongPress');
    const container = resolveElement(containerOrId);
    if (!container) return;

    // On channel switches the same #chat-messages-container element is reused: refresh the
    // dotNetHelper reference and clear any in-flight press state without duplicating listeners.
    if (longPressHost === container) {
        longPressDotNetHelper = dotNetHelper;
        if (longPressCancelActive) {
            longPressCancelActive(true);
        }
        return;
    }

    disposeLongPress();
    longPressHost = container;
    longPressDotNetHelper = dotNetHelper;

    let highlightTimer = null;
    let longPressTimer = null;
    let handoffTimer = null;
    let handoffRafId = null;
    let preventClickHandler = null;
    let preventClickTimer = null;
    let pressingRow = null;
    let startX = null;
    let startY = null;
    let moved = false;
    let triggered = false;
    let lastInteractionWasTouch = false;

    const clearPreventClick = () => {
        if (preventClickTimer) {
            clearTimeout(preventClickTimer);
            preventClickTimer = null;
        }
        if (preventClickHandler) {
            document.removeEventListener('click', preventClickHandler, true);
            preventClickHandler = null;
        }
    };

    const clearPendingTimers = () => {
        if (highlightTimer) {
            clearTimeout(highlightTimer);
            highlightTimer = null;
        }
        if (longPressTimer) {
            clearTimeout(longPressTimer);
            longPressTimer = null;
        }
        if (handoffTimer) {
            clearTimeout(handoffTimer);
            handoffTimer = null;
        }
        if (handoffRafId !== null && typeof cancelAnimationFrame === 'function') {
            cancelAnimationFrame(handoffRafId);
            handoffRafId = null;
        }
    };

    const clearActivePress = (forceClearTriggered = false) => {
        if (triggered && !forceClearTriggered) {
            // Long-press already fired and handed off to Blazor; retain data-long-pressing
            // until OnMobileLongPress settles (or the safety cap expires) so lifting the finger
            // does not blink before Blazor applies .message-row-action-target.
            if (highlightTimer) {
                clearTimeout(highlightTimer);
                highlightTimer = null;
            }
            if (longPressTimer) {
                clearTimeout(longPressTimer);
                longPressTimer = null;
            }
            return;
        }
        clearPendingTimers();
        if (pressingRow) {
            setRowLongPressing(pressingRow, false);
            pressingRow = null;
        }
        startX = null;
        startY = null;
        moved = false;
        triggered = false;
    };

    const scheduleHandoffClear = (row, settledSuccessfully) => {
        const finalize = () => {
            if (pressingRow === row) {
                clearActivePress(true);
            } else {
                setRowLongPressing(row, false);
            }
        };

        if (!settledSuccessfully) {
            finalize();
            return;
        }

        if (row.classList && typeof row.classList.contains === 'function' && row.classList.contains('message-row-action-target')) {
            finalize();
            return;
        }

        if (typeof requestAnimationFrame === 'function') {
            handoffRafId = requestAnimationFrame(() => {
                handoffRafId = null;
                finalize();
            });
        } else {
            finalize();
        }
    };

    const onTouchStart = (e) => {
        lastInteractionWasTouch = true;
        clearActivePress(true);

        if (e.touches && e.touches.length !== 1) return;
        const target = resolveClosestTarget(e.target);
        if (!target || target.closest(LONG_PRESS_IGNORED_SELECTOR)) return;

        const row = target.closest('.message-row[data-message-id]');
        if (!row || !container.contains(row)) return;

        const msgId = row.getAttribute('data-message-id');
        if (!msgId) return;

        pressingRow = row;
        moved = false;
        triggered = false;
        const touch = e.touches && e.touches[0];
        startX = touch && typeof touch.clientX === 'number' ? touch.clientX : null;
        startY = touch && typeof touch.clientY === 'number' ? touch.clientY : null;

        highlightTimer = setTimeout(() => {
            highlightTimer = null;
            if (!moved && pressingRow === row && row.isConnected && !window._blazorDisconnected) {
                setRowLongPressing(row, true);
            }
        }, LONG_PRESS_HIGHLIGHT_DELAY_MS);

        longPressTimer = setTimeout(() => {
            longPressTimer = null;
            if (highlightTimer) {
                clearTimeout(highlightTimer);
                highlightTimer = null;
            }

            if (moved || pressingRow !== row || !row.isConnected || window._blazorDisconnected || !longPressDotNetHelper) {
                clearActivePress(true);
                return;
            }

            triggered = true;
            setRowLongPressing(row, true);
            window.getSelection()?.removeAllRanges();
            if (window.triggerHaptic) { window.triggerHaptic('LIGHT'); } else { try { navigator.vibrate?.(30); } catch (_) { } }

            // Safety cap guarantees a stuck highlight can never persist if the circuit stalls or rAF is throttled.
            handoffTimer = setTimeout(() => {
                handoffTimer = null;
                if (pressingRow === row) {
                    clearActivePress(true);
                } else {
                    setRowLongPressing(row, false);
                }
            }, LONG_PRESS_HANDOFF_TIMEOUT_MS);

            const invokePromise = longPressDotNetHelper.invokeMethodAsync('OnMobileLongPress', msgId);
            if (invokePromise && typeof invokePromise.then === 'function') {
                invokePromise.then(
                    () => scheduleHandoffClear(row, true),
                    () => scheduleHandoffClear(row, false)
                );
            } else {
                scheduleHandoffClear(row, true);
            }

            clearPreventClick();
            preventClickHandler = (ev) => {
                ev.stopPropagation();
                ev.preventDefault();
                clearPreventClick();
            };
            document.addEventListener('click', preventClickHandler, true);
            preventClickTimer = setTimeout(clearPreventClick, 600);
        }, LONG_PRESS_DURATION_MS);
    };

    const onMouseDown = () => {
        lastInteractionWasTouch = false;
    };

    const onTouchMove = (e) => {
        if (!pressingRow || triggered) return;
        if (e.touches && e.touches.length !== 1) {
            moved = true;
            clearActivePress(true);
            return;
        }
        const touch = e.touches && e.touches[0];
        if (startX !== null && startY !== null && touch && typeof touch.clientX === 'number' && typeof touch.clientY === 'number') {
            if (Math.hypot(touch.clientX - startX, touch.clientY - startY) <= LONG_PRESS_MOVE_SLOP_PX) {
                return;
            }
        }
        moved = true;
        clearActivePress(true);
    };

    const onTouchEnd = () => {
        clearActivePress(false);
    };

    const onTouchCancel = () => {
        clearActivePress(true);
    };

    const onScroll = () => {
        if (!triggered) {
            moved = true;
            clearActivePress(true);
        }
    };

    const onContextMenu = (e) => {
        const target = resolveClosestTarget(e.target);
        if (lastInteractionWasTouch && target && target.closest('.message-row[data-message-id]')) {
            e.preventDefault();
        }
    };

    const onWindowBlur = () => {
        clearActivePress(true);
    };

    const onVisibilityChange = () => {
        if (document.hidden) {
            clearActivePress(true);
        }
    };

    container.addEventListener('touchstart', onTouchStart, { passive: true });
    container.addEventListener('mousedown', onMouseDown, { passive: true });
    container.addEventListener('touchmove', onTouchMove, { passive: true });
    container.addEventListener('touchend', onTouchEnd, { passive: true });
    container.addEventListener('touchcancel', onTouchCancel, { passive: true });
    container.addEventListener('scroll', onScroll, { passive: true });
    container.addEventListener('contextmenu', onContextMenu);
    window.addEventListener('blur', onWindowBlur);
    document.addEventListener('visibilitychange', onVisibilityChange);

    longPressCancelActive = clearActivePress;
    longPressCleanup = () => {
        clearPreventClick();
        container.removeEventListener('touchstart', onTouchStart);
        container.removeEventListener('mousedown', onMouseDown);
        container.removeEventListener('touchmove', onTouchMove);
        container.removeEventListener('touchend', onTouchEnd);
        container.removeEventListener('touchcancel', onTouchCancel);
        container.removeEventListener('scroll', onScroll);
        container.removeEventListener('contextmenu', onContextMenu);
        window.removeEventListener('blur', onWindowBlur);
        document.removeEventListener('visibilitychange', onVisibilityChange);
    };
}



// ─── READ RECEIPTS (INTERSECTION OBSERVER) ──────────────────────────────────

let readReceiptObserver = null;
let readReceiptMutationObserver = null;
let readReceiptFocusHandler = null;
let readReceiptTimeoutId = null;
let pendingSeenIds = new Set();
let pendingSeenFlushTimer = null;
let pendingSeenChannelId = null;
const READ_RECEIPT_COALESCE_MS = 350;

function clearPendingSeenBatch() {
    if (pendingSeenFlushTimer) {
        clearTimeout(pendingSeenFlushTimer);
        pendingSeenFlushTimer = null;
    }
    pendingSeenIds.clear();
    pendingSeenChannelId = null;
}

function isWindowActivelyViewed() {
    if (window._spokesClientIdle === true) return false;
    return typeof window.isSpokesWindowFocused === 'function'
        ? window.isSpokesWindowFocused()
        : (document.visibilityState === 'visible' && document.hasFocus());
}

export function initReadReceiptObserver(containerOrId, dotNetHelper) {
    window.spokesChatDiagnostics?.mark('Client: JS Chat.initReadReceiptObserver');
    clearPendingSeenBatch();
    if (readReceiptObserver) {
        readReceiptObserver.disconnect();
    }
    if (readReceiptMutationObserver) {
        readReceiptMutationObserver.disconnect();
    }
    if (readReceiptFocusHandler) {
        if (window._spokesReobserveReadReceipts === readReceiptFocusHandler) {
            delete window._spokesReobserveReadReceipts;
        }
        window.removeEventListener('focus', readReceiptFocusHandler);
        document.removeEventListener('visibilitychange', readReceiptFocusHandler);
        readReceiptFocusHandler = null;
    }

    const container = resolveElement(containerOrId);
    if (!container) return;

    const boundChannelId = activeChannelId || container.dataset?.channelId || null;

    const flushPendingSeen = () => {
        pendingSeenFlushTimer = null;
        if (pendingSeenIds.size === 0 || window._blazorDisconnected) {
            pendingSeenIds.clear();
            return;
        }
        if (boundChannelId && activeChannelId && activeChannelId !== boundChannelId) {
            pendingSeenIds.clear();
            return;
        }
        const batch = Array.from(pendingSeenIds);
        pendingSeenIds.clear();
        dotNetHelper.invokeMethodAsync('HandleMessagesSeen', batch)
            .catch(err => console.warn("HandleMessagesSeen failed", err));
    };

    readReceiptObserver = new IntersectionObserver((entries) => {
        if (!isWindowActivelyViewed() || window._blazorDisconnected) return;

        let addedAny = false;
        entries.forEach(entry => {
            if (entry.isIntersecting) {
                const msgId = entry.target.getAttribute('data-message-id');
                if (msgId) {
                    pendingSeenIds.add(msgId);
                    addedAny = true;

                    // Unobserve once seen while actively viewed to preserve performance
                    readReceiptObserver.unobserve(entry.target);
                }
            }
        });

        if (addedAny && !window._blazorDisconnected) {
            pendingSeenChannelId = boundChannelId;
            if (pendingSeenFlushTimer) {
                clearTimeout(pendingSeenFlushTimer);
            }
            pendingSeenFlushTimer = setTimeout(flushPendingSeen, READ_RECEIPT_COALESCE_MS);
        }
    }, {
        root: container,
        threshold: 0.5 // Message must be half-visible to count as read
    });

    const observeAllMessages = () => {
        if (!readReceiptObserver || !container) return;
        const messages = container.querySelectorAll('.message-row[data-message-id]');
        messages.forEach(msg => {
            if (msg.dataset.messageId) {
                readReceiptObserver.observe(msg);
            }
        });
    };

    readReceiptFocusHandler = () => {
        if (isWindowActivelyViewed()) {
            observeAllMessages();
        }
    };
    window._spokesReobserveReadReceipts = readReceiptFocusHandler;
    window.addEventListener('focus', readReceiptFocusHandler);
    document.addEventListener('visibilitychange', readReceiptFocusHandler);

    // Short delay to allow layout to settle before observing
    if (readReceiptTimeoutId) {
        clearTimeout(readReceiptTimeoutId);
        readReceiptTimeoutId = null;
    }
    readReceiptTimeoutId = setTimeout(() => {
        readReceiptTimeoutId = null;
        if (!readReceiptObserver) return;

        observeAllMessages();

        // Robust addition: MutationObserver to catch dynamically added messages
        readReceiptMutationObserver = new MutationObserver((mutations) => {
            if (!readReceiptObserver) return;
            mutations.forEach(mutation => {
                mutation.addedNodes.forEach(node => {
                    if (node.nodeType === 1) { // ELEMENT_NODE
                        // If the added node itself is a message
                        if (node.classList && node.classList.contains('message-row') && node.dataset && node.dataset.messageId) {
                            readReceiptObserver.observe(node);
                        } else if (node.querySelectorAll) {
                            // If the added node contains messages
                            const newMsgs = node.querySelectorAll('.message-row[data-message-id]');
                            newMsgs.forEach(msg => {
                                if (msg.dataset && msg.dataset.messageId) {
                                    readReceiptObserver.observe(msg);
                                }
                            });
                        }
                    }
                });
            });
        });

        readReceiptMutationObserver.observe(container, {
            childList: true,
            subtree: true
        });
    }, 150);
}

// ─── SCROLL UP DETECTION ────────────────────────────────────────────────────

export function initScrollListener(containerOrId, dotNetHelper) {
    window.spokesChatDiagnostics?.mark('Client: JS Chat.initScrollListener');
    const container = resolveElement(containerOrId);
    if (!container) return;

    if (container._scrollListenerInit) return;
    container._scrollListenerInit = true;

    let isCurrentlyScrolledUp = false;
    let scrollTimer = null;

    container.addEventListener('scroll', (e) => {
        if (scrollTimer) clearTimeout(scrollTimer);
        
        scrollTimer = setTimeout(() => {
            if (window._blazorDisconnected || !container.isConnected) return;
            const scrollAmount = Math.abs(container.scrollTop);
            const isScrolledUp = scrollAmount > 200; // 200px threshold
            
            if (isScrolledUp !== isCurrentlyScrolledUp) {
                isCurrentlyScrolledUp = isScrolledUp;
                dotNetHelper.invokeMethodAsync('OnScrollPositionChanged', isScrolledUp).catch(err => {
                    console.warn("OnScrollPositionChanged failed", err);
                });
            }
        }, 100); // 100ms throttle/debounce
    }, { passive: true });
}

function scheduleViewportFill(containerOrId, dotNetHelper, expectedChannelId) {
    if (!expectedChannelId || activeChannelId !== expectedChannelId) return;
    if (viewportFillSteps >= MAX_VIEWPORT_FILL_STEPS || isViewportFillInFlight || isLoadOlderInFlight) return;
    if (window._blazorDisconnected) return;

    requestAnimationFrame(() => {
        if (activeChannelId !== expectedChannelId || window._blazorDisconnected || isViewportFillInFlight || isLoadOlderInFlight) {
            return;
        }
        const container = resolveElement(containerOrId);
        if (!container || !container.isConnected || container.clientHeight <= 0) {
            return;
        }
        if (container.scrollHeight >= container.clientHeight * 1.4) {
            return;
        }

        isViewportFillInFlight = true;
        viewportFillSteps++;
        const fillStep = viewportFillSteps;
        const fillStart = performance.now();
        window.spokesChatDiagnostics?.mark(`Client: Stage2 ViewportFill #${fillStep} Start`);
        dotNetHelper.invokeMethodAsync('FillViewportMessagesJS', expectedChannelId)
            .then((stillHasMore) => {
                isViewportFillInFlight = false;
                const durMs = Math.round(performance.now() - fillStart);
                window.spokesChatDiagnostics?.mark(`Client: Stage2 ViewportFill #${fillStep} Done (${durMs}ms, hasMore=${Boolean(stillHasMore)})`);
                window.spokesPerf?.recordViewportFill?.({
                    channelId: expectedChannelId,
                    step: fillStep,
                    durationMs: durMs,
                    hasMore: Boolean(stillHasMore)
                });
                if (stillHasMore && activeChannelId === expectedChannelId && viewportFillSteps < MAX_VIEWPORT_FILL_STEPS) {
                    scheduleViewportFill(containerOrId, dotNetHelper, expectedChannelId);
                }
            })
            .catch(() => {
                isViewportFillInFlight = false;
            });
    });
}

let sharedDesktopActionsEl = null;
let sharedDesktopActionsTemplateKey = null;
let delegatedActionsHost = null;
let delegatedActionsDotNetHelper = null;
let delegatedActionsCleanup = null;
let sidebarKeyboardHost = null;
let sidebarKeydownListener = null;

function detachSharedDesktopActions() {
    if (sharedDesktopActionsEl && sharedDesktopActionsEl.parentNode) {
        sharedDesktopActionsEl.parentNode.removeChild(sharedDesktopActionsEl);
    }
}

function disposeMessageDelegatedHandlers() {
    if (delegatedActionsCleanup) {
        delegatedActionsCleanup();
        delegatedActionsCleanup = null;
    }
    delegatedActionsHost = null;
    delegatedActionsDotNetHelper = null;
}

function ensureSharedDesktopActionsEl() {
    const template = document.getElementById('spokes-desktop-message-actions-template');
    if (!template) {
        detachSharedDesktopActions();
        sharedDesktopActionsEl = null;
        sharedDesktopActionsTemplateKey = null;
        return null;
    }

    const key = template.getAttribute('data-template-key') || '';
    if (!sharedDesktopActionsEl || sharedDesktopActionsTemplateKey !== key) {
        detachSharedDesktopActions();
        const sourceRoot = template.content?.firstElementChild || template.firstElementChild;
        sharedDesktopActionsEl = sourceRoot ? sourceRoot.cloneNode(true) : null;
        sharedDesktopActionsTemplateKey = key;
    }
    return sharedDesktopActionsEl;
}

function attachSharedDesktopActionsToRow(row) {
    if (!row) return;
    // Mount exclusively into the idle JS-owned slot so Blazor popup transitions atomically
    // replace the slot without child-index collisions or hover toolbar flicker.
    const slot = row.querySelector('.message-actions-slot');
    if (!slot) return;

    const actionsEl = ensureSharedDesktopActionsEl();
    if (!actionsEl) return;
    if (actionsEl.parentNode !== slot) {
        slot.appendChild(actionsEl);
    }
}

function initSidebarKeyboardDelegation() {
    const host = document.querySelector('.chat-page-container');
    if (!host || host === sidebarKeyboardHost) return;
    if (sidebarKeyboardHost && sidebarKeydownListener) {
        sidebarKeyboardHost.removeEventListener('keydown', sidebarKeydownListener);
    }
    sidebarKeydownListener = (e) => {
        if (e.key !== 'Enter' && e.key !== ' ') return;
        const target = e.target;
        if (!target || typeof target.matches !== 'function') return;
        if (!target.matches('.mud-list-item-clickable[role="button"]')) return;
        e.preventDefault();
        target.click();
    };
    host.addEventListener('keydown', sidebarKeydownListener);
    sidebarKeyboardHost = host;
}

function initMessageDelegatedHandlers(containerOrId, dotNetHelper) {
    const container = resolveElement(containerOrId);
    if (!container) return;

    if (delegatedActionsHost === container) {
        delegatedActionsDotNetHelper = dotNetHelper;
        return;
    }

    disposeMessageDelegatedHandlers();
    delegatedActionsHost = container;
    delegatedActionsDotNetHelper = dotNetHelper;

    const handleRowHoverOrFocus = (e) => {
        const target = resolveClosestTarget(e.target);
        if (!target) return;
        const row = target.closest('.message-row[data-message-id]');
        if (!row || !container.contains(row)) return;
        attachSharedDesktopActionsToRow(row);
    };

    const handleContainerMouseLeave = () => {
        detachSharedDesktopActions();
    };

    const handleContainerClick = (e) => {
        const target = resolveClosestTarget(e.target);
        if (!target) return;

        const actionBtn = target.closest('.message-actions button[data-msg-action]');
        if (!actionBtn || window._blazorDisconnected || !delegatedActionsDotNetHelper) return;

        const row = actionBtn.closest('.message-row[data-message-id]');
        const messageId = row?.getAttribute('data-message-id');
        const action = actionBtn.getAttribute('data-msg-action');
        if (!messageId || !action) return;

        const emoji = actionBtn.getAttribute('data-emoji') || null;
        const invokePromise = delegatedActionsDotNetHelper.invokeMethodAsync('HandleDesktopMessageActionJS', messageId, action, emoji);
        if (invokePromise && typeof invokePromise.catch === 'function') {
            invokePromise.catch(() => {});
        }
    };

    container.addEventListener('pointerover', handleRowHoverOrFocus, { passive: true });
    container.addEventListener('mouseover', handleRowHoverOrFocus, { passive: true });
    container.addEventListener('focusin', handleRowHoverOrFocus, { passive: true });
    container.addEventListener('mouseleave', handleContainerMouseLeave, { passive: true });
    container.addEventListener('click', handleContainerClick);

    delegatedActionsCleanup = () => {
        container.removeEventListener('pointerover', handleRowHoverOrFocus);
        container.removeEventListener('mouseover', handleRowHoverOrFocus);
        container.removeEventListener('focusin', handleRowHoverOrFocus);
        container.removeEventListener('mouseleave', handleContainerMouseLeave);
        container.removeEventListener('click', handleContainerClick);
    };
}

export function initChatChannelView(containerOrId, sentinelOrId, dotNetHelper, maxAllowedSize, hasMoreMessages, channelId = null) {
    window.spokesChatDiagnostics?.mark('Client: JS Chat.initChatChannelView');
    detachSharedDesktopActions();
    isLoadOlderInFlight = false;
    isViewportFillInFlight = false;
    viewportFillSteps = 0;
    activeChannelId = channelId || (resolveElement(containerOrId)?.dataset?.channelId ?? null);
    window.spokesCopyMessageCode = (messageId, codeBlockIndex) => {
        if (!dotNetHelper || window._blazorDisconnected) return Promise.resolve(null);
        return dotNetHelper.invokeMethodAsync('CopyMessageCodeBlockJS', String(messageId), Number(codeBlockIndex) || 0);
    };
    if (window.spokesMediaLoader && typeof window.spokesMediaLoader.cancelDetached === 'function') {
        window.spokesMediaLoader.cancelDetached();
    }
    initSidebarKeyboardDelegation();
    scrollToBottomWhenLoaded(containerOrId);
    initFileHandlers('chat-messages-container', dotNetHelper, maxAllowedSize);
    initMessageDelegatedHandlers(containerOrId, dotNetHelper);
    initLongPress(containerOrId, dotNetHelper);
    initScrollListener(containerOrId, dotNetHelper);
    if (hasMoreMessages) {
        initScrollObserver(containerOrId, sentinelOrId, dotNetHelper);
        if (activeChannelId) {
            scheduleViewportFill(containerOrId, dotNetHelper, activeChannelId);
        }
    }
    initReadReceiptObserver(containerOrId, dotNetHelper);
}

export function dispose() {
    disposeLongPress();
    disposeMessageDelegatedHandlers();
    detachSharedDesktopActions();
    sharedDesktopActionsEl = null;
    sharedDesktopActionsTemplateKey = null;
    if (sidebarKeyboardHost && sidebarKeydownListener) {
        sidebarKeyboardHost.removeEventListener('keydown', sidebarKeydownListener);
        sidebarKeyboardHost = null;
        sidebarKeydownListener = null;
    }
    clearPendingSeenBatch();
    isLoadOlderInFlight = false;
    isViewportFillInFlight = false;
    viewportFillSteps = 0;
    activeChannelId = null;
    currentScrollAnchor = null;
    if (window.spokesCopyMessageCode) {
        delete window.spokesCopyMessageCode;
    }
    if (readReceiptObserver) {
        readReceiptObserver.disconnect();
        readReceiptObserver = null;
    }
    if (readReceiptMutationObserver) {
        readReceiptMutationObserver.disconnect();
        readReceiptMutationObserver = null;
    }
    if (readReceiptFocusHandler) {
        if (window._spokesReobserveReadReceipts === readReceiptFocusHandler) {
            delete window._spokesReobserveReadReceipts;
        }
        window.removeEventListener('focus', readReceiptFocusHandler);
        document.removeEventListener('visibilitychange', readReceiptFocusHandler);
        readReceiptFocusHandler = null;
    }
    if (readReceiptTimeoutId) {
        clearTimeout(readReceiptTimeoutId);
        readReceiptTimeoutId = null;
    }
    if (currentObserver) {
        currentObserver.disconnect();
        currentObserver = null;
    }
}
