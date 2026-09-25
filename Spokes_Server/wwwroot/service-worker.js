// Service Worker for Web Push Notifications
// This file handles incoming push events and displays native notifications

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

    const isSilent = data.isSilent === true;

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
            await self.registration.showNotification(data.title || 'Spokes Chat', options);
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
                    await self.registration.showNotification(data.title || 'Spokes Chat', minimalOptions);
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
