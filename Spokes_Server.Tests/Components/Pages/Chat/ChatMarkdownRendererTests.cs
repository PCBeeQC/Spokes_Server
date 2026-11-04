using Bunit;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using MudBlazor.Services;
using Spokes_Server.Components.Pages.Chat;
using Spokes_Server.Core.Services.Communication.Presence;
using Spokes_Server.Core.Services.Content;
using Spokes_Server.Core.Services.Security;
using Xunit;

namespace Spokes_Server.Tests.Components.Pages.Chat;

public class ChatMarkdownRendererTests : BunitContext
{
    private readonly FileTokenService _fileTokens;
    private readonly UserCircuitContext _circuitContext;

    public ChatMarkdownRendererTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddMudServices();
        Services.AddLocalization();
        Services.AddDataProtection();
        Services.AddMemoryCache();
        Services.AddSingleton<MarkdownSanitizerService>();
        Services.AddSingleton<ILogger<ChatMarkdownRenderer>>(NullLogger<ChatMarkdownRenderer>.Instance);

        _circuitContext = new UserCircuitContext { UserId = "emp-1", SessionId = "sess-circuit-1" };
        Services.AddSingleton(_circuitContext);

        _fileTokens = new FileTokenService(
            DataProtectionProvider.Create("ChatMarkdownRendererTests"),
            new MemoryCache(new MemoryCacheOptions()));
        Services.AddSingleton(_fileTokens);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
        base.Dispose(disposing);
    }

    [Fact]
    public void TrySignChatFileUrl_SameOriginMatchingChannel_AppendsValidBoundTokenAndPreservesFragment()
    {
        var rawUrl = "/spokesapi/files/chat/chan-1/photo.png#ar=1.5";

        var signed = ChatMarkdownRenderer.TrySignChatFileUrl(
            rawUrl,
            channelId: "chan-1",
            currentUserId: "emp-1",
            effectiveSessionId: "sess-1",
            channelKey: "secret-channel-key-base64",
            fileTokens: _fileTokens);

        Assert.StartsWith("/spokesapi/files/chat/chan-1/photo.png?t=", signed);
        Assert.EndsWith("#ar=1.5", signed);

        var queryStart = signed.IndexOf("?t=") + 3;
        var hashStart = signed.IndexOf('#');
        var token = signed[queryStart..hashStart];

        var payload = _fileTokens.UnprotectToken(token);
        Assert.NotNull(payload);
        Assert.Equal("chat", payload!.Category);
        Assert.Equal("chan-1", payload.ContextId);
        Assert.Equal("photo.png", payload.FileName);
        Assert.Equal("emp-1", payload.UserId);
        Assert.Equal("sess-1", payload.SessionId);
        Assert.Equal("secret-channel-key-base64", payload.EncryptionKeyBase64);
    }

    [Theory]
    [InlineData("https://attacker.example/spokesapi/files/chat/chan-1/photo.png")]
    [InlineData("http://attacker.example/spokesapi/files/chat/chan-1/photo.png")]
    [InlineData("//attacker.example/spokesapi/files/chat/chan-1/photo.png")]
    [InlineData(@"\\attacker.example\spokesapi\files\chat\chan-1\photo.png")]
    [InlineData(@"/spokesapi/files/chat/chan-1/sub\photo.png")]
    [InlineData("javascript:alert(1)//spokesapi/files/chat/chan-1/photo.png")]
    [InlineData("data:image/png;base64,iVBORw0KGgo=/spokesapi/files/chat/chan-1/photo.png")]
    [InlineData("spokesapi/files/chat/chan-1/photo.png")]
    [InlineData("/spokesapi/files/albums/chan-1/photo.png")]
    [InlineData("/spokesapi/files/chat/chan-1/nested/photo.png")]
    [InlineData("/spokesapi/files/chat/chan-1/..")]
    public void TrySignChatFileUrl_ExternalOrNonCanonicalUrls_NeverAppendsToken(string maliciousUrl)
    {
        var result = ChatMarkdownRenderer.TrySignChatFileUrl(
            maliciousUrl,
            channelId: "chan-1",
            currentUserId: "emp-1",
            effectiveSessionId: "sess-1",
            channelKey: "top-secret-aes-key",
            fileTokens: _fileTokens);

        Assert.Equal(maliciousUrl, result);
        Assert.DoesNotContain("?t=", result);
    }

    [Fact]
    public void TrySignChatFileUrl_CrossChannelFileUrl_DoesNotSignForOtherChannel()
    {
        var crossChannelUrl = "/spokesapi/files/chat/chan-other/secret.png";

        var result = ChatMarkdownRenderer.TrySignChatFileUrl(
            crossChannelUrl,
            channelId: "chan-1",
            currentUserId: "emp-1",
            effectiveSessionId: "sess-1",
            channelKey: "chan-1-aes-key",
            fileTokens: _fileTokens);

        Assert.Equal(crossChannelUrl, result);
        Assert.DoesNotContain("?t=", result);
    }

    [Theory]
    [InlineData(null, "emp-1", "sess-1")]
    [InlineData("", "emp-1", "sess-1")]
    [InlineData("   ", "emp-1", "sess-1")]
    [InlineData("chan-1", null, "sess-1")]
    [InlineData("chan-1", "", "sess-1")]
    [InlineData("chan-1", "   ", "sess-1")]
    [InlineData("chan-1", "emp-1", null)]
    [InlineData("chan-1", "emp-1", "")]
    [InlineData("chan-1", "emp-1", "   ")]
    public void TrySignChatFileUrl_MissingChannelUserOrSession_RefusesToSign(
        string? channelId,
        string? userId,
        string? sessionId)
    {
        var rawUrl = "/spokesapi/files/chat/chan-1/photo.png";

        var result = ChatMarkdownRenderer.TrySignChatFileUrl(
            rawUrl,
            channelId: channelId,
            currentUserId: userId,
            effectiveSessionId: sessionId,
            channelKey: "chan-1-aes-key",
            fileTokens: _fileTokens);

        Assert.Equal(rawUrl, result);
        Assert.DoesNotContain("?t=", result);
    }

    [Fact]
    public void Render_MarkdownWithExternalAndSameOriginImages_SignsOnlySameChannelRelativeImage()
    {
        var markdown = string.Join("\n\n",
            "![external](https://evil.example/spokesapi/files/chat/chan-1/exfil.png)",
            "![cross](/spokesapi/files/chat/chan-2/other.png)",
            "![valid](/spokesapi/files/chat/chan-1/legit.png)");

        var cut = Render<ChatMarkdownRenderer>(parameters => parameters
            .Add(p => p.Content, markdown)
            .Add(p => p.ChannelId, "chan-1")
            .Add(p => p.CurrentUserId, "emp-1")
            .Add(p => p.SessionId, "sess-explicit-1")
            .Add(p => p.ChannelKey, "secret-aes-key"));

        var markup = cut.Markup;
        Assert.Contains("src=\"https://evil.example/spokesapi/files/chat/chan-1/exfil.png\"", markup);
        Assert.DoesNotContain("evil.example/spokesapi/files/chat/chan-1/exfil.png?t=", markup);
        Assert.Contains("src=\"/spokesapi/files/chat/chan-2/other.png\"", markup);
        Assert.DoesNotContain("/spokesapi/files/chat/chan-2/other.png?t=", markup);
        Assert.Contains("src=\"/spokesapi/files/chat/chan-1/legit.png?t=", markup);
    }

    [Fact]
    public void Render_MessageWithAngleBracketPlaceholders_RendersFullContentWithoutTruncation()
    {
        var markdown = string.Join("\n\n",
            "Local URL: https://192-168-1-50.<machine-id>.plex.direct:32400\nRemote URL: https://203-0-113-42.<machine-id>.plex.direct:32400",
            "Dead URL: https://old-ip.<id>.plex.direct and https://<wan-ip>.<id>.plex.direct:32400",
            "Final paragraph with @[Alice] at the end.");

        var cut = Render<ChatMarkdownRenderer>(parameters => parameters
            .Add(p => p.Content, markdown)
            .Add(p => p.ChannelId, "chan-1")
            .Add(p => p.CurrentUserId, "emp-1"));

        var markup = cut.Markup;
        Assert.Contains("&lt;machine-id&gt;.plex.direct:32400", markup);
        Assert.Contains("Remote URL:", markup);
        Assert.Contains("&lt;wan-ip&gt;.&lt;id&gt;.plex.direct:32400", markup);
        Assert.Contains("Final paragraph with", markup);
        Assert.Contains("@Alice", markup);
    }
}
