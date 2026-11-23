// ChatComposer JS interop helpers

let _maxHeightsByOrientation = {};

function isIosDevice() {
    if (typeof navigator === 'undefined') return false;
    return /iPad|iPhone|iPod/.test(navigator.userAgent || '') ||
        (navigator.platform === 'MacIntel' && (navigator.maxTouchPoints || 0) > 1);
}

function updateViewport() {
    if (!window.visualViewport || !isIosDevice()) return;

    // Detect orientation to maintain separate max heights
    const isLandscape = window.innerWidth > window.innerHeight;
    const orientationKey = isLandscape ? 'landscape' : 'portrait';

    // Update max height for current orientation
    if (!_maxHeightsByOrientation[orientationKey] || window.visualViewport.height > _maxHeightsByOrientation[orientationKey]) {
        _maxHeightsByOrientation[orientationKey] = window.visualViewport.height;
    }

    const maxVp = _maxHeightsByOrientation[orientationKey];
    // If current height is significantly less than max height, keyboard is open
    const isKeyboardOpen = window.visualViewport.height < maxVp - 100;

    if (isKeyboardOpen) {
        document.documentElement.classList.add('keyboard-open-ios');
    } else {
        document.documentElement.classList.remove('keyboard-open-ios');
    }

    document.documentElement.style.setProperty('--viewport-height', `${window.visualViewport.height}px`);

    // Directly target the main content wrapper to force WebKit to repaint
    const mainContent = document.querySelector('.mud-main-content.chat-channel-open');
    if (mainContent) {
        mainContent.style.height = `${window.visualViewport.height}px`;
    }

    // Force iOS Safari to not push the body out of bounds
    window.scrollTo(0, 0);
    document.body.scrollTop = 0;
    document.documentElement.scrollTop = 0;
}

// Global visualViewport handler strictly for iOS Safari / WKWebView keyboard overlap.
// Android uses native `interactive-widget=resizes-content` + `100dvh` without JS viewport fights.
if (typeof window !== 'undefined' && window.visualViewport && isIosDevice()) {
    window.visualViewport.addEventListener('resize', updateViewport);
    window.visualViewport.addEventListener('scroll', updateViewport);
    updateViewport();
}

export function initComposerAll(idSuffix, autofocus, dotNetHelper) {
    window.spokesChatDiagnostics?.mark(`Client: JS ChatComposer.initComposerAll (${idSuffix})`);
    initComposer(`chat-message-input-${idSuffix}`, false, autofocus, dotNetHelper);
    initComposer(`chat-message-input-desktop-${idSuffix}`, true, autofocus, dotNetHelper);
    initMobileSendButton(`mobile-send-btn-${idSuffix}`, `chat-message-input-${idSuffix}`, dotNetHelper);
    initMentionPopover(`mention-popover-container-${idSuffix}`, dotNetHelper);
    initEventPopover(`event-popover-container-${idSuffix}`, dotNetHelper);
    initEmojiAutocompletePopover(`emoji-autocomplete-popover-container-${idSuffix}`, dotNetHelper);
    initSnippetPopover(`snippet-popover-container-${idSuffix}`, dotNetHelper);
}

/**
 * Prevent Enter (without Shift) from inserting a newline in the textarea.
 * Attaches a keydown listener and paste listener to the textarea element.
 */
