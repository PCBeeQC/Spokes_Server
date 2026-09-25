using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;
using Spokes_Server.Core.Constants;
using Spokes_Server.Core.Data;
using Spokes_Server.Core.Data.Repositories.Communication;
using Spokes_Server.Core.Data.Repositories.HR;
using Spokes_Server.Core.Data.Repositories.Projects;
using Spokes_Server.Core.Models.Communication;
using Spokes_Server.Core.Models.HR;
using Spokes_Server.Core.Models.Projects;
using Spokes_Server.Core.Services.Communication.Chat;

namespace Spokes_Server.Tests.Core.Services.Communication.Chat;

public class ChatAuthorizationServiceTests : IDisposable
{
    private readonly string _testDataDir;
    private readonly DiskPersistenceService _writer;
    private readonly ChatChannelRepository _channels;
    private readonly ProjectRepository _projects;
    private readonly TeamRepository _teams;
    private readonly EmployeeRepository _employees;
    private readonly ChatAuthorizationService _authService;

    public ChatAuthorizationServiceTests()
    {
        _testDataDir = Path.Combine(Path.GetTempPath(), $"Spokes_Test_ChatAuth_{Guid.NewGuid()}");
        Directory.CreateDirectory(_testDataDir);

        var configDict = new Dictionary<string, string> { { "DataPath", _testDataDir } };
        var config = new ConfigurationBuilder().AddInMemoryCollection(configDict).Build();

        var mockLogger = new Mock<ILogger<DiskPersistenceService>>();
        _writer = new DiskPersistenceService(mockLogger.Object);

        _channels = new ChatChannelRepository(_writer, config);
        _projects = new ProjectRepository(_writer, config);
        _teams = new TeamRepository(_writer, config);
        _employees = new EmployeeRepository(_writer, config);

        _authService = new ChatAuthorizationService(_channels, _projects, _teams, _employees);
    }

    public void Dispose()
    {
        _writer.Dispose();
        if (Directory.Exists(_testDataDir))
        {
            try { Directory.Delete(_testDataDir, true); } catch { }
        }
    }

    private Employee CreateEmployee(string id, bool isAdmin = false, string? teamId = null, bool isActive = true, bool isSuspended = false)
    {
        var emp = new Employee
        {
            Id = id,
            FirstName = "User",
            LastName = id,
            IsAdmin = isAdmin,
            TeamId = teamId,
            IsActive = isActive,
            IsSuspended = isSuspended,
            Permissions = new List<string>()
        };
        _employees.Save(emp);
        return emp;
    }

    #region CanUserAccessChannel Tests

    [Fact]
    public void CanUserAccessChannel_InactiveOrSuspended_ReturnsFalse()
    {
        var inactiveEmp = CreateEmployee("inactive_1", isActive: false);
        var suspendedEmp = CreateEmployee("suspended_1", isSuspended: true);
        var channel = new ChatChannel { Id = "c1", ChannelType = ChatChannelType.General, IsDefaultGeneral = true };
        _channels.Save(channel);

        Assert.False(_authService.CanUserAccessChannel(channel, inactiveEmp));
        Assert.False(_authService.CanUserAccessChannel(channel, suspendedEmp));
    }

    [Fact]
    public void CanUserAccessChannel_Admin_ReturnsFalseForGroupChannel_WhenNotParticipant()
    {
        var admin = CreateEmployee("admin_1", isAdmin: true);
        var secretChannel = new ChatChannel
        {
            Id = "secret_group",
            ChannelType = ChatChannelType.Group,
            ParticipantIds = new List<string> { "other_user" }
        };
        _channels.Save(secretChannel);

        Assert.False(_authService.CanUserAccessChannel(secretChannel, admin));
    }

    [Fact]
    public void CanUserAccessChannel_Admin_ReturnsFalseForDirectMessage_WhenNotParticipant()
    {
        var admin = CreateEmployee("admin_1", isAdmin: true);
        var directChannel = new ChatChannel
        {
            Id = "dm_1",
            ChannelType = ChatChannelType.Direct,
            ParticipantIds = new List<string> { "user_a", "user_b" }
        };
        _channels.Save(directChannel);

        Assert.False(_authService.CanUserAccessChannel(directChannel, admin));
    }

