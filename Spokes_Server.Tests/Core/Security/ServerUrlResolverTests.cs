using Microsoft.AspNetCore.Http;
using Spokes_Server.Core.Security;
using Xunit;

namespace Spokes_Server.Tests.Core.Security;

public class ServerUrlResolverTests
{
    [Fact]
    public void IsSameOriginRequest_NullContextOrMissingAllProofHeaders_ReturnsFalse()
    {
        Assert.False(ServerUrlResolver.IsSameOriginRequest(null));

        var noHeadersCtx = new DefaultHttpContext();
        noHeadersCtx.Request.Scheme = "https";
        noHeadersCtx.Request.Host = new HostString("spokes.example.com");

        Assert.False(ServerUrlResolver.IsSameOriginRequest(noHeadersCtx));
        Assert.False(ServerUrlResolver.IsSameOriginRequest(noHeadersCtx, "https://spokes.example.com"));
    }

    [Fact]
    public void IsSameOriginRequest_SecFetchSiteNoneWithoutOriginOrReferer_ReturnsFalse()
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Scheme = "https";
        ctx.Request.Host = new HostString("spokes.example.com");
        ctx.Request.Headers["Sec-Fetch-Site"] = "none";

        Assert.False(ServerUrlResolver.IsSameOriginRequest(ctx));
    }

    [Theory]
    [InlineData("cross-site")]
    [InlineData("same-site")]
    [InlineData("unknown-value")]
    public void IsSameOriginRequest_DisallowedSecFetchSite_ReturnsFalse(string secFetchSite)
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Scheme = "https";
        ctx.Request.Host = new HostString("spokes.example.com");
        ctx.Request.Headers["Sec-Fetch-Site"] = secFetchSite;
        ctx.Request.Headers["Origin"] = "https://spokes.example.com";

        Assert.False(ServerUrlResolver.IsSameOriginRequest(ctx, "https://spokes.example.com"));
    }

    [Fact]
    public void IsSameOriginRequest_WithConfiguredPublicUrl_EnforcesFullSchemeHostAndPort()
    {
        const string publicUrl = "https://spokes.example.com:8443";

        var exactMatch = new DefaultHttpContext();
        exactMatch.Request.Scheme = "https";
        exactMatch.Request.Host = new HostString("spokes.example.com", 8443);
        exactMatch.Request.Headers["Origin"] = "https://spokes.example.com:8443";
        Assert.True(ServerUrlResolver.IsSameOriginRequest(exactMatch, publicUrl));

        var wrongScheme = new DefaultHttpContext();
        wrongScheme.Request.Scheme = "https";
        wrongScheme.Request.Host = new HostString("spokes.example.com", 8443);
        wrongScheme.Request.Headers["Origin"] = "http://spokes.example.com:8443";
        Assert.False(ServerUrlResolver.IsSameOriginRequest(wrongScheme, publicUrl));

        var wrongPort = new DefaultHttpContext();
        wrongPort.Request.Scheme = "https";
        wrongPort.Request.Host = new HostString("spokes.example.com", 8443);
        wrongPort.Request.Headers["Origin"] = "https://spokes.example.com";
        Assert.False(ServerUrlResolver.IsSameOriginRequest(wrongPort, publicUrl));

        var spoofedHostHeader = new DefaultHttpContext();
        spoofedHostHeader.Request.Scheme = "https";
        spoofedHostHeader.Request.Host = new HostString("evil.com");
        spoofedHostHeader.Request.Headers["Origin"] = "https://evil.com";
        Assert.False(ServerUrlResolver.IsSameOriginRequest(spoofedHostHeader, publicUrl));

        var spoofedHostWithSecFetchSameOriginOnly = new DefaultHttpContext();
        spoofedHostWithSecFetchSameOriginOnly.Request.Scheme = "https";
        spoofedHostWithSecFetchSameOriginOnly.Request.Host = new HostString("evil.com");
        spoofedHostWithSecFetchSameOriginOnly.Request.Headers["Sec-Fetch-Site"] = "same-origin";
        Assert.False(ServerUrlResolver.IsSameOriginRequest(spoofedHostWithSecFetchSameOriginOnly, publicUrl));
    }

    [Fact]
    public void IsSameOriginRequest_WithoutConfiguredPublicUrl_EnforcesRequestSchemeHostAndPort()
    {
        var validReferer = new DefaultHttpContext();
        validReferer.Request.Scheme = "http";
        validReferer.Request.Host = new HostString("localhost", 5000);
        validReferer.Request.Headers["Referer"] = "http://localhost:5000/chat/general";
        Assert.True(ServerUrlResolver.IsSameOriginRequest(validReferer));

        var mismatchedPort = new DefaultHttpContext();
        mismatchedPort.Request.Scheme = "http";
        mismatchedPort.Request.Host = new HostString("localhost", 5000);
        mismatchedPort.Request.Headers["Origin"] = "http://localhost:5001";
        Assert.False(ServerUrlResolver.IsSameOriginRequest(mismatchedPort));

        var mismatchedScheme = new DefaultHttpContext();
        mismatchedScheme.Request.Scheme = "https";
        mismatchedScheme.Request.Host = new HostString("spokes.local");
        mismatchedScheme.Request.Headers["Origin"] = "http://spokes.local";
        Assert.False(ServerUrlResolver.IsSameOriginRequest(mismatchedScheme));

        var nullOrigin = new DefaultHttpContext();
        nullOrigin.Request.Scheme = "https";
        nullOrigin.Request.Host = new HostString("spokes.local");
        nullOrigin.Request.Headers["Origin"] = "null";
        Assert.False(ServerUrlResolver.IsSameOriginRequest(nullOrigin));
    }
}
