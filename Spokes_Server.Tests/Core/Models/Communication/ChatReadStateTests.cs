using System;
using Spokes_Server.Core.Models.Communication;
using Xunit;

namespace Spokes_Server.Tests.Core.Models.Communication;

public class ChatReadStateTests
{
    [Fact]
    public void CreateId_CombinesUserIdAndChannelId()
    {
        var userId = "user-abc";
        var channelId = "channel-xyz";

        var id = ChatReadState.CreateId(userId, channelId);

        Assert.Equal("user-abc_channel-xyz", id);
    }

    [Theory]
    [InlineData("General", "Mentions")]
    [InlineData("General", "All")]
    [InlineData("Direct", "Mentions")]
    [InlineData("Direct", "None")]
    [InlineData("Team", "Mentions")]
    [InlineData("Project", "None")]
    [InlineData(null, "Mentions")]
    [InlineData("", "None")]
    public void DefaultNotificationLevel_WhenIsVoiceChannel_ReturnsMentionsRegardlessOfOtherParams(string? channelType, string defaultPublicSub)
    {
        var result = ChatReadState.DefaultNotificationLevel(channelType, isVoiceChannel: true, defaultPublicSub: defaultPublicSub);

        Assert.Equal("Mentions", result);
    }

    [Fact]
    public void DefaultNotificationLevel_WhenGeneralChannel_UsesDefaultPublicSub()
    {
        // Default defaultPublicSub parameter is "Mentions"
        var defaultResult = ChatReadState.DefaultNotificationLevel(ChatChannelType.General, isVoiceChannel: false);
        Assert.Equal("Mentions", defaultResult);

        // Custom defaultPublicSub = "None"
        var noneResult = ChatReadState.DefaultNotificationLevel(ChatChannelType.General, isVoiceChannel: false, defaultPublicSub: "None");
        Assert.Equal("None", noneResult);

        // Custom defaultPublicSub = "All"
        var allResult = ChatReadState.DefaultNotificationLevel("General", isVoiceChannel: false, defaultPublicSub: "All");
        Assert.Equal("All", allResult);
    }

    [Theory]
    [InlineData(ChatChannelType.Direct)]
    [InlineData(ChatChannelType.Team)]
    [InlineData(ChatChannelType.Project)]
    [InlineData(ChatChannelType.Group)]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("RandomCustomType")]
    public void DefaultNotificationLevel_WhenNotVoiceAndNotGeneral_ReturnsAll(string? channelType)
    {
        var result = ChatReadState.DefaultNotificationLevel(channelType, isVoiceChannel: false);

        Assert.Equal("All", result);
    }

    [Theory]
    [InlineData(ChatChannelType.Direct, "Mentions")]
    [InlineData(ChatChannelType.Team, "None")]
    [InlineData(null, "None")]
    public void DefaultNotificationLevel_WhenNotVoiceAndNotGeneral_IgnoresDefaultPublicSub(string? channelType, string defaultPublicSub)
    {
        var result = ChatReadState.DefaultNotificationLevel(channelType, isVoiceChannel: false, defaultPublicSub: defaultPublicSub);

        Assert.Equal("All", result);
    }
}
