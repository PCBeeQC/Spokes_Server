let lightboxDotNetHelper = null;
let lightboxPopStateListener = null;

export function initLightboxState(dotNetRef) {
    if (lightboxDotNetHelper) {
         cleanupLightboxState(false);
    }
    lightboxDotNetHelper = dotNetRef;
    
    // Push a dummy state. The URL remains exactly the same.
    history.pushState({ lightbox: true }, "");

    lightboxPopStateListener = (e) => {
        // This fires when the user hits 'Back'
        if (lightboxDotNetHelper) {
             lightboxDotNetHelper.invokeMethodAsync('HandleBack');
             lightboxDotNetHelper = null;
        }
        if (lightboxPopStateListener) {
             window.removeEventListener('popstate', lightboxPopStateListener);
             lightboxPopStateListener = null;
        }
    };

    window.addEventListener('popstate', lightboxPopStateListener);
}

export function cleanupLightboxState(goBack) {
    if (lightboxPopStateListener) {
        window.removeEventListener('popstate', lightboxPopStateListener);
        lightboxPopStateListener = null;
    }
    lightboxDotNetHelper = null;
    
    // If we're closing it via UI (goBack = true), pop the state to keep history clean.
    if (goBack && history.state && history.state.lightbox) {
        history.back();
    }
}

class LightboxGestureHandler {
    constructor(containerId, dotNetRef, canSwipeHorizontal = true) {
        this.containerId = containerId;
        this.container = document.getElementById(containerId);
        this.dotNetRef = dotNetRef;
        this.canSwipeHorizontal = canSwipeHorizontal;
        this.imageId = "lightbox-image";
        this.imgEl = null;
        this.currentImageSrc = null;

        this.currentScale = 1;
        this.currentX = 0;
        this.currentY = 0;

        // Gesture state
        this.gestureState = 'IDLE'; // IDLE, PANNING, SWIPING_X, SWIPING_Y, ZOOMING, IGNORE
        this.startX = 0;
        this.startY = 0;
        this.startTx = 0;
        this.startTy = 0;
        
        // Zoom state
        this.initialDistance = 0;
        this.initialScale = 1;
        this.initialPinchCenterX = 0;
        this.initialPinchCenterY = 0;

        // Mouse drag state
        this.isMouseDown = false;
        this.isDragging = false;

        // State notification throttling
        this.lastNotifiedPercent = 100;
        this.lastNotificationTime = 0;
        this.notifyTimeout = null;

        // Timers
        this.longPressTimer = null;
        this.singleTapTimeout = null;
        this.lastTapTime = 0;
        this.lastPinchEndTime = 0;

        this.bindEvents();
    }

    ensureImg() {
        if (!this.imgEl || !this.imgEl.isConnected) {
            this.imgEl = document.getElementById(this.imageId || 'lightbox-image');
        }
        return this.imgEl;
    }

    setCanSwipeHorizontal(canSwipe) {
        this.canSwipeHorizontal = Boolean(canSwipe);
    }

    animateSwipe(direction) {
        if (!this.imgEl || !this.dotNetRef) return;
        
        // Disallow horizontal slide-out if only a single image exists
        if ((direction === 'left' || direction === 'right') && !this.canSwipeHorizontal) {
            this.resetZoom(true);
            return;
        }

        // Slide out
        if (direction === 'left') {
            this.currentX = -window.innerWidth;
        } else if (direction === 'right') {
            this.currentX = window.innerWidth;
        } else if (direction === 'down') {
            this.currentY = window.innerHeight;
        }
        
        this.updateTransform(true); // animate out
        
        setTimeout(() => {
            if (this.dotNetRef) {
                if (direction === 'down') {
                    this.dotNetRef.invokeMethodAsync('HandleSwipeDownClose');
                } else {
                    this.pendingAnimateInDirection = direction === 'left' ? 'right' : 'left';
                    this.dotNetRef.invokeMethodAsync('ChangeImage', direction);
                }
            }
        }, 200);
    }