    [Fact]
    public void CanUserAccessChannel_Admin_ReturnsTrueForDirectMessage_WhenParticipant()
    {
        var admin = CreateEmployee("admin_1", isAdmin: true);
        var directChannel = new ChatChannel
        {
            Id = "dm_admin",
            ChannelType = ChatChannelType.Direct,
            ParticipantIds = new List<string> { "admin_1", "user_b" }
        };
        _channels.Save(directChannel);

        Assert.True(_authService.CanUserAccessChannel(directChannel, admin));
    }

    [Fact]
    public void CanUserAccessChannel_Admin_ReturnsTrueForGeneralAndTeamChannels()
    {
        var admin = CreateEmployee("admin_1", isAdmin: true);
        var generalChannel = new ChatChannel
        {
            Id = "gen_restricted",
            ChannelType = ChatChannelType.General,
            IsDefaultGeneral = false,
            ParticipantIds = new List<string> { "other_user" }
        };
        _channels.Save(generalChannel);

        var teamChannel = new ChatChannel
        {
            Id = "team_channel",
            ChannelType = ChatChannelType.Team,
            LinkedEntityId = "other_team"
        };
        _channels.Save(teamChannel);

        Assert.True(_authService.CanUserAccessChannel(generalChannel, admin));
        Assert.True(_authService.CanUserAccessChannel(teamChannel, admin));
    }

    [Fact]
    public void CanUserAccessChannel_GeneralChannel_DefaultGeneral_ReturnsTrue()
    {
        var emp = CreateEmployee("emp_gen");
        var channel = new ChatChannel { Id = "gen_1", ChannelType = ChatChannelType.General, IsDefaultGeneral = true };
        _channels.Save(channel);

        Assert.True(_authService.CanUserAccessChannel(channel, emp));
    }

    [Fact]
    public void CanUserAccessChannel_GeneralChannel_Restricted_RespectsParticipantAndTeam()
    {
        var empInList = CreateEmployee("emp_in_list");
        var empInTeam = CreateEmployee("emp_in_team", teamId: "team_allowed");
        var empExcluded = CreateEmployee("emp_excluded", teamId: "other_team");

        var restrictedChannel = new ChatChannel
        {
            Id = "gen_restricted",
            ChannelType = ChatChannelType.General,
            IsDefaultGeneral = false,
            ParticipantIds = new List<string> { empInList.Id },
            AllowedTeamIds = new List<string> { "team_allowed" }
        };
        _channels.Save(restrictedChannel);

        Assert.True(_authService.CanUserAccessChannel(restrictedChannel, empInList));
        Assert.True(_authService.CanUserAccessChannel(restrictedChannel, empInTeam));
        Assert.False(_authService.CanUserAccessChannel(restrictedChannel, empExcluded));
    }

    [Fact]
    public void CanUserAccessChannel_DirectAndGroup_ParticipantAndCreator_ReturnsTrue()
    {
        var creator = CreateEmployee("creator_1");
        var participant = CreateEmployee("participant_1");
        var stranger = CreateEmployee("stranger_1");

        var groupChannel = new ChatChannel
        {
            Id = "group_1",
            ChannelType = ChatChannelType.Group,
            CreatedById = creator.Id,
            ParticipantIds = new List<string> { participant.Id }
        };
        _channels.Save(groupChannel);

        Assert.True(_authService.CanUserAccessChannel(groupChannel, creator));
        Assert.True(_authService.CanUserAccessChannel(groupChannel, participant));
        Assert.False(_authService.CanUserAccessChannel(groupChannel, stranger));
    }

    [Fact]
    public void CanUserAccessChannel_TeamChannel_MemberAndLeader_ReturnsTrue()
    {
        var teamId = "dev_team";
        var leader = CreateEmployee("leader_1");
        var member = CreateEmployee("member_1", teamId: teamId);
        var outsider = CreateEmployee("outsider_1");

        var team = new Team
        {
            Id = teamId,
            Name = "Dev Team",
            LeaderId = leader.Id
        };
        _teams.Save(team);

        var teamChannel = new ChatChannel
        {
            Id = "team_chan_1",
            ChannelType = ChatChannelType.Team,
            LinkedEntityId = teamId
        };
        _channels.Save(teamChannel);

        Assert.True(_authService.CanUserAccessChannel(teamChannel, member));
        Assert.True(_authService.CanUserAccessChannel(teamChannel, leader));
        Assert.False(_authService.CanUserAccessChannel(teamChannel, outsider));
    }

