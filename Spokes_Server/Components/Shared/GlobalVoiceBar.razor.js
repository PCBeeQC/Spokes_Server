// GlobalVoiceBar.razor.js — Real-time 1:1 pointer/touch pull-down & push-up drawer controller

let _controller = null;

class VoiceBarPullDrawerController {
    constructor(wrapperEl, contentEl, pullTabEl, iconEl, dotNetRef, initialExpanded) {
        this.wrapperEl = wrapperEl;
        this.contentEl = contentEl;
        this.pullTabEl = pullTabEl;
        this.iconEl = iconEl;
        this.dotNetRef = dotNetRef;
        this.isExpanded = Boolean(initialExpanded);

        this.isPointerDown = false;
        this.hasDragged = false;
        this.isAnimating = false;
        this.activePointerId = null;
        this.dragThreshold = 5;
        this.startY = 0;
        this.startHeight = 0;
        this.maxHeight = 0;
        this.lastY = 0;
        this.lastTime = 0;
        this.velocityY = 0;
        this.lastPointerFinishAt = 0;
        this.lastNotifiedExpanded = this.isExpanded;
        this.awaitingServerAckExpanded = null;
        this.animTimeout = null;

        this.onPointerDown = this.onPointerDown.bind(this);
        this.onPointerMove = this.onPointerMove.bind(this);
        this.onPointerUp = this.onPointerUp.bind(this);
        this.onPointerCancel = this.onPointerCancel.bind(this);
        this.onClick = this.onClick.bind(this);
        this.onKeyDown = this.onKeyDown.bind(this);
        this.onTouchMovePreventScroll = this.onTouchMovePreventScroll.bind(this);

        this.bind();
        this.applyStaticState(this.isExpanded);
    }

    bind() {
        if (!this.pullTabEl) return;
        this.pullTabEl.addEventListener('pointerdown', this.onPointerDown);
        this.pullTabEl.addEventListener('pointermove', this.onPointerMove);
        this.pullTabEl.addEventListener('pointerup', this.onPointerUp);
        this.pullTabEl.addEventListener('pointercancel', this.onPointerCancel);
        this.pullTabEl.addEventListener('click', this.onClick);
        this.pullTabEl.addEventListener('keydown', this.onKeyDown);
        this.pullTabEl.addEventListener('touchmove', this.onTouchMovePreventScroll, { passive: false });
    }

    destroy() {
        if (this.animTimeout) {
            clearTimeout(this.animTimeout);
            this.animTimeout = null;
        }
        if (this.pullTabEl) {
            this.pullTabEl.removeEventListener('pointerdown', this.onPointerDown);
            this.pullTabEl.removeEventListener('pointermove', this.onPointerMove);
            this.pullTabEl.removeEventListener('pointerup', this.onPointerUp);
            this.pullTabEl.removeEventListener('pointercancel', this.onPointerCancel);
            this.pullTabEl.removeEventListener('click', this.onClick);
            this.pullTabEl.removeEventListener('keydown', this.onKeyDown);
            this.pullTabEl.removeEventListener('touchmove', this.onTouchMovePreventScroll, { passive: false });
        }
        this.wrapperEl = null;
        this.contentEl = null;
        this.pullTabEl = null;
        this.iconEl = null;
        this.dotNetRef = null;
    }

    onTouchMovePreventScroll(e) {
        if (this.isPointerDown) {
            e.preventDefault();
        }
    }

    measureMaxHeight() {
        if (!this.contentEl) return 64;
        const h = this.contentEl.scrollHeight || this.contentEl.offsetHeight || 0;
        return Math.max(40, h);
    }

    notifyServerExpanded(expanded) {
        const next = Boolean(expanded);
        if (!this.dotNetRef || next === this.lastNotifiedExpanded) return;
        this.lastNotifiedExpanded = next;
        this.awaitingServerAckExpanded = next;
        try {
            const p = this.dotNetRef.invokeMethodAsync('OnDrawerExpandedChanged', next);
            if (p && typeof p.catch === 'function') {
                p.catch(() => {
                    if (this.awaitingServerAckExpanded === next) {
                        this.awaitingServerAckExpanded = null;
                    }
                });
            }
        } catch (_) {
            if (this.awaitingServerAckExpanded === next) {
                this.awaitingServerAckExpanded = null;
            }
        }
    }

    applyStaticState(expanded) {
        this.isExpanded = Boolean(expanded);
        this.lastNotifiedExpanded = this.isExpanded;
        this.awaitingServerAckExpanded = null;
        if (!this.wrapperEl) return;
        this.wrapperEl.style.transition = '';
        this.wrapperEl.style.height = this.isExpanded ? 'auto' : '0px';
        this.wrapperEl.classList.toggle('is-expanded', this.isExpanded);
        if (this.iconEl) {
            this.iconEl.style.transition = '';
            this.iconEl.style.transform = this.isExpanded ? 'rotate(180deg)' : 'rotate(0deg)';
        }
        if (this.contentEl) {
            this.contentEl.style.transition = '';
            this.contentEl.style.opacity = '';
        }
    }

