/**
 * Shared share routing logic — server selection UI and navigation.
 * Used by both index.html (cold start) and share-target.js (warm app).
 * Keep both copies in sync: Spokes_Mobile/www/js/ and Spokes_Server/wwwroot/js/
 */
window.spokesShareRouter = {

    /**
     * Routes a pending share. Shows server selector if multiple servers exist.
     * @param {object} options
     *   - onSameServer: callback when the selected server matches current origin
     *   - getCacheBustedUrl: optional function to cache-bust a URL before navigation
     * @returns {Promise<boolean>} true if routing was handled (caller should stop)
     */
    routeShare: async function(options = {}) {
        try {
            const savedServersRes = await Capacitor.Plugins.Preferences.get({ key: 'savedServers' });
            let savedServers = [];
            if (savedServersRes && savedServersRes.value) {
                savedServers = JSON.parse(savedServersRes.value);
            }

            console.log('[ShareRouter] routeShare: savedServers count=' + savedServers.length +
                ', urls=' + JSON.stringify(savedServers.map(s => s.url)));

            if (savedServers.length > 1) {
                console.log('[ShareRouter] Multiple servers found — showing server selector overlay');
                return await this.showServerSelector(savedServers, options);
            }

            // 0 or 1 server — proceed directly
            console.log('[ShareRouter] Single or no server — bypassing selector');
            if (options.onSameServer) {
                await options.onSameServer();
                return true;
            }
            return false;
        } catch (e) {
            console.error('[ShareRouter] Error:', e);
            return false;
        }
    },

    /**
     * Renders a full-screen server selector overlay.
     * Returns a Promise that resolves to true when a server is selected.
     */
    showServerSelector: function(savedServers, options = {}) {
        return new Promise((resolve) => {
            // Remove any existing overlay
            const existing = document.getElementById('spokes-share-overlay');
            if (existing) existing.remove();

            const overlay = document.createElement('div');
            overlay.id = 'spokes-share-overlay';
            overlay.style.cssText = 'position:fixed;top:0;left:0;width:100%;height:100%;'
                + 'background:#121212;z-index:2147483647;padding:20px;box-sizing:border-box;'
                + 'overflow-y:auto;font-family:-apple-system,BlinkMacSystemFont,"Segoe UI",'
                + 'Roboto,Helvetica,Arial,sans-serif;color:#ffffff;';
            overlay.style.paddingTop = 'calc(20px + env(safe-area-inset-top, 0px))';
            overlay.style.paddingBottom = 'calc(20px + env(safe-area-inset-bottom, 0px))';

            const h2 = document.createElement('h2');
            h2.innerText = 'Share to Server';
            h2.style.textAlign = 'center';
            h2.style.marginBottom = '20px';
            overlay.appendChild(h2);

            const listDiv = document.createElement('div');
            listDiv.style.cssText = 'display:flex;flex-direction:column;gap:10px;'
                + 'max-width:400px;margin:0 auto;';

            savedServers.forEach(server => {
                const btn = document.createElement('button');
                btn.style.cssText = 'display:flex;align-items:center;justify-content:flex-start;'
                    + 'padding:12px;background:#2d2d2d;border:1px solid #444;border-radius:6px;'
                    + 'color:white;cursor:pointer;width:100%;text-align:left;';

                const img = document.createElement('img');
                img.src = server.icon || (server.url.replace(/\/$/, '') + '/spokesapi/Media/Icon');
                img.style.cssText = 'width:36px;height:36px;border-radius:8px;'
                    + 'margin-right:15px;object-fit:cover;background-color:#2a2833;';
                img.onerror = function() {
                    this.onerror = null;
                    const fallbackSvg = 'data:image/svg+xml;utf8,<svg width="36" height="36" viewBox="0 0 24 24" fill="%2392929f" xmlns="http://www.w3.org/2000/svg"><path d="M12 2C6.48 2 2 6.48 2 12s4.48 10 10 10 10-4.48 10-10S17.52 2 12 2zm0 18c-4.41 0-8-3.59-8-8s3.59-8 8-8 8 3.59 8 8-3.59 8-8 8z"/></svg>';
                    this.src = fallbackSvg;
                    
                    if (window.Capacitor && window.Capacitor.Plugins && window.Capacitor.Plugins.Filesystem) {
                        const fileName = 'spokes_icon_' + btoa(server.url).replace(/=/g, '') + '.txt';
                        window.Capacitor.Plugins.Filesystem.readFile({
                            path: fileName,
                            directory: 'DATA',
                            encoding: 'utf8'
                        }).then(result => {
                            if (result && result.data) {
                                this.src = 'data:image/png;base64,' + result.data;
                            }
                        }).catch((err) => { 
                            // Ignored: Icon not cached yet
                        });
                    }
                };
                btn.appendChild(img);

                const span = document.createElement('span');
                span.innerText = server.name || server.url;
                span.style.cssText = 'font-size:16px;font-weight:500;';
                btn.appendChild(span);

                btn.onclick = async () => {
                    try {
                        overlay.remove();
                        console.log('[ShareRouter] User selected server:', server.url);
                        const currentUrlBase = window.location.origin;
                        let targetUrlBase;
                        try { targetUrlBase = new URL(server.url).origin; } catch { targetUrlBase = ''; }

                        if (options.onSameServer && currentUrlBase === targetUrlBase) {
                            console.log('[ShareRouter] Selected server matches current origin — delivering locally');
                            await options.onSameServer();
                            resolve(true);
                        } else {
                            // Navigate to different server
                            console.log('[ShareRouter] Selected server differs — navigating to', server.url);
                            
                            // Clear the lock so the target WebView's checkPendingShares doesn't defer
                            await Capacitor.Plugins.Preferences.remove({ key: 'shareClaimedBy' }).catch(e => console.error('[ShareRouter] remove shareClaimedBy failed:', e));
                            
                            window._spokesShareNavigating = true; // Prevent local checkPendingShares
                            
                            await Capacitor.Plugins.Preferences.set({ key: 'serverUrl', value: server.url }).catch(e => console.error('[ShareRouter] set serverUrl failed:', e));
                            await Capacitor.Plugins.Preferences.set({ key: 'shareDestinationConfirmed', value: 'true' }).catch(e => console.error('[ShareRouter] set shareDestinationConfirmed failed:', e));
                            
                            console.log('[ShareRouter] Available Capacitor Plugins:', Object.keys(window.Capacitor.Plugins).join(', '));
                            const platform = window.Capacitor.getPlatform();
                            if (platform === 'ios') {
                                window.location.href = server.url;
                            } else if (window.Capacitor.Plugins.BridgeManager) {
                                console.log('[ShareRouter] BridgeManager found, calling openServer for ' + server.url);
                                window.Capacitor.Plugins.BridgeManager.openServer({ url: server.url }).catch(e => console.error('[ShareRouter] openServer failed:', JSON.stringify(e)));
                            } else if (options.getCacheBustedUrl) {
                                window.location.href = options.getCacheBustedUrl(server.url);
                            } else {
                                window.location.href = server.url;
                            }
                            resolve(true);
                        }
                    } catch (e) {
                        console.error('[ShareRouter] Share selector click exception:', e.message, e.stack);
                    }
                };
                listDiv.appendChild(btn);
            });

            // Cancel button
            const cancelBtn = document.createElement('button');
            cancelBtn.innerText = 'Cancel';
            cancelBtn.style.cssText = 'margin-top:20px;padding:12px;background:transparent;'
                + 'border:1px solid #7e6fff;color:#7e6fff;border-radius:6px;cursor:pointer;'
                + 'width:100%;font-size:16px;font-weight:bold;';
            cancelBtn.onclick = async () => {
                console.log('[ShareRouter] User cancelled server selector');
                await Capacitor.Plugins.Preferences.remove({ key: 'pendingSharePayload' });
                overlay.remove();
                resolve(false);
            };
            listDiv.appendChild(cancelBtn);

            overlay.appendChild(listDiv);
            document.body.appendChild(overlay);
        });
    },

    /**
     * Normalizes a @capgo/capacitor-share-target payload into the flat format
     * expected by C# SharePayload { Text, Url, Title, Files }.
     *
     * The plugin sends "texts" (plural array) while C# expects "text" (singular string).
     * This function bridges the gap so both cold-start (native extraction) and
     * warm-start (plugin event) produce the same JSON shape.
     */
    normalizePayload: function(raw) {
        const result = {
            title: raw.title || null,
            text: raw.text || null,
            url: raw.url || null,
            files: raw.files || [],
            targetChannelId: raw.targetChannelId || null,
            targetServerUrl: raw.targetServerUrl || null
        };

        // The @capgo plugin uses "texts" (plural array) instead of "text" (singular)
        if (Array.isArray(raw.texts) && raw.texts.length > 0) {
            const urls = [];
            const plainTexts = [];
            for (const t of raw.texts) {
                // Some apps pack "Title\nURL" as a single string — split on newline
                const lines = t.split('\n').map(l => l.trim()).filter(l => l);
                for (const line of lines) {
                    try { new URL(line); urls.push(line); } catch { plainTexts.push(line); }
                }
            }
            if (urls.length > 0 && !result.url) result.url = urls[0];
            if (plainTexts.length > 0 && !result.text) result.text = plainTexts.join('\n');
            // If only URLs and no plain text, also populate text so the composer has something
            if (!result.text && urls.length > 0) result.text = urls.join('\n');
        }

        return result;
    },

    /**
     * Shows a non-intrusive "pill" overlay at the top of the screen.
     */
    showLoadingOverlay: function(message) {
        let overlay = document.getElementById('spokes-share-loading');
        if (!overlay) {
            overlay = document.createElement('div');
            overlay.id = 'spokes-share-loading';
            overlay.style.cssText = 'position:fixed;top:calc(10px + env(safe-area-inset-top, 0px));left:50%;transform:translateX(-50%);'
                + 'background:#2d2d2d;color:white;padding:10px 20px;border-radius:20px;box-shadow:0 4px 12px rgba(0,0,0,0.5);'
                + 'z-index:2147483647;display:flex;align-items:center;gap:10px;font-family:-apple-system,BlinkMacSystemFont,"Segoe UI",Roboto,Helvetica,Arial,sans-serif;font-size:14px;';

            // Keyframes for spinner (only append once)
            if (!document.getElementById('spokes-spin-style')) {
                const style = document.createElement('style');
                style.id = 'spokes-spin-style';
                style.innerHTML = '@keyframes spokes-spin { 0% { transform: rotate(0deg); } 100% { transform: rotate(360deg); } }';
                document.head.appendChild(style);
            }

            // Spinner
            const spinner = document.createElement('div');
            spinner.style.cssText = 'width:16px;height:16px;border:2px solid rgba(255,255,255,0.2);border-top-color:#7e6fff;border-radius:50%;animation:spokes-spin 1s linear infinite;';
            overlay.appendChild(spinner);

            // Message
            const msgEl = document.createElement('div');
            msgEl.id = 'spokes-share-loading-msg';
            msgEl.style.cssText = 'font-weight: 500; white-space: nowrap;';
            overlay.appendChild(msgEl);

            document.body.appendChild(overlay);
        }
        document.getElementById('spokes-share-loading-msg').innerText = message || "Preparing Share...";
    },

    /**
     * Removes the loading overlay.
     */
    hideLoadingOverlay: function() {
        const overlay = document.getElementById('spokes-share-loading');
        if (overlay) overlay.remove();
    }
};