    [Fact]
    public void CanUserAccessChannel_ProjectChannel_PublicOrMember_ReturnsTrue()
    {
        var member = CreateEmployee("proj_member");
        var teamMember = CreateEmployee("proj_team_member", teamId: "arch_team");
        var nonMember = CreateEmployee("proj_non_member");

        var project = new Project
        {
            Id = "proj_1",
            Name = "Top Secret Project",
            AccessPolicy = "Private",
            AllowedUserIds = new List<string> { member.Id },
            AllowedTeamIds = new List<string> { "arch_team" }
        };
        _projects.Save(project);

        var projectChannel = new ChatChannel
        {
            Id = "proj_chan_1",
            ChannelType = ChatChannelType.Project,
            LinkedEntityId = project.Id
        };
        _channels.Save(projectChannel);

        Assert.True(_authService.CanUserAccessChannel(projectChannel, member));
        Assert.True(_authService.CanUserAccessChannel(projectChannel, teamMember));
        Assert.False(_authService.CanUserAccessChannel(projectChannel, nonMember));
    }

    #endregion

    #region CanUserPostToChannel Tests

    [Fact]
    public void CanUserPostToChannel_NonAnnouncementChannel_ReturnsTrue()
    {
        var emp = CreateEmployee("emp_poster");
        var channel = new ChatChannel
        {
            Id = "chan_open",
            ChannelType = ChatChannelType.General,
            IsAnnouncementOnly = false
        };
        _channels.Save(channel);

        Assert.True(_authService.CanUserPostToChannel(channel, emp));
    }

    [Fact]
    public void CanUserPostToChannel_AnnouncementChannel_AdminAndAllowedPoster_ReturnsTrue()
    {
        var admin = CreateEmployee("admin_poster", isAdmin: true);
        var allowedUser = CreateEmployee("allowed_user");
        var allowedTeamUser = CreateEmployee("allowed_team_user", teamId: "news_team");
        var regularUser = CreateEmployee("regular_user");

        var announcementChannel = new ChatChannel
        {
            Id = "ann_chan",
            ChannelType = ChatChannelType.General,
            IsAnnouncementOnly = true,
            AllowedPostUserIds = new List<string> { allowedUser.Id },
            AllowedPostTeamIds = new List<string> { "news_team" }
        };
        _channels.Save(announcementChannel);

        Assert.True(_authService.CanUserPostToChannel(announcementChannel, admin));
        Assert.True(_authService.CanUserPostToChannel(announcementChannel, allowedUser));
        Assert.True(_authService.CanUserPostToChannel(announcementChannel, allowedTeamUser));
        Assert.False(_authService.CanUserPostToChannel(announcementChannel, regularUser));
    }

    #endregion

    #region CanUserModifyMessage Tests

    [Fact]
    public void CanUserModifyMessage_AuthorWithAccess_ReturnsTrue()
    {
        var author = CreateEmployee("author_1");
        var channel = new ChatChannel
        {
            Id = "chan_edit_1",
            ChannelType = ChatChannelType.General,
            IsDefaultGeneral = true
        };
        _channels.Save(channel);

        var message = new ChatMessage
        {
            Id = "msg_1",
            ChannelId = channel.Id,
            SenderId = author.Id,
            Content = "Original text"
        };

        Assert.True(_authService.CanUserModifyMessage(message, channel, author));
    }

    [Fact]
    public void CanUserModifyMessage_NonAuthor_ReturnsFalse()
    {
        var author = CreateEmployee("author_2");
        var imposter = CreateEmployee("imposter_2");
        var channel = new ChatChannel
        {
            Id = "chan_edit_2",
            ChannelType = ChatChannelType.General,
            IsDefaultGeneral = true
        };
        _channels.Save(channel);

        var message = new ChatMessage
        {
            Id = "msg_2",
            ChannelId = channel.Id,
            SenderId = author.Id,
            Content = "Original text"
        };

        Assert.False(_authService.CanUserModifyMessage(message, channel, imposter));
    }

