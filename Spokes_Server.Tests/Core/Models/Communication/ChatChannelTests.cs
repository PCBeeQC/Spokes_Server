using Spokes_Server.Core.Models.Communication;

namespace Spokes_Server.Tests.Core.Models.Communication;

public class ChatChannelTests
{
    [Theory]
    [InlineData(ChatChannelType.Direct, false)]
    [InlineData(ChatChannelType.General, true)]
    [InlineData(ChatChannelType.Team, true)]
    [InlineData(ChatChannelType.Project, true)]
    [InlineData(ChatChannelType.Group, true)]
    [InlineData("CustomChannelType", true)]
    public void SupportsAnnouncements_ReturnsExpectedValue_BasedOnChannelType(string channelType, bool expected)
    {
        var channel = new ChatChannel { ChannelType = channelType };

        Assert.Equal(expected, channel.SupportsAnnouncements);
    }

    [Fact]
    public void Normalize_DirectChannel_ResetsAnnouncementSettings()
    {
        var channel = new ChatChannel
        {
            ChannelType = ChatChannelType.Direct,
            IsAnnouncementOnly = true,
            AllowedPostUserIds = new List<string> { "user-1", "user-2" },
            AllowedPostTeamIds = new List<string> { "team-1" }
        };

        channel.Normalize();

        Assert.False(channel.IsAnnouncementOnly);
        Assert.Empty(channel.AllowedPostUserIds);
        Assert.Empty(channel.AllowedPostTeamIds);
    }

    [Theory]
    [InlineData(ChatChannelType.General)]
    [InlineData(ChatChannelType.Team)]
    [InlineData(ChatChannelType.Project)]
    [InlineData(ChatChannelType.Group)]
    [InlineData("CustomType")]
    public void Normalize_NonDirectChannel_RetainsAnnouncementSettings(string channelType)
    {
        var channel = new ChatChannel
        {
            ChannelType = channelType,
            IsAnnouncementOnly = true,
            AllowedPostUserIds = new List<string> { "user-1", "user-2" },
            AllowedPostTeamIds = new List<string> { "team-1" }
        };

        channel.Normalize();

        Assert.True(channel.IsAnnouncementOnly);
        Assert.Equal(new[] { "user-1", "user-2" }, channel.AllowedPostUserIds);
        Assert.Equal(new[] { "team-1" }, channel.AllowedPostTeamIds);
    }

    [Fact]
    public void Normalize_DirectChannelWithNullLists_DoesNotThrow()
    {
        var channel = new ChatChannel
        {
            ChannelType = ChatChannelType.Direct,
            IsAnnouncementOnly = true,
            AllowedPostUserIds = null!,
            AllowedPostTeamIds = null!
        };

        var exception = Record.Exception(() => channel.Normalize());

        Assert.Null(exception);
        Assert.False(channel.IsAnnouncementOnly);
    }

    [Fact]
    public void Equals_And_GetHashCode_Behavior()
    {
        var channelId = "same-id-12345";
        var channel1 = new ChatChannel { Id = channelId, Name = "Channel A" };
        var channel2 = new ChatChannel { Id = channelId, Name = "Channel B" };
        var channel3 = new ChatChannel { Id = "id-2" };
        var nullIdChannel = new ChatChannel { Id = null! };

        Assert.True(channel1.Equals(channel1));
        Assert.True(channel1.Equals(channel2));
        Assert.True(channel2.Equals(channel1));
        Assert.Equal(channel1.GetHashCode(), channel2.GetHashCode());
        Assert.False(channel1.Equals(channel3));
        Assert.False(channel1.Equals(null));
        Assert.False(channel1.Equals("string object"));
        Assert.False(channel1.Equals(new object()));
        Assert.Null(Record.Exception(() => nullIdChannel.GetHashCode()));
    }

}
