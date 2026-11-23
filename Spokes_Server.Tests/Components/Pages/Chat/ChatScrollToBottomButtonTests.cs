using System;
using System.IO;
using System.Text.RegularExpressions;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;
using Spokes_Server.Components.Pages.Chat;
using Xunit;

namespace Spokes_Server.Tests.Components.Pages.Chat;

/// <summary>
/// bUnit and CSS contract tests ensuring ChatScrollToBottomButton never intercepts
/// pointer events or keyboard focus when hidden, and activates cleanly when scrolled up.
/// </summary>
public class ChatScrollToBottomButtonTests : BunitContext
{
    public ChatScrollToBottomButtonTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddMudServices();
        Services.AddLocalization();
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
    public void HiddenState_OmitsVisibleClass_DisablesFocus_AndHasNoInlinePointerEventsOverride()
    {
        var cut = Render<ChatScrollToBottomButton>(parameters => parameters
            .Add(p => p.Visible, false));

        var wrapper = cut.Find(".scroll-to-bottom-wrapper");
        Assert.DoesNotContain("visible", wrapper.ClassList);
        Assert.Equal("true", wrapper.GetAttribute("aria-hidden"));
        Assert.Null(wrapper.GetAttribute("style"));

        var button = cut.Find(".scroll-to-bottom-wrapper button");
        Assert.Equal("-1", button.GetAttribute("tabindex"));
        Assert.Equal("Scroll to bottom", button.GetAttribute("aria-label"));

        var buttonStyle = button.GetAttribute("style") ?? string.Empty;
        Assert.DoesNotContain("pointer-events", buttonStyle, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void VisibleState_AppliesVisibleClass_EnablesFocus_AndInvokesOnClick()
    {
        var clicked = false;
        var cut = Render<ChatScrollToBottomButton>(parameters => parameters
            .Add(p => p.Visible, true)
            .Add(p => p.OnClick, () => { clicked = true; }));

        var wrapper = cut.Find(".scroll-to-bottom-wrapper");
        Assert.Contains("visible", wrapper.ClassList);
        Assert.Equal("false", wrapper.GetAttribute("aria-hidden"));

        var button = cut.Find(".scroll-to-bottom-wrapper button");
        Assert.Equal("0", button.GetAttribute("tabindex"));
        Assert.Equal("Scroll to bottom", button.GetAttribute("aria-label"));

        var buttonStyle = button.GetAttribute("style") ?? string.Empty;
        Assert.DoesNotContain("pointer-events", buttonStyle, StringComparison.OrdinalIgnoreCase);

        button.Click();
        Assert.True(clicked);
    }

    [Fact]
    public void TogglingVisibleParameter_TransitionsDomAttributesCleanly()
    {
        var visible = false;
        var cut = Render<ChatScrollToBottomButton>(parameters => parameters
            .Add(p => p.Visible, visible));

        Assert.DoesNotContain("visible", cut.Find(".scroll-to-bottom-wrapper").ClassList);
        Assert.Equal("-1", cut.Find(".scroll-to-bottom-wrapper button").GetAttribute("tabindex"));

        cut.Render(parameters => parameters.Add(p => p.Visible, true));
        Assert.Contains("visible", cut.Find(".scroll-to-bottom-wrapper").ClassList);
        Assert.Equal("false", cut.Find(".scroll-to-bottom-wrapper").GetAttribute("aria-hidden"));
        Assert.Equal("0", cut.Find(".scroll-to-bottom-wrapper button").GetAttribute("tabindex"));

        cut.Render(parameters => parameters.Add(p => p.Visible, false));
        Assert.DoesNotContain("visible", cut.Find(".scroll-to-bottom-wrapper").ClassList);
        Assert.Equal("true", cut.Find(".scroll-to-bottom-wrapper").GetAttribute("aria-hidden"));
        Assert.Equal("-1", cut.Find(".scroll-to-bottom-wrapper button").GetAttribute("tabindex"));
    }
}
