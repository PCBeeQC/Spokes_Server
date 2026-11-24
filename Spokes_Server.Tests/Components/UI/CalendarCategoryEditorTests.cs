using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using MudBlazor;
using MudBlazor.Services;
using Spokes_Server.Components.Dialogs.HR;
using Spokes_Server.Components.Shared;
using Spokes_Server.Core.Helpers;
using Spokes_Server.Core.Models.HR;
using Spokes_Server.Core.Services.HR;
using Spokes_Server.Core.Services.Identity;
using Xunit;

namespace Spokes_Server.Tests.Components.UI;

public class CalendarCategoryEditorTests : BunitContext
{
    public CalendarCategoryEditorTests()
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
    public void ThemeColorPicker_DoesNotMountOverlayOrPopoverUntilClicked_AndUpdatesColor()
    {
        var popoverProvider = Render<MudPopoverProvider>();
        string selected = nameof(Color.Info);

        var cut = Render<ThemeColorPicker>(parameters => parameters
            .Add(p => p.Value, selected)
            .Add(p => p.ValueChanged, EventCallback.Factory.Create<string>(this, v => selected = v)));

        // Closed state: zero overlays, zero open popovers, zero option buttons mounted
        var activator = cut.Find(".spokes-color-picker-btn");
        Assert.Equal("false", activator.GetAttribute("aria-expanded"));
        Assert.Contains(nameof(Color.Info), activator.TextContent);
        Assert.Empty(popoverProvider.FindAll(".mud-overlay"));
        Assert.Empty(popoverProvider.FindAll(".mud-popover-open"));
        Assert.Empty(popoverProvider.FindAll(".spokes-color-picker-option"));

        // Click activator -> mounts single overlay + popover with all 9 default colors
        activator.Click();
        Assert.Equal("true", cut.Find(".spokes-color-picker-btn").GetAttribute("aria-expanded"));
        Assert.Single(popoverProvider.FindAll(".mud-overlay"));
        var options = popoverProvider.FindAll(".spokes-color-picker-option");
        Assert.Equal(ThemeColorPicker.DefaultColors.Length, options.Count);

        // Select Success -> fires ValueChanged and unmounts overlay/popover cleanly
        var successOption = options.Single(o => o.TextContent.Contains(nameof(Color.Success)));
        successOption.Click();

        Assert.Equal(nameof(Color.Success), selected);
        Assert.Equal("false", cut.Find(".spokes-color-picker-btn").GetAttribute("aria-expanded"));
        Assert.Empty(popoverProvider.FindAll(".mud-overlay"));
        Assert.Empty(popoverProvider.FindAll(".mud-popover-open"));
    }

    [Fact]
    public void CalendarCategoryListEditor_RendersAllRowsImmediatelyWithoutMudHiddenOrIdlePopovers()
    {
        var popoverProvider = Render<MudPopoverProvider>();
        var categories = CalendarCategoryDefaults.GetSystemDefaults();
        int addCount = 0;
        CalendarCategory? removed = null;
        int resetCount = 0;

        var cut = Render<CalendarCategoryListEditor>(parameters => parameters
            .Add(p => p.Categories, categories)
            .Add(p => p.OnAdd, EventCallback.Factory.Create(this, () => addCount++))
            .Add(p => p.OnRemove, EventCallback.Factory.Create<CalendarCategory>(this, c => removed = c))
            .Add(p => p.OnReset, EventCallback.Factory.Create(this, () => resetCount++)));

        // All 5 rows render immediately on first render without MudHidden or MudTable
        Assert.Empty(cut.FindComponents<MudHidden>());
        Assert.Equal(5, cut.FindAll(".spokes-cat-row").Count);
        Assert.Equal(5, cut.FindAll(".spokes-color-picker-btn").Count);
        Assert.Empty(popoverProvider.FindAll(".mud-overlay"));
        Assert.Empty(popoverProvider.FindAll(".mud-popover-open"));

        // Picking a color on row 0 updates the category color and preview chip
        cut.FindAll(".spokes-color-picker-btn")[0].Click();
        popoverProvider.FindAll(".spokes-color-picker-option")
            .Single(o => o.TextContent.Contains(nameof(Color.Success)))
            .Click();
        Assert.Equal(nameof(Color.Success), categories[0].Color);

        // Footer actions invoke callbacks
        cut.FindAll(".spokes-cat-footer button")[0].Click();
        Assert.Equal(1, addCount);
        cut.FindAll(".spokes-cat-footer button")[1].Click();
        Assert.Equal(1, resetCount);

        // Row delete action invokes OnRemove with the matching category
        cut.FindAll(".spokes-cat-col-actions button")[0].Click();
        Assert.Same(categories[0], removed);
    }

    [Fact]
    public async Task CalendarCategoryManagerDialog_RendersCategoriesImmediatelyOnFirstRender_AndSaves()
    {
        var employee = new Employee { Id = "emp-demo-1", FirstName = "Alex", LastName = "Tremblay" };
        var initialCategories = new List<CalendarCategory>
        {
            new() { Id = "cat-1", Name = "Deep Work", Color = nameof(Color.Primary) },
            new() { Id = "cat-2", Name = "Team Sync", Color = nameof(Color.Info) }
        };

        var authMock = new Mock<AuthenticationStateProvider>();
        var userServiceMock = new Mock<UserService>(authMock.Object, null!, null!);
        userServiceMock.Setup(s => s.GetEmployeeAsync()).ReturnsAsync(employee);

        var calendarServiceMock = new Mock<CalendarService>(null!, null, null, null, null, null);
        calendarServiceMock.Setup(s => s.GetEffectiveCategories(employee.Id)).Returns(initialCategories);
        List<CalendarCategory>? savedCategories = null;
        calendarServiceMock
            .Setup(s => s.UpdateUserCategories(employee.Id, It.IsAny<List<CalendarCategory>>()))
            .Callback<string, List<CalendarCategory>>((_, cats) => savedCategories = cats)
            .Returns(SpokesResult.Success());

        Services.AddSingleton(userServiceMock.Object);
        Services.AddSingleton(calendarServiceMock.Object);

        var popoverProvider = Render<MudPopoverProvider>();
        var dialogProvider = Render<MudDialogProvider>();
        var dialogService = Services.GetRequiredService<IDialogService>();

        IDialogReference? dialogRef = null;
        await dialogProvider.InvokeAsync(async () =>
        {
            dialogRef = await dialogService.ShowAsync<CalendarCategoryManagerDialog>("");
        });

        // Immediate render: no MudHidden wait, no progress bar stall, zero idle popovers
        Assert.Empty(dialogProvider.FindComponents<MudHidden>());
        Assert.Empty(dialogProvider.FindComponents<MudProgressLinear>());
        Assert.Equal(2, dialogProvider.FindAll(".spokes-cat-row").Count);
        Assert.Empty(popoverProvider.FindAll(".mud-popover-open"));

        // Save invokes CalendarService.UpdateUserCategories and resolves dialog with Ok(true)
        var saveButton = dialogProvider.FindAll("button").Single(b => b.TextContent.Contains("Save Changes"));
        saveButton.Click();

        var result = await dialogRef!.Result;
        Assert.NotNull(result);
        Assert.False(result!.Canceled);
        Assert.NotNull(savedCategories);
        Assert.Equal(2, savedCategories!.Count);
        Assert.Equal("Deep Work", savedCategories[0].Name);
    }
}
