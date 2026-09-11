export function initFileHandlers(elementId, dotNetHelper) {
    const container = document.getElementById(elementId);
    if (!container) return;

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
            handleFiles(files, dotNetHelper);
        }
    });
}

async function handleFiles(fileList, dotNetHelper) {
    const uploadUrl = await dotNetHelper.invokeMethodAsync('GetUploadUrl');
    if (!uploadUrl) return;

    if (fileList.length === 0) return;

    let totalSize = 0;
    const maxAllowedSize = 268435456; // 256MB

    for (let i = 0; i < fileList.length; i++) {
        totalSize += fileList[i].size;
    }

    if (totalSize > maxAllowedSize) {
        dotNetHelper.invokeMethodAsync('OnPasteUploadError', `Total file size exceeds the allowed limit of ${Math.round(maxAllowedSize/1024/1024)}MB`);
        return;
    }

    for (let i = 0; i < fileList.length; i++) {
        const file = fileList[i];
        
        await dotNetHelper.invokeMethodAsync('OnPasteUploadStarted');
        try {
            const proxyDotNetRef = {
                invokeMethodAsync: async (methodName, ...args) => {
                    if (methodName === 'OnProgressUpdateCallback') {
                        return dotNetHelper.invokeMethodAsync('OnPasteProgressUpdate', ...args);
                    }
                }
            };

            const result = await window.spokesUpload.uploadFileObject(file, uploadUrl, proxyDotNetRef, 3840, 0.9);
            let jsonStr = typeof result === 'string' ? result : JSON.stringify(result);
            await dotNetHelper.invokeMethodAsync('OnPasteUploadCompleted', jsonStr);
        } catch (error) {
            console.error("Error uploading dragged file:", error);
            await dotNetHelper.invokeMethodAsync('OnPasteUploadError', error.message || "Upload failed");
        }
    }
}
