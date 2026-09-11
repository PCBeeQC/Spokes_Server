let _shareTargetDotNetRef = null;
let _shareListenersInitialized = false;

// --- Race condition guards ---
// Set to true while handleSharePayload is routing (showing server selector or navigating).
// checkPendingShares must defer when this is true (same JS context).
let _isRouting = false;
// Deduplicates concurrent pollNativePendingIntent calls (appUrlOpen + appStateChange fire together).
let _pollInProgress = false;
// Unique ID for this JS context — used for cross-WebView coordination via Preferences.
const _contextId = Date.now() + '_' + Math.random().toString(36).slice(2, 8);

window.registerShareTarget = (dotNetRef) => {
    console.log('[ShareTarget] Loaded v2 (navigating fix included)');
    window._blazorLoaded = true; // Signal to index.html that Blazor is handling shares
    _shareTargetDotNetRef = dotNetRef;
    
    // Check natively for pending intent before checking pending shares
    pollNativePendingIntent().then((foundShare) => {
        // Check immediately if there's any pending payload saved from index.html or extracted natively
        window.checkPendingShares();
        
        if (!foundShare) {
            window.spokesCheckAppShortcuts();
        }
    });

    if (!_shareListenersInitialized) {
        _shareListenersInitialized = true;

        // Listen to the Capacitor Share Target plugin while Blazor is running
        if (window.Capacitor && window.Capacitor.Plugins && window.Capacitor.Plugins.CapacitorShareTarget) {
            window.Capacitor.Plugins.CapacitorShareTarget.addListener('shareReceived', async (payload) => {
                console.log('[ShareTarget] Blazor active WebView received share payload:', payload);
                // This event is a fallback — checkPendingIntent() handles the primary path.
                // Only act if there's no pending payload already stored by native extraction.
                const existingRes = await window.Capacitor.Plugins.Preferences.get({ key: 'pendingSharePayload' });
                if (existingRes && existingRes.value) {
                    console.log('[ShareTarget] Payload already stored by native extraction, ignoring plugin event.');
                    return;
                }
                await handleSharePayload(payload);
            });
        }

        // iOS Warm-Start Fix: Listen to App lifecycle events to manually poll for intents
        if (window.Capacitor && window.Capacitor.Plugins && window.Capacitor.Plugins.App) {
            window.Capacitor.Plugins.App.addListener('appUrlOpen', async (data) => {
                if (data.url && data.url.includes('spokes://share')) {
                    console.log('[ShareTarget] appUrlOpen detected share URL, polling intent...');
                    await pollNativePendingIntent();
                }
            });

            window.Capacitor.Plugins.App.addListener('appStateChange', async ({ isActive }) => {
                if (isActive) {
                    // Only poll if we're not already routing a share — avoids racing with appUrlOpen
                    if (_isRouting || _pollInProgress) {
                        console.log('[ShareTarget] appStateChange active, skipping poll (routing or poll in progress).');
                        return;
                    }
                    console.log('[ShareTarget] appStateChange active, polling intent as fallback...');
                    setTimeout(async () => {
                        await pollNativePendingIntent();
                    }, 500);
                }
            });
        }
    }
};

window.unregisterShareTarget = () => {
    _shareTargetDotNetRef = null;
};

let _lastShareTime = 0;

