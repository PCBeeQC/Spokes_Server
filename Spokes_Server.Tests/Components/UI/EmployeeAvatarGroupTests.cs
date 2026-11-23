using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using MudBlazor.Services;
using Spokes_Server.Components.Dialogs.Common;
using Spokes_Server.Components.Shared;
using Spokes_Server.Core.Models.HR;
using Spokes_Server.Core.Services.Communication.Presence;
using Spokes_Server.Core.Services.Communication.Voice;
using Xunit;

namespace Spokes_Server.Tests.Components.UI;

public class EmployeeAvatarGroupTests : BunitContext
{
    public EmployeeAvatarGroupTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddMudServices();
        Services.AddLocalization();
        Services.AddSingleton(new PresenceStateService(new VoiceStateService()));
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
        base.Dispose(disposing);
    }

    private static List<Employee> CreateEmployees(int count) =>
        Enumerable.Range(1, count)
            .Select(i => new Employee
            {
                Id = $"emp-{i}",
                FirstName = $"User{i}",
                LastName = $"Test{i}",
                Position = $"Role {i}"
            })
            .ToList();

    [Fact]
    public void EmployeeAvatarGroup_WhenWithinMax_RendersAllAvatarsWithoutOverflowOrPresence()
    {
        var employees = CreateEmployees(3);
        var presence = Services.GetRequiredService<PresenceStateService>();
        presence.RegisterConnection(new ConnectionRegistrationOptions
        {
            UserId = "emp-1",
            SubscriptionId = "sub-1",
            IsFocused = true
        });
        Assert.Equal(UserPresenceBadge.OnlineDesktop, presence.GetUserPresenceBadge("emp-1"));

        var cut = Render<EmployeeAvatarGroup>(p => p
            .Add(x => x.Employees, employees)
            .Add(x => x.Max, 3)
            .Add(x => x.Size, Size.Small));

        var avatars = cut.FindAll(".employee-avatar");
        Assert.Equal(3, avatars.Count);
        Assert.Equal("User1 Test1", avatars[0].GetAttribute("title"));
        Assert.Empty(cut.FindAll(".spokes-avatar-group-overflow"));
        Assert.Empty(cut.FindAll(".spokes-avatar-group-overflow-text"));
        Assert.Empty(cut.FindAll(".presence-dot"));
        Assert.Empty(cut.FindAll(".presence-mobile-badge"));
    }

    [Fact]
    public void EmployeeAvatarGroup_WhenExceedingMax_TruncatesToMaxAndRendersEllipsisOverflow()
    {
        var employees = CreateEmployees(12);

        var cut = Render<EmployeeAvatarGroup>(p => p
            .Add(x => x.Employees, employees)
            .Add(x => x.Max, 3)
            .Add(x => x.Size, Size.Small)
            .Add(x => x.OverflowStyle, AvatarOverflowStyle.Ellipsis)
            .Add(x => x.OverflowColor, Color.Primary));

        // Strictly bounds mounted EmployeeAvatar instances to Max
        Assert.Equal(3, cut.FindAll(".employee-avatar").Count);

        var overflow = cut.Find(".spokes-avatar-group-overflow.mud-avatar-filled-primary");
        Assert.Equal("DIV", overflow.TagName);
        Assert.Equal("+9 more", overflow.GetAttribute("title"));
        Assert.NotNull(overflow.QuerySelector("svg.spokes-avatar-group-overflow-dots"));
        Assert.Equal(4, cut.FindAll(".mud-avatar").Count);
    }

    [Fact]
    public void EmployeeAvatarGroup_WhenInteractiveOverflowClicked_InvokesCallback()
    {
        var employees = CreateEmployees(6);
        int clickCount = 0;

        var cut = Render<EmployeeAvatarGroup>(p => p
            .Add(x => x.Employees, employees)
            .Add(x => x.Max, 3)
            .Add(x => x.OverflowTitle, "View all members")
            .Add(x => x.OnOverflowClick, EventCallback.Factory.Create(this, () => clickCount++)));

        var button = cut.Find("button.spokes-avatar-group-overflow.is-interactive");
        Assert.Equal("View all members", button.GetAttribute("title"));
        Assert.Equal("View all members", button.GetAttribute("aria-label"));

        button.Click();
        Assert.Equal(1, clickCount);
    }

    [Fact]
    public void EmployeeAvatarGroup_CountOverflow_SupportsStackedBadgeAndInlineTooltipNames()
    {
        var employees = new List<Employee>
        {
            new() { Id = "1", FirstName = "Alice", LastName = "Alpha" },
            new() { Id = "2", FirstName = "Bob", LastName = "Beta" },
            new() { Id = "3", FirstName = "Cara", LastName = "Gamma" },
            new() { Id = "4", FirstName = "Dana", LastName = "White" },
            new() { Id = "5", FirstName = "Eli", LastName = "Vance" }
        };

        var stackedCut = Render<EmployeeAvatarGroup>(p => p
            .Add(x => x.Employees, employees)
            .Add(x => x.Max, 2)
            .Add(x => x.Layout, AvatarGroupLayout.Stacked)
            .Add(x => x.OverflowStyle, AvatarOverflowStyle.Count)
            .Add(x => x.OverflowColor, Color.Info));

        var stackedOverflow = stackedCut.Find(".spokes-avatar-group-overflow.mud-avatar-filled-info");
        Assert.Equal("+3", stackedOverflow.TextContent.Trim());

        var inlineCut = Render<EmployeeAvatarGroup>(p => p
            .Add(x => x.Employees, employees)
            .Add(x => x.Max, 3)
            .Add(x => x.Layout, AvatarGroupLayout.Inline)
            .Add(x => x.OverflowStyle, AvatarOverflowStyle.Count)
            .Add(x => x.ShowOverflowNamesInTooltip, true));

        Assert.Equal(3, inlineCut.FindAll(".employee-avatar").Count);
        var inlineOverflow = inlineCut.Find(".spokes-avatar-group-overflow-text");
        Assert.Equal("+2", inlineOverflow.TextContent.Trim());
        Assert.Equal("Dana White, Eli Vance", inlineOverflow.GetAttribute("title"));
    }

    [Fact]
    public void EmployeeAvatarGroup_WhenEmpty_RendersEmptyContentOrNothing()
    {
        var emptyNoFallback = Render<EmployeeAvatarGroup>(p => p
            .Add(x => x.Employees, Array.Empty<Employee>())
            .Add(x => x.Max, 3));

        Assert.Empty(emptyNoFallback.FindAll(".mud-avatar"));
        Assert.True(string.IsNullOrWhiteSpace(emptyNoFallback.Markup));

        var emptyWithFallback = Render<EmployeeAvatarGroup>(p => p
            .Add(x => x.Employees, Array.Empty<Employee>())
            .Add(x => x.EmptyContent, (RenderFragment)(b =>
            {
                b.OpenElement(0, "div");
                b.AddAttribute(1, "class", "mud-avatar fallback-slot");
                b.CloseElement();
            })));

        Assert.Single(emptyWithFallback.FindAll(".spokes-avatar-group .fallback-slot"));
    }

    [Fact]
    public async Task EmployeeListDialog_RendersAlphabeticallySortedDeduplicatedUsersAndEmptyState()
    {
        Render<MudPopoverProvider>();
        var provider = Render<MudDialogProvider>();
        var dialogService = Services.GetRequiredService<IDialogService>();

        var employees = new List<Employee>
        {
            new() { Id = "e2", FirstName = "Zoe", LastName = "Zimmer", Position = "Designer" },
            new() { Id = "e1", FirstName = "Alice", LastName = "Adams", Position = "Engineer" },
            new() { Id = "e1", FirstName = "Alice", LastName = "Adams", Position = "Engineer" }
        };

        var parameters = new DialogParameters<EmployeeListDialog>
        {
            { x => x.Title, "Contributors" },
            { x => x.Employees, employees }
        };

        IDialogReference dialogRef = default!;
        await provider.InvokeAsync(async () =>
        {
            dialogRef = await dialogService.ShowAsync<EmployeeListDialog>("Contributors", parameters);
        });

        var rows = provider.FindAll(".employee-list-dialog-row");
        Assert.Equal(2, rows.Count);
        Assert.Contains("Alice Adams", rows[0].TextContent);
        Assert.Contains("Engineer", rows[0].TextContent);
        Assert.Contains("Zoe Zimmer", rows[1].TextContent);
        Assert.Contains("2 people", provider.Markup);

        await provider.InvokeAsync(() => dialogRef.Close());

        var emptyParams = new DialogParameters<EmployeeListDialog>
        {
            { x => x.Title, "Contributors" },
            { x => x.Employees, Array.Empty<Employee>() }
        };
        await provider.InvokeAsync(async () =>
        {
            dialogRef = await dialogService.ShowAsync<EmployeeListDialog>("Contributors", emptyParams);
        });

        Assert.Empty(provider.FindAll(".employee-list-dialog-row"));
        Assert.Contains("No users to display.", provider.Markup);
    }
}
