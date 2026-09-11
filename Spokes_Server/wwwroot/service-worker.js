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
    const tag = data.tag || `chat-${Date.now()}-${Math.random().toString(36).substr(2, 9)}`;

    const isSilent = data.isSilent === true;

    const options = {
        body: data.body || 'New message received',
        icon: data.icon || '/spokesapi/Media/Icon',
        vibrate: [100, 50, 100],
        data: data.data || {},
        actions: data.actions || [
            { action: 'open', title: 'Open' },
            { action: 'close', title: 'Dismiss' }
        ],
        requireInteraction: false,
        tag: tag,        // Will overwrite previous notifications with the same tag
        renotify: isSilent ? false : true,
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
                clients.forEach(c => c.postMessage({ type: 'TEST_PUSH_RESULT', success: true }));
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
                        clients.forEach(c => c.postMessage({ type: 'TEST_PUSH_RESULT', success: true, fallbackUsed: true }));
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

    if (event.action === 'close') {
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

    event.waitUntil(
        clients.matchAll({ type: 'window', includeUncontrolled: true }).then(function (clientList) {
            // Check if there's already a chat window open
            for (let client of clientList) {
                // Issue 74: Secure prefix matching instead of substring matching
                // Issue 75: Only reuse/navigate tabs that are already within the Chat feature
                if (client.url.startsWith(chatBaseUrl) && 'focus' in client) {
                    return client.navigate(absoluteTargetUrl).then(c => c ? c.focus() : client.focus());
                }
            }
            // Open a new window if no chat tab exists
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
