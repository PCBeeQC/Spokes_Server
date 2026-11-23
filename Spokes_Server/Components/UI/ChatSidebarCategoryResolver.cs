using MudBlazor;
using Spokes_Server.Core.Models.Communication;

namespace Spokes_Server.Components.UI;

public enum ChatSidebarAddAction
{
    None,
    NewTeamChannel,
    NewDirectMessage
}

public sealed record ChatSidebarSectionSpec(
    string CategoryId,
    string HeaderPaddingClass,
    string VoiceDividerMarginClass,
    bool UppercaseHeader,
    Color IconColor,
    string DefaultIcon,
    bool IsDirect,
    bool IsArchive,
    bool CanReorderChannels,
    bool CanReorderCategory,
    ChatSidebarAddAction AddAction);

/// <summary>
/// Single source of truth for ChatSidebar category classification, ordering, visibility,
/// and per-section presentation metadata across all 7 system and custom buckets.
/// </summary>
public static class ChatSidebarCategoryResolver
{
    public const string PublicCategoryId = "sys_public";
    public const string ProjectsCategoryId = "sys_projects";
    public const string TeamsCategoryId = "sys_teams";
    public const string GroupsCategoryId = "sys_groups";
    public const string DirectCategoryId = "sys_direct";
    public const string ArchiveCategoryId = "sys_archive";

    public static ChatSidebarSectionSpec GetSectionSpec(ChatCategory cat) => cat.Id switch
    {
        PublicCategoryId => new ChatSidebarSectionSpec(
            CategoryId: PublicCategoryId,
            HeaderPaddingClass: "pt-2",
            VoiceDividerMarginClass: "mt-1 mb-1",
            UppercaseHeader: false,
            IconColor: Color.Default,
            DefaultIcon: Icons.Material.Filled.Public,
            IsDirect: false,
            IsArchive: false,
            CanReorderChannels: true,
            CanReorderCategory: true,
            AddAction: ChatSidebarAddAction.None),

        ProjectsCategoryId => new ChatSidebarSectionSpec(
            CategoryId: ProjectsCategoryId,
            HeaderPaddingClass: "pt-3",
            VoiceDividerMarginClass: "mt-2 mb-1",
            UppercaseHeader: false,
            IconColor: Color.Primary,
            DefaultIcon: Icons.Material.Filled.Folder,
            IsDirect: false,
            IsArchive: false,
            CanReorderChannels: true,
            CanReorderCategory: true,
            AddAction: ChatSidebarAddAction.None),

        TeamsCategoryId => new ChatSidebarSectionSpec(
            CategoryId: TeamsCategoryId,
            HeaderPaddingClass: "pt-3",
            VoiceDividerMarginClass: "mt-2 mb-1",
            UppercaseHeader: true,
            IconColor: Color.Secondary,
            DefaultIcon: Icons.Material.Filled.Groups,
            IsDirect: false,
            IsArchive: false,
            CanReorderChannels: true,
            CanReorderCategory: true,
            AddAction: ChatSidebarAddAction.NewTeamChannel),

        GroupsCategoryId => new ChatSidebarSectionSpec(
            CategoryId: GroupsCategoryId,
            HeaderPaddingClass: "pt-3",
            VoiceDividerMarginClass: "mt-2 mb-1",
            UppercaseHeader: false,
            IconColor: Color.Tertiary,
            DefaultIcon: Icons.Material.Filled.Group,
            IsDirect: false,
            IsArchive: false,
            CanReorderChannels: true,
            CanReorderCategory: true,
            AddAction: ChatSidebarAddAction.NewDirectMessage),

        DirectCategoryId => new ChatSidebarSectionSpec(
            CategoryId: DirectCategoryId,
            HeaderPaddingClass: "pt-3",
            VoiceDividerMarginClass: "mt-2 mb-1",
            UppercaseHeader: false,
            IconColor: Color.Default,
            DefaultIcon: Icons.Material.Filled.Person,
            IsDirect: true,
            IsArchive: false,
            CanReorderChannels: false,
            CanReorderCategory: true,
            AddAction: ChatSidebarAddAction.NewDirectMessage),

        ArchiveCategoryId => new ChatSidebarSectionSpec(
            CategoryId: ArchiveCategoryId,
            HeaderPaddingClass: "pt-3",
            VoiceDividerMarginClass: "mt-2 mb-1",
            UppercaseHeader: false,
            IconColor: Color.Default,
            DefaultIcon: Icons.Material.Filled.Archive,
            IsDirect: false,
            IsArchive: true,
            CanReorderChannels: false,
            CanReorderCategory: false,
            AddAction: ChatSidebarAddAction.None),

        _ => new ChatSidebarSectionSpec(
            CategoryId: cat.Id,
            HeaderPaddingClass: "pt-2",
            VoiceDividerMarginClass: "mt-1 mb-1",
            UppercaseHeader: true,
            IconColor: Color.Default,
            DefaultIcon: Icons.Material.Filled.ChatBubbleOutline,
            IsDirect: false,
            IsArchive: false,
            CanReorderChannels: true,
            CanReorderCategory: true,
            AddAction: ChatSidebarAddAction.None)
    };

