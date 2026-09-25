// --- FAST-BOOT STATUS BAR POLLING ---
// The status bar must be styled instantly to avoid a visual flash. We don't wait for DOMContentLoaded.
(function fastBootStatusBar() {
    if (window.Capacitor && window.Capacitor.isNativePlatform && window.Capacitor.isNativePlatform()) {
        let attempts = 0;
        const applyStatusBar = async () => {
            if (window.Capacitor.Plugins && window.Capacitor.Plugins.StatusBar) {
                let isDarkMode = true;
                if (window.Capacitor.Plugins.Preferences) {
                    try {
                        const res = await window.Capacitor.Plugins.Preferences.get({ key: 'spokes_is_dark_mode' });
                        if (res && res.value !== null && res.value !== undefined) {
                            isDarkMode = res.value !== 'false';
                        }
                    } catch (e) {}
                }
                window.Capacitor.Plugins.StatusBar.setOverlaysWebView({ overlay: true }).catch(console.error);
                window.Capacitor.Plugins.StatusBar.setStyle({ style: isDarkMode ? 'DARK' : 'LIGHT' }).catch(console.error);
            } else if (attempts < 50) {
                attempts++;
                setTimeout(applyStatusBar, 10);
            }
        };
        applyStatusBar();
    }
})();

// Unified platform check
window.isCapacitorNative = function () {
    return navigator.userAgent.includes("Capacitor") || Boolean(window.Capacitor?.isNativePlatform?.());
};

// Support dynamic theme toggling from Blazor
window.setCapacitorStatusBarStyle = async function (isDarkMode) {
    if (window.isCapacitorNative()) {
        try {
            if (window.Capacitor?.Plugins?.StatusBar) {
                // Style.DARK means light text for dark backgrounds. Style.LIGHT means dark text for light backgrounds.
                const style = isDarkMode ? 'DARK' : 'LIGHT';
                await window.Capacitor.Plugins.StatusBar.setOverlaysWebView({ overlay: true });
                await window.Capacitor.Plugins.StatusBar.setStyle({ style: style });
            }
            if (window.Capacitor?.Plugins?.Preferences) {
                await window.Capacitor.Plugins.Preferences.set({
                    key: 'spokes_is_dark_mode',
                    value: isDarkMode ? 'true' : 'false'
                });
            }
        } catch (err) {
            console.error('[Capacitor-Init] Error applying StatusBar style/color:', err);
        }
    }
};

window.reapplyCapacitorStatusBarStyle = async function () {
    if (window.isCapacitorNative()) {
        try {
            let isDarkMode = true;
            if (window.Capacitor?.Plugins?.Preferences) {
                const res = await window.Capacitor.Plugins.Preferences.get({ key: 'spokes_is_dark_mode' });
                if (res && res.value !== null && res.value !== undefined) {
                    isDarkMode = res.value !== 'false';
                }
            }
            if (window.Capacitor?.Plugins?.StatusBar) {
                await window.Capacitor.Plugins.StatusBar.setOverlaysWebView({ overlay: true });
                const style = isDarkMode ? 'DARK' : 'LIGHT';
                await window.Capacitor.Plugins.StatusBar.setStyle({ style: style });
            }
        } catch (err) {
            console.error('[Capacitor-Init] Error reapplying StatusBar style:', err);
        }
    }
};

window.triggerHaptic = async function(style = 'LIGHT') {
    try {
        if (window.Capacitor && window.Capacitor.Plugins && window.Capacitor.Plugins.Haptics) {
            await window.Capacitor.Plugins.Haptics.impact({ style: style });
        } else {
            // Fallback for web
            navigator.vibrate?.(style === 'LIGHT' ? 15 : 30);
        }
    } catch (err) {
        console.warn('Haptics failed', err);
    }
};