    updateImage(imageId) {
        this.imageId = imageId || "lightbox-image";
        const el = document.getElementById(this.imageId);
        if (!el) return;
        
        const newSrc = el.currentSrc || el.src;
        const isNewImage = !this.currentImageSrc || (newSrc && this.currentImageSrc !== newSrc);
        this.imgEl = el;

        if (this.pendingAnimateInDirection) {
            this.currentImageSrc = newSrc;
            // Snap to opposite side immediately
            const inX = this.pendingAnimateInDirection === 'left' ? -window.innerWidth : window.innerWidth;
            this.currentScale = 1;
            this.currentX = inX;
            this.currentY = 0;
            this.gestureState = 'IDLE';
            this.updateTransform(false); // snap without transition
            
            this.pendingAnimateInDirection = null;

            const slideIn = () => {
                // Force browser reflow
                void this.imgEl.offsetWidth; 
                
                // Animate into center
                this.currentX = 0;
                this.updateTransform(true); // animate in
                this.updateCursor();
                this.notifyZoomChanged();
            };

            // Wait for image to load
            if (this.imgEl.complete && this.imgEl.naturalWidth > 0) {
                slideIn();
            } else {
                this.imgEl.addEventListener('load', slideIn, { once: true });
                this.imgEl.addEventListener('error', slideIn, { once: true });
            }
        } else if (isNewImage) {
            this.currentImageSrc = newSrc;
            this.resetZoom(false);
        } else {
            // Same image re-rendered: preserve scale and transform
            this.updateTransform(false);
            this.updateCursor();
        }
    }

    bindEvents() {
        if (!this.container) return;

        // Touch handlers
        this.onTouchStart = this.onTouchStart.bind(this);
        this.onTouchMove = this.onTouchMove.bind(this);
        this.onTouchEnd = this.onTouchEnd.bind(this);
        this.onTouchCancel = this.onTouchCancel.bind(this);

        // Mouse & pointer handlers
        this.onWheel = this.onWheel.bind(this);
        this.onMouseDown = this.onMouseDown.bind(this);
        this.onMouseMove = this.onMouseMove.bind(this);
        this.onMouseUp = this.onMouseUp.bind(this);
        this.onDblClick = this.onDblClick.bind(this);
        this.onVisibilityChange = this.onVisibilityChange.bind(this);

        this.container.addEventListener('touchstart', this.onTouchStart, { passive: false });
        this.container.addEventListener('touchmove', this.onTouchMove, { passive: false });
        this.container.addEventListener('touchend', this.onTouchEnd);
        this.container.addEventListener('touchcancel', this.onTouchCancel);

        this.container.addEventListener('wheel', this.onWheel, { passive: false });
        this.container.addEventListener('mousedown', this.onMouseDown);
        window.addEventListener('mousemove', this.onMouseMove);
        window.addEventListener('mouseup', this.onMouseUp);
        this.container.addEventListener('dblclick', this.onDblClick);

        document.addEventListener('visibilitychange', this.onVisibilityChange);
    }

    destroy() {
        if (this.container) {
            this.container.removeEventListener('touchstart', this.onTouchStart, { passive: false });
            this.container.removeEventListener('touchmove', this.onTouchMove, { passive: false });
            this.container.removeEventListener('touchend', this.onTouchEnd);
            this.container.removeEventListener('touchcancel', this.onTouchCancel);

            this.container.removeEventListener('wheel', this.onWheel, { passive: false });
            this.container.removeEventListener('mousedown', this.onMouseDown);
            this.container.removeEventListener('dblclick', this.onDblClick);
        }
        window.removeEventListener('mousemove', this.onMouseMove);
        window.removeEventListener('mouseup', this.onMouseUp);
        document.removeEventListener('visibilitychange', this.onVisibilityChange);

        this.clearTimers();
        this.dotNetRef = null;
        this.imgEl = null;
        this.currentImageSrc = null;
        this.container = null;
    }

    clearTimers() {
        if (this.longPressTimer) { clearTimeout(this.longPressTimer); this.longPressTimer = null; }
        if (this.singleTapTimeout) { clearTimeout(this.singleTapTimeout); this.singleTapTimeout = null; }
        if (this.notifyTimeout) { clearTimeout(this.notifyTimeout); this.notifyTimeout = null; }
    }

    getDistance(touches) {
        const dx = touches[0].clientX - touches[1].clientX;
        const dy = touches[0].clientY - touches[1].clientY;
        return Math.sqrt(dx * dx + dy * dy);
    }

