(function () {
    'use strict';

    const isNative = () => Boolean(window.Capacitor && typeof window.Capacitor.getPlatform === 'function' && window.Capacitor.Plugins)
        && (typeof window.isCapacitorNative === 'function' ? window.isCapacitorNative() : Boolean(window.Capacitor.isNativePlatform?.()));

    const sanitizeDownloadFileName = (rawName, fallback = 'download') => {
        const safeFallback = (typeof fallback === 'string' && fallback.trim()) ? fallback.trim() : 'download';
        if (typeof rawName !== 'string' || !rawName.trim()) {
            return safeFallback;
        }
        const normalized = rawName.replace(/\\/g, '/');
        const segment = normalized.slice(normalized.lastIndexOf('/') + 1);
        let cleaned = '';
        for (let i = 0; i < segment.length; i++) {
            const ch = segment[i];
            const code = segment.charCodeAt(i);
            if (code < 0x20 || code === 0x7F) continue;
            if ((code >= 0x202A && code <= 0x202E) || (code >= 0x2066 && code <= 0x2069) || code === 0x200E || code === 0x200F || code === 0x061C) continue;
            if ('<>:"/\\|?*'.includes(ch)) continue;
            cleaned += ch;
        }
        cleaned = cleaned.trim().replace(/^\.+|\.+$/g, '');
        while (cleaned.includes('..')) {
            cleaned = cleaned.replace(/\.\./g, '_');
        }
        cleaned = cleaned.trim().replace(/^\.+|\.+$/g, '');
        if (!cleaned) {
            return safeFallback;
        }
        if (cleaned.length > 200) {
            const lastDot = cleaned.lastIndexOf('.');
            const ext = lastDot > 0 ? cleaned.slice(lastDot) : '';
            if (ext && ext.length <= 20 && ext.length < 200) {
                const stem = cleaned.slice(0, 200 - ext.length).replace(/[.\s]+$/, '');
                cleaned = stem + ext;
            } else {
                cleaned = cleaned.slice(0, 200).replace(/[.\s]+$/, '');
            }
        }
        return cleaned || safeFallback;
    };

    const redactUrlForLog = (rawUrl) => {
        if (typeof rawUrl !== 'string' || !rawUrl) return '';
        try {
            const parsed = new URL(rawUrl, window.location.origin);
            return `${parsed.origin}${parsed.pathname}`;
        } catch {
            return rawUrl.split('?')[0].split('#')[0];
        }
    };

    window.downloadFile = async (fileName, contentType, content) => {
    const TAG = '[Download:downloadFile]';
    const safeName = sanitizeDownloadFileName(fileName, 'download');
    console.log(`${TAG} called. fileName=${safeName}, contentType=${contentType}, contentLength=${content?.length}`);
    const blob = new Blob([content], { type: contentType });
    console.log(`${TAG} blob created. size=${blob.size}, type=${blob.type}`);

    if (isNative()) {
        console.log(`${TAG} native platform detected. platform=${window.Capacitor.getPlatform()}`);
        try {
            if (window.Capacitor.Plugins.Filesystem && window.Capacitor.Plugins.Share) {
                if (blob.size <= 0) {
                    throw new Error('Cannot share empty download');
                }
                console.log(`${TAG} both plugins available, converting blob to base64...`);
                const base64Data = await new Promise((resolve, reject) => {
                    const reader = new FileReader();
                    reader.onloadend = () => {
                        const result = reader.result;
                        const stripped = result.includes(',') ? result.split(',')[1] : result;
                        console.log(`${TAG} base64 conversion done. base64Length=${stripped.length}`);
                        resolve(stripped);
                    };
                    reader.onerror = (err) => {
                        console.error(`${TAG} FileReader error:`, err);
                        reject(err);
                    };
                    reader.readAsDataURL(blob);
                });

                console.log(`${TAG} calling Filesystem.writeFile. path=${safeName}, directory=CACHE`);
                const writeRes = await window.Capacitor.Plugins.Filesystem.writeFile({
                    path: safeName,
                    data: base64Data,
                    directory: 'CACHE'
                });
                console.log(`${TAG} writeFile succeeded. uri=${redactUrlForLog(writeRes?.uri)}`);

                await shareCapacitorFile(writeRes.uri, safeName, TAG);
                return;
            } else {
                console.warn(`${TAG} plugins missing — skipping native path.`);
            }
        } catch (e) {
            console.error(`${TAG} native share FAILED:`, e, e?.message, e?.stack);
        }
    } else {
        console.log(`${TAG} not native. Capacitor=${!!window.Capacitor}`);
    }

    console.log(`${TAG} using web fallback (anchor.click). NOTE: this does nothing in mobile WebViews.`);
    triggerBlobDownload(blob, safeName);
};

const shareCapacitorFile = async (uri, safeName, TAG) => {
    console.log(`${TAG} calling Share.share.`);
    await window.Capacitor.Plugins.Share.share({
        title: safeName,
        url: uri
    });
    console.log(`${TAG} Share.share completed successfully.`);
};

const fetchChecked = async (url, TAG) => {
    console.log(`${TAG} fetching file...`);
    const response = await fetch(url);
    if (!response.ok) throw new Error(`Network response was not ok: ${response.status} ${response.statusText}`);
    return response;
};

// Reusable fast chunked base64 converter to prevent call stack size exceeded
const bufferToBase64Chunked = (buffer) => {
    let binary = '';
    const bytes = new Uint8Array(buffer);
    const len = bytes.byteLength;
    const chunkSize = 8192;
    for (let i = 0; i < len; i += chunkSize) {
        binary += String.fromCharCode.apply(null, bytes.subarray(i, i + chunkSize));
    }
    return window.btoa(binary);
};

const saveStreamToCapacitorCache = async (reader, savePath, emptyErrorMessage, TAG) => {
    let isFirstChunk = true;
    let totalBytes = 0;

    while (true) {
        const { done, value } = await reader.read();
        if (done) {
            console.log(`${TAG} stream complete. totalBytes=${totalBytes}`);
            break;
        }

        if (!value || value.byteLength === 0) {
            continue;
        }
        totalBytes += value.byteLength;
        const base64Chunk = bufferToBase64Chunked(value);

        if (isFirstChunk) {
            await window.Capacitor.Plugins.Filesystem.writeFile({
                path: savePath,
                data: base64Chunk,
                directory: 'CACHE'
            });
            isFirstChunk = false;
        } else {
            await window.Capacitor.Plugins.Filesystem.appendFile({
                path: savePath,
                data: base64Chunk,
                directory: 'CACHE'
            });
        }
    }

    if (totalBytes <= 0 || isFirstChunk) {
        throw new Error(emptyErrorMessage);
    }

    console.log(`${TAG} calling Filesystem.getUri to get final URI...`);
    const finalFile = await window.Capacitor.Plugins.Filesystem.getUri({
        path: savePath,
        directory: 'CACHE'
    });
    return finalFile.uri;
};

const triggerBlobDownload = (blob, safeName) => {
    const objectUrl = URL.createObjectURL(blob);
    const anchor = document.createElement('a');
    anchor.href = objectUrl;
    anchor.download = safeName;
    document.body.appendChild(anchor);
    anchor.click();
    anchor.remove();
    URL.revokeObjectURL(objectUrl);
};

window.downloadFileFromStream = async (fileName, contentStreamReference) => {
    const TAG = '[Download:downloadFileFromStream]';
    const safeName = sanitizeDownloadFileName(fileName, 'download');
    console.log(`${TAG} called. fileName=${safeName}`);

    if (isNative()) {
        console.log(`${TAG} native platform detected. platform=${window.Capacitor.getPlatform()}`);
        try {
            if (window.Capacitor.Plugins.Filesystem && window.Capacitor.Plugins.Share) {
                console.log(`${TAG} streaming DotNet stream in chunks...`);
                const stream = await contentStreamReference.stream();
                const uri = await saveStreamToCapacitorCache(
                    stream.getReader(),
                    safeName,
                    'Cannot save or share an empty stream.',
                    TAG);

                await shareCapacitorFile(uri, safeName, TAG);
                return;
            } else {
                console.warn(`${TAG} plugins missing — skipping native path.`);
            }
        } catch (e) {
            console.error(`${TAG} native share FAILED:`, e, e?.message, e?.stack);
        }
    }

    console.log(`${TAG} falling back to full memory blob...`);
    const arrayBuffer = await contentStreamReference.arrayBuffer();
    console.log(`${TAG} arrayBuffer obtained. byteLength=${arrayBuffer.byteLength}`);
    const blob = new Blob([arrayBuffer]);
    console.log(`${TAG} blob created. size=${blob.size}`);

    console.log(`${TAG} using web fallback (anchor.click). NOTE: this does nothing in mobile WebViews.`);
    triggerBlobDownload(blob, safeName);
};

window.downloadFileFromUrl = async (fileName, url, forceShareMenu = false) => {
    const TAG = '[Download:downloadFileFromUrl]';
    const safeName = sanitizeDownloadFileName(fileName, 'download');
    console.log(`${TAG} called. fileName=${safeName}, url=${redactUrlForLog(url)}`);
    try {
        const response = await fetchChecked(url, TAG);

        if (isNative()) {
            const platform = window.Capacitor.getPlatform();
            console.log(`${TAG} native platform detected. platform=${platform}`);
            
            // On Android, use the native DownloadManager to put files in the public Downloads folder
            if (platform === 'android' && window.Capacitor.Plugins.BridgeManager && !forceShareMenu) {
                try {
                    const absoluteUrl = new URL(url, window.location.origin).href;
                    console.log(`${TAG} calling BridgeManager.downloadToPublicStorage. absoluteUrl=${redactUrlForLog(absoluteUrl)}`);
                    const authToken = await window.spokesAuth.getRefreshToken();
                    await window.Capacitor.Plugins.BridgeManager.downloadToPublicStorage({
                        url: absoluteUrl,
                        fileName: safeName,
                        authToken: authToken
                    });
                    console.log(`${TAG} DownloadManager successfully enqueued the download.`);
                    return;
                } catch (e) {
                    console.error(`${TAG} BridgeManager download FAILED:`, e);
                    // Fall through to try chunking/share if it fails
                }
            }

            // On iOS (or if BridgeManager fails), use the chunking + Share mechanism
            if (window.Capacitor.Plugins.Filesystem && window.Capacitor.Plugins.Share) {
                if (platform === 'ios' && !forceShareMenu) {
                    console.log(`${TAG} iOS detected for download. Opening in Safari directly...`);
                    window.open(url, '_blank');
                    return;
                }

                try {
                    console.log(`${TAG} streaming network response in chunks...`);
                    const uri = await saveStreamToCapacitorCache(
                        response.body.getReader(),
                        safeName,
                        'Cannot save or share an empty file.',
                        TAG);

                    await shareCapacitorFile(uri, safeName, TAG);
                    return;
                } catch (e) {
                    console.error(`${TAG} native share FAILED:`, e, e?.message, e?.stack);
                    // Instead of failing completely, bubble up or let it hit fallback. 
                    // But if stream is consumed, we can't fall back to blob easily.
                    throw e; 
                }
            }
        }
        
        // If not native or plugins missing, consume as blob
        const blob = await response.blob();
        console.log(`${TAG} fetch complete. blob size=${blob.size}, type=${blob.type}`);
        
        console.log(`${TAG} not native. Capacitor=${!!window.Capacitor}`);
        // Web Share API for desktop and mobile browsers
        const file = new File([blob], safeName || 'shared_file', { type: blob.type });
        if (navigator.canShare && navigator.canShare({ files: [file] })) {
            try {
                console.log(`${TAG} trying Web Share API...`);
                await navigator.share({
                    files: [file],
                    title: safeName
                });
                console.log(`${TAG} Web Share API succeeded.`);
                return; // Web share succeeded
            } catch (err) {
                if (err.name === 'AbortError') {
                    console.log(`${TAG} user cancelled Web Share.`);
                    return; // User canceled the share prompt
                }
                console.error(`${TAG} Web share failed, falling back to download:`, err);
            }
        }

        console.log(`${TAG} using web fallback (anchor.click). NOTE: this does nothing in mobile WebViews.`);
        triggerBlobDownload(blob, safeName);
    } catch (error) {
        if (error && error.message && error.message.toLowerCase().includes('cancel')) {
            console.log(`${TAG} action canceled by user. Ignoring.`);
            return;
        }
        console.error(`${TAG} OUTER download failed:`, error, error?.message, error?.stack);
        console.log(`${TAG} last resort: opening URL in new tab.`);
        window.open(url, '_blank');
    }
};

window.downloadMediaFromUrl = async (fileName, url) => {
    const TAG = '[Download:downloadMediaFromUrl]';
    const safeName = sanitizeDownloadFileName(fileName, 'download');
    console.log(`${TAG} called. fileName=${safeName}, url=${redactUrlForLog(url)}`);
    try {
        const response = await fetchChecked(url, TAG);

        if (isNative()) {
            console.log(`${TAG} native platform detected. platform=${window.Capacitor.getPlatform()}`);
            if (window.Capacitor.Plugins.Filesystem && window.Capacitor.Plugins.Media) {
                try {
                    console.log(`${TAG} streaming network response in chunks...`);
                    const uri = await saveStreamToCapacitorCache(
                        response.body.getReader(),
                        safeName,
                        'Cannot save empty media file.',
                        TAG);

                    console.log(`${TAG} calling Media.savePhoto.`);
                    await window.Capacitor.Plugins.Media.savePhoto({
                        path: uri
                    });
                    console.log(`${TAG} Media.savePhoto succeeded.`);
                    
                    if (window.Capacitor.Plugins.Toast) {
                        await window.Capacitor.Plugins.Toast.show({ text: 'Saved to Camera Roll' });
                    } else {
                        alert('Saved to Camera Roll');
                    }
                    return;
                } catch (e) {
                    console.error(`${TAG} native media save FAILED:`, e, e?.message, e?.stack);
                    console.log(`${TAG} falling back to downloadFileFromUrl.`);
                    return window.downloadFileFromUrl(safeName, url);
                }
            } else {
                console.warn(`${TAG} plugins missing (Filesystem=${!!window.Capacitor.Plugins?.Filesystem}, Media=${!!window.Capacitor.Plugins?.Media}) — falling back to downloadFileFromUrl.`);
                return window.downloadFileFromUrl(safeName, url);
            }
        }

        const blob = await response.blob();
        console.log(`${TAG} fetch complete. blob size=${blob.size}, type=${blob.type}`);

        console.log(`${TAG} using web fallback (anchor.click). NOTE: this does nothing in mobile WebViews.`);
        triggerBlobDownload(blob, safeName);
    } catch (error) {
        console.error(`${TAG} OUTER download media failed:`, error, error?.message, error?.stack);
        console.log(`${TAG} last resort: opening URL in new tab.`);
        window.open(url, '_blank');
    }
};

window.downloadOrOpenDocument = async (fileName, url) => {
    const TAG = '[Download:downloadOrOpenDocument]';
    const safeName = sanitizeDownloadFileName(fileName, 'download');
    let downloadUrl = url;
    if (typeof downloadUrl === 'string' && safeName && !/[?&]fn=/.test(downloadUrl)) {
        if (downloadUrl.startsWith('/spokesapi/') || downloadUrl.startsWith('/internal/attachments/')) {
            const sep = downloadUrl.includes('?') ? '&' : '?';
            downloadUrl = `${downloadUrl}${sep}fn=${encodeURIComponent(safeName)}`;
        }
    }
    console.log(`${TAG} called. fileName=${safeName}, url=${redactUrlForLog(downloadUrl)}`);
    if (isNative()) {
        console.log(`${TAG} native platform detected. Browser.open fails on relative URLs and lacks auth cookies. Redirecting to downloadFileFromUrl...`);
        return window.downloadFileFromUrl(safeName, downloadUrl);
    } else {
        console.log(`${TAG} not native. Capacitor=${!!window.Capacitor}`);
    }

    console.log(`${TAG} using web fallback (anchor.click).`);
    // Web fallback - direct download
    const anchor = document.createElement('a');
    anchor.href = downloadUrl;
    anchor.download = safeName;
    anchor.target = '_blank';
    document.body.appendChild(anchor);
    anchor.click();
    anchor.remove();
};

window.shareFileFromUrl = async (fileName, url) => {
    // Forces the Share Menu even on Android
    const safeName = sanitizeDownloadFileName(fileName, 'download');
    return window.downloadFileFromUrl(safeName, url, true);
};
})();