window.initCapacitorEnv = async function () {
    if (window._capacitorInitCompleted) {
        console.error('[DOUBLE-LOAD-DEBUG] initCapacitorEnv called AGAIN (already completed at:',
            window._capacitorInitCompletedAt, '). This indicates a page reload or duplicate script execution.',
            '| URL:', window.location.href,
            '| cookies: Session=' + document.cookie.includes('Spokes_Session_v3') + ', Refresh=' + document.cookie.includes('Spokes_Refresh'));
        console.trace('[DOUBLE-LOAD-DEBUG] initCapacitorEnv duplicate call stack');
    }
    window._capacitorInitStartedAt = Date.now();
    if (window.isCapacitorNative()) {
        try {
            // Check if we restarted the server specifically to open a notification
            // Skip if runStartup() in App.razor already handled it (avoids redundant bridge call)
            if (!window._pendingNotificationUrlChecked && Capacitor.Plugins.Preferences) {
                try {
                    const pending = await Capacitor.Plugins.Preferences.get({ key: 'pendingNotificationUrl' });
                    if (pending && pending.value) {
                        await Capacitor.Plugins.Preferences.remove({ key: 'pendingNotificationUrl' });
                        // Navigate to the deep link and let Blazor handle it
                        let urlObj = new URL(pending.value, window.location.origin);
                        urlObj.searchParams.set('client', 'mobile');
                        window.location.href = urlObj.toString();
                        return;
                    }
                } catch(e) {}
            }
            
            // StatusBar initialization has been moved to the fastBootStatusBar poller at the top of the file for instant execution.


            // Intercept logout forms to append client=mobile so the backend knows to only clear local cookies
            document.addEventListener('submit', function(e) {
                if (e.target && e.target.action && e.target.action.endsWith('logout')) {
                    var input = document.createElement('input');
                    input.type = 'hidden';
                    input.name = 'client';
                    input.value = 'mobile';
                    e.target.appendChild(input);
                }
            });

            // Intercept clicks on external links and open them in the system browser
            // instead of navigating the WebView away from the app.
            // Same-origin links are NOT intercepted — they navigate inside the WebView as normal.
            // This does NOT affect the Casdoor SSO flow because openCapacitorBrowser() calls
            // Browser.open() directly and never triggers an anchor click event.
            document.addEventListener('click', async function(e) {
                var anchor = e.target.closest('a[href]');
                if (!anchor) return;

                var href = anchor.getAttribute('href');
                if (!href) return;

                // Only intercept external HTTP(S) links
                if (!href.startsWith('http://') && !href.startsWith('https://')) return;

                // Skip same-origin links (internal app navigation)
                try {
                    var linkUrl = new URL(href);
                    if (linkUrl.origin === window.location.origin) return;
                } catch (err) { return; }

                // Prevent the WebView from navigating away
                e.preventDefault();
                e.stopPropagation();

                // Open in system browser (Chrome Custom Tab / SFSafariViewController)
                console.log('[Capacitor-Init] Intercepted external link click:', href);
                if (window.Capacitor.Plugins && window.Capacitor.Plugins.Browser) {
                    await window.Capacitor.Plugins.Browser.open({ url: href });
                }
            }, true); // Capture phase to run before Blazor's event system

            // Register deep link and back button listeners
            if (Capacitor.Plugins.App) {
                // Clear any stale listeners from previous page loads BEFORE registering new ones
                try {
                    await Capacitor.Plugins.App.removeAllListeners();
                } catch (e) {
                    console.warn('[Capacitor-Init] Could not clear previous listeners.', e);
                }

                // The auth-callback deep link is now handled entirely natively via AuthTabIntent.
                // We no longer manually intercept appUrlOpen for auth-callbacks.

                // Register hardware back button listener for Android
                Capacitor.Plugins.App.addListener('backButton', () => {
                    const pathSegments = window.location.pathname.split('/').filter(Boolean);
                    
                    // Level 1 pages have 0 or 1 path segment (e.g., /, /chat, /projects, /admin)
                    if (pathSegments.length <= 1) {
                        Capacitor.Plugins.App.exitApp();
                    } else {
                        window.history.back();
                    }
                });
                console.log('[Capacitor-Init] Registered native backButton listener successfully.');

                // Push notification tap listener (early intercept)
                if (window.Capacitor.Plugins.FirebaseMessaging) {
                    window.Capacitor.Plugins.FirebaseMessaging.addListener('notificationActionPerformed', async (action) => {
                         console.log('[Capacitor-Init] Push notification tapped', action);
                         
                         if (action && (action.actionId === 'decline' || action.actionId === 'decline_call')) {
                             return;
                         }
                         
                          // Deduplicate stuck intents that Android replays in rapid succession (< 5000ms)
                          const data = action.notification ? action.notification.data : null;
                          const uniqueKey = (data && (data.google_message_id || data.messageId || data.sentAt)) 
                              ? (data.google_message_id || data.messageId || data.sentAt)
                              : ((action.notification && action.notification.id) ? (action.notification.id + '') : null);
                          
                          if (uniqueKey) {
                              try {
                                  const now = Date.now();
                                  let recentRecords = JSON.parse(localStorage.getItem('spokes_recent_notif_taps') || '[]');
                                  // Keep only records within the last 5 seconds
                                  recentRecords = recentRecords.filter(r => (now - r.time) < 5000);
                                  if (recentRecords.some(r => r.key === uniqueKey)) {
                                      console.log('[Capacitor-Init] Ignoring duplicate notification tap event within 5s window:', uniqueKey);
                                      if (window.Capacitor.Plugins.FirebaseMessaging && window.Capacitor.Plugins.FirebaseMessaging.removeAllDeliveredNotifications) {
                                          window.Capacitor.Plugins.FirebaseMessaging.removeAllDeliveredNotifications().catch(() => {});
                                      }
                                      return;
                                  }
                                  recentRecords.push({ key: uniqueKey, time: now });
                                  localStorage.setItem('spokes_recent_notif_taps', JSON.stringify(recentRecords));
                              } catch(e) {
                                  console.error('[Capacitor-Init] Error in notification deduplication', e);
                              }
                          }

                         let targetUrl = (data && data.url) ? data.url : ((data && data.Url) ? data.Url : null);
                         const originUrl = (data && data.originUrl) ? data.originUrl : ((data && data.OriginUrl) ? data.OriginUrl : null);

                         if (targetUrl) {
                             if (!targetUrl.includes('client=mobile')) {
                                 targetUrl += (targetUrl.includes('?') ? '&' : '?') + 'client=mobile';
                             }
                             const ensureCookies = async () => {
                                 if (window.Capacitor.Plugins.CapacitorCookies) {
                                     try { await window.Capacitor.Plugins.CapacitorCookies.getCookies(); } catch(e) {}
                                 }
                                 for (let i = 0; i < 20; i++) {
                                     try {
                                         let r = await window.fetch('/spokesapi/auth/current-session', { cache: 'no-store', redirect: 'manual' });
                                         if (r.ok) {
                                             let text = await r.text();
                                             if (text.includes('sessionId')) return;
                                         }
                                     } catch(e) {}
                                     await new Promise(res => setTimeout(res, 500));
                                 }
                             };

                             if (originUrl) {
                                 let targetOrigin = originUrl.replace(/\/$/, '');
                                 if (!targetOrigin.startsWith('http')) targetOrigin = 'https://' + targetOrigin;
                                 const currentOrigin = window.location.origin.replace(/\/$/, '');
                                 
                                 if (targetOrigin !== currentOrigin && targetOrigin !== 'capacitor://localhost' && targetOrigin !== 'http://localhost') {
                                     if (window.Capacitor.Plugins.Preferences) {
                                         try {
                                             await window.Capacitor.Plugins.Preferences.set({ key: 'serverUrl', value: targetOrigin });
                                             await window.Capacitor.Plugins.Preferences.set({ key: 'pendingNotificationUrl', value: targetUrl });
                                         } catch(e) {}
                                     }

                                     if (window.Capacitor.Plugins.BridgeManager) {
                                         try {
                                             await window.Capacitor.Plugins.BridgeManager.openServer({ url: targetOrigin });
                                             return;
                                         } catch(e) { }
                                     }
                                     
                                     await ensureCookies();
                                     window.location.href = targetOrigin + targetUrl;
                                     return;
                                 }
                             }
                             
                              // Same origin
                              if (window.spokesDotNetRef) {
                                  window.spokesPendingNav = targetUrl;
                                  try {
                                      await window.spokesDotNetRef.invokeMethodAsync('NavigateTo', targetUrl);
                                      window.spokesPendingNav = null;
                                  } catch (e) {
                                      console.warn('[Capacitor-Init] Blazor NavigateTo invocation failed:', e);
                                      // If the circuit was truly broken or Blazor disconnected, fallback to hard reload
                                      if (window._blazorDisconnected || !window.spokesDotNetRef) {
                                          await ensureCookies();
                                          window.location.href = targetUrl;
                                      }
                                  }
                              } else {
                                  console.log('[Capacitor-Init] Blazor not fully ready, storing pendingNotificationRoute');
                                  window.pendingNotificationRoute = targetUrl;
                              }
                          }
                    });
                    console.log('[Capacitor-Init] Registered native notificationActionPerformed listener successfully.');
                }
                
                Capacitor.Plugins.App.addListener('appStateChange', (state) => {
                    console.log('[Capacitor-Init] App state changed. isActive:', state.isActive);
                    if (!state.isActive) {
                        if (window.__spokesPushSubscriptionId) {
                            let url = `/spokesapi/push/unfocus?subscriptionId=${window.__spokesPushSubscriptionId}`;
                            fetch(url, { method: 'POST', cache: 'no-store' }).catch(console.error);
                        }
                    } else {
                        window.reapplyCapacitorStatusBarStyle();
                    }
                });

                document.addEventListener('visibilitychange', () => {
                    if (document.visibilityState === 'visible') {
                        window.reapplyCapacitorStatusBarStyle();
                    }
                });

                if (Capacitor.Plugins.Browser) {
                    Capacitor.Plugins.Browser.addListener('browserFinished', () => {
                        console.log('[Capacitor-Init] browserFinished event received. User manually closed the system browser.');
                        // If the WebView was used as a logout proxy (which opens the browser), reset it to the login screen.
                        if (window.location.pathname.includes('/logout') || 
                            document.body.innerHTML.includes('Signing out') ||
                            document.body.innerHTML.includes('Logging out')) {
                            console.log('[Capacitor-Init] Resetting WebView to login screen.');
                            window.location.href = window.location.origin + '/sso/login/auto?client=mobile';
                        }
                    });
                }

            } else {
                console.error('[Capacitor-Init] App plugin is MISSING from Capacitor.Plugins!');
            }
        } catch (err) {
            console.error('[Capacitor-Init] Error applying Capacitor configuration:', err);
        }
    } else {
        console.log('[Capacitor-Init] Not running in a native wrapper. Running as PWA or Browser.');
    }
    window._capacitorInitCompleted = true;
    window._capacitorInitCompletedAt = Date.now();
};

