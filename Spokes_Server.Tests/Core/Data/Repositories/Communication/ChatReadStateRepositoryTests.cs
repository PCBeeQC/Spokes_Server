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
using System.IO;

namespace Spokes_Server.Tests.Core.Data.Repositories.Communication
{
    public class ChatReadStateRepositoryTests : IDisposable
    {
        private readonly string _testDataDir;
        private readonly DiskPersistenceService _writer;
        private readonly ChatReadStateRepository _repo;

        public ChatReadStateRepositoryTests()
        {
            _testDataDir = Path.Combine(Path.GetTempPath(), "Spokes_Test_ReadStates_" + Guid.NewGuid().ToString());

            var mockConfig = new Mock<IConfiguration>();
            mockConfig.Setup(c => c["DataPath"]).Returns(_testDataDir);

            var mockLogger = new Mock<ILogger<DiskPersistenceService>>();
            _writer = new DiskPersistenceService(mockLogger.Object);

            _repo = new ChatReadStateRepository(_writer, mockConfig.Object, new CompanyProfileRepository(_writer, mockConfig.Object));
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
        public void MarkAsRead_CreatesNewStateIfMissing()
        {
            _repo.MarkAsRead("user1", "channel1");

            var state = _repo.GetReadState("user1", "channel1");
            Assert.NotNull(state);
            Assert.Equal("user1", state.UserId);
            Assert.Equal("channel1", state.ChannelId);

            // LastReadAt should be very recent
            Assert.True((DateTime.UtcNow - state.LastReadAt).TotalSeconds < 5);
        }

        [Fact]
        public void MarkAsRead_UpdatesExistingState()
        {
            var oldTime = DateTime.UtcNow.AddDays(-1);
            _repo.Save(new ChatReadState
            {
                Id = ChatReadState.CreateId("user2", "channel2"),
                UserId = "user2",
                ChannelId = "channel2",
                LastReadAt = oldTime
            });

            _repo.MarkAsRead("user2", "channel2");

            var state = _repo.GetReadState("user2", "channel2");
            Assert.NotNull(state);
            Assert.True(state.LastReadAt > oldTime);
        }

        [Fact]
        public void GetReadStatesForUser_ReturnsOnlyUsersStates()
        {
            _repo.MarkAsRead("user3", "channelA");
            _repo.MarkAsRead("user3", "channelB");
            _repo.MarkAsRead("user4", "channelA"); // Different user

            var states = _repo.GetReadStatesForUser("user3");
            Assert.Equal(2, states.Count);
            Assert.All(states, s => Assert.Equal("user3", s.UserId));
        }

        [Fact]
        public void GetLastReadAt_ReturnsMinValueIfNotFound()
        {
            var time = _repo.GetLastReadAt("user5", "unknown_channel");
            Assert.Equal(DateTime.MinValue, time);
        }

        [Fact]
        public void GetLastReadAt_ReturnsCorrectTimeIfFound()
        {
            _repo.MarkAsRead("user6", "channel6");
            var time = _repo.GetLastReadAt("user6", "channel6");
            Assert.True(time > DateTime.MinValue);
        }

        [Fact]
        public void SetNotificationLevel_CreatesNewStateIfMissing()
        {
            _repo.SetNotificationLevel("user7", "channel7", "mute");

            var level = _repo.GetNotificationLevel("user7", "channel7");
            Assert.Equal("mute", level);
        }

        [Fact]
        public void SetNotificationLevel_UpdatesExistingState()
        {
            _repo.MarkAsRead("user8", "channel8"); // Creates state with default level implicitly or explicitly
            _repo.SetNotificationLevel("user8", "channel8", "mentions");

            var state = _repo.GetReadState("user8", "channel8");
            Assert.NotNull(state);
            Assert.Equal("mentions", state.NotificationLevel);
        }

        [Fact]
        public void GetNotificationLevel_ReturnsFallbackIfNotSet()
        {
            // Using a user/channel combination that doesn't exist in cache
            var level = _repo.GetNotificationLevel("user9", "channel9", ChatChannelType.Project.ToString());

            // Should be the default for project
            Assert.Equal(ChatReadState.DefaultNotificationLevel(ChatChannelType.Project.ToString(), false), level);
        }
    }
}




