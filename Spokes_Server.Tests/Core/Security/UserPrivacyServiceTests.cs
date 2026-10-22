namespace Spokes_Server.Tests.Core.Security;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Moq;
using Spokes_Server.Core.Configuration;
using Spokes_Server.Core.Constants;
using Spokes_Server.Core.Data.Repositories.Core;
using Spokes_Server.Core.Data.Repositories.HR;
using Spokes_Server.Core.Models.Communication;
using Spokes_Server.Core.Models.Core;
using Spokes_Server.Core.Models.HR;
using Spokes_Server.Core.Services.Security;
using Xunit;

public class UserPrivacyServiceTests
{
    private readonly Mock<CompanyProfileRepository> _mockCompanyProfiles;
    private readonly Mock<EmployeeRepository> _mockEmployees;
    private readonly Mock<TeamRepository> _mockTeams;
    private readonly CompanyProfile _profile;
    private readonly UserPrivacyService _service;

    private readonly Employee _adminUser;
    private readonly Employee _supportAdminUser;
    private readonly Employee _aliceTeam1;
    private readonly Employee _bobTeam1;
    private readonly Employee _charlieTeam2;
    private readonly Employee _dianaNoTeam;
    private readonly Team _team1;
    private readonly Team _team2;

    public UserPrivacyServiceTests()
    {
        var mockConfig = new Mock<IConfiguration>();
        mockConfig.Setup(c => c["DataPath"]).Returns("Data");

        _mockCompanyProfiles = new Mock<CompanyProfileRepository>(null!, mockConfig.Object);
        _mockEmployees = new Mock<EmployeeRepository>(null!, mockConfig.Object);
        _mockTeams = new Mock<TeamRepository>(null!, mockConfig.Object);

        _profile = new CompanyProfile();
        _mockCompanyProfiles.Setup(c => c.Get()).Returns(_profile);

        _team1 = new Team { Id = "team-1", Name = "Engineering", LeaderId = "emp-alice" };
        _team2 = new Team { Id = "team-2", Name = "Marketing", LeaderId = "emp-leader-2" };

        _adminUser = new Employee { Id = "emp-admin", FirstName = "Admin", LastName = "User", Email = "admin@example.com", IsAdmin = true, IsActive = true };
        _supportAdminUser = new Employee { Id = "emp-support-admin", FirstName = "Support", LastName = "Lead", Email = "support@example.com", IsAdmin = true, IsActive = true };
        _aliceTeam1 = new Employee { Id = "emp-alice", FirstName = "Alice", LastName = "Smith", Email = "alice@example.com", TeamId = "team-1", IsActive = true };
        _bobTeam1 = new Employee { Id = "emp-bob", FirstName = "Bob", LastName = "Jones", Email = "bob@example.com", TeamId = "team-1", IsActive = true };
        _charlieTeam2 = new Employee { Id = "emp-charlie", FirstName = "Charlie", LastName = "Brown", Email = "charlie@example.com", TeamId = "team-2", IsActive = true };
        _dianaNoTeam = new Employee { Id = "emp-diana", FirstName = "Diana", LastName = "Prince", Email = "diana@example.com", TeamId = "", IsActive = true };

        var allEmployees = new List<Employee> { _adminUser, _supportAdminUser, _aliceTeam1, _bobTeam1, _charlieTeam2, _dianaNoTeam };
        var allTeams = new List<Team> { _team1, _team2 };

        _mockEmployees.Setup(e => e.GetAll()).Returns(allEmployees);
        _mockEmployees.Setup(e => e.GetActiveEmployees()).Returns(allEmployees);
        _mockEmployees.Setup(e => e.GetById(It.IsAny<string>())).Returns((string id) => allEmployees.FirstOrDefault(x => x.Id == id));
        _mockTeams.Setup(t => t.GetAll()).Returns(allTeams);

        _service = new UserPrivacyService(_mockCompanyProfiles.Object, _mockEmployees.Object, _mockTeams.Object);
    }