// NOTE: initCapacitorEnv() is NO LONGER auto-triggered here.
// It is now called by runStartup() in App.razor AFTER Blazor starts,
// to avoid bridge contention with initDeviceId() on Android.
// See: App.razor runStartup() for the orchestrated boot sequence.

window.openCapacitorBrowser = async function (url) {
    console.log('[Capacitor-Init] openCapacitorBrowser called with URL:', url);
    
    const isProbablyNative = window.isCapacitorNative();
    
    if (isProbablyNative) {
        try {
            console.log('[Capacitor-Init] Waiting for Capacitor and Browser plugin to populate...');
            let attempts = 0;
            while ((!window.Capacitor || !window.Capacitor.Plugins?.Browser) && attempts < 20) {
                await new Promise(r => setTimeout(r, 100));
                attempts++;
            }

            if (window.Capacitor.Plugins && window.Capacitor.Plugins.Browser) {
                console.log('[Capacitor-Init] Browser plugin found after', attempts, 'attempts. Absolute URL construction...');
                const absoluteUrl = new URL(url, window.location.origin).href;
                console.log('[Capacitor-Init] Calling Browser.open with:', absoluteUrl);
                await window.Capacitor.Plugins.Browser.open({ url: absoluteUrl });
                console.log('[Capacitor-Init] Browser.open call completed successfully.');
            } else {
                console.error('[Capacitor-Init] Browser plugin was not found after waiting!');
                alert("Failed to load native browser plugin. Please restart the app.");
            }
        } catch (e) {
            console.error('[Capacitor-Init] Error opening Capacitor Browser:', e);
            alert("Failed to open the system browser: " + e.message);
        }
    } else {
        console.log('[Capacitor-Init] Not a native platform, falling back to window.location.href');
        window.location.href = url;
    }
};

