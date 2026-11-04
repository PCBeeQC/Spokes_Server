// Service Worker for Web Push Notifications
// This file handles incoming push events and displays native notifications

const CALL_TTL_MS = 60 * 1000;
const CALENDAR_TTL_MS = 2 * 60 * 60 * 1000;
const OFFLINE_DELAY_THRESHOLD_MS = 15 * 1000;
const WAKE_BURST_WINDOW_MS = 4000;

let cachedUnreadPromise = null;
let cachedUnreadExpiresAt = 0;
let lastWakeDigestShownAt = 0;
let lastWakeDigestCount = 0;

function fetchUnreadStateDeduped() {
    const now = Date.now();
    if (cachedUnreadPromise && now < cachedUnreadExpiresAt) {
        return cachedUnreadPromise;
    }
    cachedUnreadExpiresAt = now + 2000;
    cachedUnreadPromise = fetch('/spokesapi/push/unread-count', {
        method: 'GET',
        credentials: 'include',
        cache: 'no-store'
    })
        .then(function (res) { return res.ok ? res.json() : null; })
        .catch(function () { return null; });
    return cachedUnreadPromise;
}

self.addEventListener('push', function (event) {
    if (!event.data) {
        console.log('Push event received but no data');
        return;
    }

    let data;
    try {
        data = event.data.json();
    } catch (e) {
        console.error('Failed to parse push data:', e);
        return;
    }

    // Use the explicit tag provided by the server, falling back to a unique one only if not provided
    const tag = data.tag || `chat-${Date.now()}-${Math.random().toString(36).slice(2, 11)}`;
    const category = String(data.category || (data.data && data.data.category) || 'chat').toLowerCase();
    const threadId = (data.data && data.data.threadId) || data.tag || null;
    const sentAtRaw = (data.data && data.data.sentAtUtc) || data.sentAtUtc || null;
    const sentAtMs = sentAtRaw ? Date.parse(sentAtRaw) : NaN;

    if (category === 'clear_notification') {
        if (threadId && self.registration.getNotifications) {
            event.waitUntil(
                self.registration.getNotifications().then(function (notifications) {
                    notifications.forEach(function (n) {
                        if (n.tag === threadId || n.tag === `chat-${threadId}` || (n.data && n.data.threadId === threadId)) {
                            n.close();
                        }
                    });
                })
            );
        }
        return;
    }

    const isSilent = data.isSilent === true;
    let titleToShow = data.title || 'Spokes Chat';

    const options = {
        body: data.body || 'New message received',
        icon: data.icon || '/spokesapi/Media/Icon',
        vibrate: isSilent ? [] : [100, 50, 100],
        data: data.data || {},
        actions: data.actions || [
            { action: 'open', title: 'Open' },
            { action: 'close', title: 'Dismiss' }
        ],
        requireInteraction: false,
        tag: tag,        // Will overwrite previous notifications with the same tag
        renotify: !isSilent,
        silent: isSilent
    };

    if (data.badge) {
        options.badge = data.badge;
    }

    const showNotif = async () => {
        try {
            if (options.tag !== 'test-push') {
                const nowMs = Date.now();
                const ageMs = Number.isFinite(sentAtMs) ? Math.max(0, nowMs - sentAtMs) : 0;

                // Drop expired ephemeral alerts (live call rings >60s old, calendar reminders >2h old)
                if (category === 'call' && Number.isFinite(sentAtMs) && ageMs > CALL_TTL_MS) {
                    return;
                }
                if (category === 'calendar' && Number.isFinite(sentAtMs) && ageMs > CALENDAR_TTL_MS) {
                    return;
                }

                const windowClients = await self.clients.matchAll({ type: 'window', includeUncontrolled: true });
                if (windowClients.some(c => c.focused && c.visibilityState === 'visible')) {
                    return;
                }

                // When waking from sleep / reconnecting after offline delay, reconcile against authoritative server read state
                if (Number.isFinite(sentAtMs) && ageMs > OFFLINE_DELAY_THRESHOLD_MS) {
                    const unreadState = await fetchUnreadStateDeduped();
                    if (unreadState && typeof unreadState.count === 'number') {
                        const watermarkIso = threadId && unreadState.readWatermarksUtc
                            ? unreadState.readWatermarksUtc[threadId]
                            : null;
                        const watermarkMs = watermarkIso ? Date.parse(watermarkIso) : NaN;
                        const isStatefulCategory = category === 'chat' || category === 'email' || category === 'moderation';

                        const readByWatermark = Number.isFinite(watermarkMs) && sentAtMs <= watermarkMs;
                        const readByThreadCount = isStatefulCategory &&
                            unreadState.unreadByThread &&
                            threadId &&
                            (unreadState.unreadByThread[threadId] || 0) <= 0;
                        const readByZeroTotal = (category === 'chat' || category === 'email') &&
                            !unreadState.unreadByThread &&
                            unreadState.count === 0;

                        if (readByWatermark || readByThreadCount || readByZeroTotal) {
                            if (self.registration.getNotifications) {
                                const existing = await self.registration.getNotifications({ tag: options.tag });
                                existing.forEach(n => n.close());
                            }
                            return;
                        }

                        // Coalesce multi-item offline wake bursts into one single digest notification
                        if (isStatefulCategory && unreadState.count > 1) {
                            const withinWakeBurst = (Date.now() - lastWakeDigestShownAt) < WAKE_BURST_WINDOW_MS;
                            if (withinWakeBurst && lastWakeDigestCount === unreadState.count) {
                                return;
                            }

                            const unreadThreads = unreadState.unreadByThread ? Object.keys(unreadState.unreadByThread) : [];
                            let summaryUrl = '/chat';
                            if (unreadThreads.length === 1) {
                                const onlyThread = unreadThreads[0];
                                summaryUrl = onlyThread.startsWith('email-')
                                    ? '/email'
                                    : (onlyThread.startsWith('mod-') ? '/admin/moderation' : `/chat/${onlyThread}`);
                            } else if (unreadThreads.length > 0 && unreadThreads.every(t => t.startsWith('email-'))) {
                                summaryUrl = '/email';
                            }

                            titleToShow = (data.data && data.data.serverName) || 'Spokes';
                            options.body = `You have ${unreadState.count} unread notifications`;
                            options.tag = 'spokes-unread-summary';
                            options.renotify = !withinWakeBurst && !isSilent;
                            options.silent = withinWakeBurst || isSilent;
                            options.vibrate = options.silent ? [] : [100, 50, 100];
                            options.data = Object.assign({}, options.data, { url: summaryUrl });

                            lastWakeDigestShownAt = Date.now();
                            lastWakeDigestCount = unreadState.count;
                        }
                    }
                }
            }
            await self.registration.showNotification(titleToShow, options);
            if (options.tag === 'test-push') {
                const clients = await self.clients.matchAll();
                const testCategory = data.category || (data.data && data.data.category) || 'chat';
                const testSound = data.sound || (data.data && data.data.sound);
                clients.forEach(c => c.postMessage({ type: 'TEST_PUSH_RESULT', success: true, category: testCategory, sound: testSound }));
            }
        } catch (e) {
            if (e.name === 'TypeError') {
                // iOS Safari throws a TypeError if unsupported options (like actions or badge) are provided
                const minimalOptions = {
                    body: options.body,
                    data: options.data,
                    tag: options.tag,
                    renotify: options.renotify,
                    icon: options.icon,
                    silent: options.silent,
                    vibrate: options.vibrate
                };
                try {
                    await self.registration.showNotification(titleToShow, minimalOptions);
                    if (options.tag === 'test-push') {
                        const clients = await self.clients.matchAll();
                        const testCategory = data.category || (data.data && data.data.category) || 'chat';
                        const testSound = data.sound || (data.data && data.data.sound);
                        clients.forEach(c => c.postMessage({ type: 'TEST_PUSH_RESULT', success: true, fallbackUsed: true, category: testCategory, sound: testSound }));
                    }
                } catch(e2) {
                    if (options.tag === 'test-push') {
                        const clients = await self.clients.matchAll();
                        clients.forEach(c => c.postMessage({ type: 'TEST_PUSH_RESULT', success: false, error: e2.message }));
                    }
                    throw e2;
                }
            } else {
                if (options.tag === 'test-push') {
                    const clients = await self.clients.matchAll();
                    clients.forEach(c => c.postMessage({ type: 'TEST_PUSH_RESULT', success: false, error: e.message }));
                }
                throw e;
            }
        }
    };

    event.waitUntil(showNotif());
});