    #region Directory Viewing Permissions

    [Fact]
    public void CanViewDirectory_NullCaller_ReturnsFalse()
    {
        Assert.False(_service.CanViewDirectory(null));
    }

    [Fact]
    public void CanViewDirectory_AdminCaller_AlwaysReturnsTrueAcrossAllPresets()
    {
        foreach (var preset in Enum.GetValues<PrivacyPreset>())
        {
            _profile.Privacy.ApplyPreset(preset);
            Assert.True(_service.CanViewDirectory(_adminUser));
        }
    }

    [Fact]
    public void CanViewDirectory_RegularCaller_HonorsAllowDirectoryBrowsingFlag()
    {
        _profile.Privacy.AllowDirectoryBrowsing = true;
        Assert.True(_service.CanViewDirectory(_bobTeam1));

        _profile.Privacy.AllowDirectoryBrowsing = false;
        Assert.False(_service.CanViewDirectory(_bobTeam1));
    }

    #endregion

    #region CanDiscoverUser

    [Fact]
    public void CanDiscoverUser_Self_AlwaysTrue()
    {
        foreach (var preset in Enum.GetValues<PrivacyPreset>())
        {
            _profile.Privacy.ApplyPreset(preset);
            Assert.True(_service.CanDiscoverUser(_bobTeam1, _bobTeam1));
        }
    }

    [Fact]
    public void CanDiscoverUser_AdminCaller_AlwaysTrueForAnyUser()
    {
        foreach (var preset in Enum.GetValues<PrivacyPreset>())
        {
            _profile.Privacy.ApplyPreset(preset);
            Assert.True(_service.CanDiscoverUser(_adminUser, _charlieTeam2));
        }
    }

    [Fact]
    public void CanDiscoverUser_AdminTarget_ControlledByAdminVisibilityMode()
    {
        // 1. AllAdmins
        _profile.Privacy.AdminVisibility = AdminVisibilityMode.AllAdmins;
        Assert.True(_service.CanDiscoverUser(_bobTeam1, _adminUser));
        Assert.True(_service.CanDiscoverUser(_bobTeam1, _supportAdminUser));

        // 2. DesignatedAdminsOnly
        _profile.Privacy.BaseScope = NetworkScope.SameTeam;
        _profile.Privacy.AdminVisibility = AdminVisibilityMode.DesignatedAdminsOnly;
        _profile.Privacy.DesignatedAdminIds = new List<string> { _supportAdminUser.Id };
        Assert.False(_service.CanDiscoverUser(_bobTeam1, _adminUser));
        Assert.True(_service.CanDiscoverUser(_bobTeam1, _supportAdminUser));

        // 3. None
        _profile.Privacy.AdminVisibility = AdminVisibilityMode.None;
        _profile.Privacy.BaseScope = NetworkScope.None;
        Assert.False(_service.CanDiscoverUser(_bobTeam1, _supportAdminUser));
    }

    [Fact]
    public void CanDiscoverUser_OpenPreset_RegularUserCanDiscoverOtherRegularUsers()
    {
        _profile.Privacy.ApplyPreset(PrivacyPreset.Open);
        Assert.True(_service.CanDiscoverUser(_bobTeam1, _charlieTeam2));
    }

    [Fact]
    public void CanDiscoverUser_TeamSiloPreset_TeammatesCanDiscoverEachOther_CrossTeamBlocked()
    {
        _profile.Privacy.ApplyPreset(PrivacyPreset.TeamSilo);
        // Bob and Alice are both in team-1
        Assert.True(_service.CanDiscoverUser(_bobTeam1, _aliceTeam1));
        Assert.True(_service.CanDiscoverUser(_aliceTeam1, _bobTeam1));

        // Charlie is in team-2
        Assert.False(_service.CanDiscoverUser(_bobTeam1, _charlieTeam2));
        Assert.False(_service.CanDiscoverUser(_charlieTeam2, _bobTeam1));
    }

