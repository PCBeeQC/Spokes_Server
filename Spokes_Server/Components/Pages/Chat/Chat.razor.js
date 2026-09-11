export function initFileHandlers(elementId, dotNetHelper) {
    // We ignore elementId for the container now, and prefer our specific chat-drop-zone
    const container = document.getElementById('chat-drop-zone');
    const input = document.getElementById('chat-message-input');

    if (!container) return;

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
            handleFiles(files, dotNetHelper);
        }
    });
}

async function handleFiles(fileList, dotNetHelper) {
    const uploadUrl = await dotNetHelper.invokeMethodAsync('GetUploadUrl');
    if (!uploadUrl) return;

    if (fileList.length === 0) return;

    let totalSize = 0;
    const maxAllowedSize = 268435456; // 256MB

    for (let i = 0; i < fileList.length; i++) {
        totalSize += fileList[i].size;
    }

    if (totalSize > maxAllowedSize) {
        dotNetHelper.invokeMethodAsync('OnPasteUploadError', `Total file size exceeds the allowed limit of ${Math.round(maxAllowedSize/1024/1024)}MB`);
        return;
    }

    for (let i = 0; i < fileList.length; i++) {
        const file = fileList[i];
        
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

// ─── SCROLL LOADING ─────────────────────────────────────────────────────────

let currentObserver = null;
let observerTimeoutId = null;

export function initScrollObserver(containerOrId, sentinelOrId, dotNetHelper) {
    const container = typeof containerOrId === 'string' ? document.getElementById(containerOrId) : containerOrId;
    const sentinel = typeof sentinelOrId === 'string' ? document.getElementById(sentinelOrId) : sentinelOrId;

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
        entries.forEach(entry => {
            if (entry.isIntersecting) {
                dotNetHelper.invokeMethodAsync('LoadOlderMessagesJS');
            }
        });
    }, {
        root: container,
        threshold: 0.1
    });

    // Remove the previous 800ms timeout hack.
    // By passing the Blazor ElementReference directly for the sentinel, we completely
    // bypass the DOM race conditions that required the delay in the first place.
    if (currentObserver && sentinel) {
        currentObserver.observe(sentinel);
    }
}

export async function scrollToBottomWhenLoaded(containerOrId) {
    const el = typeof containerOrId === 'string' ? document.getElementById(containerOrId) : containerOrId;
    if (!el) return;

    // Because of transform: scaleY(-1), scrollTop 0 is always the visual bottom
    // universally across all browsers (including iOS Safari).
    el.scrollTop = 0;
    
    // We add a tiny delay just in case of DOM rendering lag, but we no longer need
    // aggressive polling or abort handlers because native scroll anchoring works perfectly.
    setTimeout(() => {
        if (el) el.scrollTop = 0;
    }, 100);
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
    const container = typeof containerOrId === 'string' ? document.getElementById(containerOrId) : containerOrId;
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
            if (!moved) {
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

// ─── IOS VIRTUAL KEYBOARD SCOPED FIX ────────────────────────────────────────

let iosVisualViewportInit = false;

// ─── READ RECEIPTS (INTERSECTION OBSERVER) ──────────────────────────────────

let readReceiptObserver = null;
let readReceiptMutationObserver = null;

export function initReadReceiptObserver(containerOrId, dotNetHelper) {
    if (readReceiptObserver) {
        readReceiptObserver.disconnect();
    }
    if (readReceiptMutationObserver) {
        readReceiptMutationObserver.disconnect();
    }

    const container = typeof containerOrId === 'string' ? document.getElementById(containerOrId) : containerOrId;
    if (!container) return;

    readReceiptObserver = new IntersectionObserver((entries) => {
        entries.forEach(entry => {
            if (entry.isIntersecting) {
                const msgId = entry.target.getAttribute('data-message-id');
                if (msgId) {
                    dotNetHelper.invokeMethodAsync('HandleMessageSeen', msgId)
                        .catch(err => console.warn("HandleMessageSeen failed", err));

                    // Unobserve once seen to preserve performance
                    readReceiptObserver.unobserve(entry.target);
                }
            }
        });
    }, {
        root: container,
        threshold: 0.5 // Message must be half-visible to count as read
    });

    // Short delay to allow layout to settle before observing
    setTimeout(() => {
        if (!readReceiptObserver) return;

        const messages = container.querySelectorAll('.message-row[data-message-id]');
        messages.forEach(msg => {
            if (msg.dataset.messageId) {
                readReceiptObserver.observe(msg);
            }
        });

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
    const container = typeof containerOrId === 'string' ? document.getElementById(containerOrId) : containerOrId;
    if (!container) return;

    if (container._scrollListenerInit) return;
    container._scrollListenerInit = true;

    let isCurrentlyScrolledUp = false;
    let scrollTimer = null;

    container.addEventListener('scroll', (e) => {
        if (scrollTimer) clearTimeout(scrollTimer);
        
        scrollTimer = setTimeout(() => {
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

export function dispose() {
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