// SSO-specific browser function that uses ASWebAuthenticationSession (iOS) / AuthTabIntent (Android)
// via the native SsoAuthPlugin. 
window.openSsoAuth = async function (url) {
    console.log('[Capacitor-Init] openSsoAuth PKCE flow starting...');
    const isProbablyNative = window.isCapacitorNative();

    if (!isProbablyNative) {
        console.log('[Capacitor-Init] Not a native platform, falling back to window.location.href');
        window.location.href = url || '/';
        return;
    }

    try {
        // Resolve SsoAuth plugin: register if needed
        let ssoPlugin = window.Capacitor?.Plugins?.SsoAuth;
        if (!ssoPlugin && window.Capacitor?.registerPlugin) {
            try {
                ssoPlugin = window.Capacitor.registerPlugin('SsoAuth');
            } catch (e) {
                console.warn('[Capacitor-Init] registerPlugin(SsoAuth) threw:', e);
            }
        }

        let attempts = 0;
        while ((!ssoPlugin || !window.spokesAuth?.generateCodeVerifier) && attempts < 20) {
            await new Promise(r => setTimeout(r, 100));
            attempts++;
            ssoPlugin = window.Capacitor?.Plugins?.SsoAuth || ssoPlugin;
            if (!ssoPlugin && window.Capacitor?.registerPlugin) {
                try {
                    ssoPlugin = window.Capacitor.registerPlugin('SsoAuth');
                } catch (e) {}
            }
        }

        if (!ssoPlugin || !window.spokesAuth?.generateCodeVerifier) {
            console.warn('[Capacitor-Init] SsoAuth or spokesAuth missing. Opening external browser tab.');
            if (window.openCapacitorBrowser) {
                await window.openCapacitorBrowser(url);
            } else {
                console.error('[Capacitor-Init] Neither SsoAuth nor Browser plugin is available.');
            }
            return;
        }

        // 1. Fetch Config
        const configRes = await fetch('/spokesapi/auth/mobile/config');
        if (!configRes.ok) throw new Error('Failed to fetch mobile auth config');
        const config = await configRes.json();
        
        // 2. Generate PKCE Challenge
        const verifier = window.spokesAuth.generateCodeVerifier();
        const challenge = await window.spokesAuth.generateCodeChallenge(verifier);

        // Store the verifier so it can be used for the PKCE exchange after login
        sessionStorage.setItem('spokes_pkce_verifier', verifier);
        
        // 3. Build Auth URL using the discovered authorization endpoint
        const redirectUri = 'spokes://auth-callback';
        const authUrl = `${config.authorizationEndpoint}?client_id=${config.clientId}&redirect_uri=${encodeURIComponent(redirectUri)}&response_type=code&scope=${encodeURIComponent(config.scope)}&code_challenge=${challenge}&code_challenge_method=S256`;
        
        console.log('[Capacitor-Init] Calling SsoAuth.login via PKCE...');
        const result = await ssoPlugin.login({
            url: authUrl,
            callbackScheme: 'spokes'
        });

        if (result && result.url) {
            console.log('[Capacitor-Init] SsoAuth.login resolved:', result.url);
            const urlObj = new URL(result.url);
            const code = urlObj.searchParams.get('code');
            
            if (code) {
                // 4. Exchange Code
                console.log('[Capacitor-Init] Exchanging code...');
                
                // Get stable device ID (uses ANDROID_ID / iOS Keychain, survives reinstalls)
                let deviceId = '';
                try {
                    if (window.pushNotifications?.getDeviceId) {
                        deviceId = await window.pushNotifications.getDeviceId();
                    }
                } catch(e) {}

                const exchangeRes = await fetch('/spokesapi/auth/mobile/exchange', {
                    method: 'POST',
                    headers: { 'Content-Type': 'application/json' },
                    body: JSON.stringify({
                        code: code,
                        codeVerifier: verifier,
                        redirectUri: redirectUri,
                        deviceId: deviceId
                    })
                });
                
                if (!exchangeRes.ok) {
                    const errText = await exchangeRes.text();
                    throw new Error('Exchange failed: ' + errText);
                }
                
                const exchangeData = await exchangeRes.json();
                
                // 5. Save Token & Refresh Session
                if (exchangeData.refreshToken) {
                    const refreshKey = 'spokes_refresh_' + window.location.hostname;
                    await window.Capacitor.Plugins.Preferences.set({key: refreshKey, value: exchangeData.refreshToken});
                    console.log('[Capacitor-Init] Token saved, refreshing session...');
                    if (window.spokesAuth.refreshSessionAsync) {
                        await window.spokesAuth.refreshSessionAsync();
                    }
                    window.location.replace('/chat?client=mobile');
                } else {
                    throw new Error('No refresh token in response');
                }
            } else {
                throw new Error('No code in callback URL');
            }
        }
    } catch (e) {
        console.error('[Capacitor-Init] openSsoAuth failed:', e);
        if (e.message && (e.message.includes('cancelled') || e.message.includes('Canceled'))) {
            console.log('[Capacitor-Init] User cancelled auth session.');
            return;
        }
        // Fall back to external browser tab rather than navigating the app's internal WebView to IdP
        if (window.openCapacitorBrowser) {
            console.log('[Capacitor-Init] Falling back to openCapacitorBrowser');
            await window.openCapacitorBrowser(url);
        }
    }
};

