using MudBlazor;
using Spokes_Server.Core.Helpers;

namespace Spokes_Server.Tests.Core.Helpers;

public class FileHelperTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void IsImageFile_WhenFileNameIsNullOrEmpty_ReturnsFalse(string? fileName)
    {
        var result = FileHelper.IsImageFile(fileName!);
        Assert.False(result);
    }

    [Theory]
    [InlineData("photo.jpg")]
    [InlineData("PHOTO.JPEG")]
    [InlineData("pic.Png")]
    [InlineData("test.gif")]
    [InlineData("graphic.WEBP")]
    [InlineData("image.Bmp")]
    [InlineData("path/to/nested/file.jpg")]
    public void IsImageFile_WhenExtensionIsValidImage_ReturnsTrue(string fileName)
    {
        var result = FileHelper.IsImageFile(fileName);
        Assert.True(result);
    }

    [Theory]
    [InlineData("document.pdf")]
    [InlineData("notes.txt")]
    [InlineData("video.mp4")]
    [InlineData("audio.mp3")]
    [InlineData("archive.zip")]
    [InlineData("fileWithoutExtension")]
    public void IsImageFile_WhenExtensionIsNotImage_ReturnsFalse(string fileName)
    {
        var result = FileHelper.IsImageFile(fileName);
        Assert.False(result);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void IsVideoFile_WhenFileNameIsNullOrEmpty_ReturnsFalse(string? fileName)
    {
        var result = FileHelper.IsVideoFile(fileName!);
        Assert.False(result);
    }

    [Theory]
    [InlineData("clip.mp4")]
    [InlineData("clip.MP4")]
    [InlineData("movie.webm")]
    [InlineData("stream.mov")]
    [InlineData("video.ogg")]
    [InlineData("sample.avi")]
    [InlineData("recording.mkv")]
    [InlineData("path/to/movie.MKV")]
    public void IsVideoFile_WhenExtensionIsValidVideo_ReturnsTrue(string fileName)
    {
        var result = FileHelper.IsVideoFile(fileName);
        Assert.True(result);
    }

    [Theory]
    [InlineData("document.pdf")]
    [InlineData("photo.jpg")]
    [InlineData("audio.mp3")]
    [InlineData("notes.txt")]
    [InlineData("fileWithoutExtension")]
    public void IsVideoFile_WhenExtensionIsNotVideo_ReturnsFalse(string fileName)
    {
        var result = FileHelper.IsVideoFile(fileName);
        Assert.False(result);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void IsAudioFile_WhenFileNameIsNullOrEmpty_ReturnsFalse(string? fileName)
    {
        var result = FileHelper.IsAudioFile(fileName!);
        Assert.False(result);
    }

    [Theory]
    [InlineData("song.mp3")]
    [InlineData("audio.WAV")]
    [InlineData("sound.webm")]
    [InlineData("track.weba")]
    [InlineData("podcast.ogg")]
    [InlineData("voice.m4a")]
    [InlineData("music.aac")]
    [InlineData("path/to/track.AAC")]
    public void IsAudioFile_WhenExtensionIsValidAudio_ReturnsTrue(string fileName)
    {
        var result = FileHelper.IsAudioFile(fileName);
        Assert.True(result);
    }

    [Theory]
    [InlineData("document.pdf")]
    [InlineData("photo.jpg")]
    [InlineData("video.mp4")]
    [InlineData("notes.txt")]
    [InlineData("archive.zip")]
    [InlineData("fileWithoutExtension")]
    public void IsAudioFile_WhenExtensionIsNotAudio_ReturnsFalse(string fileName)
    {
        var result = FileHelper.IsAudioFile(fileName);
        Assert.False(result);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void GetFileIcon_WhenFileNameIsNullOrEmpty_ReturnsInsertDriveFile(string? fileName)
    {
        var icon = FileHelper.GetFileIcon(fileName!);
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
        var icon = FileHelper.GetFileIcon(fileName);
        Assert.Equal(Icons.Material.Filled.Image, icon);
    }

    [Theory]
    [InlineData("document.pdf")]
    [InlineData("REPORT.PDF")]
    public void GetFileIcon_WhenExtensionIsPdf_ReturnsPictureAsPdf(string fileName)
    {
        var icon = FileHelper.GetFileIcon(fileName);
        Assert.Equal(Icons.Material.Filled.PictureAsPdf, icon);
    }

    [Theory]
    [InlineData("document.doc")]
    [InlineData("report.docx")]
    [InlineData("DOCUMENT.DOCX")]
    public void GetFileIcon_WhenExtensionIsWord_ReturnsDescription(string fileName)
    {
        var icon = FileHelper.GetFileIcon(fileName);
        Assert.Equal(Icons.Material.Filled.Description, icon);
    }

    [Theory]
    [InlineData("sheet.xls")]
    [InlineData("budget.xlsx")]
    [InlineData("DATA.XLSX")]
    public void GetFileIcon_WhenExtensionIsExcel_ReturnsTableChart(string fileName)
    {
        var icon = FileHelper.GetFileIcon(fileName);
        Assert.Equal(Icons.Material.Filled.TableChart, icon);
    }

    [Theory]
    [InlineData("archive.zip")]
    [InlineData("compressed.rar")]
    [InlineData("backup.7z")]
    [InlineData("FILES.ZIP")]
    public void GetFileIcon_WhenExtensionIsArchive_ReturnsFolderZip(string fileName)
    {
        var icon = FileHelper.GetFileIcon(fileName);
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
        var icon = FileHelper.GetFileIcon(fileName);
        Assert.Equal(Icons.Material.Filled.VideoFile, icon);
    }

    [Theory]
    [InlineData("readme.txt")]
    [InlineData("NOTES.TXT")]
    public void GetFileIcon_WhenExtensionIsText_ReturnsTextSnippet(string fileName)
    {
        var icon = FileHelper.GetFileIcon(fileName);
        Assert.Equal(Icons.Material.Filled.TextSnippet, icon);
    }

    [Theory]
    [InlineData("unknown.xyz")]
    [InlineData("script.sh")]
    [InlineData("program.exe")]
    [InlineData("fileWithoutExtension")]
    public void GetFileIcon_WhenExtensionIsUnknown_ReturnsInsertDriveFile(string fileName)
    {
        var icon = FileHelper.GetFileIcon(fileName);
        Assert.Equal(Icons.Material.Filled.InsertDriveFile, icon);
    }
}
