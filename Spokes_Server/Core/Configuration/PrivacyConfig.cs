namespace Spokes_Server.Core.Configuration;

public enum PrivacyPreset
{
    Open = 0,
    TeamSilo = 1,
    TeamAndExactDiscovery = 2,
    PrivateContactsOnly = 3,
    StealthSupportOnly = 4,
    Custom = 99
}

public enum NetworkScope
{
    All = 0,
    SameTeam = 1,
    ContactsOnly = 2,
    None = 3
}

public enum UserDiscoveryMethod
{
    OpenSearch = 0,
    ExactEmailOnly = 1,
    ExactEmailOrUsername = 2,
    ExactEmailOrFullName = 2,
    None = 3
}

public enum AdminVisibilityMode
{
    AllAdmins = 0,
    DesignatedAdminsOnly = 1,
    None = 2
}

public enum PublicChannelPolicy
{
    Allowed = 0,
    AnnouncementOnly = 1,
    Disabled = 2
}

public enum AnnouncementReactionMode
{
    Allowed = 0,
    CountOnly = 1,
    Disabled = 2
}

public class PrivacyConfig
{
    public PrivacyPreset Preset { get; set; } = PrivacyPreset.Open;
    public NetworkScope BaseScope { get; set; } = NetworkScope.All;
    public UserDiscoveryMethod DiscoveryMethod { get; set; } = UserDiscoveryMethod.OpenSearch;
    public AdminVisibilityMode AdminVisibility { get; set; } = AdminVisibilityMode.AllAdmins;
    public List<string> DesignatedAdminIds { get; set; } = [];
    public bool AllowDirectoryBrowsing { get; set; } = true;
    public bool AllowNonAdminsCompanyWideCalendar { get; set; } = true;

    public PublicChannelPolicy PublicChannels { get; set; } = PublicChannelPolicy.Allowed;
    public AnnouncementReactionMode AnnouncementReactions { get; set; } = AnnouncementReactionMode.Allowed;
    public bool HidePublicChannelMemberList { get; set; } = false;
    public bool RestrictAlbumAudienceVisibility { get; set; } = false;

    public void ApplyPreset(PrivacyPreset preset)
    {
        Preset = preset;
        switch (preset)
        {
            case PrivacyPreset.Open:
                BaseScope = NetworkScope.All;
                DiscoveryMethod = UserDiscoveryMethod.OpenSearch;
                AdminVisibility = AdminVisibilityMode.AllAdmins;
                AllowDirectoryBrowsing = true;
                AllowNonAdminsCompanyWideCalendar = true;
                PublicChannels = PublicChannelPolicy.Allowed;
                AnnouncementReactions = AnnouncementReactionMode.Allowed;
                HidePublicChannelMemberList = false;
                RestrictAlbumAudienceVisibility = false;
                break;

            case PrivacyPreset.TeamSilo:
                BaseScope = NetworkScope.SameTeam;
                DiscoveryMethod = UserDiscoveryMethod.None;
                AdminVisibility = AdminVisibilityMode.DesignatedAdminsOnly;
                AllowDirectoryBrowsing = true;
                AllowNonAdminsCompanyWideCalendar = false;
                PublicChannels = PublicChannelPolicy.AnnouncementOnly;
                AnnouncementReactions = AnnouncementReactionMode.Disabled;
                HidePublicChannelMemberList = true;
                RestrictAlbumAudienceVisibility = true;
                break;

            case PrivacyPreset.TeamAndExactDiscovery:
                BaseScope = NetworkScope.SameTeam;
                DiscoveryMethod = UserDiscoveryMethod.ExactEmailOrUsername;
                AdminVisibility = AdminVisibilityMode.DesignatedAdminsOnly;
                AllowDirectoryBrowsing = true;
                AllowNonAdminsCompanyWideCalendar = false;
                PublicChannels = PublicChannelPolicy.AnnouncementOnly;
                AnnouncementReactions = AnnouncementReactionMode.CountOnly;
                HidePublicChannelMemberList = true;
                RestrictAlbumAudienceVisibility = true;
                break;

            case PrivacyPreset.PrivateContactsOnly:
                BaseScope = NetworkScope.ContactsOnly;
                DiscoveryMethod = UserDiscoveryMethod.ExactEmailOrUsername;
                AdminVisibility = AdminVisibilityMode.DesignatedAdminsOnly;
                AllowDirectoryBrowsing = false;
                AllowNonAdminsCompanyWideCalendar = false;
                PublicChannels = PublicChannelPolicy.Disabled;
                AnnouncementReactions = AnnouncementReactionMode.Disabled;
                HidePublicChannelMemberList = true;
                RestrictAlbumAudienceVisibility = true;
                break;

            case PrivacyPreset.StealthSupportOnly:
                BaseScope = NetworkScope.None;
                DiscoveryMethod = UserDiscoveryMethod.None;
                AdminVisibility = AdminVisibilityMode.DesignatedAdminsOnly;
                AllowDirectoryBrowsing = false;
                AllowNonAdminsCompanyWideCalendar = false;
                PublicChannels = PublicChannelPolicy.AnnouncementOnly;
                AnnouncementReactions = AnnouncementReactionMode.Disabled;
                HidePublicChannelMemberList = true;
                RestrictAlbumAudienceVisibility = true;
                break;

            case PrivacyPreset.Custom:
                // Retain current individual settings
                break;
        }
    }
}