const handleSharePayload = async (payload) => {
    const now = Date.now();
    if (now - _lastShareTime < 1000) {
        console.log('[ShareTarget] Ignored duplicate share payload (debounce)');
        return;
    }
    _lastShareTime = now;

    // Prevent concurrent routing — if we're already routing a share, ignore this one
    if (_isRouting) {
        console.log('[ShareTarget] Ignored share payload — routing already in progress');
        return;
    }

    // Check if this share was triggered via a Direct Share shortcut
    if (window.Capacitor.Plugins.ShortcutManager) {
        try {
            const pending = await window.Capacitor.Plugins.ShortcutManager.getPendingShortcut();
            if (pending && pending.id) {
                const parts = pending.id.split('|');
                if (parts.length >= 2) {
                    payload.targetServerUrl = parts[0];
                    payload.targetChannelId = parts[1];
                    console.log('[ShareTarget] Direct share shortcut detected for channel:', payload.targetChannelId);
                }
            }
        } catch (e) {
            console.error('[ShareTarget] Error getting pending shortcut:', e);
        }
    }

    const normalized = window.spokesShareRouter.normalizePayload(payload);

    try {
        const confirmedRes = await window.Capacitor.Plugins.Preferences.get({ key: 'shareDestinationConfirmed' });
        console.log('[ShareTarget] confirmedRes:', confirmedRes);
        
        if (confirmedRes && confirmedRes.value === 'true') {
            // Server was already chosen (e.g. cross-server redirect completed). Deliver directly.
            await window.Capacitor.Plugins.Preferences.remove({ key: 'shareDestinationConfirmed' });
            await window.Capacitor.Plugins.Preferences.remove({ key: 'shareClaimedBy' });
            await window.Capacitor.Plugins.Preferences.set({ key: 'pendingSharePayload', value: JSON.stringify(normalized) });
            window.spokesShareRouter.showLoadingOverlay("Preparing Share...");
            window.checkPendingShares();
            return;
        }

        // Bypass server selector if Direct Share already provided a target server
        if (normalized.targetServerUrl) {
            const currentUrlBase = window.location.origin;
            let targetUrlBase;
            try { targetUrlBase = new URL(normalized.targetServerUrl).origin; } catch { targetUrlBase = ''; }

            if (currentUrlBase === targetUrlBase) {
                await window.Capacitor.Plugins.Preferences.set({ key: 'pendingSharePayload', value: JSON.stringify(normalized) });
                window.spokesShareRouter.showLoadingOverlay("Preparing Share...");
                window.checkPendingShares();
            } else {
                window._spokesShareNavigating = true;
                await window.Capacitor.Plugins.Preferences.set({ key: 'pendingSharePayload', value: JSON.stringify(normalized) });
                await window.Capacitor.Plugins.Preferences.set({ key: 'serverUrl', value: normalized.targetServerUrl });
                await window.Capacitor.Plugins.Preferences.set({ key: 'shareDestinationConfirmed', value: 'true' });
                if (window.Capacitor.Plugins.BridgeManager) {
                    window.Capacitor.Plugins.BridgeManager.openServer({ url: normalized.targetServerUrl });
                } else {
                    window.location.href = normalized.targetServerUrl;
                }
            }
            return;
        }

        // --- Server selector routing ---
        // Set local flag AND write a cross-WebView claim to Preferences.
        // This prevents both same-context and cross-context races.
        _isRouting = true;
        const claimValue = _contextId + '|' + Date.now();
        await window.Capacitor.Plugins.Preferences.set({ key: 'shareClaimedBy', value: claimValue });
        console.log('[ShareTarget] Routing claimed by context', _contextId);

        // Store the payload to Preferences so it survives a potential app restart during routing
        await window.Capacitor.Plugins.Preferences.set({ key: 'pendingSharePayload', value: JSON.stringify(normalized) });

        // Use shared server selector module
        const handled = await window.spokesShareRouter.routeShare({
            onSameServer: async () => {
                // Clear routing state before delivering — routeShare resolved via user selection
                _isRouting = false;
                await window.Capacitor.Plugins.Preferences.remove({ key: 'shareClaimedBy' });
                console.log('[ShareTarget] Routing claim released by onSameServer');
                window.spokesShareRouter.showLoadingOverlay("Preparing Share...");
                window.checkPendingShares();
            }
        });
        if (!handled) {
            // routeShare returned false (0 or 1 servers, no selector shown) — deliver directly
            _isRouting = false;
            await window.Capacitor.Plugins.Preferences.remove({ key: 'shareClaimedBy' });
            console.log('[ShareTarget] Routing claim released — routeShare not handled');
            window.spokesShareRouter.showLoadingOverlay("Preparing Share...");
            window.checkPendingShares();
        }
    } catch(e) {
        console.error('[ShareTarget] Exception in overlay logic:', e);
        // Ensure payload is stored so checkPendingShares can recover
        try {
            await window.Capacitor.Plugins.Preferences.set({ key: 'pendingSharePayload', value: JSON.stringify(normalized) });
        } catch(_) {}
        _isRouting = false;
        await window.Capacitor.Plugins.Preferences.remove({ key: 'shareClaimedBy' }).catch(() => {});
        window.spokesShareRouter.showLoadingOverlay("Preparing Share...");
        window.checkPendingShares();
    } finally {
        // Ensure local flag is always cleared even if we missed a path above
        _isRouting = false;
    }
};

