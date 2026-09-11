using Microsoft.AspNetCore.Http;
using System;

namespace Spokes_Server.Core.Security;

public static class MobileSsoHelper
{
    /// <summary>
    /// Creates a minimal HTML page that clears the mobile auto-login loop guard
    /// and performs a client-side redirect via window.location.replace().
    /// The targetUrl must already be encoded with JavaScriptEncoder.
    /// </summary>
    public static string CreateJsRedirectPage(string jsEncodedTargetUrl)
    {
        return $@"
            <!DOCTYPE html>
            <html>
            <head>
                <meta charset='utf-8' />
                <title>Redirecting...</title>
                <script>
                    sessionStorage.removeItem('mobile_auto_login_attempts');
                    window.location.replace('{jsEncodedTargetUrl}');
                </script>
            </head>
            <body></body>
            </html>
        ";
    }

    public static string CreateMobileLogoutPageAsync(HttpContext context, string reqId)
    {
        var serverBase = $"{context.Request.Scheme}://{context.Request.Host}";

        var html = $@"
            <!DOCTYPE html>
            <html>
                <head>
                    <meta name='viewport' content='width=device-width, initial-scale=1.0, maximum-scale=1.0, user-scalable=no'>
                    <style>
                        body {{
                            margin: 0;
                            display: flex;
                            align-items: center;
                            justify-content: center;
                            min-height: 100vh;
                            background-color: #1e1e2d;
                            color: white;
                            font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, Helvetica, Arial, sans-serif;
                            text-align: center;
                            padding: 20px;
                        }}
                        .container {{
                            max-width: 400px;
                            width: 100%;
                        }}
                        h2 {{ margin-top: 0; font-size: 1.5rem; font-weight: 500; }}
                        p {{ color: rgba(255,255,255,0.7); line-height: 1.5; }}
                        .loader {{
                            border: 4px solid rgba(255,255,255,0.3);
                            border-top: 4px solid #594ae2;
                            border-radius: 50%;
                            width: 40px;
                            height: 40px;
                            animation: spin 1s linear infinite;
                            margin: 20px auto;
                        }}
                        @keyframes spin {{ 0% {{ transform: rotate(0deg); }} 100% {{ transform: rotate(360deg); }} }}
                    </style>
                    <script src='/js/capacitor-init.js'></script>
                </head>
                <body>
                    <div class='container'>
                        <h2>Signing out...</h2>
                        <div class=""loader""></div>
                    </div>
                    <script>
                        document.addEventListener('DOMContentLoaded', async () => {{
                            // Wait briefly for capacitor-init.js to register
                            await new Promise(r => setTimeout(r, 300));

                            // Clear native persistent storage for the refresh token
                            try {{
                                if (window.Capacitor && window.Capacitor.Plugins && window.Capacitor.Plugins.Preferences) {{
                                    const refreshKey = 'spokes_refresh_' + window.location.hostname;
                                    await window.Capacitor.Plugins.Preferences.remove({{ key: refreshKey }});
                                    const vaultKey = 'chat_vault_key_' + window.location.hostname;
                                    await window.Capacitor.Plugins.Preferences.remove({{ key: vaultKey }});
                                    // Also clear legacy un-namespaced keys from pre-migration installs
                                    await window.Capacitor.Plugins.Preferences.remove({{ key: 'spokes_refresh' }});
                                    await window.Capacitor.Plugins.Preferences.remove({{ key: 'chat_vault_key' }});
                                    console.log('[SSO] Cleared ' + refreshKey + ', ' + vaultKey + ', and legacy keys from Preferences.');
                                }}
                            }} catch(e) {{ console.error('[SSO] Error clearing Preferences:', e); }}

                            // Clear specific HTTP cookies from the native store for THIS origin only
                            try {{
                                if (window.Capacitor && window.Capacitor.Plugins && window.Capacitor.Plugins.CapacitorCookies) {{
                                    const cookiesToClear = [
                                        'Spokes_Refresh',
                                        'Spokes_Session',
                                        'Spokes_Session_v2',
                                        'Spokes_Session_v3',
                                        'Spokes_Session_Ready',
                                        'chat_vault_key'
                                    ];
                                    const targetUrl = window.location.origin;
                                    for (const key of cookiesToClear) {{
                                        await window.Capacitor.Plugins.CapacitorCookies.deleteCookie({{ url: targetUrl, key: key }});
                                    }}
                                    console.log('[SSO] Cleared native cookies for origin:', targetUrl);
                                }}
                            }} catch(e) {{ console.error('[SSO] Error clearing native cookies:', e); }}

                            // Fallback JS clear
                            document.cookie = 'Spokes_Refresh=; expires=Thu, 01 Jan 1970 00:00:00 UTC; path=/;';
                            document.cookie = 'Spokes_Session=; expires=Thu, 01 Jan 1970 00:00:00 UTC; path=/;';
                            document.cookie = 'Spokes_Session_v2=; expires=Thu, 01 Jan 1970 00:00:00 UTC; path=/;';
                            document.cookie = 'Spokes_Session_v2=; expires=Thu, 01 Jan 1970 00:00:00 UTC; path=/; domain=' + window.location.hostname + ';';
                            document.cookie = 'Spokes_Session_v2=; expires=Thu, 01 Jan 1970 00:00:00 UTC; path=/; domain=.' + window.location.hostname + ';';
                            document.cookie = 'Spokes_Session_v3=; expires=Thu, 01 Jan 1970 00:00:00 UTC; path=/;';
                            document.cookie = 'Spokes_Session_v3=; expires=Thu, 01 Jan 1970 00:00:00 UTC; path=/; domain=' + window.location.hostname + ';';
                            document.cookie = 'Spokes_Session_v3=; expires=Thu, 01 Jan 1970 00:00:00 UTC; path=/; domain=.' + window.location.hostname + ';';
                            document.cookie = 'Spokes_Session_Ready=; expires=Thu, 01 Jan 1970 00:00:00 UTC; path=/;';
                            document.cookie = 'chat_vault_key=; expires=Thu, 01 Jan 1970 00:00:00 UTC; path=/;';

                            // Set explicit logout flag to halt auto-login loops
                            localStorage.setItem('spokes_explicit_logout', 'true');

                            // Open the IdP end-session URL via the native auth browser
                            // (ASWebAuthenticationSession / AuthTabIntent) — same browser mechanism
                            // used for login, so the request carries the IdP's session cookie.
                            var targetUrl = '{serverBase}/sso/mobile-logout-post?reqId={reqId}';
                            if (window.openSsoLogout) {{
                                await window.openSsoLogout(targetUrl);
                            }} else if (window.openCapacitorBrowser) {{
                                await window.openCapacitorBrowser(targetUrl);
                            }}

                            // Navigate to login after logout completes
                            window.location.href = '{serverBase}/sso/login/auto?client=mobile';
                        }});
                    </script>
                </body>
            </html>
        ";
        return html;
    }
}
