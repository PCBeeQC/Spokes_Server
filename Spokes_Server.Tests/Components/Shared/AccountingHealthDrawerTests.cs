using Bunit;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using MudBlazor;
using MudBlazor.Extensions;
using MudBlazor.Services;
using Spokes_Server.Components.Shared;
using Spokes_Server.Core.Models.Accounting;
using Spokes_Server.Core.Services.Accounting;
using Xunit;

namespace Spokes_Server.Tests.Components.Shared;

public class AccountingHealthDrawerTests : BunitContext
{
    public AccountingHealthDrawerTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddMudServices();
        Services.AddLocalization();
        Services.AddScoped(_ => Mock.Of<IAccountingHealthService>());
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
    public async Task OverlayClose_PropagatesOpenChangedFalse_AndStaysClosedOnParentRerender()
    {
        bool isOpen = true;
        var report = new AccountingHealthReport();

        var cut = Render<AccountingHealthDrawer>(parameters => parameters
            .Add(p => p.Open, isOpen)
            .Add(p => p.OpenChanged, v => isOpen = v)
            .Add(p => p.Report, report));

        var drawer = cut.FindComponent<MudDrawer>();
        Assert.True(drawer.Instance.GetState(x => x.Open));

        var overlay = cut.Find(".mud-overlay");
        await overlay.ClickAsync(new MouseEventArgs());

        Assert.False(isOpen);

        // Simulate parent (Accounting.razor) re-rendering when switching tabs
        cut.Render(parameters => parameters
            .Add(p => p.Open, isOpen)
            .Add(p => p.OpenChanged, v => isOpen = v)
            .Add(p => p.Report, report));

        Assert.False(cut.FindComponent<MudDrawer>().Instance.GetState(x => x.Open));
    }

    [Fact]
    public async Task CloseButton_PropagatesOpenChangedFalse_AndStaysClosedOnParentRerender()
    {
        bool isOpen = true;
        var report = new AccountingHealthReport();

        var cut = Render<AccountingHealthDrawer>(parameters => parameters
            .Add(p => p.Open, isOpen)
            .Add(p => p.OpenChanged, v => isOpen = v)
            .Add(p => p.Report, report));

        var closeButton = cut.Find("button[title='Close']");
        await closeButton.ClickAsync(new MouseEventArgs());

        Assert.False(isOpen);

        cut.Render(parameters => parameters
            .Add(p => p.Open, isOpen)
            .Add(p => p.OpenChanged, v => isOpen = v)
            .Add(p => p.Report, report));

        Assert.False(cut.FindComponent<MudDrawer>().Instance.GetState(x => x.Open));
    }
}
