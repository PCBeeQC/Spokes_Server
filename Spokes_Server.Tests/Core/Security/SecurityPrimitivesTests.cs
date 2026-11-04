using System;
using System.Net;
using Microsoft.AspNetCore.Http;
using Spokes_Server.Core.Security;
using Xunit;

namespace Spokes_Server.Tests.Core.Security;

public class SecurityPrimitivesTests
{
    [Fact]
    public void ServerUrlResolver_GetSafeServerBaseUrl_PrefersConfiguredPublicUrlOverUntrustedHostHeader()
    {
        var context = new DefaultHttpContext();
        context.Request.Scheme = "https";
        context.Request.Host = new HostString("evil-attacker.com");

        var resolved = ServerUrlResolver.GetSafeServerBaseUrl(context, "https://spokes.mycompany.com/");
        Assert.Equal("https://spokes.mycompany.com", resolved);
    }

    [Fact]
    public void ServerUrlResolver_GetSafeServerBaseUrl_RejectsInvalidHostHeaderCharacters()
    {
        var context = new DefaultHttpContext();
        context.Request.Scheme = "http";
        context.Request.Host = new HostString("evil.com@good.com");

        var resolved = ServerUrlResolver.GetSafeServerBaseUrl(context);
        Assert.Equal("http://localhost", resolved);
    }

    [Fact]
    public void ServerUrlResolver_IsSameOriginRequest_BlocksCrossSiteAndMismatchedOrigins()
    {
        var crossSiteCtx = new DefaultHttpContext();
        crossSiteCtx.Request.Host = new HostString("spokes.mycompany.com");
        crossSiteCtx.Request.Headers["Sec-Fetch-Site"] = "cross-site";
        Assert.False(ServerUrlResolver.IsSameOriginRequest(crossSiteCtx));

        var evilOriginCtx = new DefaultHttpContext();
        evilOriginCtx.Request.Host = new HostString("spokes.mycompany.com");
        evilOriginCtx.Request.Headers["Origin"] = "https://evil.com";
        Assert.False(ServerUrlResolver.IsSameOriginRequest(evilOriginCtx));

        var validOriginCtx = new DefaultHttpContext();
        validOriginCtx.Request.Host = new HostString("spokes.mycompany.com");
        validOriginCtx.Request.Headers["Origin"] = "https://spokes.mycompany.com";
        Assert.True(ServerUrlResolver.IsSameOriginRequest(validOriginCtx));
    }

    [Theory]
    [InlineData("127.0.0.1", false)]
    [InlineData("::1", false)]
    [InlineData("10.0.0.1", false)]
    [InlineData("172.16.5.4", false)]
    [InlineData("192.168.1.100", false)]
    [InlineData("169.254.169.254", false)]
    [InlineData("100.64.0.1", false)]
    [InlineData("0.0.0.0", false)]
    [InlineData("fe80::1", false)]
    [InlineData("fc00::1", false)]
    [InlineData("fd12:3456:789a::1", false)]
    [InlineData("93.184.216.34", true)]
    [InlineData("8.8.8.8", true)]
    [InlineData("2606:2800:220:1:248:1893:25c8:1946", true)]
    public void SafeExternalHttpHandler_IsPublicIpAddress_BlocksPrivateLoopbackAndMetadataRanges(string ipString, bool expectedPublic)
    {
        var ip = IPAddress.Parse(ipString);
        Assert.Equal(expectedPublic, SafeExternalHttpHandler.IsPublicIpAddress(ip));
    }

    [Theory]
    [InlineData("https://example.com/article", true)]
    [InlineData("http://localhost:8000/api/get-users", false)]
    [InlineData("http://127.0.0.1:8080/admin", false)]
    [InlineData("http://169.254.169.254/latest/meta-data/", false)]
    [InlineData("ftp://example.com/file", false)]
    [InlineData("http://internal.local/secret", false)]
    public void SafeExternalHttpHandler_IsSafeExternalHttpUri_ValidatesSchemeAndHost(string url, bool expectedSafe)
    {
        Assert.True(Uri.TryCreate(url, UriKind.Absolute, out var uri));
        Assert.Equal(expectedSafe, SafeExternalHttpHandler.IsSafeExternalHttpUri(uri!));
    }
}
