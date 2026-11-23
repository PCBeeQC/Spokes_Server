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
        Assert.Contains("data-media-src=\"/spokesapi/files/chat/chan-1/legit.png?t=", markup);
        Assert.Contains("data-media-encrypted=\"true\"", markup);
        Assert.DoesNotContain(" src=\"/spokesapi/files/chat/chan-1/legit.png?t=", markup);
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

    [Fact]
    public void Render_HugeTruncatedCodeBlock_OmitsInlineRawCodeAndEmitsOnDemandCopyAttributes()
    {
        var hugeLog = new string('X', ChatMarkdownRenderer.MaxInlineRawCodeChars + 5000);
        var fullContent = $"[code=txt]\n{hugeLog}\n[/code]";
        var displayContent = $"[code=txt]\n{hugeLog[..500]}...\n[/code]";

        var cut = Render<ChatMarkdownRenderer>(parameters => parameters
            .Add(p => p.Content, displayContent)
            .Add(p => p.FullContentRef, new ChatTextRef(fullContent))
            .Add(p => p.MessageId, "msg-huge-1"));

        var markup = cut.Markup;
        Assert.DoesNotContain("data-raw-code=", markup);
        Assert.Contains("data-copy-msg-id=\"msg-huge-1\"", markup);
        Assert.Contains("data-copy-code-idx=\"0\"", markup);
        Assert.True(markup.Length < 4096, $"Expected compact markup without huge inline log, got {markup.Length} chars.");
    }

    [Fact]
    public void Render_SmallTruncatedCodeBlock_KeepsInlineRawCodeAttribute()
    {
        var smallCode = new string('A', 200);
        var fullContent = $"[code=txt]{smallCode}[/code]";
        var displayContent = $"[code=txt]{smallCode[..50]}...[/code]";

        var cut = Render<ChatMarkdownRenderer>(parameters => parameters
            .Add(p => p.Content, displayContent)
            .Add(p => p.FullContentRef, new ChatTextRef(fullContent))
            .Add(p => p.MessageId, "msg-small-1"));

        var markup = cut.Markup;
        Assert.Contains($"data-raw-code=\"{smallCode}\"", markup);
        Assert.DoesNotContain("data-copy-msg-id=", markup);
    }

    [Fact]
    public void ChatMarkdownRenderer_UsesReferenceWrapperForFullContent()
    {
        // Regression guard: string FullContent would be serialized verbatim into Blazor RenderBatch.Strings
        var type = typeof(ChatMarkdownRenderer);
        Assert.Null(type.GetProperty("FullContent"));
        Assert.Equal(typeof(ChatTextRef), type.GetProperty(nameof(ChatMarkdownRenderer.FullContentRef))?.PropertyType);
    }

    [Fact]
    public void Render_CodeBlock_UsesDeclarativeDelegatedCopyButtonWithZeroInlineOnclick()
    {
        var cut = Render<ChatMarkdownRenderer>(parameters => parameters
            .Add(p => p.Content, "```csharp\nConsole.WriteLine(\"Hi\");\n```"));

        var markup = cut.Markup;
        Assert.Contains("class=\"copy-code-btn\"", markup);
        Assert.Contains("data-copied-label=", markup);
        Assert.Contains("#spokes-icon-copy", markup);
        Assert.DoesNotContain("onclick=", markup, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Render_KlipyGifWithAspectRatio_UsesCssClassLayoutAndPreservesAspectRatioAndWatermark()
    {
        var cut = Render<ChatMarkdownRenderer>(parameters => parameters
            .Add(p => p.Content, "![GIF](https://media.klipy.com/sample.gif#ar=1.77)"));

        var container = cut.Find(".chat-markdown .gif-container");
        Assert.Equal("aspect-ratio: 1.77;", container.GetAttribute("style"));

        var gifImg = cut.Find(".chat-markdown .gif-container > img:not(.klipy-watermark)");
        Assert.Equal("https://media.klipy.com/sample.gif", gifImg.GetAttribute("src"));
        Assert.Null(gifImg.GetAttribute("style"));

        var watermark = cut.Find(".chat-markdown .gif-container > img.klipy-watermark");
        Assert.Equal("/Klipy/KLIPY Light with logo.svg", watermark.GetAttribute("src"));
        Assert.Null(watermark.GetAttribute("style"));
    }

    [Fact]
    public void Render_LegacyKlipyGifWithoutAspectRatio_UsesCssClassContainerAndWatermark()
    {
        var cut = Render<ChatMarkdownRenderer>(parameters => parameters
            .Add(p => p.Content, "![GIF](https://media.klipy.com/legacy.gif)"));

        var container = cut.Find(".chat-markdown .klipy-gif-container");
        Assert.Null(container.GetAttribute("style"));
        Assert.NotNull(cut.Find(".chat-markdown .klipy-gif-container > img.klipy-watermark"));
    }
}
