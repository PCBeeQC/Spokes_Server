window.spokesVoiceRecorder = {
    mediaRecorder: null,
    audioChunks: [],
    stream: null,
    timerInterval: null,
    dotNetRef: null,

    // Audio Context and Visualizer properties
    audioCtx: null,
    analyser: null,
    source: null,
    animationId: null,

    startRecording: async function (dotNetReference, canvasId = null) {
        this.dotNetRef = dotNetReference;
        this.audioChunks = [];
        
        if (!navigator.mediaDevices || !navigator.mediaDevices.getUserMedia) {
            console.error('[spokesVoiceRecorder] getUserMedia not supported in this browser.');
            return false;
        }

        try {
            this.stream = await navigator.mediaDevices.getUserMedia({ audio: true });
            
            // Check supported mime types
            let options = { mimeType: 'audio/webm' };
            if (!MediaRecorder.isTypeSupported('audio/webm')) {
                if (MediaRecorder.isTypeSupported('audio/mp4')) {
                    options = { mimeType: 'audio/mp4' };
                } else if (MediaRecorder.isTypeSupported('audio/ogg')) {
                    options = { mimeType: 'audio/ogg' };
                } else {
                    options = {}; // let browser decide
                }
            }

            this.mediaRecorder = new MediaRecorder(this.stream, options);

            this.mediaRecorder.ondataavailable = (e) => {
                if (e.data.size > 0) {
                    this.audioChunks.push(e.data);
                }
            };

            this.mediaRecorder.start(250); // chunk every 250ms

            // Setup Visualizer if canvasId is provided
            if (canvasId) {
                const canvas = document.getElementById(canvasId);
                if (canvas) {
                    const canvasCtx = canvas.getContext("2d");
                    const dpr = window.devicePixelRatio || 1;
                    const rect = canvas.getBoundingClientRect();
                    canvas.width = rect.width * dpr;
                    canvas.height = rect.height * dpr;
                    canvasCtx.scale(dpr, dpr);

                    const AudioContext = window.AudioContext || window.webkitAudioContext;
                    this.audioCtx = new AudioContext();
                    this.analyser = this.audioCtx.createAnalyser();
                    this.source = this.audioCtx.createMediaStreamSource(this.stream);
                    this.source.connect(this.analyser);
                    
                    this.analyser.fftSize = 64; // Small size for simple bars
                    const bufferLength = this.analyser.frequencyBinCount;
                    const dataArray = new Uint8Array(bufferLength);
                    
                    const draw = () => {
                        if (!this.stream) return; // Stop drawing when stream stops
                        this.animationId = requestAnimationFrame(draw);
                        
                        this.analyser.getByteFrequencyData(dataArray);
                        
                        canvasCtx.clearRect(0, 0, rect.width, rect.height);
                        const barWidth = (rect.width / bufferLength) * 2;
                        let barHeight;
                        let x = 0;
                        
                        for(let i = 0; i < bufferLength; i++) {
                            barHeight = (dataArray[i] / 255) * rect.height;
                            canvasCtx.fillStyle = 'rgba(244, 67, 54, 0.8)'; // MudBlazor Color.Error
                            canvasCtx.fillRect(x, rect.height / 2 - barHeight / 2, barWidth, barHeight);
                            x += barWidth + 1;
                        }
                    };
                    draw();
                }
            }
            
            return true;
        } catch (err) {
            console.error('[spokesVoiceRecorder] Error starting recording:', err);
            return false;
        }
    },

    cancelRecording: function () {
        if (this.animationId) {
            cancelAnimationFrame(this.animationId);
            this.animationId = null;
        }
        if (this.mediaRecorder && this.mediaRecorder.state !== 'inactive') {
            this.mediaRecorder.stop();
        }
        if (this.source) {
            this.source.disconnect();
            this.source = null;
        }
        if (this.audioCtx) {
            this.audioCtx.close();
            this.audioCtx = null;
        }
        if (this.stream) {
            this.stream.getTracks().forEach(track => track.stop());
            this.stream = null;
        }
        this.audioChunks = [];
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

                // Cleanup stream
                if (this.animationId) {
                    cancelAnimationFrame(this.animationId);
                    this.animationId = null;
                }
                if (this.source) {
                    this.source.disconnect();
                    this.source = null;
                }
                if (this.audioCtx) {
                    this.audioCtx.close();
                    this.audioCtx = null;
                }
                if (this.stream) {
                    this.stream.getTracks().forEach(track => track.stop());
                    this.stream = null;
                }
                
                this.audioChunks = [];

                // Push to the exact same upload pipeline as images/files
                if (window.spokesUpload && window.spokesUpload.uploadFileObject) {
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

window.spokesAudioPlayer = {
    play: (el) => el.play(),
    pause: (el) => el.pause(),
    setCurrentTime: (el, time) => { el.currentTime = time; },
    getDuration: async (el) => {
        if (el.duration === Infinity || isNaN(el.duration)) {
            return new Promise((resolve) => {
                el._isCalculatingDuration = true;
                let cleanup = null;
                
                const timeoutId = setTimeout(() => {
                    if (cleanup) cleanup();
                    resolve(0);
                }, 2000);

                const onDurationChange = () => {
                    if (el.duration !== Infinity && !isNaN(el.duration)) {
                        if (cleanup) cleanup();
                        resolve(el.duration);
                    }
                };
                
                cleanup = () => {
                    clearTimeout(timeoutId);
                    el.removeEventListener('durationchange', onDurationChange);
                    el.currentTime = 0;
                    el._isCalculatingDuration = false;
                };

                el.addEventListener('durationchange', onDurationChange);
                el.currentTime = 1e101;
            });
        }
        return el.duration || 0;
    },
    getCurrentTime: (el) => {
        if (el._isCalculatingDuration) return 0;
        return el.currentTime;
    }
};
