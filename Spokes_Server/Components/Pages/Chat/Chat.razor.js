export function initFileHandlers(elementId, dotNetHelper, maxAllowedSize = 268435456) {
    window.spokesChatDiagnostics?.mark('Client: JS Chat.initFileHandlers');
    // We ignore elementId for the container now, and prefer our specific chat-drop-zone
    const container = document.getElementById('chat-drop-zone');

    if (!container) return;

    container._maxAllowedSize = maxAllowedSize;

    if (container._spokesFileHandlersInit) return;
    container._spokesFileHandlersInit = true;

    let dragCounter = 0;

    container.addEventListener('dragenter', (e) => {
        e.preventDefault();
        dragCounter++;
        dotNetHelper.invokeMethodAsync('SetDraggingState', true);
    });

    container.addEventListener('dragover', (e) => {
        e.preventDefault();
    });

    container.addEventListener('dragleave', (e) => {
        e.preventDefault();
        dragCounter--;
        if (dragCounter <= 0) {
            dragCounter = 0;
            dotNetHelper.invokeMethodAsync('SetDraggingState', false);
        }
    });

    container.addEventListener('drop', (e) => {
        e.preventDefault();
        dragCounter = 0;
        dotNetHelper.invokeMethodAsync('SetDraggingState', false);

        const files = e.dataTransfer.files;
        if (files && files.length > 0) {
            handleFiles(files, dotNetHelper, container._maxAllowedSize || maxAllowedSize);
        }
    });
}

async function handleFiles(fileList, dotNetHelper, maxAllowedSize = 268435456) {
    const uploadUrl = await dotNetHelper.invokeMethodAsync('GetUploadUrl');
    if (!uploadUrl) return;

    if (fileList.length === 0) return;

    for (const file of fileList) {
        if (file.size > maxAllowedSize) {
            const limitMb = Math.round(maxAllowedSize / 1024 / 1024);
            const errorMsg = fileList.length > 1
                ? `File "${file.name}" exceeds the allowed limit of ${limitMb}MB`
                : `Total file size exceeds the allowed limit of ${limitMb}MB`;
            dotNetHelper.invokeMethodAsync('OnPasteUploadError', errorMsg);
            return;
        }
    }

    for (const file of fileList) {
        
        await dotNetHelper.invokeMethodAsync('OnPasteUploadStarted');
        try {
            const proxyDotNetRef = {
                invokeMethodAsync: async (methodName, ...args) => {
                    if (methodName === 'OnProgressUpdateCallback') {
                        return dotNetHelper.invokeMethodAsync('OnPasteProgressUpdate', ...args);
                    }
                }
            };

            const result = await window.spokesUpload.uploadFileObject(file, uploadUrl, proxyDotNetRef);
            let jsonStr = typeof result === 'string' ? result : JSON.stringify(result);
            await dotNetHelper.invokeMethodAsync('OnPasteUploadCompleted', jsonStr);
        } catch (error) {
            console.error("Error uploading dragged file:", error);
            await dotNetHelper.invokeMethodAsync('OnPasteUploadError', error.message || "Upload failed");
        }
    }
}

// ─── SCROLL ANCHORING & POSITION PRESERVATION ──────────────────────────────────

let currentScrollAnchor = null;
let isLoadOlderInFlight = false;

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
let observerTimeoutId = null;

