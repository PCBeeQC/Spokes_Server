import { MediaZoomGestureController } from '../../../js/mediaZoomGesture.js';

let activeGestureHandler = null;

export function initGestureHandler(containerId, dotNetRef, canSwipeHorizontal = true) {
    if (activeGestureHandler) {
        activeGestureHandler.destroy();
    }
    activeGestureHandler = new MediaZoomGestureController({
        container: containerId,
        getTargetElement: () => document.getElementById('lightbox-image') || document.getElementById('lightbox-video'),
        maxScale: 50,
        canSwipeHorizontal,
        enableSwipeDownClose: true,
        onZoomPercentChange: (percent) => {
            if (dotNetRef) {
                dotNetRef.invokeMethodAsync('OnZoomChanged', percent).catch(() => {});
            }
        },
        onSingleTap: () => {
            const videoEl = document.getElementById('lightbox-video');
            if (!videoEl && dotNetRef) {
                dotNetRef.invokeMethodAsync('ToggleFullscreen').catch(() => {});
            }
        },
        onSwipeDownClose: () => {
            if (dotNetRef) {
                dotNetRef.invokeMethodAsync('HandleSwipeDownClose').catch(() => {});
            }
        },
        onSwipeHorizontal: (direction) => {
            if (dotNetRef) {
                dotNetRef.invokeMethodAsync('ChangeImage', direction).catch(() => {});
            }
        },
        onEscape: () => {
            if (dotNetRef) {
                dotNetRef.invokeMethodAsync('HandleEscapeKey').catch(() => {});
            }
        }
    });
}

export function updateImage(targetId, canSwipeHorizontal) {
    if (activeGestureHandler) {
        if (canSwipeHorizontal !== undefined) {
            activeGestureHandler.setCanSwipeHorizontal(canSwipeHorizontal);
        }
        activeGestureHandler.updateMediaTarget(targetId);
    }
}

export function destroyGestureHandler() {
    if (activeGestureHandler) {
        activeGestureHandler.destroy();
        activeGestureHandler = null;
    }
}

export function resetZoom() {
    if (activeGestureHandler) {
        activeGestureHandler.resetZoom(true);
    }
}

export function zoomIn() {
    if (activeGestureHandler) {
        activeGestureHandler.zoomStep(true);
    }
}

export function zoomOut() {
    if (activeGestureHandler) {
        activeGestureHandler.zoomStep(false);
    }
}

export function setZoom(percent) {
    if (activeGestureHandler) {
        activeGestureHandler.zoomToPercent(percent);
    }
}

export function pan(dx, dy) {
    if (activeGestureHandler) {
        activeGestureHandler.panOffset(dx, dy);
    }
}

export function setFullscreen(enabled) {
    const statusBar = window.Capacitor?.Plugins?.StatusBar;
    if (enabled) {
        if (statusBar) {
            statusBar.hide();
        } else if (document.documentElement.requestFullscreen) {
            document.documentElement.requestFullscreen().catch(() => {});
        }
    } else {
        if (statusBar) {
            statusBar.show();
        }
        if (document.fullscreenElement) {
            document.exitFullscreen().catch(() => {});
        }
    }
}
