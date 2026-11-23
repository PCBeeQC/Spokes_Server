export function initFileHandlers(elementId, dotNetHelper, maxAllowedSize = 268435456) {
    window.spokesUpload?.initDropZone(elementId, dotNetHelper, maxAllowedSize, {
        forwardProcessing: true,
        compressionMaxDim: 3840,
        compressionQuality: 0.9
    });
}
