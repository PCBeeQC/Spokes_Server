using Bunit;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;
using Spokes_Server.Components.Shared;
using Xunit;

namespace Spokes_Server.Tests.Components.UI;

public class FileAttachmentChipTests : BunitContext
{
    public FileAttachmentChipTests()
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
    public void FileAttachmentChip_WhenUploading_DisplaysStatusAndPercentage()
    {
        // Act
        var cut = Render<FileAttachmentChip>(parameters => parameters
            .Add(p => p.IsUploading, true)
            .Add(p => p.FileName, "Uploading...")
            .Add(p => p.UploadProgress, 42.0));

        // Assert: must render "Uploading... (42%)" and NOT "Uploading... 42% (42%)"
        var markup = cut.Markup;
        Assert.Contains("Uploading...", markup);
        Assert.Contains("(42%)", markup);
        Assert.DoesNotContain("42% (42%)", markup);
    }

    [Fact]
    public void FileAttachmentChip_WhenUploading_WithCompressingStatus_DisplaysCompressingAndPercentage()
    {
        // Act
        var cut = Render<FileAttachmentChip>(parameters => parameters
            .Add(p => p.IsUploading, true)
            .Add(p => p.FileName, "Compressing...")
            .Add(p => p.UploadProgress, 75.0));

        // Assert: must render "Compressing... (75%)"
        var markup = cut.Markup;
        Assert.Contains("Compressing...", markup);
        Assert.Contains("(75%)", markup);
        Assert.DoesNotContain("75% (75%)", markup);
    }

    [Fact]
    public void FileAttachmentChip_WhenUploading_WithDownloadingFromICloud_DisplaysStatusAndPercentage()
    {
        // Act
        var cut = Render<FileAttachmentChip>(parameters => parameters
            .Add(p => p.IsUploading, true)
            .Add(p => p.FileName, "Downloading from iCloud...")
            .Add(p => p.UploadProgress, 15.0));

        // Assert
        var markup = cut.Markup;
        Assert.Contains("Downloading from iCloud...", markup);
        Assert.Contains("(15%)", markup);
        Assert.DoesNotContain("15% (15%)", markup);
    }

    [Fact]
    public void FileAttachmentChip_WhenUploading_WithFinishingUp_DisplaysStatusAndPercentage()
    {
        // Act
        var cut = Render<FileAttachmentChip>(parameters => parameters
            .Add(p => p.IsUploading, true)
            .Add(p => p.FileName, "Finishing up...")
            .Add(p => p.UploadProgress, 100.0));

        // Assert
        var markup = cut.Markup;
        Assert.Contains("Finishing up...", markup);
        Assert.Contains("(100%)", markup);
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

        // Assert: normal display
        var markup = cut.Markup;
        Assert.Contains("document.pdf", markup);
        Assert.Contains("MB", markup);
    }
}
