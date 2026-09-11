window.downloadFile = async (fileName, contentType, content) => {
    const TAG = '[Download:downloadFile]';
    console.log(`${TAG} called. fileName=${fileName}, contentType=${contentType}, contentLength=${content?.length}`);
    const blob = new Blob([content], { type: contentType });
    console.log(`${TAG} blob created. size=${blob.size}, type=${blob.type}`);

    if (window.Capacitor && window.Capacitor.isNativePlatform && window.Capacitor.isNativePlatform()) {
        console.log(`${TAG} native platform detected. platform=${window.Capacitor.getPlatform()}`);
        try {
            if (window.Capacitor.Plugins.Filesystem && window.Capacitor.Plugins.Share) {
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

                console.log(`${TAG} calling Filesystem.writeFile. path=${fileName || 'download'}, directory=CACHE`);
                const writeRes = await window.Capacitor.Plugins.Filesystem.writeFile({
                    path: fileName || 'download',
                    data: base64Data,
                    directory: 'CACHE'
                });
                console.log(`${TAG} writeFile succeeded. uri=${writeRes?.uri}`);

                console.log(`${TAG} calling Share.share. url=${writeRes?.uri}`);
                await window.Capacitor.Plugins.Share.share({
                    title: fileName,
                    url: writeRes.uri
                });
                console.log(`${TAG} Share.share completed successfully.`);
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
    const url = URL.createObjectURL(blob);
    const anchor = document.createElement("a");
    anchor.href = url;
    anchor.download = fileName;
    anchor.click();
    URL.revokeObjectURL(url);
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

window.downloadFileFromStream = async (fileName, contentStreamReference) => {
    const TAG = '[Download:downloadFileFromStream]';
    console.log(`${TAG} called. fileName=${fileName}`);

    if (window.Capacitor && window.Capacitor.isNativePlatform && window.Capacitor.isNativePlatform()) {
        console.log(`${TAG} native platform detected. platform=${window.Capacitor.getPlatform()}`);
        try {
            if (window.Capacitor.Plugins.Filesystem && window.Capacitor.Plugins.Share) {
                console.log(`${TAG} streaming DotNet stream in chunks...`);
                
                const stream = await contentStreamReference.stream();
                const reader = stream.getReader();
                const savePath = fileName || 'download';
                let isFirstChunk = true;
                let totalBytes = 0;

                while (true) {
                    const { done, value } = await reader.read();
                    if (done) {
                        console.log(`${TAG} stream complete. totalBytes=${totalBytes}`);
                        break;
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

                console.log(`${TAG} calling Filesystem.getUri to get final URI...`);
                const finalFile = await window.Capacitor.Plugins.Filesystem.getUri({
                    path: savePath,
                    directory: 'CACHE'
                });

                console.log(`${TAG} calling Share.share. url=${finalFile?.uri}`);
                await window.Capacitor.Plugins.Share.share({
                    title: fileName,
                    url: finalFile.uri
                });
                console.log(`${TAG} Share.share completed successfully.`);
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
    const url = URL.createObjectURL(blob);
    const anchor = document.createElement('a');
    anchor.href = url;
    anchor.download = fileName || '';
    anchor.click();
    anchor.remove();
    URL.revokeObjectURL(url);
};

window.downloadFileFromUrl = async (fileName, url, forceShareMenu = false) => {
    const TAG = '[Download:downloadFileFromUrl]';
    console.log(`${TAG} called. fileName=${fileName}, url=${url}`);
    try {
        console.log(`${TAG} fetching file...`);
        const response = await fetch(url);
        if (!response.ok) throw new Error(`Network response was not ok: ${response.status} ${response.statusText}`);

        if (window.Capacitor && window.Capacitor.isNativePlatform && window.Capacitor.isNativePlatform()) {
            const platform = window.Capacitor.getPlatform();
            console.log(`${TAG} native platform detected. platform=${platform}`);
            
            // On Android, use the native DownloadManager to put files in the public Downloads folder
            if (platform === 'android' && window.Capacitor.Plugins.BridgeManager && !forceShareMenu) {
                try {
                    const absoluteUrl = new URL(url, window.location.origin).href;
                    console.log(`${TAG} calling BridgeManager.downloadToPublicStorage. absoluteUrl=${absoluteUrl}`);
                    const authToken = await window.spokesAuth.getRefreshToken();
                    await window.Capacitor.Plugins.BridgeManager.downloadToPublicStorage({
                        url: absoluteUrl,
                        fileName: fileName || 'download',
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
                    
                    const reader = response.body.getReader();
                    const savePath = fileName || 'download';
                    let isFirstChunk = true;
                    let totalBytes = 0;

                    while (true) {
                        const { done, value } = await reader.read();
                        if (done) {
                            console.log(`${TAG} stream complete. totalBytes=${totalBytes}`);
                            break;
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

                    console.log(`${TAG} calling Filesystem.getUri to get final URI...`);
                    const finalFile = await window.Capacitor.Plugins.Filesystem.getUri({
                        path: savePath,
                        directory: 'CACHE'
                    });
                    
                    console.log(`${TAG} calling Share.share. url=${finalFile?.uri}`);
                    await window.Capacitor.Plugins.Share.share({
                        title: fileName,
                        url: finalFile.uri
                    });
                    console.log(`${TAG} Share.share completed successfully.`);
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
        const file = new File([blob], fileName || 'shared_file', { type: blob.type });
        if (navigator.canShare && navigator.canShare({ files: [file] })) {
            try {
                console.log(`${TAG} trying Web Share API...`);
                await navigator.share({
                    files: [file],
                    title: fileName
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
        const objectUrl = URL.createObjectURL(blob);
        const anchor = document.createElement('a');
        anchor.href = objectUrl;
        anchor.download = fileName || '';
        document.body.appendChild(anchor);
        anchor.click();
        anchor.remove();
        URL.revokeObjectURL(objectUrl);
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
    console.log(`${TAG} called. fileName=${fileName}, url=${url}`);
    try {
        console.log(`${TAG} fetching file...`);
        const response = await fetch(url);
        if (!response.ok) throw new Error(`Network response was not ok: ${response.status} ${response.statusText}`);

        if (window.Capacitor && window.Capacitor.isNativePlatform && window.Capacitor.isNativePlatform()) {
            console.log(`${TAG} native platform detected. platform=${window.Capacitor.getPlatform()}`);
            if (window.Capacitor.Plugins.Filesystem && window.Capacitor.Plugins.Media) {
                try {
                    console.log(`${TAG} streaming network response in chunks...`);
                    
                    const reader = response.body.getReader();
                    const savePath = fileName || 'download';
                    let isFirstChunk = true;
                    let totalBytes = 0;

                    while (true) {
                        const { done, value } = await reader.read();
                        if (done) break;

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

                    console.log(`${TAG} calling Filesystem.getUri to get final URI...`);
                    const finalFile = await window.Capacitor.Plugins.Filesystem.getUri({
                        path: savePath,
                        directory: 'CACHE'
                    });

                    console.log(`${TAG} calling Media.savePhoto. path=${finalFile?.uri}`);
                    await window.Capacitor.Plugins.Media.savePhoto({
                        path: finalFile.uri
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
                    return window.downloadFileFromUrl(fileName, url);
                }
            } else {
                console.warn(`${TAG} plugins missing (Filesystem=${!!window.Capacitor.Plugins?.Filesystem}, Media=${!!window.Capacitor.Plugins?.Media}) — falling back to downloadFileFromUrl.`);
                return window.downloadFileFromUrl(fileName, url);
            }
        }

        const blob = await response.blob();
        console.log(`${TAG} fetch complete. blob size=${blob.size}, type=${blob.type}`);

        console.log(`${TAG} using web fallback (anchor.click). NOTE: this does nothing in mobile WebViews.`);
        // Web fallback
        const objectUrl = URL.createObjectURL(blob);
        const anchor = document.createElement('a');
        anchor.href = objectUrl;
        anchor.download = fileName || '';
        document.body.appendChild(anchor);
        anchor.click();
        anchor.remove();
        URL.revokeObjectURL(objectUrl);
    } catch (error) {
        console.error(`${TAG} OUTER download media failed:`, error, error?.message, error?.stack);
        console.log(`${TAG} last resort: opening URL in new tab.`);
        window.open(url, '_blank');
    }
};

window.downloadOrOpenDocument = async (fileName, url) => {
    const TAG = '[Download:downloadOrOpenDocument]';
    console.log(`${TAG} called. fileName=${fileName}, url=${url}`);
    if (window.Capacitor && window.Capacitor.isNativePlatform && window.Capacitor.isNativePlatform()) {
        console.log(`${TAG} native platform detected. Browser.open fails on relative URLs and lacks auth cookies. Redirecting to downloadFileFromUrl...`);
        return window.downloadFileFromUrl(fileName, url);
    } else {
        console.log(`${TAG} not native. Capacitor=${!!window.Capacitor}`);
    }

    console.log(`${TAG} using web fallback (anchor.click).`);
    // Web fallback - direct download
    const anchor = document.createElement('a');
    anchor.href = url;
    anchor.download = fileName || '';
    anchor.target = '_blank';
    document.body.appendChild(anchor);
    anchor.click();
    anchor.remove();
};

window.shareFileFromUrl = async (fileName, url) => {
    // Forces the Share Menu even on Android
    return window.downloadFileFromUrl(fileName, url, true);
};