const pollNativePendingIntent = async () => {
    // Deduplicate concurrent polls (appUrlOpen + appStateChange fire together on iOS)
    if (_pollInProgress) {
        console.log('[ShareTarget] pollNativePendingIntent skipped — already in progress');
        return false;
    }
    _pollInProgress = true;
    let foundShare = false;
    try {
        if (window.Capacitor && window.Capacitor.Plugins.BridgeManager && window.Capacitor.Plugins.BridgeManager.checkPendingIntent) {
            try {
                const intentResult = await window.Capacitor.Plugins.BridgeManager.checkPendingIntent();
                if (intentResult && intentResult.isShareIntent) {
                    foundShare = true;
                    console.log('[ShareTarget] Extracted share intent from BridgeManager natively:', intentResult);
                    const payload = {};
                    if (intentResult.text) payload.text = intentResult.text;
                    if (intentResult.title) payload.title = intentResult.title;
                    if (intentResult.files) {
                        payload.files = typeof intentResult.files === 'string'
                            ? JSON.parse(intentResult.files)
                            : intentResult.files;
                    }
                    await handleSharePayload(payload);
                }
            } catch (e) {
                console.error('[ShareTarget] Error polling BridgeManager.checkPendingIntent:', e);
            }
        }
    } finally {
        _pollInProgress = false;
    }
    return foundShare;
};



let _shareDeliveryInProgress = false;
window._cancelPendingShare = null;

