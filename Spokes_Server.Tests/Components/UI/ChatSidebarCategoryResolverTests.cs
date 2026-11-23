using MudBlazor;
using Spokes_Server.Components.UI;
using Spokes_Server.Core.Models.Communication;
using Xunit;

namespace Spokes_Server.Tests.Components.UI;

public class ChatSidebarCategoryResolverTests
{
    private static ChatCategory SysCat(string id) => new() { Id = id, Name = id, IsSystem = true };
    private static ChatCategory CustomCat(string id, string name = "Custom") => new() { Id = id, Name = name, IsSystem = false };

    [Fact]
    public void IsCategoryVisible_TeamsCategory_ReturnsTrueWhenCanCreateTeamChannelEvenIfEmpty()
    {
        var teamsCat = SysCat(ChatSidebarCategoryResolver.TeamsCategoryId);

        Assert.True(ChatSidebarCategoryResolver.IsCategoryVisible(
            teamsCat,
            filteredChannels: [],
            archivedChannels: [],
            isFriendEdition: false,
            canCreateTeamChannel: true,
            canViewArchive: false));

        Assert.False(ChatSidebarCategoryResolver.IsCategoryVisible(
            teamsCat,
            filteredChannels: [],
            archivedChannels: [],
            isFriendEdition: false,
            canCreateTeamChannel: false,
            canViewArchive: false));
    }

    [Fact]
    public void IsCategoryVisible_ProjectsCategory_HiddenInFamilyEdition()
    {
        var projectsCat = SysCat(ChatSidebarCategoryResolver.ProjectsCategoryId);
        var channels = new[]
        {
            new ChatChannel { Id = "p1", ChannelType = ChatChannelType.Project }
        };

        Assert.False(ChatSidebarCategoryResolver.IsCategoryVisible(
            projectsCat, channels, [], isFriendEdition: true, canCreateTeamChannel: false, canViewArchive: false));

        Assert.True(ChatSidebarCategoryResolver.IsCategoryVisible(
            projectsCat, channels, [], isFriendEdition: false, canCreateTeamChannel: false, canViewArchive: false));
    }

    [Fact]
    public void GetChannelsForCategory_ClassifiesAllBucketsAndSortsVoiceAfterText()
    {
        var publicText = new ChatChannel { Id = "pub-1", ChannelType = ChatChannelType.General, DisplayOrder = 2 };
        var publicVoice = new ChatChannel { Id = "pub-v", ChannelType = ChatChannelType.General, IsVoiceChannel = true, DisplayOrder = 1 };
        var teamGeneral = new ChatChannel { Id = "team-g", ChannelType = ChatChannelType.General, AllowedTeamIds = ["t1"], DisplayOrder = 1 };
        var groupGeneral = new ChatChannel { Id = "grp-g", ChannelType = ChatChannelType.General, ParticipantIds = ["u1", "u2"], DisplayOrder = 1 };
        var groupDirect3 = new ChatChannel { Id = "grp-d3", ChannelType = ChatChannelType.Direct, ParticipantIds = ["u1", "u2", "u3"], DisplayOrder = 2 };
        var dmOlder = new ChatChannel { Id = "dm-old", ChannelType = ChatChannelType.Direct, ParticipantIds = ["u1", "u2"], LastActivityAt = new DateTime(2026, 1, 1) };
        var dmNewer = new ChatChannel { Id = "dm-new", ChannelType = ChatChannelType.Direct, ParticipantIds = ["u1", "u3"], LastActivityAt = new DateTime(2026, 2, 1) };
        var customChan = new ChatChannel { Id = "cust-1", ChannelType = ChatChannelType.General, CategoryId = "cat-custom", DisplayOrder = 1 };
        var archivedChan = new ChatChannel { Id = "arch-1", ChannelType = ChatChannelType.General, IsArchived = true };

        var filtered = new[] { publicVoice, publicText, teamGeneral, groupGeneral, groupDirect3, dmOlder, dmNewer, customChan };
        var archived = new[] { archivedChan };

        var pubResult = ChatSidebarCategoryResolver.GetChannelsForCategory(
            SysCat(ChatSidebarCategoryResolver.PublicCategoryId), filtered, archived, c => c.DisplayOrder, c => c.LastActivityAt);
        Assert.Equal(["pub-1", "pub-v"], pubResult.Select(c => c.Id));

        var teamResult = ChatSidebarCategoryResolver.GetChannelsForCategory(
            SysCat(ChatSidebarCategoryResolver.TeamsCategoryId), filtered, archived, c => c.DisplayOrder, c => c.LastActivityAt);
        Assert.Equal(["team-g"], teamResult.Select(c => c.Id));

        var groupResult = ChatSidebarCategoryResolver.GetChannelsForCategory(
            SysCat(ChatSidebarCategoryResolver.GroupsCategoryId), filtered, archived, c => c.DisplayOrder, c => c.LastActivityAt);
        Assert.Equal(["grp-g", "grp-d3"], groupResult.Select(c => c.Id));

        var dmResult = ChatSidebarCategoryResolver.GetChannelsForCategory(
            SysCat(ChatSidebarCategoryResolver.DirectCategoryId), filtered, archived, c => c.DisplayOrder, c => c.LastActivityAt);
        Assert.Equal(["dm-new", "dm-old"], dmResult.Select(c => c.Id));

        var customResult = ChatSidebarCategoryResolver.GetChannelsForCategory(
            CustomCat("cat-custom"), filtered, archived, c => c.DisplayOrder, c => c.LastActivityAt);
        Assert.Equal(["cust-1"], customResult.Select(c => c.Id));

        var archiveResult = ChatSidebarCategoryResolver.GetChannelsForCategory(
            SysCat(ChatSidebarCategoryResolver.ArchiveCategoryId), filtered, archived, c => c.DisplayOrder, c => c.LastActivityAt);
        Assert.Equal(["arch-1"], archiveResult.Select(c => c.Id));
    }

    [Fact]
    public void ChatUiHelper_GetChannelIcon_HandlesCustomVoiceAndFallbackIcons()
    {
        var customIconChannel = new ChatChannel { Icon = Icons.Material.Filled.Star, IsVoiceChannel = true, ChannelType = ChatChannelType.Team };
        var teamVoiceChannel = new ChatChannel { IsVoiceChannel = true, ChannelType = ChatChannelType.Team };
        var groupTextChannel = new ChatChannel { IsVoiceChannel = false, ChannelType = ChatChannelType.General };

        Assert.Equal(Icons.Material.Filled.Star, ChatUiHelper.GetChannelIcon(customIconChannel, preferVoiceIcon: true, defaultIcon: Icons.Material.Filled.Groups));
        Assert.Equal(Icons.Material.Filled.VolumeUp, ChatUiHelper.GetChannelIcon(teamVoiceChannel, preferVoiceIcon: true, defaultIcon: Icons.Material.Filled.Groups));
        Assert.Equal(Icons.Material.Filled.Groups, ChatUiHelper.GetChannelIcon(teamVoiceChannel, preferVoiceIcon: false));
        Assert.Equal(Icons.Material.Filled.Group, ChatUiHelper.GetChannelIcon(groupTextChannel, preferVoiceIcon: true, defaultIcon: Icons.Material.Filled.Group));
    }
}
