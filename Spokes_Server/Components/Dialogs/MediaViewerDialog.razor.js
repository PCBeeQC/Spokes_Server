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
    constructor(containerId, dotNetRef) {
        this.containerId = containerId;
        this.container = document.getElementById(containerId);
        this.dotNetRef = dotNetRef;
        this.imgEl = null;

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

        // Timers
        this.longPressTimer = null;
        this.singleTapTimeout = null;
        this.lastTapTime = 0;

        this.bindEvents();
    }

    animateSwipe(direction) {
        if (!this.imgEl || !this.dotNetRef) return;
        
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
        this.imgEl = document.getElementById(imageId);
        if (!this.imgEl) return;
        
        if (this.pendingAnimateInDirection) {
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
            };

            // Wait for image to load
            if (this.imgEl.complete && this.imgEl.naturalWidth > 0) {
                slideIn();
            } else {
                this.imgEl.addEventListener('load', slideIn, { once: true });
                this.imgEl.addEventListener('error', slideIn, { once: true });
            }
        } else {
            this.resetZoom();
        }
    }

    bindEvents() {
        if (!this.container) return;

        this.onTouchStart = this.onTouchStart.bind(this);
        this.onTouchMove = this.onTouchMove.bind(this);
        this.onTouchEnd = this.onTouchEnd.bind(this);
        this.onTouchCancel = this.onTouchCancel.bind(this);
        this.onVisibilityChange = this.onVisibilityChange.bind(this);

        this.container.addEventListener('touchstart', this.onTouchStart, { passive: false });
        this.container.addEventListener('touchmove', this.onTouchMove, { passive: false });
        this.container.addEventListener('touchend', this.onTouchEnd);
        this.container.addEventListener('touchcancel', this.onTouchCancel);
        document.addEventListener('visibilitychange', this.onVisibilityChange);
    }

    destroy() {
        if (!this.container) return;
        this.container.removeEventListener('touchstart', this.onTouchStart, { passive: false });
        this.container.removeEventListener('touchmove', this.onTouchMove, { passive: false });
        this.container.removeEventListener('touchend', this.onTouchEnd);
        this.container.removeEventListener('touchcancel', this.onTouchCancel);
        document.removeEventListener('visibilitychange', this.onVisibilityChange);
        this.clearTimers();
        this.dotNetRef = null;
        this.imgEl = null;
        this.container = null;
    }

    clearTimers() {
        if (this.longPressTimer) { clearTimeout(this.longPressTimer); this.longPressTimer = null; }
        if (this.singleTapTimeout) { clearTimeout(this.singleTapTimeout); this.singleTapTimeout = null; }
    }

    getDistance(touches) {
        const dx = touches[0].clientX - touches[1].clientX;
        const dy = touches[0].clientY - touches[1].clientY;
        return Math.sqrt(dx * dx + dy * dy);
    }

    updateTransform(animate = false) {
        if (!this.imgEl) return;
        
        if (animate) {
            this.imgEl.style.transition = 'transform 0.2s ease-out, opacity 0.2s ease';
        } else {
            this.imgEl.style.transition = 'opacity 0.2s ease';
        }
        this.imgEl.style.transform = `translate(${this.currentX}px, ${this.currentY}px) scale(${this.currentScale})`;
    }

    resetZoom() {
        this.currentScale = 1;
        this.currentX = 0;
        this.currentY = 0;
        this.gestureState = 'IDLE';
        this.updateTransform(true);
    }

    onTouchStart(e) {
        this.clearTimers();

        if (e.touches.length === 2) {
            this.gestureState = 'ZOOMING';
            this.initialDistance = this.getDistance(e.touches);
            this.initialScale = this.currentScale;
            this.initialPinchCenterX = (e.touches[0].clientX + e.touches[1].clientX) / 2;
            this.initialPinchCenterY = (e.touches[0].clientY + e.touches[1].clientY) / 2;
            this.startTx = this.currentX;
            this.startTy = this.currentY;
        } else if (e.touches.length === 1) {
            const now = Date.now();
            if (now - this.lastTapTime < 300) {
                // Double tap
                if (this.currentScale > 1) {
                    this.resetZoom();
                } else {
                    this.currentScale = 3; // Default zoom
                    this.currentX = 0;
                    this.currentY = 0;
                    this.updateTransform(true);
                }
                this.lastTapTime = 0; 
                e.preventDefault();
                return;
            }
            this.lastTapTime = now;

            this.startX = e.touches[0].clientX;
            this.startY = e.touches[0].clientY;
            this.startTx = this.currentX;
            this.startTy = this.currentY;
            
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
        if (!this.imgEl) return;

        if (e.touches.length === 2 && this.gestureState === 'ZOOMING') {
            e.preventDefault(); // Prevent scroll
            const dist = this.getDistance(e.touches);
            const scaleFactor = dist / this.initialDistance;
            let newScale = this.initialScale * scaleFactor;
            
            if (newScale <= 1) {
                newScale = 1;
                this.currentX = 0;
                this.currentY = 0;
            } else {
                if (newScale > 5) newScale = 5;
                const currPinchCenterX = (e.touches[0].clientX + e.touches[1].clientX) / 2;
                const currPinchCenterY = (e.touches[0].clientY + e.touches[1].clientY) / 2;
                
                const rect = this.container.getBoundingClientRect();
                const cx = rect.left + rect.width / 2;
                const cy = rect.top + rect.height / 2;
                
                const initialPcX = this.initialPinchCenterX - cx;
                const initialPcY = this.initialPinchCenterY - cy;
                const currPcX = currPinchCenterX - cx;
                const currPcY = currPinchCenterY - cy;
                
                const scaleRatio = newScale / this.initialScale;
                
                this.currentX = currPcX - (initialPcX - this.startTx) * scaleRatio;
                this.currentY = currPcY - (initialPcY - this.startTy) * scaleRatio;
            }
            this.currentScale = newScale;
            this.updateTransform(false);
            
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
                    this.currentX = this.startTx + dx;
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
                this.resetZoom();
            } else if (this.currentScale === 1) {
                let actionTriggered = false;

                if (this.gestureState === 'SWIPING_Y' && this.currentY > 100 && this.dotNetRef) {
                    this.animateSwipe('down');
                    actionTriggered = true;
                } else if (this.gestureState === 'SWIPING_X' && this.currentX > 100 && this.dotNetRef) {
                    this.animateSwipe('right');
                    actionTriggered = true;
                } else if (this.gestureState === 'SWIPING_X' && this.currentX < -100 && this.dotNetRef) {
                    this.animateSwipe('left');
                    actionTriggered = true;
                } else if (this.gestureState !== 'IDLE' && this.gestureState !== 'IGNORE') {
                    // Didn't cross threshold, snap back
                    this.resetZoom();
                }

                // If no swipe movement, it was a tap
                if (this.gestureState === 'IDLE') {
                    if (this.singleTapTimeout) { clearTimeout(this.singleTapTimeout); this.singleTapTimeout = null; }
                    this.singleTapTimeout = setTimeout(() => {
                        if (this.dotNetRef) {
                            this.dotNetRef.invokeMethodAsync('ToggleFullscreen');
                        }
                        this.singleTapTimeout = null;
                    }, 300);
                }
                
                if (actionTriggered) {
                    // reset gesture state so it doesn't fire again
                    this.gestureState = 'IDLE';
                }
            }
        }
    }

    onTouchCancel() {
        this.clearTimers();
        this.resetZoom();
    }

    onVisibilityChange() {
        if (document.hidden) {
            this.clearTimers();
            this.resetZoom();
        }
    }
}

// Global instances for Blazor to interface with
let activeGestureHandler = null;

export function initGestureHandler(containerId, dotNetRef) {
    if (activeGestureHandler) {
        activeGestureHandler.destroy();
    }
    activeGestureHandler = new LightboxGestureHandler(containerId, dotNetRef);
}

export function updateImage(imageId) {
    if (activeGestureHandler) {
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
        activeGestureHandler.resetZoom();
    }
}

export function animateSwipe(direction) {
    if (activeGestureHandler) {
        activeGestureHandler.animateSwipe(direction);
    }
}
