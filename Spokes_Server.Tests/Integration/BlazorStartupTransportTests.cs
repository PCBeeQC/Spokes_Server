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

        // 2. Custom WebSocket constructor, skipNegotiation, circuitHandlers, and partitioned lazy script loaders
        Assert.Contains("window.SpokesBlazorWebSocket", html);
        Assert.Contains("const useDirectWebSocket = Boolean(window.WebSocket) && sessionStorage.getItem('spokes_force_negotiate') !== '1';", html);
        Assert.Contains("skipNegotiation: true", html);
        Assert.Contains("WebSocket: window.SpokesBlazorWebSocket", html);
        Assert.Contains("handleInitialDirectWsFailure", html);
        Assert.Contains("spokes_force_negotiate", html);
        Assert.Contains("circuitHandlers: [", html);
        Assert.Contains("onCircuitOpened()", html);
        Assert.Contains("window.ensureUploadScriptsLoaded", html);
        Assert.Contains("window.ensureVoiceScriptsLoaded", html);
        Assert.Contains("window.ensureRichTextScriptsLoaded", html);
        Assert.Contains("window.ensureCropperScriptsLoaded", html);
        Assert.Contains("window.ensureCastScriptsLoaded", html);
        Assert.Contains("window.scheduleIdleDeferredScripts", html);

        // 3. Validate syntax of all rendered inline <script> blocks when Node.js is available
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

        // 2. Static /mobile-login-success route must NOT pre-warm /_blazor even if a valid session cookie is present
        var (sessionCookie, _) = await BootstrapAuthenticatedSessionAsync();
        var mobileLoginSuccessReq = new HttpRequestMessage(HttpMethod.Get, "/mobile-login-success");
        mobileLoginSuccessReq.Headers.Add("Cookie", $"Spokes_Session_v3={sessionCookie}");
        var mobileLoginSuccessRes = await client.SendAsync(mobileLoginSuccessReq);
        Assert.Equal(HttpStatusCode.OK, mobileLoginSuccessRes.StatusCode);
        var mobileLoginSuccessHtml = await mobileLoginSuccessRes.Content.ReadAsStringAsync();
        Assert.DoesNotContain("function prewarmBlazorSocket()", mobileLoginSuccessHtml);
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

            var fingerprintedUploadMatch = Regex.Match(setupHtml, @"loadScriptOnce\('([^']*js/upload[^']*\.js)'\)");
            Assert.True(fingerprintedUploadMatch.Success, "Expected fingerprinted upload.js path in ensureUploadScriptsLoaded.");
            var fingerprintedUploadRes = await client.GetAsync("/" + fingerprintedUploadMatch.Groups[1].Value.TrimStart('/'));
            Assert.Equal(HttpStatusCode.OK, fingerprintedUploadRes.StatusCode);
            Assert.Contains("javascript", fingerprintedUploadRes.Content.Headers.ContentType?.MediaType ?? "", StringComparison.OrdinalIgnoreCase);

            var fingerprintedQuillMatch = Regex.Match(setupHtml, @"loadScriptOnce\('([^']*lib/quill/quill[^']*\.js)'\)");
            Assert.True(fingerprintedQuillMatch.Success, "Expected fingerprinted quill.js path in ensureRichTextScriptsLoaded.");
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
