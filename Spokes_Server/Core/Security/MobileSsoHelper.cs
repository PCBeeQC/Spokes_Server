using System;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Http;

namespace Spokes_Server.Core.Security;

public static class MobileSsoHelper
{
    /// <summary>
    /// Creates a minimal HTML page that performs a client-side redirect via window.location.replace().
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
                    window.location.replace('{jsEncodedTargetUrl}');
                </script>
            </head>
            <body></body>
            </html>
        ";
    }

    /// <summary>
    /// Creates a minimal HTML page that clears the mobile session cookies/preferences and redirects to the server base URL.
    /// </summary>
    public static string CreateMobileLocalLogoutPage(string serverBaseUrl)
    {
        var safeBaseUrl = JavaScriptEncoder.Default.Encode(serverBaseUrl ?? "/");
        var cleanupScript = BuildMobileStorageCleanupScript();
        return $@"
            <!DOCTYPE html>
            <html>
            <head>
                <meta charset='utf-8' />
                <script src='/js/capacitor-init.js'></script>
            </head>
            <body>
                <script>
                    window._ssoAttempts = 0;
                    document.addEventListener('DOMContentLoaded', async () => {{
                        await new Promise(r => setTimeout(r, 150));
                        {cleanupScript}
                        window.location.href = '{safeBaseUrl}?client=mobile&cb=' + Date.now();
                    }});
                </script>
            </body>
            </html>
        ";
    }

    public static string CreateMobileLogoutPage(HttpContext context, string reqId, string? configuredPublicUrl = null)
    {
        var serverBase = ServerUrlResolver.GetSafeServerBaseUrl(context, configuredPublicUrl);
        var jsServerBase = JavaScriptEncoder.Default.Encode(serverBase);
        var jsReqId = JavaScriptEncoder.Default.Encode(Uri.EscapeDataString(reqId ?? string.Empty));
        var cleanupScript = BuildMobileStorageCleanupScript();

        return $@"
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

                            {cleanupScript}

                            // Open the IdP end-session URL via the native auth browser
                            // (ASWebAuthenticationSession / AuthTabIntent) — same browser mechanism
                            // used for login, so the request carries the IdP's session cookie.
                            var targetUrl = '{jsServerBase}/sso/mobile-logout-post?reqId={jsReqId}';
                            if (window.openSsoLogout) {{
                                await window.openSsoLogout(targetUrl);
                            }} else if (window.openCapacitorBrowser) {{
                                await window.openCapacitorBrowser(targetUrl);
                            }}

                            // Navigate to login after logout completes
                            window.location.href = '{jsServerBase}/sso/login/auto?client=mobile';
                        }});
                    </script>
                </body>
            </html>
        ";
    }

    private static string BuildMobileStorageCleanupScript() => @"
                            // Clear native persistent storage for the refresh token and chat vault key
                            try {
                                if (window.Capacitor && window.Capacitor.Plugins && window.Capacitor.Plugins.Preferences) {
                                    const origin = window.location.origin.toLowerCase();
                                    const keysToClear = [
                                        'spokes_refresh::' + origin,
                                        'chat_vault_key::' + origin,
                                        'spokes_refresh_' + window.location.hostname,
                                        'chat_vault_key_' + window.location.hostname,
                                        'spokes_refresh',
                                        'chat_vault_key'
                                    ];
                                    for (const key of keysToClear) {
                                        await window.Capacitor.Plugins.Preferences.remove({ key: key });
                                    }
                                    console.log('[SSO] Cleared origin-scoped and legacy auth/vault keys from Preferences.');
                                }
                            } catch(e) { console.error('[SSO] Error clearing Preferences:', e); }

                            // Clear specific HTTP cookies from the native store for THIS origin only
                            try {
                                if (window.Capacitor && window.Capacitor.Plugins && window.Capacitor.Plugins.CapacitorCookies) {
                                    const cookiesToClear = [
                                        'Spokes_Refresh',
                                        'Spokes_Session',
                                        'Spokes_Session_v2',
                                        'Spokes_Session_v3',
                                        'Spokes_Session_Ready',
                                        'chat_vault_key'
                                    ];
                                    const targetUrl = window.location.origin;
                                    for (const key of cookiesToClear) {
                                        await window.Capacitor.Plugins.CapacitorCookies.deleteCookie({ url: targetUrl, key: key });
                                    }
                                    console.log('[SSO] Cleared native cookies for origin:', targetUrl);
                                }
                            } catch(e) { console.error('[SSO] Error clearing native cookies:', e); }

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
                            localStorage.setItem('spokes_explicit_logout', 'true');";

    /// <summary>
    /// Creates an auto-submitting HTML form page that redirects the browser to the federated IdP end-session endpoint.
    /// </summary>
    public static string CreateAutoSubmitLogoutFormPage(
        string? endSessionUrl,
        string? idTokenHint,
        string? postLogoutRedirectUri,
        string? state = null)
    {
        var stateInput = state != null
            ? $"\n                        <input type='hidden' name='state' value='{HtmlEncoder.Default.Encode(state)}' />"
            : string.Empty;

        return $@"
                <!DOCTYPE html>
                <html>
                <head>
                    <meta charset='utf-8' />
                    <meta name='viewport' content='width=device-width, initial-scale=1, maximum-scale=1, user-scalable=0'>
                    <title>Logging out...</title>
                    <style>
                        body {{ background-color: #1e1e2d; color: white; display: flex; justify-content: center; align-items: center; height: 100vh; margin: 0; font-family: 'Roboto', sans-serif; }}
                        .spinner {{ width: 40px; height: 40px; border: 4px solid rgba(255,255,255,0.3); border-top: 4px solid #594ae2; border-radius: 50%; animation: spin 1s linear infinite; margin-bottom: 20px; }}
                        @keyframes spin {{ 0% {{ transform: rotate(0deg); }} 100% {{ transform: rotate(360deg); }} }}
                    </style>
                </head>
                <body>
                    <div style='display:flex;flex-direction:column;align-items:center;text-align:center;padding:40px;'>
                        <div class='spinner'></div>
                        <p>Securely signing out...</p>
                    </div>
                    <form id='logoutForm' method='GET' action='{HtmlEncoder.Default.Encode(endSessionUrl ?? "")}'>
                        <input type='hidden' name='id_token_hint' value='{HtmlEncoder.Default.Encode(idTokenHint ?? "")}' />
                        <input type='hidden' name='post_logout_redirect_uri' value='{HtmlEncoder.Default.Encode(postLogoutRedirectUri ?? "")}' />{stateInput}
                    </form>
                    <script>document.getElementById('logoutForm').submit();</script>
                </body>
                </html>";
    }
}
