export function initFileHandlers(elementId, dotNetHelper, maxAllowedSize = 268435456) {
    const container = document.getElementById(elementId);
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
            handleFiles(files, dotNetHelper, container._maxAllowedSize || maxAllowedSize);
        }
    });
}

async function handleFiles(fileList, dotNetHelper, maxAllowedSize = 268435456) {
    const uploadUrl = await dotNetHelper.invokeMethodAsync('GetUploadUrl');
    if (!uploadUrl) return;

    if (fileList.length === 0) return;

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

    for (const file of fileList) {
        
        await dotNetHelper.invokeMethodAsync('OnPasteUploadStarted');
        try {
            const proxyDotNetRef = {
                invokeMethodAsync: async (methodName, ...args) => {
                    if (methodName === 'OnProgressUpdateCallback') {
                        return dotNetHelper.invokeMethodAsync('OnPasteProgressUpdate', ...args);
                    } else if (methodName === 'OnProcessingCallback') {
                        return dotNetHelper.invokeMethodAsync('OnPasteProcessingUpdate', ...args);
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
