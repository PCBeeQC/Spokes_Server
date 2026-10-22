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
    [InlineData(null, null, false)]
    [InlineData("", "", false)]
    [InlineData("photo.jpg", null, true)]
    [InlineData("clip.mp4", null, true)]
    [InlineData("doc.pdf", null, false)]
    [InlineData("file.bin", "image/png", true)]
    [InlineData("file.bin", "video/mp4", true)]
    [InlineData("file.bin", "application/pdf", false)]
    [InlineData("file.bin", "text/plain", false)]
    public void IsMediaFile_ValidatesContentTypeAndExtension(string? fileName, string? contentType, bool expected)
    {
        var result = FileHelper.IsMediaFile(fileName, contentType);
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("photo.jpg", null, "image/jpeg")]
    [InlineData("image.png", null, "image/png")]
    [InlineData("video.mp4", null, "video/mp4")]
    [InlineData("document.pdf", null, "application/pdf")]
    [InlineData("custom.unknown", "custom/mime", "custom/mime")]
    [InlineData("custom.unknown", null, "application/octet-stream")]
    public void GetContentType_ResolvesAccurately(string fileName, string? fallback, string expected)
    {
        var result = FileHelper.GetContentType(fileName, fallback);
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("photo.jpg", null, MudBlazor.Icons.Material.Filled.Image)]
    [InlineData("video.mp4", null, MudBlazor.Icons.Material.Filled.Videocam)]
    [InlineData("audio.mp3", null, MudBlazor.Icons.Material.Filled.AudioFile)]
    [InlineData("doc.pdf", null, MudBlazor.Icons.Material.Filled.PictureAsPdf)]
    [InlineData("sheet.xlsx", null, MudBlazor.Icons.Material.Filled.TableChart)]
    [InlineData("archive.zip", null, MudBlazor.Icons.Material.Filled.FolderZip)]
    [InlineData("file.bin", "image/webp", MudBlazor.Icons.Material.Filled.Image)]
    [InlineData("file.bin", "video/webm", MudBlazor.Icons.Material.Filled.Videocam)]
    [InlineData("other.xyz", null, MudBlazor.Icons.Material.Filled.AttachFile)]
    public void GetFileIcon_ReturnsExpectedIcon(string fileName, string? contentType, string expectedIcon)
    {
        var result = FileHelper.GetFileIcon(fileName, contentType);
        Assert.Equal(expectedIcon, result);
    }
}
