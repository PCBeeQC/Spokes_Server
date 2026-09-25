namespace Spokes_Server.Tests.Core.Security;

using Microsoft.AspNetCore.Http;
using Spokes_Server.Core.Security;

public class MobileSsoHelperTests
{
    [Fact]
    public void CreateJsRedirectPage_ValidTargetUrl_ReturnsHtmlWithSessionStorageCleanupAndRedirect()
    {
        // Arrange
        var targetUrl = "/dashboard";

        // Act
        var html = MobileSsoHelper.CreateJsRedirectPage(targetUrl);

        // Assert
        Assert.NotNull(html);
        Assert.Contains("<!DOCTYPE html>", html);
        Assert.Contains("sessionStorage.removeItem('mobile_auto_login_attempts');", html);
        Assert.Contains($"window.location.replace('{targetUrl}');", html);
    }

    [Theory]
    [InlineData("https://spokes.company.com/sso/callback?code=abc&state=xyz")]
    [InlineData("/projects/123")]
    public void CreateJsRedirectPage_VariousUrls_ContainsExpectedRedirectLocation(string targetUrl)
    {
        // Act
        var html = MobileSsoHelper.CreateJsRedirectPage(targetUrl);

        // Assert
        Assert.Contains($"window.location.replace('{targetUrl}');", html);
        Assert.Contains("sessionStorage.removeItem('mobile_auto_login_attempts');", html);
    }

    [Fact]
    public void CreateMobileLogoutPageAsync_ValidContextAndReqId_UsesSchemeAndHostFromContext()
    {
        // Arrange
        var context = new DefaultHttpContext();
        context.Request.Scheme = "https";
        context.Request.Host = new HostString("spokes.company.com");
        var reqId = "req-456-test";

        // Act
        var html = MobileSsoHelper.CreateMobileLogoutPageAsync(context, reqId);

        // Assert
        Assert.NotNull(html);
        Assert.Contains("https://spokes.company.com/sso/mobile-logout-post?reqId=req-456-test", html);
        Assert.Contains("window.location.href = 'https://spokes.company.com/sso/login/auto?client=mobile';", html);
    }

    [Fact]
    public void CreateMobileLogoutPageAsync_CustomPortAndHttpScheme_ConstructsExpectedUrls()
    {
        // Arrange
        var context = new DefaultHttpContext();
        context.Request.Scheme = "http";
        context.Request.Host = new HostString("localhost", 5000);
        var reqId = "req-local-789";

        // Act
        var html = MobileSsoHelper.CreateMobileLogoutPageAsync(context, reqId);

        // Assert
        Assert.NotNull(html);
        Assert.Contains("http://localhost:5000/sso/mobile-logout-post?reqId=req-local-789", html);
        Assert.Contains("window.location.href = 'http://localhost:5000/sso/login/auto?client=mobile';", html);
    }

    [Fact]
    public void CreateMobileLogoutPageAsync_IncludesCapacitorPreferencesCleanup()
    {
        // Arrange
        var context = new DefaultHttpContext();
        context.Request.Scheme = "https";
        context.Request.Host = new HostString("spokes.company.com");
        var reqId = "req-pref-test";

        // Act
        var html = MobileSsoHelper.CreateMobileLogoutPageAsync(context, reqId);

        // Assert
        Assert.Contains("const refreshKey = 'spokes_refresh_' + window.location.hostname;", html);
        Assert.Contains("await window.Capacitor.Plugins.Preferences.remove({ key: refreshKey });", html);
        Assert.Contains("const vaultKey = 'chat_vault_key_' + window.location.hostname;", html);
        Assert.Contains("await window.Capacitor.Plugins.Preferences.remove({ key: vaultKey });", html);
        Assert.Contains("await window.Capacitor.Plugins.Preferences.remove({ key: 'spokes_refresh' });", html);
        Assert.Contains("await window.Capacitor.Plugins.Preferences.remove({ key: 'chat_vault_key' });", html);
    }

    [Fact]
    public void CreateMobileLogoutPageAsync_IncludesCapacitorCookiesCleanup()
    {
        // Arrange
        var context = new DefaultHttpContext();
        context.Request.Scheme = "https";
        context.Request.Host = new HostString("spokes.company.com");
        var reqId = "req-cookies-test";

        // Act
        var html = MobileSsoHelper.CreateMobileLogoutPageAsync(context, reqId);

        // Assert
        Assert.Contains("'Spokes_Refresh'", html);
        Assert.Contains("'Spokes_Session'", html);
        Assert.Contains("'Spokes_Session_v2'", html);
        Assert.Contains("'Spokes_Session_v3'", html);
        Assert.Contains("'Spokes_Session_Ready'", html);
        Assert.Contains("'chat_vault_key'", html);
        Assert.Contains("window.Capacitor.Plugins.CapacitorCookies.deleteCookie", html);
    }

    [Fact]
    public void CreateMobileLogoutPageAsync_SetsExplicitLogoutFlag()
    {
        // Arrange
        var context = new DefaultHttpContext();
        context.Request.Scheme = "https";
        context.Request.Host = new HostString("spokes.company.com");
        var reqId = "req-flag-test";

        // Act
        var html = MobileSsoHelper.CreateMobileLogoutPageAsync(context, reqId);

        // Assert
        Assert.Contains("localStorage.setItem('spokes_explicit_logout', 'true');", html);
    }
}
