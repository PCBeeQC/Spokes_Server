// mediaZoomGesture.js — Reusable Pinch-to-Zoom, Pan, Double-Tap, Rotation & Swipe-to-Dismiss Controller
// Shared across MediaViewerDialog (photos & videos) and Voice/Video Full-Screen Stage (Web/iOS <video> & Android Native underlay).

export class MediaZoomGestureController {
    constructor(options = {}) {
        this.container = typeof options.container === 'string'
            ? document.getElementById(options.container)
            : options.container;
        this.targetId = options.targetId || null;
        this.getTargetElement = typeof options.getTargetElement === 'function'
            ? options.getTargetElement
            : () => (this.targetId ? document.getElementById(this.targetId) : null);
        this.getContentAspectRatio = typeof options.getContentAspectRatio === 'function'
            ? options.getContentAspectRatio
            : null;
        this.maxScale = Number(options.maxScale) > 1 ? Number(options.maxScale) : 50;
        this.canSwipeHorizontal = Boolean(options.canSwipeHorizontal);
        this.enableSwipeDownClose = options.enableSwipeDownClose !== false;
        this.applyDomTransform = options.applyDomTransform !== false;
        this.ignoreSelector = options.ignoreSelector ||
            '.lightbox-controls-overlay, .participant-fullscreen-hud, .participant-fullscreen-btn, .participant-small-overlay, .voice-pip-no-drag, button, a, input, select';

        this.onTransformChange = typeof options.onTransformChange === 'function' ? options.onTransformChange : null;
        this.onZoomPercentChange = typeof options.onZoomPercentChange === 'function' ? options.onZoomPercentChange : null;
        this.onSingleTap = typeof options.onSingleTap === 'function' ? options.onSingleTap : null;
        this.onSwipeDownClose = typeof options.onSwipeDownClose === 'function' ? options.onSwipeDownClose : null;
        this.onSwipeHorizontal = typeof options.onSwipeHorizontal === 'function' ? options.onSwipeHorizontal : null;
        this.onLongPress = typeof options.onLongPress === 'function' ? options.onLongPress : null;
        this.onEscape = typeof options.onEscape === 'function' ? options.onEscape : null;

        this.targetEl = null;
        this.currentMediaSrc = null;

        this.currentScale = 1;
        this.currentX = 0;
        this.currentY = 0;
        this.rotationDeg = 0;

        // Gesture state: IDLE, PANNING, SWIPING_X, SWIPING_Y, ZOOMING, IGNORE
        this.gestureState = 'IDLE';
        this.startX = 0;
        this.startY = 0;
        this.startTx = 0;
        this.startTy = 0;

        // Pinch zoom state
        this.initialDistance = 0;
        this.initialScale = 1;
        this.initialPinchCenterX = 0;
        this.initialPinchCenterY = 0;

        // Mouse drag state
        this.isMouseDown = false;
        this.isDragging = false;

        // Throttled zoom notification state
        this.lastNotifiedPercent = 100;
        this.lastNotificationTime = 0;
        this.notifyTimeout = null;

        // Timers
        this.longPressTimer = null;
        this.singleTapTimeout = null;
        this.lastTapTime = 0;
        this.lastPinchEndTime = 0;
        this.pendingAnimateInDirection = null;

        this.bindEvents();
    }

    isIgnoredTarget(target) {
        if (!target || typeof target.closest !== 'function') return false;
        return Boolean(this.ignoreSelector && target.closest(this.ignoreSelector));
    }

    ensureTarget() {
        const resolved = this.getTargetElement ? this.getTargetElement() : null;
        if (resolved) {
            if (this.targetEl && this.targetEl !== resolved && this.applyDomTransform) {
                // Reset transform on previous target element if the active target swapped (e.g. camera -> screen share)
                this.targetEl.style.transform = '';
                this.targetEl.style.transition = '';
            }
            this.targetEl = resolved;
        } else if (this.targetEl && !this.targetEl.isConnected) {
            this.targetEl = null;
        }
        return this.targetEl;
    }

    setCanSwipeHorizontal(canSwipe) {
        this.canSwipeHorizontal = Boolean(canSwipe);
    }

    setRotation(deg, animate = true) {
        const normalized = ((Number(deg) || 0) % 360 + 360) % 360;
        if (this.rotationDeg === normalized) return;
        this.rotationDeg = normalized;
        this.currentScale = 1;
        this.currentX = 0;
        this.currentY = 0;
        this.gestureState = 'IDLE';
        this.updateTransform(animate);
        this.updateCursor();
        this.notifyZoomChanged();
    }