    [Fact]
    public void CanDiscoverUser_MutualContacts_AlwaysDiscoverableRegardlessOfPreset()
    {
        _profile.Privacy.ApplyPreset(PrivacyPreset.StealthSupportOnly);

        // Not contacts yet
        Assert.False(_service.CanDiscoverUser(_bobTeam1, _charlieTeam2));

        // Establish mutual contact
        _bobTeam1.ContactUserIds.Add(_charlieTeam2.Id);

        // Symmetric discovery
        Assert.True(_service.CanDiscoverUser(_bobTeam1, _charlieTeam2));
        Assert.True(_service.CanDiscoverUser(_charlieTeam2, _bobTeam1));
    }

    #endregion

    #region Filter Teams

    [Fact]
    public void FilterDiscoverableTeams_SameTeamScope_ReturnsOnlyCallerTeams()
    {
        _profile.Privacy.BaseScope = NetworkScope.SameTeam;
        var teams = new List<Team> { _team1, _team2 };

        var bobTeams = _service.FilterDiscoverableTeams(_bobTeam1, teams);
        Assert.Single(bobTeams);
        Assert.Equal("team-1", bobTeams[0].Id);
    }

    [Fact]
    public void FilterDiscoverableTeams_ContactsOnlyScope_ReturnsEmptyForRegularUser()
    {
        _profile.Privacy.BaseScope = NetworkScope.ContactsOnly;
        var teams = new List<Team> { _team1, _team2 };

        var bobTeams = _service.FilterDiscoverableTeams(_bobTeam1, teams);
        Assert.Empty(bobTeams);
    }

    [Fact]
    public void FilterDiscoverableTeams_AdminCaller_AlwaysReturnsAllTeams()
    {
        _profile.Privacy.BaseScope = NetworkScope.None;
        var teams = new List<Team> { _team1, _team2 };

        var adminTeams = _service.FilterDiscoverableTeams(_adminUser, teams);
        Assert.Equal(2, adminTeams.Count);
    }

    #endregion

    #region AddContactByIdentifierAsync & RemoveContactAsync

    [Fact]
    public async Task AddContact_ByExactEmail_Success_EstablishesMutualContactAndPersists()
    {
        _profile.Privacy.ApplyPreset(PrivacyPreset.TeamAndExactDiscovery);

        var result = await _service.AddContactByIdentifierAsync(_bobTeam1, "charlie@example.com");

        Assert.Equal(AddContactStatus.Success, result.Status);
        Assert.NotNull(result.Contact);
        Assert.Equal("emp-charlie", result.Contact!.Id);

        // Mutual addition verified
        Assert.Contains("emp-charlie", _bobTeam1.ContactUserIds);
        Assert.Contains("emp-bob", _charlieTeam2.ContactUserIds);

        // Verify repository Save called for both
        _mockEmployees.Verify(e => e.Save(_bobTeam1), Times.Once);
        _mockEmployees.Verify(e => e.Save(_charlieTeam2), Times.Once);
    }

    [Fact]
    public async Task AddContact_ByExactUsername_Success_WhenUsernameMethodAllowed()
    {
        _profile.Privacy.DiscoveryMethod = UserDiscoveryMethod.ExactEmailOrUsername;

        // "charlie" is prefix of charlie@example.com
        var result = await _service.AddContactByIdentifierAsync(_bobTeam1, "charlie");

        Assert.Equal(AddContactStatus.Success, result.Status);
        Assert.Equal("emp-charlie", result.Contact!.Id);
    }

    [Fact]
    public async Task AddContact_ByExactFullName_Success_WhenFullNameMethodAllowed()
    {
        _profile.Privacy.DiscoveryMethod = UserDiscoveryMethod.ExactEmailOrFullName;

        var result = await _service.AddContactByIdentifierAsync(_bobTeam1, _charlieTeam2.FullName);

        Assert.Equal(AddContactStatus.Success, result.Status);
        Assert.Equal("emp-charlie", result.Contact!.Id);
    }