    syncFromServer(expanded, dotNetRef) {
        if (dotNetRef) {
            this.dotNetRef = dotNetRef;
        }
        const target = Boolean(expanded);
        if (this.awaitingServerAckExpanded !== null && target === this.awaitingServerAckExpanded) {
            this.awaitingServerAckExpanded = null;
        }
        if (this.isPointerDown) {
            if (target) {
                this.maxHeight = this.measureMaxHeight();
            }
            return;
        }
        if (this.isAnimating) {
            if (this.isExpanded && target && this.wrapperEl) {
                const measured = this.measureMaxHeight();
                if (measured > this.maxHeight) {
                    this.maxHeight = measured;
                    this.wrapperEl.style.height = `${measured}px`;
                }
            }
            return;
        }
        // Ignore stale in-flight echoes (e.g. a pointerdown pre-mount arriving over WAN after
        // the user aborted the gesture and committed a collapse).
        if (this.awaitingServerAckExpanded !== null && target !== this.awaitingServerAckExpanded) {
            return;
        }
        this.lastNotifiedExpanded = target;
        if (target !== this.isExpanded) {
            this.applyStaticState(target);
        }
    }

    onClick() {
        if (this.isPointerDown) return;
        if (this.lastPointerFinishAt > 0 && (performance.now() - this.lastPointerFinishAt) < 400) {
            return;
        }
        this.hasDragged = false;
        this.animateToState(!this.isExpanded);
    }

    onKeyDown(e) {
        if (e.key !== 'Enter' && e.key !== ' ') return;
        e.preventDefault();
        this.hasDragged = false;
        this.animateToState(!this.isExpanded);
    }

    onPointerDown(e) {
        if (e.button !== undefined && e.button !== 0) return;
        if (!this.wrapperEl || !this.contentEl || !this.pullTabEl) return;

        if (this.animTimeout) {
            clearTimeout(this.animTimeout);
            this.animTimeout = null;
            this.isAnimating = false;
        }

        this.isPointerDown = true;
        this.hasDragged = false;
        this.activePointerId = e.pointerId;
        // Touch taps on Android/iOS naturally roll 6-12px; match platform touch slop so finger taps
        // reliably toggle the drawer instead of being misclassified as micro-drags that snap shut.
        this.dragThreshold = e.pointerType === 'touch' ? 12 : 5;
        try {
            this.pullTabEl.setPointerCapture(e.pointerId);
        } catch (_) {}

        if (!this.isExpanded) {
            this.notifyServerExpanded(true);
        }

        this.maxHeight = this.measureMaxHeight();
        const currentRectH = this.wrapperEl.getBoundingClientRect().height;
        this.startHeight = this.isExpanded ? Math.max(currentRectH, this.maxHeight) : currentRectH;

        this.wrapperEl.style.transition = 'none';
        this.wrapperEl.style.height = `${this.startHeight}px`;
        if (this.iconEl) {
            this.iconEl.style.transition = 'none';
            this.iconEl.style.transform = this.isExpanded ? 'rotate(180deg)' : 'rotate(0deg)';
        }
        if (this.contentEl) {
            this.contentEl.style.transition = 'none';
        }

        this.startY = e.clientY;
        this.lastY = e.clientY;
        this.lastTime = performance.now();
        this.velocityY = 0;
    }

    onPointerMove(e) {
        if (!this.isPointerDown || e.pointerId !== this.activePointerId) return;
        if (!this.wrapperEl || !this.contentEl) return;

        const now = performance.now();
        const dt = now - this.lastTime;
        const totalDy = e.clientY - this.startY;

        if (dt > 0) {
            const instantVy = (e.clientY - this.lastY) / dt;
            this.velocityY = this.velocityY * 0.35 + instantVy * 0.65;
            this.lastY = e.clientY;
            this.lastTime = now;
        }

        if (!this.hasDragged && Math.abs(totalDy) > this.dragThreshold) {
            this.hasDragged = true;
            this.maxHeight = this.measureMaxHeight();
        }

        if (!this.hasDragged) return;

        e.preventDefault();
        const rawHeight = this.startHeight + totalDy;
        let newHeight;
        if (rawHeight <= 0) {
            newHeight = 0;
        } else if (rawHeight <= this.maxHeight) {
            newHeight = rawHeight;
        } else {
            // Subtle elastic resistance when pulling past full drawer height
            newHeight = this.maxHeight + Math.min(24, (rawHeight - this.maxHeight) * 0.18);
        }

        this.wrapperEl.style.height = `${newHeight}px`;
        const progress = Math.max(0, Math.min(1, newHeight / Math.max(1, this.maxHeight)));
        if (this.iconEl) {
            this.iconEl.style.transform = `rotate(${Math.round(progress * 180)}deg)`;
        }
        if (this.contentEl) {
            this.contentEl.style.opacity = `${(0.35 + 0.65 * progress).toFixed(3)}`;
        }
    }

