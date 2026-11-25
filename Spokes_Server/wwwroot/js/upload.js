window.spokesUpload = {

    MAX_LOCAL_PREVIEW_SEED_COUNT: 8,

    /**
     * Canonical upload cache pre-seeder shared across Web, Desktop, Android, and iOS upload paths.
     * Populates window.spokesMediaLoader with local bytes immediately upon HTTP 200 so the sender's
     * chat bubble paints on frame 1 with zero skeleton wait and zero redundant WAN re-download.
     */
    seedUploadedMediaPreviews: function(results, previewItems, defaultEncrypted = false) {
        const loader = window.spokesMediaLoader;
        if (!loader || !Array.isArray(results) || results.length === 0 || !previewItems) return;

        for (let i = 0; i < results.length; i++) {
            const res = results[i];
            const item = Array.isArray(previewItems) ? previewItems[i] : previewItems[String(i)];
            if (!res || !res.filePath || !item) continue;

            const cleanPath = String(res.filePath).split('?')[0].split('#')[0];
            const isRasterImage = /\.(jpg|jpeg|png|gif|webp|bmp)$/i.test(cleanPath);
            const useThumb = Boolean(res.hasThumbnail) || isRasterImage;
            const targetUrl = (useThumb && cleanPath.startsWith('/spokesapi/files/'))
                ? cleanPath.replace('/spokesapi/files/', '/spokesapi/files/thumb/')
                : cleanPath;
            const encrypted = typeof res.isEncrypted === 'boolean' ? res.isEncrypted : Boolean(defaultEncrypted);
            const contentType = item.contentType || res.contentType || 'image/jpeg';

            try {
                if (item.blob && typeof loader.seedMedia === 'function') {
                    loader.seedMedia(targetUrl, item.blob, { encrypted, contentType });
                } else if (item.base64 && typeof loader.seedMediaBase64 === 'function') {
                    loader.seedMediaBase64(targetUrl, item.base64, contentType, { encrypted });
                }
            } catch (e) {
                console.warn('[spokesUpload] Failed to pre-seed media cache for', targetUrl, e);
            }
        }
    },

    _seedNativeUploadResult: function(result) {
        if (!result || !result.responseJson || !result.localPreviews) return;
        try {
            const parsed = JSON.parse(result.responseJson);
            if (Array.isArray(parsed) && parsed.length > 0) {
                window.spokesUpload.seedUploadedMediaPreviews(parsed, result.localPreviews, Boolean(result.isEncrypted));
            }
        } catch (e) {
            console.warn('[spokesUpload] Failed parsing native upload response for preview pre-seed:', e);
        }
    },

    robustInvoke: async function(dotNetRef, methodName, argsArray, maxRetries = 120) {
        if (!dotNetRef) return null;
        for (let i = 0; i < maxRetries; i++) {
            try {
                return await dotNetRef.invokeMethodAsync(methodName, ...(argsArray || []));
            } catch (err) {
                if (err.message && (err.message.includes("disposed") || err.message.includes("Object reference not set"))) {
                    console.warn(`[spokesUpload] dotNetRef disposed. Cancelling robustInvoke for ${methodName}.`);
                    throw err;
                }
                console.warn(`[spokesUpload] Retry ${i + 1}/${maxRetries} for ${methodName} due to error:`, err);
                await new Promise(r => setTimeout(r, 1000));
            }
        }
        throw new Error(`Max retries reached for ${methodName}`);
    },

    confirmNativeUploadStarted: async function(dotNetRef, swallowErrors = false) {
        if (!dotNetRef || window.spokesUpload.uploadStartConfirmed) return;
        try {
            await window.spokesUpload.robustInvoke(dotNetRef, 'OnUploadStartedCallback', [0], 60);
            window.spokesUpload.uploadStartConfirmed = true;
        } catch (e) {
            if (!swallowErrors) throw e;
        }
    },

    beginNativeUpload: async function(uploadUrl, dotNetRef, inputId = null) {
        const absoluteUrl = new URL(uploadUrl, window.location.origin).href;
        window.spokesUpload.ensureNativeUploadListener();
        if (inputId !== null) {
            window.spokesUpload.activeInputId = inputId;
        }
        window.spokesUpload.activeDotNetRef = dotNetRef;
        window.spokesUpload.uploadStartConfirmed = false;
        window.spokesUpload.pendingProgress = null;
        const authToken = await window.spokesAuth.getRefreshToken();
        return { absoluteUrl, authToken };
    },

    sendPendingProgress: async function() {
        if (window.spokesUpload.isSendingProgress) return;
        window.spokesUpload.isSendingProgress = true;
        
        try {
            const info = window.spokesUpload.pendingProgress;
            if (!info) return;
            const activeId = window.spokesUpload.activeInputId;
            const activeRef = window.spokesUpload.dotNetRefs?.[activeId] || window.spokesUpload.activeDotNetRef;
            if (!activeRef) return;
            
            await window.spokesUpload.confirmNativeUploadStarted(activeRef, false);
            
            const currentInfo = window.spokesUpload.pendingProgress;
            if (currentInfo) {
                if (currentInfo.state === 'downloading') {
                    await window.spokesUpload.robustInvoke(activeRef, 'OnDownloadingCallback', [currentInfo.percent], 3);
                } else if (currentInfo.compressing) {
                    await window.spokesUpload.robustInvoke(activeRef, 'OnCompressingCallback', [currentInfo.percent], 3);
                } else if (currentInfo.state === 'processing') {
                    await window.spokesUpload.robustInvoke(activeRef, 'OnProcessingCallback', [currentInfo.statusText || "Finishing up..."], 3);
                } else {
                    await window.spokesUpload.robustInvoke(activeRef, 'OnProgressUpdateCallback', [currentInfo.percent], 3);
                }
                
                if (window.spokesUpload.pendingProgress === currentInfo) {
                    window.spokesUpload.pendingProgress = null;
                }
            }
        } catch (e) {
            console.warn("[spokesUpload] Progress send failed", e);
        } finally {
            window.spokesUpload.isSendingProgress = false;
        }
    },

    /**
     * Ensures the native BridgeManager uploadProgress listener is registered exactly once.
     * Shared between openFilePicker and uploadNativeFiles to avoid duplicate listener code.
     */
    ensureNativeUploadListener: function() {
        if (window.spokesUpload.nativeListenerRegistered) return;
        
        window.Capacitor.Plugins.BridgeManager.addListener('uploadProgress', (info) => {
            if (info.error) {
                console.error('[spokesUpload] Native compression error:', info.error);
            }
            const activeId = window.spokesUpload.activeInputId;
            const activeRef = window.spokesUpload.dotNetRefs?.[activeId] || window.spokesUpload.activeDotNetRef;
            if (activeRef) {
                window.spokesUpload.pendingProgress = info;
                window.spokesUpload.sendPendingProgress();
            }
        });

        setInterval(() => {
            if (window.spokesUpload.pendingProgress) {
                window.spokesUpload.sendPendingProgress();
            }
        }, 1000);

        window.spokesUpload.nativeListenerRegistered = true;
    },

    /**
     * Triggers the file selection dialog bypasses CSP eval restrictions
     * @param {string} inputId The ID of the file input element
     */
    openFilePicker: async function(inputId) {
        console.log('[spokesUpload] openFilePicker called for input:', inputId);
        
        // Remove focus from the button that was just clicked 
        // to prevent 'Enter' key from re-triggering it later
        if (document.activeElement && document.activeElement !== document.body) {
            document.activeElement.blur();
        }

        const input = document.getElementById(inputId);
        if (!input) {
            console.error('[spokesUpload] Error: input element NOT FOUND for ID:', inputId);
            return;
        }

        if (window.Capacitor?.isNativePlatform?.() && window.Capacitor?.Plugins?.BridgeManager?.uploadFiles) {
            console.log('[spokesUpload] Routing to native BridgeManager.uploadFiles');

            const uploadUrl = input.getAttribute('data-upload-url');
            const accept = input.getAttribute('accept') || '*/*';
            const multiple = input.hasAttribute('multiple');
            const dotNetRef = window.spokesUpload.dotNetRefs?.[inputId];
            const { absoluteUrl, authToken } = await window.spokesUpload.beginNativeUpload(uploadUrl, dotNetRef, inputId);

            const maxDimension = input.dataset.maxDimension !== undefined ? parseInt(input.dataset.maxDimension, 10) : undefined;
            const quality = input.dataset.quality !== undefined ? parseInt(input.dataset.quality, 10) : undefined;
            const videoMaxDim = input.dataset.videoMaxDimension !== undefined ? parseInt(input.dataset.videoMaxDimension, 10) : undefined;
            const videoBitrate = input.dataset.videoBitrate !== undefined ? parseInt(input.dataset.videoBitrate, 10) : undefined;

            window.Capacitor.Plugins.BridgeManager.uploadFiles({
                url: absoluteUrl,
                accept: accept,
                multiple: multiple,
                authToken: authToken,
                maxDimension: maxDimension,
                quality: quality,
                videoMaxDim: videoMaxDim,
                videoBitrate: videoBitrate
            }).then(async (result) => {
                if (result.cancelled) {
                    console.log('[spokesUpload] Native upload cancelled');
                    return; // Gracefully exit, leaving UI untouched
                }
                
                const activeId = window.spokesUpload.activeInputId;
                const dynamicRef = window.spokesUpload.dotNetRefs?.[activeId] || window.spokesUpload.activeDotNetRef;

                if (dynamicRef) {
                    await window.spokesUpload.confirmNativeUploadStarted(dynamicRef, true);
                    if (result.responseJson) {
                        window.spokesUpload._seedNativeUploadResult(result);
                        await window.spokesUpload.robustInvoke(dynamicRef, 'OnUploadCompletedCallback', [result.responseJson], 60).catch(() => {});
                    } else {
                        await window.spokesUpload.robustInvoke(dynamicRef, 'OnUploadErrorCallback', ["Native upload failed"], 60).catch(() => {});
                    }
                }
            }).catch(async (err) => {
                console.error('[spokesUpload] Native upload error', err);
                const activeId = window.spokesUpload.activeInputId;
                const dynamicRef = window.spokesUpload.dotNetRefs?.[activeId] || window.spokesUpload.activeDotNetRef;

                if (dynamicRef) {
                    await window.spokesUpload.confirmNativeUploadStarted(dynamicRef, true);
                    await window.spokesUpload.robustInvoke(dynamicRef, 'OnUploadErrorCallback', [err.message || "Native upload failed"], 60).catch(() => {});
                }
            }).finally(() => {
                window.spokesUpload.activeDotNetRef = null;
                window.spokesUpload.activeInputId = null;
            });

            return;
        }

        console.log('[spokesUpload] Found input element, triggering click() programmatically.');
        input.click();
    },

    /**
     * Handles dropping files directly onto an element linked to a file input
     * @param {DragEvent} event The drag event
     * @param {string} inputId The ID of the file input element
     */
    handleDrop: function(event, inputId) {
        event.preventDefault();
        const input = document.getElementById(inputId);
        if (!input || !event.dataTransfer || !event.dataTransfer.files || event.dataTransfer.files.length === 0) return;
        input.files = event.dataTransfer.files;
        input.dispatchEvent(new Event('change', { bubbles: true }));
    },

    /**
     * Aborts an active upload if one is running
     */
    abortUpload: function() {
        if (window.Capacitor?.isNativePlatform?.() && window.Capacitor?.Plugins?.BridgeManager?.abortUpload) {
            console.log('[spokesUpload] Aborting native upload');
            window.Capacitor.Plugins.BridgeManager.abortUpload().catch(() => {});
        } else if (window.spokesUpload.activeXhr) {
            console.log('[spokesUpload] Aborting XHR upload');
            window.spokesUpload.activeXhr.abort();
            window.spokesUpload.activeXhr = null;
        }
    },

    /**
     * Uploads a list of native URIs directly via BridgeManager (bypassing JS canvas compression)
     * @param {string[]} uris Array of native URIs
     * @param {string} uploadUrl The API endpoint url
     * @param {object} dotNetRef DotNet reference to trigger callback methods
     */
    uploadNativeFiles: function(uris, uploadUrl, dotNetRef, options = {}) {
        return new Promise(async (resolve, reject) => {
            if (!window.Capacitor?.isNativePlatform?.() || !window.Capacitor?.Plugins?.BridgeManager?.uploadSharedFiles) {
                reject(new Error("Native upload not supported"));
                return;
            }

            console.log('[spokesUpload] Routing to native BridgeManager.uploadSharedFiles with', uris.length, 'URIs');
            const { absoluteUrl, authToken } = await window.spokesUpload.beginNativeUpload(uploadUrl, dotNetRef);

            window.Capacitor.Plugins.BridgeManager.uploadSharedFiles({
                url: absoluteUrl,
                uris: uris,
                authToken: authToken,
                maxDimension: options.maxDimension,
                quality: options.quality,
                videoMaxDim: options.videoMaxDim,
                videoBitrate: options.videoBitrate
            }).then(async (result) => {
                if (result.cancelled) {
                    console.log('[spokesUpload] Native shared upload cancelled');
                    resolve({ cancelled: true });
                    return;
                }
                
                if (dotNetRef) {
                    await window.spokesUpload.confirmNativeUploadStarted(dotNetRef, true);
                    if (result.responseJson) {
                        window.spokesUpload._seedNativeUploadResult(result);
                        try {
                            const parsed = JSON.parse(result.responseJson);
                            resolve(parsed);
                        } catch (e) {
                            resolve(result.responseJson);
                        }
                    } else {
                        reject(new Error("Native upload failed: no responseJson"));
                    }
                } else {
                    window.spokesUpload._seedNativeUploadResult(result);
                    resolve(result);
                }
            }).catch(async (err) => {
                console.error('[spokesUpload] Native shared upload error', err);
                reject(err);
            }).finally(() => {
                window.spokesUpload.activeDotNetRef = null;
            });
        });
    },

    /**
     * Initializes the native change event listener to bypass Blazor's disconnected SignalR state on Mobile Android
     * @param {string} inputId The ID of the file input element
     * @param {object} dotNetRef DotNet reference to trigger callback methods
     * @param {number} maxAllowedSize Max total size in bytes
     * @param {number} compressionMaxDim Max dimension for compression
     * @param {number} compressionQuality Quality for compression
     */
    initializeInput: function(inputId, dotNetRef, maxAllowedSize, compressionMaxDim = 3840, compressionQuality = 0.9) {
        window.spokesUpload.dotNetRefs = window.spokesUpload.dotNetRefs || {};
        window.spokesUpload.dotNetRefs[inputId] = dotNetRef;

        const input = document.getElementById(inputId);
        if (!input) return;

        // Store/update dynamic limits on the input element
        if (maxAllowedSize != null) {
            input.dataset.maxAllowedSize = maxAllowedSize;
        }
        input.dataset.compressionMaxDim = compressionMaxDim;
        input.dataset.compressionQuality = compressionQuality;

        if (input.dataset.spokesInitialized === 'true') return;
        input.dataset.spokesInitialized = 'true';

        input.addEventListener('change', function() {
            if (!input.files || input.files.length === 0) return;
            
            const uploadUrl = input.getAttribute('data-upload-url');
            if (!uploadUrl) return;

            const effectiveLimit = Number(input.dataset.maxAllowedSize) || maxAllowedSize;
            const effectiveMaxDim = Number(input.dataset.compressionMaxDim) || compressionMaxDim;
            const effectiveQuality = Number(input.dataset.compressionQuality) || compressionQuality;

            // Notify Blazor that we are starting
            dotNetRef.invokeMethodAsync('OnUploadStartedCallback', input.files.length).catch(() => {});

            window.spokesUpload.executeUploadFromInput(input, uploadUrl, dotNetRef, effectiveLimit, effectiveMaxDim, effectiveQuality)
                .then(async results => {
                    const jsonStr = typeof results === 'string' ? results : JSON.stringify(results);
                    await window.spokesUpload.robustInvoke(dotNetRef, 'OnUploadCompletedCallback', [jsonStr], 60);
                    input.value = ''; // clear input
                })
                .catch(async err => {
                    await window.spokesUpload.robustInvoke(dotNetRef, 'OnUploadErrorCallback', [err.message || "Upload failed"], 60);
                    input.value = ''; // clear input
                });
        });
    },

    executeUploadFromInput: function(input, uploadUrl, dotNetRef, maxAllowedSize, compressionMaxDim = 3840, compressionQuality = 0.9) {
        console.log(`[spokesUpload] executeUploadFromInput triggered on ${input.id} to ${uploadUrl}`);
        return new Promise((resolve, reject) => {
            if (!input.files || input.files.length === 0) {
                console.warn(`[spokesUpload] No files selected in ${input.id}`);
                resolve([]);
                return;
            }

            const files = Array.from(input.files);
            console.log(`[spokesUpload] Read ${files.length} file(s) from input. Total size validation...`);
            
            // Check size limits
            for (const file of files) {
                console.log(`[spokesUpload] File: ${file.name} - Size: ${file.size} bytes`);
                if (!file.size || file.size <= 0) {
                    console.error(`[spokesUpload] Empty file rejected: ${file.name}`);
                    reject(new Error(`File "${file.name}" is empty (0 bytes) and cannot be uploaded.`));
                    return;
                }
                if (file.size > maxAllowedSize) {
                    console.error(`[spokesUpload] Size limit exceeded. File: ${file.name} (${file.size} bytes), Max: ${maxAllowedSize}`);
                    const limitMb = Math.round(maxAllowedSize / 1024 / 1024);
                    const errorMsg = files.length > 1
                        ? `File "${file.name}" exceeds the allowed limit of ${limitMb}MB`
                        : `Total file size exceeds the allowed limit of ${limitMb}MB`;
                    reject(new Error(errorMsg));
                    return;
                }
            }

            const formData = new FormData();
            const thumbnailPromises = [];
            const previewItems = [];
            const shouldCollectPreviews = files.length <= window.spokesUpload.MAX_LOCAL_PREVIEW_SEED_COUNT;
            
            const processFiles = async () => {
                const hasCompressibleImages = files.some(f => f.type && f.type.startsWith('image/') && f.type !== 'image/gif');
                if (hasCompressibleImages) {
                    dotNetRef.invokeMethodAsync('OnCompressingCallback', -1).catch(() => {});
                }
                for (let i = 0; i < files.length; i++) {
                    if (files.length > 1 && hasCompressibleImages) {
                        dotNetRef.invokeMethodAsync('OnCompressingCallback', (i / files.length) * 100).catch(() => {});
                    }
                    const previewItem = await window.spokesUpload._appendPreparedFile(files[i], formData, thumbnailPromises, compressionMaxDim, compressionQuality, shouldCollectPreviews);
                    previewItems.push(previewItem);
                }
                
                console.log('[spokesUpload] Payload constructed, beginning upload request...');
                window.spokesUpload.performUpload(formData, uploadUrl, dotNetRef, resolve, reject, thumbnailPromises, previewItems);
            };
            
            processFiles();
        });
    },

    _appendPreparedFile: async function(file, formData, thumbnailPromises, compressionMaxDim, compressionQuality, collectPreview = false) {
        let previewItem = null;
        if (file.type && file.type.startsWith('image/') && file.type !== 'image/gif') {
            let preparedBlob = file;
            try {
                console.log(`[spokesUpload] Compressing image: ${file.name}`);
                preparedBlob = await window.spokesUpload.compressImage(file, compressionMaxDim, compressionQuality);
                formData.append('files', preparedBlob, file.name);
            } catch (e) {
                console.warn(`[spokesUpload] Compression failed for ${file.name}, using original.`, e);
                formData.append('files', file);
            }

            if (collectPreview && preparedBlob && preparedBlob.size > 0) {
                try {
                    const thumbBlob = preparedBlob.size <= 512 * 1024
                        ? preparedBlob
                        : await window.spokesUpload.compressImage(preparedBlob, 1280, 0.82);
                    if (thumbBlob && thumbBlob.size > 0) {
                        previewItem = { blob: thumbBlob, contentType: thumbBlob.type || 'image/jpeg' };
                    }
                } catch (_) { }
            }
        } else {
            formData.append('files', file);
            if (collectPreview && file.type === 'image/gif' && file.size > 0 && file.size <= 12 * 1024 * 1024) {
                previewItem = { blob: file, contentType: 'image/gif' };
            }
        }

        if (file.type && file.type.startsWith('video/')) {
            thumbnailPromises.push(
                window.spokesUpload.extractVideoThumbnail(file).then(blob => ({ fileName: file.name, blob: blob }))
                .catch(() => null)
            );
        }

        return previewItem;
    },

    /**
     * Uploads a dropped or pasted File object
     */
    uploadFileObject: function(file, uploadUrl, dotNetRef, compressionMaxDim = 3840, compressionQuality = 0.9) {
        return new Promise(async (resolve, reject) => {
            if (!file || !file.size || file.size <= 0) {
                reject(new Error(`File "${file?.name || 'file'}" is empty (0 bytes) and cannot be uploaded.`));
                return;
            }
            const formData = new FormData();
            if (file.type && file.type.startsWith('image/') && file.type !== 'image/gif') {
                dotNetRef.invokeMethodAsync('OnCompressingCallback', -1).catch(() => {});
            }
            
            const thumbnailPromises = [];
            const previewItem = await window.spokesUpload._appendPreparedFile(file, formData, thumbnailPromises, compressionMaxDim, compressionQuality, true);
            
            window.spokesUpload.performUpload(formData, uploadUrl, dotNetRef, resolve, reject, thumbnailPromises, [previewItem]);
        });
    },

    performUpload: function(formData, uploadUrl, dotNetRef, resolve, reject, thumbnailPromises = [], previewItems = []) {
        const xhr = new XMLHttpRequest();
        window.spokesUpload.activeXhr = xhr;
        const startTime = performance.now();
        let totalBytes = 0;
        let transferDoneTime = null;
        let lastProgressNotify = 0;
            
        xhr.upload.addEventListener("progress", function(e) {
            if (e.lengthComputable) {
                totalBytes = e.total;
                if (e.loaded === e.total && !transferDoneTime) {
                    transferDoneTime = performance.now();
                    lastProgressNotify = transferDoneTime;
                    const transferSec = (transferDoneTime - startTime) / 1000;
                    const mbps = ((totalBytes * 8) / transferSec / 1000000).toFixed(1);
                    console.log(`[UPLOAD DIAG] Transfer complete: ${(totalBytes/1024/1024).toFixed(2)} MB in ${transferSec.toFixed(2)}s = ${mbps} Mbps`);
                    if (dotNetRef) {
                        dotNetRef.invokeMethodAsync('OnProcessingCallback', 'Processing...').catch(() => {});
                    }
                } else if (dotNetRef && e.loaded < e.total) {
                    const now = performance.now();
                    if (now - lastProgressNotify > 500) {
                        lastProgressNotify = now;
                        const percentComplete = (e.loaded / e.total) * 100;
                        dotNetRef.invokeMethodAsync('OnProgressUpdateCallback', percentComplete);
                    }
                }
            }
        });

        xhr.addEventListener("load", async function() {
            const serverResponseTime = performance.now();
            const totalSec = (serverResponseTime - startTime) / 1000;
            const waitSec = transferDoneTime ? (serverResponseTime - transferDoneTime) / 1000 : 0;
            console.log(`[UPLOAD DIAG] Server responded: total=${totalSec.toFixed(2)}s, server-wait=${waitSec.toFixed(2)}s, status=${xhr.status}`);

            if (xhr.status >= 200 && xhr.status < 300) {
                try {
                    const response = JSON.parse(xhr.responseText);
                    console.log('[spokesUpload] Successful JSON parse:', response);
                    const uploadMediaScope = (xhr.getResponseHeader && xhr.getResponseHeader('X-Spokes-Media-Scope') || '').toLowerCase();
                    const defaultEncrypted = uploadMediaScope === 'encrypted';
                    if (previewItems && previewItems.length > 0) {
                        window.spokesUpload.seedUploadedMediaPreviews(response, previewItems, defaultEncrypted);
                    }
                    
                    if (thumbnailPromises.length > 0) {
                        const thumbStart = performance.now();
                        try {
                            const thumbs = await Promise.all(thumbnailPromises);
                            const totalThumbs = response.length;
                            for (let i = 0; i < response.length; i++) {
                                const res = response[i];
                                const matchingThumb = thumbs.find(t => t && t.fileName === res.fileName && t.blob);
                                if (matchingThumb) {
                                    if (dotNetRef) {
                                        dotNetRef.invokeMethodAsync('OnProcessingCallback', `Thumbnails (${i + 1}/${totalThumbs})...`).catch(() => {});
                                    }
                                    const safeName = res.filePath.substring(res.filePath.lastIndexOf('/') + 1);
                                    const parsedUrl = new URL(uploadUrl, window.location.origin);
                                    parsedUrl.pathname = parsedUrl.pathname.replace('/spokesapi/files/', '/spokesapi/files/thumbnail/') + '/' + encodeURIComponent(safeName);
                                    const thumbUrl = parsedUrl.href;
                                    const thumbFormData = new FormData();
                                    thumbFormData.append('file', matchingThumb.blob, safeName + '_thumb.jpg');
                                    
                                    const fetchRes = await fetch(thumbUrl, { method: 'POST', body: thumbFormData });
                                    if (fetchRes.ok) {
                                        res.hasThumbnail = true;
                                        window.spokesUpload.seedUploadedMediaPreviews([res], [{ blob: matchingThumb.blob, contentType: 'image/jpeg' }], defaultEncrypted);
                                    }
                                }
                            }
                        } catch(e) {
                            console.warn("[spokesUpload] Thumbnail upload failed", e);
                        }
                        console.log(`[UPLOAD DIAG] Thumbnail phase: ${((performance.now() - thumbStart)/1000).toFixed(2)}s`);
                    }
                    
                    const endTime = performance.now();
                    console.log(`[UPLOAD DIAG] TOTAL end-to-end: ${((endTime - startTime)/1000).toFixed(2)}s for ${(totalBytes/1024/1024).toFixed(2)} MB`);
                    resolve(response);
                } catch (e) {
                    console.error('[spokesUpload] Upload success but failed to parse response', e, xhr.responseText);
                    resolve(xhr.responseText);
                }
            } else {
                console.error('[spokesUpload] Upload failed with status', xhr.status, xhr.responseText);
                let message = `Upload failed with status ${xhr.status}`;
                try {
                    const parsed = JSON.parse(xhr.responseText);
                    if (parsed.error) message = parsed.error;
                    else if (parsed.message) message = parsed.message;
                    else if (parsed.ErrorMessage) message = parsed.ErrorMessage;
                } catch {}
                reject(new Error(message));
            }
            if (window.spokesUpload.activeXhr === xhr) window.spokesUpload.activeXhr = null;
        });

        xhr.addEventListener("error", function() {
            console.error("[spokesUpload] Network error occurred during upload to", uploadUrl);
            reject(new Error("Network error occurred during upload"));
            if (window.spokesUpload.activeXhr === xhr) window.spokesUpload.activeXhr = null;
        });

        xhr.addEventListener("abort", function() {
            console.warn("[spokesUpload] Upload aborted to", uploadUrl);
            reject(new Error("Upload aborted"));
            if (window.spokesUpload.activeXhr === xhr) window.spokesUpload.activeXhr = null;
        });

        console.log(`[spokesUpload] Sending POST to ${uploadUrl}`);
        xhr.open("POST", uploadUrl, true);
        
        // Antiforgery is opted out at the SpokesControllerBase level for all API controllers (SameSite cookies + [Authorize] provide CSRF protection)
        // xhr.setRequestHeader("RequestVerificationToken", document.getElementById("...").value);

        xhr.send(formData);
    },

    extractVideoThumbnail: function(file) {
        return new Promise((resolve, reject) => {
            let timeoutId;
            let objectUrl;
            let video;

            const cleanup = () => {
                if (timeoutId) clearTimeout(timeoutId);
                if (objectUrl) {
                    URL.revokeObjectURL(objectUrl);
                    objectUrl = null;
                }
                if (video && video.parentNode) {
                    video.parentNode.removeChild(video);
                    video = null;
                }
            };

            timeoutId = setTimeout(() => {
                console.warn("[spokesUpload] Video thumbnail extraction timed out.");
                cleanup();
                resolve(null);
            }, 5000);

            try {
                video = document.createElement('video');
                video.preload = 'auto'; // Ensure browser fetches media frames
                video.muted = true;
                video.playsInline = true;
                video.setAttribute('playsinline', '');
                
                // Hide and append to DOM so desktop browsers don't suspend media decoding
                video.style.display = 'none';
                video.style.opacity = '0';
                video.style.position = 'absolute';
                document.body.appendChild(video);

                objectUrl = URL.createObjectURL(file);
                video.src = objectUrl;

                // Wait for the first frame to actually be loaded before seeking
                video.onloadeddata = () => {
                    video.currentTime = Math.min(1, Math.max(0, (video.duration / 2) || 0));
                };

                video.onseeked = () => {
                    try {
                        const canvas = document.createElement('canvas');
                        let w = video.videoWidth;
                        let h = video.videoHeight;
                        if (w > 1280) {
                            h = Math.floor(h * (1280 / w));
                            w = 1280;
                        }
                        canvas.width = w;
                        canvas.height = h;
                        const ctx = canvas.getContext('2d');
                        ctx.drawImage(video, 0, 0, w, h);
                        
                        canvas.toBlob((blob) => {
                            cleanup();
                            resolve(blob);
                        }, 'image/jpeg', 0.85);
                    } catch (e) {
                        cleanup();
                        resolve(null);
                    }
                };

                video.onerror = () => {
                    cleanup();
                    resolve(null); // Resolve with null instead of reject to not break Promise.all
                };
                
                // Force load on iOS
                video.load();
            } catch (e) {
                cleanup();
                resolve(null);
            }
        });
    },

    compressImage: function(file, maxDim = 3840, quality = 0.9) {
        if (maxDim <= 0) {
            return Promise.resolve(file);
        }
        const numericQuality = quality > 1 ? quality / 100 : quality;
        return new Promise((resolve, reject) => {
            const img = new Image();
            const url = URL.createObjectURL(file);
            img.onload = () => {
                URL.revokeObjectURL(url);
                const canvas = document.createElement('canvas');
                let width = img.width;
                let height = img.height;
                
                if (width > height) {
                    if (width > maxDim) {
                        height = Math.round(height * maxDim / width);
                        width = maxDim;
                    }
                } else {
                    if (height > maxDim) {
                        width = Math.round(width * maxDim / height);
                        height = maxDim;
                    }
                }
                
                canvas.width = width;
                canvas.height = height;
                
                const isPng = file.type === 'image/png';
                const ctx = canvas.getContext('2d', { alpha: isPng });
                ctx.drawImage(img, 0, 0, width, height);
                
                const mimeType = isPng ? 'image/png' : 'image/jpeg';
                const finalQuality = isPng ? undefined : numericQuality;
                
                canvas.toBlob((blob) => {
                    if (blob) resolve(blob);
                    else reject(new Error("Canvas toBlob failed"));
                }, mimeType, finalQuality);
            };
            img.onerror = () => {
                URL.revokeObjectURL(url);
                reject(new Error("Failed to load image"));
            };
            img.src = url;
        });
    },

    /**
     * Imports an existing sample file from Data/DemoMedia on the server into the target category/context.
     * @param {string} uploadUrl The target upload URL (containing category, contextId, and optional token)
     * @param {string} fileName The name of the sample file in Data/DemoMedia
     */
    importDemoMedia: async function(uploadUrl, fileName) {
        let importUrl = uploadUrl.replace('/spokesapi/files/', '/spokesapi/files/demo-media/import/');
        const separator = importUrl.includes('?') ? '&' : '?';
        importUrl += `${separator}fileName=${encodeURIComponent(fileName)}`;

        let headers = {};
        if (window.spokesAuth?.getHeaders) {
            headers = await window.spokesAuth.getHeaders();
        }

        const response = await fetch(importUrl, {
            method: 'POST',
            headers: headers,
            credentials: 'include'
        });

        if (!response.ok) {
            const errText = await response.text();
            throw new Error(errText || 'Failed to import sample media.');
        }

        return await response.text();
    },

    uploadFilesWithDotNet: async function(fileList, dotNetHelper, options = {}) {
        let uploadUrl = null;
        if (dotNetHelper && typeof dotNetHelper.invokeMethodAsync === 'function') {
            try {
                uploadUrl = await dotNetHelper.invokeMethodAsync('GetUploadUrl');
            } catch (e) {
                if (options.allowInputUrlFallback) {
                    console.warn("Could not retrieve upload URL from dotNetHelper:", e);
                }
            }
        }
        if (!uploadUrl && options.allowInputUrlFallback) {
            const fileInput = document.querySelector('input[data-upload-url]');
            if (fileInput) {
                uploadUrl = fileInput.getAttribute('data-upload-url');
            }
        }
        if (!uploadUrl) {
            if (options.allowInputUrlFallback) {
                console.error("Could not find upload input or URL to route pasted file");
            }
            return;
        }
        if (!fileList || fileList.length === 0) return;

        const maxAllowedSize = options.maxAllowedSize;
        if (maxAllowedSize) {
            for (const file of fileList) {
                if (file.size > maxAllowedSize) {
                    const limitMb = Math.round(maxAllowedSize / 1024 / 1024);
                    const errorMsg = fileList.length > 1
                        ? `File "${file.name}" exceeds the allowed limit of ${limitMb}MB`
                        : `Total file size exceeds the allowed limit of ${limitMb}MB`;
                    dotNetHelper.invokeMethodAsync('OnPasteUploadError', errorMsg);
                    return;
                }
            }
        }

        for (const file of fileList) {
            await dotNetHelper.invokeMethodAsync('OnPasteUploadStarted');
            try {
                const proxyDotNetRef = {
                    invokeMethodAsync: async (methodName, ...args) => {
                        if (methodName === 'OnProgressUpdateCallback') {
                            return dotNetHelper.invokeMethodAsync('OnPasteProgressUpdate', ...args);
                        } else if (options.forwardProcessing && methodName === 'OnProcessingCallback') {
                            return dotNetHelper.invokeMethodAsync('OnPasteProcessingUpdate', ...args);
                        }
                    }
                };

                const result = await window.spokesUpload.uploadFileObject(
                    file,
                    uploadUrl,
                    proxyDotNetRef,
                    options.compressionMaxDim,
                    options.compressionQuality);
                const jsonStr = typeof result === 'string' ? result : JSON.stringify(result);
                await dotNetHelper.invokeMethodAsync('OnPasteUploadCompleted', jsonStr);
            } catch (error) {
                console.error(options.errorPrefix || "Error uploading dragged file:", error);
                await dotNetHelper.invokeMethodAsync('OnPasteUploadError', error.message || "Upload failed");
            }
        }
    },

    initDropZone: function(containerId, dotNetHelper, maxAllowedSize = 268435456, options = {}) {
        const container = document.getElementById(containerId);
        if (!container) return;

        container._maxAllowedSize = maxAllowedSize;

        if (container._spokesFileHandlersInit) return;
        container._spokesFileHandlersInit = true;

        let dragCounter = 0;

        container.addEventListener('dragenter', (e) => {
            e.preventDefault();
            dragCounter++;
            dotNetHelper.invokeMethodAsync('SetDraggingState', true);
        });

        container.addEventListener('dragover', (e) => {
            e.preventDefault();
        });

        container.addEventListener('dragleave', (e) => {
            e.preventDefault();
            dragCounter--;
            if (dragCounter <= 0) {
                dragCounter = 0;
                dotNetHelper.invokeMethodAsync('SetDraggingState', false);
            }
        });

        container.addEventListener('drop', (e) => {
            e.preventDefault();
            dragCounter = 0;
            dotNetHelper.invokeMethodAsync('SetDraggingState', false);

            const files = e.dataTransfer.files;
            if (files && files.length > 0) {
                window.spokesUpload.uploadFilesWithDotNet(files, dotNetHelper, {
                    ...options,
                    maxAllowedSize: container._maxAllowedSize || maxAllowedSize
                });
            }
        });
    },

    /**
     * Reusable chunked HTTP file upload primitive (File.slice + sequential PUT chunks with automatic retry).
     * Keeps multi-GB uploads (such as backup archives) completely off the Blazor SignalR circuit and safely
     * below reverse-proxy/Cloudflare per-request body size limits.
     */
    chunked: {
        DEFAULT_CHUNK_SIZE_BYTES: 8 * 1024 * 1024, // 8 MB
        MAX_RETRIES_PER_CHUNK: 3,
        _pendingFiles: {},
        _dotNetRefs: {},
        _activeUploads: {},

        initPicker: function(inputId, dotNetRef, options = {}) {
            const self = window.spokesUpload.chunked;
            self._dotNetRefs[inputId] = { dotNetRef, options };
            const input = document.getElementById(inputId);
            if (!input) return;

            if (input.dataset.chunkedInitialized === 'true') return;
            input.dataset.chunkedInitialized = 'true';

            input.addEventListener('change', () => {
                if (!input.files || input.files.length === 0) return;
                const file = input.files[0];
                input.value = '';
                self._notifyFileSelected(inputId, file);
            });
        },

        openPicker: function(inputId) {
            if (document.activeElement && document.activeElement !== document.body) {
                document.activeElement.blur();
            }
            const input = document.getElementById(inputId);
            if (input) {
                input.click();
            }
        },

        handleDrop: function(event, inputId) {
            event.preventDefault();
            const files = event.dataTransfer?.files;
            if (!files || files.length === 0) return;
            window.spokesUpload.chunked._notifyFileSelected(inputId, files[0]);
        },

        _notifyFileSelected: function(inputId, file) {
            const self = window.spokesUpload.chunked;
            const reg = self._dotNetRefs[inputId];
            if (!reg || !reg.dotNetRef || !file) return;

            const fileToken = (crypto?.randomUUID ? crypto.randomUUID() : `${Date.now()}-${Math.random().toString(36).slice(2)}`);
            self._pendingFiles[fileToken] = file;
            const callbackName = reg.options?.onSelectedMethod || 'OnChunkedFileSelected';
            reg.dotNetRef.invokeMethodAsync(callbackName, fileToken, file.name, file.size).catch((e) => {
                console.error('[spokesUpload.chunked] Failed invoking file selected callback', e);
                delete self._pendingFiles[fileToken];
            });
        },

        releaseFile: function(fileToken) {
            if (!fileToken) return;
            delete window.spokesUpload.chunked._pendingFiles[fileToken];
        },

        _getAuthHeaders: async function() {
            if (window.spokesAuth?.getHeaders) {
                try {
                    return (await window.spokesAuth.getHeaders()) || {};
                } catch {
                    return {};
                }
            }
            return {};
        },

        _extractErrorMessage: function(text, status) {
            if (text) {
                try {
                    const parsed = JSON.parse(text);
                    if (parsed.error) return parsed.error;
                    if (parsed.message) return parsed.message;
                    if (parsed.ErrorMessage) return parsed.ErrorMessage;
                } catch {
                    if (text.length < 300) return text;
                }
            }
            return `Upload failed (HTTP ${status || 'network error'}).`;
        },

        _sendChunkXhr: function(url, blob, headers, activeState, onChunkProgress) {
            return new Promise((resolve, reject) => {
                const xhr = new XMLHttpRequest();
                activeState.xhr = xhr;
                xhr.open('PUT', url, true);
                xhr.withCredentials = true;
                xhr.setRequestHeader('Content-Type', 'application/octet-stream');
                for (const [k, v] of Object.entries(headers || {})) {
                    if (v != null && k.toLowerCase() !== 'content-type') {
                        xhr.setRequestHeader(k, v);
                    }
                }

                xhr.upload.addEventListener('progress', (e) => {
                    if (e.lengthComputable && onChunkProgress) {
                        onChunkProgress(e.loaded);
                    }
                });

                xhr.addEventListener('load', () => {
                    activeState.xhr = null;
                    if (xhr.status >= 200 && xhr.status < 300) {
                        resolve({ status: xhr.status, text: xhr.responseText });
                    } else {
                        const err = new Error(window.spokesUpload.chunked._extractErrorMessage(xhr.responseText, xhr.status));
                        err.status = xhr.status;
                        err.retryable = xhr.status >= 500 || xhr.status === 408 || xhr.status === 429;
                        reject(err);
                    }
                });

                xhr.addEventListener('error', () => {
                    activeState.xhr = null;
                    const err = new Error('Network error while uploading backup chunk.');
                    err.retryable = true;
                    reject(err);
                });

                xhr.addEventListener('abort', () => {
                    activeState.xhr = null;
                    const err = new Error('Upload cancelled.');
                    err.aborted = true;
                    err.retryable = false;
                    reject(err);
                });

                xhr.send(blob);
            });
        },

        startUpload: async function(fileToken, baseUrl, dotNetRef, options = {}) {
            const self = window.spokesUpload.chunked;
            const file = self._pendingFiles[fileToken];
            if (!file) {
                await window.spokesUpload.robustInvoke(dotNetRef, 'OnChunkedUploadError', ['Selected file is no longer available. Please choose the file again.'], 5).catch(() => {});
                return;
            }
            if (!file.size || file.size <= 0) {
                delete self._pendingFiles[fileToken];
                await window.spokesUpload.robustInvoke(dotNetRef, 'OnChunkedUploadError', [`File "${file.name}" is empty (0 bytes).`], 5).catch(() => {});
                return;
            }

            const normalizedBase = baseUrl.replace(/\/+$/, '');
            const activeState = { cancelled: false, uploadId: null, baseUrl: normalizedBase, xhr: null };
            self._activeUploads[fileToken] = activeState;

            try {
                const headers = await self._getAuthHeaders();
                const requestedChunkSize = options.chunkSizeBytes || self.DEFAULT_CHUNK_SIZE_BYTES;

                const initRes = await fetch(`${normalizedBase}/init`, {
                    method: 'POST',
                    credentials: 'include',
                    headers: { ...headers, 'Content-Type': 'application/json' },
                    body: JSON.stringify({
                        fileName: file.name,
                        totalBytes: file.size,
                        chunkSizeBytes: requestedChunkSize
                    })
                });

                const initText = await initRes.text();
                if (!initRes.ok) {
                    throw new Error(self._extractErrorMessage(initText, initRes.status));
                }

                const initData = JSON.parse(initText);
                const uploadId = initData.uploadId;
                const chunkSize = initData.chunkSizeBytes || requestedChunkSize;
                const totalChunks = initData.totalChunks || Math.ceil(file.size / chunkSize);
                activeState.uploadId = uploadId;

                let lastProgressSent = 0;
                const emitProgress = (uploadedBytes, force = false) => {
                    const now = performance.now();
                    if (!force && now - lastProgressSent < 200) return;
                    lastProgressSent = now;
                    const clamped = Math.min(uploadedBytes, file.size);
                    const pct = Math.min(99.9, (clamped / file.size) * 100);
                    dotNetRef.invokeMethodAsync('OnChunkedUploadProgress', pct, clamped, file.size).catch(() => {});
                };

                emitProgress(0, true);

                for (let chunkIndex = 0; chunkIndex < totalChunks; chunkIndex++) {
                    if (activeState.cancelled) {
                        throw Object.assign(new Error('Upload cancelled.'), { aborted: true });
                    }

                    const start = chunkIndex * chunkSize;
                    const end = Math.min(file.size, start + chunkSize);
                    const chunkBlob = file.slice(start, end);
                    const chunkUrl = `${normalizedBase}/${encodeURIComponent(uploadId)}/chunks/${chunkIndex}`;

                    let attempt = 0;
                    while (true) {
                        try {
                            const chunkHeaders = await self._getAuthHeaders();
                            await self._sendChunkXhr(chunkUrl, chunkBlob, chunkHeaders, activeState, (loadedInChunk) => {
                                emitProgress(start + loadedInChunk, false);
                            });
                            emitProgress(end, chunkIndex === totalChunks - 1);
                            break;
                        } catch (err) {
                            if (activeState.cancelled || err.aborted) {
                                throw err;
                            }
                            attempt++;
                            if (!err.retryable || attempt > self.MAX_RETRIES_PER_CHUNK) {
                                throw err;
                            }
                            const delayMs = 800 * Math.pow(2, attempt - 1);
                            console.warn(`[spokesUpload.chunked] Retrying chunk ${chunkIndex} (attempt ${attempt}/${self.MAX_RETRIES_PER_CHUNK}) in ${delayMs}ms:`, err.message);
                            await new Promise(r => setTimeout(r, delayMs));
                        }
                    }
                }

                if (activeState.cancelled) {
                    throw Object.assign(new Error('Upload cancelled.'), { aborted: true });
                }

                const completeHeaders = await self._getAuthHeaders();
                const completeRes = await fetch(`${normalizedBase}/${encodeURIComponent(uploadId)}/complete`, {
                    method: 'POST',
                    credentials: 'include',
                    headers: completeHeaders
                });

                const completeText = await completeRes.text();
                if (!completeRes.ok) {
                    throw new Error(self._extractErrorMessage(completeText, completeRes.status));
                }

                delete self._pendingFiles[fileToken];
                delete self._activeUploads[fileToken];

                dotNetRef.invokeMethodAsync('OnChunkedUploadProgress', 100, file.size, file.size).catch(() => {});
                await window.spokesUpload.robustInvoke(dotNetRef, 'OnChunkedUploadCompleted', [uploadId], 60);
            } catch (err) {
                delete self._activeUploads[fileToken];
                if (activeState.uploadId) {
                    const cancelHeaders = await self._getAuthHeaders();
                    fetch(`${normalizedBase}/${encodeURIComponent(activeState.uploadId)}`, {
                        method: 'DELETE',
                        credentials: 'include',
                        headers: cancelHeaders
                    }).catch(() => {});
                }
                delete self._pendingFiles[fileToken];
                if (activeState.cancelled || err?.aborted) {
                    await window.spokesUpload.robustInvoke(dotNetRef, 'OnChunkedUploadCancelled', [], 10).catch(() => {});
                } else {
                    await window.spokesUpload.robustInvoke(dotNetRef, 'OnChunkedUploadError', [err?.message || 'Backup upload failed.'], 30).catch(() => {});
                }
            }
        },

        cancelUpload: async function(fileToken) {
            const self = window.spokesUpload.chunked;
            const active = self._activeUploads[fileToken];
            if (active) {
                active.cancelled = true;
                if (active.xhr) {
                    try { active.xhr.abort(); } catch {}
                }
                if (active.uploadId && active.baseUrl) {
                    const headers = await self._getAuthHeaders();
                    fetch(`${active.baseUrl}/${encodeURIComponent(active.uploadId)}`, {
                        method: 'DELETE',
                        credentials: 'include',
                        headers: headers
                    }).catch(() => {});
                }
                delete self._activeUploads[fileToken];
            }
            delete self._pendingFiles[fileToken];
        }
    }
};