    getRotation() {
        return this.rotationDeg;
    }

    resolveFittedScreenBox() {
        if (!this.container) {
            return { width: window.innerWidth || 1, height: window.innerHeight || 1 };
        }
        const cw = this.container.clientWidth || window.innerWidth || 1;
        const ch = this.container.clientHeight || window.innerHeight || 1;
        const el = this.ensureTarget();

        let aspect = 0;
        if (this.getContentAspectRatio) {
            const customAspect = Number(this.getContentAspectRatio());
            if (Number.isFinite(customAspect) && customAspect > 0.05 && customAspect < 20) {
                aspect = customAspect;
            }
        }
        if (aspect <= 0 && el) {
            if (el.videoWidth > 0 && el.videoHeight > 0) {
                aspect = el.videoWidth / el.videoHeight;
            } else if (el.naturalWidth > 0 && el.naturalHeight > 0) {
                aspect = el.naturalWidth / el.naturalHeight;
            } else if (el.offsetWidth > 0 && el.offsetHeight > 0 && el.tagName?.toLowerCase() === 'img') {
                aspect = el.offsetWidth / el.offsetHeight;
            }
        }
        if (aspect <= 0 && this.container) {
            const cssAspect = parseFloat(getComputedStyle(this.container).getPropertyValue('--tile-video-aspect') || '0');
            if (Number.isFinite(cssAspect) && cssAspect > 0.05 && cssAspect < 20) {
                aspect = cssAspect;
            }
        }

        const isQuarterTurn = this.rotationDeg === 90 || this.rotationDeg === 270;
        if (aspect <= 0) {
            if (!isQuarterTurn && el && el.offsetWidth > 0 && el.offsetHeight > 0) {
                return { width: el.offsetWidth, height: el.offsetHeight };
            }
            return { width: cw, height: ch };
        }

        const fitBoxW = isQuarterTurn ? ch : cw;
        const fitBoxH = isQuarterTurn ? cw : ch;
        let fittedW, fittedH;
        if ((fitBoxW / fitBoxH) > aspect) {
            fittedH = fitBoxH;
            fittedW = fitBoxH * aspect;
        } else {
            fittedW = fitBoxW;
            fittedH = fitBoxW / aspect;
        }

        return isQuarterTurn
            ? { width: fittedH, height: fittedW }
            : { width: fittedW, height: fittedH };
    }

    calculatePanBounds(scale) {
        if (!this.container || scale <= 1.001) {
            return { maxX: 0, maxY: 0 };
        }
        const containerWidth = this.container.clientWidth || 1;
        const containerHeight = this.container.clientHeight || 1;
        const fitted = this.resolveFittedScreenBox();

        const visualWidth = fitted.width * scale;
        const visualHeight = fitted.height * scale;

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
        if (isNaN(this.currentScale) || isNaN(this.currentX) || isNaN(this.currentY)) {
            this.currentScale = 1;
            this.currentX = 0;
            this.currentY = 0;
        }

        const el = this.ensureTarget();
        if (el && this.applyDomTransform) {
            el.style.transition = animate
                ? 'transform 0.2s ease-out, opacity 0.2s ease'
                : 'opacity 0.2s ease';
            const rotPart = this.rotationDeg ? ` rotate(${this.rotationDeg}deg)` : '';
            el.style.transform = `translate(${this.currentX}px, ${this.currentY}px)${rotPart} scale(${this.currentScale})`;
        }

        if (this.onTransformChange) {
            this.onTransformChange({
                scale: this.currentScale,
                x: this.currentX,
                y: this.currentY,
                rotationDeg: this.rotationDeg,
                animate,
                targetEl: el
            });
        }
    }

    updateCursor() {
        const el = this.ensureTarget();
        if (!el) return;
        if (this.isMouseDown && this.isDragging) {
            el.style.cursor = 'grabbing';
            if (this.container) this.container.style.cursor = 'grabbing';
        } else if (this.currentScale > 1.001) {
            el.style.cursor = 'grab';
            if (this.container) this.container.style.cursor = 'grab';
        } else {
            el.style.cursor = '';
            if (this.container) this.container.style.cursor = '';
        }
    }