window.checkPendingShares = async (retryCount = 0) => {
    const MAX_RETRIES = 10;
    const BASE_DELAY_MS = 1000;

    if (!window.Capacitor || !_shareTargetDotNetRef) return;
    
    // Guard 0: We are navigating away — do not process shares locally
    if (window._spokesShareNavigating) {
        console.log('[ShareTarget] checkPendingShares aborted — navigating away');
        return;
    }
    
    // Guard 1: Same-context lock — handleSharePayload is actively routing in this JS context
    if (_isRouting) {
        console.log('[ShareTarget] checkPendingShares deferred — share routing in progress (local)');
        return;
    }

    // Guard 2: Cross-context lock — another WebView's handleSharePayload is routing
    // The claim has a timestamp; if it's stale (>60s), ignore it to prevent deadlocks
    try {
        const claimRes = await Capacitor.Plugins.Preferences.get({ key: 'shareClaimedBy' });
        if (claimRes && claimRes.value) {
            const parts = claimRes.value.split('|');
            const claimTime = parseInt(parts[1] || '0');
            const claimAge = Date.now() - claimTime;
            if (claimAge < 60000) {
                console.log('[ShareTarget] checkPendingShares deferred — claimed by another context ' + claimAge + 'ms ago');
                return;
            } else {
                // Stale claim (>60s) — clear it and proceed
                console.log('[ShareTarget] Clearing stale share claim (age=' + claimAge + 'ms)');
                await Capacitor.Plugins.Preferences.remove({ key: 'shareClaimedBy' });
            }
        }
    } catch(_) {}

    // Guard 3: DOM check — server selector overlay is visible
    if (document.getElementById('spokes-share-overlay')) {
        console.log('[ShareTarget] Overlay is visible, deferring checkPendingShares');
        return;
    }
    
    // Guard 4: Delivery loop lock — prevent concurrent checks from within this context
    if (_shareDeliveryInProgress) {
        console.log('[ShareTarget] checkPendingShares deferred — already delivering');
        return;
    }
    _shareDeliveryInProgress = true;

    try {
        const res = await Capacitor.Plugins.Preferences.get({ key: 'pendingSharePayload' });
        if (res && res.value) {
            const payload = JSON.parse(res.value);
            
            // Try to invoke. If SignalR is disconnected, it will throw an exception.
            try {
                if (window._blazorDisconnected) throw new Error("Blazor explicitly disconnected");

                console.log('[ShareTarget] Attempting to invoke OnShareReceived with payload:', payload);
                const invokePromise = _shareTargetDotNetRef.invokeMethodAsync('OnShareReceived', payload);
                const timeoutPromise = new Promise((_, reject) => {
                    window._cancelPendingShare = () => reject(new Error("Cancelled by connection state change"));
                    setTimeout(() => reject(new Error("Timeout waiting for Blazor SignalR")), 3000);
                });
                
                await Promise.race([invokePromise, timeoutPromise]);
                window._cancelPendingShare = null;
                
                console.log('[ShareTarget] Successfully invoked OnShareReceived.');
                
                // Success! Clean up associated keys.
                await Capacitor.Plugins.Preferences.remove({ key: 'pendingSharePayload' });
                await Capacitor.Plugins.Preferences.remove({ key: 'shareDestinationConfirmed' });
                console.log('[ShareTarget] Share delivery complete, cleaned up Preferences.');
                if (window.spokesShareRouter && window.spokesShareRouter.hideLoadingOverlay) window.spokesShareRouter.hideLoadingOverlay();
            } catch(e) {
                window._cancelPendingShare = null;
                // SignalR not ready — DO NOT REMOVE the payload. We keep it to retry.
                if (retryCount < MAX_RETRIES) {
                    const delay = window._blazorDisconnected ? 200 : Math.min(BASE_DELAY_MS * Math.pow(2, retryCount), 10000);
                    console.warn(`[ShareTarget] SignalR not ready, retry ${retryCount + 1}/${MAX_RETRIES} in ${delay}ms`, e.message);
                    setTimeout(() => window.checkPendingShares(retryCount + 1), delay);
                } else {
                    console.error('[ShareTarget] Max retries reached. Share payload abandoned.');
                    await Capacitor.Plugins.Preferences.remove({ key: 'pendingSharePayload' });
                    await Capacitor.Plugins.Preferences.remove({ key: 'shareDestinationConfirmed' });
                    if (window.spokesShareRouter && window.spokesShareRouter.hideLoadingOverlay) window.spokesShareRouter.hideLoadingOverlay();
                }
            }
        }
    } catch(e) {
        console.error("Error checking pending shares:", e);
    } finally {
        _shareDeliveryInProgress = false;
    }
};

window.uploadSharedFiles = async (filesArray, uploadUrl) => {
    // Try native upload pipeline first if supported (fixes iOS App Group CORS issue)
    if (window.Capacitor?.isNativePlatform?.() && window.Capacitor?.Plugins?.BridgeManager?.uploadSharedFiles) {
        try {
            const uris = filesArray.map(f => f.uri).filter(uri => !!uri);
            if (uris.length > 0) {
                const nativeResult = await window.spokesUpload.uploadNativeFiles(uris, uploadUrl, null);
                if (nativeResult && nativeResult.responseJson) {
                    const parsed = typeof nativeResult.responseJson === 'string' 
                        ? JSON.parse(nativeResult.responseJson) 
                        : nativeResult.responseJson;
                    
                    if (Array.isArray(parsed)) {
                        return parsed.map(item => item.filePath);
                    }
                }
            }
        } catch (e) {
            console.error("[ShareTarget] Native shared file upload failed, falling back to JS", e);
        }
    }

    // Fallback to JS fetch & blob (works on Android but fails on iOS due to cross-origin WKWebView restrictions)
    const uploadedPaths = [];
    for (const f of filesArray) {
        if (!f.uri) continue;
        try {
            // Convert native URI to web-accessible local URL
            const webUrl = window.Capacitor.convertFileSrc(f.uri);
            const response = await fetch(webUrl);
            const blob = await response.blob();
            
            // spokesUpload expects a File object with .name and .type for compression logic
            const fileObj = new File([blob], f.name || 'shared_file', { type: f.mimeType || blob.type });
            
            // Reuse the robust chat upload pipeline (compression, thumbnails, etc.)
            const uploadResult = await window.spokesUpload.uploadFileObject(fileObj, uploadUrl, null);
            
            if (Array.isArray(uploadResult) && uploadResult.length > 0) {
                uploadedPaths.push(uploadResult[0].filePath);
            } else {
                console.error("Upload endpoint returned invalid result", uploadResult);
            }
        } catch(e) {
            console.error("Failed to upload shared file", f, e);
        }
    }
    return uploadedPaths;
};

