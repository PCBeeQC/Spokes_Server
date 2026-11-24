// SpokesSwipePager.razor.js — Reusable 3-pane 1:1 finger-tracked horizontal swipe pager
// Designed for Blazor Server zero-flicker commits with strict OS-level gesture conflict prevention.

export const EDGE_GUARD_DEFAULT_PX = 32;
export const TOUCH_SLOP_PX = 10;
export const HORIZONTAL_BIAS_RATIO = 1.25;
export const COMMIT_DISTANCE_RATIO = 0.25;
export const COMMIT_FLICK_VELOCITY = 0.35; // px/ms
export const COMMIT_FLICK_MIN_DISTANCE_PX = 24;
export const VELOCITY_SAMPLE_MAX_AGE_MS = 80;
export const PROGRAMMATIC_DURATION_MS = 140;
export const MIN_SETTLE_DURATION_MS = 110;
export const MAX_SETTLE_DURATION_MS = 210;
export const CLICK_SUPPRESS_WINDOW_MS = 350;
export const COMMIT_SAFETY_TIMEOUT_MS = 1500;

const EASE_OUT_CURVE = 'cubic-bezier(0.22, 1, 0.36, 1)';
const FAST_PROGRAMMATIC_CURVE = 'cubic-bezier(0.2, 0.9, 0.3, 1)';

/**
 * Returns true if a touch/pen gesture starts inside the left or right viewport edge guard zone
 * reserved for OS-level navigation gestures (Android predictive back, iOS edge swipe-back/forward).
 */
export function isInEdgeGuardZone(clientX, viewportWidth, edgeGuardPx = EDGE_GUARD_DEFAULT_PX) {
    if (!Number.isFinite(clientX) || !Number.isFinite(viewportWidth) || viewportWidth <= 0) {
        return false;
    }
    const guard = Math.max(0, Number(edgeGuardPx) || 0);
    if (guard <= 0) return false;
    return clientX < guard || clientX > (viewportWidth - guard);
}

/**
 * Resolves gesture axis lock once movement passes slop.
 * Returns 'undecided' | 'horizontal' | 'vertical'.
 */
export function resolveAxisLock(dx, dy, slopPx = TOUCH_SLOP_PX, horizontalBias = HORIZONTAL_BIAS_RATIO) {
    const absX = Math.abs(dx);
    const absY = Math.abs(dy);
    if (absX < slopPx && absY < slopPx) {
        return 'undecided';
    }
    if (absX > absY * horizontalBias) {
        return 'horizontal';
    }
    return 'vertical';
}

/**
 * Decides whether a released horizontal swipe should commit to the previous (-1) or next (+1) pane,
 * or snap back to center (0).
 * Swiping finger left (dx < 0) navigates forward (+1); swiping right (dx > 0) navigates backward (-1).
 */
export function shouldCommitSwipe(dx, velocityPxPerMs, paneWidth) {
    if (!Number.isFinite(paneWidth) || paneWidth <= 0 || !Number.isFinite(dx) || dx === 0) {
        return 0;
    }
    const absDx = Math.abs(dx);
    const passedDistance = absDx >= paneWidth * COMMIT_DISTANCE_RATIO;
    const passedFlick = Number.isFinite(velocityPxPerMs)
        && Math.abs(velocityPxPerMs) >= COMMIT_FLICK_VELOCITY
        && Math.sign(velocityPxPerMs) === Math.sign(dx)
        && absDx >= COMMIT_FLICK_MIN_DISTANCE_PX;

    if (!passedDistance && !passedFlick) {
        return 0;
    }
    return dx < 0 ? 1 : -1;
}

/**
 * Linearly interpolates viewport height between current pane height and incoming target pane height.
 */
export function computeInterpolatedHeight(currentHeight, targetHeight, progress) {
    if (!Number.isFinite(currentHeight) || currentHeight <= 0) return null;
    if (!Number.isFinite(targetHeight) || targetHeight <= 0) return currentHeight;
    const t = Math.max(0, Math.min(1, Number(progress) || 0));
    return currentHeight + (targetHeight - currentHeight) * t;
}