    notifyZoomChanged() {
        const percent = Math.round(this.currentScale * 100);
        if (percent === this.lastNotifiedPercent) return;

        const now = Date.now();
        if (percent <= 100 || now - this.lastNotificationTime > 50) {
            if (this.notifyTimeout) {
                clearTimeout(this.notifyTimeout);
                this.notifyTimeout = null;
            }
            this.lastNotificationTime = now;
            this.lastNotifiedPercent = percent;
            if (this.onZoomPercentChange) {
                this.onZoomPercentChange(percent);
            }
        } else {
            if (this.notifyTimeout) clearTimeout(this.notifyTimeout);
            this.notifyTimeout = setTimeout(() => {
                this.notifyTimeout = null;
                this.lastNotificationTime = Date.now();
                this.lastNotifiedPercent = percent;
                if (this.onZoomPercentChange) {
                    this.onZoomPercentChange(percent);
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
        if (!this.container) return;

        const newScale = Math.max(1, Math.min(this.maxScale, targetScale));
        if (newScale <= 1.001) {
            this.resetZoom(animate);
            return;
        }

        const rect = this.container.getBoundingClientRect();
        const cx = rect.left + rect.width / 2;
        const cy = rect.top + rect.height / 2;

        const px = (clientX !== undefined ? clientX : cx) - cx;
        const py = (clientY !== undefined ? clientY : cy) - cy;

        const mediaX = (px - this.currentX) / this.currentScale;
        const mediaY = (py - this.currentY) / this.currentScale;

        const targetTx = px - mediaX * newScale;
        const targetTy = py - mediaY * newScale;

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
            const target = Math.min(this.maxScale, Math.floor((this.currentScale + step + 0.001) / step) * step);
            this.zoomToPoint(target, undefined, undefined, true);
        } else {
            const step = this.currentScale > 10.0 ? 5.0 : (this.currentScale > 2.0 ? 0.5 : 0.25);
            const target = Math.max(1.0, Math.ceil((this.currentScale - step - 0.001) / step) * step);
            this.zoomToPoint(target, undefined, undefined, true);
        }
    }

    zoomToPercent(percent) {
        const scale = Math.max(1, Math.min(this.maxScale, percent / 100));
        this.zoomToPoint(scale, undefined, undefined, true);
    }

    panOffset(dx, dy) {
        if (this.currentScale <= 1.001) return;
        const clamped = this.clampOffsets(this.currentScale, this.currentX + dx, this.currentY + dy);
        this.currentX = clamped.x;
        this.currentY = clamped.y;
        this.updateTransform(true);
    }

    animateSwipe(direction) {
        if ((direction === 'left' || direction === 'right') && !this.canSwipeHorizontal) {
            this.resetZoom(true);
            return;
        }

        if (direction === 'left') {
            this.currentX = -window.innerWidth;
        } else if (direction === 'right') {
            this.currentX = window.innerWidth;
        } else if (direction === 'down') {
            this.currentY = window.innerHeight;
        }

        this.updateTransform(true);

        setTimeout(() => {
            if (direction === 'down') {
                if (this.onSwipeDownClose) {
                    this.onSwipeDownClose();
                }
            } else if (this.onSwipeHorizontal) {
                this.pendingAnimateInDirection = direction === 'left' ? 'right' : 'left';
                this.onSwipeHorizontal(direction);
            }
        }, 200);
    }

    updateMediaTarget(targetId) {
        if (targetId) {
            this.targetId = targetId;
        }
        const el = this.ensureTarget();
        if (!el) return;

        const newSrc = el.getAttribute?.('data-media-src') || el.currentSrc || el.src || el.id;
        const isNewMedia = !this.currentMediaSrc || (newSrc && this.currentMediaSrc !== newSrc);

        if (this.pendingAnimateInDirection) {
            this.currentMediaSrc = newSrc;
            const inX = this.pendingAnimateInDirection === 'left' ? -window.innerWidth : window.innerWidth;
            this.currentScale = 1;
            this.currentX = inX;
            this.currentY = 0;
            this.gestureState = 'IDLE';
            this.updateTransform(false);

            this.pendingAnimateInDirection = null;

            const slideIn = () => {
                void el.offsetWidth;
                this.currentX = 0;
                this.updateTransform(true);
                this.updateCursor();
                this.notifyZoomChanged();
            };

            if (el.tagName?.toLowerCase() === 'img') {
                if (el.complete && el.naturalWidth > 0) {
                    slideIn();
                } else {
                    el.addEventListener('load', slideIn, { once: true });
                    el.addEventListener('error', slideIn, { once: true });
                }
            } else {
                slideIn();
            }
        } else if (isNewMedia) {
            this.currentMediaSrc = newSrc;
            this.resetZoom(false);
        } else {
            this.updateTransform(false);
            this.updateCursor();
        }
    }

    bindEvents() {
        if (!this.container) return;

        this.onTouchStart = this.onTouchStart.bind(this);
        this.onTouchMove = this.onTouchMove.bind(this);
        this.onTouchEnd = this.onTouchEnd.bind(this);
        this.onTouchCancel = this.onTouchCancel.bind(this);

        this.onWheel = this.onWheel.bind(this);
        this.onMouseDown = this.onMouseDown.bind(this);
        this.onMouseMove = this.onMouseMove.bind(this);
        this.onMouseUp = this.onMouseUp.bind(this);
        this.onDblClick = this.onDblClick.bind(this);
        this.onVisibilityChange = this.onVisibilityChange.bind(this);
        this.onKeyDown = this.onKeyDown.bind(this);

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
        window.addEventListener('keydown', this.onKeyDown, true);
    }

    destroy(resetDomTransform = false) {
        if (this.container) {
            this.container.removeEventListener('touchstart', this.onTouchStart, { passive: false });
            this.container.removeEventListener('touchmove', this.onTouchMove, { passive: false });
            this.container.removeEventListener('touchend', this.onTouchEnd);
            this.container.removeEventListener('touchcancel', this.onTouchCancel);

            this.container.removeEventListener('wheel', this.onWheel, { passive: false });
            this.container.removeEventListener('mousedown', this.onMouseDown);
            this.container.removeEventListener('dblclick', this.onDblClick);
            this.container.style.cursor = '';
        }
        window.removeEventListener('mousemove', this.onMouseMove);
        window.removeEventListener('mouseup', this.onMouseUp);
        document.removeEventListener('visibilitychange', this.onVisibilityChange);
        window.removeEventListener('keydown', this.onKeyDown, true);

        if (resetDomTransform && this.targetEl && this.applyDomTransform) {
            this.targetEl.style.transform = '';
            this.targetEl.style.transition = '';
            this.targetEl.style.cursor = '';
        }

        this.clearTimers();
        this.targetEl = null;
        this.currentMediaSrc = null;
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

    onKeyDown(e) {
        if (e.key !== 'Escape') return;
        const target = e.target;
        const tagName = target && target.tagName ? target.tagName.toLowerCase() : '';
        if (tagName === 'input' || tagName === 'textarea' || (target && target.isContentEditable)) {
            return;
        }
        if (this.onEscape) {
            e.preventDefault();
            e.stopPropagation();
            this.onEscape();
        }
    }

    onWheel(e) {
        if (!this.container) return;
        e.preventDefault();

        let factor;
        if (e.ctrlKey) {
            factor = Math.pow(1.01, -e.deltaY);
        } else {
            factor = e.deltaY < 0 ? 1.25 : 0.8;
        }

        const targetScale = this.currentScale * factor;
        this.zoomToPoint(targetScale, e.clientX, e.clientY, false);
    }

    onMouseDown(e) {
        if (e.button !== 0 || !this.container) return;
        if (this.isIgnoredTarget(e.target)) return;

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

    onMouseUp() {
        if (this.isMouseDown) {
            this.isMouseDown = false;
            this.isDragging = false;
            this.updateCursor();
        }
    }

    onDblClick(e) {
        if (!this.container || this.isIgnoredTarget(e.target)) return;
        e.preventDefault();

        if (this.currentScale > 1.05) {
            this.resetZoom(true);
        } else {
            this.zoomToPoint(2.5, e.clientX, e.clientY, true);
        }
    }

    onTouchStart(e) {
        this.clearTimers();
        this.ensureTarget();

        if (e.touches.length === 1 && this.isIgnoredTarget(e.target)) {
            this.gestureState = 'IGNORE';
            return;
        }

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
                if (this.currentScale > 1.01) {
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

            if (this.currentScale > 1.01) {
                this.gestureState = 'PANNING';
            } else {
                this.gestureState = 'IDLE';
                if (this.onLongPress) {
                    this.longPressTimer = setTimeout(() => {
                        if (this.gestureState === 'IDLE' && this.onLongPress) {
                            if (window.triggerHaptic) {
                                window.triggerHaptic('LIGHT');
                            } else {
                                try { navigator.vibrate?.(30); } catch (_) {}
                            }
                            this.onLongPress();
                        }
                    }, 500);
                }
            }
        }
    }

    onTouchMove(e) {
        if (!this.container || this.gestureState === 'IGNORE') return;

        if (e.touches.length === 2) {
            if (this.gestureState !== 'ZOOMING' || !this.initialDistance) {
                this.gestureState = 'ZOOMING';
                this.initialDistance = Math.max(1, this.getDistance(e.touches));
                this.initialScale = this.currentScale || 1;
                this.initialPinchCenterX = (e.touches[0].clientX + e.touches[1].clientX) / 2;
                this.initialPinchCenterY = (e.touches[0].clientY + e.touches[1].clientY) / 2;
                this.startTx = this.currentX || 0;
                this.startTy = this.currentY || 0;
            }

            e.preventDefault();
            const dist = this.getDistance(e.touches);
            const scaleFactor = dist / (this.initialDistance || 1);
            let newScale = this.initialScale * scaleFactor;

            if (newScale <= 1) {
                newScale = 1;
                this.currentX = 0;
                this.currentY = 0;
            } else {
                if (newScale > this.maxScale) newScale = this.maxScale;
                const currPinchCenterX = (e.touches[0].clientX + e.touches[1].clientX) / 2;
                const currPinchCenterY = (e.touches[0].clientY + e.touches[1].clientY) / 2;

                const rect = this.container.getBoundingClientRect();
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
                e.preventDefault();
                this.currentX = this.startTx + dx;
                this.currentY = this.startTy + dy;
                this.updateTransform(false);
            } else if (this.currentScale <= 1.01) {
                if (this.gestureState === 'IDLE') {
                    if (Math.abs(dx) > 10 || Math.abs(dy) > 10) {
                        this.clearTimers();
                        if (Math.abs(dx) > Math.abs(dy)) {
                            this.gestureState = this.canSwipeHorizontal ? 'SWIPING_X' : 'IGNORE';
                        } else if (dy > 0 && this.enableSwipeDownClose) {
                            this.gestureState = 'SWIPING_Y';
                        } else {
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
        if (this.gestureState === 'IGNORE' && e.touches.length === 0) {
            this.gestureState = 'IDLE';
            return;
        }

        if (e.touches.length < 2 && this.gestureState === 'ZOOMING') {
            this.gestureState = this.currentScale > 1.01 ? 'PANNING' : 'IDLE';
            this.lastPinchEndTime = Date.now();
        }

        if (e.touches.length === 1) {
            this.startX = e.touches[0].clientX;
            this.startY = e.touches[0].clientY;
            this.startTx = this.currentX;
            this.startTy = this.currentY;
        }

        if (e.touches.length === 0) {
            this.clearTimers();

            if (this.currentScale < 1) {
                this.resetZoom(true);
            } else if (this.currentScale <= 1.01) {
                if (this.gestureState === 'SWIPING_Y' && this.currentY > 100 && this.onSwipeDownClose) {
                    this.animateSwipe('down');
                } else if (this.canSwipeHorizontal && this.gestureState === 'SWIPING_X' && this.currentX > 100 && this.onSwipeHorizontal) {
                    this.animateSwipe('right');
                } else if (this.canSwipeHorizontal && this.gestureState === 'SWIPING_X' && this.currentX < -100 && this.onSwipeHorizontal) {
                    this.animateSwipe('left');
                } else if (this.gestureState !== 'IDLE' && this.gestureState !== 'IGNORE') {
                    this.resetZoom(true);
                }

                if (this.gestureState === 'IDLE' && this.onSingleTap) {
                    if (Date.now() - this.lastPinchEndTime < 350) {
                        this.gestureState = 'IDLE';
                        return;
                    }
                    if (this.singleTapTimeout) { clearTimeout(this.singleTapTimeout); this.singleTapTimeout = null; }
                    this.singleTapTimeout = setTimeout(() => {
                        if (this.onSingleTap) {
                            this.onSingleTap();
                        }
                        this.singleTapTimeout = null;
                    }, 300);
                }

                this.gestureState = 'IDLE';
            } else {
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