    calculatePanBounds(scale) {
        const img = this.ensureImg();
        if (!img || !this.container || scale <= 1.001) {
            return { maxX: 0, maxY: 0 };
        }
        const imgWidth = img.offsetWidth;
        const imgHeight = img.offsetHeight;
        const containerWidth = this.container.clientWidth;
        const containerHeight = this.container.clientHeight;

        const visualWidth = imgWidth * scale;
        const visualHeight = imgHeight * scale;

        const maxX = visualWidth > containerWidth ? (visualWidth - containerWidth) / 2 : 0;
        const maxY = visualHeight > containerHeight ? (visualHeight - containerHeight) / 2 : 0;

        return { maxX, maxY };
    }

    clampOffsets(scale, targetTx, targetTy) {
        const bounds = this.calculatePanBounds(scale);
        const clampedX = Math.max(-bounds.maxX, Math.min(bounds.maxX, targetTx));
        const clampedY = Math.max(-bounds.maxY, Math.min(bounds.maxY, targetTy));
        return { x: clampedX, y: clampedY };
    }

    updateTransform(animate = false) {
        const img = this.ensureImg();
        if (!img) return;
        
        if (isNaN(this.currentScale) || isNaN(this.currentX) || isNaN(this.currentY)) {
            this.currentScale = 1;
            this.currentX = 0;
            this.currentY = 0;
        }

        if (animate) {
            img.style.transition = 'transform 0.2s ease-out, opacity 0.2s ease';
        } else {
            img.style.transition = 'opacity 0.2s ease';
        }
        img.style.transform = `translate(${this.currentX}px, ${this.currentY}px) scale(${this.currentScale})`;
    }

    updateCursor() {
        const img = this.ensureImg();
        if (!img) return;
        if (this.isMouseDown && this.isDragging) {
            img.style.cursor = 'grabbing';
            if (this.container) this.container.style.cursor = 'grabbing';
        } else if (this.currentScale > 1.001) {
            img.style.cursor = 'grab';
            if (this.container) this.container.style.cursor = 'grab';
        } else {
            img.style.cursor = 'zoom-in';
            if (this.container) this.container.style.cursor = 'default';
        }
    }

    notifyZoomChanged() {
        const percent = Math.round(this.currentScale * 100);
        if (percent === this.lastNotifiedPercent) return;

        const now = Date.now();
        if (now - this.lastNotificationTime > 50) {
            this.lastNotificationTime = now;
            this.lastNotifiedPercent = percent;
            if (this.dotNetRef) {
                this.dotNetRef.invokeMethodAsync('OnZoomChanged', percent);
            }
        } else {
            if (this.notifyTimeout) clearTimeout(this.notifyTimeout);
            this.notifyTimeout = setTimeout(() => {
                this.lastNotificationTime = Date.now();
                this.lastNotifiedPercent = percent;
                if (this.dotNetRef) {
                    this.dotNetRef.invokeMethodAsync('OnZoomChanged', percent);
                }
            }, 50);
        }
    }

    resetZoom(animate = true) {
        this.currentScale = 1;
        this.currentX = 0;
        this.currentY = 0;
        this.gestureState = 'IDLE';
        this.isMouseDown = false;
        this.isDragging = false;
        this.updateTransform(animate);
        this.updateCursor();
        this.notifyZoomChanged();
    }

    zoomToPoint(targetScale, clientX, clientY, animate = false) {
        if (!this.imgEl || !this.container) return;

        const newScale = Math.max(1, Math.min(50, targetScale));

        if (newScale <= 1.001) {
            this.resetZoom(animate);
            return;
        }

        const rect = this.container.getBoundingClientRect();
        const cx = rect.left + rect.width / 2;
        const cy = rect.top + rect.height / 2;

        const px = (clientX !== undefined ? clientX : cx) - cx;
        const py = (clientY !== undefined ? clientY : cy) - cy;

        const imgX = (px - this.currentX) / this.currentScale;
        const imgY = (py - this.currentY) / this.currentScale;

        const targetTx = px - imgX * newScale;
        const targetTy = py - imgY * newScale;

        const clamped = this.clampOffsets(newScale, targetTx, targetTy);

        this.currentScale = newScale;
        this.currentX = clamped.x;
        this.currentY = clamped.y;

        this.updateTransform(animate);
        this.updateCursor();
        this.notifyZoomChanged();
    }