    onPointerUp(e) {
        if (!this.isPointerDown || e.pointerId !== this.activePointerId) return;
        this.finishGesture(false);
    }

    onPointerCancel(e) {
        if (!this.isPointerDown || e.pointerId !== this.activePointerId) return;
        this.finishGesture(true);
    }

    finishGesture(wasCanceled) {
        const pointerId = this.activePointerId;
        this.isPointerDown = false;
        this.activePointerId = null;

        if (this.pullTabEl && pointerId !== null) {
            try {
                if (this.pullTabEl.hasPointerCapture(pointerId)) {
                    this.pullTabEl.releasePointerCapture(pointerId);
                }
            } catch (_) {}
        }

        if (!this.wrapperEl || !this.contentEl) return;

        this.maxHeight = this.measureMaxHeight();
        let targetExpanded;

        if (wasCanceled) {
            targetExpanded = this.isExpanded;
        } else if (!this.hasDragged) {
            // Clean click / tap toggles the drawer
            targetExpanded = !this.isExpanded;
        } else {
            const currH = this.wrapperEl.getBoundingClientRect().height;
            if (this.velocityY > 0.35) {
                targetExpanded = true;
            } else if (this.velocityY < -0.35) {
                targetExpanded = false;
            } else {
                targetExpanded = currH > (this.maxHeight * 0.4);
            }
        }

        this.lastPointerFinishAt = performance.now();
        this.animateToState(targetExpanded);
    }

    animateToState(targetExpanded) {
        if (!this.wrapperEl || !this.contentEl) return;

        this.isExpanded = Boolean(targetExpanded);
        this.isAnimating = true;
        if (this.isExpanded) {
            this.notifyServerExpanded(true);
        }
        this.maxHeight = this.measureMaxHeight();

        if (!this.hasDragged) {
            const fromH = targetExpanded ? 0 : this.maxHeight;
            this.wrapperEl.style.transition = 'none';
            this.wrapperEl.style.height = `${fromH}px`;
            void this.wrapperEl.offsetHeight;
        }

        const targetH = targetExpanded ? this.maxHeight : 0;
        this.wrapperEl.style.transition = 'height 0.22s cubic-bezier(0.22, 1, 0.36, 1)';
        this.wrapperEl.style.height = `${targetH}px`;
        this.wrapperEl.classList.toggle('is-expanded', this.isExpanded);

        if (this.iconEl) {
            this.iconEl.style.transition = 'transform 0.22s cubic-bezier(0.22, 1, 0.36, 1)';
            this.iconEl.style.transform = this.isExpanded ? 'rotate(180deg)' : 'rotate(0deg)';
        }
        if (this.contentEl) {
            this.contentEl.style.transition = 'opacity 0.18s ease';
            this.contentEl.style.opacity = this.isExpanded ? '1' : '0';
        }

        if (this.animTimeout) {
            clearTimeout(this.animTimeout);
        }
        const committedExpanded = this.isExpanded;
        this.animTimeout = setTimeout(() => {
            this.animTimeout = null;
            this.isAnimating = false;
            if (!this.isPointerDown && this.wrapperEl) {
                this.wrapperEl.style.transition = '';
                this.wrapperEl.style.height = this.isExpanded ? 'auto' : '0px';
                if (this.iconEl) {
                    this.iconEl.style.transition = '';
                    this.iconEl.style.transform = this.isExpanded ? 'rotate(180deg)' : 'rotate(0deg)';
                }
                if (this.contentEl) {
                    this.contentEl.style.transition = '';
                    this.contentEl.style.opacity = '';
                }
                if (this.dotNetRef) {
                    console.debug(`[VoiceBarDrawer] Committed drawer state expanded=${committedExpanded}`);
                    this.notifyServerExpanded(committedExpanded);
                }
            }
        }, 235);
    }
}

export function syncVoiceBarPullDrawer(wrapperId, contentId, pullTabId, iconId, dotNetRef, isExpanded) {
    const wrapperEl = document.getElementById(wrapperId);
    const contentEl = document.getElementById(contentId);
    const pullTabEl = document.getElementById(pullTabId);
    const iconEl = document.getElementById(iconId);

    if (!wrapperEl || !contentEl || !pullTabEl) {
        if (_controller) {
            _controller.destroy();
            _controller = null;
        }
        return false;
    }

    if (_controller &&
        _controller.pullTabEl === pullTabEl &&
        _controller.wrapperEl === wrapperEl &&
        _controller.contentEl === contentEl &&
        _controller.iconEl === iconEl) {
        _controller.syncFromServer(isExpanded, dotNetRef);
        return true;
    }

    if (_controller) {
        _controller.destroy();
    }
    _controller = new VoiceBarPullDrawerController(wrapperEl, contentEl, pullTabEl, iconEl, dotNetRef, isExpanded);
    return true;
}

export function disposeVoiceBarPullDrawer() {
    if (_controller) {
        _controller.destroy();
        _controller = null;
    }
}
