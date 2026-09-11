window.spokesCroppers = {};

window.initCropper = (elementId) => {
    var el = document.getElementById(elementId);
    if (!el) return;

    if (window.spokesCroppers[elementId]) {
        window.spokesCroppers[elementId].destroy();
    }

    window.spokesCroppers[elementId] = new Cropper(el, {
        aspectRatio: 1,
        viewMode: 1,
        dragMode: 'move',
        autoCropArea: 0.8,
        restore: false,
        guides: true,
        center: true,
        highlight: false,
        cropBoxMovable: true,
        cropBoxResizable: true,
        toggleDragModeOnDblclick: false,
        background: false
    });
};

window.getCroppedImage = (elementId, width) => {
    var cropper = window.spokesCroppers[elementId];
    if (!cropper) return null;

    var canvas = cropper.getCroppedCanvas({
        width: width,
        height: width,
        imageSmoothingEnabled: true,
        imageSmoothingQuality: 'high',
    });
    
    return canvas ? canvas.toDataURL("image/jpeg", 0.9) : null;
};

window.destroyCropper = (elementId) => {
    var cropper = window.spokesCroppers[elementId];
    if (cropper) {
        cropper.destroy();
        delete window.spokesCroppers[elementId];
    }
};
