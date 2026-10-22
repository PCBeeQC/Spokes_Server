using System;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using MudBlazor.Services;
using Spokes_Server.Components.Pages.Public;
using Spokes_Server.Components.UI;
using Xunit;

namespace Spokes_Server.Tests.Components.UI;

/// <summary>
/// bUnit tests validating interactive Razor component DOM rendering, parameter cascades, and events.
/// </summary>
public class BlazorComponentInteractiveRenderingTests : BunitContext
{
    public BlazorComponentInteractiveRenderingTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddMudServices();
        Services.AddLocalization();
    }

    [Fact]
    public void ChatNotificationContent_RendersProvidedParameters_InDom()
    {
        // Act
        var cut = Render<ChatNotificationContent>(parameters => parameters
            .Add(p => p.SenderName, "Jane Doe")
            .Add(p => p.Content, "Meeting starting in 5 minutes")
            .Add(p => p.Initials, "JD")
            .Add(p => p.ChannelId, "chan-100")
            .Add(p => p.ChannelName, "#engineering"));

        // Assert
        var markup = cut.Markup;
        Assert.Contains("Jane Doe", markup);
        Assert.Contains("Meeting starting in 5 minutes", markup);
        Assert.Contains("JD", markup);
        Assert.Contains("#engineering", markup);

        var avatar = cut.FindComponent<MudAvatar>();
        Assert.NotNull(avatar);
        Assert.Equal(Color.Primary, avatar.Instance.Color);
        Assert.Equal(Size.Medium, avatar.Instance.Size);
    }

    [Fact]
    public void EmailNotificationContent_RendersEmailMetadata_InDom()
    {
        // Act
        var cut = Render<EmailNotificationContent>(parameters => parameters
            .Add(p => p.SenderName, "Accounting Team")
            .Add(p => p.Subject, "Invoice INV-2026-001 Approval Required")
            .Add(p => p.Snippet, "Please review the attached invoice for project Alpha."));

        // Assert
        var markup = cut.Markup;
        Assert.Contains("New Email", markup);
        Assert.Contains("Accounting Team", markup);
        Assert.Contains("Invoice INV-2026-001 Approval Required", markup);
        Assert.Contains("Please review the attached invoice for project Alpha.", markup);

        var avatar = cut.FindComponent<MudAvatar>();
        Assert.NotNull(avatar);
        Assert.Equal(Color.Primary, avatar.Instance.Color);
    }

    [Fact]
    public void MobileLoginSuccess_RendersSuccessBannerAndReturnButton()
    {
        // Arrange
        var nav = Services.GetRequiredService<NavigationManager>();
        nav.NavigateTo("http://localhost/mobile-login-success?link=" + Uri.EscapeDataString("spokes://auth-callback?code=xyz"));

        // Act
        var cut = Render<MobileLoginSuccess>();

        // Assert
        var markup = cut.Markup;
        Assert.Contains("Login Successful", markup);
        Assert.Contains("Return to Spokes", markup);

        var icon = cut.FindComponent<MudIcon>();
        Assert.NotNull(icon);
        Assert.Equal(Color.Success, icon.Instance.Color);
    }

    [Fact]
    public void AccessDenied_RendersHeadingAndNavigatesHome_OnButtonClick()
    {
        // Arrange
        var nav = Services.GetRequiredService<NavigationManager>();

        // Act
        var cut = Render<AccessDenied>();

        // Assert markup
        Assert.Contains("Access Denied", cut.Markup);
        Assert.Contains("Back to Dashboard", cut.Markup);

        var button = cut.FindComponent<MudButton>();
        Assert.NotNull(button);
        Assert.Contains("Back to Dashboard", button.Markup);

        // Click the button to trigger navigation
        button.Find("button").Click();
        Assert.EndsWith("/", nav.Uri);
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
    public void MudButtonGroup_SplitButton_RendersAlignedActivatorWithMatchingVariantAndSize()
    {
        var cut = Render(builder =>
        {
            builder.OpenComponent<MudButtonGroup>(0);
            builder.AddAttribute(1, "Color", Color.Primary);
            builder.AddAttribute(2, "Variant", Variant.Filled);
            builder.AddAttribute(3, "Size", Size.Small);
            builder.AddAttribute(4, "ChildContent", (RenderFragment)(childBuilder =>
            {
                childBuilder.OpenComponent<MudButton>(5);
                childBuilder.AddAttribute(6, "StartIcon", Icons.Material.Filled.AddCard);
                childBuilder.AddAttribute(7, "ChildContent", (RenderFragment)(b => b.AddContent(8, "Create Bill from Email")));
                childBuilder.CloseComponent();

                childBuilder.OpenComponent<MudMenu>(9);
                childBuilder.AddAttribute(10, "Icon", Icons.Material.Filled.ArrowDropDown);
                childBuilder.AddAttribute(11, "Size", Size.Small);
                childBuilder.AddAttribute(12, "Color", Color.Primary);
                childBuilder.AddAttribute(13, "Variant", Variant.Filled);
                childBuilder.AddAttribute(14, "Style", "align-self: auto;");
                childBuilder.AddAttribute(15, "Dense", true);
                childBuilder.AddAttribute(16, "ChildContent", (RenderFragment)(b => { }));
                childBuilder.CloseComponent();
            }));
            builder.CloseComponent();
        });

        // The menu activator must be styled to match the button group's variant and size
        var menuWrapper = cut.Find(".mud-menu");
        Assert.NotNull(menuWrapper);
        Assert.Contains("align-self: auto;", menuWrapper.GetAttribute("style") ?? "");

        var menuButton = cut.Find(".mud-menu-icon-button-activator");
        Assert.NotNull(menuButton);
        Assert.Contains("mud-button-filled", menuButton.ClassName);
        Assert.Contains("mud-button-filled-primary", menuButton.ClassName);
        Assert.Contains("mud-button-filled-size-small", menuButton.ClassName);

        var svgIcon = menuButton.QuerySelector("svg");
        Assert.NotNull(svgIcon);
        Assert.Contains("mud-icon-size-small", svgIcon.ClassName);
    }
}

