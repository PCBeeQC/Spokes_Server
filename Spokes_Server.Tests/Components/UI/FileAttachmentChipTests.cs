using Bunit;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;
using Spokes_Server.Components.Shared;
using Spokes_Server.Core.Services.Communication.Presence;
using Spokes_Server.Core.Services.Security;
using Xunit;

namespace Spokes_Server.Tests.Components.UI;

public class FileAttachmentChipTests : BunitContext
{
    public FileAttachmentChipTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddMudServices();
        Services.AddLocalization();
        Services.AddMemoryCache();
        Services.AddDataProtection().UseEphemeralDataProtectionProvider();
        Services.AddSingleton<FileTokenService>();
        Services.AddScoped<UserCircuitContext>();
        Services.AddScoped<FileUrlSigner>();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
        base.Dispose(disposing);
    }

    [Theory]
    [InlineData("Uploading...", 42.0, "(42%)", "42% (42%)")]
    [InlineData("Compressing...", 75.0, "(75%)", "75% (75%)")]
    [InlineData("Downloading from iCloud...", 15.0, "(15%)", "15% (15%)")]
    [InlineData("Finishing up...", 100.0, "(100%)", null)]
    public void FileAttachmentChip_WhenUploading_DisplaysStatusAndPercentage(string statusText, double progress, string expectedPercent, string? forbiddenDuplicate)
    {
        var cut = Render<FileAttachmentChip>(parameters => parameters
            .Add(p => p.IsUploading, true)
            .Add(p => p.FileName, statusText)
            .Add(p => p.UploadProgress, progress));

        var markup = cut.Markup;
        Assert.Contains(statusText, markup);
        Assert.Contains(expectedPercent, markup);
        if (forbiddenDuplicate != null)
        {
            Assert.DoesNotContain(forbiddenDuplicate, markup);
        }
    }

    [Fact]
    public void FileAttachmentChip_WhenUploading_Indeterminate_DisplaysStatusWithoutPercentage()
    {
        // Act
        var cut = Render<FileAttachmentChip>(parameters => parameters
            .Add(p => p.IsUploading, true)
            .Add(p => p.FileName, "Preparing...")
            .Add(p => p.UploadProgress, -1.0));

        // Assert: status is displayed, but no percentage in parentheses
        var markup = cut.Markup;
        Assert.Contains("Preparing...", markup);
        Assert.DoesNotContain("Preparing... (Preparing...)", markup);
        Assert.DoesNotContain("(-1%)", markup);
        Assert.DoesNotContain("()", markup);
    }

    [Fact]
    public void FileAttachmentChip_WhenUploading_WithStageText_PrefersStageTextAndShowsPercentage()
    {
        // Act
        var cut = Render<FileAttachmentChip>(parameters => parameters
            .Add(p => p.IsUploading, true)
            .Add(p => p.StageText, "Compressing...")
            .Add(p => p.FileName, "Uploading...")
            .Add(p => p.UploadProgress, 50.0));

        // Assert: uses StageText as the main label, and percentage in parentheses
        var markup = cut.Markup;
        Assert.Contains("Compressing...", markup);
        Assert.Contains("(50%)", markup);
        Assert.DoesNotContain("Uploading... (Compressing...)", markup);
        Assert.DoesNotContain("Uploading... (Uploading...)", markup);
    }

    [Fact]
    public void FileAttachmentChip_WhenNotUploading_DisplaysFileNameAndFormattedSize()
    {
        // Act
        var cut = Render<FileAttachmentChip>(parameters => parameters
            .Add(p => p.IsUploading, false)
            .Add(p => p.FileName, "document.pdf")
            .Add(p => p.FileSizeBytes, 1048576));

        // Assert: normal display with native title attribute and zero MudTooltip popover wrappers
        var markup = cut.Markup;
        Assert.Contains("document.pdf", markup);
        Assert.Contains("MB", markup);
        Assert.Empty(cut.FindAll(".mud-tooltip-root"));
        var chip = cut.Find(".file-attachment-chip");
        Assert.Contains("document.pdf", chip.GetAttribute("title") ?? string.Empty);
    }

    [Fact]
    public void FileAttachmentChip_TransitionFromUploadingToCompleted_KeepsUnifiedChipWithoutTooltip()
    {
        var cut = Render<FileAttachmentChip>(parameters => parameters
            .Add(p => p.IsUploading, true)
            .Add(p => p.FileName, "Uploading...")
            .Add(p => p.UploadProgress, 50.0));

        Assert.Contains("uploading", cut.Find(".file-attachment-chip").ClassList);
        Assert.Null(cut.Find(".file-attachment-chip").GetAttribute("title"));

        cut.Render(parameters => parameters
            .Add(p => p.IsUploading, false)
            .Add(p => p.FileName, "photo.jpg")
            .Add(p => p.FileSizeBytes, 2048));

        var chip = cut.Find(".file-attachment-chip");
        Assert.DoesNotContain("uploading", chip.ClassList);
        Assert.Contains("photo.jpg", chip.GetAttribute("title") ?? string.Empty);
        Assert.Empty(cut.FindAll(".mud-tooltip-root"));
    }
}