export function initComposer(textareaId, preventEnter, autofocus, dotNetHelper) {
    window.spokesChatDiagnostics?.mark(`Client: JS ChatComposer.initComposer (${textareaId})`);
    const container = document.getElementById(textareaId);
    if (!container) return;

    // MudTextField renders a <textarea> inside the container div
    const textarea = container.querySelector('textarea') || container;
    if (textarea._spokesComposerInit) return;
    textarea._spokesComposerInit = true;

    textarea.addEventListener('keydown', (e) => {
        if (preventEnter && e.key === 'Enter' && !e.shiftKey) {
            e.preventDefault(); // Prevent newline insertion
        }
    });

    // iOS Safari sometimes fails to fire the visualViewport.resize event on the very first tap.
    // Instead of fighting the OS animation with a blind scroll lock, we poll the visualViewport height
    // strictly on iOS. Android handles keyboard resizing natively via interactive-widget=resizes-content.
    if (isIosDevice()) {
        textarea.addEventListener('focus', () => {
            if (!window.visualViewport) return;

            let checkCount = 0;
            let lastHeight = window.visualViewport.height;

            const pollInterval = setInterval(() => {
                // If the height has shrunk (keyboard opened), immediately update the layout
                if (window.visualViewport.height !== lastHeight) {
                    lastHeight = window.visualViewport.height;
                    updateViewport();
                }

                checkCount++;
                // Stop polling after ~1000ms (keyboard animation takes ~300-400ms)
                if (checkCount > 50) { // 50 * 20ms = 1000ms
                    clearInterval(pollInterval);
                    updateViewport(); // One final ensure
                }
            }, 20);
        });
    }

    textarea.addEventListener('paste', (e) => {
        const items = e.clipboardData.items;
        const files = [];

        for (const item of items) {
            if (item.kind === 'file') {
                const file = item.getAsFile();
                if (file) files.push(file);
            }
        }

        if (files.length > 0) {
            e.preventDefault(); // Prevent pasting filename text if it's a file
            window.spokesUpload?.uploadFilesWithDotNet(files, dotNetHelper, {
                allowInputUrlFallback: true,
                errorPrefix: 'Error uploading pasted file:'
            });
        }
    });

    // Automatically focus the textarea when it's initialized (unless explicitly disabled)
    // ONLY focus on desktop by default, to avoid mobile keyboard popups on page load
    if (autofocus && window.innerWidth >= 960) {
        textarea.focus();
        // Place cursor at the end of the text (useful for edit mode)
        const val = textarea.value;
        if (val) {
            textarea.value = '';
            textarea.value = val;
        }
    }

    // Force MudBlazor AutoGrow / InputSizing to collapse/adjust only when initial text is non-empty
    if (textarea.value && textarea.value.length > 0) {
        setTimeout(() => {
            textarea.dispatchEvent(new Event('input', { bubbles: true }));
        }, 50);
    }
}

function replaceTextareaRange(textarea, replaceStart, replaceEnd, insertion, cursorPos, focus = true) {
    const text = textarea.value || '';
    textarea.value = text.substring(0, replaceStart) + insertion + text.substring(replaceEnd);
    textarea.selectionStart = cursorPos;
    textarea.selectionEnd = cursorPos;
    if (focus) {
        textarea.focus();
    }
    textarea.dispatchEvent(new Event('input', { bubbles: true }));
    textarea.dispatchEvent(new Event('change', { bubbles: true }));
}

/**
 * Insert text at cursor position or wrap selected text, then update the Blazor binding.
 */
export function insertFormatting(textareaId, prefix, suffix) {
    const container = document.getElementById(textareaId);
    if (!container) return;
    const textarea = container.querySelector('textarea') || container;

    const start = textarea.selectionStart;
    const end = textarea.selectionEnd;
    const selectedText = (textarea.value || '').substring(start, end);
    const cursorPos = selectedText.length > 0
        ? start + prefix.length + selectedText.length + suffix.length
        : start + prefix.length;

    replaceTextareaRange(textarea, start, end, prefix + selectedText + suffix, cursorPos);
}

/**
 * Insert a prefix at the start of the current line (for quotes, lists).
 */
export function insertLinePrefix(textareaId, prefix) {
    const container = document.getElementById(textareaId);
    if (!container) return;
    const textarea = container.querySelector('textarea') || container;

    const start = textarea.selectionStart;
    const lineStart = (textarea.value || '').lastIndexOf('\n', start - 1) + 1;
    replaceTextareaRange(textarea, lineStart, lineStart, prefix, start + prefix.length);
}

function isElementVisible(el) {
    if (!el) return false;
    if (typeof el.checkVisibility === 'function') {
        return el.checkVisibility({ checkVisibilityCSS: true });
    }
    return el.offsetParent !== null;
}