// Handle notification click
self.addEventListener('notificationclick', function (event) {
    event.notification.close();

    if (event.action === 'close' || event.action === 'decline' || event.action === 'decline_call') {
        return;
    }

    // Navigate to the chat URL
    let targetUrl = event.notification.data?.url || '/chat';
    
    // Issue 73: Prevent Open Redirect
    if (targetUrl.startsWith('http') && !targetUrl.startsWith(self.location.origin)) {
        return;
    }
    
    // Ensure we have an absolute URL for comparisons
    const absoluteTargetUrl = new URL(targetUrl, self.location.origin).href;
    const chatBaseUrl = new URL('/chat', self.location.origin).href;
    const calendarBaseUrl = new URL('/planning/calendar', self.location.origin).href;

    event.waitUntil(
        clients.matchAll({ type: 'window', includeUncontrolled: true }).then(function (clientList) {
            const isCalendar = targetUrl.includes('/planning/calendar');

            // 1. Prefer existing tab matching the specific feature (calendar or chat)
            for (let client of clientList) {
                if (isCalendar && client.url.startsWith(calendarBaseUrl) && 'focus' in client) {
                    return client.navigate(absoluteTargetUrl).then(c => c ? c.focus() : client.focus());
                } else if (!isCalendar && client.url.startsWith(chatBaseUrl) && 'focus' in client) {
                    return client.navigate(absoluteTargetUrl).then(c => c ? c.focus() : client.focus());
                }
            }

            // 2. Fall back to reusing any open window from our origin
            for (let client of clientList) {
                if (client.url.startsWith(self.location.origin) && 'focus' in client) {
                    return client.navigate(absoluteTargetUrl).then(c => c ? c.focus() : client.focus());
                }
            }

            // 3. Open a new window if no tab exists
            if (clients.openWindow) {
                return clients.openWindow(absoluteTargetUrl);
            }
        })
    );
});

// Handle notification close
self.addEventListener('notificationclose', function (event) {
    console.log('Notification closed:', event.notification.tag);
});

// Install event - cache essential files if needed
self.addEventListener('install', function (event) {
    console.log('Service Worker installed');
    self.skipWaiting();
});

// Activate event - clean up old caches if needed
self.addEventListener('activate', function (event) {
    console.log('Service Worker activated');
    event.waitUntil(clients.claim());
});

// Fetch event - required for PWA to be installable
self.addEventListener('fetch', function (event) {
    // We don't need to do anything here, but the handler must exist
    return;
});