    [Fact]
    public void CanUserModifyMessage_AuthorRevokedFromChannel_ReturnsFalse()
    {
        var author = CreateEmployee("author_revoked");
        // Author was previously in group, but has now been removed
        var groupChannel = new ChatChannel
        {
            Id = "group_revoked",
            ChannelType = ChatChannelType.Group,
            CreatedById = "other_creator",
            ParticipantIds = new List<string> { "other_creator", "someone_else" } // author not in list
        };
        _channels.Save(groupChannel);

        var message = new ChatMessage
        {
            Id = "msg_revoked",
            ChannelId = groupChannel.Id,
            SenderId = author.Id,
            Content = "Message before removal"
        };

        Assert.False(_authService.CanUserModifyMessage(message, groupChannel, author));
    }

    [Fact]
    public void CanUserModifyMessage_AuthorInAnnouncementChannelWithoutPostAccess_ReturnsFalse()
    {
        var author = CreateEmployee("author_ann_no_post");
        var channel = new ChatChannel
        {
            Id = "ann_no_edit",
            ChannelType = ChatChannelType.General,
            IsDefaultGeneral = true,
            IsAnnouncementOnly = true,
            AllowedPostUserIds = new List<string> { "only_designated_spokesperson" }
        };
        _channels.Save(channel);

        var message = new ChatMessage
        {
            Id = "msg_ann_1",
            ChannelId = channel.Id,
            SenderId = author.Id,
            Content = "Announcement"
        };

        Assert.False(_authService.CanUserModifyMessage(message, channel, author));
    }

    #endregion

    #region CanUserDeleteMessage Tests

    [Fact]
    public void CanUserDeleteMessage_AuthorWithAccess_ReturnsTrue()
    {
        var author = CreateEmployee("author_del");
        var channel = new ChatChannel
        {
            Id = "chan_del_1",
            ChannelType = ChatChannelType.General,
            IsDefaultGeneral = true
        };
        _channels.Save(channel);

        var message = new ChatMessage
        {
            Id = "msg_del_1",
            ChannelId = channel.Id,
            SenderId = author.Id,
            Content = "To be deleted"
        };

        Assert.True(_authService.CanUserDeleteMessage(message, channel, author));
    }

    [Fact]
    public void CanUserDeleteMessage_NonAuthorRegularUser_ReturnsFalse()
    {
        var author = CreateEmployee("author_del_2");
        var otherUser = CreateEmployee("other_del_2");
        var channel = new ChatChannel
        {
            Id = "chan_del_2",
            ChannelType = ChatChannelType.General,
            IsDefaultGeneral = true
        };
        _channels.Save(channel);

        var message = new ChatMessage
        {
            Id = "msg_del_2",
            ChannelId = channel.Id,
            SenderId = author.Id,
            Content = "To be deleted"
        };

        Assert.False(_authService.CanUserDeleteMessage(message, channel, otherUser));
    }

    [Fact]
    public void CanUserDeleteMessage_AdminAndModerator_ReturnsTrue()
    {
        var author = CreateEmployee("author_del_3");
        var admin = CreateEmployee("admin_del_3", isAdmin: true);
        var moderator = CreateEmployee("mod_del_3");
        moderator.Permissions.Add(AppPermissions.Chat.Moderator);
        _employees.Save(moderator);

        var channel = new ChatChannel
        {
            Id = "chan_del_3",
            ChannelType = ChatChannelType.General,
            IsDefaultGeneral = true
        };
        _channels.Save(channel);

        var message = new ChatMessage
        {
            Id = "msg_del_3",
            ChannelId = channel.Id,
            SenderId = author.Id,
            Content = "Inappropriate content"
        };

        Assert.True(_authService.CanUserDeleteMessage(message, channel, admin));
        Assert.True(_authService.CanUserDeleteMessage(message, channel, moderator));
    }

    [Fact]
    public void CanUserDeleteMessage_AuthorRevokedFromChannel_ReturnsFalse()
    {
        var author = CreateEmployee("author_del_revoked");
        var groupChannel = new ChatChannel
        {
            Id = "group_del_revoked",
            ChannelType = ChatChannelType.Group,
            CreatedById = "someone_else",
            ParticipantIds = new List<string> { "someone_else" }
        };
        _channels.Save(groupChannel);

        var message = new ChatMessage
        {
            Id = "msg_del_revoked",
            ChannelId = groupChannel.Id,
            SenderId = author.Id,
            Content = "Old message"
        };

        Assert.False(_authService.CanUserDeleteMessage(message, groupChannel, author));
    }

    #endregion
}