/**
 * Computes a snappy, velocity-aware settle animation duration in milliseconds.
 */
export function computeSettleDurationMs(remainingDistancePx, velocityPxPerMs, reducedMotion = false) {
    if (reducedMotion) return 0;
    const dist = Math.max(0, Math.abs(Number(remainingDistancePx) || 0));
    if (dist < 1) return MIN_SETTLE_DURATION_MS;
    const speed = Math.max(0.9, Math.abs(Number(velocityPxPerMs) || 0) * 1.15);
    const rawMs = dist / speed;
    return Math.round(Math.max(MIN_SETTLE_DURATION_MS, Math.min(MAX_SETTLE_DURATION_MS, rawMs)));
}

/**
 * Checks whether any ancestor between targetEl and stopEl (exclusive) can scroll horizontally
 * in the direction of the finger drag (dx > 0 means scrolling content left / decreasing scrollLeft;
 * dx < 0 means scrolling content right / increasing scrollLeft).
 */
export function hasScrollableHorizontalAncestor(targetEl, stopEl, dx) {
    if (!targetEl || !stopEl) return false;
    let el = targetEl;
    while (el && el !== stopEl) {
        if (el.nodeType === 1 && el.scrollWidth > el.clientWidth + 1) {
            const style = typeof window !== 'undefined' && typeof window.getComputedStyle === 'function'
                ? window.getComputedStyle(el)
                : null;
            const overflowX = style ? style.overflowX : '';
            if (overflowX === 'auto' || overflowX === 'scroll') {
                const maxScroll = el.scrollWidth - el.clientWidth;
                if (dx < 0 && el.scrollLeft < maxScroll - 1) {
                    return true;
                }
                if (dx > 0 && el.scrollLeft > 1) {
                    return true;
                }
            }
        }
        el = el.parentElement;
    }
    return false;
}

function prefersReducedMotion() {
    return typeof window !== 'undefined'
        && typeof window.matchMedia === 'function'
        && Boolean(window.matchMedia('(prefers-reduced-motion: reduce)').matches);
}

function getViewportWidth() {
    if (typeof window !== 'undefined' && Number.isFinite(window.innerWidth) && window.innerWidth > 0) {
        return window.innerWidth;
    }
    if (typeof document !== 'undefined' && document.documentElement && document.documentElement.clientWidth > 0) {
        return document.documentElement.clientWidth;
    }
    return 0;
}

export class SwipePagerController {
    constructor(viewportEl, trackEl, dotNetRef, options = {}) {
        this.viewportEl = viewportEl;
        this.trackEl = trackEl;
        this.dotNetRef = dotNetRef;
        this.edgeGuardPx = Number.isFinite(options.edgeGuardPx) ? options.edgeGuardPx : EDGE_GUARD_DEFAULT_PX;
        this.animateHeight = options.animateHeight !== false;
        this.destroyed = false;

        // Gesture tracking state
        this.activePointerId = null;
        this.activeTouchPointers = new Set();
        this.axisLock = 'idle'; // 'idle' | 'undecided' | 'horizontal' | 'vertical' | 'ignored'
        this.startX = 0;
        this.startY = 0;
        this.currentDx = 0;
        this.downTarget = null;
        this.paneWidth = 0;
        this.paneHeights = { prev: 0, curr: 0, next: 0 };
        this.moveSamples = [];

        // Animation & Blazor commit synchronization state
        this.isAnimating = false;
        this.awaitingServerCommit = false;
        this.activeAnimTimer = null;
        this.safetyTimer = null;
        this.pendingProgrammaticResolve = null;
        this.suppressClickUntil = 0;
        this.lastObservedPageKey = viewportEl ? (viewportEl.dataset?.pageKey ?? '') : '';

        this.onPointerDown = this.onPointerDown.bind(this);
        this.onPointerMove = this.onPointerMove.bind(this);
        this.onPointerUp = this.onPointerUp.bind(this);
        this.onPointerCancel = this.onPointerCancel.bind(this);
        this.onTouchMove = this.onTouchMove.bind(this);
        this.onClickCapture = this.onClickCapture.bind(this);
        this.onVisibilityOrBlur = this.onVisibilityOrBlur.bind(this);

        this.bind();
    }

