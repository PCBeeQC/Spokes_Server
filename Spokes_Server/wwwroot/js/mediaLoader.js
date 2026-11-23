// Spokes unified canonical media cache & prioritized background scheduler (Lane 2).
// Controls concurrent image loading across Chat attachments, Video posters, AlbumMentionCards,
// Albums, and MediaViewerDialog lightbox:
//   - Decouples cache identity from ephemeral ?t= access tokens (immutable canonical path key).
//   - Tier 1: Bounded ref-counted volatile RAM ObjectURL LRU (instant frame-1 channel switches).
//   - Tier 2: Bounded user-scoped IndexedDB (persists across app/server restarts & LAN HTTP/HTTPS;
//             caches encrypted media while the device vault is unlocked and purges it immediately
//             when the vault password is removed from the device).
//   - Tier 3: Prioritized Lane-2 fetch() queue with direct HTTP status handling (401/410 token
//             recovery without HEAD probes, terminal 403/404 fast-fail, AbortController cancellation).
(function () {
    'use strict';

    if (window.spokesMediaLoader) {
        return;
    }

    const NEAR_MARGIN = '350px 0px';
    const RETRY_DELAY_MS = 600;
    const DB_PREFIX = 'spokes-media-v1::';
    const DB_VERSION = 2;
    const STORE_BLOBS = 'blobs';
    const STORE_META = 'meta';
    const LEGACY_STORE_NAME = 'media';
    const SCOPES_STORAGE_KEY = 'spokes_media_db_scopes';

    const MAX_MEM_BYTES = 150 * 1024 * 1024;    // 150 MB volatile ObjectURL LRU
    const MAX_MEM_ENTRIES = 400;
    const MAX_DISK_BYTES = 500 * 1024 * 1024;   // 500 MB persistent IndexedDB LRU
    const PRUNE_TARGET_BYTES = Math.floor(MAX_DISK_BYTES * 0.85);
    const MAX_DISK_ENTRY_BYTES = 12 * 1024 * 1024; // 12 MB per cached image
    const TOUCH_THROTTLE_MS = 60 * 1000;
    const TOUCH_FLUSH_DELAY_MS = 1500;
    const IDB_LOOKUP_TIMEOUT_MS = 250;

    /** @type {Map<HTMLImageElement, any>} */
    const entries = new Map();
    /** @type {Set<any>} */
    const active = new Set();
    /** @type {Map<string, { key: string, objectUrl: string, byteLength: number, encrypted: boolean, category: string, contextId: string, activeImgs: Set<HTMLImageElement>, pendingRevoke: boolean }>} */
    const memCache = new Map();
    let memTotalBytes = 0;

    /** @type {Map<string, { controller: AbortController, subscribers: Set<any>, promise: Promise<any> }>} */
    const inFlightFetches = new Map();

    /** @type {Map<string, number>} */
    const pendingTouches = new Map();
    let touchFlushTimer = null;

    let seqCounter = 0;
    let pumpScheduled = false;
    let visibleObserver = null;
    let nearObserver = null;
    let mutationObserver = null;

    let activeDbName = null;
    let dbPromise = null;
    let diskApproxBytes = null;
    let diskPruneInFlight = false;
    let vaultPersisted = false;
    let lastReconciledScope = null;
    let lastReconciledVault = null;
    let hasPurgedUnauthenticated = false;

    function getSessionInfo() {
        const s = (typeof window !== 'undefined' && window.__spokesSession) ? window.__spokesSession : null;
        return {
            authenticated: Boolean(s && s.authenticated),
            userScope: (s && typeof s.userScope === 'string' && s.userScope.length > 0) ? s.userScope : null,
            hasVaultKey: Boolean(s && s.hasVaultKey)
        };
    }

    function isNativeCapacitorClient() {
        try {
            if (typeof window !== 'undefined' && typeof window.isCapacitorNative === 'function') {
                return Boolean(window.isCapacitorNative());
            }
            if (typeof window !== 'undefined' && window.Capacitor && typeof window.Capacitor.isNativePlatform === 'function') {
                return Boolean(window.Capacitor.isNativePlatform());
            }
        } catch (_) { }
        return false;
    }

    function canUseFetchCache() {
        return typeof fetch === 'function' &&
            typeof URL !== 'undefined' &&
            typeof URL.createObjectURL === 'function';
    }

    function canUseIndexedDb() {
        return typeof indexedDB !== 'undefined' && indexedDB !== null;
    }

    function readKnownScopes() {
        try {
            if (typeof localStorage === 'undefined') return [];
            const raw = localStorage.getItem(SCOPES_STORAGE_KEY);
            const parsed = raw ? JSON.parse(raw) : [];
            return Array.isArray(parsed) ? parsed.filter(x => typeof x === 'string' && x.startsWith(DB_PREFIX)) : [];
        } catch (_) {
            return [];
        }
    }

    function writeKnownScopes(list) {
        try {
            if (typeof localStorage === 'undefined') return;
            const unique = Array.from(new Set(list.filter(x => typeof x === 'string' && x.startsWith(DB_PREFIX))));
            if (unique.length === 0) {
                localStorage.removeItem(SCOPES_STORAGE_KEY);
            } else {
                localStorage.setItem(SCOPES_STORAGE_KEY, JSON.stringify(unique));
            }
        } catch (_) { }
    }

    function deleteIdbDatabase(name) {
        if (!canUseIndexedDb() || !name) return Promise.resolve();
        return new Promise((resolve) => {
            try {
                const req = indexedDB.deleteDatabase(name);
                req.onsuccess = () => resolve();
                req.onerror = () => resolve();
                req.onblocked = () => resolve();
            } catch (_) {
                resolve();
            }
        });
    }

    async function discoverAndDeleteDatabases(predicate) {
        if (!canUseIndexedDb()) return;
        const targets = new Set();
        for (const name of readKnownScopes()) {
            if (predicate(name)) targets.add(name);
        }
        try {
            if (typeof indexedDB.databases === 'function') {
                const dbs = await indexedDB.databases();
                if (Array.isArray(dbs)) {
                    for (const info of dbs) {
                        if (info && typeof info.name === 'string' && info.name.startsWith(DB_PREFIX) && predicate(info.name)) {
                            targets.add(info.name);
                        }
                    }
                }
            }
        } catch (_) { }

        const remaining = readKnownScopes().filter(n => !targets.has(n));
        writeKnownScopes(remaining);
        await Promise.all(Array.from(targets).map(deleteIdbDatabase));
    }

    function clearPendingTouches() {
        pendingTouches.clear();
        if (touchFlushTimer) {
            clearTimeout(touchFlushTimer);
            touchFlushTimer = null;
        }
    }

    function openMediaDb() {
        if (!canUseIndexedDb()) return Promise.resolve(null);
        reconcileSession();
        if (!activeDbName) return Promise.resolve(null);
        if (dbPromise) return dbPromise;

        const targetName = activeDbName;
        dbPromise = new Promise((resolve) => {
            try {
                const req = indexedDB.open(targetName, DB_VERSION);
                req.onupgradeneeded = () => {
                    const db = req.result;
                    if (db.objectStoreNames.contains(LEGACY_STORE_NAME)) {
                        db.deleteObjectStore(LEGACY_STORE_NAME);
                    }
                    if (!db.objectStoreNames.contains(STORE_BLOBS)) {
                        db.createObjectStore(STORE_BLOBS, { keyPath: 'key' });
                    }
                    if (!db.objectStoreNames.contains(STORE_META)) {
                        const metaStore = db.createObjectStore(STORE_META, { keyPath: 'key' });
                        metaStore.createIndex('by_lastAccessed', 'lastAccessedAt', { unique: false });
                        metaStore.createIndex('by_context', 'contextKey', { unique: false });
                        metaStore.createIndex('by_encrypted', 'encrypted', { unique: false });
                    }
                };
                req.onsuccess = () => {
                    const db = req.result;
                    db.onversionchange = () => {
                        try { db.close(); } catch (_) { }
                        if (activeDbName === targetName) dbPromise = null;
                    };
                    resolve(db);
                };
                req.onerror = () => {
                    if (activeDbName === targetName) dbPromise = null;
                    resolve(null);
                };
                req.onblocked = () => {
                    if (activeDbName === targetName) dbPromise = null;
                    resolve(null);
                };
            } catch (_) {
                if (activeDbName === targetName) dbPromise = null;
                resolve(null);
            }
        });
        return dbPromise;
    }

    function closeCurrentDb() {
        clearPendingTouches();
        const current = dbPromise;
        dbPromise = null;
        diskApproxBytes = null;
        if (!current) return Promise.resolve();
        return current.then(db => {
            if (db) {
                try { db.close(); } catch (_) { }
            }
        }).catch(() => { });
    }

    function reconcileSession(options) {
        const info = getSessionInfo();
        vaultPersisted = info.hasVaultKey;

        if (!canUseIndexedDb()) {
            activeDbName = null;
            return;
        }

        if (!info.authenticated || !info.userScope) {
            if (activeDbName) {
                activeDbName = null;
                closeCurrentDb();
            }
            lastReconciledScope = null;
            lastReconciledVault = null;

            const shouldPurge = !isNativeCapacitorClient() || Boolean(options && options.purgeIfUnauthenticated);
            if (shouldPurge && !hasPurgedUnauthenticated) {
                hasPurgedUnauthenticated = true;
                discoverAndDeleteDatabases(() => true);
            }
            return;
        }

        hasPurgedUnauthenticated = false;
        const nextDbName = DB_PREFIX + info.userScope;
        const scopeChanged = lastReconciledScope !== info.userScope || activeDbName !== nextDbName;

        if (scopeChanged) {
            if (activeDbName && activeDbName !== nextDbName) {
                closeCurrentDb();
            }
            activeDbName = nextDbName;
            const scopes = readKnownScopes();
            if (!scopes.includes(activeDbName)) {
                scopes.push(activeDbName);
                writeKnownScopes(scopes);
            }

            // Purge any previous account's databases on this origin
            discoverAndDeleteDatabases(name => name !== activeDbName);
        }

        // Update reconciliation latches BEFORE calling purgeDiskEncryptedEntries() because
        // purgeDiskEncryptedEntries() -> openMediaDb() re-enters reconcileSession().
        const shouldPurgeEncrypted = !info.hasVaultKey && (scopeChanged || lastReconciledVault !== false);
        lastReconciledScope = info.userScope;
        lastReconciledVault = info.hasVaultKey;

        // If the device no longer has a saved vault password (removed locally or remotely),
        // immediately purge any previously cached encrypted media from IndexedDB.
        if (shouldPurgeEncrypted) {
            purgeDiskEncryptedEntries();
        }
    }

    function idbGet(key) {
        return openMediaDb().then(db => {
            if (!db) return null;
            return new Promise((resolve) => {
                let settled = false;
                const finish = (val) => {
                    if (settled) return;
                    settled = true;
                    resolve(val);
                };
                try {
                    const tx = db.transaction([STORE_BLOBS, STORE_META], 'readonly');
                    let blobRec = null;
                    let metaRec = null;
                    let completedParts = 0;
                    const onPart = () => {
                        if (++completedParts < 2) return;
                        if (!blobRec || !blobRec.blob) {
                            finish(null);
                            return;
                        }
                        finish({
                            key,
                            blob: blobRec.blob,
                            contentType: blobRec.contentType || 'image/jpeg',
                            byteLength: (metaRec && typeof metaRec.byteLength === 'number') ? metaRec.byteLength : (blobRec.blob.size || 0),
                            category: metaRec ? metaRec.category : '',
                            contextId: metaRec ? metaRec.contextId : '',
                            contextKey: metaRec ? metaRec.contextKey : '',
                            encrypted: metaRec ? metaRec.encrypted : 0,
                            lastAccessedAt: metaRec ? metaRec.lastAccessedAt : 0
                        });
                    };
                    const blobReq = tx.objectStore(STORE_BLOBS).get(key);
                    blobReq.onsuccess = () => { blobRec = blobReq.result || null; onPart(); };
                    blobReq.onerror = () => onPart();
                    const metaReq = tx.objectStore(STORE_META).get(key);
                    metaReq.onsuccess = () => { metaRec = metaReq.result || null; onPart(); };
                    metaReq.onerror = () => onPart();
                    tx.onerror = () => finish(null);
                    tx.onabort = () => finish(null);
                } catch (_) {
                    finish(null);
                }
            });
        });
    }

    function idbPut(record) {
        if (!record || !record.blob || record.byteLength <= 0 || record.byteLength > MAX_DISK_ENTRY_BYTES) {
            return Promise.resolve();
        }
        return openMediaDb().then(db => {
            if (!db) return;
            return new Promise((resolve) => {
                try {
                    const tx = db.transaction([STORE_BLOBS, STORE_META], 'readwrite');
                    tx.objectStore(STORE_BLOBS).put({
                        key: record.key,
                        blob: record.blob,
                        contentType: record.contentType || 'image/jpeg'
                    });
                    tx.objectStore(STORE_META).put({
                        key: record.key,
                        byteLength: record.byteLength,
                        category: record.category || '',
                        contextId: record.contextId || '',
                        contextKey: record.contextKey || '',
                        encrypted: record.encrypted ? 1 : 0,
                        lastAccessedAt: record.lastAccessedAt || Date.now()
                    });
                    tx.oncomplete = () => {
                        if (typeof diskApproxBytes === 'number') {
                            diskApproxBytes += record.byteLength;
                        }
                        maybePruneDiskCache();
                        resolve();
                    };
                    tx.onerror = () => resolve();
                    tx.onabort = () => resolve();
                } catch (_) {
                    resolve();
                }
            });
        });
    }

    function flushPendingTouches() {
        touchFlushTimer = null;
        if (pendingTouches.size === 0) return;
        const batch = Array.from(pendingTouches.entries());
        pendingTouches.clear();

        openMediaDb().then(db => {
            if (!db) return;
            try {
                // Lightweight metadata-only transaction: never locks or rewrites STORE_BLOBS.
                const tx = db.transaction(STORE_META, 'readwrite');
                const metaStore = tx.objectStore(STORE_META);
                for (let i = 0; i < batch.length; i++) {
                    const [key, accessedAt] = batch[i];
                    const getReq = metaStore.get(key);
                    getReq.onsuccess = () => {
                        const existing = getReq.result;
                        if (existing && (existing.lastAccessedAt || 0) < accessedAt) {
                            existing.lastAccessedAt = accessedAt;
                            try { metaStore.put(existing); } catch (_) { }
                        }
                    };
                }
            } catch (_) { }
        });
    }

    function idbTouch(record) {
        const now = Date.now();
        if (!record || !record.key || (now - (record.lastAccessedAt || 0)) < TOUCH_THROTTLE_MS) {
            return;
        }
        pendingTouches.set(record.key, now);
        if (!touchFlushTimer) {
            touchFlushTimer = setTimeout(flushPendingTouches, TOUCH_FLUSH_DELAY_MS);
        }
    }

    function computeDiskUsage(db) {
        return new Promise((resolve) => {
            let totalBytes = 0;
            let count = 0;
            try {
                // Scans only ~120-byte STORE_META records; never reads STORE_BLOBS from flash.
                const tx = db.transaction(STORE_META, 'readonly');
                const req = tx.objectStore(STORE_META).openCursor();
                req.onsuccess = () => {
                    const cursor = req.result;
                    if (!cursor) {
                        resolve({ count, totalBytes });
                        return;
                    }
                    const val = cursor.value;
                    count++;
                    if (val && typeof val.byteLength === 'number') {
                        totalBytes += val.byteLength;
                    }
                    cursor.continue();
                };
                req.onerror = () => resolve({ count, totalBytes });
            } catch (_) {
                resolve({ count, totalBytes });
            }
        });
    }

    function maybePruneDiskCache() {
        if (diskPruneInFlight) return;
        if (typeof diskApproxBytes === 'number' && diskApproxBytes <= MAX_DISK_BYTES) {
            return;
        }
        diskPruneInFlight = true;
        openMediaDb().then(async db => {
            if (!db) return;
            const stats = await computeDiskUsage(db);
            diskApproxBytes = stats.totalBytes;
            if (stats.totalBytes <= MAX_DISK_BYTES) return;

            let bytesToFree = stats.totalBytes - PRUNE_TARGET_BYTES;
            await new Promise((resolve) => {
                try {
                    const tx = db.transaction([STORE_META, STORE_BLOBS], 'readwrite');
                    const metaStore = tx.objectStore(STORE_META);
                    const blobStore = tx.objectStore(STORE_BLOBS);
                    const idx = metaStore.index('by_lastAccessed');
                    const req = idx.openCursor();
                    req.onsuccess = () => {
                        const cursor = req.result;
                        if (!cursor || bytesToFree <= 0) {
                            return;
                        }
                        const key = cursor.primaryKey || (cursor.value && cursor.value.key);
                        const size = (cursor.value && typeof cursor.value.byteLength === 'number') ? cursor.value.byteLength : 0;
                        cursor.delete();
                        if (key) {
                            blobStore.delete(key);
                            pendingTouches.delete(key);
                        }
                        bytesToFree -= size;
                        diskApproxBytes = Math.max(0, (diskApproxBytes || 0) - size);
                        cursor.continue();
                    };
                    tx.oncomplete = () => resolve();
                    tx.onerror = () => resolve();
                    tx.onabort = () => resolve();
                } catch (_) {
                    resolve();
                }
            });
        }).finally(() => {
            diskPruneInFlight = false;
        });
    }

    function purgeDiskByIndex(indexName, keyValue) {
        return openMediaDb().then(db => {
            if (!db) return;
            return new Promise((resolve) => {
                try {
                    const tx = db.transaction([STORE_META, STORE_BLOBS], 'readwrite');
                    const metaStore = tx.objectStore(STORE_META);
                    const blobStore = tx.objectStore(STORE_BLOBS);
                    const idx = metaStore.index(indexName);
                    const req = idx.openCursor(IDBKeyRange.only(keyValue));
                    req.onsuccess = () => {
                        const cursor = req.result;
                        if (!cursor) return;
                        const key = cursor.primaryKey || (cursor.value && cursor.value.key);
                        const size = (cursor.value && typeof cursor.value.byteLength === 'number') ? cursor.value.byteLength : 0;
                        cursor.delete();
                        if (key) {
                            blobStore.delete(key);
                            pendingTouches.delete(key);
                        }
                        if (typeof diskApproxBytes === 'number') {
                            diskApproxBytes = Math.max(0, diskApproxBytes - size);
                        }
                        cursor.continue();
                    };
                    tx.oncomplete = () => resolve();
                    tx.onerror = () => resolve();
                    tx.onabort = () => resolve();
                } catch (_) {
                    resolve();
                }
            });
        });
    }

    function purgeDiskEncryptedEntries() {
        return purgeDiskByIndex('by_encrypted', 1);
    }

    /**
     * Extracts a stable canonical cache key from a Spokes file URL by stripping ephemeral
     * tokens (?t=), display filenames (?fn=), and retry params (?_retry=) while preserving
     * content-variant query params (e.g. s=sm). Returns null for external/non-Spokes URLs.
     */
    function parseSpokesMediaUrl(rawUrl) {
        if (!rawUrl || typeof rawUrl !== 'string') return null;
        try {
            const base = (typeof location !== 'undefined' && location.href) ? location.href : 'https://spokes.local/';
            const parsed = new URL(rawUrl, base);
            if (typeof location !== 'undefined' && location.origin && parsed.origin !== location.origin) {
                return null;
            }
            const pathname = parsed.pathname || '';
            let category = '';
            let contextId = '';

            if (pathname.startsWith('/spokesapi/files/thumb/')) {
                const parts = pathname.slice('/spokesapi/files/thumb/'.length).split('/');
                if (parts.length !== 3 || parts[0].toLowerCase() === 'demo-media') return null;
                category = decodeURIComponent(parts[0]);
                contextId = decodeURIComponent(parts[1]);
            } else if (pathname.startsWith('/spokesapi/files/')) {
                const parts = pathname.slice('/spokesapi/files/'.length).split('/');
                if (parts.length !== 3) return null;
                const lowerCat = parts[0].toLowerCase();
                if (lowerCat === 'demo-media' || lowerCat === 'email' || lowerCat === 'thumbnail') return null;
                category = decodeURIComponent(parts[0]);
                contextId = decodeURIComponent(parts[1]);
            } else if (pathname.startsWith('/spokesapi/attachments/thumb/') || pathname.startsWith('/internal/attachments/thumb/')) {
                const prefixLen = pathname.startsWith('/spokesapi/attachments/thumb/')
                    ? '/spokesapi/attachments/thumb/'.length
                    : '/internal/attachments/thumb/'.length;
                const parts = pathname.slice(prefixLen).split('/');
                if (parts.length !== 2) return null;
                category = 'chat';
                contextId = decodeURIComponent(parts[0]);
            } else if (pathname.startsWith('/spokesapi/attachments/') || pathname.startsWith('/internal/attachments/')) {
                const prefixLen = pathname.startsWith('/spokesapi/attachments/')
                    ? '/spokesapi/attachments/'.length
                    : '/internal/attachments/'.length;
                const parts = pathname.slice(prefixLen).split('/');
                if (parts.length !== 2 || parts[0].toLowerCase() === 'email') return null;
                category = 'chat';
                contextId = decodeURIComponent(parts[0]);
            } else {
                return null;
            }

            const variantParams = [];
            parsed.searchParams.forEach((value, key) => {
                const lower = key.toLowerCase();
                if (lower === 't' || lower === 'fn' || lower === '_retry') return;
                variantParams.push([key, value]);
            });
            variantParams.sort((a, b) => a[0].localeCompare(b[0]) || a[1].localeCompare(b[1]));

            const queryStr = variantParams.length > 0
                ? '?' + variantParams.map(([k, v]) => encodeURIComponent(k) + '=' + encodeURIComponent(v)).join('&')
                : '';

            return {
                key: pathname + queryStr,
                category,
                contextId,
                contextKey: `${category}:${contextId}`
            };
        } catch (_) {
            return null;
        }
    }

    function safeRevokeObjectUrl(url) {
        if (!url) return;
        try {
            if (typeof URL !== 'undefined' && typeof URL.revokeObjectURL === 'function') {
                URL.revokeObjectURL(url);
            }
        } catch (_) { }
    }

    function getMemEntry(key) {
        const item = memCache.get(key);
        if (!item) return null;
        // Touch MRU position
        memCache.delete(key);
        memCache.set(key, item);
        return item;
    }

    function putMemEntry(meta, blob, encrypted) {
        const key = meta.key;
        const existing = memCache.get(key);
        if (existing) {
            if (encrypted && !existing.encrypted) existing.encrypted = true;
            memCache.delete(key);
            memCache.set(key, existing);
            return existing;
        }

        const objectUrl = URL.createObjectURL(blob);
        const byteLength = (blob && typeof blob.size === 'number') ? blob.size : 0;
        const item = {
            key,
            objectUrl,
            byteLength,
            encrypted: Boolean(encrypted),
            category: meta.category,
            contextId: meta.contextId,
            activeImgs: new Set(),
            pendingRevoke: false
        };
        memCache.set(key, item);
        memTotalBytes += byteLength;
        evictMemOverLimit();
        return item;
    }

    function evictMemOverLimit() {
        while ((memTotalBytes > MAX_MEM_BYTES || memCache.size > MAX_MEM_ENTRIES) && memCache.size > 0) {
            const oldestEntry = memCache.entries().next().value;
            if (!oldestEntry) break;
            const [oldestKey, oldest] = oldestEntry;
            memCache.delete(oldestKey);
            memTotalBytes = Math.max(0, memTotalBytes - oldest.byteLength);
            if (oldest.activeImgs.size === 0) {
                safeRevokeObjectUrl(oldest.objectUrl);
            } else {
                oldest.pendingRevoke = true;
            }
        }
    }

    function detachMemRef(entry) {
        if (!entry || !entry.memRef) return;
        const ref = entry.memRef;
        entry.memRef = null;
        ref.activeImgs.delete(entry.img);
        if (ref.pendingRevoke && ref.activeImgs.size === 0) {
            safeRevokeObjectUrl(ref.objectUrl);
        }
    }

    function attachMemRef(entry, memItem) {
        if (entry.memRef === memItem) return;
        detachMemRef(entry);
        entry.memRef = memItem;
        memItem.activeImgs.add(entry.img);
    }

    function getMaxConcurrency() {
        try {
            const conn = navigator.connection || navigator.mozConnection || navigator.webkitConnection;
            if (conn && (conn.saveData || conn.effectiveType === 'slow-2g' || conn.effectiveType === '2g' || conn.effectiveType === '3g')) {
                return 2;
            }
        } catch (_) { }

        try {
            const isCoarse = window.matchMedia && window.matchMedia('(pointer: coarse)').matches;
            const isMobileUa = /Android|iPhone|iPad|iPod/i.test(navigator.userAgent || '');
            if (isCoarse || isMobileUa) {
                return 2;
            }
        } catch (_) { }

        return 3;
    }

    function markLoaded(img, instant) {
        if (!img) return;
        delete img.dataset.error;
        img.dataset.loaded = 'true';
        if (instant) {
            img.dataset.instant = 'true';
        }
        const container = img.closest('.attachment-image, .attachment-video, .media-tile');
        if (container) {
            container.classList.remove('is-error');
            if (instant) {
                container.classList.add('is-instant');
            }
            container.classList.add('is-loaded');
        }
    }

    function markError(img) {
        if (!img) return;
        delete img.dataset.loaded;
        delete img.dataset.instant;
        img.dataset.error = 'true';
        const container = img.closest('.attachment-image, .attachment-video, .media-tile');
        if (container) {
            container.classList.remove('is-loaded', 'is-instant');
            container.classList.add('is-error');
        }
        // When fetch() failed before img.src was ever assigned, notify any DOM @onerror listeners (e.g., lightbox spinner)
        if (!img.getAttribute('src') && typeof img.dispatchEvent === 'function' && typeof Event === 'function') {
            try { img.dispatchEvent(new Event('error')); } catch (_) { }
        }
    }

    function clearLoaded(img) {
        if (!img) return;
        delete img.dataset.loaded;
        delete img.dataset.instant;
        delete img.dataset.error;
        const container = img.closest('.attachment-image, .attachment-video, .media-tile');
        if (container) {
            container.classList.remove('is-loaded', 'is-instant', 'is-error');
        }
    }

    function getPriorityBucket(entry) {
        if (entry.priority === 'lightbox') {
            return -1;
        }
        const isChat = entry.priority === 'chat';
        if (entry.visible) {
            return isChat ? 0 : 1;
        }
        if (entry.near) {
            return isChat ? 2 : 3;
        }
        return 99;
    }

    function compareEntries(a, b) {
        const bucketDiff = getPriorityBucket(a) - getPriorityBucket(b);
        if (bucketDiff !== 0) return bucketDiff;
        const orderDiff = a.order - b.order;
        if (orderDiff !== 0) return orderDiff;
        return a.seq - b.seq;
    }

    function detachListeners(entry) {
        if (entry.onLoad) {
            entry.img.removeEventListener('load', entry.onLoad);
            entry.onLoad = null;
        }
        if (entry.onError) {
            entry.img.removeEventListener('error', entry.onError);
            entry.onError = null;
        }
    }

    function perfNow() {
        return (typeof performance !== 'undefined' && typeof performance.now === 'function')
            ? performance.now()
            : Date.now();
    }

    function perfEpochNow() {
        return (typeof performance !== 'undefined' && typeof performance.timeOrigin === 'number' && typeof performance.now === 'function')
            ? (performance.timeOrigin + performance.now())
            : Date.now();
    }

    function readResourceTiming(url) {
        if (typeof performance === 'undefined' || typeof performance.getEntriesByName !== 'function' || !url) {
            return null;
        }
        try {
            let absUrl = url;
            if (typeof URL !== 'undefined' && typeof location !== 'undefined' && location.href) {
                absUrl = new URL(url, location.href).href;
            }
            const list = performance.getEntriesByName(absUrl, 'resource');
            if (!list || list.length === 0) return null;
            const rt = list[list.length - 1];
            const rawServerTiming = Array.isArray(rt.serverTiming) && rt.serverTiming.length > 0
                ? rt.serverTiming.map(s => ({ name: s.name, dur: Math.round(s.duration * 10) / 10, desc: s.description || '' }))
                : null;
            const transferSize = typeof rt.transferSize === 'number' ? rt.transferSize : null;
            const encodedBodySize = typeof rt.encodedBodySize === 'number' ? rt.encodedBodySize : null;
            const is304 = Boolean(rawServerTiming && rawServerTiming.some(s => s.desc === '304' || s.desc === 'mem-304'));
            const isPureLocalCacheHit = transferSize === 0 && !is304;
            const serverTiming = isPureLocalCacheHit ? null : rawServerTiming;
            const selfHealed = Boolean(!isPureLocalCacheHit && serverTiming && serverTiming.some(s => s.desc === 'self-heal'));
            const cacheHit = is304 || isPureLocalCacheHit;
            const ttfbMs = (!isPureLocalCacheHit && rt.responseStart > 0 && rt.requestStart > 0)
                ? Math.max(0, rt.responseStart - rt.requestStart)
                : null;
            const downloadMs = (!isPureLocalCacheHit && rt.responseEnd > 0 && rt.responseStart > 0)
                ? Math.max(0, rt.responseEnd - rt.responseStart)
                : null;
            return {
                durationMs: Math.round(rt.duration),
                ttfbMs: ttfbMs !== null ? Math.round(ttfbMs) : null,
                downloadMs: downloadMs !== null ? Math.round(downloadMs) : null,
                transferSize: transferSize,
                encodedBodySize: encodedBodySize,
                cacheHit: cacheHit,
                selfHealed: selfHealed,
                serverTiming: serverTiming
            };
        } catch (_) {
            return null;
        }
    }

    function emitMediaPerf(eventType, entry, extra) {
        try {
            const perf = window.spokesPerf;
            if (!perf || typeof perf.recordMediaEvent !== 'function') return;
            const payload = Object.assign({
                type: eventType,
                seq: entry.seq,
                priority: entry.priority,
                order: entry.order,
                visible: entry.visible,
                near: entry.near,
                isSmallThumb: Boolean(entry.src && entry.src.includes('s=sm')),
                retried: Boolean(entry.retried),
                view: entry.view,
                timestampMs: perfEpochNow(),
                queueWaitMs: entry.startedAt && entry.visibleAt ? Math.max(0, Math.round(entry.startedAt - entry.visibleAt)) : null,
                loadMs: entry.finishedAt && entry.startedAt ? Math.max(0, Math.round(entry.finishedAt - entry.startedAt)) : null,
                totalFromRegMs: entry.finishedAt && entry.registeredAt ? Math.max(0, Math.round(entry.finishedAt - entry.registeredAt)) : null,
                concurrencyCap: getMaxConcurrency()
            }, extra || {});
            perf.recordMediaEvent(payload);
        } catch (_) { }
    }

    function detachInFlightSubscriber(entry, abortIfEmpty) {
        if (!entry || !entry.inFlightKey) return;
        const key = entry.inFlightKey;
        entry.inFlightKey = null;
        const flight = inFlightFetches.get(key);
        if (!flight) return;
        flight.subscribers.delete(entry);
        if (abortIfEmpty && flight.subscribers.size === 0) {
            inFlightFetches.delete(key);
            try { flight.controller.abort(); } catch (_) { }
        }
    }

    function cancelEntry(entry, abortNetwork) {
        if (!entry) return;
        const wasLoading = active.has(entry) && entry.state === 'loading';
        if (entry.retryTimer) {
            clearTimeout(entry.retryTimer);
            entry.retryTimer = null;
        }
        detachListeners(entry);
        detachInFlightSubscriber(entry, abortNetwork);
        detachMemRef(entry);
        if (visibleObserver) {
            try { visibleObserver.unobserve(entry.img); } catch (_) { }
        }
        if (nearObserver) {
            try { nearObserver.unobserve(entry.img); } catch (_) { }
        }
        if (active.has(entry)) {
            active.delete(entry);
            if (abortNetwork && entry.state === 'loading' && !entry.spokesMeta) {
                // Non-Spokes direct <img src> path: clearing src aborts the in-flight HTTP/2 stream.
                try { entry.img.removeAttribute('src'); } catch (_) { }
            }
        }
        entry.state = 'cancelled';
        entries.delete(entry.img);
        if (wasLoading && abortNetwork) {
            entry.finishedAt = perfNow();
            emitMediaPerf('aborted', entry);
        }
    }

    function pruneDetached() {
        let changed = false;
        for (const entry of Array.from(entries.values())) {
            if (!entry.img.isConnected) {
                cancelEntry(entry, true);
                changed = true;
            }
        }
        if (changed) {
            schedulePump();
        }
    }

    function unobserveEntry(entry) {
        if (visibleObserver) {
            try { visibleObserver.unobserve(entry.img); } catch (_) { }
        }
        if (nearObserver) {
            try { nearObserver.unobserve(entry.img); } catch (_) { }
        }
    }

    function sanitizeMediaPath(url) {
        if (!url || typeof url !== 'string') return '';
        try {
            const u = new URL(url, window.location.origin);
            return u.origin === window.location.origin ? u.pathname : (u.origin + u.pathname);
        } catch (_) {
            return url.split('?')[0].split('#')[0];
        }
    }

    function applyCachedMemEntry(entry, memItem, cacheTier) {
        if (entry.state === 'cancelled' || !entry.img.isConnected) {
            entries.delete(entry.img);
            return;
        }

        const img = entry.img;
        const nowTs = perfNow();
        if (!entry.visibleAt) entry.visibleAt = nowTs;
        if (!entry.startedAt) entry.startedAt = nowTs;

        attachMemRef(entry, memItem);
        const isInstant = cacheTier === 'memory' || cacheTier === 'disk-idb';

        try { img.decoding = isInstant ? 'sync' : 'async'; } catch (_) { }

        const completeFromCache = () => {
            detachListeners(entry);
            active.delete(entry);
            if (entry.state === 'cancelled' || !img.isConnected) {
                entries.delete(img);
                return;
            }
            entry.state = 'loaded';
            entry.memoryCacheHit = cacheTier === 'memory';
            entry.finishedAt = perfNow();
            markLoaded(img, isInstant);
            unobserveEntry(entry);
            emitMediaPerf('loaded', entry, {
                memoryCacheHit: cacheTier === 'memory',
                cacheHit: cacheTier !== 'network',
                cacheTier: cacheTier,
                transferSize: cacheTier === 'network' ? (entry.networkMeta?.transferSize ?? memItem.byteLength) : 0,
                encodedBodySize: memItem.byteLength,
                ttfbMs: cacheTier === 'network' ? (entry.networkMeta?.ttfbMs ?? null) : null,
                downloadMs: cacheTier === 'network' ? (entry.networkMeta?.downloadMs ?? null) : null,
                selfHealed: Boolean(cacheTier === 'network' && entry.networkMeta?.selfHealed),
                serverTiming: cacheTier === 'network' ? (entry.networkMeta?.serverTiming ?? null) : null
            });
            schedulePump();
        };

        if (img.getAttribute('src') === memItem.objectUrl && img.complete && img.naturalWidth > 0) {
            completeFromCache();
            return;
        }

        entry.onLoad = completeFromCache;
        entry.onError = () => {
            detachListeners(entry);
            active.delete(entry);
            if (entry.state === 'cancelled' || !img.isConnected) {
                entries.delete(img);
                return;
            }
            entry.state = 'error';
            entry.finishedAt = perfNow();
            markError(img);
            emitMediaPerf('error', entry, {
                status: 0,
                errorReason: 'objecturl-decode',
                path: sanitizeMediaPath(entry.src)
            });
            schedulePump();
        };
        img.addEventListener('load', entry.onLoad, { once: true });
        img.addEventListener('error', entry.onError, { once: true });
        img.src = memItem.objectUrl;

        if (img.complete && img.naturalWidth > 0) {
            completeFromCache();
        }
    }

    function triggerTokenRecovery() {
        try {
            if (window._imageRecoveryCoordinator) {
                window._imageRecoveryCoordinator.invokeMethodAsync('TriggerTokenRecovery').catch(() => { });
            }
        } catch (_) { }
    }

    function scheduleTransientRetry(entry, errorMeta = null) {
        if (entry.retried || !entry.src) {
            entry.state = 'error';
            entry.finishedAt = perfNow();
            markError(entry.img);
            emitMediaPerf('error', entry, {
                status: errorMeta && typeof errorMeta.status === 'number' ? errorMeta.status : 0,
                errorReason: (errorMeta && errorMeta.errorReason) || 'retry-exhausted',
                path: sanitizeMediaPath(entry.src)
            });
            schedulePump();
            return;
        }

        entry.retried = true;
        entry.state = 'retrying';
        const expectedSrc = entry.src;
        entry.retryTimer = setTimeout(() => {
            entry.retryTimer = null;
            if (entry.state !== 'retrying' || !entry.img.isConnected || entry.src !== expectedSrc) {
                return;
            }
            if (!entry.spokesMeta) {
                const cleanSrc = expectedSrc.replace(/(&|\?)_retry=\d+/, '');
                entry.effectiveUrl = cleanSrc + (cleanSrc.includes('?') ? '&' : '?') + '_retry=' + Date.now();
            }
            entry.forceNetworkNoStore = true;
            entry.state = 'pending';
            schedulePump();
        }, RETRY_DELAY_MS);
        schedulePump();
    }

    function finishDirectImgLoad(entry, succeeded) {
        detachListeners(entry);
        active.delete(entry);

        if (entry.state === 'cancelled' || !entry.img.isConnected) {
            entries.delete(entry.img);
            schedulePump();
            return;
        }

        if (succeeded) {
            entry.state = 'loaded';
            entry.finishedAt = perfNow();
            markLoaded(entry.img, Boolean(entry.memoryCacheHit));
            const rt = readResourceTiming(entry.effectiveUrl || entry.src);
            emitMediaPerf('loaded', entry, {
                memoryCacheHit: Boolean(entry.memoryCacheHit),
                cacheHit: Boolean(entry.memoryCacheHit || (rt && rt.cacheHit)),
                cacheTier: entry.memoryCacheHit ? 'memory' : (rt && rt.cacheHit ? 'http-cache' : 'network'),
                ttfbMs: rt ? rt.ttfbMs : null,
                downloadMs: rt ? rt.downloadMs : null,
                transferSize: rt ? rt.transferSize : null,
                encodedBodySize: rt ? rt.encodedBodySize : null,
                selfHealed: Boolean(rt && rt.selfHealed),
                serverTiming: rt ? rt.serverTiming : null
            });
            unobserveEntry(entry);
            schedulePump();
            return;
        }

        scheduleTransientRetry(entry, { status: 0, errorReason: 'img-load-error' });
    }

    function startSpokesNetworkFetch(entry) {
        const meta = entry.spokesMeta;
        const url = entry.effectiveUrl || entry.src;
        const startTs = perfNow();
        if (!entry.visibleAt) entry.visibleAt = startTs;
        entry.startedAt = startTs;
        entry.state = 'loading';
        active.add(entry);
        emitMediaPerf('started', entry);

        let flight = inFlightFetches.get(meta.key);
        if (!flight) {
            const controller = typeof AbortController !== 'undefined' ? new AbortController() : { signal: undefined, abort() { } };
            const fetchOpts = {
                method: 'GET',
                credentials: 'include',
                signal: controller.signal
            };
            if (entry.forceNetworkNoStore) {
                fetchOpts.cache = 'no-store';
            }

            const promise = fetch(url, fetchOpts).then(async (resp) => {
                if (!resp.ok) {
                    return { ok: false, status: resp.status };
                }
                const ct = (resp.headers && typeof resp.headers.get === 'function')
                    ? (resp.headers.get('content-type') || '')
                    : '';
                if (ct && !ct.toLowerCase().startsWith('image/')) {
                    return { ok: false, status: 415 };
                }
                const mediaScope = (resp.headers && typeof resp.headers.get === 'function')
                    ? (resp.headers.get('x-spokes-media-scope') || '').toLowerCase()
                    : '';
                const cacheControl = (resp.headers && typeof resp.headers.get === 'function')
                    ? (resp.headers.get('cache-control') || '').toLowerCase()
                    : '';
                const isEncrypted = entry.hintEncrypted || mediaScope === 'encrypted' || cacheControl.includes('no-store');
                const blob = await resp.blob();
                const rt = readResourceTiming(url);
                return {
                    ok: true,
                    status: resp.status,
                    blob,
                    contentType: ct || (blob && blob.type) || 'image/jpeg',
                    encrypted: isEncrypted,
                    rt
                };
            }).catch((err) => {
                if (err && err.name === 'AbortError') {
                    return { ok: false, aborted: true };
                }
                return { ok: false, status: 0, error: err };
            });

            flight = {
                controller,
                subscribers: new Set(),
                promise
            };
            inFlightFetches.set(meta.key, flight);

            promise.finally(() => {
                if (inFlightFetches.get(meta.key) === flight) {
                    inFlightFetches.delete(meta.key);
                }
            });
        }

        flight.subscribers.add(entry);
        entry.inFlightKey = meta.key;

        flight.promise.then((result) => {
            if (entry.inFlightKey === meta.key) {
                entry.inFlightKey = null;
            }
            active.delete(entry);

            if (entry.state === 'cancelled' || !entry.img.isConnected) {
                entries.delete(entry.img);
                schedulePump();
                return;
            }

            if (result && result.aborted) {
                entry.state = 'pending';
                schedulePump();
                return;
            }

            if (result && result.ok && result.blob) {
                const memItem = putMemEntry(meta, result.blob, result.encrypted);

                // Persist to Tier 2 IndexedDB:
                //   - Always for standard (unencrypted) media.
                //   - For encrypted media whenever the user has their vault saved/unlocked on this device
                //     (purged immediately if the user removes their device vault password).
                if (!result.encrypted || vaultPersisted) {
                    idbPut({
                        key: meta.key,
                        blob: result.blob,
                        byteLength: result.blob.size || 0,
                        contentType: result.contentType,
                        category: meta.category,
                        contextId: meta.contextId,
                        contextKey: meta.contextKey,
                        encrypted: result.encrypted ? 1 : 0,
                        lastAccessedAt: Date.now()
                    });
                }

                entry.networkMeta = result.rt || null;
                applyCachedMemEntry(entry, memItem, 'network');
                return;
            }

            const status = result ? result.status : 0;
            if (status === 401 || status === 410) {
                triggerTokenRecovery();
                entry.state = 'error';
                entry.finishedAt = perfNow();
                markError(entry.img);
                emitMediaPerf('error', entry, {
                    status,
                    errorReason: `http-${status}`,
                    path: sanitizeMediaPath(entry.src)
                });
                schedulePump();
                return;
            }

            if (status === 403 || status === 404 || status === 415) {
                entry.state = 'error';
                entry.finishedAt = perfNow();
                markError(entry.img);
                emitMediaPerf('error', entry, {
                    status,
                    errorReason: status === 415 ? 'non-image-content-type' : `http-${status}`,
                    path: sanitizeMediaPath(entry.src)
                });
                schedulePump();
                return;
            }

            // Transient network error or 5xx: single retry with no-store
            scheduleTransientRetry(entry, {
                status,
                errorReason: status > 0 ? `http-${status}` : (result?.error?.name || 'network-error')
            });
        });
    }

    function startLoad(entry) {
        const img = entry.img;
        const url = entry.effectiveUrl || entry.src;
        if (!url || !img.isConnected) {
            cancelEntry(entry, false);
            return;
        }

        // Spokes file URLs: check Tier 1 RAM LRU again before hitting the network
        if (entry.spokesMeta && canUseFetchCache()) {
            const memHit = getMemEntry(entry.spokesMeta.key);
            if (memHit) {
                applyCachedMemEntry(entry, memHit, 'memory');
                return;
            }
            startSpokesNetworkFetch(entry);
            return;
        }

        // Direct <img src> path for external URLs or environments without fetch/ObjectURL
        const startTs = perfNow();
        if (!entry.visibleAt) entry.visibleAt = startTs;
        entry.startedAt = startTs;

        if (img.getAttribute('src') === url && img.complete && img.naturalWidth > 0) {
            entry.state = 'loaded';
            entry.memoryCacheHit = true;
            entry.finishedAt = startTs;
            markLoaded(img, true);
            emitMediaPerf('loaded', entry, { memoryCacheHit: true, cacheHit: true, cacheTier: 'memory', transferSize: 0 });
            return;
        }

        entry.state = 'loading';
        active.add(entry);
        emitMediaPerf('started', entry);

        try { img.decoding = 'async'; } catch (_) { }

        entry.onLoad = () => finishDirectImgLoad(entry, true);
        entry.onError = () => finishDirectImgLoad(entry, false);
        img.addEventListener('load', entry.onLoad, { once: true });
        img.addEventListener('error', entry.onError, { once: true });

        img.src = url;

        if (img.complete && img.naturalWidth > 0) {
            entry.memoryCacheHit = true;
            finishDirectImgLoad(entry, true);
        }
    }

    function probeDiskCache(entry) {
        const meta = entry.spokesMeta;
        entry.diskProbed = true;
        if (!meta || !canUseFetchCache() || !canUseIndexedDb()) {
            entry.state = 'pending';
            if (entry.visible || entry.near) schedulePump();
            return;
        }

        reconcileSession();
        if (!activeDbName) {
            entry.state = 'pending';
            if (entry.visible || entry.near) schedulePump();
            return;
        }

        // Never read persisted encrypted entries if the device vault password is not persisted
        if (entry.hintEncrypted && !vaultPersisted) {
            entry.state = 'pending';
            if (entry.visible || entry.near) schedulePump();
            return;
        }

        entry.state = 'disk-lookup';
        let lookupTimer = null;
        let finished = false;
        const finishLookup = (record) => {
            if (finished) return;
            finished = true;
            if (lookupTimer) {
                clearTimeout(lookupTimer);
                lookupTimer = null;
            }
            if (entry.state !== 'disk-lookup' || !entry.img.isConnected) {
                if (!entry.img.isConnected) entries.delete(entry.img);
                return;
            }

            if (record && record.blob) {
                const isEncrypted = Boolean(record.encrypted) || entry.hintEncrypted;
                if (isEncrypted && !vaultPersisted) {
                    // Vault password was removed concurrently; fall back to network check
                    entry.state = 'pending';
                    if (entry.visible || entry.near) schedulePump();
                    return;
                }
                idbTouch(record);
                const memItem = putMemEntry(meta, record.blob, isEncrypted);
                applyCachedMemEntry(entry, memItem, 'disk-idb');
                return;
            }

            entry.state = 'pending';
            if (entry.visible || entry.near) {
                schedulePump();
            }
        };

        // Never stall an image behind a slow or blocked IndexedDB open/transaction
        lookupTimer = setTimeout(() => finishLookup(null), IDB_LOOKUP_TIMEOUT_MS);
        idbGet(meta.key).then(finishLookup, () => finishLookup(null));
    }

    function pump() {
        pumpScheduled = false;

        for (const entry of Array.from(active)) {
            if (!entry.img.isConnected) {
                cancelEntry(entry, true);
            }
        }

        const maxConcurrency = getMaxConcurrency();
        if (active.size >= maxConcurrency) {
            return;
        }

        const candidates = [];
        for (const entry of entries.values()) {
            if (entry.state !== 'pending') continue;
            if (!entry.img.isConnected) {
                cancelEntry(entry, false);
                continue;
            }
            if (entry.priority === 'lightbox' || entry.visible || entry.near) {
                candidates.push(entry);
            }
        }

        if (candidates.length === 0) {
            return;
        }

        candidates.sort(compareEntries);

        for (let i = 0; i < candidates.length && active.size < maxConcurrency; i++) {
            startLoad(candidates[i]);
        }
    }

    function schedulePump() {
        if (pumpScheduled) return;
        pumpScheduled = true;
        queueMicrotask(pump);
    }

    function probeInitialGeometry(entry) {
        if (entry.priority === 'lightbox') {
            entry.visible = true;
            entry.near = true;
            return;
        }
        try {
            const rect = entry.img.getBoundingClientRect();
            if (rect.width === 0 && rect.height === 0) {
                return;
            }
            const vw = window.innerWidth || document.documentElement.clientWidth || 1280;
            const vh = window.innerHeight || document.documentElement.clientHeight || 800;
            const horizOk = rect.right > 0 && rect.left < vw;
            if (horizOk && rect.bottom > 0 && rect.top < vh) {
                entry.visible = true;
                entry.near = true;
            } else if (horizOk && rect.bottom > -350 && rect.top < vh + 350) {
                entry.near = true;
            }
        } catch (_) { }
    }

    function registerImage(img) {
        if (!(img instanceof HTMLImageElement)) return;
        const src = img.getAttribute('data-media-src');
        if (!src) return;

        reconcileSession();

        const priority = img.getAttribute('data-media-priority') || 'chat';
        const parsedOrder = parseInt(img.getAttribute('data-media-order') || '0', 10);
        const order = Number.isFinite(parsedOrder) ? parsedOrder : 0;
        const hintEncrypted = img.getAttribute('data-media-encrypted') === 'true';
        const spokesMeta = parseSpokesMediaUrl(src);

        const existing = entries.get(img);
        if (existing) {
            if (existing.src === src) {
                existing.priority = priority;
                existing.order = order;
                if (existing.state === 'pending' && (existing.priority === 'lightbox' || existing.visible || existing.near)) {
                    schedulePump();
                }
                return;
            }
            // If only the ephemeral ?t= token rotated while the exact same immutable file is already loaded,
            // keep the rendered image intact without resetting or flashing.
            if (existing.state === 'loaded' &&
                spokesMeta &&
                existing.spokesMeta &&
                existing.spokesMeta.key === spokesMeta.key) {
                existing.src = src;
                existing.effectiveUrl = src;
                existing.priority = priority;
                existing.order = order;
                return;
            }
            cancelEntry(existing, true);
            clearLoaded(img);
        }

        const regTs = perfNow();
        const viewKey = (typeof location !== 'undefined' && location.pathname) ? location.pathname : '/';
        const entry = {
            img,
            src,
            effectiveUrl: src,
            spokesMeta,
            hintEncrypted,
            priority,
            order,
            seq: ++seqCounter,
            view: viewKey,
            registeredAt: regTs,
            visibleAt: null,
            startedAt: null,
            finishedAt: null,
            memoryCacheHit: false,
            visible: false,
            near: false,
            diskProbed: false,
            state: 'pending',
            retried: false,
            forceNetworkNoStore: false,
            retryTimer: null,
            onLoad: null,
            onError: null,
            inFlightKey: null,
            memRef: null,
            networkMeta: null
        };

        entries.set(img, entry);

        // Tier 1 synchronous RAM ObjectURL LRU hit: paint on frame 1 with 0 network & 0 queue wait
        if (spokesMeta && canUseFetchCache()) {
            const memHit = getMemEntry(spokesMeta.key);
            if (memHit) {
                entry.visible = true;
                entry.near = true;
                entry.visibleAt = regTs;
                entry.startedAt = regTs;
                emitMediaPerf('registered', entry);
                applyCachedMemEntry(entry, memHit, 'memory');
                return;
            }
        }

        // Non-Spokes already loaded with exact URL (e.g. DOM move)
        if (!spokesMeta && img.getAttribute('src') === src && img.complete && img.naturalWidth > 0) {
            entry.state = 'loaded';
            entry.visible = true;
            entry.near = true;
            entry.visibleAt = regTs;
            entry.startedAt = regTs;
            entry.finishedAt = regTs;
            entry.memoryCacheHit = true;
            markLoaded(img, true);
            emitMediaPerf('registered', entry);
            emitMediaPerf('loaded', entry, { memoryCacheHit: true, cacheHit: true, cacheTier: 'memory', transferSize: 0 });
            return;
        }

        if (visibleObserver && priority !== 'lightbox') {
            visibleObserver.observe(img);
            nearObserver.observe(img);
            probeInitialGeometry(entry);
        } else {
            entry.visible = true;
            entry.near = true;
        }

        if (entry.visible) {
            entry.visibleAt = regTs;
        }
        emitMediaPerf('registered', entry);

        // Tier 2 persistent IndexedDB lookup (bypasses 3-slot WAN network cap)
        if (spokesMeta && canUseFetchCache() && canUseIndexedDb() && activeDbName) {
            if (entry.visible || entry.near) {
                probeDiskCache(entry);
            }
            return;
        }

        if (entry.visible || entry.near) {
            schedulePump();
        }
    }

    function scanNode(node) {
        if (!node || node.nodeType !== 1) return;
        const el = /** @type {Element} */ (node);
        if (el.tagName === 'IMG' && el.hasAttribute('data-media-src')) {
            registerImage(/** @type {HTMLImageElement} */ (el));
            return;
        }
        if (el.querySelectorAll) {
            const imgs = el.querySelectorAll('img[data-media-src]');
            for (let i = 0; i < imgs.length; i++) {
                registerImage(/** @type {HTMLImageElement} */ (imgs[i]));
            }
        }
    }

    function initObservers() {
        reconcileSession();

        if (typeof IntersectionObserver !== 'undefined') {
            visibleObserver = new IntersectionObserver((ioEntries) => {
                let changed = false;
                for (let i = 0; i < ioEntries.length; i++) {
                    const io = ioEntries[i];
                    const entry = entries.get(/** @type {HTMLImageElement} */ (io.target));
                    if (!entry) continue;
                    const nowVisible = io.isIntersecting;
                    if (entry.visible !== nowVisible) {
                        entry.visible = nowVisible;
                        if (nowVisible) {
                            entry.near = true;
                            if (!entry.visibleAt) {
                                entry.visibleAt = perfNow();
                                emitMediaPerf('visible', entry);
                            }
                            if (!entry.diskProbed && entry.state === 'pending' && entry.spokesMeta && canUseFetchCache() && canUseIndexedDb() && activeDbName) {
                                probeDiskCache(entry);
                                continue;
                            }
                        }
                        changed = true;
                    }
                }
                if (changed) schedulePump();
            }, { root: null, rootMargin: '0px', threshold: 0.01 });

            nearObserver = new IntersectionObserver((ioEntries) => {
                let changed = false;
                for (let i = 0; i < ioEntries.length; i++) {
                    const io = ioEntries[i];
                    const entry = entries.get(/** @type {HTMLImageElement} */ (io.target));
                    if (!entry) continue;
                    const nowNear = io.isIntersecting;
                    if (entry.near !== nowNear) {
                        entry.near = nowNear;
                        if (!nowNear) entry.visible = false;
                        if (nowNear && !entry.diskProbed && entry.state === 'pending' && entry.spokesMeta && canUseFetchCache() && canUseIndexedDb() && activeDbName) {
                            probeDiskCache(entry);
                            continue;
                        }
                        changed = true;
                    }
                }
                if (changed) schedulePump();
            }, { root: null, rootMargin: NEAR_MARGIN, threshold: 0.01 });
        }

        if (typeof MutationObserver !== 'undefined' && document.body) {
            mutationObserver = new MutationObserver((mutations) => {
                let removedAny = false;
                for (let i = 0; i < mutations.length; i++) {
                    const m = mutations[i];
                    if (m.type === 'attributes') {
                        if (m.target && m.target.tagName === 'IMG') {
                            registerImage(/** @type {HTMLImageElement} */ (m.target));
                        }
                        continue;
                    }
                    if (m.removedNodes && m.removedNodes.length > 0) {
                        removedAny = true;
                    }
                    if (m.addedNodes && m.addedNodes.length > 0) {
                        for (let j = 0; j < m.addedNodes.length; j++) {
                            scanNode(m.addedNodes[j]);
                        }
                    }
                }
                if (removedAny) {
                    pruneDetached();
                }
            });

            mutationObserver.observe(document.body, {
                childList: true,
                subtree: true,
                attributes: true,
                attributeFilter: ['data-media-src']
            });
        }

        if (document.body) {
            scanNode(document.body);
        }
    }

    function retryBroken() {
        let changed = false;
        for (const entry of entries.values()) {
            if (!entry.img.isConnected) continue;
            // Only retry genuinely errored entries, or entries whose assigned src broke after load.
            // Never clobber entries in 'pending', 'disk-lookup', 'loading', or 'retrying' (an <img>
            // without a src attribute has complete === true && naturalWidth === 0 per HTML spec).
            const isBrokenLoaded = entry.state === 'loaded' &&
                Boolean(entry.img.getAttribute('src')) &&
                entry.img.complete &&
                entry.img.naturalWidth === 0;
            if (entry.state === 'error' || isBrokenLoaded) {
                entry.retried = false;
                entry.diskProbed = true;
                if (!entry.spokesMeta) {
                    const cleanSrc = entry.src.replace(/(&|\?)_retry=\d+/, '');
                    entry.effectiveUrl = cleanSrc + (cleanSrc.includes('?') ? '&' : '?') + '_retry=' + Date.now();
                }
                entry.forceNetworkNoStore = true;
                entry.state = 'pending';
                clearLoaded(entry.img);
                changed = true;
            }
        }
        if (changed) {
            schedulePump();
        }
    }

    /**
     * Pre-seeds a canonical Spokes media URL directly into Tier-1 RAM LRU (and Tier-2 IndexedDB
     * when permitted by vault persistence rules) from local client bytes (e.g. immediately after
     * uploading a chat image), enabling 0ms frame-1 paint with zero redundant WAN re-download.
     */
    function seedMedia(rawUrl, blob, options) {
        if (!rawUrl || !blob || !canUseFetchCache()) return false;
        const meta = parseSpokesMediaUrl(rawUrl);
        if (!meta) return false;
        const byteLength = typeof blob.size === 'number' ? blob.size : 0;
        if (byteLength <= 0 || byteLength > MAX_DISK_ENTRY_BYTES) return false;

        reconcileSession();
        const isEncrypted = Boolean(options && options.encrypted);
        const contentType = (options && options.contentType) || blob.type || 'image/jpeg';
        const memItem = putMemEntry(meta, blob, isEncrypted);

        if (!isEncrypted || vaultPersisted) {
            idbPut({
                key: meta.key,
                blob,
                byteLength,
                contentType,
                category: meta.category,
                contextId: meta.contextId,
                contextKey: meta.contextKey,
                encrypted: isEncrypted ? 1 : 0,
                lastAccessedAt: Date.now()
            });
        }

        // If any mounted <img> is already waiting on this canonical key, paint it immediately.
        for (const entry of Array.from(entries.values())) {
            if (!entry.img.isConnected) continue;
            if (entry.spokesMeta && entry.spokesMeta.key === meta.key && entry.state !== 'loaded' && entry.state !== 'cancelled') {
                if (entry.retryTimer) {
                    clearTimeout(entry.retryTimer);
                    entry.retryTimer = null;
                }
                detachInFlightSubscriber(entry, true);
                active.delete(entry);
                applyCachedMemEntry(entry, memItem, 'memory');
            }
        }

        return true;
    }

    function seedMediaBase64(rawUrl, base64Data, contentType, options) {
        if (!rawUrl || !base64Data || typeof atob !== 'function' || typeof Blob !== 'function') return false;
        try {
            const bin = atob(base64Data);
            const len = bin.length;
            const bytes = new Uint8Array(len);
            for (let i = 0; i < len; i++) {
                bytes[i] = bin.charCodeAt(i);
            }
            const mime = contentType || 'image/jpeg';
            const blob = new Blob([bytes], { type: mime });
            return seedMedia(rawUrl, blob, Object.assign({}, options || {}, { contentType: mime }));
        } catch (_) {
            return false;
        }
    }

    function setVaultPersisted(enabled) {
        vaultPersisted = Boolean(enabled);
        if (typeof window !== 'undefined' && window.__spokesSession) {
            window.__spokesSession.hasVaultKey = vaultPersisted;
        }
    }

    function clearEncryptedCache() {
        setVaultPersisted(false);

        for (const [key, item] of Array.from(memCache.entries())) {
            if (!item.encrypted) continue;
            memCache.delete(key);
            memTotalBytes = Math.max(0, memTotalBytes - item.byteLength);
            for (const img of Array.from(item.activeImgs)) {
                try { img.removeAttribute('src'); } catch (_) { }
                clearLoaded(img);
            }
            item.activeImgs.clear();
            safeRevokeObjectUrl(item.objectUrl);
        }

        for (const entry of Array.from(entries.values())) {
            if (entry.hintEncrypted && entry.inFlightKey) {
                detachInFlightSubscriber(entry, true);
            }
        }

        return purgeDiskEncryptedEntries();
    }

    function evictContext(category, contextId) {
        if (!category || !contextId) return Promise.resolve();
        const contextKey = `${category}:${contextId}`;

        for (const [key, item] of Array.from(memCache.entries())) {
            if (item.category === category && item.contextId === contextId) {
                memCache.delete(key);
                memTotalBytes = Math.max(0, memTotalBytes - item.byteLength);
                for (const img of Array.from(item.activeImgs)) {
                    try { img.removeAttribute('src'); } catch (_) { }
                    clearLoaded(img);
                }
                item.activeImgs.clear();
                safeRevokeObjectUrl(item.objectUrl);
            }
        }

        return purgeDiskByIndex('by_context', contextKey);
    }

    async function clearAllCache() {
        for (const [, flight] of Array.from(inFlightFetches.entries())) {
            try { flight.controller.abort(); } catch (_) { }
        }
        inFlightFetches.clear();

        for (const [key, item] of Array.from(memCache.entries())) {
            memCache.delete(key);
            for (const img of Array.from(item.activeImgs)) {
                try { img.removeAttribute('src'); } catch (_) { }
                clearLoaded(img);
            }
            item.activeImgs.clear();
            safeRevokeObjectUrl(item.objectUrl);
        }
        memTotalBytes = 0;

        await closeCurrentDb();
        await discoverAndDeleteDatabases(() => true);
    }

    async function getCacheStatsAsync() {
        let diskEntries = 0;
        let diskBytes = 0;
        const db = await openMediaDb();
        if (db) {
            const usage = await computeDiskUsage(db);
            diskEntries = usage.count;
            diskBytes = usage.totalBytes;
            diskApproxBytes = usage.totalBytes;
        }
        return {
            memoryEntries: memCache.size,
            memoryBytes: memTotalBytes,
            diskEntries,
            diskBytes,
            totalEntries: diskEntries > 0 ? diskEntries : memCache.size,
            totalBytes: diskBytes > 0 ? diskBytes : memTotalBytes,
            userScope: getSessionInfo().userScope,
            vaultPersisted
        };
    }

    window.spokesMediaLoader = {
        observe: registerImage,
        cancelDetached: pruneDetached,
        retryBroken: retryBroken,
        reconcileSession: reconcileSession,
        seedMedia: seedMedia,
        seedMediaBase64: seedMediaBase64,
        setVaultPersisted: setVaultPersisted,
        clearEncryptedCache: clearEncryptedCache,
        clearAllCache: clearAllCache,
        evictContext: evictContext,
        getCacheStatsAsync: getCacheStatsAsync,
        getStats: function () {
            let pending = 0;
            let loaded = 0;
            for (const e of entries.values()) {
                if (e.state === 'pending' || e.state === 'disk-lookup') pending++;
                else if (e.state === 'loaded') loaded++;
            }
            return {
                active: active.size,
                pending: pending,
                loaded: loaded,
                total: entries.size,
                maxConcurrency: getMaxConcurrency(),
                memoryEntries: memCache.size,
                memoryBytes: memTotalBytes
            };
        }
    };

    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', initObservers, { once: true });
    } else {
        initObservers();
    }
})();