window.openSsoLogout = async function (url) {
    console.log('[Capacitor-Init] openSsoLogout called with URL:', url);
    const isProbablyNative = window.isCapacitorNative();
    
    if (!isProbablyNative) {
        window.location.href = url;
        return;
    }

    try {
        let plugin = window.Capacitor?.Plugins?.SsoAuth;
        if (!plugin && window.Capacitor?.registerPlugin) {
            try {
                plugin = window.Capacitor.registerPlugin('SsoAuth');
            } catch (e) {}
        }

        let attempts = 0;
        while ((!plugin) && attempts < 20) {
            await new Promise(r => setTimeout(r, 100));
            attempts++;
            plugin = window.Capacitor?.Plugins?.SsoAuth || plugin;
            if (!plugin && window.Capacitor?.registerPlugin) {
                try {
                    plugin = window.Capacitor.registerPlugin('SsoAuth');
                } catch (e) {}
            }
        }
        
        if (!plugin) {
            console.warn('[Capacitor-Init] SsoAuth plugin not available for logout. Falling back to openCapacitorBrowser.');
            if (window.openCapacitorBrowser) {
                return window.openCapacitorBrowser(url);
            }
            window.location.href = url;
            return;
        }

        const absoluteUrl = new URL(url, window.location.origin).href;
        console.log('[Capacitor-Init] Calling SsoAuth.logout with:', absoluteUrl);

        // SsoAuth.logout() always resolves — either the IdP redirected to
        // spokes://logout-complete (auto-dismiss) or the user dismissed manually.
        // Both mean the IdP session was destroyed when the end-session page loaded.
        await plugin.logout({ url: absoluteUrl, callbackScheme: 'spokes' });

        console.log('[Capacitor-Init] SsoAuth.logout completed.');
    } catch (e) {
        console.warn('[Capacitor-Init] SsoAuth.logout error (non-fatal):', e);
    }
    // Caller handles navigation after this returns
};

window.spokesCopyText = async function (text) {
    if (window.isCapacitorNative()) {
        try {
            await Capacitor.Plugins.Clipboard.write({ string: text });
            return;
        } catch (e) {
            console.error('[Capacitor-Init] Native clipboard write failed:', e);
            throw e;
        }
    }
    
    // Fallback to web clipboard
    if (navigator.clipboard && window.isSecureContext) {
        await navigator.clipboard.writeText(text);
    } else {
        // Ultimate fallback for older webviews without secure context
        var textArea = document.createElement('textarea');
        textArea.value = text;
        textArea.style.position = 'fixed';
        textArea.style.left = '-999999px';
        textArea.style.top = '-999999px';
        document.body.appendChild(textArea);
        textArea.focus();
        textArea.select();
        try {
            var res = document.execCommand('copy');
            if (!res) throw new Error('execCommand copy failed');
        } finally {
            textArea.remove();
        }
    }
};