export function initScrollObserver(containerOrId, sentinelOrId, dotNetHelper) {
    window.spokesChatDiagnostics?.mark('Client: JS Chat.initScrollObserver');
    const container = resolveElement(containerOrId, 'chat-messages-container');
    const sentinel = resolveElement(sentinelOrId, 'chat-top-sentinel');

    if (observerTimeoutId) {
        clearTimeout(observerTimeoutId);
        observerTimeoutId = null;
    }

    if (currentObserver) {
        currentObserver.disconnect();
        currentObserver = null;
    }

    if (!container || !sentinel) return;

    currentObserver = new IntersectionObserver((entries) => {
        if (window._blazorDisconnected) return;
        entries.forEach(entry => {
            if (entry.isIntersecting && !isLoadOlderInFlight) {
                // Guard: Only trigger lazy load if content actually overflows the container viewport.
                // If container.scrollHeight <= container.clientHeight, the sentinel is visible simply because
                // there aren't enough messages to fill the screen, not because the user scrolled to the top.
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

export function initLongPress(containerOrId, dotNetHelper) {
    window.spokesChatDiagnostics?.mark('Client: JS Chat.initLongPress');
    const container = resolveElement(containerOrId);
    if (!container) return;

    if (container._longPressInit) return;
    container._longPressInit = true;

    let timer = null;
    let moved = false;
    let lastInteractionWasTouch = false;

    container.addEventListener('touchstart', (e) => {
        lastInteractionWasTouch = true;
        const row = e.target.closest('[data-message-id]');
        if (!row) return;

        moved = false;
        const msgId = row.getAttribute('data-message-id');

        timer = setTimeout(() => {
            if (!moved && !window._blazorDisconnected) {
                window.getSelection()?.removeAllRanges();
                if (window.triggerHaptic) { window.triggerHaptic('LIGHT'); } else { try { navigator.vibrate?.(30); } catch (_) { } }
                dotNetHelper.invokeMethodAsync('OnMobileLongPress', msgId);

                const preventClick = (ev) => {
                    ev.stopPropagation();
                    ev.preventDefault();
                    document.removeEventListener('click', preventClick, true);
                };
                document.addEventListener('click', preventClick, true);
                setTimeout(() => document.removeEventListener('click', preventClick, true), 600);
            }
        }, 400);
    }, { passive: true });

    container.addEventListener('mousedown', (e) => {
        lastInteractionWasTouch = false;
    }, { passive: true });

    container.addEventListener('touchmove', () => {
        moved = true;
        if (timer) { clearTimeout(timer); timer = null; }
    }, { passive: true });

    container.addEventListener('touchend', () => {
        if (timer) { clearTimeout(timer); timer = null; }
    }, { passive: true });

    container.addEventListener('touchcancel', () => {
        if (timer) { clearTimeout(timer); timer = null; }
    }, { passive: true });

    container.addEventListener('contextmenu', (e) => {
        if (lastInteractionWasTouch && e.target.closest('[data-message-id]')) {
            e.preventDefault();
        }
    });
}



// ─── READ RECEIPTS (INTERSECTION OBSERVER) ──────────────────────────────────

let readReceiptObserver = null;
let readReceiptMutationObserver = null;
let readReceiptFocusHandler = null;

function isWindowActivelyViewed() {
    if (document.visibilityState !== 'visible') return false;
    if (window._spokesClientIdle === true) return false;
    if (window._spokesElectronWindowFocused === false) return false;
    const isMobile = /Android|webOS|iPhone|iPad|iPod|BlackBerry|IEMobile|Opera Mini/i.test(navigator.userAgent);
    const isNative = typeof window.isCapacitorNative === 'function' && window.isCapacitorNative();
    return isMobile || isNative || document.hasFocus();
}

export function initReadReceiptObserver(containerOrId, dotNetHelper) {
    window.spokesChatDiagnostics?.mark('Client: JS Chat.initReadReceiptObserver');
    if (readReceiptObserver) {
        readReceiptObserver.disconnect();
    }
    if (readReceiptMutationObserver) {
        readReceiptMutationObserver.disconnect();
    }
    if (readReceiptFocusHandler) {
        window.removeEventListener('focus', readReceiptFocusHandler);
        document.removeEventListener('visibilitychange', readReceiptFocusHandler);
        readReceiptFocusHandler = null;
    }

    const container = resolveElement(containerOrId);
    if (!container) return;

    readReceiptObserver = new IntersectionObserver((entries) => {
        if (!isWindowActivelyViewed() || window._blazorDisconnected) return;

        const seenIds = [];
        entries.forEach(entry => {
            if (entry.isIntersecting) {
                const msgId = entry.target.getAttribute('data-message-id');
                if (msgId) {
                    seenIds.push(msgId);

                    // Unobserve once seen while actively viewed to preserve performance
                    readReceiptObserver.unobserve(entry.target);
                }
            }
        });

        if (seenIds.length > 0 && !window._blazorDisconnected) {
            dotNetHelper.invokeMethodAsync('HandleMessagesSeen', seenIds)
                .catch(err => console.warn("HandleMessagesSeen failed", err));
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
    window.addEventListener('focus', readReceiptFocusHandler);
    document.addEventListener('visibilitychange', readReceiptFocusHandler);

    // Short delay to allow layout to settle before observing
    setTimeout(() => {
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
            if (window._blazorDisconnected) return;
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

export function initChatChannelView(containerOrId, sentinelOrId, dotNetHelper, maxAllowedSize, hasMoreMessages) {
    window.spokesChatDiagnostics?.mark('Client: JS Chat.initChatChannelView');
    scrollToBottomWhenLoaded(containerOrId);
    initFileHandlers('chat-messages-container', dotNetHelper, maxAllowedSize);
    initLongPress(containerOrId, dotNetHelper);
    initScrollListener(containerOrId, dotNetHelper);
    if (hasMoreMessages) {
        initScrollObserver(containerOrId, sentinelOrId, dotNetHelper);
    }
    initReadReceiptObserver(containerOrId, dotNetHelper);
}

export function dispose() {
    isLoadOlderInFlight = false;
    currentScrollAnchor = null;
    if (readReceiptObserver) {
        readReceiptObserver.disconnect();
        readReceiptObserver = null;
    }
    if (readReceiptMutationObserver) {
        readReceiptMutationObserver.disconnect();
        readReceiptMutationObserver = null;
    }
    if (observerTimeoutId) {
        clearTimeout(observerTimeoutId);
        observerTimeoutId = null;
    }
    if (currentObserver) {
        currentObserver.disconnect();
        currentObserver = null;
    }
}
