using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Spokes_Server.Tests.Integration;

[Collection("IntegrationTests")]
public class BlazorStartupTransportTests : IClassFixture<SpokesTestWebApplicationFactory>
{
    private readonly SpokesTestWebApplicationFactory _factory;

    public BlazorStartupTransportTests(SpokesTestWebApplicationFactory factory)
    {
        _factory = factory;
    }

    private async Task<(string Cookie, Spokes_Server.Core.Models.Core.DeviceSession Session)> BootstrapAuthenticatedSessionAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<Spokes_Server.Aggregate.Database>();

        var employee = new Spokes_Server.Core.Models.HR.Employee
        {
            Id = "emp_ws_" + Guid.NewGuid().ToString("N"),
            FirstName = "Circuit",
            LastName = "Tester",
            IsActive = true
        };
        db.Employees.Save(employee);

        var rawToken = "tok_ws_" + Guid.NewGuid().ToString("N");
        using var sha256 = SHA256.Create();
        var hash = Convert.ToBase64String(sha256.ComputeHash(Encoding.UTF8.GetBytes(rawToken)));

        var session = new Spokes_Server.Core.Models.Core.DeviceSession
        {
            Id = "sess_ws_" + Guid.NewGuid().ToString("N"),
            EmployeeId = employee.Id,
            TokenHash = hash,
            DeviceId = "dev_ws_1",
            ExpiresAt = DateTime.UtcNow.AddDays(30)
        };
        db.DeviceSessions.Save(session);

        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var bootstrapContent = new FormUrlEncodedContent(new[]
        {
            new KeyValuePair<string, string>("refreshToken", rawToken),
            new KeyValuePair<string, string>("deviceId", "dev_ws_1"),
            new KeyValuePair<string, string>("returnUrl", "/")
        });

        var bootstrapRes = await client.PostAsync("/sso/login/mobile-bootstrap", bootstrapContent);
        Assert.Equal(HttpStatusCode.OK, bootstrapRes.StatusCode);

