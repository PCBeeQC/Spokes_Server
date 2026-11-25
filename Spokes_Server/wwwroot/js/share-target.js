(function () {
    'use strict';

    let _shareTargetDotNetRef = null;
    let _shareListenersInitialized = false;
    let _pollInProgress = false;
    let _shareDeliveryInProgress = false;
    let _pendingPayload = null;
    let _lastShareTime = 0;

    window._cancelPendingShare = null;

    const normalizeSharePayload = (raw) => {
        const files = [];
        if (raw && Array.isArray(raw.files)) {
            for (const f of raw.files) {
                if (!f) continue;
                const uri = f.uri || f.path || '';
                if (!uri) continue;
                files.push({
                    uri: uri,
                    name: f.name || uri.split('/').pop() || 'shared_file',
                    mimeType: f.mimeType || f.type || 'application/octet-stream'
                });
            }
        }
        return {
            title: (raw && typeof raw.title === 'string') ? raw.title : '',
            text: (raw && typeof raw.text === 'string') ? raw.text : '',
            files: files,
            targetChannelId: (raw && typeof raw.targetChannelId === 'string') ? raw.targetChannelId : ''
        };
    };

    const pollNativePendingIntent = async () => {
        if (window._spokesHostViewActive === false) return false;
        if (_pollInProgress) return false;
        _pollInProgress = true;
        let foundShare = false;
        try {
            if (window.Capacitor?.Plugins?.BridgeManager?.checkPendingIntent) {
                const intentResult = await window.Capacitor.Plugins.BridgeManager.checkPendingIntent();
                if (intentResult && intentResult.isShareIntent) {
                    const now = Date.now();
                    if (now - _lastShareTime < 500) {
                        return true;
                    }
                    _lastShareTime = now;
                    foundShare = true;
                    const parsedFiles = typeof intentResult.files === 'string'
                        ? JSON.parse(intentResult.files)
                        : (intentResult.files || []);
                    _pendingPayload = normalizeSharePayload({
                        title: intentResult.title,
                        text: intentResult.text,
                        files: parsedFiles,
                        targetChannelId: intentResult.targetChannelId
                    });
                    await window.checkPendingShares();
                }
            }
        } catch (e) {
            console.error('[ShareTarget] Error polling BridgeManager.checkPendingIntent:', e);
        } finally {
            _pollInProgress = false;
        }
        return foundShare;
    };

    window.spokesCheckPendingShare = () => {
        pollNativePendingIntent().catch(() => {});
    };

    window.registerShareTarget = (dotNetRef) => {
        _shareTargetDotNetRef = dotNetRef;

        pollNativePendingIntent().then((foundShare) => {
            if (!foundShare) {
                window.checkPendingShares();
                window.spokesCheckAppShortcuts();
            }
        });

        if (!_shareListenersInitialized) {
            _shareListenersInitialized = true;

            if (window.Capacitor?.Plugins?.App) {
                window.Capacitor.Plugins.App.addListener('appStateChange', ({ isActive }) => {
                    if (isActive && window._spokesHostViewActive !== false && !_pollInProgress) {
                        setTimeout(() => {
                            if (window._spokesHostViewActive !== false) {
                                pollNativePendingIntent();
                            }
                        }, 300);
                    }
                });
            }
        }
    };

    window.unregisterShareTarget = () => {
        _shareTargetDotNetRef = null;
    };

    window.checkPendingShares = async (retryCount = 0) => {
        const MAX_RETRIES = 10;
        const BASE_DELAY_MS = 1000;

        if (window._spokesHostViewActive === false) return;
        if (!window.Capacitor || !_shareTargetDotNetRef) return;
        if (!_pendingPayload || _shareDeliveryInProgress) return;

        _shareDeliveryInProgress = true;
        try {
            if (window._blazorDisconnected) throw new Error('Blazor explicitly disconnected');

            const payload = _pendingPayload;
            const invokePromise = _shareTargetDotNetRef.invokeMethodAsync('OnShareReceived', payload);
            const timeoutPromise = new Promise((_, reject) => {
                window._cancelPendingShare = () => reject(new Error('Cancelled by connection state change'));
                setTimeout(() => reject(new Error('Timeout waiting for Blazor SignalR')), 3000);
            });

            await Promise.race([invokePromise, timeoutPromise]);
            window._cancelPendingShare = null;
            _pendingPayload = null;
        } catch (e) {
            window._cancelPendingShare = null;
            if (retryCount < MAX_RETRIES) {
                const delay = window._blazorDisconnected ? 200 : Math.min(BASE_DELAY_MS * Math.pow(2, retryCount), 10000);
                console.warn(`[ShareTarget] SignalR not ready, retry ${retryCount + 1}/${MAX_RETRIES} in ${delay}ms`, e.message);
                setTimeout(() => window.checkPendingShares(retryCount + 1), delay);
            } else {
                console.error('[ShareTarget] Max retries reached. Share payload abandoned.');
                _pendingPayload = null;
            }
        } finally {
            _shareDeliveryInProgress = false;
        }
    };

    // --- Shortcut Manager Integration ---
    const _shortcutIconCache = new Map();
    const _pushedDirectShareShortcuts = new Map();

    window.spokesPushDirectShareShortcut = async (serverUrl, channelId, channelName, iconBase64, iconCacheKey) => {
        if (window.Capacitor?.Plugins?.ShortcutManager) {
            try {
                let resolvedIcon = iconBase64 || '';
                if (iconCacheKey) {
                    if (resolvedIcon) {
                        _shortcutIconCache.set(iconCacheKey, resolvedIcon);
                        if (_shortcutIconCache.size > 64) {
                            const oldestKey = _shortcutIconCache.keys().next().value;
                            _shortcutIconCache.delete(oldestKey);
                        }
                    } else if (_shortcutIconCache.has(iconCacheKey)) {
                        resolvedIcon = _shortcutIconCache.get(iconCacheKey) || '';
                    }
                }

                const dedupeKey = `${serverUrl || ''}|${channelId || ''}`;
                const signature = `${channelName || ''}|${iconCacheKey || resolvedIcon.length}`;
                if (_pushedDirectShareShortcuts.get(dedupeKey) === signature) {
                    return;
                }

                await window.Capacitor.Plugins.ShortcutManager.pushDirectShareShortcut({
                    serverUrl: serverUrl,
                    id: channelId,
                    name: channelName,
                    icon: resolvedIcon
                });
                _pushedDirectShareShortcuts.set(dedupeKey, signature);
            } catch (e) {
                console.error('[Shortcuts] Failed to push direct share shortcut', e);
            }
        }
    };

    window.spokesPushAppShortcut = async (serverUrl, id, shortLabel, longLabel, iconBase64) => {
        if (window.Capacitor?.Plugins?.ShortcutManager) {
            try {
                await window.Capacitor.Plugins.ShortcutManager.pushAppShortcut({
                    serverUrl: serverUrl,
                    id: id,
                    shortLabel: shortLabel,
                    longLabel: longLabel,
                    icon: iconBase64 || ''
                });
            } catch (e) {
                console.error('[Shortcuts] Failed to push app shortcut', e);
            }
        }
    };

    window.spokesRemoveShortcut = async (serverUrl, id) => {
        if (window.Capacitor?.Plugins?.ShortcutManager) {
            try {
                await window.Capacitor.Plugins.ShortcutManager.removeShortcut({ serverUrl, id });
            } catch (e) {
                console.error('[Shortcuts] Failed to remove shortcut', e);
            }
        }
    };

    window.spokesCheckAppShortcuts = async () => {
        if (window._spokesHostViewActive === false) return;
        if (!window.Capacitor?.Plugins?.ShortcutManager || !_shareTargetDotNetRef) return;
        try {
            const pending = await window.Capacitor.Plugins.ShortcutManager.getPendingShortcut();
            if (pending && pending.id) {
                const parts = pending.id.split('|');
                if (parts.length >= 2) {
                    const serverUrl = parts[0];
                    const targetId = parts[1];
                    const targetOrigin = new URL(serverUrl).origin.toLowerCase();
                    if (window.location.origin.toLowerCase() === targetOrigin) {
                        await _shareTargetDotNetRef.invokeMethodAsync('OnAppShortcutReceived', targetId);
                    } else if (window.Capacitor?.Plugins?.BridgeManager?.openServer) {
                        await window.Capacitor.Plugins.BridgeManager.openServer({ url: serverUrl });
                    }
                }
            }
        } catch (e) {
            console.error('[Shortcuts] Error checking app shortcuts', e);
        }
    };
})();
