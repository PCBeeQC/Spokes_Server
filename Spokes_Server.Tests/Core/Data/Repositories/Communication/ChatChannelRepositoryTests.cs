using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using Spokes_Server.Core.Data;
using Spokes_Server.Core.Data.Repositories.Communication;
using Spokes_Server.Core.Models.Communication;
using Xunit;

namespace Spokes_Server.Tests.Core.Data.Repositories.Communication;

public class ChatChannelRepositoryTests : IDisposable
{
    private readonly string _testDataDir;
    private readonly DiskPersistenceService _writer;
    private readonly ChatChannelRepository _repo;

    public ChatChannelRepositoryTests()
    {
        _testDataDir = Path.Combine(Path.GetTempPath(), $"Spokes_Test_ChatChannels_{Guid.NewGuid()}");

        var mockConfig = new Mock<IConfiguration>();
        mockConfig.Setup(c => c["DataPath"]).Returns(_testDataDir);

        var mockLogger = new Mock<ILogger<DiskPersistenceService>>();
        _writer = new DiskPersistenceService(mockLogger.Object);

        _repo = new ChatChannelRepository(_writer, mockConfig.Object);
    }

    public void Dispose()
    {
        if (Directory.Exists(_testDataDir))
        {
            try { Directory.Delete(_testDataDir, true); } catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"Cleanup error: {ex.Message}"); }
        }
        _writer.Dispose();
    }

    [Fact]
    public void GetByType_ReturnsCorrectChannels()
    {
        _repo.Save(new ChatChannel { Id = "1", ChannelType = ChatChannelType.General });
        _repo.Save(new ChatChannel { Id = "2", ChannelType = ChatChannelType.Project });
        _repo.Save(new ChatChannel { Id = "3", ChannelType = ChatChannelType.Project });

        var projects = _repo.GetByType(ChatChannelType.Project);
        Assert.Equal(2, projects.Count);
    }

    [Fact]
    public void GetOrCreateDefaultGeneral_CreatesIfNeeded()
    {
        var c1 = _repo.GetOrCreateDefaultGeneral();
        Assert.NotNull(c1);
        Assert.True(c1.IsDefaultGeneral);
        Assert.Equal(ChatChannelType.General, c1.ChannelType);

        // Fetch again, should return the same
        var c2 = _repo.GetOrCreateDefaultGeneral();
        Assert.Equal(c1.Id, c2.Id);
    }

    [Fact]
    public void ProjectChannels_GetOrCreate_Works()
    {
        var channel = _repo.GetOrCreateProjectChannel("proj1", "Project Alpha");
        Assert.NotNull(channel);
        Assert.Equal("proj1", channel.LinkedEntityId);
        Assert.Equal(ChatChannelType.Project, channel.ChannelType);

        var existing = _repo.GetProjectChannel("proj1");
        Assert.NotNull(existing);
        Assert.Equal(channel.Id, existing.Id);

        var notFound = _repo.GetProjectChannel("nonexistent");
        Assert.Null(notFound);
    }

    [Fact]
    public void TeamChannels_GetOrCreate_Works()
    {
        var channel = _repo.GetOrCreateTeamChannel("team1", "Sales Team");
        Assert.NotNull(channel);
        Assert.Equal("team1", channel.LinkedEntityId);
        Assert.Equal(ChatChannelType.Team, channel.ChannelType);

        var existing = _repo.GetTeamChannel("team1");
        Assert.NotNull(existing);
        Assert.Equal(channel.Id, existing.Id);
    }

    [Fact]
    public void DirectChannels_GetOrCreate_Works()
    {
        var channel = _repo.GetOrCreateDirectChannel("userA", "userB");
        Assert.NotNull(channel);
        Assert.Equal(ChatChannelType.Direct, channel.ChannelType);
        Assert.Contains("userA", channel.ParticipantIds);
        Assert.Contains("userB", channel.ParticipantIds);

        // Deterministic ID check (userA_userB vs userB_userA)
        var channel2 = _repo.GetOrCreateDirectChannel("userB", "userA");
        Assert.Equal(channel.Id, channel2.Id);
    }

    [Fact]
    public void GetDirectChannelsForUser_ReturnsCorrectChannels()
    {
        _repo.GetOrCreateDirectChannel("user1", "user2");
        _repo.GetOrCreateDirectChannel("user1", "user3");
        _repo.GetOrCreateDirectChannel("user2", "user3"); // user1 not in this

        var channels = _repo.GetDirectChannelsForUser("user1");
        Assert.Equal(2, channels.Count);
        Assert.All(channels, c => Assert.Contains("user1", c.ParticipantIds));
    }

    [Fact]
    public void GetChannelsForUser_IncludesGeneralAndDMs()
    {
        _repo.GetOrCreateDefaultGeneral();
        _repo.GetOrCreateDirectChannel("user1", "user2");

        var channels = _repo.GetChannelsForUser("user1", [], [], false);

        Assert.Contains(channels, c => c.IsDefaultGeneral);
        Assert.Contains(channels, c => c.ChannelType == ChatChannelType.Direct && c.ParticipantIds.Contains("user1"));
    }

    [Fact]
    public void UpdateLastActivity_ChangesTimestamp()
    {
        var channel = _repo.GetOrCreateDefaultGeneral();
        var oldTime = channel.LastActivityAt;

        // Needs a slight delay to ensure time difference
        System.Threading.Thread.Sleep(10);

        _repo.UpdateLastActivity(channel.Id);

        var updated = _repo.GetById(channel.Id);
        Assert.NotNull(updated);
        Assert.True(updated.LastActivityAt > oldTime);
    }

    [Fact]
    public void SupportsAnnouncements_ReturnsFalseForDirect_AndTrueForOthers()
    {
        var direct = new ChatChannel { ChannelType = ChatChannelType.Direct };
        var general = new ChatChannel { ChannelType = ChatChannelType.General };
        var team = new ChatChannel { ChannelType = ChatChannelType.Team };
        var project = new ChatChannel { ChannelType = ChatChannelType.Project };
        var group = new ChatChannel { ChannelType = ChatChannelType.Group };

        Assert.False(direct.SupportsAnnouncements);
        Assert.True(general.SupportsAnnouncements);
        Assert.True(team.SupportsAnnouncements);
        Assert.True(project.SupportsAnnouncements);
        Assert.True(group.SupportsAnnouncements);
    }

    [Fact]
    public void Normalize_ResetsAnnouncementSettings_WhenDirectChannel()
    {
        var channel = new ChatChannel
        {
            ChannelType = ChatChannelType.Direct,
            IsAnnouncementOnly = true,
            AllowedPostUserIds = ["user1"],
            AllowedPostTeamIds = ["team1"]
        };

        channel.Normalize();

        Assert.False(channel.IsAnnouncementOnly);
        Assert.Empty(channel.AllowedPostUserIds);
        Assert.Empty(channel.AllowedPostTeamIds);
    }

    [Fact]
    public void Normalize_PreservesAnnouncementSettings_WhenNonDirectChannel()
    {
        var channel = new ChatChannel
        {
            ChannelType = ChatChannelType.General,
            IsAnnouncementOnly = true,
            AllowedPostUserIds = ["user1"],
            AllowedPostTeamIds = ["team1"]
        };

        channel.Normalize();

        Assert.True(channel.IsAnnouncementOnly);
        Assert.Single(channel.AllowedPostUserIds);
        Assert.Single(channel.AllowedPostTeamIds);
    }

    [Fact]
    public void LoadFromDisk_AutoHealsCorruptedDirectChannels()
    {
        // Manually save a direct channel file that was corrupted with IsAnnouncementOnly = true
        var corrupted = new ChatChannel
        {
            Id = "dm_corrupted_test",
            ChannelType = ChatChannelType.Direct,
            IsAnnouncementOnly = true,
            AllowedPostUserIds = ["user1"],
            AllowedPostTeamIds = ["team1"]
        };
        _repo.Save(corrupted);

        // Reload from disk to trigger auto-healing
        _repo.LoadFromDisk();

        var healed = _repo.GetById("dm_corrupted_test");
        Assert.NotNull(healed);
        Assert.False(healed.IsAnnouncementOnly);
        Assert.Empty(healed.AllowedPostUserIds);
        Assert.Empty(healed.AllowedPostTeamIds);
    }

    [Fact]
    public void Constructor_WithNullDataPath_UsesDefaultPath()
    {
        var mockConfig = new Mock<IConfiguration>();
        mockConfig.Setup(c => c["DataPath"]).Returns((string?)null);

        var repo = new ChatChannelRepository(_writer, mockConfig.Object);

        Assert.NotNull(repo);
    }

    [Fact]
    public void CreateGroupChannel_CreatesNewGroupChannelWithParticipants()
    {
        var userIds = new List<string> { "userA", "userB", "userA" }; // includes duplicates to verify Distinct()
        var group = _repo.CreateGroupChannel(userIds, "My Group", isEncrypted: true, createdById: "creator1");

        Assert.NotNull(group);
        Assert.Equal("My Group", group.Name);
        Assert.Equal(ChatChannelType.Group, group.ChannelType);
        Assert.True(group.IsEncrypted);
        Assert.Equal("creator1", group.CreatedById);
        Assert.Equal(2, group.ParticipantIds.Count);
        Assert.Contains("userA", group.ParticipantIds);
        Assert.Contains("userB", group.ParticipantIds);
    }

    [Fact]
    public async Task UpdateLastActivityAsync_UpdatesTimestamp()
    {
        var channel = _repo.GetOrCreateDefaultGeneral();
        var oldTime = channel.LastActivityAt;

        await Task.Delay(15);
        await _repo.UpdateLastActivityAsync(channel.Id);

        var updated = await _repo.GetByIdAsync(channel.Id);
        Assert.NotNull(updated);
        Assert.True(updated.LastActivityAt > oldTime);
    }

    [Fact]
    public void EvaluateChannelAccessRule_ProjectChannel_ChecksUserProjectIds()
    {
        var projectChannel = new ChatChannel
        {
            ChannelType = ChatChannelType.Project,
            LinkedEntityId = "proj-123"
        };

        var canAccess = _repo.EvaluateChannelAccessRule(projectChannel, "user1", ["proj-123"], []);
        var cannotAccess = _repo.EvaluateChannelAccessRule(projectChannel, "user1", ["proj-456"], []);

        Assert.True(canAccess);
        Assert.False(cannotAccess);
    }

    [Fact]
    public void EvaluateChannelAccessRule_TeamChannel_ChecksUserTeamIdsAndAdmin()
    {
        var teamChannel = new ChatChannel
        {
            ChannelType = ChatChannelType.Team,
            LinkedEntityId = "team-eng",
            AllowedTeamIds = ["team-qa"]
        };

        Assert.True(_repo.EvaluateChannelAccessRule(teamChannel, "user1", [], ["team-eng"]));
        Assert.True(_repo.EvaluateChannelAccessRule(teamChannel, "user2", [], ["team-qa"]));
        Assert.True(_repo.EvaluateChannelAccessRule(teamChannel, "adminUser", [], [], isAdmin: true));
        Assert.False(_repo.EvaluateChannelAccessRule(teamChannel, "user3", [], ["team-sales"]));
    }

    [Fact]
    public void EvaluateChannelAccessRule_GeneralRestricted_ChecksParticipantsAndAllowedTeams()
    {
        var restrictedGeneral = new ChatChannel
        {
            ChannelType = ChatChannelType.General,
            IsDefaultGeneral = false,
            ParticipantIds = ["user-allowed"],
            AllowedTeamIds = ["team-mgmt"]
        };

        Assert.True(_repo.EvaluateChannelAccessRule(restrictedGeneral, "user-allowed", [], []));
        Assert.True(_repo.EvaluateChannelAccessRule(restrictedGeneral, "user-team", [], ["team-mgmt"]));
        Assert.True(_repo.EvaluateChannelAccessRule(restrictedGeneral, "admin", [], [], isAdmin: true));
        Assert.False(_repo.EvaluateChannelAccessRule(restrictedGeneral, "user-denied", [], []));
    }

    [Fact]
    public void ReconcileChannelActivityTimestamps_UpdatesStaleTimestampsFromMessages()
    {
        var past = DateTime.UtcNow.AddDays(-10);
        var recent = DateTime.UtcNow.AddMinutes(-5);

        var chan1 = new ChatChannel { Id = "chan1", ChannelType = ChatChannelType.Direct, LastActivityAt = past };
        var chan2 = new ChatChannel { Id = "chan2", ChannelType = ChatChannelType.Direct, LastActivityAt = DateTime.UtcNow };
        var chan3 = new ChatChannel { Id = "chan3", ChannelType = ChatChannelType.General, LastActivityAt = default, CreatedAt = past };

        _repo.Save(chan1);
        _repo.Save(chan2);
        _repo.Save(chan3);

        var mockProfileRepo = new Mock<Spokes_Server.Core.Data.Repositories.Core.CompanyProfileRepository>(_writer, Mock.Of<IConfiguration>());
        var mockMessageRepo = new Mock<ChatMessageRepository>(_writer, Mock.Of<IConfiguration>(), mockProfileRepo.Object);
        mockMessageRepo.Setup(m => m.GetLatestMessageTimestamp("chan1")).Returns(recent);
        mockMessageRepo.Setup(m => m.GetLatestMessageTimestamp("chan2")).Returns(past); // message is older than channel last activity
        mockMessageRepo.Setup(m => m.GetLatestMessageTimestamp("chan3")).Returns((DateTime?)null); // no messages

        _repo.ReconcileChannelActivityTimestamps(mockMessageRepo.Object);

        var updatedChan1 = _repo.GetById("chan1");
        var updatedChan2 = _repo.GetById("chan2");
        var updatedChan3 = _repo.GetById("chan3");

        Assert.Equal(recent, updatedChan1!.LastActivityAt);
        Assert.True(updatedChan2!.LastActivityAt > past);
        Assert.Equal(past, updatedChan3!.LastActivityAt);
    }
}