    zoomStep(isZoomIn) {
        if (isZoomIn) {
            const step = this.currentScale >= 10.0 ? 5.0 : (this.currentScale >= 2.0 ? 0.5 : 0.25);
            const target = Math.min(50.0, Math.floor((this.currentScale + step + 0.001) / step) * step);
            this.zoomToPoint(target, undefined, undefined, true);
        } else {
            const step = this.currentScale > 10.0 ? 5.0 : (this.currentScale > 2.0 ? 0.5 : 0.25);
            const target = Math.max(1.0, Math.ceil((this.currentScale - step - 0.001) / step) * step);
            this.zoomToPoint(target, undefined, undefined, true);
        }
    }

    zoomToPercent(percent) {
        const scale = Math.max(1, Math.min(50, percent / 100));
        this.zoomToPoint(scale, undefined, undefined, true);
    }

    panOffset(dx, dy) {
        if (!this.imgEl || this.currentScale <= 1.001) return;
        const clamped = this.clampOffsets(this.currentScale, this.currentX + dx, this.currentY + dy);
        this.currentX = clamped.x;
        this.currentY = clamped.y;
        this.updateTransform(true);
    }

    onWheel(e) {
        if (!this.imgEl) return;
        e.preventDefault();

        let factor;
        if (e.ctrlKey) {
            // Trackpad pinch-to-zoom
            factor = Math.pow(1.01, -e.deltaY);
        } else {
            // Mouse wheel step
            factor = e.deltaY < 0 ? 1.25 : 0.8;
        }

        const targetScale = this.currentScale * factor;
        this.zoomToPoint(targetScale, e.clientX, e.clientY, false);
    }

    onMouseDown(e) {
        if (e.button !== 0 || !this.imgEl) return;
        if (e.target.closest && e.target.closest('.lightbox-controls-overlay')) return;

        if (this.currentScale > 1.001) {
            this.isMouseDown = true;
            this.startX = e.clientX;
            this.startY = e.clientY;
            this.startTx = this.currentX;
            this.startTy = this.currentY;
            this.isDragging = false;
            e.preventDefault();
            this.updateCursor();
        }
    }

    onMouseMove(e) {
        if (this.isMouseDown && this.currentScale > 1.001) {
            const dx = e.clientX - this.startX;
            const dy = e.clientY - this.startY;

            if (!this.isDragging && (Math.abs(dx) > 3 || Math.abs(dy) > 3)) {
                this.isDragging = true;
                this.updateCursor();
            }

            if (this.isDragging) {
                e.preventDefault();
                const clamped = this.clampOffsets(this.currentScale, this.startTx + dx, this.startTy + dy);
                this.currentX = clamped.x;
                this.currentY = clamped.y;
                this.updateTransform(false);
            }
        }
    }

    onMouseUp(e) {
        if (this.isMouseDown) {
            this.isMouseDown = false;
            this.isDragging = false;
            this.updateCursor();
        }
    }

    onDblClick(e) {
        if (!this.imgEl) return;
        if (e.target.closest && e.target.closest('.lightbox-controls-overlay')) return;
        e.preventDefault();

        if (this.currentScale > 1.05) {
            this.resetZoom(true);
        } else {
            this.zoomToPoint(2.5, e.clientX, e.clientY, true);
        }
    }