    [Fact]
    public async Task AddContact_SelfContact_ReturnsDisallowed()
    {
        _profile.Privacy.ApplyPreset(PrivacyPreset.TeamAndExactDiscovery);

        var result = await _service.AddContactByIdentifierAsync(_bobTeam1, "bob@example.com");

        Assert.Equal(AddContactStatus.SelfContactDisallowed, result.Status);
    }

    [Fact]
    public async Task AddContact_NotFound_ReturnsNotFound()
    {
        _profile.Privacy.ApplyPreset(PrivacyPreset.TeamAndExactDiscovery);

        var result = await _service.AddContactByIdentifierAsync(_bobTeam1, "nonexistent@example.com");

        Assert.Equal(AddContactStatus.NotFound, result.Status);
    }

    [Fact]
    public async Task AddContact_RateLimiting_EnforcesThreshold()
    {
        _profile.Privacy.ApplyPreset(PrivacyPreset.TeamAndExactDiscovery);

        // Issue 10 attempts
        for (int i = 0; i < 10; i++)
        {
            await _service.AddContactByIdentifierAsync(_bobTeam1, $"miss_{i}@example.com");
        }

        // 11th attempt must be rate limited
        var limited = await _service.AddContactByIdentifierAsync(_bobTeam1, "charlie@example.com");
        Assert.Equal(AddContactStatus.RateLimited, limited.Status);
    }

    [Fact]
    public async Task RemoveContact_RemovesMutualConnectionAndPersists()
    {
        _bobTeam1.ContactUserIds.Add(_charlieTeam2.Id);
        _charlieTeam2.ContactUserIds.Add(_bobTeam1.Id);

        var removed = await _service.RemoveContactAsync(_bobTeam1, _charlieTeam2.Id);

        Assert.True(removed);
        Assert.DoesNotContain(_charlieTeam2.Id, _bobTeam1.ContactUserIds);
        Assert.DoesNotContain(_bobTeam1.Id, _charlieTeam2.ContactUserIds);

        _mockEmployees.Verify(e => e.Save(_bobTeam1), Times.AtLeastOnce);
        _mockEmployees.Verify(e => e.Save(_charlieTeam2), Times.AtLeastOnce);
    }

    #endregion

    #region Company-Wide Events Privileges

    [Fact]
    public void CanCreateCompanyWideEvents_Admin_AlwaysTrue()
    {
        _profile.Privacy.AllowNonAdminsCompanyWideCalendar = false;
        Assert.True(_service.CanCreateCompanyWideEvents(_adminUser));
    }

    [Fact]
    public void CanCreateCompanyWideEvents_RegularUser_FollowsConfigFlag()
    {
        _profile.Privacy.AllowNonAdminsCompanyWideCalendar = true;
        Assert.True(_service.CanCreateCompanyWideEvents(_bobTeam1));

        _profile.Privacy.AllowNonAdminsCompanyWideCalendar = false;
        Assert.False(_service.CanCreateCompanyWideEvents(_bobTeam1));
    }

    #endregion

    #region Public Channel & Announcement Privacy