    bind() {
        if (!this.viewportEl) return;

        this.viewportEl.addEventListener('pointerdown', this.onPointerDown);
        this.viewportEl.addEventListener('pointermove', this.onPointerMove);
        this.viewportEl.addEventListener('pointerup', this.onPointerUp);
        this.viewportEl.addEventListener('pointercancel', this.onPointerCancel);
        this.viewportEl.addEventListener('touchmove', this.onTouchMove, { passive: false });
        this.viewportEl.addEventListener('click', this.onClickCapture, true);

        if (typeof window !== 'undefined' && typeof window.addEventListener === 'function') {
            window.addEventListener('blur', this.onVisibilityOrBlur);
        }
        if (typeof document !== 'undefined' && typeof document.addEventListener === 'function') {
            document.addEventListener('visibilitychange', this.onVisibilityOrBlur);
        }

        // Observe data-page-key updates from Blazor's DOM patch microtask BEFORE browser paint
        // so resetting the track from +/-paneWidth back to center (0px) has zero flicker.
        if (typeof MutationObserver === 'function') {
            this.mutationObserver = new MutationObserver(() => {
                if (this.destroyed || !this.viewportEl) return;
                const nextKey = this.viewportEl.dataset?.pageKey ?? '';
                if (nextKey !== this.lastObservedPageKey) {
                    this.lastObservedPageKey = nextKey;
                    if (this.awaitingServerCommit) {
                        this.snapToCenterImmediate();
                    }
                }
            });
            this.mutationObserver.observe(this.viewportEl, {
                attributes: true,
                attributeFilter: ['data-page-key']
            });
        }
    }

    updateOptions(options = {}) {
        if (Number.isFinite(options.edgeGuardPx)) {
            this.edgeGuardPx = options.edgeGuardPx;
        }
        if (typeof options.animateHeight === 'boolean') {
            this.animateHeight = options.animateHeight;
        }
    }

    measurePanes() {
        if (!this.viewportEl || !this.trackEl) return;
        const rectWidth = typeof this.viewportEl.getBoundingClientRect === 'function'
            ? this.viewportEl.getBoundingClientRect().width
            : 0;
        this.paneWidth = this.viewportEl.clientWidth || rectWidth || 0;

        const panes = this.trackEl.children;
        if (panes && panes.length >= 3) {
            this.paneHeights = {
                prev: panes[0].offsetHeight || panes[0].scrollHeight || 0,
                curr: panes[1].offsetHeight || panes[1].scrollHeight || 0,
                next: panes[2].offsetHeight || panes[2].scrollHeight || 0
            };
        }
    }

    clearTimers() {
        if (this.activeAnimTimer !== null) {
            clearTimeout(this.activeAnimTimer);
            this.activeAnimTimer = null;
        }
        if (this.safetyTimer !== null) {
            clearTimeout(this.safetyTimer);
            this.safetyTimer = null;
        }
    }

    resolvePendingProgrammatic(value) {
        if (this.pendingProgrammaticResolve) {
            const resolve = this.pendingProgrammaticResolve;
            this.pendingProgrammaticResolve = null;
            resolve(value);
        }
    }

