namespace Spokes_Server.Tests.Core.Configuration;

using System.Text.Json;
using Spokes_Server.Core.Configuration;
using Spokes_Server.Core.Models.Core;

public class MediaCompressionConfigTests
{
    [Fact]
    public void AlbumConfig_Defaults_AreSetCorrectly()
    {
        var config = new AlbumConfig();

        Assert.Equal(3840, config.MobilePhotoMaxDimension);
        Assert.Equal(90, config.MobilePhotoQuality);
        Assert.Equal(0, config.MobileVideoMaxDimension);
        Assert.Equal(0, config.MobileVideoBitrate);

        Assert.Equal(3840, config.DesktopPhotoMaxDimension);
        Assert.Equal(90, config.DesktopPhotoQuality);
    }

    [Fact]
    public void ChatConfig_MediaDefaults_AreSetCorrectly()
    {
        var config = new ChatConfig();

        Assert.Equal(1920, config.MobilePhotoMaxDimension);
        Assert.Equal(80, config.MobilePhotoQuality);
        Assert.Equal(1080, config.MobileVideoMaxDimension);
        Assert.Equal(5_000_000, config.MobileVideoBitrate);

        Assert.Equal(3840, config.DesktopPhotoMaxDimension);
        Assert.Equal(85, config.DesktopPhotoQuality);
    }

    [Fact]
    public void CompanyProfile_AlbumsAndChatMediaConfig_RoundTripsThroughJson()
    {
        var profile = new CompanyProfile();
        profile.Albums.MobilePhotoMaxDimension = 0; // Original
        profile.Albums.MobilePhotoQuality = 95;
        profile.Albums.MobileVideoMaxDimension = 2160;
        profile.Albums.MobileVideoBitrate = 15_000_000;
        profile.Albums.DesktopPhotoMaxDimension = 0;
        profile.Albums.DesktopPhotoQuality = 95;

        profile.Chat.MobilePhotoMaxDimension = 1280;
        profile.Chat.MobilePhotoQuality = 70;
        profile.Chat.MobileVideoMaxDimension = 720;
        profile.Chat.MobileVideoBitrate = 2_500_000;
        profile.Chat.DesktopPhotoMaxDimension = 2560;
        profile.Chat.DesktopPhotoQuality = 80;

        var json = JsonSerializer.Serialize(profile);
        var restored = JsonSerializer.Deserialize<CompanyProfile>(json);

        Assert.NotNull(restored);
        Assert.NotNull(restored.Albums);
        Assert.Equal(0, restored.Albums.MobilePhotoMaxDimension);
        Assert.Equal(95, restored.Albums.MobilePhotoQuality);
        Assert.Equal(2160, restored.Albums.MobileVideoMaxDimension);
        Assert.Equal(15_000_000, restored.Albums.MobileVideoBitrate);
        Assert.Equal(0, restored.Albums.DesktopPhotoMaxDimension);
        Assert.Equal(95, restored.Albums.DesktopPhotoQuality);

        Assert.Equal(1280, restored.Chat.MobilePhotoMaxDimension);
        Assert.Equal(70, restored.Chat.MobilePhotoQuality);
        Assert.Equal(720, restored.Chat.MobileVideoMaxDimension);
        Assert.Equal(2_500_000, restored.Chat.MobileVideoBitrate);
        Assert.Equal(2560, restored.Chat.DesktopPhotoMaxDimension);
        Assert.Equal(80, restored.Chat.DesktopPhotoQuality);
    }
}