    onTouchStart(e) {
        this.clearTimers();
        this.ensureImg();

        if (e.touches.length === 2) {
            this.gestureState = 'ZOOMING';
            this.initialDistance = Math.max(1, this.getDistance(e.touches));
            this.initialScale = this.currentScale || 1;
            this.initialPinchCenterX = (e.touches[0].clientX + e.touches[1].clientX) / 2;
            this.initialPinchCenterY = (e.touches[0].clientY + e.touches[1].clientY) / 2;
            this.startTx = this.currentX || 0;
            this.startTy = this.currentY || 0;
        } else if (e.touches.length === 1) {
            const now = Date.now();
            if (now - this.lastTapTime < 300) {
                // Double tap
                if (this.currentScale > 1) {
                    this.resetZoom(true);
                } else {
                    this.zoomToPoint(2.5, e.touches[0].clientX, e.touches[0].clientY, true);
                }
                this.lastTapTime = 0; 
                e.preventDefault();
                return;
            }
            this.lastTapTime = now;

            this.startX = e.touches[0].clientX;
            this.startY = e.touches[0].clientY;
            this.startTx = this.currentX || 0;
            this.startTy = this.currentY || 0;
            
            if (this.currentScale > 1) {
                this.gestureState = 'PANNING';
            } else {
                this.gestureState = 'IDLE';
                // start long press only if not zoomed in
                this.longPressTimer = setTimeout(() => {
                    if (this.gestureState === 'IDLE' && this.dotNetRef) {
                        if (window.triggerHaptic) { window.triggerHaptic('LIGHT'); } else { try { navigator.vibrate?.(30); } catch (_) {} }
                        this.dotNetRef.invokeMethodAsync('HandleLongPress');
                    }
                }, 500);
            }
        }
    }

    onTouchMove(e) {
        const img = this.ensureImg();
        if (!img) return;

        if (e.touches.length === 2) {
            // Self-recover into ZOOMING if touches changed mid-gesture
            if (this.gestureState !== 'ZOOMING' || !this.initialDistance) {
                this.gestureState = 'ZOOMING';
                this.initialDistance = Math.max(1, this.getDistance(e.touches));
                this.initialScale = this.currentScale || 1;
                this.initialPinchCenterX = (e.touches[0].clientX + e.touches[1].clientX) / 2;
                this.initialPinchCenterY = (e.touches[0].clientY + e.touches[1].clientY) / 2;
                this.startTx = this.currentX || 0;
                this.startTy = this.currentY || 0;
            }

            e.preventDefault(); // Prevent scroll
            const dist = this.getDistance(e.touches);
            const scaleFactor = dist / (this.initialDistance || 1);
            let newScale = this.initialScale * scaleFactor;
            
            if (newScale <= 1) {
                newScale = 1;
                this.currentX = 0;
                this.currentY = 0;
            } else {
                if (newScale > 50) newScale = 50;
                const currPinchCenterX = (e.touches[0].clientX + e.touches[1].clientX) / 2;
                const currPinchCenterY = (e.touches[0].clientY + e.touches[1].clientY) / 2;
                
                const rect = this.container ? this.container.getBoundingClientRect() : { left: 0, top: 0, width: window.innerWidth, height: window.innerHeight };
                const cx = rect.left + rect.width / 2;
                const cy = rect.top + rect.height / 2;
                
                const initialPcX = this.initialPinchCenterX - cx;
                const initialPcY = this.initialPinchCenterY - cy;
                const currPcX = currPinchCenterX - cx;
                const currPcY = currPinchCenterY - cy;
                
                const scaleRatio = newScale / (this.initialScale || 1);
                
                this.currentX = currPcX - (initialPcX - this.startTx) * scaleRatio;
                this.currentY = currPcY - (initialPcY - this.startTy) * scaleRatio;
            }
            this.currentScale = newScale;
            this.updateTransform(false);
            this.updateCursor();
            this.notifyZoomChanged();
            
        } else if (e.touches.length === 1) {
            const dx = e.touches[0].clientX - this.startX;
            const dy = e.touches[0].clientY - this.startY;

            if (this.gestureState === 'PANNING') {
                e.preventDefault(); // Prevent scroll while panning
                this.currentX = this.startTx + dx;
                this.currentY = this.startTy + dy;
                this.updateTransform(false);
            } else if (this.currentScale === 1) {
                // Determine gesture direction if currently IDLE
                if (this.gestureState === 'IDLE') {
                    if (Math.abs(dx) > 10 || Math.abs(dy) > 10) {
                        this.clearTimers(); // cancel long press
                        if (Math.abs(dx) > Math.abs(dy)) {
                            this.gestureState = 'SWIPING_X';
                        } else if (dy > 0) {
                            // only allow swipe down for dismiss, block swipe up
                            this.gestureState = 'SWIPING_Y';
                        } else {
                            // swiping up is ignored
                            this.gestureState = 'IGNORE'; 
                        }
                    }
                }

                if (this.gestureState === 'SWIPING_X') {
                    e.preventDefault();
                    if (this.canSwipeHorizontal) {
                        this.currentX = this.startTx + dx;
                    } else {
                        // Dampen horizontal movement with elastic resistance when only 1 image exists
                        this.currentX = this.startTx + (dx * 0.25);
                    }
                    this.updateTransform(false);
                } else if (this.gestureState === 'SWIPING_Y') {
                    e.preventDefault();
                    this.currentY = this.startTy + dy;
                    this.updateTransform(false);
                }
            }
        }
    }