/**
 * Returns the currently visible/active composer textarea element for the given idSuffix.
 */
export function getActiveComposerTextarea(idSuffix) {
    const desktopContainer = document.getElementById(`chat-message-input-desktop-${idSuffix}`);
    if (desktopContainer && isElementVisible(desktopContainer)) {
        return desktopContainer.querySelector('textarea') || desktopContainer;
    }

    const mobileContainer = document.getElementById(`chat-message-input-${idSuffix}`);
    if (mobileContainer && isElementVisible(mobileContainer)) {
        return mobileContainer.querySelector('textarea') || mobileContainer;
    }

    // Fallback based on viewport width (>= 960px is desktop md breakpoint in MudBlazor)
    if (window.innerWidth >= 960 && desktopContainer) {
        return desktopContainer.querySelector('textarea') || desktopContainer;
    }
    if (mobileContainer) {
        return mobileContainer.querySelector('textarea') || mobileContainer;
    }
    if (desktopContainer) {
        return desktopContainer.querySelector('textarea') || desktopContainer;
    }

    return null;
}

/**
 * Insert an emoji at the current cursor position in the active (visible) composer.
 * Returns true if inserted successfully, false otherwise.
 */
export function insertEmojiAtCursor(idSuffix, emoji, focusAfterInsert = false) {
    const textarea = getActiveComposerTextarea(idSuffix);
    if (!textarea) return false;

    const start = textarea.selectionStart ?? (textarea.value ? textarea.value.length : 0);
    const end = textarea.selectionEnd ?? (textarea.value ? textarea.value.length : 0);
    replaceTextareaRange(textarea, start, end, emoji, start + emoji.length, focusAfterInsert);
    return true;
}

/**
 * Focuses the active (visible) composer textarea.
 */
export function focusComposer(idSuffix) {
    const textarea = getActiveComposerTextarea(idSuffix);
    if (textarea) {
        textarea.focus();
    }
}

/**
 * Initializes the mobile send button to securely prevent focus loss (keyboard flashing)
 * by intercepting `touchstart` with a non-passive listener, and synchronizes send button
 * enabled state and text payload directly from the client textarea in 0ms.
 */
export function initMobileSendButton(btnId, textareaIdOrHelper, dotNetHelperArg) {
    window.spokesChatDiagnostics?.mark('Client: JS ChatComposer.initMobileSendButton');
    const btn = document.getElementById(btnId);
    if (!btn || btn._spokesMobileSendInit) return;
    btn._spokesMobileSendInit = true;

    const dotNetHelper = dotNetHelperArg || textareaIdOrHelper;
    const textareaId = typeof textareaIdOrHelper === 'string'
        ? textareaIdOrHelper
        : btnId.replace('mobile-send-btn-', 'chat-message-input-');

    const getTextarea = () => {
        const container = document.getElementById(textareaId);
        return container ? (container.querySelector('textarea') || container) : null;
    };

    const syncClientSendButton = () => {
        const textarea = getTextarea();
        if (!textarea) return;
        if (textarea.value && textarea.value.trim().length > 0) {
            if (btn.style.pointerEvents === 'none' || btn.style.opacity !== '1') {
                btn.style.opacity = '1';
                btn.style.pointerEvents = 'auto';
            }
        }
    };

    const initialTextarea = getTextarea();
    if (initialTextarea) {
        initialTextarea.addEventListener('input', syncClientSendButton);
    }

    // Prevent stale server renders from disabling the send button while the user has non-empty text
    const observer = new MutationObserver(syncClientSendButton);
    observer.observe(btn, { attributes: true, attributeFilter: ['style'] });

    const preventFocusLoss = (e) => {
        // e.preventDefault() prevents the browser from shifting focus away from the input,
        // which prevents the virtual keyboard from dismissing.
        e.preventDefault();
    };

    // Passive MUST be false to allow preventDefault()
    btn.addEventListener('touchstart', preventFocusLoss, { passive: false });
    btn.addEventListener('mousedown', preventFocusLoss, { passive: false });

    const performSend = () => {
        const textarea = getTextarea();
        const currentText = textarea ? textarea.value : null;
        if (textarea && currentText && currentText.trim().length > 0) {
            textarea.value = '';
            textarea.style.height = 'auto';
            btn.style.opacity = '0.5';
            btn.style.pointerEvents = 'none';
        }
        dotNetHelper.invokeMethodAsync('TriggerSendMobile', currentText);
    };

    // Since we prevented default on touchstart/mousedown, the native `click` 
    // event will NOT fire. We must manually trigger the callback on touchend/mouseup.
    
    let isHandlingTouch = false;

    btn.addEventListener('touchend', (e) => {
        e.preventDefault();
        isHandlingTouch = true;
        performSend();
        setTimeout(() => isHandlingTouch = false, 300); // Debounce paired clicks
    });

    btn.addEventListener('mouseup', (e) => {
        e.preventDefault();
        if (!isHandlingTouch) {
            performSend();
        }
    });
}

