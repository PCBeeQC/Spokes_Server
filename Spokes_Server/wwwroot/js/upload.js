window.spokesUpload = {

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

    sendPendingProgress: async function() {
        if (window.spokesUpload.isSendingProgress) return;
        window.spokesUpload.isSendingProgress = true;
        
        try {
            const info = window.spokesUpload.pendingProgress;
            if (!info) return;
            const activeId = window.spokesUpload.activeInputId;
            const activeRef = window.spokesUpload.dotNetRefs?.[activeId] || window.spokesUpload.activeDotNetRef;
            if (!activeRef) return;
            
            if (!window.spokesUpload.uploadStartConfirmed) {
                window.spokesUpload.activeUploadStarted = true;
                await window.spokesUpload.robustInvoke(activeRef, 'OnUploadStartedCallback', [0], 60);
                window.spokesUpload.uploadStartConfirmed = true;
            }
            
            const currentInfo = window.spokesUpload.pendingProgress;
            if (currentInfo) {
                if (currentInfo.state === 'downloading') {
                    await window.spokesUpload.robustInvoke(activeRef, 'OnDownloadingCallback', [currentInfo.percent], 3);
                } else if (currentInfo.compressing) {
                    await window.spokesUpload.robustInvoke(activeRef, 'OnCompressingCallback', [currentInfo.percent], 3);
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
            let absoluteUrl = new URL(uploadUrl, window.location.origin).href;
            const dotNetRef = window.spokesUpload.dotNetRefs?.[inputId];

            window.spokesUpload.ensureNativeUploadListener();

            window.spokesUpload.activeInputId = inputId;
            window.spokesUpload.activeDotNetRef = dotNetRef;
            window.spokesUpload.activeUploadStarted = false;
            window.spokesUpload.uploadStartConfirmed = false;
            window.spokesUpload.pendingProgress = null;

            // Get auth token for native HTTP request (Bearer token from Capacitor Preferences)
            const authToken = await window.spokesAuth.getRefreshToken();

            window.Capacitor.Plugins.BridgeManager.uploadFiles({
                url: absoluteUrl,
                accept: accept,
                multiple: multiple,
                authToken: authToken
            }).then(async (result) => {
                if (result.cancelled) {
                    console.log('[spokesUpload] Native upload cancelled');
                    return; // Gracefully exit, leaving UI untouched
                }
                
                const activeId = window.spokesUpload.activeInputId;
                const dynamicRef = window.spokesUpload.dotNetRefs?.[activeId] || window.spokesUpload.activeDotNetRef;

                if (dynamicRef) {
                    if (!window.spokesUpload.uploadStartConfirmed) {
                        try {
                            window.spokesUpload.activeUploadStarted = true;
                            await window.spokesUpload.robustInvoke(dynamicRef, 'OnUploadStartedCallback', [0], 60);
                            window.spokesUpload.uploadStartConfirmed = true;
                        } catch (e) {}
                    }
                    if (result.responseJson) {
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
                    if (!window.spokesUpload.uploadStartConfirmed) {
                        try {
                            window.spokesUpload.activeUploadStarted = true;
                            await window.spokesUpload.robustInvoke(dynamicRef, 'OnUploadStartedCallback', [0], 60);
                            window.spokesUpload.uploadStartConfirmed = true;
                        } catch (e) {}
                    }
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
     * Safely clears the input value so the same file could be picked again
     * @param {string} inputId The ID of the file input element
     */
    clearInput: function(inputId) {
        const input = document.getElementById(inputId);
        if (input) {
            input.value = '';
        }
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
    uploadNativeFiles: function(uris, uploadUrl, dotNetRef) {
        return new Promise(async (resolve, reject) => {
            if (!window.Capacitor?.isNativePlatform?.() || !window.Capacitor?.Plugins?.BridgeManager?.uploadSharedFiles) {
                reject(new Error("Native upload not supported"));
                return;
            }

            console.log('[spokesUpload] Routing to native BridgeManager.uploadSharedFiles with', uris.length, 'URIs');
            const absoluteUrl = new URL(uploadUrl, window.location.origin).href;

            window.spokesUpload.ensureNativeUploadListener();

            window.spokesUpload.activeDotNetRef = dotNetRef;
            window.spokesUpload.activeUploadStarted = false;
            window.spokesUpload.uploadStartConfirmed = false;
            window.spokesUpload.pendingProgress = null;

            // Get auth token for native HTTP request (Bearer token from Capacitor Preferences)
            const authToken = await window.spokesAuth.getRefreshToken();

            window.Capacitor.Plugins.BridgeManager.uploadSharedFiles({
                url: absoluteUrl,
                uris: uris,
                authToken: authToken
            }).then(async (result) => {
                if (result.cancelled) {
                    console.log('[spokesUpload] Native shared upload cancelled');
                    resolve({ cancelled: true });
                    return;
                }
                
                if (dotNetRef) {
                    if (!window.spokesUpload.uploadStartConfirmed) {
                        try {
                            window.spokesUpload.activeUploadStarted = true;
                            await window.spokesUpload.robustInvoke(dotNetRef, 'OnUploadStartedCallback', [0], 60);
                            window.spokesUpload.uploadStartConfirmed = true;
                        } catch (e) {}
                    }
                    if (result.responseJson) {
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

        if (input.dataset.spokesInitialized === 'true') return;
        input.dataset.spokesInitialized = 'true';

        input.addEventListener('change', function() {
            if (!input.files || input.files.length === 0) return;
            
            const uploadUrl = input.getAttribute('data-upload-url');
            if (!uploadUrl) return;

            // Notify Blazor that we are starting
            dotNetRef.invokeMethodAsync('OnUploadStartedCallback', input.files.length).catch(() => {});

            spokesUpload.executeUploadFromInput(input, uploadUrl, dotNetRef, maxAllowedSize, compressionMaxDim, compressionQuality)
                .then(results => {
                    const jsonStr = typeof results === 'string' ? results : JSON.stringify(results);
                    dotNetRef.invokeMethodAsync('OnUploadCompletedCallback', jsonStr).catch(() => {});
                    input.value = ''; // clear input
                })
                .catch(err => {
                    dotNetRef.invokeMethodAsync('OnUploadErrorCallback', err.message).catch(() => {});
                    input.value = ''; // clear input
                });
        });
    },

    /**
     * Uploads files from an input element to the given URL and tracks progress.
     * @param {string} inputId The ID of the file input element
     * @param {string} uploadUrl The API endpoint url
     * @param {object} dotNetRef DotNet reference to trigger callback methods
     * @param {number} maxAllowedSize Max total size in bytes
     * @param {number} compressionMaxDim Max dimension for compression
     * @param {number} compressionQuality Quality for compression
     */
    uploadFromInput: function(inputId, uploadUrl, dotNetRef, maxAllowedSize, compressionMaxDim = 3840, compressionQuality = 0.9) {
        const input = document.getElementById(inputId);
        if (!input) {
            console.error(`[spokesUpload] Input ${inputId} not found`);
            return Promise.resolve([]);
        }
        return this.executeUploadFromInput(input, uploadUrl, dotNetRef, maxAllowedSize, compressionMaxDim, compressionQuality);
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
            let totalSize = 0;
            for (const file of files) {
                totalSize += file.size;
                console.log(`[spokesUpload] File: ${file.name} - Size: ${file.size} bytes`);
            }
            if (totalSize > maxAllowedSize) {
                console.error(`[spokesUpload] Size limit exceeded. Total: ${totalSize}, Max: ${maxAllowedSize}`);
                reject(new Error(`Total file size exceeds the allowed limit of ${Math.round(maxAllowedSize/1024/1024)}MB`));
                return;
            }

            const formData = new FormData();
            const thumbnailPromises = [];
            
            const processFiles = async () => {
                dotNetRef.invokeMethodAsync('OnCompressingCallback', -1).catch(() => {});
                for (let i = 0; i < files.length; i++) {
                    if (files.length > 1) {
                        dotNetRef.invokeMethodAsync('OnCompressingCallback', (i / files.length) * 100).catch(() => {});
                    }
                    const file = files[i];
                    if (file.type && file.type.startsWith('image/') && file.type !== 'image/gif') {
                        try {
                            console.log(`[spokesUpload] Compressing image: ${file.name}`);
                            const compressedBlob = await spokesUpload.compressImage(file, compressionMaxDim, compressionQuality);
                            formData.append('files', compressedBlob, file.name);
                        } catch (e) {
                            console.warn(`[spokesUpload] Compression failed for ${file.name}, using original.`, e);
                            formData.append('files', file);
                        }
                    } else {
                        formData.append('files', file);
                    }
                    
                    if (file.type && file.type.startsWith('video/')) {
                        thumbnailPromises.push(
                            spokesUpload.extractVideoThumbnail(file).then(blob => ({ fileName: file.name, blob: blob }))
                            .catch(() => null)
                        );
                    }
                }
                
                console.log('[spokesUpload] Payload constructed, beginning upload request...');
                spokesUpload.performUpload(formData, uploadUrl, dotNetRef, resolve, reject, thumbnailPromises);
            };
            
            processFiles();
        });
    },

    /**
     * Uploads a dropped or pasted File object
     */
    uploadFileObject: function(file, uploadUrl, dotNetRef, compressionMaxDim = 3840, compressionQuality = 0.9) {
        return new Promise(async (resolve, reject) => {
            const formData = new FormData();
            dotNetRef.invokeMethodAsync('OnCompressingCallback', -1).catch(() => {});
            
            if (file.type && file.type.startsWith('image/') && file.type !== 'image/gif') {
                try {
                    console.log(`[spokesUpload] Compressing image: ${file.name}`);
                    const compressedBlob = await spokesUpload.compressImage(file, compressionMaxDim, compressionQuality);
                    formData.append('files', compressedBlob, file.name);
                } catch (e) {
                    console.warn(`[spokesUpload] Compression failed for ${file.name}, using original.`, e);
                    formData.append('files', file);
                }
            } else {
                formData.append('files', file);
            }
            
            const thumbnailPromises = [];
            if (file.type && file.type.startsWith('video/')) {
                thumbnailPromises.push(
                    spokesUpload.extractVideoThumbnail(file).then(blob => ({ fileName: file.name, blob: blob }))
                    .catch(() => null)
                );
            }
            
            this.performUpload(formData, uploadUrl, dotNetRef, resolve, reject, thumbnailPromises);
        });
    },

    performUpload: function(formData, uploadUrl, dotNetRef, resolve, reject, thumbnailPromises = []) {
        const xhr = new XMLHttpRequest();
        window.spokesUpload.activeXhr = xhr;
        const startTime = performance.now();
        let totalBytes = 0;
        let transferDoneTime = null;
        let lastProgressNotify = 0;
            
        xhr.upload.addEventListener("progress", function(e) {
            if (e.lengthComputable) {
                totalBytes = e.total;
                if (dotNetRef) {
                    const now = performance.now();
                    if (now - lastProgressNotify > 500 || e.loaded === e.total) {
                        lastProgressNotify = now;
                        const percentComplete = (e.loaded / e.total) * 100;
                        dotNetRef.invokeMethodAsync('OnProgressUpdateCallback', percentComplete);
                    }
                }
                if (e.loaded === e.total && !transferDoneTime) {
                    transferDoneTime = performance.now();
                    const transferSec = (transferDoneTime - startTime) / 1000;
                    const mbps = ((totalBytes * 8) / transferSec / 1000000).toFixed(1);
                    console.log(`[UPLOAD DIAG] Transfer complete: ${(totalBytes/1024/1024).toFixed(2)} MB in ${transferSec.toFixed(2)}s = ${mbps} Mbps`);
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
                    
                    if (thumbnailPromises.length > 0) {
                        const thumbStart = performance.now();
                        try {
                            const thumbs = await Promise.all(thumbnailPromises);
                            for (let i = 0; i < response.length; i++) {
                                const res = response[i];
                                const matchingThumb = thumbs.find(t => t && t.fileName === res.fileName && t.blob);
                                if (matchingThumb) {
                                    const safeName = res.filePath.substring(res.filePath.lastIndexOf('/') + 1);
                                    const parsedUrl = new URL(uploadUrl, window.location.origin);
                                    parsedUrl.pathname = parsedUrl.pathname.replace('/spokesapi/files/', '/spokesapi/files/thumbnail/') + '/' + encodeURIComponent(safeName);
                                    const thumbUrl = parsedUrl.href;
                                    const thumbFormData = new FormData();
                                    thumbFormData.append('file', matchingThumb.blob, safeName + '_thumb.jpg');
                                    
                                    const fetchRes = await fetch(thumbUrl, { method: 'POST', body: thumbFormData });
                                    if (fetchRes.ok) {
                                        res.hasThumbnail = true;
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
                reject(new Error(`Upload failed with status ${xhr.status}: ${xhr.responseText}`));
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
                        if (w > 800) {
                            h = Math.floor(h * (800 / w));
                            w = 800;
                        }
                        canvas.width = w;
                        canvas.height = h;
                        const ctx = canvas.getContext('2d');
                        ctx.drawImage(video, 0, 0, w, h);
                        
                        canvas.toBlob((blob) => {
                            cleanup();
                            resolve(blob);
                        }, 'image/jpeg', 0.7);
                    } catch (e) {
                        cleanup();
                        resolve(null);
                    }
                };

                video.onerror = (e) => {
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
                const finalQuality = isPng ? undefined : quality;
                
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
    }
};