// --- Shortcut Manager Integration ---

window.spokesPushDirectShareShortcut = async (serverUrl, channelId, channelName, iconBase64) => {
    if (window.Capacitor && window.Capacitor.Plugins.ShortcutManager) {
        try {
            await window.Capacitor.Plugins.ShortcutManager.pushDirectShareShortcut({
                serverUrl: serverUrl,
                id: channelId,
                name: channelName,
                icon: iconBase64 || ""
            });
        } catch (e) {
            console.error('[Shortcuts] Failed to push direct share shortcut', e);
        }
    }
};

window.spokesPushAppShortcut = async (serverUrl, id, shortLabel, longLabel, iconBase64) => {
    if (window.Capacitor && window.Capacitor.Plugins.ShortcutManager) {
        try {
            await window.Capacitor.Plugins.ShortcutManager.pushAppShortcut({
                serverUrl: serverUrl,
                id: id,
                shortLabel: shortLabel,
                longLabel: longLabel,
                icon: iconBase64 || ""
            });
        } catch (e) {
            console.error('[Shortcuts] Failed to push app shortcut', e);
        }
    }
};

window.spokesRemoveShortcut = async (serverUrl, id) => {
    if (window.Capacitor && window.Capacitor.Plugins.ShortcutManager) {
        try {
            await window.Capacitor.Plugins.ShortcutManager.removeShortcut({ serverUrl, id });
        } catch (e) {
            console.error('[Shortcuts] Failed to remove shortcut', e);
        }
    }
};

window.spokesCheckAppShortcuts = async () => {
    if (window.Capacitor && window.Capacitor.Plugins.ShortcutManager && _shareTargetDotNetRef) {
        try {
            const pending = await window.Capacitor.Plugins.ShortcutManager.getPendingShortcut();
            if (pending && pending.id) {
                const parts = pending.id.split('|');
                if (parts.length >= 2) {
                    const serverUrl = parts[0];
                    const targetId = parts[1];
                    console.log('[Shortcuts] App Shortcut triggered:', targetId);
                    
                    const currentUrlBase = window.location.origin;
                    const targetUrlBase = new URL(serverUrl).origin;
                    
                    if (currentUrlBase !== targetUrlBase) {
                        await window.Capacitor.Plugins.Preferences.set({ key: 'serverUrl', value: serverUrl });
                        await window.Capacitor.Plugins.Preferences.set({ key: 'pendingAppShortcut', value: targetId });
                        if (window.Capacitor.Plugins.BridgeManager) {
                            window.Capacitor.Plugins.BridgeManager.openServer({ url: serverUrl });
                        } else {
                            window.location.href = serverUrl;
                        }
                    } else {
                        await _shareTargetDotNetRef.invokeMethodAsync('OnAppShortcutReceived', targetId);
                    }
                }
            } else {
                const res = await window.Capacitor.Plugins.Preferences.get({ key: 'pendingAppShortcut' });
                if (res && res.value) {
                    await window.Capacitor.Plugins.Preferences.remove({ key: 'pendingAppShortcut' });
                    await _shareTargetDotNetRef.invokeMethodAsync('OnAppShortcutReceived', res.value);
                }
            }
        } catch (e) {
            console.error('[Shortcuts] Error checking app shortcuts', e);
        }
    }
};
