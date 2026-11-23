using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;
using MudBlazor;
using MudBlazor.Services;
using Spokes_Server.Components.Dialogs.Common;
using Spokes_Server.Components.Dialogs.Communication.Chat;
using Spokes_Server.Components.UI;

namespace Spokes_Server.Tests.Components.Dialogs.Common;

public class MediaViewerDialogTests : BunitContext
{
    public MediaViewerDialogTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddMudServices();
        Services.AddLocalization();
        Services.AddLogging();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
        base.Dispose(disposing);
    }

    private async Task<(IRenderedComponent<MudDialogProvider> Provider, IDialogReference Dialog)> OpenMediaViewerAsync(
        List<LightboxMediaItem> items,
        int initialIndex = 0)
    {
        Render<MudPopoverProvider>();
        var provider = Render<MudDialogProvider>();
        var dialogService = Services.GetRequiredService<IDialogService>();

        var parameters = new DialogParameters
        {
            { nameof(MediaViewerDialog.MediaList), items },
            { nameof(MediaViewerDialog.InitialIndex), initialIndex }
        };

        var dialog = await provider.InvokeAsync(() => dialogService.ShowAsync<MediaViewerDialog>("Media Lightbox", parameters));
        return (provider, dialog);
    }

    [Fact]
    public async Task InitialImageRender_HidesUnloadedImageAtZeroOpacity_AndShowsSpinner()
    {
        var items = new List<LightboxMediaItem>
        {
            new()
            {
                Id = "img-1",
                FileName = "vacation.jpg",
                FilePath = "/spokesapi/files/albums/alb-1/vacation.jpg?t=tok1",
                ContentType = "image/jpeg"
            }
        };

        var (provider, _) = await OpenMediaViewerAsync(items);

        var img = provider.Find("img#lightbox-image");
        Assert.Equal("/spokesapi/files/albums/alb-1/vacation.jpg?t=tok1", img.GetAttribute("data-media-src"));
        Assert.Equal("lightbox", img.GetAttribute("data-media-priority"));
        Assert.Null(img.GetAttribute("src"));
        Assert.Contains("lightbox-img-loading", img.ClassList);
        Assert.DoesNotContain("lightbox-img-loaded", img.ClassList);
        Assert.DoesNotContain("lightbox-img-error", img.ClassList);

        Assert.Single(provider.FindAll(".lightbox-spinner"));
        Assert.Empty(provider.FindAll(".lightbox-error-state"));

        // Ensure CSS hides unloaded/errored img completely (never opacity: 0.5 which flashes broken-image icon)
        var styleText = provider.Find("style").TextContent;
        Assert.DoesNotContain("opacity: 0.5", styleText, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(".lightbox-img-loading", styleText, StringComparison.Ordinal);
        Assert.Contains("opacity: 0;", styleText, StringComparison.Ordinal);
        Assert.Contains(".lightbox-img-base[data-loaded=\"true\"]", styleText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OnImageLoaded_TransitionsToLoadedClass_AndRemovesSpinner()
    {
        var items = new List<LightboxMediaItem>
        {
            new()
            {
                Id = "img-1",
                FileName = "photo.png",
                FilePath = "/spokesapi/files/chat/ch-1/photo.png?t=tok1",
                ContentType = "image/png"
            }
        };

        var (provider, _) = await OpenMediaViewerAsync(items);

        await provider.Find("img#lightbox-image").LoadAsync();

        var img = provider.Find("img#lightbox-image");
        Assert.Contains("lightbox-img-loaded", img.ClassList);
        Assert.DoesNotContain("lightbox-img-loading", img.ClassList);
        Assert.DoesNotContain("lightbox-img-error", img.ClassList);
        Assert.Empty(provider.FindAll(".lightbox-spinner"));
        Assert.Empty(provider.FindAll(".lightbox-error-state"));
    }

    [Fact]
    public async Task OnImageError_ShowsCleanErrorFallback_KeepsImgHiddenForRetry_AndRecoversOnLoad()
    {
        var items = new List<LightboxMediaItem>
        {
            new()
            {
                Id = "img-1",
                FileName = "missing.png",
                FilePath = "/spokesapi/files/chat/ch-1/missing.png?t=tok1",
                ContentType = "image/png"
            }
        };

        var (provider, _) = await OpenMediaViewerAsync(items);

        await provider.Find("img#lightbox-image").ErrorAsync();

        var imgAfterError = provider.Find("img#lightbox-image");
        Assert.Contains("lightbox-img-error", imgAfterError.ClassList);
        Assert.DoesNotContain("lightbox-img-loaded", imgAfterError.ClassList);
        Assert.DoesNotContain("lightbox-img-loading", imgAfterError.ClassList);
        Assert.Empty(provider.FindAll(".lightbox-spinner"));
        var errorState = provider.Find(".lightbox-error-state");
        Assert.Contains("Failed to load image.", errorState.TextContent);

        // If mediaLoader.retryBroken() subsequently succeeds after token recovery, onload clears the error state
        await provider.Find("img#lightbox-image").LoadAsync();

        var imgAfterRecovery = provider.Find("img#lightbox-image");
        Assert.Contains("lightbox-img-loaded", imgAfterRecovery.ClassList);
        Assert.DoesNotContain("lightbox-img-error", imgAfterRecovery.ClassList);
        Assert.Empty(provider.FindAll(".lightbox-error-state"));
    }

    [Fact]
    public async Task ChangeImage_ResetsLoadAndErrorStateForNextItem()
    {
        var items = new List<LightboxMediaItem>
        {
            new()
            {
                Id = "img-1",
                FileName = "first.jpg",
                FilePath = "/spokesapi/files/albums/alb-1/first.jpg?t=tok1",
                ContentType = "image/jpeg"
            },
            new()
            {
                Id = "img-2",
                FileName = "second.jpg",
                FilePath = "/spokesapi/files/albums/alb-1/second.jpg?t=tok2",
                ContentType = "image/jpeg"
            }
        };

        var (provider, _) = await OpenMediaViewerAsync(items);
        await provider.Find("img#lightbox-image").ErrorAsync();
        Assert.Single(provider.FindAll(".lightbox-error-state"));

        var dialogComponent = provider.FindComponent<MediaViewerDialog>();
        await dialogComponent.InvokeAsync(() => dialogComponent.Instance.ChangeImage("left"));

        var img = provider.Find("img#lightbox-image");
        Assert.Equal("/spokesapi/files/albums/alb-1/second.jpg?t=tok2", img.GetAttribute("data-media-src"));
        Assert.Contains("lightbox-img-loading", img.ClassList);
        Assert.DoesNotContain("lightbox-img-error", img.ClassList);
        Assert.Single(provider.FindAll(".lightbox-spinner"));
        Assert.Empty(provider.FindAll(".lightbox-error-state"));
    }

    [Fact]
    public async Task EmptyFilePath_EntersErrorStateImmediatelyWithoutSpinning()
    {
        var items = new List<LightboxMediaItem>
        {
            new()
            {
                Id = "img-empty",
                FileName = "empty.jpg",
                FilePath = "",
                ContentType = "image/jpeg"
            }
        };

        var (provider, _) = await OpenMediaViewerAsync(items);

        var img = provider.Find("img#lightbox-image");
        Assert.Contains("lightbox-img-error", img.ClassList);
        Assert.DoesNotContain("lightbox-img-loading", img.ClassList);
        Assert.Empty(provider.FindAll(".lightbox-spinner"));
        Assert.Single(provider.FindAll(".lightbox-error-state"));
    }

    [Fact]
    public async Task ShareButton_InvokesOsShareSheetJs_WithoutOpeningInAppShareModal()
    {
        var items = new List<LightboxMediaItem>
        {
            new()
            {
                Id = "img-1",
                FileName = "vacation.jpg",
                FilePath = "/spokesapi/files/albums/alb-1/vacation.jpg?t=tok1",
                ContentType = "image/jpeg"
            }
        };

        var (provider, _) = await OpenMediaViewerAsync(items);

        var shareButton = provider.FindAll("button")
            .Single(b => b.TextContent.Contains("Share", StringComparison.Ordinal));
        await shareButton.ClickAsync();

        var shareInvocation = Assert.Single(JSInterop.Invocations, i => i.Identifier == "shareFileFromUrl");
        Assert.Equal(2, shareInvocation.Arguments.Count);
        Assert.Equal("vacation.jpg", shareInvocation.Arguments[0]);
        Assert.Equal("/spokesapi/files/albums/alb-1/vacation.jpg?t=tok1", shareInvocation.Arguments[1]);
        Assert.Empty(provider.FindComponents<ShareModal>());
        Assert.Single(provider.FindComponents<MediaViewerDialog>());
    }

    [Fact]
    public async Task ShareButton_AfterNavigatingToNextItem_SharesActiveMedia()
    {
        var items = new List<LightboxMediaItem>
        {
            new()
            {
                Id = "img-1",
                FileName = "first.jpg",
                FilePath = "/spokesapi/files/albums/alb-1/first.jpg?t=tok1",
                ContentType = "image/jpeg"
            },
            new()
            {
                Id = "img-2",
                FileName = "second.mp4",
                FilePath = "/spokesapi/files/albums/alb-1/second.mp4?t=tok2",
                ContentType = "video/mp4"
            }
        };

        var (provider, _) = await OpenMediaViewerAsync(items);
        var dialogComponent = provider.FindComponent<MediaViewerDialog>();
        await dialogComponent.InvokeAsync(() => dialogComponent.Instance.ChangeImage("left"));

        var shareButton = provider.FindAll("button")
            .Single(b => b.TextContent.Contains("Share", StringComparison.Ordinal));
        await shareButton.ClickAsync();

        var shareInvocation = Assert.Single(JSInterop.Invocations, i => i.Identifier == "shareFileFromUrl");
        Assert.Equal("second.mp4", shareInvocation.Arguments[0]);
        Assert.Equal("/spokesapi/files/albums/alb-1/second.mp4?t=tok2", shareInvocation.Arguments[1]);
    }

    [Fact]
    public async Task ShareButton_WhenJsInteropFails_ShowsWarningSnackbar()
    {
        JSInterop.SetupVoid("shareFileFromUrl", _ => true)
            .SetException(new JSException("Share failed"));

        var items = new List<LightboxMediaItem>
        {
            new()
            {
                Id = "img-1",
                FileName = "broken.jpg",
                FilePath = "/spokesapi/files/albums/alb-1/broken.jpg?t=tok1",
                ContentType = "image/jpeg"
            }
        };

        var (provider, _) = await OpenMediaViewerAsync(items);
        var snackbar = Services.GetRequiredService<ISnackbar>();

        var shareButton = provider.FindAll("button")
            .Single(b => b.TextContent.Contains("Share", StringComparison.Ordinal));
        await shareButton.ClickAsync();

        var shown = Assert.Single(snackbar.ShownSnackbars);
        Assert.Equal(Severity.Warning, shown.Severity);
        Assert.Equal("Failed to share media.", shown.Message);
    }
}
