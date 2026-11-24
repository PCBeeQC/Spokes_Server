using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using MudBlazor.Services;
using Spokes_Server.Components.Dialogs.Common;
using Spokes_Server.Components.Shared;
using Spokes_Server.Components.UI;
using Spokes_Server.Core.Services.Maintenance;
using System.Text.Json;

namespace Spokes_Server.Tests.Components.Shared;

public class SpokesDialogProviderTests : BunitContext
{
    public SpokesDialogProviderTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddMudServices();
        Services.AddLocalization();
        Services.AddLogging();
        Services.AddScoped<StartupTimingService>();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
        base.Dispose(disposing);
    }

    private IRenderedComponent<SpokesDialogProvider> RenderHost()
    {
        Render<MudPopoverProvider>();
        return Render<SpokesDialogProvider>();
    }

    private static JsonElement SerializeArg(object? arg) =>
        JsonSerializer.SerializeToElement(arg);

    /// <summary>
    /// Polls JSInterop.Invocations directly rather than relying on bUnit's render-driven WaitForAssertion,
    /// because SpokesDialogProvider unregisters dialogs via InvokeAsync + Task.Yield() without triggering
    /// an extra component re-render.
    /// </summary>
    private async Task<List<JSRuntimeInvocation>> WaitForJsInvocationsAsync(
        Func<JSRuntimeInvocation, bool> predicate,
        int timeoutMs = 5000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (true)
        {
            List<JSRuntimeInvocation> matches;
            try
            {
                matches = JSInterop.Invocations.Where(predicate).ToList();
            }
            catch (InvalidOperationException)
            {
                // bUnit's JSRuntimeInvocationDictionary can be mutated concurrently on the renderer thread while polling
                await Task.Delay(10);
                continue;
            }
            if (matches.Count > 0 || DateTime.UtcNow >= deadline)
            {
                return matches;
            }
            await Task.Delay(10);
        }
    }

    [Fact]
    public async Task MediaViewerDialog_HeaderlessNoCloseButton_RegistersOnBackStack_AndClosesOnBack()
    {
        var provider = RenderHost();
        var dialogService = Services.GetRequiredService<IDialogService>();

        var parameters = new DialogParameters
        {
            {
                nameof(MediaViewerDialog.MediaList),
                new List<LightboxMediaItem>
                {
                    new()
                    {
                        Id = "img-1",
                        FileName = "photo.jpg",
                        FilePath = "/spokesapi/files/chat/ch-1/photo.jpg?t=tok",
                        ContentType = "image/jpeg"
                    }
                }
            },
            { nameof(MediaViewerDialog.InitialIndex), 0 }
        };
        var options = new DialogOptions
        {
            MaxWidth = MaxWidth.False,
            FullWidth = true,
            NoHeader = true,
            CloseButton = false
        };

        var dialogRef = await provider.InvokeAsync(() =>
            dialogService.ShowAsync<MediaViewerDialog>("Media Lightbox", parameters, options));

        var expectedKey = $"dialog:{dialogRef.Id:N}";
        var registerCall = JSInterop.Invocations.Last(i =>
            i.Identifier == "SpokesBridge.backStack.registerServerEntry" &&
            Equals(i.Arguments[0], expectedKey));

        Assert.Equal(nameof(SpokesDialogProvider.HandleBackForDialogAsync), registerCall.Arguments[2]);
        var regOpts = SerializeArg(registerCall.Arguments[3]);
        Assert.Equal(dialogRef.Id.ToString("N"), regOpts.GetProperty("arg").GetString());
        Assert.True(regOpts.GetProperty("allowDismiss").GetBoolean());
        Assert.True(regOpts.GetProperty("useHistoryGuard").GetBoolean());

        // Simulate Android back gesture invoking SpokesDialogProvider.HandleBackForDialogAsync
        var accepted = await provider.InvokeAsync(() =>
            provider.Instance.HandleBackForDialogAsync(dialogRef.Id.ToString("N")));

        Assert.True(accepted);
        var result = await dialogRef.Result;
        Assert.NotNull(result);
        Assert.True(result!.Canceled);

        // Verify dialog was unregistered with popHistory = false (closed by back)
        var removeMatches = await WaitForJsInvocationsAsync(i =>
            i.Identifier == "SpokesBridge.backStack.remove" &&
            Equals(i.Arguments[0], expectedKey));
        Assert.NotEmpty(removeMatches);
        var removeOpts = SerializeArg(removeMatches[^1].Arguments[1]);
        Assert.False(removeOpts.GetProperty("popHistory").GetBoolean());
    }

    [Fact]
    public async Task StackedDialogs_BackClosesTopmostFirst_LeavingUnderlyingMediaViewerOpen()
    {
        var provider = RenderHost();
        var dialogService = Services.GetRequiredService<IDialogService>();

        var mediaRef = await provider.InvokeAsync(() =>
            dialogService.ShowAsync<MediaViewerDialog>(
                "Media Lightbox",
                new DialogParameters
                {
                    {
                        nameof(MediaViewerDialog.MediaList),
                        new List<LightboxMediaItem>
                        {
                            new()
                            {
                                Id = "img-1",
                                FileName = "photo.jpg",
                                FilePath = "/spokesapi/files/chat/ch-1/photo.jpg?t=tok",
                                ContentType = "image/jpeg"
                            }
                        }
                    }
                },
                new DialogOptions { NoHeader = true, CloseButton = false }));

        var childRef = await provider.InvokeAsync(() =>
            dialogService.ShowAsync<ConfirmDialog>(
                "Confirm",
                new DialogParameters<ConfirmDialog> { { x => x.ContentText, "Topmost dialog" } },
                new DialogOptions { CloseButton = true }));

        // Back #1 targets topmost childRef
        var firstAccepted = await provider.InvokeAsync(() =>
            provider.Instance.HandleBackForDialogAsync(childRef.Id.ToString("N")));
        Assert.True(firstAccepted);
        var childResult = await childRef.Result;
        Assert.True(childResult!.Canceled);
        Assert.False(mediaRef.Result.IsCompleted);

        // Back #2 targets underlying mediaRef
        var secondAccepted = await provider.InvokeAsync(() =>
            provider.Instance.HandleBackForDialogAsync(mediaRef.Id.ToString("N")));
        Assert.True(secondAccepted);
        var mediaResult = await mediaRef.Result;
        Assert.True(mediaResult!.Canceled);
    }

    [Fact]
    public async Task MessageBox_WithNoHeaderCloseButton_ClosesCleanlyOnBack()
    {
        var provider = RenderHost();
        var dialogService = Services.GetRequiredService<IDialogService>();

        Task<bool?> msgBoxTask = null!;
        await provider.InvokeAsync(() =>
        {
            msgBoxTask = dialogService.ShowMessageBoxAsync("Delete Message", "Are you sure?", yesText: "Delete", cancelText: "Cancel");
        });

        var registerMatches = await WaitForJsInvocationsAsync(i =>
            i.Identifier == "SpokesBridge.backStack.registerServerEntry");
        Assert.NotEmpty(registerMatches);
        var registeredDialogId = SerializeArg(registerMatches[^1].Arguments[3]).GetProperty("arg").GetString();
        Assert.False(string.IsNullOrWhiteSpace(registeredDialogId));

        var accepted = await provider.InvokeAsync(() =>
            provider.Instance.HandleBackForDialogAsync(registeredDialogId!));

        Assert.True(accepted);
        var boxResult = await msgBoxTask;
        Assert.Null(boxResult);
    }

    [Fact]
    public async Task BlockingModal_CloseOnEscapeFalseOrBackdropClickFalse_RefusesBackDismissal()
    {
        var provider = RenderHost();
        var dialogService = Services.GetRequiredService<IDialogService>();

        var blockingRef = await provider.InvokeAsync(() =>
            dialogService.ShowAsync<ConfirmDialog>(
                "Blocking",
                new DialogParameters<ConfirmDialog> { { x => x.ContentText, "Cannot dismiss" } },
                new DialogOptions { BackdropClick = false, CloseOnEscapeKey = false }));

        var expectedKey = $"dialog:{blockingRef.Id:N}";
        var regCall = JSInterop.Invocations.Last(i =>
            i.Identifier == "SpokesBridge.backStack.registerServerEntry" &&
            Equals(i.Arguments[0], expectedKey));
        var regOpts = SerializeArg(regCall.Arguments[3]);
        Assert.False(regOpts.GetProperty("allowDismiss").GetBoolean());
        Assert.False(regOpts.GetProperty("useHistoryGuard").GetBoolean());

        var accepted = await provider.InvokeAsync(() =>
            provider.Instance.HandleBackForDialogAsync(blockingRef.Id.ToString("N")));

        Assert.False(accepted);
        Assert.False(blockingRef.Result.IsCompleted);

        // BackdropClick = false alone (without CloseOnEscapeKey = true) is also blocking
        Assert.False(SpokesDialogProvider.IsDismissibleOnBack(new DialogOptions { BackdropClick = false }));
        // BackdropClick = false WITH CloseOnEscapeKey = true (e.g. MigrationReviewDialog) IS dismissible
        Assert.True(SpokesDialogProvider.IsDismissibleOnBack(new DialogOptions { BackdropClick = false, CloseOnEscapeKey = true }));
    }

    [Fact]
    public async Task ConfirmDialog_BlocksBackWhileProcessing_AndCancelsWhenIdle()
    {
        var provider = RenderHost();
        var dialogService = Services.GetRequiredService<IDialogService>();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var dialogRef = await provider.InvokeAsync(() =>
            dialogService.ShowAsync<ConfirmDialog>(
                "Delete",
                new DialogParameters<ConfirmDialog>
                {
                    { x => x.ContentText, "Deleting..." },
                    { x => x.ButtonText, "Delete" },
                    { x => x.OnConfirmAsync, () => gate.Task }
                }));

        // Start processing by clicking Confirm asynchronously so the Blazor renderer dispatcher is not blocked waiting on gate.Task
        var confirmBtn = provider.FindAll("button").First(b => b.TextContent.Contains("Delete", StringComparison.Ordinal));
        var clickTask = confirmBtn.ClickAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());

        // While _isProcessing == true, Back must be blocked by ConfirmDialog's IBackDismissHandler
        provider.WaitForAssertion(() => Assert.NotEmpty(provider.FindAll(".mud-progress-circular")), TimeSpan.FromSeconds(5));
        var blockedDuringWork = await provider.InvokeAsync(() =>
            provider.Instance.HandleBackForDialogAsync(dialogRef.Id.ToString("N")));
        Assert.False(blockedDuringWork);
        Assert.False(dialogRef.Result.IsCompleted);

        gate.SetResult();
        await clickTask;
        var result = await dialogRef.Result;
        Assert.False(result!.Canceled);
    }

    [Fact]
    public async Task UiCloseFollowedImmediatelyByNavigation_UnregistersWithPopHistoryFalse()
    {
        var provider = RenderHost();
        var dialogService = Services.GetRequiredService<IDialogService>();
        var nav = Services.GetRequiredService<NavigationManager>();

        var dialogRef = await provider.InvokeAsync(() =>
            dialogService.ShowAsync<ConfirmDialog>(
                "Headerless",
                new DialogParameters<ConfirmDialog> { { x => x.ContentText, "Navigate on close" } },
                new DialogOptions { NoHeader = true, CloseButton = false }));

        var expectedKey = $"dialog:{dialogRef.Id:N}";

        // Simulate GlobalSearchDialog.NavigateTo:
        // Close dialog AND navigate synchronously on the next line.
        await provider.InvokeAsync(() =>
        {
            dialogRef.Close(DialogResult.Ok(true));
            nav.NavigateTo("/chat/destination-channel");
        });

        var removeMatches = await WaitForJsInvocationsAsync(i =>
            i.Identifier == "SpokesBridge.backStack.remove" &&
            Equals(i.Arguments[0], expectedKey));
        Assert.NotEmpty(removeMatches);
        var removeOpts = SerializeArg(removeMatches[^1].Arguments[1]);
        Assert.False(removeOpts.GetProperty("popHistory").GetBoolean());
    }

    [Fact]
    public async Task DialogAdded_RecordsProfilerSpansOnlyWhenEnabled()
    {
        var provider = RenderHost();
        var dialogService = Services.GetRequiredService<IDialogService>();
        var timings = Services.GetRequiredService<StartupTimingService>();

        // Disabled by default: opening a dialog must create zero transitions
        var firstRef = await provider.InvokeAsync(() =>
            dialogService.ShowAsync<ConfirmDialog>(
                "Silent",
                new DialogParameters<ConfirmDialog> { { x => x.ContentText, "No profiling" } }));
        Assert.Equal(0, timings.CurrentTransitionId);
        await provider.InvokeAsync(() => firstRef.Close());

        // Enabled: opening a dialog records DialogAdded and DialogBackStackRegistered spans
        timings.SetChatProfilerEnabled(true);
        var secondRef = await provider.InvokeAsync(() =>
            dialogService.ShowAsync<ConfirmDialog>(
                "Profiled",
                new DialogParameters<ConfirmDialog> { { x => x.ContentText, "With profiling" } }));

        var tid = timings.CurrentTransitionId;
        Assert.True(tid > 0);
        var expectedKey = $"dialog:{secondRef.Id:N}";
        await WaitForJsInvocationsAsync(i =>
            i.Identifier == "SpokesBridge.backStack.registerServerEntry" &&
            Equals(i.Arguments[0], expectedKey));

        var drained = timings.Drain(tid, 0);
        Assert.Contains(drained.Entries, e => e.Name.Contains("Server: DialogAdded", StringComparison.Ordinal));
        Assert.Contains(drained.Entries, e => e.Name.Contains("Server: DialogBackStackRegistered", StringComparison.Ordinal));

        await provider.InvokeAsync(() => secondRef.Close());
    }
}