    snapToCenterImmediate() {
        this.clearTimers();
        this.isAnimating = false;
        this.awaitingServerCommit = false;
        this.currentDx = 0;
        this.axisLock = 'idle';
        this.activePointerId = null;
        this.downTarget = null;
        this.moveSamples = [];
        this.resolvePendingProgrammatic(false);

        if (this.trackEl) {
            this.trackEl.style.transition = 'none';
            this.trackEl.style.transform = '';
            // Flush inline transition:none before clearing it so subsequent CSS rules never animate the snap.
            void this.trackEl.offsetHeight;
            this.trackEl.style.transition = '';
        }
        if (this.viewportEl) {
            this.viewportEl.style.transition = 'none';
            this.viewportEl.style.height = '';
            void this.viewportEl.offsetHeight;
            this.viewportEl.style.transition = '';
            this.viewportEl.classList.remove('is-swiping');
        }
    }

    applyDragOffset(dx) {
        if (!this.trackEl || this.paneWidth <= 0) return;
        const clamped = Math.max(-this.paneWidth, Math.min(this.paneWidth, dx));
        this.currentDx = clamped;
        this.trackEl.style.transition = 'none';
        this.trackEl.style.transform = `translate3d(${clamped}px, 0, 0)`;

        if (this.animateHeight && this.viewportEl && this.paneHeights.curr > 0) {
            const targetH = clamped < 0 ? this.paneHeights.next : this.paneHeights.prev;
            if (targetH > 0 && Math.abs(targetH - this.paneHeights.curr) > 0.5) {
                const progress = Math.abs(clamped) / this.paneWidth;
                const h = computeInterpolatedHeight(this.paneHeights.curr, targetH, progress);
                if (h !== null) {
                    this.viewportEl.style.transition = 'none';
                    this.viewportEl.style.height = `${h.toFixed(1)}px`;
                }
            }
        }
    }

    computeReleaseVelocity() {
        const now = performance.now();
        const fresh = this.moveSamples.filter(s => (now - s.t) <= VELOCITY_SAMPLE_MAX_AGE_MS);
        if (fresh.length < 2) return 0;
        const first = fresh[0];
        const last = fresh[fresh.length - 1];
        const dt = last.t - first.t;
        if (dt <= 0) return 0;
        return (last.x - first.x) / dt;
    }

    animateSettle(targetDelta, durationMs, curve, onComplete) {
        if (!this.trackEl || this.paneWidth <= 0) {
            onComplete();
            return;
        }

        this.clearTimers();
        this.isAnimating = true;

        // Center pane is at 0; next pane (+1) is at left: 100% so track moves to -paneWidth;
        // previous pane (-1) is at left: -100% so track moves to +paneWidth.
        const targetTranslateX = targetDelta === 0
            ? 0
            : (targetDelta > 0 ? -this.paneWidth : this.paneWidth);

        const targetHeight = targetDelta === 0
            ? this.paneHeights.curr
            : (targetDelta > 0 ? this.paneHeights.next : this.paneHeights.prev);

        if (durationMs <= 0) {
            this.trackEl.style.transition = 'none';
            this.trackEl.style.transform = `translate3d(${targetTranslateX}px, 0, 0)`;
            if (this.animateHeight && this.viewportEl && targetHeight > 0) {
                this.viewportEl.style.transition = 'none';
                this.viewportEl.style.height = `${targetHeight}px`;
            }
            this.isAnimating = false;
            onComplete();
            return;
        }

        this.trackEl.style.transition = `transform ${durationMs}ms ${curve}`;
        this.trackEl.style.transform = `translate3d(${targetTranslateX}px, 0, 0)`;

        if (this.animateHeight && this.viewportEl && targetHeight > 0 && this.paneHeights.curr > 0) {
            if (Math.abs(targetHeight - this.paneHeights.curr) > 0.5 || this.viewportEl.style.height) {
                this.viewportEl.style.transition = `height ${durationMs}ms ${curve}`;
                this.viewportEl.style.height = `${targetHeight}px`;
            }
        }

        this.activeAnimTimer = setTimeout(() => {
            this.activeAnimTimer = null;
            this.isAnimating = false;
            onComplete();
        }, durationMs + 16);
    }

