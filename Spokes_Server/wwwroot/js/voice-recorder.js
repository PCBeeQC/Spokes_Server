(function () {
    function getSupportedAudioRecorderOptions() {
        for (const mimeType of ['audio/webm', 'audio/mp4', 'audio/ogg']) {
            if (MediaRecorder.isTypeSupported(mimeType)) {
                return { mimeType };
            }
        }
        return {};
    }

    function startWaveformVisualizer(state, canvasId, fillStyle, minBarHeight) {
        if (!canvasId || !state.stream) return;
        const canvas = document.getElementById(canvasId);
        if (!canvas) return;

        const canvasCtx = canvas.getContext('2d');
        const dpr = window.devicePixelRatio || 1;
        const rect = canvas.getBoundingClientRect();
        canvas.width = rect.width * dpr;
        canvas.height = rect.height * dpr;
        canvasCtx.scale(dpr, dpr);

        const AudioContext = window.AudioContext || window.webkitAudioContext;
        state.audioCtx = new AudioContext();
        state.analyser = state.audioCtx.createAnalyser();
        state.source = state.audioCtx.createMediaStreamSource(state.stream);
        state.source.connect(state.analyser);

        state.analyser.fftSize = 64;
        const bufferLength = state.analyser.frequencyBinCount;
        const dataArray = new Uint8Array(bufferLength);

        const draw = () => {
            if (!state.stream) return;
            state.animationId = requestAnimationFrame(draw);

            state.analyser.getByteFrequencyData(dataArray);
            canvasCtx.clearRect(0, 0, rect.width, rect.height);
            const barWidth = (rect.width / bufferLength) * 2;
            let x = 0;

            for (let i = 0; i < bufferLength; i++) {
                const rawHeight = (dataArray[i] / 255) * rect.height;
                const barHeight = minBarHeight > 0 ? Math.max(minBarHeight, rawHeight) : rawHeight;
                canvasCtx.fillStyle = fillStyle;
                canvasCtx.fillRect(x, rect.height / 2 - rawHeight / 2, barWidth, barHeight);
                x += barWidth + 1;
            }
        };
        draw();
    }

    function cleanupRecorderResources(state, { stopMediaRecorder = false, clearChunks = false } = {}) {
        if (state.animationId) {
            cancelAnimationFrame(state.animationId);
            state.animationId = null;
        }
        if (stopMediaRecorder && state.mediaRecorder && state.mediaRecorder.state !== 'inactive') {
            try { state.mediaRecorder.stop(); } catch (e) {}
        }
        if (state.source) {
            try { state.source.disconnect(); } catch (e) {}
            state.source = null;
        }
        if (state.audioCtx) {
            try { state.audioCtx.close(); } catch (e) {}
            state.audioCtx = null;
        }
        if (state.stream) {
            try { state.stream.getTracks().forEach(track => track.stop()); } catch (e) {}
            state.stream = null;
        }
        if (clearChunks) {
            state.audioChunks = [];
        }
    }

    window.spokesVoiceRecorder = {
        mediaRecorder: null,
        audioChunks: [],
        stream: null,

        // Audio Context and Visualizer properties
        audioCtx: null,
        analyser: null,
        source: null,
        animationId: null,

        startRecording: async function (canvasId = null) {
            this.audioChunks = [];

            if (!navigator.mediaDevices || !navigator.mediaDevices.getUserMedia) {
                console.error('[spokesVoiceRecorder] getUserMedia not supported in this browser.');
                return false;
            }

            try {
                this.stream = await navigator.mediaDevices.getUserMedia({ audio: true });
                this.mediaRecorder = new MediaRecorder(this.stream, getSupportedAudioRecorderOptions());

                this.mediaRecorder.ondataavailable = (e) => {
                    if (e.data.size > 0) {
                        this.audioChunks.push(e.data);
                    }
                };

                this.mediaRecorder.start(250); // chunk every 250ms
                startWaveformVisualizer(this, canvasId, 'rgba(244, 67, 54, 0.8)', 0);
                return true;
            } catch (err) {
                console.error('[spokesVoiceRecorder] Error starting recording:', err);
                return false;
            }
        },

        cancelRecording: function () {
            cleanupRecorderResources(this, { stopMediaRecorder: true, clearChunks: true });
        },

        stopAndUploadRecording: function (uploadUrl, uploaderDotNetRef) {
            return new Promise((resolve, reject) => {
                if (!this.mediaRecorder || this.mediaRecorder.state === 'inactive') {
                    reject('Recorder not active');
                    return;
                }

                this.mediaRecorder.onstop = () => {
                    const mimeType = this.mediaRecorder.mimeType || 'audio/webm';
                    let ext = 'weba'; // Use .weba to explicitly denote audio and bypass video player logic
                    if (mimeType.includes('mp4')) ext = 'm4a';
                    else if (mimeType.includes('ogg')) ext = 'ogg';

                    const audioBlob = new Blob(this.audioChunks, { type: mimeType });

                    // Create a File object that works with spokesUpload
                    const fileName = `VoiceMessage_${new Date().toISOString().replace(/[:.]/g, '-')}.${ext}`;
                    const file = new File([audioBlob], fileName, { type: mimeType });

                    cleanupRecorderResources(this, { clearChunks: true });

                    // Push to the exact same upload pipeline as images/files
                    if (window.spokesUpload?.uploadFileObject) {
                        if (uploaderDotNetRef) {
                            uploaderDotNetRef.invokeMethodAsync('OnUploadStartedCallback', 1).catch(() => {});
                        }
                        window.spokesUpload.uploadFileObject(file, uploadUrl, uploaderDotNetRef)
                            .then(results => {
                                if (uploaderDotNetRef) {
                                    const jsonStr = typeof results === 'string' ? results : JSON.stringify(results);
                                    uploaderDotNetRef.invokeMethodAsync('OnUploadCompletedCallback', jsonStr).catch(() => {});
                                }
                                resolve(true);
                            })
                            .catch(err => {
                                if (uploaderDotNetRef) {
                                    uploaderDotNetRef.invokeMethodAsync('OnUploadErrorCallback', err.message).catch(() => {});
                                }
                                resolve(false);
                            });
                    } else {
                        console.error('[spokesVoiceRecorder] spokesUpload not available!');
                        resolve(false);
                    }
                };

                this.mediaRecorder.stop();
            });
        }
    };

    const _audioPlayerControllers = new WeakMap();
    const _activeAudioPlayers = new Set();
    let _audioPlayerObserver = null;

    function formatAudioTime(seconds) {
        if (!Number.isFinite(seconds) || seconds < 0) return '0:00';
        const total = Math.floor(seconds);
        const mins = Math.floor(total / 60);
        const secs = total % 60;
        return `${mins}:${secs < 10 ? '0' : ''}${secs}`;
    }

    function attachAudioPlayer(root) {
        if (!root || root.nodeType !== 1 || _audioPlayerControllers.has(root)) return;
        const audio = root.querySelector('audio');
        const toggleBtn = root.querySelector('[data-audio-toggle]');
        const slider = root.querySelector('[data-audio-slider]');
        if (!audio || !toggleBtn || !slider) return;

        const playIcon = root.querySelector('[data-audio-icon-play]');
        const pauseIcon = root.querySelector('[data-audio-icon-pause]');
        const currentLabel = root.querySelector('[data-audio-current]');
        const durationLabel = root.querySelector('[data-audio-duration]');
        const compactLabel = root.querySelector('[data-audio-compact-time]');
        const isCompact = root.getAttribute('data-compact') === 'true';

        const state = {
            root,
            audio,
            duration: 0,
            isPlaying: false,
            isProbingDuration: false,
            probeTimeoutId: null,
            lastSrc: audio.getAttribute('src') || ''
        };

        const updateToggleUi = () => {
            const label = state.isPlaying
                ? (root.getAttribute('data-label-pause') || 'Pause')
                : (root.getAttribute('data-label-play') || 'Play');
            toggleBtn.setAttribute('aria-label', label);
            toggleBtn.setAttribute('title', label);
            if (playIcon) playIcon.classList.toggle('d-none', state.isPlaying);
            if (pauseIcon) pauseIcon.classList.toggle('d-none', !state.isPlaying);
        };

        const updateTimeUi = () => {
            const cur = state.isProbingDuration ? 0 : (Number.isFinite(audio.currentTime) ? audio.currentTime : 0);
            const dur = state.duration > 0 ? state.duration : (Number.isFinite(audio.duration) && audio.duration > 0 ? audio.duration : 0);
            const pct = dur > 0 ? Math.min(100, Math.max(0, (cur / dur) * 100)) : 0;
            slider.value = String(pct);
            if (isCompact && compactLabel) {
                compactLabel.textContent = (state.isPlaying || cur > 0) ? formatAudioTime(cur) : formatAudioTime(dur);
            } else {
                if (currentLabel) currentLabel.textContent = formatAudioTime(cur);
                if (durationLabel) durationLabel.textContent = formatAudioTime(dur);
            }
        };

        const finishDurationProbe = () => {
            if (state.probeTimeoutId) {
                clearTimeout(state.probeTimeoutId);
                state.probeTimeoutId = null;
            }
            if (state.isProbingDuration) {
                state.isProbingDuration = false;
                try { audio.currentTime = 0; } catch (_) { }
            }
        };

        const probeDurationIfNeeded = () => {
            if (Number.isFinite(audio.duration) && audio.duration > 0) {
                state.duration = audio.duration;
                finishDurationProbe();
                updateTimeUi();
                return;
            }
            if (audio.duration === Infinity && !state.isProbingDuration) {
                state.isProbingDuration = true;
                state.probeTimeoutId = setTimeout(() => {
                    finishDurationProbe();
                    updateTimeUi();
                }, 2000);
                try { audio.currentTime = 1e101; } catch (_) {
                    finishDurationProbe();
                }
            }
        };

        const onDurationChange = () => {
            if (Number.isFinite(audio.duration) && audio.duration > 0) {
                state.duration = audio.duration;
                if (state.isProbingDuration) {
                    finishDurationProbe();
                }
                updateTimeUi();
            }
        };

        const onLoadedMetadata = () => {
            probeDurationIfNeeded();
        };

        const onTimeUpdate = () => {
            if (state.isProbingDuration) return;
            updateTimeUi();
        };

        const onPlay = () => {
            for (const other of _activeAudioPlayers) {
                if (other !== state && other.isPlaying) {
                    try { other.audio.pause(); } catch (_) { }
                }
            }
            state.isPlaying = true;
            updateToggleUi();
            updateTimeUi();
        };

        const onPause = () => {
            state.isPlaying = false;
            updateToggleUi();
            updateTimeUi();
        };

        const onEnded = () => {
            state.isPlaying = false;
            try { audio.currentTime = 0; } catch (_) { }
            updateToggleUi();
            updateTimeUi();
        };

        const onToggleClick = () => {
            if (audio.paused) {
                const p = audio.play();
                if (p && typeof p.catch === 'function') {
                    p.catch(() => {
                        state.isPlaying = false;
                        updateToggleUi();
                    });
                }
            } else {
                audio.pause();
            }
        };

        const onSliderInput = () => {
            const pct = Number.parseFloat(slider.value);
            if (!Number.isFinite(pct) || state.duration <= 0) return;
            const targetTime = (pct / 100) * state.duration;
            try { audio.currentTime = targetTime; } catch (_) { }
            updateTimeUi();
        };

        const resetForSrcChange = () => {
            const nextSrc = audio.getAttribute('src') || '';
            if (nextSrc === state.lastSrc) return;
            state.lastSrc = nextSrc;
            finishDurationProbe();
            try { audio.pause(); } catch (_) { }
            state.isPlaying = false;
            state.duration = 0;
            updateToggleUi();
            updateTimeUi();
        };

        const dispose = () => {
            finishDurationProbe();
            try { audio.pause(); } catch (_) { }
            toggleBtn.removeEventListener('click', onToggleClick);
            slider.removeEventListener('input', onSliderInput);
            audio.removeEventListener('loadedmetadata', onLoadedMetadata);
            audio.removeEventListener('durationchange', onDurationChange);
            audio.removeEventListener('timeupdate', onTimeUpdate);
            audio.removeEventListener('play', onPlay);
            audio.removeEventListener('pause', onPause);
            audio.removeEventListener('ended', onEnded);
            _activeAudioPlayers.delete(state);
            _audioPlayerControllers.delete(root);
        };

        toggleBtn.addEventListener('click', onToggleClick);
        slider.addEventListener('input', onSliderInput);
        audio.addEventListener('loadedmetadata', onLoadedMetadata);
        audio.addEventListener('durationchange', onDurationChange);
        audio.addEventListener('timeupdate', onTimeUpdate);
        audio.addEventListener('play', onPlay);
        audio.addEventListener('pause', onPause);
        audio.addEventListener('ended', onEnded);

        state.resetForSrcChange = resetForSrcChange;
        state.dispose = dispose;
        _audioPlayerControllers.set(root, state);
        _activeAudioPlayers.add(state);

        updateToggleUi();
        if (audio.readyState >= 1) {
            probeDurationIfNeeded();
        } else {
            updateTimeUi();
        }
    }

    function bindAllAudioPlayers(container) {
        if (typeof document === 'undefined') return;
        const scope = container || document;
        if (scope.nodeType === 1 && scope.hasAttribute && scope.hasAttribute('data-spokes-audio-player')) {
            attachAudioPlayer(scope);
        }
        if (typeof scope.querySelectorAll === 'function') {
            const list = scope.querySelectorAll('[data-spokes-audio-player]');
            for (let i = 0; i < list.length; i++) {
                attachAudioPlayer(list[i]);
            }
        }
    }

    function pruneDetachedAudioPlayers() {
        for (const state of Array.from(_activeAudioPlayers)) {
            if (!state.root.isConnected) {
                state.dispose();
            }
        }
    }

    function ensureAudioPlayerObserver() {
        if (_audioPlayerObserver || typeof MutationObserver === 'undefined' || typeof document === 'undefined' || !document.body) {
            return;
        }
        _audioPlayerObserver = new MutationObserver((mutations) => {
            let needsPrune = false;
            for (let i = 0; i < mutations.length; i++) {
                const m = mutations[i];
                if (m.type === 'attributes' && m.target && m.target.tagName === 'AUDIO') {
                    const root = m.target.closest && m.target.closest('[data-spokes-audio-player]');
                    const ctrl = root && _audioPlayerControllers.get(root);
                    if (ctrl) ctrl.resetForSrcChange();
                    continue;
                }
                if (m.addedNodes && m.addedNodes.length > 0) {
                    for (let j = 0; j < m.addedNodes.length; j++) {
                        const node = m.addedNodes[j];
                        if (node.nodeType === 1) {
                            bindAllAudioPlayers(node);
                        }
                    }
                }
                if (m.removedNodes && m.removedNodes.length > 0 && _activeAudioPlayers.size > 0) {
                    needsPrune = true;
                }
            }
            if (needsPrune) {
                pruneDetachedAudioPlayers();
            }
        });
        _audioPlayerObserver.observe(document.body, {
            childList: true,
            subtree: true,
            attributes: true,
            attributeFilter: ['src']
        });
    }

    window.spokesAudioPlayer = {
        attach: attachAudioPlayer,
        bindAll: () => bindAllAudioPlayers(document),
        pauseInContainer: (rootEl) => {
            if (!rootEl) return;
            const audio = rootEl.querySelector ? rootEl.querySelector('audio') : null;
            if (audio) {
                try { audio.pause(); } catch (_) { }
            }
        }
    };

    if (typeof document !== 'undefined') {
        if (document.readyState === 'loading') {
            document.addEventListener('DOMContentLoaded', () => {
                bindAllAudioPlayers(document);
                ensureAudioPlayerObserver();
            }, { once: true });
        } else {
            bindAllAudioPlayers(document);
            ensureAudioPlayerObserver();
        }
    }

    window.spokesMicDiagnostic = {
        mediaRecorder: null,
        audioChunks: [],
        stream: null,
        audioCtx: null,
        analyser: null,
        source: null,
        animationId: null,
        lastBlobUrl: null,

        start: async function (canvasId, deviceId = null) {
            this.stop();

            if (!navigator.mediaDevices || !navigator.mediaDevices.getUserMedia) {
                console.error('[spokesMicDiagnostic] getUserMedia not supported.');
                return false;
            }

            try {
                const constraints = {
                    audio: deviceId ? { deviceId: { exact: deviceId } } : true
                };
                this.stream = await navigator.mediaDevices.getUserMedia(constraints);
                this.audioChunks = [];

                this.mediaRecorder = new MediaRecorder(this.stream, getSupportedAudioRecorderOptions());
                this.mediaRecorder.ondataavailable = (e) => {
                    if (e.data && e.data.size > 0) {
                        this.audioChunks.push(e.data);
                    }
                };
                this.mediaRecorder.start(100);
                startWaveformVisualizer(this, canvasId, 'rgba(89, 74, 226, 0.85)', 2);
                return true;
            } catch (err) {
                console.error('[spokesMicDiagnostic] Error starting test:', err);
                return false;
            }
        },

        stop: function () {
            cleanupRecorderResources(this);

            return new Promise((resolve) => {
                if (this.mediaRecorder && this.mediaRecorder.state !== 'inactive') {
                    this.mediaRecorder.onstop = () => {
                        if (this.lastBlobUrl) {
                            try { URL.revokeObjectURL(this.lastBlobUrl); } catch (e) {}
                        }
                        const mimeType = this.mediaRecorder?.mimeType || 'audio/webm';
                        const audioBlob = new Blob(this.audioChunks, { type: mimeType });
                        this.lastBlobUrl = URL.createObjectURL(audioBlob);
                        this.mediaRecorder = null;
                        resolve(this.lastBlobUrl);
                    };
                    this.mediaRecorder.stop();
                } else {
                    this.mediaRecorder = null;
                    resolve(null);
                }
            });
        },

        cleanup: function () {
            this.stop();
            if (this.lastBlobUrl) {
                try { URL.revokeObjectURL(this.lastBlobUrl); } catch (e) {}
                this.lastBlobUrl = null;
            }
        }
    };
})();

