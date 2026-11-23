using System.Security.Claims;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Spokes_Server.Core.Services.Communication.Presence;
using Spokes_Server.Core.Services.Security;

namespace Spokes_Server.Tests.Core.Services.Security;

public class FileUrlSignerTests
{
    private readonly FileTokenService _tokenService;

    public FileUrlSignerTests()
    {
        var services = new ServiceCollection();
        services.AddMemoryCache();
        services.AddDataProtection().UseEphemeralDataProtectionProvider();
        var sp = services.BuildServiceProvider();
        _tokenService = new FileTokenService(
            sp.GetRequiredService<IDataProtectionProvider>(),
            sp.GetRequiredService<IMemoryCache>());
    }

    [Fact]
    public void SignUrl_ResolvesIdentityFromUserCircuitContext()
    {
        var circuitContext = new UserCircuitContext
        {
            UserId = "emp-42",
            SessionId = "sess-42"
        };
        var signer = new FileUrlSigner(_tokenService, circuitContext: circuitContext);

        var signed = signer.SignUrl("/spokesapi/files/projects/p-1/plan.png");

        Assert.Contains("?t=", signed);
        var token = Uri.UnescapeDataString(signed.Split("t=")[1]);
        var validation = _tokenService.ValidateAccessToken("projects", "p-1", "plan.png", token, "emp-42", "sess-42", requireToken: true);
        Assert.True(validation.IsSuccess);
    }

    [Fact]
    public void SignUrl_FallsBackToHttpContextUser_ForHeadlessRenderTokenPdfExport()
    {
        var httpContext = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
            [
                new Claim("EmployeeId", "emp-pdf"),
                new Claim("SessionId", "render-token-session")
            ], "RenderToken"))
        };
        var accessor = new HttpContextAccessor { HttpContext = httpContext };
        var signer = new FileUrlSigner(_tokenService, circuitContext: new UserCircuitContext(), httpContextAccessor: accessor);

        var signed = signer.SignUrl("/spokesapi/files/projects/p-9/hero.jpg");

        Assert.Contains("?t=", signed);
        var token = Uri.UnescapeDataString(signed.Split("t=")[1]);
        var validation = _tokenService.ValidateAccessToken("projects", "p-9", "hero.jpg", token, "emp-pdf", "render-token-session", requireToken: true);
        Assert.True(validation.IsSuccess);
    }

    [Fact]
    public void SignUrl_FallsBackToCompletedAuthenticationStateProvider()
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim("EmployeeId", "emp-auth"),
            new Claim("SessionId", "sess-auth")
        ], "TestAuth"));
        var mockAuth = new Mock<AuthenticationStateProvider>();
        mockAuth.Setup(p => p.GetAuthenticationStateAsync()).ReturnsAsync(new AuthenticationState(principal));

        var signer = new FileUrlSigner(_tokenService, authStateProvider: mockAuth.Object);

        var signed = signer.SignUrl("/spokesapi/files/bills/b-1/invoice.pdf");

        Assert.Contains("?t=", signed);
        var token = Uri.UnescapeDataString(signed.Split("t=")[1]);
        var validation = _tokenService.ValidateAccessToken("bills", "b-1", "invoice.pdf", token, "emp-auth", "sess-auth", requireToken: true);
        Assert.True(validation.IsSuccess);
    }

    [Fact]
    public void SignHtmlFileUrls_SignsSameOriginFileUrlsAndLeavesExternalUntouched()
    {
        var circuitContext = new UserCircuitContext
        {
            UserId = "emp-html",
            SessionId = "sess-html"
        };
        var signer = new FileUrlSigner(_tokenService, circuitContext: circuitContext);

        var html = """
            <p><img src="/spokesapi/files/projects/p-1/diagram.png" alt="d" /></p>
            <p><img src="https://cdn.example.com/logo.png" /></p>
            <p><a href="/spokesapi/files/bills/b-2/bill.pdf?fn=bill.pdf">Bill</a></p>
            <p><img src="/spokesapi/files/demo-media/sample.jpg" /></p>
            """;

        var signedHtml = signer.SignHtmlFileUrls(html);

        Assert.Contains("src=\"/spokesapi/files/projects/p-1/diagram.png?t=", signedHtml);
        Assert.Contains("href=\"/spokesapi/files/bills/b-2/bill.pdf?fn=bill.pdf&amp;t=", signedHtml);
        Assert.Contains("src=\"https://cdn.example.com/logo.png\"", signedHtml);
        Assert.DoesNotContain("cdn.example.com/logo.png?t=", signedHtml);
        Assert.Contains("src=\"/spokesapi/files/demo-media/sample.jpg\"", signedHtml);
        Assert.DoesNotContain("demo-media/sample.jpg?t=", signedHtml);
    }
}