    /**
     * Programmatic fast slide for Previous (-1) / Next (+1) chevron buttons.
     * Resolves true when the slide finishes and Blazor should now shift the month;
     * resolves false if interrupted/snapped (e.g., rapid clicks).
     */
    animateNavigate(delta) {
        if (this.destroyed || !this.viewportEl || !this.trackEl) {
            return Promise.resolve(false);
        }
        const normalizedDelta = delta > 0 ? 1 : (delta < 0 ? -1 : 0);
        if (normalizedDelta === 0) return Promise.resolve(false);

        // If already animating, dragging, or waiting for a previous server commit,
        // snap cleanly and signal Blazor to advance immediately without queuing animations.
        if (this.isAnimating || this.awaitingServerCommit || this.axisLock === 'horizontal') {
            this.snapToCenterImmediate();
            return Promise.resolve(false);
        }

        this.measurePanes();
        if (this.paneWidth <= 0) {
            return Promise.resolve(false);
        }

        // Anchor initial transform and height synchronously before animating.
        this.trackEl.style.transition = 'none';
        this.trackEl.style.transform = 'translate3d(0px, 0, 0)';
        if (this.animateHeight && this.paneHeights.curr > 0) {
            const targetH = normalizedDelta > 0 ? this.paneHeights.next : this.paneHeights.prev;
            if (targetH > 0 && Math.abs(targetH - this.paneHeights.curr) > 0.5) {
                this.viewportEl.style.transition = 'none';
                this.viewportEl.style.height = `${this.paneHeights.curr}px`;
            }
        }
        // Force layout so the browser registers the starting transform/height before applying transition.
        void this.trackEl.offsetHeight;

        const durationMs = prefersReducedMotion() ? 0 : PROGRAMMATIC_DURATION_MS;

        return new Promise((resolve) => {
            this.pendingProgrammaticResolve = resolve;
            this.animateSettle(normalizedDelta, durationMs, FAST_PROGRAMMATIC_CURVE, () => {
                if (this.destroyed) {
                    this.resolvePendingProgrammatic(false);
                    return;
                }
                this.awaitingServerCommit = true;
                this.armCommitSafetyTimer();
                this.resolvePendingProgrammatic(true);
            });
        });
    }

    armCommitSafetyTimer() {
        if (this.safetyTimer !== null) {
            clearTimeout(this.safetyTimer);
        }
        this.safetyTimer = setTimeout(() => {
            this.safetyTimer = null;
            if (this.awaitingServerCommit && !this.destroyed) {
                this.snapToCenterImmediate();
            }
        }, COMMIT_SAFETY_TIMEOUT_MS);
    }

    notifyCommitFromBlazor(pageKey) {
        if (this.destroyed) return;
        if (typeof pageKey === 'string') {
            this.lastObservedPageKey = pageKey;
        }
        if (this.awaitingServerCommit) {
            this.snapToCenterImmediate();
        }
    }

    onPointerDown(e) {
        if (this.destroyed || !this.viewportEl || !this.trackEl) return;

        // Touch and pen only — never intercept desktop mouse clicks, double-clicks, or text selection.
        if (e.pointerType !== 'touch' && e.pointerType !== 'pen') {
            return;
        }

        this.activeTouchPointers.add(e.pointerId);

        // Multi-touch detected (e.g., pinch-zoom or iPadOS multi-finger OS gesture): abort swipe cleanly.
        if (this.activeTouchPointers.size > 1) {
            this.abortActiveDrag(true);
            return;
        }

        // Ignore new drags while waiting for a committed month transition to render.
        if (this.awaitingServerCommit) {
            return;
        }

        // Protect OS screen-edge gestures (Android predictive back, iOS edge swipe-back/forward).
        if (isInEdgeGuardZone(e.clientX, getViewportWidth(), this.edgeGuardPx)) {
            this.axisLock = 'ignored';
            this.activePointerId = e.pointerId;
            return;
        }

        // Allow interrupting a snap-back animation cleanly.
        if (this.isAnimating) {
            this.snapToCenterImmediate();
        }

        this.activePointerId = e.pointerId;
        this.axisLock = 'undecided';
        this.startX = e.clientX;
        this.startY = e.clientY;
        this.currentDx = 0;
        this.downTarget = e.target;
        this.moveSamples = [{ x: e.clientX, t: performance.now() }];
        this.measurePanes();
    }