    [Fact]
    public void ApplyPreset_ConfiguresPublicChannelAndAnnouncementPolicies()
    {
        var config = new PrivacyConfig();

        config.ApplyPreset(PrivacyPreset.Open);
        Assert.Equal(PublicChannelPolicy.Allowed, config.PublicChannels);
        Assert.Equal(AnnouncementReactionMode.Allowed, config.AnnouncementReactions);
        Assert.False(config.HidePublicChannelMemberList);
        Assert.False(config.RestrictAlbumAudienceVisibility);

        config.ApplyPreset(PrivacyPreset.TeamSilo);
        Assert.Equal(PublicChannelPolicy.AnnouncementOnly, config.PublicChannels);
        Assert.Equal(AnnouncementReactionMode.Disabled, config.AnnouncementReactions);
        Assert.True(config.HidePublicChannelMemberList);
        Assert.True(config.RestrictAlbumAudienceVisibility);

        config.ApplyPreset(PrivacyPreset.TeamAndExactDiscovery);
        Assert.Equal(PublicChannelPolicy.AnnouncementOnly, config.PublicChannels);
        Assert.Equal(AnnouncementReactionMode.CountOnly, config.AnnouncementReactions);
        Assert.True(config.HidePublicChannelMemberList);
        Assert.True(config.RestrictAlbumAudienceVisibility);

        config.ApplyPreset(PrivacyPreset.PrivateContactsOnly);
        Assert.Equal(PublicChannelPolicy.Disabled, config.PublicChannels);
        Assert.Equal(AnnouncementReactionMode.Disabled, config.AnnouncementReactions);
        Assert.True(config.HidePublicChannelMemberList);
        Assert.True(config.RestrictAlbumAudienceVisibility);

        config.ApplyPreset(PrivacyPreset.StealthSupportOnly);
        Assert.Equal(PublicChannelPolicy.AnnouncementOnly, config.PublicChannels);
        Assert.Equal(AnnouncementReactionMode.Disabled, config.AnnouncementReactions);
        Assert.True(config.HidePublicChannelMemberList);
        Assert.True(config.RestrictAlbumAudienceVisibility);
    }

    [Fact]
    public void CanCreatePublicChannels_WhenDisabled_ReturnsFalseForAdminAndRegularUsers()
    {
        _profile.Privacy.PublicChannels = PublicChannelPolicy.Disabled;

        Assert.False(_service.CanCreatePublicChannels(_adminUser));
        Assert.False(_service.CanCreatePublicChannels(_bobTeam1));
    }

    [Fact]
    public void CanCreatePublicChannels_WhenAnnouncementOnly_ReturnsTrueForAdminOnly()
    {
        _profile.Privacy.PublicChannels = PublicChannelPolicy.AnnouncementOnly;

        Assert.True(_service.CanCreatePublicChannels(_adminUser));
        Assert.False(_service.CanCreatePublicChannels(_bobTeam1));
    }

    [Fact]
    public void CanCreatePublicChannels_WhenAllowed_ReturnsTrueForBoth()
    {
        _profile.Privacy.PublicChannels = PublicChannelPolicy.Allowed;

        Assert.True(_service.CanCreatePublicChannels(_adminUser));
        Assert.True(_service.CanCreatePublicChannels(_bobTeam1));
    }

    [Fact]
    public void ShouldHideMemberList_TeamOrProjectChannel_NeverHiddenForTeammates()
    {
        _profile.Privacy.HidePublicChannelMemberList = true;
        _profile.Privacy.PublicChannels = PublicChannelPolicy.AnnouncementOnly;

        var teamChannel = new ChatChannel { ChannelType = ChatChannelType.Team, LinkedEntityId = "team-1" };
        var projectChannel = new ChatChannel { ChannelType = ChatChannelType.Project, LinkedEntityId = "proj-1" };
        var groupChannel = new ChatChannel { ChannelType = ChatChannelType.Group, ParticipantIds = ["emp-bob", "emp-alice"] };

        Assert.False(_service.ShouldHideMemberList(teamChannel, _bobTeam1));
        Assert.False(_service.ShouldHideMemberList(projectChannel, _bobTeam1));
        Assert.False(_service.ShouldHideMemberList(groupChannel, _bobTeam1));
    }