    onTouchEnd(e) {
        if (e.touches.length < 2 && this.gestureState === 'ZOOMING') {
            this.gestureState = this.currentScale > 1 ? 'PANNING' : 'IDLE';
            this.lastPinchEndTime = Date.now();
        }
        
        if (e.touches.length === 1) {
            // If one finger remains, update start positions to avoid jumping
            this.startX = e.touches[0].clientX;
            this.startY = e.touches[0].clientY;
            this.startTx = this.currentX;
            this.startTy = this.currentY;
        }

        if (e.touches.length === 0) {
            this.clearTimers();
            
            if (this.currentScale < 1) {
                this.resetZoom(true);
            } else if (this.currentScale === 1) {
                let actionTriggered = false;

                if (this.gestureState === 'SWIPING_Y' && this.currentY > 100 && this.dotNetRef) {
                    this.animateSwipe('down');
                    actionTriggered = true;
                } else if (this.canSwipeHorizontal && this.gestureState === 'SWIPING_X' && this.currentX > 100 && this.dotNetRef) {
                    this.animateSwipe('right');
                    actionTriggered = true;
                } else if (this.canSwipeHorizontal && this.gestureState === 'SWIPING_X' && this.currentX < -100 && this.dotNetRef) {
                    this.animateSwipe('left');
                    actionTriggered = true;
                } else if (this.gestureState !== 'IDLE' && this.gestureState !== 'IGNORE') {
                    // Didn't cross threshold or cannot swipe horizontally, snap back
                    this.resetZoom(true);
                }

                // If no swipe movement, it was a touch tap
                if (this.gestureState === 'IDLE') {
                    if (Date.now() - this.lastPinchEndTime < 350) {
                        return;
                    }
                    if (this.singleTapTimeout) { clearTimeout(this.singleTapTimeout); this.singleTapTimeout = null; }
                    this.singleTapTimeout = setTimeout(() => {
                        if (this.dotNetRef) {
                            this.dotNetRef.invokeMethodAsync('ToggleFullscreen');
                        }
                        this.singleTapTimeout = null;
                    }, 300);
                }
                
                // Always reset gestureState to IDLE when all fingers are lifted
                this.gestureState = 'IDLE';
            } else {
                // If zoomed in (scale > 1), clamp to pan bounds
                const clamped = this.clampOffsets(this.currentScale, this.currentX, this.currentY);
                if (clamped.x !== this.currentX || clamped.y !== this.currentY) {
                    this.currentX = clamped.x;
                    this.currentY = clamped.y;
                    this.updateTransform(true);
                }
                this.gestureState = 'IDLE';
            }
        }
    }

    onTouchCancel() {
        this.clearTimers();
        this.resetZoom(true);
    }

    onVisibilityChange() {
        if (document.hidden) {
            this.clearTimers();
            this.resetZoom(false);
        }
    }
}

// Global instances for Blazor to interface with
let activeGestureHandler = null;

export function initGestureHandler(containerId, dotNetRef, canSwipeHorizontal = true) {
    if (activeGestureHandler) {
        activeGestureHandler.destroy();
    }
    activeGestureHandler = new LightboxGestureHandler(containerId, dotNetRef, canSwipeHorizontal);
}

export function updateImage(imageId, canSwipeHorizontal) {
    if (activeGestureHandler) {
        if (canSwipeHorizontal !== undefined) {
            activeGestureHandler.setCanSwipeHorizontal(canSwipeHorizontal);
        }
        activeGestureHandler.updateImage(imageId);
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

export function animateSwipe(direction) {
    if (activeGestureHandler) {
        activeGestureHandler.animateSwipe(direction);
    }
}