/**
 * Sets up autocomplete popovers for smooth touch scrolling on mobile while preventing
 * focus loss on desktop and avoiding accidental item selection during swipe/drag.
 */
function setupPopoverTouchScrollAndSelection(containerId, itemClass, callbackMethod, dotNetHelper) {
    const container = document.getElementById(containerId);
    if (!container || container._spokesPopoverInit) return;
    container._spokesPopoverInit = true;

    let touchStartY = 0;
    let touchStartX = 0;
    let isScrolling = false;
    let isHandlingTouch = false;

    // Allow native momentum scrolling by keeping touchstart passive and uncancelled
    container.addEventListener('touchstart', (e) => {
        if (e.touches && e.touches.length > 0) {
            touchStartY = e.touches[0].clientY;
            touchStartX = e.touches[0].clientX;
            isScrolling = false;
        }
    }, { passive: true });

    // Track finger movement: if delta > 8px, it is a scroll gesture, not a tap
    container.addEventListener('touchmove', (e) => {
        if (e.touches && e.touches.length > 0) {
            const deltaY = Math.abs(e.touches[0].clientY - touchStartY);
            const deltaX = Math.abs(e.touches[0].clientX - touchStartX);
            if (deltaY > 8 || deltaX > 8) {
                isScrolling = true;
            }
        }
    }, { passive: true });

    // Touchend selects the item only if the user tapped (did not scroll)
    container.addEventListener('touchend', (e) => {
        if (isScrolling) {
            return;
        }
        const item = e.target.closest(itemClass);
        if (item) {
            e.preventDefault(); // Prevent synthetic mouse/click and focus loss
            isHandlingTouch = true;
            const indexStr = item.getAttribute('data-index');
            if (indexStr) {
                dotNetHelper.invokeMethodAsync(callbackMethod, parseInt(indexStr, 10));
            }
            setTimeout(() => isHandlingTouch = false, 300); // Debounce paired clicks
        }
    });

    // On desktop: prevent default on mousedown so clicking popover does not blur the input
    container.addEventListener('mousedown', (e) => {
        const item = e.target.closest(itemClass);
        if (item) {
            e.preventDefault();
        }
    });

    // On desktop: trigger selection on mouseup
    container.addEventListener('mouseup', (e) => {
        const item = e.target.closest(itemClass);
        if (item) {
            e.preventDefault();
            if (!isHandlingTouch) {
                const indexStr = item.getAttribute('data-index');
                if (indexStr) {
                    dotNetHelper.invokeMethodAsync(callbackMethod, parseInt(indexStr, 10));
                }
            }
        }
    });
}

export function initMentionPopover(containerId, dotNetHelper) {
    window.spokesChatDiagnostics?.mark('Client: JS ChatComposer.initMentionPopover');
    setupPopoverTouchScrollAndSelection(containerId, '.mention-popover-item', 'TriggerMentionMobile', dotNetHelper);
}

export function initEventPopover(containerId, dotNetHelper) {
    window.spokesChatDiagnostics?.mark('Client: JS ChatComposer.initEventPopover');
    setupPopoverTouchScrollAndSelection(containerId, '.event-popover-item', 'TriggerEventMobile', dotNetHelper);
}