    [Fact]
    public void ShouldHideMemberList_PublicChannel_HidesForRegularUser_ShowsForAdmin()
    {
        _profile.Privacy.PublicChannels = PublicChannelPolicy.AnnouncementOnly;
        _profile.Privacy.HidePublicChannelMemberList = true;

        var publicChannel = new ChatChannel
        {
            ChannelType = ChatChannelType.General,
            IsDefaultGeneral = true,
            AllowedTeamIds = [],
            ParticipantIds = []
        };

        Assert.True(_service.ShouldHideMemberList(publicChannel, _bobTeam1));
        Assert.False(_service.ShouldHideMemberList(publicChannel, _adminUser));
    }

    [Fact]
    public void CanReactInChannel_AnnouncementDisabled_BlocksRegularUsers_AllowsAllowedPostersAndAdmins()
    {
        _profile.Privacy.AnnouncementReactions = AnnouncementReactionMode.Disabled;

        var announcementChannel = new ChatChannel
        {
            ChannelType = ChatChannelType.General,
            IsAnnouncementOnly = true,
            AllowedPostUserIds = ["emp-alice"]
        };

        // Bob is not an admin nor in AllowedPostUserIds
        Assert.False(_service.CanReactInChannel(announcementChannel, _bobTeam1));

        // Alice is in AllowedPostUserIds
        Assert.True(_service.CanReactInChannel(announcementChannel, _aliceTeam1));

        // Admin always allowed
        Assert.True(_service.CanReactInChannel(announcementChannel, _adminUser));
    }

    [Fact]
    public void ShouldAnonymizeReactions_CountOnly_ReturnsTrueForRegularUser_FalseForAdmin()
    {
        _profile.Privacy.AnnouncementReactions = AnnouncementReactionMode.CountOnly;

        var announcementChannel = new ChatChannel
        {
            ChannelType = ChatChannelType.General,
            IsAnnouncementOnly = true
        };

        Assert.True(_service.ShouldAnonymizeReactions(announcementChannel, _bobTeam1));
        Assert.False(_service.ShouldAnonymizeReactions(announcementChannel, _adminUser));
    }

    [Fact]
    public void CanAccessPublicChannel_WhenAllowed_AllowsStandardAndAnnouncementChannels()
    {
        _profile.Privacy.PublicChannels = PublicChannelPolicy.Allowed;

        var standardPublic = new ChatChannel { ChannelType = ChatChannelType.General, AllowedTeamIds = [], ParticipantIds = [], IsAnnouncementOnly = false };
        var announcementPublic = new ChatChannel { ChannelType = ChatChannelType.General, AllowedTeamIds = [], ParticipantIds = [], IsAnnouncementOnly = true };

        Assert.True(_service.CanAccessPublicChannel(standardPublic, _bobTeam1));
        Assert.True(_service.CanAccessPublicChannel(announcementPublic, _bobTeam1));
    }

    [Fact]
    public void CanAccessPublicChannel_WhenAnnouncementOnly_AllowsAnnouncementChannels_DeniesStandardPublicChannels()
    {
        _profile.Privacy.PublicChannels = PublicChannelPolicy.AnnouncementOnly;

        var standardPublic = new ChatChannel { ChannelType = ChatChannelType.General, AllowedTeamIds = [], ParticipantIds = [], IsAnnouncementOnly = false };
        var announcementPublic = new ChatChannel { ChannelType = ChatChannelType.General, AllowedTeamIds = [], ParticipantIds = [], IsAnnouncementOnly = true };

        Assert.False(_service.CanAccessPublicChannel(standardPublic, _bobTeam1));
        Assert.True(_service.CanAccessPublicChannel(announcementPublic, _bobTeam1));
    }

    [Fact]
    public void CanAccessPublicChannel_WhenDisabled_DeniesBothStandardAndAnnouncementChannels()
    {
        _profile.Privacy.PublicChannels = PublicChannelPolicy.Disabled;

        var standardPublic = new ChatChannel { ChannelType = ChatChannelType.General, AllowedTeamIds = [], ParticipantIds = [], IsAnnouncementOnly = false };
        var announcementPublic = new ChatChannel { ChannelType = ChatChannelType.General, AllowedTeamIds = [], ParticipantIds = [], IsAnnouncementOnly = true };

        Assert.False(_service.CanAccessPublicChannel(standardPublic, _bobTeam1));
        Assert.False(_service.CanAccessPublicChannel(announcementPublic, _bobTeam1));
    }