    onPointerMove(e) {
        if (this.destroyed || e.pointerId !== this.activePointerId) return;
        if (this.axisLock === 'idle' || this.axisLock === 'vertical' || this.axisLock === 'ignored') {
            return;
        }

        const dx = e.clientX - this.startX;
        const dy = e.clientY - this.startY;

        if (this.axisLock === 'undecided') {
            const decision = resolveAxisLock(dx, dy, TOUCH_SLOP_PX, HORIZONTAL_BIAS_RATIO);
            if (decision === 'undecided') {
                return;
            }
            if (decision === 'vertical') {
                this.axisLock = 'vertical';
                return;
            }
            // Yield to any inner horizontally scrollable element that can scroll in this direction.
            if (hasScrollableHorizontalAncestor(this.downTarget, this.viewportEl, dx)) {
                this.axisLock = 'ignored';
                return;
            }

            this.axisLock = 'horizontal';
            this.viewportEl.classList.add('is-swiping');
            this.measurePanes();
            if (this.animateHeight && this.paneHeights.curr > 0) {
                this.viewportEl.style.height = `${this.paneHeights.curr}px`;
            }
            try {
                this.viewportEl.setPointerCapture(e.pointerId);
            } catch (_) {}
        }

        if (this.axisLock === 'horizontal') {
            const now = performance.now();
            this.moveSamples.push({ x: e.clientX, t: now });
            if (this.moveSamples.length > 6) {
                this.moveSamples.shift();
            }
            this.applyDragOffset(dx);
        }
    }

    onTouchMove(e) {
        // Only prevent vertical scroll once horizontal axis lock is actively engaged.
        // Never preventDefault on edge touches, undecided touches, or vertical scroll.
        if (this.axisLock === 'horizontal') {
            if (e.touches && e.touches.length > 1) {
                this.abortActiveDrag(true);
                return;
            }
            if (e.cancelable) {
                e.preventDefault();
            }
        }
    }

    onPointerUp(e) {
        this.activeTouchPointers.delete(e.pointerId);
        if (this.destroyed || e.pointerId !== this.activePointerId) return;

        const wasHorizontal = this.axisLock === 'horizontal';
        const finalDx = this.currentDx;
        const velocity = wasHorizontal ? this.computeReleaseVelocity() : 0;

        this.releasePointerCaptureSafe(e.pointerId);
        this.activePointerId = null;
        this.downTarget = null;
        this.axisLock = 'idle';
        this.moveSamples = [];
        this.viewportEl?.classList.remove('is-swiping');

        if (!wasHorizontal) {
            return;
        }

        // Suppress trailing synthetic click after a horizontal swipe so releasing on a day/event never taps it.
        this.suppressClickUntil = performance.now() + CLICK_SUPPRESS_WINDOW_MS;

        const commitDelta = shouldCommitSwipe(finalDx, velocity, this.paneWidth);
        const reduced = prefersReducedMotion();

        if (commitDelta === 0) {
            const durationMs = computeSettleDurationMs(Math.abs(finalDx), velocity, reduced);
            this.animateSettle(0, durationMs, EASE_OUT_CURVE, () => {
                if (!this.destroyed) {
                    this.snapToCenterImmediate();
                }
            });
            return;
        }

        const remainingDist = Math.max(0, this.paneWidth - Math.abs(finalDx));
        const durationMs = computeSettleDurationMs(remainingDist, velocity, reduced);

        this.animateSettle(commitDelta, durationMs, EASE_OUT_CURVE, () => {
            if (this.destroyed) return;
            this.awaitingServerCommit = true;
            this.armCommitSafetyTimer();
            this.notifyDotNetSwipeCommit(commitDelta);
        });
    }