    public static bool MatchesCategory(ChatCategory cat, ChatChannel channel)
    {
        if (cat.Id == ArchiveCategoryId)
        {
            return channel.IsArchived;
        }

        if (channel.IsArchived)
        {
            return false;
        }

        bool matchesSystemSlot = string.IsNullOrEmpty(channel.CategoryId) || channel.CategoryId == cat.Id;

        return cat.Id switch
        {
            PublicCategoryId => channel.IsPublicChannel && matchesSystemSlot,
            ProjectsCategoryId => channel.ChannelType == ChatChannelType.Project && matchesSystemSlot,
            TeamsCategoryId => (channel.ChannelType == ChatChannelType.Team ||
                                (channel.ChannelType == ChatChannelType.General && channel.AllowedTeamIds.Any())) &&
                               matchesSystemSlot,
            GroupsCategoryId => (channel.ChannelType == ChatChannelType.Group ||
                                 (channel.ChannelType == ChatChannelType.Direct && channel.ParticipantIds.Count > 2) ||
                                 (channel.ChannelType == ChatChannelType.General && channel.ParticipantIds.Any() && !channel.AllowedTeamIds.Any())) &&
                                matchesSystemSlot,
            DirectCategoryId => channel.ChannelType == ChatChannelType.Direct &&
                                channel.ParticipantIds.Count <= 2 &&
                                channel.ParticipantIds.Any() &&
                                matchesSystemSlot,
            _ when !cat.IsSystem => channel.CategoryId == cat.Id,
            _ => false
        };
    }

    public static List<ChatChannel> GetChannelsForCategory(
        ChatCategory cat,
        IEnumerable<ChatChannel> filteredChannels,
        IEnumerable<ChatChannel> archivedChannels,
        Func<ChatChannel, int> getChannelOrder,
        Func<ChatChannel, DateTime> getDirectChannelRecency)
    {
        if (cat.Id == ArchiveCategoryId)
        {
            return archivedChannels.ToList();
        }

        if (cat.IsSystem &&
            cat.Id is not (PublicCategoryId or ProjectsCategoryId or TeamsCategoryId or GroupsCategoryId or DirectCategoryId))
        {
            return [];
        }

        var matching = filteredChannels.Where(c => MatchesCategory(cat, c));

        return cat.Id == DirectCategoryId
            ? matching.OrderBy(c => c.IsVoiceChannel ? 1 : 0).ThenByDescending(getDirectChannelRecency).ToList()
            : matching.OrderBy(c => c.IsVoiceChannel ? 1 : 0).ThenBy(getChannelOrder).ToList();
    }

    public static bool IsCategoryVisible(
        ChatCategory cat,
        IEnumerable<ChatChannel> filteredChannels,
        IEnumerable<ChatChannel> archivedChannels,
        bool isFriendEdition,
        bool canCreateTeamChannel,
        bool canViewArchive)
    {
        return cat.Id switch
        {
            PublicCategoryId => filteredChannels.Any(c => MatchesCategory(cat, c)),
            ProjectsCategoryId => !isFriendEdition && filteredChannels.Any(c => MatchesCategory(cat, c)),
            TeamsCategoryId => canCreateTeamChannel || filteredChannels.Any(c => MatchesCategory(cat, c)),
            GroupsCategoryId => filteredChannels.Any(c => MatchesCategory(cat, c)),
            DirectCategoryId => true,
            ArchiveCategoryId => canViewArchive && archivedChannels.Any(),
            _ when !cat.IsSystem => filteredChannels.Any(c => MatchesCategory(cat, c)),
            _ => false
        };
    }
}