    [Fact]
    public void CanAccessPublicChannel_NonPublicChannels_AlwaysAllowedByPolicy()
    {
        _profile.Privacy.PublicChannels = PublicChannelPolicy.Disabled;

        var teamChannel = new ChatChannel { ChannelType = ChatChannelType.Team, LinkedEntityId = "team-1" };
        var projectChannel = new ChatChannel { ChannelType = ChatChannelType.Project, LinkedEntityId = "proj-1" };
        var directChannel = new ChatChannel { ChannelType = ChatChannelType.Direct, ParticipantIds = [_bobTeam1.Id] };
        var restrictedGeneral = new ChatChannel { ChannelType = ChatChannelType.General, AllowedTeamIds = ["team-1"] };

        Assert.True(_service.CanAccessPublicChannel(teamChannel, _bobTeam1));
        Assert.True(_service.CanAccessPublicChannel(projectChannel, _bobTeam1));
        Assert.True(_service.CanAccessPublicChannel(directChannel, _bobTeam1));
        Assert.True(_service.CanAccessPublicChannel(restrictedGeneral, _bobTeam1));
    }

    [Fact]
    public void ShouldRestrictAlbumAudience_AdminOrOwner_AlwaysReturnsFalse()
    {
        _profile.Privacy.RestrictAlbumAudienceVisibility = true;
        _profile.Privacy.HidePublicChannelMemberList = true;

        var album = new Album { OwnerId = _bobTeam1.Id };

        // Admin caller
        Assert.False(_service.ShouldRestrictAlbumAudience(album, _adminUser));

        // Album owner caller
        Assert.False(_service.ShouldRestrictAlbumAudience(album, _bobTeam1));

        // Null inputs safe
        Assert.False(_service.ShouldRestrictAlbumAudience(null, _bobTeam1));
        Assert.False(_service.ShouldRestrictAlbumAudience(album, null));
    }

    [Fact]
    public void ShouldRestrictAlbumAudience_OpenServer_ReturnsFalseForRegularUser()
    {
        _profile.Privacy.ApplyPreset(PrivacyPreset.Open);

        var album = new Album { OwnerId = _aliceTeam1.Id };

        Assert.False(_service.ShouldRestrictAlbumAudience(album, _bobTeam1));
    }

    [Fact]
    public void ShouldRestrictAlbumAudience_WhenRestricted_ReturnsTrueForRegularUser()
    {
        var album = new Album { OwnerId = _aliceTeam1.Id };

        // Via explicit RestrictAlbumAudienceVisibility toggle
        _profile.Privacy.ApplyPreset(PrivacyPreset.Open);
        _profile.Privacy.RestrictAlbumAudienceVisibility = true;
        Assert.True(_service.ShouldRestrictAlbumAudience(album, _bobTeam1));

        // Via HidePublicChannelMemberList toggle
        _profile.Privacy.ApplyPreset(PrivacyPreset.Open);
        _profile.Privacy.HidePublicChannelMemberList = true;
        Assert.True(_service.ShouldRestrictAlbumAudience(album, _bobTeam1));

        // Via TeamSilo preset
        _profile.Privacy.ApplyPreset(PrivacyPreset.TeamSilo);
        Assert.True(_service.ShouldRestrictAlbumAudience(album, _bobTeam1));

        // Via StealthSupportOnly preset
        _profile.Privacy.ApplyPreset(PrivacyPreset.StealthSupportOnly);
        Assert.True(_service.ShouldRestrictAlbumAudience(album, _bobTeam1));
    }

    #endregion
}
