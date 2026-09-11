using Spokes_Server.Core.Services.Communication;
using Spokes_Server.Core.Services.Projects;
using Spokes_Server.Core.Services.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using Spokes_Server.Core.Data;
using Spokes_Server.Core.Data.Repositories.Core;
using Spokes_Server.Core.Data.Repositories.Projects;
using Spokes_Server.Core.Data.Repositories.Accounting;
using Spokes_Server.Core.Data.Repositories.Communication;
using Spokes_Server.Core.Data.Repositories.HR;
using Spokes_Server.Core.Models.Core;
using Spokes_Server.Core.Models.Projects;
using Spokes_Server.Core.Models.Accounting;
using Spokes_Server.Core.Models.Communication;
using Spokes_Server.Core.Models.HR;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Spokes_Server.Tests.Core.Data.Repositories.Communication
{
    public class ChatChannelRepositoryTests : IDisposable
    {
        private readonly string _testDataDir;
        private readonly DiskPersistenceService _writer;
        private readonly ChatChannelRepository _repo;

        public ChatChannelRepositoryTests()
        {
            _testDataDir = Path.Combine(Path.GetTempPath(), "Spokes_Test_ChatChannels_" + Guid.NewGuid().ToString());

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

            var channels = _repo.GetChannelsForUser("user1", new List<string>(), new List<string>(), false);

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
                AllowedPostUserIds = new List<string> { "user1" },
                AllowedPostTeamIds = new List<string> { "team1" }
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
                AllowedPostUserIds = new List<string> { "user1" },
                AllowedPostTeamIds = new List<string> { "team1" }
            };

            channel.Normalize();

            Assert.True(channel.IsAnnouncementOnly);
            Assert.Single(channel.AllowedPostUserIds);
            Assert.Single(channel.AllowedPostTeamIds);
        }

        [Fact]
        public void EvaluateChannelPostAccessRule_AllowsPostingInDirectChannelsEvenIfAnnouncementFlagged()
        {
            var direct = new ChatChannel
            {
                ChannelType = ChatChannelType.Direct,
                IsAnnouncementOnly = true,
                AllowedPostUserIds = new List<string> { "authorizedUser" }
            };

            // An unauthorized user tries to post in this DM
            var canPost = _repo.EvaluateChannelPostAccessRule(direct, "otherUser", new List<string>(), isAdmin: false);
            Assert.True(canPost);
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
                AllowedPostUserIds = new List<string> { "user1" },
                AllowedPostTeamIds = new List<string> { "team1" }
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
    }
}