        var cookie = ExtractCookieValue(bootstrapRes, "Spokes_Session_v3");
        Assert.False(string.IsNullOrEmpty(cookie));
        return (cookie, session);
    }

    [Fact]
    public async Task AuthenticatedInteractiveRoute_EmitsHeadPrewarmAndTransportFallbackPrimitives()
    {
        var (sessionCookie, _) = await BootstrapAuthenticatedSessionAsync();
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var request = new HttpRequestMessage(HttpMethod.Get, "/?client=mobile");
        request.Headers.Add("Cookie", $"Spokes_Session_v3={sessionCookie}");

        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var html = await response.Content.ReadAsStringAsync();

        // 1. Pre-warmed WebSocket must be emitted in <head> BEFORE fingerprinted CSS and blazor.web.js
        int prewarmIdx = html.IndexOf("function prewarmBlazorSocket()", StringComparison.Ordinal);
        int mudCssIdx = html.IndexOf("_content/MudBlazor/MudBlazor.", StringComparison.Ordinal);
        int blazorJsIdx = html.IndexOf("_framework/blazor.web.", StringComparison.Ordinal);

        Assert.True(prewarmIdx > 0, "Expected prewarmBlazorSocket() in authenticated interactive HTML.");
        Assert.True(mudCssIdx > prewarmIdx, "Expected prewarmBlazorSocket() to appear in <head> before MudBlazor CSS.");
        Assert.True(blazorJsIdx > prewarmIdx, "Expected prewarmBlazorSocket() to appear before _framework/blazor.web.js.");

        // 2. App.razor emits compact inline __spokesBootConfig + fingerprinted external spokes-boot.js (never 120KB inline JS)
        Assert.Contains("window.__spokesBootConfig =", html);
        var bootScriptMatch = Regex.Match(html, @"<script\s+src=""([^""]*js/spokes-boot[^""]*\.js)""");
        Assert.True(bootScriptMatch.Success, "Expected fingerprinted spokes-boot.js <script> tag in App.razor HTML.");
        var bootScriptRes = await client.GetAsync("/" + bootScriptMatch.Groups[1].Value.TrimStart('/'));
        Assert.Equal(HttpStatusCode.OK, bootScriptRes.StatusCode);
        Assert.Contains("javascript", bootScriptRes.Content.Headers.ContentType?.MediaType ?? "", StringComparison.OrdinalIgnoreCase);
        var bootScript = await bootScriptRes.Content.ReadAsStringAsync();

        // 3. Custom WebSocket constructor, skipNegotiation, circuitHandlers, and partitioned lazy script loaders live in spokes-boot.js
        Assert.Contains("window.SpokesBlazorWebSocket", bootScript);
        Assert.Contains("window.__abortPendingBlazorSocket", bootScript);
        Assert.Contains("FailedToNegotiateWithServerError", bootScript);
        Assert.Contains("spokes:blazor-connected", bootScript);
        Assert.Contains("const useDirectWebSocket = Boolean(window.WebSocket) && sessionStorage.getItem('spokes_force_negotiate') !== '1';", bootScript);
        Assert.Contains("skipNegotiation: true", bootScript);
        Assert.Contains("WebSocket: window.SpokesBlazorWebSocket", bootScript);
        Assert.Contains("handleInitialDirectWsFailure", bootScript);
        Assert.Contains("spokes_force_negotiate", bootScript);
        Assert.Contains("circuitHandlers: [", bootScript);
        Assert.Contains("onCircuitOpened()", bootScript);
        Assert.Contains("window.ensureUploadScriptsLoaded", bootScript);
        Assert.Contains("window.ensureVoiceScriptsLoaded", bootScript);
        Assert.Contains("window.ensureRichTextScriptsLoaded", bootScript);
        Assert.Contains("window.ensureCropperScriptsLoaded", bootScript);
        Assert.Contains("window.ensureCastScriptsLoaded", bootScript);
        Assert.Contains("window.scheduleIdleDeferredScripts", bootScript);

        // 4. Validate syntax of all rendered inline <script> blocks when Node.js is available
        ValidateInlineScriptsSyntaxIfNodeAvailable(html);
    }

    [Fact]
    public async Task UnauthenticatedAndStaticLoginRoutes_DoNotEmitHeadBlazorSocketPrewarm()
    {
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        // 1. Unauthenticated /session-expired and /mobile-login must NOT pre-warm /_blazor
        var unauthResponse = await client.GetAsync("/session-expired");
        Assert.Equal(HttpStatusCode.OK, unauthResponse.StatusCode);
        var unauthHtml = await unauthResponse.Content.ReadAsStringAsync();
        Assert.DoesNotContain("function prewarmBlazorSocket()", unauthHtml);

        var unauthMobileLogin = await client.GetAsync("/mobile-login?no_auto=1");
        Assert.Equal(HttpStatusCode.OK, unauthMobileLogin.StatusCode);
        var unauthMobileLoginHtml = await unauthMobileLogin.Content.ReadAsStringAsync();
        Assert.DoesNotContain("function prewarmBlazorSocket()", unauthMobileLoginHtml);

        // 2. Static /mobile-login?no_auto=1 route must NOT pre-warm /_blazor even if a valid session cookie is present
        var (sessionCookie, _) = await BootstrapAuthenticatedSessionAsync();
        var authMobileLoginReq = new HttpRequestMessage(HttpMethod.Get, "/mobile-login?no_auto=1");
        authMobileLoginReq.Headers.Add("Cookie", $"Spokes_Session_v3={sessionCookie}");
        var authMobileLoginRes = await client.SendAsync(authMobileLoginReq);
        Assert.Equal(HttpStatusCode.OK, authMobileLoginRes.StatusCode);
        var authMobileLoginHtml = await authMobileLoginRes.Content.ReadAsStringAsync();
        Assert.DoesNotContain("function prewarmBlazorSocket()", authMobileLoginHtml);
    }

    [Fact]
    public async Task DirectBlazorWebSocket_WithoutNegotiate_UpgradesAndCompletesProtocolHandshake()
    {
        var (sessionCookie, _) = await BootstrapAuthenticatedSessionAsync();

        var wsClient = _factory.Server.CreateWebSocketClient();
        wsClient.ConfigureRequest = req =>
        {
            req.Headers["Cookie"] = $"Spokes_Session_v3={sessionCookie}";
        };

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var webSocket = await wsClient.ConnectAsync(new Uri("ws://localhost/_blazor"), cts.Token);
        Assert.Equal(WebSocketState.Open, webSocket.State);

        // Send SignalR blazorpack v1 handshake (terminated by ASCII Record Separator 0x1E)
        var handshakeBytes = Encoding.UTF8.GetBytes("{\"protocol\":\"blazorpack\",\"version\":1}\u001e");
        await webSocket.SendAsync(new ArraySegment<byte>(handshakeBytes), WebSocketMessageType.Text, true, cts.Token);

        var receiveBuffer = new byte[1024];
        var result = await webSocket.ReceiveAsync(new ArraySegment<byte>(receiveBuffer), cts.Token);
        var handshakeReply = Encoding.UTF8.GetString(receiveBuffer, 0, result.Count);

        // Empty JSON object + 0x1E indicates an accepted SignalR/Blazor circuit handshake without errors
        Assert.StartsWith("{}\u001e", handshakeReply);

        await webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Done", cts.Token);
    }

    [Fact]
    public async Task BlazorNegotiateFallback_AndStrictSessionProbeForWsRecovery_BehaveExpectedly()
    {
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        // 1. Standard SignalR /_blazor/negotiate fallback must remain operational for edge proxies (spokes_force_negotiate=1)
        var (sessionCookie, session) = await BootstrapAuthenticatedSessionAsync();
        var authNegotiateReq = new HttpRequestMessage(HttpMethod.Post, "/_blazor/negotiate?negotiateVersion=1");
        authNegotiateReq.Headers.Add("Cookie", $"Spokes_Session_v3={sessionCookie}");
        var authNegotiateRes = await client.SendAsync(authNegotiateReq);

        Assert.Equal(HttpStatusCode.OK, authNegotiateRes.StatusCode);
        var negotiateJson = await authNegotiateRes.Content.ReadAsStringAsync();
        Assert.Contains("connectionToken", negotiateJson);
        Assert.Contains("availableTransports", negotiateJson);

        // 2. Strict session probe used by SpokesBlazorWebSocket.handleInitialDirectWsFailure returns 200 OK while active,
        // and immediately drops to 404/401 when the session is revoked (never 302 Redirect).
        var activeProbeReq = new HttpRequestMessage(HttpMethod.Get, "/spokesapi/auth/current-session?strict=1");
        activeProbeReq.Headers.Add("Cookie", $"Spokes_Session_v3={sessionCookie}");
        var activeProbeRes = await client.SendAsync(activeProbeReq);
        Assert.Equal(HttpStatusCode.OK, activeProbeRes.StatusCode);

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<Spokes_Server.Aggregate.Database>();
            session.RevokedAt = DateTime.UtcNow.AddMinutes(-1);
            db.DeviceSessions.Save(session);
        }

        var revokedProbeReq = new HttpRequestMessage(HttpMethod.Get, "/spokesapi/auth/current-session?strict=1");
        revokedProbeReq.Headers.Add("Cookie", $"Spokes_Session_v3={sessionCookie}");
        var revokedProbeRes = await client.SendAsync(revokedProbeReq);
        Assert.True(
            revokedProbeRes.StatusCode == HttpStatusCode.Unauthorized || revokedProbeRes.StatusCode == HttpStatusCode.NotFound,
            $"Expected 401 or 404 for revoked strict session probe, got {revokedProbeRes.StatusCode}");
    }

    [Fact]
    public async Task WhenSetupIncomplete_StaticAssetsUploadsBrandingAndApiContracts_BehaveCleanly()
    {
        bool originalIsSetupComplete;
        string brandingToken;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<Spokes_Server.Aggregate.Database>();
            var cfg = db.SystemConfigs.Get();
            originalIsSetupComplete = cfg.IsSetupComplete;
            brandingToken = string.IsNullOrEmpty(cfg.PublicBrandingToken) ? "default" : cfg.PublicBrandingToken;
            cfg.IsSetupComplete = false;
            db.SystemConfigs.Save(cfg);
        }

        try
        {
            var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

            // 1. GET /setup renders cleanly with valid inline JS and nosniff header
            var setupRes = await client.GetAsync("/setup");
            Assert.Equal(HttpStatusCode.OK, setupRes.StatusCode);
            Assert.Contains("nosniff", setupRes.Headers.TryGetValues("X-Content-Type-Options", out var setupNoSniff) ? string.Join(",", setupNoSniff) : "");
            var setupHtml = await setupRes.Content.ReadAsStringAsync();
            ValidateInlineScriptsSyntaxIfNodeAvailable(setupHtml);

            // 2. Both raw and fingerprinted static assets under /lib/* and /js/* return 200 OK JS/CSS (never 302 -> /setup HTML)
            foreach (var assetPath in new[]
            {
                "/js/upload.js",
                "/lib/quill/quill.js",
                "/lib/quill/quill.snow.css",
                "/lib/cropperjs/cropper.min.js",
                "/lib/cropperjs/cropper.min.css",
                "/lib/livekit/livekit-client.umd.min.js"
            })
            {
                var assetRes = await client.GetAsync(assetPath);
                Assert.Equal(HttpStatusCode.OK, assetRes.StatusCode);
                var mediaType = assetRes.Content.Headers.ContentType?.MediaType ?? "";
                Assert.DoesNotContain("text/html", mediaType, StringComparison.OrdinalIgnoreCase);
                Assert.True(assetRes.Headers.TryGetValues("X-Content-Type-Options", out var noSniffVals) && string.Join(",", noSniffVals).Contains("nosniff"));
            }

            var fingerprintedBootMatch = Regex.Match(setupHtml, @"<script\s+src=""([^""]*js/spokes-boot[^""]*\.js)""");
            Assert.True(fingerprintedBootMatch.Success, "Expected fingerprinted spokes-boot.js script tag during setup.");
            var fingerprintedBootRes = await client.GetAsync("/" + fingerprintedBootMatch.Groups[1].Value.TrimStart('/'));
            Assert.Equal(HttpStatusCode.OK, fingerprintedBootRes.StatusCode);
            Assert.Contains("javascript", fingerprintedBootRes.Content.Headers.ContentType?.MediaType ?? "", StringComparison.OrdinalIgnoreCase);

            var fingerprintedUploadMatch = Regex.Match(setupHtml, @"""upload"":""([^""]*js/upload[^""]*\.js)""");
            Assert.True(fingerprintedUploadMatch.Success, "Expected fingerprinted upload.js path in window.__spokesBootConfig.");
            var fingerprintedUploadRes = await client.GetAsync("/" + fingerprintedUploadMatch.Groups[1].Value.TrimStart('/'));
            Assert.Equal(HttpStatusCode.OK, fingerprintedUploadRes.StatusCode);
            Assert.Contains("javascript", fingerprintedUploadRes.Content.Headers.ContentType?.MediaType ?? "", StringComparison.OrdinalIgnoreCase);

            var fingerprintedQuillMatch = Regex.Match(setupHtml, @"""quillJs"":""([^""]*lib/quill/quill[^""]*\.js)""");
            Assert.True(fingerprintedQuillMatch.Success, "Expected fingerprinted quill.js path in window.__spokesBootConfig.");
            var fingerprintedQuillRes = await client.GetAsync("/" + fingerprintedQuillMatch.Groups[1].Value.TrimStart('/'));
            Assert.Equal(HttpStatusCode.OK, fingerprintedQuillRes.StatusCode);
            Assert.Contains("javascript", fingerprintedQuillRes.Content.Headers.ContentType?.MediaType ?? "", StringComparison.OrdinalIgnoreCase);

            // 3. Blazor SignalR negotiate POST (used for IBrowserFile logo/icon/license uploads over HTTP fallback) works during setup
            var negotiateRes = await client.PostAsync("/_blazor/negotiate?negotiateVersion=1", null);
            Assert.Equal(HttpStatusCode.OK, negotiateRes.StatusCode);
            var negotiateBody = await negotiateRes.Content.ReadAsStringAsync();
            Assert.Contains("connectionToken", negotiateBody);

            // 4. Backup restore POST endpoint reaches SetupApiController without redirect or 503 block
            using var emptyMultipart = new MultipartFormDataContent();
            emptyMultipart.Add(new StringContent("1"), "probe");
            var restoreRes = await client.PostAsync("/setup/api/restore-backup", emptyMultipart);
            Assert.Equal(HttpStatusCode.BadRequest, restoreRes.StatusCode);
            var restoreJson = await restoreRes.Content.ReadAsStringAsync();
            using (var restoreDoc = System.Text.Json.JsonDocument.Parse(restoreJson))
            {
                Assert.False(restoreDoc.RootElement.GetProperty("isSuccess").GetBoolean());
                Assert.Contains("No backup file provided", restoreDoc.RootElement.GetProperty("error").GetString());
            }

            // 5. Public branding (logo/icon) and Manifest endpoints remain reachable during local pre-internet setup
            foreach (var brandingOrManifestPath in new[]
            {
                $"/branding/{brandingToken}/logo.png",
                $"/branding/{brandingToken}/icon.png",
                $"/spokesapi/branding/{brandingToken}/logo.png",
                "/spokesapi/Media/Logo",
                "/spokesapi/Media/Icon",
                "/spokesapi/Manifest"
            })
            {
                var allowedRes = await client.GetAsync(brandingOrManifestPath);
                Assert.True(
                    allowedRes.StatusCode == HttpStatusCode.OK,
                    $"Expected 200 OK for '{brandingOrManifestPath}' during setup, got {allowedRes.StatusCode}");
            }

            // 6. Non-setup API endpoints return 503 Service Unavailable JSON SpokesResult (SETUP_REQUIRED), never 302 HTML
            var gatedApiRes = await client.GetAsync("/spokesapi/push/unread-count");
            Assert.Equal(HttpStatusCode.ServiceUnavailable, gatedApiRes.StatusCode);
            Assert.Contains("application/json", gatedApiRes.Content.Headers.ContentType?.MediaType ?? "", StringComparison.OrdinalIgnoreCase);
            var gatedJson = await gatedApiRes.Content.ReadAsStringAsync();
            using (var gatedDoc = System.Text.Json.JsonDocument.Parse(gatedJson))
            {
                Assert.False(gatedDoc.RootElement.GetProperty("isSuccess").GetBoolean());
                Assert.Equal("SETUP_REQUIRED", gatedDoc.RootElement.GetProperty("code").GetString());
            }

            // 7. Browser HTML page navigations redirect relative 302 -> /setup
            var albumsRes = await client.GetAsync("/albums");
            Assert.Equal(HttpStatusCode.Redirect, albumsRes.StatusCode);
            Assert.Equal("/setup", albumsRes.Headers.Location?.OriginalString);
        }
        finally
        {
            using var scope = _factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<Spokes_Server.Aggregate.Database>();
            var cfg = db.SystemConfigs.Get();
            cfg.IsSetupComplete = originalIsSetupComplete;
            db.SystemConfigs.Save(cfg);
        }
    }

    [Fact]
    public async Task ReconnectHangGuard_AbortsOrphanedWebSocketAndNeverTriggersBlazorErrorUi()
    {
        var (sessionCookie, _) = await BootstrapAuthenticatedSessionAsync();
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var request = new HttpRequestMessage(HttpMethod.Get, "/");
        request.Headers.Add("Cookie", $"Spokes_Session_v3={sessionCookie}");
        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();

        string? inlineBootConfig = null;
        foreach (Match m in Regex.Matches(html, @"<script(?![^>]*\bsrc=)[^>]*>(.*?)</script>", RegexOptions.Singleline | RegexOptions.IgnoreCase))
        {
            if (m.Groups[1].Value.Contains("window.__spokesBootConfig =", StringComparison.Ordinal))
            {
                inlineBootConfig = m.Groups[1].Value;
                break;
            }
        }
        Assert.False(string.IsNullOrEmpty(inlineBootConfig), "Expected inline __spokesBootConfig <script> block in App.razor HTML.");

        var bootScriptMatch = Regex.Match(html, @"<script\s+src=""([^""]*js/spokes-boot[^""]*\.js)""");
        Assert.True(bootScriptMatch.Success, "Expected fingerprinted spokes-boot.js <script> tag in App.razor HTML.");
        var bootScriptRes = await client.GetAsync("/" + bootScriptMatch.Groups[1].Value.TrimStart('/'));
        Assert.Equal(HttpStatusCode.OK, bootScriptRes.StatusCode);
        var bootScriptBody = await bootScriptRes.Content.ReadAsStringAsync();
        Assert.Contains("window.spokesResume =", bootScriptBody);
        var mainAppScript = inlineBootConfig + "\n" + bootScriptBody;

        var tempDir = Path.Combine(Path.GetTempPath(), "spokes_reconnect_guard_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(tempDir, "app_inline.js"), mainAppScript!, Encoding.UTF8);

            var runnerMjs = """
                import assert from 'node:assert/strict';
                import fs from 'node:fs';
                import vm from 'node:vm';

                const appScript = fs.readFileSync('./app_inline.js', 'utf8')
                    .replace('const RECONNECT_HANG_GUARD_MS = 4000;', 'const RECONNECT_HANG_GUARD_MS = 60;')
                    .replace('const RETRY_DELAYS_MS = [1000, 2000, 4000, 8000, 15000];', 'const RETRY_DELAYS_MS = [30, 60, 100];');

                const createdSockets = [];
                class FakeWebSocket {
                    constructor(url) {
                        this.url = url;
                        this.readyState = 0; // CONNECTING
                        this.onopen = null;
                        this.onmessage = null;
                        this.onerror = null;
                        this.onclose = null;
                        this._listeners = { open: [], close: [], error: [], message: [] };
                        this.closedByCaller = false;
                        createdSockets.push(this);
                    }
                    addEventListener(type, fn) {
                        if (this._listeners[type]) this._listeners[type].push(fn);
                    }
                    removeEventListener(type, fn) {
                        if (this._listeners[type]) {
                            this._listeners[type] = this._listeners[type].filter(f => f !== fn);
                        }
                    }
                    close() {
                        this.closedByCaller = true;
                        this.readyState = 3; // CLOSED
                    }
                }
                FakeWebSocket.CONNECTING = 0;
                FakeWebSocket.OPEN = 1;
                FakeWebSocket.CLOSING = 2;
                FakeWebSocket.CLOSED = 3;

                const blazorErrorUi = { style: { display: 'none' } };
                const splashEl = { style: { display: 'none' } };
                const elements = new Map([
                    ['blazor-error-ui', blazorErrorUi],
                    ['startup-splash', splashEl]
                ]);

                let blazorStartConfig = null;
                let activeCircuitConnection = null;
                let reconnectAttemptIndex = 0;

                const windowObj = {
                    location: { protocol: 'https:', host: 'spokes.elnuage.ca', origin: 'https://spokes.elnuage.ca', href: 'https://spokes.elnuage.ca/chat', pathname: '/chat', search: '', reload() {}, replace() {} },
                    navigator: { onLine: true, userAgent: 'Mozilla/5.0 Electron' },
                    document: {
                        visibilityState: 'visible',
                        cookie: '',
                        documentElement: { appendChild() {} },
                        head: { appendChild() {} },
                        body: { children: [], appendChild() {} },
                        createElement() { return { style: {} }; },
                        addEventListener() {},
                        getElementById(id) { return elements.get(id) || null; },
                        querySelector(sel) { return sel === '#blazor-error-ui' ? blazorErrorUi : null; },
                        querySelectorAll() { return []; }
                    },
                    sessionStorage: {
                        _map: new Map(),
                        getItem(k) { return this._map.has(k) ? this._map.get(k) : null; },
                        setItem(k, v) { this._map.set(k, String(v)); },
                        removeItem(k) { this._map.delete(k); }
                    },
                    localStorage: {
                        getItem() { return null; },
                        setItem() {},
                        removeItem() {}
                    },
                    performance: { timeOrigin: Date.now(), getEntriesByType() { return []; } },
                    WebSocket: FakeWebSocket,
                    MutationObserver: class { observe() {} disconnect() {} },
                    AbortController,
                    CustomEvent: class CustomEvent { constructor(type) { this.type = type; } },
                    Event: class Event { constructor(type) { this.type = type; } },
                    Response: class Response { constructor(body, init) { this._body = body; this.status = init?.status || 200; this.ok = this.status >= 200 && this.status < 300; } },
                    Request: class Request { constructor(url) { this.url = url; } },
                    fetch: async (url) => ({
                        ok: true,
                        status: 200,
                        headers: { get(h) { return h.toLowerCase() === 'content-type' ? 'application/json' : null; } },
                        async json() { return {}; }
                    }),
                    addEventListener() {},
                    dispatchEvent() {},
                    checkIsCapacitorNative() { return false; },
                    requestAnimationFrame(cb) { setTimeout(cb, 0); },
                    setTimeout,
                    clearTimeout,
                    console,
                    Date,
                    Promise,
                    URL,
                    JSON,
                    Math,
                    ArrayBuffer,
                    String,
                    Number,
                    Boolean,
                    Error
                };
                windowObj.window = windowObj;
                windowObj.self = windowObj;

                // Emulate .NET 9/10 blazor.web.js CircuitManager.startConnection() + CircuitManager.reconnect()
                windowObj.Blazor = {
                    start(config) {
                        blazorStartConfig = config;
                        return Promise.resolve();
                    },
                    addEventListener() {},
                    async reconnect() {
                        const attemptNo = ++reconnectAttemptIndex;
                        let wsCtor = windowObj.WebSocket;
                        const builder = {
                            withUrl(url, opts) {
                                if (opts && opts.WebSocket) wsCtor = opts.WebSocket;
                                return this;
                            },
                            withServerTimeout() { return this; },
                            withKeepAliveInterval() { return this; },
                            configureLogging() { return this; },
                            build() {
                                const conn = {
                                    state: 'Disconnected',
                                    async start() {
                                        const ws = new wsCtor('wss://spokes.elnuage.ca/_blazor');
                                        await new Promise((resolve, reject) => {
                                            ws.onopen = () => {
                                                conn.state = 'Connected';
                                                resolve();
                                            };
                                            ws.onclose = (ev) => {
                                                conn.state = 'Disconnected';
                                                reject(new Error(`WebSocket closed with status code: ${ev?.code || 1006} (${ev?.reason || 'no reason given'}).`));
                                            };
                                            // Attempt #1 hangs past RECONNECT_HANG_GUARD_MS (60ms); Attempt #2 opens in 10ms
                                            if (attemptNo >= 2) {
                                                setTimeout(() => {
                                                    if (ws.onopen) ws.onopen({ type: 'open' });
                                                }, 10);
                                            }
                                        });
                                    },
                                    async stop() {
                                        if (this.state === 'Connected') {
                                            this.state = 'Disconnected';
                                            blazorStartConfig.circuit.reconnectionHandler.onConnectionDown({}, new Error('Stopped by unhandledError'));
                                        }
                                    }
                                };
                                return conn;
                            }
                        };

                        blazorStartConfig.circuit.configureSignalR(builder);
                        const n = builder.build();
                        try {
                            await n.start();
                        } catch (e) {
                            // Exact blazor.web.js CircuitManager.startConnection() catch block:
                            if (activeCircuitConnection) {
                                await activeCircuitConnection.stop();
                            }
                            if (e.errorType === 'FailedToNegotiateWithServerError') {
                                throw e;
                            }
                            // $o() in blazor.web.js:
                            blazorErrorUi.style.display = 'block';
                            return false;
                        }
                        activeCircuitConnection = n;
                        blazorStartConfig.circuit.reconnectionHandler.onConnectionUp();
                        return true;
                    }
                };

                vm.createContext(windowObj);
                vm.runInContext(appScript, windowObj);
                await new Promise(r => setTimeout(r, 20));
                assert.ok(blazorStartConfig, 'Expected Blazor.start() to be called by runStartup()');

                // Mark initial circuit opened
                blazorStartConfig.circuit.circuitHandlers[0].onCircuitOpened();
                assert.equal(windowObj._blazorCircuitEverOpened, true);

                // Trigger connection drop (like Drop #7 in the user's voice call)
                blazorStartConfig.circuit.reconnectionHandler.onConnectionDown({}, new Error('WebSocket closed with status code: 1006'));

                // Wait for Attempt #1 to time out at ~60ms, abort Socket #1, and Attempt #2 to succeed after the 30ms retry delay
                await new Promise(r => setTimeout(r, 220));

                assert.equal(reconnectAttemptIndex, 2, 'Expected Attempt #1 to time out and Attempt #2 to succeed');
                assert.equal(createdSockets.length, 2, 'Expected 2 WebSocket instances across the 2 attempts');
                const socket1 = createdSockets[0];
                assert.equal(socket1.closedByCaller, true, 'Socket #1 from timed-out Attempt #1 must be explicitly closed by __abortPendingBlazorSocket');
                assert.equal(socket1.onclose, null, 'Socket #1 onclose handler must be detached on timeout');

                // Simulate the network/proxy finally delivering a late 1006 close on Socket #1 AFTER Attempt #2 has already connected
                if (typeof socket1.onclose === 'function') {
                    socket1.onclose({ code: 1006, reason: 'late proxy close', wasClean: false });
                }
                await new Promise(r => setTimeout(r, 30));

                assert.equal(blazorErrorUi.style.display, 'none', '#blazor-error-ui must NEVER be shown when a reconnect attempt times out or fails with 1006');
                assert.equal(activeCircuitConnection.state, 'Connected', 'Attempt #2 connection must remain Connected and not be torn down by Attempt #1');
                assert.equal(windowObj._blazorDisconnected, false, 'Circuit must remain connected');

                // Verify that on Spokes Desktop (isDesktop === true), a background ServerView (_spokesHostViewActive === false)
                // reconnects its Blazor circuit immediately when a 1006 drop occurs so the circuit never expires after 3 minutes.
                windowObj.SpokesBridge = { isDesktop: true };
                windowObj._spokesHostViewActive = false;
                blazorStartConfig.circuit.reconnectionHandler.onConnectionDown({}, new Error('WebSocket closed with status code: 1006'));
                await new Promise(r => setTimeout(r, 40));
                assert.equal(reconnectAttemptIndex, 3, 'Background Desktop ServerView must reconnect immediately on 1006 drop');
                assert.equal(windowObj._blazorDisconnected, false, 'Background Desktop ServerView circuit must be restored');
                assert.equal(splashEl.style.display, 'none', 'Background Desktop ServerView must not display splash overlay');

                console.log('RECONNECT_HANG_GUARD_TEST_OK');
                """;

            await File.WriteAllTextAsync(Path.Combine(tempDir, "runner.mjs"), runnerMjs, Encoding.UTF8);

            var psi = new ProcessStartInfo
            {
                FileName = "node",
                Arguments = "runner.mjs",
                WorkingDirectory = tempDir,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var proc = Process.Start(psi)!;
            string stdout = await proc.StandardOutput.ReadToEndAsync();
            string stderr = await proc.StandardError.ReadToEndAsync();
            await proc.WaitForExitAsync();

            Assert.True(proc.ExitCode == 0, $"Node reconnect hang guard test failed (exit {proc.ExitCode}):\nSTDOUT:\n{stdout}\nSTDERR:\n{stderr}");
            Assert.Contains("RECONNECT_HANG_GUARD_TEST_OK", stdout);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }
    }

    private static void ValidateInlineScriptsSyntaxIfNodeAvailable(string html)
    {
        try
        {
            using var probe = Process.Start(new ProcessStartInfo
            {
                FileName = "node",
                Arguments = "--version",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            });
            if (probe == null || !probe.WaitForExit(3000) || probe.ExitCode != 0)
            {
                return;
            }
        }
        catch
        {
            return;
        }

        var scriptMatches = Regex.Matches(
            html,
            @"<script(?![^>]*\bsrc=)([^>]*)>(.*?)</script>",
            RegexOptions.Singleline | RegexOptions.IgnoreCase);

        Assert.NotEmpty(scriptMatches);

        int idx = 0;
        foreach (Match match in scriptMatches)
        {
            idx++;
            var attrs = match.Groups[1].Value;
            var scriptBody = match.Groups[2].Value;
            if (string.IsNullOrWhiteSpace(scriptBody))
            {
                continue;
            }

            // <ImportMap /> emits <script type="importmap">{...}</script>, which is JSON rather than executable JS
            if (Regex.IsMatch(attrs, @"\btype\s*=\s*[""'](importmap|application/json|application/ld\+json)[""']", RegexOptions.IgnoreCase))
            {
                using var _ = System.Text.Json.JsonDocument.Parse(scriptBody);
                continue;
            }

            var tempFile = Path.Combine(Path.GetTempPath(), $"app_razor_inline_{Guid.NewGuid():N}.js");
            try
            {
                File.WriteAllText(tempFile, scriptBody);
                using var checkProc = Process.Start(new ProcessStartInfo
                {
                    FileName = "node",
                    Arguments = $"--check \"{tempFile}\"",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                })!;
                string stderr = checkProc.StandardError.ReadToEnd();
                checkProc.WaitForExit(5000);
                Assert.True(checkProc.ExitCode == 0, $"Inline <script> #{idx} in App.razor failed syntax check:\n{stderr}");
            }
            finally
            {
                if (File.Exists(tempFile))
                {
                    try { File.Delete(tempFile); } catch { }
                }
            }
        }
    }

    private static string ExtractCookieValue(HttpResponseMessage response, string cookieName)
    {
        if (response.Headers.TryGetValues("Set-Cookie", out var cookies))
        {
            foreach (var c in cookies)
            {
                if (c.StartsWith(cookieName + "=", StringComparison.Ordinal))
                {
                    var val = c.Substring((cookieName + "=").Length);
                    var semi = val.IndexOf(';');
                    return semi >= 0 ? val.Substring(0, semi) : val;
                }
            }
        }
        return string.Empty;
    }
}