export function initEmojiAutocompletePopover(containerId, dotNetHelper) {
    window.spokesChatDiagnostics?.mark('Client: JS ChatComposer.initEmojiAutocompletePopover');
    setupPopoverTouchScrollAndSelection(containerId, '.emoji-autocomplete-popover-item', 'TriggerEmojiAutocompleteMobile', dotNetHelper);
}

export function initSnippetPopover(containerId, dotNetHelper) {
    window.spokesChatDiagnostics?.mark('Client: JS ChatComposer.initSnippetPopover');
    setupPopoverTouchScrollAndSelection(containerId, '.snippet-popover-item', 'TriggerSnippetMobile', dotNetHelper);
}

export function scrollSnippetPopoverToSelected(containerId) {
    const container = document.getElementById(containerId);
    if (!container) return;
    // Using a small timeout to let Blazor render the selected state
    setTimeout(() => {
        const selected = container.querySelector('.snippet-popover-item[style*="background"]');
        if (selected) {
            selected.scrollIntoView({ block: 'nearest' });
        } else {
            // Fallback: scroll to bottom if nothing selected but we want bottom default
            container.scrollTop = container.scrollHeight;
        }
    }, 10);
}
export async function uploadSharedFilesToComposer(filesArray, uploadUrl, dotNetHelper) {
    const proxyDotNetRef = {
        invokeMethodAsync: async (methodName, ...args) => {
            if (methodName === 'OnProgressUpdateCallback') {
                return dotNetHelper.invokeMethodAsync('OnPasteProgressUpdate', ...args);
            } else if (methodName === 'OnCompressingCallback') {
                return dotNetHelper.invokeMethodAsync('OnPasteCompressingUpdate', ...args);
            } else if (methodName === 'OnUploadStartedCallback') {
                return dotNetHelper.invokeMethodAsync('OnPasteUploadStarted');
            } else if (methodName === 'OnUploadErrorCallback') {
                return dotNetHelper.invokeMethodAsync('OnPasteUploadError', ...args);
            }
        }
    };

    if (window.Capacitor?.isNativePlatform?.() && window.Capacitor?.Plugins?.BridgeManager?.uploadSharedFiles) {
        try {
            const uris = filesArray.map(f => {
                if (!f.uri) return null;
                if (f.uri.startsWith('/')) return 'file://' + f.uri;
                return f.uri;
            }).filter(u => !!u);
            
            if (uris.length > 0) {
                const result = await window.spokesUpload.uploadNativeFiles(uris, uploadUrl, proxyDotNetRef);
                if (result && !result.cancelled) {
                    await dotNetHelper.invokeMethodAsync('OnPasteUploadCompleted', JSON.stringify(result));
                }
            }
        } catch(e) {
            console.error("Native shared upload failed", e);
            await dotNetHelper.invokeMethodAsync('OnPasteUploadError', e.message || "Upload failed");
        }
        return;
    }

    for (const f of filesArray) {
        if (!f.uri) continue;
        await dotNetHelper.invokeMethodAsync('OnPasteUploadStarted');
        try {
            // Convert native URI to web-accessible local URL
            const webUrl = window.Capacitor?.convertFileSrc ? window.Capacitor.convertFileSrc(f.uri) : f.uri;
            const response = await fetch(webUrl);
            if (!response.ok) throw new Error(`Failed to fetch shared file (${response.status})`);
            const blob = await response.blob();
            
            // spokesUpload expects a File object with .name and .type for compression logic
            const fileObj = new File([blob], f.name || 'shared_file', { type: f.mimeType || blob.type });

            const result = await window.spokesUpload.uploadFileObject(fileObj, uploadUrl, proxyDotNetRef);
            await dotNetHelper.invokeMethodAsync('OnPasteUploadCompleted', JSON.stringify(result));
        } catch(e) {
            console.error("Failed to upload shared file", f, e);
            await dotNetHelper.invokeMethodAsync('OnPasteUploadError', e.message || "Upload failed");
        }
    }
}
