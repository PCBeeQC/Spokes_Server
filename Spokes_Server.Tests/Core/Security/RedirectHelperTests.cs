namespace Spokes_Server.Tests.Core.Security;

using Microsoft.AspNetCore.Http;
using Spokes_Server.Core.Security;

public class RedirectHelperTests
{
    private HttpContext CreateHttpContext(string host)
    {
        var context = new DefaultHttpContext();
        context.Request.Host = new HostString(host);
        context.Request.Scheme = "https";
        return context;
    }

    [Theory]
    [InlineData("/safe-path", "/safe-path")]
    [InlineData("/path/with?query=1&b=2", "/path/with?query=1&b=2")]
    [InlineData("https://spokes.app/foo", "https://spokes.app/foo")]
    [InlineData("http://spokes.app/foo", "http://spokes.app/foo")]
    public void GetSafeRedirectUrl_WithSafeUrls_ReturnsOriginal(string input, string expected)
    {
        var context = CreateHttpContext("spokes.app");
        var result = RedirectHelper.GetSafeRedirectUrl(input, context);
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("javascript://spokes.app/%0Aalert(1)")]
    [InlineData("data:text/html,<script>alert(1)</script>")]
    [InlineData("ftp://spokes.app/foo")]
    [InlineData("file:///etc/passwd")]
    [InlineData("https://evil.com/foo")]
    [InlineData("//evil.com/foo")]
    [InlineData("///evil.com/foo")]
    [InlineData("/\\evil.com")]
    [InlineData("invalid-url")]
    [InlineData("/mobile-login")]
    [InlineData("/MOBILE-LOGIN")]
    [InlineData("/mobile-login?returnUrl=/dashboard")]
    [InlineData("/sso/login")]
    [InlineData("/SSO/LOGIN")]
    [InlineData("/sso/login/auto")]
    [InlineData("/session-expired")]
    [InlineData("/SESSION-EXPIRED")]
    [InlineData("/authentication/login")]
    [InlineData("/AUTHENTICATION/LOGOUT")]
    [InlineData("https://spokes.app/mobile-login")]
    [InlineData("https://spokes.app/sso/login/auto?returnUrl=/projects")]
    [InlineData("https://spokes.app/authentication/login")]
    [InlineData("/test\0path")]
    [InlineData("/test\rpath")]
    [InlineData("/test\npath")]
    [InlineData("https://spokes.app/path\0")]
    public void GetSafeRedirectUrl_WithMaliciousUrls_ReturnsFallback(string input)
    {
        var context = CreateHttpContext("spokes.app");
        var result = RedirectHelper.GetSafeRedirectUrl(input, context);
        Assert.Equal("/", result); // Default fallback is "/"
    }

    [Theory]
    [InlineData(null, "/fallback", "/fallback")]
    [InlineData("", "/fallback", "/fallback")]
    [InlineData("   ", "/fallback", "/fallback")]
    [InlineData("https://evil.com", "/fallback", "/fallback")]
    [InlineData("javascript:alert(1)", "/fallback", "/fallback")]
    public void GetSafeRedirectUrl_WithCustomFallback_ReturnsFallback(string? input, string fallback, string expected)
    {
        var context = CreateHttpContext("spokes.app");
        var result = RedirectHelper.GetSafeRedirectUrl(input, context, fallback);
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("/safe-path", "myhost.com", "/safe-path")]
    [InlineData("/projects#tasks", "myhost.com", "/projects#tasks")]
    [InlineData("/chat?thread=1#msg-5", "myhost.com", "/chat?thread=1#msg-5")]
    [InlineData("/mobile-login#section", "myhost.com", "/")]
    [InlineData("/MOBILE-LOGIN", "myhost.com", "/")]
    [InlineData("/nav/../mobile-login", "myhost.com", "/")]
    [InlineData("/./session-expired", "myhost.com", "/")]
    [InlineData("///evil.com", "myhost.com", "/")]
    [InlineData("https://myhost.com/foo#anchor", "myhost.com", "https://myhost.com/foo#anchor")]
    [InlineData("https://myhost.com/foo", "myhost.com", "https://myhost.com/foo")]
    [InlineData("HTTPS://MYHOST.COM/foo", "myhost.com", "HTTPS://MYHOST.COM/foo")]
    [InlineData("https://evil.com/foo", "myhost.com", "/")]
    [InlineData("javascript:alert(1)", "myhost.com", "/")]
    [InlineData("/projects#\r\nX-Injected: Header", "myhost.com", "/")]
    [InlineData("/path\0nullbyte", "myhost.com", "/")]
    [InlineData("/path\ninjection", "myhost.com", "/")]
    public void GetSafeRedirectUrl_WithHostStringOverload_ValidatesCorrectly(string input, string host, string expected)
    {
        var result = RedirectHelper.GetSafeRedirectUrl(input, host);
        Assert.Equal(expected, result);
    }

    [Fact]
    public void GetSafeRedirectUrl_WithHostHavingPort_MatchesHostCorrectly()
    {
        var context = new Microsoft.AspNetCore.Http.DefaultHttpContext();
        context.Request.Host = new Microsoft.AspNetCore.Http.HostString("spokes.local", 8080);
        context.Request.Scheme = "http";

        var result = RedirectHelper.GetSafeRedirectUrl("http://spokes.local:8080/dashboard", context);
        Assert.Equal("http://spokes.local:8080/dashboard", result);
    }

    [Fact]
    public void GetSafeRedirectUrl_WithStandardHttpPort80_MatchesCorrectly()
    {
        var context = new Microsoft.AspNetCore.Http.DefaultHttpContext();
        context.Request.Host = new Microsoft.AspNetCore.Http.HostString("spokes.local");
        context.Request.Scheme = "http";

        var result = RedirectHelper.GetSafeRedirectUrl("http://spokes.local:80/dashboard", context);
        Assert.Equal("http://spokes.local:80/dashboard", result);
    }

    [Fact]
    public void GetSafeRedirectUrl_WithExplicitStandardPortInHost_MatchesCorrectly()
    {
        var context = new Microsoft.AspNetCore.Http.DefaultHttpContext();
        context.Request.Host = new Microsoft.AspNetCore.Http.HostString("spokes.app", 443);
        context.Request.Scheme = "https";

        var result = RedirectHelper.GetSafeRedirectUrl("https://spokes.app/dashboard", context);
        Assert.Equal("https://spokes.app/dashboard", result);
    }

    [Fact]
    public void GetSafeRedirectUrl_WhenPortMismatched_RejectsToFallback()
    {
        var context = new DefaultHttpContext();
        context.Request.Host = new HostString("spokes.app", 443);
        context.Request.Scheme = "https";

        var result = RedirectHelper.GetSafeRedirectUrl("https://spokes.app:9000/internal", context);
        Assert.Equal("/", result);
    }

    [Fact]
    public void GetSafeRedirectUrl_WhenDefaultHostReceivesNonStandardPort_RejectsToFallback()
    {
        var context = new DefaultHttpContext();
        context.Request.Host = new HostString("spokes.app"); // Port is null

        var result = RedirectHelper.GetSafeRedirectUrl("https://spokes.app:8443/internal", context);
        Assert.Equal("/", result);
    }
}
