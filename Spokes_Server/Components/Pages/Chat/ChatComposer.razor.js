// ChatComposer JS interop helpers

let _maxHeightsByOrientation = {};

function updateViewport() {
    if (!window.visualViewport) return;
    
    const isIos = /iPad|iPhone|iPod/.test(navigator.userAgent) || (navigator.platform === 'MacIntel' && navigator.maxTouchPoints > 1);
    
    if (isIos) {
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

// Global visualViewport handler to fix iOS Safari keyboard overlap and ensure 
// the chat composer is always perfectly positioned above the virtual keyboard.
if (window.visualViewport) {
    window.visualViewport.addEventListener('resize', updateViewport);
    window.visualViewport.addEventListener('scroll', updateViewport);
    // Initial call
    updateViewport();
}

/**
 * Prevent Enter (without Shift) from inserting a newline in the textarea.
 * Attaches a keydown listener and paste listener to the textarea element.
 */
export function initComposer(textareaId, preventEnter, autofocus, dotNetHelper) {
    const container = document.getElementById(textareaId);
    if (!container) return;

    // MudTextField renders a <textarea> inside the container div
    const textarea = container.querySelector('textarea') || container;

    textarea.addEventListener('keydown', (e) => {
        if (preventEnter && e.key === 'Enter' && !e.shiftKey) {
            e.preventDefault(); // Prevent newline insertion
        }
    });

    // iOS Safari sometimes fails to fire the visualViewport.resize event on the very first tap.
    // Instead of fighting the OS animation with a blind scroll lock, we poll the visualViewport height.
    // When the keyboard slides up and the height changes, we apply the new layout gracefully.
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

    textarea.addEventListener('paste', (e) => {
        const items = e.clipboardData.items;
        const files = [];

        for (let i = 0; i < items.length; i++) {
            if (items[i].kind === 'file') {
                const file = items[i].getAsFile();
                if (file) files.push(file);
            }
        }

        if (files.length > 0) {
            e.preventDefault(); // Prevent pasting filename text if it's a file
            handleFiles(files, dotNetHelper);
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

    // Force MudBlazor AutoGrow / InputSizing to collapse to the correct 1-line height on load
    setTimeout(() => {
        textarea.dispatchEvent(new Event('input', { bubbles: true }));
    }, 50);
}

async function handleFiles(fileList, dotNetHelper) {
    const fileInput = document.querySelector('input[data-upload-url]');
    if (!fileInput) {
        console.error("Could not find upload input to route pasted file");
        return;
    }
    const uploadUrl = fileInput.getAttribute('data-upload-url');

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
            await dotNetHelper.invokeMethodAsync('OnPasteUploadCompleted', JSON.stringify(result));
        } catch (error) {
            console.error("Error uploading pasted file:", error);
            await dotNetHelper.invokeMethodAsync('OnPasteUploadError', error.message || "Upload failed");
        }
    }
}

/**
 * Get current selection range in the textarea.
 */
export function getSelection(textareaId) {
    const container = document.getElementById(textareaId);
    if (!container) return { start: 0, end: 0 };
    const textarea = container.querySelector('textarea') || container;
    return {
        start: textarea.selectionStart ?? 0,
        end: textarea.selectionEnd ?? 0
    };
}

/**
 * Insert text at cursor position or wrap selected text, then update the Blazor binding.
 * Returns the new full text value.
 */
export function insertFormatting(textareaId, prefix, suffix, dotNetHelper) {
    const container = document.getElementById(textareaId);
    if (!container) return;
    const textarea = container.querySelector('textarea') || container;

    const start = textarea.selectionStart;
    const end = textarea.selectionEnd;
    const text = textarea.value;
    const selectedText = text.substring(start, end);

    const newText = text.substring(0, start) + prefix + selectedText + suffix + text.substring(end);

    // Update the textarea value
    textarea.value = newText;

    // Set cursor position: if no selection, place between prefix and suffix; 
    // if selection, place after the wrapped text
    const cursorPos = selectedText.length > 0
        ? start + prefix.length + selectedText.length + suffix.length
        : start + prefix.length;

    textarea.selectionStart = cursorPos;
    textarea.selectionEnd = cursorPos;
    textarea.focus();

    // Trigger input event so Blazor picks up the change
    textarea.dispatchEvent(new Event('input', { bubbles: true }));
    textarea.dispatchEvent(new Event('change', { bubbles: true }));
}

/**
 * Insert a prefix at the start of the current line (for quotes, lists).
 */
export function insertLinePrefix(textareaId, prefix) {
    const container = document.getElementById(textareaId);
    if (!container) return;
    const textarea = container.querySelector('textarea') || container;

    const start = textarea.selectionStart;
    const text = textarea.value;

    // Find the start of the current line
    let lineStart = text.lastIndexOf('\n', start - 1) + 1;

    const newText = text.substring(0, lineStart) + prefix + text.substring(lineStart);
    textarea.value = newText;

    const cursorPos = start + prefix.length;
    textarea.selectionStart = cursorPos;
    textarea.selectionEnd = cursorPos;
    textarea.focus();

    textarea.dispatchEvent(new Event('input', { bubbles: true }));
    textarea.dispatchEvent(new Event('change', { bubbles: true }));
}

/**
 * Insert an emoji at the current cursor position.
 */
export function insertAtCursor(textareaId, textToInsert, focusAfterInsert = true) {
    const container = document.getElementById(textareaId);
    if (!container) return;
    const textarea = container.querySelector('textarea') || container;

    const start = textarea.selectionStart;
    const end = textarea.selectionEnd;
    const text = textarea.value;

    const newText = text.substring(0, start) + textToInsert + text.substring(end);
    textarea.value = newText;

    const cursorPos = start + textToInsert.length;
    textarea.selectionStart = cursorPos;
    textarea.selectionEnd = cursorPos;
    
    if (focusAfterInsert !== false) {
        textarea.focus();
    }

    textarea.dispatchEvent(new Event('input', { bubbles: true }));
    textarea.dispatchEvent(new Event('change', { bubbles: true }));
}

/**
 * Initializes the mobile send button to securely prevent focus loss (keyboard flashing)
 * by intercepting `touchstart` with a non-passive listener.
 */
export function initMobileSendButton(btnId, dotNetHelper) {
    const btn = document.getElementById(btnId);
    if (!btn) return;

    const preventFocusLoss = (e) => {
        // e.preventDefault() prevents the browser from shifting focus away from the input,
        // which prevents the virtual keyboard from dismissing.
        e.preventDefault();
    };

    // Passive MUST be false to allow preventDefault()
    btn.addEventListener('touchstart', preventFocusLoss, { passive: false });
    btn.addEventListener('mousedown', preventFocusLoss, { passive: false });

    // Since we prevented default on touchstart/mousedown, the native `click` 
    // event will NOT fire. We must manually trigger the callback on touchend/mouseup.
    
    let isHandlingTouch = false;

    btn.addEventListener('touchend', (e) => {
        e.preventDefault();
        isHandlingTouch = true;
        dotNetHelper.invokeMethodAsync('TriggerSendMobile');
        setTimeout(() => isHandlingTouch = false, 300); // Debounce paired clicks
    });

    btn.addEventListener('mouseup', (e) => {
        e.preventDefault();
        if (!isHandlingTouch) {
            dotNetHelper.invokeMethodAsync('TriggerSendMobile');
        }
    });
}

/**
 * Initializes the mention popover to securely prevent focus loss (keyboard flashing)
 * when selecting a mention on mobile devices. Uses event delegation on the container.
 */
export function initMentionPopover(containerId, dotNetHelper) {
    const container = document.getElementById(containerId);
    if (!container) return;

    const preventFocusLoss = (e) => {
        // Only prevent default if we tapped a mention item
        const item = e.target.closest('.mention-popover-item');
        if (item) {
            // e.preventDefault() prevents the browser from shifting focus away from the input
            e.preventDefault();
        }
    };

    container.addEventListener('touchstart', preventFocusLoss, { passive: false });
    container.addEventListener('mousedown', preventFocusLoss, { passive: false });

    let isHandlingTouch = false;

    container.addEventListener('touchend', (e) => {
        const item = e.target.closest('.mention-popover-item');
        if (item) {
            e.preventDefault();
            isHandlingTouch = true;
            const indexStr = item.getAttribute('data-index');
            if (indexStr) {
                dotNetHelper.invokeMethodAsync('TriggerMentionMobile', parseInt(indexStr, 10));
            }
            setTimeout(() => isHandlingTouch = false, 300); // Debounce paired clicks
        }
    });

    container.addEventListener('mouseup', (e) => {
        const item = e.target.closest('.mention-popover-item');
        if (item) {
            e.preventDefault();
            if (!isHandlingTouch) {
                const indexStr = item.getAttribute('data-index');
                if (indexStr) {
                    dotNetHelper.invokeMethodAsync('TriggerMentionMobile', parseInt(indexStr, 10));
                }
            }
        }
    });
}

export function initEventPopover(containerId, dotNetHelper) {
    const container = document.getElementById(containerId);
    if (!container) return;

    const preventFocusLoss = (e) => {
        const item = e.target.closest('.event-popover-item');
        if (item) {
            e.preventDefault();
        }
    };

    container.addEventListener('touchstart', preventFocusLoss, { passive: false });
    container.addEventListener('mousedown', preventFocusLoss, { passive: false });

    let isHandlingTouch = false;

    container.addEventListener('touchend', (e) => {
        const item = e.target.closest('.event-popover-item');
        if (item) {
            e.preventDefault();
            isHandlingTouch = true;
            const indexStr = item.getAttribute('data-index');
            if (indexStr) {
                dotNetHelper.invokeMethodAsync('TriggerEventMobile', parseInt(indexStr, 10));
            }
            setTimeout(() => isHandlingTouch = false, 300);
        }
    });

    container.addEventListener('mouseup', (e) => {
        const item = e.target.closest('.event-popover-item');
        if (item) {
            e.preventDefault();
            if (!isHandlingTouch) {
                const indexStr = item.getAttribute('data-index');
                if (indexStr) {
                    dotNetHelper.invokeMethodAsync('TriggerEventMobile', parseInt(indexStr, 10));
                }
            }
        }
    });
}

export function initEmojiAutocompletePopover(containerId, dotNetHelper) {
    const container = document.getElementById(containerId);
    if (!container) return;

    const preventFocusLoss = (e) => {
        const item = e.target.closest('.emoji-autocomplete-popover-item');
        if (item) {
            e.preventDefault();
        }
    };

    container.addEventListener('touchstart', preventFocusLoss, { passive: false });
    container.addEventListener('mousedown', preventFocusLoss, { passive: false });

    let isHandlingTouch = false;

    container.addEventListener('touchend', (e) => {
        const item = e.target.closest('.emoji-autocomplete-popover-item');
        if (item) {
            e.preventDefault();
            isHandlingTouch = true;
            const indexStr = item.getAttribute('data-index');
            if (indexStr) {
                dotNetHelper.invokeMethodAsync('TriggerEmojiAutocompleteMobile', parseInt(indexStr, 10));
            }
            setTimeout(() => isHandlingTouch = false, 300);
        }
    });

    container.addEventListener('mouseup', (e) => {
        const item = e.target.closest('.emoji-autocomplete-popover-item');
        if (item) {
            e.preventDefault();
            if (!isHandlingTouch) {
                const indexStr = item.getAttribute('data-index');
                if (indexStr) {
                    dotNetHelper.invokeMethodAsync('TriggerEmojiAutocompleteMobile', parseInt(indexStr, 10));
                }
            }
        }
    });
}

export function initSnippetPopover(containerId, dotNetHelper) {
    const container = document.getElementById(containerId);
    if (!container) return;

    const preventFocusLoss = (e) => {
        const item = e.target.closest('.snippet-popover-item');
        if (item) {
            e.preventDefault();
        }
    };

    container.addEventListener('touchstart', preventFocusLoss, { passive: false });
    container.addEventListener('mousedown', preventFocusLoss, { passive: false });

    let isHandlingTouch = false;

    container.addEventListener('touchend', (e) => {
        const item = e.target.closest('.snippet-popover-item');
        if (item) {
            e.preventDefault();
            isHandlingTouch = true;
            const indexStr = item.getAttribute('data-index');
            if (indexStr) {
                dotNetHelper.invokeMethodAsync('TriggerSnippetMobile', parseInt(indexStr, 10));
            }
            setTimeout(() => isHandlingTouch = false, 300);
        }
    });

    container.addEventListener('mouseup', (e) => {
        const item = e.target.closest('.snippet-popover-item');
        if (item) {
            e.preventDefault();
            if (!isHandlingTouch) {
                const indexStr = item.getAttribute('data-index');
                if (indexStr) {
                    dotNetHelper.invokeMethodAsync('TriggerSnippetMobile', parseInt(indexStr, 10));
                }
            }
        }
    });
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
            const webUrl = window.Capacitor.convertFileSrc(f.uri);
            const response = await fetch(webUrl);
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
