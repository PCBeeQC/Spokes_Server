using MudBlazor;
using Spokes_Server.Components.UI;
using Xunit;

namespace Spokes_Server.Tests.Components.UI;

public class FileUiHelperTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void GetFileIcon_WhenFileNameIsNullOrEmpty_ReturnsInsertDriveFile(string? fileName)
    {
        var icon = FileUiHelper.GetFileIcon(fileName!);
        Assert.Equal(Icons.Material.Filled.InsertDriveFile, icon);
    }

    [Theory]
    [InlineData("photo.jpg")]
    [InlineData("image.jpeg")]
    [InlineData("pic.png")]
    [InlineData("animation.gif")]
    [InlineData("modern.webp")]
    [InlineData("bitmap.bmp")]
    [InlineData("PHOTO.JPG")]
    [InlineData("IMAGE.PNG")]
    public void GetFileIcon_WhenExtensionIsImage_ReturnsImage(string fileName)
    {
        var icon = FileUiHelper.GetFileIcon(fileName);
        Assert.Equal(Icons.Material.Filled.Image, icon);
    }

    [Theory]
    [InlineData("document.pdf")]
    [InlineData("REPORT.PDF")]
    public void GetFileIcon_WhenExtensionIsPdf_ReturnsPictureAsPdf(string fileName)
    {
        var icon = FileUiHelper.GetFileIcon(fileName);
        Assert.Equal(Icons.Material.Filled.PictureAsPdf, icon);
    }

    [Theory]
    [InlineData("document.doc")]
    [InlineData("report.docx")]
    [InlineData("DOCUMENT.DOCX")]
    public void GetFileIcon_WhenExtensionIsWord_ReturnsDescription(string fileName)
    {
        var icon = FileUiHelper.GetFileIcon(fileName);
        Assert.Equal(Icons.Material.Filled.Description, icon);
    }

    [Theory]
    [InlineData("sheet.xls")]
    [InlineData("budget.xlsx")]
    [InlineData("DATA.XLSX")]
    public void GetFileIcon_WhenExtensionIsExcel_ReturnsTableChart(string fileName)
    {
        var icon = FileUiHelper.GetFileIcon(fileName);
        Assert.Equal(Icons.Material.Filled.TableChart, icon);
    }

    [Theory]
    [InlineData("archive.zip")]
    [InlineData("compressed.rar")]
    [InlineData("backup.7z")]
    [InlineData("FILES.ZIP")]
    public void GetFileIcon_WhenExtensionIsArchive_ReturnsFolderZip(string fileName)
    {
        var icon = FileUiHelper.GetFileIcon(fileName);
        Assert.Equal(Icons.Material.Filled.FolderZip, icon);
    }

    [Theory]
    [InlineData("movie.mp4")]
    [InlineData("clip.webm")]
    [InlineData("recording.ogg")]
    [InlineData("film.mov")]
    [InlineData("VIDEO.MP4")]
    public void GetFileIcon_WhenExtensionIsVideo_ReturnsVideoFile(string fileName)
    {
        var icon = FileUiHelper.GetFileIcon(fileName);
        Assert.Equal(Icons.Material.Filled.VideoFile, icon);
    }

    [Theory]
    [InlineData("readme.txt")]
    [InlineData("NOTES.TXT")]
    public void GetFileIcon_WhenExtensionIsText_ReturnsTextSnippet(string fileName)
    {
        var icon = FileUiHelper.GetFileIcon(fileName);
        Assert.Equal(Icons.Material.Filled.TextSnippet, icon);
    }

    [Theory]
    [InlineData("unknown.xyz")]
    [InlineData("script.sh")]
    [InlineData("program.exe")]
    [InlineData("fileWithoutExtension")]
    public void GetFileIcon_WhenExtensionIsUnknown_ReturnsInsertDriveFile(string fileName)
    {
        var icon = FileUiHelper.GetFileIcon(fileName);
        Assert.Equal(Icons.Material.Filled.InsertDriveFile, icon);
    }
}