    onPointerCancel(e) {
        this.activeTouchPointers.delete(e.pointerId);
        if (this.destroyed || e.pointerId !== this.activePointerId) return;
        this.abortActiveDrag(true);
    }

    onClickCapture(e) {
        if (this.suppressClickUntil > 0 && performance.now() <= this.suppressClickUntil) {
            e.preventDefault();
            e.stopPropagation();
        }
    }

    onVisibilityOrBlur() {
        this.activeTouchPointers.clear();
        if (this.axisLock === 'horizontal' || this.activePointerId !== null) {
            this.abortActiveDrag(false);
        }
    }

    abortActiveDrag(animateBack) {
        const wasHorizontal = this.axisLock === 'horizontal';
        const dx = this.currentDx;
        if (this.activePointerId !== null) {
            this.releasePointerCaptureSafe(this.activePointerId);
        }
        this.activePointerId = null;
        this.downTarget = null;
        this.axisLock = 'idle';
        this.moveSamples = [];
        this.viewportEl?.classList.remove('is-swiping');

        if (!wasHorizontal) return;

        if (animateBack && !prefersReducedMotion() && Math.abs(dx) > 1) {
            const durationMs = computeSettleDurationMs(Math.abs(dx), 0, false);
            this.animateSettle(0, durationMs, EASE_OUT_CURVE, () => {
                if (!this.destroyed) {
                    this.snapToCenterImmediate();
                }
            });
        } else {
            this.snapToCenterImmediate();
        }
    }

    releasePointerCaptureSafe(pointerId) {
        try {
            if (this.viewportEl && typeof this.viewportEl.hasPointerCapture === 'function' && this.viewportEl.hasPointerCapture(pointerId)) {
                this.viewportEl.releasePointerCapture(pointerId);
            }
        } catch (_) {}
    }

    notifyDotNetSwipeCommit(delta) {
        if (!this.dotNetRef || this.destroyed) {
            this.snapToCenterImmediate();
            return;
        }
        try {
            const p = this.dotNetRef.invokeMethodAsync('HandleSwipeCommit', delta);
            if (p && typeof p.catch === 'function') {
                p.catch(() => {
                    if (!this.destroyed && this.awaitingServerCommit) {
                        this.snapToCenterImmediate();
                    }
                });
            }
        } catch (_) {
            this.snapToCenterImmediate();
        }
    }

    destroy() {
        this.destroyed = true;
        this.clearTimers();
        this.resolvePendingProgrammatic(false);
        this.activeTouchPointers.clear();

        if (this.mutationObserver) {
            this.mutationObserver.disconnect();
            this.mutationObserver = null;
        }

        if (this.viewportEl) {
            this.viewportEl.removeEventListener('pointerdown', this.onPointerDown);
            this.viewportEl.removeEventListener('pointermove', this.onPointerMove);
            this.viewportEl.removeEventListener('pointerup', this.onPointerUp);
            this.viewportEl.removeEventListener('pointercancel', this.onPointerCancel);
            this.viewportEl.removeEventListener('touchmove', this.onTouchMove, { passive: false });
            this.viewportEl.removeEventListener('click', this.onClickCapture, true);
        }

        if (typeof window !== 'undefined' && typeof window.removeEventListener === 'function') {
            window.removeEventListener('blur', this.onVisibilityOrBlur);
        }
        if (typeof document !== 'undefined' && typeof document.removeEventListener === 'function') {
            document.removeEventListener('visibilitychange', this.onVisibilityOrBlur);
        }

        this.viewportEl = null;
        this.trackEl = null;
        this.dotNetRef = null;
    }
}

export function createSwipePager(viewportEl, trackEl, dotNetRef, options) {
    return new SwipePagerController(viewportEl, trackEl, dotNetRef, options);
}
